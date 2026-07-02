using System.Collections.Generic;
using System.Text.Json;

namespace FujinTerm.Services;

/// <summary>
/// In-memory reverse index of <c>Shops.json</c> for the active game-data
/// set: item id → the set of shop <c>Number</c>s that stock it. Backs the
/// Settings → Other "buy item if needed" affordance — when the walker plans
/// a route through an <c>(Item: N)</c> / <c>(Ticket: N)</c> gate whose item
/// we don't carry, <see cref="Game.Map.PathItemShopRouter"/> asks this index
/// which shops sell it, joins those against the live room graph, and detours
/// to the nearest one.
/// </summary>
/// <remarks>
/// <para>
/// A shop "sells" an item when the id appears in any of its twenty
/// <c>Item-0</c>..<c>Item-19</c> stock slots. That is the authoritative
/// stock list — reading it sidesteps the buy/sell-flag ambiguity of the
/// <c>Items.json</c> <c>Obtained From</c> string.
/// </para>
/// <para>
/// Level / class gates (<c>MinLVL</c> / <c>MaxLVL</c> / <c>ClassRest</c>)
/// are deliberately NOT read: in the shipped 1.11p set they carry no
/// meaningful buy restriction (only trainer shops gate, and those don't
/// stock path items), and a genuinely refused <c>buy</c> is handled
/// gracefully by the router's buy-timeout rather than pre-filtered here.
/// </para>
/// <para>
/// Mirrors <see cref="ItemNameStore"/>: subscribes to
/// <see cref="GameDataCache.ActiveSetChanged"/>, reads the raw table once,
/// builds the index, and evicts the <see cref="JsonDocument"/>.
/// </para>
/// </remarks>
public sealed class ShopStockIndex
{
    private const int StockSlots = 20;

    private readonly GameDataCache _cache;
    private readonly LogService? _log;
    private readonly Dictionary<int, HashSet<int>> _shopsByItem = new();

    /// <summary>Set the index was last built from, or <c>null</c> if empty.</summary>
    public string? ActiveSet { get; private set; }

    /// <summary>Number of distinct stocked item ids in the active set.</summary>
    public int ItemCount => _shopsByItem.Count;

    /// <summary>Fires after every successful (re)load, including the transition to no-set-active.</summary>
    public event Action? StoreReloaded;

    public ShopStockIndex(GameDataCache cache) : this(cache, log: null) { }

    public ShopStockIndex(GameDataCache cache, LogService? log)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _log = log;
    }

    /// <summary>
    /// Shop <c>Number</c>s that stock <paramref name="itemId"/>, or an empty
    /// collection when nothing in the active set sells it. The returned
    /// collection is a live view of the index — callers read it, never
    /// mutate it.
    /// </summary>
    public IReadOnlyCollection<int> ShopsSelling(int itemId)
        => _shopsByItem.TryGetValue(itemId, out HashSet<int>? shops)
            ? shops
            : Array.Empty<int>();

    /// <summary>True when at least one shop in the active set stocks the item.</summary>
    public bool AnyShopSells(int itemId) => _shopsByItem.ContainsKey(itemId);

    /// <summary>
    /// Reload the index from <paramref name="setName"/>'s <c>Shops.json</c>.
    /// Pass <c>null</c> to clear. Wired by <see cref="AppServices"/> to
    /// <see cref="GameDataCache.ActiveSetChanged"/>.
    /// </summary>
    public void OnActiveSetChanged(string? setName)
    {
        _shopsByItem.Clear();
        ActiveSet = setName;

        if (string.IsNullOrWhiteSpace(setName))
        {
            _log?.Info("ShopStockIndex", "No active set; cleared.");
            StoreReloaded?.Invoke();
            return;
        }

        JsonDocument? doc = _cache.GetRawTable("Shops");
        if (doc is null)
        {
            _log?.Info("ShopStockIndex", $"Active set '{setName}' has no Shops.json; empty.");
            StoreReloaded?.Invoke();
            return;
        }

        int stockedShops = 0;
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!TryReadInt(row, "Number", out int shopNumber) || shopNumber <= 0)
                continue;

            bool stockedAny = false;
            for (int slot = 0; slot < StockSlots; slot++)
            {
                if (!TryReadInt(row, $"Item-{slot}", out int itemId) || itemId <= 0)
                    continue;
                if (!_shopsByItem.TryGetValue(itemId, out HashSet<int>? set))
                    _shopsByItem[itemId] = set = new HashSet<int>();
                set.Add(shopNumber);
                stockedAny = true;
            }
            if (stockedAny) stockedShops++;
        }

        _cache.EvictTable("Shops");

        _log?.Info("ShopStockIndex",
            $"Indexed {_shopsByItem.Count} stocked item(s) across {stockedShops} shop(s) from '{setName}'.");

        StoreReloaded?.Invoke();
    }

    private static bool TryReadInt(JsonElement row, string property, out int value)
    {
        value = 0;
        return row.TryGetProperty(property, out JsonElement el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value);
    }
}
