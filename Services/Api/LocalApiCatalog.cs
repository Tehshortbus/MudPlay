using MudPlay.Game.Combat;
using MudPlay.Game.Map;

namespace MudPlay.Services.Api;

// The reference side of the read API: the saved loop library, and what lives in
// a room. Runs ON THE UI THREAD like the rest — the server marshals first.
//
// Answers the question the live-state endpoints can't: "where should I be
// hunting?" State says what the client is doing; this says what's out there.
//
// Every room→monster answer comes from the same two sources the map's ROOM INFO
// panel uses, and NOT from re-reading the raw tables:
//   - the room's own Lair tag, parsed by RoomTooltipBuilder.ParseLairTag (lair spawners), and
//   - MonsterSpawnIndex (placed fixtures and assigned roamers, derived from each
//     monster's `Summoned By` field).
// Those two disagree in interesting ways and the distinction matters — a lair
// spawner respawns on a timer you can camp, an assigned roamer wanders in on its
// own schedule. Reimplementing either here would drift from what the map shows.
public static class LocalApiCatalog
{
    // Every saved loop, with enough per-loop shape to compare them without
    // fetching each one. Ordered by area then name so the listing is stable.
    public static object Loops(AppServices svc)
    {
        ArgumentNullException.ThrowIfNull(svc);
        return new
        {
            set = svc.Loops.SetName,
            count = svc.Loops.Loops.Count,
            loops = svc.Loops.Loops
                .OrderBy(l => l.Folder ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                .Select(l => Summarise(svc, l))
                .ToArray(),
        };
    }

    // One loop in full: every waypoint with its resolved room name, plus the
    // monsters at each stop.
    public static object? Loop(AppServices svc, string name)
    {
        ArgumentNullException.ThrowIfNull(svc);
        if (svc.Loops.Get(name) is not { } loop) return null;

        return new
        {
            name = loop.Name,
            folder = loop.Folder,
            notes = loop.Notes,
            // The next three are the loaded character's own for this loop, not the
            // loop file's.
            favorite = loop.Favorite,
            onlyAttackInLairRooms = loop.OnlyAttackInLairRooms,
            lairEntryDebuff = loop.LairEntryDebuff.ToString(),
            summary = Summarise(svc, loop),
            waypoints = loop.Waypoints.Select(w =>
            {
                RoomKey? key = TryKey(w.Room);
                return new
                {
                    room = w.Room,
                    name = key is { } k ? svc.RoomGraph.GetRoom(k)?.Name : null,
                    command = w.Command,
                    delayMs = w.DelayMs,
                    monsters = key is { } k2 ? RoomMonsters(svc, k2) : null,
                };
            }).ToArray(),
        };
    }

    // What's in a room, by the same grouping the map's ROOM INFO panel shows.
    public static object Room(AppServices svc, RoomKey key)
    {
        ArgumentNullException.ThrowIfNull(svc);
        Room? room = svc.RoomGraph.GetRoom(key);
        return new
        {
            key = key.ToString(),
            name = room?.Name,
            known = room is not null,
            exits = room?.Exits.Keys.Select(d => d.ToString()).ToArray(),
            lairTag = room?.RawLairTag,
            monsters = RoomMonsters(svc, key),
        };
    }

    public static object? Monster(AppServices svc, int id)
    {
        ArgumentNullException.ThrowIfNull(svc);
        return svc.MonsterCatalog.Get(id) is { } m ? Describe(m) : null;
    }

    // Per-loop rollup. Deliberately reports MEDIAN exp rather than mean: a loop
    // whose lairs are mostly modest with one boss reads as a boss loop under a
    // mean, and the median answers "what will I mostly be killing?".
    private static object Summarise(AppServices svc, Game.Map.Loop loop)
    {
        var rooms = loop.Waypoints
            .Select(w => TryKey(w.Room))
            .Where(k => k is not null)
            .Select(k => k!.Value)
            .Distinct()
            .ToArray();

        List<MonsterCatalogEntry> lairMobs = [];
        int lairRooms = 0;
        foreach (RoomKey key in rooms)
        {
            IReadOnlyList<int> ids = LairMonsterIds(svc, key);
            if (ids.Count == 0) continue;
            lairRooms++;
            foreach (int id in ids)
                if (svc.MonsterCatalog.Get(id) is { } m) lairMobs.Add(m);
        }

        var distinct = lairMobs.GroupBy(m => m.Number).Select(g => g.First()).ToArray();
        long[] exps = distinct.Select(m => m.EffectiveExp).Where(e => e > 0).Order().ToArray();

