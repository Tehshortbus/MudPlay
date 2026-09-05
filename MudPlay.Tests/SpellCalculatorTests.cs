using MudPlay.Game.Spells;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// Pins <see cref="SpellCalculator"/> against MMUD Explorer's
/// <c>modMMudDatabase.bas</c> getters. Every expected value below is
/// hand-computed from the verbatim VB6 algorithm (base + slope, flat
/// override, level clamp, per-round energy multiplier, chained end-cast,
/// and the <c>Fix</c> truncation edge cases).
/// </summary>
public sealed class SpellCalculatorTests
{
    private static SpellAbility Dmg(int value = 0) => new(1, value);
    private static SpellAbility Heal(int value = 0) => new(18, value);
    private static SpellAbility Drain(int value = 0) => new(8, value);
    private static SpellAbility EndCast(int spellNumber) => new(151, spellNumber);

    // ----- base value, no scaling, no energy multiplier ----------------

    [Fact]
    public void BaseOnly_NoScaling_ReturnsBase()
    {
        SpellFormulaInput spell = new()
        {
            MinBase = 5,
            MaxBase = 10,
            Abilities = [Dmg()],
        };

        Assert.Equal(5, SpellCalculator.MinDamage(spell, 10));
        Assert.Equal(10, SpellCalculator.MaxDamage(spell, 10));
    }

    // ----- level slope + Fix truncation --------------------------------

    [Fact]
    public void LevelSlope_TruncatesTowardZero()
    {
        SpellFormulaInput spell = new()
        {
            MinBase = 2, MinInc = 3, MinIncLVLs = 2,
            MaxBase = 4, MaxInc = 5, MaxIncLVLs = 2,
            Abilities = [Dmg()],
        };

        // Min: 2 + Fix(1.5 * 7 = 10.5) = 12
        Assert.Equal(12, SpellCalculator.MinDamage(spell, 7));
        // Max: 4 + Fix(2.5 * 7 = 17.5) = 21
        Assert.Equal(21, SpellCalculator.MaxDamage(spell, 7));
    }

    [Fact]
    public void LevelBelowOne_UsesBaseUnscaled()
    {
        SpellFormulaInput spell = new()
        {
            MinBase = 9, MinInc = 100, MinIncLVLs = 1,
            Abilities = [Dmg()],
        };

        Assert.Equal(9, SpellCalculator.MinDamage(spell, 0));
    }

    // ----- per-round energy multiplier ---------------------------------

    [Theory]
    [InlineData(500, 20)]  // 1000-500=500, Fix(500/500)=1 → 10 + 10*1
    [InlineData(333, 30)]  // 1000-333=667, Fix(667/333)=2 → 10 + 10*2
    [InlineData(250, 40)]  // 1000-250=750, Fix(750/250)=3 → 10 + 10*3
    [InlineData(143, 60)]  // 1000-143=857, Fix(857/143)=5 → 10 + 10*5
    public void EnergyMultiplier_MultipliesPerRoundFires(int energyCost, long expected)
    {
        SpellFormulaInput spell = new()
        {
            MinBase = 10, MaxBase = 10,
            EnergyCost = energyCost,
            Abilities = [Dmg()],
        };

        Assert.Equal(expected, SpellCalculator.MinDamage(spell, 5));
    }

    [Fact]
    public void EnergyCostBelow143_DamageGetsNoMultiplier()
    {
        SpellFormulaInput spell = new()
        {
            MinBase = 10, MaxBase = 10,
            EnergyCost = 100, // < 143 damage gate
            Abilities = [Dmg()],
        };

        Assert.Equal(10, SpellCalculator.MinDamage(spell, 5));
    }

    // ----- flat override (last qualifying slot wins) -------------------

