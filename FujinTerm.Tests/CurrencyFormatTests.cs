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
    [InlineData(0, "0 copp")]
    [InlineData(-500, "0 copp")]         // negatives clamp, never a "-5 gold"
    [InlineData(5, "5 copp")]
    [InlineData(10, "1 silv")]
    [InlineData(100, "1 gold")]
    [InlineData(1000, "10 gold")]        // the headline case: 1000 copper/hr → "10 gold"
    [InlineData(1050, "10.5 gold")]      // one decimal when not whole
    [InlineData(250, "2.5 gold")]
    [InlineData(10_000, "1 plat")]
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

    // Only the top (runic) rung takes the board-renamed word; every lower rung keeps
    // its stock label, and a blank/whitespace name falls back to "runic".
    [Fact]
    public void Denominate_UsesRenamedRunicWordOnTopRungOnly()
    {
        Assert.Equal("1 quatloos", CurrencyFormat.Denominate(1_000_000, "quatloos"));
        Assert.Equal("10 gold", CurrencyFormat.Denominate(1000, "quatloos")); // lower rung unchanged
        Assert.Equal("1 runic", CurrencyFormat.Denominate(1_000_000, "  "));  // blank → stock word
    }

    [Fact]
    public void Full_UsesRenamedRunicWordOnTopRungOnly()
    {
        Assert.Equal(
            "1 quatloos 93 platinum 5 gold 6 copper",
            CurrencyFormat.Full(1_930_506, "quatloos"));
    }
}
