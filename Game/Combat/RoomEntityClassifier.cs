using System.Collections.Generic;
using System.Text.RegularExpressions;
using FujinTerm.Game.Map;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Combat;

// Turns the wire's "Also here:" line into a classified entity list (Player /
// Monster / Unknown). The output feeds CombatStateTracker — which holds the
// Combat gate on MovementCoordinator while any classified Monster carries
// non-zero AttackPriority — and the LogPane's double-click-to-fix flow for
// unknown names.
//
// Algorithm:
// 1. Split the comma-separated occupant capture, normalising the Oxford " and "
//    form into a comma the same way AutoPartyManager does.
// 2. For each entry: try direct match against every MonsterMessageRecord.Name
//    where AllowNoPrefix is true; else try the prefix-stripped form against
//    every monster's FlavorPrefixes.
// 3. If no monster match, fall through to player lookup: the entry's first
//    whitespace token compared against every PlayerRecord.GivenName in the
//    active per-BBS database (case-insensitive).
// 4. Else Unknown — emit a Warn-severity log row with the raw "Also here:" line
//    carried as LogEntry.Context. The LogPane double-click handler opens the
//    UnknownEntityFixDialogViewModel from this row.
//
// Performance: ~1100 monster records in a typical realm × ~5 entries per
// Also-Here line × ~prefix-list-size-per-record ≈ ~50k string comparisons in the
// worst pathological case. Naive scan is fine for the hot path (rooms display
// every few seconds at most); a prefix-aware lookup index can replace this if
// profiling shows it matters.
public sealed class RoomEntityClassifier : IDisposable
{
    // LogService category for all rows this classifier emits.
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
    private DateTimeOffset? _lastAlsoHereAt;  // when the last real "Also here:" parsed
    private bool _disposed;

    // Late-wired flee probe (HealthManager is constructed after this classifier,
    // so it can't come through the ctor). Returns true while we're actively
    // fleeing — in that window a pursuing monster must NOT be kept across the room
    // change, so the confirmed-move wipe stands and we keep running rather than
    // halting to fight. Null/unset reads as "not fleeing" (stop-and-fight), the
    // normal case.
    public Func<bool>? FleeProbe { get; set; }

    // Fires after each successful "Also here:" parse. Subscribers run on the
    // MessageRouter's marshalled thread — the UI thread in normal app use; the
    // test thread under xUnit.
    public event Action<RoomEntitiesObservation>? EntitiesObserved;

    // Last observation parsed, or null when no "Also here:" line has been seen
    // this session.
    public RoomEntitiesObservation? Current { get; private set; }

    public RoomEntityClassifier(
        MessageRouter router,
        MonsterMessageStore monsters,
        PlayerDatabase players,
        LogService? log = null)
        : this(router, monsters, players, roomTracker: null, log) { }

    // Construct with a RoomTracker binding so the classifier wipes its
    // observation when the player moves rooms. Without this, a stale Also-Here
    // from the previous room would keep CombatManager swinging at a target that
    // didn't follow us in — the "wasted combat round on move" scenario.
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

    // Bind to the per-session LineExtractor so the classifier can stitch wrapped
    // "Also here:" lines back together. The MajorMUD server wraps occupant lists
    // at the 80-column boundary, so the regex-based MessageRouter pattern fires
    // only when the list fits on one row. The fallback path here buffers
    // everything from "Also here:" until a line ends with "." then re-feeds the
    // joined text through the parse. Idempotent — re-attaching to the same
    // extractor is a no-op.
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

    // A `look <dir>` peek renders a full room display — name, "You notice…",
    // "Also here:", "Obvious exits:" — for the ADJACENT room the player never
    // entered. RoomTracker arms a suppression window on the outbound look and
    // consumes it on the exits line, but the "Also here:" line arrives first, so
    // this check still sees the armed flag. Emitting EntitiesObserved for a peek
    // would assert the combat gate against a room we're not in (and pollute
    // Current so the real walk-in's re-parse can't re-engage). Skip it — the
    // genuine walk-in re-fires this parse with the flag cleared.
    private bool IsPeek() => _roomTracker?.IsPeekSuppressed() ?? false;

