using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using MudPlay.Game.Inventory;
using MudPlay.Game.Quests;
using MudPlay.Game.Spells;
using MudPlay.Services;

namespace MudPlay.Game.Calculators;

// Pure MajorMUD character-stat formulas (CP, HP, mana/kai regen). Most methods
// take primitive inputs and return a result — no UI, no manager dependencies.
// Realm-dependent formulas branch on RealmType; callers resolve the active realm
// from GameDataCache.ActiveRealm.
// Experience-curve math lives in ExperienceTableCalculator; combat math lives in
// CombatCalculator. The equipment-stat aggregation here (AggregateEquipmentStats)
// is the one method that reads game data — it resolves each worn item against
// GameDataCache to sum ability bonuses.
public static class CharacterCalculator
{
    // ----- CP --------------------------------------------------------------

    // CP gained by TRAINING to the given level (level 2 and up):
    // ((level - 1) / 10) * 5 + 10 — 10 CP per level for 2–10, 15 for 11–20,
    // 20 for 21–30, and so on. The step rises at the *first* level of each new
    // decade (11, 21, 31…), so level 10 still grants 10 and level 20 still 15;
    // the old (level/10)*5+10 was off by one and over-paid every decade top.
    // Level 1 is NOT a training gain — a character begins with the race's
    // BaseCP pool (100 for the standard races), which CalcTotalCpAtLevel folds
    // in via its baseCP argument — so this returns 0 below level 2.
    public static int CalcCpGainedAtLevel(int level)
    {
        if (level < 2) return 0;
        return ((level - 1) / 10) * 5 + 10;
    }

    // CP cost to raise a stat by one point from currentStat:
    // cost = Floor((currentStat - raceMin) / 10) + 1. ParaMUD has no cap; Stock
    // caps the per-point cost at 10.
    public static int CalcCpCostForStatPoint(int raceMin, int currentStat, RealmType realmType = RealmType.ParaMud)
    {
        int delta = currentStat - raceMin;
        if (delta < 0) delta = 0;
        int cost = (delta / 10) + 1;
        if (realmType == RealmType.Stock && cost > 10)
            cost = 10;
        return cost;
    }

    // Total CP cost to raise a stat from startVal to endVal, summing the
    // per-point cost over each point.
    public static int CalcTotalCpCostForStatRange(int raceMin, int startVal, int endVal, RealmType realmType = RealmType.ParaMud)
    {
        int total = 0;
        for (int v = startVal; v < endVal; v++)
        {
            total += CalcCpCostForStatPoint(raceMin, v, realmType);
        }
        return total;
    }

    // ----- HP --------------------------------------------------------------

    // Estimate max HP at a level. Core:
    // (HEA/2 + Level*MinHitsPerLevel) + ((HEA-50)*Level)/16 + Random,
    // then + raceHpPerLevel*Level + plusMaxHp. In game data MaxHits is the random
    // range (a delta, not an absolute), so the random portion brackets to the
    // chosen rollMode.
    public static int CalcMaxHp(int health, int level, int minHitsPerLevel, int maxHitsPerLevel,
                                int raceHpPerLevel, int plusMaxHp, HpRollMode rollMode)
    {
        int range = maxHitsPerLevel;

        int random = rollMode switch
        {
            HpRollMode.Min => range,                              // level-1 max roll, zeros after
            HpRollMode.Average => range + (range * (level - 1) / 2), // level-1 max + average rolls
            HpRollMode.Max => range * level,                      // max roll every level
            _ => range * level
        };

        int baseHp = (health / 2) + (level * minHitsPerLevel)
                   + ((health - 50) * level / 16)
                   + random;

        return baseHp + (raceHpPerLevel * level) + plusMaxHp;
    }

    // HP regen per tick. Base (level+20)*health/divisor with divisor 500
    // (ParaMUD) or 750 (Stock), floored at 1, tripled while resting, then scaled
    // by equipment +HP-regen%.
    public static int CalcHpRegen(int level, int health, int hpRegenPercent, bool isResting, RealmType realmType)
    {
        int divisor = realmType == RealmType.ParaMud ? 500 : 750;
        int regen = (level + 20) * health / divisor;
        if (regen < 1) regen = 1;
        if (isResting) regen *= 3;
        regen = (hpRegenPercent + 100) * regen / 100;
        return regen;
    }

    // Paradigm pays the idle HP amount in thirds: one standing gain every 10 s, and
    // the low gains of a rest. The third is of the amount before any regen percent,
    // with the fraction dropped, and is kept to at least 1 as the amount itself is.
    // What the percent adds to the whole amount comes on the last gain of each three
    // (GAME_MECHANICS "Rest and meditate tick timing").
    public static int ParadigmHpRegenThird(int baseAmount) => Math.Max(1, baseAmount / 3);

    // ----- Mana / Kai ------------------------------------------------------

    // Max mana: (mageryLevel * level * 2) + 6 + plusMaxMana. Returns 0 for
    // non-casters (mageryLevel <= 0).
    public static int CalcMaxMana(int mageryLevel, int level, int plusMaxMana)
    {
        if (mageryLevel <= 0) return 0;
        return (mageryLevel * level * 2) + 6 + plusMaxMana;
    }

