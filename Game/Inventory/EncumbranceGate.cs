namespace FujinTerm.Game.Inventory;

// Shared encumbrance-bracket gate math for the ground-collect engines — coins
// in Game.Cash.CashManager and items in AutoGetItemsManager. Given the
// character's carry-weight reading and which encumbrance brackets the user has
// flagged as no-go, it returns the tightest weight the character may carry
// without tipping into a gated bracket.
//
// The bracket start percentages are Stock-calibrated — the same limitation the
// coin gate shipped with (no per-realm branching yet). Both engines read the
// same numbers here so a future realm-aware table only changes one place.
public static class EncumbranceGate
{
    // Stock bracket start percentages: at/above each, the displayed
    // encumbrance label flips to the next-heavier bracket.
    public const int StockLightStartPct  = 17;
    public const int StockMediumStartPct = 34;
    public const int StockHeavyStartPct  = 67;

    // Tightest cap weight across the enabled gate flags. Each gate caps
    // collection at the highest weight that still displays one bracket below it
    // (so a Light gate keeps the character in None). No flags set → full
    // MaxWeight, which by itself is the hard "can't exceed capacity" cap.
    public static long ComputeCapWeight(bool skipLight, bool skipMedium, bool skipHeavy,
                                        EncumbranceReading enc)
    {
        long cap = enc.MaxWeight;
        if (skipHeavy)  cap = Math.Min(cap, GateBoundaryCap(enc.MaxWeight, StockHeavyStartPct));
        if (skipMedium) cap = Math.Min(cap, GateBoundaryCap(enc.MaxWeight, StockMediumStartPct));
        if (skipLight)  cap = Math.Min(cap, GateBoundaryCap(enc.MaxWeight, StockLightStartPct));
        return cap;
    }

    // Largest weight whose displayed percent (floor(weight*100/max)) stays
    // strictly below thresholdPercent — i.e. the most a character can carry
    // without tipping into the next bracket. Integer inverse of the game's
    // rounding: (pct*max - 1) / 100.
    public static long GateBoundaryCap(long maxWeight, long thresholdPercent) =>
        Math.Max(0, (thresholdPercent * maxWeight - 1) / 100);
}
