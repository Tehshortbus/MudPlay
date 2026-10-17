using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MudPlay.Game.Spells;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Turns a loop's waypoints into the ordered ExpRoute the LoopExpSimulator
// replays: BFS-expands the cycle to its room sequence, then reads each room's
// lair and NPC-fixture exp yield + respawn timer from game data. Reuses
// LoopExpander (route), LairTimerStore (respawn seconds — already realm-aware)
// and the raw Monsters / Lairs tables. The monster index is lazy and drops when
// the active set changes.
//
// A room can contribute two targets: its lair group AND a separately-placed NPC
// fixture (e.g. the cave-worm room). Only exp-bearing targets are emitted —
// a 0-exp fixture (the barmaid) is left out so it doesn't tax lap time for no
// gain. See GAME_MECHANICS "Lair respawn timers" and "NPC-placed monsters".
public sealed class RouteExpResolver : IDisposable
{
    private readonly RoomGraphManager _graph;
    private readonly BfsMapper _bfs;
    private readonly LairTimerStore _timers;
    private readonly GameDataCache _cache;
    private readonly LogService? _log;

    private Dictionary<int, MonsterInfo>? _monsters;        // monster Number -> resolved info
    private Dictionary<int, List<int>>? _summonSpells;     // spell Number -> summoned monster ids
    private Dictionary<(int Id, int Mobs), CascadeResult>? _roomCascades;   // (base monster, room spawn count) -> capped cascade
    private Dictionary<int, (int TextBlock, string Name)>? _spellTextBlocks; // spell Number -> its TextBlock action + name
    private Dictionary<int, string>? _tbActions;           // TBInfo Number -> raw Action string
    private Dictionary<int, RoomSummon?>? _roomSummons;    // room-entry spell Number -> resolved summon (null cached)
    private Dictionary<int, RoomSummonTable?>? _summonTables; // placed room-entry spell Number -> its summon roll table (null = summons nothing)

    // Abil slot value 12 marks a "summon monster" ability; AbilVal holds the summoned
    // monster Number. A monster's DeathSpell fires this spell on death.
    private const int SummonAbility = 12;

    // Per-monster facts the estimator needs. Exp is the true value EXP × ExpMulti
    // (bosses store the un-multiplied EXP with a separate multiplier). A boss —
    // GameLimit 1 (BossCatalog.IsBoss) — is killable only once per its
    // regen, so it's pulled out of the lair average and amortised over RegenHours.
    // DeathSpell is the spell fired when it dies (0 = none) — the summon-cascade root.
    // HasRegenWait is the raw RegenTime > 0 (RegenHours is floored at 1 for the
    // amortising maths and so can't tell a regen of 0).
    private readonly record struct MonsterInfo(
        int Exp, bool IsBoss, int RegenHours, string Name, int DeathSpell, bool HasRegenWait);

    public RouteExpResolver(
        RoomGraphManager graph, BfsMapper bfs, LairTimerStore timers, GameDataCache cache, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(cache);
        _graph = graph;
        _bfs = bfs;
        _timers = timers;
        _cache = cache;
        _log = log;
        _cache.ActiveSetChanged += OnSetChanged;
    }

    public void Dispose() => _cache.ActiveSetChanged -= OnSetChanged;

    private void OnSetChanged(string? _)
    {
        _monsters = null;
        _summonSpells = null;
        _roomCascades = null;
        _spellTextBlocks = null;
        _tbActions = null;
        _roomSummons = null;
        _summonTables = null;
    }

    public ExpRoute Resolve(IReadOnlyList<LoopWaypoint> waypoints, IRoomFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(waypoints);
        IReadOnlyList<RoomKey> keys = LoopExpander.ResolveCycleRoomKeys(waypoints, _bfs, _graph, filter);
        if (keys.Count < 2) return new ExpRoute(Array.Empty<ExpRoomVisit>());

        // keys ends back at the start; drop that closing duplicate — the wrap
        // step is implicit in the simulator's lap.
        int lapCount = keys.Count - 1;
        var lap = new List<ExpRoomVisit>(lapCount);
        for (int i = 0; i < lapCount; i++)
            lap.Add(new ExpRoomVisit(keys[i], ResolveTargets(keys[i]), SummonForRoom(keys[i])));
        return new ExpRoute(lap);
    }

