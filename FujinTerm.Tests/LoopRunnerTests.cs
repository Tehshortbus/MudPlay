using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class LoopRunnerTests : IDisposable
{
    private readonly string _root;

    public LoopRunnerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-looprunner-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

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
        public required MovementCoordinator Coordinator { get; init; }
        public required LoopRunner Runner { get; init; }
        public List<byte[]> Sent { get; } = new();
        public List<LoopEvent> Events { get; } = new();
        public void Dispose() { }
    }

    private Harness NewHarness(string json = GraphJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        // v3: runner expands waypoints → steps via BfsMapper at Start.
        // Without a BFS the expansion yields an empty step list and the
        // runner can't push the first step.
        BfsMapper bfs = new(graph);
        LoopRunner runner = new(tracker, coord, graph: graph, bfs: bfs);
        Harness h = new() { Tracker = tracker, Coordinator = coord, Runner = runner };
        runner.SetWireSender(b => h.Sent.Add(b));
        runner.Event += e => h.Events.Add(e);
        return h;
    }

    // Smallest viable v3 cycle on the test graph: waypoints 1/1 and
    // 1/2 expand to [N (1→2), S (2→1)] — a 2-step cycle the runner
    // can complete a full lap of with just one round-trip observation
    // pair.
    private static Loop AbCycle() =>
        new("ab", new[] { new RoomKey(1, 1), new RoomKey(1, 2) });

    [Fact]
    public void Start_EmptyLoop_ReturnsFalse()
    {
        Harness h = NewHarness();
        Loop empty = new("empty", Array.Empty<LoopWaypoint>());
        Assert.False(h.Runner.Start(empty));
    }

    [Fact]
    public void Start_SingleWaypoint_ReturnsFalse()
    {
        // v3: cycles need 2+ waypoints to form a closed loop.
        Harness h = NewHarness();
        Loop one = new("one", new[] { new RoomKey(1, 1) });
        Assert.False(h.Runner.Start(one));
    }

    [Fact]
    public void Start_SendsFirstStep()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));

        h.Runner.Start(AbCycle());

        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Single(h.Sent);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Started);
    }

    [Fact]
    public void WrapsAtEnd_AndFiresRepeatStarted()
    {
        // Complete one full lap (N + S back to 1/1) — wrap fires
        // RepeatStarted.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());

        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        h.Tracker.NoteRoomObserved(new RoomObservation("A",
            new HashSet<Direction> { Direction.N }));

        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.RepeatStarted);
    }

    [Fact]
    public void Stop_DuringRun_GoesIdle()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        h.Runner.Stop();
        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Stopped);
    }

    [Fact]
    public void CoordinatorPause_DuringRun_HoldsRunner()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        int sentBefore = h.Sent.Count;

        h.Coordinator.AssertGate("user");
        Assert.Equal(LoopState.Paused, h.Runner.State);

        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        // Confirmation arrived while paused — must not send next step.
        Assert.Equal(sentBefore, h.Sent.Count);
    }

    [Fact]
    public void Waypoint_WithCommand_FiresCommandFirst_ThenMove()
    {
        // v3: commands attach to waypoints, sending before moves. With
        // a command on waypoint 0 (1/1), Start sends the command and
        // arms the delay timer; FireDelayForTests pushes the
        // subsequent move.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Loop loop = new("with-cmd", new[]
        {
            new LoopWaypoint(new RoomKey(1, 1), "dep 100", 500),
            new LoopWaypoint(new RoomKey(1, 2)),
        });
        h.Runner.Start(loop);

        Assert.Single(h.Sent);
        Assert.Equal("dep 100\r", Encoding.Latin1.GetString(h.Sent[0]));

        h.Runner.FireDelayForTests();

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));
    }

    [Fact]
    public void Waypoint_WithCommandDelay0_WaitsForPrompt()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Loop loop = new("with-cmd", new[]
        {
            new LoopWaypoint(new RoomKey(1, 1), "ask barmaid pie", 0),
            new LoopWaypoint(new RoomKey(1, 2)),
        });
        h.Runner.Start(loop);

        Assert.Single(h.Sent);
        Assert.Equal("ask barmaid pie\r", Encoding.Latin1.GetString(h.Sent[0]));

        h.Runner.FirePromptForTests();
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));
    }

    [Fact]
    public void MissingExit_FailsRun()
    {
        // Player at C (1/3 — only S exit). Loop is [A, B] which
        // expands to [N (1→2), S (2→1)]. The runner expands from
        // waypoint 0 (1/1) but tries to send the first step's N from
        // the LIVE current room (1/3) — fails immediately.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 3));
        h.Runner.Start(AbCycle());

        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Failed);
    }

    [Fact]
    public void Failed_RaisedAfterReset_HandlerSeesIdleState()
    {
        // Regression: the Nav "Looping/moving" chip stuck on after a loop
        // failed because the Failed event was raised while the runner was
        // still Running (Reset() ran afterwards, firing no follow-up event).
        // A synchronous handler that re-reads runner state — as
        // NavigationViewModel does to drive the engine-action chip — must
        // observe the final Idle state at event time.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 3));   // C: only S exit; AbCycle's first step is N

        LoopState? stateAtFail = null;
        Loop? loopAtFail = null;
        bool sawFail = false;
        h.Runner.Event += e =>
        {
            if (e.Kind != LoopEventKind.Failed) return;
            sawFail = true;
            stateAtFail = h.Runner.State;
            loopAtFail = h.Runner.CurrentLoop;
        };

        h.Runner.Start(AbCycle());

        Assert.True(sawFail);
        Assert.Equal(LoopState.Idle, stateAtFail);
        Assert.Null(loopAtFail);
    }

    // ----- auto-recovery: blocked-at-source reroute --------------------

    [Fact]
    public void BlockedAtSource_ReroutesAndReSendsStep_InsteadOfFailing()
    {
        // Player + loop entry both at 1/1. Start sends the first step (N). The
        // move is refused (a mob in the doorway, lag): the game re-prints the
        // source room, so the tracker re-confirms 1/1 with the same room as its
        // previous. Old behavior failed straight to Idle; the fix enters bounded
        // recovery — since we're confirmed back on the loop, it reroutes from
        // here and re-sends the blocked step rather than giving up.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));

        // Refused-move redisplay: the source room (A / 1/1) re-appears.
        h.Tracker.NoteRoomObserved(new RoomObservation("A",
            new HashSet<Direction> { Direction.N }));

        // Rerouted, not failed: still driving and the blocked step went out again.
        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
        Assert.Contains(h.Events, e =>
            e.Kind == LoopEventKind.Paused && e.Detail.Contains("recovering"));
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));
    }

    [Fact]
    public void BlockedAtSource_PersistentBlock_ExhaustsBudget_ThenFails()
    {
        // A block that never clears must not reroute forever — the bounded
        // budget (MaxRecoverAttempts = 3) eventually surfaces as Failed so the
        // Nav chip and toolbar don't hang in a "recovering" state indefinitely.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());

        // Four refused redisplays: three consume the retry budget (each reroutes
        // + re-sends), the fourth trips the cap and fails.
        for (int i = 0; i < 4; i++)
        {
            h.Tracker.NoteRoomObserved(new RoomObservation("A",
                new HashSet<Direction> { Direction.N }));
        }

        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Failed);
    }

    // ----- PR C: lap timing + ReachedFirstWaypoint ---------------------

    [Fact]
    public void Start_FiresReachedFirstWaypoint_OnceWhenNoApproachNeeded()
    {
        // Harness doesn't bind a walker, so Start always BeginCircles
        // immediately. ReachedFirstWaypoint should fire exactly once on
        // that path, alongside Started.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());

        Assert.Equal(1, h.Events.Count(e => e.Kind == LoopEventKind.Started));
        Assert.Equal(1, h.Events.Count(e => e.Kind == LoopEventKind.ReachedFirstWaypoint));
    }

    [Fact]
    public void LapTime_RecordsOnWrap()
    {
        // Complete one full lap N + S returning to 1/1 — wrap fires
        // RepeatStarted and pushes a duration into LapHistory.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());

        Assert.Empty(h.Runner.LapHistory);

        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        h.Tracker.NoteRoomObserved(new RoomObservation("A",
            new HashSet<Direction> { Direction.N }));

        Assert.Single(h.Runner.LapHistory);
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.RepeatStarted);
        Assert.True(h.Runner.LapHistory[0] >= TimeSpan.Zero);
    }

    // ----- PR D: avoid-list re-expand ---------------------------------

    [Fact]
    public void NotifyAvoidedChanged_TriggersStopAndRestart()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        h.Events.Clear();

        h.Runner.NotifyAvoidedChanged();

        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Stopped);
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Started);
    }

    [Fact]
    public void NotifyAvoidedChanged_WhenIdle_NoOp()
    {
        Harness h = NewHarness();
        h.Runner.NotifyAvoidedChanged();
        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Empty(h.Events);
    }

    [Fact]
    public void Reset_ClearsLapHistory()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        h.Tracker.NoteRoomObserved(new RoomObservation("A",
            new HashSet<Direction> { Direction.N }));

        Assert.NotEmpty(h.Runner.LapHistory);
        h.Runner.Stop();
        Assert.Empty(h.Runner.LapHistory);
    }

    [Fact]
    public void CompletedLaps_CountsEachWrap_AndResetsOnStop()
    {
        // The Nav lap counter reads CompletedLaps (uncapped), unlike LapHistory.Count
        // which caps at MaxLapHistory. One full lap → 1; the displayed "lap N" is this
        // + 1. Stop resets it so a fresh run starts back at lap 1.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Equal(0, h.Runner.CompletedLaps);

        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        h.Tracker.NoteRoomObserved(new RoomObservation("A",
            new HashSet<Direction> { Direction.N }));
        Assert.Equal(1, h.Runner.CompletedLaps);

        // A second lap increments again.
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        h.Tracker.NoteRoomObserved(new RoomObservation("A",
            new HashSet<Direction> { Direction.N }));
        Assert.Equal(2, h.Runner.CompletedLaps);

        h.Runner.Stop();
        Assert.Equal(0, h.Runner.CompletedLaps);
    }

    // ----- circuit-phase special exits (shared with the walker) ------

    // Docks (1/1) → Pier (1/2) via a Text exit ("borrow skiff"); Pier
    // returns north plainly. A 2-waypoint cycle crosses the Text exit
    // on its first circuit step.
    private const string TextExitGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Docks",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2 (Text: borrow skiff, go skiff)", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Pier",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/1", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void Circuit_TextExit_SendsCommand_NotCardinal()
    {
        // The bug this fixes: a loop circuit used to send the bare
        // cardinal ("s\r") for a Text exit instead of the command the
        // exit actually requires ("borrow skiff").
        Harness h = NewHarness(TextExitGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));

        h.Runner.Start(new Loop("docks", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));

        Assert.Single(h.Sent);
        Assert.Equal("borrow skiff\r", Encoding.Latin1.GetString(h.Sent[0]));
    }

    [Fact]
    public void Circuit_TextExit_LandsAtTarget_Advances()
    {
        Harness h = NewHarness(TextExitGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(new Loop("docks", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));

        // Landing at Pier confirms the Text step and pushes the return.
        h.Tracker.NoteRoomObserved(new RoomObservation("Pier",
            new HashSet<Direction> { Direction.N }));

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));
    }

    // Outside (1/1) → Foyer (1/2) behind a closed door; Foyer returns
    // west. A loop circuit has no door-open FSM, so the door step must
    // fail loudly rather than send a cardinal into a closed door.
    private const string DoorGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Outside",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/2 (Door)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Foyer",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1 (Door)",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void Circuit_ClosedDoor_FailsLoud_NoCardinalSent()
    {
        Harness h = NewHarness(DoorGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));

        h.Runner.Start(new Loop("house", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));

        Assert.Empty(h.Sent);
        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Contains(h.Events,
            e => e.Kind == LoopEventKind.Failed && e.Detail.Contains("closed door"));
    }
}
