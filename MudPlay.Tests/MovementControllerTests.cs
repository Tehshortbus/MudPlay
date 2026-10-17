using System;
using System.IO;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Coalesced run-state + Pause / Resume / Stop routing for
// MovementController. Headless: drives the graph +
// tracker directly, no Telnet socket. State-machine progression past
// the initial dispatch needs DispatcherTimer ticks (not pumped under
// xUnit), so these pin the synchronous facts: idle detection, walker
// pause/resume via the shared gate, Auto-Lair pause routing through the
// manager's own Pause, full Stop, and StateChanged fan-out.
public sealed class MovementControllerTests : IDisposable
{
    private readonly string _root;

    public MovementControllerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-movectl-tests-" + Path.GetRandomFileName());
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
        // The game's prompts, as the loop runner reads them.
        public required WirePromptScanner Prompt { get; init; }

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
        WirePromptScanner prompt = new();
        LoopRunner loops = new(tracker, coord, promptScanner: prompt, graph: graph, bfs: bfs);
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
            Prompt = prompt,
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

    // A Resume with the master switch off is refused (with its notice, by the
    // hook) and the user's pause stays; with it on, Resume works as above.
    [Fact]
    public void Resume_RefusedForTheMasterSwitch_KeepsTheUserPause()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));
        h.Controller.Pause();
        int asked = 0;
        bool refuse = true;
        h.Controller.RefuseResume = () => { asked++; return refuse; };

        h.Controller.Resume();
        h.Controller.TogglePause();

        Assert.Equal(2, asked);
        Assert.True(h.Controller.IsUserPaused);

        refuse = false;
        h.Controller.Resume();
        Assert.False(h.Controller.IsUserPaused);
    }

    // A loop the reconnect set aside waits for the master switch; Stop is how the
    // user says it should not come back with it.
    [Fact]
    public void Stop_CallsOffALoopRestartHeldForTheMasterSwitch()
    {
        using Harness h = NewHarness();
        h.Loops.MasterSwitchOff = () => true;
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Loops.Start(new Loop("ab", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));
        h.Loops.NotifyDisconnected();
        h.Prompt.Append(System.Text.Encoding.Latin1.GetBytes("[HP=100]: "));
        Assert.True(h.Loops.ReconnectResumeHeldForMasterSwitch);

        h.Controller.Stop();

        Assert.False(h.Loops.ReconnectResumeHeldForMasterSwitch);
        Assert.Null(h.Loops.PendingReconnectResumeName);
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
    public void SuspendForAutoAll_WhileWalking_Pauses_ReleaseResumes()
    {
        // Auto-All kill switch parks an in-flight walk (retaining the destination)
        // and restores it exactly where it left off.
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));

        h.Controller.SuspendForAutoAll();
        Assert.Equal(MovementEngineState.Paused, h.Controller.State);
        Assert.Equal(WalkState.Paused, h.Walker.State);

        h.Controller.ReleaseFromAutoAll();
        Assert.Equal(MovementEngineState.Running, h.Controller.State);
        Assert.False(h.Coordinator.IsPaused);
    }

    [Fact]
    public void SuspendForAutoAll_WhileAutoLair_FreezesViaGate_ReleaseResumes()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.AutoLair.Mark(new RoomKey(1, 1));
        h.AutoLair.Mark(new RoomKey(1, 3));
        h.AutoLair.Start();

        // Auto-All engaged asserts the AutoAllGate — the walker the auto-lair drives
        // holds, so no step goes out. Release drops the gate and movement resumes.
        h.Controller.SuspendForAutoAll();
        Assert.True(h.Coordinator.IsPaused);

        h.Controller.ReleaseFromAutoAll();
        Assert.False(h.Coordinator.IsPaused);
        Assert.Equal(MovementEngineState.Running, h.Controller.State);
    }

    [Fact]
    public void SuspendForAutoAll_WhenIdle_AssertsGate_HoldsLaterWalk()
    {
        // The reported bug: Auto-All off, but a right-click Queue-walk-to still ran.
        // Engaging Auto-All while nothing is moving must assert the gate so a walk
        // STARTED afterwards is held, not free to run.
        using Harness h = NewHarness();
        h.Controller.SuspendForAutoAll();
        Assert.True(h.Coordinator.IsPaused);   // gate asserted even though idle

        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));
        Assert.Equal(WalkState.Paused, h.Walker.State);   // held — didn't send a step

        h.Controller.ReleaseFromAutoAll();
        Assert.False(h.Coordinator.IsPaused);             // gate cleared — walk resumes
    }

    [Fact]
    public void SuspendForAutoAll_RespectsPreExistingUserPause()
    {
        // The user paused the walk themselves before engaging Auto-All. Releasing
        // Auto-All must NOT resume it — only the user's own Resume should.
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));
        h.Controller.Pause();                     // user's own pause
        Assert.True(h.Controller.IsUserPaused);

        h.Controller.SuspendForAutoAll();         // no-op — already user-paused
        h.Controller.ReleaseFromAutoAll();        // must not resume the user's pause

        Assert.Equal(MovementEngineState.Paused, h.Controller.State);
        Assert.True(h.Controller.IsUserPaused);
    }

    [Fact]
    public void ReleaseFromAutoAll_AfterUserStopped_NoUnwantedResume()
    {
        // The user Stopped the walk while Auto-All was engaged. Restoring Auto-All
        // must not revive a nav the user deliberately ended.
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));
        h.Controller.SuspendForAutoAll();
        h.Controller.Stop();                      // user ends it
        Assert.Equal(MovementEngineState.Idle, h.Controller.State);

        h.Controller.ReleaseFromAutoAll();
        Assert.Equal(MovementEngineState.Idle, h.Controller.State);
        Assert.False(h.Coordinator.IsPaused);
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

    // ----- An errand held by Stop --------------------------------------
    // Stop used to end a money or training errand where it stood. It now holds it,
    // and the next thing the user starts asks whether to finish the errand first.

    private sealed class Errand
    {
        public string? Active = "the stash transfer";
        public int Abandoned;
        public bool Answer = true;
        public int Asked;
        public System.Threading.Tasks.TaskCompletionSource<bool>? Pending;
        public DateTime Now = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

        public void Wire(MovementController c) => c.SetErrandHooks(
            activeErrand: () => Active,
            abandonErrand: _ => { Abandoned++; Active = null; },
            askResume: _ =>
            {
                Asked++;
                return Pending is { } p ? p.Task : System.Threading.Tasks.Task.FromResult(Answer);
            },
            utcNow: () => Now);
    }

    [Fact]
    public void Stop_WithAnErrandUnderWay_HoldsIt_AndTheWalkKeepsItsDestination()
    {
        using Harness h = NewHarness();
        Errand e = new();
        e.Wire(h.Controller);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));

        Assert.True(h.Controller.HoldErrandOnStop());

        Assert.Equal("the stash transfer", h.Controller.SuspendedErrand);
        Assert.True(h.Controller.IsUserPaused);
        Assert.NotEqual(WalkState.Idle, h.Walker.State);      // paused, not stopped
        Assert.Equal(0, e.Abandoned);
    }

    [Fact]
    public void Stop_WithNoErrand_IsNotHeld()
    {
        using Harness h = NewHarness();
        Errand e = new() { Active = null };
        e.Wire(h.Controller);

        Assert.False(h.Controller.HoldErrandOnStop());
        Assert.Null(h.Controller.SuspendedErrand);
        Assert.False(h.Controller.IsUserPaused);
    }

    [Fact]
    public void Resume_CarriesTheHeldErrandOn()
    {
        using Harness h = NewHarness();
        Errand e = new();
        e.Wire(h.Controller);
        h.Controller.HoldErrandOnStop();

        h.Controller.Resume();

        Assert.Null(h.Controller.SuspendedErrand);
        Assert.False(h.Controller.IsUserPaused);
        Assert.Equal(0, e.Abandoned);
    }

    [Fact]
    public void StartingARun_WithNothingHeld_StartsAtOnce_WithoutAsking()
    {
        using Harness h = NewHarness();
        Errand e = new();
        e.Wire(h.Controller);
        int started = 0;

        h.Controller.StartUserRun(() => started++);

        Assert.Equal(1, started);
        Assert.Equal(0, e.Asked);
    }

    [Fact]
    public void StartingARun_AnsweredYes_ResumesTheErrand_AndStartsTheRunWhenItEnds()
    {
        using Harness h = NewHarness();
        Errand e = new() { Answer = true };
        e.Wire(h.Controller);
        h.Controller.HoldErrandOnStop();
        int started = 0;

        h.Controller.StartUserRun(() => started++);

        Assert.Equal(1, e.Asked);
        Assert.Equal(0, started);                              // the errand goes first
        Assert.False(h.Controller.IsUserPaused);               // and is moving again
        Assert.Equal(0, e.Abandoned);

        e.Active = null;                                       // the errand finishes
        h.Controller.NoteErrandStateChanged();
        Assert.Equal(1, started);

        h.Controller.NoteErrandStateChanged();                 // only once
        Assert.Equal(1, started);
    }

    [Fact]
    public void StartingARun_AnsweredNo_AbandonsTheErrand_AndStartsTheRunNow()
    {
        using Harness h = NewHarness();
        Errand e = new() { Answer = false };
        e.Wire(h.Controller);
        h.Controller.HoldErrandOnStop();
        int started = 0;

        h.Controller.StartUserRun(() => started++);

        Assert.Equal(1, e.Abandoned);
        Assert.Equal(1, started);
        Assert.Null(h.Controller.SuspendedErrand);
        Assert.False(h.Controller.IsUserPaused);
    }

    // The errand finished on its own while the question was still on screen: there
    // is nothing left to choose, so the run just starts.
    [Fact]
    public void TheErrandEndingWhileTheQuestionStands_StartsTheRun()
    {
        using Harness h = NewHarness();
        Errand e = new() { Pending = new System.Threading.Tasks.TaskCompletionSource<bool>() };
        e.Wire(h.Controller);
        h.Controller.HoldErrandOnStop();
        int started = 0;
        h.Controller.StartUserRun(() => started++);
        Assert.Equal(0, started);

        e.Active = null;
        h.Controller.NoteErrandStateChanged();
        e.Pending.SetResult(true);

        Assert.Equal(1, started);
        Assert.Equal(0, e.Abandoned);
        Assert.False(h.Controller.IsUserPaused);
    }

    // The errand ended while held (cancelled from its own menu): the hold Stop placed
    // must not strand the next run behind a pause nobody asked for.
    [Fact]
    public void TheErrandEndingWhileHeld_LiftsTheHold()
    {
        using Harness h = NewHarness();
        Errand e = new();
        e.Wire(h.Controller);
        h.Controller.HoldErrandOnStop();

        e.Active = null;
        h.Controller.NoteErrandStateChanged();

        Assert.Null(h.Controller.SuspendedErrand);
        Assert.False(h.Controller.IsUserPaused);
    }

    // A real Stop (Reset States, a death halt) still stops, errand or not.
    [Fact]
    public void ARealStop_ClearsTheHold()
    {
        using Harness h = NewHarness();
        Errand e = new();
        e.Wire(h.Controller);
        h.Controller.HoldErrandOnStop();

        h.Controller.Stop();

        Assert.Null(h.Controller.SuspendedErrand);
        Assert.False(h.Controller.IsUserPaused);
    }

    // The Navigation window's own Resume lifts the user gate without going through
    // the controller. That carries the errand on, so nothing is left to ask about.
    [Fact]
    public void ThePauseLiftedElsewhere_CarriesTheErrandOn()
    {
        using Harness h = NewHarness();
        Errand e = new();
        e.Wire(h.Controller);
        h.Controller.HoldErrandOnStop();

        h.Coordinator.ClearGate(MovementCoordinator.UserGate);

        Assert.Null(h.Controller.SuspendedErrand);
        int started = 0;
        h.Controller.StartUserRun(() => started++);
        Assert.Equal(1, started);
        Assert.Equal(0, e.Asked);
    }

    // The errand was resumed with a run queued behind it, then everything was
    // stopped outright (a death, Reset States). The queued run must not start itself.
    [Fact]
    public void ARealStop_DropsTheRunQueuedBehindTheErrand()
    {
        using Harness h = NewHarness();
        Errand e = new();
        e.Wire(h.Controller);
        h.Controller.HoldErrandOnStop();
        int started = 0;
        h.Controller.StartUserRun(() => started++);

        h.Controller.Stop();
        e.Active = null;
        h.Controller.NoteErrandStateChanged();

        Assert.Equal(0, started);
    }

    // The question is modeless: a second start while it stands must not ask twice,
    // and the answer applies to the start made last.
    [Fact]
    public void ASecondStartWhileTheQuestionStands_AsksOnce_AndTheLastStartWins()
    {
        using Harness h = NewHarness();
        Errand e = new() { Pending = new System.Threading.Tasks.TaskCompletionSource<bool>() };
        e.Wire(h.Controller);
        h.Controller.HoldErrandOnStop();
        int first = 0, second = 0;

        h.Controller.StartUserRun(() => first++);
        h.Controller.StartUserRun(() => second++);
        e.Pending.SetResult(false);

        Assert.Equal(1, e.Asked);
        Assert.Equal(0, first);
        Assert.Equal(1, second);
    }

    // ----- The user's Stop, the same from the toolbar and the Navigation window -----

    [Fact]
    public void StopByUser_HoldsAnErrandUnderWay_AndTheNextPressEndsIt()
    {
        using Harness h = NewHarness();
        Errand e = new();
        e.Wire(h.Controller);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));

        Assert.False(h.Controller.StopByUser());
        Assert.Equal("the stash transfer", h.Controller.SuspendedErrand);
        Assert.NotEqual(WalkState.Idle, h.Walker.State);

        e.Now += TimeSpan.FromSeconds(5);
        Assert.True(h.Controller.StopByUser());

        Assert.Equal(1, e.Abandoned);
        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Equal(MovementEngineState.Idle, h.Controller.State);
    }

    [Fact]
    public void StopByUser_DropsTheRunQueuedBehindAHeldErrand()
    {
        using Harness h = NewHarness();
        Errand e = new();
        e.Wire(h.Controller);
        Assert.False(h.Controller.StopByUser());
        int started = 0;
        h.Controller.StartUserRun(() => started++);   // asked, answered "resume it first"

        e.Now += TimeSpan.FromSeconds(5);
        Assert.False(h.Controller.StopByUser());      // the resumed errand is held again
        e.Now += TimeSpan.FromSeconds(5);
        Assert.True(h.Controller.StopByUser());       // and ended
        e.Active = null;
        h.Controller.NoteErrandStateChanged();

        Assert.Equal(0, started);
    }

    [Fact]
    public void StopByUser_EndsAPausedWalk_AndLiftsThePause()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));
        h.Controller.Pause();
        Assert.True(h.Controller.IsUserPaused);

        Assert.True(h.Controller.StopByUser());

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.False(h.Controller.IsUserPaused);
        Assert.False(h.Coordinator.IsPaused);
    }

    // Stop is the only stop control there is: pressed again while the errand is
    // still held, it ends it, and the caller's own stop runs.
    [Fact]
    public void ASecondStop_EndsTheHeldErrand()
    {
        using Harness h = NewHarness();
        Errand e = new();
        e.Wire(h.Controller);
        Assert.True(h.Controller.HoldErrandOnStop());

        e.Now += TimeSpan.FromSeconds(5);
        Assert.False(h.Controller.HoldErrandOnStop());

        Assert.Equal(1, e.Abandoned);
        Assert.Null(h.Controller.SuspendedErrand);
        Assert.False(h.Controller.IsUserPaused);
    }

    // A double-click on Stop is one press; it must not hold and end in one go.
    [Fact]
    public void ADoubleClickOnStop_StillOnlyHolds()
    {
        using Harness h = NewHarness();
        Errand e = new();
        e.Wire(h.Controller);
        Assert.True(h.Controller.HoldErrandOnStop());

        e.Now += TimeSpan.FromMilliseconds(300);
        Assert.True(h.Controller.HoldErrandOnStop());

        Assert.Equal(0, e.Abandoned);
        Assert.Equal("the stash transfer", h.Controller.SuspendedErrand);
    }

    // Resumed and stopped again: that Stop is a first press once more.
    [Fact]
    public void AStopAfterResume_HoldsAgain()
    {
        using Harness h = NewHarness();
        Errand e = new();
        e.Wire(h.Controller);
        h.Controller.HoldErrandOnStop();
        h.Controller.Resume();

        e.Now += TimeSpan.FromSeconds(5);
        Assert.True(h.Controller.HoldErrandOnStop());

        Assert.Equal(0, e.Abandoned);
        Assert.Equal("the stash transfer", h.Controller.SuspendedErrand);
    }

    // ----- paused by a typed move ------------------------------------

    [Fact]
    public void PauseForTypedMove_PausesAndRemembersTheCommand_UntilResumed()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));
        int notes = 0;
        h.Controller.PausedByTypedMoveChanged += () => notes++;

        h.Controller.PauseForTypedMove("u");

        Assert.True(h.Controller.IsUserPaused);
        Assert.True(h.Coordinator.IsGateAsserted(MovementCoordinator.UserGate));
        Assert.Equal("u", h.Controller.PausedByTypedMove);
        Assert.Equal(1, notes);

        h.Controller.Resume();
        Assert.Null(h.Controller.PausedByTypedMove);
        Assert.Equal(2, notes);
    }

    [Fact]
    public void PauseForTypedMove_IsDroppedByStop_AndByAnyoneLiftingThePause()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));
        h.Controller.PauseForTypedMove("go path");
        h.Controller.Stop();
        Assert.Null(h.Controller.PausedByTypedMove);

        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));
        h.Controller.PauseForTypedMove("n");
        h.Coordinator.ClearGate(MovementCoordinator.UserGate, "someone else");   // a remote resume, say
        Assert.Null(h.Controller.PausedByTypedMove);
    }

    [Fact]
    public void PauseForTypedMove_LeavesAPauseTheUserPressedAlone()
    {
        using Harness h = NewHarness();
        Assert.Null(h.Controller.PausedByTypedMove);
        h.Controller.PauseForTypedMove("u");             // nothing running
        Assert.Null(h.Controller.PausedByTypedMove);
        Assert.False(h.Coordinator.IsGateAsserted(MovementCoordinator.UserGate));

        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));
        h.Controller.Pause();                            // the Pause button
        h.Controller.PauseForTypedMove("u");
        Assert.Null(h.Controller.PausedByTypedMove);     // already paused by choice
    }

    // ----- puzzle solvers --------------------------------------------

    [Fact]
    public void Solver_CountsAsNavigationRunning_AndStopEndsIt()
    {
        using Harness h = NewHarness();
        bool active = false, held = false;
        string? stopped = null;
        bool otherStopped = false;
        h.Controller.AddSolver(() => false, () => false, _ => otherStopped = true);   // an idle one alongside
        h.Controller.AddSolver(() => active, () => held, reason => { stopped = reason; active = false; });
        int changes = 0;
        h.Controller.StateChanged += () => changes++;

        Assert.Equal(MovementEngineState.Idle, h.Controller.State);

        active = true;
        h.Controller.NoteSolverStateChanged();
        Assert.Equal(1, changes);
        Assert.Equal(MovementEngineState.Running, h.Controller.State);

        held = true;
        Assert.Equal(MovementEngineState.Paused, h.Controller.State);
        held = false;

        // Pause is the user gate, which the solvers hold on.
        h.Controller.Pause();
        Assert.True(h.Coordinator.IsGateAsserted(MovementCoordinator.UserGate));
        Assert.True(h.Controller.IsUserPaused);

        h.Controller.Stop();
        Assert.NotNull(stopped);
        Assert.False(otherStopped);
        Assert.False(h.Coordinator.IsGateAsserted(MovementCoordinator.UserGate));
        Assert.Equal(MovementEngineState.Idle, h.Controller.State);
    }
}
