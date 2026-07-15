using System.Collections.Generic;
using System.Linq;

namespace FujinTerm.Game.Map;

// BFS over the active room graph. Two roles:
//   Pathfinding: shortest-path step list between two rooms — consumed by
//     AutoWalkManager (walk-to), LoopManager (gap-fill between
//     user-clicked rooms), and AutoLairScheduler (wait-room selection).
//   Layout: planar (X, Y) assignment from an origin room — consumed by
//     the map control to draw the map. U/D exits are represented as
//     VerticalHint flags on the room cell instead of contributing to the
//     2D layout.
//
// The imported Rooms.json is treated as the authoritative graph (no live
// discovery / extension), so the mapper stays focused on its two jobs.
//
// Layout cache: BuildLayout results are memoized by origin.
// OnGraphReloaded drops the cache; AppServices subscribes that handler
// to RoomGraphManager.GraphReloaded.
public sealed class BfsMapper
{
    private readonly RoomGraphManager _graph;
    private readonly Dictionary<(RoomKey Origin, int Radius), RoomLayout> _layoutCache = new();

    // LRU eviction order for _layoutCache, most-recently-used at the front.
    // A full-realm layout pins the whole room graph (~6590 cells across five
    // dictionaries, a couple of MB) — an unbounded cache would ratchet memory
    // on a player who tours the realm (auto-lair across zones, repeated gotos),
    // one retained layout per distinct origin visited. The map only ever renders
    // one origin at a time; the cache exists solely to skip rebuilds when the
    // origin oscillates over a small set of recently-visited rooms (a camp
    // loop). The cap comfortably covers a camp loop while bounding a grand tour;
    // past it the least-recently-used origin is dropped.
    private readonly LinkedList<(RoomKey Origin, int Radius)> _lruOrder = new();
    private const int MaxCachedLayouts = 32;

    private readonly object _cacheLock = new();

    // Vertical-plane index: each room → (component, floor), where floor is
    // net U/D displacement (U:+1, D:-1) within the room's connected
    // component computed over NON-text exits only. Text exits are excluded
    // so a teleport-like jump (a sewer manhole, the Crypt go-portal back to
    // the graveyard) never links two levels here. The layout BFS consults
    // this to spot a cross-plane text exit — endpoints sharing a component
    // but sitting on different floors — and stub it instead of dragging the
    // far floor's rooms onto the current plane. Rebuilt lazily; dropped by
    // OnGraphReloaded (blacklist changes don't affect vertical structure).
    private Dictionary<RoomKey, (int Component, int Floor)>? _planeIndex;

    // Memoized directed walk-reachability over NON-text exits, keyed by
    // (source, target). Refines the cross-plane test: a text exit whose
    // endpoints share a component but sit on different floors is only a
    // genuine cross-level portal when the target is otherwise unreachable
    // on foot. Filled lazily by WalkReachableOverNonText; dropped with the
    // plane index on reload (it depends on the same exit graph).
    private Dictionary<(RoomKey Source, RoomKey Target), bool>? _walkReachCache;

    // Set of room keys to hide from the planar layout (BBS-tier room
    // blacklist). Blacklisted neighbours still get an EDGE recorded so the
    // renderer draws a dangling stub, but they are NOT placed in
    // CoordToRoom and NOT enqueued — they don't take up planar coords and
    // the BFS doesn't traverse through them. The origin is exempt: when
    // the player is currently inside a blacklisted room, the layout starts
    // there and stays visible until they exit.
    //
    // Set by ConfigureBlacklist at startup from RoomBlacklistStore. The
    // store fires Changed on Add/Remove and on BBS pin — consumers
    // (NavigationViewModel) react by calling InvalidateCache + rebuilding
    // the layout.
    private Func<RoomKey, bool>? _isBlacklisted;
    private readonly Services.LogService? _log;

    public BfsMapper(RoomGraphManager graph) : this(graph, log: null) { }

