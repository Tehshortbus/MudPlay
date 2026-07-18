using System;

namespace FujinTerm.Game.Calculators;

// Pure MajorMUD combat formulas (hit/dodge, magic resist, backstab damage +
// accuracy, attack accuracy, swings). Stock and ParaMUD branches select on
// RealmType; callers resolve the active realm from GameDataCache.ActiveRealm.
// All methods are pure.
public static class CombatCalculator
{
    // ----- Constants -------------------------------------------------------

    // Stock hit-chance floor.
    public const int STOCK_HIT_MIN = 8;
    // ParaMUD hit-chance floor (1 against light armour types <= 6).
    public const int PARAMUD_HIT_MIN = 2;
    // Stock hit-chance ceiling.
    public const int STOCK_HIT_CAP = 99;
    // ParaMUD hit-chance ceiling.
    public const int PARAMUD_HIT_CAP = 100;
    // Stock dodge ceiling.
    public const int STOCK_DODGE_CAP = 95;
    // ParaMUD dodge soft cap (diminishing returns kick in above this).
    public const int PARAMUD_DODGE_SOFTCAP = 55;
    // ParaMUD dodge hard ceiling.
    public const int PARAMUD_DODGE_CAP = 98;
    // Maximum swings landable in a single round.
    public const int MAX_SWINGS = 5;

    // ----- Hit chance ------------------------------------------------------

    // Hit chance against a defender — returns the attacker hit%, the defender
    // dodge%, and the net hit% after dodge, plus the realm caps applied.
    public static HitCalcResult CalculateHitChance(
        int attackerAccuracy, int defenderAC, int defenderDodge,
        int protEvil = 0, int protGood = 0, int perception = 0,
        int bsDefense = 0, int vileWard = 0,
        bool isBackstab = false, bool isVsPlayer = false,
        bool hasShadow = false, bool hasSeeHidden = false,
        EvilLevel evilLevel = EvilLevel.Saint,
        int defenderArmourType = 0, RealmType realmType = RealmType.ParaMud)
    {
        attackerAccuracy = Math.Clamp(attackerAccuracy, 1, 9999);
        defenderAC = Math.Clamp(defenderAC, 0, 9999);
        defenderDodge = Math.Clamp(defenderDodge, 0, 9999);
        protEvil = Math.Clamp(protEvil, 0, 9999);
        perception = Math.Clamp(perception, 0, 9999);
        bsDefense = Math.Clamp(bsDefense, 0, 9999);
        vileWard = Math.Clamp(vileWard, 0, 9999);

        // Shared accuracy temp (ParaMUD paths + Stock normal attacks).
        int accTemp = (attackerAccuracy * attackerAccuracy) / 140;
        if (accTemp < 1) accTemp = 1;

        int hitChance;
        int defense;

        if (defenderAC <= 0)
        {
            hitChance = 100;
        }
        else if (isBackstab)
        {
            if (realmType == RealmType.ParaMud)
            {
                if (isVsPlayer)
                {
                    int adjVileWard = AdjustVileWard(vileWard, evilLevel);
                    defense = defenderAC + protEvil + (int)(perception * 0.8) + adjVileWard;
                    int shadow = hasShadow ? 10 : 0;
                    defense = (defense / 2) + shadow;
                }
                else
                {
                    defense = hasSeeHidden
                        ? defenderAC + bsDefense
                        : (defenderAC / 4) + bsDefense;
                }

                defense = Math.Clamp(defense, 0, 9999);
                hitChance = 100 - ((defense * defense) / accTemp);
            }
            else
            {
                if (isVsPlayer)
                {
                    defense = (defenderAC + perception) / 2;
                }
                else
                {
                    defense = hasSeeHidden
                        ? defenderAC + bsDefense
                        : (defenderAC / 4) + bsDefense;
                }

                defense = Math.Clamp(defense, 0, 9999);
                hitChance = attackerAccuracy - defense;
            }
        }
        else
        {
            int secondaryDef = protEvil + protGood + (hasShadow ? 10 : 0);
            if (realmType == RealmType.ParaMud && vileWard > 0 && evilLevel > EvilLevel.Saint)
            {
                secondaryDef += AdjustVileWard(vileWard, evilLevel);
            }
            defense = defenderAC + secondaryDef;
            hitChance = 100 - ((defense * defense) / accTemp);
        }

        int hitMin = GetHitMin(defenderArmourType, realmType);
        int hitMax = realmType == RealmType.ParaMud ? PARAMUD_HIT_CAP : STOCK_HIT_CAP;
        hitChance = Math.Clamp(hitChance, hitMin, hitMax);

        int dodgePercent = 0;
        if (realmType == RealmType.ParaMud)
        {
            if (defenderDodge > 0 || (perception > 0 && isBackstab && isVsPlayer))
            {
                int dodgeValue = defenderDodge;
                if (isBackstab && isVsPlayer)
                {
                    int dodgeTemp = (defenderDodge + (perception / 2)) / 2;
                    if (hasSeeHidden && defenderDodge - 9 > dodgeTemp)
                        dodgeTemp = defenderDodge;
                    dodgeValue = dodgeTemp;
                }
                dodgePercent = CalcDodgeVSAccuracy(dodgeValue, attackerAccuracy, realmType);
            }
        }
        else if (defenderDodge > 0 && attackerAccuracy > 8)
        {
            dodgePercent = CalcDodgeVSAccuracy(defenderDodge, attackerAccuracy, realmType);
            if (isBackstab) dodgePercent /= 5; // Stock BS dodge penalty
        }

        int dodgeCap = realmType == RealmType.ParaMud ? PARAMUD_DODGE_CAP : STOCK_DODGE_CAP;
        int overallHit = hitChance - (hitChance * dodgePercent / 100);

        return new HitCalcResult(
            HitPercent: hitChance,
            DodgePercent: dodgePercent,
            OverallHitPercent: overallHit,
            HitMinCap: hitMin,
            HitMaxCap: hitMax,
            DodgeCap: dodgeCap);
    }

