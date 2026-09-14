using System;
using System.Collections.Generic;
using System.IO;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Rooms holding a level gate the character can't pass — the room you can still
// walk into whose way onward is shut. Depends on the level and nothing else.
public sealed class LevelGatedRoomsTests : IDisposable
{
    private readonly string _root;

    public LevelGatedRoomsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-levelgated-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // 1/1 Road — D (levels 1-3) → 1/2 Arena → 1/3 Dungeon. 1/1 also has an
    // ungated E → 1/4, so the gate is one exit of a room with two.
    private const string ArenaishJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Road",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/4", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "1/2 (Level: 1 to 3)" },
          { "Map Number": 1, "Room Number": 2, "Name": "Arena",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "1/1", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Dungeon",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Open Field",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private RoomGraphManager NewGraph(string json = ArenaishJson)
    {
        string dir = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Rooms.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return graph;
    }

    // The room holding the gate is marked — and only it. The rooms beyond hold
    // no gate of their own, so nothing marks them.
    [Fact]
    public void OverLevel_MarksTheRoomHoldingTheGate()
    {
        RoomGraphManager graph = NewGraph();

        IReadOnlySet<RoomKey> gated = LevelGatedRooms.Compute(graph, level: 40);

        Assert.Equal(new HashSet<RoomKey> { new(1, 1) }, new HashSet<RoomKey>(gated));
    }


    // A gate is a property of the room, so the mark depends on the level and
    // nothing else. There is no standpoint to compute it from and no current
    // room to wait for.
    [Fact]
    public void TheAnswerDoesNotDependOnWhereWeStand()
    {
        RoomGraphManager graph = NewGraph();

        Assert.Contains(new RoomKey(1, 1), LevelGatedRooms.Compute(graph, level: 40));
    }

    // Inside the window the gate admits us, so nothing is marked.
    [Fact]
    public void WithinTheWindow_NothingIsMarked()
    {
        RoomGraphManager graph = NewGraph();

        Assert.Empty(LevelGatedRooms.Compute(graph, level: 2));
    }

    // A floor shuts out the under-level character the same way a cap shuts out
    // the over-level one.
    [Fact]
    public void UnderLevel_IsMarkedByAMinimumFloor()
    {
        RoomGraphManager graph =
            NewGraph(ArenaishJson.Replace("(Level: 1 to 3)", "(Level: 30 to 0)"));

        Assert.Contains(new RoomKey(1, 1), LevelGatedRooms.Compute(graph, level: 5));
    }

    // Unknown level never gates, mirroring MovementFilter's rule.
    [Fact]
    public void UnknownLevel_MarksNothing()
    {
        RoomGraphManager graph = NewGraph();

        Assert.Empty(LevelGatedRooms.Compute(graph, level: 0));
    }

    // Nothing but level enters into it. A room whose only obstacle is a door
    // nobody can open is not level-gated — that belongs to some other overlay.
    [Fact]
    public void ImpossibleDoor_IsNotALevelGate()
    {
        RoomGraphManager graph = NewGraph(
            ArenaishJson.Replace("1/2 (Level: 1 to 3)", "1/2 (Door [1000 picklocks/strength])"));

        Assert.Empty(LevelGatedRooms.Compute(graph, level: 40));
    }
}
