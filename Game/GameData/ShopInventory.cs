using System.Collections.Generic;
using System.Text.Json;
using FujinTerm.Game.Calculators;
using FujinTerm.Services;

namespace FujinTerm.Game.GameData;

// One stock slot of a shop, resolved against Items.json. BaseCopper is the item's
// MDB Price folded through its Currency (via ShopPriceCalculator.ToCopper) so the
// caller can price buy/sell without re-reading the item row. The four restock
// fields are the raw Shops-table values for the slot:
//   RestockPercent 100 → always replenishes; 0 → never self-restocks, the shop
//   only holds one if a player sold it there; between → a probabilistic trickle.
//   RestockPeriod (in minutes) / RestockAmount are the shop's per-cycle cadence;
//   MaxStock is the cap the shop will hold.
public readonly record struct ShopStockEntry(
    int ItemId, string ItemName, double BaseCopper,
    int MaxStock, int RestockPeriod, int RestockAmount, int RestockPercent);

// A shop's static definition for display: identity, buy markup, and its resolved
// stock list in slot order. Banks / trainers legitimately have an empty Stock.
// MinLevel / MaxLevel / ClassRestriction are only meaningful for a training room
// (ShopType 8): the level band it trains and the single Classes row it serves
// (ClassRestriction 0 = any class). They're the raw Shops-table values.
public sealed record ShopDefinition(
    int Number, string Name, int ShopType, int MarkupPercent,
    IReadOnlyList<ShopStockEntry> Stock,
    int MinLevel, int MaxLevel, int ClassRestriction);

// Reads a shop's full stock from the active game-data set. Distinct from
// ShopStockIndex (which builds only an item→shops reverse map and evicts the
// table): this resolves every slot's restock parameters + item pricing for the
// room-detail popup's shop readout. Does NOT evict the cached tables — the Game
// Data browser that hosts the popup keeps reading them.
public static class ShopInventoryReader
{
    private const int StockSlots = 20;

    // Read shopNumber's row from Shops.json, resolve each populated Item-N slot
    // against Items.json, and return the definition. Null when no set is active,
    // the set has no Shops.json, or the number isn't present.
    public static ShopDefinition? Read(GameDataCache cache, int shopNumber)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (shopNumber <= 0) return null;

        JsonDocument? shops = cache.GetRawTable("Shops");
        if (shops is null) return null;

        JsonElement? rowOpt = FindByNumber(shops, shopNumber);
        if (rowOpt is not { } row) return null;

        string name = ReadString(row, "Name");
        int shopType = ReadInt(row, "ShopType");
        int markup = ReadInt(row, "Markup%");
        int minLevel = ReadInt(row, "MinLVL");
        int maxLevel = ReadInt(row, "MaxLVL");
        int classRest = ReadInt(row, "ClassRest");

        // First pass over the shop row: gather the raw slots in order and the set
        // of item ids we need to price.
        var slots = new List<(int ItemId, int Max, int Time, int Amount, int Pct)>();
        var neededIds = new HashSet<int>();
        for (int slot = 0; slot < StockSlots; slot++)
        {
            int itemId = ReadInt(row, $"Item-{slot}");
            if (itemId <= 0) continue;
            slots.Add((itemId,
                ReadInt(row, $"Max-{slot}"),
                ReadInt(row, $"Time-{slot}"),
                ReadInt(row, $"Amount-{slot}"),
                ReadInt(row, $"%-{slot}")));
            neededIds.Add(itemId);
        }

        // One pass over Items.json to resolve name + base copper for just the
        // needed ids (avoids O(slots × items) repeated lookups).
        var itemInfo = ResolveItems(cache, neededIds);

        var stock = new List<ShopStockEntry>(slots.Count);
        foreach ((int itemId, int max, int time, int amount, int pct) in slots)
        {
            (string itemName, double baseCopper) = itemInfo.TryGetValue(itemId, out (string Name, double Copper) info)
                ? info
                : ($"#{itemId}", 0.0);
            stock.Add(new ShopStockEntry(itemId, itemName, baseCopper, max, time, amount, pct));
        }

        return new ShopDefinition(shopNumber, name, shopType, markup, stock,
            minLevel, maxLevel, classRest);
    }

    private static Dictionary<int, (string Name, double Copper)> ResolveItems(
        GameDataCache cache, HashSet<int> ids)
    {
        var result = new Dictionary<int, (string, double)>();
        if (ids.Count == 0) return result;

        JsonDocument? items = cache.GetRawTable("Items");
        if (items is null) return result;

        foreach (JsonElement row in items.RootElement.EnumerateArray())
        {
            int number = ReadInt(row, "Number");
            if (number <= 0 || !ids.Contains(number)) continue;
            double copper = ShopPriceCalculator.ToCopper(ReadInt(row, "Price"), ReadInt(row, "Currency"));
            result[number] = (ReadString(row, "Name"), copper);
            if (result.Count == ids.Count) break;
        }
        return result;
    }

    private static JsonElement? FindByNumber(JsonDocument doc, int number)
    {
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (ReadInt(row, "Number") == number) return row;
        }
        return null;
    }

    private static int ReadInt(JsonElement row, string property)
        => row.TryGetProperty(property, out JsonElement el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out int v)
            ? v
            : 0;

    private static string ReadString(JsonElement row, string property)
        => row.TryGetProperty(property, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? string.Empty
            : string.Empty;
}