    // Scale a defender's vile ward by evil level: <=Seedy → 0, <=Criminal →
    // halved, then always divided by 10.
    private static int AdjustVileWard(int vileWard, EvilLevel evilLevel)
    {
        if (vileWard <= 0 || evilLevel <= EvilLevel.Saint) return 0;
        if (evilLevel <= EvilLevel.Seedy) return 0;
        if (evilLevel <= EvilLevel.Criminal) vileWard /= 2;
        return vileWard / 10;
    }

    // Hit-chance floor. ParaMUD: 2 (1 against light armour type <= 6). Stock: 8.
    private static int GetHitMin(int defenderArmourType, RealmType realmType)
    {
        if (realmType == RealmType.ParaMud)
        {
            int min = PARAMUD_HIT_MIN;
            if (defenderArmourType > 0 && defenderArmourType <= 6)
                min -= 1;
            return min;
        }
        return STOCK_HIT_MIN;
    }

    // ----- Dodge -----------------------------------------------------------

    // Raw dodge value from stats (NOT a percentage — feed it to
    // CalcDodgeVSAccuracy). Includes a low-encumbrance bonus when under 33%
    // encumbered.
    public static int CalcDodge(int level, int agility, int charm, int plusDodge,
                                double currentEncum = 0, double maxEncum = -1)
    {
        int dodge = level / 5;
        dodge += (charm - 50) / 5;
        dodge += (agility - 50) / 3;
        dodge += plusDodge;

        if (maxEncum > 0)
        {
            int encumPct = (int)(currentEncum / maxEncum * 100);
            if (encumPct < 33)
            {
                dodge += 10 - (encumPct / 10);
            }
        }

        return dodge;
    }

