using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Planning round a door the character can't open, or can open only by picking at
// poor odds. The fixture is the Ancient Coliseum as both realms' data has it: from
// the Viewing Stands 3/592 the Arena 3/595 is one step down through a door needing
// 301 Strength or Picklocks, or three steps east (a door any character bashes),
// down and west.
public sealed class DoorDetourTests
{
    private static readonly RoomKey Outside = new(3, 589);
    private static readonly RoomKey Stands = new(3, 592);
    private static readonly RoomKey Arena = new(3, 595);

    private static string Room(int map, int number, string name, params (string Dir, string Cell)[] exits)
    {
        StringBuilder sb = new();
        sb.Append($$"""{ "Map Number": {{map}}, "Room Number": {{number}}, "Name": "{{name}}", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0""");
        foreach (string dir in new[] { "N", "S", "E", "W", "NE", "NW", "SE", "SW", "U", "D" })
        {
            string cell = "0";
            foreach ((string d, string c) in exits) if (d == dir) cell = c;
            sb.Append($", \"{dir}\": \"{cell}\"");
        }
        return sb.Append(" }").ToString();
    }

    // down: the cell of the Viewing Stands' down exit. wayRound: whether the east
    // door out of the Stands exists.
    private static string Coliseum(string down = "3/595 (Door [301 picklocks/strength])", bool wayRound = true) =>
        "[" + string.Join(",\n",
            Room(3, 589, "Outside Coliseum", ("N", "3/592")),
            Room(3, 592, "Viewing Stands", ("S", "3/589"), ("D", down),
                ("E", wayRound ? "3/593 (Door [21 picklocks/strength])" : "0")),
            Room(3, 593, "Wide Passage", ("W", "3/592 (Door [21 picklocks/strength])"), ("D", "3/594")),
            Room(3, 594, "Preparation Chamber", ("W", "3/595"), ("U", "3/593")),
            Room(3, 595, "Arena", ("E", "3/594"), ("U", "3/592 (Door [201 picklocks/strength])"))) + "]";

    // A poor-odds door one step from the goal, and a plain way round of the given
    // number of steps.
    private static string DoorWithWayRound(int roundSteps)
    {
        List<string> rooms = new()
        {
            Room(1, 1, "Start", ("D", "1/99 (Door [301 picklocks])"), ("N", "1/2")),
        };
        for (int i = 2; i <= roundSteps; i++)
            rooms.Add(Room(1, i, $"Round{i}", ("N", i == roundSteps ? "1/99" : $"1/{i + 1}")));
        rooms.Add(Room(1, 99, "Goal"));
        return "[" + string.Join(",\n", rooms) + "]";
    }