    [Fact]
    public void FlatOverride_LastSlotWins_IgnoresBase()
    {
        SpellFormulaInput spell = new()
        {
            MinBase = 99, MaxBase = 99, // ignored
            Abilities = [Dmg(7), Dmg(15)],
        };

        Assert.Equal(15, SpellCalculator.MinDamage(spell, 5));
        Assert.Equal(15, SpellCalculator.MaxDamage(spell, 5));
    }

    [Fact]
    public void FlatOverride_StillGetsEnergyMultiplier()
    {
        SpellFormulaInput spell = new()
        {
            MinBase = 0, MaxBase = 0,
            EnergyCost = 500,
            Abilities = [Dmg(15)],
        };

        // override 15, then 15 + 15*Fix(500/500=1) = 30
        Assert.Equal(30, SpellCalculator.MinDamage(spell, 5));
    }

    // ----- heal vs damage slot gating ----------------------------------

    [Fact]
    public void HealSlot_HealsButDoesNoDamage()
    {
        SpellFormulaInput spell = new()
        {
            MinBase = 20, MaxBase = 30,
            Abilities = [Heal()],
        };

        Assert.Equal(20, SpellCalculator.MinHeal(spell, 5));
        Assert.Equal(30, SpellCalculator.MaxHeal(spell, 5));
        Assert.Equal(0, SpellCalculator.MinDamage(spell, 5));
        Assert.Equal(0, SpellCalculator.MaxDamage(spell, 5));
    }

    [Fact]
    public void DamageSlot_DamagesButDoesNoHeal()
    {
        SpellFormulaInput spell = new()
        {
            MinBase = 7, MaxBase = 9,
            Abilities = [Dmg()],
        };

        Assert.Equal(7, SpellCalculator.MinDamage(spell, 5));
        Assert.Equal(0, SpellCalculator.MinHeal(spell, 5));
    }

    [Fact]
    public void DrainSlot_DamagesEnemyAndHealsSelf()
    {
        SpellFormulaInput spell = new()
        {
            MinBase = 8, MaxBase = 12,
            Abilities = [Drain()],
        };

        // Drain (code 8) is a damage slot for MinDamage, a heal slot for MinHeal.
        Assert.Equal(8, SpellCalculator.MinDamage(spell, 5));
        Assert.Equal(8, SpellCalculator.MinHeal(spell, 5));
    }

    // ----- level clamp -------------------------------------------------

    [Fact]
    public void CapClampsCastLevelDown()
    {
        SpellFormulaInput spell = new()
        {
            MinBase = 0, MinInc = 10, MinIncLVLs = 1,
            Cap = 5,
            Abilities = [Dmg()],
        };

        // level 100 clamps to 5 → 0 + Fix(10*5) = 50
        Assert.Equal(50, SpellCalculator.MinDamage(spell, 100));
        // level 3 stays 3 → 0 + Fix(10*3) = 30
        Assert.Equal(30, SpellCalculator.MinDamage(spell, 3));
    }

    [Fact]
    public void ReqLevelClampsCastLevelUp()
    {
        SpellFormulaInput spell = new()
        {
            MinBase = 0, MinInc = 10, MinIncLVLs = 1,
            ReqLevel = 4,
            Abilities = [Dmg()],
        };

        // level 2 floors to ReqLevel 4 → 0 + Fix(10*4) = 40
        Assert.Equal(40, SpellCalculator.MinDamage(spell, 2));
    }

    // ----- duration ----------------------------------------------------

    [Fact]
    public void Duration_BaseAndSlope()
    {
        SpellFormulaInput spell = new()
        {
            Dur = 10, DurInc = 5, DurIncLVLs = 2,
            Abilities = [Dmg()],
        };

        // 10 + Fix(2.5 * 6 = 15) = 25
        Assert.Equal(25, SpellCalculator.Duration(spell, 6));
    }

    [Fact]
    public void Duration_NoSlope_ReturnsBase()
    {
        SpellFormulaInput spell = new() { Dur = 10 };
        Assert.Equal(10, SpellCalculator.Duration(spell, 6));
    }

