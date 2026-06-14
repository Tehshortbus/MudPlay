using System.Collections.Generic;
using System.Text.RegularExpressions;
using FujinTerm.Game.Map;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Combat;

/// <summary>
/// Phase 9 PR 9.0b — turns the wire's <c>Also here:</c> line into a
/// classified entity list (Player / Monster / Unknown). The output
/// feeds <c>CombatStateTracker</c> (PR 9.0b sub-C) — which holds the
/// <c>Combat</c> gate on <see cref="Game.Map.MovementCoordinator"/>
/// while any classified Monster carries non-zero AttackPriority — and
/// the LogPane's double-click-to-fix flow for unknown names.
/// </summary>
/// <remarks>
/// <para>
/// Algorithm per docs/10-phase-9 § Cross-cut 2:
/// </para>
/// <list type="number">
/// <item>Split the comma-separated occupant capture, normalising the
/// Oxford <c>" and "</c> form into a comma the same way
/// <see cref="AutoPartyManager"/> does.</item>
/// <item>For each entry: try direct match against every
/// <see cref="MonsterMessageRecord.Name"/> where
/// <see cref="MonsterMessageRecord.AllowNoPrefix"/> is true; else try
/// the prefix-stripped form against every monster's
/// <see cref="MonsterMessageRecord.FlavorPrefixes"/>.</item>
/// <item>If no monster match, fall through to player lookup: the
/// entry's first whitespace token compared against every
/// <see cref="PlayerRecord.GivenName"/> in the active per-BBS
/// database (case-insensitive).</item>
/// <item>Else Unknown — emit a Warn-severity log row with the raw
/// <c>Also here:</c> line carried as <see cref="LogEntry.Context"/>.
/// PR 9.0b sub-D's LogPane double-click handler opens the
/// <see cref="ViewModels.UnknownEntityFixDialogViewModel"/> from this
/// row.</item>
/// </list>
/// <para>
/// Performance: ~1100 monster records in a typical realm × ~5 entries
/// per Also-Here line × ~prefix-list-size-per-record ≈ ~50k string
/// comparisons in the worst pathological case. Naive scan is fine for
/// the post-Phase-9 hot path (rooms display every few seconds at most);
/// a prefix-aware lookup index can replace this if profiling shows it
/// matters.
/// </para>
/// </remarks>
public sealed class RoomEntityClassifier : IDisposable
{
    /// <summary>LogService category for all rows this classifier emits.</summary>
    public const string LogCategory = "RoomClassifier";

    private static readonly Regex AndNormaliser = new(@"\s+and\s+", RegexOptions.Compiled);

    private readonly MessageRouter _router;
    private readonly MonsterMessageStore _monsters;
    private readonly PlayerDatabase _players;
    private readonly RoomTracker? _roomTracker;
    private readonly LogService? _log;
    private readonly IDisposable _alsoHereSub;
    private Terminal.LineExtractor? _lines;
    private string? _alsoHereBuffer;     // multi-line continuation
    private string? _alsoHereRawFirst;   // raw line that started the buffer
    private bool _disposed;

    /// <summary>Fires after each successful <c>Also here:</c> parse.
    /// Subscribers run on the MessageRouter's marshalled thread — the
    /// UI thread in normal app use; the test thread under xUnit.</summary>
    public event Action<RoomEntitiesObservation>? EntitiesObserved;

    /// <summary>Last observation parsed, or <c>null</c> when no
    /// <c>Also here:</c> line has been seen this session.</summary>
    public RoomEntitiesObservation? Current { get; private set; }

    public RoomEntityClassifier(
        MessageRouter router,
        MonsterMessageStore monsters,
        PlayerDatabase players,
        LogService? log = null)
        : this(router, monsters, players, roomTracker: null, log) { }

