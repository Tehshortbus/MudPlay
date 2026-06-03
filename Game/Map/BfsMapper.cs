using System.Collections.Generic;
using System.Linq;

namespace FujinTerm.Game.Map;

/// <summary>
/// BFS over the active room graph. Two roles:
/// <list type="bullet">
///   <item><b>Pathfinding</b>: shortest-path step list between two
///         rooms — consumed by <c>AutoWalkManager</c> (walk-to),
///         <c>LoopManager</c> (gap-fill between user-clicked rooms),
///         and <c>AutoLairScheduler</c> (wait-room selection).</item>
///   <item><b>Layout</b>: planar (X, Y) assignment from an origin
///         room — consumed by <c>MapControl</c> (PR 7.11) to draw the
///         map. U/D exits are represented as
///         <see cref="VerticalHint"/> flags on the room cell instead
///         of contributing to the 2D layout.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Inspired by MudProxy's <c>MapBfsMapper.cs</c> but written fresh —
/// MudProxy bundles live-discovery + per-tile palette caching + a
/// dialog-renderer compatibility layer that we don't need. We treat
/// the imported <c>Rooms.json</c> as the authoritative graph (no live
/// extension in Phase 7) so the mapper stays focused on its two jobs.
/// </para>
/// <para>
/// Layout cache: <see cref="BuildLayout"/> results are memoized by
/// origin. <see cref="OnGraphReloaded"/> drops the cache; AppServices
/// subscribes that handler to
/// <see cref="RoomGraphManager.GraphReloaded"/>.
/// </para>
/// </remarks>
public sealed class BfsMapper
{
    private readonly RoomGraphManager _graph;
    private readonly Dictionary<(RoomKey Origin, int Radius), RoomLayout> _layoutCache = new();

    public BfsMapper(RoomGraphManager graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
    }

    /// <summary>
    /// Shortest-path step list from <paramref name="source"/> to
    /// <paramref name="destination"/>. Returns <c>null</c> when either
    /// endpoint isn't in the active graph, when they're the same room
    /// (empty path doesn't need to be walked), or when no path exists.
    /// Returns an empty list only for the source==destination case if
    /// the caller passed <paramref name="returnEmptyWhenAtDestination"/>
    /// = true.
    /// </summary>
    /// <param name="filter">Optional avoided-rooms filter; null = no filtering. PR 7.6 supplies the profile-backed implementation.</param>
    /// <param name="returnEmptyWhenAtDestination">When true, source==destination returns an empty list instead of null.</param>
    public IReadOnlyList<Direction>? FindPath(
        RoomKey source,
        RoomKey destination,
        IRoomFilter? filter = null,
        bool returnEmptyWhenAtDestination = false)
    {
        if (_graph.GetRoom(source) is null) return null;
        if (_graph.GetRoom(destination) is null) return null;

        if (source.Equals(destination))
            return returnEmptyWhenAtDestination ? Array.Empty<Direction>() : null;

        // Per-node parent + direction-from-parent, replayed on hit.
        var parent = new Dictionary<RoomKey, (RoomKey ParentKey, Direction Step)>();
        var queue = new Queue<RoomKey>();
        queue.Enqueue(source);
        parent[source] = (source, default);                    // sentinel

        while (queue.Count > 0)
        {
            RoomKey here = queue.Dequeue();
            Room? room = _graph.GetRoom(here);
            if (room is null) continue;                        // graph mutation between enqueue / dequeue

            foreach ((Direction dir, RoomExit exit) in room.Exits)
            {
                RoomKey next = exit.Target;
                if (parent.ContainsKey(next)) continue;

                // Avoid filter applies to intermediates AND to the
                // destination itself — walking *into* an avoided room
                // is the thing the user wants to forbid.
                if (filter is not null && filter.IsAvoided(next)) continue;

                // Destination room must still exist in the graph.
                if (_graph.GetRoom(next) is null) continue;

                parent[next] = (here, dir);

                if (next.Equals(destination))
                    return ReconstructPath(parent, source, destination);

                queue.Enqueue(next);
            }
        }

        return null;
    }

    /// <summary>
    /// Hop count from source to destination, or <c>null</c> when no
    /// path exists. Equivalent to <c>FindPath(...)?.Count</c> but
    /// cheaper for the right-rail GOTO list's "X steps" badges since
    /// we don't allocate the path array.
    /// </summary>
    public int? DistanceBetween(RoomKey source, RoomKey destination, IRoomFilter? filter = null)
    {
        IReadOnlyList<Direction>? path = FindPath(source, destination, filter,
            returnEmptyWhenAtDestination: true);
        return path?.Count;
    }

