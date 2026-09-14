using System;
using System.Collections.Generic;
using System.IO;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Rooms holding a level gate — a property of the MAP, not of the character, so
// the set takes no level and no position and reads the same however the player
// is placed (including not connected at all).
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

    private RoomGraphManager NewGraph(string rooms = ArenaishJson, string? tbinfo = null)
    {
        string dir = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Rooms.json"), rooms);
        if (tbinfo is not null) File.WriteAllText(Path.Combine(dir, "TBInfo.json"), tbinfo);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");

        // The graph only mints CMD-teleport edges when it has a TBInfo store —
        // without one a `minlevel` teleport never reaches an exit at all.
        TBInfoStore? store = null;
        if (tbinfo is not null)
        {
            store = new TBInfoStore(cache);
            store.OnActiveSetChanged("alpha");
        }
        RoomGraphManager graph = new(cache, log: null, store);
        graph.OnActiveSetChanged("alpha");
        return graph;
    }

    // The room holding the gate is marked — and only it. The rooms beyond hold
    // no gate of their own, so nothing marks them.
    [Fact]
    public void GatedExit_MarksOnlyTheRoomHoldingIt()
    {
        RoomGraphManager graph = NewGraph();

        Assert.Equal(
            new HashSet<RoomKey> { new(1, 1) },
            new HashSet<RoomKey>(LevelGatedRooms.Compute(graph)));
    }

    // Any level window marks, whichever end it bounds — a floor, a cap, or both.
    // The overlay says "there is a gate here", not "this gate refuses you".
    [Theory]
    [InlineData("(Level: 1 to 3)")]     // cap only
    [InlineData("(Level: 30 to 0)")]    // floor only
    [InlineData("(Level: 10 to 25)")]   // both ends
    public void AnyLevelWindow_MarksItsRoom(string window)
    {
        RoomGraphManager graph = NewGraph(ArenaishJson.Replace("(Level: 1 to 3)", window));

        Assert.Contains(new RoomKey(1, 1), LevelGatedRooms.Compute(graph));
    }

    // No level gate anywhere, nothing marked — the overlay must not paint rooms
    // that merely have exits.
    [Fact]
    public void NoLevelGate_MarksNothing()
    {
        RoomGraphManager graph = NewGraph(ArenaishJson.Replace(" (Level: 1 to 3)", ""));

        Assert.Empty(LevelGatedRooms.Compute(graph));
    }

    // Nothing but level enters into it. A room whose only obstacle is a door
    // nobody can open is not level-gated and is not marked here.
    [Fact]
    public void ImpossibleDoor_IsNotALevelGate()
    {
        RoomGraphManager graph = NewGraph(
            ArenaishJson.Replace("1/2 (Level: 1 to 3)", "1/2 (Door [1000 picklocks/strength])"));

        Assert.Empty(LevelGatedRooms.Compute(graph));
    }

    // The reported miss: stock 3/756's "go vortex" is a CMD teleport whose
    // minlevel reaches the graph as a synthesised Teleport edge rather than as
    // an exit modifier. The tooltip surfaced "Level 20+" while the room itself
    // went unmarked.
    [Fact]
    public void CmdTeleportWithMinLevel_MarksItsRoom()
    {
        const string rooms = """
            [
              { "Map Number": 3, "Room Number": 756, "Name": "Black Wasteland",
                "Light": 0, "Shop": 0, "NPC": 0, "CMD": 9001, "Lair": "", "Delay": 0,
                "N": "3/757", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "3/755", "U": "0", "D": "0" },
              { "Map Number": 3, "Room Number": 757, "Name": "Black Wasteland",
                "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "3/756", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 3, "Room Number": 755, "Name": "Black Wasteland",
                "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "3/756", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 3, "Room Number": 782, "Name": "Hazy Swamp",
                "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        const string tbinfo = """
            [ { "Number": 9001, "LinkTo": 0,
                "Action": "go vortex:minlevel 20 1220:message 1205:teleport 782 3:message 1221\n",
                "Called From": "Room 3/756" } ]
            """;
        RoomGraphManager graph = NewGraph(rooms, tbinfo);

        // Guard the fixture: if the edge stopped being synthesised the assert
        // below could pass for the wrong reason.
        Assert.True(graph.GetRoom(new RoomKey(3, 756))!.Exits
                        .TryGetValue(Direction.Teleport, out RoomExit tp) && tp.MinLevel == 20,
            "fixture no longer produces a level-gated teleport edge");

        IReadOnlySet<RoomKey> gated = LevelGatedRooms.Compute(graph);

        Assert.Contains(new RoomKey(3, 756), gated);
        Assert.DoesNotContain(new RoomKey(3, 782), gated);
    }
}