    // Max Kai for Mystics (magery type 5). Kai is not mana — the mana formula
    // gives wildly wrong values for Mystics — it approximates to level - 1 (a
    // level-82 Mystic has 81 Kai), with no equipment contribution. Returns 0
    // below level 2.
    public static int CalcMaxKai(int level)
    {
        if (level <= 1) return 0;
        return level - 1;
    }

    // Mana regen per tick. Base stat depends on magery type
    // (1=INT, 2=WIL, 3=(INT+WIL)/2, 4=CHM, 5=Kai fixed-rate, 0=none); core is
    // ((level+20)*baseStat*(mageryLevel+2))/1650. While meditating the core value
    // is returned before the equipment +mana-regen% modifier, which itself
    // differs by realm.
    public static int CalcManaRegen(int level, int intellect, int willpower, int charm,
                                    int mageryType, int mageryLevel, int mpRegenPercent,
                                    bool isMeditating, RealmType realmType)
    {
        if (mageryType == 0) return 0;

        // Kai: special fixed-rate path
        if (mageryType == 5)
        {
            return (mpRegenPercent + 100) * 1 / 100;
        }

        int baseStat = mageryType switch
        {
            1 => intellect,                       // Mage
            2 => willpower,                        // Priest
            3 => (intellect + willpower) / 2,      // Druid
            4 => charm,                            // Bard
            _ => 0
        };

        if (baseStat == 0) return 0;

        int regen = ((level + 20) * baseStat * (mageryLevel + 2)) / 1650;

        // Meditating exits before the equipment modifier applies.
        if (isMeditating) return regen;

        if (realmType == RealmType.ParaMud)
        {
            regen = regen + (mpRegenPercent * regen / 100);
        }
        else
        {
            regen = (mpRegenPercent + 100) * regen / 100;
        }

        return regen;
    }

    // ----- stat-derived secondary stats (CP-planning surface) --------------
    // These four are the stat→derived formulas the score screen / DLL expose that
    // don't already live as combat helpers. Ground truth is the reverse-engineered Stock
    // DLL; Paradigm's derivations are in GAME_MECHANICS "Realm differences in stat
    // derivation & Paradigm-verification summary".

    // Base critical rating from stats (0x710): Level/10 + (Int-50)/10 + (Agl-50)/20
    // + (Chm-50)/30, floored at 1. Stock also caps it at 75; Paradigm has no cap
    // here, and gives a class whose Combat rating (the class table's CombatLVL − 2)
    // is 1–4 another 5 − Combat. Combat crit % = this + gear crit bonus, then
    // CombatCalculator.CalcCritChance applies the realm's diminishing-returns curve.
    public static int CalcBaseCritRating(int level, int intellect, int agility, int charm,
                                         RealmType realm, int classCombatLvl = 0)
    {
        int rating = level / 10
                   + (intellect - 50) / 10
                   + (agility - 50) / 20
                   + (charm - 50) / 30;
        if (realm == RealmType.Stock && rating > 75) rating = 75;
        rating = Math.Max(rating, 1);
        int combat = classCombatLvl - 2;
        if (realm == RealmType.ParaMud && combat is > 0 and < 5) rating += 5 - combat;
        return rating;
    }

    // Base stealth skill (0x5fa) from stats + level: stat terms + stealthLvl + 20,
    // where stealthLvl = Level*2 below 16, else Level+15. Excludes the per-race /
    // per-class +10 / -15 flat adjusts (character-specific grants, not a stat
    // effect), so this is the stat-and-level base a stealth-capable class starts
    // from. Non-stealth classes have no stealth at all.
    // REALM-SPLIT: Stock truncates each stat term on its own (Agl/4 + Int/8 + Chm/6,
    // each floored); Paradigm sums the stat contributions as a float and rounds once,
    // so the two realms can differ by a point or two.
    public static int CalcStealthBase(int level, int intellect, int agility, int charm, RealmType realm)
    {
        int baseVal = (level < 16 ? level * 2 : level + 15) + 20;
        if (realm == RealmType.ParaMud)
            return baseVal + (int)Math.Round(
                agility / 4.0 + intellect / 8.0 + charm / 6.0, MidpointRounding.ToEven);
        return baseVal + agility / 4 + intellect / 8 + charm / 6;
    }

    // Max encumbrance / carry weight (0xb2): Str*48, with a steeper term above 100
    // (Str*36 - 3600 added). The same on both realms.
    public static int CalcMaxEncumbrance(int strength)
    {
        int enc = strength * 48;
        if (strength > 100) enc += strength * 36 - 3600;
        return enc;
    }

    // Magic resistance (0xc0) from stats: (Int + 3*Wil)/4, the same on both realms.
    // Excludes the +magic-resist ability (0x24), a gear/innate grant.
    public static int CalcMagicResistance(int intellect, int willpower)
        => (intellect + 3 * willpower) / 4;

    // ----- utility / thief skills ------------------------------------------
    // The five stat-and-level skills the score screen shows beside Stealth.
    // Ground truth is the RE'd stock DLL (calculate_secondary_stats @ 0x41a424);
    // each is an integer division, so points that don't complete a division buy
    // nothing — the CP tooltips surface the next value that ticks each one up.
    // Paradigm has its own Picklocks formula; the other four are unverified there
    // and use Stock's. The +skill abilities (Perception 77, Thievery 39,
    // Traps 40/179, Picklocks 37/180, Tracking 38) are gear / innate grants and
    // stack on top of these bases.

