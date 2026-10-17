using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using MudPlay.Game.GameData;
using MudPlay.Game.Spells;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// In-memory graph of every room in the active game-data set — primary lookup
// for the navigation stack (room tracker, BFS mapper, walker, loop manager,
// auto-lair scheduler).
//
// Seeding: the graph reads Rooms.json through GameDataCache.GetTable once per
// active-set switch. Every row turns into a typed Room indexed by RoomKey.
// RoomExit values are produced inline from the per-direction MDB cells
// ("1/3" / "1/3 (Door)" / "0"). The Rooms table is evicted from
// GameDataCache immediately after conversion (per the project's memory-hygiene
// pattern set by MonsterOverlaySeedStore).
//
// Uniqueness index: a side table keyed on (Name, ExitMask) gives the room
// tracker its "is this a 1-of-1 room?" answer in O(1). When the user lands in a
// room whose tuple resolves to exactly one candidate, the tracker promotes to
// Located without further reconciliation. Buckets with > 1 candidate are
// surfaced via FindCandidates for the Tier-2 footprint matcher.
//
// Wiring: AppServices subscribes OnActiveSetChanged to
// GameDataCache.ActiveSetChanged at construction time — every set swap rebuilds
// the graph from scratch. Subscribers to GraphReloaded drop any per-set room
// references they were holding.
public sealed class RoomGraphManager
{
    private readonly GameDataCache _cache;
    private readonly LogService? _log;

    // TBInfo source consulted at build time to promote CMD-teleport-shadowed
    // door exits (see PromoteCmdTeleportExits). Null in test / no-teleport
    // configurations, in which case the promotion pass is skipped.
    private readonly TBInfoStore? _tbinfo;

    // Spell catalog consulted at build time to synthesise routable teleport
    // edges for cast-based CMD teleports (see BuildTeleportEdges). Null in
    // test / no-catalog configurations, where only literal-teleport CMD chains
    // become routable edges.
    private readonly KnownSpellCatalog? _spellCatalog;