    /// <summary>
    /// Construct with a <see cref="RoomTracker"/> binding so the
    /// classifier wipes its observation when the player moves rooms.
    /// Without this, a stale Also-Here from the previous room would
    /// keep CombatManager swinging at a target that didn't follow us
    /// in — the user's "wasted combat round on move" scenario.
    /// </summary>
    public RoomEntityClassifier(
        MessageRouter router,
        MonsterMessageStore monsters,
        PlayerDatabase players,
        RoomTracker? roomTracker,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(monsters);
        ArgumentNullException.ThrowIfNull(players);
        _router   = router;
        _monsters = monsters;
        _players  = players;
        _roomTracker = roomTracker;
        _log      = log;
        _alsoHereSub = _router.Subscribe(KnownPatterns.RoomAlsoHere, OnRoomAlsoHere);
        if (_roomTracker is not null)
            _roomTracker.StateChanged += OnRoomTrackerStateChanged;
    }

    /// <summary>
    /// Bind to the per-session <see cref="Terminal.LineExtractor"/> so
    /// the classifier can stitch wrapped "Also here:" lines back
    /// together. The MajorMUD server wraps occupant lists at the
    /// 80-column boundary, so the regex-based MessageRouter pattern
    /// fires only when the list fits on one row. The fallback path
    /// here buffers everything from "Also here:" until a line ends
    /// with "." then re-feeds the joined text through the parse.
    /// Idempotent — re-attaching to the same extractor is a no-op.
    /// </summary>
    public void AttachLineExtractor(Terminal.LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (ReferenceEquals(_lines, lines)) return;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    private void OnLine(Terminal.LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine) return;
        string text = line.Text.TrimEnd();
        if (text.Length == 0) return;

        if (_alsoHereBuffer is not null)
        {
            // Continuation: append (space-separated) to the buffer.
            _alsoHereBuffer = _alsoHereBuffer + " " + text;
            if (text.EndsWith(".", StringComparison.Ordinal))
            {
                string complete = _alsoHereBuffer;
                string raw      = _alsoHereRawFirst ?? complete;
                _alsoHereBuffer = null;
                _alsoHereRawFirst = null;
                ProcessAlsoHere(complete, raw);
            }
            return;
        }

        if (text.StartsWith("Also here:", StringComparison.Ordinal))
        {
            if (text.EndsWith(".", StringComparison.Ordinal))
            {
                // Single line — pattern subscription will also fire
                // for this; skip here to avoid double-processing.
                return;
            }
            _alsoHereBuffer = text;
            _alsoHereRawFirst = line.Text;
        }
    }

    private void ProcessAlsoHere(string completeLine, string rawFirst)
    {
        // Strip the "Also here: " prefix and the trailing period.
        const string prefix = "Also here:";
        int start = prefix.Length;
        while (start < completeLine.Length && completeLine[start] == ' ') start++;
        string body = completeLine[start..].TrimEnd();
        if (body.EndsWith(".", StringComparison.Ordinal))
            body = body[..^1];

        List<RoomEntity> entities = new();
        foreach (string raw in SplitOccupantList(body))
        {
            string cleaned = StripTrailingNoise(raw);
            if (cleaned.Length == 0) continue;
            entities.Add(Classify(cleaned, rawFirst));
        }

        RoomEntitiesObservation obs = new(rawFirst, entities, DateTimeOffset.Now);
        Current = obs;
        EntitiesObserved?.Invoke(obs);
    }

    private void OnRoomAlsoHere(MatchResult match)
    {
        // (?<players>.+?) capture: comma-separated occupant list.
        if (match.Groups.Count == 0) return;
        string list = match.Groups[0];
        if (string.IsNullOrWhiteSpace(list)) return;

        string rawLine = match.Text;
        List<RoomEntity> entities = new();

        foreach (string raw in SplitOccupantList(list))
        {
            string cleaned = StripTrailingNoise(raw);
            if (cleaned.Length == 0) continue;
            entities.Add(Classify(cleaned, rawLine));
        }

        RoomEntitiesObservation obs = new(rawLine, entities, DateTimeOffset.Now);
        Current = obs;
        EntitiesObserved?.Invoke(obs);
    }