    /// <summary>
    /// BFS-planar layout from <paramref name="origin"/>. Caches the
    /// result; <see cref="OnGraphReloaded"/> evicts. The origin sits
    /// at (0, 0). Rooms whose grid position collides with an
    /// already-placed room go into <see cref="RoomLayout.OffGrid"/>.
    /// </summary>
    /// <param name="maxRadius">
    /// Cap on hop distance from origin. <see cref="int.MaxValue"/>
    /// means "until queue drains". Map UIs typically pass a small
    /// number (e.g. 25) to bound layout work on huge realms.
    /// </param>
    public RoomLayout BuildLayout(RoomKey origin, int maxRadius = int.MaxValue)
    {
        (RoomKey, int) cacheKey = (origin, maxRadius);
        if (_layoutCache.TryGetValue(cacheKey, out RoomLayout? cached)) return cached;

        if (_graph.GetRoom(origin) is null)
        {
            RoomLayout empty = new(
                Origin: origin,
                Positions: new Dictionary<RoomKey, (int X, int Y)>(),
                VerticalHints: new Dictionary<RoomKey, VerticalHint>(),
                OffGrid: Array.Empty<RoomKey>());
            _layoutCache[cacheKey] = empty;
            return empty;
        }

        var positions = new Dictionary<RoomKey, (int X, int Y)>
        {
            [origin] = (0, 0),
        };
        var vertical = new Dictionary<RoomKey, VerticalHint>();
        var offGrid = new List<RoomKey>();
        var depth = new Dictionary<RoomKey, int> { [origin] = 0 };
        var coordTaken = new HashSet<(int X, int Y)> { (0, 0) };

        var queue = new Queue<RoomKey>();
        queue.Enqueue(origin);

        AnnotateVertical(_graph.GetRoom(origin)!, vertical);

        while (queue.Count > 0)
        {
            RoomKey here = queue.Dequeue();
            int hereDepth = depth[here];
            if (hereDepth >= maxRadius) continue;

            Room? room = _graph.GetRoom(here);
            if (room is null) continue;

            // Current node may itself be off-grid (a Cellar reached via
            // D, or a collision-routed room). When that's the case, all
            // descendants discovered through it are off-grid too —
            // there's no planar coord to extend from.
            bool herePlanar = positions.TryGetValue(here, out (int X, int Y) hereXY);

            foreach ((Direction dir, RoomExit exit) in room.Exits)
            {
                RoomKey next = exit.Target;
                if (positions.ContainsKey(next) || offGrid.Contains(next)) continue;
                Room? nextRoom = _graph.GetRoom(next);
                if (nextRoom is null) continue;

                AnnotateVertical(nextRoom, vertical);

                if (!herePlanar || !TryPlanarOffset(dir, out int dx, out int dy))
                {
                    // Either we're stepping off the plane (U/D) or we
                    // started off-plane — either way, the descendant
                    // joins the off-grid lane.
                    offGrid.Add(next);
                    depth[next] = hereDepth + 1;
                    queue.Enqueue(next);
                    continue;
                }

                (int X, int Y) target = (hereXY.X + dx, hereXY.Y + dy);
                if (!coordTaken.Add(target))
                {
                    // Conflict — the same coord was already assigned
                    // to a different room on a shorter / earlier path.
                    // Park this room in the off-grid lane.
                    offGrid.Add(next);
                    depth[next] = hereDepth + 1;
                    queue.Enqueue(next);
                    continue;
                }

                positions[next] = target;
                depth[next] = hereDepth + 1;
                queue.Enqueue(next);
            }
        }

        RoomLayout layout = new(origin, positions, vertical, offGrid);
        _layoutCache[cacheKey] = layout;
        return layout;
    }

    /// <summary>
    /// Subscribed by <see cref="Services.AppServices"/> to
    /// <see cref="RoomGraphManager.GraphReloaded"/> — flushes the
    /// layout cache since per-room references are invalidated.
    /// </summary>
    public void OnGraphReloaded() => _layoutCache.Clear();

    // ----- helpers ---------------------------------------------------

    private static IReadOnlyList<Direction> ReconstructPath(
        Dictionary<RoomKey, (RoomKey ParentKey, Direction Step)> parent,
        RoomKey source,
        RoomKey destination)
    {
        var stack = new Stack<Direction>();
        RoomKey here = destination;
        while (!here.Equals(source))
        {
            (RoomKey p, Direction d) = parent[here];
            stack.Push(d);
            here = p;
        }
        return stack.ToArray();
    }

    private static bool TryPlanarOffset(Direction dir, out int dx, out int dy)
    {
        switch (dir)
        {
            case Direction.N:  dx =  0; dy = -1; return true;
            case Direction.S:  dx =  0; dy =  1; return true;
            case Direction.E:  dx =  1; dy =  0; return true;
            case Direction.W:  dx = -1; dy =  0; return true;
            case Direction.NE: dx =  1; dy = -1; return true;
            case Direction.NW: dx = -1; dy = -1; return true;
            case Direction.SE: dx =  1; dy =  1; return true;
            case Direction.SW: dx = -1; dy =  1; return true;
            case Direction.U:
            case Direction.D:
            default:
                dx = dy = 0;
                return false;
        }
    }

    private static void AnnotateVertical(Room room, Dictionary<RoomKey, VerticalHint> vertical)
    {
        VerticalHint hint = VerticalHint.None;
        if (room.Exits.ContainsKey(Direction.U)) hint |= VerticalHint.Up;
        if (room.Exits.ContainsKey(Direction.D)) hint |= VerticalHint.Down;
        if (hint != VerticalHint.None) vertical[room.Key] = hint;
    }
}
