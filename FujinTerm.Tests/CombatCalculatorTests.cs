using FujinTerm.Game;
using FujinTerm.Game.Calculators;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Realm-branch coverage for <see cref="CombatCalculator"/>: hit floor/cap,
/// dodge soft/hard caps, magic resist, backstab, accuracy, swings, and the
/// Quick &amp; Deadly bonus. Focused on the Stock vs ParaMUD divergences and
/// the clamp boundaries the formulas must honor.
/// </summary>
public sealed class CombatCalculatorTests
{
    // ----- Hit chance caps -------------------------------------------------

    [Fact]
    public void CalculateHitChance_ZeroAC_IsAlwaysHundredBeforeDodge()
    {
        HitCalcResult r = CombatCalculator.CalculateHitChance(
            attackerAccuracy: 100, defenderAC: 0, defenderDodge: 0,
            realmType: RealmType.ParaMud);
        Assert.Equal(100, r.HitPercent);
    }

    [Fact]
    public void CalculateHitChance_HighAC_ClampsToRealmFloor()
    {
        // Overwhelming AC vs weak accuracy floors the hit chance.
        HitCalcResult stock = CombatCalculator.CalculateHitChance(
            attackerAccuracy: 10, defenderAC: 500, defenderDodge: 0,
            realmType: RealmType.Stock);
        HitCalcResult para = CombatCalculator.CalculateHitChance(
            attackerAccuracy: 10, defenderAC: 500, defenderDodge: 0,
            realmType: RealmType.ParaMud);

        Assert.Equal(CombatCalculator.STOCK_HIT_MIN, stock.HitPercent);
        Assert.Equal(CombatCalculator.PARAMUD_HIT_MIN, para.HitPercent);
        Assert.Equal(CombatCalculator.STOCK_HIT_MIN, stock.HitMinCap);
        Assert.Equal(CombatCalculator.PARAMUD_HIT_MIN, para.HitMinCap);
    }

    [Fact]
    public void CalculateHitChance_ParaMudLightArmour_LowersFloorByOne()
    {
        HitCalcResult heavy = CombatCalculator.CalculateHitChance(
            attackerAccuracy: 10, defenderAC: 500, defenderDodge: 0,
            defenderArmourType: 10, realmType: RealmType.ParaMud);
        HitCalcResult light = CombatCalculator.CalculateHitChance(
            attackerAccuracy: 10, defenderAC: 500, defenderDodge: 0,
            defenderArmourType: 5, realmType: RealmType.ParaMud);

        Assert.Equal(CombatCalculator.PARAMUD_HIT_MIN, heavy.HitMinCap);
        Assert.Equal(CombatCalculator.PARAMUD_HIT_MIN - 1, light.HitMinCap);
    }

    [Fact]
    public void CalculateHitChance_CapsDifferByRealm()
    {
        HitCalcResult stock = CombatCalculator.CalculateHitChance(
            attackerAccuracy: 9999, defenderAC: 1, defenderDodge: 0,
            realmType: RealmType.Stock);
        HitCalcResult para = CombatCalculator.CalculateHitChance(
            attackerAccuracy: 9999, defenderAC: 1, defenderDodge: 0,
            realmType: RealmType.ParaMud);

        Assert.Equal(CombatCalculator.STOCK_HIT_CAP, stock.HitMaxCap);
        Assert.Equal(CombatCalculator.PARAMUD_HIT_CAP, para.HitMaxCap);
    }

    // ----- Dodge -----------------------------------------------------------

    [Fact]
    public void CalcDodgeVSAccuracy_ZeroRawDodge_IsZero()
    {
        Assert.Equal(0, CombatCalculator.CalcDodgeVSAccuracy(0, 100, RealmType.ParaMud));
        Assert.Equal(0, CombatCalculator.CalcDodgeVSAccuracy(0, 100, RealmType.Stock));
    }

    [Fact]
    public void CalcDodgeVSAccuracy_StockLowAccuracy_NoDodge()
    {
        // Stock requires accuracy > 8.
        Assert.Equal(0, CombatCalculator.CalcDodgeVSAccuracy(50, 8, RealmType.Stock));
    }