    // Convert a raw dodge value to a dodge percentage against an accuracy.
    // ParaMUD uses a quadratic with diminishing returns above the soft cap;
    // Stock uses a linear formula.
    public static int CalcDodgeVSAccuracy(int rawDodge, int accuracy, RealmType realmType)
    {
        if (rawDodge <= 0) return 0;

        int dodgePercent;

        if (realmType == RealmType.ParaMud)
        {
            int tempAccy = ((accuracy * accuracy) / 14) / 10;
            if (tempAccy < 1) tempAccy = 1;
            dodgePercent = (rawDodge * rawDodge) / tempAccy;

            if (dodgePercent > PARAMUD_DODGE_SOFTCAP)
            {
                dodgePercent = PARAMUD_DODGE_SOFTCAP
                    + (int)ParaMud_DiminishingReturns(dodgePercent - PARAMUD_DODGE_SOFTCAP, 4.0);
            }
            if (dodgePercent > PARAMUD_DODGE_CAP) dodgePercent = PARAMUD_DODGE_CAP;
        }
        else
        {
            if (accuracy <= 8) return 0;
            int tempAccy = accuracy / 8;
            if (tempAccy < 1) tempAccy = 1;
            dodgePercent = (rawDodge * 10) / tempAccy;
            if (dodgePercent > STOCK_DODGE_CAP) dodgePercent = STOCK_DODGE_CAP;
        }

        return dodgePercent;
    }

    // ParaMUD diminishing-returns curve:
    // triNum = (sqrt(8*value/scale + 1) - 1) / 2, sign-preserving, rescaled by
    // scale.
    public static double ParaMud_DiminishingReturns(double value, double scale)
    {
        if (scale <= 0) return value;

        bool isNeg = value < 0;
        if (isNeg) value = -value;

        double mult = value / scale;
        double triNum = (Math.Sqrt(8.0 * mult + 1.0) - 1.0) / 2.0;

        return isNeg ? -triNum * scale : triNum * scale;
    }

    // ----- Magic resistance ------------------------------------------------

    // Magic resistance: Floor((INT + WIL*3) / 4) + modifiers.
    public static int CalcMR(int intellect, int willpower, int modifiers = 0)
    {
        return (intellect + (willpower * 3)) / 4 + modifiers;
    }

    // ----- Backstab damage -------------------------------------------------

    // Backstab damage range, matching the MajorMUD backstab formula.
    // Core per bound: (level*2) + (stealth/10) + (damage*2) + bsDmgMod, then
    // class-stealth scales by (level+100)/100 while racial-only stealth scales by
    // 75% with no level term (no realm branch — the realm difference lives in the
    // strength folding). weaponMin / weaponMax are the raw weapon bounds;
    // strength folds in here — min gets (STR-100)/10 (doubled in Stock, floored
    // at 0), max gets (STR-50)/10 (ParaMUD floors at 0). maxDmgBonus is the item
    // +max-damage ability sum (Abil 4) only; the strength max bonus is computed
    // internally, so callers pass the item-only value.
    public static BSDamageResult CalcBSDamage(int level, int stealth, int strength,
                                               int weaponMin, int weaponMax,
                                               int bsMinBonus, int bsMaxBonus,
                                               int maxDmgBonus, bool hasClassStealth,
                                               RealmType realmType)
    {
        int minStrBonus = (strength - 100) / 10;
        if (realmType == RealmType.Stock)
            minStrBonus *= 2;
        minStrBonus = Math.Max(minStrBonus, 0);

        int maxStrBonus = (strength - 50) / 10;
        if (realmType == RealmType.ParaMud && maxStrBonus < 0)
            maxStrBonus = 0;                  // GreaterMUD has no negative-strength penalty

        int minDamage = weaponMin + minStrBonus;
        int maxDamage = weaponMax + maxStrBonus + maxDmgBonus;

        int minBS = CalcBSDamageSingle(level, stealth, minDamage, bsMinBonus, hasClassStealth);
        int maxBS = CalcBSDamageSingle(level, stealth, maxDamage, bsMaxBonus, hasClassStealth);

        // bsMinBonus / bsMaxBonus come from independent ability IDs (117/118),
        // so a high min bonus can push the computed min above max — swap so
        // min ≤ max for display and downstream use.
        if (maxBS < minBS)
            (minBS, maxBS) = (maxBS, minBS);

        return new BSDamageResult(minBS, maxBS);
    }

