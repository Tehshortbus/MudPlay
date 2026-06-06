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
