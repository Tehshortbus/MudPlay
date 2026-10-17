using System;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using Xunit;

namespace MudPlay.Tests;

// Coverage for ExperienceTableCalculator — the exp-chart term,
// the Stock vs ParaMUD cumulative progressions, and the time-to-level helper.
// Monotonicity + realm-divergence are the invariants the loops must preserve.
public sealed class ExperienceTableCalculatorTests
{
    [Fact]
    public void CalcExpChart_AddsHundredPlusRace()
    {
        // (classExpTable + 100) + raceExpTable.
        Assert.Equal(250, ExperienceTableCalculator.CalcExpChart(100, 50));
        Assert.Equal(100, ExperienceTableCalculator.CalcExpChart(0, 0));
    }

    [Fact]
    public void CalcExpNeeded_StockLevel1IsZero()
    {
        Assert.Equal(0, ExperienceTableCalculator.CalcExpNeeded(1, 100, RealmType.Stock));
    }

    [Fact]
    public void CalcExpNeeded_ParaMudLevel1IsChartTimesTen()
    {
        // ParaMUD seeds nRes = chart*10 and iterates level-1 times, so the
        // level-1 value is the un-iterated seed — not zero like Stock.
        Assert.Equal(1000, ExperienceTableCalculator.CalcExpNeeded(1, 100, RealmType.ParaMud));
    }

    [Theory]
    [InlineData(RealmType.Stock)]
    [InlineData(RealmType.ParaMud)]
    public void CalcExpNeeded_IsMonotonicAcrossLevels(RealmType realm)
    {
        long prev = -1;
        for (int level = 1; level <= 60; level++)
        {
            long exp = ExperienceTableCalculator.CalcExpNeeded(level, 200, realm);
            Assert.True(exp >= prev, $"level {level} exp {exp} < prev {prev} ({realm})");
            prev = exp;
        }
    }

    [Theory]
    [InlineData(30, 37_970_158L)]
    [InlineData(35, 76_371_547L)]              // taper begins: 115% until level 34
    [InlineData(40, 152_274_714L)]
    [InlineData(50, 530_710_194L)]
    [InlineData(60, 1_547_879_322L)]
    [InlineData(75, 5_533_282_720L)]
    [InlineData(100, 37_894_549_431L)]         // floored at 108% from level 69
    [InlineData(120, 176_624_870_861L)]
    public void CalcExpNeeded_ParaMudFollowsTheLevel34Taper(int level, long expected)
    {
        // ParaMUD 1.9 drops the 115% growth by a point every 5 levels from
        // level 34, floored at 108%. Chart 230 pins the curve on both sides of
        // the taper start and the floor.
        Assert.Equal(expected, ExperienceTableCalculator.CalcExpNeeded(level, 230, RealmType.ParaMud));
    }

    [Fact]
    public void CalcExpNeeded_Paradigm_SaturatesAtLongMax_NotOverflow()
    {
        // A huge chart at a high level clamps, never throws or wraps negative. (Stock
        // keeps the game's own unsigned rollover arithmetic instead.)
        long exp = ExperienceTableCalculator.CalcExpNeeded(200, int.MaxValue, RealmType.ParaMud);
        Assert.Equal(long.MaxValue, exp);
    }

    [Fact]
    public void CalcTimeToLevel_NonPositiveRate_ReturnsNull()
    {
        Assert.Null(ExperienceTableCalculator.CalcTimeToLevel(1000, 0, 0));
        Assert.Null(ExperienceTableCalculator.CalcTimeToLevel(1000, 0, -5));
    }

    [Fact]
    public void CalcTimeToLevel_AlreadyThere_ReturnsZero()
    {
        Assert.Equal(TimeSpan.Zero, ExperienceTableCalculator.CalcTimeToLevel(1000, 1000, 100));
        Assert.Equal(TimeSpan.Zero, ExperienceTableCalculator.CalcTimeToLevel(1000, 1500, 100));
    }

    [Fact]
    public void CalcTimeToLevel_ComputesHoursFromRemaining()
    {
        // remaining 1000 at 500/hr = 2 hours.
        TimeSpan? t = ExperienceTableCalculator.CalcTimeToLevel(1000, 0, 500);
        Assert.NotNull(t);
        Assert.Equal(2.0, t!.Value.TotalHours, 3);
    }

    [Theory]
    [InlineData(0, 0, 30, "30s")]            // sub-minute → seconds
    [InlineData(0, 4, 12, "4m 12s")]         // under 10m → minutes and seconds
    [InlineData(0, 9, 59, "9m 59s")]
    [InlineData(0, 10, 0, "10m")]            // 10m and up → plain minutes
    [InlineData(0, 45, 0, "45m")]            // < 90m → plain minutes
    [InlineData(0, 89, 0, "89m")]            // 60–89m stays minutes, not "1h 29m"
    [InlineData(0, 90, 0, "1h 30m")]         // 90m → h/m cutover
    [InlineData(4, 10, 0, "4h 10m")]
    [InlineData(24, 59, 0, "24h 59m")]       // just under the day cutover
    [InlineData(25, 0, 0, "1d 1h 0m")]       // 25h → days
    [InlineData(26, 30, 0, "1d 2h 30m")]
    public void FormatTimeToLevel_TiersByMagnitude(int h, int m, int s, string expected)
        => Assert.Equal(expected, ExperienceTableCalculator.FormatTimeToLevel(new TimeSpan(h, m, s)));
}