    [Fact]
    public void CalcDodgeVSAccuracy_ParaMudClampsToHardCap()
    {
        int dodge = CombatCalculator.CalcDodgeVSAccuracy(9999, 1, RealmType.ParaMud);
        Assert.True(dodge <= CombatCalculator.PARAMUD_DODGE_CAP);
    }

    [Fact]
    public void CalcDodgeVSAccuracy_StockClampsToCap()
    {
        // Low accuracy (just above the floor of 8) keeps the divisor at 1, so a
        // huge raw dodge overflows the cap.
        int dodge = CombatCalculator.CalcDodgeVSAccuracy(9999, 9, RealmType.Stock);
        Assert.Equal(CombatCalculator.STOCK_DODGE_CAP, dodge);
    }

    [Fact]
    public void CalcDodge_LowEncumbranceAddsBonus()
    {
        int noEncum = CombatCalculator.CalcDodge(50, 100, 100, 0);
        int lowEncum = CombatCalculator.CalcDodge(50, 100, 100, 0, currentEncum: 10, maxEncum: 100);
        Assert.True(lowEncum > noEncum);
    }

    // ----- Diminishing returns ---------------------------------------------

    [Fact]
    public void ParaMudDiminishingReturns_IsSignPreserving()
    {
        Assert.Equal(-CombatCalculator.ParaMud_DiminishingReturns(40, 4),
                      CombatCalculator.ParaMud_DiminishingReturns(-40, 4), 6);
    }

    [Fact]
    public void ParaMudDiminishingReturns_NonPositiveScale_PassesThrough()
    {
        Assert.Equal(42.0, CombatCalculator.ParaMud_DiminishingReturns(42, 0));
    }

    // ----- Magic resist ----------------------------------------------------

    [Fact]
    public void CalcMR_WeightsWillpowerTriple()
    {
        // (INT + WIL*3)/4 + mods. INT 100, WIL 100 → 400/4 = 100.
        Assert.Equal(100, CombatCalculator.CalcMR(100, 100));
        Assert.Equal(110, CombatCalculator.CalcMR(100, 100, 10));
    }

    // ----- Backstab --------------------------------------------------------

    [Fact]
    public void CalcBSDamage_ReturnsOrderedRange()
    {
        BSDamageResult r = CombatCalculator.CalcBSDamage(
            level: 50, stealth: 200, strength: 150,
            weaponMin: 10, weaponMax: 30,
            bsMinBonus: 0, bsMaxBonus: 0, maxDmgBonus: 0,
            hasClassStealth: true, realmType: RealmType.ParaMud);

        Assert.True(r.MinDamage <= r.MaxDamage);
        Assert.Equal((r.MinDamage + r.MaxDamage) / 2.0, r.AvgDamage);
    }

    [Fact]
    public void CalcBSDamage_StockDoublesMinStrBonus()
    {
        // Stock doubles the (STR-100)/10 min strength bonus, so for high STR
        // the Stock min damage exceeds the ParaMUD min (level-scaling aside,
        // the bonus enters before scaling for both).
        BSDamageResult stock = CombatCalculator.CalcBSDamage(
            50, 200, 200, 10, 30, 0, 0, 0, true, RealmType.Stock);
        BSDamageResult para = CombatCalculator.CalcBSDamage(
            50, 200, 200, 10, 30, 0, 0, 0, true, RealmType.ParaMud);

        Assert.True(stock.MinDamage > 0);
        Assert.True(para.MinDamage > 0);
    }

    [Fact]
    public void CalcBSDamage_RacialOnlyHasNoLevelScaling()
    {
        // Engine: racial-only stealth scales by a flat 75% with no (level+100)
        // term, identically in both realms. Manual: minDamage 10, maxDamage
        // 30 + (100-50)/10 = 35. min temp = 100+20+20 = 140 → 105;
        // max temp = 100+20+70 = 190 → 142.
        BSDamageResult stock = CombatCalculator.CalcBSDamage(
            level: 50, stealth: 200, strength: 100,
            weaponMin: 10, weaponMax: 30,
            bsMinBonus: 0, bsMaxBonus: 0, maxDmgBonus: 0,
            hasClassStealth: false, realmType: RealmType.Stock);

        Assert.Equal(105, stock.MinDamage);
        Assert.Equal(142, stock.MaxDamage);
    }

