using System.Collections.Generic;
using System.IO;
using System.Linq;
using FujinTerm.Game.Events;
using FujinTerm.Game.Map;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Coverage for the walk-to auto-resume flow in
/// <see cref="EventManager"/> — snapshot precedence, the OnResume
/// walker-event handler's three outcomes (Finished / Failed /
/// Stopped), and the cascade case where one event-walk fires while
/// another's still in flight. Uses a real engine fixture (graph +
/// BFS + tracker + walker + loop runner + auto-lair) because the
/// resume contract spans all of them.
/// </summary>
public sealed class EventManagerResumeTests : IDisposable
{
    // BBS name LoopManager.Save writes under. AppPaths roots are cached
    // at static-init, so saved loops land in the real Data/BBS tree; the
    // "test-" prefix lets TestSessionCleanup's module sweep reclaim the
    // folder, and Dispose removes it eagerly on a clean run.
    private const string TestBbs = "test-event-resume";

    private readonly string _root;

    public EventManagerResumeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-event-resume-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
        try { Directory.Delete(AppPaths.BbsFolder(TestBbs), recursive: true); }
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
        public required LoopRunner Runner { get; init; }
        public required AutoLairManager AutoLair { get; init; }
        public required LoopManager Loops { get; init; }
        public required LairManager Lairs { get; init; }
        public required EventManager Events { get; init; }
        public required LairTimerStore Timers { get; init; }

        public void Dispose()
        {
            AutoLair.Dispose();
            Timers.Dispose();
        }
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
        LoopRunner runner = new(tracker, coord, graph: graph, bfs: bfs);
        runner.SetWireSender(_ => { });
        LairTimerStore timers = new(cache, graph, tracker);
        AutoLairManager autoLair = new(walker, tracker, graph, bfs, timers);
        LoopManager loops = new(bfs, graph);
        // LoopManager.Save no-ops unless a BBS context is set. LoadAll
        // binds the catalogue to TestBbs (no folder yet, so it seeds an
        // empty collection); the cascade tests that call Save then write
        // a .loop file under AppPaths.BbsFolder(TestBbs), which Dispose
        // reclaims.
        loops.LoadAll(TestBbs);
        LairManager lairs = new();

        // EventManager via its full-engine ctor — parameterless ctor
        // leaves the engines null and the resume code can't exercise.
        ProfileService profile = new();
        profile.LoadBlank();
        EventManager events = new(profile, loops, lairs, runner, autoLair, walker);