    private static void WithGraph(string roomsJson, Action<BfsMapper, RoomGraphManager, MovementFilter> body)
    {
        string root = Path.Combine(Path.GetTempPath(), "mudplay-doordetour-" + Path.GetRandomFileName());
        try
        {
            string setDir = Path.Combine(root, "alpha");
            Directory.CreateDirectory(setDir);
            File.WriteAllText(Path.Combine(setDir, "Rooms.json"), roomsJson);
            GameDataCache cache = new(root);
            cache.SwitchSet("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");
            ProfileService profile = TestProfiles.Service();
            profile.LoadDefaultProfile();
            body(new BfsMapper(graph), graph, new MovementFilter(profile));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    private static void SetStats(MovementFilter filter, int strength, int picklocks)
    {
        filter.StrengthProvider = () => strength;
        filter.PicklocksProvider = () => picklocks;
        filter.MaxBashableStrengthProvider = () => 200;
    }

    private static readonly Direction[] ThroughTheDoor = { Direction.D };
    private static readonly Direction[] RoundTheDoor = { Direction.E, Direction.D, Direction.W };

    // ----- the pick formula and the poor-odds rule ---------------------

    [Theory]
    [InlineData(301, 0, 0)]       // no Picklocks never opens anything
    [InlineData(301, 300, 0)]
    [InlineData(301, 301, 1)]
    [InlineData(101, 150, 50)]    // GAME_MECHANICS: a [101 picklocks] door at 150 opens about half the time
    [InlineData(21, 400, 100)]
    [InlineData(0, 40, 41)]       // an "any" lock adds to the skill
    public void PickChancePercent_IsPicklocksLessTheLockPlusOne(int requirement, int picklocks, int expected) =>
        Assert.Equal(expected, DoorPolicy.PickChancePercent(requirement, picklocks));

    [Fact]
    public void IsPoorOddsPick_EitherSideOfTheThreshold()
    {
        int atThreshold = 301 + DoorPolicy.PoorPickChancePercent - 1;   // exactly the threshold chance
        Assert.True(DoorPolicy.IsPoorOddsPick(301, canBash: true, playerStrength: 120, playerPicklocks: atThreshold - 1));
        Assert.False(DoorPolicy.IsPoorOddsPick(301, canBash: true, playerStrength: 120, playerPicklocks: atThreshold));
    }

    [Fact]
    public void IsPoorOddsPick_NeverForADoorTheCharacterCanBashOrCantOpenAtAll()
    {
        // Bashable: it opens in the end, whatever the lock's odds.
        Assert.False(DoorPolicy.IsPoorOddsPick(101, canBash: true, playerStrength: 120, playerPicklocks: 102));
        // Neither verb: that is an impossible door, which the exit gate closes.
        Assert.False(DoorPolicy.IsPoorOddsPick(301, canBash: true, playerStrength: 120, playerPicklocks: 0));
    }

    // ----- an impossible door ------------------------------------------

    [Fact]
    public void ImpossibleDoor_OneStepAway_IsGoneRound()
    {
        WithGraph(Coliseum(), (bfs, _, filter) =>
        {
            SetStats(filter, strength: 120, picklocks: 0);
            Assert.Equal(RoundTheDoor, bfs.FindPath(Stands, Arena, filter));
            Assert.Equal(4, bfs.DistanceBetween(Outside, Arena, filter));
        });
    }

    [Fact]
    public void ImpossibleDoor_WithNoWayRound_BlocksTheRoute_AsBefore()
    {
        WithGraph(Coliseum(wayRound: false), (bfs, graph, filter) =>
        {
            SetStats(filter, strength: 120, picklocks: 0);
            Assert.Null(bfs.FindPath(Stands, Arena, filter));
            // Not something to fetch: a plain door has no key, so no route card.
            Assert.Null(RouteChoicePlanner.Evaluate(bfs, filter, graph, Stands, Arena));
        });
    }

    [Fact]
    public void UnknownStats_LeaveTheDoorToTheDoorHandler_AsBefore()
    {
        WithGraph(Coliseum(), (bfs, _, filter) =>
        {
            // No stat screen read yet: the filter has no Strength or Picklocks.
            Assert.False(filter.IsPoorOddsDoor(DownDoor()));
            Assert.Equal(ThroughTheDoor, bfs.FindPath(Stands, Arena, filter));
        });
    }

    [Fact]
    public void KeyedDoor_WithTheKeyHeld_IsWalkedThrough_WhateverTheLockOdds()
    {
        WithGraph(Coliseum(down: "3/595 (Key: 77 [or 301 picklocks/strength])"), (bfs, _, filter) =>
        {
            SetStats(filter, strength: 120, picklocks: 303);   // a 3% pick
            filter.InventoryReadyProbe = () => true;

            filter.ItemCarriedProbe = id => id == 77;
            Assert.Equal(ThroughTheDoor, bfs.FindPath(Stands, Arena, filter));

            filter.ItemCarriedProbe = _ => false;
            Assert.Equal(RoundTheDoor, bfs.FindPath(Stands, Arena, filter));
        });
    }

    // ----- a door that only a poor pick opens ---------------------------

    [Fact]
    public void PoorOddsPick_WithAShortWayRound_IsGoneRound()
    {
        WithGraph(Coliseum(), (bfs, _, filter) =>
        {
            SetStats(filter, strength: 120, picklocks: 303);   // 3% a try
            Assert.False(filter.IsExitBlocked(DownDoor()));    // not impossible
            Assert.True(filter.IsPoorOddsDoor(DownDoor()));
            Assert.Equal(RoundTheDoor, bfs.FindPath(Stands, Arena, filter));
            Assert.Equal(RoundTheDoor, bfs.FindPath(Stands, Arena, filter, avoidTraps: true));
        });
    }

    [Fact]
    public void FairOddsPick_IsWalkedThrough()
    {
        WithGraph(Coliseum(), (bfs, _, filter) =>
        {
            SetStats(filter, strength: 120, picklocks: 301 + DoorPolicy.PoorPickChancePercent - 1);
            Assert.False(filter.IsPoorOddsDoor(DownDoor()));
            Assert.Equal(ThroughTheDoor, bfs.FindPath(Stands, Arena, filter));
        });
    }

    [Fact]
    public void PoorOddsPick_WithNoWayRound_IsStillTheWay()
    {
        WithGraph(Coliseum(wayRound: false), (bfs, _, filter) =>
        {
            SetStats(filter, strength: 120, picklocks: 303);
            Assert.Equal(ThroughTheDoor, bfs.FindPath(Stands, Arena, filter));
        });
    }

    [Fact]
    public void PoorOddsPick_IsGoneRound_OnlyWithinTheDetourBound()
    {
        RoomKey start = new(1, 1), goal = new(1, 99);
        int longest = 1 + DoorPolicy.PoorPickDetourSteps;   // the door's one step plus the bound

        WithGraph(DoorWithWayRound(longest), (bfs, _, filter) =>
        {
            SetStats(filter, strength: 120, picklocks: 303);
            Assert.Equal(longest, bfs.FindPath(start, goal, filter)!.Count);
            Assert.Equal(longest, bfs.ComputeDistancesFrom(start, filter)[goal]);
        });
        WithGraph(DoorWithWayRound(longest + 1), (bfs, _, filter) =>
        {
            SetStats(filter, strength: 120, picklocks: 303);
            Assert.Equal(new[] { Direction.D }, bfs.FindPath(start, goal, filter));
            Assert.Equal(1, bfs.ComputeDistancesFrom(start, filter)[goal]);
        });
    }

    // The map-wide hop counts are FindPath's, rooms before the door and past it alike.
    [Fact]
    public void DistanceMaps_AgreeWithFindPath_AroundAPoorOddsDoor()
    {
        WithGraph(Coliseum(), (bfs, graph, filter) =>
        {
            SetStats(filter, strength: 120, picklocks: 303);
            IReadOnlyDictionary<RoomKey, int> fromOutside = bfs.ComputeDistancesFrom(Outside, filter);
            foreach (RoomKey room in new[] { Stands, new RoomKey(3, 593), new RoomKey(3, 594), Arena })
                Assert.Equal(bfs.FindPath(Outside, room, filter)!.Count, fromOutside[room]);
            Assert.Equal(4, bfs.ComputeDistancesTo(Outside, new[] { Arena }, filter)[Arena]);
        });
    }

    // ----- what the log and the bug report are told ---------------------

    [Fact]
    public void DescribeDoorRefusal_SaysWhyTheDoorIsKeptOff()
    {
        WithGraph(Coliseum(), (_, _, filter) =>
        {
            SetStats(filter, strength: 120, picklocks: 0);
            Assert.Equal("bash needs 301 Strength, more than any character can reach; pick chance 0% (Picklocks 0)",
                filter.DescribeDoorRefusal(DownDoor()));

            SetStats(filter, strength: 120, picklocks: 303);
            Assert.Contains("pick chance 3% (Picklocks 303)", filter.DescribeDoorRefusal(DownDoor()));

            // The east door is one it bashes: nothing to explain.
            Assert.Null(filter.DescribeDoorRefusal(new RoomExit(new RoomKey(3, 593), RoomExitHint.Door, RawHint: null, StatRequirement: 21)));
        });
    }

    private static RoomExit DownDoor() =>
        new(Arena, RoomExitHint.Door, RawHint: null, StatRequirement: 301, CanBash: true);
}
