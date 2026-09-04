using MudPlay.Game.Calculators;
using MudPlay.Game.Combat;
using Xunit;

namespace MudPlay.Tests;

// The damage-per-minute helpers for Monster Intel: the rollover-averaged (fractional)
// monster swing rate that DPM rides, and the element code↔name round-trip used to
// render a monster's own resist profile.
public sealed class MonsterDpmTests
{
    [Fact]
    public void RoundsPerMinute_IsTwelve()
        => Assert.Equal(12, MonsterMatchupCalculator.RoundsPerMinute);

    [Fact]
    public void AverageSwings_IsFractional_WhereTheFloorUndercounts()
    {
        // Budget 1000, attack costs 300: a single round only lands 3 swings (floor),
        // but the 100 leftover rolls over, so the minute-long average is 3.33/round.
        Assert.Equal(3, MonsterMatchupCalculator.MonsterSwingsPerRound(1000, 300));
        Assert.Equal(1000.0 / 300.0, MonsterMatchupCalculator.AverageMonsterSwingsPerRound(1000, 300), 5);
    }

    [Fact]
    public void AverageSwings_EvenBudget_MatchesTheFloor()
        => Assert.Equal(5.0, MonsterMatchupCalculator.AverageMonsterSwingsPerRound(1000, 200), 5);

    [Theory]
    [InlineData(0, 300)]
    [InlineData(1000, 0)]
    public void AverageSwings_MissingEnergy_FallsBackToOne(int budget, int cost)
        => Assert.Equal(1.0, MonsterMatchupCalculator.AverageMonsterSwingsPerRound(budget, cost), 5);

    [Fact]
    public void Dpm_Composition_HitTimesDamageTimesSwingsTimesTwelve()
    {
        // 50% to hit · 10 dmg/hit · 1000/300 avg swings · 12 rounds = 200 dmg/min.
        double swings = MonsterMatchupCalculator.AverageMonsterSwingsPerRound(1000, 300);
        double dpm = 50 / 100.0 * 10 * swings * MonsterMatchupCalculator.RoundsPerMinute;
        Assert.Equal(200.0, dpm, 5);
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

    [Fact]
    public void SpellDamagePerMinute_IsEffectivePerRoundTimesTwelve()
    {
        // EffectiveDamage is the resist-adjusted per-round figure, so /min is ×12.
        var spell = new SpellEffectivenessResult(
            "Magic Missile", "mmis", "Normal", EffectiveDamage: 40,
            ManaCostPerRound: 12, Eligible: true, BlockedReason: null);
        Assert.Equal(480, spell.DamagePerMinute);
    }
}