    [Fact]
    public void CalcBSDamage_IncludesStrengthMaxBonus()
    {
        // Higher strength raises the max bound via (STR-50)/10, even with no
        // item +max-damage ability.
        BSDamageResult low = CombatCalculator.CalcBSDamage(
            50, 200, 100, 10, 30, 0, 0, 0, true, RealmType.ParaMud);
        BSDamageResult high = CombatCalculator.CalcBSDamage(
            50, 200, 200, 10, 30, 0, 0, 0, true, RealmType.ParaMud);

        Assert.True(high.MaxDamage > low.MaxDamage);
    }

    [Fact]
    public void CalcBackstabAccuracy_RealmFormulasDiffer()
    {
        int para = CombatCalculator.CalcBackstabAccuracy(
            stealth: 200, agility: 100, level: 50, strength: 100,
            weaponStrReq: 0, plusBSAccuracy: 0, plusNormalAccuracy: 0,
            hasClassStealth: true, realmType: RealmType.ParaMud);
        int stock = CombatCalculator.CalcBackstabAccuracy(
            stealth: 200, agility: 100, level: 50, strength: 100,
            weaponStrReq: 0, plusBSAccuracy: 0, plusNormalAccuracy: 0,
            hasClassStealth: true, realmType: RealmType.Stock);

        Assert.NotEqual(para, stock);
    }

    [Fact]
    public void CalcBackstabAccuracy_ParaMudStrReqPenalty()
    {
        int meets = CombatCalculator.CalcBackstabAccuracy(
            200, 100, 50, 100, 50, 0, 0, true, RealmType.ParaMud);
        int fails = CombatCalculator.CalcBackstabAccuracy(
            200, 100, 50, 40, 50, 0, 0, true, RealmType.ParaMud);
        Assert.Equal(meets - 15, fails);
    }

    // ----- Base accuracy ---------------------------------------------------

    [Fact]
    public void CalcBaseAccuracy_NonPositiveLevel_IsZero()
    {
        Assert.Equal((0, 0), CombatCalculator.CalcBaseAccuracy(0, 5, RealmType.ParaMud));
    }

    [Fact]
    public void CalcBaseAccuracy_StockSqrtCorrection()
    {
        // For a perfect square the parts agree; the correction loop only
        // matters where Math.Sqrt rounds low — assert sqrtPart is the floor.
        var (sqrtPart, _) = CombatCalculator.CalcBaseAccuracy(100, 0, RealmType.Stock);
        Assert.Equal(10, sqrtPart);
    }

    // ----- Accuracy by attack type -----------------------------------------

    [Fact]
    public void CalcAccuracy_BashAndSmashApplyPenalties()
    {
        int normal = CombatCalculator.CalcAccuracy(
            MudAttackType.Normal, RealmType.Stock, 50, 5,
            strength: 100, agility: 100, intellect: 100, charm: 100,
            totalWornAccy: 20, maxSingleAbil22: 0,
            currentEncum: 0, maxEncum: 100);
        int bash = CombatCalculator.CalcAccuracy(
            MudAttackType.Bash, RealmType.Stock, 50, 5,
            100, 100, 100, 100, 20, 0, 0, 100);

        Assert.Equal(normal - 15, bash);
    }

    [Fact]
    public void CalcAccuracy_ParaMudSmashMultiplierExceedsNormal()
    {
        int normal = CombatCalculator.CalcAccuracy(
            MudAttackType.Normal, RealmType.ParaMud, 50, 5,
            150, 100, 100, 100, 40, 0, 0, 100);
        int smash = CombatCalculator.CalcAccuracy(
            MudAttackType.Smash, RealmType.ParaMud, 50, 5,
            150, 100, 100, 100, 40, 0, 0, 100);

        // Smash gets a 1.5x multiplier (then -25); for a healthy base it still
        // lands above the normal accuracy.
        Assert.True(smash > normal);
    }

    // ----- Melee damage ----------------------------------------------------

