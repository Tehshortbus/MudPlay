using System.Text.Json;
using FujinTerm.Services;

namespace FujinTerm.Game.Combat;

// Fast lookup of a spell's AttType (its damage element) by the spell's Short
// cast-code in the active game-data set.
//
// AttType is the top-level integer column on each Spells row that names the
// spell's damage flavor (per LookupEnums.SpellAttackTypeNames: 0 Cold, 1 Fire,
// 2 Stone, 3 Lightning, 4 Normal, 5 Water, 6 Poison). The combat resist guard
// reads it to decide whether a configured attack spell is *elementally*
// pre-emptable against a resistant monster — only 0/1/2/3/5 map to a
// deterministic resist (MonsterResistIndex.ElementalResistCode); 4 (Magic
// Resist) and 6 (poison) do not.
//
// Keyed by Short because the combat spell slots (CombatSpellSlot.SpellName) store
// the cast-code, not the display Name — matches SpellReqLevelIndex, whose lazy
// build / cache / drop-on-set-switch lifecycle this mirrors. Unknown cast-code →
// -1 (fail-open: the caller treats "no data" as "don't pre-empt").
public sealed class SpellAttackTypeIndex
{
    private readonly GameDataCache _cache;
    private Dictionary<string, int>? _byShort;

    public SpellAttackTypeIndex(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ => _byShort = null;
    }

    // The spell's AttType (0–6), or -1 when the cast-code is unknown — fail-open.
    public int AttackType(string? castCode)
    {
        if (string.IsNullOrWhiteSpace(castCode)) return -1;
        return Build().TryGetValue(castCode.Trim(), out int type) ? type : -1;
    }

    private Dictionary<string, int> Build()
    {
        if (_byShort is { } cached) return cached;

        Dictionary<string, int> map = new(StringComparer.OrdinalIgnoreCase);
        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is not null)
        {
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("Short", out JsonElement shortEl)) continue;
                if (shortEl.ValueKind != JsonValueKind.String) continue;
                string? code = shortEl.GetString();
                if (string.IsNullOrWhiteSpace(code)) continue;

                if (!row.TryGetProperty("AttType", out JsonElement typeEl)) continue;
                if (typeEl.ValueKind != JsonValueKind.Number) continue;
                if (!typeEl.TryGetInt32(out int attType)) continue;

                // Last writer wins on duplicate cast-codes (rare in game data).
                map[code.Trim()] = attType;
            }
        }

        // Folded into the map — release the pinned raw Spells JsonDocument.
        // (SpellReqLevelIndex evicts the same table; whichever builds second just
        // triggers a one-time reload via GetRawTable.)
        _cache.EvictTable("Spells");
        _byShort = map;
        return map;
    }
}
