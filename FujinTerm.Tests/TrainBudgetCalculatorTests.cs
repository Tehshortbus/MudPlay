using FujinTerm.Game;
using FujinTerm.Game.Calculators;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 10.8 — coverage for <see cref="TrainBudgetCalculator"/>, the pure banked-exp
/// budgeter behind auto-train / Train Now. Pins the boundary the coordinator relies
/// on: a level sitting exactly on its exp threshold is trainable (the <c>&lt;</c> vs
/// <c>&lt;=</c> the loop hinges on), the cap clamps the scan, and the reserve buffer
/// carves the trainable count down (and clamps at zero). The Train Now CP-only
/// reconcile fires precisely when <see cref="TrainBudgetCalculator.LevelsToTrain"/>
/// is 0 yet CP is still applicable, so the reserve-covered → 0 cases below also pin
/// that gate's banked-exp side.
/// </summary>
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
}