    [Fact]
    public void CalcMeleeDamage_NormalFoldsStrengthIntoRange()
    {
        // STR 100 → max bonus (100-50)/10 = 5, min bonus (100-100)/10 = 0.
        MeleeDamageResult r = CombatCalculator.CalcMeleeDamage(
            MudAttackType.Normal, RealmType.ParaMud,
            strength: 100, weaponMin: 10, weaponMax: 30, plusMaxDamage: 0);

        Assert.Equal(10, r.MinDamage);
        Assert.Equal(35, r.MaxDamage);
    }

    [Fact]
    public void CalcMeleeDamage_StockDoublesMinStrBonus()
    {
        // STR 200 → min bonus (200-100)/10 = 10, doubled to 20 in Stock.
        MeleeDamageResult stock = CombatCalculator.CalcMeleeDamage(
            MudAttackType.Normal, RealmType.Stock, 200, 10, 30, 0);
        MeleeDamageResult para = CombatCalculator.CalcMeleeDamage(
            MudAttackType.Normal, RealmType.ParaMud, 200, 10, 30, 0);

        Assert.Equal(30, stock.MinDamage);   // 10 + 20
        Assert.Equal(20, para.MinDamage);    // 10 + 10
    }

    [Fact]
    public void CalcMeleeDamage_ParaMudFloorsNegativeMaxStrBonus()
    {
        // STR 30 → (30-50)/10 = -2; ParaMUD floors at 0, Stock keeps it.
        MeleeDamageResult stock = CombatCalculator.CalcMeleeDamage(
            MudAttackType.Normal, RealmType.Stock, 30, 10, 30, 0);
        MeleeDamageResult para = CombatCalculator.CalcMeleeDamage(
            MudAttackType.Normal, RealmType.ParaMud, 30, 10, 30, 0);

        Assert.Equal(28, stock.MaxDamage);   // 30 + (-2)
        Assert.Equal(30, para.MaxDamage);    // 30 + 0
    }

    [Fact]
    public void CalcMeleeDamage_BashAndSmashExceedNormal()
    {
        MeleeDamageResult normal = CombatCalculator.CalcMeleeDamage(
            MudAttackType.Normal, RealmType.Stock, 100, 10, 30, 0);
        MeleeDamageResult bash = CombatCalculator.CalcMeleeDamage(
            MudAttackType.Bash, RealmType.Stock, 100, 10, 30, 0);
        MeleeDamageResult smash = CombatCalculator.CalcMeleeDamage(
            MudAttackType.Smash, RealmType.Stock, 100, 10, 30, 0);

        Assert.True(bash.MaxDamage > normal.MaxDamage);
        Assert.True(smash.MaxDamage > bash.MaxDamage);
    }

    // ----- Martial arts (Mystic) -------------------------------------------

    [Fact]
    public void CalcMartialArtsDamage_NoSkillYieldsZeroRange()
    {
        MeleeDamageResult r = CombatCalculator.CalcMartialArtsDamage(
            MudAttackType.Punch, RealmType.Stock, level: 20, maPlusSkill: 0,
            strength: 50, plusMaxDamage: 0, maPlusDamage: 0);

        Assert.Equal(0, r.MinDamage);
        Assert.Equal(0, r.MaxDamage);
    }

    [Fact]
    public void CalcMartialArtsDamage_PunchMatchesStockFormula()
    {
        // skill 21, level 20 (nTemp=20), STR 50 (no strength bonus):
        //   min = 21*20/8 + 2       = 52 + 2  = 54
        //   max = 21*(20+3)/4 + 6   = 120 + 6 = 126
        MeleeDamageResult r = CombatCalculator.CalcMartialArtsDamage(
            MudAttackType.Punch, RealmType.Stock, level: 20, maPlusSkill: 21,
            strength: 50, plusMaxDamage: 0, maPlusDamage: 0);

        Assert.Equal(54, r.MinDamage);
        Assert.Equal(126, r.MaxDamage);
    }

    [Fact]
    public void CalcMartialArtsDamage_KickAppliesStockPrerollMultiplier()
    {
        // skill 21, level 20, STR 50: base min 54, kick max = 21*20/6 + 7 = 77.
        // Stock kick pre-roll ×1.33 (truncated): min Fix(54*1.33)=71, max Fix(77*1.33)=102.
        MeleeDamageResult r = CombatCalculator.CalcMartialArtsDamage(
            MudAttackType.Kick, RealmType.Stock, level: 20, maPlusSkill: 21,
            strength: 50, plusMaxDamage: 0, maPlusDamage: 0);

        Assert.Equal(71, r.MinDamage);
        Assert.Equal(102, r.MaxDamage);
    }