    // The loop as the simulator walks it: every room in step order with its placed
    // fixture, its lair (spawn count, respawn, the monsters it refills from), its
    // bosses, its summon table and the delay of a waypoint command run there. Unlike
    // Resolve, nothing is averaged and 0-exp monsters stay in (they still spawn and
    // cost fight time); bosses are split out, as their once-per-regen kill can't show
    // up in an hour's run.
    public IReadOnlyList<Simulation.SimRoom> ResolveSimLap(IReadOnlyList<LoopWaypoint> waypoints, IRoomFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(waypoints);
        (IReadOnlyList<LoopStep> steps, var unreachable) = LoopExpander.Expand(waypoints, _bfs, filter);
        if (steps.Count == 0 || unreachable.Count > 0) return Array.Empty<Simulation.SimRoom>();

        // Walks the steps itself rather than through LoopExpander.ResolveCycleRoomKeys:
        // it needs each command's delay against the room it runs in, and must refuse a
        // broken leg where the map overlay's walk just stops short.
        var lap = new List<Simulation.SimRoom>();
        RoomKey cursor = waypoints[0].Key;
        double pause = 0;
        foreach (LoopStep step in steps)
        {
            if (step is CommandLoopStep cmd) { pause += cmd.DelayMs / 1000.0; continue; }
            if (step is not MoveLoopStep move) continue;
            lap.Add(SimRoomAt(cursor, pause));
            pause = 0;
            if (_graph.GetRoom(cursor) is not { } room || !room.Exits.TryGetValue(move.Direction, out RoomExit exit))
                return Array.Empty<Simulation.SimRoom>();
            cursor = exit.Target;
        }
        // A command after the last move runs back in the start room (a closing leg of
        // zero length), before the next lap walks on from there.
        if (pause > 0 && lap.Count > 0) lap[0] = lap[0] with { PauseSeconds = lap[0].PauseSeconds + pause };
        return lap;
    }

    private Simulation.SimRoom SimRoomAt(RoomKey key, double pause)
    {
        Room? room = _graph.GetRoom(key);
        bool Known(int id) => Monsters().ContainsKey(id);
        int npc = room is { Npc: > 0 } && Known(room.Npc) && !Monster(room.Npc).IsBoss ? room.Npc : 0;
        var bosses = new List<int>();
        if (room is { Npc: > 0 } && Known(room.Npc) && Monster(room.Npc).IsBoss) bosses.Add(room.Npc);
        int lairMax = 0, respawn = 0;
        IReadOnlyList<int> lairIds = Array.Empty<int>();
        if (room is { HasLair: true } && ParseLair(room.RawLairTag) is (int mobs, List<int> ids))
        {
            lairIds = ids.Where(id => Known(id) && !Monster(id).IsBoss).ToList();
            bosses.AddRange(ids.Where(id => Known(id) && Monster(id).IsBoss));
            lairMax = mobs;
            respawn = _timers.DefaultRespawnSeconds(key) ?? 0;
        }
        RoomSummonTable? summon = room is { Spell: > 0 } ? SummonTableForSpell(room.Spell) : null;
        return new Simulation.SimRoom(key, npc, lairMax, lairIds, respawn, bosses, summon, pause);
    }

    // Hop count from a room to every room reachable from it (one search); viaBoats
    // also crosses the sailings and gateway portals the filter allows.
    public IReadOnlyDictionary<RoomKey, int> DistancesFrom(RoomKey from, IRoomFilter? filter = null, bool viaBoats = false) =>
        _bfs.ComputeDistancesFrom(from, filter, viaBoats);

