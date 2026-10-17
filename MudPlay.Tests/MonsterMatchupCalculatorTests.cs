using System;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using Xunit;

namespace MudPlay.Tests;

// Behavioural coverage for MonsterMatchupCalculator: the
// player → monster DPS / rounds-to-kill projection, the monster → player
// return-fire preview, the prot-ward gating on monster alignment, DR
// flooring, and the unarmed / no-attack edge gates.
public sealed class MonsterMatchupCalculatorTests
{
    private static PlayerMatchupProfile Player(
        int accuracy = 200, int avgDmg = 10, double swings = 2.0, bool hasWeapon = true, int dr = 0) =>
        new(RealmType.ParaMud, accuracy, avgDmg, swings, hasWeapon, dr);

    private static MonsterMatchupProfile Monster(
        int ac = 50, int dr = 2, int hp = 100, int dodge = 0, bool hasAttack = true, int avgAttack = 8) =>
        new(ac, dr, hp, dodge, hasAttack, avgAttack);

    // A monster has no class armour type, so a swing at it floors at the realm's
    // default (Paradigm 2%, Stock 9%), and its dodge still comes off that. Dodge 80
    // dodges 40% of 150 accuracy on Paradigm (6400/160), and 72 does on Stock
    // (720/(150/8)); the whole-percent net hit read 2 − 2·40/100 = 2 and 9 − 3 = 6.
    [Theory]
    [InlineData(RealmType.ParaMud, 80, 2 * 0.6)]
    [InlineData(RealmType.Stock, 72, 9 * 0.6)]
    public void PlayerToMonster_DodgeCountsAtTheRealmFloor(RealmType realm, int dodge, double expected)
    {
        PlayerMatchupProfile p = new(realm, 150, 10, 1.0, true, 0);
        MonsterMatchupResult r = MonsterMatchupCalculator.Compute(p, Monster(ac: 9999, dodge: dodge));
        Assert.Equal(expected, r.PlayerHitChance, 6);
    }

    [Fact]
    public void PlayerToMonster_MatchesHitFormula_AndProjectsDps()
    {
        PlayerMatchupProfile p = Player(accuracy: 200, avgDmg: 10, swings: 2.0, dr: 0);
        MonsterMatchupProfile m = Monster(ac: 50, dr: 2, hp: 100);

        double expectedHit = CombatCalculator.CalculateHitChance(
            attackerAccuracy: 200, defenderAC: 50, defenderDodge: 0,
            realmType: RealmType.ParaMud).OverallHitChance;
        int expectedDmg = 10 - 2;
        double expectedDps = expectedHit / 100.0 * expectedDmg * 2.0;
        int expectedRounds = (int)Math.Ceiling(100 / expectedDps);

        MonsterMatchupResult r = MonsterMatchupCalculator.Compute(p, m);

        Assert.Equal(expectedHit, r.PlayerHitChance, 6);
        Assert.Equal(expectedDmg, r.PlayerDamagePerHit);
        Assert.Equal(expectedDps, r.PlayerDps, 5);
        Assert.Equal(expectedRounds, r.RoundsToKill);
        Assert.True(r.HasWeapon);
    }

