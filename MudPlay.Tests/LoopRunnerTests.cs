using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class LoopRunnerTests : IDisposable
{
    private readonly string _root;

    public LoopRunnerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-looprunner-tests-" + Path.GetRandomFileName());
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

    // Minimal in-memory IRoomFilter — a mutable avoided-room set the tests can
    // toggle to drive NotifyAvoidedChanged. All other filter gates fail open.
    private sealed class TestAvoidFilter : IRoomFilter
    {
        public HashSet<RoomKey> Avoided { get; } = new();
        public bool IsAvoided(RoomKey key) => Avoided.Contains(key);
    }

    private sealed class Harness : IDisposable
    {
        public required RoomTracker Tracker { get; init; }
        public required MovementCoordinator Coordinator { get; init; }
        public required LoopRunner Runner { get; init; }
        public required TestAvoidFilter Filter { get; init; }
        // Present only when NewHarness(wireRecovery: true). ResyncReasons records
        // every reason NoteSuspectedMismatch handed to the (stubbed) Paradigm
        // resync hook, so a test can assert the stall watchdog escalated.
        public EngineRecoveryGate? Gate { get; init; }
        public List<string> ResyncReasons { get; init; } = new();
        public List<byte[]> Sent { get; } = new();
        public List<LoopEvent> Events { get; } = new();
        // Resume-dispatch queue when the harness is built in deferred mode
        // (postToUi captures instead of running). Drain() runs them in order to
        // simulate the next UI tick. Empty/unused in the default synchronous mode.
        public required List<Action> Posted { get; init; }
        // Present only when NewHarness(withWalker: true).
        public AutoWalkManager? Walker { get; init; }
        public void Drain()
        {
            // Copy-then-clear so a posted action that re-posts (a chained resume)
            // lands in a fresh batch rather than mutating the list mid-iteration.
            Action[] batch = Posted.ToArray();
            Posted.Clear();
            foreach (Action a in batch) a();
        }
        public void Dispose() { }
    }

    // deferResume=false (default) runs resume dispatches synchronously so the
    // long-standing tests observe the immediate send they always did. Pass true
    // to capture them in Harness.Posted for manual Drain() — needed to interleave
    // a same-burst gate assert between a resume and its deferred send.
    private Harness NewHarness(string json = GraphJson, bool deferResume = false,
        bool wireRecovery = false, bool withWalker = false)
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
        List<Action> posted = new();
        // When requested, wire a real recovery gate with a stubbed resync hook so a
        // test can observe the stall watchdog escalating through NoteSuspectedMismatch.
        // Returning true mimics the Paradigm rm-resync fast-path (the gate pauses the
        // engine awaiting the authoritative reply).
        List<string> resyncReasons = new();
        EngineRecoveryGate? gate = null;
        if (wireRecovery)
        {
            gate = new EngineRecoveryGate(graph, tracker);
            gate.TryResync = reason => { resyncReasons.Add(reason); return true; };
        }
        TestAvoidFilter filter = new();
        // Constructed BEFORE the runner (when requested) so its RoomTracker.StateChanged
        // subscription registers first — matching AppServices' real construction order
        // (Walker before LoopRunner) and reproducing the same-burst reentrancy that order
        // depends on.
        AutoWalkManager? walker = withWalker ? new AutoWalkManager(graph, bfs, tracker, coord) : null;
        LoopRunner runner = new(tracker, coord, graph: graph, recovery: gate, bfs: bfs,
            walker: walker, filter: filter, postToUi: deferResume ? posted.Add : a => a());
        Harness h = new()
        {
            Tracker = tracker, Coordinator = coord, Runner = runner, Posted = posted,
            Gate = gate, ResyncReasons = resyncReasons, Filter = filter, Walker = walker,
        };
        runner.SetWireSender(b => h.Sent.Add(b));
        runner.Event += e => h.Events.Add(e);
        walker?.SetWireSender(b => h.Sent.Add(b));
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
    public void RenameCurrentLoop_UpdatesLiveNameAndFiresRenamed_WithoutDisruptingRun()
    {
        // Save-current on a still-running loop persists a rename without
        // restarting the cycle; the runner must reflect the new name in place so
        // the nav header stops holding the old (builder-generated) one.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        int sentBefore = h.Sent.Count;
        int indexBefore = h.Runner.CurrentIndex;

        h.Runner.RenameCurrentLoop("My Route");

        Assert.Equal("My Route", h.Runner.CurrentLoop?.Name);
        Assert.Contains(h.Events,
            e => e.Kind == LoopEventKind.Renamed && e.Detail == "My Route");
        // Rename must not disturb the lap: no extra step sent, same position,
        // still running.
        Assert.Equal(sentBefore, h.Sent.Count);
        Assert.Equal(indexBefore, h.Runner.CurrentIndex);
        Assert.Equal(LoopState.Running, h.Runner.State);
    }

    [Fact]
    public void RenameCurrentLoop_NoLoopOrUnchangedName_IsNoOp()
    {
        Harness h = NewHarness();

        // Nothing running — no crash, no event.
        h.Runner.RenameCurrentLoop("whatever");
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Renamed);

        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());

        // Same name / blank — no event fires.
        h.Runner.RenameCurrentLoop("ab");
        h.Runner.RenameCurrentLoop("   ");
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Renamed);
        Assert.Equal("ab", h.Runner.CurrentLoop?.Name);
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
    public void BlockedAtSource_LeansOnRmFirst_ReroutesFromCorrectedRoom()
    {
        // A "blocked at source" mismatch is exactly what a name-ambiguous zone
        // (many identically-named rooms sharing an exit pattern) can produce: the
        // tracker's Confirmed belief LOOKS right but is actually the wrong
        // physical room, so rerouting from it just repeats the same failure
        // (report paradigm-20260901-100523). Leaning on rm first — stubbed here
        // to resolve to a DIFFERENT room than the tracker's stale belief, mirroring
        // ParadigmPositionResolver hard-locating the tracker via SetLocated before
        // invoking the callback — must reroute from the CORRECTED room, not the
        // stale one.
        Harness h = NewHarness(wireRecovery: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));

        List<string> resyncCalls = new();
        h.Gate!.TryResyncOnce = (reason, onResolved, _) =>
        {
            resyncCalls.Add(reason);
            h.Tracker.SetLocated(new RoomKey(1, 2));   // rm's authoritative correction
            onResolved(new RoomKey(1, 2));
            return true;
        };

        h.Tracker.NoteMoveBlocked();   // reverts to Confirmed at the stale belief (1/1)

        Assert.Single(resyncCalls);
        // Rerouted from the corrected room (1/2): the next step is S (1/2 → 1/1),
        // not another N from the stale 1/1 belief.
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("s\r", Encoding.Latin1.GetString(h.Sent[1]));
        Assert.Equal(LoopState.Running, h.Runner.State);
    }

    [Fact]
    public void BlockedAtSource_RmUnavailable_FallsBackToTrustingTracker()
    {
        // Stock realm / no rm reply: TryResyncOnce returns false, so the existing
        // "trust the tracker, reroute immediately" behavior is unchanged.
        Harness h = NewHarness(wireRecovery: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        h.Gate!.TryResyncOnce = (_, _, _) => false;

        h.Tracker.NoteMoveBlocked();

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));   // re-sent from 1/1, unchanged
        Assert.Equal(LoopState.Running, h.Runner.State);
    }

    // ----- disconnect / reconnect resume --------------------------------

    [Fact]
    public void NotifyDisconnected_WhileIdle_DoesNothing()
    {
        Harness h = NewHarness();

        h.Runner.NotifyDisconnected();

        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Null(h.Runner.PendingReconnectResumeForTests);
        Assert.Empty(h.Events);
    }

    [Fact]
    public void NotifyDisconnected_WhileRunning_StopsCleanlyAndRemembersLoop()
    {
        // Live bug: nothing in the recovery ladder (the gate's Tier2/Tier3/
        // awaiting-rm wait, or this runner's own local EnterRecovery) has any way
        // to know the connection died mid-wait. It just sits there, and when the
        // wire comes back the FIRST post-reconnect room render gets fed into that
        // stale wait as if it were the landing/reply it was expecting — a false
        // "Lost" (report paradigm-20260901-191945). NotifyDisconnected must stop
        // cleanly (no Lost dialog) and remember the loop to resume.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Equal(LoopState.Running, h.Runner.State);

        h.Runner.NotifyDisconnected();

        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.NotNull(h.Runner.PendingReconnectResumeForTests);
        Assert.Equal("ab", h.Runner.PendingReconnectResumeForTests!.Name);
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Stopped && e.Detail.Contains("disconnected"));
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
    }

    [Fact]
    public void FirstPromptAfterDisconnect_ResumesTheRememberedLoop()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);   // step 1 ("n") of the original run

        h.Runner.NotifyDisconnected();
        h.Events.Clear();

        // First in-game prompt after reconnect — the same trigger
        // DeferredCollectReconnectReleaser uses. Tracker is still located at 1/1
        // (a real reconnect would have re-established it via the login sequence).
        h.Runner.FirePromptObservedForTests();

        Assert.Null(h.Runner.PendingReconnectResumeForTests);   // one-shot, consumed
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Started);
        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Equal(2, h.Sent.Count);   // fresh Start() sent step 1 again
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));
    }

    [Fact]
    public void PromptObserved_WithNoPendingReconnect_DoesNotReStartTheLoop()
    {
        // A prompt with nothing pending must fall through to the normal
        // custom-command-step handling, unaffected by the reconnect path.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        h.Events.Clear();

        h.Runner.FirePromptObservedForTests();

        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Started);
        Assert.Single(h.Sent);   // no extra send
    }

    [Fact]
    public void ResumeAfterPause_LandedAtUnexpectedThirdRoom_ForwardsToRecoveryGate()
    {
        // Live bug: a step's confirmation lands somewhere that's neither its
        // expected target (the overshoot guard) nor its source
        // (refused-while-paused) while the loop is paused — the exit's real
        // destination simply doesn't match what the graph said, or a name-
        // ambiguous zone misattributed the landing to the wrong room
        // entirely. None of the existing resume guards catch this shape, so
        // it fell through to a blind resend of the stale step on resume.
        // SendMove's fresh room-lookup can paper over that ONE hop by luck
        // (it reads the real current room, not a stale target), but the rest
        // of the 18-step plan was drawn for a route that no longer matches
        // reality from here, and the very next step hard-fails with "no exit"
        // one hop later (report paradigm-20260902-072545).
        Harness h = NewHarness(wireRecovery: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());   // step 1: N, expects 1/1 -> 1/2
        Assert.Single(h.Sent);

        h.Coordinator.AssertGate("Combat");   // pause mid-step

        // Landed at room C (1/3) — neither the expected target (1/2) nor the
        // source (1/1) — while paused. A real move genuinely completed
        // (unlike a refusal), just to somewhere the plan never expected.
        h.Tracker.NoteRoomObserved(new RoomObservation("C",
            new HashSet<Direction> { Direction.S }));

        h.Coordinator.ClearGate("Combat");   // resume

        Assert.Single(h.ResyncReasons);   // forwarded to the gate, not blindly resent
    }

    [Fact]
    public void RefusedWhilePaused_EntersRecoveryOnResume_InsteadOfResendingSameMove()
    {
        // Regression (paradigm-20260829-084558 / paradigm-20260829-104437): a
        // MoveRefusal that resolves WHILE a combat gate has the loop paused
        // reverts the tracker to Confirmed at the source room via
        // NoteMoveBlocked, but OnTrackerStateChanged ignores tracker events
        // while paused (State != Running), so the old resume path never saw
        // it — the step stayed marked in flight, and resume fell through to
        // blindly re-sending the exact same already-refused direction. That
        // resend got refused again, and with no gate left to clear and retry
        // this time, the loop just sat there — observed stalls of 17 minutes
        // and, in the worse report, over an hour, only ever "fixed" by an
        // unrelated external event forcing a fresh room observation. The fix
        // recognizes "Confirmed, still at the room the move was sent from" on
        // resume as the equivalent of the real-time "blocked at source"
        // branch and enters recovery (reroute) immediately instead.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));

        // Combat gate holds the loop while the move is still in flight.
        h.Coordinator.AssertGate(MovementCoordinator.CombatGate);
        Assert.Equal(LoopState.Paused, h.Runner.State);

        // The refusal resolves WHILE paused — the tracker correctly reverts
        // to Confirmed at 1/1, but the runner can't react to it in real time.
        h.Tracker.NoteMoveBlocked();
        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);

        // Resume must NOT blindly re-send "n" a second time on the stale
        // in-flight flag — it must enter recovery and reroute instead.
        h.Coordinator.ClearGate(MovementCoordinator.CombatGate);

        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
        Assert.Contains(h.Events, e =>
            e.Kind == LoopEventKind.Paused && e.Detail.Contains("recovering"));
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));
    }

    [Fact]
    public void RefusedWhilePaused_PersistentBlock_ExhaustsBudget_ThenFails()
    {
        // Same shape as BlockedAtSource_PersistentBlock_ExhaustsBudget_ThenFails,
        // but every refusal resolves while paused — confirms the resume-time
        // recovery path is bounded by MaxRecoverAttempts exactly like the
        // real-time one, not an unbounded retry loop.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());

        for (int i = 0; i < 4; i++)
        {
            h.Coordinator.AssertGate(MovementCoordinator.CombatGate);
            h.Tracker.NoteMoveBlocked();
            h.Coordinator.ClearGate(MovementCoordinator.CombatGate);
        }

        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Failed);
    }

    [Fact]
    public void BlockedAtSource_WhileConfused_DoesNotExhaustBudget()
    {
        // Report paradigm-20260902-113201: a confusion fumble ("You convulse
        // violently!") can bonk several moves in a row on the same room, well
        // inside MaxRecoverAttempts' window — charging those against the same
        // budget a genuine desync uses starved it in seconds and permanently
        // failed the loop while the character was otherwise fine, just waiting
        // out the status effect. More blocks than MaxRecoverAttempts while
        // confused must keep rerouting/resending, never fail.
        Harness h = NewHarness();
        h.Runner.SetConfusedCheck(() => true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());

        for (int i = 0; i < 6; i++)
        {
            h.Tracker.NoteMoveBlocked();
        }

        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
        // Initial send + one resend per block — every block rerouted, none skipped.
        Assert.Equal(7, h.Sent.Count);
        Assert.All(h.Sent, b => Assert.Equal("n\r", Encoding.Latin1.GetString(b)));
    }

    [Fact]
    public void BlockedAtSource_ConfusionClearing_GenuineBlockAfterwardStillExhaustsBudget()
    {
        // Confusion exempting recovery attempts from the budget must not leak
        // into a real problem once the status clears — a persistent block hit
        // right after confusion wears off still fails after MaxRecoverAttempts
        // genuine attempts, exactly like BlockedAtSource_PersistentBlock above.
        Harness h = NewHarness();
        bool confused = true;
        h.Runner.SetConfusedCheck(() => confused);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());

        for (int i = 0; i < 5; i++)
        {
            h.Tracker.NoteMoveBlocked();
        }
        Assert.Equal(LoopState.Running, h.Runner.State);   // unaffected while confused

        confused = false;
        for (int i = 0; i < 4; i++)
        {
            h.Tracker.NoteMoveBlocked();
        }

        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Failed);
    }

    [Fact]
    public void SuspectWhilePaused_ForwardsToRecoveryGateOnResume_InsteadOfResendingSameMove()
    {
        // Regression (paradigm-20260829-111627): an ambiguous room
        // observation that lands the tracker in Suspect WHILE the loop is
        // paused (a combat redisplay, another player's arrival, etc.) is
        // invisible to OnTrackerStateChanged in real time (State != Running)
        // — the same seam as the "refused while paused" fix above, but for
        // Suspect/Lost/Unknown instead of a plain refusal. The old resume
        // path fell through to a blind resend — but NoteMoveSentCore
        // deliberately never re-arms Pending from Suspect (no confirmed
        // anchor to predict a landing from), so a refusal on that resent
        // move is silently dropped too (NoteMoveBlocked only acts from
        // Pending), stranding the loop in Suspect with no way out. The fix
        // forwards to the recovery gate on resume, exactly like the
        // real-time branch already does.
        Harness h = NewHarness(wireRecovery: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);
        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);

        h.Coordinator.AssertGate(MovementCoordinator.CombatGate);
        Assert.Equal(LoopState.Paused, h.Runner.State);

        // An ambiguous/unrecognized observation lands the tracker in
        // Suspect while paused — the runner never sees the transition in
        // real time.
        h.Tracker.NoteRoomObserved(new RoomObservation("Somewhere Else",
            new HashSet<Direction> { Direction.N }));
        Assert.Equal(RoomConfidence.Suspect, h.Tracker.State.Confidence);

        // Resume must forward to the recovery gate, not blindly re-send "n".
        h.Coordinator.ClearGate(MovementCoordinator.CombatGate);

        Assert.Single(h.Sent);   // not re-sent
        Assert.Single(h.ResyncReasons);
        Assert.Contains("Suspect", h.ResyncReasons[0]);
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
    public void InFlightStall_ConfirmationNeverArrives_WatchdogEscalatesToRecovery()
    {
        // Regression (report paradigm-20260807-133143): a loop move went Pending,
        // a combat gate paused the loop before it confirmed, and after the kill the
        // move's confirmation never arrived (the interrupting combat swallowed it; in
        // a same-named-room zone no Confirmed transition ever fired). The resume path
        // correctly kept the step in flight "not re-sending" — but then the loop hung
        // for 5½ minutes with nothing to break the wait. The stall watchdog now
        // escalates to the recovery gate, which re-establishes position and reroutes.
        Harness h = NewHarness(wireRecovery: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);
        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);

        // Combat interrupts the in-flight move, then clears — no room ever observed,
        // so the tracker stays Pending on the move that will never confirm.
        h.Coordinator.AssertGate(MovementCoordinator.CombatGate);
        Assert.Equal(LoopState.Paused, h.Runner.State);
        h.Coordinator.ClearGate(MovementCoordinator.CombatGate);
        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Single(h.Sent);   // not re-sent

        // The wait window elapses with the move still wedged Pending → escalate.
        h.Runner.FireStallWatchdogForTests();

        Assert.Single(h.ResyncReasons);
        Assert.Contains("in-flight stall", h.ResyncReasons[0]);
    }

    [Fact]
    public void InFlightStall_NoPauseInvolved_WatchdogStillEscalates()
    {
        // Regression (reports paradigm-20260831-091353 and -100557: "the debuff
        // wore off and it got stuck" / "movement stopped again"): the stall
        // watchdog used to be armed only from the resume-reconciliation path
        // (see InFlightStall_ConfirmationNeverArrives_WatchdogEscalatesToRecovery
        // above), so an ordinary mid-loop move that went Pending with NO pause
        // anywhere near it had no timeout at all if its confirmation got
        // swallowed (there, a debuff reapplying the same instant the move was
        // sent). One incident hung 19s, the other over 4 minutes, both only
        // ending because the player noticed and filed a report. EmitCardinal now
        // arms the watchdog on every send, not just the resume path.
        Harness h = NewHarness(wireRecovery: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);
        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);

        // The real regression: FireStallWatchdogForTests bypasses arming (it
        // invokes the elapsed-handler directly regardless), so it can't catch
        // "never armed in the first place" -- this can.
        Assert.True(h.Runner.IsStallWatchdogArmedForTests);

        // No pause, no resume -- just the plain send above, then the wait window
        // elapses with the move still wedged Pending.
        h.Runner.FireStallWatchdogForTests();

        Assert.Single(h.ResyncReasons);
        Assert.Contains("in-flight stall", h.ResyncReasons[0]);
    }

    [Fact]
    public void ResumeWhileMoveInFlight_TrackerBecameSuspectDuringPause_RecoversWithoutReSending()
    {
        // Regression (paradigm-20260829-154032): an interleaved bright-cyan
        // player ability was parsed as the arriving room's name while Combat
        // had the loop paused. The tracker became Suspect, but the runner's
        // normal mismatch handler ignores transitions while Paused. Resume then
        // re-sent the already-completed N step from room B, where N was a wall.
        // The primary fix is RoomDisplayParser (it no longer misreads the ability
        // as the title); this pins the shared resume-time backstop that catches a
        // Suspect-on-resume regardless of cause (same guard as the 111627 case).
        Harness h = NewHarness(wireRecovery: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));

        h.Coordinator.AssertGate(MovementCoordinator.CombatGate);
        Assert.Equal(LoopState.Paused, h.Runner.State);

        // The observation carries B's exits but an impossible asynchronous
        // message as its name, reproducing the parser failure from the report.
        h.Tracker.NoteRoomObserved(new RoomObservation(
            "Astro invokes the way of the monkey!",
            new HashSet<Direction> { Direction.N, Direction.S }));
        Assert.Equal(RoomConfidence.Suspect, h.Tracker.State.Confidence);

        // Clearing Combat must request rm recovery and re-pause, never emit a
        // second N from the room the first N already reached.
        h.Coordinator.ClearGate(MovementCoordinator.CombatGate);
        Assert.Equal(LoopState.Paused, h.Runner.State);
        Assert.Single(h.Sent);
        Assert.Single(h.ResyncReasons);
        Assert.Contains("on resume at step", h.ResyncReasons[0]);

        // The authoritative reply says the original move reached B. Recovery
        // advances exactly once and sends the correct return step S.
        h.Tracker.SetLocated(new RoomKey(1, 2));
        h.Gate!.NoteAuthoritativePosition(new RoomKey(1, 2));

        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("s\r", Encoding.Latin1.GetString(h.Sent[1]));
        Assert.DoesNotContain(h.Sent.Skip(1), bytes => Encoding.Latin1.GetString(bytes) == "n\r");
    }

    [Fact]
    public void InFlightStall_StockRealmWithNoResync_StillBreaksTheWedge()
    {
        // Every other stall test stubs TryResync => true, i.e. the Paradigm
        // rm fast-path, so none of them exercised what a stock realm does. There
        // the escalation landed in tier 2 — a watch that only advances as the
        // engine executes FURTHER steps — and a wedged engine has none, so
        // nothing paused, nothing sent, and the watchdog (already stopped, and
        // re-armed only on a send or a resume) never fired again. The loop hung
        // permanently.
        Harness h = NewHarness(wireRecovery: true);
        h.Gate!.TryResync = _ => false;   // stock realm: there is no `rm` to ask
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);
        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);

        h.Runner.FireStallWatchdogForTests();

        // The escalation has to reach the tier-3 ladder, which can actually do
        // something about a stationary engine.
        // Control is handed to the tier-3 ladder, which can act on a stationary
        // engine (ground truth, then the reverse-walk, then a clean Lost dialog).
        // Tier 3's own convergence is covered in EngineRecoveryGateTests; what
        // matters here is that we no longer park in tier 2 with nothing pending.
        Assert.Equal(TierLevel.Tier3, h.Gate!.CurrentTier);
        Assert.Equal(LoopState.Paused, h.Runner.State);
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Paused && e.Detail.Contains("stall"));
    }

    [Fact]
    public void InFlightStall_Watchdog_NoOpAfterLoopStopped()
    {
        // The watchdog must not escalate once the loop is no longer running — a
        // late timer tick after a Stop is a no-op, not a spurious recovery.
        Harness h = NewHarness(wireRecovery: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        h.Runner.Stop();
        Assert.Equal(LoopState.Idle, h.Runner.State);

        h.Runner.FireStallWatchdogForTests();

        Assert.Empty(h.ResyncReasons);
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

    [Fact]
    public void ResumeDispatchDeferred_SameBurstRePause_DoesNotLeakNextMove()
    {
        // Regression (the @wait race): the coordinator fires PauseStateChanged
        // synchronously mid server-line burst. A Combat gate clearing on a room
        // re-display resumed the loop, and the OLD code dispatched the next move
        // synchronously — but a LATER line in the SAME burst (a party @wait
        // telepath) then asserted PartyWait. The move had already left, walking
        // us out of formation. The fix defers the resume dispatch past the burst
        // so the @wait re-pauses first and the deferred send aborts.
        Harness h = NewHarness(deferResume: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);                       // "n" for step 0 (target B)

        // A gate pauses while the move is in flight; the arrival lands during the
        // pause (tracker events are ignored while paused, so the step stays in
        // flight and resolves via the overshoot guard on resume).
        h.Coordinator.AssertGate(MovementCoordinator.CombatGate);
        Assert.Equal(LoopState.Paused, h.Runner.State);
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));

        // --- one server-line burst ---
        // Combat gate clears (room went empty) → resume; the advance+send is now
        // deferred, not run.
        h.Coordinator.ClearGate(MovementCoordinator.CombatGate);
        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Single(h.Posted);                     // dispatch queued, not sent
        Assert.Single(h.Sent);                        // nothing new on the wire yet
        // Later line in the SAME burst: a party @wait asserts PartyWait.
        h.Coordinator.AssertGate(MovementCoordinator.PartyWaitGate);
        Assert.Equal(LoopState.Paused, h.Runner.State);
        // --- burst ends; the deferred dispatch runs ---
        h.Drain();

        // The move did NOT leak past the @wait — still only the original "n".
        Assert.Single(h.Sent);

        // When the @wait finally clears, the deferred advance re-fires and the
        // step completes cleanly.
        h.Coordinator.ClearGate(MovementCoordinator.PartyWaitGate);
        Assert.Equal(LoopState.Running, h.Runner.State);
        h.Drain();
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("s\r", Encoding.Latin1.GetString(h.Sent[1]));
    }

    // Three-room line used by the double-advance regression below. The loop
    // 1/1 → 1/2 → 1/3 expands to [E (1→2), N (2→3), S (3→2), W (2→1)]. The key
    // property: room 1/2 ("B") has NO south exit, so a step-2 (S) move sent
    // while still physically at 1/2 fails "no exit S" — exactly the stale-room
    // send the double-advance produces.
    private const string LineGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/2", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "C",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void OvershootResume_TargetRedisplayInSameBurst_DoesNotDoubleAdvance()
    {
        // Regression (report paradigm-20260715-174119: "loop moves a room or two
        // then fails out and sits idle"). Combat pauses the loop as it enters a
        // room; the move confirms during the pause; then the kill fires a room
        // re-display AND clears the Combat gate in the same server-line burst. On
        // resume the overshoot guard schedules a deferred advance — but the SAME
        // re-display re-confirms the current room (Confirmed → Confirmed), which
        // OnTrackerStateChanged treats as the step's arrival and advances too. Two
        // advances for one completed step: the deferred body then sends the step
        // AFTER next from the pre-move room, "no exit" fails the lap, and the loop
        // detaches to Idle. The fix makes the deferred overshoot body a no-op when
        // the step it was scheduled to advance has already advanced.
        Harness h = NewHarness(LineGraphJson, deferResume: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(new Loop("line",
            new[] { new RoomKey(1, 1), new RoomKey(1, 2), new RoomKey(1, 3) }));
        Assert.Single(h.Sent);                        // step 0: "e" → 1/2
        Assert.Equal("e\r", Encoding.Latin1.GetString(h.Sent[0]));

        // Combat asserts as we enter 1/2; the move confirms while paused (tracker
        // events are ignored, so the step stays in flight and the overshoot guard
        // owns it on resume).
        h.Coordinator.AssertGate(MovementCoordinator.CombatGate);
        Assert.Equal(LoopState.Paused, h.Runner.State);
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.W, Direction.N }));

        // --- one server-line burst: kill clears the gate AND re-displays 1/2 ---
        h.Coordinator.ClearGate(MovementCoordinator.CombatGate);
        Assert.Equal(LoopState.Running, h.Runner.State);
        // The forced room re-display re-confirms the current room. Before the
        // deferred advance runs, this re-confirmation advances the step itself.
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.W, Direction.N }));
        Assert.Equal(2, h.Sent.Count);                // step 1: "n" → 1/3
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));

        // --- burst ends; the deferred overshoot dispatch runs ---
        h.Drain();

        // No double-advance: the loop did NOT send step 2 ("s") from the stale
        // room 1/2, did NOT fail, and is still running.
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
    }

    [Fact]
    public void ResumeAfterRecovery_CoordinatorStillPaused_DefersInsteadOfDoubleSending()
    {
        // Regression (Roomba Mode field report: a sweep crashed mid-run, stranding
        // ~15 picked-up items with no drop). Root cause is in shared LoopRunner /
        // MovementCoordinator plumbing, not Roomba-specific: an authoritative rm
        // resync (EngineRecoveryGate.NoteAuthoritativePosition) can resolve while an
        // UNRELATED MovementCoordinator gate is still asserted (e.g. GhSweepManager's
        // GhSort gate holding a room for a get/drop dispatch, or AutoSearchManager's
        // Search gate holding for a room-entry search). The old ResumeAfterRecovery
        // only checked its OWN State==Paused before resuming + sending the next
        // step's move — State==Paused is ambiguous between "my own recovery pause"
        // and "some other gate paused me", so it sent the next move while the OTHER
        // gate was still up, desyncing the loop's step counter one step early. On
        // this line graph (B has no south exit) the step AFTER that premature send
        // then fires "s" from the wrong room and fails "no exit S from B" — the exact
        // crash from the field report.
        Harness h = NewHarness(LineGraphJson, deferResume: true, wireRecovery: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(new Loop("line",
            new[] { new RoomKey(1, 1), new RoomKey(1, 2), new RoomKey(1, 3) }));
        Assert.Single(h.Sent);                        // step 0: "e" -> 1/2
        Assert.Equal(LoopState.Running, h.Runner.State);

        // An unrelated gate (standing in for GhSort / Search) asserts on arrival at
        // 1/2, the way GhSweepManager / AutoSearchManager do.
        h.Coordinator.AssertGate("TestOtherEngine");
        Assert.Equal(LoopState.Paused, h.Runner.State);
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.W, Direction.N }));

        // A resync that was already in flight (started before the gate asserted)
        // resolves now, to the room we're already standing in — the step's expected
        // target, which is the "recovered at expected target" fast path.
        h.Gate!.NoteSuspectedMismatch("test mismatch");
        Assert.Single(h.ResyncReasons);
        h.Gate.NoteAuthoritativePosition(new RoomKey(1, 2));

        // Must NOT have sent a second move — TestOtherEngine is still asserted, so
        // the resync resolving must defer to it instead of resuming right away.
        Assert.Single(h.Sent);
        Assert.Equal(LoopState.Paused, h.Runner.State);
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);

        // The other gate clears (its real dispatch finishes) — exactly one deferred
        // advance fires, sending the CORRECT next step ("n" -> 1/3), never "s" (which
        // would fail — B has no south exit).
        h.Coordinator.ClearGate("TestOtherEngine");
        Assert.Equal(LoopState.Running, h.Runner.State);
        h.Drain();

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
    }

    [Fact]
    public void RoomChangedSubscriber_RegisteredBeforeLoopRunner_HoldsTheLoopBeforeItAdvances()
    {
        // Regression (Roomba Mode field report: "its not stopping in the room,
        // its trying to run in grab stuff and the engine has it leaving the room
        // before they pick anything up"). Root cause: GhSweepManager originally
        // subscribed to RoomTracker.StateChanged in its OWN constructor, which
        // runs AFTER LoopRunner's constructor (GhSweepManager needs the LoopRunner
        // instance to exist first) — multicast delegates fire in registration
        // order, so GhSweepManager's handler always ran SECOND on every arrival,
        // after LoopRunner's own confirm-and-advance had already sent the next
        // move. The fix moved the subscription to an external wrapper lambda in
        // AppServices, registered BEFORE LoopRunner is constructed — the same
        // early-registration pattern AutoSearchManager's own working Search-gate
        // hold already relied on. This test proves the underlying mechanism
        // that fix depends on, independent of GhSweepManager's own wiring: a
        // reactor subscribed to RoomTracker.StateChanged BEFORE LoopRunner's own
        // subscription can hold the loop (via a MovementCoordinator gate) before
        // LoopRunner ships the next move; constructing LoopRunner first (as
        // GhSweepManager used to, indirectly) would let the next move ship first.
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        BfsMapper bfs = new(graph);

        // The "reactor" — asserts a gate the instant it sees arrival at B (1/2),
        // exactly what GhSweepManager.OnRoomChanged does on arrival at a labeled
        // room. Registered BEFORE the LoopRunner below is even constructed.
        bool reactorFired = false;
        tracker.StateChanged += t =>
        {
            if (t.NewRoom is not { } room || !room.Key.Equals(new RoomKey(1, 2))) return;
            reactorFired = true;
            coord.AssertGate("TestReactorGate");
        };

        TestAvoidFilter filter = new();
        // Deferred resume-dispatch mode (captured in `posted`, drained manually) —
        // the same mode every other same-burst-pause test in this file uses;
        // resume dispatches are deliberately posted past the burst rather than run
        // inline, so the test drives that explicitly via Drain() below.
        List<Action> posted = new();
        LoopRunner runner = new(tracker, coord, graph: graph, bfs: bfs, filter: filter,
            postToUi: posted.Add);
        List<byte[]> sent = new();
        runner.SetWireSender(sent.Add);
        List<LoopEvent> events = new();
        runner.Event += e => events.Add(e);

        tracker.SetLocated(new RoomKey(1, 1));
        runner.Start(AbCycle());
        Assert.Single(sent);   // step 0: "n" -> 1/2

        // Arrival at 1/2 — the reactor's handler (registered first) runs before
        // LoopRunner's own OnTrackerStateChanged (registered second via the
        // constructor above), asserting TestReactorGate before LoopRunner gets a
        // chance to decide whether to advance.
        tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));

        Assert.True(reactorFired);
        Assert.True(coord.IsGateAsserted("TestReactorGate"));
        // The loop must NOT have shipped the next move ("s") while the reactor's
        // gate is still up — held, not raced past.
        Assert.Single(sent);
        Assert.Equal(LoopState.Paused, runner.State);

        // The reactor's own dispatch finishes and clears its gate — the resume
        // dispatch is queued (posted), not yet sent.
        coord.ClearGate("TestReactorGate");
        Assert.Equal(LoopState.Running, runner.State);
        Assert.Single(sent);
        Assert.Single(posted);

        // Burst ends; the deferred resume dispatch runs — exactly one new move.
        Action[] batch = posted.ToArray();
        posted.Clear();
        foreach (Action a in batch) a();

        Assert.Equal(2, sent.Count);
        Assert.Equal("s\r", Encoding.Latin1.GetString(sent[1]));
        Assert.DoesNotContain(events, e => e.Kind == LoopEventKind.Failed);
    }

    [Fact]
    public void RepeatStarted_SubscriberAssertsGateSynchronously_HoldsBeforeWrapAroundMoveShips()
    {
        // Regression (Roomba Mode field report: an entire room's worth of `get`
        // commands dispatched while already standing in a DIFFERENT room —
        // more severe than the earlier one-step-late desync). Root cause:
        // SendNextStep checks State/_stepInFlight ONCE at entry, then — on a
        // lap wrap — calls Raise(RepeatStarted) and unconditionally falls
        // through to send the new lap's first move, without re-checking
        // whether the Raise() call itself changed State. A RepeatStarted
        // subscriber that synchronously asserts a MovementCoordinator gate
        // (e.g. a room-arrival dispatcher holding the room the loop just
        // wrapped back into) gets silently overridden: the loop ships the
        // wrap-around move anyway, physically leaving that room while the
        // subscriber's own commands are still queued/outstanding against it.
        // The fix re-checks State/_stepInFlight immediately after Raise()
        // returns and aborts the fall-through if either changed.
        Harness h = NewHarness(deferResume: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Single(h.Sent);   // step 0: "n" -> 1/2

        // Complete step 0 normally (no gate involved yet).
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        Assert.Equal(2, h.Sent.Count);   // step 1: "s" -> 1/1

        // A RepeatStarted subscriber that asserts a gate the instant the lap
        // wraps — the same thing a room-arrival dispatcher does on arrival.
        h.Runner.Event += e =>
        {
            if (e.Kind == LoopEventKind.RepeatStarted) h.Coordinator.AssertGate("TestDispatchGate");
        };

        // Completing step 1 lands back at 1/1 — the lap wraps, RepeatStarted
        // fires, the subscriber above asserts the gate mid-event, and
        // SendNextStep (still executing, several frames up) must NOT ship
        // the new lap's first move ("n" again) while that gate is up.
        h.Tracker.NoteRoomObserved(new RoomObservation("A",
            new HashSet<Direction> { Direction.N }));

        Assert.True(h.Coordinator.IsGateAsserted("TestDispatchGate"));
        Assert.Equal(2, h.Sent.Count);   // NOT 3 — the wrap-around move must be held
        Assert.Equal(LoopState.Paused, h.Runner.State);
        Assert.Equal(1, h.Runner.CompletedLaps);

        // The dispatcher's own work finishes and clears the gate — the held
        // move ships, exactly once, once the burst ends and the deferred
        // resume dispatch runs.
        h.Coordinator.ClearGate("TestDispatchGate");
        h.Drain();

        Assert.Equal(3, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[2]));
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
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

    // A real walker's arrival-confirming observation fires RoomTracker.StateChanged
    // once — but the walker's own subscription (registered first, mirroring
    // AppServices' construction order) synchronously hands off into
    // LoopRunner.BeginCircle/SendNextStep before the SAME dispatch reaches the
    // runner's own StateChanged subscription. Without deferring that hand-off, the
    // runner would process the walker's already-consumed arrival transition against
    // its own freshly-advanced Running/step-in-flight state and misread it as a bad
    // landing of the step it had just sent (report paradigm-20260901-090044).
    [Fact]
    public void ApproachArrival_DoesNotLeakStaleTransitionAsCircleStepMismatch()
    {
        Harness h = NewHarness(deferResume: true, wireRecovery: true, withWalker: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));

        // 1/1 isn't a waypoint of this loop, so Start takes the walker-approach
        // branch: closest waypoint is 1/2, one hop north.
        Loop loop = new("bc", new[] { new RoomKey(1, 2), new RoomKey(1, 3) });
        Assert.True(h.Runner.Start(loop));
        Assert.Equal(LoopState.Approaching, h.Runner.State);
        Assert.Single(h.Sent);   // the approach's "n"

        // Confirm the approach's arrival at 1/2 — the walker's single-hop path
        // completes on this one observation, synchronously firing Finished into
        // the runner before this same dispatch reaches the runner's own
        // subscription for it.
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));

        // The fix defers BeginCircle past the current dispatch, so the circle
        // hasn't started yet — and, critically, nothing wrongly escalated to
        // recovery off the walker's own already-consumed transition.
        Assert.Equal(LoopState.Approaching, h.Runner.State);
        Assert.Single(h.Sent);
        Assert.Empty(h.ResyncReasons);

        h.Drain();

        // The deferred hand-off now runs cleanly: circle begins, step 1 sent.
        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));
        Assert.Empty(h.ResyncReasons);
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
    public void NotifyAvoidedChanged_RoomOffLoop_ContinuesUninterrupted()
    {
        // Report 160212 / 160829: toggling avoid on a room the loop never
        // traverses must NOT disturb the running loop — no Stop, no Start, no
        // session reset. The AbCycle visits 1/1 and 1/2; room 1/3 is off-path.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        h.Events.Clear();

        h.Filter.Avoided.Add(new RoomKey(1, 3));   // off the loop
        h.Runner.NotifyAvoidedChanged();

        Assert.Empty(h.Events);                     // loop left completely alone
        Assert.Equal(LoopState.Running, h.Runner.State);
    }

    [Fact]
    public void NotifyAvoidedChanged_RoomOnLoop_ReRoutesWithoutReFiringSessionStart()
    {
        // When the avoided room IS on the loop's path, re-plan around it — but
        // still keep the SAME session: the one-shot ReachedFirstWaypoint (whose
        // side effects are a session-stats reset + party @reset) must not re-fire.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(AbCycle());
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.ReachedFirstWaypoint);
        h.Events.Clear();

        h.Filter.Avoided.Add(new RoomKey(1, 2));   // a waypoint the loop traverses
        h.Runner.NotifyAvoidedChanged();

        // A re-route was attempted (not the silent "path clear" no-op)…
        Assert.Contains(h.Events, e =>
            e.Kind is LoopEventKind.Started or LoopEventKind.Failed);
        // …and it did not re-arm the session-start one-shot.
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.ReachedFirstWaypoint);
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
    public void LastRunLoopName_SetOnStart_SurvivesStop()
    {
        // @path recovery: the last-run loop's name must outlive the run so a dead
        // / stopped player can be pointed back at their circuit.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.Null(h.Runner.LastRunLoopName);          // nothing run yet

        h.Runner.Start(AbCycle());
        Assert.Equal("ab", h.Runner.LastRunLoopName);

        h.Runner.Stop();
        Assert.Equal("ab", h.Runner.LastRunLoopName);   // retained past the stop
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
    public void ClosedDoorInFlight_CombatPauseThenResume_WaitsForDoor_DoesNotRecoverOrResend()
    {
        // A door-open FSM in flight when a Combat gate pauses the loop must NOT be
        // aborted on resume. The FSM has set _stepInFlight and _expectedMoveSource
        // but hasn't crossed yet, so the tracker legitimately reads Confirmed at the
        // source room — which the resume-time "refused while paused" check would
        // otherwise misread as blocked-at-source and spuriously enter recovery,
        // burning a recover attempt and killing the in-progress open. The loop must
        // wait for the door reply instead.
        Harness h = NewHarness(DoorGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));

        Action<DoorOpenResult>? doorReply = null;
        h.Runner.SetDoorEnqueuer((_, _, _, _, _, reply) => doorReply = reply);
        h.Runner.SetDoorStopper(() => { });

        h.Runner.Start(new Loop("house", new[] { new RoomKey(1, 1), new RoomKey(1, 2) }));
        Assert.NotNull(doorReply);
        Assert.Empty(h.Sent);                        // door FSM in flight, nothing on the wire
        Assert.Equal(LoopState.Running, h.Runner.State);

        // Combat asserts mid-open, then clears.
        h.Coordinator.AssertGate(MovementCoordinator.CombatGate);
        Assert.Equal(LoopState.Paused, h.Runner.State);
        h.Coordinator.ClearGate(MovementCoordinator.CombatGate);

        // Resume must NOT recover and must NOT resend — the door FSM is still owed
        // its reply.
        Assert.DoesNotContain(h.Events, e => e.Kind == LoopEventKind.Failed);
        Assert.DoesNotContain(h.Events,
            e => e.Kind == LoopEventKind.Paused && e.Detail.Contains("recovering"));
        Assert.Empty(h.Sent);
        Assert.Equal(LoopState.Running, h.Runner.State);

        // The door finally opens — the loop crosses as normal, proving it only waited.
        doorReply!(DoorOpenResult.Opened.Instance);
        Assert.Single(h.Sent);
        Assert.Equal("e\r", Encoding.Latin1.GetString(h.Sent[0]));
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
