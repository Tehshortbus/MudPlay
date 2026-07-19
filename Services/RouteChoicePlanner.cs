using System.Collections.Generic;
using System.Linq;
using FujinTerm.Game.Map;

namespace FujinTerm.Services;

// The kind of acquirable gate a direct route crosses. Drives the requirement
// phrasing in the route picker (a raft to carry, a ticket, a door key, a hazard
// counter item) and lets the planner's tests assert classification.
public enum RouteRequirementKind
{
    CarryItem,          // (Item: N) — a raft / held item that must be in hand.
    Ticket,             // (Ticket: N) — a consumed passage ticket.
    DoorKey,            // (Key: N) — the key that opens a locked door.
    HazardProtection,   // a cast-on-enter room hazard; carry any listed counter.
}

// One requirement the direct route imposes: the item id(s) that satisfy it. For
// CarryItem / Ticket / DoorKey the list is the single gate item; for
// HazardProtection it's the any-of counter set (hold at least one).
public sealed record RouteRequirement(RouteRequirementKind Kind, IReadOnlyList<int> ItemIds);

// A free-vs-direct route comparison for one destination: the free route's step
// count, the shorter direct route's step count, the requirements the direct
// route demands, and each route as a RoomKey sequence (source first, then every
// hop's target) so the picker can draw a map preview of whichever the user
// selects before committing. Only produced when the direct route is a genuine
// shortcut that needs at least one acquirable item — otherwise the caller just
// walks free.
public sealed record RouteChoice(
    int FreeStepCount,
    int GatedStepCount,
    IReadOnlyList<RouteRequirement> Requirements,
    IReadOnlyList<RoomKey> FreePath,
    IReadOnlyList<RoomKey> GatedPath);

// Compares the free-preferring route (acquirable gates active, so BFS detours
// around them) against the direct route (gates suspended, so BFS crosses them as
// if every gate item were carried). When the direct route saves steps AND needs
// something acquirable, it returns a RouteChoice the picker offers the user;
// otherwise null and the caller walks the plain free route.
//
// Only the four acquirable gates (item / ticket / key-door / hazard) are
// suspended for the direct pass — level / toll / class gates stay active, so the
// direct route never routes through a gate the crosser fundamentally can't pass.
public static class RouteChoicePlanner
{
    // Minimum rooms a shorter item / ticket / key shortcut must save before the
    // picker offers it. Below this, the acquisition (often purchase) detour isn't
    // worth the saving, so the free route wins outright. Hazard-only shortcuts
    // ignore this floor.
    private const int MinItemGateSavings = 2;

    public static RouteChoice? Evaluate(
        BfsMapper bfs,
        MovementFilter filter,
        RoomGraphManager graph,
        RoomKey source,
        RoomKey destination)
    {
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(graph);

        // Free route: gates active. No free route → nothing to weigh a shortcut
        // against, so leave the decision to the plain walk (which will report the
        // gated-only failure).
        IReadOnlyList<Direction>? free = bfs.FindPath(source, destination, filter);
        if (free is null || free.Count == 0) return null;

        // Direct route: acquirable gates suspended so BFS crosses them.
        IReadOnlyList<Direction>? gated;
        using (filter.SuspendAcquirableGates())
            gated = bfs.FindPath(source, destination, filter);
        if (gated is null || gated.Count == 0) return null;

        // Only offer the choice when the direct route is actually shorter — an
        // equal-or-longer "shortcut" is no bargain (and equal length is what we
        // get when the player already carries the gate items, so the two routes
        // coincide).
        if (gated.Count >= free.Count) return null;

        List<RouteRequirement> reqs = CollectRequirements(graph, filter, source, gated);
        if (reqs.Count == 0) return null;   // shorter but needs nothing acquirable → not a gated choice

        // A shortcut that shaves only a single room isn't worth a detour to
        // acquire — and usually to buy from a shop — the gate item: a player
        // won't sail to a boatman and spend gold on a skiff to save one step.
        // Suppress the offer for such a marginal item / ticket / key shortcut and
        // just walk the free route. Hazard-only shortcuts are exempt (walking a
        // warded room you can counter is a fair trade even for one room, and
        // nothing needs buying).
        int saved = free.Count - gated.Count;
        if (saved < MinItemGateSavings
            && reqs.Any(r => r.Kind != RouteRequirementKind.HazardProtection))
            return null;

        return new RouteChoice(
            free.Count, gated.Count, reqs,
            BuildKeyPath(graph, source, free),
            BuildKeyPath(graph, source, gated));
    }

