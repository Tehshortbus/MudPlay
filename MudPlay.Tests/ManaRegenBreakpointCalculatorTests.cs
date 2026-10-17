using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using Xunit;

namespace MudPlay.Tests;

// ManaRegenBreakpointCalculator — the reroll-planning math on top of the
// engine-verified CharacterCalculator.CalcManaRegen. Numbers below are worked by
// hand from the stock formula: base = trunc((lvl+20)*S*(mageryLvl+2)/1650),
// tick = trunc((regen%+100)*base/100). Mage L40 INT80 mageryLvl3 gear0:
// base = 60*80*5/1650 = 14; tick(0)=14, tick(8)=15 (108*14/100=15.12),
// tick(15)=16 (115*14/100=16.1).
public sealed class ManaRegenBreakpointCalculatorTests
{
    private static ManaRegenBreakpointCalculator.Inputs Mage40(int gear = 0) => new(
        Level: 40, MageryType: 1, Intellect: 80, Willpower: 0,
        MageryLevel: 3, GearRegenPercent: gear, Realm: RealmType.Stock);

    [Fact]
    public void Tick_MatchesEngineFormula()
    {
        var i = Mage40();
        Assert.Equal(14, ManaRegenBreakpointCalculator.Tick(i, 0));
        Assert.Equal(15, ManaRegenBreakpointCalculator.Tick(i, 8));
        Assert.Equal(16, ManaRegenBreakpointCalculator.Tick(i, 15));
    }

    [Fact]
    public void Compute_RangeCrossesBreakpoints_MapsRollValues()
    {
        // Spell range [5,15] at this level: worst roll (5) still gives tick 14,
        // best roll (15) gives 16 — two steps sit inside the range.
        var r = ManaRegenBreakpointCalculator.Compute(Mage40(), rollMin: 5, rollMax: 15);

        Assert.Equal(14, r.WorstTick);
        Assert.Equal(16, r.BestTick);
        Assert.Equal(5, r.RollMin);
        Assert.Equal(15, r.RollMax);

        // tick 15 needs +8% → roll value 8 (gear 0); tick 16 needs the max roll (15).
        Assert.Equal(
            new[] { new ManaRegenBreakpointCalculator.Breakpoint(15, 8), new ManaRegenBreakpointCalculator.Breakpoint(16, 15) },
            r.Breakpoints);
    }

    [Fact]
    public void Compute_RangeTooSmallToCrossAStep_NoBreakpoints()
    {
        // [5,7] never lifts the tick off 14 — rerolling here is pure mana waste.
        var r = ManaRegenBreakpointCalculator.Compute(Mage40(), rollMin: 5, rollMax: 7);

        Assert.Equal(14, r.WorstTick);
        Assert.Equal(14, r.BestTick);
        Assert.Empty(r.Breakpoints);
    }

    [Fact]
    public void Compute_Gear_LiftsTheBaselineTick()
    {
        // +8% from gear alone already reaches tick 15 before the spell rolls.
        var r = ManaRegenBreakpointCalculator.Compute(Mage40(gear: 8), rollMin: 5, rollMax: 15);
        Assert.Equal(14, ManaRegenBreakpointCalculator.Tick(Mage40(gear: 8) with { GearRegenPercent = 0 }, 0));   // level + stat only
        Assert.Equal(15, ManaRegenBreakpointCalculator.Tick(Mage40(gear: 8), 0));   // gear folded in
        Assert.True(r.WorstTick >= 15);
    }

    [Fact]
    public void Druid_UsesAveragedIntWil()
    {
        // Druid S = (INT+WIL)/2 = (80+40)/2 = 60; base = 60*60*5/1650 = 10.9 → 10.
        var druid = new ManaRegenBreakpointCalculator.Inputs(
            Level: 40, MageryType: 3, Intellect: 80, Willpower: 40,
            MageryLevel: 3, GearRegenPercent: 0, Realm: RealmType.Stock);
        Assert.Equal(10, ManaRegenBreakpointCalculator.Tick(druid, 0));
    }

    [Fact]
    public void Breakpoints_AreStrictlyIncreasingTicks()
    {
        var r = ManaRegenBreakpointCalculator.Compute(Mage40(), rollMin: 0, rollMax: 60);
        for (int n = 1; n < r.Breakpoints.Count; n++)
        {
            Assert.True(r.Breakpoints[n].Tick > r.Breakpoints[n - 1].Tick);
            Assert.True(r.Breakpoints[n].RollValueNeeded >= r.Breakpoints[n - 1].RollValueNeeded);
        }
    }
}
