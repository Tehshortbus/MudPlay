using System.Text.RegularExpressions;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;

namespace MudPlay.Game;

// Writes party-membership and per-member state into PartyState from observed
// server lines. Sole writer of every observable field on PartyState and
// PartyMember (the IL-scan test enforces this).
//
// Three input shapes drive membership:
//   1. "X started to follow you." — adds X to PartyState.Members.
//   2. "X stops following you." — removes X.
//   3. The multi-line `par` table — parsed via a tiny state machine on
//      LineExtractor.LineEmitted (same shape as WhoListParser). Each row
//      updates the corresponding PartyMember (HP%, MA%, Position, leader
//      marker). Members observed in `par` that weren't yet in the roster are
//      added; members not observed are NOT removed by `par` alone — death and
//      disconnect detection is a separate path.
//
// The on-join `@health` exchange captures PartyMember.BaselineHp / BaselineMp;
// from then on the `par`-driven percentages render against real absolute
// numbers in the UI. Per-member status-flag observation lines (poison applied,
// blindness cured, etc.) surface the ailment chips.
//
// Threading: MessageRouter already marshals to the UI thread;
// LineExtractor.LineEmitted is forwarded on the same dispatcher path. All
// PartyState mutations therefore happen on the UI thread and the
// ObservableCollection raises change notifications consumers can bind to
// directly.
public sealed partial class PartyManager : IDisposable
{
    private readonly MessageRouter _router;
    private LineExtractor? _lines;
    private readonly List<IDisposable> _subs = new();
    private readonly LogService? _log;
    private bool _disposed;

    // Live party state — manager owns every observable field.
    public PartyState State { get; }

    // True on a Stock realm. Stock ends the follow of a member whose follow move
    // an exit refused and leaves them on the leader's member list, with no line to
    // either side (GAME_MECHANICS "A follower who can't move is left behind");
    // Paradigm drops them and says so. Unset reads as Paradigm, where nothing
    // here differs from what it always did.
    public Func<bool>? OnStock { get; set; }

    // Stock, follower side: the leader whose member list we are still on though
    // we follow nobody, by the game's own rule. Set when a follow the game never
    // ended in words is given up (NoteFollowGivenUp). The roster is the solo one
    // while this is set: every engine of ours is free, and a `join` of that leader
    // puts us back with no fresh invite.
    //
    // That a `join` then works is read from the Stock DLL and not yet seen in
    // play; the game's "You must be invited first!" to a join of ours clears this
    // (NoteNotListedWith), and so does every line that says where we stand.
    public string? ListedWithLeader { get; private set; }

    private void SetListedWith(string? leader, string why)
    {
        if (string.Equals(ListedWithLeader, leader, StringComparison.OrdinalIgnoreCase)) return;
        string? was = ListedWithLeader;
        ListedWithLeader = leader;
        _log?.Info("Party", leader is not null
            ? $"following nobody, still on {leader}'s member list (Stock): {why}"
            : $"no longer counted as on {was}'s member list: {why}");
    }

    // The game refused a `join` of ours to that leader with "You must be invited
    // first!": we are off their list after all, and only an invite brings us back.
    public void NoteNotListedWith(string leader)
    {
        if (ListedWithLeader is { } listed
            && GivenNameOf(listed).Equals(GivenNameOf(leader), StringComparison.OrdinalIgnoreCase))
            SetListedWith(null, "the game answered our join with \"You must be invited first!\"");
    }

    // Our link dropped. Leaving the game takes a player off every member list.
    public void NoteLinkDropped() => SetListedWith(null, "the link dropped");

    // Leader side: the members who have followed us in this party and whose row
    // has not been taken off the roster since. One of them reading [Invited] is
    // not somebody we invited who never came: they joined and stopped following.
    private readonly HashSet<string> _followedUs = new(StringComparer.OrdinalIgnoreCase);

