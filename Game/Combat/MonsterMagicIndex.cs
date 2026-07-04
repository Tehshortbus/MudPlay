using System.Text.Json;
using FujinTerm.Services;

namespace FujinTerm.Game.Combat;

// Fast lookup of a monster's magic-hit requirement (Magical, MajorMUD ability
// code 28) and spell immunity (SpellImmu, code 139) by monster Number in the
// active game-data set.
//
// Both values gate combat eligibility deterministically from game data:
// - Magical N — a physical weapon hits this monster only when the weapon's
//   HitMagic (ItemMagicIndex) is ≥ N. A monster with no Magical ability is level
//   0, so any weapon hits.
// - SpellImmu N — a spell affects this monster only when its ReqLevel
//   (SpellReqLevelIndex) is ≥ N. Applies to both attack spells and debuffs. No
//   SpellImmu = 0, so any spell is allowed.
//
// Mirrors SeeHiddenIndex: the map is built lazily by scanning the raw Monsters
// table's paired Abil-0..9 / AbilVal-0..9 columns, cached, and dropped on
// game-data set switch so the next query rebuilds against the new set. Unlike
// SeeHidden we read the AbilVal too, because the level value (not mere presence)
// is what gates the comparison.
public sealed class MonsterMagicIndex
{
    // MajorMUD ability code for the magic-hit requirement (per
    // GameData.AbilityNames).
    private const int MagicalAbilityCode = 28;

    // MajorMUD ability code for spell immunity.
    private const int SpellImmuneAbilityCode = 139;

    // Number of Abil-N slots on a Monsters row.
    private const int AbilitySlots = 10;

    private readonly GameDataCache _cache;
    private Dictionary<int, MonsterMagic>? _byNumber;

    public MonsterMagicIndex(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ => _byNumber = null;
    }

    // The monster's magic-hit requirement (0 when it carries no Magical ability —
    // any weapon hits).
    public int MagicalLevel(int monsterNumber) =>
        Build().TryGetValue(monsterNumber, out MonsterMagic m) ? m.Magical : 0;

    // The monster's spell immunity (0 when it carries no SpellImmu ability — any
    // spell is allowed).
    public int SpellImmunity(int monsterNumber) =>
        Build().TryGetValue(monsterNumber, out MonsterMagic m) ? m.SpellImmu : 0;

    private Dictionary<int, MonsterMagic> Build()
    {
        if (_byNumber is { } cached) return cached;

        Dictionary<int, MonsterMagic> map = new();
        JsonDocument? doc = _cache.GetRawTable("Monsters");
        if (doc is not null)
        {
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("Number", out JsonElement numEl)) continue;
                if (numEl.ValueKind != JsonValueKind.Number) continue;
                if (!numEl.TryGetInt32(out int number)) continue;

                int magical = 0;
                int spellImmu = 0;
                for (int i = 0; i < AbilitySlots; i++)
                {
                    if (!row.TryGetProperty($"Abil-{i}", out JsonElement abilEl)) continue;
                    if (abilEl.ValueKind != JsonValueKind.Number) continue;
                    if (!abilEl.TryGetInt32(out int code)) continue;
                    if (code != MagicalAbilityCode && code != SpellImmuneAbilityCode) continue;
                    if (!row.TryGetProperty($"AbilVal-{i}", out JsonElement valEl)) continue;
                    if (valEl.ValueKind != JsonValueKind.Number) continue;
                    if (!valEl.TryGetInt32(out int val)) continue;
                    if (code == MagicalAbilityCode) magical = val;
                    else spellImmu = val;
                }

                if (magical != 0 || spellImmu != 0)
                    map[number] = new MonsterMagic(magical, spellImmu);
            }
        }

        // Folded into the map — release the pinned raw Monsters JsonDocument.
        _cache.EvictTable("Monsters");
        _byNumber = map;
        return map;
    }

    // A monster's paired magic-hit requirement + spell immunity.
    private readonly record struct MonsterMagic(int Magical, int SpellImmu);
}
