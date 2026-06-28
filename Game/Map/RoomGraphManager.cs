using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

/// <summary>
/// In-memory graph of every room in the active game-data set —
/// primary lookup for the Phase 7 navigation stack (room tracker,
/// BFS mapper, walker, loop manager, auto-lair scheduler).
/// </summary>
/// <remarks>
/// <para>
/// <b>Seeding</b>: the graph reads <c>Rooms.json</c> through
/// <see cref="GameDataCache.GetRawTable"/> once per active-set switch.
/// Every row turns into a typed <see cref="Room"/> indexed by
/// <see cref="RoomKey"/>. <see cref="Game.Map.RoomExit"/> values are
/// produced inline from the per-direction MDB cells
/// (<c>"1/3"</c> / <c>"1/3 (Door)"</c> / <c>"0"</c>). The raw
/// <see cref="JsonDocument"/> is evicted from <see cref="GameDataCache"/>
/// immediately after conversion (per the project's memory-hygiene
/// pattern set by <see cref="MonsterOverlaySeedStore"/>).
/// </para>
/// <para>
/// <b>Uniqueness index</b>: a side table keyed on <c>(Name, ExitMask)</c>
/// gives the room tracker its "is this a 1-of-1 room?" answer in O(1).
/// When the user lands in a room whose tuple resolves to exactly one
/// candidate, the tracker promotes to <c>Located</c> without further
/// reconciliation. Buckets with &gt; 1 candidate are surfaced via
/// <see cref="FindCandidates"/> for the Tier-2 footprint matcher.
/// </para>
/// <para>
/// <b>Wiring</b>: <see cref="AppServices"/> subscribes
/// <see cref="OnActiveSetChanged"/> to
/// <see cref="GameDataCache.ActiveSetChanged"/> at construction time —
/// every set swap rebuilds the graph from scratch. Subscribers to
/// <see cref="GraphReloaded"/> drop any per-set room references they
/// were holding.
/// </para>
/// </remarks>
public sealed class RoomGraphManager
{
    private readonly GameDataCache _cache;
    private readonly LogService? _log;
    private readonly Dictionary<RoomKey, Room> _rooms = new();
    private readonly Dictionary<string, List<Room>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Name, uint ExitMask), List<RoomKey>> _byNameAndExits = new();

    /// <summary>Set the graph was last built from, or <c>null</c> if empty.</summary>
    public string? ActiveSet { get; private set; }

    /// <summary>Number of rooms in the active graph (<c>0</c> when no set is active or load failed).</summary>
    public int RoomCount => _rooms.Count;

    /// <summary>
    /// Fires after every successful (re)load, including the
    /// transition to no-set-active (empty graph). Subscribers should
    /// drop any cached room references and re-pull what they need.
    /// </summary>
    public event Action? GraphReloaded;

    public RoomGraphManager(GameDataCache cache) : this(cache, log: null) { }

    public RoomGraphManager(GameDataCache cache, LogService? log)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _log = log;
    }

    /// <summary>
    /// Direct lookup by primary key. Returns <c>null</c> when the key
    /// doesn't resolve in the active set's graph.
    /// </summary>
    public Room? GetRoom(RoomKey key) =>
        _rooms.TryGetValue(key, out Room? room) ? room : null;

    /// <summary>
    /// Fires once per name learned via
    /// <see cref="LearnRoomName"/>. Carries the room key + the name
    /// the tracker just adopted. <see cref="Services.AppServices"/>
    /// subscribes to surface the persist-to-Rooms.json prompt.
    /// </summary>
    public event Action<RoomKey, string>? RoomNameLearned;

    /// <summary>
    /// Replace the in-memory <see cref="Room"/> at <paramref name="key"/>
    /// with a copy whose <see cref="Room.Name"/> is the new value, and
    /// reshuffle the <c>_byName</c> / <c>_byNameAndExits</c> indexes so
    /// later candidate searches find the updated tuple. No-op when the
    /// key isn't in the graph, the existing room already has the same
    /// name, or the new name is null/empty.
    /// </summary>
    /// <returns>
    /// The new <see cref="Room"/> instance (with the updated name)
    /// when the learn succeeded; <c>null</c> when it was a no-op.
    /// </returns>
    public Room? LearnRoomName(RoomKey key, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (!_rooms.TryGetValue(key, out Room? existing)) return null;
        if (string.Equals(existing.Name, name, StringComparison.Ordinal)) return null;

        // Drop the old (name, mask) index entries before re-inserting
        // — the empty-name bucket still belongs in _byName when the
        // pre-learn name was empty, but we just won't find it by an
        // empty-string lookup anyway. Be defensive on both sides.
        DropFromIndexes(existing);

        Room replaced = existing with { Name = name };
        _rooms[key] = replaced;
        AddToIndexes(replaced);

        _log?.Log(LogSeverity.Info, "RoomGraph",
            $"Learned name '{name}' for {key} (was '{existing.Name}').");
        RoomNameLearned?.Invoke(key, name);
        return replaced;
    }

    private void DropFromIndexes(Room room)
    {
        if (!string.IsNullOrEmpty(room.Name)
            && _byName.TryGetValue(room.Name, out List<Room>? nameBucket))
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

    /// <summary>
    /// All rooms in the active set whose <see cref="Room.Name"/> matches
    /// <paramref name="name"/> case-insensitively. Empty when no match.
    /// </summary>
    public IReadOnlyList<Room> FindByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return Array.Empty<Room>();
        return _byName.TryGetValue(name, out List<Room>? rooms)
            ? rooms
            : (IReadOnlyList<Room>)Array.Empty<Room>();
    }

    /// <summary>
    /// All rooms in the active set whose <c>(Name, exit-set)</c> tuple
    /// matches. Used by RoomTracker to detect the 1-of-1 case — when
    /// the result has exactly one entry, the tracker can promote to
    /// <c>Located</c> without further reconciliation.
    /// </summary>
    public IReadOnlyList<RoomKey> FindCandidates(string name, IReadOnlySet<Direction> exits)
    {
        if (string.IsNullOrEmpty(name)) return Array.Empty<RoomKey>();
        ArgumentNullException.ThrowIfNull(exits);

        uint mask = MaskFromSet(exits);
        return _byNameAndExits.TryGetValue((name, mask), out List<RoomKey>? keys)
            ? keys
            : (IReadOnlyList<RoomKey>)Array.Empty<RoomKey>();
    }

    /// <summary>
    /// Read-only snapshot of every room in the active set, in load
    /// order. The Navigation search (PR 7.12) iterates this for
    /// substring matches; the room tree / favourites populators reuse
    /// the same enumeration. Empty when no set is active.
    /// </summary>
    public IEnumerable<Room> Rooms => _rooms.Values;

    /// <summary>
    /// Every room in the active set that carries at least one trapped
    /// exit (per the imported <c>(Trap)</c> hint). The
    /// <c>MapControl</c> (PR 7.11) iterates this to overlay red
    /// half-connector glyphs without rescanning every room.
    /// </summary>
    public IEnumerable<Room> TrappedRooms => _rooms.Values.Where(r => r.HasTrappedExits);

    /// <summary>
    /// True when the active set contains exactly one room with this
    /// room's <c>(Name, ExitMask)</c> tuple. False for ambiguous tuples
    /// and for rooms not in the active graph.
    /// </summary>
    public bool IsUnique(RoomKey key)
    {
        if (!_rooms.TryGetValue(key, out Room? room)) return false;
        return _byNameAndExits.TryGetValue((room.Name, room.ExitMask), out List<RoomKey>? keys)
               && keys.Count == 1;
    }

    /// <summary>
    /// Rebuild the graph from <paramref name="setName"/>'s
    /// <c>Rooms.json</c>. Pass <c>null</c> to clear. Safe to call
    /// repeatedly; idempotent on no-op transitions (same set still
    /// fires <see cref="GraphReloaded"/> because the caller may have
    /// re-imported the underlying file).
    /// </summary>
    public void OnActiveSetChanged(string? setName)
    {
        Clear();
        ActiveSet = setName;

        if (string.IsNullOrWhiteSpace(setName))
        {
            _log?.Log(LogSeverity.Info, "RoomGraph", "No active game-data set; room graph cleared.");
            GraphReloaded?.Invoke();
            return;
        }

        JsonDocument? doc = _cache.GetRawTable("Rooms");
        if (doc is null)
        {
            _log?.Log(LogSeverity.Warn, "RoomGraph",
                $"Active set '{setName}' has no Rooms.json; room graph is empty.");
            GraphReloaded?.Invoke();
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

        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (!TryReadRoom(row, out Room? room))
            {
                skipped++;
                continue;
            }
            _rooms[room.Key] = room;
            parsed++;

            // Pre-cache MultiAction modifier strings + scan for
            // Action#N cells living in non-exit slots of the same row.
            if (row.ValueKind == JsonValueKind.Object)
            {
                Dictionary<Direction, string>? modBucket = null;
                foreach (Direction dir in s_directions)
                {
                    string? cell = TryReadString(row, s_exitPropertyNames[(int)dir]);
                    if (string.IsNullOrWhiteSpace(cell)) continue;
                    // "Action#N [on the …]" (multi-step variant) and the
                    // step-less "Action [on the …]" (single-action variant)
                    // both start with "Action" + a non-word character. Hand
                    // both forms to the parser; non-matching cells return
                    // null and are silently skipped.
                    if (cell.StartsWith("Action", StringComparison.OrdinalIgnoreCase)
                        && cell.Length > 6
                        && (cell[6] == '#' || cell[6] == ' ' || cell[6] == '['))
                    {
                        MultiActionExitData.ActionCell? action = MultiActionExitData.ParseActionCell(cell);
                        if (action is not null)
                            actionCells.Add((room.Key, action));
                    }
                    else if (cell.Contains("Hidden/Needs", StringComparison.OrdinalIgnoreCase))
                    {
                        modBucket ??= new();
                        modBucket[dir] = cell;
                    }
                }
                if (modBucket is not null) perRoomModifiers[room.Key] = modBucket;
            }
        }

        // Second pass: attach gathered action cells to the right
        // MultiActionHidden exit. "On the X exit of this room" → action
        // belongs to the source room's X exit. "On the X exit of room
        // M/R" → action lives in the SOURCE row but applies to the
        // REMOTE room's X exit; carry RemoteSourceRoom forward so v1
        // can fail gracefully on those (cross-room expander is a
        // follow-up).
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
            // knows to fail on cross-row data v1.
            RoomKey? remote = cell.RemoteSourceRoom is not null ? sourceRoom : null;
            list.Add(new ExitAction(cell.StepNumber, cell.Commands, remote));
        }

        // Patch each MultiActionHidden exit with the gathered data.
        foreach (((RoomKey roomKey, Direction dir), List<ExitAction> actions) in byExit)
        {
            if (!_rooms.TryGetValue(roomKey, out Room? room)) continue;
            if (!room.Exits.TryGetValue(dir, out RoomExit exit)) continue;
            if (exit.Hint != RoomExitHint.MultiActionHidden) continue;

            (int count, bool specific) = perRoomModifiers.TryGetValue(roomKey, out var mods)
                && mods.TryGetValue(dir, out string? modCell)
                ? MultiActionExitData.ParseModifier(modCell)
                : (actions.Count, false);

            actions.Sort(static (a, b) => a.StepNumber.CompareTo(b.StepNumber));
            var data = new MultiActionExitData(count, specific, actions);
            var rebuilt = new Dictionary<Direction, RoomExit>(room.Exits)
            {
                [dir] = exit with { MultiAction = data }
            };
            _rooms[roomKey] = room with { Exits = rebuilt };
        }

        BuildSecondaryIndexes();

        // Free the raw JSON; the typed graph is the source of truth now.
        _cache.EvictTable("Rooms");

        _log?.Log(LogSeverity.Info, "RoomGraph",
            $"Loaded {parsed} room(s) from '{setName}' Rooms.json"
            + (skipped > 0 ? $" ({skipped} malformed row(s) skipped)." : "."));

        GraphReloaded?.Invoke();
    }

    private void Clear()
    {
        _rooms.Clear();
        _byName.Clear();
        _byNameAndExits.Clear();
    }

    private void BuildSecondaryIndexes()
    {
        foreach (Room room in _rooms.Values)
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
    }

    private static bool TryReadRoom(JsonElement row, out Room room)
    {
        room = null!;
        if (row.ValueKind != JsonValueKind.Object) return false;

        if (!TryReadInt(row, "Map Number", out int map)) return false;
        if (!TryReadInt(row, "Room Number", out int roomNumber)) return false;
        if (map <= 0 || roomNumber <= 0) return false;

        // The MDB export emits rows with a literal null Name for rooms
        // the sysop fills on a separate table (typical of map-15
        // ganghouse rooms in 1.x non-Paradigm exports). Don't drop
        // those — keep them in the graph as null-name so the tracker
        // can learn the real name on first observation. Render through
        // <see cref="Room.DisplayName"/> for any user-facing label.
        string name = TryReadString(row, "Name") ?? string.Empty;

        int cmd = TryReadIntOrZero(row, "CMD");

        var exits = new Dictionary<Direction, RoomExit>();
        uint mask = 0;
        foreach (Direction dir in s_directions)
        {
            string? cell = TryReadString(row, s_exitPropertyNames[(int)dir]);
            if (!RoomExit.TryParseWire(cell, out RoomExit exit)) continue;

            // Item → Teleport promotion: an (Item: N) exit on a room
            // whose CMD field is non-zero is the party-breaking
            // teleport pattern (TBInfo chain → text keyword →
            // teleport directive). The exit parser can't see Room.Cmd,
            // so the promotion happens here.
            if (exit.Hint == RoomExitHint.Item && cmd > 0)
            {
                exit = exit with { Hint = RoomExitHint.Teleport };
            }

            exits[dir] = exit;
            mask |= 1u << (int)dir;
        }

        string? lairRaw = TryReadString(row, "Lair");
        if (IsLairSentinel(lairRaw)) lairRaw = null;

        room = new Room
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
            Exits = exits,
            ExitMask = mask,
        };
        return true;
    }

    /// <summary>
    /// MDB exports the empty cell as either <c>" "</c> (NUL),
    /// a literal blank, or whitespace. Treat any of those as "no lair".
    /// </summary>
    private static bool IsLairSentinel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return true;
        // The NUL-as-single-character case from the MDB importer.
        if (raw.Length == 1 && raw[0] == '\0') return true;
        return false;
    }

    private static bool TryReadInt(JsonElement row, string property, out int value)
    {
        if (row.TryGetProperty(property, out JsonElement el) &&
            el.ValueKind == JsonValueKind.Number &&
            el.TryGetInt32(out value))
            return true;
        value = 0;
        return false;
    }

    private static int TryReadIntOrZero(JsonElement row, string property)
        => TryReadInt(row, property, out int v) ? v : 0;

    private static string? TryReadString(JsonElement row, string property)
        => row.TryGetProperty(property, out JsonElement el) && el.ValueKind == JsonValueKind.String
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