    // Stock, leader side: that member followed us, stopped, and is still on our
    // list (their row reads [Invited]). That is how the game leaves a follower an
    // exit turned away, and such a member rejoins with a `join` and no invite, so
    // an `uninvite` would cost them that. On Paradigm a member left behind has no
    // row at all, and this is never true.
    public bool IsLeftBehindOnOurList(string name)
    {
        if (OnStock?.Invoke() != true) return false;
        string given = GivenNameOf(name);
        if (!_followedUs.Contains(given)) return false;
        foreach (PartyMember m in State.Members)
            if (!m.IsSelf && m.IsInvited && GivenNameOf(m.Name).Equals(given, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // ----- par-block state machine -----
    private enum ParState { Idle, ReadingRows }
    private ParState _parState = ParState.Idle;
    // Names observed in the current par block; used to skip duplicates.
    private readonly HashSet<string> _parBlockNames = new(StringComparer.OrdinalIgnoreCase);
    // The leader we believed we followed when this par block opened, and whether
    // the game said so itself: a follower's `par` opens with "You are following
    // X." ahead of the header, and a player who follows nobody isn't printed it.
    private string? _parFollowedLeader;
    private bool _parFollowing;
    // This block is the reply to a `par` of a client that is on a leader's list
    // and follows nobody (ListedWithLeader): its rows are recorded, not applied.
    private bool _parListedOnly;
    private DateTimeOffset _parFollowingLineAt = DateTimeOffset.MinValue;
    // The following line and the header are one reply; this only has to span them.
    private static readonly TimeSpan ParFollowingLineWindow = TimeSpan.FromSeconds(2);

    // ----- disconnect grace window + auto-invite -------
    // Disconnected members keyed by name → moment we last saw them drop.
    // Lazy-expires on access.
    private readonly Dictionary<string, DateTimeOffset> _recentlyDisconnected
        = new(StringComparer.OrdinalIgnoreCase);

    // Members who re-entered the realm after a drop, with when they dropped.
    // Re-entry spends their _recentlyDisconnected entry (so they're invited once),
    // but the @comeback their client telepaths straight after must still count as
    // from a recent member, or it's refused as not allowed (report
    // paradigm-20260928-074527).
    private readonly Dictionary<string, DateTimeOffset> _returnedAfterDrop
        = new(StringComparer.OrdinalIgnoreCase);

    // "If leading, accept @comeback for (min)" — how long after a member's drop
    // their @comeback is still honoured (WasRecentlyPartied). Separate from the
    // re-invite grace window above.
    public TimeSpan ComebackWindow { get; set; } = TimeSpan.FromMinutes(2);
    private Action<byte[]>? _wireSender;

    // "If leading, wait only" grace window (Settings → Party, mirrored from
    // PartySettings.IfLeadingWaitTotalSec) — how long after a disconnect we'll
    // auto-invite a returning party member back. 30 s only until a profile's
    // setting is applied.
    public TimeSpan DisconnectGraceWindow { get; set; } = TimeSpan.FromSeconds(30);

    // Master switch for the auto-invite-on-reconnect flow. When false,
    // disconnect tracking still works (members are still removed from the
    // roster on drop, still get a grace-window entry) but no `invite` command
    // goes out when they return. The Settings.Party tab binds this.
    public bool AutoInviteEnabled { get; set; } = true;

    // Local character's combat-rank preference (Settings → Party → Rank). Sent
    // to the server as `frontrank` / `backrank` only when the local character
    // joins a party as a follower (OnYouFollowing). Party leaders are forced to
    // frontrank by MajorMUD and can't change it — so the preference is only
    // meaningful on the follower path. Mid is no-op — it's the server default,
    // no command needed. AppServices pushes dto.Rank in via
    // AppServices.ApplyPartyFromActiveProfile.
    public Models.Profile.PartyRank LocalRankPreference { get; set; }
        = Models.Profile.PartyRank.Mid;

    private readonly Func<DateTimeOffset> _now;

    // Locally-connected character's given name (matches the profile name; e.g.
    // "MudPlay" / "Raijin"). Used to detect PartyMember.IsSelf when parsing par
    // rows whose name field is "Given Family". null when no profile is loaded;
    // in that case the par parser can't tell which row is us and IsSelf stays
    // false on every row. AppServices sets this from ProfileService.ProfileLoaded
    // / ProfileClosed.
    //
    // Setting it (re)seeds the SOLO self row: while we're NOT in a party, a lone
    // self row is kept in Members so the PartyWindow shows the local character —
    // HP/mana (via SyncSelfFromPlayerState) and ailment chips (via the Self*
    // responders) live-update on it exactly as they would in a party. Clearing
    // the name (profile close) removes it. The party lifecycle still owns the self
    // row while IsInParty is true, so this is a no-op then. See RefreshSoloSelfRow.
    private string? _localCharacterName;
    public string? LocalCharacterName
    {
        get => _localCharacterName;
        set
        {
            if (string.Equals(_localCharacterName, value, StringComparison.Ordinal)) return;
            _localCharacterName = value;
            SetListedWith(null, "another character was loaded");
            RefreshSoloSelfRow();
        }
    }

    // Keep a live self row in the roster while SOLO so the PartyWindow always shows
    // the local character out of a party (a diagnostic surface for watching ailment
    // apply/clear + state, and groundwork for future self-in-roster features). Never
    // touches IsInParty — a lone self row here is explicitly NOT "a party" (the
    // IsInParty recomputes elsewhere only run during real par/party flows, which
    // don't happen while solo). While in a party the party lifecycle owns the self
    // row, so this stands down.
    private void RefreshSoloSelfRow()
    {
        if (State.IsInParty) return;   // party lifecycle owns the self row
        // Drop a stale solo self row — name cleared (profile close) or swapped to a
        // different character (profile swap).
        for (int i = State.Members.Count - 1; i >= 0; i--)
        {
            PartyMember m = State.Members[i];
            if (!m.IsSelf) continue;
            bool matchesCurrent = !string.IsNullOrEmpty(LocalCharacterName)
                && GivenNameOf(m.Name).Equals(GivenNameOf(LocalCharacterName), StringComparison.OrdinalIgnoreCase);
            if (!matchesCurrent) State.Members.RemoveAt(i);
        }
        if (string.IsNullOrEmpty(LocalCharacterName)) return;
        foreach (PartyMember m in State.Members)
            if (m.IsSelf) return;   // already present
        // CollectionChanged.Add here triggers SyncSelfFromPlayerState (AttachPlayerState
        // subscribed), so the new row gets its HP/mana from PlayerState immediately.
        State.Members.Add(new PartyMember { Name = LocalCharacterName, IsSelf = true });
    }

    // True when we're in the solo shape — not in a party, no leader, and the only
    // member (if any) is our own self row. The dissolution / self-dropped guards use
    // this to recognise "already solo" now that a lone self row persists while solo.
    private bool IsAlreadySolo()
    {
        if (State.IsInParty || State.LeaderName is not null || State.SelfIsLeader) return false;
        foreach (PartyMember m in State.Members)
            if (!m.IsSelf) return false;
        return true;
    }

    // Fires with the joiner's name whenever a member confirms they are
    // following us ("X started to follow you."). Distinct from the invite echo —
    // this is the actual follow confirmation. PartyComebackManager awaits this
    // after re-inviting a recovered follower so it knows when the follower is
    // back under our lead and the paused movement engine can resume.
    public event Action<string>? MemberFollowConfirmed;

    // Fires with the leader's name when we start following someone ("You are now
    // following X."), after the roster and leader state are set. Raised from the
    // game's line rather than read off PartyState changes, which pass through
    // half-set states (in a party, with the last leader's name still on it).
    public event Action<string>? FollowingLeader;

    // Fires with the given name of a party FOLLOWER who just dropped connection
    // while WE lead — the leader-side signal PartyDisconnectMovementGate rides to
    // hold movement through the reconnect grace window. Not fired for a leader
    // drop (that dissolves the party) nor when we're only a follower (our own
    // movement is already held by FollowerGate). See ApplyMemberDisconnect.
    public event Action<string>? MemberDisconnected;

    // Fires with the given name of a dropped member re-entering the realm inside
    // the grace window while we're positioned to re-collect them (leading or
    // solo-fallback, auto-invite on) — the leader-side reconnect signal
    // PartyComebackManager rides to run its @where probe + recovery walk. Raised
    // from OnPlayerEnters before the grace entry is cleared, alongside the bare
    // `invite X` fast path, so a co-located return is handled by the invite and
    // the probe short-circuits. Not fired when we're a follower of someone else.
    public event Action<string>? MemberReturned;

    // Fires with the given name of a follower left behind by our move while we
    // lead (see OnLeftBehind) — PartyComebackManager rides it to backtrack and
    // re-invite them. Gated on AutoInviteEnabled at the raise site.
    public event Action<string>? MemberLeftBehind;

    // Fires with the leader's given name when our own `par` lists the leader we
    // believed we followed as [Invited]: we are still on the list but follow
    // nobody, which is how the game leaves a follower whose follow move it refused
    // without a word (GAME_MECHANICS "A follower who can't move is left behind").
    // Raised before the row is applied to the roster.
    public event Action<string>? LeaderListedAsInvited;

    // Fires on a leader-side reconnect reform with the given names of the followers we
    // were leading when we dropped. A leader disconnect DISSOLVES the party, and the
    // followers stay put in the room (they never left, so no "entered the Realm" auto-
    // invite ever fires for them) — so this drives AutoPartyManager to re-invite each once
    // it observes them present in the room. Gated on AutoInviteEnabled at the raise site.
    public event Action<IReadOnlyList<string>>? LeaderReconnectReformInvites;

    // A `par` reply was read to its end and the roster squared with it: the rows
    // it listed, or the game's word that there is no party. For a consumer that
    // asked `par` a question and must not act before the answer is in.
    public event Action? ParReplyRead;

    // Board-specific disconnect line support. The provider returns the active
    // BBS's raw DisconnectPattern (literal {name}/* syntax, empty/null when the
    // board uses only the standard lines); the resolver maps a captured presence
    // name — which on some boards (Playpen) is the BBS account name, not the
    // character name — back to the player's given name via the account-name
    // override table. Both are set by AppServices; a null on either disables the
    // custom-pattern path, leaving only the built-in disconnect / hang-up lines.
    public Func<string?>? DisconnectPatternProvider { get; set; }
    public Func<string, string?>? PresenceNameResolver { get; set; }

    // Compiled form of the current DisconnectPattern, cached against its raw
    // source so we only recompile when the user edits the pattern or swaps BBS.
    private string? _compiledDisconnectSource;
    private Regex? _compiledDisconnect;

    // par row regex — anchored on the real MajorMUD format:
    //
    //   Raijin WuzHere                  (Priest)        [M:100%] [H:100%]   - Midrank
    //   MudPlay WuzHere                   (Mystic)        [K: 60%] [H: 96%]   - Frontrank
    //   MudPlay WuzHere                   (Mystic)                  [H: 96%]   - Frontrank
    //   Raijin WuzHere                  (Priest)        [M:100%] [H: 85%]   - Backrank
    //
    // Name is given + (optional) family. Class is in parens and can contain
    // spaces ("High Priest" etc.). [H:N%] is load-bearing; rows without it
    // aren't member rows.
    //
    // The secondary-resource bracket is optional AND its letter is
    // class-dependent: casters show mana ([M:N%]), Mystics / monks show kai
    // ([K:N%]) — so the bracket must accept BOTH letters. par omits the whole
    // bracket when the pool is EXACTLY 0 points (not merely 0% — a caster with a
    // few points left still prints [M: 0%]); this 0-point omission is the same
    // for mana and kai. Missing the [K:] alternative was a real bug, kai-unique
    // only because [M:] was already recognized: a Mystic party member's [K:N%]
    // row failed to match, so par reconciliation read them as having left and
    // dropped their roster row every poll — then re-added them (firing the
    // on-join @health round-trip) on the one poll where their kai hit 0 and the
    // bracket disappeared. Both letters feed the same mp group.
    //
    // IMPORTANT: the percentage is right-padded to a 3-char column. At 100%
    // there's no padding ([H:100%]), at <100% there's a leading space ([H: 85%],
    // [H:  5%]). The regex must allow that space — otherwise every non-100% row
    // silently fails to match and HP percent stays frozen between full and empty.
    //
    // A poisoned member's row carries a trailing `P` flag butted right up against
    // the HP bracket with no space ("[H: 89%]P  - Frontrank"). It MUST be consumed
    // between the bracket and the rank chip: without the optional (?<poison>P)?
    // here, the `-` rank suffix never matched on a poisoned row, so `rank` fell
    // through to its Mid default and the PartyWindow silently demoted a
    // force-frontranked leader (or any poisoned member) to midrank every poll
    // they were poisoned. The flag is now also the AUTHORITATIVE source for an
    // OTHER member's poison chip (set/cleared each poll where it's processed) —
    // poison is never announced verbosely, so par is the only cross-client way to
    // see it. Self's poison chip stays owned by ConditionTracker, not par.
    //
    // - Rank is an optional trailing chip (Frontrank / Midrank / Backrank). par
    // doesn't carry Position — that field stays at its default (Standing) for
    // non-self members until a per-member status query fills it.
    [GeneratedRegex(
        @"^\s+(?<name>\S[\w '-]*?)\s+\((?<class>[^)]+)\)\s*(?:\[[MK]:\s*(?<mp>\d+)%\])?\s*\[H:\s*(?<hp>\d+)%\]\s*(?<poison>P)?\s*(?<state>[RM])?\s*(?:-\s*(?<rank>\w+))?",
        RegexOptions.CultureInvariant)]
    private static partial Regex ParRow();

    // Pending-invitee form of a par row:
    //   "   Raijin WuzHere                  (Priest)        [Invited]"
    // The HP / MA / rank columns are absent because the player hasn't accepted
    // yet; the literal [Invited] marker appears in their place. We treat this as
    // an OR signal alongside "You have invited X to follow you." — par re-seeds
    // the IsInvited row state if the user re-opens the client and types par
    // before the original outbound echo scrolls back into view (or if they
    // missed the echo entirely).
    [GeneratedRegex(
        @"^\s+(?<name>\S[\w '-]*?)\s+\((?<class>[^)]+)\)\s*\[Invited\]\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ParInvitedRow();

    // True when text is a row of the `par` party screen — an active member or a
    // pending invitee. The unrecognized-line capture asks this because par is read
    // here as a stateful block rather than through a router pattern, so
    // MessageRouter.AnyPatternMatches can't vouch for these rows and every poll
    // staged the whole roster for review.
    //
    // Both row patterns anchor on the indent par emits, and callers hand us
    // already-trimmed text, so the indent is re-supplied instead of keeping a
    // second, drift-prone copy of each pattern.
    public static bool IsRosterRow(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string indented = " " + text.TrimStart();
        return ParRow().IsMatch(indented) || ParInvitedRow().IsMatch(indented);
    }

    // Construct with the app-singleton MessageRouter and a fresh PartyState. The
    // per-session LineExtractor is supplied later via AttachLineExtractor — the
    // main-window VM owns it because it lives only for the active terminal
    // session, while the manager is app-level so consumers (the remote-command
    // engine, the PartyWindow) can hold a stable reference.
    public PartyManager(MessageRouter router, PartyState state, LogService? log = null, Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(state);
        _router = router;
        State   = state;
        _log    = log;

        _subs.Add(_router.Subscribe(KnownPatterns.PartyFollowsYou,     OnFollowsYou));
        _subs.Add(_router.Subscribe(KnownPatterns.PartyYouFollowing,   OnYouFollowing));
        _subs.Add(_router.Subscribe(KnownPatterns.PartyStopsFollowing, OnStopsFollowing));
        _subs.Add(_router.Subscribe(KnownPatterns.PartyLeftBehind,     OnLeftBehind));
        _subs.Add(_router.Subscribe(KnownPatterns.PartyYouInvited,     OnYouInvited));
        _subs.Add(_router.Subscribe(KnownPatterns.PartyParFollowing,   _ => _parFollowingLineAt = _now()));
        _subs.Add(_router.Subscribe(KnownPatterns.PartyHeader,         OnParHeader));
        // Disconnect / death / reconnect grace window. We watch every "X just
        // disconnected" / "X just entered the Realm" line because a party
        // member who drops while we're looking has to leave the roster
        // immediately, but if they re-connect within the grace window and we're
        // the leader we auto-invite them back. PartyMemberDeath is the
        // conservative PvP-kill match.
        _subs.Add(_router.Subscribe(KnownPatterns.PlayerDisconnects,        OnPlayerDisconnects));
        // "X just hung up!!!" — clean logoff via in-game hangup. Same
        // remove-and-grace-window treatment as a connection drop; the
        // re-invite path works identically.
        _subs.Add(_router.Subscribe(KnownPatterns.PlayerHungUp,             OnPlayerDisconnects));
        // "X just left the Realm." — a member ducking out to the main menu
        // (train / train stats / quit) drops from the party exactly like a
        // disconnect: a follower who trains is removed server-side and only
        // rejoins on a fresh leader invite, and a LEADER who trains dissolves
        // the whole party (MajorMUD leadership rule, same as a leader drop). So
        // route it through the same correlation — stamp the grace window on a
        // follower's exit so the re-entry ("X just entered the Realm") auto-
        // re-invites them, and fall through to full dissolution on a leader's.
        _subs.Add(_router.Subscribe(KnownPatterns.PlayerExits,              OnPlayerDisconnects));
        _subs.Add(_router.Subscribe(KnownPatterns.PlayerEnters,             OnPlayerEnters));
        _subs.Add(_router.Subscribe(KnownPatterns.PartyMemberDeath,         OnMemberDeath));
        // Dissolution signals — the per-row evictions we already had
        // (StopsFollowing / MemberDeath / Disconnect) handle individual
        // departures; these cover the uninvite + leader-removal + total-
        // dissolve cases the screenshot-reported bug exposed.
        _subs.Add(_router.Subscribe(KnownPatterns.PartyFollowerRemoved,      OnFollowerRemoved));
        _subs.Add(_router.Subscribe(KnownPatterns.PartyYouNoLongerFollowing, OnYouNoLongerFollowing));
        _subs.Add(_router.Subscribe(KnownPatterns.PartyDissolved,            OnPartyDissolved));
        // Live rank-change observation — keeps PartyMember.Rank in sync the
        // instant someone reranks, instead of waiting until the next par poll
        // catches up.
        _subs.Add(_router.Subscribe(KnownPatterns.PartyMemberRankChanged,    OnMemberRankChanged));
        _subs.Add(_router.Subscribe(KnownPatterns.PartySelfRankChanged,      OnSelfRankChanged));
        // Live rest observation — flips a member's Resting flag the moment we
        // see "X stops to rest.", ahead of the 5s par poll, so a follower's
        // HealthManager can mirror the leader's rest immediately.
        _subs.Add(_router.Subscribe(KnownPatterns.PartyMemberRestObserved,   OnMemberRestObserved));
        State.Members.CollectionChanged += OnMembersChangedForHpEstimates;
    }

    // Bind the wire-sender used for auto-invite of a reconnecting disconnected
    // member. Without it, reconnect detection still works but no `invite <name>`
    // goes out — the member stays out of the party until manually re-invited.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Send `uninvite X` on the wire — drops X's pending invite (or, on a member
    // who already joined, kicks them from the party); PartyDeathRosterCleanup uses
    // it for a member who died mid-route. Safe to call for any roster row. No-op when no
    // wire-sender is bound (pre-connect).
    //
    // We do NOT pre-emptively remove the row from PartyState.Members — the
    // server-side echo ("X has been removed from your followers.") goes through
    // OnFollowerRemoved and removes the row through the normal path. Removing
    // here first would race with the server's own state and leave a brief window
    // where the row is gone locally but still tracked server-side; routing
    // through the echo keeps both sides authoritative.
    public void Uninvite(string playerGiven)
    {
        if (string.IsNullOrWhiteSpace(playerGiven)) return;
        // MajorMUD addresses players by given name only — strip any
        // family suffix the caller passed through (a PartyWindow row
        // might bind to "Raijin WuzHere").
        string given = GivenNameOf(playerGiven);
        if (string.IsNullOrEmpty(given)) return;
        byte[] bytes = System.Text.Encoding.Latin1.GetBytes($"uninvite {given}\r");
        _wireSender?.Invoke(bytes);
    }

    // Re-invite a player to follow (`invite X`). Used by PartyComebackManager to
    // pull a stranded follower back into the party after the leader walks back
    // to re-collect them — Paradigm removes left-behind members from the party,
    // so recovery requires an explicit re-invite before the follower can resume
    // following. (On Stock a member an exit left behind is still on the list and
    // is told to join; the invite is that flow's fallback: PartyComebackManager's
    // AskBack.) Addresses by given name only (family suffix stripped).
    // The leader-side "You have invited X to follow you." echo still flows
    // through OnYouInvited as usual.
    public void Invite(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        string given = GivenNameOf(name);
        if (string.IsNullOrEmpty(given)) return;
        byte[] bytes = System.Text.Encoding.Latin1.GetBytes($"invite {given}\r");
        _wireSender?.Invoke(bytes);
    }

    // Reconnect-recovery teardown for the @forget flow (PartyComebackManager) —
    // drop a player from the roster AND clear their disconnect grace entry, so a
    // returning member is neither re-invited by OnPlayerEnters nor chased by the
    // recovery walk. Given-name keyed to match both the grace map (which stores a
    // mix of short + long forms) and the roster comparison. Local state only — no
    // wire command; the server already dropped a reconnect-lost member.
    public void ForgetReconnectMember(string playerGiven)
    {
        if (string.IsNullOrWhiteSpace(playerGiven)) return;
        string given = GivenNameOf(playerGiven);
        if (string.IsNullOrEmpty(given)) return;
        List<string>? stale = null;
        foreach (string key in _recentlyDisconnected.Keys)
            if (GivenNameOf(key).Equals(given, StringComparison.OrdinalIgnoreCase))
                (stale ??= new()).Add(key);
        if (stale is not null)
            foreach (string key in stale) _recentlyDisconnected.Remove(key);
        foreach (string key in _returnedAfterDrop.Keys.Where(k => GivenNameOf(k).Equals(given, StringComparison.OrdinalIgnoreCase)).ToList())
            _returnedAfterDrop.Remove(key);
        RemoveMember(given);
    }

    // Bind the live PlayerState so the local character's PartyMember row stays
    // in sync with every prompt (PromptParser writes HP/MA on every status
    // line). Without this the self row only updates on a `par` poll, which means
    // per-prompt damage taken between polls doesn't show in the PartyWindow.
    //
    // We mirror absolute values into the same observable fields the rest of the
    // roster uses — PartyMember.BaselineHp + HpPercent, etc. — so the
    // PartyMember.HpRichDisplay computation works identically for self and others.
    // For non-self members we only know percent from par + max from the on-join
    // @health round-trip (computed-back current = max × pct / 100); for self we
    // know exact current + max from PromptParser, and the percent is recomputed
    // from those exact values so the display matches.
    public void AttachPlayerState(PlayerState playerState)
    {
        ArgumentNullException.ThrowIfNull(playerState);
        if (_playerState is not null) _playerState.PropertyChanged -= OnPlayerStateChanged;
        _playerState = playerState;
        _playerState.PropertyChanged += OnPlayerStateChanged;
        // Also sync whenever the roster changes — when AddSelfIfKnown
        // first inserts the self row, it has zero baselines until
        // a prompt fires PropertyChanged. CollectionChanged here
        // catches that moment so the row is correct from the first
        // PartyWindow render.
        State.Members.CollectionChanged += OnMembersChangedForSelfSync;
        // Initial sync — covers the case where the self row already
        // exists when this method is called.
        SyncSelfFromPlayerState();
    }

    private PlayerState? _playerState;

    private void OnPlayerStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerState.Hp):
            case nameof(PlayerState.MaxHp):
            case nameof(PlayerState.Ma):
            case nameof(PlayerState.MaxMa):
            case nameof(PlayerState.ManaType):
                SyncSelfFromPlayerState();
                break;
        }
    }

    private void OnMembersChangedForSelfSync(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Only Add events warrant a sync — Remove / Reset don't create
        // a new self row that needs population.
        if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add) return;
        SyncSelfFromPlayerState();
    }

    private void SyncSelfFromPlayerState()
    {
        if (_playerState is null) return;
        foreach (PartyMember m in State.Members)
        {
            if (!m.IsSelf) continue;
            // Baseline = max (drives HpRichDisplay's "cur/max" formatting).
            // Percent = exact current * 100 / max — int rounding here is
            // fine because HpRichDisplay multiplies max back out the same
            // way for non-self members; self always has the precise
            // baseline so the cur it computes lines up.
            m.BaselineHp = _playerState.MaxHp;
            m.HpPercent  = _playerState.MaxHp > 0
                ? _playerState.Hp * 100 / _playerState.MaxHp
                : 0;
            m.BaselineMp = _playerState.MaxMa;
            m.MpPercent  = _playerState.MaxMa > 0
                ? _playerState.Ma * 100 / _playerState.MaxMa
                : 0;
            m.IsKai = _playerState.ManaType == ManaType.Kai;
            return;
        }
    }

    // Bind to the active session's LineExtractor so the par-block state machine
    // can read row lines. Calling again with a new extractor (rare — only if the
    // main window is rebuilt) unhooks the previous one first.
    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (_lines is not null) _lines.LineEmitted -= OnLineEmitted;
        _lines = lines;
        _lines.LineEmitted += OnLineEmitted;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_lines is not null) _lines.LineEmitted -= OnLineEmitted;
        State.Members.CollectionChanged -= OnMembersChangedForHpEstimates;
        foreach (IDisposable s in _subs) s.Dispose();
        _subs.Clear();
    }

    // ----- Single-line observers -----------------------------------------

    // "X started to follow you." — X joined OUR party, so we lead. Add X to
    // PartyState.Members, ensure self is also present (if we know our name), and
    // flip PartyState.SelfIsLeader + LeaderName accordingly.
    private void OnFollowsYou(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        string name = result.Groups[0];
        if (string.IsNullOrEmpty(name)) return;
        // Flip IsInParty FIRST so any CollectionChanged.Add subscriber
        // (PartyPoller's on-join @health round-trip in particular)
        // sees the state already consistent at the moment of the add.
        // Without this the @health request was being suppressed by
        // defensive gates that checked IsInParty before it propagated.
        State.IsInParty    = true;
        State.SelfIsLeader = true;
        State.LeaderName ??= LocalCharacterName;
        SetListedWith(null, $"{name} follows us: we lead a party of our own");
        PartyMember joiner = AddOrTouchMember(name);
        // Acceptance — clear any pending invite chip on the same row
        // (added by OnYouInvited when we sent `invite X`). The
        // PartyPoller's on-join @health round-trip subscribes to
        // PropertyChanged on this row so flipping IsInvited triggers
        // the round-trip without us needing a re-Add.
        joiner.IsInvited = false;
        _followedUs.Add(GivenNameOf(name));
        AddSelfIfKnown(isLeader: true);
        // No rank-preference command on the leader path — MajorMUD
        // forces leaders to frontrank, and the server rejects the
        // rerank attempt. The follower path (OnYouFollowing) is the
        // only place LocalRankPreference matters.
        MemberFollowConfirmed?.Invoke(name);
    }

    // "You have invited X to follow you." — fires on the leader-side echo
    // whenever `invite X` lands on the wire (whether typed manually, sent by
    // AutoPartyManager, or returned by PartyEssentialHandlers's @invite
    // handler). Adds X to the roster with PartyMember.IsInvited true so the
    // PartyWindow renders an "Invited" chip + × uninvite button — the user gets
    // visible accountability for every pending invitation.
    //
    // Sending an invite makes us a leader-in-the-making — PartyState.SelfIsLeader
    // flips to true here so the existing leader-only PartyWindow controls (the ×
    // uninvite button in particular) activate immediately. If the invitee
    // declines or times out the row is removed via OnFollowerRemoved; if no rows
    // remain after a dissolution-side cleanup, leadership decays back through the
    // normal OnPartyDissolved path.
    //
    // If X is already on the roster as a real member (e.g. someone fat-fingers a
    // second invite while they're already partying), AddOrTouchMember just
    // touches the existing row and we set IsInvited=true on it. That's a
    // harmless visual state for one par poll cycle; the next par will reflect
    // the actual roster without the chip.
    private void OnYouInvited(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        string name = result.Groups[0];
        if (string.IsNullOrEmpty(name)) return;
        State.IsInParty    = true;
        State.SelfIsLeader = true;
        State.LeaderName ??= LocalCharacterName;
        SetListedWith(null, $"we invited {name}: we lead a party of our own");
        AddSelfIfKnown(isLeader: true);
        AddOrTouchMember(name, isInvited: true);
    }

    // "You are now following X." — WE joined X's party, so X leads. Add X with
    // IsLeader=true, add self as follower if we know our name, set
    // PartyState.LeaderName to X.
    private void OnYouFollowing(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        string leaderName = result.Groups[0];
        if (string.IsNullOrEmpty(leaderName)) return;
        bool wasInParty = State.IsInParty;
        SetListedWith(null, $"we follow {leaderName}");
        // Same early-set rationale as OnFollowsYou — derived state
        // (IsInParty + LeaderName + SelfIsLeader) needs to be
        // consistent at the moment the CollectionChanged.Add fires
        // so the on-join @health round-trip + future event-driven
        // consumers see the right snapshot.
        State.IsInParty    = true;
        State.LeaderName   = leaderName;
        State.SelfIsLeader = false;
        PartyMember leader = AddOrTouchMember(leaderName);
        leader.IsLeader = true;
        AddSelfIfKnown(isLeader: false);
        // Initial-join edge — see OnFollowsYou for the rationale.
        if (!wasInParty) SendRankPreferenceCommand();
        FollowingLeader?.Invoke(leaderName);
    }

    // The master switch (true = off): off, no rank command and no re-invite of a
    // returning member goes out. Invite and Uninvite themselves are not gated
    // here: their callers are.
    public Func<bool>? MasterSwitchOff { get; set; }

    // Send the rerank command (`frontrank` / `backrank`) to the server iff
    // LocalRankPreference is non-Mid and a wire-sender is bound. Mid is the
    // server-side default rank — no command needed when that's the preference.
    private void SendRankPreferenceCommand()
    {
        if (_wireSender is null) return;
        if (MasterSwitchOff?.Invoke() == true) return;
        string? cmd = LocalRankPreference switch
        {
            Models.Profile.PartyRank.Front => "frontrank\r",
            Models.Profile.PartyRank.Back  => "backrank\r",
            _                              => null,
        };
        if (cmd is null) return;
        _wireSender(System.Text.Encoding.Latin1.GetBytes(cmd));
    }

    private void OnStopsFollowing(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        string name = result.Groups[0];
        if (string.IsNullOrEmpty(name)) return;
        // "X stops following you" is leader-side only — we didn't
        // initiate (uninvite goes through OnFollowerRemoved instead),
        // so stamp X into the grace window. If they re-enter the realm
        // inside the window, the Re-invite-lost-members flow picks
        // them up.
        _recentlyDisconnected[name] = _now();
        RemoveMember(name);
    }

    // "X is no longer following you." — a follower who couldn't move when we did
    // (held, knocked down) is left in the room we walked out of and dropped from
    // the party; with only one follower the whole party disbands. Same roster
    // treatment as OnStopsFollowing, plus MemberLeftBehind so the leader goes back
    // for them rather than walking on.
    private void OnLeftBehind(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        string name = result.Groups[0];
        if (string.IsNullOrEmpty(name)) return;
        bool leading = State.SelfIsLeader;
        _recentlyDisconnected[name] = _now();
        RemoveMember(name);
        if (leading && AutoInviteEnabled) MemberLeftBehind?.Invoke(GivenNameOf(name));
    }

    // "X has been removed from your followers." — fires on the LEADER's side
    // when the leader uninvites X (or when X self-departs). Same treatment as
    // OnStopsFollowing: drop X from the roster. The follow-up "You are not in a
    // party at the present time." (if it comes) handles total dissolution
    // separately.
    private void OnFollowerRemoved(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        string name = result.Groups[0];
        if (string.IsNullOrEmpty(name)) return;
        RemoveMember(name);
    }

    // "You are no longer following X." — the FOLLOWER's view of leaving the party
    // outright: the leader uninvited us, we `unfollow`ed, or we died (death removes
    // a follower from the party — including an instant `suicide` that skips the
    // HP<=0 drop). We're solo now, so wipe the WHOLE roster, not just X: dropping
    // only X leaves self in Members, so IsInParty stays true and @join/@invite keep
    // replying "I'm following someone; denied." (reported: died via suicide, the
    // client still thought it was following).
    private void OnYouNoLongerFollowing(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        if (string.IsNullOrEmpty(result.Groups[0])) return;
        // The line is printed by what takes a follower off every member list.
        SetListedWith(null, "the game said we no longer follow");
        LeavePartySolo();
    }

    // "You are not in a party at the present time." — authoritative dissolution
    // signal. Fires after the per-row eviction lines and guarantees the whole
    // party is gone, so we wipe state to the known-empty shape regardless of
    // what the per-row handlers saw. Idempotent — already-empty state is a no-op.
    private void OnPartyDissolved(MatchResult _)
    {
        // Also flush the par-block state machine. Without this, _parState
        // can carry over from a previous in-party par-block (BBS output
        // doesn't emit a blank line between the par table and the next
        // prompt, so _parState stays in ReadingRows). The line that
        // follows "You are not in a party at the present time." is the
        // local character's own row in par's "solo" format
        // ("MudPlay WuzHere ..."), which would otherwise match ParRow,
        // re-add us to Members, flip IsInParty back to true, and keep
        // the par poller alive even though the server just told us
        // we're alone.
        _parState = ParState.Idle;
        _parBlockNames.Clear();
        SetListedWith(null, "the game says we are in no party");

        if (IsAlreadySolo())
        {
            ParReplyRead?.Invoke();
            return;
        }
        // If WE were the leader, snapshot every other-member name into
        // the grace-window map before clearing. Covers the "BBS only
        // emits account-name logoff" failure mode where we never get a
        // per-player disconnect / hung-up line but the party dissolves
        // on its own — any of the prior members could be the player
        // who dropped, so all of them become re-invite candidates. The
        // re-entry handler (OnPlayerEnters) gates on SelfIsLeader at
        // that point, so a returning member only gets auto-invited if
        // we're still leader / soloed at that moment.
        if (State.SelfIsLeader)
        {
            DateTimeOffset now = _now();
            foreach (PartyMember m in State.Members)
            {
                if (m.IsSelf) continue;
                string given = GivenNameOf(m.Name);
                if (string.IsNullOrEmpty(given)) continue;
                // Key by given name to match the lookup in OnPlayerEnters
                // ("X just entered the Realm" only carries the given
                // name). The existing per-player disconnect-stamp uses
                // the same convention.
                _recentlyDisconnected[given] = now;
            }
        }
        ResetPartyMembership();
        ParReplyRead?.Invoke();
    }

    // Wipe the tracked party to the solo state. Shared by the dissolve handler and
    // the self-drop path; callers do their own par-state flush / grace-window
    // snapshot before invoking this.
    private void ResetPartyMembership()
    {
        _followedUs.Clear();
        State.Members.Clear();
        State.LeaderName   = null;
        State.SelfIsLeader = false;
        State.IsInParty    = false;
        // Back to solo but still connected — re-seed the lone self row so the
        // PartyWindow keeps showing the local character (no-op if no character is
        // loaded, e.g. a profile-close disband).
        RefreshSoloSelfRow();
    }

    // Flush the par-block machine and wipe the roster to solo — the shared exit for
    // any event that drops US from the party (HP<=0 drop, train-stats excursion, a
    // "no longer following" leave line, self death). The par-state flush stops a
    // trailing solo par row from re-adding us and flipping IsInParty back on.
    private void LeavePartySolo()
    {
        _parState = ParState.Idle;
        _parListedOnly = false;
        _parBlockNames.Clear();
        ResetPartyMembership();
    }

    // Hitting 0 HP drops the local character from the party on the game-engine side
    // (a follower is removed; a leader can't lead while mortally wounded). Being
    // dragged by a party member afterwards is physical relocation, not membership —
    // the game no longer counts us in the party — so our tracked roster + leader go
    // stale the instant we drop. Clear them; recovery re-confirms membership only
    // from a real "You are following X" / par signal, never from a "is dragging you
    // around" line. Called by PlayerDroppedGate on the HP<=0 transition. Unlike the
    // dissolve handler this takes no leader grace-window snapshot: a mortally-
    // wounded character isn't re-inviting anyone, and recovery re-forms the party
    // through the normal signals.
    public void NoteSelfDropped()
    {
        // At 0 HP the game takes a player off every member list.
        SetListedWith(null, "we dropped or died");
        if (IsAlreadySolo()) return;
        LeavePartySolo();
    }

    // The leader we asked to come back for us refused, or nobody came in the time a
    // leader takes such a request (ComebackRequester.FollowGivenUp). Stock ends a
    // follow at an exit without a line to the follower, so nothing else would clear
    // the follow we still believe in, and the client would hold its own movement
    // for good. Cleared as the game's own "no longer following" line clears it
    // (user, 2026-10-10). A no-op once that line has come, or for another leader.
    //
    // On Stock the game has not taken us off that leader's member list: only the
    // follow ended. So the roster goes solo all the same (the hold on our own
    // movement is what giving up is for), and the leader is kept as
    // ListedWithLeader, which is what a `par` of ours and a later `join` are read
    // against.
    public void NoteFollowGivenUp(string leader)
    {
        if (!State.IsInParty || State.SelfIsLeader || State.LeaderName is not { Length: > 0 } ours) return;
        if (!GivenNameOf(ours).Equals(GivenNameOf(leader), StringComparison.OrdinalIgnoreCase)) return;
        string given = GivenNameOf(ours);
        LeavePartySolo();
        if (OnStock?.Invoke() == true)
            SetListedWith(given, "the follow was given up, and the game ended it without taking us off the list");
    }

    // Entering the train-stats screen is a realm excursion that breaks up our party
    // server-side (see GAME_MECHANICS "Party"). For a FOLLOWER, clear our own stale
    // "following <leader>" roster now — no leave-line reaches our client, so nothing
    // else clears it, and the stale state makes us REJECT the leader's fresh
    // re-invite on return ("already following <leader>") instead of rejoining
    // (report stock-20260801-002423). A LEADER is deliberately left alone: it
    // reforms its group on trainer exit from a roster snapshot that needs
    // SelfIsLeader intact (AutoPartyManager.OnTrainerMenuExited), and its own state
    // resets from the game's "You are not in a party…" line on return.
    public void NoteTrainStatsExcursion()
    {
        if (!State.IsInParty || State.SelfIsLeader) return;   // followers only
        LeavePartySolo();
    }

    private void OnParHeader(MatchResult _)
    {
        // Safety net — if the previous par block is somehow still open (its
        // terminating prompt line never arrived), reconcile it before the new
        // block clears the captured names, so a departed member isn't carried
        // an extra poll cycle. In the normal flow the block already ended on
        // its prompt line and _parState is Idle here, making this a no-op.
        if (_parState == ParState.ReadingRows) ReconcileMissingFromPar();
        _parState = ParState.ReadingRows;
        _parBlockNames.Clear();
        _parFollowedLeader = State.IsInParty && !State.SelfIsLeader && State.LeaderName is { Length: > 0 } leader
            ? GivenNameOf(leader)
            : null;
        // The game's own word outranks what we believe: told we follow someone, the
        // leader can't be an [Invited] row, and nothing in the block makes us lead.
        bool saysFollowing = _now() - _parFollowingLineAt <= ParFollowingLineWindow;
        _parFollowingLineAt = DateTimeOffset.MinValue;
        _parFollowing = saysFollowing || _parFollowedLeader is not null;
        if (saysFollowing)
        {
            _parFollowedLeader = null;
            SetListedWith(null, "`par` says we follow someone");
        }
        // Stock: we follow nobody and are still on a leader's list. What this reply
        // lists is that leader's party, every row [Invited] because we follow none
        // of them and our own row last. None of it is an invite of ours, so none of
        // it is applied: read as such, it made this client a leader with the old
        // party as its pending invites.
        _parListedOnly = ListedWithLeader is not null;
    }

    // ----- disconnect / death / reconnect ---------------

    // "X just disconnected!!!." (carrier lost) or "X just hung up!!!" (clean
    // logoff) — if X is in our roster, remove them.
    //
    // Follower drop: evict the row and record the moment in the grace-window
    // map so a later "just entered the Realm" within the window can auto-invite
    // them back (when we're leader). The grace-window length is the user's
    // Settings → Party "If leading, wait only" value, mirrored into
    // DisconnectGraceWindow.
    //
    // Leader drop: the whole party dissolves per MajorMUD's game rule —
    // leadership doesn't transfer on disconnect. Wipe the full roster via the
    // same path OnPartyDissolved uses. No grace-window entry for a dropped
    // leader: a returning leader has no party to be auto-re-invited into, so the
    // entry would only mislead the reconnect handler.
    //
    // The built-in "just disconnected" / "just hung up" lines key on the
    // character's given name, so route the captured name straight into the shared
    // correlation. The BBS-level "[Account] logs OFF" signal — which keys on the
    // account name on boards like Playpen — is handled separately via the
    // configurable DisconnectPattern (TryCustomDisconnectLine), because it needs
    // the account→character resolution the standard lines don't.
    private void OnPlayerDisconnects(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        string name = result.Groups[0];
        if (string.IsNullOrEmpty(name)) return;
        ApplyMemberDisconnect(GivenNameOf(name));
    }

    // Match the active BBS's custom disconnect line (if one is configured)
    // against a freshly-emitted terminal line. Additive to the built-in
    // KnownPatterns.PlayerDisconnects / PlayerHungUp handlers — a board that
    // emits both forms stays idempotent (the second hit finds the member already
    // evicted). {name} captures the disconnecting player as the BBS renders it
    // (account or character name); the presence resolver maps that to the given
    // name before the shared correlation runs.
    private void TryCustomDisconnectLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Regex? rx = CurrentDisconnectRegex();
        if (rx is null) return;
        Match m = rx.Match(text);
        if (!m.Success) return;
        Group g = m.Groups["name"];
        string captured = g.Success ? g.Value.Trim() : string.Empty;
        if (captured.Length == 0) return;
        // Resolver maps an account-name override back to the given name; it
        // already falls back to the given-name token when nothing maps, so a
        // null return only happens with no resolver wired.
        string given = PresenceNameResolver?.Invoke(captured) ?? GivenNameOf(captured);
        if (string.IsNullOrEmpty(given)) return;
        ApplyMemberDisconnect(given);
    }

    // Lazily (re)compile the BBS DisconnectPattern into a Regex, caching against
    // the raw source so recompilation only happens on an edit / BBS swap. Uses
    // the same literal→regex translation the trigger engine applies ({name} →
    // named capture, * → greedy run, everything else escaped). A malformed
    // pattern disables the custom path rather than throwing on every line.
    private Regex? CurrentDisconnectRegex()
    {
        string? raw = DisconnectPatternProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(raw))
        {
            _compiledDisconnectSource = null;
            _compiledDisconnect = null;
            return null;
        }
        if (!string.Equals(raw, _compiledDisconnectSource, StringComparison.Ordinal))
        {
            _compiledDisconnectSource = raw;
            try
            {
                _compiledDisconnect = new Regex(
                    TriggerEngine.LiteralToRegex(raw),
                    RegexOptions.CultureInvariant);
            }
            catch (ArgumentException)
            {
                _compiledDisconnect = null;
            }
        }
        return _compiledDisconnect;
    }

    // Shared disconnect correlation. given is the character's given-name token
    // (built-in patterns capture it directly; the custom path resolves an
    // account/character presence name to it first). A follower drop evicts the
    // row, stamps the reconnect grace window, and — when WE lead — fires
    // MemberDisconnected so the movement gate holds us in place for the returning
    // member. A leader drop dissolves the whole party (leadership doesn't transfer
    // on disconnect). Not-a-member is a safe no-op.
    private void ApplyMemberDisconnect(string given)
    {
        if (string.IsNullOrEmpty(given)) return;
        // A leader who leaves the game takes their party with them, lists and all.
        if (ListedWithLeader is { } listed && GivenNameOf(listed).Equals(given, StringComparison.OrdinalIgnoreCase))
            SetListedWith(null, $"{given} left the game");
        bool wasMember = false;
        bool wasLeader = false;
        foreach (PartyMember m in State.Members)
        {
            if (!GivenNameOf(m.Name).Equals(given, StringComparison.OrdinalIgnoreCase)) continue;
            wasMember = true;
            wasLeader = m.IsLeader;
            break;
        }
        if (!wasMember) return;
        if (wasLeader)
        {
            // Full dissolution — same shape as OnPartyDissolved so the
            // par-state machine, leader-name, and IsInParty all reset.
            OnPartyDissolved(default);
            return;
        }
        bool selfLeads = State.SelfIsLeader;
        RemoveMember(given);
        _recentlyDisconnected[given] = _now();
        // Leader-only: a follower's own movement is already held by FollowerGate,
        // so a second wait gate there would only linger to timeout.
        if (selfLeads) MemberDisconnected?.Invoke(given);
    }

    // "X just entered the Realm." — if X is in the grace-window map AND we're
    // not currently following someone else, auto-invite. Reconnect window length
    // comes from DisconnectGraceWindow (Settings → Party "If leading, wait
    // only"), default 2 minutes.
    //
    // Gate is "not currently a follower" rather than strict SelfIsLeader because
    // the dissolution-fallback path (OnPartyDissolved) stamps prior members into
    // the grace window AND wipes our leadership flag — we're solo at re-entry
    // time, but solo-becomes-leader the moment the invite goes out, so we should
    // still send it. A genuine follower of someone else's party can't invite, so
    // that case is excluded.
    private void OnPlayerEnters(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        string name = result.Groups[0];
        if (string.IsNullOrEmpty(name)) return;
        if (!_recentlyDisconnected.TryGetValue(name, out DateTimeOffset droppedAt)) return;
        _returnedAfterDrop[name] = droppedAt;
        if (_now() - droppedAt > DisconnectGraceWindow)
        {
            // Past the window — clear the stale entry and bail.
            _recentlyDisconnected.Remove(name);
            return;
        }
        _recentlyDisconnected.Remove(name);
        if (State.IsInParty && !State.SelfIsLeader) return;
        if (!AutoInviteEnabled) return;
        if (MasterSwitchOff?.Invoke() == true) return;
        // Signal the recovery manager before the bare invite: if they came back
        // far away, it probes @where and walks to collect them; a co-located
        // return is handled by the invite below and the probe short-circuits.
        MemberReturned?.Invoke(GivenNameOf(name));
        // Send the invite if we have a wire-sender wired. The wire
        // command is the plain MajorMUD "invite <name>" — the server
        // does the rest.
        if (_wireSender is null) return;
        // MajorMUD addresses other players by GIVEN name only — never
        // family. "invite Raijin", not "invite Raijin WuzHere".
        byte[] bytes = System.Text.Encoding.Latin1.GetBytes($"invite {GivenNameOf(name)}\r");
        _wireSender(bytes);
    }

    // "X has been slain by Y." — if X is in our roster, remove them immediately.
    // No grace window for death (death isn't recoverable the way a disconnect is
    // — the leader doesn't auto-invite a corpse).
    private void OnMemberDeath(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        string name = result.Groups[0];
        if (string.IsNullOrEmpty(name)) return;
        foreach (PartyMember m in State.Members)
        {
            if (m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                RemoveMember(name);
                return;
            }
        }
    }

    // Test seam — read-only view of the disconnect grace window.
    internal IReadOnlyDictionary<string, DateTimeOffset> RecentlyDisconnected => _recentlyDisconnected;

    // Was sender a party member of ours who departed (got left behind / dropped
    // / disconnected) inside the ComebackWindow — and NOT one we
    // deliberately uninvited? Backs the leader-side authorisation of a stranded
    // follower's @comeback: a left-behind follower is dropped from the party
    // server-side (so IsActivePartyMember is false), but they're still
    // recoverable, so we honour their request.
    //
    // Self-departures ("X stops following you") and disconnects stamp the grace
    // window (OnStopsFollowing, the dissolution / par-reconcile snapshots);
    // deliberate uninvites (OnFollowerRemoved) do NOT — so a player we kicked is
    // absent from the map and correctly rejected. Matches on the given name so a
    // "Buddy" telepath pairs with a stamped "Buddy Lastname" entry. Stale
    // entries past the window are a non-match (read-only — expiry/removal stays
    // with the auto-invite path so this can be called from the auth hot-path
    // without mutating).
    public bool WasRecentlyPartied(string sender)
    {
        if (string.IsNullOrEmpty(sender)) return false;
        string senderGiven = GivenNameOf(sender);
        DateTimeOffset now = _now();
        foreach (KeyValuePair<string, DateTimeOffset> kv in _recentlyDisconnected.Concat(_returnedAfterDrop))
        {
            if (now - kv.Value > ComebackWindow) continue;
            if (kv.Key.Equals(sender, StringComparison.OrdinalIgnoreCase)
                || GivenNameOf(kv.Key).Equals(senderGiven, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // A member we know went missing on a trip of ours is about to ask to be fetched
    // (PartyTrainCoordinator, at the end of a party train trip). Their @comeback is
    // a party member's however long ago an exit turned them out of the party, so
    // the window it is honoured in starts now.
    public void ExpectComebackFrom(string given)
    {
        string name = GivenNameOf(given);
        if (name.Length > 0) _recentlyDisconnected[name] = _now();
    }

    // Given names of the followers we're currently leading (self + pending
    // invitees excluded). Snapshotted at disconnect by PartyReformCoordinator so
    // the leader-side reconnect reform knows who to wait for — the live roster is
    // wiped by par reconciliation after the reconnect, so it can't be read back
    // then. Empty when we aren't the leader.
    public IReadOnlyList<string> SnapshotLedFollowers()
    {
        if (!State.SelfIsLeader) return Array.Empty<string>();
        List<string> givens = new();
        foreach (PartyMember m in State.Members)
        {
            if (m.IsSelf || m.IsInvited) continue;
            string given = GivenNameOf(m.Name);
            if (!string.IsNullOrEmpty(given)) givens.Add(given);
        }
        return givens;
    }

    // Leader-side reconnect party reform. A disconnect dissolves our party
    // server-side (leadership doesn't survive a drop; a nightly-cleanup reconnect
    // is the common case), so on reconnect the roster is stale and any returning
    // follower's @comeback would be denied. PartyReformCoordinator hands us the
    // followers we were leading (captured at disconnect) once we're back in-game;
    // we reset our tracked roster to solo (the game's own par output would too, but
    // this keeps the PartyWindow honest immediately) and, for each follower, rebase
    // the reconnect grace window to now — which re-authorises their @comeback
    // (WasRecentlyPartied) and re-enables their auto-invite-on-return
    // (OnPlayerEnters) — then fire MemberDisconnected so PartyDisconnectMovementGate
    // holds the movement loop for the wait window, resuming when they re-party or
    // when the window elapses. Mirrors the grace snapshot OnPartyDissolved takes.
    public void BeginLeaderReconnectReform(IReadOnlyList<string> followerGivens)
    {
        ArgumentNullException.ThrowIfNull(followerGivens);
        if (followerGivens.Count == 0) return;

        _parState = ParState.Idle;
        _parBlockNames.Clear();
        ResetPartyMembership();

        DateTimeOffset now = _now();
        foreach (string name in followerGivens)
        {
            string given = GivenNameOf(name);
            if (string.IsNullOrEmpty(given)) continue;
            _recentlyDisconnected[given] = now;
            MemberDisconnected?.Invoke(given);
        }

        // Proactively re-invite the co-located followers: the grace window above only
        // auto-invites a follower who RE-ENTERS the realm, but a leader-drop leaves them
        // standing in the room (they never left), so nothing would ever pull them back.
        // AutoPartyManager holds the `invite X` until each is observed present.
        if (AutoInviteEnabled)
            LeaderReconnectReformInvites?.Invoke(followerGivens);
    }

    // End-of-par-block reconciliation — any member the par output omitted has
    // left the travel party, so drop the roster row. This is the authoritative
    // membership correction: `par` is the game's own list, so a member missing
    // from it is gone no matter how they left (walked away, logged out, was
    // dropped) and no matter which departure line — if any — the realm printed.
    // That wording-independence matters on re-created MajorMUD realms whose
    // per-departure messages differ from the canonical game. Works for parties
    // of 3+ where a single drop doesn't dissolve the whole party (par just shows
    // N-1 instead of N); the leader additionally stamps the grace window so the
    // Re-invite-lost-party-members flow can pull them back if they return.
    //
    // Defensive guard: only acts when at least one row was parsed this cycle. An
    // empty _parBlockNames set means the par output was malformed / truncated /
    // never finished — not "everyone disappeared". Without this gate every
    // transient parse failure would flag the entire party as missing.
    private void ReconcileMissingFromPar()
    {
        if (_parBlockNames.Count == 0) return;

        // Compare on given name — _parBlockNames stores the long par-row form
        // ("Raijin WuzHere") while a roster row first added via chat may still
        // be short ("Raijin"). AddOrTouchMember upgrades present members to the
        // long form during the same block, but reducing both sides to the
        // given token is robust regardless of which form each carries.
        HashSet<string> present = new(StringComparer.OrdinalIgnoreCase);
        foreach (string n in _parBlockNames) present.Add(GivenNameOf(n));

        DateTimeOffset now = _now();
        List<PartyMember>? missing = null;
        foreach (PartyMember m in State.Members)
        {
            if (m.IsSelf) continue;
            if (string.IsNullOrEmpty(m.Name)) continue;
            if (present.Contains(GivenNameOf(m.Name))) continue;
            (missing ??= new()).Add(m);
        }
        if (missing is null) return;

        foreach (PartyMember m in missing)
        {
            // par is authoritative for the roster regardless of who leads — a
            // member it omits has left the travel party (walked away, logged
            // out, was dropped), so the row goes. Only the leader stamps the
            // grace window: the auto-reinvite-on-return flow is leader-only, so
            // a follower recording a re-invite candidate would be pointless.
            if (State.SelfIsLeader)
            {
                string given = GivenNameOf(m.Name);
                if (!string.IsNullOrEmpty(given))
                    _recentlyDisconnected[given] = now;
            }
            _followedUs.Remove(GivenNameOf(m.Name));
            State.Members.Remove(m);
        }
        State.IsInParty = State.Members.Count > 0;
    }

    // ----- Live rank-change observers -----------------------------------

    // "X just moved to the {front|back} rank in your group." / "X just moved to
    // the middle of your group." — update the named member's PartyMember.Rank
    // immediately so the PartyWindow rank chip reflects the new rank without
    // waiting for the next par poll. No-op when the named player isn't in the
    // roster (defensive — covers a race where a rerank line arrives for a member
    // we just dropped).
    private void OnMemberRankChanged(MatchResult result)
    {
        if (result.Groups.Count < 2) return;
        string name = result.Groups[0];
        if (string.IsNullOrEmpty(name)) return;
        ApplyRankByGivenName(name, result.Groups[1]);
    }

    // "You have moved to the {front|middle|back} ranks of your group." — self's
    // own rerank confirmation. No name in the message; we locate the row by
    // PartyMember.IsSelf (set by the par-row parser whenever the local character
    // appears).
    private void OnSelfRankChanged(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        Models.Profile.PartyRank rank = ParseRankWord(result.Groups[0]);
        foreach (PartyMember m in State.Members)
        {
            if (m.IsSelf) { m.Rank = rank; return; }
        }
    }

    // "X stops to rest." — flip the matching roster member's Resting flag now,
    // instead of waiting for the next par poll to catch the posture change. The
    // pattern matches any resting player in the room; scoping to roster members
    // here makes a non-party bystander's rest a safe no-op. Only ever sets true —
    // the par poll owns the clear when the member stands. A non-member match
    // finds no row and does nothing.
    private void OnMemberRestObserved(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        string name = result.Groups[0];
        if (string.IsNullOrEmpty(name)) return;
        string given = GivenNameOf(name);
        foreach (PartyMember m in State.Members)
        {
            if (!GivenNameOf(m.Name).Equals(given, StringComparison.OrdinalIgnoreCase)) continue;
            m.Resting = true;
            return;
        }
    }

    private void ApplyRankByGivenName(string name, string rankWord)
    {
        string given = GivenNameOf(name);
        Models.Profile.PartyRank rank = ParseRankWord(rankWord);
        foreach (PartyMember m in State.Members)
        {
            if (!GivenNameOf(m.Name).Equals(given, StringComparison.OrdinalIgnoreCase)) continue;
            m.Rank = rank;
            return;
        }
    }

    private static Models.Profile.PartyRank ParseRankWord(string word) => word switch
    {
        "front"  => Models.Profile.PartyRank.Front,
        "back"   => Models.Profile.PartyRank.Back,
        _        => Models.Profile.PartyRank.Mid,
    };

    // ----- par-block row parser ------------------------------------------

    private void OnLineEmitted(LineExtractor.EmittedLine line)
    {
        // Board-specific disconnect line runs first (independent of the par-block
        // state machine HandleLine drives).
        TryCustomDisconnectLine(line.Text);
        HandleLine(line.Text, line.IsPromptLine);
    }

    private void HandleLine(string text, bool isPromptLine = false)
    {
        if (_parState != ParState.ReadingRows) return;
        // The statline prompt ([HP=...]:) is the real par-block terminator.
        // MajorMUD ends the table by returning to the prompt, not by emitting a
        // blank line, so the blank-line branch below effectively never fires
        // against live output — which is why a departed member used to linger
        // in the roster forever. Reconciling here drops them within the same
        // poll. Interleave-safe: a mid-combat line between polls isn't a prompt,
        // so it can't prematurely close the block.
        if (isPromptLine)
        {
            EndParBlock();
            return;
        }
        // Blank line ends the par block. Don't reset the parState until
        // we've seen the terminator so a mid-block blank doesn't kill the
        // parser, but in practice the par table is contiguous.
        if (string.IsNullOrWhiteSpace(text))
        {
            EndParBlock();
            return;
        }
        if (_parListedOnly)
        {
            // Only who is listed matters: whether the leader still is.
            Match listed = ParInvitedRow().Match(text);
            if (!listed.Success) listed = ParRow().Match(text);
            if (listed.Success && listed.Groups["name"].Value.Trim() is { Length: > 0 } listedName)
                _parBlockNames.Add(listedName);
            return;
        }
        // Pending-invitee row — check this first because the regex is
        // strictly narrower than ParRow (no [H:]/[M:]/rank), and we
        // need to skip the HP / Position / Rank assignments below
        // (no data for them on an invited row).
        Match invited = ParInvitedRow().Match(text);
        if (invited.Success)
        {
            string inviteeName = invited.Groups["name"].Value.Trim();
            if (inviteeName.Length == 0) return;
            string inviteeClass = invited.Groups["class"].Success
                ? invited.Groups["class"].Value.Trim()
                : string.Empty;
            _parBlockNames.Add(inviteeName);
            // While we follow someone an [Invited] row says nothing about who
            // leads: it is a member on the list who isn't following our leader (left
            // behind, dead, only invited). Reading it as an invite of our own made
            // this client the leader, which let go of the follower movement hold and
            // of the leader remembered for a reconnect. The one row that does
            // concern us is our own leader's: we are on their list and follow nobody.
            if (_parFollowedLeader is { } followed
                && GivenNameOf(inviteeName).Equals(followed, StringComparison.OrdinalIgnoreCase))
            {
                _parFollowedLeader = null;
                LeaderListedAsInvited?.Invoke(followed);
            }
            PartyMember row = AddOrTouchMember(inviteeName, isInvited: true);
            if (inviteeClass.Length > 0) row.Class = inviteeClass;
            if (_parFollowing) return;
            // Sending an invite implies leadership-in-the-making —
            // mirrors what OnYouInvited does when the outbound echo
            // fires, so the X-uninvite button is enabled even if the
            // user came back to a session via par alone.
            State.IsInParty    = true;
            State.SelfIsLeader = true;
            State.LeaderName ??= LocalCharacterName;
            return;
        }

        Match m = ParRow().Match(text);
        if (!m.Success)
        {
            // Non-row line during ReadingRows — likely the column header
            // or a separator. Stay in ReadingRows until we either see a
            // real row or a blank line; only step out on blank.
            return;
        }

        string name  = m.Groups["name"].Value.Trim();
        if (name.Length == 0) return;
        string klass = m.Groups["class"].Success ? m.Groups["class"].Value.Trim() : string.Empty;
        int hpPct = int.Parse(m.Groups["hp"].Value, System.Globalization.CultureInfo.InvariantCulture);
        // Secondary-resource bracket ([M:N%] mana or [K:N%] kai) is omitted
        // when the class has none (Warriors) or the pool is exactly 0 points —
        // par drops the whole bracket at 0 for mana and kai alike. Both letters
        // feed this one mp group; the PartyWindow renders a single secondary bar
        // per member. An absent bracket is ambiguous on its own, so the write
        // below disambiguates by BaselineMp (see there).
        int? mpPct = m.Groups["mp"].Success
            ? int.Parse(m.Groups["mp"].Value, System.Globalization.CultureInfo.InvariantCulture)
            : (int?)null;

        // Single-letter state column — `R` between the HP bracket and
        // the `- <rank>` suffix means Resting, `M` means Meditating,
        // blank means Standing/idle. Default Standing when the column
        // is absent (the optional regex group doesn't match).
        PlayerPosition position = m.Groups["state"].Success
            ? m.Groups["state"].Value switch
            {
                "R" => PlayerPosition.Resting,
                "M" => PlayerPosition.Meditating,
                _   => PlayerPosition.Standing,
            }
            : PlayerPosition.Standing;

        // Rank text from the last par column (Frontrank / Midrank /
        // Backrank). The regex already captures the word; map it to
        // the PartyRank enum so the PartyWindow rank-chip can render
        // a consistent label + colour per row. Unknown / missing
        // values fall through to Mid.
        Models.Profile.PartyRank rank = m.Groups["rank"].Success
            ? m.Groups["rank"].Value switch
            {
                "Frontrank" => Models.Profile.PartyRank.Front,
                "Backrank"  => Models.Profile.PartyRank.Back,
                _           => Models.Profile.PartyRank.Mid,
            }
            : Models.Profile.PartyRank.Mid;

        // IsSelf detection — both sides are reduced to given (first
        // whitespace token) before comparing. The par row carries
        // "Given Family"; LocalCharacterName may carry the same shape
        // (the loaded profile name often includes family because that's
        // what the user picked at character creation). Without the
        // given-from-both extraction the compare would mismatch
        // ("MudPlay" vs "MudPlay WuzHere"), IsSelf would stay false on
        // OUR OWN row, and PartyPoller.OnMembersChanged would telepath
        // /MudPlay @health to us — the exact spam the screenshot showed.
        bool isSelf = false;
        if (!string.IsNullOrEmpty(LocalCharacterName))
        {
            isSelf = GivenNameOf(name)
                .Equals(GivenNameOf(LocalCharacterName), StringComparison.OrdinalIgnoreCase);
        }

        _parBlockNames.Add(name);
        // Pass isSelf so AddOrTouchMember sets it at construction —
        // CollectionChanged.Add subscribers (PartyPoller) need it
        // correct AT THE MOMENT the add fires, not after.
        PartyMember member = AddOrTouchMember(name, isSelf);
        if (klass.Length > 0) member.Class = klass;
        NoteHpReading(member, hpPct, "par");
        member.HpPercent = hpPct;
        if (mpPct is { } v)
        {
            member.MpPercent = v;
        }
        else if (member.BaselineMp > 0)
        {
            // Bracket absent for a member we KNOW has a secondary pool (a prior
            // @health set BaselineMp) means the pool drained to exactly 0 — par
            // omits [M:]/[K:] at 0. Show 0 rather than freezing the bar at the
            // last non-zero percent. BaselineMp == 0 is a no-pool class (or a
            // member not yet @health'd), where an absent bracket carries no
            // percentage to infer, so leave MpPercent alone there.
            member.MpPercent = 0;
        }
        member.Position = position;
        member.Rank     = rank;
        // Mirror the par-state into the boolean flags so the PartyWindow
        // status-chip strip (which keys on these booleans) lights up too.
        member.Resting    = position == PlayerPosition.Resting;
        member.Meditating = position == PlayerPosition.Meditating;
        // Poison: the par `P` flag is the authoritative cross-client source for
        // OTHER members' poison chip — no client announces poison verbosely (see
        // GAME_MECHANICS "Party ailment signaling"), so drive it straight off the
        // flag each poll: set when `P` is present, clear when it drops. Skip self —
        // our own poison is owned by ConditionTracker (timelier than the 5s par
        // poll), mirrored to the self chip by SelfAilmentChipResponder.
        if (!isSelf)
            member.Poisoned = m.Groups["poison"].Success;

        // A joined par row (this branch only matches a row carrying an [H:] bracket)
        // is proof the member is actually in the party — pending invitees print as a
        // bare "[Invited]" row (ParInvitedRow), never with health. So clear any stale
        // invite chip here: OnFollowsYou is the usual clear, but that "X started to
        // follow you." line prints only for the LEADER, so a FOLLOWER's client never
        // saw it and left the row stuck "Invited" forever — which also suppressed the
        // member's health display and made CastingDirector skip them for party heals.
        // Idempotent: SetProperty no-ops when already false, so the on-join @health
        // round-trip (subscribed to this PropertyChanged) fires once, on the real edge.
        member.IsInvited = false;
        // A full row on a leader's `par` is a member following them.
        if (!isSelf && State.SelfIsLeader) _followedUs.Add(GivenNameOf(name));

        State.IsInParty = State.Members.Count > 0;
    }

    // Close out the current par block: leave ReadingRows, reconcile the roster
    // against what the block listed, and clear the captured names for the next
    // poll. Reconcile must run before the clear (it reads _parBlockNames).
    private void EndParBlock()
    {
        _parState = ParState.Idle;
        // A block with no rows was cut short or misread, and answers nothing.
        bool read = _parBlockNames.Count > 0;
        if (_parListedOnly)
        {
            _parListedOnly = false;
            if (read && ListedWithLeader is { } leader)
            {
                bool stillListed = false;
                foreach (string name in _parBlockNames)
                    if (GivenNameOf(name).Equals(GivenNameOf(leader), StringComparison.OrdinalIgnoreCase)) stillListed = true;
                if (stillListed)
                    _log?.Info("Party",
                        $"`par` lists {leader}'s party as [Invited]: we are on their list and follow nobody (Stock). The rows are not invites of ours and the roster stays solo.");
                else
                    SetListedWith(null, "a `par` of ours no longer lists them");
            }
            _parBlockNames.Clear();
            if (read) ParReplyRead?.Invoke();
            return;
        }
        ReconcileMissingFromPar();
        _parBlockNames.Clear();
        if (read) ParReplyRead?.Invoke();
    }

    // Ensure self is represented in PartyState.Members with the leader marker
    // set per isLeader. No-op when LocalCharacterName is null (we don't know our
    // name well enough to disambiguate from other rows). Used by follows-you /
    // you-following handlers so the PartyWindow shows us immediately without
    // waiting for the next par observation.
    private void AddSelfIfKnown(bool isLeader)
    {
        if (string.IsNullOrEmpty(LocalCharacterName)) return;
        foreach (PartyMember existing in State.Members)
        {
            if (existing.IsSelf)
            {
                existing.IsLeader = isLeader;
                return;
            }
        }
        PartyMember self = new() { Name = LocalCharacterName, IsSelf = true, IsLeader = isLeader };
        State.Members.Add(self);
    }

    // ----- Helpers --------------------------------------------------------

    // Find the existing member whose given name (first whitespace token) matches
    // the given name in name, or add a fresh row. Roster matching is by GIVEN
    // name only because MajorMUD addresses players different ways at different
    // times — chat lines use short form ("Raijin"), par output uses long form
    // ("Raijin WuzHere"). Storing both would create duplicate rows.
    //
    // Name field always upgrades to the LONGER form when observed — once we see
    // "Raijin WuzHere" in par, the row's Name becomes "Raijin WuzHere" even if
    // it was first added as just "Raijin" via follows-you. Going shorter is
    // no-op (don't downgrade).
    //
    // isSelf is applied at construction so any CollectionChanged subscriber
    // (PartyPoller's on-join @health round-trip in particular) sees the right
    // value at the moment the Add fires. Without this, par parsing the local
    // character's row for the first time would race the IsSelf assignment and
    // PartyPoller would telepath /MudPlay @health to ourselves — the server
    // replies "Why are you telepathing to yourself?" and the noise lands in chat.
    private PartyMember AddOrTouchMember(string name, bool isSelf = false, bool isInvited = false)
    {
        string given = GivenNameOf(name);
        foreach (PartyMember m in State.Members)
        {
            if (GivenNameOf(m.Name).Equals(given, StringComparison.OrdinalIgnoreCase))
            {
                // Upgrade to the longer form if applicable.
                if (name.Length > m.Name.Length) m.Name = name;
                // Upgrade IsSelf if a later observation establishes it.
                if (isSelf && !m.IsSelf) m.IsSelf = true;
                // Re-seed the invite chip on an existing row (double-invite echo
                // or a par re-observation). A join never clears it here — the
                // accept paths own IsInvited=false.
                if (isInvited && !m.IsInvited) m.IsInvited = true;
                return m;
            }
        }
        // IsInvited must be set BEFORE the row enters the collection: Members.Add
        // fires CollectionChanged.Add synchronously, and PartyPoller's on-join
        // @health round-trip subscribes to that add. If the flag were set after
        // the add (as it once was), the subscriber would see a still-false
        // IsInvited and fire @health at a pending invitee — the request must wait
        // for the invitee to actually join. See PartyMember._isInvited.
        PartyMember created = new() { Name = name, IsSelf = isSelf, IsInvited = isInvited };
        State.Members.Add(created);
        return created;
    }

    // First whitespace-delimited token. "Raijin WuzHere" → "Raijin"; "Raijin" → "Raijin".
    private static string GivenNameOf(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }

    // Record a member's on-join `@health` snapshot. The reply has the shape
    // {HP=cur/max,MA=cur/max} — we store the max as PartyMember.BaselineHp /
    // BaselineMp AND compute HpPercent / MpPercent from cur so the row shows a
    // meaningful bar immediately, without waiting for the next par poll to fill
    // in the percentage. (Earlier shape only took the max, leaving the row stuck
    // at "H:0/36 0%" until par caught up.) Routes through the manager so the
    // Owner-marked fields keep a single writer (the IL scan enforces this).
    // No-op when the named member isn't in the roster. mpMax = 0 marks a
    // no-mana class (Warriors) — both baseline and percent stay 0 and the
    // PartyWindow hides the MA sub-row entirely via GreaterThanZeroConverter.
    // isKai flags a kai pool (reply carried KAI= rather than MA=) so the row
    // labels the secondary bar K: instead of M:.
    public void SetMemberHealthSnapshot(string name, int hpCur, int hpMax, int mpCur, int mpMax, bool isKai)
    {
        if (string.IsNullOrEmpty(name)) return;
        string given = GivenNameOf(name);
        foreach (PartyMember m in State.Members)
        {
            if (!GivenNameOf(m.Name).Equals(given, StringComparison.OrdinalIgnoreCase)) continue;
            int hpPct = hpMax > 0 ? hpCur * 100 / hpMax : 0;
            NoteHpReading(m, hpPct, "@health");
            m.BaselineHp = hpMax;
            m.BaselineMp = mpMax;
            m.HpPercent  = hpPct;
            m.MpPercent  = mpMax > 0 ? mpCur * 100 / mpMax : 0;
            m.IsKai      = isKai;
            return;
        }
    }

    // ----- HP between polls ----------------------------------------------
    // `par` only gives a member's HP as a percentage, every few seconds. Between polls
    // PartyHpEstimator moves it by the damage and heals we see land on them, so a party
    // heal can react before the next poll. The estimate is kept in absolute HP here —
    // stepping the rounded percentage instead would drift a little on every line — and
    // is thrown away whenever `par` or an @health reply states the real value.

    private readonly Dictionary<PartyMember, int> _hpEstimates = new();
    private readonly Dictionary<PartyMember, PartyHpReading> _hpReadings = new();

    // A `par` row or @health reply replaced an estimate that had drifted from it:
    // (member name, estimated %, stated %, "par" / "@health").
    public event Action<string, int, int, string>? HpEstimateCorrected;

    // True when some member other than us can be estimated — joined, with a max HP
    // from the @health exchange and a percentage seen. Every line checks this first, so
    // a solo player or a party of non-MudPlay members costs nothing.
    public bool HasHpEstimableMember()
    {
        foreach (PartyMember m in State.Members)
            if (IsHpEstimable(m)) return true;
        return false;
    }

    // Move a member's HP by delta (damage negative, heals positive). Returns the member
    // and their new estimate, or null when the name isn't an estimable member. Clamped
    // to 0..max; the written percentage never drops below 1, because 0 means "not seen
    // yet" to PartyVitalsWatcher.
    public (string Name, int Hp, int MaxHp)? AdjustMemberHp(string name, int delta)
    {
        if (string.IsNullOrEmpty(name) || delta == 0) return null;
        string given = GivenNameOf(name.Trim());
        foreach (PartyMember m in State.Members)
        {
            if (!IsHpEstimable(m)) continue;
            if (!GivenNameOf(m.Name).Equals(given, StringComparison.OrdinalIgnoreCase)) continue;
            int max = m.BaselineHp;
            int current = _hpEstimates.TryGetValue(m, out int est) ? est : max * m.HpPercent / 100;
            int next = Math.Clamp(current + delta, 0, max);
            _hpEstimates[m] = next;
            m.HpPercent = Math.Max(1, next * 100 / max);
            return (m.Name, next, max);
        }
        return null;
    }

    // The member's running estimate (null when none is in play) and the last HP the
    // game stated for them — the bug report's between-polls readout.
    public (int? EstimateHp, PartyHpReading? LastReading) HpEstimateOf(PartyMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return (_hpEstimates.TryGetValue(member, out int est) ? est : null,
                _hpReadings.TryGetValue(member, out PartyHpReading r) ? r : null);
    }

    private static bool IsHpEstimable(PartyMember m)
        => !m.IsSelf && !m.IsInvited && m.BaselineHp > 0 && m.HpPercent > 0;

    // The game just stated the member's HP: the estimate gives way to it.
    private void NoteHpReading(PartyMember member, int percent, string source)
    {
        if (member.IsSelf) return;
        if (_hpEstimates.Remove(member) && member.HpPercent != percent)
            HpEstimateCorrected?.Invoke(member.Name, member.HpPercent, percent, source);
        _hpReadings[member] = new PartyHpReading(percent, _now(), source);
    }

    private void OnMembersChangedForHpEstimates(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            _hpEstimates.Clear();
            _hpReadings.Clear();
            return;
        }
        if (e.OldItems is null) return;
        foreach (object? o in e.OldItems)
        {
            if (o is not PartyMember m) continue;
            _hpEstimates.Remove(m);
            _hpReadings.Remove(m);
        }
    }

    // Set or clear a party member's ailment chip by given name. Find-only — does
    // NOT create a roster row for an unknown speaker, so a non-party player's
    // .@poisoned say can't conjure a phantom member. No-op when the named member
    // isn't present or ailment isn't one of the surfaced ailment bits (Poisoned
    // / Blinded / Confused / Diseased / MovementPrevented). Routes the write
    // through the manager so the Owner-marked PartyMember fields keep a single
    // writer (the IL scan enforces this).
    public void SetMemberAilment(string name, Models.GameData.MessageFlags ailment, bool active)
    {
        if (string.IsNullOrEmpty(name)) return;
        string given = GivenNameOf(name);
        foreach (PartyMember m in State.Members)
        {
            if (!GivenNameOf(m.Name).Equals(given, StringComparison.OrdinalIgnoreCase)) continue;
            switch (ailment)
            {
                case Models.GameData.MessageFlags.Poisoned: m.Poisoned = active; break;
                case Models.GameData.MessageFlags.Blinded:  m.Blinded  = active; break;
                case Models.GameData.MessageFlags.Confused: m.Confused = active; break;
                case Models.GameData.MessageFlags.Diseased: m.Diseased = active; break;
                case Models.GameData.MessageFlags.MovementPrevented: m.Held = active; break;
            }
            return;
        }
    }

    private void RemoveMember(string name)
    {
        string given = GivenNameOf(name);
        _followedUs.Remove(given);
        bool removedSelf = false;
        for (int i = State.Members.Count - 1; i >= 0; i--)
        {
            // Given-name comparison so short-form chat ("Raijin") matches
            // long-form par rows ("Raijin WuzHere") — see AddOrTouchMember.
            if (GivenNameOf(State.Members[i].Name).Equals(given, StringComparison.OrdinalIgnoreCase))
            {
                if (State.Members[i].IsSelf) removedSelf = true;
                State.Members.RemoveAt(i);
            }
        }
        if (State.LeaderName is { } lead
            && GivenNameOf(lead).Equals(given, StringComparison.OrdinalIgnoreCase))
            State.LeaderName = null;
        State.IsInParty = State.Members.Count > 0;
        // Only revoke self-leadership when the row removed WAS self —
        // a follower leaving doesn't change who's leading. Without this
        // a leader watching a follower disconnect would lose their own
        // leader badge and the auto-invite path would deny.
        if (removedSelf) State.SelfIsLeader = false;
    }

}
