using FujinTerm.Game.Inventory;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

// DeathRecord.LostItemsText folds the coins-on-hand into the "Inventory lost"
// column, listed per-denomination (not re-bucketed into a consolidated total).
public sealed class DeathRecordCoinTests
{
    [Fact]
    public void ListsEachHeldDenominationByItsOwnCount()
    {
        var record = new DeathRecord
        {
            LostItems = new List<DeathItem> { new("torch") },
            // 100 gold + 1 platinum must NOT collapse into "2 platinum".
            CoinsAtDeath = new CurrencyHoldings(0, 0, 100, 1, 0, 20_000),
        };

        string[] lines = record.LostItemsText.Split('\n');

        Assert.Equal(new[] { "torch", "1 platinum piece", "100 gold crowns" }, lines);
    }

    [Fact]
    public void SingularAndPluralCoinNames()
    {
        var record = new DeathRecord
        {
            CoinsAtDeath = new CurrencyHoldings(1, 2, 0, 0, 0, 21),
        };

        Assert.Equal("2 silver nobles\n1 copper farthing", record.LostItemsText);
    }

    [Fact]
    public void ZeroCoinsAndNoItems_ReadsNoneRecorded()
    {
        var record = new DeathRecord { CoinsAtDeath = CurrencyHoldings.Empty };

        Assert.Equal("None recorded.", record.LostItemsText);
    }

    [Fact]
    public void NullCoins_FallsBackToItemsOnly()
    {
        var record = new DeathRecord
        {
            LostItems = new List<DeathItem> { new("ration"), new("rope") },
        };

        Assert.Equal("ration\nrope", record.LostItemsText);
    }
}