    [Fact]
    public void CalcMartialArtsDamage_PrerollOrderingHoldsAcrossAttacks()
    {
        MeleeDamageResult punch = CombatCalculator.CalcMartialArtsDamage(
            MudAttackType.Punch, RealmType.Stock, 20, 21, 50, 0, 0);
        MeleeDamageResult kick = CombatCalculator.CalcMartialArtsDamage(
            MudAttackType.Kick, RealmType.Stock, 20, 21, 50, 0, 0);
        MeleeDamageResult jump = CombatCalculator.CalcMartialArtsDamage(
            MudAttackType.Jumpkick, RealmType.Stock, 20, 21, 50, 0, 0);

        // Kick/jumpkick share a base max formula; the higher jumpkick pre-roll
        // (×1.66 vs ×1.33) makes its max strictly larger.
        Assert.True(jump.MaxDamage > kick.MaxDamage);
        // The pre-roll multipliers lift the (shared) base min floor in order:
        // punch (no multiplier) < kick (×1.33) < jumpkick (×1.66).
        Assert.True(kick.MinDamage > punch.MinDamage);
        Assert.True(jump.MinDamage > kick.MinDamage);
    }

    [Fact]
    public void CalcMartialArtsDamage_LevelCapsSkillMultiplierAt20()
    {
        // nTemp = min(level, 20), so level 20 and level 99 are identical.
        MeleeDamageResult atCap = CombatCalculator.CalcMartialArtsDamage(
            MudAttackType.Punch, RealmType.Stock, 20, 21, 50, 0, 0);
        MeleeDamageResult overCap = CombatCalculator.CalcMartialArtsDamage(
            MudAttackType.Punch, RealmType.Stock, 99, 21, 50, 0, 0);

        Assert.Equal(atCap.MinDamage, overCap.MinDamage);
        Assert.Equal(atCap.MaxDamage, overCap.MaxDamage);
    }

    [Fact]
    public void CalcMartialArtsDamage_ItemMaBonusAddsToBothBounds()
    {
        MeleeDamageResult plain = CombatCalculator.CalcMartialArtsDamage(
            MudAttackType.Punch, RealmType.Stock, 20, 21, 50, plusMaxDamage: 0, maPlusDamage: 0);
        MeleeDamageResult withBonus = CombatCalculator.CalcMartialArtsDamage(
            MudAttackType.Punch, RealmType.Stock, 20, 21, 50, plusMaxDamage: 0, maPlusDamage: 5);

        // Punch has no pre-roll multiplier, so the +5 lands raw on both bounds.
        Assert.Equal(plain.MinDamage + 5, withBonus.MinDamage);
        Assert.Equal(plain.MaxDamage + 5, withBonus.MaxDamage);
    }

    [Fact]
    public void CalcMartialArtsDamage_StrengthFoldsIntoRange()
    {
        // STR 200 (Stock): max bonus (200-50)/10 = 15; min bonus (200-100)/10*2 = 20.
        MeleeDamageResult baseline = CombatCalculator.CalcMartialArtsDamage(
            MudAttackType.Punch, RealmType.Stock, 20, 21, strength: 50, plusMaxDamage: 0, maPlusDamage: 0);
        MeleeDamageResult strong = CombatCalculator.CalcMartialArtsDamage(
            MudAttackType.Punch, RealmType.Stock, 20, 21, strength: 200, plusMaxDamage: 0, maPlusDamage: 0);

        Assert.Equal(baseline.MinDamage + 20, strong.MinDamage);
        Assert.Equal(baseline.MaxDamage + 15, strong.MaxDamage);
    }

