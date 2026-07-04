namespace FujinTerm.Game.Map;

// Converts a raw BFS hop count to an estimated wall-clock duration the
// AutoLairScheduler uses for travel-time scoring. Pulled out as an
// interface so the encumbrance-aware implementation (driven by
// AutoLairSettings) can drop in without rewriting the scheduler.
//
// The scheduler treats hop time as a per-edge cost, not a function of the
// path's source / destination. Per-segment overrides (e.g. a known-slow
// corridor with doors that delay each step) would need a richer surface.
//
// Implementations must be cheap to call — PickNext invokes EstimateTravel
// once per candidate every tick. Caching belongs inside the
// implementation if the model does any non-trivial work; the scheduler
// passes hop counts only.
public interface ITravelCostModel
{
    // Estimate the wall-clock duration to traverse hopCount BFS hops at the
    // current encumbrance. Implementations that don't observe encumbrance
    // (e.g. FlatTravelCostModel) return the same value regardless. hopCount
    // is a non-negative hop count from BFS.
    TimeSpan EstimateTravel(int hopCount);

    // Cost of a single hop AT THE LAIR ENTRY — the final step from
    // wait-room into the lair itself. Logically the same as one call to
    // EstimateTravel with hopCount=1, but called out separately so the
    // scheduler can document the entry-step as a distinct timing boundary
    // in its decision log.
    TimeSpan EntryHopDuration => EstimateTravel(1);
}
