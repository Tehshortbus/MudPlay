using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Services;

namespace MudPlay.Game.Combat;

// Given a room, lists the spells castable by the monsters that appear there
// (placed / assigned / lair) — so an unrecognized wire line captured in that
// room can be narrowed to a likely source: "this line probably belongs to one
// of these spells, whose message we're missing." Pure read of the active set;
// reuses RoomTooltipBuilder's room→monster resolution and MonsterCatalog's
// parsed spell slots rather than re-walking the raw tables.
public static class RoomSpellAttributor
{
    // Distinct (spell number, name) pairs castable by any monster in the room,
    // ordered by number. Empty when the room hosts no spell-casters (or the data
    // isn't loaded).
    public static IReadOnlyList<(int Number, string Name)> SpellsCastableIn(
        RoomKey key,
        RoomGraphManager? rooms,
        GameDataCache? data,
        MonsterSpawnIndex? spawns,
        MonsterCatalog? monsters,
        KnownSpellCatalog? spells)
    {
        if (rooms is null || monsters is null || spells is null) return System.Array.Empty<(int, string)>();
        Room? room = rooms.GetRoom(key);
        if (room is null) return System.Array.Empty<(int, string)>();

        RoomTooltipBuilder.RoomMonsters rm = RoomTooltipBuilder.ResolveRoomMonsters(room, data, spawns);

        var seen = new HashSet<int>();
        var result = new List<(int Number, string Name)>();
        foreach (RoomTooltipBuilder.RoomMonsterRef mref in rm.Placed.Concat(rm.Assigned).Concat(rm.Lair))
        {
            MonsterCatalogEntry? mc = monsters.Get(mref.Id);
            if (mc is null) continue;
            foreach (int spellNum in CastSpellNumbers(mc))
            {
                if (spellNum <= 0 || !seen.Add(spellNum)) continue;
                string name = spells.GetSpellNameByNumber(spellNum) ?? $"Spell #{spellNum}";
                result.Add((spellNum, name));
            }
        }
        result.Sort((a, b) => a.Number.CompareTo(b.Number));
        return result;
    }

    // Every spell number a monster casts: combat-cast slots (AttType 2 → Accuracy
    // holds the spell number), on-hit procs (HitSpell), between-rounds MidSpells,
    // and the on-spawn / on-death spells.
    private static IEnumerable<int> CastSpellNumbers(MonsterCatalogEntry mc)
    {
        foreach (MonsterAttackSlot a in mc.Attacks)
        {
            if (a.Type == 2 && a.Accuracy > 0) yield return a.Accuracy;
            if (a.HitSpell > 0) yield return a.HitSpell;
        }
        foreach (MonsterMidSpellSlot m in mc.MidSpells)
            if (m.SpellId > 0) yield return m.SpellId;
        if (mc.CreateSpell > 0) yield return mc.CreateSpell;
        if (mc.DeathSpell > 0) yield return mc.DeathSpell;
    }

    // Compact "Likely source" label — up to maxSpells "name (#N)" entries, with a
    // "+K more" tail. Empty string when nothing casts in the room.
    public static string LikelySource(
        RoomKey key,
        RoomGraphManager? rooms,
        GameDataCache? data,
        MonsterSpawnIndex? spawns,
        MonsterCatalog? monsters,
        KnownSpellCatalog? spells,
        int maxSpells = 6)
    {
        IReadOnlyList<(int Number, string Name)> list =
            SpellsCastableIn(key, rooms, data, spawns, monsters, spells);
        if (list.Count == 0) return string.Empty;

        IEnumerable<string> shown = list.Take(maxSpells).Select(s => $"{s.Name} (#{s.Number})");
        string text = string.Join(", ", shown);
        if (list.Count > maxSpells) text += $", +{list.Count - maxSpells} more";
        return text;
    }
}
