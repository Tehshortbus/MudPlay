using System;

namespace MudPlay.Game.Calculators;

// Pure MajorMUD combat formulas (hit/dodge, magic resist, backstab damage +
// accuracy, attack accuracy, swings). Stock and ParaMUD branches select on
// RealmType; callers resolve the active realm from GameDataCache.ActiveRealm.
// All methods are pure.
public static class CombatCalculator
{
    // ----- Constants -------------------------------------------------------

    // Stock landing chance: the engine clamps the chance to 10–99, rolls 1–99 (its
    // roll never returns the top of the range it is asked for) and hits only when
    // the roll is under the chance. So a chance of c lands (c − 1) times in 99: a
    // floor of 9/99 and a ceiling of 98/99, 9% and 99% as whole percents
    // (GAME_MECHANICS "To-hit floor — the minimum chance a monster can ever land, by
    // realm and armour type").
    private const int STOCK_ENGINE_CLAMP_MIN = 10, STOCK_ENGINE_CLAMP_MAX = 99, STOCK_ROLL_FACES = 99;
    public const int STOCK_HIT_MIN = 9;
    // ParaMUD hit-chance floor (1 against light armour types <= 6).
    public const int PARAMUD_HIT_MIN = 2;
    public const int STOCK_HIT_CAP = 99;

