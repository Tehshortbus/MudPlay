using System;
using FujinTerm.Game;
using FujinTerm.Game.Calculators;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Behavioural coverage for <see cref="MonsterMatchupCalculator"/>: the
/// player → monster DPS / rounds-to-kill projection, the monster → player
/// return-fire preview, the prot-ward gating on monster alignment, DR
/// flooring, and the unarmed / no-attack edge gates.
/// </summary>
public sealed class MonsterMatchupCalculatorTests
{
    private static PlayerMatchupProfile Player(
        int accuracy = 200, int avgDmg = 10, double swings = 2.0, bool hasWeapon = true,
        int ac = 60, int dodge = 0, int protEvil = 0, int protGood = 0, int dr = 0) =>
        new(RealmType.ParaMud, accuracy, avgDmg, swings, hasWeapon, ac, dodge, protEvil, protGood, dr);

    private static MonsterMatchupProfile Monster(
        int ac = 50, int dr = 2, int hp = 100, bool hasAttack = true,
        int attackAcc = 120, int avgAttack = 8, bool isEvil = false, bool isGood = false) =>
        new(ac, dr, hp, hasAttack, attackAcc, avgAttack, isEvil, isGood);

    [Fact]
    public void PlayerToMonster_MatchesHitFormula_AndProjectsDps()
    {
        PlayerMatchupProfile p = Player(accuracy: 200, avgDmg: 10, swings: 2.0, dr: 0);
        MonsterMatchupProfile m = Monster(ac: 50, dr: 2, hp: 100);

        int expectedHit = CombatCalculator.CalculateHitChance(
            attackerAccuracy: 200, defenderAC: 50, defenderDodge: 0,
            realmType: RealmType.ParaMud).OverallHitPercent;
        int expectedDmg = 10 - 2;
        double expectedDps = expectedHit / 100.0 * expectedDmg * 2.0;
        int expectedRounds = (int)Math.Ceiling(100 / expectedDps);

        MonsterMatchupResult r = MonsterMatchupCalculator.Compute(p, m);

        Assert.Equal(expectedHit, r.PlayerHitPercent);
        Assert.Equal(expectedDmg, r.PlayerDamagePerHit);
        Assert.Equal(expectedDps, r.PlayerDps, 5);
        Assert.Equal(expectedRounds, r.RoundsToKill);
        Assert.True(r.HasWeapon);
    }

    [Fact]
    public void PlayerDamage_FloorsAtZero_WhenDrExceedsAverage()
    {
        MonsterMatchupResult r = MonsterMatchupCalculator.Compute(
            Player(avgDmg: 3), Monster(dr: 10));

        Assert.Equal(0, r.PlayerDamagePerHit);
        Assert.Equal(0, r.PlayerDps);
        Assert.Equal(0, r.RoundsToKill); // zero DPS → not killable → renders as "—"
    }

    [Fact]
    public void Unarmed_YieldsNoDpsOrRounds()
    {
        MonsterMatchupResult r = MonsterMatchupCalculator.Compute(
            Player(hasWeapon: false, swings: 2.0), Monster());

        Assert.False(r.HasWeapon);
        Assert.Equal(0, r.PlayerDps);
        Assert.Equal(0, r.PlayerSwingsPerRound);
        Assert.Equal(0, r.RoundsToKill);
    }

    [Fact]
    public void MonsterWithoutPhysicalAttack_HasNoReturnPreview()
    {
        MonsterMatchupResult r = MonsterMatchupCalculator.Compute(
            Player(), Monster(hasAttack: false));

        Assert.False(r.MonsterHasPhysicalAttack);
        Assert.Equal(0, r.MonsterHitPercent);
        Assert.Equal(0, r.MonsterDamagePerHit);
    }

    [Fact]
    public void ProtEvil_AppliesOnlyWhenMonsterIsEvil()
    {
        PlayerMatchupProfile p = Player(ac: 60, protEvil: 40);

        MonsterMatchupResult evil = MonsterMatchupCalculator.Compute(p, Monster(isEvil: true));
        MonsterMatchupResult neutral = MonsterMatchupCalculator.Compute(p, Monster(isEvil: false));

        // The ward raises our effective defense against an evil monster, so its
        // hit chance must be no higher than against a neutral monster (and
        // strictly lower here, since 40 prot-evil meaningfully shifts defense).
        Assert.True(evil.MonsterHitPercent < neutral.MonsterHitPercent);
    }

    [Fact]
    public void ProtGood_IsIgnored_AgainstNonGoodMonster()
    {
        PlayerMatchupProfile withWard = Player(ac: 60, protGood: 40);
        PlayerMatchupProfile noWard = Player(ac: 60, protGood: 0);

        // Monster is neither good nor evil → the prot-good ward must not change
        // the monster's hit chance.
        int warded = MonsterMatchupCalculator.Compute(withWard, Monster(isGood: false)).MonsterHitPercent;
        int plain = MonsterMatchupCalculator.Compute(noWard, Monster(isGood: false)).MonsterHitPercent;

        Assert.Equal(plain, warded);
    }

    [Fact]
    public void MonsterDamage_SubtractsPlayerDamageResist()
    {
        MonsterMatchupResult r = MonsterMatchupCalculator.Compute(
            Player(dr: 3), Monster(avgAttack: 8));

        Assert.Equal(5, r.MonsterDamagePerHit);
    }
}
