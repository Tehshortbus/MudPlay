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
        // move is refused (a mob in the doorway, a shut door, an impairment): the
        // game prints an explicit refusal line — NOT a room redisplay — which
        // MovementRefusalDetector routes to RoomTracker.NoteMoveBlocked, dropping
        // the pending move and re-confirming 1/1 with the same room as its
        // previous. Old behavior failed straight to Idle; the fix enters bounded
        // recovery — since we're confirmed back on the loop, it reroutes from
        // here and re-sends the blocked step rather than giving up.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));

        // Explicit refusal line seen: the move never took, tracker reverts to
        // Confirmed at the source (1/1).
        h.Tracker.NoteMoveBlocked();

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

        // Four explicit refusals: three consume the retry budget (each reroutes
        // + re-sends, putting the tracker back into Pending), the fourth trips the
        // cap and fails.
        for (int i = 0; i < 4; i++)
        {
            h.Tracker.NoteMoveBlocked();
        }

        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Failed);
    }

    [Fact]
    public void PassiveSourceRedisplay_WhileMovePending_IsIgnored_NoFalseRecovery()
    {
        // CONFIRMED game mechanic: a refused move never redisplays the room — it
        // always prints an explicit refusal line instead. So when the SOURCE room
        // re-appears while a move is pending, it can only be a passive re-look (a
        // combat-clear, a mob arrival, a bare re-glance), never the move's
        // outcome. The tracker must ignore it and keep waiting for the real move
        // result — NOT infer a refusal and cascade the loop into a bogus recovery.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);

        // Passive redisplay of the source room (A / 1/1) while the N move is still
        // in flight.
        h.Tracker.NoteRoomObserved(new RoomObservation("A",
            new HashSet<Direction> { Direction.N }));

        // No recovery, no extra step, still running with the move pending.
        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
        Assert.DoesNotContain(h.Events, e =>
            e.Kind == LoopEventKind.Paused && e.Detail.Contains("recovering"));
        Assert.Single(h.Sent);
        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);

        // The move's real result (room B) now confirms cleanly and advances.
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));

        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.StepCompleted);
    }

    // ----- resume-while-in-flight + Pending-at-target self-heal --------

    [Fact]
    public void ResumeWhileMoveInFlight_DoesNotReSendMove_ThenAdvancesOnConfirmation()
    {
        // Regression (the multi-minute loop stall): an instantaneous pause →
        // resume (a PartyWait gate that asserts and clears in the same instant)
        // landed while a loop step's move was still on the wire — its
        // confirmation hadn't arrived, so the tracker was still Pending. The old
        // resume path fell through to SendNextStep and RE-SENT the same move: a
        // duplicate command on the wire AND a phantom duplicate in the tracker's
        // pending queue that never emptied, wedging the tracker in
        // Pending-at-target and hanging the loop. The fix keeps the in-flight
        // step and waits for its real confirmation.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));
        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);

        // Instantaneous pause → resume while the N move is still in flight
        // (no room observed yet, tracker still Pending on it).
        h.Coordinator.AssertGate(MovementCoordinator.PartyWaitGate);
        Assert.Equal(LoopState.Paused, h.Runner.State);
        h.Coordinator.ClearGate(MovementCoordinator.PartyWaitGate);
        Assert.Equal(LoopState.Running, h.Runner.State);

        // Not re-sent: the move was not duplicated onto the wire.
        Assert.Single(h.Sent);

        // The real confirmation now lands and the loop advances cleanly.
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("s\r", Encoding.Latin1.GetString(h.Sent[1]));
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.StepCompleted);
    }

    [Fact]
    public void ArrivesAtTargetWhilePendingQueueNotEmpty_Advances_NoHang()
    {
        // Defense in depth for the same stall: if a queue desync ever leaves a
        // phantom move behind the confirming one, the tracker lands physically
        // at the step's target but stays Pending ("move confirmed, queue not
        // empty") instead of Confirmed. The loop only ever has one move in
        // flight, so any queue residue at the target is spurious — arriving at
        // the target means the step completed. The runner must advance rather
        // than hang forever on a Confirmed the wedged queue never delivers.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);

        // Simulate the desync: a phantom duplicate of the in-flight N move is
        // enqueued behind the real one.
        h.Tracker.NoteMoveSent(Direction.N);

        // The move confirms at B (1/2 — the step's target) but the phantom keeps
        // the queue non-empty, so the tracker lands Pending, not Confirmed.
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);

        // Still advanced: the return step went out despite the Pending posture.
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("s\r", Encoding.Latin1.GetString(h.Sent[1]));
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.StepCompleted);
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
    public void ResumeAfterDetour_SuppressesReachedFirstWaypoint()
    {
        // Auto-deposit round-trip: a genuine Start fires the once-per-session
        // ReachedFirstWaypoint (the stats-reset / party @reset trigger). The
        // detour Stop()s and ResumeAfterDetour()s the loop — a continuation of the
        // same session, so the event must NOT re-fire while the loop still Starts.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Equal(1, h.Events.Count(e => e.Kind == LoopEventKind.ReachedFirstWaypoint));

        h.Runner.Stop("auto-deposit reroute");
        h.Events.Clear();

        h.Runner.ResumeAfterDetour(AbCycle());

        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Started);
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.ReachedFirstWaypoint);
    }

    [Fact]
    public void Start_AfterDetourResume_FiresReachedFirstWaypointAgain()
    {
        // The suppression is one-shot: after a detour resume, a genuine user Start
        // begins a new hunting session, so ReachedFirstWaypoint fires again.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        h.Runner.Stop("auto-deposit reroute");
        h.Runner.ResumeAfterDetour(AbCycle());
        h.Runner.Stop("user stop");
        h.Events.Clear();

        h.Runner.Start(AbCycle());

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

    [Fact]
    public void Circuit_ClosedDoor_WithEnqueuer_OpensThenCrosses()
    {
        // Report 152210: a loop used to idle on a closed door mid-circuit. With
        // a door enqueuer bound (as MainWindowViewModel wires it to the shared
        // DoorOpenManager), the circuit routes the closed-door step through the
        // FSM and — on Opened — crosses with the plain cardinal instead of
        // detaching the whole lap. No cardinal reaches the wire until the door
        // reports open.
        Harness h = NewHarness(DoorGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));

        Direction? requested = null;
        Action<DoorOpenResult>? doorReply = null;
        h.Runner.SetDoorEnqueuer((dir, _, _, _, _, reply) =>
        {
            requested = dir;
            doorReply = reply;
        });
        h.Runner.SetDoorStopper(() => { });

        h.Runner.Start(new Loop("house", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));

        // Door enqueued, nothing on the wire yet, loop still driving.
        Assert.Empty(h.Sent);
        Assert.Equal(Direction.E, requested);
        Assert.NotNull(doorReply);
        Assert.Equal(LoopState.Running, h.Runner.State);

        // FSM reports the door open — the circuit crosses with the cardinal.
        doorReply!(DoorOpenResult.Opened.Instance);
        Assert.Single(h.Sent);
        Assert.Equal("e\r", Encoding.Latin1.GetString(h.Sent[0]));

        // Landing at Foyer completes the step. The return west is ALSO a closed
        // door, so it routes through the FSM again rather than firing a bare
        // cardinal — nothing new on the wire until that door reports open.
        h.Tracker.NoteRoomObserved(new RoomObservation("Foyer",
            new HashSet<Direction> { Direction.W }));
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.StepCompleted);
        Assert.Equal(Direction.W, requested);
        Assert.Single(h.Sent);

        // The return door opens; the circuit crosses back west.
        doorReply!(DoorOpenResult.Opened.Instance);
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("w\r", Encoding.Latin1.GetString(h.Sent[1]));
    }

    [Fact]
    public void Circuit_ClosedDoor_WithEnqueuer_FailsLoud_WhenDoorWontOpen()
    {
        // The door FSM exhausting its verbs (bash/pick out, key missing) must
        // surface as a loud Failed, not a silent stall — the same terminal
        // outcome as the no-enqueuer path, just reached through the FSM.
        Harness h = NewHarness(DoorGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));

        Action<DoorOpenResult>? doorReply = null;
        h.Runner.SetDoorEnqueuer((_, _, _, _, _, reply) => doorReply = reply);
        h.Runner.SetDoorStopper(() => { });

        h.Runner.Start(new Loop("house", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));
        Assert.NotNull(doorReply);
        Assert.Empty(h.Sent);

        doorReply!(new DoorOpenResult.Failed("bash exhausted"));

        Assert.Empty(h.Sent);
        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Contains(h.Events,
            e => e.Kind == LoopEventKind.Failed && e.Detail.Contains("door open failed"));
    }
}