    // Walking hop counts from a room, searching only until every target is reached.
    public IReadOnlyDictionary<RoomKey, int> DistancesTo(RoomKey from, IReadOnlyCollection<RoomKey> targets, IRoomFilter? filter = null) =>
        _bfs.ComputeDistancesTo(from, targets, filter);

    // Every lair room in the active set with its non-boss lair monsters — the input
    // AreaTours groups into hunting areas.
    public IEnumerable<(RoomKey Room, IReadOnlyList<int> LairMonsters)> LairRooms()
    {
        foreach (Room room in _graph.Rooms)
        {
            if (!room.HasLair || ParseLair(room.RawLairTag) is not (int _, List<int> ids)) continue;
            List<int> regular = ids.Where(id => Monsters().ContainsKey(id) && !Monster(id).IsBoss).ToList();
            if (regular.Count > 0) yield return (room.Key, regular);
        }
    }

    // The monsters a monster's death spell summons (its Abil-12 slots), or null.
    public IReadOnlyList<int>? DeathSummonsOf(int monsterNumber) => SummonsOf(monsterNumber);

    // The monster-summoning entry spell in a room, or null. A room's Spell that carries
    // a TextBlock ability whose roll table summons monsters yields expected exp
    // per visit — folded into the estimate on top of any placed lair. Memoised per
    // spell (null cached too) so a repeated room-spell resolves once per set.
    private RoomSummon? SummonForRoom(RoomKey key)
    {
        if (_graph.GetRoom(key) is not { Spell: > 0 } room) return null;
        var cache = _roomSummons ??= new Dictionary<int, RoomSummon?>();
        if (cache.TryGetValue(room.Spell, out RoomSummon? cached)) return cached;

        RoomSummon? result = null;
        if (SummonTableForSpell(room.Spell) is { } table
            && SpellTextBlocks().TryGetValue(room.Spell, out (int TextBlock, string Name) tb))
            result = new RoomSummon(
                tb.Name, table.ExpPerRoll, table.SummonChance, table.NoMonstersGate, table.EmptyRoomExpPerRoll);
        cache[room.Spell] = result;
        return result;
    }

    // The d100 summon table behind a room-entry spell, or null when the spell
    // summons nothing worth exp.
    private RoomSummonTable? SummonTableForSpell(int spell) =>
        SummonTables().TryGetValue(spell, out RoomSummonTable? table) ? table : null;

    // Every placed room spell's summon table. Read together the first time one is
    // asked for after a set change, not spell by spell as rooms come up, so the log
    // can say once which rooms the estimate credits a summon to.
    private Dictionary<int, RoomSummonTable?> SummonTables()
    {
        if (_summonTables is not null) return _summonTables;
        var roomsBySpell = new SortedDictionary<int, int>();
        foreach (Room room in _graph.Rooms)
            if (room.Spell > 0) roomsBySpell[room.Spell] = roomsBySpell.GetValueOrDefault(room.Spell) + 1;

        var tables = new Dictionary<int, RoomSummonTable?>();
        var summoning = new List<string>();
        var leftOut = new List<string>();
        int rooms = 0;
        foreach ((int spell, int count) in roomsBySpell)
        {
            if (!SpellTextBlocks().TryGetValue(spell, out (int TextBlock, string Name) tb))
            {
                tables[spell] = null;
                continue;
            }
            var skipped = new List<RoomSummonLeftOut>();
            RoomSummonTable? table = RoomSummonParser.Resolve(
                tb.TextBlock,
                n => TbActions().TryGetValue(n, out string? a) ? a : null,
                id => (Math.Max(0, Monster(id).Exp), Monster(id).IsBoss && Monster(id).HasRegenWait),
                skipped);
            if (table is not { ExpPerRoll: > 0 }) table = null;
            tables[spell] = table;
            foreach (RoomSummonLeftOut line in skipped)
                leftOut.Add($"{spell} {tb.Name} line {line.Threshold}: "
                    + $"{string.Join(", ", line.Monsters.Select(id => $"{id} {Monster(id).Name}"))} ({line.Reason})");
            if (table is null) continue;
            rooms += count;
            string gate = table.NoMonstersGate ? ", empty room only"
                : table.EmptyRoomExpPerRoll > 0
                    ? string.Create(CultureInfo.InvariantCulture, $", {table.EmptyRoomExpPerRoll:0} of it in an empty room only")
                    : string.Empty;
            summoning.Add(string.Create(CultureInfo.InvariantCulture,
                $"{spell} {tb.Name} (textblock {tb.TextBlock}, {count} room(s), {table.ExpPerRoll:0} exp a roll, {table.SummonChance * 100:0.#}% summon{gate})"));
        }

        if (roomsBySpell.Count > 0)
        {
            _log?.Info("RouteExpResolver",
                $"Room-spell summons in '{_cache.ActiveSet}': {summoning.Count} of {roomsBySpell.Count} placed room spell(s) summon monsters, "
                + $"over {rooms} room(s){(summoning.Count > 0 ? ": " + string.Join("; ", summoning) : string.Empty)}.");
            // Said here because a room that shows no summon in the estimate is
            // otherwise indistinguishable from one whose table was never read.
            if (leftOut.Count > 0)
                _log?.Info("RouteExpResolver", $"Summon line(s) not counted: {string.Join("; ", leftOut)}.");
        }
        return _summonTables = tables;
    }

