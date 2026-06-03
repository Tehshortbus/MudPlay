using System.Collections.Generic;
using System.IO;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class AutoLairManagerTests : IDisposable
{
    private readonly string _root;

    public AutoLairManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-autolair-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // 1/1 ↔ 1/2 ↔ 1/3 linear strip.
    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "C",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private sealed class Harness : IDisposable
    {
        public required RoomTracker Tracker { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public required AutoLairManager Roam { get; init; }
        public void Dispose() => Roam.Dispose();
    }

    private Harness NewHarness()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        AutoWalkManager walker = new(graph, bfs, tracker, coord);
        walker.SetWireSender(_ => { });
        AutoLairManager roam = new(walker, tracker);
        return new Harness { Tracker = tracker, Walker = walker, Roam = roam };
    }

    [Fact]
    public void Fresh_NotActive_NoMarks()
    {
        using Harness h = NewHarness();
        Assert.False(h.Roam.IsActive);
        Assert.Empty(h.Roam.Marked);
    }

    [Fact]
    public void Mark_AddsAndFiresChanged()
    {
        using Harness h = NewHarness();
        int fires = 0;
        h.Roam.MarkedChanged += () => fires++;

        h.Roam.Mark(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 2));

        Assert.Equal(2, h.Roam.Marked.Count);
        Assert.Equal(2, fires);
    }

    [Fact]
    public void Mark_Idempotent_DoesNotRefire()
    {
        using Harness h = NewHarness();
        int fires = 0;
        h.Roam.MarkedChanged += () => fires++;
        h.Roam.Mark(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 1));
        Assert.Equal(1, fires);
    }

    [Fact]
    public void Toggle_AddsThenRemoves()
    {
        using Harness h = NewHarness();
        h.Roam.Toggle(new RoomKey(1, 1));
        Assert.True(h.Roam.IsMarked(new RoomKey(1, 1)));
        h.Roam.Toggle(new RoomKey(1, 1));
        Assert.False(h.Roam.IsMarked(new RoomKey(1, 1)));
    }

    [Fact]
    public void Start_FewerThanTwoMarks_RefusesToStart()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 1));

        Assert.False(h.Roam.Start());
        Assert.False(h.Roam.IsActive);
    }

    [Fact]
    public void Start_NoCurrentRoom_RefusesToStart()
    {
        using Harness h = NewHarness();
        h.Roam.Mark(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 3));

        Assert.False(h.Roam.Start());
        Assert.False(h.Roam.IsActive);
    }

    [Fact]
    public void Start_DispatchesAWalkLeg()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 3));

        Assert.True(h.Roam.Start());
        Assert.True(h.Roam.IsActive);

        // Walker should be heading to a marked room other than current.
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);
    }

    [Fact]
    public void OnLegFinished_DispatchesNextLeg()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 3));
        h.Roam.Start();

        // Confirm leg arrival: tracker says we're at 1/3 now.
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        h.Tracker.NoteRoomObserved(new RoomObservation("C",
            new HashSet<Direction> { Direction.S }));

        // Walker should pick the only other marked room (1/1).
        Assert.True(h.Roam.IsActive);
        Assert.Equal(new RoomKey(1, 1), h.Walker.Destination);
    }

    [Fact]
    public void Stop_DeactivatesAndCancelsWalker()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 3));
        h.Roam.Start();
        Assert.True(h.Roam.IsActive);

        h.Roam.Stop();

        Assert.False(h.Roam.IsActive);
        Assert.Equal(WalkState.Idle, h.Walker.State);
    }

    [Fact]
    public void ActiveChanged_FiresOnStartAndStop()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 3));

        var events = new List<bool>();
        h.Roam.ActiveChanged += b => events.Add(b);

        h.Roam.Start();
        h.Roam.Stop();

        Assert.Equal(new[] { true, false }, events);
    }
}
