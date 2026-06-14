namespace FujinTerm.Game.Calculators;

/// <summary>
/// Pure character-stat formulas (CP, HP, mana/kai regen) ported from the
/// MMUD Explorer VB6 source. Every method takes primitive inputs and returns
/// a result — no UI, no game-data, no manager dependencies. Realm-dependent
/// formulas branch on <see cref="RealmType"/>; callers resolve the active
/// realm from <see cref="Services.GameDataCache.ActiveRealm"/>.
/// </summary>
/// <remarks>
/// Experience-curve math lives in <see cref="ExperienceTableCalculator"/>;
/// combat math lives in <see cref="CombatCalculator"/>. Equipment stat
/// aggregation (ability-slot scanning) is deferred until the equipment model
/// exists in a later Workshop PR.
/// </remarks>
public static class CharacterCalculator
{
    // ----- CP --------------------------------------------------------------

    /// <summary>
    /// CP gained when training to <paramref name="level"/>. VB6
    /// <c>CalcCPLevel</c> step: <c>(Floor(level/10) * 5) + 10</c> — 10 CP per
    /// level through 9, 15 through 19, 20 through 29, and so on. Returns 0
    /// below level 1.
    /// </summary>
    public static int CalcCpGainedAtLevel(int level)
    {
        if (level < 1) return 0;
        return (level / 10) * 5 + 10;
    }

    /// <summary>
    /// Total CP accumulated from level 1 to <paramref name="targetLevel"/>
    /// (exclusive upper step, matching VB6 <c>CalcCPLevel</c>'s
    /// <c>i = 1..nLevel-1</c> loop), plus <paramref name="baseCP"/> from race.
    /// </summary>
    public static int CalcTotalCpAtLevel(int targetLevel, int baseCP = 0)
    {
        int total = baseCP;
        for (int i = 1; i < targetLevel; i++)
        {
            total += (i / 10) * 5 + 10;
        }
        return total;
    }

    /// <summary>
    /// CP cost to raise a stat by one point from <paramref name="currentStat"/>.
    /// <c>cost = Floor((currentStat - raceMin) / 10) + 1</c>. ParaMUD has no
    /// cap; Stock caps the per-point cost at 10.
    /// </summary>
    public static int CalcCpCostForStatPoint(int raceMin, int currentStat, RealmType realmType = RealmType.ParaMud)
    {
        int delta = currentStat - raceMin;
        if (delta < 0) delta = 0;
        int cost = (delta / 10) + 1;
        if (realmType == RealmType.Stock && cost > 10)
            cost = 10;
        return cost;
    }

    /// <summary>
    /// Total CP cost to raise a stat from <paramref name="startVal"/> to
    /// <paramref name="endVal"/>, summing <see cref="CalcCpCostForStatPoint"/>
    /// over each point.
    /// </summary>
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

    /// <summary>
    /// Estimate max HP at a level. VB6 core:
    /// <c>(HEA/2 + Level*MinHitsPerLevel) + ((HEA-50)*Level)/16 + Random</c>,
    /// then <c>+ raceHpPerLevel*Level + plusMaxHp</c>. In game data
    /// <c>MaxHits</c> is the random range (a delta, not an absolute), so the
    /// random portion brackets to the chosen <paramref name="rollMode"/>.
    /// </summary>
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

    /// <summary>
    /// HP regen per tick. Base <c>(level+20)*health/divisor</c> with divisor
    /// 500 (ParaMUD) or 750 (Stock), floored at 1, tripled while resting, then
    /// scaled by equipment <c>+HP-regen%</c>.
    /// </summary>
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

    /// <summary>
    /// Max mana: <c>(mageryLevel * level * 2) + 6 + plusMaxMana</c>. Returns 0
    /// for non-casters (<paramref name="mageryLevel"/> ≤ 0).
    /// </summary>
    public static int CalcMaxMana(int mageryLevel, int level, int plusMaxMana)
    {
        if (mageryLevel <= 0) return 0;
        return (mageryLevel * level * 2) + 6 + plusMaxMana;
    }

    /// <summary>
    /// Max Kai for Mystics (magery type 5). Kai is not mana — the mana formula
    /// gives wildly wrong values for Mystics — it approximates to
    /// <c>level - 1</c> (a level-82 Mystic has 81 Kai), with no equipment
    /// contribution. Returns 0 below level 2.
    /// </summary>
    public static int CalcMaxKai(int level)
    {
        if (level <= 1) return 0;
        return level - 1;
    }

    /// <summary>
    /// Mana regen per tick. Base stat depends on magery type
    /// (1=INT, 2=WIL, 3=(INT+WIL)/2, 4=CHM, 5=Kai fixed-rate, 0=none); core
    /// is <c>((level+20)*baseStat*(mageryLevel+2))/1650</c>. While meditating
    /// the core value is returned before the equipment <c>+mana-regen%</c>
    /// modifier, which itself differs by realm.
    /// </summary>
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

        // Meditating exits before the equipment modifier (VB6 early-return).
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
}
