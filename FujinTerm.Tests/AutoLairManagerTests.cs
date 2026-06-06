using System.Collections.Generic;
using System.IO;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 7.19 — AutoLairManager session control (Mark / Unmark / Override /
/// Start / Stop) + integration with the scheduler. Headless: drives the
/// graph + tracker directly, no Telnet socket. State-machine progression
/// past Approaching needs DispatcherTimer ticks, which xUnit doesn't
/// pump — those transitions are exercised in the scheduler unit tests
/// (PR 7.19 first-half) instead. Here we pin the marker store, the
/// Start refusal gates, and the initial Approaching dispatch.
/// </summary>
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

    // 1/1 ↔ 1/2 ↔ 1/3 linear strip. 1/3 carries a lair tag so the
    // timer store has something to resolve against; 1/1 stays a plain
    // room used as the start-position + alternate marker.
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
            "Light": 0, "Shop": 0, "Lair": "[1-1-1][1]Group(lair): 1/3", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private const string LairsJson = """
        [ { "GroupIndex": "1-1-1", "AvgDelay": 5 } ]
        """;

    private sealed class Harness : IDisposable
    {
        public required RoomTracker Tracker { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public required AutoLairManager Roam { get; init; }
        public required LairTimerStore Timers { get; init; }
        public void Dispose()
        {
            Roam.Dispose();
            Timers.Dispose();
        }
    }

    private Harness NewHarness()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        File.WriteAllText(Path.Combine(_root, "alpha", "Lairs.json"), LairsJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        AutoWalkManager walker = new(graph, bfs, tracker, coord);
        walker.SetWireSender(_ => { });
        LairTimerStore timers = new(cache, graph, tracker);
        AutoLairManager roam = new(walker, tracker, graph, bfs, timers);
        return new Harness
        {
            Tracker = tracker,
            Walker = walker,
            Roam = roam,
            Timers = timers,
        };
    }

    // ----- marker CRUD ----------------------------------------------

    [Fact]
    public void Fresh_NotActive_NoMarks()
    {
        using Harness h = NewHarness();
        Assert.Equal(AutoLairPhase.Idle, h.Roam.Phase);
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
    public void Mark_WithOverride_PersistsAndRetrievable()
    {
        using Harness h = NewHarness();
        h.Roam.Mark(new RoomKey(1, 3), overrideRespawnSeconds: 120);

        Assert.True(h.Roam.IsMarked(new RoomKey(1, 3)));
        Assert.Equal(120, h.Roam.GetOverride(new RoomKey(1, 3)));
    }

    [Fact]
    public void SetOverride_OnExistingMarker_Updates()
    {
        using Harness h = NewHarness();
        h.Roam.Mark(new RoomKey(1, 3));
        h.Roam.SetOverride(new RoomKey(1, 3), 999);

        Assert.Equal(999, h.Roam.GetOverride(new RoomKey(1, 3)));
    }

    [Fact]
    public void SetOverride_OnUnknownKey_NoOp()
    {
        using Harness h = NewHarness();
        h.Roam.SetOverride(new RoomKey(9, 9), 100);

        Assert.False(h.Roam.IsMarked(new RoomKey(9, 9)));
        Assert.Null(h.Roam.GetOverride(new RoomKey(9, 9)));
    }

    [Fact]
    public void Clear_RemovesAllMarkers()
    {
        using Harness h = NewHarness();
        h.Roam.Mark(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 3));
        int fires = 0;
        h.Roam.MarkedChanged += () => fires++;

        h.Roam.Clear();

        Assert.Empty(h.Roam.Marked);
        Assert.Equal(1, fires);
    }

    // ----- Start gates ----------------------------------------------

    [Fact]
    public void Start_FewerThanTwoMarks_Refuses()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 1));

        Assert.False(h.Roam.Start());
        Assert.False(h.Roam.IsActive);
    }

    [Fact]
    public void Start_NoCurrentRoom_Refuses()
    {
        using Harness h = NewHarness();
        h.Roam.Mark(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 3));

        Assert.False(h.Roam.Start());
        Assert.False(h.Roam.IsActive);
    }

    // ----- Start dispatch ------------------------------------------

    [Fact]
    public void Start_DispatchesToWaitRoomNotLair()
    {
        // Self-cycle behaviour: player is standing in 1/1, which is
        // itself a marker. The scheduler picks 1/1 as the next target
        // and steps out to 1/2 (a non-marker neighbour) as the wait-
        // room so the respawn check can re-fire on re-entry. From
        // there the walker takes one hop south back into 1/1.
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 3));

        Assert.True(h.Roam.Start());
        Assert.True(h.Roam.IsActive);
        Assert.Equal(AutoLairPhase.Approaching, h.Roam.Phase);
        // Target is the self-lair (1/1); wait-room is the one-hop
        // non-marker neighbour (1/2). Walker heads for 1/2.
        Assert.Equal(new RoomKey(1, 1), h.Roam.CurrentTarget);
        Assert.Equal(new RoomKey(1, 2), h.Roam.CurrentWaitRoom);
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Equal(new RoomKey(1, 2), h.Walker.Destination);
    }

    [Fact]
    public void Start_DecisionRecordedForBottomStrip()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 3));

        h.Roam.Start();

        Assert.NotNull(h.Roam.LastDecision);
        // See Start_DispatchesToWaitRoomNotLair — self-cycle wins
        // when current room is itself a marker.
        Assert.Equal(new RoomKey(1, 1), h.Roam.LastDecision!.Lair);
        Assert.Equal(new RoomKey(1, 2), h.Roam.LastDecision.WaitRoom);
    }

    // ----- Stop -----------------------------------------------------

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
        Assert.Equal(AutoLairPhase.Idle, h.Roam.Phase);
        Assert.Null(h.Roam.CurrentTarget);
        Assert.Null(h.Roam.CurrentWaitRoom);
        Assert.Null(h.Roam.LastDecision);
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

    [Fact]
    public void PhaseChanged_FiresOnStart()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 3));

        var phases = new List<AutoLairPhase>();
        h.Roam.PhaseChanged += p => phases.Add(p);

        h.Roam.Start();

        Assert.Contains(AutoLairPhase.Approaching, phases);
    }

    [Fact]
    public void Stop_ClearsEntryArrivalLatch()
    {
        // CurrentEntryArrivalAt is part of the public surface so the
        // CURRENT NAV countdown can render against it. Stop() must
        // wipe the latch alongside the rest of the runtime state so
        // a stale value doesn't leak into the idle render.
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 3));

        h.Roam.Start();
        h.Roam.Stop();

        Assert.Null(h.Roam.CurrentEntryArrivalAt);
        Assert.Null(h.Roam.CurrentTarget);
        Assert.Null(h.Roam.CurrentWaitRoom);
        Assert.Null(h.Roam.LastDecision);
    }
}
