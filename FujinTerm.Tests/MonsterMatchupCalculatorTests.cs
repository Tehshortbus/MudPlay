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
        int ac = 50, int dr = 2, int hp = 100, int dodge = 0, bool hasAttack = true,
        int attackAcc = 120, int avgAttack = 8, bool isEvil = false, bool isGood = false) =>
        new(ac, dr, hp, dodge, hasAttack, attackAcc, avgAttack, isEvil, isGood);

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
    public void MonsterDodge_LowersPlayerHitChance()
    {
        // A monster's Dodge ability (e.g. Lord of the Hunt's 70) feeds the
        // player → monster hit calc as the defender's dodge, so raising it can
        // only lower our hit chance — never raise it.
        PlayerMatchupProfile p = Player(accuracy: 200, avgDmg: 10, swings: 2.0, dr: 0);
        MonsterMatchupProfile noDodge = Monster(ac: 50, dr: 2, hp: 100, dodge: 0);
        MonsterMatchupProfile withDodge = Monster(ac: 50, dr: 2, hp: 100, dodge: 70);

        int expected = CombatCalculator.CalculateHitChance(
            attackerAccuracy: 200, defenderAC: 50, defenderDodge: 70,
            realmType: RealmType.ParaMud).OverallHitPercent;

        MonsterMatchupResult rNo = MonsterMatchupCalculator.Compute(p, noDodge);
        MonsterMatchupResult rDodge = MonsterMatchupCalculator.Compute(p, withDodge);

        Assert.Equal(expected, rDodge.PlayerHitPercent);
        Assert.True(rDodge.PlayerHitPercent <= rNo.PlayerHitPercent);
    }

    [Fact]
    public void CritChance_RaisesDps_ButLeavesPerHitDisplayUnchanged()
    {
        PlayerMatchupProfile noCrit = Player(accuracy: 200, avgDmg: 20, swings: 2.0, dr: 0);
        PlayerMatchupProfile withCrit = noCrit with { CritChancePercent = 50, AvgCritDamage = 60 };
        MonsterMatchupProfile m = Monster(ac: 50, dr: 0, hp: 1000);

        MonsterMatchupResult rNo = MonsterMatchupCalculator.Compute(noCrit, m);
        MonsterMatchupResult rCrit = MonsterMatchupCalculator.Compute(withCrit, m);

        // The per-hit display stays the non-crit average; only DPS folds in crits.
        Assert.Equal(rNo.PlayerDamagePerHit, rCrit.PlayerDamagePerHit);
        // Effective per-swing = 0.5*20 + 0.5*60 = 40 (vs 20) → exactly double the DPS.
        Assert.Equal(rNo.PlayerDps * 2.0, rCrit.PlayerDps, 5);
    }

    [Fact]
    public void CritDamage_SubtractsMonsterDr_LikeNormalHits()
    {
        // Normal 20 - DR 10 = 10; crit 60 - DR 10 = 50; at 50% crit the effective
        // per-swing is 0.5*10 + 0.5*50 = 30.
        PlayerMatchupProfile p = Player(accuracy: 9999, avgDmg: 20, swings: 1.0, dr: 0)
            with { CritChancePercent = 50, AvgCritDamage = 60 };
        MonsterMatchupProfile m = Monster(ac: 1, dr: 10, hp: 1000);

        MonsterMatchupResult r = MonsterMatchupCalculator.Compute(p, m);

        double hit = CombatCalculator.CalculateHitChance(
            attackerAccuracy: 9999, defenderAC: 1, defenderDodge: 0,
            realmType: RealmType.ParaMud).OverallHitPercent / 100.0;
        Assert.Equal(hit * 30.0 * 1.0, r.PlayerDps, 5);
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
