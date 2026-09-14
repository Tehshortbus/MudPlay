using System;
using System.Collections.Generic;
using System.IO;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The map's level-blocked overlay: which rooms this character's level shuts them
// out of. Modelled on Newhaven's Arena — one gated way in, with rooms beyond it
// that have no other entrance, so the gate seals a pocket rather than a room.
public sealed class LevelBlockedRoomsTests : IDisposable
{
    private readonly string _root;

    public LevelBlockedRoomsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-levelblocked-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // 1/1 Road — D (levels 1-3) → 1/2 Arena — N → 1/3 Dungeon — N → 1/4 Cavern.
    // 1/1 also has E → 1/5, an ungated room that must never be flagged.
    private const string ArenaishJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Road",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/5", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "1/2 (Level: 1 to 3)" },
          { "Map Number": 1, "Room Number": 2, "Name": "Arena",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "1/1", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Dungeon",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "1/4", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Cavern",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/3", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Open Field",
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

    // Over-level: the gate seals the Arena AND everything only reachable through
    // it — the whole pocket, not just the gated room.
    [Fact]
    public void OverLevel_SealsTheWholePocketBehindTheGate()
    {
        RoomGraphManager graph = NewGraph();

        IReadOnlySet<RoomKey> blocked =
            LevelBlockedRooms.Compute(graph, filter: null, new RoomKey(1, 1), level: 40);

        Assert.Equal(
            new HashSet<RoomKey> { new(1, 2), new(1, 3), new(1, 4) },
            new HashSet<RoomKey>(blocked));
    }

    // In-range: nothing is blocked, including the gated room itself.
    [Fact]
    public void WithinTheWindow_NothingIsBlocked()
    {
        RoomGraphManager graph = NewGraph();

        Assert.Empty(LevelBlockedRooms.Compute(graph, filter: null, new RoomKey(1, 1), level: 2));
    }

    // An ungated neighbour is never flagged — the overlay must not paint rooms
    // that are simply elsewhere.
    [Fact]
    public void UngatedNeighbour_IsNeverBlocked()
    {
        RoomGraphManager graph = NewGraph();

        IReadOnlySet<RoomKey> blocked =
            LevelBlockedRooms.Compute(graph, filter: null, new RoomKey(1, 1), level: 40);

        Assert.DoesNotContain(new RoomKey(1, 5), blocked);
        Assert.DoesNotContain(new RoomKey(1, 1), blocked);
    }

    // The gate is tested on ENTRY, so standing inside the pocket while over-level
    // leaves nothing blocked — the way out is not gated.
    [Fact]
    public void StandingInsideThePocket_IsNotBlockedFromIt()
    {
        RoomGraphManager graph = NewGraph();

        Assert.Empty(LevelBlockedRooms.Compute(graph, filter: null, new RoomKey(1, 2), level: 40));
    }

    // Unknown level never gates, mirroring MovementFilter's rule.
    [Fact]
    public void UnknownLevel_BlocksNothing()
    {
        RoomGraphManager graph = NewGraph();

        Assert.Empty(LevelBlockedRooms.Compute(graph, filter: null, new RoomKey(1, 1), level: 0));
        Assert.Empty(LevelBlockedRooms.Compute(graph, filter: null, origin: null, level: 40));
    }

    // A MinLevel floor gates the under-level character the same way a cap gates
    // an over-level one.
    [Fact]
    public void UnderLevel_IsBlockedByAMinimumFloor()
    {
        RoomGraphManager graph =
            NewGraph(ArenaishJson.Replace("(Level: 1 to 3)", "(Level: 30 to 0)"));

        IReadOnlySet<RoomKey> blocked =
            LevelBlockedRooms.Compute(graph, filter: null, new RoomKey(1, 1), level: 5);

        Assert.Contains(new RoomKey(1, 2), blocked);
        Assert.Contains(new RoomKey(1, 4), blocked);
    }

    // The Ancient Fortress shape (Paradigm): a pocket behind a level gate whose
    // only OTHER way in is a door nobody can open. Without the filter the sweep
    // walks that door, decides the pocket is already reachable, and paints
    // nothing — the whole pocket silently goes unmarked for a character who
    // definitely cannot enter it.
    //
    // 1/5 Open Field — E (impassable door) → 1/4 Cavern, which sits inside the
    // pocket behind the 1/1 D level gate.
    private const string BackDoorJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Road",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/5", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "1/2 (Level: 30 to 0)" },
          { "Map Number": 1, "Room Number": 2, "Name": "Arena",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "1/1", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Dungeon",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "1/4", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Cavern",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/3", "E": "0", "W": "1/5 (Door [1000 picklocks/strength])",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Open Field",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/4 (Door [1000 picklocks/strength])", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void PocketBehindAnImpassableBackDoor_IsStillBlockedByLevel()
    {
        RoomGraphManager graph = NewGraph(BackDoorJson);

        // Sanity: the back door really is in the fixture and really does reach
        // the pocket, so a green result can't come from the route being absent.
        Assert.True(graph.GetRoom(new RoomKey(1, 5))!.Exits[Direction.E].Target
                        .Equals(new RoomKey(1, 4)),
            "fixture no longer wires the back door into the pocket");

        // Level-only (no filter) walks the back door and paints nothing — this is
        // the bug, pinned so the fallback's limits stay visible.
        Assert.Empty(LevelBlockedRooms.Compute(graph, filter: null, new RoomKey(1, 1), level: 5));

        // With the router's filter, the impassable door is a wall in BOTH passes,
        // so the level gate is the only way in and the pocket paints.
        IReadOnlySet<RoomKey> blocked = LevelBlockedRooms.Compute(
            graph, new ImpassableDoorFilter(), new RoomKey(1, 1), level: 5);

        Assert.Contains(new RoomKey(1, 2), blocked);
        Assert.Contains(new RoomKey(1, 3), blocked);
        Assert.Contains(new RoomKey(1, 4), blocked);
        Assert.DoesNotContain(new RoomKey(1, 5), blocked);
    }

    // A non-level obstacle is applied to BOTH passes, so it cancels out and can
    // never paint a room on its own — only the level gate moves a room into the
    // set.
    [Fact]
    public void AnImpassableDoorAloneNeverPaintsARoom()
    {
        RoomGraphManager graph =
            NewGraph(BackDoorJson.Replace("(Level: 30 to 0)", ""));

        Assert.Empty(LevelBlockedRooms.Compute(
            graph, new ImpassableDoorFilter(), new RoomKey(1, 1), level: 5));
    }

    // Stands in for MovementFilter's door achievability: the MDB's
    // 1000-picklocks sentinel is a wall no build can pass.
    private sealed class ImpassableDoorFilter : IRoomFilter
    {
        public bool IsAvoided(RoomKey key) => false;

        public ExitBlockReason DescribeExitBlock(in RoomExit exit)
        {
            ExitBlockReason reasons = ExitBlockReason.None;
            if (exit.StatRequirement >= 1000) reasons |= ExitBlockReason.Door;
            if (exit.ExcludesLevel(5)) reasons |= ExitBlockReason.Level;
            return reasons;
        }

        public bool IsExitBlocked(in RoomExit exit)
            => DescribeExitBlock(in exit) != ExitBlockReason.None;
    }
}
