using FujinTerm.Game;
using FujinTerm.Game.Calculators;
using Xunit;

namespace FujinTerm.Tests;

// Pins the MajorMUD shop buy/sell math against the reference client's numbers:
// copper conversion, charm+markup BUY (realm-agnostic), realm-branched SELL,
// and the friendly denomination reduction.
public sealed class ShopPriceCalculatorTests
{
    // ----- Copper conversion ----------------------------------------------

    [Theory]
    [InlineData(5, 0, 5)]        // Copper
    [InlineData(5, 1, 50)]       // Silver
    [InlineData(5, 2, 500)]      // Gold
    [InlineData(5, 3, 50000)]    // Platinum
    [InlineData(5, 4, 5000000)]  // Runic
    public void ToCopper_ScalesByCurrencyCode(long price, int currency, double expected)
    {
        Assert.Equal(expected, ShopPriceCalculator.ToCopper(price, currency));
    }

    // ----- BUY (markup + charm, realm-agnostic) ---------------------------

    [Fact]
    public void BuyCopper_MarkupDoublesThenCharm100Discounts10Percent()
    {
        // base 100 + 100% markup = 200; charm 100 → ×0.9 = 180.
        Assert.Equal(180, ShopPriceCalculator.BuyCopper(100, 100, 100));
    }

    [Fact]
    public void BuyCopper_LowCharmMarksUpAboveBase()
    {
        // base 100, no markup; charm 25 → ×1.05 = 105.
        Assert.Equal(105, ShopPriceCalculator.BuyCopper(100, 0, 25));
    }

    [Fact]
    public void BuyCopper_Charm50IsRetail()
    {
        // charm 50 → ×1.0, no markup → base unchanged.
        Assert.Equal(100, ShopPriceCalculator.BuyCopper(100, 0, 50));
    }

    // ----- SELL (charm only, realm-branched) ------------------------------

    [Fact]
    public void SellCopper_StockCharm100()
    {
        // (Fix(100/2)+25) * 100 / 100 = 75.
        Assert.Equal(75, ShopPriceCalculator.SellCopper(100, 100, RealmType.Stock));
    }

    [Fact]
    public void SellCopper_StockCharm51TruncatesTo50()
    {
        // Fix(51/2)=25 → (25+25)*100/100 = 50.
        Assert.Equal(50, ShopPriceCalculator.SellCopper(100, 51, RealmType.Stock));
    }

    [Fact]
    public void SellCopper_ParaMudCharm100()
    {
        // base/2 = 50; (1 + Fix(50/5)/100) = 1.1 → 55.
        Assert.Equal(55, ShopPriceCalculator.SellCopper(100, 100, RealmType.ParaMud));
    }

    [Fact]
    public void SellCopper_ParaMudCharm50IsHalf()
    {
        // base/2 = 50; Fix(0)/100 = 0 → 50.
        Assert.Equal(50, ShopPriceCalculator.SellCopper(100, 50, RealmType.ParaMud));
    }

    // ----- Friendly denomination reduction --------------------------------

    [Theory]
    [InlineData(50, "50 Copper")]
    [InlineData(150, "15 Silver")]
    [InlineData(180, "18 Silver")]
    [InlineData(1800, "18 Gold")]
    [InlineData(100000, "10 Platinum")]
    [InlineData(10000000, "10 Runic")]
    public void FormatCopper_ReducesToFriendliestDenomination(double copper, string expected)
    {
        Assert.Equal(expected, ShopPriceCalculator.FormatCopper(copper));
    }
}
