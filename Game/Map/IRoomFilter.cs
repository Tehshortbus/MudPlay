using System;

namespace FujinTerm.Game.Map;

// Pathing-time room filter — when supplied to BfsMapper.FindPath, any
// room with IsAvoided=true is treated as a non-traversable node (cannot be
// on a path; cannot be a path's intermediate hop). The source and
// destination are evaluated by the caller so a user can still SetLocated
// on an avoided room without breaking the walker.
//
// The interface is kept separate from its per-character AvoidedRoomFilter
// implementation so the BFS engine can be tested without wiring the
// profile layer.
public interface IRoomFilter
{
    bool IsAvoided(RoomKey key);

    // True when the exit's movement restriction (e.g. a Form-A
    // (Level: MIN to MAX) gate) excludes the player, making the exit
    // non-traversable for path planning. Default implementation never
    // blocks — only filters that know the player's level
    // (Services.MovementFilter) override it. BFS skips a blocked exit the
    // same way it skips an avoided room; the walker can re-probe with gates
    // ignored to tell "all routes level-gated" apart from
    // "graph-disconnected".
    bool IsExitBlocked(in RoomExit exit) => false;

    // Called once at walk-start (walker approach, loop expansion) with the
    // route's endpoints so a filter can warm any route-scoped state before
    // BFS plans the path. MovementFilter uses it to fire the party @wealth
    // probe — but only when the tolls-permitted shortest route actually
    // crosses a toll, so an off-path toll edge inside the BFS frontier never
    // triggers a poll. Default is a no-op; only the wealth-aware filter
    // overrides it.
    void WarmForRoute(BfsMapper bfs, RoomKey source, RoomKey destination) { }

    // Stands the acquirable gates (item / ticket / key-door / hazard) down for
    // a single planning pass, so a caller can compute the route the crosser
    // WOULD take with every gate item in hand — the "gated" alternative the
    // route picker compares against the free route. Dispose to restore gating.
    // Default is a no-op scope for filters that don't model acquirable gates;
    // only Services.MovementFilter suspends anything real.
    IDisposable SuspendAcquirableGates() => NoGateSuspension.Instance;

    // The default-implementation's inert scope — disposing it does nothing.
    private sealed class NoGateSuspension : IDisposable
    {
        public static readonly NoGateSuspension Instance = new();
        public void Dispose() { }
    }
}
