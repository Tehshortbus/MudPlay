using System.Text.Json;
using FujinTerm.Services;

namespace FujinTerm.Game.Combat;

// Fast lookup of a weapon's magic-hit level by item Name in the active
// game-data set.
//
// A physical weapon hits a monster only when its magic-hit level is ≥ the
// monster's Magical level (MonsterMagicIndex). A weapon's magic-hit level is the
// SUM of TWO abilities — Magical (code 28) and HitMagic (code 142) — matching the
// character sheet's "Hit Magic" total (CharacterCalculator buckets 28+142 the
// same way). An inherently magical weapon (a "shimmering" longsword, etc.) often
// carries only the Magical ability, so reading code 142 alone misreads it as
// level 0 and strands the walker "un-actionable" against a monster it could hit.
// A weapon with neither ability is level 0 and so can only hit non-magical
// monsters (Magical 0).
//
// Mirrors SeeHiddenIndex / MonsterMagicIndex: the map is built lazily by
// scanning the raw Items table's paired Abil-0..19 / AbilVal-0..19 columns,
// cached, and dropped on game-data set switch so the next query rebuilds against
// the new set. Items carry 20 ability slots (vs 10 on Monsters). The key is the
// item Name because CombatSettings.NormalWeapon and its siblings store the
// weapon by name. Unknown name → -1 (fail-open: the caller treats "no data" as
// "don't second-guess the configured weapon").
public sealed class ItemMagicIndex
{
    // MajorMUD ability codes that grant a weapon its magic-hit level (per
    // GameData.AbilityNames): Magical (28) is the weapon's inherent magic, HitMagic
    // (142) an explicit +hit-magic bonus. Both count — summed like the char sheet.
    private const int MagicalAbilityCode = 28;
    private const int HitMagicAbilityCode = 142;

    // Number of Abil-N slots on an Items row.
    private const int AbilitySlots = 20;

    private readonly GameDataCache _cache;
    private Dictionary<string, int>? _byName;

    public ItemMagicIndex(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ => _byName = null;
    }

    // The weapon's magic-hit level (Magical + HitMagic summed), or -1 when the
    // item is unknown (no row with that name) — fail-open. A known weapon with
    // neither ability returns 0.
    public int HitMagic(string? itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return -1;
        return Build().TryGetValue(itemName.Trim(), out int level) ? level : -1;
    }

    private Dictionary<string, int> Build()
    {
        if (_byName is { } cached) return cached;

        Dictionary<string, int> map = new(StringComparer.OrdinalIgnoreCase);
        JsonDocument? doc = _cache.GetRawTable("Items");
        if (doc is not null)
        {
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("Name", out JsonElement nameEl)) continue;
                if (nameEl.ValueKind != JsonValueKind.String) continue;
                string? name = nameEl.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;

                int hitMagic = 0;
                for (int i = 0; i < AbilitySlots; i++)
                {
                    if (!row.TryGetProperty($"Abil-{i}", out JsonElement abilEl)) continue;
                    if (abilEl.ValueKind != JsonValueKind.Number) continue;
                    if (!abilEl.TryGetInt32(out int code)) continue;
                    if (code != MagicalAbilityCode && code != HitMagicAbilityCode) continue;
                    if (!row.TryGetProperty($"AbilVal-{i}", out JsonElement valEl)) continue;
                    if (valEl.ValueKind != JsonValueKind.Number) continue;
                    // Sum both abilities (a weapon can carry either or both) — no
                    // early break, matching the char sheet's Hit Magic total.
                    if (valEl.TryGetInt32(out int val)) hitMagic += val;
                }

                // Last writer wins on duplicate names — game data rarely
                // duplicates weapon names, and the chooser only needs a level.
                map[name.Trim()] = hitMagic;
            }
        }

        // Folded into the map — release the pinned raw Items JsonDocument.
        _cache.EvictTable("Items");
        _byName = map;
        return map;
    }
}
