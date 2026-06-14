using FujinTerm.Game;
using FujinTerm.Game.Calculators;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Realm-branch coverage for <see cref="CharacterCalculator"/> — the formulas
/// compile-time checking can't validate. Asserts the Stock vs ParaMUD
/// divergences (CP-cost cap, regen divisors) plus the shared CP/HP/mana math.
/// </summary>
public sealed class CharacterCalculatorTests
{
    // ----- CP --------------------------------------------------------------

    [Theory]
    [InlineData(1, 10)]    // (1/10)*5+10
    [InlineData(9, 10)]
    [InlineData(10, 15)]   // (10/10)*5+10
    [InlineData(19, 15)]
    [InlineData(20, 20)]
    [InlineData(0, 0)]     // below level 1
    [InlineData(-5, 0)]
    public void CalcCpGainedAtLevel_MatchesStepFormula(int level, int expected)
    {
        Assert.Equal(expected, CharacterCalculator.CalcCpGainedAtLevel(level));
    }

    [Fact]
    public void CalcTotalCpAtLevel_SumsExclusiveUpperStep_PlusBase()
    {
        // Levels 1..9 each give 10 → 90, plus baseCP 5.
        Assert.Equal(95, CharacterCalculator.CalcTotalCpAtLevel(10, baseCP: 5));
    }

    [Fact]
    public void CalcCpCostForStatPoint_ScalesEveryTenAboveRaceMin()
    {
        // delta 0..9 → cost 1; delta 10..19 → cost 2.
        Assert.Equal(1, CharacterCalculator.CalcCpCostForStatPoint(50, 55));
        Assert.Equal(2, CharacterCalculator.CalcCpCostForStatPoint(50, 60));
        Assert.Equal(3, CharacterCalculator.CalcCpCostForStatPoint(50, 70));
    }

    [Fact]
    public void CalcCpCostForStatPoint_BelowRaceMin_Floors()
    {
        Assert.Equal(1, CharacterCalculator.CalcCpCostForStatPoint(50, 40));
    }

    [Fact]
    public void CalcCpCostForStatPoint_StockCapsCostAtTen_ParaMudUncapped()
    {
        // delta 200 → raw cost (200/10)+1 = 21.
        int raceMin = 0, stat = 200;
        Assert.Equal(10, CharacterCalculator.CalcCpCostForStatPoint(raceMin, stat, RealmType.Stock));
        Assert.Equal(21, CharacterCalculator.CalcCpCostForStatPoint(raceMin, stat, RealmType.ParaMud));
    }

    // ----- HP regen --------------------------------------------------------

    [Fact]
    public void CalcHpRegen_DivisorBranchesByRealm()
    {
        // (level+20)*health/divisor; ParaMUD 500, Stock 750.
        // level 30, health 200 → (50*200)=10000.
        Assert.Equal(20, CharacterCalculator.CalcHpRegen(30, 200, 0, false, RealmType.ParaMud)); // 10000/500
        Assert.Equal(13, CharacterCalculator.CalcHpRegen(30, 200, 0, false, RealmType.Stock));   // 10000/750
    }

    [Fact]
    public void CalcHpRegen_RestingTriples_ThenScalesByPercent()
    {
        // base 20, resting ×3 = 60, +50% = 90.
        Assert.Equal(90, CharacterCalculator.CalcHpRegen(30, 200, 50, true, RealmType.ParaMud));
    }

    [Fact]
    public void CalcHpRegen_FloorsAtOne()
    {
        Assert.Equal(1, CharacterCalculator.CalcHpRegen(1, 1, 0, false, RealmType.ParaMud));
    }

    // ----- Mana / Kai ------------------------------------------------------

    [Fact]
    public void CalcMaxMana_NonCasterReturnsZero()
    {
        Assert.Equal(0, CharacterCalculator.CalcMaxMana(0, 50, 100));
    }

    [Fact]
    public void CalcMaxMana_CasterFormula()
    {
        // (mageryLevel*level*2)+6+plusMaxMana.
        Assert.Equal(206, CharacterCalculator.CalcMaxMana(2, 50, 0)); // 200+6
        Assert.Equal(256, CharacterCalculator.CalcMaxMana(2, 50, 50));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(82, 81)]
    public void CalcMaxKai_IsLevelMinusOne(int level, int expected)
    {
        Assert.Equal(expected, CharacterCalculator.CalcMaxKai(level));
    }

    [Fact]
    public void CalcManaRegen_NonCasterReturnsZero()
    {
        Assert.Equal(0, CharacterCalculator.CalcManaRegen(50, 100, 100, 100, 0, 5, 0, false, RealmType.ParaMud));
    }

    [Fact]
    public void CalcManaRegen_MeditatingExitsBeforeEquipmentModifier()
    {
        // Mage (type 1): ((level+20)*INT*(mageryLevel+2))/1650.
        // level 30, INT 100, mageryLevel 5 → (50*100*7)/1650 = 21.
        int meditating = CharacterCalculator.CalcManaRegen(30, 100, 0, 0, 1, 5, 999, true, RealmType.ParaMud);
        Assert.Equal(21, meditating);
    }

    [Fact]
    public void CalcManaRegen_RealmBranchesOnEquipmentModifier()
    {
        // Core 21 (as above). ParaMUD: regen + mpRegen%*regen/100.
        // Stock: (mpRegen%+100)*regen/100.
        int para = CharacterCalculator.CalcManaRegen(30, 100, 0, 0, 1, 5, 100, false, RealmType.ParaMud);
        int stock = CharacterCalculator.CalcManaRegen(30, 100, 0, 0, 1, 5, 100, false, RealmType.Stock);
        Assert.Equal(42, para);  // 21 + 21
        Assert.Equal(42, stock); // 200*21/100
    }

    [Fact]
    public void CalcManaRegen_KaiFixedRate()
    {
        // Magery type 5 → (mpRegen%+100)*1/100.
        Assert.Equal(2, CharacterCalculator.CalcManaRegen(50, 0, 0, 0, 5, 0, 100, false, RealmType.ParaMud));
    }

    // ----- HP --------------------------------------------------------------

    [Fact]
    public void CalcMaxHp_RollModeBracketsRandomPortion()
    {
        // range = maxHitsPerLevel = 10. base without random:
        // (health/2)+(level*minHits)+((health-50)*level/16).
        // health 100, level 10, minHits 5 → 50+50+((50*10)/16=31) = 131.
        int min = CharacterCalculator.CalcMaxHp(100, 10, 5, 10, 0, 0, HpRollMode.Min);
        int max = CharacterCalculator.CalcMaxHp(100, 10, 5, 10, 0, 0, HpRollMode.Max);
        Assert.Equal(131 + 10, min);       // range only
        Assert.Equal(131 + 100, max);      // range*level
        Assert.True(max > min);
    }

    [Fact]
    public void CalcMaxHp_AddsRaceAndPlusMaxHp()
    {
        int withExtras = CharacterCalculator.CalcMaxHp(100, 10, 5, 10, 2, 50, HpRollMode.Min);
        int without = CharacterCalculator.CalcMaxHp(100, 10, 5, 10, 0, 0, HpRollMode.Min);
        Assert.Equal(without + (2 * 10) + 50, withExtras);
    }
}
