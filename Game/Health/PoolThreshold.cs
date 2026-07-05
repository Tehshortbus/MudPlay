using FujinTerm.Models.Profile;

namespace FujinTerm.Game.Health;

// Shared HP / MA threshold math for the Health tab's dual-radial model: every
// pool threshold (rest / run / hang triggers, heal + bless trigger floors) is
// stored as a raw int that means "percent of max" in Percentage mode or "raw
// pool value" in Absolute mode, per the HP / MA radial the user picked. Both the
// passive HealthManager gates and the active CastingDirector heal / buff pickers
// resolve through here so a mode change is honoured identically on both sides.
internal static class PoolThreshold
{
    // Percentage mode: treat value as 0..100 of max. Absolute mode: pass through
    // as-is. Defensive against max being zero or negative (returns 0 — no
    // false-positive gate fire when prompt data isn't loaded yet).
    public static int Resolve(ThresholdMode mode, int value, int max)
    {
        if (mode == ThresholdMode.Percentage)
        {
            if (max <= 0) return 0;
            return (int)Math.Round(max * (value / 100.0));
        }
        return value;
    }
}