    // Shared level term for the four thief skills (0x5fe / 0x606 / 0x60a / 0x60c):
    // the level slope HALVES at 16, so levelling past it grows them half as fast.
    // Stealth has its own half-slope at the same level (see CalcStealthBase).
    public static int CalcThiefSkillLevelTerm(int level)
        => level < 16 ? level : 15 + (level - 15) / 2;

    // Perception (0x5f8): (Int*5 + Wil*2 + Chm)/8. The one skill here every class
    // carries — INT dominates it 5:2:1.
    public static int CalcPerception(int intellect, int willpower, int charm)
        => (intellect * 5 + willpower * 2 + charm) / 8;

    // Thievery (0x5fe): (Agl + Int + Chm + lvlTerm*24)/6.
    public static int CalcThievery(int level, int intellect, int agility, int charm)
        => (agility + intellect + charm + CalcThiefSkillLevelTerm(level) * 24) / 6;

    // Traps (0x606): (Int + Agl + Chm*2 + lvlTerm*28)/7 — the single skill
    // governing both finding and disarming. CHM is weighted double here.
    public static int CalcTraps(int level, int intellect, int agility, int charm)
        => (intellect + agility + charm * 2 + CalcThiefSkillLevelTerm(level) * 28) / 7;

    // Picklocks (0x60a): ((Agl + Int + lvlTerm*10)*2)/7 on Stock. The doubling
    // happens BEFORE the divide, so the effective divisor is 3.5 — keep the order.
    // Paradigm takes CHM as well, weighted double: (Int + Agl + Chm*2 + lvlTerm*28)/7.
    public static int CalcPicklocks(int level, int intellect, int agility, int charm, RealmType realm)
        => realm == RealmType.ParaMud
            ? (intellect + agility + charm * 2 + CalcThiefSkillLevelTerm(level) * 28) / 7
            : (agility + intellect + CalcThiefSkillLevelTerm(level) * 10) * 2 / 7;

    // Tracking (0x60c): (Int*2 + Wil + Chm + lvlTerm*40)/8 — the heaviest level
    // term of the four, so it grows mostly by levelling rather than by CP.
    public static int CalcTracking(int level, int intellect, int willpower, int charm)
        => (intellect * 2 + willpower + charm + CalcThiefSkillLevelTerm(level) * 40) / 8;

    // Spellcasting skill (spellLvl, 0x604): Level*2 + manaStat + mageryLevel*5 +
    // the +spellcasting ability (0x46 = 70, gear/innate). manaStat blends the caster
    // stat by class type — 1=(3*Int+Wil)/6 (Mage), 2=(3*Wil+Int)/6 (Priest),
    // 3=(Int+Wil)/3 (Druid), 4=(3*Chm+Wil)/6 (Bard), and a flat 500 for Mystics (Kai),
    // which no stat moves. Non-casters have none. Verified in the Stock DLL (asm
    // 0x41aae0); Paradigm uses the same five blends.
    public static int CalcSpellcasting(int level, int intellect, int willpower, int charm,
                                       int mageryType, int mageryLevel, int plusSpellcasting)
    {
        int manaStat = mageryType switch
        {
            1 => (3 * intellect + willpower) / 6,
            2 => (3 * willpower + intellect) / 6,
            3 => (intellect + willpower) / 3,
            4 => (3 * charm + willpower) / 6,
            5 => 500,
            _ => -1,   // non-caster — no spellcasting
        };
        if (manaStat < 0) return 0;
        return level * 2 + manaStat + mageryLevel * 5 + plusSpellcasting;
    }

    // ----- equipment stat aggregation --------------------------------------

    // MajorMUD items carry up to 20 ability slots (Abil-0..Abil-19); race /
    // class records use the first 10. Both are scanned the same way.
    private const int MaxItemAbilSlots = 20;
    private const int MaxRecordAbilSlots = 10;

    // Sum the equipment-stat bonuses of every worn item, resolving each one
    // against the cache's Items table. Each item's base AC/DR and its
    // Abil-0..Abil-19 slots are folded into an EquipmentStatBreakdown (totals +
    // per-stat item sources for tooltips). Items not found in game data are
    // skipped silently — a custom realm may rename items the active set doesn't
    // carry.
    public static EquipmentStatBreakdown AggregateEquipmentStats(
        IReadOnlyList<EquippedItem> equippedItems, GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(equippedItems);
        ArgumentNullException.ThrowIfNull(cache);

        var result = new EquipmentStatBreakdown();
        foreach (EquippedItem item in equippedItems)
        {
            GameRow? row = cache.FindRowByName("Items", item.Name);
            if (row is GameRow itemData)
                FoldItemRow(result, itemData, item.Name, item.Slot);
        }

        return result;
    }

    // Fold one already-resolved Items row into a fresh EquipmentStatBreakdown —
    // the per-item half of AggregateEquipmentStats, exposed for callers that
    // already hold the row. The Item Finder enumerates the whole Items
    // table, so a name round-trip through GameDataCache.FindRowByName per item
    // would be wasteful. slotTag mirrors EquippedItem.Slot: "Weapon Hand"
    // surfaces the weapon-base fields (Min / Max / StrReq / Type / Speed),
    // "Off-Hand" the off-hand accuracy, any other tag folds as generic worn gear.
    public static EquipmentStatBreakdown AggregateItemRow(
        GameRow itemRow, string itemName, string slotTag)
    {
        ArgumentNullException.ThrowIfNull(itemName);
        ArgumentNullException.ThrowIfNull(slotTag);

        var result = new EquipmentStatBreakdown();
        FoldItemRow(result, itemRow, itemName, slotTag);
        return result;
    }

