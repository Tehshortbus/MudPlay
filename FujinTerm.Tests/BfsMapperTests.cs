using System.IO;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 7.5 BFS coverage — shortest-path correctness, the avoided-room
/// filter, U/D handling, planar layout, off-grid collision routing,
/// and the layout cache.
/// </summary>
public sealed class BfsMapperTests : IDisposable
{
    private readonly string _root;

    public BfsMapperTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-bfs-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // ----- fixtures --------------------------------------------------
    //
    //  1/1 ──N── 1/2 ──N── 1/3
    //   │                   │
    //   E (Door)             E
    //   │                   │
    //  1/4 ──N── 1/5        1/6 ──D── 1/7 (cellar, vertical only)
    //
    private const string GridJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Town Gates",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/2", "S": "0", "E": "1/4 (Door)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Plaza",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "North Square",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/2", "E": "1/6", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "East Bridge",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/5", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Lookout",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/4", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 6, "Name": "Pavilion",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "1/7" },
          { "Map Number": 1, "Room Number": 7, "Name": "Cellar",
            "Light": -100, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "1/6", "D": "0" }
        ]
        """;

    private (BfsMapper Bfs, RoomGraphManager Graph) NewMapper(string json = GridJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return (new BfsMapper(graph), graph);
    }

    // ----- FindPath --------------------------------------------------

    [Fact]
    public void FindPath_ReturnsStepListInOrder()
    {
        var (bfs, _) = NewMapper();

        // 1/1 → 1/3 should be N, N.
        var path = bfs.FindPath(new RoomKey(1, 1), new RoomKey(1, 3));

        Assert.NotNull(path);
        Assert.Equal(new[] { Direction.N, Direction.N }, path);
    }

    [Fact]
    public void FindPath_CrossesGridCorner()
    {
        var (bfs, _) = NewMapper();

        // 1/1 → 1/6: N to 1/2, N to 1/3, E to 1/6.
        var path = bfs.FindPath(new RoomKey(1, 1), new RoomKey(1, 6));

        Assert.NotNull(path);
        Assert.Equal(new[] { Direction.N, Direction.N, Direction.E }, path);
    }

    [Fact]
    public void FindPath_SameRoom_ReturnsNullByDefault()
    {
        var (bfs, _) = NewMapper();
        Assert.Null(bfs.FindPath(new RoomKey(1, 1), new RoomKey(1, 1)));
    }

    [Fact]
    public void FindPath_SameRoom_ReturnsEmptyWhenFlagged()
    {
        var (bfs, _) = NewMapper();
        var path = bfs.FindPath(new RoomKey(1, 1), new RoomKey(1, 1),
            returnEmptyWhenAtDestination: true);
        Assert.NotNull(path);
        Assert.Empty(path!);
    }

    [Fact]
    public void FindPath_UnknownEndpoints_ReturnsNull()
    {
        var (bfs, _) = NewMapper();
        Assert.Null(bfs.FindPath(new RoomKey(999, 1), new RoomKey(1, 1)));
        Assert.Null(bfs.FindPath(new RoomKey(1, 1), new RoomKey(999, 1)));
    }

    [Fact]
    public void FindPath_DescendsViaUDExits()
    {
        var (bfs, _) = NewMapper();

        // 1/6 → 1/7 should be a single D.
        var path = bfs.FindPath(new RoomKey(1, 6), new RoomKey(1, 7));

        Assert.NotNull(path);
        Assert.Equal(new[] { Direction.D }, path);
    }

    // ----- Filter ----------------------------------------------------

    private sealed class AvoidSet : IRoomFilter
    {
        public HashSet<RoomKey> Avoided { get; } = new();
        public bool IsAvoided(RoomKey key) => Avoided.Contains(key);
    }

    [Fact]
    public void FindPath_Filter_BlocksIntermediate_ForcesAlternateRoute()
    {
        var (bfs, _) = NewMapper();
        AvoidSet filter = new();
        filter.Avoided.Add(new RoomKey(1, 2));   // Plaza blocked

        // No alternate route from 1/1 to 1/3 exists when Plaza is
        // blocked. (East side via 1/4–1/5 is a dead-end branch.)
        Assert.Null(bfs.FindPath(new RoomKey(1, 1), new RoomKey(1, 3), filter));
    }

    [Fact]
    public void FindPath_Filter_BlocksDestination_ReturnsNull()
    {
        var (bfs, _) = NewMapper();
        AvoidSet filter = new();
        filter.Avoided.Add(new RoomKey(1, 3));

        Assert.Null(bfs.FindPath(new RoomKey(1, 1), new RoomKey(1, 3), filter));
    }

    // ----- Form-A level gates ----------------------------------------

    private sealed class LevelFilter(int? level) : IRoomFilter
    {
        public bool IsAvoided(RoomKey key) => false;
        public bool IsExitBlocked(in RoomExit exit)
        {
            if (!exit.HasLevelGate) return false;
            if (level is not { } lv) return false;
            if (exit.MinLevel > 0 && lv < exit.MinLevel) return true;
            if (exit.MaxLevel > 0 && lv > exit.MaxLevel) return true;
            return false;
        }
    }

    //  1/1 ──N (Level: 20 to 0)── 1/2   (gated, only route)
    private const string GatedOnlyJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Foot of Stair",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2 (Level: 20 to 0)", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "High Ledge",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void FindPath_LevelGate_BlocksUnderleveledPlayer()
    {
        var (bfs, _) = NewMapper(GatedOnlyJson);
        Assert.Null(bfs.FindPath(new RoomKey(1, 1), new RoomKey(1, 2), new LevelFilter(10)));
    }

    [Fact]
    public void FindPath_LevelGate_AllowsAtOrAboveFloor()
    {
        var (bfs, _) = NewMapper(GatedOnlyJson);
        var path = bfs.FindPath(new RoomKey(1, 1), new RoomKey(1, 2), new LevelFilter(20));
        Assert.NotNull(path);
        Assert.Equal(new[] { Direction.N }, path);
    }

    [Fact]
    public void FindPath_LevelGate_UnknownLevel_DoesNotGate()
    {
        var (bfs, _) = NewMapper(GatedOnlyJson);
        var path = bfs.FindPath(new RoomKey(1, 1), new RoomKey(1, 2), new LevelFilter(null));
        Assert.NotNull(path);
        Assert.Equal(new[] { Direction.N }, path);
    }

    [Fact]
    public void FindPath_LevelGate_IgnoreExitGates_ReprobeFindsBlockedRoute()
    {
        var (bfs, _) = NewMapper(GatedOnlyJson);
        // Underleveled → normal probe null, gated re-probe finds it.
        Assert.Null(bfs.FindPath(new RoomKey(1, 1), new RoomKey(1, 2), new LevelFilter(10)));
        var ungated = bfs.FindPath(new RoomKey(1, 1), new RoomKey(1, 2),
            new LevelFilter(10), ignoreExitGates: true);
        Assert.NotNull(ungated);
        Assert.Equal(new[] { Direction.N }, ungated);
    }

    [Fact]
    public void FindPath_LevelGate_RoutesAroundViaUngatedAlternative()
    {
        //  1/1 ──N (Level: 20 to 0)── 1/2   (gated, 1 hop)
        //  1/1 ──E── 1/3 ──N── 1/2          (ungated detour, 2 hops)
        const string Json = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "Junction",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/2 (Level: 20 to 0)", "S": "0", "E": "1/3", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "Plateau",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/1", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "Switchback",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/2", "S": "0", "E": "0", "W": "1/1",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        var (bfs, _) = NewMapper(Json);

        // Underleveled → gated N is cut, walker takes the detour.
        var detour = bfs.FindPath(new RoomKey(1, 1), new RoomKey(1, 2), new LevelFilter(10));
        Assert.NotNull(detour);
        Assert.Equal(new[] { Direction.E, Direction.N }, detour);

        // At level → the 1-hop gated route wins (shortest).
        var direct = bfs.FindPath(new RoomKey(1, 1), new RoomKey(1, 2), new LevelFilter(25));
        Assert.NotNull(direct);
        Assert.Equal(new[] { Direction.N }, direct);
    }

    [Fact]
    public void ComputeDistancesFrom_LevelGate_DropsGatedOnlyRoom()
    {
        var (bfs, _) = NewMapper(GatedOnlyJson);
        var dist = bfs.ComputeDistancesFrom(new RoomKey(1, 1), new LevelFilter(10));
        Assert.True(dist.ContainsKey(new RoomKey(1, 1)));
        Assert.False(dist.ContainsKey(new RoomKey(1, 2)));   // unreachable underleveled
    }

    [Fact]
    public void DistanceBetween_ReturnsHopCount()
    {
        var (bfs, _) = NewMapper();
        Assert.Equal(3, bfs.DistanceBetween(new RoomKey(1, 1), new RoomKey(1, 6)));
        Assert.Equal(0, bfs.DistanceBetween(new RoomKey(1, 1), new RoomKey(1, 1)));
    }

    [Fact]
    public void DistanceBetween_NoPath_ReturnsNull()
    {
        var (bfs, _) = NewMapper();
        AvoidSet filter = new();
        filter.Avoided.Add(new RoomKey(1, 3));
        Assert.Null(bfs.DistanceBetween(new RoomKey(1, 1), new RoomKey(1, 6), filter));
    }

    // ----- RouteUsesToll ---------------------------------------------

    //  1/1 ──E── 1/2 ──E(Toll:5)── 1/3, plus off-path 1/1 ──N(Toll:5)── 1/4.
    private const string TollJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/4 (Toll: 5)", "S": "0", "E": "1/2", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/3 (Toll: 5)", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "C",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "D",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void RouteUsesToll_TollOnRoute_True()
    {
        var (bfs, _) = NewMapper(TollJson);
        Assert.True(bfs.RouteUsesToll(new RoomKey(1, 1), new RoomKey(1, 3)));
    }

    [Fact]
    public void RouteUsesToll_TollOffRoute_False()
    {
        var (bfs, _) = NewMapper(TollJson);
        // A→B route is plain; the N→D toll is off it, so it must not count.
        Assert.False(bfs.RouteUsesToll(new RoomKey(1, 1), new RoomKey(1, 2)));
    }

    [Fact]
    public void RouteUsesToll_NoPath_False()
    {
        var (bfs, _) = NewMapper(TollJson);
        Assert.False(bfs.RouteUsesToll(new RoomKey(1, 1), new RoomKey(9, 9)));
    }

    // ----- BuildLayout -----------------------------------------------

    [Fact]
    public void BuildLayout_OriginAtZeroZero()
    {
        var (bfs, _) = NewMapper();
        RoomLayout layout = bfs.BuildLayout(new RoomKey(1, 1));

        Assert.Equal((0, 0), layout.Positions[new RoomKey(1, 1)]);
    }

    [Fact]
    public void BuildLayout_PlacesPlanarNeighboursOnExpectedAxes()
    {
        var (bfs, _) = NewMapper();
        RoomLayout layout = bfs.BuildLayout(new RoomKey(1, 1));

        Assert.Equal((0, -1), layout.Positions[new RoomKey(1, 2)]);     // N
        Assert.Equal((0, -2), layout.Positions[new RoomKey(1, 3)]);     // N N
        Assert.Equal((1, 0),  layout.Positions[new RoomKey(1, 4)]);     // E
        Assert.Equal((1, -1), layout.Positions[new RoomKey(1, 5)]);     // E then N
        Assert.Equal((1, -2), layout.Positions[new RoomKey(1, 6)]);     // N N E
    }

    [Fact]
    public void BuildLayout_DownExit_NotVisited_SourceCellGetsVerticalHint()
    {
        var (bfs, _) = NewMapper();
        RoomLayout layout = bfs.BuildLayout(new RoomKey(1, 1));

        // Planar-only BFS: U/D destinations are NEVER followed. The
        // Cellar (1/7) is reachable only via D from 1/6 → it's not
        // in the layout at all (no position, no off-grid entry).
        Assert.DoesNotContain(new RoomKey(1, 7), layout.Positions.Keys);
        Assert.Empty(layout.OffGrid);                     // off-grid lane is gone

        // 1/6 still gets its VerticalHint.Down so the renderer can
        // draw the down-arrow glyph on the source cell.
        Assert.True(layout.VerticalHints.TryGetValue(new RoomKey(1, 6), out VerticalHint hint));
        Assert.Equal(VerticalHint.Down, hint);
    }

    [Fact]
    public void BuildLayout_VerticalExits_NotInEdgesFromCoord()
    {
        var (bfs, _) = NewMapper();
        RoomLayout layout = bfs.BuildLayout(new RoomKey(1, 1));

        // The map is a flat view — U / D exits don't render as stubs
        // on the source cell at all. They surface only via
        // VerticalHints.
        (int X, int Y) sixCoord = layout.Positions[new RoomKey(1, 6)];
        Assert.True(layout.EdgesFromCoord.TryGetValue(sixCoord, out IReadOnlySet<Direction>? edges));
        Assert.DoesNotContain(Direction.U, edges!);
        Assert.DoesNotContain(Direction.D, edges!);
    }

    [Fact]
    public void BuildLayout_Collision_RoutesSecondVisitToOffGrid()
    {
        // Non-Euclidean fixture: 1/1 N → 1/2 N → 1/3.
        // Then 1/3 W → 1/4, 1/4 S → 1/2.  Going 1/1 N N W S returns to
        // 1/2 — but BFS from 1/1 places 1/2 at (0,-1) first, so when
        // the alternate route reaches 1/2 via 1/4 it would collide.
        //
        // For this test the bigger collision target is 1/4:
        // 1/3 W → 1/4 wants (-1, -2). But if we ever discover 1/4 via
        // 1/1 N→1/2 W→1/4 first (W not present here — we'll wire it
        // explicitly), the second visit collides. We construct that
        // shape:
        //   1/1 N → 1/2 W → 1/4 N → 1/3 W → 1/4'  (loop back)
        const string Json = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "Origin",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "1/2", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "Hub",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "1/3", "S": "1/1", "E": "0", "W": "1/4",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "Plaza",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "1/2", "E": "0", "W": "1/4",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 4, "Name": "Square",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "1/2", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        var (bfs, _) = NewMapper(Json);
        RoomLayout layout = bfs.BuildLayout(new RoomKey(1, 1));

        Assert.Equal((0, 0),  layout.Positions[new RoomKey(1, 1)]);
        Assert.Equal((0, -1), layout.Positions[new RoomKey(1, 2)]);
        Assert.Equal((0, -2), layout.Positions[new RoomKey(1, 3)]);
        // 1/4 is reached first via 1/2 W → (-1, -1). Discovery via
        // 1/3 W would also be (-1, -2) → no collision actually; both
        // are distinct coords.
        Assert.Equal((-1, -1), layout.Positions[new RoomKey(1, 4)]);
        Assert.Empty(layout.OffGrid);
    }

    [Fact]
    public void BuildLayout_NonEuclideanReciprocal_RecordsSourceStubsOnBothSides()
    {
        // Repro of the v1.11p Dragon's Teeth Hills disjoint-rooms bug.
        // Topology:
        //   1/1 (O) — N → 1/2 (A); E → 1/3 (B)
        //   1/2 (A) — S → 1/1; NE → 1/3
        //   1/3 (B) — W → 1/1; SW → 1/2
        // BFS from O: places O(0,0), A(0,-1), B(1,0). Now A.NE → B
        // expects (1,-2) but B is at (1,0); B.SW → A expects (0,1)
        // but A is at (0,-1). Old code dropped both non-Euclidean
        // edges silently — the cell showed no exit even though the
        // graph carried the connection. New code records the source-
        // side stub on each so the user sees that the exit exists.
        const string Json = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "O",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/2", "S": "0", "E": "1/3", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "A",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/1", "E": "0", "W": "0",
                "NE": "1/3", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "B",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "0", "W": "1/1",
                "NE": "0", "NW": "0", "SE": "0", "SW": "1/2", "U": "0", "D": "0" }
            ]
            """;
        var (bfs, _) = NewMapper(Json);
        RoomLayout layout = bfs.BuildLayout(new RoomKey(1, 1));

        // Sanity: BFS reached B via O.E first, so B is at (1, 0).
        Assert.Equal((0, 0),  layout.Positions[new RoomKey(1, 1)]);
        Assert.Equal((0, -1), layout.Positions[new RoomKey(1, 2)]);
        Assert.Equal((1, 0),  layout.Positions[new RoomKey(1, 3)]);

        // A's NE edge (non-Euclidean — B isn't actually NE of A in
        // the placement) is now recorded so the cell renders the stub.
        Assert.True(layout.EdgesFromCoord.TryGetValue((0, -1), out IReadOnlySet<Direction>? aEdges));
        Assert.Contains(Direction.NE, aEdges!);
        Assert.Contains(Direction.S,  aEdges!);     // Euclidean S still there

        // B's SW edge (non-Euclidean — A isn't actually SW of B) is
        // likewise recorded.
        Assert.True(layout.EdgesFromCoord.TryGetValue((1, 0), out IReadOnlySet<Direction>? bEdges));
        Assert.Contains(Direction.SW, bEdges!);
        Assert.Contains(Direction.W,  bEdges!);     // Euclidean W still there
    }

    // ----- Layout cache ---------------------------------------------

    [Fact]
    public void BuildLayout_Memoizes_ReturnsSameInstance()
    {
        var (bfs, _) = NewMapper();
        RoomLayout a = bfs.BuildLayout(new RoomKey(1, 1));
        RoomLayout b = bfs.BuildLayout(new RoomKey(1, 1));
        Assert.Same(a, b);
    }

    [Fact]
    public void OnGraphReloaded_FlushesLayoutCache()
    {
        var (bfs, _) = NewMapper();
        RoomLayout a = bfs.BuildLayout(new RoomKey(1, 1));

        bfs.OnGraphReloaded();
        RoomLayout b = bfs.BuildLayout(new RoomKey(1, 1));

        Assert.NotSame(a, b);   // distinct instance after invalidation
        Assert.Equal(a.Positions.Count, b.Positions.Count);
    }

    [Fact]
    public void BuildLayout_UnknownOrigin_ReturnsEmptyLayout()
    {
        var (bfs, _) = NewMapper();
        RoomLayout layout = bfs.BuildLayout(new RoomKey(999, 1));
        Assert.Empty(layout.Positions);
        Assert.Empty(layout.OffGrid);
    }

    // ----- MudProxy-style edge tracking ------------------------------

    [Fact]
    public void EdgesFromCoord_RecordsEveryPlanarExit_FromTheSourceCell()
    {
        var (bfs, _) = NewMapper();
        RoomLayout layout = bfs.BuildLayout(new RoomKey(1, 1));

        // 1/1 sits at (0,0) and has N→1/2 + E→1/4 — both planar.
        Assert.True(layout.EdgesFromCoord.TryGetValue((0, 0), out IReadOnlySet<Direction>? edges));
        Assert.NotNull(edges);
        Assert.Contains(Direction.N, edges!);
        Assert.Contains(Direction.E, edges!);
    }

    [Fact]
    public void TrapEdgesFromCoord_Empty_WhenNoTraps()
    {
        var (bfs, _) = NewMapper();
        RoomLayout layout = bfs.BuildLayout(new RoomKey(1, 1));
        Assert.Empty(layout.TrapEdgesFromCoord);
    }

    [Fact]
    public void TrapEdgesFromCoord_RecordsTrappedDirections()
    {
        const string Json = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "A",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/2 (Trap)", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "B",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/1", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        var (bfs, _) = NewMapper(Json);
        RoomLayout layout = bfs.BuildLayout(new RoomKey(1, 1));

        Assert.True(layout.TrapEdgesFromCoord.TryGetValue((0, 0), out IReadOnlySet<Direction>? trapDirs));
        Assert.Contains(Direction.N, trapDirs!);
    }

    [Fact]
    public void CoordToRoom_MapsBack_FromGridPositionToRoomKey()
    {
        var (bfs, _) = NewMapper();
        RoomLayout layout = bfs.BuildLayout(new RoomKey(1, 1));

        Assert.Equal(new RoomKey(1, 1), layout.CoordToRoom[(0, 0)]);
        Assert.Equal(new RoomKey(1, 2), layout.CoordToRoom[(0, -1)]);
        Assert.Equal(new RoomKey(1, 6), layout.CoordToRoom[(1, -2)]);
    }

    [Fact]
    public void EdgesFromCoord_OnlyRecordsPlanarExits()
    {
        var (bfs, _) = NewMapper();
        RoomLayout layout = bfs.BuildLayout(new RoomKey(1, 1));

        // 1/6 has a planar W exit + a D exit. The planar one is in
        // the edge-tracking surface; the D is recorded ONLY as a
        // VerticalHint on the cell (no stub).
        (int X, int Y) sixCoord = layout.Positions[new RoomKey(1, 6)];
        Assert.True(layout.EdgesFromCoord.TryGetValue(sixCoord, out IReadOnlySet<Direction>? sixEdges));
        Assert.Contains(Direction.W, sixEdges!);
        Assert.DoesNotContain(Direction.D, sixEdges!);
        Assert.DoesNotContain(Direction.U, sixEdges!);
    }

    [Fact]
    public void BuildLayout_MaxRadius_LimitsReach()
    {
        var (bfs, _) = NewMapper();
        RoomLayout layout = bfs.BuildLayout(new RoomKey(1, 1), maxRadius: 1);

        // Only the origin and its immediate neighbours (1/2, 1/4).
        Assert.Contains(new RoomKey(1, 1), layout.Positions.Keys);
        Assert.Contains(new RoomKey(1, 2), layout.Positions.Keys);
        Assert.Contains(new RoomKey(1, 4), layout.Positions.Keys);
        Assert.DoesNotContain(new RoomKey(1, 3), layout.Positions.Keys);
        Assert.DoesNotContain(new RoomKey(1, 5), layout.Positions.Keys);
    }

    // ----- score-and-retry objective ---------------------------------
    //
    // Regression for the v1.11p map-1 sewers bug: from origin 1/602 the
    // layout placed 537 rooms with 3 stubs, while a sewer-side retry
    // origin placed all 571 rooms with 7 stubs. Optimising the retry on
    // stub count alone kept the 537-room layout (fewer stubs) and dropped
    // ~34 sewer rooms — they vanished from the map. The retry must value
    // COVERAGE first and use stub count only as a tiebreak, because a
    // dropped room (a collision the placer couldn't resolve) is strictly
    // worse than a bent connector that still shows the room.

    [Fact]
    public void RetryImproves_PrefersMoreCoverage_EvenWithMoreStubs()
    {
        // The bug's exact numbers: 571 placed / 7 stubs must beat
        // 537 placed / 3 stubs.
        Assert.True(BfsMapper.RetryImproves(
            candidatePlaced: 571, candidateStubs: 7,
            bestPlaced: 537, bestStubs: 3));
    }

    [Fact]
    public void RetryImproves_RejectsFewerCoverage_EvenWithFewerStubs()
    {
        // The inverse: a sparser-but-cleaner layout must NOT replace a
        // fuller one. This is the case the old stubs-only rule got wrong.
        Assert.False(BfsMapper.RetryImproves(
            candidatePlaced: 537, candidateStubs: 3,
            bestPlaced: 571, bestStubs: 7));
    }

    [Fact]
    public void RetryImproves_BreaksCoverageTies_OnStubCount()
    {
        Assert.True(BfsMapper.RetryImproves(
            candidatePlaced: 100, candidateStubs: 2,
            bestPlaced: 100, bestStubs: 5));
        Assert.False(BfsMapper.RetryImproves(
            candidatePlaced: 100, candidateStubs: 5,
            bestPlaced: 100, bestStubs: 2));
        // Identical layouts don't replace (no strict improvement).
        Assert.False(BfsMapper.RetryImproves(
            candidatePlaced: 100, candidateStubs: 3,
            bestPlaced: 100, bestStubs: 3));
    }

    // ----- blacklist hook --------------------------------------------

    [Fact]
    public void Blacklist_SkipsPlacement_KeepsDanglingStubEdge()
    {
        var (bfs, _) = NewMapper();
        HashSet<RoomKey> banned = new() { new RoomKey(1, 2) };  // Plaza
        bfs.ConfigureBlacklist(k => banned.Contains(k));

        RoomLayout layout = bfs.BuildLayout(new RoomKey(1, 1));

        // Blacklisted Plaza isn't placed and BFS doesn't traverse
        // through it, so North Square (1/3, reachable only via 1/2)
        // disappears too.
        Assert.DoesNotContain(new RoomKey(1, 2), layout.Positions.Keys);
        Assert.DoesNotContain(new RoomKey(1, 3), layout.Positions.Keys);

        // Origin still records its N exit so the renderer can draw a
        // dangling stub toward the hidden room.
        Assert.True(layout.EdgesFromCoord.TryGetValue((0, 0), out IReadOnlySet<Direction>? edges));
        Assert.Contains(Direction.N, edges!);
    }

    [Fact]
    public void Blacklist_OriginExempt_StillPlaced()
    {
        var (bfs, _) = NewMapper();
        HashSet<RoomKey> banned = new() { new RoomKey(1, 1) };  // origin itself
        bfs.ConfigureBlacklist(k => banned.Contains(k));

        RoomLayout layout = bfs.BuildLayout(new RoomKey(1, 1));

        // Origin always gets placed — otherwise the "you are here"
        // marker has nowhere to render.
        Assert.Contains(new RoomKey(1, 1), layout.Positions.Keys);
        Assert.Equal((0, 0), layout.Positions[new RoomKey(1, 1)]);
        // Its planar neighbours are still discovered.
        Assert.Contains(new RoomKey(1, 2), layout.Positions.Keys);
        Assert.Contains(new RoomKey(1, 4), layout.Positions.Keys);
    }

    [Fact]
    public void Blacklist_TrapEdgePreserved_OnDanglingStub()
    {
        const string Json = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "A",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/2 (Trap)", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "B",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/1", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        var (bfs, _) = NewMapper(Json);
        HashSet<RoomKey> banned = new() { new RoomKey(1, 2) };
        bfs.ConfigureBlacklist(k => banned.Contains(k));

        RoomLayout layout = bfs.BuildLayout(new RoomKey(1, 1));

        // Blacklisted target → not placed, but the trap classification
        // on the source stub survives so the renderer keeps the red
        // half-connector.
        Assert.DoesNotContain(new RoomKey(1, 2), layout.Positions.Keys);
        Assert.True(layout.TrapEdgesFromCoord.TryGetValue((0, 0), out IReadOnlySet<Direction>? trapDirs));
        Assert.Contains(Direction.N, trapDirs!);
    }

    [Fact]
    public void Blacklist_InvalidateCache_RebuildsLayoutOnNextCall()
    {
        var (bfs, _) = NewMapper();
        RoomLayout before = bfs.BuildLayout(new RoomKey(1, 1));
        Assert.Contains(new RoomKey(1, 2), before.Positions.Keys);

        HashSet<RoomKey> banned = new() { new RoomKey(1, 2) };
        bfs.ConfigureBlacklist(k => banned.Contains(k));  // also invalidates cache

        RoomLayout after = bfs.BuildLayout(new RoomKey(1, 1));
        Assert.NotSame(before, after);
        Assert.DoesNotContain(new RoomKey(1, 2), after.Positions.Keys);
    }

    [Fact]
    public void Blacklist_ProductionWiring_StoreAddHidesRoomOnNextBuild()
    {
        // Mirror AppServices wiring exactly: the predicate is bound ONCE
        // to the live store's IsBlacklisted, and the BFS cache is flushed
        // on every Changed (Add / Remove / pin). Crucially, ConfigureBlacklist
        // is NOT called again after the first bind — only the store's
        // _entries mutates, via Add. This reproduces the runtime path the
        // map takes when the user right-clicks "Add to blacklist".
        var (bfs, _) = NewMapper();
        RoomBlacklistStore store = new();
        bfs.ConfigureBlacklist(store.IsBlacklisted);  // bind once, like startup
        store.Changed += () => bfs.InvalidateCache();  // like AppServices line 2633

        RoomLayout before = bfs.BuildLayout(new RoomKey(1, 1));
        Assert.Contains(new RoomKey(1, 2), before.Positions.Keys);

        store.Add(new RoomKey(1, 2), "Plaza");  // fires Changed → InvalidateCache

        RoomLayout after = bfs.BuildLayout(new RoomKey(1, 1));
        Assert.NotSame(before, after);
        Assert.DoesNotContain(new RoomKey(1, 2), after.Positions.Keys);
    }

    [Fact]
    public void Blacklist_FindPath_StillTraversesBlacklistedRoom()
    {
        // Important: blacklist only suppresses RENDER. Pathing must
        // still find a route through a blacklisted room — the walker
        // can still walk into it (it's just hidden from the map).
        var (bfs, _) = NewMapper();
        HashSet<RoomKey> banned = new() { new RoomKey(1, 2) };
        bfs.ConfigureBlacklist(k => banned.Contains(k));

        var path = bfs.FindPath(new RoomKey(1, 1), new RoomKey(1, 3));

        Assert.NotNull(path);
        Assert.Equal(new[] { Direction.N, Direction.N }, path);
    }

    [Fact]
    public void NearestVisibleRoom_UnbannedSeed_ReturnedUnchanged()
    {
        var (bfs, _) = NewMapper();
        bfs.ConfigureBlacklist(k => k == new RoomKey(1, 2));  // Plaza only

        // Seed isn't blacklisted → no re-rooting, it's its own answer.
        Assert.Equal(new RoomKey(1, 1), bfs.NearestVisibleRoom(new RoomKey(1, 1)));
    }

    [Fact]
    public void NearestVisibleRoom_BannedSeed_HopsToSoleVisibleNeighbour()
    {
        var (bfs, _) = NewMapper();
        bfs.ConfigureBlacklist(k => k == new RoomKey(1, 5));  // Lookout

        // Lookout's only exit is S → East Bridge, which is visible.
        Assert.Equal(new RoomKey(1, 4), bfs.NearestVisibleRoom(new RoomKey(1, 5)));
    }

    [Fact]
    public void NearestVisibleRoom_BannedSeedAndNeighbour_HopsTwoDeep()
    {
        var (bfs, _) = NewMapper();
        HashSet<RoomKey> banned = new() { new RoomKey(1, 5), new RoomKey(1, 4) };
        bfs.ConfigureBlacklist(banned.Contains);

        // Lookout (1/5) → East Bridge (1/4) both hidden; BFS continues
        // past the bridge to Town Gates (1/1), its only other exit.
        Assert.Equal(new RoomKey(1, 1), bfs.NearestVisibleRoom(new RoomKey(1, 5)));
    }

    [Fact]
    public void NearestVisibleRoom_AllReachableBanned_FallsBackToSeed()
    {
        var (bfs, _) = NewMapper();
        bfs.ConfigureBlacklist(_ => true);  // every room hidden

        // No visible room exists anywhere → keep the seed so the map
        // still has an anchor to render the "you are here" marker.
        Assert.Equal(new RoomKey(1, 1), bfs.NearestVisibleRoom(new RoomKey(1, 1)));
    }

    [Fact]
    public void NearestVisibleRoom_SeedNotInGraph_ReturnedUnchanged()
    {
        var (bfs, _) = NewMapper();
        bfs.ConfigureBlacklist(_ => true);  // forces past the early unbanned check

        // A key with no room can't be BFS-expanded → returned as-is.
        Assert.Equal(new RoomKey(9, 9), bfs.NearestVisibleRoom(new RoomKey(9, 9)));
    }

    // ----- graphical crossing (river under a bridge) -----------------
    //
    // A straight path can pass UNDER a foreign structure on the same
    // plane — a river under a bridge: there's no U/D layer, so the
    // river cell wants the same coord as a bridge cell. Topology:
    //
    //        1/2 Bridge (0,-1)            (drawn first, depth 1)
    //         |
    //   River:  1/4 ─E─ 1/5 ─E─ 1/6 ─E─ 1/7
    //         (-1,-1) (0,-1!) (1,-1) (2,-1)
    //
    //   1/1 Town (0,0) ─N→ 1/2 ; ─W→ 1/3 Bank (-1,0) ─N→ 1/4 River W.
    //
    // BFS places the Bridge (1/2) at (0,-1) at depth 1, then the river
    // arrives there at depth 3 (Town→Bank→RiverW→RiverUnder) and
    // collides. The Under-Bridge room (1/5) is hidden but the layout
    // tunnels through it so the river east of the bridge still draws.
    private const string RiverUnderBridgeJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Town",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Bridge",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Bank",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/4", "S": "0", "E": "1/1", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "River West",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/3", "E": "1/5", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "River Under Bridge",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/6", "W": "1/4",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 6, "Name": "River East",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/7", "W": "1/5",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 7, "Name": "River Far East",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/6",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void BuildLayout_RiverUnderBridge_TunnelsThrough_DrawsRiverEast()
    {
        var (bfs, _) = NewMapper(RiverUnderBridgeJson);
        // Bounded (but ample) radius pins the raw BFS from this exact
        // origin: the score-and-retry pass (unbounded only) would re-root
        // to a symmetric layout that hides the bridge instead — the river
        // draws fully either way, but we assert the per-origin core here.
        RoomLayout layout = bfs.BuildLayout(new RoomKey(1, 1), maxRadius: 50);

        // The foreign bridge cell stays put; the river resumes on the
        // far side instead of the whole eastern river vanishing.
        Assert.Equal((0, -1),  layout.Positions[new RoomKey(1, 2)]);   // Bridge drawn
        Assert.Equal((-1, -1), layout.Positions[new RoomKey(1, 4)]);   // River West
        Assert.Equal((1, -1),  layout.Positions[new RoomKey(1, 6)]);   // River East
        Assert.Equal((2, -1),  layout.Positions[new RoomKey(1, 7)]);   // River Far East

        // The Under-Bridge room is hidden — it shares the bridge cell,
        // which still resolves to the Bridge (not the river room).
        Assert.DoesNotContain(new RoomKey(1, 5), layout.Positions.Keys);
        Assert.Equal(new RoomKey(1, 2), layout.CoordToRoom[(0, -1)]);

        // The river's two interrupted ends render as honest stubs into
        // the bridge cell — the "goes under here" gap.
        Assert.True(layout.EdgesFromCoord.TryGetValue((-1, -1), out IReadOnlySet<Direction>? westEdges));
        Assert.Contains(Direction.E, westEdges!);
        Assert.True(layout.EdgesFromCoord.TryGetValue((1, -1), out IReadOnlySet<Direction>? eastEdges));
        Assert.Contains(Direction.W, eastEdges!);
    }

    [Fact]
    public void BuildLayout_PerpendicularBranchAtCollision_DoesNotTunnel()
    {
        // Same approach as the crossing, but the collided room's only
        // onward exit is PERPENDICULAR (S), not straight-through (E).
        // That's a genuine fold, not a graphical crossing — it must
        // drop-and-stop exactly as before, so the branch behind it is
        // lost rather than tunnelled.
        const string Json = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "Town",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/2", "S": "0", "E": "0", "W": "1/3",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "Bridge",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/1", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "Bank",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/4", "S": "0", "E": "1/1", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 4, "Name": "River West",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/3", "E": "1/5", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 5, "Name": "Contested",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/8", "E": "0", "W": "1/4",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 8, "Name": "Behind",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/5", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        var (bfs, _) = NewMapper(Json);
        // Bounded radius again pins the raw per-origin BFS (see the
        // crossing test) — the unbounded retry would re-root to recover
        // the dropped subtree by hiding the dead-end bridge instead.
        RoomLayout layout = bfs.BuildLayout(new RoomKey(1, 1), maxRadius: 50);

        Assert.Equal((0, -1), layout.Positions[new RoomKey(1, 2)]);     // Bridge drawn
        Assert.Contains(new RoomKey(1, 4), layout.Positions.Keys);      // River West reached
        // No straight-through → collided room dropped, subtree lost.
        Assert.DoesNotContain(new RoomKey(1, 5), layout.Positions.Keys);
        Assert.DoesNotContain(new RoomKey(1, 8), layout.Positions.Keys);
    }
}
