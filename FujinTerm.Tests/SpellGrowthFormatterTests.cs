using FujinTerm.Game.Spells;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins <see cref="SpellGrowthFormatter"/> — the Game Data Browser's
/// human-readable growth summary — against MMUD Explorer's spell-browser
/// "LVL Cap" / "LVL Increases" block. Magnitude ranges route through
/// <see cref="SpellCalculator.AffectMagnitude"/> (per-cast, no energy
/// multiplier); the growth formula and GCD reduction are pure string math,
/// so every expected value is hand-computed from the live data examples
/// (holy force #110, magic missile #1, frost jet #5).
/// </summary>
public sealed class SpellGrowthFormatterTests
{
    private static SpellAbility DamageMr(int value = 0) => new(17, value);   // Damage(-MR)
    private static SpellAbility Damage(int value = 0) => new(1, value);
    private static SpellAbility Heal(int value = 0) => new(18, value);
    private static SpellAbility Drain(int value = 0) => new(8, value);
    private static SpellAbility NotEvil() => new(111, 0);
    private static SpellAbility NonMagical() => new(144, 0);

    // ----- holy force #110 (the screenshot's worked example) ------------

    private static SpellFormulaInput HolyForce() => new()
    {
        Number = 110,
        MinBase = 18, MaxBase = 24,
        MaxInc = 2, MaxIncLVLs = 1,
        Cap = 22, ReqLevel = 13, EnergyCost = 1000,
        Abilities = [DamageMr(), NotEvil()],
    };

    [Fact]
    public void HolyForce_MagnitudeRange_IsAtCap()
        => Assert.Equal("18 to 68", SpellGrowthFormatter.MagnitudeRange(HolyForce()));

    [Fact]
    public void HolyForce_GrowthFormula_ScalesMaxOnly()
        => Assert.Equal("Max: 24+(2*lvl)", SpellGrowthFormatter.GrowthFormula(HolyForce()));

    [Fact]
    public void HolyForce_MagnitudeLabel_IsDamageMr()
        => Assert.Equal("Damage(-MR)", SpellGrowthFormatter.MagnitudeLabel(HolyForce()));

    [Fact]
    public void HolyForce_DisplayLevel_IsCap()
        => Assert.Equal(22, SpellGrowthFormatter.DisplayLevel(HolyForce()));

    // ----- magic missile #1 (IncLVLs == 0 → flat, no growth) ------------

    [Fact]
    public void MagicMissile_FlatMaxInc_HasNoGrowthFormula()
    {
        SpellFormulaInput spell = new()
        {
            Number = 1,
            MinBase = 4, MaxBase = 12,
            MaxInc = 1, MaxIncLVLs = 0, // IncLVLs 0 ⇒ no scaling, despite Inc != 0
            Cap = 6,
            Abilities = [Damage()],
        };

        Assert.Null(SpellGrowthFormatter.GrowthFormula(spell));
        Assert.Equal("4 to 12", SpellGrowthFormatter.MagnitudeRange(spell));
        Assert.Equal("Damage", SpellGrowthFormatter.MagnitudeLabel(spell));
    }

    // ----- frost jet #5 (slope 1/1 → "Max: 14+(lvl)") -------------------

    [Fact]
    public void FrostJet_UnitSlope_DropsTheCoefficient()
    {
        SpellFormulaInput spell = new()
        {
            Number = 5,
            MinBase = 8, MaxBase = 14,
            MaxInc = 1, MaxIncLVLs = 1,
            Cap = 11,
            Abilities = [Damage()],
        };

        Assert.Equal("Max: 14+(lvl)", SpellGrowthFormatter.GrowthFormula(spell));
        Assert.Equal("8 to 25", SpellGrowthFormatter.MagnitudeRange(spell));
    }

    // ----- GCD reduction of the per-level term --------------------------

    [Fact]
    public void GrowthTerm_ReducesWholeCoefficient()
    {
        // 4/2 → 2*lvl
        SpellFormulaInput spell = new()
        {
            MaxBase = 10, MaxInc = 4, MaxIncLVLs = 2, Cap = 5,
            Abilities = [Damage()],
        };
        Assert.Equal("Max: 10+(2*lvl)", SpellGrowthFormatter.GrowthFormula(spell));
    }

    [Fact]
    public void GrowthTerm_ReducesToFraction()
    {
        // 2/4 → lvl/2
        SpellFormulaInput spell = new()
        {
            MinBase = 5, MinInc = 2, MinIncLVLs = 4, Cap = 5,
            Abilities = [Damage()],
        };
        Assert.Equal("Min: 5+(lvl/2)", SpellGrowthFormatter.GrowthFormula(spell));
    }