    private static int CalcBSDamageSingle(int level, int stealth, int damage,
                                           int bsDmgMod, bool hasClassStealth)
    {
        int result = (level * 2) + (stealth / 10) + (damage * 2) + bsDmgMod;

        // Engine: class-stealth scales by (level+100)/100; racial-only stealth
        // scales by a flat 75% with no level term.
        return hasClassStealth
            ? (level + 100) * result / 100
            : result * 75 / 100;
    }

    // ----- Melee damage (Normal / Bash / Smash) ----------------------------

    // Per-hit weapon damage range for a Normal / Bash / Smash MajorMUD attack.
    // Strength folds into the base range — max gets (STR-50)/10 (Stock allows
    // negative; ParaMUD floors at 0), min gets (STR-100)/10 (doubled in Stock,
    // floored at 0). Then by type: Bash pre-rolls x1.1 and multiplies x3 (Stock)
    // / x2.5-3 (ParaMUD); Smash pre-rolls x1.2 and multiplies x5. Both
    // truncations match the game's integer Fix. No defender DR is modelled (this
    // is the raw range before mitigation). plusMaxDamage is the item +max damage
    // ability sum (Abil 4); strength is added on top here, so callers pass the
    // item-only value to avoid double-counting.
    public static MeleeDamageResult CalcMeleeDamage(MudAttackType attackType, RealmType realmType,
                                                    int strength, int weaponMin, int weaponMax,
                                                    int plusMaxDamage, int plusMinDamage = 0)
    {
        int strMaxBonus = (strength - 50) / 10;
        if (realmType == RealmType.ParaMud && strMaxBonus < 0)
            strMaxBonus = 0;                  // GreaterMUD has no negative-strength penalty

        int strMinBonus = (strength - 100) / 10;
        if (realmType == RealmType.Stock)
            strMinBonus *= 2;
        strMinBonus = Math.Max(strMinBonus, 0);

        int min = weaponMin + strMinBonus + plusMinDamage;
        int max = weaponMax + strMaxBonus + plusMaxDamage;
        if (min > max) min = max;
        if (min < 0) min = 0;
        if (max < 0) max = 0;

        (double preRoll, double multMin, double multMax) = attackType switch
        {
            MudAttackType.Bash => (1.1, realmType == RealmType.ParaMud ? 2.5 : 3.0, 3.0),
            MudAttackType.Smash => (1.2, 5.0, 5.0),
            _ => (1.0, 1.0, 1.0),
        };

        // Pre-roll multiplier is applied first and truncated, then the type
        // multiplier — the same order MMUD uses (Fix at each step).
        if (preRoll > 1.0)
        {
            min = (int)(min * preRoll);
            max = (int)(max * preRoll);
        }
        min = (int)(min * multMin);
        max = (int)(max * multMax);

        return new MeleeDamageResult(min, max);
    }

