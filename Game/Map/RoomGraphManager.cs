using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FujinTerm.Game.GameData;
using FujinTerm.Game.Spells;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// In-memory graph of every room in the active game-data set — primary lookup
// for the navigation stack (room tracker, BFS mapper, walker, loop manager,
// auto-lair scheduler).
//
// Seeding: the graph reads Rooms.json through GameDataCache.GetRawTable once per
// active-set switch. Every row turns into a typed Room indexed by RoomKey.
// RoomExit values are produced inline from the per-direction MDB cells
// ("1/3" / "1/3 (Door)" / "0"). The raw JsonDocument is evicted from
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

    // Live predicate identifying rooms a normal player can never stand in (dev /
    // orphan rooms flagged CannotBeReached in the room blacklist). When set,
    // FindCandidates drops matching keys so the RoomTracker never resolves the
    // player's position into one. Evaluated per call so blacklist edits take
    // effect without rebuilding any index. Null (default) means "no exclusions".
    private Func<RoomKey, bool>? _isUnreachable;

    // Set the graph was last built from, or null if empty.
    public string? ActiveSet { get; private set; }

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

    // Fires once per name learned via LearnRoomName. Carries the room key + the
    // name the tracker just adopted. AppServices subscribes to surface the
    // persist-to-Rooms.json prompt.
    public event Action<RoomKey, string>? RoomNameLearned;

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

    // Every room in the active set that carries at least one trapped exit (per
    // the imported (Trap) hint). MapControl iterates this to overlay red
    // half-connector glyphs without rescanning every room.
    public IEnumerable<Room> TrappedRooms => _rooms.Values.Where(r => r.HasTrappedExits);

    // True when the active set contains exactly one room with this room's
    // (Name, ExitMask) tuple. False for ambiguous tuples and for rooms not in
    // the active graph.
    public bool IsUnique(RoomKey key)
    {
        if (!_rooms.TryGetValue(key, out Room? room)) return false;
        return _byNameAndExits.TryGetValue((room.Name, room.ExitMask), out List<RoomKey>? keys)
               && keys.Count == 1;
    }

    // Rebuild the graph from setName's Rooms.json. Pass null to clear. Safe to
    // call repeatedly; idempotent on no-op transitions (same set still fires
    // GraphReloaded because the caller may have re-imported the underlying
    // file).
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

        // Fold in guard-monster doors: a placed monster whose greet dialogue
        // opens a door on its own room contributes an `ask` command that the
        // promotion below treats exactly like a lever action.
        InjectGuardDoorActions(byExit);

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
            if (exit.Hint != RoomExitHint.MultiActionHidden && !leverDoor) continue;

            (int count, bool specific) = perRoomModifiers.TryGetValue(roomKey, out var mods)
                && mods.TryGetValue(dir, out string? modCell)
                ? MultiActionExitData.ParseModifier(modCell)
                : (actions.Count, false);

            actions.Sort(static (a, b) => a.StepNumber.CompareTo(b.StepNumber));
            var data = new MultiActionExitData(count, specific, actions);
            RoomExit patched = leverDoor
                ? exit with { Hint = RoomExitHint.MultiActionHidden, MultiAction = data }
                : exit with { MultiAction = data };
            var rebuilt = new Dictionary<Direction, RoomExit>(room.Exits)
            {
                [dir] = patched
            };
            _rooms[roomKey] = room with { Exits = rebuilt };

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

        PromoteCmdTeleportExits();
        PromoteCastTeleportExits();
        MarkCastPocketEntrances();
        BuildTeleportEdges();
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
        _leversByRoom.Clear();
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
    private void InjectGuardDoorActions(Dictionary<(RoomKey Room, Direction Dir), List<ExitAction>> byExit)
    {
        if (_tbinfo is null) return;

        // Build a small id → (greet, name) map of only monsters carrying a greet,
        // then evict the raw table — memory-hygiene parity with the Rooms load.
        JsonDocument? monstersDoc = _cache.GetRawTable("Monsters");
        if (monstersDoc is null) return;

        var greeters = new Dictionary<int, (int Greet, string Name)>();
        foreach (JsonElement row in monstersDoc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!TryReadInt(row, "Number", out int num) || num <= 0) continue;
            int greet = TryReadIntOrZero(row, "GreetTXT");
            if (greet <= 0) continue;
            greeters[num] = (greet, TryReadString(row, "Name") ?? string.Empty);
        }
        _cache.EvictTable("Monsters");
        if (greeters.Count == 0) return;

        // Gather the alternative ask-commands per guarded exit first. Every greet
        // topic that opens the same door is an ALTERNATIVE — the crossing sends
        // only the first — so they collapse into one ExitAction rather than
        // inflating the exit's required-action count.
        var guardExits = new Dictionary<(RoomKey Room, Direction Dir),
            (List<string> Commands, int AbilityGate, RoomKey Target)>();

        foreach ((RoomKey key, Room room) in _rooms)
        {
            foreach (int monsterId in EnumerateRoomMonsters(room))
            {
                if (!greeters.TryGetValue(monsterId, out (int Greet, string Name) g)) continue;

                foreach (GuardDoorCommandResolver.GuardDoorCommand cmd in
                         GuardDoorCommandResolver.Resolve(_tbinfo, g.Greet, g.Name, key.Room))
                {
                    // Only a pick/bash-proof door needs the promotion; a normal
                    // exit that happens to share the direction is left untouched.
                    if (!room.Exits.TryGetValue(cmd.Direction, out RoomExit exit)) continue;
                    if (exit.Hint is not (RoomExitHint.Door or RoomExitHint.KeyLocked)) continue;

                    var exitKey = (key, cmd.Direction);
                    if (!guardExits.TryGetValue(exitKey, out (List<string> Commands, int AbilityGate, RoomKey Target) acc))
                        guardExits[exitKey] = acc = (new List<string>(), cmd.AbilityGate, exit.Target);
                    if (!acc.Commands.Any(c => string.Equals(c, cmd.Command, StringComparison.OrdinalIgnoreCase)))
                        acc.Commands.Add(cmd.Command);
                }
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

    // Monster numbers standing in a room: the single placed NPC (Room.Npc) plus
    // every mob id in the room's lair group. Yields each id once in that order.
    private static IEnumerable<int> EnumerateRoomMonsters(Room room)
    {
        if (room.Npc > 0) yield return room.Npc;
        foreach (int id in LairMonsterIds(room.RawLairTag)) yield return id;
    }

    // Monster ids from a raw Lair cell — the integers between the "(Max N):"
    // header and the trailing "[group-index]" token (e.g.
    // "(Max 2): 503,[30-24-24-2]" → 503; "(Max 2): 1141,2175,2176,[…]" →
    // 1141, 2175, 2176).
    private static IEnumerable<int> LairMonsterIds(string? rawLairTag)
    {
        if (string.IsNullOrEmpty(rawLairTag)) yield break;
        int colon = rawLairTag.IndexOf(':');
        int start = colon >= 0 ? colon + 1 : 0;
        int bracket = rawLairTag.IndexOf('[', start);
        int end = bracket >= 0 ? bracket : rawLairTag.Length;
        if (end <= start) yield break;
        foreach (string part in rawLairTag[start..end].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), out int id) && id > 0) yield return id;
        }
    }

    // Re-hint the cardinal exits a room's CMD teleport shadows. A room with
    // Room.Cmd > 0 whose TBInfo Action chain teleports to a room that is ALSO
    // the target of a hard cardinal exit — a stat-gated Door or a KeyLocked
    // exit — gets that exit re-hinted Teleport, so the walker crosses it with
    // the CMD keyword (e.g. `ring chime`) via SpecialExitDispatch instead of
    // trying to bash / pick a door it may not be able to open. TryReadRoom's
    // Item->Teleport promotion covers the (Item: N) variant; this covers the
    // door-bypass variant, which the exit cell alone can't detect — it needs the
    // TBInfo destination to know WHICH exit the teleport stands in for. Reusing
    // the existing cardinal slot (rather than synthesising a directionless edge)
    // keeps graph connectivity and the planar map layout unchanged. No-op
    // without a TBInfo source (parameterless / test construction).
    private void PromoteCmdTeleportExits()
    {
        if (_tbinfo is null) return;

        foreach (RoomKey key in _rooms.Keys.ToArray())
        {
            Room room = _rooms[key];
            if (room.Cmd <= 0) continue;

            HashSet<RoomKey>? destinations = null;
            foreach (var (_, dest, _) in TBInfoTeleportResolver.EnumerateTeleports(_tbinfo, room.Cmd))
                (destinations ??= new()).Add(dest);
            if (destinations is null) continue;

            Dictionary<Direction, RoomExit>? rebuilt = null;
            foreach ((Direction dir, RoomExit exit) in room.Exits)
            {
                if (exit.Hint is not (RoomExitHint.Door or RoomExitHint.KeyLocked)) continue;
                if (!destinations.Contains(exit.Target)) continue;
                (rebuilt ??= new(room.Exits))[dir] = exit with { Hint = RoomExitHint.Teleport };
                _log?.Log(LogSeverity.Debug, "RoomGraph",
                    $"Room {key} {dir} → {exit.Target}: door re-hinted Teleport (CMD {room.Cmd} teleport bypass).");
            }
            if (rebuilt is not null) _rooms[key] = room with { Exits = rebuilt };
        }
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
    private void PromoteCastTeleportExits()
    {
        if (_spellCatalog is null) return;

        foreach (RoomKey key in _rooms.Keys.ToArray())
        {
            Room room = _rooms[key];

            Dictionary<Direction, RoomExit>? rebuilt = null;
            foreach ((Direction dir, RoomExit exit) in room.Exits)
            {
                if (exit.PostCastSpell <= 0) continue;
                if (_spellCatalog.GetFormulaByNumber(exit.PostCastSpell) is not { } spell) continue;
                if (!TBInfoCastTeleportResolver.IsRandomTeleport(spell)) continue;

                IReadOnlyList<RoomKey>? pool = TBInfoCastTeleportResolver.RandomTeleportTargets(spell, key.Map);
                (rebuilt ??= new(room.Exits))[dir] =
                    exit with { CastTeleportRandom = true, CastTeleportTargets = pool };
                _log?.Log(LogSeverity.Debug, "RoomGraph",
                    $"Room {key} {dir} → {exit.Target}: cast-on-walk spell {exit.PostCastSpell} is a random teleport "
                    + $"(pool of {pool?.Count ?? 0}) — marked non-routable.");
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
    private void MarkCastPocketEntrances()
    {
        foreach (RoomKey key in _rooms.Keys.ToArray())
        {
            Room room = _rooms[key];

            Dictionary<Direction, RoomExit>? rebuilt = null;
            foreach ((Direction dir, RoomExit exit) in room.Exits)
            {
                if (!exit.CastsOnWalk) continue;
                if (CanReach(exit.Target, key)) continue;

                (rebuilt ??= new(room.Exits))[dir] = exit with { CastPocketEntrance = true };
                _log?.Log(LogSeverity.Debug, "RoomGraph",
                    $"Room {key} {dir} → {exit.Target}: one-way cast pocket entrance (no walk-back) — layout stops here from outside.");
            }
            if (rebuilt is not null) _rooms[key] = room with { Exits = rebuilt };
        }
    }

    // Directed reachability over cardinal exits: can `from` reach `goal` by any
    // sequence of room.Exits hops? Early-exits the moment `goal` is found. Used
    // only at graph-build time to classify cast pocket entrances, so a per-call
    // BFS bounded by the source's reachable set is cheap enough (sink pockets are
    // small; the rare fixed cast-teleport that DOES return finds its goal fast).
    private bool CanReach(RoomKey from, RoomKey goal)
    {
        if (from == goal) return true;

        var seen = new HashSet<RoomKey> { from };
        var queue = new Queue<RoomKey>();
        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            RoomKey cur = queue.Dequeue();
            if (!_rooms.TryGetValue(cur, out Room? room)) continue;
            foreach (RoomExit exit in room.Exits.Values)
            {
                RoomKey next = exit.Target;
                if (next == goal) return true;
                if (seen.Add(next)) queue.Enqueue(next);
            }
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
    private void BuildTeleportEdges()
    {
        if (_tbinfo is null) return;

        foreach (RoomKey key in _rooms.Keys.ToArray())
        {
            Room room = _rooms[key];
            if (room.Cmd <= 0) continue;
            if (room.Exits.ContainsKey(Direction.Teleport)) continue;   // already has one
            if (key == ParadigmAsylumLeverRoom) continue;               // pocket-dimension: lever ignored

            // Existing exit targets — a teleport whose destination is already a
            // cardinal (or re-hinted) exit target needs no synthetic edge; the
            // walker reaches it the normal way.
            HashSet<RoomKey> existingTargets = new();
            foreach ((Direction _, RoomExit ex) in room.Exits) existingTargets.Add(ex.Target);

            if (TryFirstRoutableTeleport(room, existingTargets,
                    out RoomKey dest, out string keyword, out int minLevel, out bool isGateway))
            {
                RoomExit tele = new(
                    dest,
                    RoomExitHint.Teleport,
                    RawHint: "CMD teleport",
                    TextCommands: new[] { keyword },
                    MinLevel: minLevel,
                    GatewayTeleport: isGateway);
                var rebuilt = new Dictionary<Direction, RoomExit>(room.Exits)
                {
                    [Direction.Teleport] = tele
                };
                _rooms[key] = room with { Exits = rebuilt };
                _log?.Log(LogSeverity.Debug, "RoomGraph",
                    $"Room {key}: synthesised {(isGateway ? "gateway " : string.Empty)}Teleport edge "
                    + $"'{keyword}' → {dest}"
                    + (minLevel > 0 ? $" (Level {minLevel}+)." : "."));
            }
        }
    }

    // First single-destination teleport off a room's CMD chain whose landing
    // isn't already an exit target — literal teleports first, then cast teleports
    // (which need the spell catalog). Returns false when the room has no routable
    // single-dest teleport to a new room.
    private bool TryFirstRoutableTeleport(Room room, HashSet<RoomKey> existingTargets,
        out RoomKey dest, out string keyword, out int minLevel, out bool isGateway)
    {
        dest = default;
        keyword = string.Empty;
        minLevel = 0;
        isGateway = false;

        foreach ((string kw, RoomKey d, int lvl) in
                 TBInfoTeleportResolver.EnumerateTeleports(_tbinfo!, room.Cmd))
        {
            if (existingTargets.Contains(d)) continue;
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
            foreach ((string kw, IReadOnlyList<RoomKey> dests, bool random, int lvl) in
                     TBInfoCastTeleportResolver.EnumerateCastTeleports(
                         _tbinfo!, room.Cmd, room.Key.Map, _spellCatalog))
            {
                bool branchFixed = !random && dests.Count == 1;
                if (!byKeyword.TryGetValue(kw, out CastKeywordRoutability acc))
                    keywordOrder.Add(kw);
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
                    if (existingTargets.Contains(d)) continue;
                    dest = d; keyword = kw; minLevel = ck.MinLevel; isGateway = gateway;
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
        // Room.DisplayName for any user-facing label.
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

    // MDB exports the empty cell as either a NUL, a literal blank, or
    // whitespace. Treat any of those as "no lair".
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
