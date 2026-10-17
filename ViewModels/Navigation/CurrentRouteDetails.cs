using System.Linq;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.ViewModels.Navigation;

// Builds the "Details…" step list for the route the nav engine is CURRENTLY
// executing (or previewing), and for a route-picker choice. Input is the route's
// room-key polyline (source-first) — the very sequence the map draws as the route
// line, so the same builder serves a point-to-point walk, a loop circuit, and an
// Auto-Lair approach. The polyline is turned into the expanded WalkStep sequence
// the walker runs (DirectionsAlong → RemoteActionPathExpander.Expand → RouteStepList.Build); each
// resulting row is then annotated with its room's monsters and a warning (a
// room-entry hazard and/or an item-gated exit).
//
// Pure aside from the injected lookups (per-room monster links, the
// per-room hazard, the item-link factory).
public static class CurrentRouteDetails
{
    public static IReadOnlyList<RouteDetailRow> Build(
        RoomGraphManager graph,
        BfsMapper? bfs,
        IRoomFilter? filter,
        IReadOnlyList<RoomKey> route,
        Func<RoomKey, IReadOnlyList<RoomDetailLink>> roomMonsterLinks,
        Action<RoomKey> onRoomClick,
        Func<RoomKey, RouteStepWarning?> roomHazard,
        Func<int, RoomDetailLink> itemLink,
        int maxHp = 0,
        Game.TrapDisarmOdds? disarmOdds = null,
        // The route's walk when the caller already has it expanded (a route-picker
        // choice whose lever detour crosses gates the live filter would refuse, so
        // expanding the polyline here would cut it short).
        IReadOnlyList<WalkStep>? walk = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(roomMonsterLinks);
        ArgumentNullException.ThrowIfNull(onRoomClick);
        ArgumentNullException.ThrowIfNull(roomHazard);
        ArgumentNullException.ThrowIfNull(itemLink);

        // A route needs at least one hop (two rooms) to have any steps.
        if (route is not { Count: > 1 }) return Array.Empty<RouteDetailRow>();

        RoomKey source = route[0];
        IReadOnlyList<WalkStep> steps;
        if (walk is not null)
            steps = walk;
        else
        {
            IReadOnlyList<Direction> dirs = RouteStepList.DirectionsAlong(graph, route);
            if (dirs.Count == 0) return Array.Empty<RouteDetailRow>();
            steps = RemoteActionPathExpander.Expand(graph, source, dirs, bfs, filter);
        }

        // The walker's expanded steps already carry the door/winch/lever crossings.
        // The rows are 1:1 with `steps`, in order — which lets us pair each row with
        // the exit its step crosses to spot an item gate.
        IReadOnlyList<RouteStepRow> rows = RouteStepList.Build(
            source, steps, key => graph.GetRoom(key)?.DisplayName);

        // Per-step required item ids for an item-gated exit (rope & grapple, a raft,
        // a ticket…). Walk the steps tracking the current room so each MoveStep's
        // exit can be resolved and inspected.
        var gateItemsByStep = new List<IReadOnlyList<int>>(steps.Count);
        // Per-step trap damage of the exit the step crosses: null = crosses no trap;
        // a value = the trap's damage (0 when the export carried no figure).
        var trapByStep = new List<int?>(steps.Count);
        RoomKey cur = source;
        foreach (WalkStep step in steps)
        {
            IReadOnlyList<int> ids = Array.Empty<int>();
            int? trap = null;
            if (step is MoveStep move
                && graph.GetRoom(cur) is { } room
                && room.Exits.TryGetValue(move.Direction, out RoomExit exit))
            {
                ids = ExitGateItems(exit);
                if (exit.Hint == RoomExitHint.Trap) trap = exit.TrapDamage;
            }
            gateItemsByStep.Add(ids);
            trapByStep.Add(trap);
            if (step is MoveStep m) cur = m.ExpectedTarget;
            else if (step is SysGotoStep g) cur = g.LandingRoom;
        }

        var details = new List<RouteDetailRow>(rows.Count + 1);
        for (int i = 0; i < rows.Count; i++)
        {
            RouteStepRow row = rows[i];
            RoomKey rk = row.Room;
            IReadOnlyList<int> gateIds = i < gateItemsByStep.Count ? gateItemsByStep[i] : Array.Empty<int>();
            RouteStepWarning? warning = MergeWarning(roomHazard(rk), gateIds, itemLink);
            string? trapText = TrapTextFor(i < trapByStep.Count ? trapByStep[i] : null, maxHp, disarmOdds);
            details.Add(new RouteDetailRow(
                row, roomMonsterLinks(rk), new RelayCommand(() => onRoomClick(rk)), warning, trapText));
        }

        // The step rows each name the room a command is issued FROM, so the room the
        // route ENDS in — the destination, no command from it — gets no row. Append
        // it as a final arrival row so the plan shows where it lands, with the same
        // per-room monster/hazard detail.
        RoomKey dest = route[route.Count - 1];
        int lastNumber = rows.Count > 0 ? rows[rows.Count - 1].Number : 0;
        var arrival = new RouteStepRow(
            lastNumber + 1, LocationLabel(dest, graph.GetRoom(dest)?.DisplayName),
            Command: string.Empty, Room: dest, IsArrival: true);
        details.Add(new RouteDetailRow(
            arrival, roomMonsterLinks(dest), new RelayCommand(() => onRoomClick(dest)),
            MergeWarning(roomHazard(dest), Array.Empty<int>(), itemLink)));
        return details;
    }

