using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Combat;

// Turns the wire's "Also here:" line into a classified entity list (Player /
// Monster / Unknown). The output feeds CombatStateTracker — which holds the
// Combat gate on MovementCoordinator while an engageable monster is here — and
// the LogPane's double-click-to-fix flow for unknown names.
//
// Algorithm:
// 1. Split the comma-separated occupant capture, normalising the Oxford " and "
//    form into a comma the same way AutoPartyManager does.
// 2. For each entry: try a bare-name match against every MonsterMessageRecord.Name.
//    A flavor adjective ("large giant rat") is stripped generically later (Pass 3.5)
//    using the shared per-set FlavorPrefixStore vocabulary, then the base name is
//    re-fed here — so no per-monster prefix data is involved.
// 3. If no monster match, fall through to player lookup: the entry's first
//    whitespace token compared against every PlayerRecord.GivenName in the
//    active per-BBS database (case-insensitive).
// 4. If still unmatched, match the WHOLE entry against the Monsters table by
//    name — this catches a monster whose Monsters-table row exists under its
//    full (possibly multi-word) name but which carries no MonsterMessageRecord
//    ("brigand chief"). The full name is canonical, so no missing-flavor warning
//    is logged.
// 5. If still unmatched and the entry is multi-word, strip the FIRST word and
//    match the remainder against the Monsters table by name — this catches a
//    monster whose Monsters-table row exists but whose leading flavor word
//    isn't in the FlavorPrefixStore vocabulary ("vicious kobold" → "kobold").
//    On a hit, classify as that monster AND log the leading word under
//    MissingFlavorCategory so its LogPane double-click adds it to the vocabulary.
// 6. Else Unknown — emit a Warn-severity log row with the raw "Also here:" line
//    carried as LogEntry.Context. The LogPane double-click handler opens the
//    UnknownEntityFixDialogViewModel from this row.
//
// Performance: ~1100 monster records in a typical realm × ~5 entries per
// Also-Here line, a bare-name compare each, plus one prefix strip per entry.
// Naive scan is fine for the hot path (rooms display every few seconds at most).
public sealed class RoomEntityClassifier : IDisposable
{
    // LogService category for all rows this classifier emits.
    public const string LogCategory = "RoomClassifier";

    // LogService category for a room monster recognized only by stripping a leading
    // word that isn't in the flavor vocabulary (e.g. "vicious kobold" resolved to the
    // Monsters-table "kobold"). Distinct from LogCategory so its LogPane double-click
    // adds that leading word to the per-set FlavorPrefixStore vocabulary rather than
    // opening the unknown-entity fix dialog. The leading word rides in LogEntry.Context.
    public const string MissingFlavorCategory = "RoomFlavorMissing";

    private static readonly Regex AndNormaliser = new(@"\s+and\s+", RegexOptions.Compiled);

    private readonly MessageRouter _router;
    private readonly Func<DateTimeOffset> _now;
    private readonly MonsterMessageStore _monsters;
    private readonly PlayerDatabase _players;
    private readonly RoomTracker? _roomTracker;
    private readonly LogService? _log;
    private readonly GameDataCache? _gameData;
    private readonly FlavorPrefixStore _flavorPrefixes;

    // Room-aware name → Number resolver (RoomAwareMonsterResolver.ResolveInCurrentRoom), backing
    // Pass 0. Late-bound: the resolver depends on this classifier's ResolveBaseName, so it's
    // constructed after us and wired in via SetRoomAwareResolver. Null → Pass 0 is inert.
    private Func<string, int?>? _roomAwareResolve;

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

    // Late-wired probe for CombatManager's active target (constructed after this
    // classifier). On a confirmed dark-room advance we reset the accumulated
    // roster but keep the one mob matching this name, so an ongoing dark fight
    // isn't dropped mid-swing while pursuit arrivals stop piling up. Null/unset →
    // keep nothing (reset to empty).
    public Func<string?>? ActiveCombatTargetProbe { get; set; }

    // Fires after each successful "Also here:" parse. Subscribers run on the
    // MessageRouter's marshalled thread.
    public event Action<RoomEntitiesObservation>? EntitiesObserved;

