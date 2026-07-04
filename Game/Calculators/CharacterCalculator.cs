using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using FujinTerm.Game.Inventory;
using FujinTerm.Game.Quests;
using FujinTerm.Services;

namespace FujinTerm.Game.Calculators;

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

    // CP gained when training to the given level:
    // (Floor(level/10) * 5) + 10 — 10 CP per level through 9, 15 through 19,
    // 20 through 29, and so on. Returns 0 below level 1.
    public static int CalcCpGainedAtLevel(int level)
    {
        if (level < 1) return 0;
        return (level / 10) * 5 + 10;
    }

    // Total CP accumulated from level 1 to targetLevel (exclusive upper step:
    // the i = 1..targetLevel-1 loop), plus baseCP from race.
    public static int CalcTotalCpAtLevel(int targetLevel, int baseCP = 0)
    {
        int total = baseCP;
        for (int i = 1; i < targetLevel; i++)
        {
            total += (i / 10) * 5 + 10;
        }
        return total;
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
            JsonElement? row = cache.FindRowByName("Items", item.Name);
            if (row is JsonElement itemData)
                FoldItemRow(result, itemData, item.Name, item.Slot);
        }

        return result;
    }

    // Fold one already-resolved Items row into a fresh EquipmentStatBreakdown —
    // the per-item half of AggregateEquipmentStats, exposed for callers that
    // already hold the JSON row. The Item Finder enumerates the whole Items
    // table, so a name round-trip through GameDataCache.FindRowByName per item
    // would be wasteful. slotTag mirrors EquippedItem.Slot: "Weapon Hand"
    // surfaces the weapon-base fields (Min / Max / StrReq / Type / Speed),
    // "Off-Hand" the off-hand accuracy, any other tag folds as generic worn gear.
    public static EquipmentStatBreakdown AggregateItemRow(
        JsonElement itemRow, string itemName, string slotTag)
    {
        ArgumentNullException.ThrowIfNull(itemName);
        ArgumentNullException.ThrowIfNull(slotTag);

        var result = new EquipmentStatBreakdown();
        if (itemRow.ValueKind == JsonValueKind.Object)
            FoldItemRow(result, itemRow, itemName, slotTag);
        return result;
    }

    private static void FoldItemRow(
        EquipmentStatBreakdown result, JsonElement itemData, string itemName, string slotTag)
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
            totals.WeaponType = GetInt(itemData, "WeaponType");
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
        EquipmentStatBreakdown breakdown, JsonElement data, string sourceName)
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
            case 10: totals.PlusAC += abilVal; statKey = "Armour Class"; tag = "[BLUR]"; break;
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
            case 4: totals.PlusMaxDamage += abilVal; statKey = "Max Damage"; break;
            case 165: totals.SpellDamageBonus += abilVal; statKey = "Spell Damage"; break;

            case 34: totals.PlusDodge += abilVal; statKey = "Dodge"; break;
            case 36: totals.PlusMagicResist += abilVal; statKey = "Magic Resist"; break;

            case 116: totals.PlusBSAccuracy += abilVal; statKey = "BS Accuracy"; break;
            case 117: totals.PlusBSMin += abilVal; statKey = "BS Min Dmg"; break;
            case 118: totals.PlusBSMax += abilVal; statKey = "BS Max Dmg"; break;

            case 27: totals.PlusStealth += abilVal; statKey = "Stealth"; break;
            case 77: totals.PlusPerception += abilVal; statKey = "Perception"; break;
            case 70: totals.PlusSpellcasting += abilVal; statKey = "Spellcasting"; break;
            case 96: totals.PlusEncumbrance += abilVal; statKey = "Encumbrance"; break;
            case 40: case 179: totals.PlusTraps += abilVal; statKey = "Traps"; break;
            case 37: case 180: totals.PlusPicklocks += abilVal; statKey = "Picklocks"; break;
            case 13: case 14: totals.PlusIlluminate += abilVal; statKey = "Illuminate"; break;
            case 67: totals.PlusQuickness += abilVal; statKey = "Quickness"; break;

            case 3: totals.PlusColdResist += abilVal; statKey = "Cold Resist"; break;
            case 5: totals.PlusFireResist += abilVal; statKey = "Fire Resist"; break;
            case 65: totals.PlusStoneResist += abilVal; statKey = "Stone Resist"; break;
            case 66: totals.PlusLightningResist += abilVal; statKey = "Lightning Resist"; break;
            case 147: totals.PlusWaterResist += abilVal; statKey = "Water Resist"; break;

            case 24: totals.PlusProtEvil += abilVal; statKey = "Prot Evil"; break;
            case 25: totals.PlusProtGood += abilVal; statKey = "Prot Good"; break;

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
    private static int GetInt(JsonElement row, string property)
    {
        if (row.ValueKind != JsonValueKind.Object) return 0;
        if (!row.TryGetProperty(property, out JsonElement el)) return 0;
        return el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v) ? v : 0;
    }
}
