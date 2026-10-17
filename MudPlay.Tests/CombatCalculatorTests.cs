using MudPlay.Game;
using MudPlay.Game.Calculators;
using Xunit;

namespace MudPlay.Tests;

// Realm-branch coverage for CombatCalculator: hit floor/cap,
// dodge soft/hard caps, magic resist, backstab, accuracy, swings, and the
// Quick & Deadly bonus. Focused on the Stock vs ParaMUD divergences and
// the clamp boundaries the formulas must honor.
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

    // ----- ProtGood / VileWard realm exclusivity ---------------------------
    // Stock uses Protection-from-Good (ability 25); Paradigm dropped it for
    // VileWard (ability 1113). Each counts only in its own realm.

    [Fact]
    public void CalculateHitChance_ProtGood_LowersHitOnStock_IgnoredOnParaMud()
    {
        // Accuracy/AC chosen so the hit chance sits mid-band (not clamped), so a
        // ProtGood contribution to the defender's defense is visible in the delta.
        HitCalcResult stockNoPg = CombatCalculator.CalculateHitChance(
            attackerAccuracy: 200, defenderAC: 30, defenderDodge: 0,
            protGood: 0, realmType: RealmType.Stock);
        HitCalcResult stockPg = CombatCalculator.CalculateHitChance(
            attackerAccuracy: 200, defenderAC: 30, defenderDodge: 0,
            protGood: 20, realmType: RealmType.Stock);

        // On Stock, Prot-Good raises the defender's effective AC → fewer hits land.
        Assert.True(stockPg.HitPercent < stockNoPg.HitPercent);

        HitCalcResult paraNoPg = CombatCalculator.CalculateHitChance(
            attackerAccuracy: 200, defenderAC: 30, defenderDodge: 0,
            protGood: 0, realmType: RealmType.ParaMud);
        HitCalcResult paraPg = CombatCalculator.CalculateHitChance(
            attackerAccuracy: 200, defenderAC: 30, defenderDodge: 0,
            protGood: 20, realmType: RealmType.ParaMud);

        // On Paradigm the server ignores Prot-Good entirely — a leaked value must
        // not change the outcome.
        Assert.Equal(paraNoPg.HitPercent, paraPg.HitPercent);
    }

    [Fact]
    public void CalculateHitChance_VileWard_LowersHitOnParaMud_IgnoredOnStock()
    {
        HitCalcResult paraNoVw = CombatCalculator.CalculateHitChance(
            attackerAccuracy: 200, defenderAC: 30, defenderDodge: 0,
            vileWard: 0, evilLevel: EvilLevel.Villain, realmType: RealmType.ParaMud);
        HitCalcResult paraVw = CombatCalculator.CalculateHitChance(
            attackerAccuracy: 200, defenderAC: 30, defenderDodge: 0,
            vileWard: 200, evilLevel: EvilLevel.Villain, realmType: RealmType.ParaMud);

        // On Paradigm, VileWard (vs an evil attacker) raises effective AC.
        Assert.True(paraVw.HitPercent < paraNoVw.HitPercent);

        HitCalcResult stockNoVw = CombatCalculator.CalculateHitChance(
            attackerAccuracy: 200, defenderAC: 30, defenderDodge: 0,
            vileWard: 0, evilLevel: EvilLevel.Villain, realmType: RealmType.Stock);
        HitCalcResult stockVw = CombatCalculator.CalculateHitChance(
            attackerAccuracy: 200, defenderAC: 30, defenderDodge: 0,
            vileWard: 200, evilLevel: EvilLevel.Villain, realmType: RealmType.Stock);

        // Stock has no VileWard ability — it must not affect the hit chance.
        Assert.Equal(stockNoVw.HitPercent, stockVw.HitPercent);
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

    // Paradigm capture: a level-2 Gypsy (combat 4; STR 60 AGI 70 INT 70 CHM 30, +5
    // accuracy from gear) at 964/2880 = 33% read Attack 43 / Bash 27 / Dodge 9 in
    // `stat all`. All three need the light-load bonus, so 33% still earns it there.
    // Paradigm captures, 2026-09-30: a level-13 Gypsy (combat 4; STR 70 AGI 85 INT 70
    // CHM 46) at 1051/3360 = 31%, worn Accy 11 (rapier 10 + bracers 1) and +5 from an
    // accuracy ability, read Attack / Bash 83 / 65; without the +5, 78 / 60; without
    // the bracers' 1, 82 / 64. The gear adds unrounded.
    [Theory]
    [InlineData(11, 5, 83, 65)]
    [InlineData(11, 0, 78, 60)]
    [InlineData(10, 5, 82, 64)]
    public void Paradigm_Accuracy_MatchesStatAll(int worn, int ability, int attack, int bash)
    {
        Assert.Equal(attack, CombatCalculator.CalcAccuracy(MudAttackType.Normal, RealmType.ParaMud,
            level: 13, nCombatLevel: 4, strength: 70, agility: 85, intellect: 70, charm: 46,
            totalWornAccy: worn, maxSingleAbil22: ability, currentEncum: 1051, maxEncum: 3360));
        Assert.Equal(bash, CombatCalculator.CalcAccuracy(MudAttackType.Bash, RealmType.ParaMud,
            level: 13, nCombatLevel: 4, strength: 70, agility: 85, intellect: 70, charm: 46,
            totalWornAccy: worn, maxSingleAbil22: ability, currentEncum: 1051, maxEncum: 3360));
    }

    [Fact]
    public void Paradigm_LightLoadBonusStillAppliesAtExactly33Percent()
    {
        int attack = CombatCalculator.CalcAccuracy(MudAttackType.Normal, RealmType.ParaMud,
            level: 2, nCombatLevel: 4, strength: 60, agility: 70, intellect: 70, charm: 30,
            totalWornAccy: 0, maxSingleAbil22: 5, currentEncum: 964, maxEncum: 2880);
        int bash = CombatCalculator.CalcAccuracy(MudAttackType.Bash, RealmType.ParaMud,
            level: 2, nCombatLevel: 4, strength: 60, agility: 70, intellect: 70, charm: 30,
            totalWornAccy: 0, maxSingleAbil22: 5, currentEncum: 964, maxEncum: 2880);
        int dodge = CombatCalculator.CalcDodge(2, 70, 30, 0, 964, 2880, RealmType.ParaMud);

        Assert.Equal(43, attack);
        Assert.Equal(27, bash);
        Assert.Equal(9, dodge);
    }

    [Fact]
    public void Stock_LightLoadBonusStopsBelow33Percent()
    {
        int at32 = CombatCalculator.CalcDodge(2, 70, 30, 0, 32, 100, RealmType.Stock);
        int at33 = CombatCalculator.CalcDodge(2, 70, 30, 0, 33, 100, RealmType.Stock);
        Assert.Equal(at32 - 7, at33);
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

        Assert.True(stock.MinDamage > para.MinDamage);
    }

    [Fact]
    public void CalcBSDamage_RacialOnly_StockScalesByLevel_ParadigmDoesNot()
    {
        // Racial-only stealth takes 75%; Stock then applies (level+100)/100 as it
        // does for class stealth, Paradigm doesn't. minDamage 10, maxDamage
        // 30 + (100-50)/10 = 35. min 100+20+20 = 140 → 105; max 100+20+70 = 190 → 142.
        // Stock ×150/100: 157 / 213.
        BSDamageResult stock = CombatCalculator.CalcBSDamage(
            level: 50, stealth: 200, strength: 100,
            weaponMin: 10, weaponMax: 30,
            bsMinBonus: 0, bsMaxBonus: 0, maxDmgBonus: 0,
            hasClassStealth: false, realmType: RealmType.Stock);
        BSDamageResult para = CombatCalculator.CalcBSDamage(
            level: 50, stealth: 200, strength: 100,
            weaponMin: 10, weaponMax: 30,
            bsMinBonus: 0, bsMaxBonus: 0, maxDmgBonus: 0,
            hasClassStealth: false, realmType: RealmType.ParaMud);

        Assert.Equal(157, stock.MinDamage);
        Assert.Equal(213, stock.MaxDamage);
        Assert.Equal(105, para.MinDamage);
        Assert.Equal(142, para.MaxDamage);
    }

    // The min side (+min damage, BS min) can outgrow the max side (+max damage, BS
    // max). Paradigm swaps them; Stock raises the max to the min.
    [Fact]
    public void CalcBSDamage_MinAboveMax_ParadigmSwaps_StockRaisesMax()
    {
        // Level 10, stealth 0, STR 50, weapon 5-5, BS min +40, class stealth:
        //   min (20 + 0 + 10 + 40) = 70 → ×110/100 = 77; max (20 + 10) = 30 → 33.
        BSDamageResult para = CombatCalculator.CalcBSDamage(
            10, 0, 50, 5, 5, bsMinBonus: 40, bsMaxBonus: 0, maxDmgBonus: 0,
            hasClassStealth: true, realmType: RealmType.ParaMud);
        BSDamageResult stock = CombatCalculator.CalcBSDamage(
            10, 0, 50, 5, 5, bsMinBonus: 40, bsMaxBonus: 0, maxDmgBonus: 0,
            hasClassStealth: true, realmType: RealmType.Stock);

        Assert.Equal((33, 77), (para.MinDamage, para.MaxDamage));
        Assert.Equal((77, 77), (stock.MinDamage, stock.MaxDamage));
    }

    // +min damage feeds the backstab minimum on Paradigm; Stock has no +min damage.
    [Fact]
    public void CalcBSDamage_MinDamageBonus_ParadigmOnly()
    {
        BSDamageResult para = CombatCalculator.CalcBSDamage(
            10, 0, 50, 5, 20, 0, 0, 0, hasClassStealth: true, RealmType.ParaMud, minDmgBonus: 5);
        BSDamageResult paraNone = CombatCalculator.CalcBSDamage(
            10, 0, 50, 5, 20, 0, 0, 0, hasClassStealth: true, RealmType.ParaMud);
        BSDamageResult stock = CombatCalculator.CalcBSDamage(
            10, 0, 50, 5, 20, 0, 0, 0, hasClassStealth: true, RealmType.Stock, minDmgBonus: 5);
        BSDamageResult stockNone = CombatCalculator.CalcBSDamage(
            10, 0, 50, 5, 20, 0, 0, 0, hasClassStealth: true, RealmType.Stock);

        Assert.True(para.MinDamage > paraNone.MinDamage);
        Assert.Equal(stockNone, stock);
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
    public void CalcMartialArtsDamage_ParadigmPunch_TruncatesAndAddsMinMaxDamage()
    {
        // Paradigm server formula, level 12, STR 50, +2 min / +3 max damage:
        //   min = 12/8 + 2 = 3, + 2 min damage          = 5   (rounding gave 6)
        //   max = (12+3)/4 + 6 = 9, + 3 max damage       = 12
        MeleeDamageResult r = CombatCalculator.CalcMartialArtsDamage(
            MudAttackType.Punch, RealmType.ParaMud, level: 12, maPlusSkill: 1,
            strength: 50, plusMaxDamage: 3, maPlusDamage: 0, plusMinDamage: 2);

        Assert.Equal(5, r.MinDamage);
        Assert.Equal(12, r.MaxDamage);

        // Level 30: min max(5, 30/6) = 5; max max(12, 30/4) = 12.
        MeleeDamageResult high = CombatCalculator.CalcMartialArtsDamage(
            MudAttackType.Punch, RealmType.ParaMud, level: 30, maPlusSkill: 1,
            strength: 50, plusMaxDamage: 0, maPlusDamage: 0);
        Assert.Equal(5, high.MinDamage);
        Assert.Equal(12, high.MaxDamage);
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
    public void CalcMartialArtsDamage_Paradigm_HasNoSkillTerm()
    {
        // Paradigm server formula: the level band alone — the +MA-skill value adds
        // nothing. L30 punch, STR 50: min max(5, 30/6) = 5; max max(12, 30/4) = 12.
        MeleeDamageResult r = CombatCalculator.CalcMartialArtsDamage(
            MudAttackType.Punch, RealmType.ParaMud, level: 30, maPlusSkill: 30,
            strength: 50, plusMaxDamage: 0, maPlusDamage: 0);

        Assert.Equal(5, r.MinDamage);
        Assert.Equal(12, r.MaxDamage);
    }

    [Fact]
    public void CalcMartialArtsDamage_Paradigm_KickAppliesMultiplier()
    {
        // L30 kick: min 5; max max(10, 30/4) = 10. Kick ×1.33 truncated: 6 / 13.
        MeleeDamageResult r = CombatCalculator.CalcMartialArtsDamage(
            MudAttackType.Kick, RealmType.ParaMud, level: 30, maPlusSkill: 1,
            strength: 50, plusMaxDamage: 0, maPlusDamage: 0);

        Assert.Equal(6, r.MinDamage);
        Assert.Equal(13, r.MaxDamage);
    }

    [Fact]
    public void CalcMartialArtsDamage_Paradigm_SubTwentyBand()
    {
        // L10 (<20), integer division: min 10/8 + 2 = 3; punch max 13/4 + 6 = 9.
        MeleeDamageResult r = CombatCalculator.CalcMartialArtsDamage(
            MudAttackType.Punch, RealmType.ParaMud, level: 10, maPlusSkill: 1,
            strength: 50, plusMaxDamage: 0, maPlusDamage: 0);

        Assert.Equal(3, r.MinDamage);
        Assert.Equal(9, r.MaxDamage);
    }

    [Fact]
    public void CalcMartialArtsDamage_Paradigm_IgnoresStrength()
    {
        // Paradigm's strike damage has no strength term (Stock's does). L30 punch
        // at STR 150 stays at the 5 / 12 base.
        MeleeDamageResult r = CombatCalculator.CalcMartialArtsDamage(
            MudAttackType.Punch, RealmType.ParaMud, level: 30, maPlusSkill: 1,
            strength: 150, plusMaxDamage: 0, maPlusDamage: 0);

        Assert.Equal(5, r.MinDamage);
        Assert.Equal(12, r.MaxDamage);
    }

    [Theory]
    // Level-1 Kang Mystic, STR 80, on the Paradigm server formula: band min
    // 1/8+2 = 2; max (1+3)/4+6 = 7 punch, 1/5+7 = 7 kick, 1/6+7 = 7 jk. STR adds
    // nothing on Paradigm. Kick ×1.33 / jumpkick ×1.66 truncate afterward.
    [InlineData(MudAttackType.Punch, 2, 7)]
    [InlineData(MudAttackType.Kick, 2, 9)]
    [InlineData(MudAttackType.Jumpkick, 3, 11)]
    public void CalcMartialArtsDamage_Paradigm_Level1Mystic(
        MudAttackType attack, int expectedMin, int expectedMax)
    {
        MeleeDamageResult r = CombatCalculator.CalcMartialArtsDamage(
            attack, RealmType.ParaMud, level: 1, maPlusSkill: 1,
            strength: 80, plusMaxDamage: 0, maPlusDamage: 0);

        Assert.Equal(expectedMin, r.MinDamage);
        Assert.Equal(expectedMax, r.MaxDamage);
    }

    [Theory]
    // Fixed martial-arts swing speeds shared by the Item Finder swing model and
    // the Monster Matchup projection. Punch/Kick are realm-independent; only
    // jumpkick differs (Stock 1900 vs ParaMUD/GreaterMUD 2800).
    [InlineData(MudAttackType.Punch, RealmType.Stock, 1150)]
    [InlineData(MudAttackType.Punch, RealmType.ParaMud, 1150)]
    [InlineData(MudAttackType.Kick, RealmType.Stock, 1400)]
    [InlineData(MudAttackType.Kick, RealmType.ParaMud, 1400)]
    [InlineData(MudAttackType.Jumpkick, RealmType.Stock, 1900)]
    [InlineData(MudAttackType.Jumpkick, RealmType.ParaMud, 2800)]
    [InlineData(MudAttackType.Normal, RealmType.ParaMud, 0)]
    public void MartialArtsSpeed_PinsFixedStrikeSpeeds(
        MudAttackType attack, RealmType realm, int expected)
        => Assert.Equal(expected, CombatCalculator.MartialArtsSpeed(attack, realm));

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

    // Swings are hard-capped per realm: 5 on Stock, 6 on Paradigm (GAME_MECHANICS).
    // A max-speed weapon hits the cap exactly — pins that Paradigm gets 6, not 5.
    [Theory]
    [InlineData(RealmType.ParaMud, 6)]
    [InlineData(RealmType.Stock, 5)]
    public void CalcSwings_RawSwingsCappedAtRealmMax(RealmType realm, int cap)
    {
        SwingCalcResult r = CombatCalculator.CalcSwings(
            combatLevel: 10, level: 100, attackSpeed: 1, agility: 200,
            strength: 200, weaponStrReq: 0, currentEncum: 0, maxEncum: 100,
            realmType: realm);

        Assert.Equal(cap, CombatCalculator.MaxSwingsForRealm(realm));
        Assert.Equal((double)cap, r.RawSwings);
        Assert.Equal(10, r.SwingsPerRound.Length);
        Assert.All(r.SwingsPerRound, s => Assert.True(s <= cap));
    }

    // Live-game regression: a L28 Paladin (CombatLVL 6), STR 135, AGI 77, 57% encum,
    // throwing hammers (speed 1100, StrReq 0). Paradigm `stat all` shows normal 7.143
    // and bash 3.572. Pins that the energy term is level × CombatLVL — the class combat
    // term (CombatLVL−2) feeds a (nCombat+2) form; passing the raw
    // CombatLVL into a (combatLevel+2) form (the bug) inflated swings ~26%.
    [Fact]
    public void CalcEnergyUsed_UsesRawCombatLvl_MatchesLiveGameSwings()
    {
        int energy = CombatCalculator.CalcEnergyUsed(
            combatLevel: 6, level: 28, attackSpeed: 1100, agility: 77,
            strength: 135, weaponStrReq: 0, encumPercent: 57);
        Assert.Equal(140, energy);            // 1000 / 140 = 7.143 normal swings (game's stat all)

        SwingCalcResult bash = CombatCalculator.CalcSwings(
            combatLevel: 6, level: 28, attackSpeed: 1100, agility: 77,
            strength: 135, weaponStrReq: 0, currentEncum: 57, maxEncum: 100,
            isBashing: true, realmType: RealmType.ParaMud);
        Assert.Equal(3.57, bash.RawSwings, 2);  // 1000 / (140×2), the reported "should be 3.572"
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

    // A normal attack's crit starts from the stat-and-level crit rating, not just
    // the +Crits from gear: INT 100 is +5 over INT 50 with everything else equal.
    [Fact]
    public void ComputeMeleeOffense_CritIncludesStatRating()
    {
        MeleeOffense avg = CombatCalculator.ComputeMeleeOffense(
            MudAttackType.Normal, RealmType.Stock, level: 30, combatLevel: 5,
            strength: 100, agility: 50, intellect: 50, charm: 50, weaponMin: 10, weaponMax: 20,
            weaponSpeed: 3000, weaponStrReq: 0, plusMaxDamage: 0, plusMinDamage: 0, plusCrits: 2,
            currentEncum: 0, maxEncum: 1000);
        MeleeOffense smart = CombatCalculator.ComputeMeleeOffense(
            MudAttackType.Normal, RealmType.Stock, level: 30, combatLevel: 5,
            strength: 100, agility: 50, intellect: 100, charm: 50, weaponMin: 10, weaponMax: 20,
            weaponSpeed: 3000, weaponStrReq: 0, plusMaxDamage: 0, plusMinDamage: 0, plusCrits: 2,
            currentEncum: 0, maxEncum: 1000);

        Assert.Equal(CharacterCalculator.CalcBaseCritRating(30, 50, 50, 50, RealmType.Stock) + 2, avg.CritChance);
        Assert.Equal(avg.CritChance + 5, smart.CritChance);
    }

    // Ability-1 "Damage" gear raises the low end of a weapon's damage, so it must
    // lift the average — the shared ComputeMeleeOffense (used by both the
    // Calculators tab and Monster Intel) carries plusMinDamage, where the tab's
    // old inline call silently dropped it.
    [Fact]
    public void ComputeMeleeOffense_PlusMinDamage_RaisesAverage()
    {
        MeleeOffense without = CombatCalculator.ComputeMeleeOffense(
            MudAttackType.Normal, RealmType.ParaMud, level: 20, combatLevel: 20,
            strength: 100, agility: 50, intellect: 50, charm: 50, weaponMin: 10, weaponMax: 20, weaponSpeed: 30, weaponStrReq: 0,
            plusMaxDamage: 0, plusMinDamage: 0, plusCrits: 0, currentEncum: 0, maxEncum: 1000);
        MeleeOffense with = CombatCalculator.ComputeMeleeOffense(
            MudAttackType.Normal, RealmType.ParaMud, level: 20, combatLevel: 20,
            strength: 100, agility: 50, intellect: 50, charm: 50, weaponMin: 10, weaponMax: 20, weaponSpeed: 30, weaponStrReq: 0,
            plusMaxDamage: 0, plusMinDamage: 6, plusCrits: 0, currentEncum: 0, maxEncum: 1000);

        Assert.True(with.HasWeapon);
        Assert.True(with.AvgDamage > without.AvgDamage,
            $"+MinDamage should raise avg damage ({with.AvgDamage} vs {without.AvgDamage})");
    }

    // Stock: the engine clamps the chance to 10–99 and its 1–99 roll must come in
    // under it, so a chance of c lands (c − 1) times in 99.
    [Theory]
    [InlineData(-50, 9)]    // floor: 9 in 99
    [InlineData(10, 9)]
    [InlineData(11, 10)]
    [InlineData(50, 49)]    // 49 in 99 = 49.5%
    [InlineData(60, 60)]    // 59 in 99 = 59.6%
    [InlineData(99, 99)]    // ceiling: 98 in 99
    [InlineData(500, 99)]
    public void StockLandingPercent_IsChanceLessOne_OutOf99(int chance, int percent)
    {
        Assert.Equal(percent, CombatCalculator.StockLandingPercent(chance));
    }
}