    [Fact]
    public void CalcMartialArtsDamage_GreaterMud_UsesLevelBandPlusFlatSkill()
    {
        // ParaMUD/GreaterMUD: level-driven band + skill added flat (no skill×level).
        // L30 (>=20): min = round(30/6=5) floored 5 = 5; punch max = max(round(30/4=7.5→8),12)=12.
        // skill 30, STR 50 (no strength bonus): min 35, max 42. Punch has no multiplier.
        MeleeDamageResult r = CombatCalculator.CalcMartialArtsDamage(
            MudAttackType.Punch, RealmType.ParaMud, level: 30, maPlusSkill: 30,
            strength: 50, plusMaxDamage: 0, maPlusDamage: 0);

        Assert.Equal(35, r.MinDamage);
        Assert.Equal(42, r.MaxDamage);
    }

    [Fact]
    public void CalcMartialArtsDamage_GreaterMud_KickAppliesMultiplier()
    {
        // L30 kick: min 35; max = max(round(30/4=8),10)=10 → +skill30 = 40.
        // Kick ×1.33 truncated: min Fix(35*1.33)=46, max Fix(40*1.33)=53.
        MeleeDamageResult r = CombatCalculator.CalcMartialArtsDamage(
            MudAttackType.Kick, RealmType.ParaMud, level: 30, maPlusSkill: 30,
            strength: 50, plusMaxDamage: 0, maPlusDamage: 0);

        Assert.Equal(46, r.MinDamage);
        Assert.Equal(53, r.MaxDamage);
    }

    [Fact]
    public void CalcMartialArtsDamage_GreaterMud_SubTwentyBand()
    {
        // L10 (<20): min = round(10/8+2 = 3.25) = 3; punch max = round((13)/4+6 = 9.25) = 9.
        // skill 20: min 23, max 29.
        MeleeDamageResult r = CombatCalculator.CalcMartialArtsDamage(
            MudAttackType.Punch, RealmType.ParaMud, level: 10, maPlusSkill: 20,
            strength: 50, plusMaxDamage: 0, maPlusDamage: 0);

        Assert.Equal(23, r.MinDamage);
        Assert.Equal(29, r.MaxDamage);
    }

    [Fact]
    public void CalcMartialArtsDamage_GreaterMud_FoldsPositiveStrengthOnly()
    {
        // GreaterMUD: max gets (STR-50)/10 (>0 only); min gets (STR-100)/10 (NOT
        // doubled), floored 0. STR 150: max +10, min +5. L30 punch base 35/42.
        MeleeDamageResult r = CombatCalculator.CalcMartialArtsDamage(
            MudAttackType.Punch, RealmType.ParaMud, level: 30, maPlusSkill: 30,
            strength: 150, plusMaxDamage: 0, maPlusDamage: 0);

        Assert.Equal(40, r.MinDamage);  // 35 + 5
        Assert.Equal(52, r.MaxDamage);  // 42 + 10
    }

    [Theory]
    // Ground truth captured from MMUD Explorer (GreaterMUD / Paradigm 1.9),
    // Weapons tab → Calc Combat → Martial Arts, for a level-1 Kang Mystic with
    // STR 80. maPlusSkill is MME's nMAPlusSkill floored to 1 (no +MA-skill item).
    // L1 band min = round(1/8+2)=2 (+1 = 3); max band = round((1+3)/4+6)=7 for
    // punch, round(1/5+7)=7 kick, round(1/6+7)=7 jk (+1 = 8). STR 80 adds +3 to
    // max only. Kick ×1.33 / jumpkick ×1.66 truncate afterward.
    [InlineData(MudAttackType.Punch, 3, 11)]
    [InlineData(MudAttackType.Kick, 3, 14)]
    [InlineData(MudAttackType.Jumpkick, 4, 18)]
    public void CalcMartialArtsDamage_GreaterMud_MatchesMmeLevel1Mystic(
        MudAttackType attack, int expectedMin, int expectedMax)
    {
        MeleeDamageResult r = CombatCalculator.CalcMartialArtsDamage(
            attack, RealmType.ParaMud, level: 1, maPlusSkill: 1,
            strength: 80, plusMaxDamage: 0, maPlusDamage: 0);

        Assert.Equal(expectedMin, r.MinDamage);
        Assert.Equal(expectedMax, r.MaxDamage);
    }

    // ----- Critical hit chance (gear/quest crit + Quick-and-Deadly) --------

