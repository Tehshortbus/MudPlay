namespace FujinTerm.Game.Calculators;

/// <summary>
/// One level's projected progression numbers — the pure output of
/// <see cref="LevelProjectionCalculator"/> that the Workshop Level Projection
/// grid formats into a row. HP is a bracket (the per-level rolls are random);
/// <see cref="Mana"/> and the two regens are deterministic. <see cref="Mana"/>
/// is 0 for non-casters.
/// </summary>
/// <remarks>
/// <see cref="TotalXp"/> is the cumulative exp threshold to reach this level
/// (saturates at <see cref="long.MaxValue"/> rather than overflowing). The
/// "exp remaining to reach this level" the grid shows is derived per-row from
/// <c>TotalXp - currentExp</c>, so it isn't stored here.
/// </remarks>
public readonly record struct LevelProjection(
    int Level,
    long TotalXp,
    int HpMin,
    int HpMax,
    int HpRegen,
    int Mana,
    int MpRegen);
