using FujinTerm.Game.Map;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game;

// Engine that consumes the per-player PlayerCustomization auto-party flags. Two
// behaviours, both gated on the loaded character's PlayerDatabase
// customizations:
//
//   Invite-on-seen — when a player whose row carries InviteToPartyIfSeen
//   appears in our current room (via the RoomAlsoHere "Also here: ..." line),
//   send `invite <given>` on the wire. TTL-suppressed at InviteCooldown per
//   recipient so subsequent room re-renders don't re-spam. Skipped when the
//   player is already in PartyState.Members.
//
//   Accept-invite — when another player sends us an in-game party invite
//   (PartyInviteReceived, matching "X has invited you to follow him/her"), look
//   up their customization. If JoinPartyIfInvited is set, send `follow <given>`
//   (the MajorMUD accept mechanism — joining someone's party is "follow them";
//   PartyManager already maps "You are now following X" to "we joined X's
//   party").
//
// Threading: handler invocation is on the dispatcher thread (the MessageRouter
// marshals upstream). All state reads + writes happen there, so the
// _recentlyInvited dictionary doesn't need its own lock.
public sealed class AutoPartyManager : IDisposable
{
    private readonly MessageRouter _router;
    private readonly PlayerDatabase _players;
    private readonly PartyState _party;
    private readonly TrainerMenuTracker? _trainerMenu;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();
    private readonly IDisposable _alsoHereSub;
    private readonly IDisposable _partyInviteSub;
    private readonly IDisposable _telepathSub;
    private readonly IDisposable _followerRemovedSub;
    private readonly IDisposable _youInvitedSub;
    private readonly IDisposable _teleportArrivalSub;
    private bool _disposed;

    // Per-recipient TTL on auto-invites. Subsequent room renders within this
    // window won't re-fire the invite — they either accepted (and would be in
    // PartyState.Members, taking the already-in-party branch) or they declined,
    // in which case we shouldn't keep nagging them once per move. Default 60 s;
    // tunable at runtime if a feature surfaces a knob for it.
    public TimeSpan InviteCooldown { get; set; } = TimeSpan.FromSeconds(60);

    // ----- @join nag escalation knobs (Settings → Party) ------------------
    // Wait this long after the initial `invite` before the first `@join` nag.
    public TimeSpan JoinNagInitialDelay { get; set; } = TimeSpan.FromSeconds(5);
    // Cadence for subsequent `@join` resends.
    public TimeSpan JoinNagFrequency { get; set; } = TimeSpan.FromSeconds(10);
    // Hard cap on the total nag window measured from the initial `invite`.
    public TimeSpan JoinNagMaxTotal { get; set; } = TimeSpan.FromSeconds(55);

    // Master switch for the `@join` nag. When false, invites still go out but
    // no `@join` follow-up is ever sent — and any in-flight nag stops firing.
    // Mirrors PartySettings.SendJoinToInvited.
    public bool JoinNagEnabled { get; set; } = true;

    // Test seam — overrides DateTime.UtcNow for the TTL math so unit tests don't
    // have to Thread.Sleep. Defaults to DateTime.UtcNow.
    public Func<DateTime> NowProvider { get; set; } = () => DateTime.UtcNow;

    // Reconnect-rejoin override. When set (AppServices wires it to
    // PartyRejoinCoordinator.IsRememberedLeader), an invite from a sender this
    // returns true for is auto-accepted even without a per-player
    // JoinPartyIfInvited grant — remembering we were in that leader's party is
    // itself standing consent to rejoin them after a reconnect. Null = no
    // override (plain per-player rules apply).
    public Func<string, bool>? ForceAcceptFrom { get; set; }

    // Test seam — most recent bytes the engine asked to write to the wire.
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    // Per-given-name TTL map suppressing rapid re-invites.
    private readonly Dictionary<string, DateTime> _recentlyInvited =
        new(StringComparer.OrdinalIgnoreCase);

    // Per-given-name TTL map suppressing auto-invites for a player we just
    // kicked. When the user clicks the Uninvite button (or any other path
    // through `uninvite X`), the server emits "X has been removed from your
    // followers." — we stamp X here so the next "Also here: X" line doesn't
    // immediately re-add them and start the nag flow again. Default suppression
    // window is 1 hour; users who want a longer / permanent block can turn off
    // InviteToPartyIfSeen on the Players-tab record instead.
    private readonly Dictionary<string, DateTime> _recentlyUninvited =
        new(StringComparer.OrdinalIgnoreCase);

    // Window after an uninvite during which the player won't be auto-invited
    // again. Default 1 h.
    public TimeSpan UninviteSuppression { get; set; } = TimeSpan.FromHours(1);

    // ----- Invite-as-wait-signal (Settings → Party "If leading, wait only") --
    // How long, after auto-inviting a seen player while a loop is running, we
    // hold the loop (via MovementCoordinator.PartyInviteGate) waiting for them
    // to join before giving up — at which point we `uninvite` them and let the
    // loop resume. Mirrors the Party-tab "If leading, wait only (s)" value
    // (PartySettings.IfLeadingWaitTotalSec). 0 disables the wait-signal
    // entirely (invite + nag still run, but the loop never pauses and we never
    // auto-uninvite).
    public TimeSpan InviteWaitWindow { get; set; } = TimeSpan.FromSeconds(90);

