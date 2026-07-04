namespace FujinTerm.Game.Calculators;

// Assembles the per-level rows the Workshop Level Projection grid shows,
// composing ExperienceTableCalculator and CharacterCalculator for a given race /
// class / realm. Pure — no UI and no game-data reads: the caller resolves the
// class/race fields (ExpTable, MinHits/MaxHits, HPPerLVL, MageryType/MageryLVL)
// and passes them in.
// The projection is gear-independent (base race/class progression), so the
// equipment +MaxHP / regen-% inputs are passed as 0 and the HP/MP regens are the
// non-resting, non-meditating per-tick base. Each call projects a single level
// from the stats supplied for it, so the caller varies HEA/INT/WIL/CHM per level
// to layer in the CP Allocation plan.
public static class LevelProjectionCalculator
{
    // Project a single level's numbers.
    public static LevelProjection ProjectLevel(
        int level, int chart,
        int health, int intellect, int willpower, int charm,
        int minHitsPerLevel, int maxHitsPerLevel, int raceHpPerLevel,
        int mageryType, int mageryLevel,
        RealmType realm)
    {
        // Cumulative exp threshold to reach this level. The grid derives the
        // "exp remaining" from this minus the character's current exp.
        long total = ExperienceTableCalculator.CalcExpNeeded(level, chart, realm);

        int hpMin = CharacterCalculator.CalcMaxHp(health, level, minHitsPerLevel, maxHitsPerLevel,
            raceHpPerLevel, plusMaxHp: 0, HpRollMode.Min);
        int hpMax = CharacterCalculator.CalcMaxHp(health, level, minHitsPerLevel, maxHitsPerLevel,
            raceHpPerLevel, plusMaxHp: 0, HpRollMode.Max);
        int hpRegen = CharacterCalculator.CalcHpRegen(level, health, hpRegenPercent: 0, isResting: false, realm);

        // Magery type 5 (Mystic) carries Kai, not Mana — the mana formula gives
        // wildly wrong values for it.
        int mana = mageryType == 5
            ? CharacterCalculator.CalcMaxKai(level)
            : CharacterCalculator.CalcMaxMana(mageryLevel, level, plusMaxMana: 0);
        int mpRegen = CharacterCalculator.CalcManaRegen(level, intellect, willpower, charm,
            mageryType, mageryLevel, mpRegenPercent: 0, isMeditating: false, realm);

        return new LevelProjection(level, total, hpMin, hpMax, hpRegen, mana, mpRegen);
    }
}
