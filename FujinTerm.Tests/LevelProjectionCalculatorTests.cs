using System.Collections.Generic;
using FujinTerm.Game;
using FujinTerm.Game.Calculators;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 10.6 — <see cref="LevelProjectionCalculator"/> composes the experience +
/// character calculators into the per-level grid rows. These pin the
/// composition (each cell matches its source formula) and the range / caster
/// branching; the underlying formulas are covered by their own tests.
/// </summary>
public sealed class LevelProjectionCalculatorTests
{
    // A Warrior-ish non-caster and a Mage-ish caster, Human race (HPPerLVL 0).
    private const int Chart = 100;          // CalcExpChart(0, 0) for stock Warrior/Human
    private const int Health = 60;
    private const int MinHits = 6, MaxHits = 4, RaceHpPerLevel = 0;

    [Fact]
    public void ProjectLevel_TotalXpIsCumulativeThreshold()
    {
        const int level = 12;
        LevelProjection p = LevelProjectionCalculator.ProjectLevel(
            level, Chart, Health, intellect: 40, willpower: 40, charm: 40,
            MinHits, MaxHits, RaceHpPerLevel, mageryType: 0, mageryLevel: 0, RealmType.Stock);

        Assert.Equal(ExperienceTableCalculator.CalcExpNeeded(level, Chart, RealmType.Stock), p.TotalXp);
    }

    [Fact]
    public void ProjectLevel_HumanWarriorThresholds()
    {
        // Human Warrior chart = (0+100)+0 = 100.
        LevelProjection l4 = LevelProjectionCalculator.ProjectLevel(
            level: 4, chart: 100, Health, 40, 40, 40,
            MinHits, MaxHits, RaceHpPerLevel, 0, 0, RealmType.Stock);
        LevelProjection l5 = LevelProjectionCalculator.ProjectLevel(
            level: 5, chart: 100, Health, 40, 40, 40,
            MinHits, MaxHits, RaceHpPerLevel, 0, 0, RealmType.Stock);

        Assert.Equal(3666, l4.TotalXp);
        Assert.Equal(6721, l5.TotalXp);
    }

    // ----- row: "exp remaining to reach this level" from current exp ------

    [Fact]
    public void Row_ExpToLevel_IsRemainingFromCurrentExp()
    {
        var p = new LevelProjection(Level: 4, TotalXp: 10000, HpMin: 1, HpMax: 1, HpRegen: 1, Mana: 0, MpRegen: 0);
        var row = new FujinTerm.ViewModels.CharacterWorkshop.LevelProjectionRow(
            p, currentExp: 7000, isCurrentLevel: false, isCaster: false);

        Assert.Equal("3,000", row.ExpToLevel);   // 10000 - 7000
        Assert.Equal("10,000", row.TotalXp);
    }

    [Fact]
    public void Row_ExpToLevel_ZeroWhenAlreadyReached()
    {
        var p = new LevelProjection(Level: 3, TotalXp: 5000, HpMin: 1, HpMax: 1, HpRegen: 1, Mana: 0, MpRegen: 0);
        var row = new FujinTerm.ViewModels.CharacterWorkshop.LevelProjectionRow(
            p, currentExp: 7723, isCurrentLevel: false, isCaster: false);

        Assert.Equal("0", row.ExpToLevel);   // already have enough → 0 remaining
    }

    [Fact]
    public void ProjectLevel_HpCellsBracketTheRolls()
    {
        const int level = 20;
        LevelProjection p = LevelProjectionCalculator.ProjectLevel(
            level, Chart, Health, 40, 40, 40,
            MinHits, MaxHits, RaceHpPerLevel, mageryType: 0, mageryLevel: 0, RealmType.Stock);

        int expectMin = CharacterCalculator.CalcMaxHp(Health, level, MinHits, MaxHits, RaceHpPerLevel, 0, HpRollMode.Min);
        int expectMax = CharacterCalculator.CalcMaxHp(Health, level, MinHits, MaxHits, RaceHpPerLevel, 0, HpRollMode.Max);

        Assert.Equal(expectMin, p.HpMin);
        Assert.Equal(expectMax, p.HpMax);
        Assert.True(p.HpMin <= p.HpMax);
        Assert.Equal(
            CharacterCalculator.CalcHpRegen(level, Health, 0, false, RealmType.Stock),
            p.HpRegen);
    }

    [Fact]
    public void ProjectLevel_NonCaster_HasNoMana()
    {
        LevelProjection p = LevelProjectionCalculator.ProjectLevel(
            level: 15, Chart, Health, 40, 40, 40,
            MinHits, MaxHits, RaceHpPerLevel, mageryType: 0, mageryLevel: 0, RealmType.Stock);

        Assert.Equal(0, p.Mana);
        Assert.Equal(0, p.MpRegen);
    }

    [Fact]
    public void ProjectLevel_Caster_HasMana()
    {
        const int level = 15, mageryLevel = 4;
        LevelProjection p = LevelProjectionCalculator.ProjectLevel(
            level, Chart, Health, intellect: 80, willpower: 40, charm: 40,
            MinHits, MaxHits, RaceHpPerLevel, mageryType: 1, mageryLevel, RealmType.Stock);

        Assert.Equal(CharacterCalculator.CalcMaxMana(mageryLevel, level, 0), p.Mana);
        Assert.True(p.Mana > 0);
        Assert.Equal(
            CharacterCalculator.CalcManaRegen(level, 80, 40, 40, 1, mageryLevel, 0, false, RealmType.Stock),
            p.MpRegen);
    }

    [Fact]
    public void ProjectLevel_Mystic_UsesKaiNotMana()
    {
        const int level = 30;
        LevelProjection p = LevelProjectionCalculator.ProjectLevel(
            level, Chart, Health, 40, 40, 40,
            MinHits, MaxHits, RaceHpPerLevel, mageryType: 5, mageryLevel: 0, RealmType.Stock);

        // Mystic Kai ≈ level - 1, not the mana formula.
        Assert.Equal(CharacterCalculator.CalcMaxKai(level), p.Mana);
        Assert.Equal(level - 1, p.Mana);
    }

    [Fact]
    public void Project_ReturnsAscendingRowsAcrossRange()
    {
        IReadOnlyList<LevelProjection> rows = LevelProjectionCalculator.Project(
            fromLevel: 5, toLevel: 10, Chart, Health, 40, 40, 40,
            MinHits, MaxHits, RaceHpPerLevel, mageryType: 0, mageryLevel: 0, RealmType.Stock);

        Assert.Equal(6, rows.Count);
        Assert.Equal(5, rows[0].Level);
        Assert.Equal(10, rows[^1].Level);
        for (int i = 1; i < rows.Count; i++)
            Assert.Equal(rows[i - 1].Level + 1, rows[i].Level);
    }

    [Fact]
    public void Project_ClampsFromBelowOne_AndCollapsesInvertedRange()
    {
        IReadOnlyList<LevelProjection> clamped = LevelProjectionCalculator.Project(
            fromLevel: -3, toLevel: 2, Chart, Health, 40, 40, 40,
            MinHits, MaxHits, RaceHpPerLevel, 0, 0, RealmType.Stock);
        Assert.Equal(1, clamped[0].Level);
        Assert.Equal(2, clamped[^1].Level);

        IReadOnlyList<LevelProjection> inverted = LevelProjectionCalculator.Project(
            fromLevel: 10, toLevel: 3, Chart, Health, 40, 40, 40,
            MinHits, MaxHits, RaceHpPerLevel, 0, 0, RealmType.Stock);
        Assert.Single(inverted);
        Assert.Equal(10, inverted[0].Level);
    }
}
