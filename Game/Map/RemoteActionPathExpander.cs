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
    public static IReadOnlyList<WalkStep> Expand(
        RoomGraphManager graph,
        RoomKey source,
        IReadOnlyList<Direction> directions)
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

}