    public BfsMapper(RoomGraphManager graph, Services.LogService? log)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
        _log = log;
    }

    // Bind the blacklist predicate. Pass null to disable. Cache is
    // invalidated so the next layout build picks up the new filter.
    public void ConfigureBlacklist(Func<RoomKey, bool>? isBlacklisted)
    {
        _isBlacklisted = isBlacklisted;
        InvalidateCache();
    }

    // Drop all cached layouts — called when the blacklist contents change
    // so the next render sees the updated filter.
    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _layoutCache.Clear();
            _lruOrder.Clear();
        }
    }

    // Record cacheKey as most-recently-used. Caller holds _cacheLock. O(n) over
    // the cache size, which is capped at MaxCachedLayouts — trivial next to a
    // layout build, and this only runs on a map draw (~once per room change).
    private void TouchLru((RoomKey Origin, int Radius) cacheKey)
    {
        _lruOrder.Remove(cacheKey);
        _lruOrder.AddFirst(cacheKey);
    }

    // Insert layout, mark it most-recently-used, and evict the LRU tail past the
    // cap. Caller holds _cacheLock.
    private void StoreLayout((RoomKey Origin, int Radius) cacheKey, RoomLayout layout)
    {
        _layoutCache[cacheKey] = layout;
        TouchLru(cacheKey);
        while (_layoutCache.Count > MaxCachedLayouts && _lruOrder.Last is { } tail)
        {
            _layoutCache.Remove(tail.Value);
            _lruOrder.RemoveLast();
        }
    }

    // Shortest-path step list from source to destination. Returns null
    // when either endpoint isn't in the active graph, when they're the
    // same room (empty path doesn't need to be walked), or when no path
    // exists. Returns an empty list only for the source==destination case
    // if the caller passed returnEmptyWhenAtDestination = true.
    //
    // filter: optional avoided-rooms filter; null = no filtering.
    // returnEmptyWhenAtDestination: when true, source==destination
    //   returns an empty list instead of null.
    // ignoreExitGates: when true, exit movement restrictions (Form-A
    //   level gates via IRoomFilter.IsExitBlocked) are NOT applied — only
    //   the avoid filter still cuts nodes. The walker uses this to
    //   re-probe a failed walk and tell "all routes are level-gated"
    //   apart from "graph-disconnected".
    public IReadOnlyList<Direction>? FindPath(
        RoomKey source,
        RoomKey destination,
        IRoomFilter? filter = null,
        bool returnEmptyWhenAtDestination = false,
        bool ignoreExitGates = false)
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

                // A random-destination cast-teleport exit lands the walker in an
                // unpredictable room — never plan a route through it (even when
                // exit gates are being ignored: the non-determinism, not a gate,
                // is what rules it out). The exit still lays out on the map (its
                // nominal target is a real adjacent room); it's only routing that
                // must avoid it.
                if (exit.CastTeleportRandom) continue;

                // Avoid filter applies to intermediates AND to the
                // destination itself — walking *into* an avoided room
                // is the thing the user wants to forbid.
                if (filter is not null && filter.IsAvoided(next)) continue;

                // Movement restriction on the exit itself (Form-A level
                // gate) — non-traversable when the player doesn't meet
                // it, unless the caller is probing with gates ignored.
                if (!ignoreExitGates && filter is not null && filter.IsExitBlocked(exit)) continue;

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

    // Single-source shortest-path distances from source to every
    // reachable room (one BFS, all destinations). Returns a hop-count
    // keyed by RoomKey; rooms not in the map are unreachable under the
    // supplied filter. The blacklist hook is NOT consulted — render and
    // pathing have always disagreed on blacklisted rooms (the walker can
    // still traverse), and the search box wants distance to anywhere the
    // player COULD walk.
    //
    // Cheaper than calling DistanceBetween in a loop: O(rooms + edges)
    // once vs O((rooms + edges) × matches). The Navigation search box uses
    // this to score 50+ matches per keystroke without re-scanning the
    // graph for each.
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
                if (exit.CastTeleportRandom) continue; // unpredictable landing — not routable
                if (filter is not null && filter.IsAvoided(next)) continue;
                if (filter is not null && filter.IsExitBlocked(exit)) continue;
                if (_graph.GetRoom(next) is null) continue;
                dist[next] = here_d + 1;
                queue.Enqueue(next);
            }
        }
        return dist;
    }

    // Hop count from source to destination, or null when no path exists.
    // Equivalent to FindPath(...)?.Count but cheaper for the right-rail
    // GOTO list's "X steps" badges since we don't allocate the path array.
    public int? DistanceBetween(RoomKey source, RoomKey destination, IRoomFilter? filter = null)
    {
        IReadOnlyList<Direction>? path = FindPath(source, destination, filter,
            returnEmptyWhenAtDestination: true);
        return path?.Count;
    }

    // True when the shortest route from source to destination crosses at
    // least one (Toll: N) exit under the supplied filter. Used at walk-start
    // to decide whether a party @wealth probe is worth firing: the probe only
    // matters when a toll is actually on the route the walker will take, not
    // on some off-path toll edge the BFS frontier happened to touch. The
    // caller (MovementFilter.WarmForRoute) suspends its own toll gate before
    // calling, so the returned path is the one the party WOULD walk if every
    // toll were affordable — level gates and avoided rooms still apply.
    public bool RouteUsesToll(RoomKey source, RoomKey destination, IRoomFilter? filter = null)
    {
        IReadOnlyList<Direction>? path = FindPath(source, destination, filter);
        if (path is null || path.Count == 0) return false;

        RoomKey cursor = source;
        foreach (Direction dir in path)
        {
            Room? room = _graph.GetRoom(cursor);
            if (room is null) return false;
            if (!room.Exits.TryGetValue(dir, out RoomExit exit)) return false;
            if (exit.Hint == RoomExitHint.Toll && exit.TollGold > 0) return true;
            cursor = exit.Target;
        }
        return false;
    }

    // BFS-planar layout from origin. Caches the result; OnGraphReloaded
    // evicts. The origin sits at (0, 0). Rooms whose grid position
    // collides with an already-placed room go into RoomLayout.OffGrid.
    //
    // maxRadius: cap on hop distance from origin. int.MaxValue means
    //   "until queue drains". Map UIs typically pass a small number (e.g.
    //   25) to bound layout work on huge realms.
    public RoomLayout BuildLayout(RoomKey origin, int maxRadius = int.MaxValue)
    {
        (RoomKey Origin, int Radius) cacheKey = (origin, maxRadius);
        lock (_cacheLock)
        {
            if (_layoutCache.TryGetValue(cacheKey, out RoomLayout? cached))
            {
                TouchLru(cacheKey);
                return cached;
            }
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
                TrapEdgesFromCoord: new Dictionary<(int X, int Y), IReadOnlySet<Direction>>(),
                SpellEdgesFromCoord: new Dictionary<(int X, int Y), IReadOnlySet<Direction>>())
            { LayoutRoot = origin };
            lock (_cacheLock) StoreLayout(cacheKey, empty);
            return empty;
        }

        // First attempt — BFS + refinement from the requested origin.
        RoomLayout primary = BuildLayoutCore(origin, maxRadius);

        // Score-and-retry pass. If the primary layout carries any
        // non-Euclidean stubs (cluster, typically caused by reaching a
        // maze region via a bent path), try a small handful of retry
        // origins and keep the BEST layout after translating back so the
        // original origin sits at (0,0). The user never sees the worse one.
        //
        // "Best" = most rooms placed, with stub count as the tiebreak.
        // Coverage comes first because a dropped room (collision the
        // placer couldn't resolve) vanishes from the map entirely —
        // strictly worse than a bent connector, which at least still
        // shows the room and the fact that an exit exists. Optimising on
        // stub count alone backfires: a layout that DROPS a whole cluster
        // scores *fewer* stubs than one that places the cluster with a
        // few bends, so the old metric actively preferred the sparser,
        // more-wrong layout. Every dropped room leaves at least one stub
        // on its source side, so "stubs > 0" reliably triggers the retry
        // whenever coverage is incomplete.
        //
        // Candidate seeds (see PickRetryOrigins): the highest-degree
        // PLACED rooms come first. A layout's coverage hinges on how
        // central its origin is — a peripheral origin reaches a sub-area's
        // single doorway at a bent angle, drops it, and never explores the
        // block behind it. (Real case: v1.11p map-1 sewers from origin
        // 1/602 place 567 of 571 rooms — the map-1→map-11 doorway collides
        // and the whole map-11 block vanishes behind one stub. Re-rooting
        // at almost any central sewer room recovers all 571.) Seeding from
        // the dropped rooms themselves is useless: a dropped sub-area is
        // reached through a one-way / vertical seam, so BFS from inside it
        // can't return to the requested origin and the candidate is
        // discarded. Placed-but-stubbed rooms are tried after the central
        // ones, for bend reduction once coverage is already complete.
        //
        // Bounded layouts (maxRadius < int.MaxValue) are typically
        // minimap-scoped and already region-isolated; skip the retry
        // there.
        int finalStubs;
        if (maxRadius == int.MaxValue)
        {
            int primaryStubs = CountStubs(primary);
            int primaryPlaced = primary.Positions.Count;
            _log?.Log(Services.LogSeverity.Debug, "BfsMapper",
                $"BuildLayout origin={origin} primaryStubs={primaryStubs} positions={primaryPlaced}.");
            if (primaryStubs > 0)
            {
                int triedCount = 0;
                foreach (RoomKey retryOrigin in PickRetryOrigins(primary, origin, RetryCandidateCount))
                {
                    if (retryOrigin.Equals(origin)) continue;
                    triedCount++;
                    RoomLayout secondary = BuildLayoutCore(retryOrigin, maxRadius);
                    if (!secondary.Positions.ContainsKey(origin))
                    {
                        _log?.Log(Services.LogSeverity.Debug, "BfsMapper",
                            $"  retry#{triedCount} from {retryOrigin}: origin {origin} unreachable; skip.");
                        continue;
                    }
                    int secondaryStubs = CountStubs(secondary);
                    int secondaryPlaced = secondary.Positions.Count;
                    bool replaces = RetryImproves(
                        secondaryPlaced, secondaryStubs, primaryPlaced, primaryStubs);
                    _log?.Log(Services.LogSeverity.Debug, "BfsMapper",
                        $"  retry#{triedCount} from {retryOrigin}: placed={secondaryPlaced} stubs={secondaryStubs}"
                        + (replaces ? " — replaces primary." : " — keep primary."));
                    if (replaces)
                    {
                        primary = TranslateLayoutToNewOrigin(secondary, origin);
                        primaryStubs = secondaryStubs;
                        primaryPlaced = secondaryPlaced;
                        // A zero-stub layout has no dropped rooms (every
                        // exit lands flat), so coverage is already maximal
                        // — nothing left to improve.
                        if (primaryStubs == 0) break;
                    }
                }
                _log?.Log(Services.LogSeverity.Debug, "BfsMapper",
                    $"BuildLayout origin={origin} final placed={primaryPlaced} stubs={primaryStubs} after {triedCount} retry attempt(s).");
            }
            finalStubs = primaryStubs;
        }
        else
        {
            // Bounded (minimap) layouts skip the retry pass; still record
            // the stub count so the seed reads honestly if ever surfaced.
            finalStubs = CountStubs(primary);
        }
        primary = primary with { StubCount = finalStubs };

        lock (_cacheLock)
        {
            if (_layoutCache.TryGetValue(cacheKey, out RoomLayout? existing))
            {
                TouchLru(cacheKey);
                return existing;
            }
            StoreLayout(cacheKey, primary);
        }
        return primary;
    }

    // Nearest graph-reachable room to seed (inclusive) that the blacklist
    // does not hide, by BFS hop distance. Returns seed unchanged when it
    // isn't blacklisted (or no blacklist is configured), and falls back to
    // seed when every reachable room is hidden.
    //
    // BuildLayout exempts its origin from the blacklist so the "you are
    // here" marker always has an anchor. That exemption is correct for the
    // live current room but wrong for a fallback anchor (the offline
    // last-known room): blacklisting the parked room should hide it at
    // once, not keep it visible just because it happens to be the layout
    // origin. The map routes its non-live origin through here so the
    // layout never anchors on — and so never exempts — a hidden room,
    // mirroring the movement path's re-root off a blacklisted origin.
    public RoomKey NearestVisibleRoom(RoomKey seed)
    {
        if (_isBlacklisted?.Invoke(seed) != true) return seed;
        if (_graph.GetRoom(seed) is null) return seed;

        Queue<RoomKey> queue = new();
        HashSet<RoomKey> seen = new() { seed };
        queue.Enqueue(seed);
        while (queue.Count > 0)
        {
            RoomKey here = queue.Dequeue();
            if (_graph.GetRoom(here) is not { } room) continue;
            foreach ((Direction _, RoomExit exit) in room.Exits)
            {
                RoomKey next = exit.Target;
                if (!seen.Add(next)) continue;
                if (_graph.GetRoom(next) is null) continue;
                if (_isBlacklisted?.Invoke(next) != true) return next;
                queue.Enqueue(next);
            }
        }
        return seed;
    }

    // How many retry origins to try, ordered by stub count descending.
    // Each one costs another full BFS + refinement pass, so a small cap
    // keeps worst-case build cost bounded. The improvement loop breaks
    // early when a zero-stub layout is found.
    private const int RetryCandidateCount = 8;

    // Maximum consecutive cells a single graphical crossing may tunnel
    // through (see the collision branch in BuildLayoutCore). A bridge over
    // a river is 1–3 cells wide; the cap is defensive against a
    // pathological structure that would otherwise hide a long run of
    // rooms. Matches the renderer's BridgeMaxCells gap tolerance so what
    // tunnels here can still draw a bridge connector.
    private const int TunnelMaxCells = 4;

    private RoomLayout BuildLayoutCore(RoomKey origin, int maxRadius)
    {
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
        var spellEdgesFromCoord = new Dictionary<(int X, int Y), HashSet<Direction>>();
        var vertical = new Dictionary<RoomKey, VerticalHint>();
        var depth = new Dictionary<RoomKey, int> { [origin] = 0 };

        // Tunnel-through bookkeeping (see the collision branch below).
        // `covered` rooms hold a phantom coord in `positions` for
        // child-offset math + BFS continuation but are kept out of
        // `coordToRoom` and stripped from the drawn output; `coveredRun`
        // caps how many consecutive cells a single crossing may hide.
        var covered = new HashSet<RoomKey>();
        var coveredRun = new Dictionary<RoomKey, int>();

        var queue = new Queue<RoomKey>();
        queue.Enqueue(origin);

        AnnotateVertical(_graph.GetRoom(origin)!, vertical);

        // Planar-only BFS. U/D destinations are NEVER visited or
        // enqueued; the flat 2D map renders the current floor only. When
        // the player traverses U/D, the navigation VM rebuilds the layout
        // from the new origin.
        //
        // Collisions: when a non-Euclidean exit produces a coord already
        // occupied by another room, the destination is normally dropped
        // (the edge becomes a render-time stub once edges are rebuilt
        // below). The one exception is a graphical crossing — a straight
        // path passing under a foreign structure on the same plane — which
        // tunnels through so the path resumes on the far side; see the
        // collision branch.
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

                // One-way cast pocket mouth (the Warped Asylum entrance): a
                // cast-on-walk exit whose target can't walk back. Expanding
                // through it pours the whole sink area (108 rooms) into the
                // HOUSING map's grid, drawn on top of it. Stop here — the source
                // room stays placed, so the edge-rebuild pass below still records
                // its spell-wall stub glyph pointing at the hidden pocket. A
                // walker standing INSIDE the pocket lays the area out fully: its
                // internal cast exits are reciprocal, so none are flagged, and
                // the mouth lives on the outside room it can never reach.
                if (exit.CastPocketEntrance) continue;

                // Cross-plane text portal: the importer files a text exit
                // (go portal, go manhole) under a cardinal slot, so it looks
                // planar — but its target lives on another vertical level
                // reached by real stairs. Placing it here drags that whole
                // foreign floor onto this plane (the Crypt trainers' go-portal
                // pulling the surface graveyard onto level 3). Record the
                // source-side stub so the exit stays visible, then skip
                // placement. Same-floor text exits (go path, follow trail)
                // fall through and lay out normally.
                if (exit.Hint == RoomExitHint.Text && IsCrossPlaneTextExit(here, next))
                {
                    AddEdge(edgesFromCoord, hereXY, dir);
                    continue;
                }

                if (!TryPlanarOffset(dir, out int dx, out int dy)) continue;
                (int X, int Y) tentative = (hereXY.X + dx, hereXY.Y + dy);

                // Already-placed destination via a different exit —
                // record the source-side stub regardless of whether
                // the placement lines up planarly. When it lines up
                // the renderer connects both ends in DrawAllExitLines;
                // when it doesn't (non-Euclidean reciprocal — BFS
                // reached the destination via a path whose planar
                // offset disagrees with this exit's direction), the
                // stub still shows the user "this room has an exit
                // here" instead of dropping the connection silently.
                if (positions.TryGetValue(next, out (int X, int Y) _))
                {
                    AddEdge(edgesFromCoord, hereXY, dir);
                    if (exit.Hint == RoomExitHint.Trap)
                        AddEdge(trapEdgesFromCoord, hereXY, dir);
                    continue;
                }

                // Smarter placement: instead of always taking the
                // parent-edge offset (tentative), look at every exit
                // on the candidate room. For each exit pointing to a
                // room already in `positions`, compute the coord the
                // candidate WOULD need to occupy for that reciprocal
                // to lie flat — i.e. `nb - exitOffset`. Score every
                // free option by how many of the candidate's reciprocal
                // exits would line up there, then pick the highest-
                // scoring free coord. The parent-edge offset stays in
                // the candidate pool, so a simple no-reciprocals case
                // collapses to the prior behaviour.
                //
                // Why this helps: maze areas like v1.11p Dragon's
                // Teeth Hills have tight reciprocal cycles. BFS from a
                // far origin reaches them via a path that bends the
                // initial parent-edge placement away from the cycle's
                // geometry; the next BFS visit to a neighbour would
                // have used parent-edge offset too and inherited the
                // bend. With this scan, the second visit picks the
                // coord that closes the cycle planarly instead.
                (int X, int Y)? chosen =
                    ChooseBestPlacement(nextRoom, tentative, positions, coordToRoom);

                if (chosen is null)
                {
                    // No free coord found (including tentative) —
                    // collision. Normally we drop the room and stop
                    // traversing it: that's the safety valve that keeps
                    // non-Euclidean folds from stacking rooms on one
                    // cell. But a *graphical crossing* — a straight path
                    // passing under a foreign structure on the same plane
                    // (a river under a bridge; there's no U/D layer, so
                    // the river cell wants the same coord as a bridge
                    // cell) — should TUNNEL THROUGH: hide the covered
                    // room but keep walking so the path resumes on the
                    // far side instead of the whole river east of the
                    // bridge vanishing. We give the covered room a phantom
                    // coord (its natural parent-edge offset) used only for
                    // child-offset math and enqueue it; it's kept out of
                    // coordToRoom and stripped from the drawn output.

                    // Blacklisted target: never tunnel it — only record the
                    // source-side stub, so neither the tunnel path nor a
                    // BFS continuation through it can resurrect a hidden room
                    // or drag the rooms behind it onto the map. (Mirrors the
                    // in-grid blacklist branch below.)
                    if (_isBlacklisted?.Invoke(next) == true)
                    {
                        AddEdge(edgesFromCoord, hereXY, dir);
                        if (exit.Hint == RoomExitHint.Trap)
                            AddEdge(trapEdgesFromCoord, hereXY, dir);
                        continue;
                    }

                    int run = (covered.Contains(here) ? coveredRun[here] : 0) + 1;
                    if (run <= TunnelMaxCells
                        && ShouldTunnelThrough(nextRoom, dir, tentative, positions, coordToRoom))
                    {
                        positions[next] = tentative;     // phantom (traversal only)
                        depth[next] = hereDepth + 1;
                        covered.Add(next);
                        coveredRun[next] = run;
                        queue.Enqueue(next);
                    }
                    continue;
                }
                (int X, int Y) target = chosen.Value;

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

        // Deferred resolution pass — a two-pass "delay contested rooms
        // until the frontier settles" trick. By this point the BFS has
        // committed a placement for every
        // reachable room, but rooms reached early (when `positions`
        // was sparse) didn't have full reciprocal context to score
        // against. Walk the placement set again and, for each room,
        // see if a candidate coord (implied by a now-known reciprocal
        // neighbour) scores higher than the BFS-committed coord. Move
        // the room if so. Bounded by a small iteration cap because the
        // moves can cascade — the loop converges quickly in practice
        // because every move strictly increases the global match
        // count (which is finite).
        //
        // After moves settle, rebuild edgesFromCoord from scratch:
        // a moved room's stub edges live at the OLD coord. Easier to
        // recompute than to chase the deltas.
        RefineWithDeferredResolution(positions, coordToRoom, origin, covered);

        // Drop the tunnelled-through (covered) rooms from the drawn set.
        // They served their purpose during BFS — anchoring the far side of
        // a crossing so traversal continued — but they share a cell with
        // the foreign structure overhead, so they must not draw. Their
        // neighbours' exits into them become honest stubs at render time
        // (the target isn't in Positions), giving the clean "path goes
        // under here" gap. coordToRoom never held them.
        foreach (RoomKey c in covered) positions.Remove(c);

        edgesFromCoord.Clear();
        trapEdgesFromCoord.Clear();
        spellEdgesFromCoord.Clear();
        foreach ((RoomKey rk, (int X, int Y) rcoord) in positions)
        {
            Room? rroom = _graph.GetRoom(rk);
            if (rroom is null) continue;
            foreach ((Direction edir, RoomExit eexit) in rroom.Exits)
            {
                if (!IsPlanar(edir)) continue;
                AddEdge(edgesFromCoord, rcoord, edir);
                if (eexit.Hint == RoomExitHint.Trap)
                    AddEdge(trapEdgesFromCoord, rcoord, edir);
                if (eexit.CastsOnWalk)
                    AddEdge(spellEdgesFromCoord, rcoord, edir);
            }
        }

        return new RoomLayout(
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
                kvp => (IReadOnlySet<Direction>)kvp.Value),
            SpellEdgesFromCoord: spellEdgesFromCoord.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlySet<Direction>)kvp.Value))
        { LayoutRoot = origin };
    }

    // Retry-acceptance rule for the score-and-retry pass: a candidate
    // layout replaces the current best when it places MORE rooms, or
    // places the same number with FEWER stubs. Coverage dominates because
    // a dropped room disappears from the map entirely (strictly worse than
    // a bent connector); stub count only breaks ties between equal-
    // coverage layouts. Extracted + internal so the ordering is
    // unit-testable without standing up a full graph — optimising on stubs
    // alone silently prefers layouts that drop whole clusters.
    internal static bool RetryImproves(
        int candidatePlaced, int candidateStubs,
        int bestPlaced, int bestStubs)
        => candidatePlaced > bestPlaced
        || (candidatePlaced == bestPlaced && candidateStubs < bestStubs);

    // Count exit-direction edges that don't actually reach their declared
    // target room at the expected planar offset. A stub-heavy layout
    // indicates BFS reached a cluster via a path whose tentative placement
    // disagrees with the cluster's internal geometry; the score-and-retry
    // pass uses this signal to decide whether to retry from a different
    // origin.
    private int CountStubs(RoomLayout layout)
    {
        int stubs = 0;
        foreach ((RoomKey key, (int X, int Y) coord) in layout.Positions)
        {
            Room? room = _graph.GetRoom(key);
            if (room is null) continue;
            foreach ((Direction dir, RoomExit exit) in room.Exits)
            {
                if (!TryPlanarOffset(dir, out int dx, out int dy)) continue;
                (int X, int Y) expected = (coord.X + dx, coord.Y + dy);
                if (!layout.CoordToRoom.TryGetValue(expected, out RoomKey actual)
                    || !actual.Equals(exit.Target))
                {
                    stubs++;
                }
            }
        }
        return stubs;
    }

    // Up to count retry-origin seeds for the score-and-retry pass, in try
    // order. Two pools:
    //   1) High-degree placed rooms — the placed rooms with the most
    //      planar exits, which sit in the interior of the placed region. A
    //      layout's coverage is governed by how CENTRAL its origin is: a
    //      peripheral origin reaches a sub-area through its single doorway
    //      at a bent angle, collides on that doorway, drops it, and then
    //      never explores the whole block behind it. (The v1.11p sewers
    //      from 1/602 place only 567 of 571 rooms for exactly this reason
    //      — the map-1→map-11 doorway collides and the entire map-11 block
    //      vanishes behind a single stub.) Re-rooting BFS at a central,
    //      high-degree room reaches every sub-area symmetrically and
    //      recovers the dropped block: empirically every top-degree placed
    //      sewer room lays out all 571 rooms. Seeding from the dropped
    //      rooms themselves does NOT help — a dropped sub-area is typically
    //      reached through a one-way / vertical seam, so BFS from inside it
    //      can't even get back to the requested origin and the candidate
    //      is discarded. Ranked by planar degree descending.
    //   2) Placed-but-stubbed rooms — rooms whose exits bend. Bend
    //      reduction once coverage is complete. Ranked by stub count.
    // Both pools carry a deterministic (Map, Room) tiebreak: ties are
    // common (every grid room shares a degree / stub count) and List.Sort
    // is an introsort — not stable — so without a total order the tie
    // ordering, and thus which seeds the Take keeps, would be left to the
    // runtime. Each seed is a different BFS root yielding a wholesale-
    // different layout, which is how the same graph laid out differently
    // on different machines. The tiebreak makes the chosen layout a pure
    // function of (graph, origin).
    private IEnumerable<RoomKey> PickRetryOrigins(RoomLayout layout, RoomKey origin, int count)
    {
        // Pool 1: placed rooms ranked by planar degree (centrality proxy).
        // The most-connected placed rooms are interior junctions; re-rooting
        // BFS there reaches every sub-area symmetrically and maximises
        // coverage (see summary for the sewers case).
        List<(int Degree, RoomKey Key)> central = new();
        foreach ((RoomKey key, (int X, int Y) _) in layout.Positions)
        {
            if (key.Equals(origin)) continue;
            Room? room = _graph.GetRoom(key);
            if (room is null) continue;
            int degree = 0;
            foreach ((Direction dir, RoomExit _) in room.Exits)
                if (IsPlanar(dir)) degree++;
            central.Add((degree, key));
        }
        central.Sort(static (a, b) =>
        {
            int byDeg = b.Degree.CompareTo(a.Degree);
            if (byDeg != 0) return byDeg;
            if (a.Key.Map != b.Key.Map) return a.Key.Map.CompareTo(b.Key.Map);
            return a.Key.Room.CompareTo(b.Key.Room);
        });

        // Pool 2: placed rooms with bent exits, ranked by stub count. Used
        // for bend reduction once coverage is already maximal.
        List<(int Stubs, RoomKey Key)> stubbed = new();
        foreach ((RoomKey key, (int X, int Y) coord) in layout.Positions)
        {
            if (key.Equals(origin)) continue;
            Room? room = _graph.GetRoom(key);
            if (room is null) continue;
            int stubs = 0;
            foreach ((Direction dir, RoomExit exit) in room.Exits)
            {
                if (!TryPlanarOffset(dir, out int dx, out int dy)) continue;
                (int X, int Y) expected = (coord.X + dx, coord.Y + dy);
                if (!layout.CoordToRoom.TryGetValue(expected, out RoomKey actual)
                    || !actual.Equals(exit.Target))
                {
                    stubs++;
                }
            }
            if (stubs > 0) stubbed.Add((stubs, key));
        }
        stubbed.Sort(static (a, b) =>
        {
            int byStubs = b.Stubs.CompareTo(a.Stubs);
            if (byStubs != 0) return byStubs;
            if (a.Key.Map != b.Key.Map) return a.Key.Map.CompareTo(b.Key.Map);
            return a.Key.Room.CompareTo(b.Key.Room);
        });

        // A high-degree room can also be stubbed; Distinct keeps each seed
        // to one BFS pass and preserves first-occurrence (centrality) order.
        return central.Select(c => c.Key)
            .Concat(stubbed.Select(s => s.Key))
            .Distinct()
            .Take(count);
    }

    // Translate every coord in source so that newOrigin lands at (0,0).
    // Preserves the retry layout's structural decisions (placements,
    // edges, vertical hints) while honouring the outer-method contract
    // that BuildLayout(A).Origin == A.
    private static RoomLayout TranslateLayoutToNewOrigin(RoomLayout source, RoomKey newOrigin)
    {
        if (!source.Positions.TryGetValue(newOrigin, out (int X, int Y) anchor))
            return source;
        int dx = -anchor.X;
        int dy = -anchor.Y;

        Dictionary<RoomKey, (int X, int Y)> positions = new();
        foreach ((RoomKey k, (int X, int Y) v) in source.Positions)
            positions[k] = (v.X + dx, v.Y + dy);

        Dictionary<(int X, int Y), RoomKey> coordToRoom = new();
        foreach (((int X, int Y) c, RoomKey k) in source.CoordToRoom)
            coordToRoom[(c.X + dx, c.Y + dy)] = k;

        Dictionary<(int X, int Y), IReadOnlySet<Direction>> edges = new();
        foreach (((int X, int Y) c, IReadOnlySet<Direction> dirs) in source.EdgesFromCoord)
            edges[(c.X + dx, c.Y + dy)] = dirs;

        Dictionary<(int X, int Y), IReadOnlySet<Direction>> trapEdges = new();
        foreach (((int X, int Y) c, IReadOnlySet<Direction> dirs) in source.TrapEdgesFromCoord)
            trapEdges[(c.X + dx, c.Y + dy)] = dirs;

        Dictionary<(int X, int Y), IReadOnlySet<Direction>> spellEdges = new();
        foreach (((int X, int Y) c, IReadOnlySet<Direction> dirs) in source.SpellEdgesFromCoord)
            spellEdges[(c.X + dx, c.Y + dy)] = dirs;

        return new RoomLayout(
            Origin: newOrigin,
            Positions: positions,
            VerticalHints: source.VerticalHints,
            OffGrid: source.OffGrid,
            CoordToRoom: coordToRoom,
            EdgesFromCoord: edges,
            TrapEdgesFromCoord: trapEdges,
            SpellEdgesFromCoord: spellEdges)
        {
            // Origin moves to the requested room, but the BFS was actually
            // anchored at source's root — keep that as the seed so the
            // header reports the re-root honestly.
            LayoutRoot = source.LayoutRoot,
            StubCount = source.StubCount,
        };
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
        d != Direction.U && d != Direction.D && d != Direction.Teleport;

    // Subscribed by AppServices to RoomGraphManager.GraphReloaded —
    // flushes the layout cache since per-room references are invalidated.
    public void OnGraphReloaded()
    {
        lock (_cacheLock)
        {
            _layoutCache.Clear();
            _planeIndex = null;
            _walkReachCache = null;
        }
    }

    // Lazily assign every room a (component, floor) over the NON-text exit
    // graph. One BFS per component; U/D shift the floor, cardinals keep it,
    // text exits are skipped so they can't fuse two vertical levels. Result
    // is cached until the graph reloads.
    private Dictionary<RoomKey, (int Component, int Floor)> PlaneIndex()
    {
        lock (_cacheLock)
        {
            if (_planeIndex is not null) return _planeIndex;

            var index = new Dictionary<RoomKey, (int Component, int Floor)>();
            int component = 0;
            var queue = new Queue<RoomKey>();
            foreach (Room seed in _graph.Rooms)
            {
                if (index.ContainsKey(seed.Key)) continue;
                index[seed.Key] = (component, 0);
                queue.Enqueue(seed.Key);
                while (queue.Count > 0)
                {
                    RoomKey cur = queue.Dequeue();
                    Room? room = _graph.GetRoom(cur);
                    if (room is null) continue;
                    int curFloor = index[cur].Floor;
                    foreach ((Direction dir, RoomExit exit) in room.Exits)
                    {
                        if (exit.Hint is RoomExitHint.Text or RoomExitHint.Teleport) continue;   // portals don't link floors
                        RoomKey t = exit.Target;
                        if (index.ContainsKey(t)) continue;
                        if (_graph.GetRoom(t) is null) continue;
                        int df = dir == Direction.U ? 1 : dir == Direction.D ? -1 : 0;
                        index[t] = (component, curFloor + df);
                        queue.Enqueue(t);
                    }
                }
                component++;
            }
            _planeIndex = index;
            return index;
        }
    }

    // A text exit is cross-plane when its endpoints share a non-text
    // component (so their floor numbers are comparable), sit on different
    // floors, AND the target can't be walked to on foot. The floor-delta
    // gate alone false-positives on outdoor terrain: the Darkwood forest's
    // two halves are joined by a 100-plus-room rocky-cliff detour whose
    // gradual descent inflates the plane-index floor gap to 4, so the
    // same-plane go-path linking them looked cross-level and got stubbed —
    // hiding half the forest. Requiring "target unreachable on foot"
    // distinguishes that shortcut (target still walkable the long way, so
    // draw it) from a genuine portal like the Crypt trainers' go-portal to
    // the surface graveyard (target reachable ONLY through the portal, so
    // stub it to keep the foreign floor off this plane).
    private bool IsCrossPlaneTextExit(RoomKey source, RoomKey target)
    {
        Dictionary<RoomKey, (int Component, int Floor)> idx = PlaneIndex();
        if (!idx.TryGetValue(source, out (int Component, int Floor) a)) return false;
        if (!idx.TryGetValue(target, out (int Component, int Floor) b)) return false;
        if (a.Component != b.Component || a.Floor == b.Floor) return false;
        return !WalkReachableOverNonText(source, target);
    }

    // Directed BFS reachability from source to target over NON-text exits
    // (cardinals + U/D, excluding the text portals we're classifying).
    // Answers "can the player walk here without a teleport?" Cheap for the
    // isolated-pocket case (a one-way-in tomb exhausts in a handful of
    // rooms); the reachable case terminates at the target. Memoized per
    // pair — only ever called for the few cardinal-filed text exits that
    // already pass the floor-delta gate.
    private bool WalkReachableOverNonText(RoomKey source, RoomKey target)
    {
        (RoomKey Source, RoomKey Target) pair = (source, target);
        lock (_cacheLock)
        {
            if (_walkReachCache is not null &&
                _walkReachCache.TryGetValue(pair, out bool cached))
                return cached;
        }

        bool reachable = source == target;
        if (!reachable)
        {
            var seen = new HashSet<RoomKey> { source };
            var queue = new Queue<RoomKey>();
            queue.Enqueue(source);
            while (queue.Count > 0)
            {
                Room? room = _graph.GetRoom(queue.Dequeue());
                if (room is null) continue;
                foreach ((Direction _, RoomExit exit) in room.Exits)
                {
                    if (exit.Hint is RoomExitHint.Text or RoomExitHint.Teleport) continue;
                    RoomKey t = exit.Target;
                    if (t == target) { reachable = true; break; }
                    if (!seen.Add(t)) continue;
                    if (_graph.GetRoom(t) is null) continue;
                    queue.Enqueue(t);
                }
                if (reachable) break;
            }
        }

        lock (_cacheLock)
        {
            _walkReachCache ??= new Dictionary<(RoomKey, RoomKey), bool>();
            _walkReachCache[pair] = reachable;
        }
        return reachable;
    }

    // Eagerly build (and cache) the layout from the first room in the
    // active graph on a thread-pool task. Called by AppServices after
    // OnGraphReloaded so the Navigation window's first render doesn't pay
    // the BFS cost on the UI thread — real realms have ~2000 rooms and the
    // BFS allocates a few hundred KB worth of dictionaries.
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

    // Two-pass deferred-resolution refinement. After the BFS has
    // committed an initial placement for every reachable room, walk the
    // placement set and try to move each room to a coord that scores
    // higher (satisfies more reciprocal exits) against the now-fully-
    // populated positions map. The origin is pinned because it anchors the
    // layout — moving it would shift everyone in lockstep with no net win.
    //
    // Convergence: every move strictly increases the global "matched
    // reciprocals" count (we only move when the new score is strictly
    // higher). The count is bounded by the total number of planar exits in
    // the graph, so the loop terminates. The iteration cap is defensive —
    // in practice the loop runs 1–3 passes.
    private void RefineWithDeferredResolution(
        Dictionary<RoomKey, (int X, int Y)> positions,
        Dictionary<(int X, int Y), RoomKey> coordToRoom,
        RoomKey origin,
        IReadOnlySet<RoomKey> covered)
    {
        const int MaxIterations = 8;
        for (int iter = 0; iter < MaxIterations; iter++)
        {
            bool moved = false;
            foreach (RoomKey roomKey in positions.Keys.ToArray())
            {
                if (roomKey.Equals(origin)) continue;       // anchor
                // Tunnelled-through rooms hold a phantom coord that
                // deliberately overlaps a foreign cell and aren't in
                // coordToRoom; a free-move here would promote one to a
                // real coord and draw it. Leave them pinned.
                if (covered.Contains(roomKey)) continue;
                Room? roomData = _graph.GetRoom(roomKey);
                if (roomData is null) continue;

                (int X, int Y) current = positions[roomKey];
                int currentScore = ScoreCoord(roomData, current, positions);

                // Two move shapes:
                //   1) Move to a free coord that scores higher.
                //   2) Swap with another room when the sum of both
                //      rooms' scores strictly improves. The free-coord
                //      pass usually handles loose graphs; the swap pass
                //      is what unlocks tight maze clusters where every
                //      candidate is claimed by another cluster member.
                (int X, int Y)? bestFree = null;
                int bestFreeScore = currentScore;
                (RoomKey Partner, (int X, int Y) PartnerCoord, int NewSum)? bestSwap = null;

                foreach ((Direction dir, RoomExit exit) in roomData.Exits)
                {
                    if (!TryPlanarOffset(dir, out int dx, out int dy)) continue;
                    if (!positions.TryGetValue(exit.Target, out (int X, int Y) nb)) continue;
                    (int X, int Y) candidate = (nb.X - dx, nb.Y - dy);
                    if (candidate.Equals(current)) continue;

                    if (coordToRoom.TryGetValue(candidate, out RoomKey occupant))
                    {
                        if (occupant.Equals(roomKey)) continue;
                        if (occupant.Equals(origin)) continue;
                        Room? partnerData = _graph.GetRoom(occupant);
                        if (partnerData is null) continue;
                        int partnerCurrent = ScoreCoord(partnerData, candidate, positions);
                        // Hypothetical: candidate occupies "current"
                        // after swap. Score it there. We don't need to
                        // mutate positions for the score because Score
                        // reads neighbour coords from positions and
                        // neither this room nor the partner appears as
                        // their own neighbour.
                        int partnerSwapped = ScoreCoord(partnerData, current, positions);
                        int candidateMine  = ScoreCoord(roomData, candidate, positions);
                        int sumNow = currentScore + partnerCurrent;
                        int sumNew = candidateMine + partnerSwapped;
                        if (sumNew > sumNow
                            && (bestSwap is null || sumNew > bestSwap.Value.NewSum))
                        {
                            bestSwap = (occupant, candidate, sumNew);
                        }
                        continue;
                    }

                    int score = ScoreCoord(roomData, candidate, positions);
                    if (score > bestFreeScore)
                    {
                        bestFreeScore = score;
                        bestFree = candidate;
                    }
                }

                // Prefer the free move (lower disruption) when both are
                // viable; only swap when free isn't an option.
                if (bestFree is { } moveTo)
                {
                    coordToRoom.Remove(current);
                    positions[roomKey] = moveTo;
                    coordToRoom[moveTo] = roomKey;
                    moved = true;
                }
                else if (bestSwap is { } swap)
                {
                    positions[roomKey]      = swap.PartnerCoord;
                    positions[swap.Partner] = current;
                    coordToRoom[swap.PartnerCoord] = roomKey;
                    coordToRoom[current]           = swap.Partner;
                    moved = true;
                }
            }
            if (!moved) break;
        }
    }

    // Decide whether a collided room is a graphical crossing that should
    // tunnel through rather than be dropped. True when the path continues
    // straight out the far side into territory we haven't drawn yet, AND
    // the blocking cell holds a structure this room has no two-way
    // connection to. The guards together pick out the crossing cases — a
    // river under a foreign bridge cell, and a one-way bridge gap where
    // the river room exits into a dock it can't return from (so the two
    // genuinely share a cell) — while leaving a true non-Euclidean fold,
    // which collides with one of its own reciprocal neighbours, to
    // drop-and-stub as before.
    private bool ShouldTunnelThrough(
        Room collided,
        Direction entryDir,
        (int X, int Y) tentative,
        Dictionary<RoomKey, (int X, int Y)> positions,
        Dictionary<(int X, int Y), RoomKey> coordToRoom)
    {
        // Straight-through continuation into an as-yet-undrawn room. No
        // continuation (a dead-end side room) or an already-placed far
        // side means tunnelling buys nothing — drop as before.
        if (!collided.Exits.TryGetValue(entryDir, out RoomExit through)) return false;
        if (!IsPlanar(entryDir)) return false;
        if (positions.ContainsKey(through.Target)) return false;

        // The blocking cell must NOT hold a two-way neighbour. A tight fold
        // collides with one of its own reciprocal neighbours (kept as an
        // honest stub); a crossing collides either with a room it shares no
        // exit with (a foreign bridge) or with one it exits into one-way (a
        // bridge gap) — both of which legitimately share the cell.
        if (!coordToRoom.TryGetValue(tentative, out RoomKey occupant)) return false;
        foreach ((Direction _, RoomExit ex) in collided.Exits)
        {
            if (!ex.Target.Equals(occupant)) continue;
            // collided has an exit into the occupant's cell. Disqualify the
            // crossing only when it's a TWO-WAY link — a genuine fold we keep
            // as an honest stub. A ONE-WAY exit (occupant has no edge back) is
            // a bridge gap: you drop out of the river into the dock with no
            // way to return, so the river room legitimately shares the dock's
            // cell and should tunnel (drawn only when it's the current room).
            Room? occRoom = _graph.GetRoom(occupant);
            if (occRoom is null) return false;          // unknown — be safe, don't tunnel
            foreach ((Direction _, RoomExit back) in occRoom.Exits)
                if (back.Target.Equals(collided.Key)) return false;
        }
        return true;
    }

    // Pick the best free coord for candidate among the parent-edge offset
    // (tentative) and every alternative implied by an already-placed
    // reciprocal exit. "Best" = highest count of reciprocal exits that
    // would lie flat at that coord; ties favour tentative for the
    // least-surprising behaviour when nothing's better. Returns null when
    // every candidate coord is occupied by a different room.
    private static (int X, int Y)? ChooseBestPlacement(
        Room candidate,
        (int X, int Y) tentative,
        Dictionary<RoomKey, (int X, int Y)> positions,
        Dictionary<(int X, int Y), RoomKey> coordToRoom)
    {
        bool tentativeFree = !coordToRoom.ContainsKey(tentative);
        int bestScore = tentativeFree ? ScoreCoord(candidate, tentative, positions) : -1;
        (int X, int Y)? best = tentativeFree ? tentative : null;

        foreach ((Direction dir, RoomExit exit) in candidate.Exits)
        {
            if (!TryPlanarOffset(dir, out int dx, out int dy)) continue;
            if (!positions.TryGetValue(exit.Target, out (int X, int Y) neighbour)) continue;
            // candidate.dir → neighbour, so candidate sits at
            // neighbour - (dx, dy). If that coord is free, score it.
            (int X, int Y) coord = (neighbour.X - dx, neighbour.Y - dy);
            if (coord.Equals(tentative)) continue;          // already scored
            if (coordToRoom.ContainsKey(coord)) continue;   // taken
            int score = ScoreCoord(candidate, coord, positions);
            if (score > bestScore)
            {
                bestScore = score;
                best = coord;
            }
        }
        return best;
    }

    // Number of planar exits on room whose reciprocal would land flat if
    // room sat at coord. Used by ChooseBestPlacement to rank candidates.
    private static int ScoreCoord(Room room, (int X, int Y) coord,
        Dictionary<RoomKey, (int X, int Y)> positions)
    {
        int score = 0;
        foreach ((Direction dir, RoomExit exit) in room.Exits)
        {
            if (!TryPlanarOffset(dir, out int dx, out int dy)) continue;
            if (!positions.TryGetValue(exit.Target, out (int X, int Y) nb)) continue;
            if (nb.X == coord.X + dx && nb.Y == coord.Y + dy) score++;
        }
        return score;
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
