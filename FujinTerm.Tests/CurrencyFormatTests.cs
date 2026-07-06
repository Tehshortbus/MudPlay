using FujinTerm.Game.Cash;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="CurrencyFormat"/> renders a copper-farthing amount as MajorMUD coin
/// denominations. <see cref="CurrencyFormat.Denominate"/> "flips up" to the largest
/// denomination with a whole unit (the Session Stats compact total / per-hour text);
/// <see cref="CurrencyFormat.Full"/> is the exact itemised breakdown behind the tooltip.
/// The ladder (copper 1, silver 10, gold 100, platinum 10 000, runic 1 000 000) mirrors
/// CurrencyHoldings.ToCopper — the ratios are non-uniform, so the rung boundaries are
/// worth pinning. Output is invariant-culture: the decimal separator stays a dot.
/// </summary>
public sealed class CurrencyFormatTests
{
    [Theory]
    [InlineData(0, "0 copper")]
    [InlineData(-500, "0 copper")]       // negatives clamp, never a "-5 gold"
    [InlineData(5, "5 copper")]
    [InlineData(10, "1 silver")]
    [InlineData(100, "1 gold")]
    [InlineData(1000, "10 gold")]        // the headline case: 1000 copper/hr → "10 gold"
    [InlineData(1050, "10.5 gold")]      // one decimal when not whole
    [InlineData(250, "2.5 gold")]
    [InlineData(10_000, "1 platinum")]
    [InlineData(1_000_000, "1 runic")]
    public void Denominate_FlipsUpToLargestWholeRung(double copper, string expected)
    {
        Assert.Equal(expected, CurrencyFormat.Denominate(copper));
    }

    [Fact]
    public void Denominate_RoundsToOneDecimal()
    {
        // 1234 copper / 100 = 12.34 gold → one-decimal, away-from-zero.
        Assert.Equal("12.3 gold", CurrencyFormat.Denominate(1234));
    }

    [Theory]
    [InlineData(0, "0 copper")]
    [InlineData(6, "6 copper")]
    [InlineData(25, "2 silver 5 copper")]
    [InlineData(1000, "10 gold")]                                  // no zero rungs emitted
    [InlineData(1_930_506, "1 runic 93 platinum 5 gold 6 copper")] // zero silver rung skipped
    public void Full_ItemisesLargestFirst_SkippingEmptyRungs(long copper, string expected)
    {
        Assert.Equal(expected, CurrencyFormat.Full(copper));
    }
}