    private static void FoldItemRow(
        EquipmentStatBreakdown result, GameRow itemData, string itemName, string slotTag)
    {
        EquipmentStatSummary totals = result.Totals;

        // Base AC/DR are stored ×10 in game data — divide for the real value.
        double baseAC = GetInt(itemData, "ArmourClass") / 10.0;
        double baseDR = GetInt(itemData, "DamageResist") / 10.0;
        if (baseAC != 0 || baseDR != 0)
        {
            totals.PlusAC += baseAC;
            totals.PlusDR += baseDR;
            AddContribution(result, "Armour Class", itemName,
                string.Create(CultureInfo.InvariantCulture, $"{baseAC:0.#}/{baseDR:0.#}"));
        }

        // Per-item Accy folds into the worn-accuracy total for every item;
        // the weapon / off-hand pieces also surface their own accuracy.
        int itemAccy = GetInt(itemData, "Accy");
        totals.TotalWornAccy += itemAccy;
        if (itemAccy != 0)
            AddContribution(result, "Accuracy", itemName,
                itemAccy.ToString("+0;-0", CultureInfo.InvariantCulture));

        bool isWeaponHand = slotTag == "Weapon Hand";
        if (isWeaponHand)
        {
            totals.WeaponHandAccy = itemAccy;
            totals.WeaponStrReq = GetInt(itemData, "StrReq");
            totals.WeaponMin = GetInt(itemData, "Min");
            totals.WeaponMax = GetInt(itemData, "Max");
            totals.WeaponSpeed = GetInt(itemData, "Speed");
        }
        else if (slotTag == "Off-Hand")
        {
            totals.OffHandAccy = itemAccy;
        }

        for (int i = 0; i < MaxItemAbilSlots; i++)
        {
            int abilId = GetInt(itemData, $"Abil-{i}");
            int abilVal = GetInt(itemData, $"AbilVal-{i}");
            if (abilId <= 0 || abilVal == 0) continue;

            if (abilId is 22 or 105 or 106 && abilVal > totals.MaxSingleAbil22)
                totals.MaxSingleAbil22 = abilVal;

            // Hit Magic (28/142): every item adds to the running total, but
            // only a Weapon-Hand piece contributes to weapon hit-magic and
            // shows up in the breakdown — handled before MapAbilityToStat.
            if (abilId is 28 or 142)
            {
                totals.PlusHitMagic += abilVal;
                if (isWeaponHand)
                {
                    totals.WeaponHitMagic += abilVal;
                    AddContribution(result, "Hit Magic", itemName,
                        abilVal.ToString("+0;-0", CultureInfo.InvariantCulture));
                }
                continue;
            }

            MapAbilityToStat(result, totals, itemName, abilId, abilVal);
        }
    }

    // Fold a race or class record's Abil-0..Abil-9 bonuses into an existing
    // breakdown. These contribute to AC/DR and the other derived stats exactly
    // like item abilities, so the Workshop can show innate racial / class bonuses
    // alongside worn gear.
    public static void ApplyAbilityBonuses(
        EquipmentStatBreakdown breakdown, GameRow data, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(breakdown);
        EquipmentStatSummary totals = breakdown.Totals;
        for (int i = 0; i < MaxRecordAbilSlots; i++)
        {
            int abilId = GetInt(data, $"Abil-{i}");
            int abilVal = GetInt(data, $"AbilVal-{i}");
            if (abilId <= 0 || abilVal == 0) continue;

            if (abilId is 22 or 105 or 106 && abilVal > totals.MaxSingleAbil22)
                totals.MaxSingleAbil22 = abilVal;

            MapAbilityToStat(breakdown, totals, sourceName, abilId, abilVal);
        }
    }

    // Fold completed-quest stat rewards into an existing breakdown. Each
    // QuestBonus's ability id maps onto the same summary fields equipment +
    // race/class bonuses use, so a quest's permanent reward (e.g. addability 4
    // max-damage, addability 34 dodge) feeds the derived combat exactly as the
    // game applies it. sourceName labels the per-stat contribution (e.g. the
    // quest name) for the Character Info breakdown.
    public static void ApplyQuestBonuses(
        EquipmentStatBreakdown breakdown, IEnumerable<QuestBonus> bonuses, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(breakdown);
        ArgumentNullException.ThrowIfNull(bonuses);
        EquipmentStatSummary totals = breakdown.Totals;
        foreach (QuestBonus bonus in bonuses)
        {
            if (bonus.AbilityId <= 0 || bonus.Value == 0) continue;

            if (bonus.AbilityId is 22 or 105 or 106 && bonus.Value > totals.MaxSingleAbil22)
                totals.MaxSingleAbil22 = bonus.Value;

            MapAbilityToStat(breakdown, totals, sourceName, bonus.AbilityId, bonus.Value);
        }
    }

