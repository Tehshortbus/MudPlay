using System.Collections.Generic;

namespace FujinTerm.Game.Map;

// Static expansion of a saved Loop's persistent LoopWaypoint list into the
// flat runtime LoopStep sequence the runner executes. BFS-fills the move
// directions between consecutive waypoints (plus the closing leg back to
// waypoint 0) and inserts per-waypoint commands after each arrival.
//
// Pure static helper — takes no state and produces no side effects beyond
// the returned tuple. Lets the runner do the expansion once at Start and
// the Navigation overlay do it on demand for the right-click "Preview on
// map" pane, both off the same code path.
public static class LoopExpander
{
    // Expand waypoints into the cycle's runtime step list. Returns the flat
    // list of moves + commands AND a per-leg unreachable list so callers
    // can surface "fix the loop" hints. Empty steps + empty unreachables
    // means "too few waypoints to form a cycle" (need at least 2).
    // waypoints is the ordered cycle (closing edge implicit); bfs is the
    // pathfinder bound to the active room graph; filter is an optional
    // avoided-rooms / stash filter.
    public static (IReadOnlyList<LoopStep> Steps,
                   IReadOnlyList<(RoomKey From, RoomKey To)> UnreachableSegments)
        Expand(IReadOnlyList<LoopWaypoint> waypoints, BfsMapper bfs, IRoomFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(waypoints);
        ArgumentNullException.ThrowIfNull(bfs);
        if (waypoints.Count < 2)
            return (Array.Empty<LoopStep>(), Array.Empty<(RoomKey, RoomKey)>());

        var steps = new List<LoopStep>();
        var unreachable = new List<(RoomKey From, RoomKey To)>();

        for (int i = 0; i < waypoints.Count; i++)
        {
            LoopWaypoint here = waypoints[i];
            LoopWaypoint next = waypoints[(i + 1) % waypoints.Count];

            // Per-waypoint command fires on arrival, BEFORE the legs
            // walking away. Lap 1: W[0]'s command fires first.
            // Subsequent laps: W[0]'s command fires after the closing
            // leg lands back at W[0] (i.e. the next iteration of the
            // outer wrap inside the runner).
            if (!string.IsNullOrEmpty(here.Command))
                steps.Add(new CommandLoopStep(here.Command!, here.DelayMs));

            AppendMoves(here.Key, next.Key, bfs, filter, steps, unreachable);
        }

        return (steps, unreachable);
    }

    // Walk the expanded cycle from the first waypoint and accumulate every
    // room visited (start, each intermediate hop, every waypoint, ending
    // back at the start). Used by the map overlay to render the closed-loop
    // polyline anchored at the rotation entry. Returns empty when the cycle
    // can't be resolved (too few waypoints, unreachable legs).
    public static IReadOnlyList<RoomKey> ResolveCycleRoomKeys(
        IReadOnlyList<LoopWaypoint> waypoints,
        BfsMapper bfs,
        RoomGraphManager graph,
        IRoomFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        (IReadOnlyList<LoopStep> steps, _) = Expand(waypoints, bfs, filter);
        if (steps.Count == 0 || waypoints.Count < 2) return Array.Empty<RoomKey>();

        // Walk the move steps from the FIRST waypoint and accumulate
        // each landing room. Command steps don't change position so
        // they're skipped for the polyline.
        RoomKey start = waypoints[0].Key;
        var keys = new List<RoomKey>(steps.Count + 1) { start };
        RoomKey cursor = start;
        foreach (LoopStep step in steps)
        {
            if (step is not MoveLoopStep move) continue;
            if (graph.GetRoom(cursor) is not { } room) break;
            if (!room.Exits.TryGetValue(move.Direction, out RoomExit exit)) break;
            cursor = exit.Target;
            keys.Add(cursor);
        }
        return keys;
    }

    private static void AppendMoves(
        RoomKey from, RoomKey to,
        BfsMapper bfs, IRoomFilter? filter,
        List<LoopStep> steps,
        List<(RoomKey From, RoomKey To)> unreachable)
    {
        IReadOnlyList<Direction>? path = bfs.FindPath(from, to, filter);
        if (path is null || path.Count == 0)
        {
            unreachable.Add((from, to));
            return;
        }
        foreach (Direction d in path) steps.Add(new MoveLoopStep(d));
    }
}
