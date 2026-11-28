using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Route-stitching coverage for BoatRoutePlanner: the planner runs BFS twice per
// candidate sailing (source→dock, arrival→goal) and keeps the passable sailing
// with the fewest total land hops. The fixture is a two-dock map so a test can
// prove the planner weighs the SUM of both legs, not just dock proximity.
public sealed class BoatRoutePlannerTests : IDisposable
{
    private readonly string _root;

    public BoatRoutePlannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-boatroute-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // Two docks bracket the same island cluster:
    //
    //   1/1(src) ─N─ 1/2(dock B, CMD 101) ─N─ 1/3(dock A, CMD 100)
    //
    //   dock A → arrival 1/10 ─N─ 1/11(dest)                       (FromArrival 1 hop)
    //   dock B → arrival 1/20 ─N─ 1/21 ─N─ 1/22 ─N─ 1/11(dest)     (FromArrival 3 hops)
    //
    // Via A: ToDock 2 + FromArrival 1 = 3.   Via B: ToDock 1 + FromArrival 3 = 4.
    // Dock B's pier is CLOSER, yet A wins on total hops — the property under test.
    private const string TwoDockRooms = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Home", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Fork", "CMD": 101,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Pier", "CMD": 100,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 10, "Name": "Isle A Port", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/11", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 11, "Name": "Isle Town", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/10", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 20, "Name": "Isle B Port", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/21", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 21, "Name": "Isle B Trail", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/22", "S": "1/20", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 22, "Name": "Isle B Ridge", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/11", "S": "1/21", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private const string TwoDockTbInfo = """
        [
          { "Number": 100, "LinkTo": 0,
            "Action": "secure passage to islea:minlevel 1 0:price 100 0:random 200:text 0\n",
            "Called From": "Room 1/3" },
          { "Number": 101, "LinkTo": 0,
            "Action": "secure passage to isleb:minlevel 1 0:price 100 0:random 201:text 0\n",
            "Called From": "Room 1/2" },
          { "Number": 200, "LinkTo": 0, "Action": "100:cast 600\n", "Called From": "rndm" },
          { "Number": 201, "LinkTo": 0, "Action": "100:cast 601\n", "Called From": "rndm" }
        ]
        """;

    // Disembark spells carry no Abil 141, so arrival map falls back to the dock's
    // map (1): island A lands at 1/10, island B at 1/20.
    private const string TwoDockSpells = """
        [
          { "Number": 600, "Name": "disembark isle a", "MinBase": 0, "MaxBase": 0,
            "Abil-0": 140, "AbilVal-0": 10 },
          { "Number": 601, "Name": "disembark isle b", "MinBase": 0, "MaxBase": 0,
            "Abil-0": 140, "AbilVal-0": 20 }
        ]
        """;