    [Theory]
    [InlineData(20, 15, RealmType.Stock, 35)]     // below 40 → straight sum
    [InlineData(20, 15, RealmType.ParaMud, 35)]
    [InlineData(50, 20, RealmType.Stock, 50)]     // 70 → 40 + (30)/3 = 50
    [InlineData(80, 20, RealmType.ParaMud, 65)]   // ParaMUD hard cap 65
    [InlineData(300, 0, RealmType.Stock, 99)]     // 40 + 260/3 = 126 → Stock cap 99
    [InlineData(-5, 0, RealmType.Stock, 0)]       // floored at 0
    public void CalcCritChance_AppliesRealmDiminishingReturns(
        int rating, int qnd, RealmType realm, int expected)
        => Assert.Equal(expected, CombatCalculator.CalcCritChance(rating, qnd, realm));

    // ----- Swings ----------------------------------------------------------

    [Fact]
    public void CalcEnergyUsed_StrengthUnderRequirement_RaisesEnergy()
    {
        // Slow weapon + low level keeps base energy large enough that the
        // strength-penalty multiplier is visible.
        int meets = CombatCalculator.CalcEnergyUsed(1, 10, 200, 50, strength: 100, weaponStrReq: 50);
        int under = CombatCalculator.CalcEnergyUsed(1, 10, 200, 50, strength: 40, weaponStrReq: 50);
        Assert.True(under > meets);
    }

    [Fact]
    public void CalcSwings_RawSwingsCappedAtMax()
    {
        SwingCalcResult r = CombatCalculator.CalcSwings(
            combatLevel: 10, level: 100, attackSpeed: 1, agility: 200,
            strength: 200, weaponStrReq: 0, currentEncum: 0, maxEncum: 100,
            realmType: RealmType.ParaMud);

        Assert.True(r.RawSwings <= CombatCalculator.MAX_SWINGS);
        Assert.Equal(10, r.SwingsPerRound.Length);
        Assert.All(r.SwingsPerRound, s => Assert.True(s <= CombatCalculator.MAX_SWINGS));
    }

    [Fact]
    public void CalcSwings_BashingDoublesEnergyPerSwing()
    {
        // Slow weapon + low level so per-swing energy stays well above 1 and
        // the ×2 bashing factor isn't masked by the floor clamp.
        SwingCalcResult normal = CombatCalculator.CalcSwings(
            1, 10, 200, 50, 100, 0, 0, 100, isBashing: false, realmType: RealmType.ParaMud);
        SwingCalcResult bashing = CombatCalculator.CalcSwings(
            1, 10, 200, 50, 100, 0, 0, 100, isBashing: true, realmType: RealmType.ParaMud);

        Assert.Equal(normal.EnergyPerSwing * 2, bashing.EnergyPerSwing);
    }

    // ----- Quick & Deadly --------------------------------------------------

    [Fact]
    public void CalcQuickAndDeadly_ZeroAtHighEnergy()
    {
        Assert.Equal(0, CombatCalculator.CalcQuickAndDeadlyBonus(100, 200, 0, RealmType.ParaMud));
        Assert.Equal(0, CombatCalculator.CalcQuickAndDeadlyBonus(100, 250, 0, RealmType.Stock));
    }

    [Fact]
    public void CalcQuickAndDeadly_StockZeroAboveHeavyEncum()
    {
        Assert.Equal(0, CombatCalculator.CalcQuickAndDeadlyBonus(100, 50, 70, RealmType.Stock));
    }

    [Fact]
    public void CalcQuickAndDeadly_RealmFormulasDiffer()
    {
        int para = CombatCalculator.CalcQuickAndDeadlyBonus(100, 50, 0, RealmType.ParaMud);
        int stock = CombatCalculator.CalcQuickAndDeadlyBonus(100, 50, 0, RealmType.Stock);
        Assert.True(para > 0);
        Assert.True(stock > 0);
        Assert.NotEqual(para, stock);
    }

    [Fact]
    public void CalcQuickAndDeadly_StockCapsAtTwenty()
    {
        int bonus = CombatCalculator.CalcQuickAndDeadlyBonus(999, 1, 0, RealmType.Stock);
        Assert.True(bonus <= 20);
    }
}