    // Spell Number -> (the TBInfo number its TextBlock ability names, spell Name), for
    // the spells that have one. Only these can be summon room-spells.
    private Dictionary<int, (int TextBlock, string Name)> SpellTextBlocks()
    {
        if (_spellTextBlocks is not null) return _spellTextBlocks;
        var map = new Dictionary<int, (int, string)>();
        if (_cache.GetTable("Spells") is { } doc)
        {
            foreach (GameRow row in doc.Rows)
            {
                SpellFormulaInput spell = SpellFormulaReader.Read(row);
                int tb = SpellTextBlock.First(spell);
                if (spell.Number <= 0 || tb <= 0) continue;
                string name = row.TryGetProperty("Name", out GameValue nm) && nm.ValueKind == JsonValueKind.String
                    ? nm.GetString()?.Trim() ?? string.Empty : string.Empty;
                map[spell.Number] = (tb, name);
            }
        }
        return _spellTextBlocks = map;
    }

    // TBInfo Number -> its raw Action string (the roll-table / redirect chain
    // RoomSummonParser walks). Built once, dropped on set change.
    private Dictionary<int, string> TbActions()
    {
        if (_tbActions is not null) return _tbActions;
        var map = new Dictionary<int, string>();
        if (_cache.GetTable("TBInfo") is { } doc)
        {
            foreach (GameRow row in doc.Rows)
            {
                if (!row.TryGetProperty("Number", out GameValue n) || !n.TryGetInt32(out int id)) continue;
                if (row.TryGetProperty("Action", out GameValue a) && a.ValueKind == JsonValueKind.String)
                    map[id] = a.GetString() ?? string.Empty;
            }
        }
        return _tbActions = map;
    }