    // Expand a planned direction list to the RoomKey sequence it visits (source
    // first, then each hop's target) for the picker's map preview. Stops at the
    // first hop the graph can't resolve — a defensive guard; a freshly-planned
    // BFS path is always resolvable end to end.
    private static IReadOnlyList<RoomKey> BuildKeyPath(
        RoomGraphManager graph, RoomKey source, IReadOnlyList<Direction> path)
    {
        var keys = new List<RoomKey>(path.Count + 1) { source };
        RoomKey cur = source;
        foreach (Direction dir in path)
        {
            Room? room = graph.GetRoom(cur);
            if (room is null || !room.Exits.TryGetValue(dir, out RoomExit exit)) break;
            cur = exit.Target;
            keys.Add(cur);
        }
        return keys;
    }

    // Walk the direct path hop by hop; every hop the live filter still blocks is
    // an acquirable gate (level/toll/class gates were never suspended, so the
    // direct route can't contain one). Classify and dedupe.
    private static List<RouteRequirement> CollectRequirements(
        RoomGraphManager graph,
        MovementFilter filter,
        RoomKey source,
        IReadOnlyList<Direction> gated)
    {
        var reqs = new List<RouteRequirement>();
        RoomKey cur = source;

        foreach (Direction dir in gated)
        {
            Room? room = graph.GetRoom(cur);
            if (room is null || !room.Exits.TryGetValue(dir, out RoomExit exit)) break;

            if (filter.IsExitBlocked(in exit) && Classify(filter, in exit) is { } req && !AlreadyHave(reqs, req))
                reqs.Add(req);

            cur = exit.Target;
        }

        return reqs;
    }

    private static RouteRequirement? Classify(MovementFilter filter, in RoomExit exit) => exit.Hint switch
    {
        RoomExitHint.Item when exit.KeyItemId > 0 =>
            new RouteRequirement(RouteRequirementKind.CarryItem, new[] { exit.KeyItemId }),
        RoomExitHint.Ticket when exit.KeyItemId > 0 =>
            new RouteRequirement(RouteRequirementKind.Ticket, new[] { exit.KeyItemId }),
        RoomExitHint.KeyLocked when exit.KeyItemId > 0 =>
            new RouteRequirement(RouteRequirementKind.DoorKey, new[] { exit.KeyItemId }),
        // A plain cardinal the filter still blocks is a hazard-room entry: resolve
        // the room's cast-on-enter spell to its any-of counter items.
        _ => HazardRequirement(filter, exit.Target),
    };

    private static RouteRequirement? HazardRequirement(MovementFilter filter, RoomKey target)
    {
        if (filter.Hazards is not { } hazards || filter.RoomEntrySpellProbe is not { } spellOf)
            return null;

        int spell = spellOf(target);
        if (hazards.HazardForSpell(spell) is not { } hazard) return null;

        IReadOnlyList<int> items = hazard.ProtectingItems;
        return items.Count > 0
            ? new RouteRequirement(RouteRequirementKind.HazardProtection, items)
            : null;
    }

    private static bool AlreadyHave(List<RouteRequirement> reqs, RouteRequirement candidate) =>
        reqs.Any(r => r.Kind == candidate.Kind && r.ItemIds.SequenceEqual(candidate.ItemIds));
}
