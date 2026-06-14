namespace FujinTerm.Game.Calculators;

/// <summary>
/// Which per-level HP roll to assume when estimating max HP. MajorMUD rolls
/// a random HP gain in <c>[0, range]</c> each level; the projection picks one
/// of these to bracket the unknown rolls.
/// </summary>
public enum HpRollMode
{
    /// <summary>Level-1 max roll, then all-zero rolls after (lower bound).</summary>
    Min,

    /// <summary>Level-1 max roll, then average rolls thereafter (expected value).</summary>
    Average,

    /// <summary>Max roll every level (upper bound).</summary>
    Max,
}