        return new
        {
            name = loop.Name,
            folder = loop.Folder,
            steps = loop.Waypoints.Count,
            rooms = rooms.Length,
            lairRooms,
            distinctLairMonsters = distinct.Length,
            medianExp = exps.Length == 0 ? 0 : exps[exps.Length / 2],
            maxExp = exps.Length == 0 ? 0 : exps[^1],
            // Worst-case incoming damage on the circuit — the number that decides
            // whether the loop is survivable, not the average.
            maxAvgDamage = distinct.Length == 0 ? 0 : distinct.Max(m => m.PrimaryPhysicalAvgDamage),
            maxHp = distinct.Length == 0 ? 0 : distinct.Max(m => m.Hp),
            toughest = distinct.OrderByDescending(m => m.EffectiveExp).Take(3)
                .Select(m => new { m.Number, m.Name, exp = m.EffectiveExp, hp = m.Hp, avgDamage = m.PrimaryPhysicalAvgDamage })
                .ToArray(),
        };
    }

    // Lair spawners at a room, from the room's own tag.
    //
    // RoomTooltipBuilder.ParseLairTag, deliberately — it's what the map tooltip
    // and ROOM INFO already use, so this endpoint and the UI can't disagree about
    // what's in a room. The cells read `(Max 1): 53,54,…,[10-10-15-1]`; a parser
    // that missed that shape once reported every loop as having zero lair monsters.
    private static IReadOnlyList<int> LairMonsterIds(AppServices svc, RoomKey key)
    {
        Room? room = svc.RoomGraph.GetRoom(key);
        if (room?.RawLairTag is not { Length: > 0 } tag) return [];
        RoomTooltipBuilder.ParseLairTag(tag, out _, out IReadOnlyList<int> ids);
        return ids;
    }

    private static int? LairMaxRegen(AppServices svc, RoomKey key)
    {
        if (svc.RoomGraph.GetRoom(key)?.RawLairTag is not { Length: > 0 } tag) return null;
        RoomTooltipBuilder.ParseLairTag(tag, out int? max, out _);
        return max;
    }

    // Grouped exactly as the map tooltip groups them, because the difference is
    // what a hunter needs: lair spawners are campable, roamers are not.
    private static object RoomMonsters(AppServices svc, RoomKey key)
    {
        IReadOnlyList<int> lair = LairMonsterIds(svc, key);
        IReadOnlyList<int> placed = svc.MonsterSpawns.PlacedMonsterIdsAt(key);
        IReadOnlyList<int> assigned = svc.MonsterSpawns.AssignedMonsterIdsAt(key);
        return new
        {
            lair = lair.Select(i => Brief(svc, i)).Where(x => x is not null).ToArray(),
            placed = placed.Select(i => Brief(svc, i)).Where(x => x is not null).ToArray(),
            assigned = assigned.Select(i => Brief(svc, i)).Where(x => x is not null).ToArray(),
            maxRegen = LairMaxRegen(svc, key),
        };
    }

    private static object? Brief(AppServices svc, int id)
        => svc.MonsterCatalog.Get(id) is not { } m ? null : new
        {
            m.Number,
            m.Name,
            exp = m.EffectiveExp,
            hp = m.Hp,
            avgDamage = m.PrimaryPhysicalAvgDamage,
            armourClass = m.ArmourClass,
            magicRes = m.MagicRes,
            undead = m.Undead,
        };

    private static object Describe(MonsterCatalogEntry m) => new
    {
        m.Number,
        m.Name,
        exp = m.EffectiveExp,
        rawExp = m.Exp,
        m.ExpMulti,
        hp = m.Hp,
        m.ArmourClass,
        m.MagicRes,
        m.DamageResist,
        m.Undead,
        m.Align,
        avgDamage = m.PrimaryPhysicalAvgDamage,
        attacks = m.Attacks.Select(a => new
        {
            a.Name, a.Accuracy, a.MinDamage, a.MaxDamage, a.Percent, a.TruePercent, a.Type,
        }).ToArray(),
        drops = m.Drops.Select(d => new { d.ItemId, d.Percent }).ToArray(),
    };

    // "12/431" → RoomKey. Null for a malformed waypoint rather than throwing: a
    // hand-edited loop file shouldn't take the endpoint down.
    private static RoomKey? TryKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string[] parts = raw.Split('/', 2);
        return parts.Length == 2
            && int.TryParse(parts[0], out int map)
            && int.TryParse(parts[1], out int room)
            ? new RoomKey(map, room)
            : null;
    }
}