    // Last observation parsed, or null when no "Also here:" line has been seen
    // this session.
    public RoomEntitiesObservation? Current { get; private set; }

    // Whether Current can be taken for everyone the room would list. True from a
    // read of the room: its "Also here:" line, or an arrival whose display listed
    // nobody. False again as soon as someone may be here that Current doesn't name:
    // an arrival line that names nobody (a follow, a directionless spawn, a summon),
    // an arrival under a name the game data can't place, a swing from nobody listed,
    // a cast line naming a caster it doesn't list, a room entered unseen, or the
    // roster emptied on some other ground than a display. The next read of the room
    // sets it again.
    //
    // For whoever has to rule a monster OUT by its absence (CastReach). The combat
    // gate asks who is here to fight, and a roster that is short does it no harm.
    public bool RosterIsWhole { get; private set; }

    // Why RosterIsWhole is false, for the bug report; null while it is true.
    public string? RosterNotWholeBecause { get; private set; } = "no room has been read yet";

    private void SetRosterWhole(bool whole, string? because = null)
    {
        RosterIsWhole = whole;
        RosterNotWholeBecause = whole ? null : because;
    }

    // Someone may be in the room whom the roster does not name.
    public void NoteUnlistedArrival(string why)
    {
        if (!RosterIsWhole) return;
        SetRosterWhole(false, why);
        _log?.Debug(LogCategory, $"roster no longer known to be whole — {why}");
    }

    // A cast line named who cast it, and a caster has to be in the room: one the
    // roster doesn't list is someone it is short of.
    public void NoteCasterNamed(string caster, string line)
    {
        if (!RosterIsWhole || Current is not { } room || room.Lists(caster)) return;
        NoteUnlistedArrival($"a cast line names '{caster}', who is not on the roster: '{line}'");
    }

    public RoomEntityClassifier(
        MessageRouter router,
        MonsterMessageStore monsters,
        PlayerDatabase players,
        LogService? log = null,
        Func<DateTimeOffset>? now = null)
        : this(router, monsters, players, roomTracker: null, log, now: now) { }

    // Construct with a RoomTracker binding so the classifier wipes its
    // observation when the player moves rooms. Without this, a stale Also-Here
    // from the previous room would keep CombatManager swinging at a target that
    // didn't follow us in — the "wasted combat round on move" scenario. The
    // optional GameDataCache backs the first-word-strip Monsters-table fallback
    // (Pass 3); null skips that pass. flavorPrefixes supplies
    // the per-set adjective vocabulary for the Pass-3.5 prefix strip; null → a
    // default store carrying the built-in stock list.
    public RoomEntityClassifier(
        MessageRouter router,
        MonsterMessageStore monsters,
        PlayerDatabase players,
        RoomTracker? roomTracker,
        LogService? log = null,
        GameDataCache? gameData = null,
        FlavorPrefixStore? flavorPrefixes = null,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(monsters);
        ArgumentNullException.ThrowIfNull(players);
        _now      = now ?? (static () => DateTimeOffset.Now);
        _router   = router;
        _monsters = monsters;
        _players  = players;
        _roomTracker = roomTracker;
        _log      = log;
        _gameData = gameData;
        _flavorPrefixes = flavorPrefixes ?? new FlavorPrefixStore();
        _alsoHereSub = _router.Subscribe(KnownPatterns.RoomAlsoHere, OnRoomAlsoHere);
        if (_roomTracker is not null)
            _roomTracker.StateChanged += OnRoomTrackerStateChanged;
    }

