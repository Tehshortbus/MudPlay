using System.Collections.Generic;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// A sysop-goto routing shortcut: fire `sys goto <name>` to land at a curated
// location, then walk the land leg from there to the goal. The jump is instant
// from anywhere, so — unlike a boat, which has a leg to the dock — there is no
// leg BEFORE the jump; only the landing→destination leg counts toward "which
// route is shorter". That land-hop count is the cost proxy the walker weighs
// against a pure land route (see AutoWalkManager.ChooseSysGotoRoute).
public readonly record struct SysopGotoRoutePlan(
    SysopGotoLocation Location,
    RoomKey LandingRoom,
    IReadOnlyList<Direction> FromArrival)
{
    public int LandHops => FromArrival.Count;
}

// The sys-goto analogue of BoatRoutePlanner. A sys goto is an origin-independent
// teleport, so it can't be a graph edge on every room (each room has exactly one
// Direction.Teleport slot) — this side planner bridges the gap by BFS-routing each
// usable location's landing room to the goal and keeping the location whose land
// leg is shortest. Locations come from the live SysopGoto table (empty when the
// power is off, so the planner then finds nothing), and the level gate is honoured
// at planning time: unlike a manual fire (which trusts the user), the router
// EXCLUDES a level-gated location when the character's level is unknown or too low
// — auto-routing must never silently take a shortcut that could be lethal.
public sealed class SysopGotoRoutePlanner
{
    private readonly RoomGraphManager _graph;
    private readonly BfsMapper _bfs;
    private readonly Func<IReadOnlyList<SysopGotoLocation>> _locations;
    private readonly Func<int?> _knownLevel;
    private readonly LogService? _log;

    public SysopGotoRoutePlanner(
        RoomGraphManager graph,
        BfsMapper bfs,
        Func<IReadOnlyList<SysopGotoLocation>> locations,
        Func<int?> knownLevel,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(knownLevel);
        _graph = graph;
        _bfs = bfs;
        _locations = locations;
        _knownLevel = knownLevel;
        _log = log;
    }

    // Best sys-goto shortcut from source to destination, or null when none helps.
    // The caller compares LandHops against the pure land route and takes the jump
    // only when it's shorter (or the sole crossing). filter gates the land leg
    // (avoided rooms, level / toll / class / item / hazard gates) exactly as a
    // plain route — the goto ROOM itself is a safe room needing no weighting, but
    // the landing→destination leg does, so it goes through the normal filter. The
    // other three are the walk's own route rules (BfsMapper.FindPath), so the leg is
    // planned as the land route it is weighed against.
    public SysopGotoRoutePlan? TryPlan(
        RoomKey source, RoomKey destination, IRoomFilter? filter,
        bool refuseTeleports = false, bool avoidTraps = false, bool ignoreAvoids = false)
    {
        if (_graph.GetRoom(destination) is null) return null;

        SysopGotoRoutePlan? best = null;
        int bestHops = int.MaxValue;
        int? level = _knownLevel();

        foreach (SysopGotoLocation loc in _locations())
        {
            // Router level gate: an ungated row (MinLevel <= 0) always qualifies; a
            // gated row needs a KNOWN level at or above the floor. Unknown level
            // excludes it — the router won't gamble a shortcut it can't judge safe.
            if (loc.MinLevel > 0 && (level is not int lvl || lvl < loc.MinLevel)) continue;

            var landing = new RoomKey(loc.Map, loc.Room);
            if (_graph.GetRoom(landing) is null) continue;   // landing not in the active graph
            if (landing.Equals(source)) continue;            // already here — the jump saves nothing

            IReadOnlyList<Direction>? leg = LegTo(landing, destination, filter, refuseTeleports, avoidTraps, ignoreAvoids);
            if (leg is null) continue;

            if (leg.Count < bestHops)
            {
                bestHops = leg.Count;
                best = new SysopGotoRoutePlan(loc, landing, leg);
            }
        }

        if (best is { } plan)
            _log?.Log(LogSeverity.Info, "SysGotoRoute",
                $"Sys-goto shortcut {source}→{destination} via '{plan.Location.Name}' "
                + $"(land {plan.LandingRoom}): {plan.FromArrival.Count} hop(s) from landing.");
        return best;
    }

    // A land leg between two rooms; empty when they're the same room (FindPath
    // returns null for source==dest, but a zero-hop leg is legitimate when the
    // landing room is itself the goal).
    private IReadOnlyList<Direction>? LegTo(
        RoomKey from, RoomKey to, IRoomFilter? filter, bool refuseTeleports, bool avoidTraps, bool ignoreAvoids)
    {
        if (from.Equals(to)) return Array.Empty<Direction>();
        return _bfs.FindPath(from, to, filter,
            refuseTeleports: refuseTeleports, avoidTraps: avoidTraps, ignoreAvoids: ignoreAvoids);
    }
}
