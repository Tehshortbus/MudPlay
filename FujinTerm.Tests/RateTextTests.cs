using FujinTerm.Game.Combat;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="RateText.Compact"/> abbreviates a per-hour figure to k / M so it fits the
/// narrow Session Stats graph headers and the main-window looping chip without a
/// comma-grouped digit run. Invariant culture keeps the decimal a dot on every locale.
/// </summary>
public sealed class RateTextTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(-42, "0")]         // clamps, never a negative rate
    [InlineData(42, "42")]
    [InlineData(999, "999")]
    [InlineData(5749, "5.7k")]     // the table/graph parity case
    [InlineData(1000, "1k")]
    [InlineData(1_000_000, "1M")]
    [InlineData(1_200_000, "1.2M")]
    public void Compact_AbbreviatesLargeFigures(double value, string expected)
    {
        Assert.Equal(expected, RateText.Compact(value));
    }
}
