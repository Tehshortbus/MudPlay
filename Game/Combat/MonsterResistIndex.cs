using System.Text.Json;
using FujinTerm.Services;

namespace FujinTerm.Game.Combat;

// Fast lookup of a monster's elemental damage-type resistance by monster Number
// in the active game-data set.
//
// Only the five *elemental* resist abilities are indexed — Resist-Cold (3),
// Resist-Fire (5), Resist-Stone (65), Resist-Lightning (66), Resist-Water (147)
// — because they alone gate deterministically: an elemental attack spell's
// damage is cut by dmg*(resist/100), so a resist ≥ 100 means 0 damage (exactly
// 100) or the spell *heals* the monster (> 100). That determinism is what lets
// the combat engine pre-emptively skip an attack spell whose element the target
// resists ≥ 100% — see GAME_MECHANICS.md mechanism 3a.
//
// The value is signed: negative = vulnerability (the element deals *extra*
// damage), so only the ≥ 100 end is a skip signal — a negative or 1–99% resist
// must still fire the spell.
//
// The other two damage flavors are deliberately NOT here: Magic Resist (M.R.,
// code 36, on AttType 4 "Normal" spells) is a capped, probabilistic reduction —
// never a deterministic 0 — and poison (AttType 6) isn't resistible at all
// (binary race/item immunity). Neither is pre-emptable, so neither belongs in a
// skip index.
//
// Mirrors MonsterMagicIndex: built lazily by scanning the raw Monsters table's
// paired Abil-0..9 / AbilVal-0..9 columns, cached, and dropped on game-data set
// switch. A monster with no elemental resist ability → no entry → 0 for every
// code.
public sealed class MonsterResistIndex
{
    // MajorMUD ability codes for the five elemental resists (per
    // GameData.AbilityNames), paired below with the spell AttType they gate.
    private const int ResistCold = 3;
    private const int ResistFire = 5;
    private const int ResistStone = 65;
    private const int ResistLightning = 66;
    private const int ResistWater = 147;

    // Number of Abil-N slots on a Monsters row.
    private const int AbilitySlots = 10;

    private readonly GameDataCache _cache;
    private Dictionary<int, Dictionary<int, int>>? _byNumber;

    public MonsterResistIndex(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ => _byNumber = null;
    }

    // The elemental resist ability code that gates spell attack type attType, or
    // -1 when attType is not an elemental type. AttType 4 "Normal" (Magic Resist)
    // and 6 "Poison" are not deterministically pre-emptable, so they map to
    // nothing. AttType values are from LookupEnums.SpellAttackTypeNames:
    // 0 Cold, 1 Fire, 2 Stone, 3 Lightning, 4 Normal, 5 Water, 6 Poison.
    public static int ElementalResistCode(int attackType) => attackType switch
    {
        0 => ResistCold,
        1 => ResistFire,
        2 => ResistStone,
        3 => ResistLightning,
        5 => ResistWater,
        _ => -1,
    };

    // The monster's resist percentage for resistCode (0 when it carries no such
    // resist ability). Positive = damage reduction (100 = 0 damage, > 100 = heal);
    // negative = vulnerability (extra damage).
    public int ResistPercent(int monsterNumber, int resistCode)
    {
        if (!Build().TryGetValue(monsterNumber, out Dictionary<int, int>? resists)) return 0;
        return resists.TryGetValue(resistCode, out int pct) ? pct : 0;
    }

    private Dictionary<int, Dictionary<int, int>> Build()
    {
        if (_byNumber is { } cached) return cached;

        Dictionary<int, Dictionary<int, int>> map = new();
        JsonDocument? doc = _cache.GetRawTable("Monsters");
        if (doc is not null)
        {
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("Number", out JsonElement numEl)) continue;
                if (numEl.ValueKind != JsonValueKind.Number) continue;
                if (!numEl.TryGetInt32(out int number)) continue;

                Dictionary<int, int>? resists = null;
                for (int i = 0; i < AbilitySlots; i++)
                {
                    if (!row.TryGetProperty($"Abil-{i}", out JsonElement abilEl)) continue;
                    if (abilEl.ValueKind != JsonValueKind.Number) continue;
                    if (!abilEl.TryGetInt32(out int code)) continue;
                    if (!IsElementalResistCode(code)) continue;
                    if (!row.TryGetProperty($"AbilVal-{i}", out JsonElement valEl)) continue;
                    if (valEl.ValueKind != JsonValueKind.Number) continue;
                    if (!valEl.TryGetInt32(out int val)) continue;
                    if (val == 0) continue;   // 0% resist is no resist — skip the noise
                    (resists ??= new Dictionary<int, int>())[code] = val;
                }

                if (resists is not null) map[number] = resists;
            }
        }

        // Folded into the map — release the pinned raw Monsters JsonDocument.
        // (MonsterMagicIndex evicts the same table; whichever builds second just
        // triggers a one-time reload via GetRawTable.)
        _cache.EvictTable("Monsters");
        _byNumber = map;
        return map;
    }

    private static bool IsElementalResistCode(int code) =>
        code is ResistCold or ResistFire or ResistStone or ResistLightning or ResistWater;
}
