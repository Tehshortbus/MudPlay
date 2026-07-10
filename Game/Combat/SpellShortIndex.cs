using System.Text.Json;
using FujinTerm.Services;

namespace FujinTerm.Game.Combat;

// Fast lookup of a spell's Short cast-code by its Spells.Number in the active
// game-data set.
//
// The per-monster override spell slots (MonsterOverlay.OverrideAttackSpellId /
// OverridePreAttackSpellId) store a Spells.Number, but the combat engine sends
// the Short cast-code to the server (CombatSpellSlot.SpellName is a Short too —
// see SpellReqLevelIndex). This index bridges the two so the chooser can
// substitute a numbered override in place of the configured cast-code slot.
//
// Mirrors SpellReqLevelIndex: built lazily by scanning the raw Spells table,
// cached, and dropped on game-data set switch. Unknown number → null.
public sealed class SpellShortIndex
{
    private readonly GameDataCache _cache;
    private Dictionary<int, string>? _byNumber;

    public SpellShortIndex(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ => _byNumber = null;
    }

    // The spell's Short cast-code, or null when no Spells row carries that
    // Number (or the number is non-positive).
    public string? ShortByNumber(int number)
    {
        if (number <= 0) return null;
        return Build().TryGetValue(number, out string? code) ? code : null;
    }

    private Dictionary<int, string> Build()
    {
        if (_byNumber is { } cached) return cached;

        Dictionary<int, string> map = new();
        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is not null)
        {
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("Number", out JsonElement nEl)
                    || nEl.ValueKind != JsonValueKind.Number
                    || !nEl.TryGetInt32(out int number)
                    || number <= 0)
                    continue;
                if (!row.TryGetProperty("Short", out JsonElement shortEl)
                    || shortEl.ValueKind != JsonValueKind.String)
                    continue;
                string? code = shortEl.GetString();
                if (string.IsNullOrWhiteSpace(code)) continue;

                // First writer wins on duplicate numbers (numbers are the MDB
                // primary key, so collisions shouldn't occur).
                map.TryAdd(number, code.Trim());
            }
        }

        // Folded into the map — release the pinned raw Spells JsonDocument.
        _cache.EvictTable("Spells");
        _byNumber = map;
        return map;
    }
}
