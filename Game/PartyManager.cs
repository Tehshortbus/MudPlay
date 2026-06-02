using System.Text.RegularExpressions;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;

namespace FujinTerm.Game;

/// <summary>
/// Writes party-membership and per-member state into <see cref="PartyState"/>
/// from observed server lines. Sole writer of every observable field on
/// <see cref="PartyState"/> and <see cref="PartyMember"/> (the Phase 3 PR 3.5
/// IL-scan test enforces this).
/// </summary>
/// <remarks>
/// <para>
/// PR 6.1 covers three input shapes:
/// </para>
/// <list type="number">
///   <item>"<c>X now follows you.</c>" — adds <c>X</c> to <see cref="PartyState.Members"/>.</item>
///   <item>"<c>X stops following you.</c>" — removes <c>X</c>.</item>
///   <item>The multi-line <c>par</c> table — parsed via a tiny state machine
///         on <see cref="LineExtractor.LineEmitted"/> (same shape as
///         <see cref="WhoListParser"/>). Each row updates the corresponding
///         <see cref="PartyMember"/> (HP%, MA%, Position, leader marker).
///         Members observed in <c>par</c> that weren't yet in the roster
///         are added; members not observed are NOT removed by <c>par</c>
///         alone (death + disconnect detection ships in PR 6.5).</item>
/// </list>
/// <para>
/// PR 6.4 layers on the on-join <c>@health</c> exchange that captures
/// <see cref="PartyMember.BaselineHp"/> / <see cref="PartyMember.BaselineMp"/>;
/// from then on the <c>par</c>-driven percentages render against real
/// absolute numbers in the UI. PR 6.5 adds the per-member status-flag
/// observation lines (poison applied, blindness cured, etc.).
/// </para>
/// <para>
/// Threading: <see cref="MessageRouter"/> already marshals to the UI
/// thread; <see cref="LineExtractor.LineEmitted"/> is forwarded on the
/// same dispatcher path. All <see cref="PartyState"/> mutations therefore
/// happen on the UI thread and the <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/>
/// raises change notifications consumers can bind to directly.
/// </para>
/// </remarks>
public sealed partial class PartyManager : IDisposable
{
    private readonly MessageRouter _router;
    private LineExtractor? _lines;
    private readonly List<IDisposable> _subs = new();
    private bool _disposed;

    /// <summary>Live party state — manager owns every observable field.</summary>
    public PartyState State { get; }

    // ----- par-block state machine -----
    private enum ParState { Idle, ReadingRows }
    private ParState _parState = ParState.Idle;
    /// <summary>Names observed in the current par block; used to skip duplicates.</summary>
    private readonly HashSet<string> _parBlockNames = new(StringComparer.OrdinalIgnoreCase);

    // ----- Phase 6 PR 6.5: disconnect grace window + auto-invite -------
    /// <summary>Disconnected members keyed by name → moment we last saw them drop. Lazy-expires on access.</summary>
    private readonly Dictionary<string, DateTimeOffset> _recentlyDisconnected
        = new(StringComparer.OrdinalIgnoreCase);
    private Action<byte[]>? _wireSender;

