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
    private readonly object _cacheLock = new();

    /// <summary>
    /// Set of room keys to hide from the planar layout (BBS-tier
    /// room blacklist). Blacklisted neighbours still get an EDGE
    /// recorded so the renderer draws a dangling stub, but they
    /// are NOT placed in <see cref="RoomLayout.CoordToRoom"/> and
    /// NOT enqueued — they don't take up planar coords and the BFS
    /// doesn't traverse through them. The origin is exempt: when
    /// the player is currently inside a blacklisted room, the
    /// layout starts there and stays visible until they exit.
    /// </summary>
    /// <remarks>
    /// Set by <see cref="ConfigureBlacklist"/> at startup from
    /// <see cref="Services.RoomBlacklistStore"/>. The store fires
    /// <c>Changed</c> on Add/Remove and on BBS pin — consumers
    /// (NavigationViewModel) react by calling <see cref="InvalidateCache"/>
    /// + rebuilding the layout.
    /// </remarks>
    private Func<RoomKey, bool>? _isBlacklisted;

    public BfsMapper(RoomGraphManager graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
    }

    /// <summary>
    /// Bind the blacklist predicate. Pass <c>null</c> to disable.
    /// Cache is invalidated so the next layout build picks up the
    /// new filter.
    /// </summary>
    public void ConfigureBlacklist(Func<RoomKey, bool>? isBlacklisted)
    {
        _isBlacklisted = isBlacklisted;
        InvalidateCache();
    }

    /// <summary>
    /// Drop all cached layouts — called when the blacklist
    /// contents change so the next render sees the updated filter.
    /// </summary>
    public void InvalidateCache()
    {
        lock (_cacheLock) _layoutCache.Clear();
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
    /// Single-source shortest-path distances from <paramref name="source"/>
    /// to every reachable room (one BFS, all destinations). Returns a
    /// hop-count keyed by <see cref="RoomKey"/>; rooms not in the map
    /// are unreachable under the supplied filter. The blacklist hook
    /// is NOT consulted — render and pathing have always disagreed on
    /// blacklisted rooms (the walker can still traverse), and the search
    /// box wants distance to anywhere the player COULD walk.
    /// </summary>
    /// <remarks>
    /// Cheaper than calling <see cref="DistanceBetween"/> in a loop:
    /// O(rooms + edges) once vs O((rooms + edges) × matches). The
    /// Navigation search box uses this to score 50+ matches per
    /// keystroke without re-scanning the graph for each.
    /// </remarks>
    public IReadOnlyDictionary<RoomKey, int> ComputeDistancesFrom(
        RoomKey source, IRoomFilter? filter = null)
    {
        Dictionary<RoomKey, int> dist = new();
        if (_graph.GetRoom(source) is null) return dist;
        if (filter is not null && filter.IsAvoided(source)) return dist;

        Queue<RoomKey> queue = new();
        queue.Enqueue(source);
        dist[source] = 0;

        while (queue.Count > 0)
        {
            RoomKey here = queue.Dequeue();
            Room? room = _graph.GetRoom(here);
            if (room is null) continue;
            int here_d = dist[here];

            foreach ((Direction _, RoomExit exit) in room.Exits)
            {
                RoomKey next = exit.Target;
                if (dist.ContainsKey(next)) continue;
                if (filter is not null && filter.IsAvoided(next)) continue;
                if (_graph.GetRoom(next) is null) continue;
                dist[next] = here_d + 1;
                queue.Enqueue(next);
            }
        }
        return dist;
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
        lock (_cacheLock)
        {
            if (_layoutCache.TryGetValue(cacheKey, out RoomLayout? cached)) return cached;
        }

        if (_graph.GetRoom(origin) is null)
        {
            RoomLayout empty = new(
                Origin: origin,
                Positions: new Dictionary<RoomKey, (int X, int Y)>(),
                VerticalHints: new Dictionary<RoomKey, VerticalHint>(),
                OffGrid: Array.Empty<RoomKey>(),
                CoordToRoom: new Dictionary<(int X, int Y), RoomKey>(),
                EdgesFromCoord: new Dictionary<(int X, int Y), IReadOnlySet<Direction>>(),
                TrapEdgesFromCoord: new Dictionary<(int X, int Y), IReadOnlySet<Direction>>());
            lock (_cacheLock) _layoutCache[cacheKey] = empty;
            return empty;
        }

        var positions = new Dictionary<RoomKey, (int X, int Y)>
        {
            [origin] = (0, 0),
        };
        var coordToRoom = new Dictionary<(int X, int Y), RoomKey>
        {
            [(0, 0)] = origin,
        };
        var edgesFromCoord = new Dictionary<(int X, int Y), HashSet<Direction>>();
        var trapEdgesFromCoord = new Dictionary<(int X, int Y), HashSet<Direction>>();
        var vertical = new Dictionary<RoomKey, VerticalHint>();
        var depth = new Dictionary<RoomKey, int> { [origin] = 0 };

        var queue = new Queue<RoomKey>();
        queue.Enqueue(origin);

        AnnotateVertical(_graph.GetRoom(origin)!, vertical);

        // Planar-only BFS — modeled on MMUD-Explorer's MapActivateCell
        // (Case 8/9 GoTo DontActivate). U/D destinations are NEVER
        // visited or enqueued; the flat 2D map renders the current
        // floor only. When the player traverses U/D, the navigation
        // VM rebuilds the layout from the new origin.
        //
        // Collisions: when a non-Euclidean exit produces a coord
        // already occupied by another room, we skip the destination
        // AND the edge. Drawing the edge stub without a destination
        // produces a connector pointing into empty space, which was
        // the user-visible bug in the prior off-grid-lane approach.
        while (queue.Count > 0)
        {
            RoomKey here = queue.Dequeue();
            int hereDepth = depth[here];
            if (hereDepth >= maxRadius) continue;

            Room? room = _graph.GetRoom(here);
            if (room is null) continue;

            // Every room in the queue has a position (origin was
            // placed up-front; every enqueue below places before
            // enqueueing).
            (int X, int Y) hereXY = positions[here];

            foreach ((Direction dir, RoomExit exit) in room.Exits)
            {
                if (!IsPlanar(dir))
                {
                    // U/D source cell still gets a vertical hint glyph
                    // (already captured by AnnotateVertical) but no
                    // edge stub on the flat layer — the user's design.
                    continue;
                }

                RoomKey next = exit.Target;
                Room? nextRoom = _graph.GetRoom(next);
                if (nextRoom is null) continue;

                // Tentative target coord — skip ALL edge recording
                // when the coord clashes with an already-placed room
                // (matches MudProxy's silent-skip behaviour; avoids
                // dangling stubs into empty cells).
                if (!TryPlanarOffset(dir, out int dx, out int dy)) continue;
                (int X, int Y) target = (hereXY.X + dx, hereXY.Y + dy);

                // Already-placed destination via a different exit —
                // record the source-side stub regardless of whether
                // the placement lines up planarly. When it lines up
                // the renderer connects both ends in DrawAllExitLines;
                // when it doesn't (non-Euclidean reciprocal — BFS
                // reached the destination via a path whose planar
                // offset disagrees with this exit's direction), the
                // stub still shows the user "this room has an exit
                // here" instead of dropping the connection silently.
                // The crawler routes through the graph regardless, so
                // the visual stub matches the actual walkable topology.
                if (positions.TryGetValue(next, out (int X, int Y) _))
                {
                    AddEdge(edgesFromCoord, hereXY, dir);
                    if (exit.Hint == RoomExitHint.Trap)
                        AddEdge(trapEdgesFromCoord, hereXY, dir);
                    continue;
                }

                // First visit but target coord already taken by a
                // different room — same source-side-stub treatment as
                // the non-Euclidean case above. The destination either
                // gets placed at a non-colliding coord via a later BFS
                // path or stays unplaced; either way the source cell
                // shows that the exit exists.
                if (coordToRoom.ContainsKey(target))
                {
                    AddEdge(edgesFromCoord, hereXY, dir);
                    if (exit.Hint == RoomExitHint.Trap)
                        AddEdge(trapEdgesFromCoord, hereXY, dir);
                    continue;
                }

                // Blacklist (BBS-tier): record the edge so the renderer
                // draws a dangling stub pointing at the hidden room, but
                // do NOT place the room in CoordToRoom and do NOT
                // enqueue it. Blacklisted rooms don't claim planar
                // coords (declutters dense areas) and BFS doesn't
                // traverse through them. The origin is exempt — it
                // was placed before this loop started.
                if (_isBlacklisted?.Invoke(next) == true)
                {
                    AddEdge(edgesFromCoord, hereXY, dir);
                    if (exit.Hint == RoomExitHint.Trap)
                        AddEdge(trapEdgesFromCoord, hereXY, dir);
                    continue;
                }

                AnnotateVertical(nextRoom, vertical);
                AddEdge(edgesFromCoord, hereXY, dir);
                if (exit.Hint == RoomExitHint.Trap)
                    AddEdge(trapEdgesFromCoord, hereXY, dir);

                positions[next] = target;
                coordToRoom[target] = next;
                depth[next] = hereDepth + 1;
                queue.Enqueue(next);
            }
        }

        RoomLayout layout = new(
            Origin: origin,
            Positions: positions,
            VerticalHints: vertical,
            OffGrid: Array.Empty<RoomKey>(),
            CoordToRoom: coordToRoom,
            EdgesFromCoord: edgesFromCoord.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlySet<Direction>)kvp.Value),
            TrapEdgesFromCoord: trapEdgesFromCoord.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlySet<Direction>)kvp.Value));
        lock (_cacheLock)
        {
            // Cache the layout; if a concurrent prewarm landed it
            // first, prefer the existing entry so consumers continue
            // to share the same instance.
            if (_layoutCache.TryGetValue(cacheKey, out RoomLayout? existing))
                return existing;
            _layoutCache[cacheKey] = layout;
        }
        return layout;
    }

    private static void AddEdge(Dictionary<(int X, int Y), HashSet<Direction>> map,
        (int X, int Y) coord, Direction dir)
    {
        if (!map.TryGetValue(coord, out HashSet<Direction>? set))
        {
            set = new HashSet<Direction>();
            map[coord] = set;
        }
        set.Add(dir);
    }

    private static bool IsPlanar(Direction d) =>
        d != Direction.U && d != Direction.D;

    /// <summary>
    /// Subscribed by <see cref="Services.AppServices"/> to
    /// <see cref="RoomGraphManager.GraphReloaded"/> — flushes the
    /// layout cache since per-room references are invalidated.
    /// </summary>
    public void OnGraphReloaded()
    {
        lock (_cacheLock) _layoutCache.Clear();
    }

    /// <summary>
    /// Eagerly build (and cache) the layout from the first room in
    /// the active graph on a thread-pool task. Called by AppServices
    /// after <see cref="OnGraphReloaded"/> so the Navigation window's
    /// first render doesn't pay the BFS cost on the UI thread —
    /// real realms have ~2000 rooms and the BFS allocates a few
    /// hundred KB worth of dictionaries.
    /// </summary>
    public void PrewarmAsync()
    {
        Room? first = null;
        foreach (Room room in _graph.Rooms) { first = room; break; }
        if (first is null) return;
        RoomKey origin = first.Key;

        // Build off the UI thread; the cache itself is plain
        // Dictionary so all reads serialize through BuildLayout's
        // first-call path — concurrent reads while warming would
        // see the cached entry as soon as the warm task writes it.
        // We don't expose partial results, so a fresh BuildLayout
        // call on the UI thread before the prewarm finishes simply
        // computes the same layout (slight waste, no correctness
        // risk) and the prewarm's later cache.Add becomes a no-op.
        System.Threading.Tasks.Task.Run(() =>
        {
            try { BuildLayout(origin); } catch { /* best-effort */ }
        });
    }

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
