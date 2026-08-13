using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Game.Combat;

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
    private Dictionary<string, int>? _numberByShort;

    public SpellShortIndex(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ =>
        {
            _byNumber = null;
            _numberByShort = null;
        };
    }

    // The spell's Short cast-code, or null when no Spells row carries that
    // Number (or the number is non-positive).
    public string? ShortByNumber(int number)
    {
        if (number <= 0) return null;
        return Build().TryGetValue(number, out string? code) ? code : null;
    }

    // Reverse lookup: the Spells.Number that answers to a cast-code, or null
    // when nothing matches (case-insensitive — cast-codes are typed by hand).
    // Multiple spells can share a Short (e.g. several "turn"-coded undead
    // spells) — any one of them resolves to the same wire text once
    // ShortByNumber converts back, so which candidate wins is immaterial;
    // first-seen wins for a stable result. Used by the Monster editor's
    // "Override Attack" box to auto-resolve a typed cast-code onto the
    // mana-gated spell rung instead of falling through to a raw, ungated
    // command (report paradigm-20260813-070249).
    public int? NumberByShort(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        BuildReverse();
        return _numberByShort!.TryGetValue(code.Trim(), out int number) ? number : null;
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

    private void BuildReverse()
    {
        if (_numberByShort is not null) return;

        Dictionary<string, int> map = new(StringComparer.OrdinalIgnoreCase);
        foreach ((int number, string code) in Build())
            map.TryAdd(code, number);
        _numberByShort = map;
    }
}
