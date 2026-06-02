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

    /// <summary>Test-friendly clock — overridable so PR 6.5 tests don't have to wait real time.</summary>
    internal Func<DateTimeOffset> NowProvider { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// par row regex — flexible enough to handle the typical MajorMUD
    /// layout (leader marker, name, optional class, two percent values,
    /// position word) while tolerating column-width variance between
    /// realms. The two <c>\d+%</c> tokens are the load-bearing anchors;
    /// rows without them aren't member rows.
    /// </summary>
    [GeneratedRegex(
        @"^\s*(?<leader>\*)?\s*(?<name>\w[\w '-]*?)(?:\s*\(You\))?\s*(?::\s*(?<class>\w+))?\s+(?<hp>\d+)%\s+(?<mp>\d+)%\s+(?<pos>\w+)\s*$",
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
        _subs.Add(_router.Subscribe(KnownPatterns.PartyStopsFollowing, OnStopsFollowing));
        _subs.Add(_router.Subscribe(KnownPatterns.PartyHeader,         OnParHeader));
        // Phase 6 PR 6.5 — disconnect / death / reconnect grace window.
        // We watch every "X just disconnected" / "X just entered the
        // Realm" line because a party member who drops while we're
        // looking has to leave the roster immediately, but if they
        // re-connect within the grace window and we're the leader we
        // auto-invite them back. PartyMemberDeath is the conservative
        // PvP-kill match.
        _subs.Add(_router.Subscribe(KnownPatterns.PlayerDisconnects,   OnPlayerDisconnects));
        _subs.Add(_router.Subscribe(KnownPatterns.PlayerEnters,        OnPlayerEnters));
        _subs.Add(_router.Subscribe(KnownPatterns.PartyMemberDeath,    OnMemberDeath));
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

    private void OnFollowsYou(MatchResult result)
    {
        // Pattern's only capture group is the player name (group 1 in the
        // regex, index 0 in MatchResult.Groups since group 0 is dropped).
        if (result.Groups.Count == 0) return;
        string name = result.Groups[0];
        if (string.IsNullOrEmpty(name)) return;
        AddOrTouchMember(name);
        State.IsInParty = State.Members.Count > 0;
    }

    private void OnStopsFollowing(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        string name = result.Groups[0];
        if (string.IsNullOrEmpty(name)) return;
        RemoveMember(name);
    }

    private void OnParHeader(MatchResult _)
    {
        _parState = ParState.ReadingRows;
        _parBlockNames.Clear();
    }

    // ----- Phase 6 PR 6.5: disconnect / death / reconnect ---------------

    /// <summary>
    /// "X just disconnected!!!." — if X is in our roster, remove them
    /// immediately and record the moment in the grace-window map so a
    /// later <c>just entered the Realm</c> within the window can
    /// auto-invite them back.
    /// </summary>
    private void OnPlayerDisconnects(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        string name = result.Groups[0];
        if (string.IsNullOrEmpty(name)) return;
        bool wasMember = false;
        foreach (PartyMember m in State.Members)
        {
            if (m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { wasMember = true; break; }
        }
        if (!wasMember) return;
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
        // Send the invite if we have a wire-sender wired. The wire
        // command is the plain MajorMUD "invite <name>" — the server
        // does the rest.
        if (_wireSender is null) return;
        byte[] bytes = System.Text.Encoding.Latin1.GetBytes($"invite {name}\r");
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
            // ("Name Class Hits Mana ...") or the separator line.
            // Stay in ReadingRows until we either see a real row or a
            // blank line; only step out on blank.
            return;
        }

        string name = m.Groups["name"].Value.Trim();
        if (name.Length == 0) return;
        bool isLeader = m.Groups["leader"].Success;
        string klass  = m.Groups["class"].Success ? m.Groups["class"].Value.Trim() : string.Empty;
        int hpPct = int.Parse(m.Groups["hp"].Value, System.Globalization.CultureInfo.InvariantCulture);
        int mpPct = int.Parse(m.Groups["mp"].Value, System.Globalization.CultureInfo.InvariantCulture);
        PlayerPosition pos = ParsePosition(m.Groups["pos"].Value);

        // "ME" is MajorMUD's marker for the locally connected character's
        // row. Treat it as IsSelf=true but skip the membership update —
        // the local player's HP / MA is tracked via PromptParser, not par.
        bool isSelf = name.Equals("ME", StringComparison.OrdinalIgnoreCase);

        _parBlockNames.Add(name);
        PartyMember member = AddOrTouchMember(name);
        member.IsLeader = isLeader;
        member.IsSelf   = isSelf;
        if (klass.Length > 0) member.Class = klass;
        member.HpPercent = hpPct;
        member.MpPercent = mpPct;
        member.Position  = pos;

        if (isLeader) State.LeaderName = name;
        State.SelfIsLeader = State.LeaderName != null
                          && State.Members.Any(x => x.IsSelf && x.IsLeader);
        State.IsInParty = State.Members.Count > 0;
    }

    // ----- Helpers --------------------------------------------------------

    private PartyMember AddOrTouchMember(string name)
    {
        foreach (PartyMember m in State.Members)
        {
            if (m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return m;
        }
        PartyMember created = new();
        created.Name = name;
        State.Members.Add(created);
        return created;
    }

    /// <summary>
    /// Set the absolute-HP / absolute-MP baseline on a named member. Called
    /// by <see cref="PartyPoller"/> (PR 6.4) after parsing a member's
    /// reply to an on-join <c>@health</c> request. Routes through the
    /// manager so the <see cref="PartyMember"/>'s
    /// <see cref="OwnerAttribute"/>-marked fields keep a single writer
    /// (the Phase 3 PR 3.5 IL scan enforces this). No-op when the named
    /// member isn't in the roster.
    /// </summary>
    public void SetMemberBaseline(string name, int hp, int mp)
    {
        if (string.IsNullOrEmpty(name)) return;
        foreach (PartyMember m in State.Members)
        {
            if (!m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            m.BaselineHp = hp;
            m.BaselineMp = mp;
            return;
        }
    }

    private void RemoveMember(string name)
    {
        bool removedSelf = false;
        for (int i = State.Members.Count - 1; i >= 0; i--)
        {
            if (State.Members[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (State.Members[i].IsSelf) removedSelf = true;
                State.Members.RemoveAt(i);
            }
        }
        if (State.LeaderName is { } lead && lead.Equals(name, StringComparison.OrdinalIgnoreCase))
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
