using FujinTerm.Game.Spells;
using Xunit;

namespace FujinTerm.Tests;

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
}
