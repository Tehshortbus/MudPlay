using System.Collections.Generic;

namespace FujinTerm.Game.Map;

// Flattens a BFS shortest-path direction list into the linear WalkStep
// sequence the walker actually executes. Inserts CommandStep entries for
// in-room prerequisites (door opens today; lever pulls / button presses when
// game data describes them).
//
// Game-data sourcing: prerequisite actions are read from the static
// RoomExit.Hint imported alongside each exit cell. If game data is silent about
// a remote action on a given exit, the expander treats it as a plain passage —
// there's no per-room hardcoding here.
//
// Door wording matches the MajorMUD verb form (open door <direction>). The
// direction is the full word (north, east, …) since that's the form the server
// accepts on the door verb; movements themselves use the abbreviated form
// (n, e, …) which AutoWalkManager.EncodeMove handles.
public static class RemoteActionPathExpander
{
    // Expand directions against the actual exits rooted at source. Stops at the
    // first step whose source room or exit cell can't be resolved — the walker
    // will detect the truncation as a stale-path failure when it runs out of
    // steps before reaching the destination.
    //
    // bfs/filter are optional: when supplied, a cross-room multi-action exit
    // (its prerequisite commands must be typed in OTHER rooms) is linearized
    // into an explicit go-act-return detour, spliced into the approach at the
    // room nearest the action rooms. Without a mapper the exit falls through to
    // the plain single-step form and the send-side dispatcher reports the
    // missing wiring.
    public static IReadOnlyList<WalkStep> Expand(
        RoomGraphManager graph,
        RoomKey source,
        IReadOnlyList<Direction> directions,
        BfsMapper? bfs = null,
        IRoomFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(directions);

        if (directions.Count == 0) return Array.Empty<WalkStep>();

        var steps = new List<WalkStep>(directions.Count);
        // roomBefore[i] is the room the walker stands in immediately before
        // executing steps[i]. Those approach rooms (plus the exit's host room)
        // are the candidate anchors for a lever detour.
        var roomBefore = new List<RoomKey>(directions.Count);
        // Round-trip lever detours to splice in before their exit's cross step,
        // each keyed by the step index at which the walker stands in its anchor.
        var detours = new List<(int InsertAt, List<WalkStep> Steps)>();

        RoomKey current = source;

        foreach (Direction dir in directions)
        {
            Room? room = graph.GetRoom(current);
            if (room is null) break;
            if (!room.Exits.TryGetValue(dir, out RoomExit exit)) break;

            // Cross-room multi-action exit — the prerequisite commands live in
            // (and must be typed from) OTHER rooms. Build a round-trip detour
            // that visits each action room and returns to a chosen approach
            // room, then cross the exit as a plain cardinal. Anchoring the
            // detour at the approach room nearest the action rooms — not at the
            // exit's host room — pulls the levers on the way past instead of
            // overshooting to the door and backtracking to them. Same-room
            // multi-action exits keep the single-MoveStep form below
            // (SpecialExitDispatch owns them). A detour that can't be routed
            // truncates the path.
            if (exit.Hint == RoomExitHint.MultiActionHidden
                && exit.MultiAction is { HasRemoteActions: true } maData
                && bfs is not null)
            {
                (int InsertAt, List<WalkStep> Steps)? detour =
                    BuildAnchoredDetour(graph, bfs, filter, roomBefore, current, maData);
                if (detour is null) break;
                if (detour.Value.Steps.Count > 0) detours.Add(detour.Value);

                roomBefore.Add(current);
                steps.Add(new MoveStep(dir, exit.Target) { SkipSpecialDispatch = true });
                current = exit.Target;
                continue;
            }

            // Door + KeyLocked prerequisites are not encoded as CommandStep at
            // expand-time. The walker checks exit.Hint at step-send time and
            // routes through DoorOpenManager / the keyed-door FSM before letting
            // the MoveStep bytes go out. The real door verb is `open <dir>` (no
            // "door" word) — synthesising `open door <dir>` here triggered the
            // "Syntax: OPEN {Direction|Item}" failure.

            // Text exits — `(Text: cmd1, cmd2, ...)` — are traversed by a
            // fixed command, never the cardinal. Pin the first alternative
            // as the step's display label so the Navigation step list shows
            // the command the walker actually sends (e.g. "borrow skiff")
            // rather than the misleading direction. Mirrors the send-side
            // choice in AutoWalkManager.SendMoveStep (first TextCommand).
            string? label = exit.Hint == RoomExitHint.Text
                            && exit.TextCommands is { Count: > 0 } cmds
                ? cmds[0]
                : null;

            roomBefore.Add(current);
            steps.Add(new MoveStep(dir, exit.Target, label));
            current = exit.Target;
        }

        // Splice detours in from the highest anchor index down so earlier
        // insertion indices stay valid as later inserts shift the tail.
        detours.Sort(static (a, b) => b.InsertAt - a.InsertAt);
        foreach ((int insertAt, List<WalkStep> dsteps) in detours)
            steps.InsertRange(insertAt, dsteps);

        return steps;
    }

