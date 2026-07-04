namespace FujinTerm.Game.Calculators;

// Which per-level HP roll to assume when estimating max HP. MajorMUD rolls a
// random HP gain in [0, range] each level; the projection picks one of these to
// bracket the unknown rolls.
public enum HpRollMode
{
    // Level-1 max roll, then all-zero rolls after (lower bound).
    Min,

    // Level-1 max roll, then average rolls thereafter (expected value).
    Average,

    // Max roll every level (upper bound).
    Max,
}
