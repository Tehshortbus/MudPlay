using System;
using System.IO;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Coalesced run-state + Pause / Resume / Stop routing for
/// <see cref="MovementController"/>. Headless: drives the graph +
/// tracker directly, no Telnet socket. State-machine progression past
/// the initial dispatch needs DispatcherTimer ticks (not pumped under
/// xUnit), so these pin the synchronous facts: idle detection, walker
/// pause/resume via the shared gate, Auto-Lair pause routing through the
/// manager's own Pause, full Stop, and StateChanged fan-out.
/// </summary>
public sealed class MovementControllerTests : IDisposable
{
    private readonly string _root;

    public MovementControllerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-movectl-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // 1/1 ↔ 1/2 ↔ 1/3 linear strip. 1/3 carries a lair tag so the
    // timer store resolves a respawn; 1/1 is the start position.
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
        public required MovementCoordinator Coordinator { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public required LoopRunner Loops { get; init; }
        public required AutoLairManager AutoLair { get; init; }
        public required LairTimerStore Timers { get; init; }
        public required MovementController Controller { get; init; }

        public void Dispose()
        {
            Controller.Dispose();
            AutoLair.Dispose();
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
        LoopRunner loops = new(tracker, coord, graph: graph, bfs: bfs);
        loops.SetWireSender(_ => { });
        LairTimerStore timers = new(cache, graph, tracker);
        AutoLairManager autoLair = new(walker, tracker, graph, bfs, timers, log: null, coordinator: coord);
        MovementController controller = new(walker, loops, autoLair, coord);
        return new Harness
        {
            Tracker = tracker,
            Coordinator = coord,
            Walker = walker,
            Loops = loops,
            AutoLair = autoLair,
            Timers = timers,
            Controller = controller,
        };
    }

    [Fact]
    public void Fresh_IsIdle()
    {
        using Harness h = NewHarness();
        Assert.Equal(MovementEngineState.Idle, h.Controller.State);
        Assert.True(h.Controller.IsIdle);
        Assert.False(h.Controller.IsActive);
        Assert.False(h.Controller.IsPaused);
    }

    [Fact]
    public void IdlePauseResumeStop_AreNoOps()
    {
        using Harness h = NewHarness();
        // Nothing running: none of these should assert a gate or throw.
        h.Controller.Pause();
        h.Controller.Resume();
        h.Controller.Stop();
        Assert.Equal(MovementEngineState.Idle, h.Controller.State);
        Assert.False(h.Coordinator.IsPaused);
    }

    [Fact]
    public void WalkerWalking_IsRunning()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));
        Assert.Equal(MovementEngineState.Running, h.Controller.State);
        Assert.True(h.Controller.IsActive);
    }

    [Fact]
    public void Pause_WhileWalking_AssertsGate_AndPaused()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));

        h.Controller.Pause();

        Assert.True(h.Coordinator.IsPaused);
        Assert.Equal(WalkState.Paused, h.Walker.State);
        Assert.Equal(MovementEngineState.Paused, h.Controller.State);
        Assert.True(h.Controller.IsPaused);
    }

    [Fact]
    public void Resume_AfterPause_ClearsGate_BackToRunning()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));
        h.Controller.Pause();

        h.Controller.Resume();

        Assert.False(h.Coordinator.IsPaused);
        Assert.Equal(MovementEngineState.Running, h.Controller.State);
    }

    [Fact]
    public void TogglePause_FlipsBothWays()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));

        h.Controller.TogglePause();
        Assert.Equal(MovementEngineState.Paused, h.Controller.State);
        h.Controller.TogglePause();
        Assert.Equal(MovementEngineState.Running, h.Controller.State);
    }

    [Fact]
    public void Stop_WhileWalking_ReturnsToIdle()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));

        h.Controller.Stop();

        Assert.Equal(MovementEngineState.Idle, h.Controller.State);
        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.False(h.Coordinator.IsPaused);
    }

    [Fact]
    public void Pause_StacksOnEngineWait_SurvivesWaitClearing()
    {
        // Backs the @stop fix: a remote stop (routed through Pause) must hold on
        // top of a combat wait so movement stays paused after the fight clears,
        // instead of resuming the moment the CombatGate drops.
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));

        // An engine wait (combat) holds the walker first — NOT a user pause.
        h.Coordinator.AssertGate(MovementCoordinator.CombatGate);
        Assert.Equal(MovementEngineState.Paused, h.Controller.State);
        Assert.False(h.Controller.IsUserPaused);

        // @stop stacks the user pause on top.
        h.Controller.Pause();
        Assert.True(h.Controller.IsUserPaused);

        // Combat clears — the user override still holds, so we stay paused.
        h.Coordinator.ClearGate(MovementCoordinator.CombatGate);
        Assert.True(h.Controller.IsUserPaused);
        Assert.Equal(MovementEngineState.Paused, h.Controller.State);
        Assert.Equal(WalkState.Paused, h.Walker.State);

        // @rego (Resume) lifts the user override and the walk continues.
        h.Controller.Resume();
        Assert.False(h.Controller.IsUserPaused);
        Assert.Equal(MovementEngineState.Running, h.Controller.State);
    }

    [Fact]
    public void Pause_WhileAutoLair_RoutesThroughManager()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.AutoLair.Mark(new RoomKey(1, 1));
        h.AutoLair.Mark(new RoomKey(1, 3));
        Assert.True(h.AutoLair.Start());
        Assert.Equal(MovementEngineState.Running, h.Controller.State);

        h.Controller.Pause();

        Assert.True(h.AutoLair.IsPaused);
        Assert.Equal(MovementEngineState.Paused, h.Controller.State);

        h.Controller.Resume();
        Assert.False(h.AutoLair.IsPaused);
        Assert.Equal(MovementEngineState.Running, h.Controller.State);
    }

    [Fact]
    public void Stop_WhileAutoLair_TearsDownSession()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.AutoLair.Mark(new RoomKey(1, 1));
        h.AutoLair.Mark(new RoomKey(1, 3));
        h.AutoLair.Start();

        h.Controller.Stop();

        Assert.False(h.AutoLair.IsActive);
        Assert.Equal(MovementEngineState.Idle, h.Controller.State);
    }

    [Fact]
    public void StateChanged_FiresOnWalkerActivity()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        int fires = 0;
        h.Controller.StateChanged += () => fires++;

        h.Walker.WalkTo(new RoomKey(1, 3));

        Assert.True(fires > 0);
    }
}
