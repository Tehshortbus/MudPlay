using System;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Health;

// Resolves a rest trigger + rest-max for a pool. The percentages read against the
// DEFAULT gear set's max (the loadout the user's rest %s are tuned for, so a Pre-rest
// set that swaps a +MaxHP/+MaxMana item doesn't move the target), then BOTH are
// capped at the CURRENT gear's real max so a rest set that lowers the pool can never
// push a target out of reach and strand the rest forever (report
// paradigm-20260902-052036). Two ceilings cap it: the stat-screen max (realMax) AND
// the live gear-swap-aware max (liveMax = PlayerState.MaxHp, kept current by
// EquipmentMaxPoolSync) — whichever is SMALLER. The stat screen goes stale-high after a
// medi/pre-rest swap that lowers the pool (the target then never becomes reachable and
// the rest hangs at full — report paradigm-20260903-110346), so the live max is the
// authoritative reachable ceiling. Falls back to the real / live max for the basis when
// the default-set value isn't known yet. Absolute-mode thresholds pass through
// PoolThreshold.Resolve unchanged; only the max caps still apply.
internal static class RestThresholds
{
    public static (int Trigger, int Max) Resolve(
        ThresholdMode mode, int triggerPct, int maxPct,
        int defaultMax, int realMax, int liveMax)
    {
        int basis = defaultMax > 0 ? defaultMax : realMax > 0 ? realMax : liveMax;
        int trigger = PoolThreshold.Resolve(mode, triggerPct, basis);
        int max = PoolThreshold.Resolve(mode, maxPct, basis);
        // Cap at each known ceiling — the smaller wins, so neither a stale-high stat
        // screen nor a stale-high live max can push a target out of reach.
        if (realMax > 0)
        {
            trigger = Math.Min(trigger, realMax);
            max = Math.Min(max, realMax);
        }
        if (liveMax > 0)
        {
            trigger = Math.Min(trigger, liveMax);
            max = Math.Min(max, liveMax);
        }
        return (trigger, max);
    }

    // A single threshold (a flee / hang / heal trigger) resolved against the same
    // Default-set basis + real-max cap. Heal/run/hang anchor to the Default set's pool
    // like rest does, so a Pre-rest set that alters the pool doesn't shift them.
    public static int ResolveValue(
        ThresholdMode mode, int pct, int defaultMax, int realMax, int liveMax)
        => Resolve(mode, pct, pct, defaultMax, realMax, liveMax).Max;
}
