using System.Collections.Generic;
using System.Text.Json;

namespace MudPlay.Services;

// In-memory reverse index of Shops.json for the active game-data set: item id →
// the set of shop Numbers that stock it. Backs the item record's "Auto-obtain
// for path" flag — when the walker plans a route through an (Item: N) /
// (Ticket: N) gate whose item we don't carry, PathItemShopRouter asks this
// index which shops sell it, joins those against the live room graph, and
// detours to the nearest one.
//
// A shop "sells" an item when the id appears in any of its twenty
// Item-0..Item-19 stock slots. That is the authoritative stock list — reading
// it sidesteps the buy/sell-flag ambiguity of the Items.json Obtained From
// string.
//
// Level / class gates (MinLVL / MaxLVL / ClassRest) are deliberately NOT read:
// in the shipped 1.11p set they carry no meaningful buy restriction (only
// trainer shops gate, and those don't stock path items), and a genuinely
// refused buy is handled gracefully by the router's buy-timeout rather than
// pre-filtered here.
//
// Mirrors ItemNameStore: subscribes to GameDataCache.ActiveSetChanged, reads
// the table once, builds the index, and evicts it.
public sealed class ShopStockIndex
{
    private const int StockSlots = 20;
    private static readonly string[] ItemFields = NumberedFields.Of("Item-", StockSlots);

    private readonly GameDataCache _cache;
    private readonly LogService? _log;
    private readonly Dictionary<int, HashSet<int>> _shopsByItem = new();

    // Set the index was last built from, or null if empty.
    public string? ActiveSet { get; private set; }

    // The load of the set whose stock is in place; none while the index is being
    // rebuilt. The cache's load moves before the handler that rebuilds this runs, so
    // something asked in that moment that builds from the stock and keeps the result
    // for the cache's load checks this first. Written and read on the thread that
    // switches the set.
    public GameDataCache.Load LoadedFor { get; private set; }

    public ShopStockIndex(GameDataCache cache) : this(cache, log: null) { }

    public ShopStockIndex(GameDataCache cache, LogService? log)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _log = log;
    }

    // Shop Numbers that stock itemId, or an empty collection when nothing in the
    // active set sells it. The returned collection is a live view of the index —
    // callers read it, never mutate it.
    public IReadOnlyCollection<int> ShopsSelling(int itemId)
        => _shopsByItem.TryGetValue(itemId, out HashSet<int>? shops)
            ? shops
            : Array.Empty<int>();

    // True when at least one shop in the active set stocks the item.
    public bool AnyShopSells(int itemId) => _shopsByItem.ContainsKey(itemId);

    // Reload the index from setName's Shops.json. Pass null to clear. Wired by
    // AppServices to GameDataCache.ActiveSetChanged.
    public void OnActiveSetChanged(string? setName)
    {
        GameDataCache.Load load = _cache.ActiveLoad;
        LoadedFor = GameDataCache.Load.None;
        _shopsByItem.Clear();
        ActiveSet = setName;

        if (string.IsNullOrWhiteSpace(setName))
        {
            LoadedFor = load;
            _log?.Info("ShopStockIndex", "No active set; cleared.");
            return;
        }

        GameTable? doc = _cache.GetTable("Shops");
        if (doc is null)
        {
            LoadedFor = load;
            _log?.Info("ShopStockIndex", $"Active set '{setName}' has no Shops.json; empty.");
            return;
        }

        int stockedShops = 0;
        foreach (GameRow row in doc.Rows)
        {
            if (!TryReadInt(row, "Number", out int shopNumber) || shopNumber <= 0)
                continue;

            bool stockedAny = false;
            for (int slot = 0; slot < StockSlots; slot++)
            {
                if (!TryReadInt(row, ItemFields[slot], out int itemId) || itemId <= 0)
                    continue;
                if (!_shopsByItem.TryGetValue(itemId, out HashSet<int>? set))
                    _shopsByItem[itemId] = set = new HashSet<int>();
                set.Add(shopNumber);
                stockedAny = true;
            }
            if (stockedAny) stockedShops++;
        }

        _cache.EvictTable("Shops");
        LoadedFor = load;

        _log?.Info("ShopStockIndex",
            $"Indexed {_shopsByItem.Count} stocked item(s) across {stockedShops} shop(s) from '{setName}'.");

    }

    private static bool TryReadInt(GameRow row, string property, out int value)
    {
        value = 0;
        return row.TryGetProperty(property, out GameValue el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value);
    }
}
