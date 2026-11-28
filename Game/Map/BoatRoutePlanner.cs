using System.Collections.Generic;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// A boat-inclusive route: walk the land leg to the dock, take one `secure
// passage` sailing, then walk the land leg from the arrival port to the goal.
// LandHops is the routable-cost proxy the walker weighs against a pure land
// route — the boat hop itself is a single party-split teleport, so only the two
// land legs count toward "which route is shorter". Either leg is empty when the
// crosser already stands on the dock, or the arrival port is itself the goal.
public readonly record struct BoatRoutePlan(
    BoatPassage Passage,
    IReadOnlyList<Direction> ToDock,
    IReadOnlyList<Direction> FromArrival,
    ExitBlockReason Block = ExitBlockReason.None)
{
    public int LandHops => ToDock.Count + FromArrival.Count;

    // A gated plan is one the crosser can't currently board (a member falls under
    // the level floor or can't cover the fare). It's only ever returned when the
    // sail is the SOLE crossing, which is more useful than hiding the only route
    // behind a bare "no path": the walker warns and lets the captain refuse, or,
    // when it is the crosser's own purse that can't pay the fare, fails naming it.
    public bool IsGated => Block != ExitBlockReason.None;
}

// Stitches a two-land-leg route around a single boat hop. A boat is a CMD
// teleport whose several parallel sailings (a dock offers up to a handful at
// once) can't live in the planar graph's single Direction.Teleport slot, so BFS
// can't route through a dock on its own — this planner bridges that gap by
// running BfsMapper twice per candidate sailing (source→dock, arrival→goal) and
// keeping the passable sailing with the fewest total land hops. It never
// hardcodes a realm's dock / port numbers: every candidate comes from the
// data-driven boat index RoomGraphManager built off the docks' CMD lines.
public sealed class BoatRoutePlanner
{
    private readonly RoomGraphManager _graph;
    private readonly BfsMapper _bfs;
    private readonly LogService? _log;

    public BoatRoutePlanner(RoomGraphManager graph, BfsMapper bfs, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(bfs);
        _graph = graph;
        _bfs = bfs;
        _log = log;
    }

    // Best boat route from source to destination, or null when no dock sailing
    // helps. The caller compares LandHops against the pure land route and picks
    // the boat only when it's shorter (or the only way across). filter gates each
    // land leg (avoided rooms, level / toll / class / item gates) AND — through
    // DescribeBoatBlock — the sailing's per-member level + fare.
    //
    // A boardable sailing always wins over a gated one. allowGated lets the caller
    // still accept a gated sailing (a member under-level / too-poor to board) when
    // it's the ONLY crossing, rather than the route vanishing into a bare "no path"
    // (what the walker does with one is on BoatRoutePlan.IsGated). With allowGated
    // false a gated sailing is skipped, so a land route (or a boardable boat) is
    // preferred.
    //
    // landHopsUnder: the caller takes a boat only when its land legs come to fewer
    // hops than this (the land route less the boat's overhead), so a sailing that
    // can't is not planned. Each sailing's legs are two searches of the whole map,
    // planned on the UI thread with the walk; weighing every sailing held a walk's
    // commit for ten seconds (report paradigm-20261021-135647). Hop floors to every
    // dock and from every port (RouteFloors), two searches in all, leave out a
    // sailing that can't beat the best found or the land route, or can't be reached.
    // The plan is the one weighing every sailing makes.
    //
    // refuseTeleports, avoidTraps and ignoreAvoids are the walk's own route rules
    // (BfsMapper.FindPath), so each leg is planned as the land route it is weighed
    // against.
    public BoatRoutePlan? TryPlan(
        RoomKey source, RoomKey destination, IRoomFilter? filter, bool allowGated = false, int? landHopsUnder = null,
        bool refuseTeleports = false, bool avoidTraps = false, bool ignoreAvoids = false)
    {
        if (_graph.GetRoom(source) is null || _graph.GetRoom(destination) is null)
            return null;

        BoatRoutePlan? best = null;
        (int Gated, int Hops) bestRank = (int.MaxValue, int.MaxValue);
        RouteFloors? floors = null;

        foreach (BoatPassage passage in _graph.AllBoatPassages)
        {
            // Per-member level / fare gate. A null filter (test / no-profile)
            // leaves the boat ungated — same "don't refuse on what we can't
            // evaluate" rule the land gates use.
            ExitBlockReason block = filter?.DescribeBoatBlock(passage) ?? ExitBlockReason.None;
            bool gated = block != ExitBlockReason.None;
            if (gated && !allowGated) continue;

            floors ??= FloorsFor(source, destination, filter, ignoreAvoids);
            if (floors.Bound(passage.DockRoom, passage.ArrivalRoom) is not { } floor) continue;
            if (floor >= landHopsUnder) continue;
            if (((gated ? 1 : 0), floor).CompareTo(bestRank) >= 0) continue;

            IReadOnlyList<Direction>? toDock = LegTo(
                source, passage.DockRoom, filter, refuseTeleports, avoidTraps, ignoreAvoids);
            if (toDock is null) continue;

            IReadOnlyList<Direction>? fromArrival = LegTo(
                passage.ArrivalRoom, destination, filter, refuseTeleports, avoidTraps, ignoreAvoids);
            if (fromArrival is null) continue;

            // Rank boardable-before-gated, then fewest total land hops — so a
            // boardable sailing always beats a gated one even if it's a touch
            // longer, and a gated sailing only surfaces when nothing boardable does.
            (int Gated, int Hops) rank = (gated ? 1 : 0, toDock.Count + fromArrival.Count);
            if (rank.Hops >= landHopsUnder) continue;
            if (rank.CompareTo(bestRank) < 0)
            {
                bestRank = rank;
                best = new BoatRoutePlan(passage, toDock, fromArrival, block);
            }
        }

        if (best is { } plan)
            _log?.Log(LogSeverity.Info, "BoatRoute",
                $"Boat route {source}→{destination} via '{plan.Passage.Keyword}' "
                + $"(dock {plan.Passage.DockRoom}, arrive {plan.Passage.ArrivalRoom}): "
                + $"{plan.ToDock.Count} hop(s) to dock + {plan.FromArrival.Count} from port"
                + (plan.IsGated ? $"; GATED ({plan.Block}) — sole crossing, will warn." : "."));
        return best;
    }

    private RouteFloors FloorsFor(RoomKey source, RoomKey destination, IRoomFilter? filter, bool ignoreAvoids)
    {
        HashSet<RoomKey> ends = new();
        foreach (BoatPassage passage in _graph.AllBoatPassages)
        {
            ends.Add(passage.DockRoom);
            ends.Add(passage.ArrivalRoom);
        }
        return RouteFloors.Via(_bfs, source, destination, ends, filter, ignoreAvoids);
    }

    // A land leg between two rooms; empty when they're the same room. FindPath
    // returns null for source==dest, but a boat route legitimately has a
    // zero-hop leg when the crosser already stands on the dock or the port is
    // itself the goal — so that case is an empty list, not a miss.
    private IReadOnlyList<Direction>? LegTo(
        RoomKey from, RoomKey to, IRoomFilter? filter, bool refuseTeleports, bool avoidTraps, bool ignoreAvoids)
    {
        if (from.Equals(to)) return Array.Empty<Direction>();
        return _bfs.FindPath(from, to, filter,
            refuseTeleports: refuseTeleports, avoidTraps: avoidTraps, ignoreAvoids: ignoreAvoids);
    }
}