    // Per-hit damage range for a Mystic martial-arts MajorMUD attack (Punch /
    // Kick / Jumpkick) for the realm selected by realmType. Requires a positive
    // maPlusSkill; returns a zero range otherwise.
    //
    // maPlusSkill is the item-granted per-attack martial-arts SKILL bonus, NOT
    // the character's Martial Arts skill stat (that stat feeds accuracy, never
    // this damage formula). No stock ability grants a +MA-skill bonus, so it is
    // normally 0; the combat-calc path floors it to 1, which is what the
    // Character Info panel feeds. Passing the Martial Arts skill stat here
    // inflates the damage by that stat's whole magnitude (~20-30x).
    //
    // Stock scales the skill by the level (capped at 20):
    //   min = skill*nTemp/8 + 2; punch max = skill*(nTemp+3)/4 + 6,
    //   kick max = skill*nTemp/6 + 7, jumpkick max = skill*nTemp/6 + 8
    //   (all Fix() truncated).
    // GreaterMUD / ParaMUD uses a level-driven band with the skill added flat —
    //   min = round(lvl/8+2) + skill below 20 (else round(lvl/6) floored 5);
    //   per-type max round((lvl+3)/4+6) / round(lvl/5+7) / round(lvl/6+7) below
    //   20 (else round(lvl/4) floored 12/10/10) + skill — rounding half-to-even.
    // Strength then folds in exactly as CalcMeleeDamage does (max gets
    // (STR-50)/10, min gets (STR-100)/10 doubled in Stock, floored at 0; ParaMUD
    // has no negative-strength penalty). After the range is clamped, the item
    // martial-arts damage bonus (maPlusDamage, Abil 92/93/94) is added to both
    // bounds, then the kick x1.33 / jumpkick x1.66 multiplier (truncated).
    // plusMaxDamage is the item +max-damage sum (Abil 4); strength is added
    // internally.
    public static MeleeDamageResult CalcMartialArtsDamage(MudAttackType attackType, RealmType realmType,
                                                          int level, int maPlusSkill, int strength,
                                                          int plusMaxDamage, int maPlusDamage)
    {
        if (maPlusSkill <= 0)
            return new MeleeDamageResult(0, 0);

        int min, max;
        if (realmType == RealmType.ParaMud)
        {
            // GreaterMUD: a level-driven band with the skill added flat (not the
            // Stock skill×level scaling).
            min = GmudMaBand(level, level / 8.0 + 2, level / 6.0, floor: 5) + maPlusSkill;
            max = attackType switch
            {
                MudAttackType.Punch => GmudMaBand(level, (level + 3) / 4.0 + 6, level / 4.0, floor: 12),
                MudAttackType.Kick => GmudMaBand(level, level / 5.0 + 7, level / 4.0, floor: 10),
                MudAttackType.Jumpkick => GmudMaBand(level, level / 6.0 + 7, level / 4.0, floor: 10),
                _ => 0,
            } + maPlusSkill;
        }
        else
        {
            // Stock: skill scales by level (capped at 20), per the Fix() formula.
            int nTemp = Math.Min(level, 20);
            min = (maPlusSkill * nTemp) / 8 + 2;
            max = attackType switch
            {
                MudAttackType.Punch => (maPlusSkill * (nTemp + 3)) / 4 + 6,
                MudAttackType.Kick => (maPlusSkill * nTemp) / 6 + 7,
                MudAttackType.Jumpkick => (maPlusSkill * nTemp) / 6 + 8,
                _ => 0,
            };
        }

        int strMaxBonus = (strength - 50) / 10;
        if (realmType == RealmType.ParaMud && strMaxBonus < 0)
            strMaxBonus = 0;                  // GreaterMUD has no negative-strength penalty

        int strMinBonus = (strength - 100) / 10;
        if (realmType == RealmType.Stock)
            strMinBonus *= 2;
        strMinBonus = Math.Max(strMinBonus, 0);

        min += strMinBonus;
        max += strMaxBonus + plusMaxDamage;
        if (min > max) min = max;
        if (min < 0) min = 0;
        if (max < 0) max = 0;

        // Item martial-arts damage bonus applies after the range is settled.
        min += maPlusDamage;
        max += maPlusDamage;

        // Stock pre-roll multiplier, truncated to match the game's Fix.
        double mult = attackType switch
        {
            MudAttackType.Kick => 1.33,
            MudAttackType.Jumpkick => 1.66,
            _ => 1.0,
        };
        if (mult > 1.0)
        {
            min = (int)(min * mult);
            max = (int)(max * mult);
        }

        return new MeleeDamageResult(min, max);
    }

    // Fixed attack speed (energy cost per strike) for a bare-handed martial-arts
    // attack — these have no weapon speed of their own, so their swing rate comes
    // from this constant instead. MajorMUD's values: Punch 1150, Kick 1400;
    // Jumpkick differs by realm (Stock is faster at 1900, ParaMUD/GreaterMUD 2800,
    // older 2900 builds aside). Returns 0 for a non-martial-arts type.
    public static int MartialArtsSpeed(MudAttackType attackType, RealmType realmType) => attackType switch
    {
        MudAttackType.Punch => 1150,
        MudAttackType.Kick => 1400,
        MudAttackType.Jumpkick => realmType == RealmType.ParaMud ? 2800 : 1900,
        _ => 0,
    };

