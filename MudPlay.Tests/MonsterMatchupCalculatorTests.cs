using System;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using Xunit;

namespace MudPlay.Tests;

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

// Pins MonsterMatchupCalculatorSpells — the Monster Intel "Your Matchup"
// panel's spell-vs-monster ranking: the weapon-vs-Magical eligibility gate and
// RankAttackSpells' SpellImmu / elemental-resist logic.
public sealed class MonsterMatchupCalculatorSpellsTests
{
    private static PlayerAttackSpell Spell(
        string name = "fireball", string shortCode = "fire", int reqLevel = 10,
        int attType = 1, long maxDmg = 100, long mana = 20,
        bool undeadOnly = false, bool livingOnly = false) =>
        new(name, shortCode, reqLevel, attType, maxDmg, mana, undeadOnly, livingOnly);

    [Theory]
    [InlineData(5, 3, true)]
    [InlineData(3, 5, false)]
    [InlineData(4, 4, true)]
    public void WeaponMeetsMagical_ComparesHitMagicToMonsterMagical(int weaponHitMagic, int monsterMagical, bool expected)
        => Assert.Equal(expected, MonsterMatchupCalculatorSpells.WeaponMeetsMagical(weaponHitMagic, monsterMagical));

    // Backs Monster Intel's "Hits You %" column / Safe filter — every catalog
    // row needs this, not just one picked monster, so it's a standalone
    // function rather than folded into Compute().
    [Fact]
    public void IncomingHitPercent_NoPhysicalAccuracy_ReturnsNull()
    {
        Assert.Null(MonsterMatchupCalculatorSpells.IncomingHitPercent(
            physicalAccuracy: null, alignment: 0,
            defenderAc: 50, defenderDodge: 10, protEvil: 20, protGood: 20,
            realm: RealmType.ParaMud));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(6)]
    public void IncomingHitPercent_EvilAlignment_AppliesProtEvilNotProtGood(int evilAlign)
    {
        int withProtEvil = MonsterMatchupCalculatorSpells.IncomingHitPercent(
            (140, 140), evilAlign, defenderAc: 60, defenderDodge: 0, protEvil: 20, protGood: 0, RealmType.ParaMud)!.Value;
        int noWard = MonsterMatchupCalculatorSpells.IncomingHitPercent(
            (140, 140), evilAlign, defenderAc: 60, defenderDodge: 0, protEvil: 0, protGood: 0, RealmType.ParaMud)!.Value;
        int protGoodOnly = MonsterMatchupCalculatorSpells.IncomingHitPercent(
            (140, 140), evilAlign, defenderAc: 60, defenderDodge: 0, protEvil: 0, protGood: 20, RealmType.ParaMud)!.Value;

        Assert.True(withProtEvil < noWard);
        Assert.Equal(noWard, protGoodOnly);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void IncomingHitPercent_GoodAlignment_AppliesProtGoodNotProtEvil(int goodAlign)
    {
        int withProtGood = MonsterMatchupCalculatorSpells.IncomingHitPercent(
            (140, 140), goodAlign, defenderAc: 60, defenderDodge: 0, protEvil: 0, protGood: 20, RealmType.ParaMud)!.Value;
        int noWard = MonsterMatchupCalculatorSpells.IncomingHitPercent(
            (140, 140), goodAlign, defenderAc: 60, defenderDodge: 0, protEvil: 0, protGood: 0, RealmType.ParaMud)!.Value;
        int protEvilOnly = MonsterMatchupCalculatorSpells.IncomingHitPercent(
            (140, 140), goodAlign, defenderAc: 60, defenderDodge: 0, protEvil: 20, protGood: 0, RealmType.ParaMud)!.Value;

        Assert.True(withProtGood < noWard);
        Assert.Equal(noWard, protEvilOnly);
    }

    [Fact]
    public void IncomingHitPercent_NeutralAlignment_IgnoresBothWards()
    {
        int withWards = MonsterMatchupCalculatorSpells.IncomingHitPercent(
            (120, 120), alignment: 3, defenderAc: 100, defenderDodge: 0, protEvil: 50, protGood: 50, RealmType.ParaMud)!.Value;
        int noWards = MonsterMatchupCalculatorSpells.IncomingHitPercent(
            (120, 120), alignment: 3, defenderAc: 100, defenderDodge: 0, protEvil: 0, protGood: 0, RealmType.ParaMud)!.Value;

        Assert.Equal(noWards, withWards);
    }

    // Monster Intel's defense simulator feeds a raw Vile Ward through to
    // CombatCalculator's AdjustVileWard: it lowers an EVIL monster's hit chance
    // (scaled by the defender's own evil tier), does NOTHING versus a neutral
    // monster (evil-only ward), and the higher the alignment tier the bigger the
    // AC it converts to (villain/fiend > outlaw/criminal).
    [Fact]
    public void IncomingHitPercent_VileWard_EvilOnly_ScalesWithEvilTier()
    {
        int noVile = MonsterMatchupCalculatorSpells.IncomingHitPercent(
            (140, 140), alignment: 1, defenderAc: 60, defenderDodge: 0, protEvil: 0, protGood: 0,
            RealmType.ParaMud, hasShadow: false, vileWard: 500, defenderEvil: EvilLevel.Saint)!.Value;
        int halfTier = MonsterMatchupCalculatorSpells.IncomingHitPercent(
            (140, 140), alignment: 1, defenderAc: 60, defenderDodge: 0, protEvil: 0, protGood: 0,
            RealmType.ParaMud, hasShadow: false, vileWard: 500, defenderEvil: EvilLevel.Criminal)!.Value;
        int fullTier = MonsterMatchupCalculatorSpells.IncomingHitPercent(
            (140, 140), alignment: 1, defenderAc: 60, defenderDodge: 0, protEvil: 0, protGood: 0,
            RealmType.ParaMud, hasShadow: false, vileWard: 500, defenderEvil: EvilLevel.Fiend)!.Value;
        int neutralFull = MonsterMatchupCalculatorSpells.IncomingHitPercent(
            (140, 140), alignment: 3, defenderAc: 60, defenderDodge: 0, protEvil: 0, protGood: 0,
            RealmType.ParaMud, hasShadow: false, vileWard: 500, defenderEvil: EvilLevel.Fiend)!.Value;
        int neutralNone = MonsterMatchupCalculatorSpells.IncomingHitPercent(
            (140, 140), alignment: 3, defenderAc: 60, defenderDodge: 0, protEvil: 0, protGood: 0,
            RealmType.ParaMud, hasShadow: false, vileWard: 0, defenderEvil: EvilLevel.Fiend)!.Value;

        Assert.True(halfTier < noVile);        // "not evil" tier converts nothing; Criminal halves the ward to AC
        Assert.True(fullTier < halfTier);      // Fiend converts the full ward → more AC → fewer hits
        Assert.Equal(neutralNone, neutralFull); // evil-only: no effect on a neutral monster
    }

    // Regression: an earlier version of IncomingHitPercent never passed
    // hasShadow through to CombatCalculator, silently ignoring the flat
    // +10 AC Shadow (Abil 9) grants against every attacker regardless of
    // alignment (unlike Prot Evil/Good, which are alignment-conditional).
    // Caught via a live character's own @st readout: AC vs Evil and AC vs
    // Good both included the same Shadow bonus on top of bare AC.
    [Fact]
    public void IncomingHitPercent_HasShadow_LowersHitChance()
    {
        int withShadow = MonsterMatchupCalculatorSpells.IncomingHitPercent(
            (140, 140), alignment: 2, defenderAc: 60, defenderDodge: 0, protEvil: 0, protGood: 0,
            realm: RealmType.ParaMud, hasShadow: true)!.Value;
        int noShadow = MonsterMatchupCalculatorSpells.IncomingHitPercent(
            (140, 140), alignment: 2, defenderAc: 60, defenderDodge: 0, protEvil: 0, protGood: 0,
            realm: RealmType.ParaMud, hasShadow: false)!.Value;

        Assert.True(withShadow < noShadow);
    }

    [Fact]
    public void RankAttackSpells_BelowSpellImmu_IsBlockedWithReason()
    {
        var result = MonsterMatchupCalculatorSpells.RankAttackSpells(
            new[] { Spell(reqLevel: 5) }, monsterSpellImmunity: 10,
            new Dictionary<int, int>());

        SpellEffectivenessResult r = Assert.Single(result);
        Assert.False(r.Eligible);
        Assert.Equal(0, r.EffectiveDamage);
        Assert.Contains("immune", r.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RankAttackSpells_ExactlyMeetsSpellImmu_IsEligible()
    {
        var result = MonsterMatchupCalculatorSpells.RankAttackSpells(
            new[] { Spell(reqLevel: 10) }, monsterSpellImmunity: 10,
            new Dictionary<int, int>());

        Assert.True(Assert.Single(result).Eligible);
    }

    [Fact]
    public void RankAttackSpells_FullyResisted_IsBlocked()
    {
        // AttType 1 = Fire → resist code 5.
        var result = MonsterMatchupCalculatorSpells.RankAttackSpells(
            new[] { Spell(attType: 1, maxDmg: 100) }, monsterSpellImmunity: 0,
            new Dictionary<int, int> { [5] = 100 });

        SpellEffectivenessResult r = Assert.Single(result);
        Assert.False(r.Eligible);
        Assert.Contains("resisted", r.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RankAttackSpells_OverHundredResist_HealsInsteadOfBlocking()
    {
        var result = MonsterMatchupCalculatorSpells.RankAttackSpells(
            new[] { Spell(attType: 1, maxDmg: 100) }, monsterSpellImmunity: 0,
            new Dictionary<int, int> { [5] = 150 });

        SpellEffectivenessResult r = Assert.Single(result);
        Assert.False(r.Eligible);
        Assert.Contains("heals", r.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RankAttackSpells_PartialResist_ScalesEffectiveDamage()
    {
        var result = MonsterMatchupCalculatorSpells.RankAttackSpells(
            new[] { Spell(attType: 1, maxDmg: 100) }, monsterSpellImmunity: 0,
            new Dictionary<int, int> { [5] = 25 });

        SpellEffectivenessResult r = Assert.Single(result);
        Assert.True(r.Eligible);
        Assert.Equal(75, r.EffectiveDamage);
    }

    [Fact]
    public void RankAttackSpells_NoResistData_FullDamageThrough()
    {
        var result = MonsterMatchupCalculatorSpells.RankAttackSpells(
            new[] { Spell(attType: 1, maxDmg: 100) }, monsterSpellImmunity: 0,
            new Dictionary<int, int>());

        Assert.Equal(100, Assert.Single(result).EffectiveDamage);
    }

    [Fact]
    public void RankAttackSpells_SortsEligibleFirstThenByEffectiveDamageDescending()
    {
        var spells = new[]
        {
            Spell(name: "weak",    attType: 1, maxDmg: 20),   // eligible, low damage
            Spell(name: "blocked", reqLevel: 1),               // blocked (immu 10)
            Spell(name: "strong",  attType: 1, maxDmg: 90),   // eligible, high damage
        };
        var result = MonsterMatchupCalculatorSpells.RankAttackSpells(
            spells, monsterSpellImmunity: 10, new Dictionary<int, int>());

        Assert.Equal(new[] { "strong", "weak", "blocked" }, result.Select(r => r.Name));
    }

    [Fact]
    public void RankAttackSpells_ElementName_MatchesLookupEnumsFormatting()
    {
        var result = MonsterMatchupCalculatorSpells.RankAttackSpells(
            new[] { Spell(attType: 1) }, monsterSpellImmunity: 0, new Dictionary<int, int>());

        Assert.Equal("Fire", Assert.Single(result).Element);
    }

    // Abil 23 (AffectsUndeadOnly) / Abil 108 (AffectsLivingOnly) — a caster-
    // side target-type gate independent of SpellImmu/resist.
    [Fact]
    public void RankAttackSpells_UndeadOnlySpell_BlockedAgainstLivingMonster()
    {
        var result = MonsterMatchupCalculatorSpells.RankAttackSpells(
            new[] { Spell(undeadOnly: true) }, monsterSpellImmunity: 0,
            new Dictionary<int, int>(), monsterIsUndead: false);

        SpellEffectivenessResult r = Assert.Single(result);
        Assert.False(r.Eligible);
        Assert.Contains("undead only", r.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RankAttackSpells_UndeadOnlySpell_EligibleAgainstUndeadMonster()
    {
        var result = MonsterMatchupCalculatorSpells.RankAttackSpells(
            new[] { Spell(undeadOnly: true) }, monsterSpellImmunity: 0,
            new Dictionary<int, int>(), monsterIsUndead: true);

        Assert.True(Assert.Single(result).Eligible);
    }

    [Fact]
    public void RankAttackSpells_LivingOnlySpell_BlockedAgainstUndeadMonster()
    {
        var result = MonsterMatchupCalculatorSpells.RankAttackSpells(
            new[] { Spell(livingOnly: true) }, monsterSpellImmunity: 0,
            new Dictionary<int, int>(), monsterIsUndead: true);

        SpellEffectivenessResult r = Assert.Single(result);
        Assert.False(r.Eligible);
        Assert.Contains("living only", r.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RankAttackSpells_LivingOnlySpell_EligibleAgainstLivingMonster()
    {
        var result = MonsterMatchupCalculatorSpells.RankAttackSpells(
            new[] { Spell(livingOnly: true) }, monsterSpellImmunity: 0,
            new Dictionary<int, int>(), monsterIsUndead: false);

        Assert.True(Assert.Single(result).Eligible);
    }

    [Fact]
    public void RankAttackSpells_NoTargetRestriction_DefaultsEligibleEitherWay()
    {
        var result = MonsterMatchupCalculatorSpells.RankAttackSpells(
            new[] { Spell() }, monsterSpellImmunity: 0,
            new Dictionary<int, int>(), monsterIsUndead: true);

        Assert.True(Assert.Single(result).Eligible);
    }

    // ----- Weighted "Hits You %" across all physical attacks -----

    [Fact]
    public void WeightedIncomingHitPercent_NullForNoAttacks()
        => Assert.Null(MonsterMatchupCalculatorSpells.WeightedIncomingHitPercent(
            Array.Empty<(int, double)>(), accuracyDelta: 0, alignment: 3,
            defenderAc: 30, defenderDodge: 0, protEvil: 0, protGood: 0, realm: RealmType.Stock));

    [Fact]
    public void WeightedIncomingHitPercent_SingleAttack_EqualsThatAttack()
    {
        int single = MonsterMatchupCalculatorSpells.AttackHitPercent(
            60, 3, 40, 0, 0, 0, RealmType.Stock);
        int? weighted = MonsterMatchupCalculatorSpells.WeightedIncomingHitPercent(
            new (int, double)[] { (60, 100.0) }, 0, 3, 40, 0, 0, 0, RealmType.Stock);
        Assert.Equal(single, weighted);
    }

    [Fact]
    public void WeightedIncomingHitPercent_BlendsByUseWeight()
    {
        int hitA = MonsterMatchupCalculatorSpells.AttackHitPercent(80, 3, 30, 0, 0, 0, RealmType.Stock);
        int hitB = MonsterMatchupCalculatorSpells.AttackHitPercent(40, 3, 30, 0, 0, 0, RealmType.Stock);
        int expected = (int)Math.Round((75.0 * hitA + 25.0 * hitB) / 100.0);
        int? actual = MonsterMatchupCalculatorSpells.WeightedIncomingHitPercent(
            new (int, double)[] { (80, 75.0), (40, 25.0) }, 0, 3, 30, 0, 0, 0, RealmType.Stock);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void WeightedIncomingHitPercent_AllZeroWeights_FallsBackToMean()
    {
        int hitA = MonsterMatchupCalculatorSpells.AttackHitPercent(80, 3, 30, 0, 0, 0, RealmType.Stock);
        int hitB = MonsterMatchupCalculatorSpells.AttackHitPercent(40, 3, 30, 0, 0, 0, RealmType.Stock);
        int? actual = MonsterMatchupCalculatorSpells.WeightedIncomingHitPercent(
            new (int, double)[] { (80, 0.0), (40, 0.0) }, 0, 3, 30, 0, 0, 0, RealmType.Stock);
        Assert.Equal((int)Math.Round((hitA + hitB) / 2.0), actual);
    }

    [Fact]
    public void WeightedIncomingHitPercent_AccuracyDelta_LowersOrHolds()
    {
        var attacks = new (int, double)[] { (80, 100.0) };
        int? baseHit = MonsterMatchupCalculatorSpells.WeightedIncomingHitPercent(attacks, 0, 3, 50, 0, 0, 0, RealmType.Stock);
        int? debuffed = MonsterMatchupCalculatorSpells.WeightedIncomingHitPercent(attacks, 40, 3, 50, 0, 0, 0, RealmType.Stock);
        Assert.True(debuffed <= baseHit, "an accuracy debuff must not raise the hit chance");
    }
}
