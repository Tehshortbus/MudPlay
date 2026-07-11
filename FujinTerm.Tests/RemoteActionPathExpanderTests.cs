using System.IO;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class RemoteActionPathExpanderTests : IDisposable
{
    private readonly string _root;

    public RemoteActionPathExpanderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-expander-tests-" + Path.GetRandomFileName());
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

        // 1/2 doesn't have S, so expansion stops there.
        var steps = RemoteActionPathExpander.Expand(
            graph, new RoomKey(1, 2),
            new[] { Direction.N, Direction.S });

        // First step N → 1/3. Then trying S from 1/3 — 1/3.S = 1/2,
        // valid. So both should expand.
        Assert.Equal(2, steps.Count);

        // Now try a truly broken sequence — S from 1/1 (no S).
        steps = RemoteActionPathExpander.Expand(
            graph, new RoomKey(1, 1), new[] { Direction.S });
        Assert.Empty(steps);
    }

}
