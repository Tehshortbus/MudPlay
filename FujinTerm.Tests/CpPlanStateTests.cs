using System.Collections.Generic;
using FujinTerm.Models.Profile;
using FujinTerm.ViewModels.CharacterWorkshop;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 10.7 — <see cref="CpPlanState"/> is the live bridge from the CP Allocation
/// tab to the Level Projection tab. Pins the level-resolution (latest planned row
/// at or below a level, else the baseline) and the change signal the projection
/// rebuilds on.
/// </summary>
public sealed class CpPlanStateTests
{
    [Fact]
    public void StatsAtLevel_ReturnsLatestRowAtOrBelow_ElseBaseline()
    {
        var state = new CpPlanState();
        var baseline = new CpPlanEntry(2, 40, 40, 40, 40, 40, 40);   // current level 2
        var rows = new List<CpPlanEntry>
        {
            new(3, 41, 40, 40, 40, 45, 40),   // +HEA at level 3
            new(5, 42, 40, 40, 40, 50, 40),   // more HEA at level 5
        };
        state.Update(baseline, currentLevel: 2, rows);

        Assert.True(state.HasData);
        Assert.Equal(40, state.StatsAtLevel(2).Health);   // at/below current → baseline
        Assert.Equal(45, state.StatsAtLevel(3).Health);   // level-3 row
        Assert.Equal(45, state.StatsAtLevel(4).Health);   // carries forward to 4
        Assert.Equal(50, state.StatsAtLevel(5).Health);   // level-5 row
        Assert.Equal(50, state.StatsAtLevel(9).Health);   // last row carries up
    }

    [Fact]
    public void StatsAtLevel_BeforeUpdate_ReturnsDefaultBaseline()
    {
        var state = new CpPlanState();
        Assert.False(state.HasData);
        Assert.Equal(0, state.StatsAtLevel(5).Health);
    }

    [Fact]
    public void Update_RaisesChanged()
    {
        var state = new CpPlanState();
        int fired = 0;
        state.Changed += () => fired++;

        state.Update(new CpPlanEntry(1, 40, 40, 40, 40, 40, 40), 1, new List<CpPlanEntry>());

        Assert.Equal(1, fired);
    }
}