    // Resolves the player's live Normal-attack matchup profile (accuracy,
    // average weapon damage, swings/round, crit chance) for
    // MonsterMatchupCalculator.Compute — the same equipment + race/class
    // aggregation the Character Workshop's Calculators tab performs for its
    // own Normal-attack case (see CalculatorsSectionViewModel.CaptureActuals /
    // ComputeWeaponOffense), extracted here for Monster Intel's rounds-to-kill
    // estimate. Deliberately skips completed-quest OFFENSE bonuses — Monster Intel
    // folds quest rewards into its defense AC (see MonsterIntelViewModel), but a
    // quick pre-fight rounds-to-kill glance doesn't need full quest-adjusted DPS fidelity.
    public static PlayerMatchupProfile BuildNormalAttackProfile(
        PlayerStats stats, IReadOnlyList<EquippedItem> worn, EncumbranceReading encum, GameDataCache gameData)
        => BuildMeleeAttackProfile(MudAttackType.Normal, stats, worn, encum, gameData);

    // The melee attack types this character can actually use, in the order the
    // Character Info panel lists them: Normal + Bash are always available, the
    // rest are class/race-gated. Monster Intel iterates this to offer a
    // rounds-to-kill line per usable attack instead of Normal alone. The gates
    // mirror CharacterInfoSectionViewModel.ComputeDerivedCombat exactly, so the
    // two surfaces never disagree on what a character can do.
    public static IReadOnlyList<MudAttackType> UsableMeleeAttacks(PlayerStats stats, GameDataCache gameData)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(gameData);

        GameRow? classRow = gameData.FindRowByName("Classes", stats.Class);
        GameRow? raceRow = gameData.FindRowByName("Races", stats.Race);

        var attacks = new List<MudAttackType> { MudAttackType.Normal, MudAttackType.Bash };

        // A null smash set means "no class-restricted smash chain in the active
        // set, so assume every class can" — matching CharacterInfo's gate.
        HashSet<string>? smashClasses = ClassCapabilities.GetSmashCapableClasses(gameData);
        bool canSmash = smashClasses is null
            || (!string.IsNullOrEmpty(stats.Class) && smashClasses.Contains(stats.Class));
        if (canSmash) attacks.Add(MudAttackType.Smash);

        if (ClassCapabilities.ClassHasStealth(classRow) || ClassCapabilities.RaceHasStealth(raceRow))
            attacks.Add(MudAttackType.Backstab);

        if (ClassCapabilities.ClassHasPunch(classRow)) attacks.Add(MudAttackType.Punch);
        if (ClassCapabilities.ClassHasKick(classRow)) attacks.Add(MudAttackType.Kick);
        if (ClassCapabilities.ClassHasJumpKick(classRow)) attacks.Add(MudAttackType.Jumpkick);