    // Stock takes the monster's DR off the rolled damage before the bash ×3 / smash
    // ×5, so DR counts that many times against a hit; Paradigm takes it off after.
    [Fact]
    public void StockBashAndSmash_MultiplyTheMonstersDr_ParadigmDoesNot()
    {
        MonsterMatchupProfile m = Monster(dr: 2);
        PlayerMatchupProfile bash = Player(avgDmg: 30) with
        {
            Realm = RealmType.Stock,
            MonsterDrMultiplier = CombatCalculator.DrMultiplierFor(MudAttackType.Bash, RealmType.Stock),
        };
        PlayerMatchupProfile smash = bash with
        {
            MonsterDrMultiplier = CombatCalculator.DrMultiplierFor(MudAttackType.Smash, RealmType.Stock),
        };

        Assert.Equal(30 - 6, MonsterMatchupCalculator.Compute(bash, m).PlayerDamagePerHit);
        Assert.Equal(30 - 10, MonsterMatchupCalculator.Compute(smash, m).PlayerDamagePerHit);
        Assert.Equal(1, CombatCalculator.DrMultiplierFor(MudAttackType.Bash, RealmType.ParaMud));
        Assert.Equal(1, CombatCalculator.DrMultiplierFor(MudAttackType.Normal, RealmType.Stock));
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

        double expected = CombatCalculator.CalculateHitChance(
            attackerAccuracy: 200, defenderAC: 50, defenderDodge: 70,
            realmType: RealmType.ParaMud).OverallHitChance;

        MonsterMatchupResult rNo = MonsterMatchupCalculator.Compute(p, noDodge);
        MonsterMatchupResult rDodge = MonsterMatchupCalculator.Compute(p, withDodge);

        Assert.Equal(expected, rDodge.PlayerHitChance, 6);
        Assert.True(rDodge.PlayerHitChance < rNo.PlayerHitChance);
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
            realmType: RealmType.ParaMud).OverallHitChance / 100.0;
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
        Assert.Equal(0, r.MonsterDamagePerHit);
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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(6)]
    public void AttackHitChance_EvilAlignment_AppliesProtEvilNotProtGood(int evilAlign)
    {
        double withProtEvil = MonsterMatchupCalculatorSpells.AttackHitChance(
            140, evilAlign, defenderAc: 60, defenderDodge: 0, protEvil: 20, protGood: 0, RealmType.ParaMud);
        double noWard = MonsterMatchupCalculatorSpells.AttackHitChance(
            140, evilAlign, defenderAc: 60, defenderDodge: 0, protEvil: 0, protGood: 0, RealmType.ParaMud);
        double protGoodOnly = MonsterMatchupCalculatorSpells.AttackHitChance(
            140, evilAlign, defenderAc: 60, defenderDodge: 0, protEvil: 0, protGood: 20, RealmType.ParaMud);

        Assert.True(withProtEvil < noWard);
        Assert.Equal(noWard, protGoodOnly);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void AttackHitChance_GoodAlignment_AppliesProtGoodNotProtEvil_OnStock(int goodAlign)
    {
        // Prot-Good is a STOCK-only ward (ability 25). Against a good monster it
        // lowers incoming hits; Prot-Evil (an evil-only ward) does nothing.
        double withProtGood = MonsterMatchupCalculatorSpells.AttackHitChance(
            140, goodAlign, defenderAc: 60, defenderDodge: 0, protEvil: 0, protGood: 20, RealmType.Stock);
        double noWard = MonsterMatchupCalculatorSpells.AttackHitChance(
            140, goodAlign, defenderAc: 60, defenderDodge: 0, protEvil: 0, protGood: 0, RealmType.Stock);
        double protEvilOnly = MonsterMatchupCalculatorSpells.AttackHitChance(
            140, goodAlign, defenderAc: 60, defenderDodge: 0, protEvil: 20, protGood: 0, RealmType.Stock);

        Assert.True(withProtGood < noWard);
        Assert.Equal(noWard, protEvilOnly);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void AttackHitChance_GoodAlignment_ProtGoodIgnoredOnParaMud(int goodAlign)
    {
        // Paradigm dropped Prot-Good for VileWard: a Prot-Good value must not
        // change a good monster's hit chance on ParaMUD.
        double withProtGood = MonsterMatchupCalculatorSpells.AttackHitChance(
            140, goodAlign, defenderAc: 60, defenderDodge: 0, protEvil: 0, protGood: 20, RealmType.ParaMud);
        double noWard = MonsterMatchupCalculatorSpells.AttackHitChance(
            140, goodAlign, defenderAc: 60, defenderDodge: 0, protEvil: 0, protGood: 0, RealmType.ParaMud);

        Assert.Equal(noWard, withProtGood);
    }

    [Fact]
    public void AttackHitChance_NeutralAlignment_IgnoresBothWards()
    {
        double withWards = MonsterMatchupCalculatorSpells.AttackHitChance(
            120, alignment: 3, defenderAc: 100, defenderDodge: 0, protEvil: 50, protGood: 50, RealmType.ParaMud);
        double noWards = MonsterMatchupCalculatorSpells.AttackHitChance(
            120, alignment: 3, defenderAc: 100, defenderDodge: 0, protEvil: 0, protGood: 0, RealmType.ParaMud);

        Assert.Equal(noWards, withWards);
    }

    // Monster Intel's defense simulator feeds a raw Vile Ward through to
    // CombatCalculator's AdjustVileWard: it lowers an EVIL monster's hit chance
    // (scaled by the defender's own evil tier), does NOTHING versus a neutral
    // monster (evil-only ward), and the higher the alignment tier the bigger the
    // AC it converts to (villain/fiend > outlaw/criminal).
    [Fact]
    public void AttackHitChance_VileWard_EvilOnly_ScalesWithEvilTier()
    {
        double noVile = MonsterMatchupCalculatorSpells.AttackHitChance(
            140, alignment: 1, defenderAc: 60, defenderDodge: 0, protEvil: 0, protGood: 0,
            RealmType.ParaMud, hasShadow: false, vileWard: 500, defenderEvil: EvilLevel.Saint);
        double halfTier = MonsterMatchupCalculatorSpells.AttackHitChance(
            140, alignment: 1, defenderAc: 60, defenderDodge: 0, protEvil: 0, protGood: 0,
            RealmType.ParaMud, hasShadow: false, vileWard: 500, defenderEvil: EvilLevel.Criminal);
        double fullTier = MonsterMatchupCalculatorSpells.AttackHitChance(
            140, alignment: 1, defenderAc: 60, defenderDodge: 0, protEvil: 0, protGood: 0,
            RealmType.ParaMud, hasShadow: false, vileWard: 500, defenderEvil: EvilLevel.Fiend);
        double neutralFull = MonsterMatchupCalculatorSpells.AttackHitChance(
            140, alignment: 3, defenderAc: 60, defenderDodge: 0, protEvil: 0, protGood: 0,
            RealmType.ParaMud, hasShadow: false, vileWard: 500, defenderEvil: EvilLevel.Fiend);
        double neutralNone = MonsterMatchupCalculatorSpells.AttackHitChance(
            140, alignment: 3, defenderAc: 60, defenderDodge: 0, protEvil: 0, protGood: 0,
            RealmType.ParaMud, hasShadow: false, vileWard: 0, defenderEvil: EvilLevel.Fiend);

        Assert.True(halfTier < noVile);        // "not evil" tier converts nothing; Criminal halves the ward to AC
        Assert.True(fullTier < halfTier);      // Fiend converts the full ward → more AC → fewer hits
        Assert.Equal(neutralNone, neutralFull); // evil-only: no effect on a neutral monster
    }

    // Regression: an earlier version of the incoming hit chance never passed
    // hasShadow through to CombatCalculator, silently ignoring the flat
    // +10 AC Shadow (Abil 9) grants against every attacker regardless of
    // alignment (unlike Prot Evil/Good, which are alignment-conditional).
    // Caught via a live character's own @st readout: AC vs Evil and AC vs
    // Good both included the same Shadow bonus on top of bare AC.
    [Fact]
    public void AttackHitChance_HasShadow_LowersHitChance()
    {
        double withShadow = MonsterMatchupCalculatorSpells.AttackHitChance(
            140, alignment: 2, defenderAc: 60, defenderDodge: 0, protEvil: 0, protGood: 0,
            realm: RealmType.ParaMud, hasShadow: true);
        double noShadow = MonsterMatchupCalculatorSpells.AttackHitChance(
            140, alignment: 2, defenderAc: 60, defenderDodge: 0, protEvil: 0, protGood: 0,
            realm: RealmType.ParaMud, hasShadow: false);

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
    public void WeightedIncomingHitChance_NullForNoAttacks()
        => Assert.Null(MonsterMatchupCalculatorSpells.WeightedIncomingHitChance(
            Array.Empty<(int, double)>(), accuracyDelta: 0, alignment: 3,
            defenderAc: 30, defenderDodge: 0, protEvil: 0, protGood: 0, realm: RealmType.Stock));

    [Fact]
    public void WeightedIncomingHitChance_SingleAttack_EqualsThatAttack()
    {
        double single = MonsterMatchupCalculatorSpells.AttackHitChance(
            60, 3, 40, 0, 0, 0, RealmType.Stock);
        double? weighted = MonsterMatchupCalculatorSpells.WeightedIncomingHitChance(
            new (int, double)[] { (60, 100.0) }, 0, 3, 40, 0, 0, 0, RealmType.Stock);
        Assert.Equal(single, weighted);
    }

    [Fact]
    public void WeightedIncomingHitChance_BlendsByUseWeight()
    {
        double hitA = MonsterMatchupCalculatorSpells.AttackHitChance(80, 3, 30, 0, 0, 0, RealmType.Stock);
        double hitB = MonsterMatchupCalculatorSpells.AttackHitChance(40, 3, 30, 0, 0, 0, RealmType.Stock);
        double? actual = MonsterMatchupCalculatorSpells.WeightedIncomingHitChance(
            new (int, double)[] { (80, 75.0), (40, 25.0) }, 0, 3, 30, 0, 0, 0, RealmType.Stock);
        Assert.Equal((75.0 * hitA + 25.0 * hitB) / 100.0, actual!.Value, 6);
    }

    [Fact]
    public void WeightedIncomingHitChance_AllZeroWeights_FallsBackToMean()
    {
        double hitA = MonsterMatchupCalculatorSpells.AttackHitChance(80, 3, 30, 0, 0, 0, RealmType.Stock);
        double hitB = MonsterMatchupCalculatorSpells.AttackHitChance(40, 3, 30, 0, 0, 0, RealmType.Stock);
        double? actual = MonsterMatchupCalculatorSpells.WeightedIncomingHitChance(
            new (int, double)[] { (80, 0.0), (40, 0.0) }, 0, 3, 30, 0, 0, 0, RealmType.Stock);
        Assert.Equal((hitA + hitB) / 2.0, actual!.Value, 6);
    }

    [Fact]
    public void WeightedIncomingHitChance_AccuracyDelta_LowersOrHolds()
    {
        var attacks = new (int, double)[] { (80, 100.0) };
        double? baseHit = MonsterMatchupCalculatorSpells.WeightedIncomingHitChance(attacks, 0, 3, 50, 0, 0, 0, RealmType.Stock);
        double? debuffed = MonsterMatchupCalculatorSpells.WeightedIncomingHitChance(attacks, 40, 3, 50, 0, 0, 0, RealmType.Stock);
        Assert.True(debuffed <= baseHit, "an accuracy debuff must not raise the hit chance");
    }

    // Report paradigm-20261014-121847: a Paradigm Mage (Silk, ArmourType 1) with raw
    // dodge 79 against a brute zombie's 150-accuracy fists. The whole-percent figure
    // truncated the dodge share away (2 − 2·39/100 = 2), so dodge never showed at the
    // floor; the unrounded chance keeps it: 1% floor × (100 − 39)% = 0.61%.
    [Fact]
    public void AttackHitChance_KeepsDodgeAtTheFloor()
    {
        int dodgePct = CombatCalculator.CalcDodgeVSAccuracy(79, 150, RealmType.ParaMud);
        Assert.Equal(39, dodgePct);
        double chance = MonsterMatchupCalculatorSpells.AttackHitChance(
            150, 2, defenderAc: 9999, defenderDodge: 79, protEvil: 0, protGood: 0,
            RealmType.ParaMud, defenderArmourType: 1);
        Assert.Equal(1 * (100 - dodgePct) / 100.0, chance, 6);
    }

    // Away from the floor too, the net chance is the hit × (1 − dodge) product.
    [Fact]
    public void AttackHitChance_IsHitTimesMissedDodge()
    {
        HitCalcResult r = CombatCalculator.CalculateHitChance(
            attackerAccuracy: 150, defenderAC: 90, defenderDodge: 60, realmType: RealmType.ParaMud);
        Assert.True(r.DodgePercent > 0);
        Assert.Equal(r.HitPercent * (100 - r.DodgePercent) / 100.0, r.OverallHitChance, 6);
    }

    // The class ArmourType only moves the ParaMUD hit FLOOR: light-armour classes
    // (1..6) drop to 1%, everyone else stays at 2%; Stock ignores it (flat 9%).
    // A huge AC forces the raw chance far below the floor so only GetHitMin decides.
    [Fact]
    public void AttackHitChance_ParaMudFloor_IsClassArmourTypeAware()
    {
        // Light-armour ParaMUD class (ArmourType 3 = Leather) → 1%.
        Assert.Equal(1.0, MonsterMatchupCalculatorSpells.AttackHitChance(
            accuracy: 100, alignment: 3, defenderAc: 9999, defenderDodge: 0,
            protEvil: 0, protGood: 0, realm: RealmType.ParaMud, defenderArmourType: 3));

        // Heavy-armour ParaMUD class (ArmourType 9 = Platemail) → 2%.
        Assert.Equal(2.0, MonsterMatchupCalculatorSpells.AttackHitChance(
            100, 3, 9999, 0, 0, 0, RealmType.ParaMud, defenderArmourType: 9));

        // Unknown armour type (0) keeps the ParaMUD default floor of 2%.
        Assert.Equal(2.0, MonsterMatchupCalculatorSpells.AttackHitChance(
            100, 3, 9999, 0, 0, 0, RealmType.ParaMud));

        // Stock ignores ArmourType entirely — 9% floor for every class.
        Assert.Equal(9.0, MonsterMatchupCalculatorSpells.AttackHitChance(
            100, 3, 9999, 0, 0, 0, RealmType.Stock, defenderArmourType: 3));
    }
}