    /// <summary>
    /// Public for direct callers (the sub-G fix dialog can re-classify
    /// a fixed-up name to confirm the prefix took). Skips the
    /// MessageRouter subscription path.
    /// </summary>
    public RoomEntity Classify(string entry) => Classify(entry, rawAlsoHereLine: string.Empty);

    /// <summary>
    /// Append a single freshly-arrived entity to the current room
    /// observation and re-fire <see cref="EntitiesObserved"/>. Called
    /// by <see cref="RoomEntryWatcher"/> when the wire reports
    /// <c>"&lt;name&gt; &lt;verb&gt; into the room from &lt;dir&gt;."</c>
    /// — the new entity slots into the existing
    /// <see cref="RoomEntitiesObservation.Entities"/> list so
    /// downstream consumers (CombatStateTracker re-evaluating the
    /// Combat gate, CombatManager re-picking by priority) see the
    /// updated room state without waiting for a full re-display.
    /// </summary>
    /// <remarks>
    /// If no <see cref="Current"/> observation exists yet (player
    /// just connected, mob spawned before the first Also-Here line),
    /// synthesises a fresh observation with an empty raw line. The
    /// caller's <paramref name="rawWireLine"/> is captured for the
    /// new observation's raw-line field so consumers that read it
    /// (LogPane click-to-fix) see the arrival line rather than the
    /// stale Also-Here.
    /// </remarks>
    public void AppendArrivalEntity(RoomEntity entity, string rawWireLine)
    {
        IReadOnlyList<RoomEntity> baseEntities = Current is { } cur
            ? cur.Entities
            : Array.Empty<RoomEntity>();

        List<RoomEntity> updated = new(baseEntities.Count + 1);
        updated.AddRange(baseEntities);
        updated.Add(entity);

        RoomEntitiesObservation obs = new(
            rawWireLine, updated, DateTimeOffset.Now, RoomObservationSource.Arrival);
        Current = obs;
        EntitiesObserved?.Invoke(obs);
    }

    private RoomEntity Classify(string entry, string rawAlsoHereLine)
    {
        // Pass 1 — monster match (direct + prefix-stripped).
        if (TryMatchMonster(entry, out MonsterMessageRecord? mm))
            return new RoomEntity(entry, mm!.Name, EntityKind.Monster, ResolveMonsterNumber(mm));

        // Pass 2 — player match (first whitespace token vs GivenName).
        string given = FirstToken(entry);
        if (given.Length > 0 && TryMatchPlayer(given, out PlayerRecord? pr))
            return new RoomEntity(entry, pr!.GivenName, EntityKind.Player, MonsterNumber: null);

        // Unknown — surface to the LogPane so the user can double-click
        // to copy the raw line + open the fix dialog.
        if (_log is not null && rawAlsoHereLine.Length > 0)
        {
            _log.Warn(LogCategory,
                $"unknown entity '{entry}' — double-click to fix",
                context: rawAlsoHereLine);
        }
        return new RoomEntity(entry, entry, EntityKind.Unknown, MonsterNumber: null);
    }

