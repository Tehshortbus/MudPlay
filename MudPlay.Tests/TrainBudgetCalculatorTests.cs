using MudPlay.Game;
using MudPlay.Game.Calculators;
using Xunit;

namespace MudPlay.Tests;

// coverage for TrainBudgetCalculator, the pure banked-exp
// budgeter behind auto-train / Train Now. Pins the boundary the coordinator relies
// on: a level sitting exactly on its exp threshold is trainable (the < vs
// <= the loop hinges on), the cap clamps the scan, and the reserve buffer
// carves the trainable count down (and clamps at zero). The Train Now CP-only
// reconcile fires precisely when TrainBudgetCalculator.LevelsToTrain
// is 0 yet CP is still applicable, so the reserve-covered → 0 cases below also pin
// that gate's banked-exp side.
public sealed class TrainBudgetCalculatorTests
{
    private const int Chart = 200;
    private const int Cap = 60;
    private const RealmType Realm = RealmType.Stock;

    // Cumulative exp threshold to reach a level — the same curve the budgeter scans.
    private static long T(int level) => ExperienceTableCalculator.CalcExpNeeded(level, Chart, Realm);

    [Fact]
    public void BankableLevels_ExpOneShortOfNext_CountsNone()
    {
        // One exp below the level-6 threshold → can't reach 6 → nothing banked.
        Assert.Equal(0, TrainBudgetCalculator.BankableLevels(T(6) - 1, currentLevel: 5, Chart, Realm, Cap));
    }

    [Fact]
    public void BankableLevels_ExpExactlyOnThreshold_CountsThatLevel()
    {
        // Exactly on the level-6 threshold → level 6 is trainable. Pairing this with
        // the one-short case above pins the inclusive boundary the loop depends on.
        Assert.True(TrainBudgetCalculator.BankableLevels(T(6), currentLevel: 5, Chart, Realm, Cap) >= 1);
    }

    [Fact]
    public void BankableLevels_CountsEveryReachableLevel()
    {
        // Enough banked exp for levels 6,7,8,9 but not 10 → 4 bankable.
        Assert.True(T(10) > T(9), "test needs a strictly increasing bracket");
        Assert.Equal(4, TrainBudgetCalculator.BankableLevels(T(9), currentLevel: 5, Chart, Realm, Cap));
    }

    [Fact]
    public void BankableLevels_StopsAtCap()
    {
        // Unlimited exp but a cap of 3 → only levels 6,7,8 are counted.
        Assert.Equal(3, TrainBudgetCalculator.BankableLevels(long.MaxValue, currentLevel: 5, Chart, Realm, cap: 3));
    }

    [Theory]
    [InlineData(0)]      // unknown level
    [InlineData(-1)]
    public void BankableLevels_NonPositiveLevel_IsZero(int level)
    {
        Assert.Equal(0, TrainBudgetCalculator.BankableLevels(long.MaxValue, level, Chart, Realm, Cap));
    }

    [Fact]
    public void BankableLevels_NoChartOrCap_IsZero()
    {
        Assert.Equal(0, TrainBudgetCalculator.BankableLevels(long.MaxValue, currentLevel: 5, chart: 0, Realm, Cap));
        Assert.Equal(0, TrainBudgetCalculator.BankableLevels(long.MaxValue, currentLevel: 5, Chart, Realm, cap: 0));
    }

    // ----- BankableLevelsFractional (the status-bar "(+N.NN lvls)") ------

    [Fact]
    public void BankableLevelsFractional_OnThreshold_IsWholeWithNoFraction()
    {
        // Exactly on the level-9 threshold at level 5: 4 whole banked (6..9), 0% to 10.
        double v = TrainBudgetCalculator.BankableLevelsFractional(T(9), currentLevel: 5, Chart, Realm, Cap);
        Assert.True(System.Math.Abs(v - 4.0) < 1e-6, $"expected 4.00, got {v}");
    }

    [Fact]
    public void BankableLevelsFractional_HalfwayToNext_AddsHalf()
    {
        // Halfway between the level-9 and level-10 thresholds → 4 banked + 0.5 progress.
        long mid = T(9) + (T(10) - T(9)) / 2;
        double v = TrainBudgetCalculator.BankableLevelsFractional(mid, currentLevel: 5, Chart, Realm, Cap);
        Assert.True(System.Math.Abs(v - 4.5) < 0.01, $"expected ~4.50, got {v}");
    }

    [Fact]
    public void BankableLevelsFractional_NothingFullyBanked_IsPartialToNextOnly()
    {
        // Between the current level's threshold and the next: no whole level banked,
        // just partial progress toward it — so 0 < v < 1.
        long mid = T(5) + (T(6) - T(5)) / 2;
        double v = TrainBudgetCalculator.BankableLevelsFractional(mid, currentLevel: 5, Chart, Realm, Cap);
        Assert.True(System.Math.Abs(v - 0.5) < 0.01, $"expected ~0.50, got {v}");
    }

    [Theory]
    [InlineData(0, 4)]    // keep nothing → train all 4 banked
    [InlineData(1, 3)]    // hold one in reserve
    [InlineData(3, 1)]
    [InlineData(4, 0)]    // reserve covers everything banked → train nothing
    [InlineData(10, 0)]   // reserve exceeds banked → clamp at 0
    [InlineData(-2, 4)]   // negative reserve treated as 0
    public void LevelsToTrain_SubtractsReserveAndClampsAtZero(int keep, int expected)
    {
        // T(9) at level 5 banks exactly 4 levels (6..9); the reserve carves into that.
        Assert.Equal(expected,
            TrainBudgetCalculator.LevelsToTrain(T(9), currentLevel: 5, Chart, Realm, keep, Cap));
    }

    // ----- "Do not train above N" ceiling (reach N, then stop) -----------

    [Theory]
    [InlineData(0, 4)]    // no ceiling → all 4 banked (6..9)
    [InlineData(7, 2)]    // reach 7 → only 6,7
    [InlineData(9, 4)]    // ceiling at the top of what's banked → all 4
    [InlineData(20, 4)]   // ceiling above what's banked → no effect
    [InlineData(6, 1)]    // reach 6 → just 6
    [InlineData(5, 0)]    // already at the ceiling → nothing
    [InlineData(4, 0)]    // below current level → nothing
    public void LevelsToTrain_Ceiling_CapsSoFinalLevelNeverExceedsN(int ceiling, int expected)
    {
        // T(9) at level 5 banks 4 (6..9); the ceiling limits to (ceiling - level).
        Assert.Equal(expected,
            TrainBudgetCalculator.LevelsToTrain(T(9), currentLevel: 5, Chart, Realm, keep: 0, Cap, ceiling));
    }

    [Theory]
    [InlineData(5, 0, true)]    // 0 = no ceiling
    [InlineData(5, 7, true)]    // below the ceiling → may train
    [InlineData(6, 7, true)]
    [InlineData(7, 7, false)]   // at the ceiling → stop
    [InlineData(8, 7, false)]   // above → stop
    public void WithinCeiling_TrueWhileBelowCeiling(int level, int ceiling, bool expected)
    {
        Assert.Equal(expected, TrainBudgetCalculator.WithinCeiling(level, ceiling));
    }

    // A level not yet reached never reads as banked (report paradigm-20260930-203138).
    [Theory]
    [InlineData(0.9956, "0.99")]
    [InlineData(1.0, "1.00")]
    [InlineData(0.29, "0.29")]
    [InlineData(2.914, "2.91")]
    public void FormatBankableLevels_RoundsDown(double levels, string expected)
        => Assert.Equal(expected, TrainBudgetCalculator.FormatBankableLevels(levels));
}