        return attacks;
    }

    // Worn gear plus the race's and class's own abilities and the completed quests'
    // bonuses: the totals the game's whole-character figures (regen among them) run on.
    public static EquipmentStatSummary CharacterTotals(
        PlayerStats stats, IReadOnlyList<EquippedItem> worn, GameDataCache gameData,
        IReadOnlyList<Quests.QuestBonus>? questBonuses)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(worn);
        ArgumentNullException.ThrowIfNull(gameData);
        EquipmentStatBreakdown combined = AggregateEquipmentStats(worn, gameData);
        if (gameData.FindRowByName("Races", stats.Race) is GameRow race) ApplyAbilityBonuses(combined, race, stats.Race);
        if (gameData.FindRowByName("Classes", stats.Class) is GameRow cls) ApplyAbilityBonuses(combined, cls, stats.Class);
        if (questBonuses is not null) ApplyQuestBonuses(combined, questBonuses, "Quests");
        return combined.Totals;
    }

    // The caster's spell-damage bonus % (AlterSpDmg, ability 165): worn gear plus
    // race and class abilities, the same sources the melee profile folds in, and on
    // Paradigm the Spellcasting share (SpellcastingSpellDamageBonus).
    public static int SpellDamageBonus(PlayerStats stats, IReadOnlyList<EquippedItem> worn, GameDataCache gameData)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(worn);
        ArgumentNullException.ThrowIfNull(gameData);
        EquipmentStatBreakdown combined = AggregateEquipmentStats(worn, gameData);
        if (gameData.FindRowByName("Races", stats.Race) is GameRow r) ApplyAbilityBonuses(combined, r, stats.Race);
        if (gameData.FindRowByName("Classes", stats.Class) is GameRow c) ApplyAbilityBonuses(combined, c, stats.Class);
        return combined.Totals.SpellDamageBonus + SpellcastingSpellDamageBonus(stats.Spellcasting, gameData.ActiveRealm);
    }

    // Paradigm turns Spellcasting above 100 into spell damage: +1% per full 50
    // (GAME_MECHANICS "Spell damage — Stock vs Paradigm"). Stock has no such term.
    public static int SpellcastingSpellDamageBonus(int spellcasting, RealmType realm) =>
        realm == RealmType.ParaMud ? Math.Max(0, spellcasting - 100) / 50 : 0;

    // Builds the matchup profile for one melee attack type. The defensive side
    // (AC / dodge / prot wards / DR) and the realm are identical across every
    // type; only the offensive fields (to-hit, avg damage, swings, crit) branch
    // on the attack. Each branch mirrors the corresponding path in
    // CharacterInfoSectionViewModel.ComputeDerivedCombat so Monster Intel's
    // rounds-to-kill and the Character Info sheet compute from one recipe.
    // buff adds the what-if offense of buffs the caller assumes are up (see
    // BuffOffenseCalculator); its accuracy joins the same highest-single rule
    // Stock applies to every other accuracy source.
    public static PlayerMatchupProfile BuildMeleeAttackProfile(
        MudAttackType type, PlayerStats stats, IReadOnlyList<EquippedItem> worn,
        EncumbranceReading encum, GameDataCache gameData, BuffOffense buff = default)
    {
        ArgumentNullException.ThrowIfNull(stats);
        return BuildMeleeAttackProfile(type, AttackerStats.From(stats), worn, encum, gameData, buff);
    }

    // The same recipe for a character described by hand (the Item Finder's trial
    // set: typed stats, a gearset nobody is wearing). stats must already hold the
    // bonuses of worn, as the stat screen's do. questBonuses adds the completed
    // quests' rewards, which the live profile above leaves out.
    public static PlayerMatchupProfile BuildMeleeAttackProfile(
        MudAttackType type, AttackerStats stats, IReadOnlyList<EquippedItem> worn,
        EncumbranceReading encum, GameDataCache gameData, BuffOffense buff = default,
        IReadOnlyList<QuestBonus>? questBonuses = null)
    {
        ArgumentNullException.ThrowIfNull(worn);
        ArgumentNullException.ThrowIfNull(gameData);

        EquipmentStatBreakdown combined = AggregateEquipmentStats(worn, gameData);
        GameRow? classRow = gameData.FindRowByName("Classes", stats.Class);
        GameRow? raceRow = gameData.FindRowByName("Races", stats.Race);
        if (raceRow is GameRow r) ApplyAbilityBonuses(combined, r, stats.Race);
        if (classRow is GameRow c) ApplyAbilityBonuses(combined, c, stats.Class);
        if (questBonuses is not null) ApplyQuestBonuses(combined, questBonuses, "Quests");
        EquipmentStatSummary t = combined.Totals;
        if (buff.Any)
        {
            t.PlusAccuracy += buff.Accuracy;
            if (buff.Accuracy > t.MaxSingleAbil22) t.MaxSingleAbil22 = buff.Accuracy;
            t.PlusBSAccuracy += buff.BsAccuracy;
            t.PlusBSMin += buff.BsMin;
            t.PlusBSMax += buff.BsMax;
            t.PlusMaxDamage += buff.MaxDamage;
            t.PlusCrits += buff.Crits;
        }
        int stealth = stats.Stealth + buff.Stealth;

        RealmType realm = gameData.ActiveRealm;
        int nCombatLevel = classRow is GameRow cr ? GetInt(cr, "CombatLVL") : 0;
        int effectiveAbil22 = realm == RealmType.ParaMud ? t.PlusAccuracy : t.MaxSingleAbil22;

        int accuracy;
        int avgDamage;
        double swingsPerRound;
        bool hasWeapon;
        int critChance = 0;
        int avgCritDamage = 0;
        int bsMin = 0, bsMax = 0;
        int minDamage, maxDamage, quickAndDeadly = 0;

        switch (type)
        {
            case MudAttackType.Normal:
            case MudAttackType.Bash:
            case MudAttackType.Smash:
            {
                accuracy = CombatCalculator.CalcAccuracy(
                    type, realm, stats.Level, nCombatLevel,
                    stats.Strength, stats.Agility, stats.Intellect, stats.Charm,
                    t.TotalWornAccy, effectiveAbil22, encum.CurrentWeight, encum.MaxWeight, t.WeaponStrReq);

                // ComputeMeleeOffense already zeroes crit for non-Normal and locks
                // Smash to a single swing, so no per-type special-casing is needed.
                MeleeOffense offense = CombatCalculator.ComputeMeleeOffense(
                    type, realm, stats.Level, nCombatLevel,
                    stats.Strength, stats.Agility, stats.Intellect, stats.Charm,
                    t.WeaponMin, t.WeaponMax, t.WeaponSpeed, t.WeaponStrReq,
                    t.PlusMaxDamage, t.PlusMinDamage, t.PlusCrits, encum.CurrentWeight, encum.MaxWeight);

                avgDamage = offense.AvgDamage;
                swingsPerRound = offense.SwingsPerRound;
                hasWeapon = offense.HasWeapon;
                critChance = offense.CritChance;
                avgCritDamage = offense.AvgCritDamage;
                minDamage = offense.MinDamage;
                maxDamage = offense.MaxDamage;
                quickAndDeadly = offense.QuickAndDeadlyBonus;
                break;
            }
            case MudAttackType.Backstab:
            {
                bool hasClassStealth = ClassCapabilities.ClassHasStealth(classRow);
                int bsNormAccy = realm == RealmType.ParaMud ? t.TotalWornAccy + effectiveAbil22 : effectiveAbil22;
                accuracy = CombatCalculator.CalcBackstabAccuracy(
                    stealth, stats.Agility, stats.Level, stats.Strength, t.WeaponStrReq,
                    t.PlusBSAccuracy, bsNormAccy, hasClassStealth, realm);

                // WeaponMin/Max are 0 unarmed, which CalcBSDamage folds into the
                // strength-only profile — a backstab still lands bare-handed, so
                // gate HasWeapon on damage output rather than a weapon being worn.
                BSDamageResult bsDmg = CombatCalculator.CalcBSDamage(
                    stats.Level, stealth, stats.Strength, t.WeaponMin, t.WeaponMax,
                    t.PlusBSMin, t.PlusBSMax, t.PlusMaxDamage, hasClassStealth, realm, t.PlusMinDamage);
                avgDamage = (bsDmg.MinDamage + bsDmg.MaxDamage) / 2;
                bsMin = minDamage = bsDmg.MinDamage;
                bsMax = maxDamage = bsDmg.MaxDamage;
                swingsPerRound = 1;   // a backstab is always a single strike
                hasWeapon = avgDamage > 0;
                break;
            }
            case MudAttackType.Punch:
            case MudAttackType.Kick:
            case MudAttackType.Jumpkick:
            {
                // A martial-arts strike ignores the wielded weapon's accuracy, so
                // strip the weapon/off-hand accy off before the normal-attack base.
                int maWornAccy = t.TotalWornAccy - t.WeaponHandAccy - t.OffHandAccy;
                if (maWornAccy < 0) maWornAccy = 0;
                int maBaseAccy = CombatCalculator.CalcAccuracy(
                    MudAttackType.Normal, realm, stats.Level, nCombatLevel,
                    stats.Strength, stats.Agility, stats.Intellect, stats.Charm,
                    maWornAccy, effectiveAbil22, encum.CurrentWeight, encum.MaxWeight, weaponStrReq: 0);

                // GreaterMUD applies a per-attack accuracy penalty (kick -10,
                // jumpkick -15); Stock has none.
                int kickPenalty = realm == RealmType.ParaMud ? 10 : 0;
                int jumpKickPenalty = realm == RealmType.ParaMud ? 15 : 0;
                (int accyBonus, int maPlusDmg) = type switch
                {
                    MudAttackType.Punch => (t.PlusPunchAccy, t.PlusPunchDmg),
                    MudAttackType.Kick => (t.PlusKickAccy - kickPenalty, t.PlusKickDmg),
                    _ => (t.PlusJumpKickAccy - jumpKickPenalty, t.PlusJumpKickDmg),
                };
                accuracy = maBaseAccy + accyBonus;

                // The damage formula takes the item-granted +MA-skill bonus floored
                // to 1, never the Martial Arts skill stat (that stat drives accuracy).
                const int maPlusSkill = 1;
                MeleeDamageResult d = CombatCalculator.CalcMartialArtsDamage(
                    type, realm, stats.Level, maPlusSkill, stats.Strength, t.PlusMaxDamage, maPlusDmg,
                    plusMinDamage: t.PlusMinDamage);
                avgDamage = (d.MinDamage + d.MaxDamage) / 2;
                minDamage = d.MinDamage;
                maxDamage = d.MaxDamage;

                swingsPerRound = CombatCalculator.CalcSwings(
                    nCombatLevel, stats.Level, CombatCalculator.MartialArtsSpeed(type, realm),
                    stats.Agility, stats.Strength, weaponStrReq: 0,
                    encum.CurrentWeight, encum.MaxWeight, realmType: realm).RawSwings;
                hasWeapon = avgDamage > 0;
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "Not a melee attack type.");
        }

        return new PlayerMatchupProfile(
            Realm: realm,
            NormalAccuracy: accuracy,
            AvgWeaponDamage: avgDamage,
            SwingsPerRound: swingsPerRound,
            HasWeapon: hasWeapon,
            DamageResist: (int)t.PlusDR,
            CritChancePercent: critChance,
            AvgCritDamage: avgCritDamage,
            MonsterDrMultiplier: CombatCalculator.DrMultiplierFor(type, realm),
            BackstabMin: bsMin,
            BackstabMax: bsMax,
            MinDamage: minDamage,
            MaxDamage: maxDamage,
            QuickAndDeadlyBonus: quickAndDeadly);
    }

    // Maps a single MajorMUD ability ID + value onto the matching summary field
    // and records the per-item contribution.
    private static void MapAbilityToStat(EquipmentStatBreakdown result, EquipmentStatSummary totals,
                                         string itemName, int abilId, int abilVal)
    {
        string? statKey = null;
        string? tag = null;

        switch (abilId)
        {
            case 2: totals.PlusAC += abilVal; statKey = "Armour Class"; break;
            // Blur AC (Abil 10) folds into the inclusive PlusAC for combat math, but is
            // also tracked in PlusAcBlur and grouped under its own "AC Blur" stat key so
            // readouts show it apart from flat worn AC (it's scaled in play, not flat).
            case 10: totals.PlusAC += abilVal; totals.PlusAcBlur += abilVal; statKey = "AC Blur"; break;
            case 7: totals.PlusDR += abilVal / 10.0; statKey = "Damage Resist"; break;

            case 46: totals.PlusStrength += abilVal; statKey = "Strength"; break;
            case 44: totals.PlusIntellect += abilVal; statKey = "Intellect"; break;
            case 45: totals.PlusWillpower += abilVal; statKey = "Willpower"; break;
            case 48: totals.PlusAgility += abilVal; statKey = "Agility"; break;
            case 47: totals.PlusHealth += abilVal; statKey = "Health"; break;
            case 49: totals.PlusCharm += abilVal; statKey = "Charm"; break;

            case 88: totals.PlusMaxHp += abilVal; statKey = "Max HP"; break;
            case 69: totals.PlusMaxMana += abilVal; statKey = "Max Mana"; break;
            case 123: totals.HpRegenPercent += abilVal; statKey = "HP Regen"; break;
            case 145: totals.MpRegenPercent += abilVal; statKey = "Mana Regen"; break;

            case 58: totals.PlusCrits += abilVal; statKey = "Crits"; break;
            case 22: case 105: case 106: totals.PlusAccuracy += abilVal; statKey = "Accuracy"; break;
            case 1: totals.PlusMinDamage += abilVal; statKey = "Min Damage"; break;
            case 4: totals.PlusMaxDamage += abilVal; statKey = "Max Damage"; break;
            case 165: totals.SpellDamageBonus += abilVal; statKey = "Spell Damage"; break;

            case 34: totals.PlusDodge += abilVal; statKey = "Dodge"; break;
            case 36: totals.PlusMagicResist += abilVal; statKey = "Magic Resist"; break;
            case 72: totals.PlusShockShield += abilVal; statKey = "ShockShield"; break;

            case 116: totals.PlusBSAccuracy += abilVal; statKey = "BS Accuracy"; break;
            case 117: totals.PlusBSMin += abilVal; statKey = "BS Min Dmg"; break;
            case 118: totals.PlusBSMax += abilVal; statKey = "BS Max Dmg"; break;

            case 27: totals.PlusStealth += abilVal; statKey = "Stealth"; break;
            case 77: totals.PlusPerception += abilVal; statKey = "Perception"; break;
            case 70: totals.PlusSpellcasting += abilVal; statKey = "Spellcasting"; break;
            case 96: totals.PlusEncumbrance += abilVal; statKey = "Encumbrance"; break;
            case 40: case 179: totals.PlusTraps += abilVal; statKey = "Traps"; break;
            case 41: totals.PlusDisarmTraps += abilVal; statKey = "Disarm Traps"; break;
            case 37: case 180: totals.PlusPicklocks += abilVal; statKey = "Picklocks"; break;
            case 13: case 14: totals.PlusIlluminate += abilVal; statKey = "Illuminate"; break;
            case 67: totals.PlusQuickness += abilVal; statKey = "Quickness"; break;
            case 39: totals.PlusThievery += abilVal; statKey = "Thievery"; break;
            case 38: totals.PlusTracking += abilVal; statKey = "Tracking"; break;

            case 3: totals.PlusColdResist += abilVal; statKey = "Cold Resist"; break;
            case 5: totals.PlusFireResist += abilVal; statKey = "Fire Resist"; break;
            case 65: totals.PlusStoneResist += abilVal; statKey = "Stone Resist"; break;
            case 66: totals.PlusLightningResist += abilVal; statKey = "Lightning Resist"; break;
            case 147: totals.PlusWaterResist += abilVal; statKey = "Water Resist"; break;
            case 21: totals.PlusPoisonResist += abilVal; break;
            case 9: totals.PlusShadowResist += abilVal; statKey = "Shadow Resist"; break;

            case 24: totals.PlusProtEvil += abilVal; statKey = "Prot Evil"; break;
            case 25: totals.PlusProtGood += abilVal; statKey = "Prot Good"; break;
            case 1113: totals.PlusVileWard += abilVal; statKey = "VileWard"; break;

            case 92: totals.PlusPunchDmg += abilVal; statKey = "Punch Dmg"; break;
            case 89: totals.PlusPunchAccy += abilVal; statKey = "Punch Accy"; break;
            case 93: totals.PlusKickDmg += abilVal; statKey = "Kick Dmg"; break;
            case 90: totals.PlusKickAccy += abilVal; statKey = "Kick Accy"; break;
            case 94: totals.PlusJumpKickDmg += abilVal; statKey = "JumpKick Dmg"; break;
            case 91: totals.PlusJumpKickAccy += abilVal; statKey = "JumpKick Accy"; break;
        }

        if (statKey is null) return;

        // DR (ability 7) is stored ×10 — show the divided value; everything
        // else shows the raw signed integer.
        string displayVal = abilId == 7
            ? string.Create(CultureInfo.InvariantCulture, $"{abilVal / 10.0:+0.#;-0.#}")
            : abilVal.ToString("+0;-0", CultureInfo.InvariantCulture);
        AddContribution(result, statKey, itemName, displayVal, tag);
    }

    private static void AddContribution(EquipmentStatBreakdown result, string statKey,
                                        string itemName, string displayValue, string? tag = null)
    {
        if (!result.PerStatSources.TryGetValue(statKey, out List<StatContribution>? sources))
        {
            sources = new List<StatContribution>();
            result.PerStatSources[statKey] = sources;
        }
        sources.Add(new StatContribution(itemName, displayValue, tag));
    }

    // Safe numeric read of an Abil-N / base field off a game-data row: missing
    // or non-numeric properties read as 0.
    private static int GetInt(GameRow row, string property)
    {
        if (!row.TryGetProperty(property, out GameValue el)) return 0;
        return el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v) ? v : 0;
    }
}