    // The whole-percent chance a Stock attack lands, from the engine's raw chance.
    public static int StockLandingPercent(int chance) =>
        (int)Math.Round(
            (Math.Clamp(chance, STOCK_ENGINE_CLAMP_MIN, STOCK_ENGINE_CLAMP_MAX) - 1) * 100.0 / STOCK_ROLL_FACES,
            MidpointRounding.AwayFromZero);
    // ParaMUD hit-chance ceiling.
    public const int PARAMUD_HIT_CAP = 100;
    // Stock dodge ceiling.
    public const int STOCK_DODGE_CAP = 95;
    // ParaMUD dodge soft cap (diminishing returns kick in above this).
    public const int PARAMUD_DODGE_SOFTCAP = 55;
    // ParaMUD dodge hard ceiling.
    public const int PARAMUD_DODGE_CAP = 98;
    // Maximum swings landable in a single round — realm-dependent: hard-capped
    // at 5 on Stock, 6 on Paradigm (GAME_MECHANICS "Player physical swings per
    // round: cap and energy formula"). A fixed 5 under-counted every Paradigm
    // swing/DPS/rounds figure.
    public static int MaxSwingsForRealm(RealmType realm) => realm == RealmType.ParaMud ? 6 : 5;

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
            // ProtGood and VileWard are realm-EXCLUSIVE (user-confirmed): Stock uses
            // Protection-from-Good (ability 25); Paradigm dropped it and uses VileWard
            // (ability 1113) instead — its server ignores ProtGood, and its gear carries
            // none, so ProtGood must never count on Paradigm even if a stray value leaks
            // in. VileWard is already Paradigm-only below. See GAME_MECHANICS "Armour
            // Class contributions — shadow, Prot-Evil, Prot-Good, VileWard".
            int realmProtGood = realmType == RealmType.ParaMud ? 0 : protGood;
            int secondaryDef = protEvil + realmProtGood + (hasShadow ? 10 : 0);
            if (realmType == RealmType.ParaMud && vileWard > 0 && evilLevel > EvilLevel.Saint)
            {
                secondaryDef += AdjustVileWard(vileWard, evilLevel);
            }
            defense = defenderAC + secondaryDef;
            hitChance = 100 - ((defense * defense) / accTemp);
        }

        int hitMin = GetHitMin(defenderArmourType, realmType);
        int hitMax = realmType == RealmType.ParaMud ? PARAMUD_HIT_CAP : STOCK_HIT_CAP;
        hitChance = realmType == RealmType.ParaMud
            ? Math.Clamp(hitChance, hitMin, hitMax)
            : StockLandingPercent(hitChance);

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

        return new HitCalcResult(
            HitPercent: hitChance,
            DodgePercent: dodgePercent,
            HitMinCap: hitMin,
            HitMaxCap: hitMax,
            DodgeCap: dodgeCap,
            OverallHitChance: hitChance * (100 - dodgePercent) / 100.0);
    }

    // Scale a defender's vile ward by evil level: <=Seedy → 0, <=Criminal →
    // halved, then always divided by 10. Public so callers computing a displayed
    // "effective AC vs an evil target" (Monster Intel) convert the raw ward the
    // same way the hit-chance math does, instead of re-deriving the ~10:1 rate.
    public static int AdjustVileWard(int vileWard, EvilLevel evilLevel)
    {
        if (vileWard <= 0 || evilLevel <= EvilLevel.Saint) return 0;
        if (evilLevel <= EvilLevel.Seedy) return 0;
        if (evilLevel <= EvilLevel.Criminal) vileWard /= 2;
        return vileWard / 10;
    }

    // Hit-chance floor. ParaMUD: 2 (1 against light armour type <= 6). Stock: 9.
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
    // CalcDodgeVSAccuracy). Includes the light-load bonus (HasLightLoadBonus):
    // under 33% encumbered on Stock, up to 33% on Paradigm.
    public static int CalcDodge(int level, int agility, int charm, int plusDodge,
                                double currentEncum = 0, double maxEncum = -1,
                                RealmType realmType = RealmType.Stock)
    {
        int dodge = level / 5;
        dodge += (charm - 50) / 5;
        dodge += (agility - 50) / 3;
        dodge += plusDodge;

        if (maxEncum > 0)
        {
            int encumPct = (int)(currentEncum / maxEncum * 100);
            if (HasLightLoadBonus(encumPct, realmType))
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

    // ----- Strength onto weapon damage -------------------------------------

    // GAME_MECHANICS "Melee damage bonus": Stock doubles the min term and lets the
    // max term go negative below 50 STR; Paradigm never lets either drop below 0.
    public static int StrMinDamageBonus(int strength, RealmType realmType)
    {
        int bonus = (strength - 100) / 10;
        if (realmType == RealmType.Stock) bonus *= 2;
        return Math.Max(bonus, 0);
    }

    public static int StrMaxDamageBonus(int strength, RealmType realmType)
    {
        int bonus = (strength - 50) / 10;
        return realmType == RealmType.ParaMud ? Math.Max(bonus, 0) : bonus;
    }

    // ----- Backstab damage -------------------------------------------------

    // Backstab damage range (GAME_MECHANICS "Backstab damage and accuracy").
    // Core per bound: (level*2) + (stealth/10) + (damage*2) + bsDmgMod; racial-only
    // stealth then takes 75%, and the (level+100)/100 scale applies on Stock always
    // but on Paradigm only with class stealth. weaponMin / weaponMax are the raw
    // weapon bounds; strength folds in here — min gets (STR-100)/10 (doubled in
    // Stock, floored at 0), max gets (STR-50)/10 (ParaMUD floors at 0).
    // maxDmgBonus is the item +max-damage ability sum (Abil 4) only; minDmgBonus
    // (Abil 1) counts on Paradigm only — Stock has no +min damage.
    public static BSDamageResult CalcBSDamage(int level, int stealth, int strength,
                                               int weaponMin, int weaponMax,
                                               int bsMinBonus, int bsMaxBonus,
                                               int maxDmgBonus, bool hasClassStealth,
                                               RealmType realmType, int minDmgBonus = 0)
    {
        (int minSide, int maxSide) = CalcBSSides(level, stealth, strength, weaponMin, weaponMax,
            bsMinBonus, bsMaxBonus, maxDmgBonus, hasClassStealth, realmType, minDmgBonus);
        return ResolveBSRange(minSide, maxSide, realmType);
    }

    // The two backstab sides before the realm decides which is the min: the side fed
    // by the weapon min (+min damage, BS min) and the side fed by the weapon max
    // (+max damage, BS max). Each side is a plain sum of its bonuses, which is what
    // lets Find Best push one side at a time.
    public static (int MinSide, int MaxSide) CalcBSSides(int level, int stealth, int strength,
                                                         int weaponMin, int weaponMax,
                                                         int bsMinBonus, int bsMaxBonus,
                                                         int maxDmgBonus, bool hasClassStealth,
                                                         RealmType realmType, int minDmgBonus = 0)
    {
        int minStrBonus = StrMinDamageBonus(strength, realmType);
        int maxStrBonus = StrMaxDamageBonus(strength, realmType);

        int minDamage = weaponMin + minStrBonus
            + (realmType == RealmType.ParaMud ? minDmgBonus : 0);
        int maxDamage = weaponMax + maxStrBonus + maxDmgBonus;

        return (CalcBSDamageSingle(level, stealth, minDamage, bsMinBonus, hasClassStealth, realmType),
                CalcBSDamageSingle(level, stealth, maxDamage, bsMaxBonus, hasClassStealth, realmType));
    }

    // The sides are fed by independent bonuses, so the min side can come out higher.
    // Paradigm then swaps them; Stock raises the max to the min.
    public static BSDamageResult ResolveBSRange(int minSide, int maxSide, RealmType realmType)
    {
        if (minSide <= maxSide) return new BSDamageResult(minSide, maxSide);
        return realmType == RealmType.ParaMud
            ? new BSDamageResult(maxSide, minSide)
            : new BSDamageResult(minSide, minSide);
    }

    private static int CalcBSDamageSingle(int level, int stealth, int damage,
                                           int bsDmgMod, bool hasClassStealth, RealmType realmType)
    {
        int result = (level * 2) + (stealth / 10) + (damage * 2) + bsDmgMod;
        if (!hasClassStealth) result = result * 75 / 100;
        return hasClassStealth || realmType == RealmType.Stock
            ? (level + 100) * result / 100
            : result;
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
        int strMaxBonus = StrMaxDamageBonus(strength, realmType);
        int strMinBonus = StrMinDamageBonus(strength, realmType);

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

    // How many times a defender's DR counts against one hit. Stock takes DR off the
    // rolled damage BEFORE the bash ×3 / smash ×5, so it's multiplied with the hit;
    // Paradigm takes it off after (GAME_MECHANICS "Bash and smash damage vs DR").
    public static int DrMultiplierFor(MudAttackType attackType, RealmType realmType) =>
        realmType != RealmType.Stock ? 1 : attackType switch
        {
            MudAttackType.Bash => 3,
            MudAttackType.Smash => 5,
            _ => 1,
        };

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
    // Paradigm uses a level-driven band, integer division throughout, with the
    // item +min / +max damage added to its own bound (the server's own formula,
    // GAME_MECHANICS "Martial-arts strike damage") —
    //   min = lvl/8+2 below 20 (else lvl/6 floored 5) + plusMinDamage;
    //   per-type max (lvl+3)/4+6 / lvl/5+7 / lvl/6+7 below 20 (else lvl/4
    //   floored 12/10/10) + plusMaxDamage. No skill term.
    // On Stock, strength then folds in as CalcMeleeDamage does (max gets
    // (STR-50)/10, min gets (STR-100)/10 doubled, floored at 0); Paradigm adds no
    // strength. After the range is clamped, the item martial-arts damage bonus
    // (maPlusDamage, Abil 92/93/94) is added to both bounds, then the kick x1.33 /
    // jumpkick x1.66 multiplier (truncated) — on Paradigm too, pending a check
    // against a Mystic's `stat all`.
    // plusMaxDamage is the item +max-damage sum (Abil 4); strength (Stock only) is
    // added internally.
    public static MeleeDamageResult CalcMartialArtsDamage(MudAttackType attackType, RealmType realmType,
                                                          int level, int maPlusSkill, int strength,
                                                          int plusMaxDamage, int maPlusDamage,
                                                          int plusMinDamage = 0)
    {
        if (maPlusSkill <= 0)
            return new MeleeDamageResult(0, 0);

        int min, max;
        if (realmType == RealmType.ParaMud)
        {
            // Paradigm: a level-driven band (not the Stock skill×level scaling),
            // +min damage on the low end; +max damage joins the high end below.
            min = GmudMaBand(level, level / 8 + 2, level / 6, floor: 5) + plusMinDamage;
            max = attackType switch
            {
                MudAttackType.Punch => GmudMaBand(level, (level + 3) / 4 + 6, level / 4, floor: 12),
                MudAttackType.Kick => GmudMaBand(level, level / 5 + 7, level / 4, floor: 10),
                MudAttackType.Jumpkick => GmudMaBand(level, level / 6 + 7, level / 4, floor: 10),
                _ => 0,
            };
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

        // Stock folds strength in as CalcMeleeDamage does; Paradigm's strike damage
        // has no strength term.
        if (realmType == RealmType.Stock)
        {
            min += StrMinDamageBonus(strength, realmType);
            max += StrMaxDamageBonus(strength, realmType);
        }
        max += plusMaxDamage;
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

    // One Paradigm martial-arts level band: under level 20 the subTwenty term is
    // used; at 20+ the twentyPlus term, floored at floor.
    private static int GmudMaBand(int level, int subTwenty, int twentyPlus, int floor) =>
        level < 20 ? subTwenty : Math.Max(floor, twentyPlus);

    // Backstab accuracy. ParaMUD:
    //   (Stealth/3) + ((AGI-50+LVL)/2) + 15 + PlusBSAccy + NormAccy, minus 15
    //   when STR is under the weapon requirement.
    // Stock:
    //   (Stealth+AGI)/2 + PlusBSAccy/2, +5 with class stealth else -15, + NormAccy.
    // NormAccy is the realm's normal-accuracy bonus: worn accuracy + abilities on
    // Paradigm, only the accuracy-ability bonus (highest of 22/105/106) on Stock;
    // the callers pick it.
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
            accy += plusNormalAccuracy;
        }

        return accy;
    }

    // Whether a load (whole percent of max, truncated) still earns the light-load
    // accuracy / dodge bonus. Paradigm keeps it at exactly 33%: a character at
    // 964/2880 (33%) read Attack 43 / Dodge 9 in `stat all`, both only reachable with
    // the bonus. Stock stops below 33%.
    private static bool HasLightLoadBonus(int encumPct, RealmType realm) =>
        realm == RealmType.ParaMud ? encumPct <= 33 : encumPct < 33;

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
    // and stat weighting differ, ParaMUD applies a 1.5x smash multiplier plus a
    // weapon-StrReq penalty. Bash applies -15, smash -25 (both realms). totalWornAccy is the summed Accy field of all worn
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
            // Up to 33%: 14 - (encumPct-1)/10 (14→13→12→11 at 11/21/31); past it,
            // nothing. Fits every Paradigm `stat all` checked (2026-09-30): an earlier
            // 15 here, with +1 past 33%, read one high on Attack and Bash alike.
            if (HasLightLoadBonus(encumPct, realm))
                accyCalc += 14 - ((encumPct - 1) / 10);
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

        // Gear: worn Accy + the accuracy abilities, unrounded on both realms (a
        // Paradigm even-round of the sum didn't match `stat all` with worn 11 / 10 and
        // a +5 ability on and off, 2026-09-30).
        int result = accyCalc + wornAccy + plusAccy;

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

        // Combat term is level × the class CombatLVL. Written with Combat = CombatLVL − 2
        // in a (Combat + 2) term it comes to the same thing; we pass the raw CombatLVL and
        // drop the +2. Passing the raw CombatLVL into a (combatLevel + 2) form was the bug:
        // an extra level×2 in the divisor undercut energy ~26% and inflated every
        // swing/DPS/rounds figure (report: L28 Paladin, throwing hammers speed 1100, 57%
        // encum — read 9 swings uncapped / bash 4.5 where the game shows 7.143 / 3.572).
        // Accuracy keeps the raw CombatLVL (CalcAccuracy), which the game does too.
        int divisor = ((level * combatLevel) + 45) * (agility + 150) / 6;
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

        int maxSwings = MaxSwingsForRealm(realmType);
        double rawSwings = 1000.0 / energy;
        if (rawSwings > maxSwings) rawSwings = maxSwings;

        var swingsPerRound = new int[10];
        var energyRemaining = new int[10];
        int remaining = 1000;

        for (int round = 0; round < 10; round++)
        {
            int swings = remaining / energy;
            if (swings > maxSwings) swings = maxSwings;
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

    // Damage / swings / crit for one melee attack type, from resolved weapon +
    // stat inputs. The shared core behind BOTH the Character Workshop Calculators
    // tab's weapon offense and CharacterCalculator.BuildNormalAttackProfile
    // (Monster Intel's rounds-to-kill) — kept in one place so a +MinDamage source,
    // a Bash swing rate, or the Smash single-swing can't read differently on the
    // two surfaces. Accuracy is deliberately NOT computed here: the Calculators
    // tab folds martial-arts strikes into its accuracy path, which has no analogue
    // on the weapon side, so each caller resolves accuracy itself. combatLevel is
    // the class table's raw CombatLVL; plusCrits is the +Crits from gear, race,
    // class, quests and buffs, and the stat-and-level crit rating is added here.
    public static MeleeOffense ComputeMeleeOffense(
        MudAttackType type, RealmType realmType, int level, int combatLevel,
        int strength, int agility, int intellect, int charm,
        int weaponMin, int weaponMax, int weaponSpeed, int weaponStrReq,
        int plusMaxDamage, int plusMinDamage, int plusCrits, int currentEncum, int maxEncum)
    {
        bool hasWeapon = weaponMax > 0;

        MeleeDamageResult dmg = CalcMeleeDamage(
            type, realmType, strength, weaponMin, weaponMax, plusMaxDamage, plusMinDamage);
        int avgDamage = hasWeapon ? (dmg.MinDamage + dmg.MaxDamage) / 2 : 0;

        SwingCalcResult swings = CalcSwings(
            combatLevel, level, weaponSpeed, agility, strength, weaponStrReq,
            currentEncum, maxEncum, isBashing: type == MudAttackType.Bash, realmType: realmType);
        // Smash locks the round to a single swing regardless of weapon speed.
        // A no-weapon projection reads 0 swings (the DPS gate + the UI both key
        // off HasWeapon, so the raw value is never shown for an empty hand).
        double swingsPerRound = !hasWeapon ? 0
            : type == MudAttackType.Smash ? 1 : swings.RawSwings;

        // Crit folds into DPS only for the plain Normal attack (Bash / Smash crit
        // interaction isn't a verified mechanic); a crit averages 3x the max, and
        // the Quick-and-Deadly bonus only applies when STR meets the weapon's req.
        int critChance = 0, avgCritDamage = 0, qnd = 0;
        if (type == MudAttackType.Normal && hasWeapon)
        {
            qnd = (weaponStrReq <= 0 || strength >= weaponStrReq) ? swings.QnDCritBonus : 0;
            int critRating = CharacterCalculator.CalcBaseCritRating(
                level, intellect, agility, charm, realmType, combatLevel) + plusCrits;
            critChance = CalcCritChance(critRating, qnd, realmType);
            avgCritDamage = dmg.MaxDamage * 3;
        }

        return new MeleeOffense(
            avgDamage, swingsPerRound, critChance, avgCritDamage, hasWeapon,
            MinDamage: hasWeapon ? dmg.MinDamage : 0, MaxDamage: hasWeapon ? dmg.MaxDamage : 0,
            QuickAndDeadlyBonus: qnd);
    }
}
