using System.Text.Json;
using FujinTerm.Services;

namespace FujinTerm.Game.Combat;

// Fast lookup of a weapon's magic-hit level (HitMagic, MajorMUD ability code
// 142) by item Name in the active game-data set.
//
// A physical weapon hits a monster only when its HitMagic is ≥ the monster's
// Magical level (MonsterMagicIndex). A weapon with no HitMagic ability is level
// 0 and so can only hit non-magical monsters (Magical 0).
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
    // MajorMUD ability code for the magic-hit level (per GameData.AbilityNames).
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

    // The weapon's magic-hit level, or -1 when the item is unknown (no row with
    // that name) — fail-open. A known weapon with no HitMagic ability returns 0.
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
                    if (code != HitMagicAbilityCode) continue;
                    if (!row.TryGetProperty($"AbilVal-{i}", out JsonElement valEl)) continue;
                    if (valEl.ValueKind != JsonValueKind.Number) continue;
                    if (valEl.TryGetInt32(out int val)) hitMagic = val;
                    break;
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