    // ----- round → wall-clock seconds ----------------------------------
    // Duration is in spell ROUNDS; a spell round is 3 s (NOT the 5-second
    // combat round). Consumers convert with * SpellRoundSeconds.

    [Fact]
    public void SpellRoundSeconds_IsThree_NotTheCombatRound()
        // Guards the spell-round length against being "corrected" to the
        // 5-second combat round — the two are deliberately different, and
        // conflating them is what made the recast clock fire 3× too early.
        => Assert.Equal(3, SpellCalculator.SpellRoundSeconds);

    [Fact]
    public void WallClockRound_RunsSlightlyLongerThanNominal()
        // Live buff timers use the wall-clock round (~3.04s) because server rounds
        // run long (report paradigm-20260816-222917); it must exceed the nominal 3s
        // or the recast clock under-estimates the duration and fires early.
        => Assert.True(SpellCalculator.SpellRoundSecondsWallClock > SpellCalculator.SpellRoundSeconds);

    [Fact]
    public void WallClockRound_50RoundBuff_IsAbout152Seconds()
    {
        // prev (protection from evil): Dur=50 flat → 50 rounds. The recast clock arms
        // ~152s (not the nominal 150s), so "recast within 15s" fires at ~137s in — the
        // buff's REAL remaining time — instead of ~1-2s early off 150s.
        SpellFormulaInput prev = new() { Dur = 50 };
        long durSec = (long)System.Math.Round(
            SpellCalculator.Duration(prev, 50) * SpellCalculator.SpellRoundSecondsWallClock);
        Assert.Equal(152, durSec);
    }

    [Theory]
    [InlineData(100, 0, 0, 50, 300)]  // nature tap: Dur=100 flat → 100 rounds → 5:00
    [InlineData(7, 1, 4, 50, 57)]     // regeneration: 7 + Fix(50/4)=19 rounds → 57 s
    [InlineData(10, 0, 0, 50, 30)]    // rejuvinating field: Dur=10 → 30 s
    public void Duration_TimesRoundSeconds_GivesWallClock(
        int dur, int durInc, int durIncLvls, int level, long expectedSeconds)
    {
        SpellFormulaInput spell = new()
        {
            Dur = dur, DurInc = durInc, DurIncLVLs = durIncLvls,
        };

        long seconds = SpellCalculator.Duration(spell, level) * SpellCalculator.SpellRoundSeconds;
        Assert.Equal(expectedSeconds, seconds);
    }

    // ----- mana cost (no 143 gate) -------------------------------------

    [Theory]
    [InlineData(50, 250, 200)]  // 50 * Fix(1000/250=4)
    [InlineData(50, 100, 500)]  // 50 * Fix(1000/100=10) — below 143, still multiplies
    [InlineData(50, 0, 50)]     // no energy cost → no multiplier
    [InlineData(50, 600, 50)]   // > 500 → no multiplier
    public void ManaCost_MultipliesWithoutEnergyGate(int manaCost, int energyCost, long expected)
    {
        SpellFormulaInput spell = new() { ManaCost = manaCost, EnergyCost = energyCost };
        Assert.Equal(expected, SpellCalculator.ManaCost(spell));
    }

    // ----- chained end-cast --------------------------------------------

    [Fact]
    public void ChainedEndCast_AddsResolvedSpellResult()
    {
        SpellFormulaInput child = new()
        {
            Number = 42,
            MinBase = 5,
            EnergyCost = 200,
            Abilities = [Dmg()],
        };
        SpellFormulaInput parent = new()
        {
            MinBase = 10,
            EnergyCost = 200,
            Abilities = [Dmg(), EndCast(42)],
        };

        SpellFormulaInput? Resolve(int n) => n == 42 ? child : null;

        // parent base 10 (no self-multiply because endCast != 0) + child:
        //   child base 5, energyRem 800-200=600, Fix(600/200)=3 → 5 + 5*3 = 20
        // total = 30
        Assert.Equal(30, SpellCalculator.MinDamage(parent, 5, Resolve));
    }