    private MovementCoordinator? _coordinator;
    private Func<bool>? _isLooping;

    // Per-given-name invite-wait deadlines — present while we're holding the
    // loop for an auto-invited player to join. Maps to the moment the invite
    // went out; the wait expires at invitedAt + InviteWaitWindow.
    private readonly Dictionary<string, DateTime> _inviteWaits =
        new(StringComparer.OrdinalIgnoreCase);

    // Subset of _inviteWaits whose hold came from a party-splitting teleport
    // reform (BeginReformWait) rather than a loop auto-invite (BeginInviteWait).
    // Lets a user movement-stop drop just the reform holds via AbortReformWaits
    // so the gate releases and they can walk elsewhere, without disturbing a
    // loop's own invite-wait.
    private readonly HashSet<string> _reformGiven =
        new(StringComparer.OrdinalIgnoreCase);

    // Members whose reform re-invite is deferred until they materialise in our
    // room after a party-splitting teleport. A chime/CMD teleport moves the
    // leader first and flashes the followers in a beat later, so inviting the
    // instant we cross the teleport races ahead of their arrival — the server
    // answers "You don't see X here!" and drops the invite. We hold the gate
    // immediately (BeginReformWait) but withhold the `invite X` until X's
    // "appears in a blinding flash of light!" (or an "Also here:" listing) shows
    // they've landed. Names leave the set once their invite goes out.
    private readonly HashSet<string> _reformPendingInvite =
        new(StringComparer.OrdinalIgnoreCase);

    // Members whose IsInvited we're watching so a pending invite flipping
    // accepted releases the loop hold.
    private readonly HashSet<PartyMember> _watchedMembers = new();

    // Identifier surfaced in MovementCoordinator.History when we flip the
    // PartyInvite gate.
    private const string InviteGateAsserter = "AutoPartyManager";

    // Late-bind the movement gate used by the invite-as-wait-signal behaviour.
    // AppServices constructs AutoPartyManager before the MovementCoordinator /
    // loop engine exist, so they're injected here once available. isLooping
    // reports whether a loop circuit is currently active — the wait only
    // engages while looping.
    public void SetMovementGate(MovementCoordinator coordinator, Func<bool> isLooping)
    {
        _coordinator = coordinator;
        _isLooping   = isLooping;
    }

    // Per-target nag-escalation state — live for the duration of the @join
    // sequence.
    private readonly Dictionary<string, NagState> _activeNags =
        new(StringComparer.OrdinalIgnoreCase);

    // One target's @join nag progression. The engine ticks all active nags on
    // every dispatcher tick (UI thread) and on every external observation
    // (telepath reply, party-add, follower-state flip).
    private sealed class NagState
    {
        public string Given { get; set; } = string.Empty;
        public DateTime InvitedAt { get; set; }
        public DateTime? LastJoinAt { get; set; }
        public int JoinSends { get; set; }
        // True once the target telepathed back {Ok} — stop firing @join but
        // keep waiting for them to actually follow.
        public bool Acknowledged { get; set; }
    }

    // Immutable view of one active nag, for the bug-report engine-state dump.
    public readonly record struct NagSnapshot(
        string Given, DateTime InvitedAt, DateTime? LastJoinAt, int JoinSends, bool Acknowledged);

    // Read-only snapshot of the in-flight @join nags. UI-thread only (same as
    // every mutation), so no lock — the bug-report capture runs on that thread.
    public IReadOnlyList<NagSnapshot> ActiveNagSnapshot()
    {
        List<NagSnapshot> list = new(_activeNags.Count);
        foreach (NagState s in _activeNags.Values)
            list.Add(new NagSnapshot(s.Given, s.InvitedAt, s.LastJoinAt, s.JoinSends, s.Acknowledged));
        return list;
    }

    // UI-thread tick that walks _activeNags and fires `@join` resends + cap
    // checks. Started on first nag, stopped when the map empties.
    private Avalonia.Threading.DispatcherTimer? _nagTimer;

    public AutoPartyManager(MessageRouter router, PlayerDatabase players, PartyState party, LogService? log = null)
        : this(router, players, party, trainerMenu: null, log) { }

