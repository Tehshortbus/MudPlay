namespace FujinTerm.Game.Calculators;

// One level's projected progression numbers — the pure output of
// LevelProjectionCalculator that the Workshop Level Projection grid formats into
// a row. HP is a bracket (the per-level rolls are random); Mana and the two
// regens are deterministic. Mana is 0 for non-casters.
// TotalXp is the cumulative exp threshold to reach this level (saturates at
// long.MaxValue rather than overflowing). The "exp remaining to reach this
// level" the grid shows is derived per-row from TotalXp - currentExp, so it
// isn't stored here.
public readonly record struct LevelProjection(
    int Level,
    long TotalXp,
    int HpMin,
    int HpMax,
    int HpRegen,
    int Mana,
    int MpRegen);