    private IReadOnlyList<ExpTarget> ResolveTargets(RoomKey key)
    {
        if (_graph.GetRoom(key) is not { } room) return Array.Empty<ExpTarget>();
        List<ExpTarget>? targets = null;

        if (room.Npc > 0)
        {
            MonsterInfo npc = Monster(room.Npc);
            if (npc.Exp > 0)
            {
                if (npc.IsBoss)
                    // A PLACED boss (GameLimit 1 / regen ≥ 1h) is killable only once
                    // per its regen even as a fixture — amortise it like a lair boss,
                    // not an every-pass instant fixture (e.g. animated juggernaut
                    // 17/7055: 1.3M, 3h regen → +433k/hr, once).
                    (targets ??= new List<ExpTarget>()).Add(new ExpTarget(
                        1, npc.Exp, npc.RegenHours * 3600, MonsterId: room.Npc, IsBoss: true, MonsterName: npc.Name));
                else
                {
                    // Normal NPC fixture: respawns instantly on entry (RespawnSeconds
                    // 0), regardless of RegenTime — that governs LAIR respawn, not
                    // fixtures. See GAME_MECHANICS.md. A placed summoner folds in its
                    // death-summon tree (exp + extra kill/wave cost) just like a lair mob.
                    CascadeResult c = RoomCascadeOf(room.Npc, 1);
                    (targets ??= new List<ExpTarget>()).Add(new ExpTarget(
                        1, c.Exp, 0, ClearWaves: c.Waves, KillsPerMob: c.Kills));
                }
            }
        }
        if (room.HasLair && ParseLair(room.RawLairTag) is (int mobs, List<int> ids))
        {
            int respawn = _timers.DefaultRespawnSeconds(key) ?? 0;
            // Split the lair: bosses (GameLimit 1 / long regen) come out of the
            // average and become their own once-per-regen target; the rest average
            // as normal lair mobs on the room's respawn.
            double sumRoomExp = 0;
            double sumRoomKills = 0;
            int maxWaves = 1;
            int n = 0;
            foreach (int id in ids)
            {
                MonsterInfo m = Monster(id);
                if (m.Exp <= 0) continue;
                if (m.IsBoss)
                {
                    (targets ??= new List<ExpTarget>()).Add(new ExpTarget(
                        1, m.Exp, m.RegenHours * 3600, MonsterId: id, IsBoss: true, MonsterName: m.Name));
                    continue;
                }
                // Each non-boss listed type contributes its whole death-summon cascade,
                // simulated at the room's spawn count under the 20-monster cap: room exp
                // into the average, room kills into the single-target count, and the
                // deepest type's tier count drives the AoE wave count.
                CascadeResult c = RoomCascadeOf(id, mobs);
                sumRoomExp += c.Exp;
                sumRoomKills += c.Kills;
                maxWaves = Math.Max(maxWaves, c.Waves);
                n++;
            }
            if (n > 0)
            {
                // Back to per-mob so MobCount × ExpPerMob reproduces the (capped) room
                // total the simulator scales; likewise KillsPerMob × MobCount = room kills.
                double roomExp = sumRoomExp / n;
                double roomKills = sumRoomKills / n;
                (targets ??= new List<ExpTarget>()).Add(new ExpTarget(
                    mobs, roomExp / mobs, respawn, ClearWaves: maxWaves, KillsPerMob: roomKills / mobs));
            }
        }
        return targets ?? (IReadOnlyList<ExpTarget>)Array.Empty<ExpTarget>();
    }

    // Parse the raw MDB lair cell "(Max N): id,id,...,[group-maxregen]" → the
    // per-room max simultaneous spawn count and the listed monster ids, the on-disk
    // shape for these sets. Boss-vs-regular split + averaging happens in
    // ResolveTargets.
    private static (int Mobs, List<int> Ids)? ParseLair(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        Match maxM = Regex.Match(raw, @"Max\s+(\d+)");
        int mobs = maxM.Success && int.TryParse(maxM.Groups[1].Value, out int mv) && mv > 0 ? mv : 1;

        int colon = raw.IndexOf(':');
        if (colon < 0) return null;
        int bracket = raw.IndexOf('[', colon);
        int end = bracket > colon ? bracket : raw.Length;

        var ids = new List<int>();
        foreach (string tok in raw[(colon + 1)..end].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(tok, out int id) && id > 0) ids.Add(id);
        return ids.Count > 0 ? (mobs, ids) : null;
    }

    private MonsterInfo Monster(int id) => Monsters().TryGetValue(id, out MonsterInfo m) ? m : default;

