using FujinTerm.Game.Calculators;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Behavioural coverage for <see cref="MovementSpeedCalculator"/>: the base
/// 1100 ms speed, the encumbrance / slowness / quickness terms, and the
/// three-way cap classification with its quickness-to-cap distance.
/// </summary>
public sealed class MovementSpeedCalculatorTests
{
    [Fact]
    public void BareCharacter_IsSlightlyAboveTheCap_AndNeedsTenQuickness()
    {
        // 1100 base, no encumbrance / slowness / quickness → 100 ms over the cap,
        // which is exactly 10 quickness short.
        MovementSpeedResult r = MovementSpeedCalculator.Compute(0, 0, 0);

        Assert.Equal(1100.0, r.SpeedMillis, 5);
        Assert.Equal(MovementCapState.TooSlow, r.State);
        Assert.Equal(10.0, r.QuicknessToCap, 5);
    }

    [Fact]
    public void TenQuickness_HitsTheCapExactly()
    {
        MovementSpeedResult r = MovementSpeedCalculator.Compute(0, 10, 0);

        Assert.Equal(1000.0, r.SpeedMillis, 5);
        Assert.Equal(MovementCapState.AtCap, r.State);
        Assert.Equal(0.0, r.QuicknessToCap, 5);
    }

    [Fact]
    public void ExcessQuickness_IsAboveCap_WithSpareToShed()
    {
        // 1100 - 15*10 = 950, i.e. 50 ms under the cap → 5 spare quickness.
        MovementSpeedResult r = MovementSpeedCalculator.Compute(0, 15, 0);

        Assert.Equal(950.0, r.SpeedMillis, 5);
        Assert.Equal(MovementCapState.AboveCap, r.State);
        Assert.Equal(5.0, r.QuicknessToCap, 5);
    }

    [Fact]
    public void Encumbrance_AddsQuadraticPenalty()
    {
        // 50% encumbrance adds (0.5)^2 * 2000 = 500 ms on top of the 1100 base.
        MovementSpeedResult r = MovementSpeedCalculator.Compute(50, 0, 0);

        Assert.Equal(1600.0, r.SpeedMillis, 5);
        Assert.Equal(MovementCapState.TooSlow, r.State);
        Assert.Equal(60.0, r.QuicknessToCap, 5);
    }

    [Fact]
    public void Slowness_AddsSevenMillisEach()
    {
        // 1100 + 20*7 = 1240.
        MovementSpeedResult r = MovementSpeedCalculator.Compute(0, 0, 20);

        Assert.Equal(1240.0, r.SpeedMillis, 5);
        Assert.Equal(MovementCapState.TooSlow, r.State);
    }
}