    // One GreaterMUD martial-arts level band: under level 20 the subTwenty term
    // is used; at 20+ the twentyPlus term is used and floored at floor. Both
    // round half-to-even, mirroring the game's Double→Long assignment (which uses
    // /, not the Fix() the Stock branch uses).
    private static int GmudMaBand(int level, double subTwenty, double twentyPlus, int floor)
    {
        if (level < 20)
            return (int)Math.Round(subTwenty, MidpointRounding.ToEven);
        int t = (int)Math.Round(twentyPlus, MidpointRounding.ToEven);
        return t < floor ? floor : t;
    }

    // Backstab accuracy. ParaMUD:
    //   (Stealth/3) + ((AGI-50+LVL)/2) + 15 + PlusBSAccy + NormAccy, minus 15
    //   when STR is under the weapon requirement.
    // Stock:
    //   (Stealth+AGI)/2 + PlusBSAccy/2, +5 with class stealth else -15.
    // Encumbrance is not applied here — the displayed Stealth stat already
    // incorporates it.
    public static int CalcBackstabAccuracy(int stealth, int agility, int level,
                                            int strength, int weaponStrReq,
                                            int plusBSAccuracy, int plusNormalAccuracy,
                                            bool hasClassStealth, RealmType realmType)
    {
        int accy;

        if (realmType == RealmType.ParaMud)
        {
            accy = (stealth / 3) + ((agility - 50 + level) / 2) + 15 + plusBSAccuracy;

            // Equipment +Accy is always added in ParaMUD, even when StrReq fails.
            accy += plusNormalAccuracy;

            if (strength < weaponStrReq)
                accy -= 15;
        }
        else
        {
            accy = (stealth + agility) / 2 + (plusBSAccuracy / 2);
            if (hasClassStealth)
                accy += 5;
            else
                accy -= 15;
        }

        return accy;
    }

    // ----- Attack accuracy -------------------------------------------------

    // Level + combat-level base accuracy term shared by Stock and ParaMUD.
    // Returns (sqrtPart, basePart) so renderers can show the intermediate
    // sqrt(level). Stock applies an integer-sqrt precision-correction loop that
    // ParaMUD skips.
    public static (int sqrtPart, int basePart) CalcBaseAccuracy(int level, int nCombatLevel, RealmType realm)
    {
        if (level <= 0) return (0, 0);

        int sqrtPart = (int)Math.Sqrt(level);
        if (realm != RealmType.ParaMud)
        {
            while ((sqrtPart + 1) * (sqrtPart + 1) <= level) sqrtPart++;
        }

        if (nCombatLevel <= 0) return (sqrtPart, sqrtPart);

        int basePart = sqrtPart * (nCombatLevel - 1);
        basePart = (basePart + (nCombatLevel * 2) + (level / 2) - 2) * 2;
        return (sqrtPart, basePart);
    }

