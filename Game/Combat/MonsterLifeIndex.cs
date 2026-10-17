using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Game.Combat;

// Fast lookup of whether a monster can be affected by a life-DRAIN spell, by
// monster Number in the active game-data set. A drain (life-steal) spell can only
// hit a target that is LIVING and NOT undead — draining a corpse-thing / construct
// has no life to steal, so the game returns "Your spell has no effect on X.".
//
// Two independent monster attributes decide it (see GAME_MECHANICS "Spell
// targeting: monster type tags"):
//   - NonLiving — MajorMUD ability code 109 (presence = nonliving; absence = living).
//   - Undead — a dedicated top-level `Undead` byte-boolean column, NOT an ability
//     slot. It holds 0 (not undead), 1, AND 255 (the MDB's Boolean True stored as
//     -1), so the test is `!= 0`, never `== 1` (== 1 silently drops the 255 rows —
//     banshee, zombie cat, skeletal steed…).
//
// Mirrors MonsterMagicIndex: the map is built lazily by scanning the raw Monsters
// table, cached, and dropped on game-data set switch so the next query rebuilds
// against the new set. Only monsters that are NonLiving or Undead are stored — the
// common living case is the map's absence, so an unknown / unresolved number reads
// as drain-eligible (fail-open; a genuine data gap is then caught reactively by the
// "no effect" line).
public sealed class MonsterLifeIndex
{
    // MajorMUD ability codes (per GameData.AbilityNames).
    private const int NonLivingAbilityCode = 109;
    private const int AnimalAbilityCode = 78;

    // Number of Abil-N slots on a Monsters row.
    private const int AbilitySlots = 10;

    private readonly GameDataCache _cache;
    private Dictionary<int, MonsterLife>? _byNumber;

    public MonsterLifeIndex(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ => _byNumber = null;
    }

    // Whether a drain spell can affect this monster: true unless it's NonLiving or
    // Undead. An unknown number (-1 / no game-data row) reads eligible (fail-open) —
    // a genuine miss is caught by the reactive "no effect" line downstream.
    public bool CanDrain(int monsterNumber)
    {
        if (monsterNumber < 0) return true;
        return !Build().TryGetValue(monsterNumber, out MonsterLife life)
            || (!life.NonLiving && !life.Undead);
    }

    // Whether a spell with the given target-class restriction can affect this monster.
    // Any ⇒ always. The other three are provable from data: a LivingOnly spell can't
    // touch a NonLiving or Undead target; an UndeadOnly spell only an Undead; an
    // AnimalsOnly spell only an Animal. A monster absent from the map carries none of the
    // three flags, so it's normal LIVING (not undead, not an animal, not a construct) —
    // which is exactly what the "only" restrictions need to decide. An unknown number
    // (-1) fails OPEN (don't pre-empt; the reactive "no effect" line is the backstop).
    public bool CanAffect(int monsterNumber, SpellTargetType targetType)
    {
        if (targetType == SpellTargetType.Any) return true;
        if (monsterNumber < 0) return true;
        bool inMap = Build().TryGetValue(monsterNumber, out MonsterLife life);
        return targetType switch
        {
            SpellTargetType.LivingOnly  => !inMap || (!life.NonLiving && !life.Undead),
            SpellTargetType.UndeadOnly  => inMap && life.Undead,
            SpellTargetType.AnimalsOnly => inMap && life.Animal,
            _ => true,
        };
    }

    private Dictionary<int, MonsterLife> Build()
    {
        if (_byNumber is { } cached) return cached;

        Dictionary<int, MonsterLife> map = new();
        GameTable? doc = _cache.GetTable("Monsters");
        if (doc is not null)
        {
            foreach (GameRow row in doc.Rows)
            {
                if (!row.TryGetProperty("Number", out GameValue numEl)) continue;
                if (numEl.ValueKind != JsonValueKind.Number) continue;
                if (!numEl.TryGetInt32(out int number)) continue;

                bool nonLiving = false, animal = false;
                for (int i = 0; i < AbilitySlots; i++)
                {
                    if (!row.TryGetProperty($"Abil-{i}", out GameValue abilEl)) continue;
                    if (abilEl.ValueKind != JsonValueKind.Number) continue;
                    if (!abilEl.TryGetInt32(out int code)) continue;
                    if (code == NonLivingAbilityCode) nonLiving = true;
                    else if (code == AnimalAbilityCode) animal = true;
                }

                bool undead = row.TryGetProperty("Undead", out GameValue undeadEl)
                    && undeadEl.ValueKind == JsonValueKind.Number
                    && undeadEl.TryGetInt32(out int u)
                    && u != 0;

                if (nonLiving || undead || animal)
                    map[number] = new MonsterLife(nonLiving, undead, animal);
            }
        }

        // Folded into the map — the cache may drop the Monsters table.
        _cache.EvictTable("Monsters");
        _byNumber = map;
        return map;
    }

    private readonly record struct MonsterLife(bool NonLiving, bool Undead, bool Animal);
}
