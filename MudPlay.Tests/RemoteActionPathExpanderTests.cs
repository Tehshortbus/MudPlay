using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class RemoteActionPathExpanderTests : IDisposable
{
    private readonly string _root;

    public RemoteActionPathExpanderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-expander-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // 1/1 ──E (Door)── 1/2 ──N── 1/3
    private const string DoorGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Outside",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/2 (Door)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Foyer",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "0", "E": "0", "W": "1/1 (Door)",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Hall",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private RoomGraphManager NewGraph(string json = DoorGraphJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return graph;
    }

    [Fact]
    public void EmptyPath_ReturnsEmptyList()
    {
        RoomGraphManager graph = NewGraph();
        var steps = RemoteActionPathExpander.Expand(
            graph, new RoomKey(1, 1), Array.Empty<Direction>());
        Assert.Empty(steps);
    }

    [Fact]
    public void NoDoorOnPath_ProducesOnlyMoveSteps()
    {
        RoomGraphManager graph = NewGraph();
        var steps = RemoteActionPathExpander.Expand(
            graph, new RoomKey(1, 2), new[] { Direction.N });

        Assert.Single(steps);
        Assert.IsType<MoveStep>(steps[0]);
        Assert.Equal(Direction.N, ((MoveStep)steps[0]).Direction);
        Assert.Equal(new RoomKey(1, 3), ((MoveStep)steps[0]).ExpectedTarget);
    }

    [Fact]
    public void DoorExit_EmitsMoveStepOnly_WalkerHandlesAtRuntime()
    {
        // Door handling moved from expand-time to step-send-time —
        // the walker routes Door-hint MoveSteps through
        // DoorOpenManager (bash/pick/open) before the cardinal move
        // bytes go out. The expander no longer inserts a CommandStep
        // for the door.
        RoomGraphManager graph = NewGraph();
        var steps = RemoteActionPathExpander.Expand(
            graph, new RoomKey(1, 1), new[] { Direction.E });

        Assert.Single(steps);
        Assert.IsType<MoveStep>(steps[0]);
        Assert.Equal(Direction.E, ((MoveStep)steps[0]).Direction);
    }

    [Fact]
    public void MultiHopWithDoor_AllMoveSteps()
    {
        RoomGraphManager graph = NewGraph();

        // 1/1 → 1/3 via E (door) then N. No CommandStep — door
        // handling is runtime now.
        var steps = RemoteActionPathExpander.Expand(
            graph, new RoomKey(1, 1),
            new[] { Direction.E, Direction.N });

        Assert.Equal(2, steps.Count);
        Assert.Equal(Direction.E, ((MoveStep)steps[0]).Direction);
        Assert.Equal(Direction.N, ((MoveStep)steps[1]).Direction);
    }

    // A cross-room multi-action layout. The gated exit lives on 1/2's E slot
    // (target 1/9); its single prerequisite command must be typed in room 1/5,
    // which is one N hop off the host room. Start is 1/1 (one E hop into 1/2).
    //
    //   1/1 ──E──▶ 1/2 ──E (Hidden/Needs 1 Actions)──▶ 1/9
    //                │N
    //                ▼
    //               1/5  (D slot carries the Action#1 data → command typed here)
    private const string RemoteActionGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/2", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Host",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/5", "S": "0", "E": "1/9 (Hidden/Needs 1 Actions, specific order)", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "LeverRoom",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0",
            "D": "Action#1 [on the E exit of room 1/2]: pull lever" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // A lever 120 rooms up a corridor from the gated exit: the go-act-return detour
    // is 241 steps. The new master assassin's passage needs about 232, which an
    // earlier 200-step backstop cut off, ending the walk at the shut exit.
    [Fact]
    public void CrossRoomMultiAction_LongDetour_StillBuilt()
    {
        const int corridor = 120;
        var rooms = new System.Text.StringBuilder("[");
        static string Room(int n, string name, string north, string south, string east, string west, string down) =>
            $"{{\"Map Number\": 1, \"Room Number\": {n}, \"Name\": \"{name}\", \"Light\": 0, \"Shop\": 0, \"Lair\": \"\", \"Delay\": 0, "
            + $"\"N\": \"{north}\", \"S\": \"{south}\", \"E\": \"{east}\", \"W\": \"{west}\", "
            + "\"NE\": \"0\", \"NW\": \"0\", \"SE\": \"0\", \"SW\": \"0\", \"U\": \"0\", "
            + $"\"D\": \"{down}\"}}";
        rooms.Append(Room(1, "Start", "0", "0", "1/2", "0", "0")).Append(',');
        rooms.Append(Room(2, "Host", "1/100", "0", "1/9 (Hidden/Needs 1 Actions, specific order)", "1/1", "0")).Append(',');
        for (int i = 0; i < corridor; i++)
        {
            int n = 100 + i;
            string north = i < corridor - 1 ? $"1/{n + 1}" : "0";
            string south = i == 0 ? "1/2" : $"1/{n - 1}";
            string down = i == corridor - 1 ? "Action#1 [on the E exit of room 1/2]: pull lever" : "0";
            rooms.Append(Room(n, i == corridor - 1 ? "LeverRoom" : "Corridor", north, south, "0", "0", down)).Append(',');
        }
        rooms.Append(Room(9, "Vault", "0", "0", "0", "1/2", "0")).Append(']');

        RoomGraphManager graph = NewGraph(rooms.ToString());
        BfsMapper bfs = new(graph);
        var steps = RemoteActionPathExpander.Expand(graph, new RoomKey(1, 1), new[] { Direction.E, Direction.E }, bfs);

        Assert.Contains(steps, s => s is CommandStep { Command: "pull lever" });
        MoveStep cross = Assert.IsType<MoveStep>(steps[^1]);
        Assert.Equal(new RoomKey(1, 9), cross.ExpectedTarget);
        Assert.Equal(2 + 2 * corridor + 1, steps.Count);   // in, up, pull, back down, cross
    }

    [Fact]
    public void CrossRoomMultiAction_WithMapper_InjectsWalkActWalkBackCross()
    {
        RoomGraphManager graph = NewGraph(RemoteActionGraphJson);
        BfsMapper bfs = new(graph);

        // Path 1/1 → 1/9 is E then E (BFS crosses the multi-action exit freely).
        var steps = RemoteActionPathExpander.Expand(
            graph, new RoomKey(1, 1),
            new[] { Direction.E, Direction.E }, bfs);

        // E into host; N to lever room; pull lever; S back; final primed E cross.
        Assert.Equal(5, steps.Count);

        Assert.Equal(Direction.E, ((MoveStep)steps[0]).Direction);
        Assert.Equal(new RoomKey(1, 2), ((MoveStep)steps[0]).ExpectedTarget);

        Assert.Equal(Direction.N, ((MoveStep)steps[1]).Direction);
        Assert.Equal(new RoomKey(1, 5), ((MoveStep)steps[1]).ExpectedTarget);

        Assert.Equal("pull lever", ((CommandStep)steps[2]).Command);

        Assert.Equal(Direction.S, ((MoveStep)steps[3]).Direction);
        Assert.Equal(new RoomKey(1, 2), ((MoveStep)steps[3]).ExpectedTarget);

        MoveStep cross = Assert.IsType<MoveStep>(steps[4]);
        Assert.Equal(Direction.E, cross.Direction);
        Assert.Equal(new RoomKey(1, 9), cross.ExpectedTarget);
        Assert.True(cross.SkipSpecialDispatch);   // prerequisites already emitted
    }

    // Standing in the host room with the gated exit already showing open (its lever
    // was pulled, or "The exit to the east just opened!"): cross it as it stands,
    // no detour to pull the lever again.
    [Fact]
    public void CrossRoomMultiAction_AlreadyOpenAtSource_NoDetour()
    {
        RoomGraphManager graph = NewGraph(RemoteActionGraphJson);
        BfsMapper bfs = new(graph);

        var steps = RemoteActionPathExpander.Expand(
            graph, new RoomKey(1, 2), new[] { Direction.E }, bfs,
            openAtSource: new HashSet<Direction> { Direction.E });

        MoveStep cross = Assert.IsType<MoveStep>(Assert.Single(steps));
        Assert.Equal(Direction.E, cross.Direction);
        Assert.Equal(new RoomKey(1, 9), cross.ExpectedTarget);
    }

    // Crypt-style layout: the gated door is several rooms past a junction, and
    // both levers branch off that junction — not off the door's host room. The
    // approach threads the junction (1/2) on the way to the host (1/4).
    //
    //            1/5  (lever 1 — Action#1 for 1/4's N door)
    //             ▲N
    //   1/1 ─E─▶ 1/2 ─E─▶ 1/3 ─E─▶ 1/4 ─N (Hidden/Needs 2 Actions)─▶ 1/9
    //             │S
    //             ▼
    //            1/6  (lever 2 — Action#2 for 1/4's N door)
    private const string DistantDoorTwoLeverGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/2", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Junction",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/5", "S": "1/6", "E": "1/3", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Corridor",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/4", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "DoorRoom",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/9 (Hidden/Needs 2 Actions, any order)", "S": "0", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Lever1",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0",
            "D": "Action#1 [on the N exit of room 1/4]: pull lever" },
          { "Map Number": 1, "Room Number": 6, "Name": "Lever2",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0",
            "D": "Action#2 [on the N exit of room 1/4]: pull lever" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "1/4" }
        ]
        """;

    [Fact]
    public void CrossRoomMultiAction_AnchorsDetourAtNearestApproachRoom_NotAtDoor()
    {
        // Regression: the levers must be pulled where the approach passes nearest
        // them (the junction 1/2), not after climbing all the way to the door's
        // host room (1/4) and backtracking. The bug walked to the door first,
        // wasting the whole 1/2↔1/4 stretch there and back.
        RoomGraphManager graph = NewGraph(DistantDoorTwoLeverGraphJson);
        BfsMapper bfs = new(graph);

        // Path 1/1 → 1/9 is E, E, E, N (BFS crosses the gated door freely).
        var steps = RemoteActionPathExpander.Expand(
            graph, new RoomKey(1, 1),
            new[] { Direction.E, Direction.E, Direction.E, Direction.N }, bfs);

        // Approach one hop to the junction, pull both levers off it, then finish
        // the approach and cross:
        //   E→1/2, N→1/5, pull, S→1/2, S→1/6, pull, N→1/2, E→1/3, E→1/4, N→1/9
        Assert.Equal(10, steps.Count);

        Assert.Equal(new RoomKey(1, 2), ((MoveStep)steps[0]).ExpectedTarget);
        Assert.Equal(new RoomKey(1, 5), ((MoveStep)steps[1]).ExpectedTarget);
        Assert.Equal("pull lever", ((CommandStep)steps[2]).Command);
        Assert.Equal(new RoomKey(1, 2), ((MoveStep)steps[3]).ExpectedTarget);
        Assert.Equal(new RoomKey(1, 6), ((MoveStep)steps[4]).ExpectedTarget);
        Assert.Equal("pull lever", ((CommandStep)steps[5]).Command);
        Assert.Equal(new RoomKey(1, 2), ((MoveStep)steps[6]).ExpectedTarget);
        Assert.Equal(new RoomKey(1, 3), ((MoveStep)steps[7]).ExpectedTarget);
        Assert.Equal(new RoomKey(1, 4), ((MoveStep)steps[8]).ExpectedTarget);

        MoveStep cross = Assert.IsType<MoveStep>(steps[9]);
        Assert.Equal(Direction.N, cross.Direction);
        Assert.Equal(new RoomKey(1, 9), cross.ExpectedTarget);
        Assert.True(cross.SkipSpecialDispatch);

        // The core invariant: both lever pulls precede the walker's first arrival
        // at the door's host room (1/4), so we never overshoot to the door and
        // come back.
        int lastPull = -1, firstHostArrival = -1;
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i] is CommandStep) lastPull = i;
            if (firstHostArrival < 0
                && steps[i] is MoveStep m && m.ExpectedTarget.Equals(new RoomKey(1, 4)))
                firstHostArrival = i;
        }
        Assert.True(lastPull >= 0 && lastPull < firstHostArrival);
    }

    // Two guardroom levers with IDENTICAL commands both open the same gate, each a
    // bare "Action" (StepNumber 1) and the gate a plain Door with NO "Needs N
    // Actions" modifier — the real Paradigm Chancellor-Annora layout. One pull opens
    // it; the levers are redundant alternatives.
    private const string RedundantLeverDoorGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/2", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Junction",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/5", "S": "1/6", "E": "1/3", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Corridor",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/4", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "DoorRoom",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/9 (Door [301 picklocks/strength])", "S": "0", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Lever1",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0",
            "D": "Action [on the N exit of room 1/4]: pull lever" },
          { "Map Number": 1, "Room Number": 6, "Name": "Lever2",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0",
            "D": "Action [on the N exit of room 1/4]: pull lever" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "1/4" }
        ]
        """;

    [Fact]
    public void RedundantLevers_SameStep_NoCountModifier_PullOnlyOne()
    {
        RoomGraphManager graph = NewGraph(RedundantLeverDoorGraphJson);
        BfsMapper bfs = new(graph);

        var steps = RemoteActionPathExpander.Expand(
            graph, new RoomKey(1, 1),
            new[] { Direction.E, Direction.E, Direction.E, Direction.N }, bfs);

        int pulls = 0;
        foreach (WalkStep s in steps)
            if (s is CommandStep c && c.Command == "pull lever") pulls++;
        Assert.Equal(1, pulls);   // redundant alternatives — one lever, not both

        // The gated exit is still primed and crossed.
        MoveStep cross = Assert.IsType<MoveStep>(steps[^1]);
        Assert.Equal(new RoomKey(1, 9), cross.ExpectedTarget);
        Assert.True(cross.SkipSpecialDispatch);
    }

    [Fact]
    public void CrossRoomMultiAction_NoMapper_FallsBackToSingleStep()
    {
        // Without a mapper the expander can't route the detour — it emits the
        // plain single MoveStep and leaves the send-side dispatcher to report
        // the unsupported cross-room case (the loop-runner path).
        RoomGraphManager graph = NewGraph(RemoteActionGraphJson);

        var steps = RemoteActionPathExpander.Expand(
            graph, new RoomKey(1, 2), new[] { Direction.E });

        MoveStep only = Assert.IsType<MoveStep>(Assert.Single(steps));
        Assert.Equal(Direction.E, only.Direction);
        Assert.False(only.SkipSpecialDispatch);   // not a primed cross
    }

    // Same layout as the cross-room fixture, but the action cell lives in the
    // host room's own D slot ("of this room") — so it's NOT remote.
    private const string SameRoomActionGraphJson = """
        [
          { "Map Number": 1, "Room Number": 2, "Name": "Host",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9 (Hidden/Needs 1 Actions, specific order)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0",
            "D": "Action#1 [on the E exit of this room]: pull lever" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void SameRoomMultiAction_StaysSingleStep_DispatcherOwnsIt()
    {
        // Action lives in the host room ("this room"), so it isn't remote. The
        // expander leaves it as one plain MoveStep — SpecialExitDispatch runs
        // the prerequisite commands at send time. No detour, no CommandStep.
        RoomGraphManager graph = NewGraph(SameRoomActionGraphJson);
        BfsMapper bfs = new(graph);

        var steps = RemoteActionPathExpander.Expand(
            graph, new RoomKey(1, 2), new[] { Direction.E }, bfs);

        MoveStep only = Assert.IsType<MoveStep>(Assert.Single(steps));
        Assert.Equal(Direction.E, only.Direction);
        Assert.Equal(new RoomKey(1, 9), only.ExpectedTarget);
        Assert.False(only.SkipSpecialDispatch);
    }

    [Fact]
    public void CrossRoomMultiAction_UnroutableDetour_TruncatesPath()
    {
        // Sever the host→lever link (drop 1/2's N exit) so the command room is
        // unreachable. The detour can't be built, so expansion truncates before
        // the gated hop — the walker then fails the walk as stale.
        string severed = RemoteActionGraphJson.Replace("\"N\": \"1/5\"", "\"N\": \"0\"");
        RoomGraphManager graph = NewGraph(severed);
        BfsMapper bfs = new(graph);

        var steps = RemoteActionPathExpander.Expand(
            graph, new RoomKey(1, 1),
            new[] { Direction.E, Direction.E }, bfs);

        // Only the first plain hop survives; the multi-action hop is dropped.
        MoveStep only = Assert.IsType<MoveStep>(Assert.Single(steps));
        Assert.Equal(Direction.E, only.Direction);
        Assert.Equal(new RoomKey(1, 2), only.ExpectedTarget);
    }

    // Nested remote action: the lever room for 1/2's gated E exit (1/5) is itself
    // reachable only through 1/2's gated N exit, whose own action lives in 1/6.
    // Reaching the outer lever thus requires first opening an inner remote gate —
    // the expander recurses into it (open 1/6's switch, cross into 1/5, pull the
    // outer lever), so the whole route is solved. This is the 6/878→6/924
    // four-lever-alcove report in miniature.
    //
    //   1/1 ─E─▶ 1/2 ─E (Hidden/Needs 1 Actions)─▶ 1/9   (Action#1 in 1/5)
    //             │N (Hidden/Needs 1 Actions)             (Action#1 in 1/6)
    //             ▼
    //            1/5  (D slot → Action#1 for 1/2's E exit)
    //             │S back to 1/2
    //            1/6  (D slot → Action#1 for 1/2's N exit), reachable 1/2 S
    private const string NestedRemoteActionGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/2", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Host",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/5 (Hidden/Needs 1 Actions, specific order)", "S": "1/6",
            "E": "1/9 (Hidden/Needs 1 Actions, specific order)", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "OuterLever",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0",
            "D": "Action#1 [on the E exit of room 1/2]: pull lever" },
          { "Map Number": 1, "Room Number": 6, "Name": "InnerLever",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0",
            "D": "Action#1 [on the N exit of room 1/2]: pull switch" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void NestedRemoteAction_RecursivelySolved_OpensInnerThenCrossesOuter()
    {
        RoomGraphManager graph = NewGraph(NestedRemoteActionGraphJson);
        BfsMapper bfs = new(graph);

        // BFS routes 1/1 → 1/9 as E, E (it crosses both gated exits freely). To
        // prime 1/2's E exit the walker must reach lever room 1/5, whose only way
        // in crosses 1/2's own gated N exit — so the expander opens that nested
        // gate first (walk to 1/6, pull switch, return), crosses into 1/5, pulls
        // the outer lever, returns, then crosses the primed E exit to 1/9.
        var steps = RemoteActionPathExpander.Expand(
            graph, new RoomKey(1, 1),
            new[] { Direction.E, Direction.E }, bfs);

        Assert.Equal(8, steps.Count);

        Assert.Equal(new RoomKey(1, 2), ((MoveStep)steps[0]).ExpectedTarget);   // approach

        // Inner go-act-return: open 1/2's N gate by pulling the switch in 1/6.
        Assert.Equal(Direction.S, ((MoveStep)steps[1]).Direction);
        Assert.Equal(new RoomKey(1, 6), ((MoveStep)steps[1]).ExpectedTarget);
        Assert.Equal("pull switch", ((CommandStep)steps[2]).Command);
        Assert.Equal(new RoomKey(1, 2), ((MoveStep)steps[3]).ExpectedTarget);   // inner return

        // Cross the now-primed nested exit into the alcove, pull the outer lever.
        MoveStep nestedCross = Assert.IsType<MoveStep>(steps[4]);
        Assert.Equal(Direction.N, nestedCross.Direction);
        Assert.Equal(new RoomKey(1, 5), nestedCross.ExpectedTarget);
        Assert.True(nestedCross.SkipSpecialDispatch);
        Assert.Equal("pull lever", ((CommandStep)steps[5]).Command);

        Assert.Equal(new RoomKey(1, 2), ((MoveStep)steps[6]).ExpectedTarget);   // outer return

        // Finally cross the top-level primed E exit to the destination.
        MoveStep topCross = Assert.IsType<MoveStep>(steps[7]);
        Assert.Equal(Direction.E, topCross.Direction);
        Assert.Equal(new RoomKey(1, 9), topCross.ExpectedTarget);
        Assert.True(topCross.SkipSpecialDispatch);

        Assert.True(RemoteActionPathExpander.ReachesDestination(steps, new RoomKey(1, 9)));

        // Sequence invariant: inner switch (idx 2) pulled before the nested cross
        // (idx 4), which precedes the outer lever (idx 5) — "open alcove, then
        // pull the order-lever," never the reverse.
        int innerPull = -1, nested = -1, outerPull = -1;
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i] is CommandStep { Command: "pull switch" }) innerPull = i;
            if (steps[i] is MoveStep { ExpectedTarget.Room: 5 }) nested = i;
            if (steps[i] is CommandStep { Command: "pull lever" }) outerPull = i;
        }
        Assert.True(innerPull >= 0 && innerPull < nested && nested < outerPull);
    }

    // Build a synthetic linear lever-chain graph with `gates` nested action-gated
    // exits, mirroring the Paradigm treetops vine puzzle's shape: the destination
    // (1/9000) sits behind the top gate, whose lever is in a room reachable only
    // by crossing the next-deeper gate, and so on down to a freely-reached bottom
    // lever. Opening the top exit therefore recurses gates-1 levels deep — it
    // solves when gates-1 <= MaxNestedDepth (i.e. gates <= cap+1) and truncates
    // beyond that. The five-gate case is the actual vine puzzle in miniature.
    //
    // Layout: 1 (start) -E-> spine 2..(gates+1), a plain W/E line. Gate k departs
    // spine room (gates-k+2) upward (U); the top gate (k == gates, at room 2)
    // leads to the vault, every other gate to lever room 500+k+1. Lever room
    // 500+k carries gate k's "pull lever" (plus a plain reverse to its source);
    // the bottom lever (gate 1) sits in the freely-reached room 501.
    private static string LinearLeverChainJson(int gates)
    {
        static string R(int num, string name, params (string Dir, string Val)[] exits)
        {
            string[] order = { "N", "S", "E", "W", "NE", "NW", "SE", "SW", "U", "D" };
            var map = order.ToDictionary(d => d, _ => "0");
            foreach ((string dir, string val) in exits) map[dir] = val;
            string cells = string.Join(", ", order.Select(d => $"\"{d}\": \"{map[d]}\""));
            return $"{{ \"Map Number\": 1, \"Room Number\": {num}, \"Name\": \"{name}\", "
                 + $"\"Light\": 0, \"Shop\": 0, \"Lair\": \"\", \"Delay\": 0, {cells} }}";
        }

        var rooms = new List<string> { R(1, "Start", ("E", "1/2")) };

        for (int r = 2; r <= gates + 1; r++)
        {
            int k = gates - r + 2;   // gate number departing this spine room
            var exits = new List<(string, string)>();
            if (r > 2) exits.Add(("W", $"1/{r - 1}"));
            if (r < gates + 1) exits.Add(("E", $"1/{r + 1}"));
            string gateTarget = k == gates ? "1/9000" : $"1/{500 + k + 1}";
            exits.Add(("U", $"{gateTarget} (Hidden/Needs 1 Actions, any order)"));
            if (k == 1) exits.Add(("D", "1/501"));   // free bottom-lever room
            rooms.Add(R(r, "Spine", exits.ToArray()));
        }

        rooms.Add(R(9000, "Vault", ("W", "1/2")));

        for (int k = 2; k <= gates; k++)
            rooms.Add(R(500 + k, $"Lever{k}",
                ("D", $"1/{gates - k + 3}"),   // reverse to source of gate k-1
                ("N", $"Action#1 [on the U exit of room 1/{gates - k + 2}]: pull lever")));

        rooms.Add(R(501, "Lever1",
            ("U", $"1/{gates + 1}"),
            ("N", $"Action#1 [on the U exit of room 1/{gates + 1}]: pull lever")));

        return "[" + string.Join(",\n", rooms) + "]";
    }

    [Fact]
    public void DeeplyNestedRemoteAction_WithinDepthCap_RecursivelySolved()
    {
        // Five nested gates — the vine puzzle's depth — sit well inside
        // MaxNestedDepth, so the detour is fully built: every gate's lever is
        // pulled and the walk crosses the primed top exit into the vault.
        const int gates = 5;
        RoomGraphManager graph = NewGraph(LinearLeverChainJson(gates));
        BfsMapper bfs = new(graph);

        var steps = RemoteActionPathExpander.Expand(
            graph, new RoomKey(1, 1),
            new[] { Direction.E, Direction.U }, bfs);

        Assert.True(RemoteActionPathExpander.ReachesDestination(steps, new RoomKey(1, 9000)));
        Assert.Equal(gates, steps.Count(s => s is CommandStep { Command: "pull lever" }));

        MoveStep topCross = Assert.IsType<MoveStep>(steps[^1]);
        Assert.Equal(Direction.U, topCross.Direction);
        Assert.Equal(new RoomKey(1, 9000), topCross.ExpectedTarget);
        Assert.True(topCross.SkipSpecialDispatch);
    }

    // Lever-cycle: gate N (→1/30) needs a lever in 1/31, reachable only through
    // gate S; gate S (→1/31) needs a lever in 1/32, reachable only back through
    // gate N. Opening the top E exit recurses N→S→(N again); the second crossing
    // of N is still on the recursion stack, so the visited-set catches the cycle
    // and clean-fails (well before the depth cap). 1/33 holds E's own lever.
    private const string CyclicNestedGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/2", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Hub",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/30 (Hidden/Needs 1 Actions, any order)",
            "S": "1/31 (Hidden/Needs 1 Actions, any order)",
            "E": "1/9 (Hidden/Needs 1 Actions, any order)", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 30, "Name": "BehindN",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "1/33", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0",
            "D": "Action#1 [on the S exit of room 1/2]: pull lever" },
          { "Map Number": 1, "Room Number": 33, "Name": "LeverE",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/30",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0",
            "D": "Action#1 [on the E exit of room 1/2]: pull lever" },
          { "Map Number": 1, "Room Number": 31, "Name": "BehindS",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0",
            "D": "Action#1 [on the N exit of room 1/2]: pull lever" }
        ]
        """;

    [Fact]
    public void CyclicNestedRemoteAction_Truncates()
    {
        RoomGraphManager graph = NewGraph(CyclicNestedGraphJson);
        BfsMapper bfs = new(graph);

        var steps = RemoteActionPathExpander.Expand(
            graph, new RoomKey(1, 1),
            new[] { Direction.E, Direction.E }, bfs);

        // The two gates depend on each other, so the recursion detects the cycle
        // and abandons the detour — only the first plain hop survives.
        MoveStep only = Assert.IsType<MoveStep>(Assert.Single(steps));
        Assert.Equal(new RoomKey(1, 2), only.ExpectedTarget);
        Assert.False(RemoteActionPathExpander.ReachesDestination(steps, new RoomKey(1, 9)));
    }

    [Fact]
    public void DeeplyNestedRemoteAction_ExceedsDepthCap_Truncates()
    {
        // Seventeen nested gates exceed MaxNestedDepth (15): opening the top exit
        // would recurse sixteen levels, so the deepest crossing clean-fails and the
        // whole detour is abandoned — only the first plain hop survives. This is the
        // mis-modelled / runaway guard the cap exists for.
        RoomGraphManager graph = NewGraph(LinearLeverChainJson(17));
        BfsMapper bfs = new(graph);

        var steps = RemoteActionPathExpander.Expand(
            graph, new RoomKey(1, 1),
            new[] { Direction.E, Direction.U }, bfs);

        MoveStep only = Assert.IsType<MoveStep>(Assert.Single(steps));
        Assert.Equal(new RoomKey(1, 2), only.ExpectedTarget);
        Assert.False(RemoteActionPathExpander.ReachesDestination(steps, new RoomKey(1, 9000)));
    }

    [Fact]
    public void ReachesDestination_TrueOnlyWhenLastCrossingTargetsIt()
    {
        var dest = new RoomKey(6, 924);

        // Ends on the primed cross to the destination (a trailing CommandStep or
        // intervening non-move steps don't hide the last crossing).
        WalkStep[] reaching =
        {
            new MoveStep(Direction.E, new RoomKey(6, 877)),
            new CommandStep("pull lever"),
            new MoveStep(Direction.D, dest),
        };
        Assert.True(RemoteActionPathExpander.ReachesDestination(reaching, dest));

        // Truncated short — the last crossing lands elsewhere.
        WalkStep[] truncated =
        {
            new MoveStep(Direction.E, new RoomKey(6, 877)),
            new MoveStep(Direction.N, new RoomKey(6, 876)),
        };
        Assert.False(RemoteActionPathExpander.ReachesDestination(truncated, dest));

        // No crossing at all.
        Assert.False(RemoteActionPathExpander.ReachesDestination(Array.Empty<WalkStep>(), dest));
    }

    [Fact]
    public void UnknownSource_ReturnsEmpty()
    {
        RoomGraphManager graph = NewGraph();
        var steps = RemoteActionPathExpander.Expand(
            graph, new RoomKey(999, 999), new[] { Direction.N });
        Assert.Empty(steps);
    }

    [Fact]
    public void DirectionWithoutExit_StopsExpansion()
    {
        RoomGraphManager graph = NewGraph();

        // 1/1 has no S exit, so nothing expands.
        var steps = RemoteActionPathExpander.Expand(
            graph, new RoomKey(1, 1), new[] { Direction.S });
        Assert.Empty(steps);
    }

    // 1/1 ─E→ 1/2 (lever A) ─E→ 1/3 ─E→ 1/5 (host) ─E (needs A then B)→ 1/9
    //                            1/3 ⇄N 1/4 (lever B);  1/5 ─W→ 1/3
    // Lever A's room has no way back in: the walk passes it, but nothing returns to it.
    private const string OneWayLeverGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "0", "S": "0", "E": "1/2", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "LeverA", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "0", "S": "0", "E": "1/3", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "Action#1 [on the E exit of room 1/5]: pull lever" },
          { "Map Number": 1, "Room Number": 3, "Name": "Mid", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "1/4", "S": "0", "E": "1/5", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "LeverB", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "0", "S": "1/3", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "Action#2 [on the E exit of room 1/5]: turn wheel" },
          { "Map Number": 1, "Room Number": 5, "Name": "Host", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "0", "S": "0", "E": "1/9 (Hidden/Needs 2 Actions, specific order)", "W": "1/3", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "0", "S": "0", "E": "0", "W": "1/5", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Report paradigm-20261003-132757: the first lever room lay on the route but
    // couldn't be walked back to, so no round trip existed and the walk failed.
    [Fact]
    public void LeverRoomWithNoWayBack_IsWorkedOneWayAlongTheRoute()
    {
        RoomGraphManager graph = NewGraph(OneWayLeverGraphJson);
        BfsMapper bfs = new(graph);

        var steps = RemoteActionPathExpander.Expand(
            graph, new RoomKey(1, 1),
            new[] { Direction.E, Direction.E, Direction.E, Direction.E }, bfs);

        string[] walked = steps.Select(s => s switch
        {
            MoveStep m => $"{m.ExpectedTarget.Room}",
            CommandStep c => c.Command,
            _ => "?",
        }).ToArray();
        Assert.Equal(new[] { "2", "pull lever", "3", "4", "turn wheel", "3", "5", "9" }, walked);
        Assert.True(((MoveStep)steps[^1]).SkipSpecialDispatch);
    }

    [Fact]
    public void LeverRoomOutOfReach_NamesTheLegThatCannotBeWalked()
    {
        RoomGraphManager graph = NewGraph(OneWayLeverGraphJson);
        BfsMapper bfs = new(graph);
        List<UnroutableLeverLeg> unroutable = new();

        var steps = RemoteActionPathExpander.Expand(
            graph, new RoomKey(1, 5), new[] { Direction.E }, bfs, unroutable: unroutable);

        Assert.Empty(steps);
        UnroutableLeverLeg leg = Assert.Single(unroutable);
        Assert.Equal(new UnroutableLeverLeg(new RoomKey(1, 5), Direction.E, new RoomKey(1, 5), new RoomKey(1, 2)), leg);
    }
}