    public AutoPartyManager(MessageRouter router, PlayerDatabase players, PartyState party,
                            TrainerMenuTracker? trainerMenu, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(party);
        _router      = router;
        _players     = players;
        _party       = party;
        _trainerMenu = trainerMenu;
        _log         = log;
        if (_trainerMenu is not null) _trainerMenu.MenuExited += OnTrainerMenuExited;

        _alsoHereSub    = _router.Subscribe(KnownPatterns.RoomAlsoHere,        OnRoomAlsoHere);
        _partyInviteSub = _router.Subscribe(KnownPatterns.PartyInviteReceived, OnPartyInviteReceived);
        // Incoming telepath replies feed the @join nag escalation —
        // {Ok} stops the sends, anything else aborts the nag entirely.
        _telepathSub    = _router.Subscribe(KnownPatterns.ConversationTelepathIn, OnTelepathIncoming);
        // "X has been removed from your followers" — leader-side
        // confirmation that an `uninvite` we (or someone on our
        // behalf) sent landed. Stamp X into the uninvite-suppression
        // map and kill any active nag so we don't immediately
        // re-invite + re-nag the person we just kicked.
        _followerRemovedSub = _router.Subscribe(KnownPatterns.PartyFollowerRemoved, OnFollowerRemoved);
        // "You have invited X to follow you." — server echo on every
        // `invite` we send, whether AutoPartyManager.TryAutoInvite
        // routed it through the InviteToPartyIfSeen flag OR the user
        // typed `invite X` manually at the prompt. Starting the @join
        // nag here covers BOTH paths, so a manual invite escalates
        // the same way an auto-invite does.
        _youInvitedSub = _router.Subscribe(KnownPatterns.PartyYouInvited, OnYouInvited);
        // "X appears in a blinding flash of light!" — a player teleporting into
        // our room. Fires the deferred reform re-invite for a member we're
        // waiting on after a party-splitting teleport, timed to their actual
        // arrival rather than the instant the leader crossed.
        _teleportArrivalSub = _router.Subscribe(KnownPatterns.PlayerTeleportsIn, OnTeleportArrival);

        // TTL housekeeping — drop the cooldown entry for any member that
        // leaves the roster (so a player who separates from us and then
        // re-enters our room is eligible for a fresh auto-invite without
        // waiting out the 60 s window) and flush the entire map when the
        // party fully dissolves (every roster member is gone — any of
        // them could be re-invited the next "Also here:").
        _party.Members.CollectionChanged += OnPartyMembersChanged;
        _party.PropertyChanged           += OnPartyPropertyChanged;
        foreach (PartyMember m in _party.Members) Watch(m);
    }