    private Dictionary<int, MonsterInfo> Monsters()
    {
        if (_monsters is not null) return _monsters;
        var map = new Dictionary<int, MonsterInfo>();
        if (_cache.GetTable("Monsters") is { } doc)
        {
            foreach (GameRow row in doc.Rows)
            {
                if (!row.TryGetProperty("Number", out GameValue n) || !n.TryGetInt32(out int id)) continue;
                int baseExp = row.TryGetProperty("EXP", out GameValue e) && e.TryGetInt32(out int ev) ? ev : 0;
                // True exp is EXP × ExpMulti (the boss/exp multiplier). The Game-Data
                // browser already displays it this way; the estimator read only EXP
                // before, so a ×20 boss showed 1/20th its real value.
                int multi = row.TryGetProperty("ExpMulti", out GameValue em) && em.TryGetInt32(out int mvv) && mvv > 0 ? mvv : 1;
                int regen = row.TryGetProperty("RegenTime", out GameValue rt) && rt.TryGetInt32(out int rv) ? rv : 0;
                int limit = row.TryGetProperty("GameLimit", out GameValue gl) && gl.TryGetInt32(out int lv) ? lv : 0;
                string name = row.TryGetProperty("Name", out GameValue nm) && nm.ValueKind == JsonValueKind.String
                    ? nm.GetString() ?? string.Empty : string.Empty;
                int deathSpell = row.TryGetProperty("DeathSpell", out GameValue ds) && ds.TryGetInt32(out int dv) ? dv : 0;
                bool isBoss = Game.GameData.BossCatalog.IsBoss(limit);
                map[id] = new MonsterInfo(baseExp * multi, isBoss, Math.Max(1, regen), name, deathSpell, regen > 0);
            }
        }
        return _monsters = map;
    }

    // Spell Number -> the monster ids it summons (its Abil==12 "summon monster"
    // slots). Only slots whose Abil is 12 count — a spell row also carries unrelated
    // AbilVal payloads in its other slots. A spell targeting no one or a user (Targets
    // 0 / 8, e.g. "calls for aid") never fires as a death spell, which is cast with no
    // user (GAME_MECHANICS "Death-summon cascades"). Built once, dropped on set change.
    private Dictionary<int, List<int>> SummonSpells()
    {
        if (_summonSpells is not null) return _summonSpells;
        var map = new Dictionary<int, List<int>>();
        if (_cache.GetTable("Spells") is { } doc)
        {
            foreach (GameRow row in doc.Rows)
            {
                if (!row.TryGetProperty("Number", out GameValue n) || !n.TryGetInt32(out int id)) continue;
                if (row.TryGetProperty("Targets", out GameValue t) && t.TryGetInt32(out int targets) && targets is 0 or 8) continue;
                List<int>? ids = null;
                // Slots are Abil-0..Abil-N / AbilVal-0..AbilVal-N; walk while the pair exists.
                for (int i = 0; row.TryGetProperty($"Abil-{i}", out GameValue ab); i++)
                {
                    if (ab.TryGetInt32(out int abv) && abv == SummonAbility
                        && row.TryGetProperty($"AbilVal-{i}", out GameValue av) && av.TryGetInt32(out int mon) && mon > 0)
                        (ids ??= new List<int>()).Add(mon);
                }
                if (ids is not null) map[id] = ids;
            }
        }
        return _summonSpells = map;
    }

    // Room-level death-summon cascade for a base spawn of `mobs` copies of `id`,
    // capped at 20 living monsters per tier. Memoised per (monster, spawn count) —
    // the sim is a pure function of the data, so the same key resolves the same.
    private CascadeResult RoomCascadeOf(int id, int mobs)
    {
        var cache = _roomCascades ??= new Dictionary<(int, int), CascadeResult>();
        var key = (id, mobs);
        if (cache.TryGetValue(key, out CascadeResult c)) return c;
        (int cap, bool whole) = DeathSummonCascade.RulesFor(_cache.ActiveRealm);
        c = DeathSummonCascade.Simulate(id, mobs, ExpOf, SummonsOf, cap, wholeCasts: whole);
        cache[key] = c;
        return c;
    }

    private int ExpOf(int id) => Math.Max(0, Monster(id).Exp);

    private IReadOnlyList<int>? SummonsOf(int id)
    {
        int spell = Monster(id).DeathSpell;
        if (spell <= 0) return null;
        return SummonSpells().TryGetValue(spell, out List<int>? s) ? s : null;
    }
}