    [Fact]
    public void ChainedEndCast_UnresolvableChain_AddsNothing()
    {
        SpellFormulaInput parent = new()
        {
            MinBase = 10,
            EnergyCost = 200,
            Abilities = [Dmg(), EndCast(42)],
        };

        // resolveChain returns null → chain contributes nothing, parent = base 10
        Assert.Equal(10, SpellCalculator.MinDamage(parent, 5, _ => null));
    }

    // ----- single cast (monster spell attack): no per-round energy fold ----
    // A monster casts its assigned spell once when the attack lands; how often
    // it fires per round rides the monster's own attack energy, not the spell's
    // EnergyCost. So the per-cast figure is the base/slope value with NO
    // multiplier — the same figure the per-round getters inflate.

    [Theory]
    [InlineData(1000)] // spits acid #325 — clamps to 1× anyway
    [InlineData(500)]  // lightning bolt — per-round getter doubles this
    [InlineData(166)]  // magma blast — per-round getter 6×'s this
    [InlineData(0)]    // 0-energy monster spell
    public void SingleCast_IgnoresEnergyMultiplier_RegardlessOfEnergyCost(int energyCost)
    {
        SpellFormulaInput spell = new()
        {
            MinBase = 12, MaxBase = 40,
            EnergyCost = energyCost,
            Abilities = [Dmg()],
        };

        Assert.Equal(12, SpellCalculator.SingleCastMinDamage(spell, 11));
        Assert.Equal(40, SpellCalculator.SingleCastMaxDamage(spell, 11));
    }

    [Fact]
    public void SingleCast_StillScalesByLevel()
    {
        // lightning bolt shape: 12 + L min, 20 + 2L max — a monster at level 20
        // casts 32–60 per cast (not the per-round total).
        SpellFormulaInput spell = new()
        {
            MinBase = 12, MinInc = 1, MinIncLVLs = 1,
            MaxBase = 20, MaxInc = 2, MaxIncLVLs = 1,
            EnergyCost = 500,
            Abilities = [Dmg()],
        };

        Assert.Equal(32, SpellCalculator.SingleCastMinDamage(spell, 20));
        Assert.Equal(60, SpellCalculator.SingleCastMaxDamage(spell, 20));
    }

    [Fact]
    public void SingleCast_FollowsEndCastChainOnce_NoMultiplier()
    {
        SpellFormulaInput child = new()
        {
            Number = 42,
            MinBase = 5,
            EnergyCost = 200, // per-round getter would multiply; single cast must not
            Abilities = [Dmg()],
        };
        SpellFormulaInput parent = new()
        {
            Number = 41,
            MinBase = 10,
            EnergyCost = 200,
            Abilities = [Dmg(), EndCast(42)],
        };

        SpellFormulaInput? Resolve(int n) => n == 42 ? child : null;

        // parent base 10 + child base 5, both single-cast → 15 (vs 30 per-round).
        Assert.Equal(15, SpellCalculator.SingleCastMinDamage(parent, 5, Resolve));
    }

    [Fact]
    public void SingleCast_ChainLoop_TerminatesViaVisitedGuard()
    {
        // A → B → A. The per-round path self-terminates via energy depletion, but
        // single cast has no such bound, so the visited-guard must stop it.
        SpellFormulaInput a = new() { Number = 1, MinBase = 10, Abilities = [Dmg(), EndCast(2)] };
        SpellFormulaInput b = new() { Number = 2, MinBase = 5, Abilities = [Dmg(), EndCast(1)] };
        SpellFormulaInput? Resolve(int n) => n == 1 ? a : n == 2 ? b : null;

        // A(10) → B(5) → A already visited, stops → 15.
        Assert.Equal(15, SpellCalculator.SingleCastMinDamage(a, 5, Resolve));
    }
}