    // Build the round-trip lever detour for a cross-room multi-action exit and
    // pick where on the approach to splice it. host is the room the exit
    // departs from (where the player stands before crossing); approach is the
    // ordered rooms already traversed to reach it (roomBefore). For an
    // all-remote exit the anchor is the approach room (or host) from which
    // visiting every action room and returning is cheapest, so the levers are
    // pulled as the walker passes nearest them rather than after climbing all
    // the way to the door. Returns (insertAt, steps): insertAt is the step index
    // at which the walker stands in the anchor room; steps is the round-trip
    // that ends back there. A same-room action (RemoteSourceRoom null) must be
    // typed at host, so a mixed exit stays host-anchored (the historical form).
    //
    // The opened exit stays passable for minutes (a game-side timer, not in the
    // data) so the go-act-return round-trip lands well inside the window; each
    // command's confirmation text is absent from game data, so the commands are
    // fire-and-forget CommandSteps (the walker waits on the next generic prompt,
    // not a matched reply).
    //
    // Returns null when no anchor can route the full round-trip — the caller
    // truncates so the walk fails as stale rather than crossing an un-primed
    // exit.
    private static (int InsertAt, List<WalkStep> Steps)? BuildAnchoredDetour(
        RoomGraphManager graph,
        BfsMapper bfs,
        IRoomFilter? filter,
        IReadOnlyList<RoomKey> approach,
        RoomKey host,
        MultiActionExitData maData)
    {
        int hostIndex = approach.Count;

        bool allRemote = true;
        foreach (ExitAction a in maData.Actions)
            if (a.Commands.Count > 0 && a.RemoteSourceRoom is null) { allRemote = false; break; }

        // Mixed same-room + remote actions must issue the same-room commands at
        // host, so keep the whole detour anchored there (nearest-approach
        // anchoring only applies when every action lives in another room).
        if (!allRemote)
        {
            List<WalkStep>? hostSteps = BuildDetourFromAnchor(graph, bfs, filter, host, maData);
            return hostSteps is null ? null : (hostIndex, hostSteps);
        }

        // Only the anchor→firstAction and lastAction→anchor legs vary with the
        // anchor choice; the inter-action legs are anchor-independent. So the
        // cheapest anchor is the one minimising those two legs' combined length.
        RoomKey? first = null, last = null;
        foreach (ExitAction a in maData.Actions)
        {
            if (a is not { Commands.Count: > 0, RemoteSourceRoom: { } r }) continue;
            first ??= r;
            last = r;
        }

        // HasRemoteActions counts a remote action with no command; if that's all
        // there is, there's nothing to type — fall back to a host anchor.
        if (first is null || last is null)
        {
            List<WalkStep>? plain = BuildDetourFromAnchor(graph, bfs, filter, host, maData);
            return plain is null ? null : (hostIndex, plain);
        }

        int bestIndex = -1;
        int bestVariable = int.MaxValue;
        for (int i = 0; i <= hostIndex; i++)
        {
            RoomKey anchor = i < hostIndex ? approach[i] : host;
            int? toFirst = Dist(bfs, filter, anchor, first.Value);
            if (toFirst is null) continue;
            int? fromLast = Dist(bfs, filter, last.Value, anchor);
            if (fromLast is null) continue;
            int variable = toFirst.Value + fromLast.Value;
            if (variable < bestVariable) { bestVariable = variable; bestIndex = i; }
        }

        if (bestIndex < 0) return null;

        RoomKey bestAnchor = bestIndex < hostIndex ? approach[bestIndex] : host;
        List<WalkStep>? steps = BuildDetourFromAnchor(graph, bfs, filter, bestAnchor, maData);
        return steps is null ? null : (bestIndex, steps);
    }

