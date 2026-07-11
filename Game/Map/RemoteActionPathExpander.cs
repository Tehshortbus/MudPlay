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
    // into an explicit walk-there / act / walk-back / cross detour. Without a
    // mapper the exit falls through to the plain single-step form and the
    // send-side dispatcher reports the missing wiring.
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

        var result = new List<WalkStep>(directions.Count);
        RoomKey current = source;

        foreach (Direction dir in directions)
        {
            Room? room = graph.GetRoom(current);
            if (room is null) break;
            if (!room.Exits.TryGetValue(dir, out RoomExit exit)) break;

            // Cross-room multi-action exit — the prerequisite commands live in
            // (and must be typed from) OTHER rooms. Linearize into a detour:
            // walk to each action's room, issue the command, walk back, then
            // cross the exit as a plain cardinal. Same-room multi-action exits
            // keep the single-MoveStep form below (SpecialExitDispatch owns
            // them). A detour that can't be routed truncates the path.
            if (exit.Hint == RoomExitHint.MultiActionHidden
                && exit.MultiAction is { HasRemoteActions: true } maData
                && bfs is not null)
            {
                List<WalkStep>? detour =
                    BuildRemoteActionDetour(graph, bfs, filter, current, dir, exit, maData);
                if (detour is null) break;
                result.AddRange(detour);
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

            result.Add(new MoveStep(dir, exit.Target, label));
            current = exit.Target;
        }

        return result;
    }

    // Linearize a cross-room multi-action exit into walk + command + walk-back +
    // cross steps. hostRoom is where the exit lives (and where the player stands
    // before crossing). For each action in StepNumber order, route to the room
    // the command must be typed in (its RemoteSourceRoom, or hostRoom for a
    // same-row action), emit the command, and continue from there; after the
    // last action, route back to hostRoom and cross the exit as a plain cardinal
    // marked SkipSpecialDispatch (its prerequisites are already emitted).
    //
    // The opened exit stays passable for minutes (a game-side timer, not in the
    // data) so the walk-there / act / walk-back round-trip lands well inside the
    // window; each command's confirmation text is absent from game data, so the
    // commands are fire-and-forget CommandSteps (the walker waits on the next
    // generic prompt, not a matched reply).
    //
    // Returns null when any detour leg can't be routed — the caller truncates so
    // the walk fails as stale rather than crossing an un-primed exit.
    private static List<WalkStep>? BuildRemoteActionDetour(
        RoomGraphManager graph,
        BfsMapper bfs,
        IRoomFilter? filter,
        RoomKey hostRoom,
        Direction crossDir,
        RoomExit exit,
        MultiActionExitData maData)
    {
        var steps = new List<WalkStep>();
        RoomKey cursor = hostRoom;

        foreach (ExitAction action in maData.Actions)
        {
            if (action.Commands.Count == 0) continue;
            RoomKey issueRoom = action.RemoteSourceRoom ?? hostRoom;

            if (!cursor.Equals(issueRoom))
            {
                if (!TryAppendLeg(graph, bfs, filter, steps, cursor, issueRoom)) return null;
                cursor = issueRoom;
            }

            steps.Add(new CommandStep(action.Commands[0]));
        }

        if (!cursor.Equals(hostRoom))
        {
            if (!TryAppendLeg(graph, bfs, filter, steps, cursor, hostRoom)) return null;
        }

        steps.Add(new MoveStep(crossDir, exit.Target) { SkipSpecialDispatch = true });
        return steps;
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