        return new Harness
        {
            Tracker = tracker, Walker = walker, Runner = runner, AutoLair = autoLair,
            Loops = loops, Lairs = lairs, Events = events, Timers = timers,
        };
    }

    private static ScheduledEvent WalkToEvent(int map, int room) => new()
    {
        Name = $"walk-{map}-{room}",
        TriggerType = EventTriggerType.Logon,
        ActionType = EventActionType.WalkTo,
        WalkToTarget = new RoomRef(map, room),
    };

    // ----- Snapshot precedence ---------------------------------------

    [Fact]
    public void Snapshot_NothingRunning_ReturnsNull()
    {
        using Harness h = NewHarness();
        Assert.Null(h.Events.SnapshotCurrentActivity());
    }

    [Fact]
    public void Snapshot_WalkerOnly_ReturnsWalkerPlan()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));  // give walker a current room.
        h.Walker.WalkTo(new RoomKey(1, 3));

        EventManager.EventResumePlan? plan = h.Events.SnapshotCurrentActivity();
        EventManager.EventResumePlan.Walker walker =
            Assert.IsType<EventManager.EventResumePlan.Walker>(plan);
        Assert.Equal(new RoomKey(1, 3), walker.Destination);
    }

    [Fact]
    public void Snapshot_LoopRunning_BeatsWalkerLeg()
    {
        // A running loop drives the walker for its approach leg. The
        // snapshot should pick the loop, not the walker leg under it.
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Loop loop = new("ab", new[] { new RoomKey(1, 1), new RoomKey(1, 2) });
        h.Runner.Start(loop);

        EventManager.EventResumePlan? plan = h.Events.SnapshotCurrentActivity();
        EventManager.EventResumePlan.Loop loopPlan =
            Assert.IsType<EventManager.EventResumePlan.Loop>(plan);
        Assert.Same(loop, loopPlan.SavedLoop);
    }

    [Fact]
    public void Snapshot_AutoLairActive_BeatsLoopAndWalker()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.AutoLair.Mark(new RoomKey(1, 2));
        h.AutoLair.Mark(new RoomKey(1, 3));
        Assert.True(h.AutoLair.Start());

        EventManager.EventResumePlan? plan = h.Events.SnapshotCurrentActivity();
        EventManager.EventResumePlan.AutoLair lairPlan =
            Assert.IsType<EventManager.EventResumePlan.AutoLair>(plan);
        Assert.Equal(2, lairPlan.Markers.Count);
        Assert.Contains(new RoomKey(1, 2), lairPlan.Markers.Keys);
        Assert.Contains(new RoomKey(1, 3), lairPlan.Markers.Keys);
    }

    // ----- Walker-event handler outcomes -----------------------------

    [Fact]
    public void OnResume_Finished_DispatchesWalkerResume()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Events.PendingResumeForTests =
            new EventManager.EventResumePlan.Walker(new RoomKey(1, 3));

        h.Events.OnResumeWalkEvent(new WalkEvent(WalkEventKind.Finished, "done", null));

        // The resume should have called walker.WalkTo(1/3); the walker
        // accepts it (current room is 1/1, valid path), so its state
        // moves to Walking with Destination=1/3.
        Assert.Null(h.Events.PendingResumeForTests);  // plan cleared.
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);
    }

    [Fact]
    public void OnResume_Finished_DispatchesLoopResume()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Loop loop = new("ab", new[] { new RoomKey(1, 1), new RoomKey(1, 2) });
        h.Events.PendingResumeForTests = new EventManager.EventResumePlan.Loop(loop);

        h.Events.OnResumeWalkEvent(new WalkEvent(WalkEventKind.Finished, "done", null));

        Assert.Null(h.Events.PendingResumeForTests);
        Assert.NotEqual(LoopState.Idle, h.Runner.State);
    }

    [Fact]
    public void OnResume_Finished_DispatchesAutoLairResume()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Dictionary<RoomKey, int?> markers = new()
        {
            [new RoomKey(1, 2)] = null,
            [new RoomKey(1, 3)] = 120,
        };
        h.Events.PendingResumeForTests = new EventManager.EventResumePlan.AutoLair(markers);

        h.Events.OnResumeWalkEvent(new WalkEvent(WalkEventKind.Finished, "done", null));

        Assert.Null(h.Events.PendingResumeForTests);
        Assert.True(h.AutoLair.IsActive);
        Assert.Equal(2, h.AutoLair.Marked.Count);
    }

    [Fact]
    public void OnResume_Failed_DropsPlanWithoutDispatch()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Events.PendingResumeForTests =
            new EventManager.EventResumePlan.Walker(new RoomKey(1, 3));

        h.Events.OnResumeWalkEvent(new WalkEvent(WalkEventKind.Failed, "no path", null));

        Assert.Null(h.Events.PendingResumeForTests);
        Assert.Equal(WalkState.Idle, h.Walker.State);  // resume did NOT run.
    }

    [Fact]
    public void OnResume_Stopped_DropsPlanWithoutDispatch()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Events.PendingResumeForTests =
            new EventManager.EventResumePlan.Walker(new RoomKey(1, 3));

        h.Events.OnResumeWalkEvent(new WalkEvent(WalkEventKind.Stopped, "superseded", null));

        Assert.Null(h.Events.PendingResumeForTests);
        Assert.Equal(WalkState.Idle, h.Walker.State);
    }

    [Fact]
    public void OnResume_OtherKinds_DoNotTouchPlan()
    {
        using Harness h = NewHarness();
        EventManager.EventResumePlan.Walker plan =
            new(new RoomKey(1, 3));
        h.Events.PendingResumeForTests = plan;

        h.Events.OnResumeWalkEvent(new WalkEvent(WalkEventKind.Paused,  "user", null));
        h.Events.OnResumeWalkEvent(new WalkEvent(WalkEventKind.Resumed, "user", null));
        h.Events.OnResumeWalkEvent(new WalkEvent(WalkEventKind.Started, "user", null));

        Assert.Same(plan, h.Events.PendingResumeForTests);  // untouched.
    }

    // ----- ExecuteWalkTo end-to-end ----------------------------------

    [Fact]
    public void Fire_WalkToEvent_NothingRunning_NoResumePlanQueued()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));

        h.Events.Fire(WalkToEvent(1, 3));

        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);
        Assert.Null(h.Events.PendingResumeForTests);
    }

    [Fact]
    public void Fire_WalkToEvent_WithRunningLoop_QueuesLoopResume()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Loop loop = new("ab", new[] { new RoomKey(1, 1), new RoomKey(1, 2) });
        h.Runner.Start(loop);

        h.Events.Fire(WalkToEvent(1, 3));

        // Loop was stopped (event-walk supersede) and its plan is queued.
        EventManager.EventResumePlan? plan = h.Events.PendingResumeForTests;
        EventManager.EventResumePlan.Loop loopPlan =
            Assert.IsType<EventManager.EventResumePlan.Loop>(plan);
        Assert.Same(loop, loopPlan.SavedLoop);
        Assert.Equal(new RoomKey(1, 3), h.Walker.Destination);
    }

    [Fact]
    public void Fire_WalkToEvent_WithActiveAutoLair_QueuesLairResume()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.AutoLair.Mark(new RoomKey(1, 2));
        h.AutoLair.Mark(new RoomKey(1, 3));
        h.AutoLair.Start();

        h.Events.Fire(WalkToEvent(1, 2));

        EventManager.EventResumePlan? plan = h.Events.PendingResumeForTests;
        EventManager.EventResumePlan.AutoLair lairPlan =
            Assert.IsType<EventManager.EventResumePlan.AutoLair>(plan);
        Assert.Equal(2, lairPlan.Markers.Count);
    }

    [Fact]
    public void Fire_CascadingWalkToEvents_PreservesOriginalPlan()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Loop loop = new("ab", new[] { new RoomKey(1, 1), new RoomKey(1, 2) });
        h.Runner.Start(loop);

        // First event-walk while loop is running.
        h.Events.Fire(WalkToEvent(1, 3));
        EventManager.EventResumePlan? after1 = h.Events.PendingResumeForTests;
        Assert.IsType<EventManager.EventResumePlan.Loop>(after1);

        // Second event-walk while the first is in flight. The plan
        // must STILL point at the loop, not the intermediate walk-to
        // 1/3 destination.
        h.Events.Fire(WalkToEvent(1, 2));
        EventManager.EventResumePlan? after2 = h.Events.PendingResumeForTests;
        EventManager.EventResumePlan.Loop preserved =
            Assert.IsType<EventManager.EventResumePlan.Loop>(after2);
        Assert.Same(loop, preserved.SavedLoop);
    }

    [Fact]
    public void OnResume_Stopped_DirectFire_DropsPlan()
    {
        // Synthesised Stopped — pure watcher-contract test.
        using Harness h = NewHarness();
        h.Events.PendingResumeForTests =
            new EventManager.EventResumePlan.Loop(
                new Loop("ab", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));

        h.Events.OnResumeWalkEvent(new WalkEvent(WalkEventKind.Stopped, "event supersede", null));

        Assert.Null(h.Events.PendingResumeForTests);
    }

    [Fact]
    public void Fire_LoopEvent_AfterWalkToInFlight_DropsResumePlan()
    {
        // End-to-end: the Loop event's walker.Stop("event supersede")
        // call should raise Stopped to our resume watcher, which then
        // drops the plan. This catches breakage in the inter-engine
        // call graph (ExecuteLoop's Stop ordering, walker event
        // subscription, etc.) that the direct-fire test above can't.
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Loop loop = new("ab", new[] { new RoomKey(1, 1), new RoomKey(1, 2) });
        h.Runner.Start(loop);
        h.Loops.Save(new Loop("xy", new[] { new RoomKey(1, 2), new RoomKey(1, 3) }));

        h.Events.Fire(WalkToEvent(1, 3));
        Assert.NotNull(h.Events.PendingResumeForTests);
        Assert.Equal(WalkState.Walking, h.Walker.State);  // event-walk in flight.

        ScheduledEvent loopEvent = new()
        {
            Name = "switch",
            TriggerType = EventTriggerType.Logon,
            ActionType = EventActionType.Loop,
            LoopName = "xy",
        };
        h.Events.Fire(loopEvent);

        Assert.Null(h.Events.PendingResumeForTests);
    }
}