    [Fact]
    public void GrowthTerm_ReducesMixedFraction()
    {
        // 6/4 → 3*lvl/2 (on the duration component)
        SpellFormulaInput spell = new()
        {
            Dur = 3, DurInc = 6, DurIncLVLs = 4, Cap = 5,
            Abilities = [Damage()],
        };
        Assert.Equal("Dur: 3+(3*lvl/2)", SpellGrowthFormatter.GrowthFormula(spell));
    }

    [Fact]
    public void GrowthTerm_NegativeSlope_UsesMinusSignAndUnsignedTerm()
    {
        SpellFormulaInput spell = new()
        {
            MinBase = 5, MinInc = -2, MinIncLVLs = 1, Cap = 5,
            Abilities = [Damage()],
        };
        Assert.Equal("Min: 5-(2*lvl)", SpellGrowthFormatter.GrowthFormula(spell));
    }

    [Fact]
    public void GrowthFormula_JoinsEveryScalingComponent()
    {
        SpellFormulaInput spell = new()
        {
            MinBase = 2, MinInc = 1, MinIncLVLs = 1,
            MaxBase = 4, MaxInc = 2, MaxIncLVLs = 1,
            Dur = 6, DurInc = 1, DurIncLVLs = 1,
            Cap = 5,
            Abilities = [Damage()],
        };
        Assert.Equal("Min: 2+(lvl), Max: 4+(2*lvl), Dur: 6+(lvl)", SpellGrowthFormatter.GrowthFormula(spell));
    }

    [Fact]
    public void GrowthFormula_NothingScales_IsNull()
    {
        SpellFormulaInput spell = new()
        {
            MinBase = 5, MaxBase = 10, // no Inc/IncLVLs anywhere
            Abilities = [Damage()],
        };
        Assert.Null(SpellGrowthFormatter.GrowthFormula(spell));
    }

    // ----- magnitude range edge cases -----------------------------------

    [Fact]
    public void MagnitudeRange_MinEqualsMax_ShowsSingleValue()
    {
        SpellFormulaInput spell = new()
        {
            MinBase = 10, MaxBase = 10,
            Abilities = [Heal()],
        };
        Assert.Equal("10", SpellGrowthFormatter.MagnitudeRange(spell));
    }

    [Fact]
    public void MagnitudeRange_NoStoredMagnitude_IsNull()
    {
        SpellFormulaInput spell = new() { Abilities = [NotEvil()] };
        Assert.Null(SpellGrowthFormatter.MagnitudeRange(spell));
    }

    // ----- magnitude label ----------------------------------------------

    [Fact]
    public void MagnitudeLabel_Heal()
    {
        SpellFormulaInput spell = new() { MinBase = 1, MaxBase = 2, Abilities = [Heal()] };
        Assert.Equal("Heal", SpellGrowthFormatter.MagnitudeLabel(spell));
    }

    [Fact]
    public void MagnitudeLabel_DrainLife()
    {
        SpellFormulaInput spell = new() { MinBase = 1, MaxBase = 2, Abilities = [Drain()] };
        Assert.Equal("DrainLife", SpellGrowthFormatter.MagnitudeLabel(spell));
    }

    [Fact]
    public void MagnitudeLabel_NonMagicalSpell_RewritesDamageMrToDamage()
    {
        SpellFormulaInput spell = new()
        {
            MinBase = 1, MaxBase = 2,
            Abilities = [DamageMr(), NonMagical()],
        };
        Assert.Equal("Damage", SpellGrowthFormatter.MagnitudeLabel(spell));
    }

    [Fact]
    public void MagnitudeLabel_NoDamageOrHeal_FallsBackToMagnitude()
    {
        SpellFormulaInput spell = new() { MinBase = 1, MaxBase = 2, Abilities = [NotEvil()] };
        Assert.Equal("Magnitude", SpellGrowthFormatter.MagnitudeLabel(spell));
    }

    // ----- display level + duration -------------------------------------

    [Fact]
    public void DisplayLevel_NoCap_UsesReqLevelFlooredToOne()
    {
        Assert.Equal(5, SpellGrowthFormatter.DisplayLevel(new SpellFormulaInput { ReqLevel = 5 }));
        Assert.Equal(1, SpellGrowthFormatter.DisplayLevel(new SpellFormulaInput { ReqLevel = 0 }));
    }

    [Fact]
    public void DurationSeconds_ConvertsTicksToSeconds()
    {
        // Dur 10 + Fix(2.5 * 6) = 25 ticks at cap 6 → 25 * 3 = 75 seconds
        SpellFormulaInput spell = new()
        {
            Dur = 10, DurInc = 5, DurIncLVLs = 2, Cap = 6,
            Abilities = [Damage()],
        };
        Assert.Equal(75, SpellGrowthFormatter.DurationSeconds(spell));
    }

    [Fact]
    public void DurationSeconds_NoDuration_IsZero()
        => Assert.Equal(0, SpellGrowthFormatter.DurationSeconds(HolyForce()));
}