    // The trap note for a step that crosses a trapped exit, related to the player's HP:
    // "trap: 36 dmg (~11% of HP)". Falls back to a plain damage figure when max HP is
    // unknown, and to "trap (damage unknown)" for a trapped exit whose export carried no
    // damage figure. With the Traps skill, our odds of disarming it follow
    // ("· disarm ~71%, failure (no dmg) 10%, failure (dmg) 19%"). Null when the step crosses no trap.
    private static string? TrapTextFor(int? damage, int maxHp, Game.TrapDisarmOdds? odds)
    {
        if (damage is not { } d) return null;
        string text = d <= 0
            ? "trap (damage unknown)"
            : maxHp > 0
                ? $"trap: {d} dmg (~{(int)Math.Round(100.0 * d / maxHp)}% of HP)"
                : $"trap: {d} dmg";
        return odds is { } o ? $"{text} · {o.Summary}" : text;
    }

    // "12/431 Tower" — the map/room key plus its name (name omitted when unknown),
    // matching the label RouteStepList renders for a step row's room.
    private static string LocationLabel(RoomKey key, string? roomName) =>
        string.IsNullOrWhiteSpace(roomName)
            ? $"{key.Map}/{key.Room}"
            : $"{key.Map}/{key.Room} {roomName}";

    // The item(s) an exit REQUIRES to cross — a carry-item (rope & grapple, a raft,
    // a phoenix feather item-use teleport), a ticket, or a multi-action's required
    // items. Door keys are deliberately excluded (a locked door can be picked or
    // bashed, so it isn't strictly item-gated). Empty for an ordinary exit.
    private static IReadOnlyList<int> ExitGateItems(in RoomExit exit)
    {
        switch (exit.Hint)
        {
            case RoomExitHint.Item:
            case RoomExitHint.Ticket:
            case RoomExitHint.Teleport:
                return exit.KeyItemId > 0 ? new[] { exit.KeyItemId } : Array.Empty<int>();
            case RoomExitHint.MultiActionHidden when exit.MultiAction is { } ma:
                int[] ids = ma.Actions
                    .Where(a => a.RequiredItemId > 0)
                    .Select(a => a.RequiredItemId)
                    .Distinct()
                    .ToArray();
                return ids;
            default:
                return Array.Empty<int>();
        }
    }

    // Fold a room hazard (spell + counter items) and an item-gated exit's required
    // items into one warning. Null when there's neither. Items are deduped by name
    // so a hazard counter that's also the gate item isn't listed twice.
    private static RouteStepWarning? MergeWarning(
        RouteStepWarning? hazard, IReadOnlyList<int> gateItemIds, Func<int, RoomDetailLink> itemLink)
    {
        if (hazard is null && gateItemIds.Count == 0) return null;

        var items = new List<RoomDetailLink>();
        var seen = new HashSet<string>();
        if (hazard is not null)
            foreach (RoomDetailLink l in hazard.Items)
                if (seen.Add(l.Text)) items.Add(l);
        foreach (int id in gateItemIds)
        {
            RoomDetailLink l = itemLink(id);
            if (seen.Add(l.Text)) items.Add(l);
        }
        return new RouteStepWarning(hazard?.Spell, items, hazard?.NoProtection ?? false);
    }
}
