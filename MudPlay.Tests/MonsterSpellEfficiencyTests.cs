using System.Collections.Generic;
using MudPlay.Game.Calculators;
using MudPlay.Game.Combat;
using Xunit;

namespace MudPlay.Tests;

// Monster Intel's spell mana-efficiency ranking — damage per mana, rounds-to-kill
// and total mana-to-kill against a target — plus the element code↔name round-trip
// used to render a monster's own resist profile.
public sealed class MonsterSpellEfficiencyTests
{
    private static readonly IReadOnlyDictionary<int, int> NoResists = new Dictionary<int, int>();

    [Fact]
    public void DamagePerMana_IsEffectiveOverManaPerRound()
    {
        var r = new SpellEffectivenessResult(
            "Magic Missile", "mmis", "Normal", EffectiveDamage: 40,
            ManaCostPerRound: 10, Eligible: true, BlockedReason: null);
        Assert.Equal(4.0, r.DamagePerMana, 5);
    }

    [Fact]
    public void DamagePerMana_ZeroMana_IsZero()
    {
        var r = new SpellEffectivenessResult(
            "Free", "free", "Normal", EffectiveDamage: 40,
            ManaCostPerRound: 0, Eligible: true, BlockedReason: null);
        Assert.Equal(0, r.DamagePerMana);
    }

    [Fact]
    public void RankAttackSpells_FillsKillEstimate_WhenHpSupplied()
    {
        var spells = new[]
        {
            new PlayerAttackSpell("Bolt", "bolt", ReqLevel: 1, AttType: 0,
                MaxDamagePerRound: 100, ManaCostPerRound: 20, Targets: 0, ManaCostPerCast: 10),
        };

        SpellEffectivenessResult r = MonsterMatchupCalculatorSpells
            .RankAttackSpells(spells, monsterSpellImmunity: 0, NoResists, monsterIsUndead: false, monsterHp: 250)[0];

        Assert.True(r.Eligible);
        Assert.Equal(100, r.EffectiveDamage);
        Assert.Equal(3, r.RoundsToKill);      // ceil(250 / 100)
        Assert.Equal(60, r.ManaToKill);       // 3 rounds × 20 mana/round
        Assert.Equal(5.0, r.DamagePerMana, 5); // 100 / 20
        Assert.Equal(10, r.ManaCostPerCast);
        Assert.True(r.HasKillEstimate);
    }

    [Fact]
    public void RankAttackSpells_NoHp_LeavesKillEstimateUnset()
    {
        var spells = new[]
        {
            new PlayerAttackSpell("Bolt", "bolt", 1, 0, MaxDamagePerRound: 100, ManaCostPerRound: 20),
        };
        SpellEffectivenessResult r = MonsterMatchupCalculatorSpells
            .RankAttackSpells(spells, 0, NoResists, false)[0];   // monsterHp defaults to 0

        Assert.Equal(0, r.RoundsToKill);
        Assert.False(r.HasKillEstimate);
    }

    [Fact]
    public void RankAttackSpells_SortsByEfficiency_NotRawDamage()
    {
        // Big hits harder (200 vs 60) but Cheap is far more mana-efficient
        // (6 dmg/mana vs 2), so Cheap ranks first.
        var spells = new[]
        {
            new PlayerAttackSpell("Big",   "big",   1, 0, MaxDamagePerRound: 200, ManaCostPerRound: 100),
            new PlayerAttackSpell("Cheap", "cheap", 1, 0, MaxDamagePerRound: 60,  ManaCostPerRound: 10),
        };
        IReadOnlyList<SpellEffectivenessResult> ranked =
            MonsterMatchupCalculatorSpells.RankAttackSpells(spells, 0, NoResists, false, monsterHp: 300);

        Assert.Equal("Cheap", ranked[0].Name);
        Assert.Equal("Big", ranked[1].Name);
    }

    [Theory]
    [InlineData(3, "Cold")]
    [InlineData(5, "Fire")]
    [InlineData(65, "Stone")]
    [InlineData(66, "Lightning")]
    [InlineData(147, "Water")]
    public void ElementName_RoundTripsWithCode(int code, string name)
    {
        Assert.Equal(name, ElementalResistIndex.NameForCode(code));
        Assert.Equal(code, ElementalResistIndex.CodeForName(name));
    }

    [Fact]
    public void ElementName_UnknownCode_IsNull()
        => Assert.Null(ElementalResistIndex.NameForCode(999));
}
