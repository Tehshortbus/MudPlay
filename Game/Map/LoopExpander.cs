using System.Collections.Generic;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Static expansion of a saved Loop's persistent LoopWaypoint list into the
// flat runtime LoopStep sequence the runner executes. BFS-fills the move
// directions between consecutive waypoints (plus the closing leg back to
// waypoint 0) and inserts per-waypoint commands after each arrival. A leg
// through a lever exit whose actions are typed in other rooms gets the
// walker's go-act-return detour (RemoteActionPathExpander), so a lap opens
// the exit the way a walk-to does.
//
// Pure static helper — no side effects beyond the returned plan (and the
// optional leg memo it fills). Lets the runner do the expansion once at Start
// and the Navigation overlay do it on demand for the right-click "Preview on
// map" pane, both off the same code path.
public static class LoopExpander
{
    // Expand waypoints into the cycle's runtime step list, with the legs that
    // don't route, the lever detours on the lap (step indices into Steps) and
    // the lever rooms a detour couldn't reach. Empty steps + empty unreachables
    // means "too few waypoints to form a cycle" (need at least 2).
    // waypoints is the ordered cycle (closing edge implicit); bfs is the
    // pathfinder bound to graph, the active room graph; filter is an optional
    // avoided-rooms / stash filter. A leg whose lever detour can't be routed is
    // unreachable, as the walker refuses the same route. log gets the detour
    // builder's lines. memo keeps legs already expanded with the same filter.
    public static LoopExpansion Plan(
        IReadOnlyList<LoopWaypoint> waypoints, BfsMapper bfs, RoomGraphManager graph,
        IRoomFilter? filter = null, LogService? log = null, LoopLegMemo? memo = null)
    {
        ArgumentNullException.ThrowIfNull(waypoints);
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(graph);
        if (waypoints.Count < 2) return LoopExpansion.Empty;

        var steps = new List<LoopStep>();
        var unreachable = new List<(RoomKey From, RoomKey To)>();
        var detours = new List<LeverDetour>();
        var unroutable = new List<UnroutableLeverLeg>();

        // A waypoint no route reaches is left out of the lap, and the next leg is
        // planned from the last room the lap does reach. Planned from the missed
        // waypoint instead, that leg's first move was sent from the wrong room (report
        // paradigm-20261106-024151: a relocked door shut the gang-house entrance off,
        // and every Roomba lap walked the entrance's own exit from the room inside it).
        LoopWaypoint here = waypoints[0];
        bool arrived = true;
        for (int i = 0; i < waypoints.Count; i++)
        {
            LoopWaypoint next = waypoints[(i + 1) % waypoints.Count];

            // Per-waypoint command fires on arrival, BEFORE the legs
            // walking away. Lap 1: W[0]'s command fires first.
            // Subsequent laps: W[0]'s command fires after the closing
            // leg lands back at W[0] (i.e. the next iteration of the
            // outer wrap inside the runner).
            if (arrived)
            {
                if (!string.IsNullOrEmpty(here.Command))
                    steps.Add(new CommandLoopStep(here.Command!, here.DelayMs));
                // The lap wait comes after the command: the command goes out on arrival,
                // then the room holds until the lap clock reaches the wait.
                if (here.LapWaitMs > 0)
                    steps.Add(new LapWaitLoopStep(here.LapWaitMs));
            }

            // A zero-length leg — the same room listed twice in a row — is a
            // deliberate "stay put": no movement, just run the next waypoint's
            // command in place. So it's valid, NOT unreachable. Users add it to fire
            // several commands in one room (e.g. hand pies to the barmaid, then
            // convert the coin, without leaving).
            if (here.Key.Equals(next.Key))
            {
                here = next;
                continue;
            }

            LoopLeg leg = memo?.Get(here.Key, next.Key, filter) is { } kept
                ? kept
                : ExpandLeg(here.Key, next.Key, bfs, graph, filter, log);
            memo?.Keep(here.Key, next.Key, filter, leg);
            unroutable.AddRange(leg.Unroutable);
            if (!leg.Reaches)
            {
                unreachable.Add((here.Key, next.Key));
                arrived = false;
                continue;
            }
            foreach (LeverDetour detour in leg.Detours) detours.Add(detour.Offset(steps.Count));
            steps.AddRange(leg.Steps);
            here = next;
            arrived = true;
        }

        return new LoopExpansion(steps, unreachable, detours, unroutable);
    }

    // Plan's steps and unreachable legs, for a caller that needs no more.
    public static (IReadOnlyList<LoopStep> Steps,
                   IReadOnlyList<(RoomKey From, RoomKey To)> UnreachableSegments)
        Expand(IReadOnlyList<LoopWaypoint> waypoints, BfsMapper bfs, RoomGraphManager graph, IRoomFilter? filter = null,
            LoopLegMemo? memo = null)
    {
        LoopExpansion plan = Plan(waypoints, bfs, graph, filter, memo: memo);
        return (plan.Steps, plan.UnreachableSegments);
    }

