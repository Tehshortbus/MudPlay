using System.IO;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 7.21 — the Room / RoomGraphManager surface the MapControl + the
/// PR 7.22 walker integration need to recognise trapped exits.
/// </summary>
public sealed class TrapOverlayTests : IDisposable
{
    private readonly string _root;

    public TrapOverlayTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-trap-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Safe Hall",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2 (Trap)", "S": "0", "E": "1/3", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Pit Room",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Clean Hall",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Multi-Trap",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/5 (Trap)", "S": "1/6 (Trap)", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private RoomGraphManager NewGraph()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return graph;
    }

    [Fact]
    public void HasTrappedExits_TrueWhenAnyExitIsTrap()
    {
        RoomGraphManager graph = NewGraph();
        Room safe = graph.GetRoom(new RoomKey(1, 1))!;
        Assert.True(safe.HasTrappedExits);
    }

    [Fact]
    public void HasTrappedExits_FalseWhenNoneFlagged()
    {
        RoomGraphManager graph = NewGraph();
        Room clean = graph.GetRoom(new RoomKey(1, 3))!;
        Assert.False(clean.HasTrappedExits);
    }

    [Fact]
    public void TrappedDirections_ReturnsOnlyFlaggedDirections()
    {
        RoomGraphManager graph = NewGraph();
        Room safe = graph.GetRoom(new RoomKey(1, 1))!;

        var dirs = safe.TrappedDirections;

        Assert.Single(dirs);
        Assert.Equal(Direction.N, dirs[0]);
        // The E exit isn't trapped.
        Assert.DoesNotContain(Direction.E, dirs);
    }

    [Fact]
    public void TrappedDirections_HandlesMultipleTrappedExits()
    {
        RoomGraphManager graph = NewGraph();
        Room multi = graph.GetRoom(new RoomKey(1, 4))!;

        var dirs = multi.TrappedDirections;

        Assert.Equal(2, dirs.Count);
        Assert.Contains(Direction.N, dirs);
        Assert.Contains(Direction.S, dirs);
    }

    [Fact]
    public void TrappedDirections_EmptyWhenNoTraps()
    {
        RoomGraphManager graph = NewGraph();
        Room clean = graph.GetRoom(new RoomKey(1, 3))!;
        Assert.Empty(clean.TrappedDirections);
    }

    [Fact]
    public void GraphTrappedRooms_EnumeratesOnlyRoomsWithTraps()
    {
        RoomGraphManager graph = NewGraph();
        var trapped = graph.TrappedRooms.Select(r => r.Key).ToHashSet();

        Assert.Contains(new RoomKey(1, 1), trapped);
        Assert.Contains(new RoomKey(1, 4), trapped);
        Assert.DoesNotContain(new RoomKey(1, 2), trapped);
        Assert.DoesNotContain(new RoomKey(1, 3), trapped);
    }
}