    private bool TryMatchMonster(string entry, out MonsterMessageRecord? hit)
    {
        // Direct match: AllowNoPrefix records whose Name == entry.
        // Records with no flavor prefixes are implicitly bare-name-only
        // — without this fallback, a record carrying AllowNoPrefix=false
        // AND FlavorPrefixes=[] is unreachable: the prefix-stripped
        // path below skips empty-list records, and the direct path
        // here would reject it. 535 of 1100 v1.11p seed entries
        // (cave bear, shade, Colin, Lady Sentara, …) were silently
        // unmatchable before this guard.
        foreach (MonsterMessageRecord m in _monsters.Messages)
        {
            if ((m.AllowNoPrefix || m.FlavorPrefixes.Count == 0) &&
                string.Equals(m.Name, entry, StringComparison.OrdinalIgnoreCase))
            {
                hit = m;
                return true;
            }
        }

        // Prefix-stripped match: "{prefix} {Name}" forms across every
        // monster's FlavorPrefixes list.
        foreach (MonsterMessageRecord m in _monsters.Messages)
        {
            if (m.FlavorPrefixes.Count == 0) continue;
            foreach (string prefix in m.FlavorPrefixes)
            {
                if (prefix.Length == 0) continue;
                // Length check before substring extraction so prefix > entry
                // doesn't ToString-allocate.
                int composed = prefix.Length + 1 + m.Name.Length;
                if (entry.Length != composed) continue;
                if (entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    entry[prefix.Length] == ' ' &&
                    MemoryExtensions.Equals(
                        entry.AsSpan(prefix.Length + 1),
                        m.Name.AsSpan(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    hit = m;
                    return true;
                }
            }
        }

        hit = null;
        return false;
    }

    /// <summary>
    /// The <see cref="MonsterMessageRecord"/> carries a back-reference
    /// to the Monsters-table row via <see cref="MonsterMessageRecord.Links"/>;
    /// typically a single <c>(Monsters, N)</c> entry. Returns the first
    /// such number, or <c>null</c> when the record has none (a
    /// user-curated entry not bound to a specific monster row).
    /// </summary>
    private static int? ResolveMonsterNumber(MonsterMessageRecord m)
    {
        if (m.Links is null) return null;
        foreach (GameDataLink link in m.Links)
        {
            if (string.Equals(link.Table, "Monsters", StringComparison.OrdinalIgnoreCase))
                return link.Number;
        }
        return null;
    }

    private bool TryMatchPlayer(string givenName, out PlayerRecord? hit)
    {
        foreach (PlayerRecord p in _players.Players)
        {
            if (string.Equals(p.GivenName, givenName, StringComparison.OrdinalIgnoreCase))
            {
                hit = p;
                return true;
            }
        }
        hit = null;
        return false;
    }

    /// <summary>
    /// Mirror of <see cref="AutoPartyManager.SplitOccupantList"/>'s
    /// Oxford-comma-aware split. Duplicated rather than reused because
    /// the AutoParty helper is private + this consumer needs the same
    /// semantics; the alternative is hoisting the helper into a shared
    /// utility, which inflates the surface for a 12-line function.
    /// </summary>
    private static IEnumerable<string> SplitOccupantList(string list)
    {
        string normalised = AndNormaliser.Replace(list, ", ");
        foreach (string part in normalised.Split(','))
        {
            string trimmed = part.Trim();
            if (trimmed.Length > 0) yield return trimmed;
        }
    }

    /// <summary>
    /// "Raijin (sneaking)" → "Raijin"; "nasty giant rat." → "nasty
    /// giant rat". Strips a trailing parenthetical AND any non-letter
    /// trailing characters. Preserves spaces inside the name so multi-
    /// word monster names ("giant rat") survive.
    /// </summary>
    private static string StripTrailingNoise(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        // Cut a trailing parenthetical first.
        int paren = raw.LastIndexOf('(');
        if (paren > 0) raw = raw[..paren].TrimEnd();
        // Then trim non-letter, non-space tail (period, comma…).
        int cut = raw.Length;
        while (cut > 0 && !char.IsLetterOrDigit(raw[cut - 1]) && raw[cut - 1] != ' ') cut--;
        return cut <= 0 ? string.Empty : raw[..cut];
    }

    private static string FirstToken(string s)
    {
        int sp = s.IndexOf(' ');
        return sp >= 0 ? s[..sp] : s;
    }

    private void OnRoomTrackerStateChanged(RoomTransition transition)
    {
        // Only act on true location changes — confidence-only flips
        // (Located ↔ Lost without a real room swap) don't qualify.
        if (transition.PreviousRoom is null) return;     // initial location set
        if (transition.NewRoom is null) return;
        if (ReferenceEquals(transition.PreviousRoom, transition.NewRoom)) return;
        if (transition.PreviousRoom.Key.Equals(transition.NewRoom.Key)) return;

        // Wire order within a room display is name → "Also here:" →
        // "Obvious exits:", and RoomTracker only CONFIRMS the move on the
        // exits line. So by the time this confirmed transition fires, the
        // new room's occupants have ALREADY been parsed into Current. A
        // blind wipe here nulls CombatManager's just-picked target and
        // burns a second attack when the room re-displays. Only wipe when
        // Current is genuinely STALE — observed before the move that
        // produced this transition. A post-move observation is the new
        // room's own data; keep it. (DateTimeOffset comparison is
        // offset-aware, so the classifier's local .Now timestamps and the
        // tracker's UTC move time compare on the same absolute instant.)
        if (_roomTracker is { LastMoveSentAt: { } moveAt }
            && Current is { Entities.Count: > 0 } cur
            && cur.At >= moveAt)
        {
            return;
        }

        NoteRoomChanged();
    }

    /// <summary>
    /// Remove ONE entity matching <paramref name="monsterName"/>
    /// (case-insensitive match against <see cref="RoomEntity.ResolvedName"/>
    /// then <see cref="RoomEntity.RawName"/> as fallback) from
    /// <see cref="Current"/> and re-fire <see cref="EntitiesObserved"/>.
    /// Called by <see cref="MonsterDeathWatcher"/> when a specific
    /// death-line pattern is observed so CombatManager doesn't sit
    /// on a stale entity that already died — the bug behind the
    /// "fierce kobold thief arrived but no attack" scenario where the
    /// just-killed "giant rat" still appeared in the Current list and
    /// blocked the target re-pick.
    /// </summary>
    /// <param name="monsterName">The dead monster's name. The
    /// death-line patterns use the base / canonical name (no flavour
    /// prefix), so resolved-name matches first; raw-name match
    /// covers the edge case where a no-prefix monster carries the
    /// same string in both fields.</param>
    /// <returns><c>true</c> when a matching entity was removed,
    /// <c>false</c> otherwise (defensive caller logging).</returns>
    public bool RemoveDeadEntity(string monsterName)
    {
        if (string.IsNullOrWhiteSpace(monsterName)) return false;
        if (Current is not { } cur) return false;

        int removeIndex = -1;
        for (int i = 0; i < cur.Entities.Count; i++)
        {
            RoomEntity e = cur.Entities[i];
            if (e.Kind != EntityKind.Monster) continue;
            if (string.Equals(e.ResolvedName, monsterName, StringComparison.OrdinalIgnoreCase)
             || string.Equals(e.RawName,      monsterName, StringComparison.OrdinalIgnoreCase))
            {
                removeIndex = i;
                break;
            }
        }
        if (removeIndex < 0) return false;

        List<RoomEntity> updated = new(cur.Entities.Count - 1);
        for (int i = 0; i < cur.Entities.Count; i++)
            if (i != removeIndex) updated.Add(cur.Entities[i]);

        RoomEntitiesObservation obs = new(
            cur.RawAlsoHereLine, updated, DateTimeOffset.Now, RoomObservationSource.Death);
        Current = obs;
        EntitiesObserved?.Invoke(obs);
        return true;
    }

    /// <summary>
    /// Wipe <see cref="Current"/> and re-fire <see cref="EntitiesObserved"/>
    /// with an empty observation — drives CombatManager to clear its
    /// target so the next round doesn't waste a swing on a monster
    /// that didn't follow us in. The next Also-Here parse (arrives
    /// within milliseconds of the move) rebuilds the list for the
    /// new room and CombatManager picks afresh.
    /// </summary>
    /// <remarks>
    /// Public so callers other than <see cref="RoomTracker"/> can
    /// drive the wipe (tests, future forced-refresh paths).
    /// Idempotent on already-empty observations.
    /// </remarks>
    public void NoteRoomChanged()
    {
        RoomEntitiesObservation wiped = new(
            RawAlsoHereLine: string.Empty,
            Entities: Array.Empty<RoomEntity>(),
            At: DateTimeOffset.Now,
            Source: RoomObservationSource.RoomChange);
        Current = wiped;
        EntitiesObserved?.Invoke(wiped);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _alsoHereSub.Dispose();
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = null;
        if (_roomTracker is not null)
            _roomTracker.StateChanged -= OnRoomTrackerStateChanged;
    }
}