    // Linearize the multi-action prerequisites into a go-act-return detour
    // rooted at anchor: for each action in StepNumber order, route to the room
    // the command must be typed in (its RemoteSourceRoom, or anchor for a
    // same-row action), emit the command, and continue from there; after the
    // last action, route back to anchor. Emitting the commands in order keeps a
    // "specific order" exit valid, and a single anchor keeps them contiguous.
    // Returns null when any leg can't be routed.
    private static List<WalkStep>? BuildDetourFromAnchor(
        RoomGraphManager graph,
        BfsMapper bfs,
        IRoomFilter? filter,
        RoomKey anchor,
        MultiActionExitData maData)
    {
        var steps = new List<WalkStep>();
        RoomKey cursor = anchor;

        foreach (ExitAction action in maData.Actions)
        {
            if (action.Commands.Count == 0) continue;
            RoomKey issueRoom = action.RemoteSourceRoom ?? anchor;

            if (!cursor.Equals(issueRoom))
            {
                if (!TryAppendLeg(graph, bfs, filter, steps, cursor, issueRoom)) return null;
                cursor = issueRoom;
            }

            steps.Add(new CommandStep(action.Commands[0]));
        }

        if (!cursor.Equals(anchor))
        {
            if (!TryAppendLeg(graph, bfs, filter, steps, cursor, anchor)) return null;
        }

        return steps;
    }

    // BFS hop count from→to (0 when equal), or null when unroutable. Used only
    // to score anchor candidates, so it discards the direction list.
    private static int? Dist(BfsMapper bfs, IRoomFilter? filter, RoomKey from, RoomKey to)
    {
        if (from.Equals(to)) return 0;
        IReadOnlyList<Direction>? legs = bfs.FindPath(from, to, filter);
        return legs is { Count: > 0 } ? legs.Count : null;
    }

    // Route from→to via BFS and append the leg as MoveSteps (Text exits carry
    // their command label). Detour legs are ordinary passages in practice, so
    // nested multi-action exits aren't re-expanded here. False when no route
    // exists or a hop can't be resolved.
    private static bool TryAppendLeg(
        RoomGraphManager graph,
        BfsMapper bfs,
        IRoomFilter? filter,
        List<WalkStep> steps,
        RoomKey from,
        RoomKey to)
    {
        IReadOnlyList<Direction>? legs = bfs.FindPath(from, to, filter);
        if (legs is null || legs.Count == 0) return false;

        RoomKey cur = from;
        foreach (Direction d in legs)
        {
            Room? room = graph.GetRoom(cur);
            if (room is null || !room.Exits.TryGetValue(d, out RoomExit e)) return false;
            string? label = e.Hint == RoomExitHint.Text && e.TextCommands is { Count: > 0 } tc
                ? tc[0]
                : null;
            steps.Add(new MoveStep(d, e.Target, label));
            cur = e.Target;
        }
        return true;
    }
}
