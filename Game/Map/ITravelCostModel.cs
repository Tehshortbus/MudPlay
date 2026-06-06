namespace FujinTerm.Game.Map;

/// <summary>
/// Converts a raw BFS hop count to an estimated wall-clock duration the
/// <see cref="AutoLairScheduler"/> uses for travel-time scoring. Pulled
/// out as an interface so the encumbrance-aware implementation (PR 7.24,
/// driven by <see cref="Models.Profile.AutoLairSettings"/>) can drop in
/// without rewriting the scheduler.
/// </summary>
/// <remarks>
/// <para>
/// The scheduler treats hop time as a per-edge cost, not a function of
/// the path's source / destination. Per-segment overrides (e.g. a
/// known-slow corridor with doors that delay each step) would need a
/// richer surface — punt until a calibration session proves it matters.
/// </para>
/// <para>
/// Implementations must be cheap to call — <see cref="AutoLairScheduler.PickNext"/>
/// invokes <see cref="EstimateTravel(int)"/> once per candidate every
/// tick. Caching belongs inside the implementation if the model does
/// any non-trivial work; the scheduler passes hop counts only.
/// </para>
/// </remarks>
public interface ITravelCostModel
{
    /// <summary>
    /// Estimate the wall-clock duration to traverse <paramref name="hopCount"/>
    /// BFS hops at the current encumbrance. Implementations that don't
    /// observe encumbrance (e.g. <see cref="FlatTravelCostModel"/>)
    /// return the same value regardless.
    /// </summary>
    /// <param name="hopCount">Non-negative hop count from BFS.</param>
    TimeSpan EstimateTravel(int hopCount);

    /// <summary>
    /// Cost of a single hop AT THE LAIR ENTRY — the final step from
    /// wait-room into the lair itself. Logically the same as one call
    /// to <see cref="EstimateTravel"/> with hopCount=1, but called out
    /// separately so the scheduler can document the entry-step as a
    /// distinct timing boundary in its decision log.
    /// </summary>
    TimeSpan EntryHopDuration => EstimateTravel(1);
}