    private readonly Dictionary<RoomKey, Room> _rooms = new();
    private readonly Dictionary<string, List<Room>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Name, uint ExitMask), List<RoomKey>> _byNameAndExits = new();

    // Reverse index: for a room where a remote lever/switch physically sits, the
    // exits (in OTHER rooms) that lever controls. Built during the action-cell
    // patch pass — a guardroom's "Action [on the N exit of room 1/1331]" cell
    // means the guardroom holds a lever governing 1/1331's north gate, but the
    // MultiAction data attaches to 1/1331's exit, so the guardroom's own tooltip
    // would otherwise show nothing. Keyed by the lever's host room.
    private readonly Dictionary<RoomKey, List<RemoteLeverRef>> _leversByRoom = new();

    // Sea-captain docks and the sailings each offers, keyed by dock room. Built
    // from every room's CMD `secure passage to <place>` TBInfo lines (see
    // BuildBoatEdges). Distinct from the Direction.Teleport slot: a dock can offer
    // several sailings at once, so they live in this side index the boat router
    // consults rather than being minted as cardinal teleport edges. Empty without
    // a TBInfo source + spell catalog (parameterless / test construction).
    private readonly Dictionary<RoomKey, IReadOnlyList<BoatPassage>> _boatPassages = new();

    // Every exit flagged CastPocketEntrance, in graph order (rooms as loaded, a
    // room's exits in its own order). The maze index starts from these few rather
    // than looking for them across every exit of every room.
    private readonly List<(RoomKey Source, Direction Direction)> _castPocketEntrances = new();
    internal IReadOnlyList<(RoomKey Source, Direction Direction)> CastPocketEntrances => _castPocketEntrances;

    // Live predicate identifying rooms a normal player can never stand in (dev /
    // orphan rooms flagged CannotBeReached in the room blacklist). When set,
    // FindCandidates drops matching keys so the RoomTracker never resolves the
    // player's position into one. Evaluated per call so blacklist edits take
    // effect without rebuilding any index. Null (default) means "no exclusions".
    private Func<RoomKey, bool>? _isUnreachable;

    // Set the graph was last built from, or null if empty.
    public string? ActiveSet { get; private set; }

    // The load of the set whose rooms are in place; none while the graph is being
    // rebuilt. The cache's load moves before the handler that rebuilds the graph
    // runs, so something asked in that moment that builds from the rooms and keeps
    // the result for the cache's load checks this first. Written and read on the
    // thread that switches the set.
    public GameDataCache.Load LoadedFor { get; private set; }

    // Number of rooms in the active graph (0 when no set is active or load failed).
    public int RoomCount => _rooms.Count;

    // Fires after every successful (re)load, including the transition to
    // no-set-active (empty graph). Subscribers should drop any cached room
    // references and re-pull what they need.
    public event Action? GraphReloaded;

    public RoomGraphManager(GameDataCache cache) : this(cache, log: null) { }

    public RoomGraphManager(GameDataCache cache, LogService? log,
        TBInfoStore? tbinfo = null, KnownSpellCatalog? spellCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _log = log;
        _tbinfo = tbinfo;
        _spellCatalog = spellCatalog;
    }

    // Direct lookup by primary key. Returns null when the key doesn't resolve
    // in the active set's graph.
    public Room? GetRoom(RoomKey key) =>
        _rooms.TryGetValue(key, out Room? room) ? room : null;

    // A remote lever/switch sitting in one room that governs an exit in another.
    // ControlledRoom + Direction name the exit it operates; Commands are the
    // alternative verbs that work it (e.g. "pull lever" / "push lever").
    public readonly record struct RemoteLeverRef(
        RoomKey ControlledRoom, Direction Direction, IReadOnlyList<string> Commands);

    // Levers physically in `room` that control a gated exit elsewhere. Empty when
    // the room holds no remote-action switch. Lets a guardroom's tooltip name the
    // gate its lever opens even though the exit's MultiAction attaches to the
    // gate room, not here.
    public IReadOnlyList<RemoteLeverRef> LeversControlledFrom(RoomKey room) =>
        _leversByRoom.TryGetValue(room, out List<RemoteLeverRef>? list)
            ? list
            : Array.Empty<RemoteLeverRef>();

    // Replace the in-memory Room at key with a copy whose Name is the new value,
    // and reshuffle the _byName / _byNameAndExits indexes so later candidate
    // searches find the updated tuple. No-op (returns null) when the key isn't
    // in the graph, the existing room already has the same name, or the new name
    // is null/empty. Otherwise returns the new Room instance with the updated
    // name.
    public Room? LearnRoomName(RoomKey key, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (!_rooms.TryGetValue(key, out Room? existing)) return null;
        if (string.Equals(existing.Name, name, StringComparison.Ordinal)) return null;

        Room replaced = Rename(existing, name);
        bool unnamedInData = _unnamedInData.Contains(key);
        if (unnamedInData) _learnedThisSession.Add(key);
        // Learned over a stored name: the first one replaced is the one on file.
        if (_namedFromStore.Contains(key)) _learnedOverStored.TryAdd(key, existing.Name);

        _log?.Log(LogSeverity.Info, "RoomGraph",
            $"Learned name '{name}' for {key} (was '{existing.Name}').");
        if (unnamedInData) LearnedNamesMoved(tellKeptLists: false);
        return replaced;
    }

    // What ApplyStoredNames did: how many rooms now bear a name from the client's
    // store, how many stored names were left off because the tracker has learned
    // another for the room since, and the rooms it was handed a name for that the
    // data names itself.
    public readonly record struct StoredNames(int Applied, int Replaced, IReadOnlyList<RoomKey> NamedByData);

    // Make the rooms the data gives no name bear exactly the names the client's own
    // store holds for this set (LearnedRoomNameStore), and no other name from it: a
    // name put here by an earlier call and absent from this one is taken off again.
    // A name the tracker learned this session and the store never took is not this
    // call's to touch; a change of realm takes those off (WithdrawLearnedNames).
    //
    // The quiet side of LearnRoomName: nothing is learned here, so no line is
    // logged per room; the caller says the count.
    //
    // A room the data names keeps the data's name. Those are handed back for the
    // store to drop. A room not in the graph is skipped.
    public StoredNames ApplyStoredNames(IReadOnlyDictionary<RoomKey, string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        bool moved = false;
        List<RoomKey>? withdrawn = null;
        foreach (RoomKey key in _namedFromStore)
            if (!names.ContainsKey(key)) (withdrawn ??= new()).Add(key);
        if (withdrawn is not null)
        {
            foreach (RoomKey key in withdrawn)
            {
                _namedFromStore.Remove(key);
                // The name it bears now was learned since: that one is the session's.
                if (_learnedOverStored.Remove(key)) continue;
                if (!_rooms.TryGetValue(key, out Room? room)) continue;
                Rename(room, string.Empty);
                moved = true;
            }
        }

        int applied = 0, left = 0;
        List<RoomKey>? namedByData = null;
        foreach ((RoomKey key, string name) in names)
        {
            if (string.IsNullOrWhiteSpace(name) || !_rooms.TryGetValue(key, out Room? room)) continue;
            if (!_unnamedInData.Contains(key))
            {
                (namedByData ??= new()).Add(key);
                continue;
            }
            // The game has printed another name for the room since this one was
            // stored, and the tracker took it: the stored one is the older word
            // and is not put back over it. A store that holds something else by
            // now (the new name kept, or another client's) is newer and stands.
            if (_learnedOverStored.TryGetValue(key, out string? replaced))
            {
                if (string.Equals(replaced, name, StringComparison.Ordinal))
                {
                    left++;
                    continue;
                }
                _learnedOverStored.Remove(key);
                moved = true;
            }
            if (!string.Equals(room.Name, name, StringComparison.Ordinal))
            {
                Rename(room, name);
                moved = true;
            }
            // A name the room already bore, now the store's: it was kept. The
            // lists that keep room names are told, as when one goes on.
            if (_namedFromStore.Add(key)) moved = true;
            applied++;
        }
        if (moved) LearnedNamesMoved(tellKeptLists: true);
        return new StoredNames(applied, left, namedByData is null ? Array.Empty<RoomKey>() : namedByData);
    }

    // Take off every name the tracker learned this session for a room the data
    // leaves unnamed, kept or not. For a change of realm: what such a room was
    // called on the board being left says nothing of this one, and a room still
    // bearing that name could never be learned here (it is no longer nameless), so
    // it would never be asked about or kept for this realm. The names the new
    // realm's store holds go back on with ApplyStoredNames.
    public void WithdrawLearnedNames()
    {
        if (_learnedThisSession.Count == 0) return;
        bool moved = false;
        foreach (RoomKey key in _learnedThisSession)
        {
            _namedFromStore.Remove(key);
            if (!_rooms.TryGetValue(key, out Room? room) || room.HasUnknownName) continue;
            Rename(room, string.Empty);
            moved = true;
        }
        _learnedThisSession.Clear();
        _learnedOverStored.Clear();
        if (moved) LearnedNamesMoved(tellKeptLists: true);
    }

    // The names the map has for rooms the data leaves unnamed, stored or learned
    // this session: what a list built from the Rooms table asks for when the table
    // has no name (RoomNameIndex.NameOf). A new dictionary each time one of them
    // changes and never altered after, so a list built off the UI thread reads it
    // without a lock.
    public IReadOnlyDictionary<RoomKey, string> LearnedNames => _learnedNames;

    // A stored name went on a room, a learned one came off, or one the room bore
    // was kept (the map was built, the realm changed, another client wrote, the
    // user pressed Save): a list that kept names out of LearnedNames is out of
    // date. A name the tracker learns is told too, but not at once: each telling
    // rebuilds the lists, and a walk through a house learns a name a room. It is
    // told once no name has been learned for KeptListsQuietBeats heartbeats
    // (TellKeptListsWhenQuiet), so the house costs one rebuild.
    public event Action? LearnedNamesChanged;

    // Heartbeats with no name learned before the names the tracker learned are
    // told to the kept lists.
    public const int KeptListsQuietBeats = 5;

    private static readonly IReadOnlyDictionary<RoomKey, string> NoLearnedNames = new Dictionary<RoomKey, string>();
    private volatile IReadOnlyDictionary<RoomKey, string> _learnedNames = NoLearnedNames;

    // Names learned and not yet told to the kept lists, and the heartbeats since
    // the last of them.
    private int _learnedUntold, _beatsSinceLearned;

    private void LearnedNamesMoved(bool tellKeptLists)
    {
        Dictionary<RoomKey, string> names = new();
        foreach (RoomKey key in _unnamedInData)
            if (_rooms.TryGetValue(key, out Room? room) && !room.HasUnknownName) names[key] = room.Name;
        _learnedNames = names;
        if (tellKeptLists)
        {
            _learnedUntold = 0;
            LearnedNamesChanged?.Invoke();
            return;
        }
        _learnedUntold++;
        _beatsSinceLearned = 0;
    }

    // The heartbeat: tell the kept lists of the names the tracker learned, once
    // the walk has gone quiet.
    public void TellKeptListsWhenQuiet()
    {
        if (_learnedUntold == 0 || ++_beatsSinceLearned < KeptListsQuietBeats) return;
        _log?.Log(LogSeverity.Debug, "RoomGraph",
            $"{_learnedUntold} room name(s) learned on the walk: the lists that keep room names are rebuilt");
        _learnedUntold = 0;
        LearnedNamesChanged?.Invoke();
    }

    // The rooms the data gives no name: the only ones a stored name may be put on.
    // Whether a room's name came from the data can't be read off the room once the
    // tracker or the store has named it, so it is noted when the graph is built.
    private readonly HashSet<RoomKey> _unnamedInData = new();

    // Of those, the ones bearing a name ApplyStoredNames put there.
    private readonly HashSet<RoomKey> _namedFromStore = new();

    // Of those, the ones LearnRoomName named since the graph was built or the
    // realm last changed.
    private readonly HashSet<RoomKey> _learnedThisSession = new();

    // Rooms whose stored name the tracker has learned another name over, with the
    // stored name it replaced. A board's name for such a room can change, so the
    // name printed now outranks the one on file until the store holds another.
    private readonly Dictionary<RoomKey, string> _learnedOverStored = new();

    // Whether the imported data gives this room no name, whatever it is called on
    // the map by now. Such a room's name is the board's to give and to change, so
    // the tracker does not hold the name it has for one against what is printed.
    public bool IsUnnamedInData(RoomKey key) => _unnamedInData.Contains(key);

    // Whether this room, one the data names, could have shown a display of this
    // name and exits as its own: it bears the name, every exit shown is one of its
    // exits, and every exit its exits line always prints is shown (an unsearched
    // hidden exit or a text exit is left off; GAME_MECHANICS "Room display parsing
    // — the room title is positional, not just bright cyan"). The tracker asks it
    // of the room being left and the rooms beside it, whose display could be
    // taken for a room ahead the data leaves unnamed. A room the data leaves
    // unnamed doesn't count, whatever it is called by now: the rooms of one house
    // can share a name, and would each stand in the way of the next. An
    // unreachable room doesn't either (see ConfigureUnreachable): the character
    // can't be in one.
    public bool NamedInDataCouldShow(RoomKey key, string name, IReadOnlySet<Direction> exits)
    {
        ArgumentNullException.ThrowIfNull(exits);
        if (_unnamedInData.Contains(key) || !_rooms.TryGetValue(key, out Room? room)) return false;
        if (!string.Equals(room.Name, name, StringComparison.OrdinalIgnoreCase)) return false;
        if (_isUnreachable is { } excluded && excluded(key)) return false;
        uint shown = MaskFromSet(exits);
        uint printed = PrintedExitMask(room);
        return (shown & room.ExitMask) == shown && (printed & shown) == printed;
    }

    private Room Rename(Room existing, string name)
    {
        DropFromIndexes(existing);
        Room replaced = existing with { Name = name };
        _rooms[existing.Key] = replaced;
        AddToIndexes(replaced);
        return replaced;
    }

    private void DropFromIndexes(Room room)
    {
        // The nameless bucket too: a room can be given its name back and lose it
        // again, and would otherwise be listed there once for each time.
        if (_byName.TryGetValue(room.Name, out List<Room>? nameBucket))
        {
            nameBucket.RemoveAll(r => r.Key.Equals(room.Key));
            if (nameBucket.Count == 0) _byName.Remove(room.Name);
        }
        var tuple = (room.Name, room.ExitMask);
        if (_byNameAndExits.TryGetValue(tuple, out List<RoomKey>? exitBucket))
        {
            exitBucket.RemoveAll(k => k.Equals(room.Key));
            if (exitBucket.Count == 0) _byNameAndExits.Remove(tuple);
        }
    }

    private void AddToIndexes(Room room)
    {
        if (!_byName.TryGetValue(room.Name, out List<Room>? nameBucket))
        {
            nameBucket = new List<Room>();
            _byName[room.Name] = nameBucket;
        }
        nameBucket.Add(room);

        var tuple = (room.Name, room.ExitMask);
        if (!_byNameAndExits.TryGetValue(tuple, out List<RoomKey>? exitBucket))
        {
            exitBucket = new List<RoomKey>();
            _byNameAndExits[tuple] = exitBucket;
        }
        exitBucket.Add(room.Key);
    }

    // All rooms in the active set whose Name matches name case-insensitively.
    // Empty when no match.
    public IReadOnlyList<Room> FindByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return Array.Empty<Room>();
        return _byName.TryGetValue(name, out List<Room>? rooms)
            ? rooms
            : (IReadOnlyList<Room>)Array.Empty<Room>();
    }

    // Bind (or clear, with null) the predicate that marks rooms as unreachable by
    // a normal player. Once set, FindCandidates excludes matching keys so the
    // tracker can't land the player in a dev / orphan room. AppServices wires this
    // to RoomBlacklistStore.IsUnreachable and re-invokes on blacklist changes; the
    // predicate reads the store live, so no reindex is needed when the flag set
    // changes.
    public void ConfigureUnreachable(Func<RoomKey, bool>? isUnreachable)
        => _isUnreachable = isUnreachable;

    // All rooms in the active set whose (Name, exit-set) tuple matches, minus any
    // flagged unreachable (see ConfigureUnreachable). Used by RoomTracker to
    // detect the 1-of-1 case — when the result has exactly one entry, the tracker
    // can promote to Located without further reconciliation. Excluding unreachable
    // rooms here means an ambiguous bucket that resolves to a single reachable
    // room now promotes cleanly, and a bucket of only-unreachable rooms resolves
    // to zero (tracker replays / stays Lost) instead of stranding the player.
    public IReadOnlyList<RoomKey> FindCandidates(string name, IReadOnlySet<Direction> exits)
    {
        if (string.IsNullOrEmpty(name)) return Array.Empty<RoomKey>();
        ArgumentNullException.ThrowIfNull(exits);

        uint mask = MaskFromSet(exits);
        if (!_byNameAndExits.TryGetValue((name, mask), out List<RoomKey>? keys))
            return Array.Empty<RoomKey>();

        if (_isUnreachable is not { } excluded) return keys;

        // Only allocate a filtered copy when at least one key is excluded —
        // the common case (nothing flagged) returns the stored bucket as-is.
        List<RoomKey>? filtered = null;
        for (int i = 0; i < keys.Count; i++)
        {
            if (!excluded(keys[i]))
            {
                filtered?.Add(keys[i]);
                continue;
            }
            filtered ??= new List<RoomKey>(keys.Take(i));
        }
        if (filtered is null) return keys;

        // An exclusion actually fired — trace it so a "nav won't resolve into
        // that room" report shows the CannotBeReached drop. Rare (only when a
        // matching room is flagged), and interpolation is Debug-gated up front.
        if (_log?.IsDebugEnabled == true)
            _log.Debug("RoomGraph",
                $"FindCandidates('{name}', mask={mask:X}): dropped {keys.Count - filtered.Count} unreachable of {keys.Count}; {filtered.Count} left.");
        return filtered;
    }

    // Door-tolerant re-anchor fallback: same-name rooms whose graph ExitMask is
    // a SUPERSET of the observed exit set — every observed exit is a known graph
    // exit, but the graph may carry extra exits the observation hid (a closed
    // door, an unsearched hidden exit). Excludes unreachable rooms (see
    // ConfigureUnreachable). Distinct from FindCandidates, which demands an EXACT
    // (Name, ExitMask) match: a single closed door drops one bit from the
    // observed mask and defeats the exact lookup even for a room that's unique by
    // name. RoomTracker calls this when the exact search misses and re-latches
    // only on a 1-of-1 reachable result, so a name-unique room recovers through a
    // door instead of freezing at Lost.
    public IReadOnlyList<RoomKey> FindByNameCoveringExits(string name, IReadOnlySet<Direction> exits)
    {
        if (string.IsNullOrEmpty(name)) return Array.Empty<RoomKey>();
        ArgumentNullException.ThrowIfNull(exits);
        if (!_byName.TryGetValue(name, out List<Room>? rooms)) return Array.Empty<RoomKey>();

        uint observedMask = MaskFromSet(exits);
        List<RoomKey>? result = null;
        foreach (Room room in rooms)
        {
            // Every observed exit must be present in the graph room's mask.
            if ((observedMask & room.ExitMask) != observedMask) continue;
            if (_isUnreachable is { } excluded && excluded(room.Key)) continue;
            (result ??= new List<RoomKey>()).Add(room.Key);
        }
        return result is null ? Array.Empty<RoomKey>() : result;
    }

    // Read-only snapshot of every room in the active set, in load order. The
    // Navigation search iterates this for substring matches; the room tree /
    // favourites populators reuse the same enumeration. Empty when no set is
    // active.
    public IEnumerable<Room> Rooms => _rooms.Values;

    // Every room's key, for a pass that only sorts rooms by number: the keys
    // lie side by side, where reading each Room to get its key is a jump apiece
    // through tens of thousands of them.
    public IEnumerable<RoomKey> RoomKeys => _rooms.Keys;

    // Per-monster lair-size aggregate: for each monster named in a room's lair
    // tag, how many such rooms name it (Count) and the sum / biggest of those
    // rooms' per-room "(Max N)" caps. Built once in BuildSecondaryIndexes (all
    // rooms are present up front and lair tags are static), so it's an immutable
    // snapshot the Monsters game-data table can read from its worker-thread build
    // without racing the live, UI-thread-only room dictionary. Key = monster number.
    private IReadOnlyDictionary<int, (int Count, long SumMax, int MaxMax)> _lairSizeByMonster
        = new Dictionary<int, (int, long, int)>();
    public IReadOnlyDictionary<int, (int Count, long SumMax, int MaxMax)> LairSizeByMonster => _lairSizeByMonster;

    // Sailings a sea-captain dock at `room` offers, empty when the room is not a
    // dock. The boat router reads this to decide whether a `secure passage`
    // hop can shortcut a cross-realm route from here.
    public IReadOnlyList<BoatPassage> BoatPassagesAt(RoomKey room) =>
        _boatPassages.TryGetValue(room, out IReadOnlyList<BoatPassage>? list)
            ? list
            : Array.Empty<BoatPassage>();

    // Every sailing discovered across every dock in the active set, in dock load
    // order. The boat router enumerates this to find a passage whose dock/arrival
    // brackets a long land route.
    public IEnumerable<BoatPassage> AllBoatPassages => _boatPassages.Values.SelectMany(p => p);

    // Whether the map can take the room display of shown for other: the two bear
    // the same name, and every exit shown prints on its exits line is an exit of
    // other. That is the test a position match makes (RoomTracker.MatchesPredicted),
    // and the exact (Name, ExitMask) search lands on such a room when shown itself
    // has exits its display leaves out: an exit taken by a text command, or a
    // hidden one, is in the graph and on no exits line. So a character standing in
    // shown can be confirmed in other. False for a room with itself, and for a
    // room the graph doesn't have.
    public bool CouldBeTakenFor(RoomKey shown, RoomKey other)
    {
        if (shown == other) return false;
        if (!_rooms.TryGetValue(shown, out Room? room) || !_rooms.TryGetValue(other, out Room? twin)) return false;
        if (!string.Equals(room.Name, twin.Name, StringComparison.OrdinalIgnoreCase)) return false;
        uint printed = PrintedExitMask(room);
        return (printed & twin.ExitMask) == printed;
    }

    private static uint PrintedExitMask(Room room)
    {
        uint printed = 0;
        foreach ((Direction direction, RoomExit exit) in room.Exits)
            if (PrintsOnExitsLine(exit)) printed |= 1u << (int)direction;
        return printed;
    }

    // Whether some room has an exit this line crosses: a text exit (`go path`), or
    // a room command the graph made an edge of (`touch altar`). For a caller that
    // doesn't know which room the character stands in and needs to know whether a
    // line could have moved it. Built once per graph, on the first question.
    public bool IsExitCommandSomewhere(string command) =>
        !string.IsNullOrWhiteSpace(command)
        && Derived("exit commands of every room", BuildExitCommands).Contains(command.Trim());

    private HashSet<string> BuildExitCommands()
    {
        HashSet<string> commands = new(StringComparer.OrdinalIgnoreCase);
        foreach (Room room in _rooms.Values)
            foreach (RoomExit exit in room.Exits.Values)
                if (exit.TextCommands is { } lines)
                    foreach (string line in lines) commands.Add(line.Trim());
        return commands;
    }

    // A hidden exit that a plain step crosses (`Hidden/Passable`) carries no hint
    // of its own, the walker having nothing special to do for it; only the cell's
    // own text says it is hidden.
    private static bool PrintsOnExitsLine(RoomExit exit) =>
        exit.Hint != RoomExitHint.Text
        && !MpFile.MegaMudHash.IsHidden(exit.Hint)
        && exit.RawHint?.StartsWith("Hidden", StringComparison.OrdinalIgnoreCase) != true;

    // Rebuild the graph from setName's Rooms.json. Pass null to clear. Safe to
    // call repeatedly; idempotent on no-op transitions (same set still fires
    // GraphReloaded because the caller may have re-imported the underlying
    // file).
    public void OnActiveSetChanged(string? setName)
    {
        long started = Stopwatch.GetTimestamp();
        GameDataCache.Load load = _cache.ActiveLoad;
        LoadedFor = GameDataCache.Load.None;
        Clear();
        ActiveSet = setName;

        if (string.IsNullOrWhiteSpace(setName))
        {
            _log?.Log(LogSeverity.Info, "RoomGraph", "No active game-data set; room graph cleared.");
            Reloaded(load);
            return;
        }

        GameTable? doc = _cache.GetTable("Rooms");
        if (doc is null)
        {
            _log?.Log(LogSeverity.Warn, "RoomGraph",
                $"Active set '{setName}' has no Rooms.json; room graph is empty.");
            Reloaded(load);
            return;
        }

        int parsed = 0;
        int skipped = 0;
        // First pass: build the typed Room graph from the JSON rows.
        // Action#N cells in exit fields don't parse as exits (no RoomKey
        // prefix) so they're naturally skipped — the second pass below
        // re-iterates to recover them.
        var actionCells = new List<(RoomKey Source, MultiActionExitData.ActionCell Cell)>();
        var perRoomModifiers = new Dictionary<RoomKey, Dictionary<Direction, string>>();

        // Each row is read on its own, so the 57,000 of a full realm are spread over
        // every core: read on one thread they were over a second of the launch
        // freeze. The results are folded in below in row order, as a plain loop would
        // have. All the readers share is the text they keep: a realm has fifteen
        // rooms to a name, and lair tags and exit hints repeat as much, so each is
        // kept once for the build.
        GameRow[] rows = doc.Rows.ToArray();
        RowRead[] read = new RowRead[rows.Length];
        ConcurrentDictionary<string, string> texts = new();
        Parallel.For(0, rows.Length,
            static () => new RoomExitSet.Builder(),
            (i, _, exits) =>
            {
                read[i] = ReadRow(rows[i], exits, texts);
                return exits;
            },
            static _ => { });
        foreach (RowRead row in read)
        {
            if (row.Room is not { } room)
            {
                skipped++;
                continue;
            }
            _rooms[room.Key] = room;
            parsed++;
            if (row.Actions is not null)
                foreach (MultiActionExitData.ActionCell action in row.Actions)
                    actionCells.Add((room.Key, action));
            if (row.Modifiers is not null) perRoomModifiers[room.Key] = row.Modifiers;
        }

        // Second pass: attach gathered action cells to the right
        // MultiActionHidden exit. "On the X exit of this room" → action
        // belongs to the source room's X exit. "On the X exit of room
        // M/R" → action lives in the SOURCE row but applies to the
        // REMOTE room's X exit; carry RemoteSourceRoom forward so the
        // walker fails gracefully on those (no cross-room expander).
        var byExit = new Dictionary<(RoomKey Room, Direction Dir), List<ExitAction>>();
        foreach ((RoomKey sourceRoom, MultiActionExitData.ActionCell cell) in actionCells)
        {
            RoomKey target = cell.RemoteSourceRoom ?? sourceRoom;
            var key = (target, cell.ExitDirection);
            if (!byExit.TryGetValue(key, out List<ExitAction>? list))
            {
                list = new List<ExitAction>();
                byExit[key] = list;
            }
            // RemoteSourceRoom is the row the action DATA lived in,
            // not the room the action targets — flagged so the walker
            // knows to fail on cross-row data.
            RoomKey? remote = cell.RemoteSourceRoom is not null ? sourceRoom : null;
            list.Add(new ExitAction(cell.StepNumber, cell.Commands, remote, cell.RequiredItemId));
        }

        double rowsMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        // The passes below each concern a few hundred rooms out of tens of
        // thousands: the ones with a CMD, with a cast-on-walk exit, or holding a
        // monster that has a greet. One walk finds them all, so no pass scans the
        // whole graph for its own handful.
        PassRooms rooms = FindPassRooms(ReadGreeters());

        // Fold in guard-monster doors: a placed monster whose greet dialogue
        // opens a door on its own room contributes an `ask` command that the
        // promotion below treats exactly like a lever action.
        InjectGuardDoorActions(byExit, rooms.Greeters);

        // Fold in room-CMD reveal actions: a hidden exit whose opener lives in the
        // room's CMD chain (a `remoteaction`, e.g. 9/1012's `clear rubble` revealing
        // its N passage) rather than an Action cell. Without this it imports as a
        // MultiActionHidden with NO action data and BFS skips it unconditionally.
        InjectRoomCmdRemoteActions(byExit, rooms.WithCmd);

        // Patch each action-gated exit with the gathered data.
        foreach (((RoomKey roomKey, Direction dir), List<ExitAction> actions) in byExit)
        {
            if (!_rooms.TryGetValue(roomKey, out Room? room)) continue;
            if (!room.Exits.TryGetValue(dir, out RoomExit exit)) continue;

            // Action cells normally decorate a MultiActionHidden exit, but the
            // same "Action [on the X exit of …]" annotation also targets plain
            // Door / KeyLocked exits that a lever — not a pick or key — opens
            // (e.g. the inner-gate portcullis that lifts only when levers in the
            // flanking guardrooms are pulled). Those import as a Door with an
            // impossibly high pick/strength requirement, so without the lever
            // data the route picker routes around them (IsImpassableDoorBlocked)
            // or the door FSM fails on them. Promote such a door to a
            // MultiActionHidden exit so the lever detour + same-room dispatch
            // that already back the hidden exits take over, and so the plan-time
            // door block no longer rejects a route the lever can open. Search /
            // text / teleport exits that happen to collide with an action key
            // are left as-is.
            bool leverDoor = exit.Hint is RoomExitHint.Door or RoomExitHint.KeyLocked;
            if (!exit.AcceptsActions) continue;

            // With no explicit "Needs N Actions" modifier, actions that share a
            // StepNumber are redundant alternatives (e.g. two guardroom levers that
            // open the same gate), so the required count is the number of DISTINCT
            // steps, not the raw cell count — one lever per step suffices. A genuine
            // multi-step gate declares its own count via the modifier.
            (int count, bool specific) = perRoomModifiers.TryGetValue(roomKey, out var mods)
                && mods.TryGetValue(dir, out string? modCell)
                ? MultiActionExitData.ParseModifier(modCell)
                : (actions.Select(a => a.StepNumber).Distinct().Count(), false);

            actions.Sort(static (a, b) => a.StepNumber.CompareTo(b.StepNumber));
            var data = new MultiActionExitData(count, specific, actions);
            RoomExit patched = leverDoor
                ? exit with { Hint = RoomExitHint.MultiActionHidden, MultiAction = data }
                : exit with { MultiAction = data };
            _rooms[roomKey] = room with { Exits = ExitsOf(room).With(dir, patched) };

            // Index each remote-sourced action so the room the lever physically
            // sits in can name the exit it governs (see LeversControlledFrom).
            foreach (ExitAction step in actions)
            {
                if (step.RemoteSourceRoom is not { } leverRoom) continue;
                if (!_leversByRoom.TryGetValue(leverRoom, out List<RemoteLeverRef>? refs))
                    _leversByRoom[leverRoom] = refs = new List<RemoteLeverRef>();
                refs.Add(new RemoteLeverRef(roomKey, dir, step.Commands));
            }
        }

        // A CMD's text reads the same for every room that carries it and for both
        // passes that ask where it teleports to, so it is read once per CMD.
        Dictionary<int, (string Keyword, RoomKey Destination, int MinLevel)[]> cmdTeleports = new();

        PromoteCmdTeleportExits(rooms.WithCmd, cmdTeleports);
        PromoteCastTeleportExits(rooms.WithCastExit);
        MarkCastPocketEntrances(rooms.WithCastExit);
        BuildTeleportEdges(rooms.WithCmd, cmdTeleports);
        BuildGreetTeleportEdges(rooms.Greeters);
        BuildItemUseTeleportEdges();
        BuildBoatEdges(rooms.WithCmd);
        ApplyStockArenaEntryBlock();
        BuildSecondaryIndexes();

        // Hand the Rooms table back; the typed graph is the source of truth now.
        _cache.EvictTable("Rooms");

        double totalMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _log?.Log(LogSeverity.Info, "RoomGraph",
            $"Loaded {parsed} room(s) from '{setName}' Rooms.json"
            + (skipped > 0 ? $" ({skipped} malformed row(s) skipped)" : "")
            + $" in {totalMs:0} ms (reading the rows {rowsMs:0} ms, "
            + $"doors, teleports, boats and indexes {totalMs - rowsMs:0} ms).");

        Reloaded(load);
    }

    // The rooms the build passes have work in, each list in graph order. What puts
    // a room on a list (its CMD, the spells on its exits, the monsters in it) is
    // read from the row and never changed by a pass, so the lists hold for the
    // whole build; a pass reads the room itself fresh from the graph.
    private readonly record struct PassRooms(
        List<RoomKey> WithCmd,
        List<RoomKey> WithCastExit,
        List<(RoomKey Room, Greeter Monster)> Greeters);

    // A monster with a greet, and what that greet does: the doors it opens and
    // the places it ports to. A greet reads the same in every room its monster
    // stands in, and a lair monster stands in hundreds, so it is read once, the
    // first time the walk meets the monster.
    private sealed class Greeter(int greet, string name)
    {
        public int Greet { get; } = greet;
        public string Name { get; } = name;
        public GuardDoorCommandResolver.GreetDoors? Doors { get; set; }
        public GreetTeleportResolver.GreetTeleport[] Ports { get; set; } = [];
    }

    private PassRooms FindPassRooms(Dictionary<int, Greeter> greeters)
    {
        PassRooms found = new(new(), new(), new());
        foreach (Room room in _rooms.Values)
        {
            if (room.Cmd > 0) found.WithCmd.Add(room.Key);

            foreach ((Direction _, RoomExit exit) in ExitsOf(room))
            {
                if (!exit.CastsOnWalk) continue;
                found.WithCastExit.Add(room.Key);
                break;
            }

            if (greeters.Count == 0) continue;
            // The placed NPC first, then the lair's mobs in the order the tag lists
            // them: the order a pass that takes the first match depends on.
            if (room.Npc > 0) AddIfGreetActs(room.Key, room.Npc, greeters, found.Greeters);
            foreach (int id in LairMonsterIds(room.RawLairTag))
                AddIfGreetActs(room.Key, id, greeters, found.Greeters);
        }
        return found;
    }

    // Lists the monster as one of the room's greeters when its greet opens a door
    // or ports somewhere at all; most greets only talk.
    private void AddIfGreetActs(RoomKey room, int monsterId,
        Dictionary<int, Greeter> greeters, List<(RoomKey Room, Greeter Monster)> into)
    {
        if (!greeters.TryGetValue(monsterId, out Greeter? greeter)) return;
        if (greeter.Doors is null)
        {
            greeter.Doors = GuardDoorCommandResolver.Read(_tbinfo!, greeter.Greet, greeter.Name);
            greeter.Ports = GreetTeleportResolver.Resolve(_tbinfo!, greeter.Greet, greeter.Name).ToArray();
        }
        if (!greeter.Doors.IsEmpty || greeter.Ports.Length > 0) into.Add((room, greeter));
    }

    // The graph builds every room's exits as a RoomExitSet. Read as one, a pass
    // walks them without the boxed enumerator the read-only interface hands out:
    // an allocation for every room walked, on walks that cover half the realm. A
    // room made with any other kind of exits is read through a set made of them.
    private static RoomExitSet ExitsOf(Room room) => RoomExitSet.From(room.Exits);

    // The same for a room looked up by key, for the maze index: it walks out from
    // each pocket entrance over up to a few thousand rooms. Null when the key
    // isn't in the graph.
    internal RoomExitSet? ExitsAt(RoomKey key) =>
        _rooms.TryGetValue(key, out Room? room) ? ExitsOf(room) : null;

    private static bool HasExitTo(RoomExitSet exits, RoomKey target) => exits.Targets.Contains(target);

    // Newhaven's Arena (1/2150) admits only levels 1-3. That block lives on the
    // ROOM and is tested when you try to ENTER it — an early-engine construct the
    // MDB has no field for, so the down exit that leads there reads as ungated
    // while the same room's west exit to the Healer carries an ordinary
    // "(Level: 1 to 3)" modifier. Left alone, the router plans an over-level
    // character straight in and the server refuses the move, with nothing in the
    // graph to route around next time.
    //
    // Surface it as the level window the MDB would have used, so the router, the
    // route picker and the room tooltip all read this exit exactly like the west
    // one. Stock only — Paradigm's dats already edit this exit to carry its own
    // (different) window. Skipped when a gate is already present, so an edited
    // stock realm that expresses the block itself keeps its own values.
    private void ApplyStockArenaEntryBlock()
    {
        if (_cache.ActiveRealm != RealmType.Stock) return;
        if (!_rooms.TryGetValue(ArenaApproach, out Room? approach)) return;
        if (!approach.Exits.TryGetValue(Direction.D, out RoomExit down)) return;
        if (down.Target != Arena || down.HasLevelGate) return;

        _rooms[ArenaApproach] = approach with
        {
            Exits = ExitsOf(approach).With(Direction.D, down with { MinLevel = 1, MaxLevel = 3 }),
        };

        _log?.Log(LogSeverity.Info, "RoomGraph",
            $"Room {ArenaApproach} D → {Arena}: applied the Arena's level-1-3 entry block "
            + "(engine-side room gate, absent from stock Rooms.json).");
    }

    // The Arena and the Narrow Road above it. Named so the block above reads as
    // intent rather than two bare map/room pairs.
    private static readonly RoomKey Arena = new(1, 2150);
    private static readonly RoomKey ArenaApproach = new(1, 2146);

    // Results worked out from the whole graph (the rooms with level gates, with
    // teleports), kept until it's rebuilt. Each took 150 ms on a 57,000-room graph
    // and was redone by every Navigation open; the graph only changes when the
    // game-data set loads. build runs outside the lock (a background warm-up may
    // ask too); a result built across a rebuild is returned but not kept.
    private readonly object _derivedLock = new();
    private readonly Dictionary<string, object> _derived = new(StringComparer.Ordinal);
    private long _derivedGeneration;

    public T Derived<T>(string key, Func<T> build) where T : class
    {
        ArgumentNullException.ThrowIfNull(build);
        long generation;
        lock (_derivedLock)
        {
            if (_derived.TryGetValue(key, out object? hit)) return (T)hit;
            generation = _derivedGeneration;
        }
        T value = build();
        lock (_derivedLock)
        {
            if (generation != _derivedGeneration) return value;
            if (_derived.TryGetValue(key, out object? won)) return (T)won;
            _derived[key] = value;
            return value;
        }
    }

    private void ForgetDerived()
    {
        lock (_derivedLock)
        {
            _derived.Clear();
            _derivedGeneration++;
        }
    }

    // The graph is whole again: anything worked out while it was being rebuilt is
    // dropped before listeners rebuild theirs.
    private void Reloaded(GameDataCache.Load load)
    {
        LoadedFor = load;
        ForgetDerived();
        GraphReloaded?.Invoke();
    }

    private void Clear()
    {
        ForgetDerived();
        _rooms.Clear();
        _unnamedInData.Clear();
        _namedFromStore.Clear();
        _learnedThisSession.Clear();
        _learnedOverStored.Clear();
        _learnedNames = NoLearnedNames;
        _learnedUntold = 0;
        _byName.Clear();
        _byNameAndExits.Clear();
        _leversByRoom.Clear();
        _boatPassages.Clear();
        _castPocketEntrances.Clear();
    }

    // Recognise placed guardian monsters whose greet dialogue opens a door on
    // their own room (the "ask the guard the password and the gate lifts"
    // mechanic). Each resolved `ask` command is folded into the byExit table the
    // Action#N lever cells populate, so the Door → MultiActionHidden promotion
    // below routes the walker across the guarded door via ask-then-move instead
    // of the planner discarding it as an unpickable / unbashable door. The quest
    // ability the game gates the open behind is untrackable by the client, so the
    // door is always made routable and the walker reacts to whether the command
    // actually opens it. No-op without a TBInfo source (parameterless / test
    // construction) or when the set has no Monsters table.
    private void InjectGuardDoorActions(
        Dictionary<(RoomKey Room, Direction Dir), List<ExitAction>> byExit,
        List<(RoomKey Room, Greeter Monster)> greeterRooms)
    {
        // Gather the alternative ask-commands per guarded exit first. Every greet
        // topic that opens the same door is an ALTERNATIVE — the crossing sends
        // only the first — so they collapse into one ExitAction rather than
        // inflating the exit's required-action count.
        var guardExits = new Dictionary<(RoomKey Room, Direction Dir),
            (List<string> Commands, int AbilityGate, RoomKey Target)>();

        foreach ((RoomKey key, Greeter greeter) in greeterRooms)
        {
            if (greeter.Doors is not { IsEmpty: false } doors) continue;

            RoomExitSet exits = ExitsOf(_rooms[key]);
            foreach (GuardDoorCommandResolver.GuardDoorCommand cmd in doors.For(key.Room))
            {
                // Only a pick/bash-proof door needs the promotion; a normal
                // exit that happens to share the direction is left untouched.
                if (!exits.TryGetValue(cmd.Direction, out RoomExit exit)) continue;
                if (exit.Hint is not (RoomExitHint.Door or RoomExitHint.KeyLocked)) continue;

                var exitKey = (key, cmd.Direction);
                if (!guardExits.TryGetValue(exitKey, out (List<string> Commands, int AbilityGate, RoomKey Target) acc))
                    guardExits[exitKey] = acc = (new List<string>(), cmd.AbilityGate, exit.Target);
                if (!acc.Commands.Any(c => string.Equals(c, cmd.Command, StringComparison.OrdinalIgnoreCase)))
                    acc.Commands.Add(cmd.Command);
            }
        }

        foreach (((RoomKey Room, Direction Dir) exitKey,
                  (List<string> commands, int abilityGate, RoomKey target)) in guardExits)
        {
            if (commands.Count == 0) continue;
            if (!byExit.TryGetValue(exitKey, out List<ExitAction>? list))
                byExit[exitKey] = list = new List<ExitAction>();
            list.Add(new ExitAction(StepNumber: 1, commands, RemoteSourceRoom: null));

            _log?.Log(LogSeverity.Info, "RoomGraph",
                $"Room {exitKey.Room} {exitKey.Dir} → {target}: guarded door opened by '{commands[0]}'"
                + (commands.Count > 1 ? $" (or {commands.Count - 1} other topic(s))" : "")
                + (abilityGate > 0
                    ? $" — gated on {AbilityNames.GetName(abilityGate) ?? $"ability {abilityGate}"}; walker issues the command and reacts to whether it opens."
                    : "."));
        }
    }

    // The monsters that carry a greet, by number: what the guard-door and
    // ask-transport passes look a room's monsters up in. The raw table is evicted
    // once read — memory-hygiene parity with the Rooms load. Empty without a
    // TBInfo source (nothing could read a greet) or a Monsters table.
    private Dictionary<int, Greeter> ReadGreeters()
    {
        if (_tbinfo is null) return new();

        GameTable? monstersDoc = _cache.GetTable("Monsters");
        if (monstersDoc is null) return new();

        var greeters = new Dictionary<int, Greeter>();
        foreach (GameRow row in monstersDoc.Rows)
        {
            if (!TryReadInt(row, "Number", out int num) || num <= 0) continue;
            int greet = TryReadIntOrZero(row, "GreetTXT");
            if (greet <= 0) continue;
            greeters[num] = new Greeter(greet, TryReadString(row, "Name") ?? string.Empty);
        }
        _cache.EvictTable("Monsters");
        return greeters;
    }

    // Promote room-CMD reveal actions into routable exit data. A room whose CMD
    // chain ends in a `remoteaction` that opens one of its own hidden exits (the
    // canonical case: 9/1012 "Crumbling Ruin, Entrance" CMD 1422 `clear rubble`
    // revealing the N passage to 9/1013) imports the exit as MultiActionHidden but
    // with NO action data — so BFS skips it unconditionally and the room beyond is
    // unroutable. The keyword the player types already parses (it backs the room
    // tooltip); here we fold it into the same `byExit` bucket the guard-door / lever
    // promotions use, so the patch loop attaches a satisfiable MultiActionExitData
    // and SpecialExitDispatch sends the keyword then the cardinal. A chain with a
    // `testskill` (the rubble case) is carried as a Rolled step: its roll can miss,
    // and the engines send the keyword again when the cardinal bonks.
    private void InjectRoomCmdRemoteActions(
        Dictionary<(RoomKey Room, Direction Dir), List<ExitAction>> byExit, List<RoomKey> cmdRooms)
    {
        if (_tbinfo is null) return;

        foreach (RoomKey key in cmdRooms)
        {
            Room room = _rooms[key];

            // Collapse synonyms (clear/move/push × rubble/mound/rock all reveal the
            // same exit) to the first keyword per (target room, direction).
            Dictionary<(int Room, int Dir), (string Keyword, bool Rolled)>? firstByExit = null;
            foreach (TBInfoActionResolver.RemoteActionExit ra in
                     TBInfoActionResolver.EnumerateRemoteActions(_tbinfo, room.Cmd))
                (firstByExit ??= new()).TryAdd((ra.RoomNumber, ra.DirectionIndex), (ra.Keyword, ra.Rolled));
            if (firstByExit is null) continue;

            foreach (((int roomNum, int dirIdx), (string keyword, bool rolled)) in firstByExit)
            {
                Direction dir = s_directions[dirIdx];
                RoomKey target = new(key.Map, roomNum);
                // Same-room reveal (target is the CMD's own room) attaches to that
                // room's own exit; a cross-room remoteaction carries RemoteSourceRoom
                // so the dispatch treats it as remote (and safely fails if it can't
                // resolve, rather than sending the keyword in the wrong room).
                RoomKey? remote = target == key ? null : key;

                var exitKey = (target, dir);
                if (!byExit.TryGetValue(exitKey, out List<ExitAction>? list))
                    byExit[exitKey] = list = new List<ExitAction>();
                list.Add(new ExitAction(StepNumber: 1, new[] { keyword }, remote, Rolled: rolled));

                _log?.Log(LogSeverity.Info, "RoomGraph",
                    $"Room {target} {dir}: hidden exit revealed by room-CMD action '{keyword}'"
                    + (remote is { } r ? $" (issued from {r})" : "") + ".");
            }
        }
    }

    // Monster ids from a raw Lair cell — the integers between the "(Max N):"
    // header and the trailing "[group-index]" token (e.g.
    // "(Max 2): 503,[30-24-24-2]" → 503; "(Max 2): 1141,2175,2176,[…]" →
    // 1141, 2175, 2176).
    private static LairMonsterIdReader LairMonsterIds(string? rawLairTag) => new(rawLairTag);

    // Reads the cell in place and as a struct: the build asks this of every lair
    // room twice, and slicing and splitting the cell, or even an allocated
    // iterator over it, each time adds up to a tenth of the graph build.
    private struct LairMonsterIdReader
    {
        private readonly string? _tag;
        private readonly int _end;
        private int _pos;

        public LairMonsterIdReader(string? rawLairTag)
        {
            _tag = rawLairTag;
            Current = 0;
            if (string.IsNullOrEmpty(rawLairTag))
            {
                _pos = _end = 0;
                return;
            }
            int colon = rawLairTag.IndexOf(':');
            int start = colon >= 0 ? colon + 1 : 0;
            int bracket = rawLairTag.IndexOf('[', start);
            _end = bracket >= 0 ? bracket : rawLairTag.Length;
            _pos = start;
        }

        public int Current { get; private set; }

        public readonly LairMonsterIdReader GetEnumerator() => this;

        public bool MoveNext()
        {
            while (_pos < _end)
            {
                int comma = _tag!.IndexOf(',', _pos, _end - _pos);
                int stop = comma >= 0 ? comma : _end;
                bool parsed = int.TryParse(_tag.AsSpan(_pos, stop - _pos).Trim(), out int id) && id > 0;
                _pos = stop + 1;
                if (!parsed) continue;
                Current = id;
                return true;
            }
            return false;
        }
    }

    // Re-hint the cardinal exits a room's CMD teleport shadows. A room with
    // Room.Cmd > 0 whose TBInfo Action chain teleports to a room that is ALSO
    // the target of a hard cardinal exit — a stat-gated Door or a KeyLocked
    // exit — gets that exit re-hinted Teleport, so the walker crosses it with
    // the CMD keyword (e.g. `ring chime`) via SpecialExitDispatch instead of
    // trying to bash / pick a door it may not be able to open. This also covers
    // the `(Item: N)` variant (an emblem/chime teleport gated on carrying an item):
    // promoting it here — TBInfo-gated on the CMD actually teleporting to the
    // exit's target — instead of blanket-promoting every `(Item: N)` exit in a
    // CMD room (which mis-fired on non-teleport CMDs like a make-emblem chain,
    // stranding a walkable item-gated passage; report paradigm-20260729-221044).
    // The exit cell alone can't detect any of this — it needs the TBInfo
    // destination to know WHICH exit the teleport stands in for. Reusing
    // the existing cardinal slot (rather than synthesising a directionless edge)
    // keeps graph connectivity and the planar map layout unchanged. No-op
    // without a TBInfo source (parameterless / test construction).
    private void PromoteCmdTeleportExits(List<RoomKey> cmdRooms,
        Dictionary<int, (string Keyword, RoomKey Destination, int MinLevel)[]> cmdTeleports)
    {
        if (_tbinfo is null) return;

        foreach (RoomKey key in cmdRooms)
        {
            Room room = _rooms[key];

            (string Keyword, RoomKey Destination, int MinLevel)[] teleports = TeleportsOf(room.Cmd, cmdTeleports);
            if (teleports.Length == 0) continue;

            RoomExitSet exits = ExitsOf(room);
            RoomExitSet? rebuilt = null;
            foreach ((Direction dir, RoomExit exit) in exits)
            {
                if (exit.Hint is not (RoomExitHint.Door or RoomExitHint.KeyLocked or RoomExitHint.Item)) continue;
                if (!TeleportsTo(teleports, exit.Target)) continue;
                // Clear the door key on a re-hinted Teleport: you teleport PAST a
                // shadowed Door/KeyLocked exit, so its key isn't needed. Keep it only
                // when the origin was a genuine Item gate (a real carry requirement).
                // Without this, the Teleport possession-gate in MovementFilter would
                // wrongly block a chime/emblem teleport on the bypassed door's key.
                int keyItemId = exit.Hint == RoomExitHint.Item ? exit.KeyItemId : 0;
                rebuilt = (rebuilt ?? exits).With(dir,
                    exit with { Hint = RoomExitHint.Teleport, KeyItemId = keyItemId });
                _log?.Log(LogSeverity.Debug, "RoomGraph",
                    $"Room {key} {dir} → {exit.Target}: {exit.Hint} re-hinted Teleport (CMD {room.Cmd} teleport bypass).");
            }
            if (rebuilt is not null) _rooms[key] = room with { Exits = rebuilt };
        }
    }

    // The literal teleports of a CMD's own block, in the order it lists them.
    private (string Keyword, RoomKey Destination, int MinLevel)[] TeleportsOf(int cmd,
        Dictionary<int, (string Keyword, RoomKey Destination, int MinLevel)[]> cmdTeleports)
    {
        if (!cmdTeleports.TryGetValue(cmd, out (string Keyword, RoomKey Destination, int MinLevel)[]? teleports))
            cmdTeleports[cmd] = teleports = TBInfoTeleportResolver.EnumerateTeleports(_tbinfo!, cmd).ToArray();
        return teleports;
    }

    private static bool TeleportsTo((string Keyword, RoomKey Destination, int MinLevel)[] teleports, RoomKey room)
    {
        foreach ((string _, RoomKey destination, int _) in teleports)
            if (destination == room) return true;
        return false;
    }

    // Flag the cardinal exits whose post-move cast is a random-destination
    // teleport. A "(Cast: pre-N, post-M)" exit is parsed as a plain cardinal
    // carrying PreCastSpell/PostCastSpell; resolving whether the post-cast spell
    // teleports (and to a random room) needs the spell catalog, which isn't
    // available at exit-parse time. Here, with the catalog loaded, every exit
    // whose post-cast spell is a random TeleportRoom (Abil 140 value 0, spanning
    // more than one room) gets CastTeleportRandom set — the "we can't predict
    // where this lands" signal the planar map and the router key off. A fixed
    // (single-room) cast-teleport stays an ordinary cardinal that happens to
    // cast. No-op without a catalog (parameterless / test construction).
    //
    // A post-cast spell that teleports through a textblock is read here too: the
    // room the exit cell names is only passed through, and the exit is given the
    // rooms its spell sends on to (see RoomExit CastLandings). Left as a plain
    // cardinal when a landing isn't in the graph.
    private void PromoteCastTeleportExits(List<RoomKey> castRooms)
    {
        if (_spellCatalog is null) return;

        // A realm's cast-on-walk exits share a dozen or so spells between them, and
        // reading a spell's row into a formula is the costly part of this pass.
        Dictionary<int, SpellFormulaInput?> spells = new();

        foreach (RoomKey key in castRooms)
        {
            Room room = _rooms[key];

            RoomExitSet exits = ExitsOf(room);
            RoomExitSet? rebuilt = null;
            foreach ((Direction dir, RoomExit exit) in exits)
            {
                if (exit.PostCastSpell <= 0) continue;
                if (!spells.TryGetValue(exit.PostCastSpell, out SpellFormulaInput? read))
                    spells[exit.PostCastSpell] = read = _spellCatalog.GetFormulaByNumber(exit.PostCastSpell);
                if (read is not { } spell) continue;
                if (TBInfoCastTeleportResolver.IsRandomTeleport(spell))
                {
                    IReadOnlyList<RoomKey>? pool = TBInfoCastTeleportResolver.RandomTeleportTargets(spell, key.Map);
                    rebuilt = (rebuilt ?? exits).With(dir,
                        exit with { CastTeleportRandom = true, CastTeleportTargets = pool });
                    _log?.Log(LogSeverity.Debug, "RoomGraph",
                        $"Room {key} {dir} → {exit.Target}: cast-on-walk spell {exit.PostCastSpell} is a random teleport "
                        + $"(pool of {pool?.Count ?? 0}) — marked non-routable.");
                    continue;
                }

                if (_tbinfo is null) continue;
                if (TBInfoCastTeleportResolver.TextblockLandings(spell, _tbinfo) is not { } landings) continue;
                if (landings.Any(l => !_rooms.ContainsKey(l.Room))) continue;
                rebuilt = (rebuilt ?? exits).With(dir, exit with { CastLandings = landings });
                _log?.Log(LogSeverity.Debug, "RoomGraph",
                    $"Room {key} {dir} → {exit.Target}: cast-on-walk spell {exit.PostCastSpell} teleports on to "
                    + string.Join(", ", landings.Select(l => l.ItemId > 0 ? $"{l.Room} (with item {l.ItemId})" : l.Room.ToString()))
                    + " — a party is split on the way through.");
            }
            if (rebuilt is not null) _rooms[key] = room with { Exits = rebuilt };
        }
    }

    // Flag the one-way mouth of a cast-teleport "pocket" — a sink area (e.g. the
    // Warped Asylum) entered by a cast-on-walk exit with no walk-back. A cast
    // exit a→b is a pocket entrance iff b cannot reach a by any directed route:
    // stepping through it commits you to the pocket. The planar mapper keys off
    // this to stop expanding the layout through the mouth, so the pocket's rooms
    // don't get poured into the HOUSING map's coordinate grid and drawn on top of
    // it — while a walker standing inside the pocket (origin within it) still lays
    // the whole area out, because its internal cast exits are reciprocal and so
    // are NOT entrances. Distinct from CastTeleportRandom: that flag is about an
    // unpredictable LANDING, this is about one-way TOPOLOGY. A cast exit can be
    // both (the asylum mouth is), one, or neither. No-op without any cast-on-walk
    // exits present.
    private void MarkCastPocketEntrances(List<RoomKey> castRooms)
    {
        ReachSearch search = new();
        foreach (RoomKey key in castRooms)
        {
            Room room = _rooms[key];

            RoomExitSet exits = ExitsOf(room);
            RoomExitSet? rebuilt = null;
            foreach ((Direction dir, RoomExit exit) in exits)
            {
                if (!exit.CastsOnWalk) continue;
                if (CanReach(exit.Target, key, search)) continue;

                rebuilt = (rebuilt ?? exits).With(dir, exit with { CastPocketEntrance = true });
                _castPocketEntrances.Add((key, dir));
                _log?.Log(LogSeverity.Debug, "RoomGraph",
                    $"Room {key} {dir} → {exit.Target}: one-way cast pocket entrance (no walk-back) — layout stops here from outside.");
            }
            if (rebuilt is not null) _rooms[key] = room with { Exits = rebuilt };
        }
    }

    // What the reachability searches of one build share: their working sets, and
    // the dead ends they have proven so far.
    private sealed class ReachSearch
    {
        public HashSet<RoomKey> Seen = new();
        public readonly Queue<RoomKey> Queue = new();

        // Each set is closed — no exit leads out of it — kept from a search that
        // ran out without finding its goal. A later search that walks into one
        // not holding its own goal has nothing to find in there. The mouths of
        // one sink are told apart this way without walking the sink again for
        // each, and so are the few one-way drops into the open world, each of
        // which would otherwise walk half the realm.
        public readonly List<HashSet<RoomKey>> DeadEnds = new();

        // Of those, the ones the search in hand may stop at, and the ones it did.
        public readonly List<HashSet<RoomKey>> WithoutGoal = new();
        public readonly List<HashSet<RoomKey>> StoppedAt = new();
    }

    // Directed reachability over cardinal exits: can `from` reach `goal` by any
    // sequence of room.Exits hops? Early-exits the moment `goal` is found. Used
    // only at graph-build time to classify cast pocket entrances. Between two
    // searches that pass only flags an exit, never moves one, so what one search
    // proved unreachable stays so for the next.
    private bool CanReach(RoomKey from, RoomKey goal, ReachSearch search)
    {
        if (from == goal) return true;

        HashSet<RoomKey> seen = search.Seen;
        Queue<RoomKey> queue = search.Queue;
        seen.Clear();
        queue.Clear();
        seen.Add(from);
        queue.Enqueue(from);

        search.WithoutGoal.Clear();
        search.StoppedAt.Clear();
        foreach (HashSet<RoomKey> deadEnd in search.DeadEnds)
            if (!deadEnd.Contains(goal)) search.WithoutGoal.Add(deadEnd);

        bool walked = false;
        while (queue.Count > 0)
        {
            RoomKey cur = queue.Dequeue();

            bool stopped = false;
            foreach (HashSet<RoomKey> deadEnd in search.WithoutGoal)
            {
                if (!deadEnd.Contains(cur)) continue;
                if (!search.StoppedAt.Contains(deadEnd)) search.StoppedAt.Add(deadEnd);
                stopped = true;
                break;
            }
            if (stopped) continue;

            if (!_rooms.TryGetValue(cur, out Room? room)) continue;
            walked = true;
            foreach (RoomKey next in ExitsOf(room).Targets)
            {
                if (next == goal) return true;
                if (seen.Add(next)) queue.Enqueue(next);
            }
        }

        // Ran out: every exit of a room seen leads to another one seen or into a
        // dead end stopped at, so together they are closed too. Kept only when
        // the walk covered rooms of its own.
        if (walked)
        {
            foreach (HashSet<RoomKey> deadEnd in search.StoppedAt) seen.UnionWith(deadEnd);
            search.DeadEnds.Add(seen);
            search.Seen = new HashSet<RoomKey>();
        }
        return false;
    }

    // Paradigm-1.9.1 data quirk: room 9/1259, inside the Warped Asylum, carries a
    // `pull lever` CMD teleport back to 9/1180 (the entry area) that stock v1.11p
    // doesn't have (its 9/1259 CMD is 0). Left in the graph, that one escape edge
    // makes the asylum look two-way: pocket-entrance detection's CanReach walks the
    // lever back out, and CollectPocket balloons through it into the overworld, so
    // the cast mouth never gets flagged CastPocketEntrance and the maze never gets
    // indexed — the reshuffle/relocalize solver only engages on a detected pocket.
    // Skipping the lever's synthesis makes the Paradigm asylum act as the same
    // one-way pocket dimension it already is on stock. Scoped to this exact room;
    // inert on stock, where the edge doesn't exist. The lever remains a real in-game
    // exit the player can still pull manually — we just don't route through it.
    private static readonly RoomKey ParadigmAsylumLeverRoom = new(9, 1259);

    // Synthesise a routable Direction.Teleport edge for a CMD teleport whose
    // destination isn't already reachable by a cardinal exit. PromoteCmdTeleport-
    // Exits handles the case where a teleport shadows an existing Door/KeyLocked
    // cardinal (re-hinting that slot in place); this covers the other shape — a
    // `go hole` / cast-teleport hop that drops the player into a room with no
    // cardinal edge from here, which the planar graph otherwise can't route
    // through. The synthesised exit carries the crossing keyword in TextCommands
    // and the minlevel gate in MinLevel, so SpecialExitDispatch crosses it with
    // that command and MovementFilter skips it for an under-level character. It's
    // filed under the non-planar Direction.Teleport slot so it never perturbs the
    // map layout or the cardinal ExitMask fingerprint.
    //
    // A DETERMINISTIC single-destination teleport becomes a plain routable edge.
    // A purely-random / multi-room landing (a "bridge jump" into one of five
    // river rooms, with no fixed branch to anchor a nominal target) can't be
    // planned through BFS and is left un-synthesised. A keyword whose branches
    // DISAGREE — a quest-gated portal that's a fixed hop for some players but a
    // random dump for others — becomes a GATEWAY edge (flagged GatewayTeleport,
    // nominal target = the fixed branch's landing): still routable, but BFS uses
    // it only as a last resort when no deterministic path exists (see
    // TryFirstRoutableTeleport / BfsMapper.FindPath). A room with several
    // distinct single-dest teleports keeps the first — one Direction.Teleport
    // slot per room.
    //
    // Exception: the Paradigm-1.9.1 Warped Asylum lever (see ParadigmAsylumLever-
    // Room) is deliberately NOT synthesised, so the asylum stays a one-way pocket
    // the maze solver can detect and navigate.
    private void BuildTeleportEdges(List<RoomKey> cmdRooms,
        Dictionary<int, (string Keyword, RoomKey Destination, int MinLevel)[]> cmdTeleports)
    {
        if (_tbinfo is null) return;

        foreach (RoomKey key in cmdRooms)
        {
            Room room = _rooms[key];
            RoomExitSet exits = ExitsOf(room);
            if (exits.ContainsKey(Direction.Teleport)) continue;        // already has one
            if (key == ParadigmAsylumLeverRoom) continue;               // pocket-dimension: lever ignored

            // A teleport whose destination is already a cardinal (or re-hinted)
            // exit target needs no synthetic edge; the walker reaches it the
            // normal way. TryFirstRoutableTeleport checks each landing against
            // the room's exits.
            if (TryFirstRoutableTeleport(room, exits, cmdTeleports,
                    out RoomKey dest, out string keyword, out int minLevel, out bool isGateway,
                    out bool wholeParty))
            {
                RoomExit tele = new(
                    dest,
                    RoomExitHint.Teleport,
                    RawHint: "CMD teleport",
                    TextCommands: new[] { keyword },
                    MinLevel: minLevel,
                    GatewayTeleport: isGateway,
                    MovesWholeParty: wholeParty);
                _rooms[key] = room with { Exits = exits.With(Direction.Teleport, tele) };
                _log?.Log(LogSeverity.Debug, "RoomGraph",
                    $"Room {key}: synthesised {(isGateway ? "gateway " : string.Empty)}Teleport edge "
                    + $"'{keyword}' → {dest}"
                    + (minLevel > 0 ? $" (Level {minLevel}+)." : "."));
            }
        }
    }

    // Synthesise a routable Direction.Teleport edge for an item whose USE teleports
    // the player (ItemUseTeleportResolver). Unlike CMD / greet / cast teleports,
    // the source room is not a world anchor — the teleport lives entirely on the
    // item, gated on a room fixture (`roomitem`). We anchor the edge at that fixture
    // item's own room (its "Obtained From: Room M/R"), which is exactly where the
    // game lets the teleport fire. The synthesised exit carries `use <item>` in
    // TextCommands and the holder item as KeyItemId, so SpecialExitDispatch crosses
    // it by using the item and MovementFilter skips it when the item isn't carried.
    // One Teleport slot per room; filed under the non-planar Direction.Teleport slot
    // so the map layout is unchanged. No-op without a spell catalog / TBInfo
    // (parameterless / test construction). The item is consumed on use, but the
    // return trip is a normal exit, so the walker never needs it to come back.
    private void BuildItemUseTeleportEdges()
    {
        if (_tbinfo is null || _spellCatalog is null) return;
        GameTable? itemsDoc = _cache.GetTable("Items");
        if (itemsDoc is null) return;

        // A teleport with no fixture gate has nothing to anchor on. The teleports
        // are a handful of items at most, so they are found first and only their
        // gate items are then looked up.
        List<ItemUseTeleportResolver.ItemUseTeleport> teleports = new();
        HashSet<int> gateItems = new();
        foreach (ItemUseTeleportResolver.ItemUseTeleport t in
                 ItemUseTeleportResolver.Enumerate(itemsDoc, _spellCatalog, _tbinfo))
        {
            if (t.GateItemId <= 0) continue;
            teleports.Add(t);
            gateItems.Add(t.GateItemId);
        }
        if (teleports.Count == 0) return;

        // Gate item Number → "Obtained From" text, for anchoring a teleport's
        // roomitem gate to its source room. The raw Items table stays cached for the
        // many lazy runtime consumers (unlike Rooms / Monsters, it is NOT evicted here).
        Dictionary<int, string> obtainedFrom = new();
        foreach (GameRow row in itemsDoc.Rows)
        {
            if (!TryReadInt(row, "Number", out int num) || !gateItems.Contains(num)) continue;
            if (TryReadString(row, "Obtained From") is { } src && !string.IsNullOrWhiteSpace(src))
                obtainedFrom[num] = src;
        }

        foreach (ItemUseTeleportResolver.ItemUseTeleport t in teleports)
        {
            if (!obtainedFrom.TryGetValue(t.GateItemId, out string? src)) continue;

            foreach (RoomKey source in ParseObtainedFromRooms(src))
            {
                if (!_rooms.TryGetValue(source, out Room? room)) continue;
                if (room.Exits.ContainsKey(Direction.Teleport)) continue;             // one Teleport slot per room
                if (room.Exits.Values.Any(e => e.Target == t.Destination)) continue;  // already reachable

                string cmd = string.IsNullOrEmpty(t.HolderItemName) ? "use" : "use " + t.HolderItemName;
                RoomExit tele = new(
                    t.Destination,
                    RoomExitHint.Teleport,
                    RawHint: "item-use teleport",
                    KeyItemId: t.HolderItemId,
                    TextCommands: new[] { cmd },
                    MinLevel: t.MinLevel);
                _rooms[source] = room with { Exits = ExitsOf(room).With(Direction.Teleport, tele) };
                _log?.Log(LogSeverity.Info, "RoomGraph",
                    $"Room {source}: synthesised item-use Teleport edge '{cmd}' → {t.Destination} "
                    + $"(gated on carrying item #{t.HolderItemId})"
                    + (t.MinLevel > 0 ? $" (Level {t.MinLevel}+)." : "."));
            }
        }
    }

    private static readonly System.Text.RegularExpressions.Regex s_obtainedFromRoom =
        new(@"Room\s+(\d+)/(\d+)",
            System.Text.RegularExpressions.RegexOptions.Compiled
            | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Pull every "Room M/R" entry out of an item's "Obtained From" text as a RoomKey:
    // the rooms the item lies in. The cell is split into its entries first, and only
    // one that starts "Room " counts. Searching the whole cell would also pick a room
    // out of a "Room(<command>) M/R" entry (a room where a command hands the item
    // over, not one where it lies) or out of a command's own wording.
    private static IEnumerable<RoomKey> ParseObtainedFromRooms(string obtainedFrom)
    {
        foreach (string entry in GameData.RoomCommandSourceParser.Entries(obtainedFrom))
        {
            if (!entry.StartsWith("Room ", StringComparison.Ordinal)) continue;
            System.Text.RegularExpressions.Match m = s_obtainedFromRoom.Match(entry);
            if (m.Success && m.Index == 0
                && int.TryParse(m.Groups[1].Value, out int map) && int.TryParse(m.Groups[2].Value, out int room))
                yield return new RoomKey(map, room);
        }
    }

    // NPC ask-transport edges. A placed/lair monster whose greet dialogue ports
    // the player elsewhere (GreetTeleportResolver) contributes a Direction.Teleport
    // exit whose crossing command is `ask <noun> <keyword>` — the analogue of the
    // CMD teleport edges BuildTeleportEdges mints, for pockets whose only egress is
    // asking an NPC (the Floating Citadel's Grey Lord ports a character to Town
    // Square). Runs AFTER BuildTeleportEdges so a room's own CMD teleport wins the
    // single Direction.Teleport slot; only UNGATED transports are synthesised (see
    // GreetTeleportResolver), so the walker never routes through a quest-gated port
    // it can't satisfy.
    private void BuildGreetTeleportEdges(List<(RoomKey Room, Greeter Monster)> greeterRooms)
    {
        // A room's greeters come one after another, the placed NPC first. Once one
        // of them has minted the room's edge, the slot check below turns the rest
        // away: the first NPC that yields a routable transport wins.
        foreach ((RoomKey key, Greeter greeter) in greeterRooms)
        {
            GreetTeleportResolver.GreetTeleport[] ports = greeter.Ports;
            if (ports.Length == 0) continue;

            Room room = _rooms[key];
            RoomExitSet exits = ExitsOf(room);
            if (exits.ContainsKey(Direction.Teleport)) continue; // CMD teleport already claimed the slot

            foreach (GreetTeleportResolver.GreetTeleport t in ports)
            {
                if (HasExitTo(exits, t.Destination)) continue;         // reachable the normal way already
                if (t.Destination == key) continue;                    // a self-port is no egress

                RoomExit tele = new(
                    t.Destination,
                    RoomExitHint.Teleport,
                    RawHint: "greet teleport",
                    TextCommands: new[] { t.Command },
                    MinLevel: t.MinLevel,
                    // A class-restricted transport (a `class N` directive in the
                    // greet chain) is stamped as the edge's ClassGate so
                    // MovementFilter.IsClassGateBlocked drops it for every class
                    // but N — the wrong class never routes through it.
                    ClassGate: t.RequiredClass,
                    GatewayTeleport: false,
                    // Every person who asks pays the fare, so MovementFilter
                    // gates the edge on the poorest party member's wallet.
                    FareCopper: t.FareCopper);
                _rooms[key] = room with { Exits = exits.With(Direction.Teleport, tele) };

                _log?.Log(LogSeverity.Info, "RoomGraph",
                    $"Room {key}: synthesised NPC ask-transport Teleport edge "
                    + $"'{t.Command}' → {t.Destination}"
                    + (t.MinLevel > 0 ? $" (Level {t.MinLevel}+)" : "")
                    + (t.RequiredClass > 0 ? $" (class {t.RequiredClass} only)" : "")
                    + (t.FareCopper > 0 ? $" (fare {t.CostText})" : "")
                    + ".");
                break; // one Direction.Teleport slot per room
            }
        }
    }

    // First single-destination teleport off a room's CMD chain whose landing
    // isn't already an exit target — literal teleports first, then cast teleports
    // (which need the spell catalog). Returns false when the room has no routable
    // single-dest teleport to a new room.
    private bool TryFirstRoutableTeleport(Room room, RoomExitSet exits,
        Dictionary<int, (string Keyword, RoomKey Destination, int MinLevel)[]> cmdTeleports,
        out RoomKey dest, out string keyword, out int minLevel, out bool isGateway, out bool wholeParty)
    {
        dest = default;
        keyword = string.Empty;
        minLevel = 0;
        isGateway = false;
        wholeParty = false;

        foreach ((string kw, RoomKey d, int lvl) in TeleportsOf(room.Cmd, cmdTeleports))
        {
            if (HasExitTo(exits, d)) continue;
            dest = d; keyword = kw; minLevel = lvl;
            return true;
        }

        if (_spellCatalog is not null)
        {
            // Classify each cast keyword by the branches it fires. A keyword
            // whose branches are ALL deterministic hops to the SAME room is a
            // plain routable shortcut. A keyword with a fixed branch AND a random
            // (or disagreeing) sibling is a GATEWAY — routable, but only as a
            // last resort, with its nominal landing set to a fixed branch.
            //
            // MajorMUD gates some portals on an untrackable quest flag and swaps
            // the landing on it: Paradigm map 9's `go portal` casts "lower portal
            // (invited)" (fixed → 9/1424) for quest-flagged characters but "lower
            // portal (uninvited)" (random → the Caves of Chaos) for everyone
            // else, both under the one keyword. The client can't read that flag,
            // so the fixed branch can't be minted as a plain shortcut (it would
            // random-dump an unflagged character mid-route). As a gateway it's
            // ignored by BFS's deterministic pass — the narrow-stair climb to
            // Morukai wins from inside the cluster — and crossed only by the
            // fallback pass from the overworld, where no deterministic path up
            // exists. The walker re-plans from wherever the cast drops it.
            Dictionary<string, CastKeywordRoutability> byKeyword =
                new(StringComparer.OrdinalIgnoreCase);
            List<string> keywordOrder = new();
            foreach ((string kw, IReadOnlyList<RoomKey> dests, bool random, int lvl, bool party) in
                     TBInfoCastTeleportResolver.EnumerateCastTeleports(
                         _tbinfo!, room.Cmd, room.Key.Map, _spellCatalog))
            {
                bool branchFixed = !random && dests.Count == 1;
                bool seen = byKeyword.TryGetValue(kw, out CastKeywordRoutability acc);
                if (!seen)
                {
                    keywordOrder.Add(kw);
                    acc.WholeParty = true;
                }
                // Party-safe only when every branch the keyword can fire moves the party.
                acc.WholeParty &= party;
                if (branchFixed)
                {
                    if (acc.FixedDest is null) { acc.FixedDest = dests[0]; acc.MinLevel = lvl; }
                    else if (acc.FixedDest != dests[0]) acc.SawDisagree = true;
                }
                else acc.SawNonFixed = true;
                byKeyword[kw] = acc;
            }

            // Prefer a purely-deterministic keyword; fall back to a gateway.
            foreach (bool wantGateway in new[] { false, true })
            {
                foreach (string kw in keywordOrder)
                {
                    CastKeywordRoutability ck = byKeyword[kw];
                    if (ck.FixedDest is not { } d) continue;   // no fixed landing to anchor
                    bool gateway = ck.SawNonFixed || ck.SawDisagree;
                    if (gateway != wantGateway) continue;
                    if (HasExitTo(exits, d)) continue;
                    dest = d; keyword = kw; minLevel = ck.MinLevel; isGateway = gateway;
                    wholeParty = ck.WholeParty;
                    return true;
                }
            }
        }

        return false;
    }

    // Accumulator for TryFirstRoutableTeleport: tracks, per cast keyword, the
    // first fixed-branch landing (the nominal target) and whether any sibling
    // branch is random or disagrees — which turns the keyword into a gateway.
    private struct CastKeywordRoutability
    {
        public RoomKey? FixedDest;
        public int MinLevel;
        public bool SawNonFixed;
        public bool SawDisagree;
        public bool WholeParty;
    }

    // Index every sea-captain dock's `secure passage to <place>` sailings off its
    // CMD chain into the boat side-table. Unlike BuildTeleportEdges, a boat is NOT
    // minted as a Direction.Teleport cardinal: a dock offers several sailings at
    // once and the single Teleport slot holds only one, so they live in
    // _boatPassages for the boat router to stitch a land → boat → land route.
    // BoatPassageResolver walks each sailing's random-table + transit-spell chain
    // to land the arrival RoomKey; a sailing whose arrival it can't resolve is
    // dropped (not routable). No-op without both a TBInfo source and a spell
    // catalog — a boat's arrival can't be resolved without the transit spells.
    private void BuildBoatEdges(List<RoomKey> cmdRooms)
    {
        if (_tbinfo is null || _spellCatalog is null) return;

        // Empty text blocks linked round in a circle (a damaged set). Every room
        // whose command leads into one meets the same blocks, so they are gathered
        // over the whole pass and named in one line.
        HashSet<int> loopedBlocks = new();

        foreach (RoomKey key in cmdRooms)
        {
            Room room = _rooms[key];

            List<BoatPassage>? passages = null;
            foreach (BoatPassage passage in
                     BoatPassageResolver.EnumerateBoatPassages(_tbinfo, key, room.Cmd, _spellCatalog, loopedBlocks))
                (passages ??= new()).Add(passage);
            if (passages is null) continue;

            _boatPassages[key] = passages;
            foreach (BoatPassage p in passages)
                _log?.Log(LogSeverity.Info, "RoomGraph",
                    $"Dock {key}: '{p.Keyword}' → {p.ArrivalRoom} (Level {p.MinLevel}+, "
                    + $"fare {p.FareCopper} copper"
                    + (p.RequiresCheckability ? ", quest-attunement required)." : ")."));
        }

        if (loopedBlocks.Count > 0)
            _log?.Log(LogSeverity.Warn, "RoomGraph",
                $"TBInfo text block(s) {string.Join(", ", loopedBlocks.Order())} of '{ActiveSet}' are empty and link "
                + "back to themselves; nothing was read past them. The set's text blocks are damaged there.");
    }

    private void BuildSecondaryIndexes()
    {
        var lairSize = new Dictionary<int, (int Count, long SumMax, int MaxMax)>();
        foreach (Room room in _rooms.Values)
        {
            if (room.HasUnknownName) _unnamedInData.Add(room.Key);

            if (!_byName.TryGetValue(room.Name, out List<Room>? nameBucket))
            {
                nameBucket = new List<Room>();
                _byName[room.Name] = nameBucket;
            }
            nameBucket.Add(room);

            var tuple = (room.Name, room.ExitMask);
            if (!_byNameAndExits.TryGetValue(tuple, out List<RoomKey>? exitBucket))
            {
                exitBucket = new List<RoomKey>();
                _byNameAndExits[tuple] = exitBucket;
            }
            exitBucket.Add(room.Key);

            // Accumulate lair sizes per monster named in this room's tag. The
            // per-room cap is the "(Max N)"; a lair room with no cap counts as 0
            // (matching the record's Spawns-In "(lair: 0)").
            if (room.HasLair)
            {
                int lairMax = RoomTooltipBuilder.TryParseLairMax(room.RawLairTag, out int m) ? m : 0;
                foreach (int id in LairMonsterIds(room.RawLairTag))
                    lairSize[id] = lairSize.TryGetValue(id, out (int Count, long SumMax, int MaxMax) a)
                        ? (a.Count + 1, a.SumMax + lairMax, Math.Max(a.MaxMax, lairMax))
                        : (1, lairMax, lairMax);
            }
        }
        _lairSizeByMonster = lairSize;
    }

    // One Rooms row: the typed room, the Action#N cells living in its exit slots,
    // and its MultiAction modifier strings.
    private readonly record struct RowRead(
        Room? Room, List<MultiActionExitData.ActionCell>? Actions, Dictionary<Direction, string>? Modifiers);

    // exits belongs to the reading thread and is empty before and after each row;
    // texts is the one thing the readers share (see Shared).
    private static RowRead ReadRow(GameRow row, RoomExitSet.Builder exits, ConcurrentDictionary<string, string> texts)
    {
        if (!TryReadInt(row, "Map Number", out int map)) return default;
        if (!TryReadInt(row, "Room Number", out int roomNumber)) return default;
        if (map <= 0 || roomNumber <= 0) return default;

        // The MDB export emits rows with a literal null Name for rooms
        // the sysop fills on a separate table (typical of map-15
        // ganghouse rooms in 1.x non-Paradigm exports). Don't drop
        // those — keep them in the graph as null-name so the tracker
        // can learn the real name on first observation. Render through
        // Room.DisplayName for any user-facing label.
        string name = Shared(texts, TryReadString(row, "Name") ?? string.Empty);

        int cmd = TryReadIntOrZero(row, "CMD");

        // Each exit cell is read once and looked at twice: as an exit, and as the
        // Action#N cell or "Needs N Actions" modifier of a multi-action exit. An
        // Action cell has no RoomKey prefix, so it never parses as an exit.
        uint mask = 0;
        List<MultiActionExitData.ActionCell>? actions = null;
        Dictionary<Direction, string>? modBucket = null;
        foreach (Direction dir in s_directions)
        {
            if (!row.TryGetProperty(s_exitPropertyNames[(int)dir], out GameValue el)
                || el.ValueKind != JsonValueKind.String)
                continue;
            // Three cells in four are the "0" of no exit. Told apart in place,
            // they are never made into strings.
            if (el.ValueEquals("0"u8)) continue;
            string? cell = el.GetString();
            if (string.IsNullOrWhiteSpace(cell)) continue;

            if (RoomExit.TryParseWire(cell, out RoomExit exit))
            {
                // NOTE: the Item → Teleport promotion is NOT done here. A blanket
                // "(Item: N) exit + non-zero CMD → Teleport" test mis-fires on rooms
                // whose CMD is an unrelated action (e.g. a make-emblem chain, CMD 1618
                // at 15/945), turning a walkable item-gated passage into a teleport the
                // walker can't resolve — the walk dies on step 1 (report
                // paradigm-20260729-221044). The real promotion runs in the instance
                // pass PromoteCmdTeleportExits, which can see _tbinfo and only promotes
                // exits whose CMD chain genuinely teleports to the exit's target.
                if (exit.RawHint is { } hint) exit = exit with { RawHint = Shared(texts, hint) };
                exits.Set(dir, exit);
                mask |= 1u << (int)dir;
            }

            // "Action#N [on the …]" (multi-step variant) and the
            // step-less "Action [on the …]" (single-action variant)
            // both start with "Action" + a non-word character. Hand
            // both forms to the parser; non-matching cells return
            // null and are silently skipped.
            if (cell.StartsWith("Action", StringComparison.OrdinalIgnoreCase)
                && cell.Length > 6
                && (cell[6] == '#' || cell[6] == ' ' || cell[6] == '['))
            {
                if (MultiActionExitData.ParseActionCell(cell) is { } action)
                    (actions ??= new()).Add(action);
            }
            else if (cell.Contains("Hidden/Needs", StringComparison.OrdinalIgnoreCase))
            {
                modBucket ??= new();
                modBucket[dir] = cell;
            }
        }

        string? lairRaw = TryReadString(row, "Lair");
        lairRaw = lairRaw is null || IsLairSentinel(lairRaw) ? null : Shared(texts, lairRaw);

        Room room = new()
        {
            Key = new RoomKey(map, roomNumber),
            Name = name,
            Light = TryReadIntOrZero(row, "Light"),
            Shop  = TryReadIntOrZero(row, "Shop"),
            Spell = TryReadIntOrZero(row, "Spell"),
            Npc   = TryReadIntOrZero(row, "NPC"),
            Delay = TryReadIntOrZero(row, "Delay"),
            Cmd   = cmd,
            RawLairTag = lairRaw,
            Exits = exits.Build(),
            ExitMask = mask,
        };
        return new RowRead(room, actions, modBucket);
    }

    // The one string the build keeps for this text. Which reader's copy that is
    // depends on who got there first; the copies read the same.
    private static string Shared(ConcurrentDictionary<string, string> texts, string text) =>
        texts.GetOrAdd(text, text);

    // MDB exports the empty cell as either a NUL, a literal blank, or
    // whitespace. Treat any of those as "no lair".
    private static bool IsLairSentinel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return true;
        // The NUL-as-single-character case from the MDB importer.
        if (raw.Length == 1 && raw[0] == '\0') return true;
        return false;
    }

    private static bool TryReadInt(GameRow row, string property, out int value)
    {
        if (row.TryGetProperty(property, out GameValue el) &&
            el.ValueKind == JsonValueKind.Number &&
            el.TryGetInt32(out value))
            return true;
        value = 0;
        return false;
    }

    private static int TryReadIntOrZero(GameRow row, string property)
        => TryReadInt(row, property, out int v) ? v : 0;

    private static string? TryReadString(GameRow row, string property)
        => row.TryGetProperty(property, out GameValue el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static uint MaskFromSet(IReadOnlySet<Direction> exits)
    {
        uint mask = 0;
        foreach (Direction d in exits) mask |= 1u << (int)d;
        return mask;
    }

    // Direction-to-MDB-property-name table — order matches the enum.
    private static readonly Direction[] s_directions =
    {
        Direction.N, Direction.S, Direction.E, Direction.W,
        Direction.NE, Direction.NW, Direction.SE, Direction.SW,
        Direction.U, Direction.D,
    };

    private static readonly string[] s_exitPropertyNames =
    {
        "N", "S", "E", "W", "NE", "NW", "SE", "SW", "U", "D",
    };
}