    // Walk the expanded cycle from the first waypoint and accumulate every
    // room visited (start, each intermediate hop, every waypoint, ending
    // back at the start) — the whole ring, wherever a run enters it. Returns
    // empty when the cycle can't be resolved (too few waypoints, unreachable
    // legs).
    public static IReadOnlyList<RoomKey> ResolveCycleRoomKeys(
        IReadOnlyList<LoopWaypoint> waypoints,
        BfsMapper bfs,
        RoomGraphManager graph,
        IRoomFilter? filter = null,
        LoopLegMemo? memo = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        (IReadOnlyList<LoopStep> steps, _) = Expand(waypoints, bfs, graph, filter, memo);
        if (steps.Count == 0 || waypoints.Count < 2) return Array.Empty<RoomKey>();
        var keys = new List<RoomKey> { waypoints[0].Key };
        WalkMoves(graph, waypoints[0].Key, steps, keys);
        return keys;
    }

    // Every room the cycle passes through, leg by leg, its lever detours
    // included, for a map overlay. Unlike ResolveCycleRoomKeys it doesn't stop
    // at an unreachable leg: that leg's end waypoint just follows unconnected
    // (the map draws no line across a pair that shares no exit) and the walk
    // carries on from there — so a half-fixed loop still shows every leg that
    // does route. A leg whose detour can't be routed shows its plain path.
    public static IReadOnlyList<RoomKey> ResolveLegRoomKeys(
        IReadOnlyList<LoopWaypoint> waypoints,
        BfsMapper bfs,
        RoomGraphManager graph,
        IRoomFilter? filter = null,
        LoopLegMemo? memo = null)
    {
        ArgumentNullException.ThrowIfNull(waypoints);
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(graph);
        if (waypoints.Count < 2) return Array.Empty<RoomKey>();

        var keys = new List<RoomKey> { waypoints[0].Key };
        for (int i = 0; i < waypoints.Count; i++)
        {
            RoomKey from = waypoints[i].Key;
            RoomKey to = waypoints[(i + 1) % waypoints.Count].Key;
            if (from.Equals(to)) continue;
            LoopLeg leg = memo?.Get(from, to, filter) is { } kept ? kept : ExpandLeg(from, to, bfs, graph, filter, log: null);
            memo?.Keep(from, to, filter, leg);
            RoomKey cursor = from;
            if (leg.Reaches)
                cursor = WalkMoves(graph, from, leg.Steps, keys);
            else if (bfs.FindPath(from, to, filter) is { Count: > 0 } path)
                cursor = WalkMoves(graph, from, path.Select(static d => (LoopStep)new MoveLoopStep(d)).ToList(), keys);
            if (!cursor.Equals(to)) keys.Add(to);
        }
        return keys;
    }

    // Follow the move steps from start over the map, adding each room landed in;
    // the room it ends in. Command steps don't change position.
    private static RoomKey WalkMoves(RoomGraphManager graph, RoomKey start, IReadOnlyList<LoopStep> steps, List<RoomKey> keys)
    {
        RoomKey cursor = start;
        foreach (LoopStep step in steps)
        {
            if (step is not MoveLoopStep move) continue;
            if (graph.GetRoom(cursor) is not { } room || !room.Exits.TryGetValue(move.Direction, out RoomExit exit)) break;
            cursor = exit.Target;
            keys.Add(cursor);
        }
        return cursor;
    }

    private static LoopLeg ExpandLeg(
        RoomKey from, RoomKey to, BfsMapper bfs, RoomGraphManager graph, IRoomFilter? filter, LogService? log)
    {
        IReadOnlyList<Direction>? path = bfs.FindPath(from, to, filter);
        if (path is null || path.Count == 0) return LoopLeg.NoRoute;

        var unroutable = new List<UnroutableLeverLeg>();
        var spans = new List<LeverDetour>();
        IReadOnlyList<WalkStep> walk = RemoteActionPathExpander.Expand(
            graph, from, path, bfs, filter, log, unroutable: unroutable, spans: spans);
        if (!RemoteActionPathExpander.ReachesDestination(walk, to))
            return new LoopLeg(false, Array.Empty<LoopStep>(), Array.Empty<LeverDetour>(), unroutable);

        var steps = new List<LoopStep>(walk.Count);
        foreach (WalkStep step in walk)
        {
            steps.Add(step switch
            {
                MoveStep move => new MoveLoopStep(move.Direction) { SkipSpecialDispatch = move.SkipSpecialDispatch },
                CommandStep command => new CommandLoopStep(command.Command)
                    { IsExitAction = true, IsWinchPull = command.IsWinchPull, DoorOrGate = command.DoorOrGate },
                _ => throw new InvalidOperationException($"a loop leg expanded to a {step.GetType().Name}"),
            });
        }
        return new LoopLeg(true, steps, spans, unroutable);
    }
}