    // Accuracy for a given attack type (Normal / Bash / Smash), aiming to match
    // the in-game "stat all" Accy column. Branches heavily by realm: pity-accy
    // and stat weighting differ, ParaMUD even-rounds the gear sum and applies a
    // 1.5x smash multiplier plus a weapon-StrReq penalty. Bash applies -15, smash
    // -25 (both realms). totalWornAccy is the summed Accy field of all worn
    // items; maxSingleAbil22 is the highest single accuracy ability
    // (22/105/106).
    public static int CalcAccuracy(MudAttackType attackType, RealmType realm,
                                    int level, int nCombatLevel,
                                    int strength, int agility, int intellect, int charm,
                                    int totalWornAccy, int maxSingleAbil22,
                                    int currentEncum, int maxEncum,
                                    int weaponStrReq = 0)
    {
        bool isParaMud = (realm == RealmType.ParaMud);
        bool isBashOrSmash = (attackType == MudAttackType.Bash || attackType == MudAttackType.Smash);

        int accyCalc = 0;
        int wornAccy = totalWornAccy;
        int plusAccy = maxSingleAbil22;

        if (wornAccy < 0) wornAccy = 0;

        // Pity accy — Stock only.
        if (!isParaMud && wornAccy == 0) wornAccy = 1;

        int encumPct = (maxEncum > 0) ? (int)((long)currentEncum * 100 / maxEncum) : 1;
        if (encumPct <= 0) encumPct = 1;

        if (isParaMud)
        {
            // < 33%: 15 - (encumPct-1)/10 (15→14→13→12 at 11/21/31); ≥ 33%: +1.
            if (encumPct < 33)
                accyCalc += 15 - ((encumPct - 1) / 10);
            else
                accyCalc += 1;
        }
        else
        {
            // Stock: only < 33% gets a bonus; ≥ 33% adds nothing.
            if (encumPct < 33)
                accyCalc += 15 - (encumPct / 10);
        }

        // Stock MUD rounding applies to the encum bonus only, before the base.
        if (!isParaMud)
            accyCalc = (accyCalc / 2) * 2;

        var (_, basePart) = CalcBaseAccuracy(level, nCombatLevel, realm);
        accyCalc += basePart;

        // Strength: Stock always; ParaMUD only bash/smash.
        if (strength > 0 && (!isParaMud || isBashOrSmash))
            accyCalc += (strength - 50) / 3;

        // Agility: Stock /6 always; ParaMUD bash/smash /6, normal /3.
        if (agility > 0)
        {
            if (!isParaMud || isBashOrSmash)
                accyCalc += (agility - 50) / 6;
            else
                accyCalc += (agility - 50) / 3;
        }

        // Intellect: ParaMUD only, not bash/smash.
        if (intellect > 0 && isParaMud && !isBashOrSmash)
            accyCalc += (intellect - 50) / 6;

        // Charm: ParaMUD only, not bash/smash.
        if (charm > 0 && isParaMud && !isBashOrSmash)
            accyCalc += (charm - 50) / 10;

        // Gear: worn Accy + abil22 sum; ParaMUD even-rounds the combined sum.
        int gearAccy = wornAccy + plusAccy;
        if (isParaMud)
            gearAccy = (gearAccy / 2) * 2;
        int result = accyCalc + gearAccy;

        // ParaMUD smash 1.5x multiplier, before penalties.
        if (isParaMud && attackType == MudAttackType.Smash)
            result = (result * 3) / 2;

        if (attackType == MudAttackType.Bash)
            result -= 15;
        else if (attackType == MudAttackType.Smash)
            result -= 25;

        // Weapon StrReq penalty: ParaMUD only, all attack types.
        if (isParaMud && weaponStrReq > 0 && strength < weaponStrReq)
            result -= 15;

        return result;
    }

    // ----- Swings ----------------------------------------------------------

    // Energy per swing. Core
    // (speed*1000) / (((Level*(Combat+2)+45) * (AGI+150)) / 6), then a
    // strength-under-requirement penalty, a speed modifier (skipped during
    // backstab), and an encumbrance adjustment.
    public static int CalcEnergyUsed(int combatLevel, int level, int attackSpeed, int agility,
                                      int strength = 0, int weaponStrReq = 0,
                                      int encumPercent = -1, int speedModifier = 100,
                                      bool hasSlowness = false, bool isBackstab = false)
    {
        int speed = hasSlowness ? (attackSpeed * 3) / 2 : attackSpeed;

        int divisor = ((level * (combatLevel + 2)) + 45) * (agility + 150) / 6;
        if (divisor < 1) divisor = 1;
        int energy = (speed * 1000) / divisor;

        if (strength > 0 && strength < weaponStrReq)
        {
            energy = (((weaponStrReq - strength) * 3 + 200) * energy) / 200;
        }

        if (speedModifier > 0 && speedModifier != 100 && !isBackstab)
        {
            energy = (energy * speedModifier) / 100;
        }

        if (encumPercent >= 0)
        {
            energy = (energy * (encumPercent / 2 + 75)) / 100;
        }

        return energy;
    }

