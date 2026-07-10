using System.Collections.Generic;
using FujinTerm.Game.Inventory;
using Xunit;

namespace FujinTerm.Tests;

// ShopListParser slices the fixed-width shop `list` body into stock rows. The
// column offsets come from the "Quantity" / "Price" header row; item names carry
// spaces so the parser can't split on whitespace.
public sealed class ShopListParserTests
{
    // Column offsets: Quantity at 24, Price at 40 — mirrors the aligned in-game
    // grid. Data rows are laid out to the same offsets.
    private const int QtyCol = 24;
    private const int PriceCol = 40;

    private static string Header() =>
        "Item".PadRight(QtyCol) + "Quantity".PadRight(PriceCol - QtyCol) + "Price";

    private static string Row(string name, string qty, string price) =>
        name.PadRight(QtyCol) + qty.PadRight(PriceCol - QtyCol) + price;

    [Fact]
    public void ParsesNamesQuantitiesAndPrices()
    {
        List<string> body = new()
        {
            string.Empty,
            Header(),
            new string('-', 47),
            Row("torch", "250", "Free"),
            Row("lantern", "40", "4 gold crowns"),
            Row("iron ration", "430", "10 silver nobles"),
        };

        IReadOnlyList<ShopListParser.StockRow> rows = ShopListParser.Parse(body);

        Assert.Equal(3, rows.Count);
        Assert.Equal(new ShopListParser.StockRow("torch", 250, "Free"), rows[0]);
        Assert.Equal(new ShopListParser.StockRow("lantern", 40, "4 gold crowns"), rows[1]);
        Assert.Equal(new ShopListParser.StockRow("iron ration", 430, "10 silver nobles"), rows[2]);
    }

    [Fact]
    public void PreservesMultiWordItemNames()
    {
        List<string> body = new()
        {
            Header(),
            Row("rope and grapple", "56", "10 gold crowns"),
        };

        IReadOnlyList<ShopListParser.StockRow> rows = ShopListParser.Parse(body);

        Assert.Single(rows);
        Assert.Equal("rope and grapple", rows[0].Name);
        Assert.Equal(56, rows[0].Quantity);
    }

    [Fact]
    public void KeepsUsabilityHintInPriceText()
    {
        List<string> body = new()
        {
            Header(),
            Row("crowbar", "35", "6 gold crowns (You can't use)"),
        };

        IReadOnlyList<ShopListParser.StockRow> rows = ShopListParser.Parse(body);

        Assert.Single(rows);
        // The "(You can't use)" hint rides along in Price and does not gate the row.
        Assert.Equal("6 gold crowns (You can't use)", rows[0].Price);
    }

    [Fact]
    public void SkipsSeparatorsBlanksAndPreHeaderNoise()
    {
        List<string> body = new()
        {
            "The following items are for sale here:",
            string.Empty,
            Header(),
            new string('-', 47),
            string.Empty,
            Row("torch", "250", "Free"),
        };

        IReadOnlyList<ShopListParser.StockRow> rows = ShopListParser.Parse(body);

        Assert.Single(rows);
        Assert.Equal("torch", rows[0].Name);
    }

    [Fact]
    public void RejectsRowWithNonNumericQuantity()
    {
        List<string> body = new()
        {
            Header(),
            Row("mystery", "lots", "Free"),
            Row("torch", "250", "Free"),
        };

        IReadOnlyList<ShopListParser.StockRow> rows = ShopListParser.Parse(body);

        Assert.Single(rows);
        Assert.Equal("torch", rows[0].Name);
    }

    [Fact]
    public void ReturnsEmptyWhenHeaderNeverFound()
    {
        List<string> body = new()
        {
            "just some prose",
            Row("torch", "250", "Free"),
        };

        Assert.Empty(ShopListParser.Parse(body));
    }
}
