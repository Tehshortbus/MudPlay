using MudPlay.Game.Combat;
using Xunit;

namespace MudPlay.Tests;

// RateText.Compact abbreviates a per-hour figure to k / M so it fits the
// narrow Session Stats graph headers and the main-window looping chip without a
// comma-grouped digit run. Invariant culture keeps the decimal a dot on every locale.
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

    // A rate to a tenth that never reads 0% or 100% unless it is exactly that (report
    // paradigm-20260930-213857: 1,067 of 1,069 sneaks read "100%").
    [Theory]
    [InlineData(99.81, "99.8%")]
    [InlineData(99.96, "99.9%")]   // would round to 100 — it isn't
    [InlineData(100.0, "100%")]
    [InlineData(0.04, "0.1%")]     // would round to 0 — it isn't
    [InlineData(0.0, "0%")]
    [InlineData(47.25, "47.3%")]
    [InlineData(50.0, "50%")]
    public void Percent_ToATenth_NeverFalselyWhole(double percent, string expected)
        => Assert.Equal(expected, RateText.Percent(percent));
}