    // Swings per round across a 10-round simulation, carrying the energy
    // remainder forward each round (which produces the repeating swing pattern).
    // Includes the Quick & Deadly crit bonus.
    public static SwingCalcResult CalcSwings(int combatLevel, int level, int attackSpeed,
                                              int agility, int strength, int weaponStrReq,
                                              int currentEncum, int maxEncum,
                                              int speedModifier = 100, bool hasSlowness = false,
                                              bool isBashing = false, RealmType realmType = RealmType.ParaMud)
    {
        int encumPercent = maxEncum > 0
            ? Math.Clamp((int)((long)currentEncum * 100 / maxEncum), 0, 100)
            : 0;

        int energy = CalcEnergyUsed(combatLevel, level, attackSpeed, agility,
                                     strength, weaponStrReq, encumPercent, speedModifier,
                                     hasSlowness);

        if (isBashing) energy *= 2;
        if (energy < 1) energy = 1;

        int qndBonus = CalcQuickAndDeadlyBonus(agility, energy, encumPercent, realmType);

        double rawSwings = 1000.0 / energy;
        if (rawSwings > MAX_SWINGS) rawSwings = MAX_SWINGS;

        var swingsPerRound = new int[10];
        var energyRemaining = new int[10];
        int remaining = 1000;

        for (int round = 0; round < 10; round++)
        {
            int swings = remaining / energy;
            if (swings > MAX_SWINGS) swings = MAX_SWINGS;
            swingsPerRound[round] = swings;
            remaining = (remaining % energy) + 1000;
            energyRemaining[round] = remaining - 1000; // carry into next round
        }

        return new SwingCalcResult(
            EnergyPerSwing: energy,
            RawSwings: rawSwings,
            EncumPercent: encumPercent,
            QnDCritBonus: qndBonus,
            SwingsPerRound: swingsPerRound,
            EnergyRemaining: energyRemaining);
    }

    // Quick & Deadly crit bonus from a fast weapon at low encumbrance.
    // ParaMUD: (1000 - energy*5) / 50. Stock: (200 - energy) + (AGI-50)/10,
    // capped at 20, halved at >= 33% encumbrance. Zero at energy >= 200 (and,
    // Stock-only, above 66% encum).
    public static int CalcQuickAndDeadlyBonus(int agility, int energyUsed, int encumPercent,
                                               RealmType realmType)
    {
        if (energyUsed >= 200) return 0;
        if (encumPercent > 66 && realmType != RealmType.ParaMud) return 0;

        if (realmType == RealmType.ParaMud)
        {
            int energyRemain = 1000 - (energyUsed * 5);
            return energyRemain / 50;
        }
        else
        {
            int bonus = (200 - energyUsed) + ((agility - 50) / 10);
            if (bonus > 20) bonus = 20;
            if (encumPercent >= 33) bonus /= 2;
            return bonus;
        }
    }

    // Critical-hit chance: the gear / quest crit rating (ability 58, already
    // aggregated) plus the Quick & Deadly bonus, then MajorMUD's
    // diminishing-returns handling above 40 — ParaMUD (GreaterMUD) hard-caps at
    // 65; Stock compresses the overflow to 40 + (over-40)/3, capped at 99.
    // Floored at 0.
    public static int CalcCritChance(int critRating, int quickAndDeadlyBonus, RealmType realmType)
    {
        int crit = critRating + quickAndDeadlyBonus;
        if (crit > 40)
        {
            if (realmType == RealmType.ParaMud)
            {
                if (crit > 65) crit = 65;
            }
            else
            {
                crit = 40 + ((crit - 40) / 3);
                if (crit > 99) crit = 99;
            }
        }
        return crit < 0 ? 0 : crit;
    }
}
