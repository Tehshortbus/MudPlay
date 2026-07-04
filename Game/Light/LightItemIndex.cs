using System.Text.Json;
using FujinTerm.Services;

namespace FujinTerm.Game.Light;

// Catalogue of every light-source item (ItemType 6) in the active game-data set,
// keyed by name, for the auto-light provisioning logic: it answers a light's
// projected illumination (ability code 54, IlluTarget) and its burn budget
// (UseCount) so the engine can pick a light strong enough for a route and compute
// how many to carry for a target duration.
//
// Mirrors ItemMagicIndex: the catalogue is built lazily by scanning the raw Items
// table's paired Abil-0..19 / AbilVal-0..19 columns, cached, and dropped on a
// game-data set switch so the next query rebuilds against the new set.
public sealed class LightItemIndex
{
    // MDB ItemType for a light source.
    private const int LightItemType = 6;

    // Ability code for a readied light's projected illumination (IlluTarget).
    private const int IlluTargetAbilityCode = 54;

    // Number of Abil-N slots on an Items row.
    private const int AbilitySlots = 20;

    private readonly GameDataCache _cache;
    private IReadOnlyList<LightItem>? _all;
    private Dictionary<string, LightItem>? _byName;

    public LightItemIndex(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ => { _all = null; _byName = null; };
    }

    // Every light item in the active set, in MDB order. Empty when no set is active
    // or the set has no Items.json.
    public IReadOnlyList<LightItem> All => _all ??= Build(out _byName);

    // Resolve a light by its game name (case-insensitive, whitespace-trimmed), or
    // null when no light with that name exists in the active set. Feeds off the
    // same catalogue as All; used to look up a readied light's strength / burn
    // budget from its parsed name.
    public LightItem? FindByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        _ = All;   // ensure the by-name map is built alongside the list
        return _byName is not null && _byName.TryGetValue(name.Trim(), out LightItem item)
            ? item : null;
    }

    private IReadOnlyList<LightItem> Build(out Dictionary<string, LightItem>? byName)
    {
        List<LightItem> list = new();
        Dictionary<string, LightItem> map = new(StringComparer.OrdinalIgnoreCase);

        JsonDocument? doc = _cache.GetRawTable("Items");
        if (doc is not null)
        {
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                if (ReadInt(row, "ItemType") != LightItemType) continue;
                if (!row.TryGetProperty("Number", out JsonElement numEl)
                    || !numEl.TryGetInt32(out int number) || number <= 0)
                    continue;
                if (!row.TryGetProperty("Name", out JsonElement nameEl)
                    || nameEl.ValueKind != JsonValueKind.String)
                    continue;
                string? name = nameEl.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;

                LightItem item = new(number, name.Trim(),
                    Strength: ReadIlluTarget(row), UseCount: ReadInt(row, "UseCount"));
                list.Add(item);
                // First writer wins on duplicate names — the id/strength are what
                // callers need and the first row is as good as any.
                map.TryAdd(item.Name, item);
            }
        }

        // Folded into the catalogue — release the pinned raw Items JsonDocument.
        _cache.EvictTable("Items");
        byName = map;
        return list;
    }

    private static int ReadIlluTarget(JsonElement row)
    {
        for (int i = 0; i < AbilitySlots; i++)
        {
            if (!row.TryGetProperty($"Abil-{i}", out JsonElement abilEl)) continue;
            if (abilEl.ValueKind != JsonValueKind.Number) continue;
            if (!abilEl.TryGetInt32(out int code) || code != IlluTargetAbilityCode) continue;
            if (!row.TryGetProperty($"AbilVal-{i}", out JsonElement valEl)) continue;
            if (valEl.ValueKind == JsonValueKind.Number && valEl.TryGetInt32(out int val))
                return val;
            return 0;
        }
        return 0;
    }

    private static int ReadInt(JsonElement row, string property)
        => row.TryGetProperty(property, out JsonElement el)
           && el.ValueKind == JsonValueKind.Number
           && el.TryGetInt32(out int v)
            ? v : 0;
}
