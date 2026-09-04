using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.ViewModels.Navigation;

// One row of the route step list. A move/detour row carries the room the command
// is executed FROM plus the command itself ("13/497 Rugged Shoreline" / "s",
// "pull lever", "open door east"…). An acquire row (IsAcquire) is the buy/ask/hunt
// the run does to get through a gate — its literal walk-to-shop sub-steps are
// resolved reactively at walk time, so it's shown as one named step at the gate it
// unlocks rather than fabricated hop-by-hop.
// Room is the (map, room) the command is issued FROM — the same room LocationLabel
// renders into the display string, exposed as a key so callers can look up per-room
// detail (e.g. a room's lair monsters for the Current-route Details view). Defaulted
// so existing constructions that only care about the display line are unaffected.
public sealed record RouteStepRow(
    int Number, string Location, string Command,
    bool IsAcquire = false, RoomKey Room = default, bool IsArrival = false)
{
    // "1> 13/497 Rugged Shoreline < s" for a move/detour. An acquire row is marked
    // with a ◆ and names the room the obtain happens at, so you can see exactly
    // which step in the plan detours to fetch an item:
    // "3> ◆ 13/498 Sea Cavern — obtain a raft (buy at General Store)". An arrival
    // row (the destination the route ends in, no command issued from it) is shown
    // with a → and no command.
    public string Line =>
        IsArrival ? $"{Number}> → {Location} (arrive)"
        : IsAcquire ? $"{Number}> ◆ {Location} — {Command}"
        : $"{Number}> {Location} < {Command}";
}

// One gated crossing on a route: the room the walker stands in, the direction it
// crosses, and what that crossing needs. Positioned (not deduped) so the step list
// can drop the acquire row at exactly the hop that requires it.
public sealed record RouteGateStop(RoomKey Room, Direction Dir, RouteRequirement Requirement);

// Turns an expanded WalkStep sequence into the numbered "N> map/room name < command"
// rows shown by the route picker's Show-steps panel — the full start-to-finish
// command sequence, detours included. Pure: the room name + item/source lookups are
// injected, so it's unit-tested without a live graph. The per-step room is
// reconstructed by walking the sequence (a MoveStep advances to its ExpectedTarget;
// a CommandStep — lever/door/winch — stays put), which is exactly the room the
// walker is standing in when it sends that command.
public static class RouteStepList
{
    public static IReadOnlyList<RouteStepRow> Build(
        RoomKey source,
        IReadOnlyList<WalkStep> steps,
        IReadOnlyList<RouteGateStop> gatedStops,
        Func<RoomKey, string?> roomName,
        Func<int, string?> itemName,
        Func<RouteRequirement, string?> obtainSource)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(gatedStops);
        ArgumentNullException.ThrowIfNull(roomName);
        ArgumentNullException.ThrowIfNull(itemName);
        ArgumentNullException.ThrowIfNull(obtainSource);

        var rows = new List<RouteStepRow>(steps.Count + gatedStops.Count);
        // Each gate is announced once, the first time its hop is reached — a route
        // that re-crosses the same gate doesn't re-buy.
        var announced = new HashSet<RouteRequirement>();
        int n = 0;
        RoomKey current = source;

        foreach (WalkStep step in steps)
        {
            // Before a gated crossing, drop the acquire row(s) for what it needs.
            if (step is MoveStep move)
            {
                foreach (RouteGateStop stop in gatedStops)
                {
                    if (stop.Room.Equals(current) && stop.Dir == move.Direction
                        && announced.Add(stop.Requirement))
                    {
                        // The acquire step is marked at the room the run detours FROM
                        // to fetch the item — the gate room the walker stands in when
                        // it needs it (== current, the room before this crossing).
                        string items = ItemsLabel(stop.Requirement, itemName);
                        string command = obtainSource(stop.Requirement) is { Length: > 0 } src
                            ? $"obtain {items} ({src})"
                            : $"obtain {items} first";
                        rows.Add(new RouteStepRow(
                            ++n, LocationLabel(current, roomName), command, IsAcquire: true, Room: current));
                    }
                }
            }

            rows.Add(new RouteStepRow(++n, LocationLabel(current, roomName), StepCommand(step), Room: current));

            if (step is MoveStep m) current = m.ExpectedTarget;
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

    // "a raft" / "the iron key or a skeleton key" — the item(s) that satisfy a gate.
    private static string ItemsLabel(RouteRequirement req, Func<int, string?> itemName)
        => string.Join(" or ", req.ItemIds.Select(id => itemName(id) ?? $"item #{id}"));
}
