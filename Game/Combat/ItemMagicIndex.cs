using System.Text.Json;
using FujinTerm.Services;

namespace FujinTerm.Game.Combat;

/// <summary>
/// Fast lookup of a weapon's magic-hit level (<c>HitMagic</c>, MajorMUD
/// ability code 142) by item Name in the active game-data set.
/// </summary>
/// <remarks>
/// <para>
/// A physical weapon hits a monster only when its <c>HitMagic</c> is ≥ the
/// monster's <c>Magical</c> level (<see cref="MonsterMagicIndex"/>). A
/// weapon with no HitMagic ability is level 0 and so can only hit
/// non-magical monsters (Magical 0).
/// </para>
/// <para>
/// Mirrors <see cref="SeeHiddenIndex"/> / <see cref="MonsterMagicIndex"/>:
/// the map is built lazily by scanning the raw <c>Items</c> table's paired
/// <c>Abil-0..19</c> / <c>AbilVal-0..19</c> columns, cached, and dropped on
/// game-data set switch so the next query rebuilds against the new set.
/// Items carry 20 ability slots (vs 10 on Monsters). The key is the item
/// <c>Name</c> because <see cref="Models.Profile.CombatSettings.NormalWeapon"/>
/// and its siblings store the weapon by name. Unknown name → <c>-1</c>
/// (fail-open: the caller treats "no data" as "don't second-guess the
/// configured weapon").
/// </para>
/// </remarks>
public sealed class ItemMagicIndex
{
    /// <summary>MajorMUD ability code for the magic-hit level (per
    /// <see cref="GameData.AbilityNames"/>).</summary>
    private const int HitMagicAbilityCode = 142;

    /// <summary>Number of <c>Abil-N</c> slots on an Items row.</summary>
    private const int AbilitySlots = 20;

    private readonly GameDataCache _cache;
    private Dictionary<string, int>? _byName;

    public ItemMagicIndex(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ => _byName = null;
    }

    /// <summary>The weapon's magic-hit level, or <c>-1</c> when the item is
    /// unknown (no row with that name) — fail-open. A known weapon with no
    /// <c>HitMagic</c> ability returns 0.</summary>
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

        _byName = map;
        return map;
    }
}
