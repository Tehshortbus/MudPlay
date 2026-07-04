namespace FujinTerm.Game.Calculators;

// Pure banked-exp budgeting for auto-train / Train Now: how many levels above the
// current one the character's banked exp can already reach, and how many of those
// to actually train once the configured reserve buffer is held back. Mirrors the
// per-row "Exp to level == 0" test the Level Projection grid uses, so the count
// is always in lock-step with what that grid shows. No UI and no game-data reads
// — the caller resolves the exp total, level, chart and realm and passes them in.
public static class TrainBudgetCalculator
{
    // Count of levels strictly above currentLevel whose cumulative exp threshold
    // exp already meets — i.e. the number of banked levels ready to train.
    // Thresholds are monotonic, so the first miss ends the scan; cap bounds it (a
    // safety net against a misbehaving curve). A level sitting exactly on its
    // threshold counts as reachable.
    public static int BankableLevels(long exp, int currentLevel, int chart, RealmType realm, int cap)
    {
        if (currentLevel <= 0 || chart <= 0 || cap <= 0) return 0;
        int count = 0;
        for (int level = currentLevel + 1; level <= currentLevel + cap; level++)
        {
            if (exp < ExperienceTableCalculator.CalcExpNeeded(level, chart, realm)) break;
            count++;
        }
        return count;
    }

    // Levels to train right now: the BankableLevels count less the reserve keep
    // the character always carries, clamped at 0. A negative or zero result means
    // "nothing to train — the reserve already covers everything banked".
    public static int LevelsToTrain(long exp, int currentLevel, int chart, RealmType realm, int keep, int cap)
        => System.Math.Max(0, BankableLevels(exp, currentLevel, chart, realm, cap) - System.Math.Max(0, keep));
}