    /// <summary>
    /// "Wait for party members" grace window — how long after a disconnect
    /// we'll auto-invite a returning party member back. Default 30 s per
    /// MegaMUD's typical Settings.Party value; PR 6.9 wires the
    /// Settings.Party tab to make this user-configurable.
    /// </summary>
    public TimeSpan DisconnectGraceWindow { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Master switch for the PR 6.5 auto-invite-on-reconnect flow. When
    /// false, disconnect tracking still works (members are still removed
    /// from the roster on drop, still get a grace-window entry) but no
    /// <c>invite</c> command goes out when they return. Default true;
    /// PR 6.9's Settings.Party tab binds this.
    /// </summary>
    public bool AutoInviteEnabled { get; set; } = true;

    /// <summary>
    /// Local character's combat-rank preference (Settings → Party → Rank).
    /// Sent to the server as <c>frontrank</c> / <c>backrank</c> the
    /// moment the local character joins a party (<see cref="PartyState.IsInParty"/>
    /// transitions false → true). <see cref="Models.Profile.PartyRank.Mid"/>
    /// is no-op — Mid is the default rank, no command needed. AppServices
    /// pushes <c>dto.Rank</c> in via
    /// <see cref="Services.AppServices.ApplyPartyFromActiveProfile"/>.
    /// </summary>
    public Models.Profile.PartyRank LocalRankPreference { get; set; }
        = Models.Profile.PartyRank.Mid;

    /// <summary>Test-friendly clock — overridable so PR 6.5 tests don't have to wait real time.</summary>
    internal Func<DateTimeOffset> NowProvider { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// Locally-connected character's given name (matches the profile
    /// name; e.g. "Fujin" / "Raijin"). Used to detect <see cref="PartyMember.IsSelf"/>
    /// when parsing par rows whose name field is <c>"Given Family"</c>.
    /// <c>null</c> when no profile is loaded; in that case the par parser
    /// can't tell which row is us and IsSelf stays false on every row.
    /// AppServices sets this from <c>ProfileService.ProfileLoaded</c> /
    /// <c>ProfileClosed</c>.
    /// </summary>
    public string? LocalCharacterName { get; set; }

    /// <summary>
    /// par row regex — anchored on the real MajorMUD format observed on
    /// Playpen BBS:
    /// <code>
    ///   Raijin WuzHere                  (Priest)        [M:100%] [H:100%]   - Midrank
    ///   Fujin WuzHere                   (Mystic)                  [H: 96%]   - Frontrank
    ///   Raijin WuzHere                  (Priest)        [M:100%] [H: 85%]   - Backrank
    /// </code>
    /// Name is given + (optional) family. Class is in parens and can
    /// contain spaces ("High Priest" etc.). <c>[M:N%]</c> is optional —
    /// non-caster classes / display rules omit it. <c>[H:N%]</c> is
    /// load-bearing; rows without it aren't member rows.
    /// <para>
    /// IMPORTANT: the percentage is right-padded to a 3-char column.
    /// At 100% there's no padding (<c>[H:100%]</c>), at &lt;100% there's
    /// a leading space (<c>[H: 85%]</c>, <c>[H:  5%]</c>). The regex
    /// must allow that space — otherwise every non-100% row silently
    /// fails to match and HP percent stays frozen between full and
    /// empty.
    /// </para>
    /// <c>- Rank</c> is an optional trailing chip (Frontrank / Midrank /
    /// Backrank). par doesn't carry Position — that field stays at its
    /// default (Standing) for non-self members until a future PR adds a
    /// per-member status query.
    /// </summary>
    [GeneratedRegex(
        @"^\s+(?<name>\S[\w '-]*?)\s+\((?<class>[^)]+)\)\s*(?:\[M:\s*(?<mp>\d+)%\])?\s*\[H:\s*(?<hp>\d+)%\]\s*(?<state>[RM])?\s*(?:-\s*(?<rank>\w+))?",
        RegexOptions.CultureInvariant)]
    private static partial Regex ParRow();

    /// <summary>
    /// Construct with the app-singleton <see cref="MessageRouter"/> and a
    /// fresh <see cref="PartyState"/>. The per-session <see cref="LineExtractor"/>
    /// is supplied later via <see cref="AttachLineExtractor"/> — the
    /// main-window VM owns it because it lives only for the active
    /// terminal session, while the manager is app-level so consumers
    /// (the Phase 6 PR 6.2 remote-command engine, the PR 6.6 PartyWindow)
    /// can hold a stable reference.
    /// </summary>
    public PartyManager(MessageRouter router, PartyState state)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(state);
        _router = router;
        State   = state;

        _subs.Add(_router.Subscribe(KnownPatterns.PartyFollowsYou,     OnFollowsYou));
        _subs.Add(_router.Subscribe(KnownPatterns.PartyYouFollowing,   OnYouFollowing));
        _subs.Add(_router.Subscribe(KnownPatterns.PartyStopsFollowing, OnStopsFollowing));
        _subs.Add(_router.Subscribe(KnownPatterns.PartyHeader,         OnParHeader));
        // Phase 6 PR 6.5 — disconnect / death / reconnect grace window.
        // We watch every "X just disconnected" / "X just entered the
        // Realm" line because a party member who drops while we're
        // looking has to leave the roster immediately, but if they
        // re-connect within the grace window and we're the leader we
        // auto-invite them back. PartyMemberDeath is the conservative
        // PvP-kill match.
        _subs.Add(_router.Subscribe(KnownPatterns.PlayerDisconnects,        OnPlayerDisconnects));
        _subs.Add(_router.Subscribe(KnownPatterns.PlayerEnters,             OnPlayerEnters));
        _subs.Add(_router.Subscribe(KnownPatterns.PartyMemberDeath,         OnMemberDeath));
        // Dissolution signals — the per-row evictions we already had
        // (StopsFollowing / MemberDeath / Disconnect) handle individual
        // departures; these cover the uninvite + leader-removal + total-
        // dissolve cases the screenshot-reported bug exposed.
        _subs.Add(_router.Subscribe(KnownPatterns.PartyFollowerRemoved,      OnFollowerRemoved));
        _subs.Add(_router.Subscribe(KnownPatterns.PartyYouNoLongerFollowing, OnYouNoLongerFollowing));
        _subs.Add(_router.Subscribe(KnownPatterns.PartyDissolved,            OnPartyDissolved));
        // Live rank-change observation (Phase 6 follow-up) — keeps
        // PartyMember.Rank in sync the instant someone reranks, instead
        // of waiting until the next par poll catches up.
        _subs.Add(_router.Subscribe(KnownPatterns.PartyMemberRankChanged,    OnMemberRankChanged));
        _subs.Add(_router.Subscribe(KnownPatterns.PartySelfRankChanged,      OnSelfRankChanged));
    }

    /// <summary>
    /// Bind the wire-sender used for auto-invite of a reconnecting
    /// disconnected member (PR 6.5). Without it, reconnect detection
    /// still works but no <c>invite &lt;name&gt;</c> goes out — the
    /// member stays out of the party until manually re-invited.
    /// </summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    /// <summary>
    /// Bind the live <see cref="PlayerState"/> so the local
    /// character's <see cref="PartyMember"/> row stays in sync with
    /// every prompt (PromptParser writes HP/MA on every status line).
    /// Without this the self row only updates on a <c>par</c> poll,
    /// which means per-prompt damage taken between polls doesn't show
    /// in the PartyWindow.
    /// </summary>
    /// <remarks>
    /// We mirror absolute values into the same observable fields the
    /// rest of the roster uses — <see cref="PartyMember.BaselineHp"/>
    /// + <see cref="PartyMember.HpPercent"/>, etc. — so the
    /// <see cref="PartyMember.HpDisplay"/> computation works
    /// identically for self and others. For non-self members we only
    /// know percent from par + max from the on-join @health round-trip
    /// (computed-back current = max × pct / 100); for self we know
    /// exact current + max from PromptParser, and the percent is
    /// recomputed from those exact values so the display matches.
    /// </remarks>
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
            // Baseline = max (drives HpDisplay's "cur/max" formatting).
            // Percent = exact current * 100 / max — int rounding here is
            // fine because HpDisplay multiplies max back out the same
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
            return;
        }
    }

    /// <summary>
    /// Bind to the active session's <see cref="LineExtractor"/> so the
    /// par-block state machine can read row lines. Calling again with a
    /// new extractor (rare — only if the main window is rebuilt)
    /// unhooks the previous one first.
    /// </summary>
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
        foreach (IDisposable s in _subs) s.Dispose();
        _subs.Clear();
    }

    // ----- Single-line observers -----------------------------------------

    /// <summary>
    /// "X started to follow you." — X joined OUR party, so we lead.
    /// Add X to <see cref="PartyState.Members"/>, ensure self is also
    /// present (if we know our name), and flip <see cref="PartyState.SelfIsLeader"/>
    /// + <see cref="PartyState.LeaderName"/> accordingly.
    /// </summary>
    private void OnFollowsYou(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        string name = result.Groups[0];
        if (string.IsNullOrEmpty(name)) return;
        bool wasInParty = State.IsInParty;
        // Flip IsInParty FIRST so any CollectionChanged.Add subscriber
        // (PartyPoller's on-join @health round-trip in particular)
        // sees the state already consistent at the moment of the add.
        // Without this the @health request was being suppressed by
        // defensive gates that checked IsInParty before it propagated.
        State.IsInParty    = true;
        State.SelfIsLeader = true;
        State.LeaderName ??= LocalCharacterName;
        AddOrTouchMember(name);
        AddSelfIfKnown(isLeader: true);
        // Only on the false→true edge: this is the "initial join"
        // moment the rank-preference command applies to. Subsequent
        // followers joining our existing party don't re-trigger it.
        if (!wasInParty) SendRankPreferenceCommand();
    }

    /// <summary>
    /// "You are now following X." — WE joined X's party, so X leads.
    /// Add X with IsLeader=true, add self as follower if we know our
    /// name, set <see cref="PartyState.LeaderName"/> to X.
    /// </summary>
    private void OnYouFollowing(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        string leaderName = result.Groups[0];
        if (string.IsNullOrEmpty(leaderName)) return;
        bool wasInParty = State.IsInParty;
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
    }

    /// <summary>
    /// Send the rerank command (<c>frontrank</c> / <c>backrank</c>) to
    /// the server iff <see cref="LocalRankPreference"/> is non-Mid and
    /// a wire-sender is bound. Mid is the server-side default rank —
    /// no command needed when that's the preference.
    /// </summary>
    private void SendRankPreferenceCommand()
    {
        if (_wireSender is null) return;
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
        RemoveMember(name);
    }

    /// <summary>
    /// "X has been removed from your followers." — fires on the LEADER's
    /// side when the leader uninvites X (or when X self-departs). Same
    /// treatment as <see cref="OnStopsFollowing"/>: drop X from the
    /// roster. The follow-up "You are not in a party at the present
    /// time." (if it comes) handles total dissolution separately.
    /// </summary>
    private void OnFollowerRemoved(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        string name = result.Groups[0];
        if (string.IsNullOrEmpty(name)) return;
        RemoveMember(name);
    }

    /// <summary>
    /// "You are no longer following X." — fires on the FOLLOWER's side
    /// when the leader uninvites us, or when we issue our own
    /// <c>unfollow</c>. Drop X from the roster.
    /// </summary>
    private void OnYouNoLongerFollowing(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        string name = result.Groups[0];
        if (string.IsNullOrEmpty(name)) return;
        RemoveMember(name);
    }

    /// <summary>
    /// "You are not in a party at the present time." — authoritative
    /// dissolution signal. Fires after the per-row eviction lines and
    /// guarantees the whole party is gone, so we wipe state to the
    /// known-empty shape regardless of what the per-row handlers saw.
    /// Idempotent — already-empty state is a no-op.
    /// </summary>
    private void OnPartyDissolved(MatchResult _)
    {
        // Also flush the par-block state machine. Without this, _parState
        // can carry over from a previous in-party par-block (BBS output
        // doesn't emit a blank line between the par table and the next
        // prompt, so _parState stays in ReadingRows). The line that
        // follows "You are not in a party at the present time." is the
        // local character's own row in par's "solo" format
        // ("Fujin WuzHere ..."), which would otherwise match ParRow,
        // re-add us to Members, flip IsInParty back to true, and keep
        // the par poller alive even though the server just told us
        // we're alone.
        _parState = ParState.Idle;
        _parBlockNames.Clear();

        if (State.Members.Count == 0
            && !State.IsInParty
            && State.LeaderName is null
            && !State.SelfIsLeader)
        {
            return;
        }
        State.Members.Clear();
        State.LeaderName   = null;
        State.SelfIsLeader = false;
        State.IsInParty    = false;
    }

    private void OnParHeader(MatchResult _)
    {
        _parState = ParState.ReadingRows;
        _parBlockNames.Clear();
    }

    // ----- Phase 6 PR 6.5: disconnect / death / reconnect ---------------

    /// <summary>
    /// "X just disconnected!!!." — if X is in our roster, remove them.
    /// <para>
    /// <b>Follower drop:</b> evict the row and record the moment in the
    /// grace-window map so a later <c>just entered the Realm</c> within
    /// the window can auto-invite them back (when we're leader).
    /// </para>
    /// <para>
    /// <b>Leader drop:</b> the whole party dissolves per MajorMUD's
    /// game rule — leadership doesn't transfer on disconnect. Wipe
    /// the full roster via the same path <see cref="OnPartyDissolved"/>
    /// uses. No grace-window entry for a dropped leader: a returning
    /// leader has no party to be auto-re-invited into, so the entry
    /// would only mislead the reconnect handler.
    /// </para>
    /// </summary>
    private void OnPlayerDisconnects(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        string name = result.Groups[0];
        if (string.IsNullOrEmpty(name)) return;
        string given = GivenNameOf(name);
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
        RemoveMember(name);
        _recentlyDisconnected[name] = NowProvider();
    }

    /// <summary>
    /// "X just entered the Realm." — if X is in the grace-window map
    /// AND we're the party leader, auto-invite. The reconnect window
    /// is short (default 30 s) so this only fires for actual quick
    /// dropoffs, not for someone who left an hour ago.
    /// </summary>
    private void OnPlayerEnters(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        string name = result.Groups[0];
        if (string.IsNullOrEmpty(name)) return;
        if (!_recentlyDisconnected.TryGetValue(name, out DateTimeOffset droppedAt)) return;
        if (NowProvider() - droppedAt > DisconnectGraceWindow)
        {
            // Past the window — clear the stale entry and bail.
            _recentlyDisconnected.Remove(name);
            return;
        }
        _recentlyDisconnected.Remove(name);
        if (!State.SelfIsLeader) return;
        if (!AutoInviteEnabled) return;
        // Send the invite if we have a wire-sender wired. The wire
        // command is the plain MajorMUD "invite <name>" — the server
        // does the rest.
        if (_wireSender is null) return;
        // MajorMUD addresses other players by GIVEN name only — never
        // family. "invite Raijin", not "invite Raijin WuzHere".
        byte[] bytes = System.Text.Encoding.Latin1.GetBytes($"invite {GivenNameOf(name)}\r");
        _wireSender(bytes);
    }

    /// <summary>
    /// "X has been slain by Y." — if X is in our roster, remove them
    /// immediately. No grace window for death (death isn't recoverable
    /// the way a disconnect is — the leader doesn't auto-invite a corpse).
    /// </summary>
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

    /// <summary>Test seam — read-only view of the disconnect grace window.</summary>
    internal IReadOnlyDictionary<string, DateTimeOffset> RecentlyDisconnected => _recentlyDisconnected;

    // ----- Live rank-change observers -----------------------------------

    /// <summary>
    /// "X just moved to the {front|back} rank in your group." /
    /// "X just moved to the middle of your group." — update the named
    /// member's <see cref="PartyMember.Rank"/> immediately so the
    /// PartyWindow rank chip reflects the new rank without waiting for
    /// the next par poll. No-op when the named player isn't in the
    /// roster (defensive — covers a race where a rerank line arrives
    /// for a member we just dropped).
    /// </summary>
    private void OnMemberRankChanged(MatchResult result)
    {
        if (result.Groups.Count < 2) return;
        string name = result.Groups[0];
        if (string.IsNullOrEmpty(name)) return;
        ApplyRankByGivenName(name, result.Groups[1]);
    }

    /// <summary>
    /// "You have moved to the {front|middle|back} ranks of your group." —
    /// self's own rerank confirmation. No name in the message; we
    /// locate the row by <see cref="PartyMember.IsSelf"/> (set by the
    /// par-row parser whenever the local character appears).
    /// </summary>
    private void OnSelfRankChanged(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        Models.Profile.PartyRank rank = ParseRankWord(result.Groups[0]);
        foreach (PartyMember m in State.Members)
        {
            if (m.IsSelf) { m.Rank = rank; return; }
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

    private void OnLineEmitted(LineExtractor.EmittedLine line) => HandleLine(line.Text);

    /// <summary>
    /// Test seam — feeds the par-block state machine without spinning up
    /// a real <see cref="LineExtractor"/>. Tests prime the state via
    /// <see cref="TestEnterParBlock"/> then pump rows here. Same shape as
    /// <see cref="WhoListParser.FeedTestLines"/>.
    /// </summary>
    internal void FeedTestLines(IEnumerable<string> lines)
    {
        foreach (string text in lines) HandleLine(text);
    }

    /// <summary>Test seam — flips the state machine into ReadingRows without dispatching the par-header pattern.</summary>
    internal void TestEnterParBlock()
    {
        _parState = ParState.ReadingRows;
        _parBlockNames.Clear();
    }

    private void HandleLine(string text)
    {
        if (_parState != ParState.ReadingRows) return;
        // Blank line ends the par block. Don't reset the parState until
        // we've seen the terminator so a mid-block blank doesn't kill the
        // parser, but in practice the par table is contiguous.
        if (string.IsNullOrWhiteSpace(text))
        {
            _parState = ParState.Idle;
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
        // [M:N%] bracket is omitted when the class has no mana (Warriors,
        // level-1 Mystics with 0 Kai, etc.). Absent = leave MpPercent
        // unchanged on existing members; default 0 on new ones.
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
        // ("Fujin" vs "Fujin WuzHere"), IsSelf would stay false on
        // OUR OWN row, and PartyPoller.OnMembersChanged would telepath
        // /Fujin @health to us — the exact spam the screenshot showed.
        bool isSelf = false;
        if (!string.IsNullOrEmpty(LocalCharacterName))
        {
            isSelf = GivenNameOf(name)
                .Equals(GivenNameOf(LocalCharacterName), StringComparison.OrdinalIgnoreCase);
        }

        _parBlockNames.Add(name);
        PartyMember member = AddOrTouchMember(name);
        member.IsSelf = isSelf;
        if (klass.Length > 0) member.Class = klass;
        member.HpPercent = hpPct;
        if (mpPct is { } v) member.MpPercent = v;
        member.Position = position;
        member.Rank     = rank;
        // Mirror the par-state into the boolean flags so the PartyWindow
        // status-chip strip (which keys on these booleans) lights up too.
        member.Resting    = position == PlayerPosition.Resting;
        member.Meditating = position == PlayerPosition.Meditating;

        State.IsInParty = State.Members.Count > 0;
    }

    /// <summary>
    /// Ensure self is represented in <see cref="PartyState.Members"/> with
    /// the leader marker set per <paramref name="isLeader"/>. No-op when
    /// <see cref="LocalCharacterName"/> is null (we don't know our name
    /// well enough to disambiguate from other rows). Used by follows-you
    /// / you-following handlers so the PartyWindow shows us immediately
    /// without waiting for the next par observation.
    /// </summary>
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

    /// <summary>
    /// Find the existing member whose given name (first whitespace token)
    /// matches the given name in <paramref name="name"/>, or add a fresh
    /// row. Roster matching is by GIVEN name only because MajorMUD
    /// addresses players different ways at different times — chat lines
    /// use short form ("Raijin"), par output uses long form
    /// ("Raijin WuzHere"). Storing both would create duplicate rows.
    /// </summary>
    /// <remarks>
    /// Name field always upgrades to the LONGER form when observed —
    /// once we see "Raijin WuzHere" in par, the row's Name becomes
    /// "Raijin WuzHere" even if it was first added as just "Raijin"
    /// via follows-you. Going shorter is no-op (don't downgrade).
    /// </remarks>
    private PartyMember AddOrTouchMember(string name)
    {
        string given = GivenNameOf(name);
        foreach (PartyMember m in State.Members)
        {
            if (GivenNameOf(m.Name).Equals(given, StringComparison.OrdinalIgnoreCase))
            {
                // Upgrade to the longer form if applicable.
                if (name.Length > m.Name.Length) m.Name = name;
                return m;
            }
        }
        PartyMember created = new();
        created.Name = name;
        State.Members.Add(created);
        return created;
    }

    /// <summary>First whitespace-delimited token. "Raijin WuzHere" → "Raijin"; "Raijin" → "Raijin".</summary>
    private static string GivenNameOf(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }

    /// <summary>
    /// Record a member's on-join <c>@health</c> snapshot. The reply has
    /// the shape <c>{HP=cur/max,MA=cur/max}</c> — we store the max as
    /// <see cref="PartyMember.BaselineHp"/> / <see cref="PartyMember.BaselineMp"/>
    /// AND compute <see cref="PartyMember.HpPercent"/> /
    /// <see cref="PartyMember.MpPercent"/> from <c>cur</c> so the row
    /// shows a meaningful bar immediately, without waiting for the next
    /// par poll to fill in the percentage. (Earlier shape only took the
    /// max, leaving the row stuck at "H:0/36 0%" until par caught up.)
    /// Routes through the manager so the
    /// <see cref="OwnerAttribute"/>-marked fields keep a single writer
    /// (the Phase 3 PR 3.5 IL scan enforces this). No-op when the named
    /// member isn't in the roster. <paramref name="mpMax"/> = 0 marks a
    /// no-mana class (Warriors) — both baseline and percent stay 0 and
    /// the PartyWindow hides the MA sub-row entirely via
    /// <c>GreaterThanZeroConverter</c>.
    /// </summary>
    public void SetMemberHealthSnapshot(string name, int hpCur, int hpMax, int mpCur, int mpMax)
    {
        if (string.IsNullOrEmpty(name)) return;
        string given = GivenNameOf(name);
        foreach (PartyMember m in State.Members)
        {
            if (!GivenNameOf(m.Name).Equals(given, StringComparison.OrdinalIgnoreCase)) continue;
            m.BaselineHp = hpMax;
            m.BaselineMp = mpMax;
            m.HpPercent  = hpMax > 0 ? hpCur * 100 / hpMax : 0;
            m.MpPercent  = mpMax > 0 ? mpCur * 100 / mpMax : 0;
            return;
        }
    }

    private void RemoveMember(string name)
    {
        string given = GivenNameOf(name);
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
        // leader badge and the PR 6.5 auto-invite path would deny.
        if (removedSelf) State.SelfIsLeader = false;
    }

    private static PlayerPosition ParsePosition(string raw) => raw.ToLowerInvariant() switch
    {
        "resting"    => PlayerPosition.Resting,
        "meditating" => PlayerPosition.Meditating,
        _            => PlayerPosition.Standing,
    };
}