    private void OnPartyMembersChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (object? item in e.OldItems)
            {
                if (item is not PartyMember m) continue;
                Unwatch(m);
                string given = ExtractGiven(m.Name);
                if (string.IsNullOrEmpty(given)) continue;
                _recentlyInvited.Remove(given);
                // Row gone (uninvited / left) — release any loop hold for them.
                EndInviteWait(given, reason: "left the roster");
            }
        }
        // Any name that's now in the roster — the @join nag for them
        // succeeded; stop sending.
        if (e.NewItems is not null)
        {
            foreach (object? item in e.NewItems)
            {
                if (item is not PartyMember m) continue;
                Watch(m);
                string given = ExtractGiven(m.Name);
                if (string.IsNullOrEmpty(given)) continue;
                CancelNag(given, reason: "joined the party");
                // A row added already non-invited is a real join (par
                // discovered them); the invited-placeholder add keeps
                // waiting until its IsInvited flips false (OnMemberPropertyChanged).
                if (!m.IsInvited) EndInviteWait(given, reason: "joined the party");
            }
        }
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            foreach (PartyMember m in _watchedMembers) m.PropertyChanged -= OnMemberPropertyChanged;
            _watchedMembers.Clear();
            foreach (PartyMember m in _party.Members) Watch(m);
        }
    }

    private void Watch(PartyMember m)
    {
        if (_watchedMembers.Add(m)) m.PropertyChanged += OnMemberPropertyChanged;
    }

    private void Unwatch(PartyMember m)
    {
        if (_watchedMembers.Remove(m)) m.PropertyChanged -= OnMemberPropertyChanged;
    }

    // A pending-invite row flipping IsInvited false is the realm-confirmed join
    // (set by PartyManager.OnFollowsYou) — stop the @join nag and release the
    // loop hold for them.
    private void OnMemberPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PartyMember.IsInvited)) return;
        if (sender is not PartyMember m || m.IsInvited) return;
        string given = ExtractGiven(m.Name);
        if (string.IsNullOrEmpty(given)) return;
        // Leader-side accept flips the placeholder row's IsInvited true→false
        // in place (PropertyChanged, not a CollectionChanged.Add), so the
        // add-based CancelNag never fires for the real join — stop it here too.
        CancelNag(given, reason: "joined the party");
        EndInviteWait(given, reason: "joined the party");
    }

    private void OnPartyPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Full dissolution wipes the cooldown so any prior roster
        // member that re-appears in our room can be auto-invited fresh.
        if (e.PropertyName == nameof(PartyState.IsInParty) && !_party.IsInParty)
        {
            _recentlyInvited.Clear();
            // Nobody left to wait for — release any loop hold.
            ClearAllInviteWaits("party dissolved");
        }
        // If we just became a follower, abort every active nag — only
        // solo or leader configurations should be inviting people.
        if ((e.PropertyName == nameof(PartyState.IsInParty)
             || e.PropertyName == nameof(PartyState.SelfIsLeader))
            && _party.IsInParty && !_party.SelfIsLeader)
        {
            CancelAllNags("became a follower");
            ClearAllInviteWaits("became a follower");
        }
    }

    // "X has been removed from your followers." — leader-side uninvite
    // confirmation. Suppress further auto-invites of X for UninviteSuppression
    // and cancel any in-flight nag so we don't immediately re-add the person we
    // just kicked.
    private void OnFollowerRemoved(MatchResult match)
    {
        if (match.Groups.Count == 0) return;
        string name = match.Groups[0];
        if (string.IsNullOrEmpty(name)) return;
        string given = ExtractGiven(name);
        if (string.IsNullOrEmpty(given)) return;
        _recentlyUninvited[given] = NowProvider();
        CancelNag(given, reason: "uninvited by leader");
        _log?.Log(LogSeverity.Info, "AutoParty",
            $"Suppressing auto-invite of {given} for {UninviteSuppression.TotalMinutes:0} min (uninvited).");
    }

    private void OnTelepathIncoming(MatchResult match)
    {
        // Group 0 = player; Group 1 = message.
        if (match.Groups.Count < 2) return;
        string sender = match.Groups[0];
        string body   = match.Groups[1].Trim();
        if (string.IsNullOrEmpty(sender)) return;
        if (!_activeNags.TryGetValue(sender, out NagState? state)) return;

        // {Ok} (case-insensitive, optional surrounding whitespace) =
        // acknowledgement that they're coming — stop the @join sends
        // but leave the nag entry alive so the join-confirmation
        // path can still cancel it cleanly.
        if (body.Equals("{Ok}", StringComparison.OrdinalIgnoreCase))
        {
            state.Acknowledged = true;
            _log?.Log(LogSeverity.Info, "AutoParty",
                $"{sender} acknowledged @join with {{Ok}} — holding further sends.");
            return;
        }

        // Any other fully-braced {…} body is a machine-generated remote-command
        // reply (e.g. an @health payload like {HP=43/43,MA=15/34, Resting}), not
        // the invited player themselves deciding not to come. Ignore it — the
        // leader pinging the invitee's @health mid-invite must not kill the
        // @join chase before the first nag even fires.
        if (IsBracedPayload(body)) return;

        // Non-braced free text is the human replying — treat as a decline and
        // stop chasing.
        CancelNag(sender, reason: $"replied '{body}' (not {{Ok}})");
    }

    // A telepath body wholly wrapped in braces is an automated remote-command
    // reply from another FujinTerm client (@health, @status, {Ok}, …), never a
    // person typing a decline.
    private static bool IsBracedPayload(string body) =>
        body.Length >= 2 && body[0] == '{' && body[^1] == '}';

    // Bind the wire-sender — same shape as
    // Remote.PartyEssentialHandlers.SetWireSender. The main-window VM supplies
    // SendUserInput; pre-binding, the engine still processes events but produces
    // no wire output (so tests can inspect LastSentForTests without configuring
    // a real sender).
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _alsoHereSub.Dispose();
        _partyInviteSub.Dispose();
        _telepathSub.Dispose();
        _followerRemovedSub.Dispose();
        _youInvitedSub.Dispose();
        _teleportArrivalSub.Dispose();
        _party.Members.CollectionChanged -= OnPartyMembersChanged;
        _party.PropertyChanged           -= OnPartyPropertyChanged;
        foreach (PartyMember m in _watchedMembers) m.PropertyChanged -= OnMemberPropertyChanged;
        _watchedMembers.Clear();
        if (_trainerMenu is not null) _trainerMenu.MenuExited -= OnTrainerMenuExited;
        // Release any held gate so a disposed manager doesn't strand the loop.
        ClearAllInviteWaits("manager disposed");
        StopNagTimer();
    }

    // ----- Handlers ------------------------------------------------------

    private void OnRoomAlsoHere(MatchResult match)
    {
        // Group 0 in the positional list = the (?<players>.+?) capture.
        if (match.Groups.Count == 0) return;
        string list = match.Groups[0];
        if (string.IsNullOrWhiteSpace(list)) return;

        foreach (string raw in SplitOccupantList(list))
        {
            string given = ExtractGiven(raw);
            if (string.IsNullOrEmpty(given)) continue;
            // A reform member already listed at room-render (they teleported in
            // ahead of the leader) never emits a flash line the leader can see,
            // so the "Also here:" listing is their arrival signal — send the
            // withheld invite. Returns early if they're not pending, so the
            // normal auto-invite path below still runs for everyone else.
            TrySendDeferredReformInvite(given);
            TryAutoInvite(given);
        }
    }

    private void OnPartyInviteReceived(MatchResult match)
    {
        // Group 0 in the positional list = the (?<player>\w+) capture.
        if (match.Groups.Count == 0) return;
        string sender = match.Groups[0];
        if (string.IsNullOrEmpty(sender)) return;
        TryAutoAccept(sender);
    }

    // "You have invited X to follow you." — server echo after any outbound
    // `invite X` we sent. Catches BOTH the TryAutoInvite auto-path AND the
    // manual-typed path (user types `invite Raijin` at the prompt). Starts the
    // @join nag for X if one isn't already running, so the escalation behaviour
    // is identical regardless of who initiated the invite. Idempotent — if
    // TryAutoInvite just fired and already started a nag for X, StartNag would
    // simply replace the entry with a fresh one (same invited-at timestamp since
    // both paths use NowProvider()).
    private void OnYouInvited(MatchResult match)
    {
        if (match.Groups.Count == 0) return;
        string given = match.Groups[0];
        if (string.IsNullOrEmpty(given)) return;
        // Follower-state suppression — same rule TryAutoInvite uses
        // for outbound invites. The server-echo shouldn't have fired
        // if we're a follower (the realm would have rejected the
        // command), but be defensive.
        if (_party.IsInParty && !_party.SelfIsLeader) return;
        // Already a real member? They accepted between the invite
        // echo and now — no nag needed.
        foreach (PartyMember m in _party.Members)
        {
            if (string.Equals(ExtractGiven(m.Name), given, StringComparison.OrdinalIgnoreCase))
            {
                // Pending-invite row is fine — that's what the nag
                // is for. Real (non-invited) member is the skip.
                if (!m.IsInvited) return;
                break;
            }
        }
        if (_activeNags.ContainsKey(given)) return;
        StartNag(given, NowProvider());
        _log?.Log(LogSeverity.Info, "AutoParty",
            $"Started @join nag for {given} on manual `invite` echo.");
    }

    // ----- Behaviour ----------------------------------------------------

    private void TryAutoInvite(string given)
    {
        // Already in our party? Nothing to do.
        foreach (PartyMember m in _party.Members)
        {
            if (string.Equals(ExtractGiven(m.Name), given, StringComparison.OrdinalIgnoreCase))
                return;
        }

        // Follower gate — inviting people is only meaningful when we're
        // solo or leading our own party. As a follower we have no
        // authority to grow someone else's roster.
        if (_party.IsInParty && !_party.SelfIsLeader) return;

        if (!FindCustomization(given, out PlayerCustomization c)) return;
        if (!c.InviteToPartyIfSeen) return;

        // Uninvite suppression — if we just kicked this player, don't
        // re-add them. Lazy expiry on read so the map self-prunes.
        if (_recentlyUninvited.TryGetValue(given, out DateTime kickedAt))
        {
            if (NowProvider() - kickedAt < UninviteSuppression) return;
            _recentlyUninvited.Remove(given);
        }

        // Bail BEFORE the TTL bookkeeping if we can't actually send.
        // Without this guard a too-early "Also here:" line (e.g. the
        // engine subscribed before MainWindowViewModel bound the wire-
        // sender) would burn the cooldown on a wire-less attempt, then
        // every subsequent "Also here:" within 60 s would be TTL-
        // suppressed and the user would never get auto-invited that
        // session.
        if (!_wire.IsBound) return;

        // TTL suppression — skip if we've invited them in the cooldown
        // window. Lazy pruning happens here on read.
        DateTime now = NowProvider();
        if (_recentlyInvited.TryGetValue(given, out DateTime sentAt)
            && now - sentAt < InviteCooldown)
        {
            return;
        }
        _recentlyInvited[given] = now;

        _wire.Send($"invite {given}");
        _log?.Log(LogSeverity.Info, "AutoParty", $"Auto-invited {given} (InviteToPartyIfSeen).");

        // Start the @join nag escalation — fires the first /given @join
        // after JoinNagInitialDelay, then re-sends every JoinNagFrequency
        // until they join, telepath back, or JoinNagMaxTotal expires.
        StartNag(given, now);

        // Invite-as-wait-signal — if a loop is running, hold it while we
        // wait for this player to form up. Expiry uninvites + resumes.
        BeginInviteWait(given, now);
    }

    // Trainer-menu exit hook — re-fire `invite` for every member who was in the
    // party when the menu opened but is no longer in the roster (their
    // follower-side view dissolved during our absence even though the
    // leader-side [Invited] slot is still hot). Existing flows handle the rest:
    // the follower's OnPartyInviteReceived auto-accepts if they have
    // JoinPartyIfInvited set, and the @join nag escalation covers anyone who
    // doesn't auto-accept within the initial-delay window.
    private void OnTrainerMenuExited()
    {
        if (_trainerMenu is null) return;
        if (!_wire.IsBound) return;
        IReadOnlyList<string> snapshot = _trainerMenu.RosterAtMenuEntry;
        if (snapshot.Count == 0) return;

        DateTime now = NowProvider();
        foreach (string fullName in snapshot)
        {
            string given = ExtractGiven(fullName);
            if (string.IsNullOrEmpty(given)) continue;

            // Already a joined member? Nothing to do. An [Invited] placeholder
            // does NOT count — that's exactly the stuck state a trainer trip
            // leaves behind: the leader's roster still shows the follower as
            // [Invited] while their follower-side view has dissolved, so we must
            // re-invite (and nag) rather than treat the hot slot as live.
            bool stillJoined = false;
            foreach (PartyMember m in _party.Members)
            {
                if (!m.IsInvited
                    && string.Equals(ExtractGiven(m.Name), given, StringComparison.OrdinalIgnoreCase))
                { stillJoined = true; break; }
            }
            if (stillJoined) continue;

            // Respect the uninvite-suppression map — if the user
            // explicitly kicked them during the menu trip, don't
            // re-add.
            if (_recentlyUninvited.TryGetValue(given, out DateTime kickedAt)
                && now - kickedAt < UninviteSuppression)
                continue;

            // Override the regular re-invite cooldown — the menu exit
            // is a deliberate refresh signal, not the rapid room
            // re-render the cooldown protects against.
            _recentlyInvited[given] = now;
            _wire.Send($"invite {given}");
            _log?.Log(LogSeverity.Info, "AutoParty",
                $"Re-invited {given} after trainer-menu exit.");
            StartNag(given, now);
        }
    }

    private void TryAutoAccept(string sender)
    {
        // Reconnect-rejoin override runs first: if the inviter is the leader we
        // remember following before a reconnect, join regardless of whether a
        // per-player customization exists or has JoinPartyIfInvited set. The
        // remembered-leader memory is the consent. Otherwise fall back to the
        // per-player grant.
        bool forced = ForceAcceptFrom?.Invoke(sender) == true;
        if (!forced)
        {
            if (!FindCustomization(sender, out PlayerCustomization c)) return;
            if (!c.JoinPartyIfInvited) return;
        }

        // Already in this player's party? The PartyManager would have
        // populated PartyState.Members on the follow-confirmation line,
        // so a duplicate follow command is a no-op — but skip it
        // anyway to keep the wire quiet.
        foreach (PartyMember m in _party.Members)
        {
            if (string.Equals(ExtractGiven(m.Name), sender, StringComparison.OrdinalIgnoreCase))
                return;
        }

        _wire.Send($"follow {sender}");
        _log?.Log(LogSeverity.Info, "AutoParty", $"Auto-accepted invite from {sender} (JoinPartyIfInvited).");
    }

    private bool FindCustomization(string given, out PlayerCustomization c)
    {
        foreach (PlayerRecord p in _players.Players)
        {
            if (string.Equals(p.GivenName, given, StringComparison.OrdinalIgnoreCase))
            {
                c = new PlayerCustomization(
                    RemoteControls:      p.RemoteControls,
                    InviteToPartyIfSeen: p.InviteToPartyIfSeen,
                    JoinPartyIfInvited:  p.JoinPartyIfInvited,
                    DontAutoDelete:      p.DontAutoDelete,
                    Notes:               p.Notes);
                return true;
            }
        }
        c = default;
        return false;
    }

    // ----- @join nag escalation ----------------------------------------

    // Begin (or replace) the @join nag flow for given.
    private void StartNag(string given, DateTime invitedAt)
    {
        // Master opt-out — the invite still went out, we just don't chase it.
        if (!JoinNagEnabled) return;
        _activeNags[given] = new NagState
        {
            Given     = given,
            InvitedAt = invitedAt,
            JoinSends = 0,
        };
        EnsureNagTimerRunning();
    }

    // End the nag flow for given — they joined, declined, replied non-Ok, or
    // the window expired.
    private void CancelNag(string given, string reason)
    {
        if (!_activeNags.Remove(given)) return;
        _log?.Log(LogSeverity.Info, "AutoParty",
            $"Stopped @join nag for {given} — {reason}.");
        if (_activeNags.Count == 0) StopNagTimer();
    }

    // Abort every active nag — used on the became-a-follower transition.
    private void CancelAllNags(string reason)
    {
        if (_activeNags.Count == 0) return;
        foreach (string given in _activeNags.Keys.ToArray())
        {
            _activeNags.Remove(given);
            _log?.Log(LogSeverity.Info, "AutoParty",
                $"Stopped @join nag for {given} — {reason}.");
        }
        StopNagTimer();
    }

    // Lazily spin up the dispatcher tick that walks active nags. 500 ms cadence
    // is fine — nag decisions are second-resolution, not millisecond-sensitive.
    private void EnsureNagTimerRunning()
    {
        if (_nagTimer is not null) return;
        _nagTimer = new Avalonia.Threading.DispatcherTimer(
            interval: TimeSpan.FromMilliseconds(500),
            priority: Avalonia.Threading.DispatcherPriority.Background,
            callback: (_, _) => TickNags());
        _nagTimer.Start();
    }

    private void StopNagTimer()
    {
        _nagTimer?.Stop();
        _nagTimer = null;
    }

    // Test seam — runs one pass of the nag loop without a real timer.
    internal void TickNagsForTests() => TickNags();

    private void TickNags()
    {
        if (_activeNags.Count == 0 && _inviteWaits.Count == 0) { StopNagTimer(); return; }
        DateTime now = NowProvider();

        ExpireInviteWaits(now);

        // Snapshot keys — cancel paths mutate _activeNags.
        foreach (string given in _activeNags.Keys.ToArray())
        {
            if (!_activeNags.TryGetValue(given, out NagState? s)) continue;

            // Hard cap from the original invite — give up regardless of state.
            if (now - s.InvitedAt >= JoinNagMaxTotal)
            {
                CancelNag(given, reason: $"window of {JoinNagMaxTotal.TotalSeconds:0}s expired");
                continue;
            }

            // {Ok} acknowledged — hold off on further sends; the
            // join-confirmation path (CollectionChanged) or the
            // total-cap above will close the nag out.
            if (s.Acknowledged) continue;

            if (s.LastJoinAt is null)
            {
                // Phase 1: initial delay after the invite before the
                // first @join fires.
                if (now - s.InvitedAt >= JoinNagInitialDelay)
                {
                    SendJoinNag(s, now);
                }
            }
            else if (now - s.LastJoinAt.Value >= JoinNagFrequency)
            {
                // Phase 2: re-send cadence.
                SendJoinNag(s, now);
            }
        }
    }

    private void SendJoinNag(NagState s, DateTime now)
    {
        // Covers a mid-session disable of an already-active nag.
        if (!JoinNagEnabled) return;
        if (!_wire.IsBound) return;
        _wire.Send($"/{s.Given} @join");
        s.LastJoinAt = now;
        s.JoinSends++;
        _log?.Log(LogSeverity.Info, "AutoParty",
            $"Sent @join nag #{s.JoinSends} to {s.Given}.");
    }

    // ----- Invite-as-wait-signal ---------------------------------------

    // Engage the loop hold for an auto-invited player. Only fires while a loop
    // is running and the wait window is non-zero — otherwise the invite + nag
    // run unchanged and the circuit keeps moving.
    private void BeginInviteWait(string given, DateTime invitedAt)
    {
        if (_coordinator is null) return;
        if (InviteWaitWindow <= TimeSpan.Zero) return;
        if (_isLooping?.Invoke() != true) return;
        HoldInviteWait(given, invitedAt, "loop", isReform: false);
    }

    // Engage the movement hold for a member being re-invited after a
    // party-splitting teleport. Unlike BeginInviteWait this does NOT gate on
    // _isLooping — a chime-style split can happen mid one-shot walk-to (walking
    // into the mansion), so the hold must engage for the walker too. The shared
    // PartyInvite gate pauses whichever movement engine is active.
    private void BeginReformWait(string given, DateTime invitedAt)
    {
        if (_coordinator is null) return;
        if (InviteWaitWindow <= TimeSpan.Zero) return;
        HoldInviteWait(given, invitedAt, "movement", isReform: true);
    }

    private void HoldInviteWait(string given, DateTime invitedAt, string engineLabel, bool isReform)
    {
        _inviteWaits[given] = invitedAt;
        if (isReform) _reformGiven.Add(given);
        RefreshInviteGate();
        EnsureNagTimerRunning();
        _log?.Log(LogSeverity.Info, "AutoParty",
            $"Holding {engineLabel} for {given} to join (up to {InviteWaitWindow.TotalSeconds:0}s).");
    }

    // A party-splitting CMD teleport (chime-style) was just crossed by the local
    // leader. The `.@party <kw>` relay already sent every follower through the
    // same teleport, but teleporting dissolves the follow chain — each member
    // must be re-invited to reform. Snapshot the roster NOW (before the server's
    // dissolve lines clear PartyState.Members), re-invite + @join-nag each former
    // member, and hold the movement gate until they rejoin so the leader doesn't
    // walk off without the reforming group. Mirrors OnTrainerMenuExited (the
    // other party-dissolving event we re-invite through) but adds the gate hold
    // since a split happens mid-movement.
    public void NotePartySplitTeleport()
    {
        // Only a leader reforms — a follower / solo character crossing the same
        // teleport has nobody to re-invite. SelfIsLeader implies a live party.
        if (!_party.SelfIsLeader) return;
        if (!_wire.IsBound) return;

        DateTime now = NowProvider();
        foreach (PartyMember m in _party.Members.ToArray())
        {
            if (m.IsSelf) continue;
            string given = ExtractGiven(m.Name);
            if (string.IsNullOrEmpty(given)) continue;
            // Hold the movement gate NOW so the leader doesn't walk off, but
            // DEFER the `invite X` until X materialises in the room. The
            // teleport lands the leader first and flashes the followers in a
            // beat later, so an immediate invite races ahead of their arrival
            // ("You don't see X here!") and is lost. The invite goes out from
            // OnTeleportArrival / OnRoomAlsoHere once X is actually present.
            _reformPendingInvite.Add(given);
            BeginReformWait(given, now);
            _log?.Log(LogSeverity.Info, "AutoParty",
                $"Awaiting {given}'s arrival to re-invite after party-splitting teleport.");
        }
    }

    // A party member we're waiting to reform has materialised (teleport-arrival
    // line or "Also here:" listing). Send the withheld `invite X` now that the
    // server will actually see them in the room. No-op for anyone not pending —
    // strangers recalling in, or a member already invited.
    private void TrySendDeferredReformInvite(string given)
    {
        if (!_reformPendingInvite.Remove(given)) return;
        if (!_wire.IsBound) return;
        DateTime now = NowProvider();
        // Override the re-invite cooldown — the split is a deliberate reform
        // trigger, not the rapid room re-render the cooldown guards against.
        _recentlyInvited[given] = now;
        _wire.Send($"invite {given}");
        StartNag(given, now);
        _log?.Log(LogSeverity.Info, "AutoParty",
            $"Re-inviting {given} on arrival after party-splitting teleport.");
    }

    private void OnTeleportArrival(MatchResult match)
    {
        if (match.Groups.Count == 0) return;
        string given = ExtractGiven(match.Groups[0]);
        if (string.IsNullOrEmpty(given)) return;
        TrySendDeferredReformInvite(given);
    }

    // Drop the invite-wait for given (they joined, were uninvited, or the party
    // dissolved) and re-evaluate the gate.
    private void EndInviteWait(string given, string reason)
    {
        if (!_inviteWaits.Remove(given)) return;
        _reformGiven.Remove(given);
        _reformPendingInvite.Remove(given);
        _log?.Log(LogSeverity.Info, "AutoParty",
            $"Released loop hold for {given} — {reason}.");
        RefreshInviteGate();
        if (_activeNags.Count == 0 && _inviteWaits.Count == 0) StopNagTimer();
    }

    // User stopped movement mid-reform. Drop just the re-invite holds engaged by
    // a party-splitting teleport (and their @join nags) so the PartyInvite gate
    // releases and the user can walk elsewhere immediately, rather than being
    // pinned until every re-invited member rejoins or the 90s window elapses. A
    // loop's own invite-waits are left untouched. Bound to the walker's stop.
    public void AbortReformWaits(string reason)
    {
        if (_reformGiven.Count == 0) return;
        foreach (string given in _reformGiven.ToArray())
        {
            _inviteWaits.Remove(given);
            CancelNag(given, reason);
        }
        _reformGiven.Clear();
        _reformPendingInvite.Clear();
        _log?.Log(LogSeverity.Info, "AutoParty", $"Aborted party-reform holds — {reason}.");
        RefreshInviteGate();
        if (_activeNags.Count == 0 && _inviteWaits.Count == 0) StopNagTimer();
    }

    // Uninvite anyone whose wait window has elapsed. The server's "removed from
    // your followers" echo then suppresses re-invite and cancels their nag via
    // OnFollowerRemoved; here we just drop the wait so the gate can release and
    // the loop resume.
    private void ExpireInviteWaits(DateTime now)
    {
        if (_inviteWaits.Count == 0) return;
        foreach (string given in _inviteWaits.Keys.ToArray())
        {
            if (!_inviteWaits.TryGetValue(given, out DateTime invitedAt)) continue;
            if (now - invitedAt < InviteWaitWindow) continue;
            if (_wire.IsBound) _wire.Send($"uninvite {given}");
            _log?.Log(LogSeverity.Info, "AutoParty",
                $"{given} didn't join within {InviteWaitWindow.TotalSeconds:0}s — uninviting, resuming loop.");
            EndInviteWait(given, reason: "wait window expired");
        }
    }

    // Assert the PartyInvite gate while any wait is pending, clear it otherwise.
    private void RefreshInviteGate()
    {
        if (_coordinator is null) return;
        if (_inviteWaits.Count > 0)
        {
            _coordinator.AssertGate(MovementCoordinator.PartyInviteGate, InviteGateAsserter,
                $"awaiting join: {string.Join(", ", _inviteWaits.Keys)}");
        }
        else
        {
            _coordinator.ClearGate(MovementCoordinator.PartyInviteGate, InviteGateAsserter,
                "all invited members resolved");
        }
    }

    // Drop every pending invite-wait and release the gate.
    private void ClearAllInviteWaits(string reason)
    {
        if (_inviteWaits.Count == 0) return;
        _inviteWaits.Clear();
        _reformGiven.Clear();
        _reformPendingInvite.Clear();
        _log?.Log(LogSeverity.Info, "AutoParty", $"Released all loop holds — {reason}.");
        RefreshInviteGate();
        if (_activeNags.Count == 0) StopNagTimer();
    }

    // ----- Parsing helpers ---------------------------------------------

    // Split an "Also here:" list capture into individual names. Handles the
    // three forms observed in MajorMUD: single ("Raijin"), comma ("Foo, Bar"),
    // and Oxford-and ("Foo, Bar and Baz" / "Foo, Bar, and Baz"). The capture is
    // already `.`-stripped by the regex.
    private static IEnumerable<string> SplitOccupantList(string list)
    {
        // Normalise " and " → ", " so the comma split handles both forms
        // uniformly. Word-boundary on either side so we don't mangle a
        // name that happens to contain "and" (e.g. "Brandon").
        string normalised = System.Text.RegularExpressions.Regex
            .Replace(list, @"\s+and\s+", ", ");
        foreach (string part in normalised.Split(','))
        {
            string trimmed = part.Trim();
            if (trimmed.Length > 0) yield return trimmed;
        }
    }

    // Extract the given name from a list entry. The "Also here:" list can
    // include suffixes like "Raijin (sneaking)" or "Forged WuzHere" (full
    // display name with family). MajorMUD's `invite` command only accepts the
    // given name, so always take the first whitespace-delimited token, then
    // strip any trailing punctuation or parenthetical.
    private static string ExtractGiven(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        // First whitespace-delimited token.
        int space = raw.IndexOf(' ');
        string token = space >= 0 ? raw[..space] : raw;
        // Strip any non-letter trailing chars (paren, period, comma…).
        int cut = token.Length;
        while (cut > 0 && !char.IsLetter(token[cut - 1])) cut--;
        return cut <= 0 ? string.Empty : token[..cut];
    }
}