    // Wire the room-aware resolver used by Pass 0 (RoomAwareMonsterResolver.ResolveInCurrentRoom).
    // Late-bound because that resolver takes this classifier's ResolveBaseName, so it's built
    // after us; AppServices calls this once both exist.
    public void SetRoomAwareResolver(Func<string, int?> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        _roomAwareResolve = resolve;
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

        RoomEntitiesObservation obs = new(rawFirst, entities, _now());
        Current = obs;
        _lastAlsoHereAt = obs.At;
        SetRosterWhole(true);
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

        RoomEntitiesObservation obs = new(rawLine, entities, _now());
        Current = obs;
        _lastAlsoHereAt = obs.At;
        SetRosterWhole(true);
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
            rawWireLine, updated, _now(), RoomObservationSource.Arrival);
        Current = obs;
        EntitiesObserved?.Invoke(obs);
    }

    private RoomEntity Classify(string entry, string rawAlsoHereLine)
    {
        // Pass 0 — room-aware. Resolve the observed name to the specific monster the CURRENT
        // room actually places or summons here — its NPC fixture, lair members, Summoned-By
        // spawns, AND the monsters those can summon (RoomAwareMonsterResolver) — before any
        // global name match. This pins a homonym to the variant in THIS room, so per-monster
        // overrides that key on the Number (spell overrides, relationship, KillOnSight) land on
        // the right record. Falls through when the room doesn't host that name, or the current
        // room / resolver isn't wired, in which case the name-only passes below take over.
        if (_roomAwareResolve?.Invoke(entry) is { } roomNum)
            return new RoomEntity(entry, _gameData?.FindNameByNumber("Monsters", roomNum) ?? entry,
                                  EntityKind.Monster, roomNum);

        // Pass 1 — monster match (direct + prefix-stripped).
        if (TryMatchMonster(entry, out MonsterMessageRecord? mm))
            return new RoomEntity(entry, mm!.Name, EntityKind.Monster, ResolveMonsterNumber(mm));

        // Pass 2 — player match (first whitespace token vs GivenName).
        string given = FirstToken(entry);
        if (given.Length > 0 && TryMatchPlayer(given, out PlayerRecord? pr))
            return new RoomEntity(entry, pr!.GivenName, EntityKind.Player, MonsterNumber: null);

        // Pass 3 — full-name Monsters-table match. A monster whose Monsters-table
        // row exists under its full (possibly multi-word) name but which carries
        // no MonsterMessageRecord — e.g. a lair boss like "brigand chief" with no
        // flavor prefix — is missed by both message-catalog passes above AND the
        // first-word-strip fallback below (which would reduce "brigand chief" to
        // "chief" and find nothing). Match the whole entry against the Monsters
        // table directly; on a hit it's the canonical name, so no missing-flavor
        // warning applies.
        if (TryMatchMonstersTable(entry, out int fullMatchNumber))
            return new RoomEntity(entry, entry, EntityKind.Monster, fullMatchNumber);

        // Pass 3.5 — shared flavor-prefix vocabulary. Strip a leading adjective from
        // the per-set vocabulary (FlavorPrefixStore: large / nasty / huge / … by
        // default, plus any custom words a modified realm adds) and match the remainder
        // against a known monster, so "large giant rat" resolves without any per-monster
        // prefix data. Runs AFTER the full-name pass above so a canonical name that
        // starts with an adjective ("huge basilisk", "adult red dragon") has already
        // matched itself and isn't reduced to its tail. No missing-flavor warning — this
        // prefix is expected.
        int gsp = entry.IndexOf(' ');
        if (gsp > 0 && _flavorPrefixes.IsPrefix(entry[..gsp]))
        {
            string baseName = entry[(gsp + 1)..];
            if (TryMatchMonster(baseName, out MonsterMessageRecord? gm) && gm is not null)
                return new RoomEntity(entry, gm.Name, EntityKind.Monster, ResolveMonsterNumber(gm));
            if (TryMatchMonstersTable(baseName, out int gnum))
                return new RoomEntity(entry, baseName, EntityKind.Monster, gnum);
        }

        // Pass 4 — leading word not in the flavor vocabulary. A monster whose
        // Monsters-table row exists but whose leading adjective isn't in the per-set
        // FlavorPrefixStore shows up as "<unknown-word> <base name>" ("vicious kobold").
        // Strip only the first word and match the remainder against the Monsters table;
        // on a hit, treat the entry as that monster so combat engages it, and flag the
        // leading word in the log — its double-click adds that word to the vocabulary so
        // the prefix resolves cleanly next time (the word rides in LogEntry.Context).
        int sp = entry.IndexOf(' ');
        if (sp > 0 && sp + 1 < entry.Length
            && TryMatchMonstersTable(entry[(sp + 1)..], out int monsterNumber))
        {
            if (_log is not null && rawAlsoHereLine.Length > 0)
            {
                string leadingWord = entry[..sp];
                _log.Warn(MissingFlavorCategory,
                    $"'{entry}' — leading word '{leadingWord}' isn't in the flavor vocabulary; " +
                    "double-click to add it",
                    context: leadingWord);
            }
            return new RoomEntity(entry, entry[(sp + 1)..], EntityKind.Monster, monsterNumber);
        }

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

    // Match name against the active set's Monsters table by Name (case-
    // insensitive) and hand back its Number. Returns false when no GameDataCache
    // is bound, the table isn't loaded, no row matches, or the row carries no
    // numeric Number. Backs the first-word-strip flavor fallback in Classify.
    private bool TryMatchMonstersTable(string name, out int number)
    {
        number = 0;
        if (_gameData is null) return false;
        if (_gameData.FindRowByName("Monsters", name) is not { } row) return false;
        if (!row.TryGetProperty("Number", out GameValue numEl)) return false;
        if (numEl.ValueKind != JsonValueKind.Number) return false;
        return numEl.TryGetInt32(out number);
    }


    // Strip a monster display name's flavor prefix down to its canonical base name
    // ("short orc lieutenant" → "orc lieutenant") using the same matching Classify does
    // — so a room-aware resolver can compare a looked-at name against room monsters' base
    // names without re-implementing the prefix rules. Returns null when the name resolves
    // to no known monster.
    public string? ResolveBaseName(string display)
    {
        if (string.IsNullOrWhiteSpace(display)) return null;
        string trimmed = display.Trim();
        if (TryMatchMonster(trimmed, out MonsterMessageRecord? m) && m is not null)
            return m.Name;
        // Exact Monsters-table hit: the name is already its own base (no prefix).
        if (TryMatchMonstersTable(trimmed, out _)) return trimmed;
        // Strip a leading flavor adjective from the per-set vocabulary and retry the base
        // name — mirrors Classify's Pass 3.5 so "short orc lieutenant" → "orc lieutenant".
        int sp = trimmed.IndexOf(' ');
        if (sp > 0 && _flavorPrefixes.IsPrefix(trimmed[..sp]))
        {
            string baseName = trimmed[(sp + 1)..];
            if (TryMatchMonster(baseName, out MonsterMessageRecord? bm) && bm is not null)
                return bm.Name;
            if (TryMatchMonstersTable(baseName, out _)) return baseName;
        }
        return null;
    }

    private bool TryMatchMonster(string entry, out MonsterMessageRecord? hit)
    {
        // Direct bare-name match against the message-catalog Names. A prefixed
        // display name ("large giant rat") never lands here — the flavor adjective
        // is stripped one level up in Pass 3.5 (shared FlavorPrefixStore vocabulary)
        // and the base name re-fed through this method. So no per-monster prefix data
        // is needed: every record is matchable by its bare Name.
        foreach (MonsterMessageRecord m in _monsters.Messages)
        {
            if (string.Equals(m.Name, entry, StringComparison.OrdinalIgnoreCase))
            {
                hit = m;
                return true;
            }
        }

        hit = null;
        return false;
    }

    // The MonsterMessageRecord carries a back-reference to the Monsters-table row via its
    // Links; typically a single (Monsters, N) entry. When it has none — a user-curated entry,
    // or a greet/flavor record that was never bound to a specific row — fall back to matching
    // the record's own Name against the Monsters table. Without this fallback the classifier
    // hands back a numberless Monster, and the combat gate then can't read its relationship /
    // KillOnSight overlay, so it defaults a friendly NPC (a greet-only "old man" quest-giver)
    // to a fightable enemy. Returns null only when neither a link nor a name match resolves.
    private int? ResolveMonsterNumber(MonsterMessageRecord m)
    {
        if (m.Links is { } links)
            foreach (GameDataLink link in links)
                if (string.Equals(link.Table, "Monsters", StringComparison.OrdinalIgnoreCase))
                    return link.Number;
        return TryMatchMonstersTable(m.Name, out int number) ? number : null;
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

        // A dark-room advance carries NO occupant information: the room display
        // shows no name, no exits, and no "Also here:" line (see
        // GAME_MECHANICS.md), so we can't re-derive the new room's roster. Keeping
        // Current wholesale here (the old fix) protected an in-progress dark fight
        // from a mid-move gate drop, but it also let every pursuit arrival
        // (RoomEntryWatcher) pile onto a list that never resets — over a corridor
        // hunt that grows to hundreds of phantom mobs. That count trips
        // CombatManager's max-monsters gate so we never engage, while
        // CombatStateTracker still counts them and holds the Combat gate until the
        // idle-stall watchdog tears it down ~30s later: "a party member fights the
        // monster and we just sit there." Reset the roster on each dark advance but
        // KEEP the mob we're actively fighting (below), so the real fight survives
        // and the accumulation doesn't.
        if (_roomTracker is { IsInDarkRoom: true })
        {
            ResetDarkRoomRoster();
            return;
        }

        // No move has gone out this session, so this isn't a step into another room:
        // the tracker corrected where it thinks we stand (the login `rm` resync
        // replacing a stale saved room). Everything in Current was seen in the room
        // we're in. Wiping it dropped the Combat gate mid-fight, and auto-sneak then
        // sent `sn` at a monster that was attacking us (report
        // paradigm-20260930-231958). Re-emit so room-keyed gates re-decide.
        if (_roomTracker is { LastMoveSentAt: null } && Current is { Entities.Count: > 0 })
        {
            _log?.Debug(LogCategory, "kept roster across a position correction — no move sent this session");
            ReemitCurrent();
            return;
        }

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
            // The new room's occupants were parsed BEFORE this confirmation, while
            // RoomTracker.CurrentRoom still pointed at the room we were leaving. A
            // gate decision that keys off the room's IDENTITY — the loop's "only
            // attack in lair rooms" / per-room "do not attack" suppression — was
            // therefore decided for the WRONG room: a lair whose hostiles were seen
            // while the previous (non-lair) connector was still "current" read as
            // suppressed, the Combat gate never held, and the loop walked straight
            // through it (report paradigm-20260915-130624). Now that the move is
            // confirmed and CurrentRoom points here, re-emit the kept roster so every
            // gate re-decides against the correct room — the CombatGate re-asserts
            // and the loop holds to fight (the classifier is subscribed to
            // RoomTracker.StateChanged ahead of the movement engines, so the gate
            // lands before the loop's own advance sees it). Idempotent otherwise: an
            // already-engaged fight short-circuits (server still swinging), a suppressed
            // room stays clear, and the greet / sighting consumers de-dupe per player.
            ReemitCurrent();
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

        // The roster is emptied either way. It is the new room as read only when a
        // display of that room decided the move and printed no "Also here:" of its
        // own: one read since the move and then lost (the listed monster died ahead
        // of the exits line) leaves others the roster no longer names.
        bool shownEmpty = transition.Displayed
            && _roomTracker is { LastMoveSentAt: { } sentAt }
            && !(_lastAlsoHereAt is { } readAt && readAt >= sentAt);
        WipeRoster(shownEmpty);
    }

    // Remove ONE entity matching monsterName (case-insensitive match against
    // ResolvedName then RawName as fallback) from Current and re-fire
    // EntitiesObserved, for a monster taken for dead, so CombatManager doesn't
    // sit on a stale entity that already died and re-pick it. Returns true when
    // a matching entity was removed, false otherwise (defensive caller logging).
    public bool RemoveDeadEntity(string monsterName)
        => RemoveMonsterEntity(monsterName, RoomObservationSource.Death);

    // Whether a "You don't see <X> here" / "You do not see <X> here!" refusal is about
    // a monster: <X> is the target we attacked, or resolves to a monster. The same
    // refusal answers a targeted cast at a hiding party member and a `get` that found
    // nothing, and neither means the monster we're fighting is gone.
    public bool RefusalNamesMonster(string refused, string? currentTarget)
    {
        string name = refused.Trim();
        if (name.Length == 0) return false;
        if (currentTarget is { Length: > 0 } t && name.Equals(t, StringComparison.OrdinalIgnoreCase))
            return true;
        return Classify(name).Kind == EntityKind.Monster;
    }

    // Remove ONE monster matching monsterName because it WALKED OUT of the room —
    // a "<mob> walks out of the room to <dir>." departure line, most often when a
    // fleeing player drags the mob we were engaged with out with them (see the
    // 180449 capture). Without this, the Combat gate the departed mob held stays
    // asserted forever: CombatStateTracker only re-evaluates on EntitiesObserved,
    // and a departure produced no such re-fire, so the walker froze and the client
    // kept swinging at empty air ("Your command had no effect."). Removing the
    // entity + re-firing EntitiesObserved drops the gate when the last hostile
    // leaves. Returns true when a matching entity was removed.
    public bool RemoveDepartedEntity(string monsterName)
        => RemoveMonsterEntity(monsterName, RoomObservationSource.Departure);

    // A player's own departure line. Their place on the roster matters where a
    // player's presence is acted on (a room attack held for someone outside the
    // party, an Enemy answered on sight): left listed, they would count as here
    // until the room next re-displayed.
    public bool RemoveDepartedPlayer(string givenName)
        => RemoveEntity(givenName, EntityKind.Player, RoomObservationSource.Departure);

    // Shared core for the death / departure removals: drop the first Monster-kind
    // entity whose ResolvedName (then RawName) matches, re-fire EntitiesObserved
    // tagged with the caller's source so consumers can tell why the list shrank.
    private bool RemoveMonsterEntity(string monsterName, RoomObservationSource source)
        => RemoveEntity(monsterName, EntityKind.Monster, source);

    private bool RemoveEntity(string monsterName, EntityKind kind, RoomObservationSource source)
    {
        if (string.IsNullOrWhiteSpace(monsterName)) return false;
        if (Current is not { } cur) return false;

        int removeIndex = -1;
        for (int i = 0; i < cur.Entities.Count; i++)
        {
            RoomEntity e = cur.Entities[i];
            if (e.Kind != kind) continue;
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
            cur.RawAlsoHereLine, updated, _now(), source);
        Current = obs;
        EntitiesObserved?.Invoke(obs);
        return true;
    }

    // Reset the roster on a confirmed dark-room advance: shed every accumulated
    // entity EXCEPT the mob we're actively fighting. A too-dark room prints no
    // "Also here:" to rebuild from, so pursuit arrivals would otherwise pile up
    // forever; resetting each advance keeps the list honest. The active target is
    // matched by name (Resolved then Raw) against CombatManager's current pick and
    // carried across so a live dark fight isn't dropped mid-swing — the reason this
    // branch used to keep Current wholesale. A genuine pursuer we're NOT yet
    // targeting re-reveals off its next attack line (DarkRoomCombatWatcher) within
    // the settle window. While fleeing we keep nothing and keep running, mirroring
    // the lit-room pursuer rule in OnRoomTrackerStateChanged.
    private void ResetDarkRoomRoster()
    {
        RoomEntity? keep = null;
        if (!(FleeProbe?.Invoke() ?? false)
            && ActiveCombatTargetProbe?.Invoke() is { Length: > 0 } target
            && Current is { } cur)
        {
            foreach (RoomEntity e in cur.Entities)
            {
                if (e.Kind != EntityKind.Monster) continue;
                if (string.Equals(e.ResolvedName, target, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(e.RawName, target, StringComparison.OrdinalIgnoreCase))
                {
                    keep = e;
                    break;
                }
            }
        }

        RoomEntity[] entities = keep is { } k ? new[] { k } : Array.Empty<RoomEntity>();
        RoomEntitiesObservation obs = new(
            RawAlsoHereLine: string.Empty,
            Entities: entities,
            At: _now(),
            Source: RoomObservationSource.RoomChange);
        Current = obs;
        SetRosterWhole(false, "a room too dark to list who is in it was entered");
        _log?.Debug(LogCategory, keep is { } kept
            ? $"dark advance — roster reset, kept active target {kept.ResolvedName}"
            : "dark advance — roster reset to empty");
        EntitiesObserved?.Invoke(obs);
    }

    // Wipe Current and re-fire EntitiesObserved with an empty observation —
    // drives CombatManager to clear its target so the next round doesn't waste a
    // swing on a monster that didn't follow us in. The next Also-Here parse
    // (arrives within milliseconds of the move) rebuilds the list for the new
    // room and CombatManager picks afresh. Public so callers other than
    // RoomTracker can drive the wipe (forced-refresh paths). Idempotent on
    // already-empty observations.
    public void NoteRoomChanged() => WipeRoster(shownEmpty: false);

    // shownEmpty: the wipe follows a display of the room that listed nobody, so the
    // empty roster is the room as read. Any other wipe is a roster dropped as stale,
    // which says nothing of who is here.
    private void WipeRoster(bool shownEmpty)
    {
        RoomEntitiesObservation wiped = new(
            RawAlsoHereLine: string.Empty,
            Entities: Array.Empty<RoomEntity>(),
            At: _now(),
            Source: RoomObservationSource.RoomChange);
        Current = wiped;
        SetRosterWhole(shownEmpty, "the room changed and its list was dropped");
        EntitiesObserved?.Invoke(wiped);
    }

    // The game was left: the link dropped, or the character went out to the board's
    // menu. Whoever was in the room is not known to be there when we are back, so
    // the roster goes back to what it is at the start of a session, none. Left
    // standing, it was re-issued by anything that re-emits it (the Auto-Combat
    // toggle, a PvP fight ending) while offline or at a menu, and an arrival after
    // the return was appended to monsters that had long since left.
    //
    // Dropped WITHOUT raising EntitiesObserved, and to null, not to an empty roster:
    // an empty one raised now, or re-emitted later, is a room found clear, and every
    // subscriber would act on that while out of the game (the combat tracker drops
    // its gate, or reads a fight walked out on and halts the walk the reconnect is
    // meant to resume). The room display on the way back in rebuilds it.
    public void NoteGameLeft()
    {
        _alsoHereBuffer = null;
        _alsoHereRawFirst = null;
        _lastAlsoHereAt = null;
        Current = null;
        SetRosterWhole(false, "the game was left; no room has been read since");
    }

    // Look again at the names the roster couldn't place. A player we had no record
    // of reads as unknown until `who` (or the top list, or a look) supplies one;
    // once it has, they are a player here and now, not at the next room display.
    // Fires EntitiesObserved only when something changed.
    public void ReclassifyUnknown()
    {
        if (Current is not { } cur) return;
        List<RoomEntity>? updated = null;
        for (int i = 0; i < cur.Entities.Count; i++)
        {
            RoomEntity e = cur.Entities[i];
            if (e.Kind != EntityKind.Unknown) continue;
            RoomEntity again = Classify(e.RawName);
            if (again.Kind == EntityKind.Unknown) continue;
            updated ??= new List<RoomEntity>(cur.Entities);
            updated[i] = again;
        }
        if (updated is null) return;

        RoomEntitiesObservation obs = cur with { Entities = updated };
        Current = obs;
        EntitiesObserved?.Invoke(obs);
    }

    // Re-fire EntitiesObserved with the current observation unchanged, so every
    // subscriber re-evaluates on demand. Used when a state flip that is NOT itself
    // a room observation — the auto-combat master toggle — must reach all
    // room-observation consumers at once (combat gate, deferred-cash / get-items /
    // search acquisition holds), not just the one that flipped it. Without this, a
    // toggle that only re-runs the combat tracker leaves a sibling gate (e.g. the
    // deferred-cash Acquisition hold) asserted, stalling the walker until a manual
    // room re-display. No-op before the first observation.
    public void ReemitCurrent()
    {
        if (Current is { } obs) EntitiesObserved?.Invoke(obs);
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