    private (BoatRoutePlanner Planner, RoomGraphManager Graph, BfsMapper Bfs) NewPlanner(
        string rooms = TwoDockRooms, string tbinfo = TwoDockTbInfo, string spells = TwoDockSpells)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), rooms);
        File.WriteAllText(Path.Combine(_root, "alpha", "TBInfo.json"), tbinfo);
        File.WriteAllText(Path.Combine(_root, "alpha", "Spells.json"), spells);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        TBInfoStore store = new(cache);
        store.OnActiveSetChanged("alpha");
        KnownSpellCatalog catalog = new(cache);
        RoomGraphManager graph = new(cache, log: null, tbinfo: store, spellCatalog: catalog);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        return (new BoatRoutePlanner(graph, bfs), graph, bfs);
    }

    // Toggleable filter double: avoids a set of rooms, and gates each sailing by a
    // predicate — mirrors what MovementFilter's DescribeBoatBlock does for the
    // planner without wiring the profile layer. BoatGate returns true when the
    // sailing IS boardable; the planner gates via DescribeBoatBlock, so a false
    // verdict maps to a Fare block (any non-None reason gates identically).
    private sealed class StubFilter : IRoomFilter
    {
        public HashSet<RoomKey> Avoided { get; } = new();
        public Func<BoatPassage, bool>? BoatGate { get; set; }
        public bool IsAvoided(RoomKey key) => Avoided.Contains(key);
        public ExitBlockReason DescribeBoatBlock(in BoatPassage passage) =>
            (BoatGate?.Invoke(passage) ?? true) ? ExitBlockReason.None : ExitBlockReason.Fare;
        public bool IsBoatPassable(in BoatPassage passage) =>
            DescribeBoatBlock(in passage) == ExitBlockReason.None;
    }

    [Fact]
    public void TryPlan_PicksFewestTotalLandHops_NotNearestDock()
    {
        var (planner, _, _) = NewPlanner();

        BoatRoutePlan? plan = planner.TryPlan(new RoomKey(1, 1), new RoomKey(1, 11), filter: null);

        Assert.NotNull(plan);
        Assert.Equal("secure passage to islea", plan!.Value.Passage.Keyword);
        Assert.Equal(new RoomKey(1, 3), plan.Value.Passage.DockRoom);   // the farther pier
        Assert.Equal(new RoomKey(1, 10), plan.Value.Passage.ArrivalRoom);
        Assert.Equal(new[] { Direction.N, Direction.N }, plan.Value.ToDock);
        Assert.Equal(new[] { Direction.N }, plan.Value.FromArrival);
        Assert.Equal(3, plan.Value.LandHops);
    }

    [Fact]
    public void TryPlan_ZeroHopLegs_WhenAtDockAndArrivalIsGoal()
    {
        var (planner, _, _) = NewPlanner();

        // Standing on pier 1/3 with the arrival 1/10 as the goal: both legs empty.
        BoatRoutePlan? plan = planner.TryPlan(new RoomKey(1, 3), new RoomKey(1, 10), filter: null);

        Assert.NotNull(plan);
        Assert.Equal("secure passage to islea", plan!.Value.Passage.Keyword);
        Assert.Empty(plan.Value.ToDock);
        Assert.Empty(plan.Value.FromArrival);
        Assert.Equal(0, plan.Value.LandHops);
    }

    [Fact]
    public void TryPlan_FilterGatesWinner_FallsBackToOtherSailing()
    {
        var (planner, _, _) = NewPlanner();
        var filter = new StubFilter { BoatGate = p => p.Place != "islea" };   // block the cheaper route

        BoatRoutePlan? plan = planner.TryPlan(new RoomKey(1, 1), new RoomKey(1, 11), filter);

        Assert.NotNull(plan);
        Assert.Equal("secure passage to isleb", plan!.Value.Passage.Keyword);
        Assert.Equal(4, plan.Value.LandHops);
    }

    [Fact]
    public void TryPlan_FilterGatesEverySailing_ReturnsNull()
    {
        var (planner, _, _) = NewPlanner();
        var filter = new StubFilter { BoatGate = _ => false };

        Assert.Null(planner.TryPlan(new RoomKey(1, 1), new RoomKey(1, 11), filter));
    }

    [Fact]
    public void TryPlan_AllowGated_SurfacesSoleGatedSailing_AsGatedPlan()
    {
        var (planner, _, _) = NewPlanner();
        var filter = new StubFilter { BoatGate = _ => false };   // every sailing gated

        // allowGated: a member can't board any sailing, but with no land route the
        // captain's refusal is the user's to make — surface the fewest-hop gated
        // sailing (islea, 3 hops) rather than vanishing into a bare "no path".
        BoatRoutePlan? plan = planner.TryPlan(
            new RoomKey(1, 1), new RoomKey(1, 11), filter, allowGated: true);

        Assert.NotNull(plan);
        Assert.Equal("secure passage to islea", plan!.Value.Passage.Keyword);
        Assert.True(plan.Value.IsGated);
        Assert.Equal(ExitBlockReason.Fare, plan.Value.Block);
        Assert.Equal(3, plan.Value.LandHops);
    }

    [Fact]
    public void TryPlan_AllowGated_PrefersBoardableSailing_OverShorterGatedOne()
    {
        var (planner, _, _) = NewPlanner();
        // Gate the shorter route (islea, 3 hops); leave the longer isleb (4 hops)
        // boardable. A boardable sailing must beat a gated one even when longer.
        var filter = new StubFilter { BoatGate = p => p.Place != "islea" };

        BoatRoutePlan? plan = planner.TryPlan(
            new RoomKey(1, 1), new RoomKey(1, 11), filter, allowGated: true);

        Assert.NotNull(plan);
        Assert.Equal("secure passage to isleb", plan!.Value.Passage.Keyword);
        Assert.False(plan.Value.IsGated);
        Assert.Equal(4, plan.Value.LandHops);
    }

    [Fact]
    public void TryPlan_AvoidedRoomStrandsSource_ReturnsNull()
    {
        var (planner, _, _) = NewPlanner();
        // 1/2 is the only way off 1/1 toward either pier — avoiding it strands the
        // source, so neither sailing's ToDock leg can be walked.
        var filter = new StubFilter();
        filter.Avoided.Add(new RoomKey(1, 2));

        Assert.Null(planner.TryPlan(new RoomKey(1, 1), new RoomKey(1, 11), filter));
    }

    // A walk routed through avoided rooms plans its legs through them, the hop
    // floors that weed out sailings included.
    [Fact]
    public void TryPlan_IgnoreAvoids_PlansTheLegThroughTheAvoidedRoom()
    {
        var (planner, _, _) = NewPlanner();
        var filter = new StubFilter();
        filter.Avoided.Add(new RoomKey(1, 2));

        BoatRoutePlan? plan = planner.TryPlan(new RoomKey(1, 1), new RoomKey(1, 11), filter, ignoreAvoids: true);

        Assert.NotNull(plan);
        Assert.Equal("islea", plan.Value.Passage.Place);
        Assert.Equal(new[] { Direction.N, Direction.N }, plan.Value.ToDock);
    }

    [Fact]
    public void TryPlan_SourceNotInGraph_ReturnsNull()
    {
        var (planner, _, _) = NewPlanner();
        Assert.Null(planner.TryPlan(new RoomKey(9, 999), new RoomKey(1, 11), filter: null));
    }

    [Fact]
    public void TryPlan_DestinationNotInGraph_ReturnsNull()
    {
        var (planner, _, _) = NewPlanner();
        Assert.Null(planner.TryPlan(new RoomKey(1, 1), new RoomKey(9, 999), filter: null));
    }

    // Every room each search looks into, counted.
    private sealed class CountingFilter : IRoomFilter
    {
        public int Looks { get; set; }
        public bool IsAvoided(RoomKey key)
        {
            Looks++;
            return false;
        }
    }

    // Report paradigm-20261021-135647: weighing every sailing's two legs held a
    // walk's commit for ten seconds. A sailing whose legs can't come in under the
    // land route is not planned, and neither is one that can't beat the best found.
    [Fact]
    public void TryPlan_LandRouteBound_PlansOnlyASailingThatCanBeatIt()
    {
        var (planner, _, _) = NewPlanner();
        CountingFilter unbounded = new(), bounded = new(), beaten = new();

        BoatRoutePlan? any = planner.TryPlan(new RoomKey(1, 1), new RoomKey(1, 11), unbounded);
        BoatRoutePlan? under4 = planner.TryPlan(new RoomKey(1, 1), new RoomKey(1, 11), bounded, landHopsUnder: 4);
        BoatRoutePlan? under3 = planner.TryPlan(new RoomKey(1, 1), new RoomKey(1, 11), beaten, landHopsUnder: 3);

        Assert.Equal("secure passage to islea", any?.Passage.Keyword);
        Assert.Equal(any?.Passage.Keyword, under4?.Passage.Keyword);
        Assert.Equal(any?.LandHops, under4?.LandHops);
        Assert.Null(under3);
        // No sailing comes in under three hops: only the floors are searched.
        Assert.True(beaten.Looks < bounded.Looks, $"{beaten.Looks} vs {bounded.Looks}");
    }

    [Fact]
    public void TryPlan_NoDocksInSet_ReturnsNull()
    {
        // Same rooms, but no TBInfo → no sailings discovered, so nothing to stitch.
        var (planner, _, _) = NewPlanner(tbinfo: "[]", spells: "[]");
        Assert.Null(planner.TryPlan(new RoomKey(1, 1), new RoomKey(1, 11), filter: null));
    }
}
