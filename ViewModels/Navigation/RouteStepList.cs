using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.ViewModels.Navigation;

// One row of the route step list. A move/detour row carries the room the command
// is executed FROM plus the command itself ("13/497 Rugged Shoreline" / "s",
// "pull lever", "open door east"…).
// Room is the (map, room) the command is issued FROM — the same room LocationLabel
// renders into the display string, exposed as a key so callers can look up per-room
// detail (e.g. a room's lair monsters for the Current-route Details view). Defaulted
// so existing constructions that only care about the display line are unaffected.
public sealed record RouteStepRow(
    int Number, string Location, string Command,
    RoomKey Room = default, bool IsArrival = false)
{
    // "1> 13/497 Rugged Shoreline < s" for a move/detour. An arrival row (the
    // destination the route ends in, no command issued from it) is shown with a →
    // and no command.
    public string Line =>
        IsArrival ? $"{Number}> → {Location} (arrive)"
        : $"{Number}> {Location} < {Command}";
}

// Turns an expanded WalkStep sequence into the numbered "N> map/room name < command"
// rows shown by the route picker's Show-steps panel — the full start-to-finish
// command sequence, detours included. Pure: the room name lookup is injected. The per-step room is
// reconstructed by walking the sequence (a MoveStep advances to its ExpectedTarget;
// a CommandStep — lever/door/winch — stays put), which is exactly the room the
// walker is standing in when it sends that command.
public static class RouteStepList
{
    public static IReadOnlyList<RouteStepRow> Build(
        RoomKey source,
        IReadOnlyList<WalkStep> steps,
        Func<RoomKey, string?> roomName)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(roomName);

        var rows = new List<RouteStepRow>(steps.Count);
        int n = 0;
        RoomKey current = source;

        foreach (WalkStep step in steps)
        {
            rows.Add(new RouteStepRow(++n, LocationLabel(current, roomName), StepCommand(step), Room: current));

            // A move advances to its target; a sys-goto jump advances to its landing
            // room (no graph edge, so it can't be inferred from an exit).
            if (step is MoveStep m) current = m.ExpectedTarget;
            else if (step is SysGotoStep g) current = g.LandingRoom;
        }

        return rows;
    }

    // The hop directions implied by a RoomKey polyline — for each consecutive pair,
    // the exit off the first room whose target is the next room. Stops at the first
    // pair the graph can't connect (a stale path); RemoteActionPathExpander truncates
    // likewise. Shared by the route picker and the Current-route Details view, both of
    // which turn a room-key route into the expanded step sequence.
    public static IReadOnlyList<Direction> DirectionsAlong(
        RoomGraphManager graph, IReadOnlyList<RoomKey> path)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(path);
        var dirs = new List<Direction>(Math.Max(0, path.Count - 1));
        for (int i = 0; i + 1 < path.Count; i++)
        {
            Room? room = graph.GetRoom(path[i]);
            if (room is null) break;
            Direction? hop = null;
            foreach ((Direction d, RoomExit exit) in room.Exits)
                if (exit.Target.Equals(path[i + 1])) { hop = d; break; }
            if (hop is null) break;
            dirs.Add(hop.Value);
        }
        return dirs;
    }

    // The exact command a step sends: a cardinal's wire token ("s", "ne"), the
    // pinned command for a special exit ("borrow skiff"), or a lever/door/winch
    // command verbatim. Matches what the walker actually puts on the wire.
    private static string StepCommand(WalkStep step) => step switch
    {
        MoveStep m => m.CommandLabel ?? m.Direction.ToToken(),
        _ => step.Display,
    };

    // "13/497 Rugged Shoreline" — map/room plus the graph's display name when known.
    private static string LocationLabel(RoomKey key, Func<RoomKey, string?> roomName)
    {
        string? name = roomName(key);
        return string.IsNullOrWhiteSpace(name)
            ? $"{key.Map}/{key.Room}"
            : $"{key.Map}/{key.Room} {name}";
    }
}
