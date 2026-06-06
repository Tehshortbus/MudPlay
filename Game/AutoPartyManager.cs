using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game;

/// <summary>
/// Engine that consumes the per-player <see cref="PlayerCustomization"/>
/// auto-party flags. Two behaviours, both gated on the loaded character's
/// PlayerDatabase customizations:
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>Invite-on-seen</b> — when a player whose row carries
///         <see cref="PlayerCustomization.InviteToPartyIfSeen"/>
///         appears in our current room (via the
///         <see cref="KnownPatterns.RoomAlsoHere"/> "Also here: ..." line),
///         send <c>invite &lt;given&gt;</c> on the wire. TTL-suppressed at
///         <see cref="InviteCooldown"/> per recipient so subsequent room
///         re-renders don't re-spam. Skipped when the player is already
///         in <see cref="PartyState.Members"/>.</item>
///   <item><b>Accept-invite</b> — when another player sends us an in-game
///         party invite (<see cref="KnownPatterns.PartyInviteReceived"/>,
///         matching "X has invited you to follow him/her"), look up
///         their customization. If
///         <see cref="PlayerCustomization.JoinPartyIfInvited"/> is set,
///         send <c>follow &lt;given&gt;</c> (the MajorMUD accept
///         mechanism — joining someone's party is "follow them";
///         <see cref="PartyManager"/> already maps "You are now
///         following X" to "we joined X's party").</item>
/// </list>
/// <para>
/// Threading: handler invocation is on the dispatcher thread (the
/// MessageRouter marshals upstream). All state reads + writes happen
/// there, so the <c>_recentlyInvited</c> dictionary doesn't need its
/// own lock.
/// </para>
/// </remarks>
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
    private bool _disposed;

    /// <summary>
    /// Per-recipient TTL on auto-invites. Subsequent room renders within
    /// this window won't re-fire the invite — they either accepted
    /// (and would be in <see cref="PartyState.Members"/>, taking the
    /// already-in-party branch) or they declined, in which case we
    /// shouldn't keep nagging them once per move. Default 60 s; tunable
    /// at runtime if a feature surfaces a knob for it.
    /// </summary>
    public TimeSpan InviteCooldown { get; set; } = TimeSpan.FromSeconds(60);

    // ----- @join nag escalation knobs (Settings → Party) ------------------
    /// <summary>Wait this long after the initial <c>invite</c> before the first <c>@join</c> nag.</summary>
    public TimeSpan JoinNagInitialDelay { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Cadence for subsequent <c>@join</c> resends.</summary>
    public TimeSpan JoinNagFrequency { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Hard cap on the total nag window measured from the initial <c>invite</c>.</summary>
    public TimeSpan JoinNagMaxTotal { get; set; } = TimeSpan.FromSeconds(55);

    /// <summary>
    /// Test seam — overrides <see cref="DateTime.UtcNow"/> for the TTL
    /// math so unit tests don't have to <c>Thread.Sleep</c>. Defaults
    /// to <see cref="DateTime.UtcNow"/>.
    /// </summary>
    public Func<DateTime> NowProvider { get; set; } = () => DateTime.UtcNow;

    /// <summary>Test seam — most recent bytes the engine asked to write to the wire.</summary>
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    /// <summary>Per-given-name TTL map suppressing rapid re-invites.</summary>
    private readonly Dictionary<string, DateTime> _recentlyInvited =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-given-name TTL map suppressing auto-invites for a player we
    /// just kicked. When the user clicks the Uninvite button (or any
    /// other path through <c>uninvite X</c>), the server emits
    /// "X has been removed from your followers." — we stamp X here so
    /// the next "Also here: X" line doesn't immediately re-add them
    /// and start the nag flow again. Default suppression window is
    /// 1 hour; users who want a longer / permanent block can turn off
    /// <c>InviteToPartyIfSeen</c> on the Players-tab record instead.
    /// </summary>
    private readonly Dictionary<string, DateTime> _recentlyUninvited =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Window after an uninvite during which the player won't be auto-invited again. Default 1 h.</summary>
    public TimeSpan UninviteSuppression { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Per-target nag-escalation state — live for the duration of the @join sequence.</summary>
    private readonly Dictionary<string, NagState> _activeNags =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One target's @join nag progression. The engine ticks all active
    /// nags on every dispatcher tick (UI thread) and on every external
    /// observation (telepath reply, party-add, follower-state flip).
    /// </summary>
    private sealed class NagState
    {
        public string Given { get; set; } = string.Empty;
        public DateTime InvitedAt { get; set; }
        public DateTime? LastJoinAt { get; set; }
        public int JoinSends { get; set; }
        /// <summary>True once the target telepathed back <c>{Ok}</c> — stop firing @join but keep waiting for them to actually follow.</summary>
        public bool Acknowledged { get; set; }
    }

    /// <summary>
    /// UI-thread tick that walks <see cref="_activeNags"/> and fires
    /// <c>@join</c> resends + cap checks. Started on first nag, stopped
    /// when the map empties.
    /// </summary>
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

        // TTL housekeeping — drop the cooldown entry for any member that
        // leaves the roster (so a player who separates from us and then
        // re-enters our room is eligible for a fresh auto-invite without
        // waiting out the 60 s window) and flush the entire map when the
        // party fully dissolves (every roster member is gone — any of
        // them could be re-invited the next "Also here:").
        _party.Members.CollectionChanged += OnPartyMembersChanged;
        _party.PropertyChanged           += OnPartyPropertyChanged;
    }

    private void OnPartyMembersChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (object? item in e.OldItems)
            {
                if (item is not PartyMember m) continue;
                string given = ExtractGiven(m.Name);
                if (!string.IsNullOrEmpty(given)) _recentlyInvited.Remove(given);
            }
        }
        // Any name that's now in the roster — the @join nag for them
        // succeeded; stop sending.
        if (e.NewItems is not null)
        {
            foreach (object? item in e.NewItems)
            {
                if (item is not PartyMember m) continue;
                string given = ExtractGiven(m.Name);
                if (!string.IsNullOrEmpty(given)) CancelNag(given, reason: "joined the party");
            }
        }
    }

    private void OnPartyPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Full dissolution wipes the cooldown so any prior roster
        // member that re-appears in our room can be auto-invited fresh.
        if (e.PropertyName == nameof(PartyState.IsInParty) && !_party.IsInParty)
        {
            _recentlyInvited.Clear();
        }
        // If we just became a follower, abort every active nag — only
        // solo or leader configurations should be inviting people.
        if ((e.PropertyName == nameof(PartyState.IsInParty)
             || e.PropertyName == nameof(PartyState.SelfIsLeader))
            && _party.IsInParty && !_party.SelfIsLeader)
        {
            CancelAllNags("became a follower");
        }
    }

    /// <summary>
    /// "X has been removed from your followers." — leader-side
    /// uninvite confirmation. Suppress further auto-invites of X for
    /// <see cref="UninviteSuppression"/> and cancel any in-flight
    /// nag so we don't immediately re-add the person we just kicked.
    /// </summary>
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
        // path can still cancel it cleanly. Anything else from this
        // target ends the entire nag attempt.
        if (body.Equals("{Ok}", StringComparison.OrdinalIgnoreCase))
        {
            state.Acknowledged = true;
            _log?.Log(LogSeverity.Info, "AutoParty",
                $"{sender} acknowledged @join with {{Ok}} — holding further sends.");
        }
        else
        {
            CancelNag(sender, reason: $"replied '{body}' (not {{Ok}})");
        }
    }

    /// <summary>
    /// Bind the wire-sender — same shape as
    /// <see cref="Remote.PartyEssentialHandlers.SetWireSender"/>. The
    /// main-window VM supplies <c>SendUserInput</c>; pre-binding, the
    /// engine still processes events but produces no wire output (so
    /// tests can inspect <see cref="LastSentForTests"/> without
    /// configuring a real sender).
    /// </summary>
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
        _party.Members.CollectionChanged -= OnPartyMembersChanged;
        _party.PropertyChanged           -= OnPartyPropertyChanged;
        if (_trainerMenu is not null) _trainerMenu.MenuExited -= OnTrainerMenuExited;
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

    /// <summary>
    /// "You have invited X to follow you." — server echo after any
    /// outbound <c>invite X</c> we sent. Catches BOTH the
    /// <see cref="TryAutoInvite"/> auto-path AND the manual-typed
    /// path (user types <c>invite Raijin</c> at the prompt). Starts
    /// the @join nag for X if one isn't already running, so the
    /// escalation behaviour is identical regardless of who initiated
    /// the invite. Idempotent — if <see cref="TryAutoInvite"/> just
    /// fired and already started a nag for X, <see cref="StartNag"/>
    /// would simply replace the entry with a fresh one (same
    /// invited-at timestamp since both paths use NowProvider()).
    /// </summary>
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
    }

    /// <summary>
    /// Trainer-menu exit hook — re-fire <c>invite</c> for every member
    /// who was in the party when the menu opened but is no longer in
    /// the roster (their follower-side view dissolved during our
    /// absence even though the leader-side <c>[Invited]</c> slot is
    /// still hot). Existing AutoPartyManager flows handle the rest:
    /// the follower's <see cref="OnPartyInviteReceived"/> auto-accepts
    /// if they have <see cref="PlayerCustomization.JoinPartyIfInvited"/>
    /// set, and the @join nag escalation covers anyone who doesn't
    /// auto-accept within the initial-delay window.
    /// </summary>
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

            // Already in our party? Nothing to do.
            bool stillIn = false;
            foreach (PartyMember m in _party.Members)
            {
                if (string.Equals(ExtractGiven(m.Name), given, StringComparison.OrdinalIgnoreCase))
                { stillIn = true; break; }
            }
            if (stillIn) continue;

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
        if (!FindCustomization(sender, out PlayerCustomization c)) return;
        if (!c.JoinPartyIfInvited) return;

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

    /// <summary>Begin (or replace) the @join nag flow for <paramref name="given"/>.</summary>
    private void StartNag(string given, DateTime invitedAt)
    {
        _activeNags[given] = new NagState
        {
            Given     = given,
            InvitedAt = invitedAt,
            JoinSends = 0,
        };
        EnsureNagTimerRunning();
    }

    /// <summary>End the nag flow for <paramref name="given"/> — they joined, declined, replied non-Ok, or the window expired.</summary>
    private void CancelNag(string given, string reason)
    {
        if (!_activeNags.Remove(given)) return;
        _log?.Log(LogSeverity.Info, "AutoParty",
            $"Stopped @join nag for {given} — {reason}.");
        if (_activeNags.Count == 0) StopNagTimer();
    }

    /// <summary>Abort every active nag — used on the became-a-follower transition.</summary>
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

    /// <summary>
    /// Lazily spin up the dispatcher tick that walks active nags.
    /// 500 ms cadence is fine — nag decisions are second-resolution,
    /// not millisecond-sensitive.
    /// </summary>
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

    /// <summary>Test seam — runs one pass of the nag loop without a real timer.</summary>
    internal void TickNagsForTests() => TickNags();

    private void TickNags()
    {
        if (_activeNags.Count == 0) { StopNagTimer(); return; }
        DateTime now = NowProvider();

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
        if (!_wire.IsBound) return;
        _wire.Send($"/{s.Given} @join");
        s.LastJoinAt = now;
        s.JoinSends++;
        _log?.Log(LogSeverity.Info, "AutoParty",
            $"Sent @join nag #{s.JoinSends} to {s.Given}.");
    }

    // ----- Parsing helpers ---------------------------------------------

    /// <summary>
    /// Split an "Also here:" list capture into individual names. Handles
    /// the three forms observed in MajorMUD: single ("Raijin"), comma
    /// ("Foo, Bar"), and Oxford-and ("Foo, Bar and Baz" / "Foo, Bar, and
    /// Baz"). The capture is already <c>.</c>-stripped by the regex.
    /// </summary>
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

    /// <summary>
    /// Extract the given name from a list entry. The "Also here:" list
    /// can include suffixes like "Raijin (sneaking)" or "Forged WuzHere"
    /// (full display name with family). MajorMUD's <c>invite</c> command
    /// only accepts the given name, so always take the first
    /// whitespace-delimited token, then strip any trailing punctuation
    /// or parenthetical.
    /// </summary>
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