    private void ProcessAlsoHere(string completeLine, string rawFirst)
    {
        if (IsPeek())
        {
            _log?.Debug(LogCategory, "dropped peeked Also-here (look-direction preview)");
            return;
        }

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
        _lastAlsoHereAt = obs.At;
        EntitiesObserved?.Invoke(obs);
    }

    private void OnRoomAlsoHere(MatchResult match)
    {
        if (IsPeek())
        {
            _log?.Debug(LogCategory, "dropped peeked Also-here (look-direction preview)");
            return;
        }

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
        _lastAlsoHereAt = obs.At;
        EntitiesObserved?.Invoke(obs);
    }

    // Public for direct callers (the fix dialog can re-classify a fixed-up name
    // to confirm the prefix took). Skips the MessageRouter subscription path.
    public RoomEntity Classify(string entry) => Classify(entry, rawAlsoHereLine: string.Empty);

    // Resolve a looked-at monster name to a specific Monsters-table Number.
    // Shared display names (~14% of a stock realm — "giant rat", "skeleton", …)
    // map to several Numbers with different HP, so a name-only classify can pick
    // the wrong variant. Prefer the variant ACTUALLY present in the room: the
    // Current observation already resolved each occupant to its own Number when
    // the room displayed, and a looked-at monster is by definition in the room.
    // Match the look name against those first (raw or resolved, case-insensitive),
    // then fall back to a global classify for the rare case the room list is
    // stale/empty. Returns null when the name isn't a known monster.
    public int? ResolveLookedMonsterNumber(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string trimmed = name.Trim();

        if (Current is { } cur)
        {
            foreach (RoomEntity e in cur.Entities)
            {
                if (e.Kind != EntityKind.Monster || e.MonsterNumber is null) continue;
                if (string.Equals(e.RawName, trimmed, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(e.ResolvedName, trimmed, StringComparison.OrdinalIgnoreCase))
                    return e.MonsterNumber;
            }
        }

        RoomEntity classified = Classify(trimmed);
        return classified.Kind == EntityKind.Monster ? classified.MonsterNumber : null;
    }

    // Append a single freshly-arrived entity to the current room observation and
    // re-fire EntitiesObserved. Called by RoomEntryWatcher when the wire reports
    // "<name> <verb> into the room from <dir>." — the new entity slots into the
    // existing observation's Entities list so downstream consumers
    // (CombatStateTracker re-evaluating the Combat gate, CombatManager re-picking
    // by priority) see the updated room state without waiting for a full
    // re-display.
    //
    // If no Current observation exists yet (player just connected, mob spawned
    // before the first Also-Here line), synthesises a fresh observation with an
    // empty raw line. The caller's rawWireLine is captured for the new
    // observation's raw-line field so consumers that read it (LogPane
    // click-to-fix) see the arrival line rather than the stale Also-Here.
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

    // The MonsterMessageRecord carries a back-reference to the Monsters-table row
    // via its Links; typically a single (Monsters, N) entry. Returns the first
    // such number, or null when the record has none (a user-curated entry not
    // bound to a specific monster row).
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

    // Mirror of AutoPartyManager.SplitOccupantList's Oxford-comma-aware split.
    // Duplicated rather than reused because the AutoParty helper is private + this
    // consumer needs the same semantics; the alternative is hoisting the helper
    // into a shared utility, which inflates the surface for a 12-line function.
    private static IEnumerable<string> SplitOccupantList(string list)
    {
        string normalised = AndNormaliser.Replace(list, ", ");
        foreach (string part in normalised.Split(','))
        {
            string trimmed = part.Trim();
            if (trimmed.Length > 0) yield return trimmed;
        }
    }

    // "Raijin (sneaking)" → "Raijin"; "nasty giant rat." → "nasty giant rat".
    // Strips a trailing parenthetical AND any non-letter trailing characters.
    // Preserves spaces inside the name so multi-word monster names ("giant rat")
    // survive.
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
        // burns a second attack when the room re-displays. Only keep when
        // Current is the new room's OWN room-display data — an "Also here:"
        // observation (Source == AlsoHere) that post-dates the move.
        //
        // An Arrival ("<mob> walks in from <dir>.") is always relative to
        // the room the player currently stands in, so one that lands after
        // the move command is sent but before the move resolves belongs to
        // the OLD room, not the new one — its timestamp is post-move yet it
        // is stale. Keeping it stranded a walked-in monster in Current when
        // the player stepped into a fresh, empty room: the Combat gate
        // stayed asserted, the client kept firing the attack command, and
        // the game answered "Your command had no effect." while the map's
        // fighting chip hung with no monster in the room. So the base keep
        // is AlsoHere-sourced observations only. (Death / RoomChange sources
        // are likewise stale old-room data and fall through to the wipe.)
        // DateTimeOffset comparison is offset-aware, so the classifier's
        // local .Now timestamps and the tracker's UTC move time compare on
        // the same absolute instant.
        if (_roomTracker is { LastMoveSentAt: { } moveAt }
            && Current is { Entities.Count: > 0, Source: RoomObservationSource.AlsoHere } cur
            && cur.At >= moveAt)
        {
            return;
        }

        // But a monster that PURSUES us into the new room announces itself with
        // the same "<mob> walks in from <dir>." arrival line, and when that line
        // lands in the narrow window between the new room's "Also here:" parse and
        // its "Obvious exits:" line (which confirms the move and fires this
        // handler), it flips Current.Source AlsoHere → Arrival right as we decide
        // keep-vs-wipe. The base rule above then wipes a monster that is genuinely
        // in the room with us — the gate oscillates, and the walker grabs a step
        // in the clear-window, dragging us (and the pursuer) onward.
        //
        // Distinguish a pursuer from the stale old-room arrival by whether a
        // NEW-room "Also here:" parsed before the arrival: _lastAlsoHereAt >=
        // moveAt means the new room already displayed its occupants, so this
        // arrival is a new-room pursuer → keep it (stop and fight). A stale
        // old-room arrival has no post-move Also-Here anchoring it (empty new
        // rooms never emit one), so _lastAlsoHereAt stays pre-move and it still
        // wipes. Suppressed while fleeing: a pursuer caught mid-flee must NOT
        // re-arm the gate — we keep running instead of turning to fight it.
        if (_roomTracker is { LastMoveSentAt: { } confirmedMoveAt }
            && Current is { Entities.Count: > 0, Source: RoomObservationSource.Arrival } arr
            && arr.At >= confirmedMoveAt
            && _lastAlsoHereAt is { } alsoHereAt
            && alsoHereAt >= confirmedMoveAt
            && alsoHereAt <= arr.At
            && !(FleeProbe?.Invoke() ?? false))
        {
            _log?.Debug(LogCategory,
                "kept pursuer across room change (stop-and-fight) — arrival " +
                "post-dates new-room Also-here, not fleeing");
            return;
        }

        NoteRoomChanged();
    }

    // Remove ONE entity matching monsterName (case-insensitive match against
    // ResolvedName then RawName as fallback) from Current and re-fire
    // EntitiesObserved. Called by MonsterDeathWatcher when a specific death-line
    // pattern is observed so CombatManager doesn't sit on a stale entity that
    // already died — the bug behind the "fierce kobold thief arrived but no
    // attack" scenario where the just-killed "giant rat" still appeared in the
    // Current list and blocked the target re-pick.
    //
    // The death-line patterns use the base / canonical name (no flavour prefix),
    // so resolved-name matches first; raw-name match covers the edge case where a
    // no-prefix monster carries the same string in both fields. Returns true when
    // a matching entity was removed, false otherwise (defensive caller logging).
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

    // Wipe Current and re-fire EntitiesObserved with an empty observation —
    // drives CombatManager to clear its target so the next round doesn't waste a
    // swing on a monster that didn't follow us in. The next Also-Here parse
    // (arrives within milliseconds of the move) rebuilds the list for the new
    // room and CombatManager picks afresh. Public so callers other than
    // RoomTracker can drive the wipe (tests, forced-refresh paths). Idempotent on
    // already-empty observations.
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
