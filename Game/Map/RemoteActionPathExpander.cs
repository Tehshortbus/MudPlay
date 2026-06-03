using System.Collections.Generic;

namespace FujinTerm.Game.Map;

/// <summary>
/// Flattens a BFS shortest-path direction list into the linear
/// <see cref="WalkStep"/> sequence the walker actually executes.
/// Inserts <see cref="CommandStep"/> entries for in-room prerequisites
/// (door opens today; lever pulls / button presses when game data
/// describes them).
/// </summary>
/// <remarks>
/// <para>
/// <b>Game-data sourcing</b> (per the Phase 7 doc): prerequisite
/// actions are read from the static <see cref="RoomExit.Hint"/>
/// imported alongside each exit cell. If game data is silent about
/// a remote action on a given exit, the expander treats it as a
/// plain passage — there's no per-room hardcoding here.
/// </para>
/// <para>
/// Door wording matches the MajorMUD verb form (<c>open door &lt;direction&gt;</c>).
/// The direction is the full word (<c>north</c>, <c>east</c>, …) since
/// that's the form the server accepts on the door verb; movements
/// themselves use the abbreviated form (<c>n</c>, <c>e</c>, …) which
/// <see cref="AutoWalkManager.EncodeMove"/> handles.
/// </para>
/// </remarks>
public static class RemoteActionPathExpander
{
    /// <summary>
    /// Expand <paramref name="directions"/> against the actual exits
    /// rooted at <paramref name="source"/>. Stops at the first step
    /// whose source room or exit cell can't be resolved — the walker
    /// will detect the truncation as a stale-path failure when it
    /// runs out of steps before reaching the destination.
    /// </summary>
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

            if (exit.Hint == RoomExitHint.Door)
                result.Add(new CommandStep($"open door {DirectionWord(dir)}"));

            result.Add(new MoveStep(dir, exit.Target));
            current = exit.Target;
        }

        return result;
    }

    private static string DirectionWord(Direction dir) => dir switch
    {
        Direction.N  => "north",
        Direction.S  => "south",
        Direction.E  => "east",
        Direction.W  => "west",
        Direction.NE => "northeast",
        Direction.NW => "northwest",
        Direction.SE => "southeast",
        Direction.SW => "southwest",
        Direction.U  => "up",
        Direction.D  => "down",
        _ => throw new ArgumentOutOfRangeException(nameof(dir), dir, "unknown direction"),
    };
}
