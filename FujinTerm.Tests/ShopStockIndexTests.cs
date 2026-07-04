using System.IO;
using System.Linq;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class ShopStockIndexTests : IDisposable
{
    private readonly string _root;

    public ShopStockIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-shopstock-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // Shop 3 stocks items 175 & 176; shop 5 stocks 176 & 64 (176 is sold by
    // two shops). Shop 8 has only empty (0) slots — proves 0 is skipped and a
    // stock-less shop is never indexed. Extra Item-N columns beyond those set
    // are simply absent (mirrors the real MDB export's sparse tail).
    private const string ShopsJson = """
        [
          { "Number": 3, "Name": "General Store", "Item-0": 175, "Item-1": 176, "Item-2": 0 },
          { "Number": 5, "Name": "Sword Shop",    "Item-0": 176, "Item-1": 64,  "Item-2": 0 },
          { "Number": 8, "Name": "Empty Stall",   "Item-0": 0,   "Item-1": 0 }
        ]
        """;

    private ShopStockIndex NewIndex(string set = "alpha", string json = ShopsJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, set));
        File.WriteAllText(Path.Combine(_root, set, "Shops.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet(set);
        ShopStockIndex index = new(cache);
        index.OnActiveSetChanged(set);
        return index;
    }

    [Fact]
    public void Load_IndexesDistinctStockedItems()
    {
        ShopStockIndex s = NewIndex();
        // 175, 176, 64 — three distinct stocked ids (0 slots skipped).
        Assert.Equal(3, s.ItemCount);
    }

    [Fact]
    public void ShopsSelling_SingleShopItem_ReturnsThatShop()
    {
        ShopStockIndex s = NewIndex();
        Assert.Equal(new[] { 3 }, s.ShopsSelling(175));
        Assert.Equal(new[] { 5 }, s.ShopsSelling(64));
    }

    [Fact]
    public void ShopsSelling_ItemInTwoShops_ReturnsBoth()
    {
        ShopStockIndex s = NewIndex();
        Assert.Equal(new[] { 3, 5 }, s.ShopsSelling(176).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void ShopsSelling_UnstockedItem_ReturnsEmpty()
    {
        ShopStockIndex s = NewIndex();
        Assert.Empty(s.ShopsSelling(9999));
        Assert.False(s.AnyShopSells(9999));
    }

    [Fact]
    public void AnyShopSells_StockedItem_True()
    {
        ShopStockIndex s = NewIndex();
        Assert.True(s.AnyShopSells(176));
    }

    [Fact]
    public void ZeroSlot_IsNotIndexedAsItem()
    {
        ShopStockIndex s = NewIndex();
        // The 0 sentinel must never become a stocked "item 0".
        Assert.False(s.AnyShopSells(0));
    }

    [Fact]
    public void ActiveSetChanged_ToNull_ClearsIndex()
    {
        ShopStockIndex s = NewIndex();
        Assert.True(s.ItemCount > 0);

        s.OnActiveSetChanged(null);

        Assert.Equal(0, s.ItemCount);
        Assert.False(s.AnyShopSells(176));
    }
}
