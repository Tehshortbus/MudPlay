using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Walker coverage — single-shot walk happy path, retry on
// blocked move, abort on desync, pause/resume via coordinator,
// destination-equals-source short-circuit, and superseding starts.
public sealed class AutoWalkManagerTests : IDisposable
{
    private readonly string _root;

    public AutoWalkManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-walker-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // ----- fixtures --------------------------------------------------
    //
    // 1/1 ──N── 1/2 ──N── 1/3
    private const string LineGraphJson = """
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


    // 1/1 ──N (Item: 807)── 1/2: the only route, gated on carrying item 807.
    // Mirrors the Lower Caverns orb gate at the shape the walker sees.
    private const string ItemGatedLineJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2 (Item: 807)", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;


    // 1/1 ──N (Key: 806 [or 101 picklocks])── 1/2: a locked door whose key is being
    // fetched by a summon detour. The Black Steel Gate shape.
    private const string KeyGatedLineJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2 (Key: 806 [or 101 picklocks])", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // A route with a cross-room lever detour: reaching 1/3 from 1/2 crosses
    // 1/2's N exit, gated by a lever in 1/4 (one E hop off 1/2). So the planned
    // path is the go-act-return round-trip E→1/4, pull, W→1/2, then N→1/3 — the
    // current room (1/2) is revisited on the return leg.
    //
    //   1/1 ─N─ 1/2 ─N (Hidden/Needs 1 Actions)─ 1/3
    //            │E
    //           1/4  (D slot → Action#1 for 1/2's N exit)
    private const string LeverDetourGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3 (Hidden/Needs 1 Actions, any order)", "S": "1/1", "E": "1/4", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "C",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "LeverRoom",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0",
            "D": "Action#1 [on the N exit of room 1/2]: pull lever" }
        ]
        """;

    // Stall watchdogs armed and still to run.
    private static int Watchdogs(Harness h) =>
        h.WalkTimers.PendingDelays.Count(d => d == AutoWalkManager.StallWatchdogInterval);

    private sealed class Harness : IDisposable
    {
        public required RoomGraphManager Graph { get; init; }
        public required BfsMapper Bfs { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required MovementCoordinator Coordinator { get; init; }
        public required AutoWalkManager Walker { get; init; }
        // Present only when NewHarness(wireRecovery: true).
        public EngineRecoveryGate? Gate { get; init; }
        public List<byte[]> Sent { get; } = new();
        public List<WalkEvent> Events { get; } = new();
        // The game's prompts, as the walker reads them.
        public required WirePromptScanner Prompt { get; init; }
        // The walker's timers: the stall watchdog, the voyage and the other waits.
        public ManualTimers WalkTimers { get; } = new();
        public void Dispose() { /* nothing to dispose */ }
    }

    private Harness NewHarness(string json = LineGraphJson, bool wireRecovery = false, string? tbinfoJson = null)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), json);
        // TBInfo so the teleport fixture's CMD-100 room legitimately teleports to
        // 7/131 — the Item→Teleport promotion is TBInfo-gated on the CMD actually
        // teleporting to the exit's target (only rooms with CMD 100 + an exit to
        // 7/131, i.e. the teleport tests, are affected).
        File.WriteAllText(Path.Combine(_root, "alpha", "TBInfo.json"),
            tbinfoJson ?? """[ { "Number": 100, "Action": "go arch:teleport 131 7\n" } ]""");
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        TBInfoStore tbinfo = new(cache);
        tbinfo.OnActiveSetChanged("alpha");
        RoomGraphManager graph = new(cache, log: null, tbinfo);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        EngineRecoveryGate? gate = wireRecovery ? new EngineRecoveryGate(graph, tracker) : null;
        WirePromptScanner prompt = new();
        AutoWalkManager walker = new(graph, bfs, tracker, coord, promptScanner: prompt, recovery: gate);
        Harness h = new()
        {
            Graph = graph,
            Bfs = bfs,
            Tracker = tracker,
            Coordinator = coord,
            Walker = walker,
            Gate = gate,
            Prompt = prompt,
        };
        walker.SetVoyageScheduler(h.WalkTimers.Arm);
        walker.SetWireSender(b => h.Sent.Add(b));
        walker.Event += evt => h.Events.Add(evt);
        return h;
    }

    // ----- happy path -----------------------------------------------

    [Fact]
    public void WalkTo_NoSourceRoom_FailsImmediately()
    {
        Harness h = NewHarness();
        // RoomTracker stays Unknown — no observation has been fed.
        bool started = h.Walker.WalkTo(new RoomKey(1, 3));

        Assert.False(started);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Equal(WalkState.Idle, h.Walker.State);
    }

    [Fact]
    public void HaltForAbandonedCombat_AnyEngineActive_AssertsGateWhileWalkerIdle()
    {
        // Report stock-20260731-010401: a loop (not a point-to-point walk)
        // abandoned a spawned hostile via an in-flight step. The point-to-point
        // walker is Idle while a loop drives, so the halt must still assert the
        // coordinator-wide AbandonedCombat gate — which pauses the loop — whenever
        // any engine is active, not only when this walker is walking.
        Harness h = NewHarness();
        Assert.Equal(WalkState.Idle, h.Walker.State);
        // Hostile present → Combat gate held, so the abandon hold isn't released.
        h.Coordinator.AssertGate(MovementCoordinator.CombatGate, "test", "hostile");

        // Nothing driving → halt no-ops.
        h.Walker.SetAnyEngineActiveCheck(() => false);
        h.Walker.HaltForAbandonedCombat("nothing active");
        Assert.DoesNotContain(MovementCoordinator.AbandonedCombatGate, h.Coordinator.AssertedGates);

        // A loop is driving (walker still Idle) → the abandon gate asserts.
        h.Walker.SetAnyEngineActiveCheck(() => true);
        h.Walker.HaltForAbandonedCombat("abandoned via loop step");
        Assert.Contains(MovementCoordinator.AbandonedCombatGate, h.Coordinator.AssertedGates);
    }

    [Fact]
    public void AbandonHold_SettlesPastCombatClear_ReleasesOnlyWhenSettleFires()
    {
        // The abandon gate must outlast the Combat-gate clear by a settle window so
        // a monster following us out has time to re-assert Combat before we resume
        // (report stock-20260731-010401). With a manual scheduler the release is
        // deferred until the settle callback fires.
        Harness h = NewHarness();
        Action? settle = null;
        h.Walker.SetVoyageScheduler((_, cb) => { settle = cb; return new NoopDisposable(); });
        h.Walker.SetAnyEngineActiveCheck(() => true);

        h.Coordinator.AssertGate(MovementCoordinator.CombatGate, "test", "hostile");
        h.Walker.HaltForAbandonedCombat("abandoned");
        Assert.Contains(MovementCoordinator.AbandonedCombatGate, h.Coordinator.AssertedGates);

        // Combat clears (we left the room) → the abandon gate must NOT release yet —
        // it's holding the settle window open.
        h.Coordinator.ClearGate(MovementCoordinator.CombatGate, "test", "left room");
        Assert.Contains(MovementCoordinator.AbandonedCombatGate, h.Coordinator.AssertedGates);
        Assert.NotNull(settle);

        // Settle elapses with no follower → release.
        settle!();
        Assert.DoesNotContain(MovementCoordinator.AbandonedCombatGate, h.Coordinator.AssertedGates);
    }

    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }

    [Fact]
    public void LastEvent_TracksMostRecentWalkEvent()
    {
        Harness h = NewHarness();
        Assert.Null(h.Walker.LastEvent);

        // A no-source walk fails immediately; LastEvent must retain that reason.
        h.Walker.WalkTo(new RoomKey(1, 3));

        Assert.NotNull(h.Walker.LastEvent);
        Assert.Equal(WalkEventKind.Failed, h.Walker.LastEvent!.Value.Kind);
    }

    [Fact]
    public void WalkTo_AlreadyAtDestination_FiresFinished()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));

        Assert.True(h.Walker.WalkTo(new RoomKey(1, 1)));
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void WalkTo_FirstStep_PutsDirectionOnWire()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));

        h.Walker.WalkTo(new RoomKey(1, 3));

        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Single(h.Sent);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Started);
    }

    // Report paradigm-20261006-221209: the pre-step gear swap (worn from the ready
    // check) asserted and cleared its gate in one go. The resume sent the step, then
    // the send the check was guarding sent it again: two `w` on the wire.
    [Fact]
    public void MoveReadyCheck_ThatPausesAndResumes_SendsTheStepOnce()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        bool swapped = false;
        h.Walker.SetMoveReadyCheck(() =>
        {
            if (swapped) return true;
            swapped = true;
            h.Coordinator.AssertGate(MovementCoordinator.GearSwapGate);
            h.Coordinator.ClearGate(MovementCoordinator.GearSwapGate);
            return true;
        });

        h.Walker.WalkTo(new RoomKey(1, 3));

        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Single(h.Sent);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));
    }

    // Reports stock-20260914-000112 / -000155: a step went out, its echo was displaced
    // off the prompt, and the tracker held the landing Pending as an ambiguous re-look
    // in an identically-named grid. The walker had no bound on that wait — it sat in
    // Walking with the step in flight until the player hand-typed a move. The watchdog
    // escalates to the recovery gate so the walk resyncs from ground truth instead.
    [Fact]
    public void StallWatchdog_StepInFlightStillPending_EscalatesToRecovery()
    {
        Harness h = NewHarness(wireRecovery: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));

        string? resyncReason = null;
        h.Gate!.TryResync = reason => { resyncReason = reason; return true; };

        h.Walker.WalkTo(new RoomKey(1, 3));
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Single(h.Sent);

        // No confirming observation ever arrives — the move's landing went unrecognised.
        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);
        h.WalkTimers.RunDue(AutoWalkManager.StallWatchdogInterval);

        Assert.NotNull(resyncReason);
    }

    // Each send re-arms the watchdog, so a timer that survives the walk is stale by
    // definition. It must no-op rather than escalate — an arrived walker is not a
    // wedged one, and resyncing it would yank a finished journey back into recovery.
    [Fact]
    public void StallWatchdog_WalkFinished_DoesNotEscalate()
    {
        Harness h = NewHarness(wireRecovery: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));

        bool resynced = false;
        h.Gate!.TryResync = _ => { resynced = true; return true; };

        h.Walker.WalkTo(new RoomKey(1, 3));
        // Both steps confirm; the walk reaches its destination and goes Idle.
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        h.Tracker.NoteRoomObserved(new RoomObservation("C",
            new HashSet<Direction> { Direction.S }));
        Assert.Equal(WalkState.Idle, h.Walker.State);

        h.WalkTimers.RunDue(AutoWalkManager.StallWatchdogInterval);

        Assert.False(resynced);
    }

    [Fact]
    public void Walker_AdvancesThroughPath_OnConfirmedSteps()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));

        h.Walker.WalkTo(new RoomKey(1, 3));

        // Walker.SendStep already invoked NoteMoveSent on the tracker;
        // simulate the server confirming step 1 (now at 1/2).
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));

        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Equal(2, h.Sent.Count);                       // step 2 sent
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));

        // Confirm step 2 (now at 1/3).
        h.Tracker.NoteRoomObserved(new RoomObservation("C",
            new HashSet<Direction> { Direction.S }));

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
        Assert.Equal(2, h.Events.Count(e => e.Kind == WalkEventKind.StepCompleted));
    }

    [Fact]
    public void JourneyOrigin_TracksWalkSource_AndClearsWhenIdle()
    {
        // The flee anchor: JourneyOrigin is null while idle, becomes the room
        // the walk was planned from once a walk starts, and clears back to null
        // when the walk finishes.
        Harness h = NewHarness();
        Assert.Null(h.Walker.JourneyOrigin);

        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));
        Assert.Equal(new RoomKey(1, 1), h.Walker.JourneyOrigin);

        // Walk to completion — origin clears when the walker returns to Idle.
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        h.Tracker.NoteRoomObserved(new RoomObservation("C",
            new HashSet<Direction> { Direction.S }));
        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Null(h.Walker.JourneyOrigin);
    }

    // ----- blocked retry --------------------------------------------

    [Fact]
    public void BlockedStep_RetriesOnce_ThenAdvances()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));

        // Walker already announced the move; the server refuses it →
        // tracker reverts Pending → Confirmed at the SAME room (1/1).
        h.Tracker.NoteMoveBlocked();

        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Retrying);
        Assert.Equal(2, h.Sent.Count);                       // retry sent
        Assert.Equal(WalkState.Walking, h.Walker.State);

        // Now the retry succeeds (walker re-announced the move on send).
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));

        Assert.Equal(3, h.Sent.Count);                       // next step
    }

    [Fact]
    public void BlockedTwice_HandsOffToReplan_NotImmediateFailure()
    {
        // Live bug: MaxRetriesPerStep's tight budget (1) can't tell a
        // genuinely blocked exit from a run of bad luck — two confusion
        // fumbles on the same direction in a row exhaust it just as fast as
        // a real block, and failing the WHOLE walk right there was too eager
        // (report paradigm-20260901-201514). Exhausting the per-step retry
        // now hands off to TryReplanOrFail instead of failing immediately.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));

        h.Tracker.NoteMoveBlocked();   // retry #1 (per-step budget)
        h.Tracker.NoteMoveBlocked();   // per-step budget exhausted -> hand off

        Assert.Equal(WalkState.Walking, h.Walker.State);   // still trying, not Idle
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Retrying);
    }

    [Fact]
    public void PersistentBlock_StillFailsCleanly_OnceReplanBudgetAlsoExhausts()
    {
        // The hand-off is still bounded: an exit that's genuinely,
        // persistently blocked (not just an unlucky fumble streak) eventually
        // fails cleanly rather than retrying forever.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));

        for (int i = 0; i < 20; i++)
        {
            if (h.Walker.State == WalkState.Idle) break;
            h.Tracker.NoteMoveBlocked();
        }

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    [Fact]
    public void PersistentBlock_WhileConfused_DoesNotExhaustReplanBudget()
    {
        // Report paradigm-20260902-173754: LoopRunner's own recovery budget was
        // already exempted from confusion fumbles (paradigm-20260902-113201),
        // but it can hand off into this walker's separate replan budget (e.g.
        // via BlockedTwice_HandsOffToReplan above), which had no such exemption
        // — a short burst of confusion fumbles during that fallback could still
        // exhaust MaxReplansPerWalk and fail the whole walk while the character
        // was otherwise fine, just waiting out the status effect.
        Harness h = NewHarness();
        h.Walker.SetConfusedCheck(() => true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));

        for (int i = 0; i < 10; i++)
        {
            h.Tracker.NoteMoveBlocked();
        }

        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    [Fact]
    public void ReplanBudget_ConfusionClearing_GenuineBlockAfterwardStillFails()
    {
        // Confusion exempting replan attempts from the budget must not leak into
        // a real problem once the status clears — a persistently blocked exit
        // hit right after confusion wears off still fails eventually, exactly
        // like PersistentBlock_StillFailsCleanly_OnceReplanBudgetAlsoExhausts.
        Harness h = NewHarness();
        bool confused = true;
        h.Walker.SetConfusedCheck(() => confused);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));

        for (int i = 0; i < 10; i++)
        {
            h.Tracker.NoteMoveBlocked();
        }
        Assert.Equal(WalkState.Walking, h.Walker.State);   // unaffected while confused

        confused = false;
        for (int i = 0; i < 20; i++)
        {
            if (h.Walker.State == WalkState.Idle) break;
            h.Tracker.NoteMoveBlocked();
        }

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    // ----- desync ---------------------------------------------------

    [Fact]
    public void DesyncedLanding_ReplansFromNewRoom()
    {
        // Walker plans 1/1 → 1/2 → 1/3 (N then N). Server skips ahead
        // and reports 1/3 ("C") after the first N. Old behaviour was
        // to Fail; new behaviour is to replan from the new location.
        // Since we're already at the destination after the
        // unexpected-landing observation, the replan immediately
        // Finishes — the walker never gets stuck waiting for a tracker
        // event that will never come.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));

        h.Tracker.NoteMoveSent(Direction.N);
        h.Tracker.NoteRoomObserved(new RoomObservation("C",
            new HashSet<Direction> { Direction.S }));

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Retrying);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    // Graph with duplicate names to force the ambiguous-Suspect
    // path. Walker steps N from 1/1 expecting "B" at 1/2; the
    // observation "Hall" with {S} matches both 1/3 and 1/4 → no
    // 1-of-1 candidate, ReconcileFromPending enters Suspect.
    private const string AmbiguousGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Hall",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Hall",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void Walker_TrackerEntersSuspectMidStep_RePlans()
    {
        // Live bug from a real run: user typed a manual movement at
        // the terminal mid-walk, the next room observation no longer
        // matched the walker's predicted target, tracker went Suspect.
        // OnTrackerStateChanged previously only handled Confirmed
        // transitions, so the walker sat with _stepInFlight=true
        // forever. Now it re-plans from the tracker's best-guess
        // current room — Suspect preserves the anchor, so re-planning
        // is possible (vs Lost where the room is cleared and the
        // walker fails cleanly).
        Harness h = NewHarness(AmbiguousGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));
        Assert.Equal(WalkState.Walking, h.Walker.State);
        int sentBeforeSuspect = h.Sent.Count;

        // Observation matches neither the predicted target ("B") nor
        // the source ("A"); two graph rooms match ("Hall" 1/3, 1/4)
        // → ambiguous → Suspect (anchor 1/1 preserved).
        h.Tracker.NoteRoomObserved(new RoomObservation("Hall",
            new HashSet<Direction> { Direction.S }));

        // Walker must emit a Retrying event referencing the re-plan
        // (NOT silently stuck). With the anchor still on 1/1 and the
        // destination still 1/2, the re-plan re-sends "n\r".
        Assert.Contains(h.Events,
            e => e.Kind == WalkEventKind.Retrying
                 && e.Detail.Contains("re-planning"));
        Assert.True(h.Sent.Count > sentBeforeSuspect,
            "Walker must re-send the next step after re-planning.");
        Assert.Equal(WalkState.Walking, h.Walker.State);
    }

    [Fact]
    public void Walker_InternalReplan_DoesNotRaiseStopped()
    {
        // Regression: a mid-step Suspect re-plan re-issues the walk to the
        // SAME destination through WalkTo, whose supersede branch used to
        // Stop() the in-flight walk and raise a Stopped event. A driving
        // reroute (AutoDepositManager and the shop routers subscribe to the
        // walker) read that Stopped as an external abort and tore itself
        // down mid-detour — the client reached the bank but never deposited
        // or returned. The re-plan must surface Retrying (and keep walking),
        // never Stopped: Stopped is reserved for a genuine external halt.
        Harness h = NewHarness(AmbiguousGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));
        Assert.Equal(WalkState.Walking, h.Walker.State);

        h.Tracker.NoteRoomObserved(new RoomObservation("Hall",
            new HashSet<Direction> { Direction.S }));

        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Retrying);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Stopped);
        Assert.Equal(WalkState.Walking, h.Walker.State);
    }

    [Fact]
    public void WalkTo_SupersedeSilently_RedirectsWithoutStopped()
    {
        // Regression: a path-item router redirects the in-flight walk to a shop
        // via WalkTo. supersedeSilently keeps that redirect from raising a Stopped
        // the router (subscribed to Walker.Event) reads as an external abort and
        // resets its own detour on — the "walked to the shop, sat idle, never
        // bought" bug. The redirect surfaces Started to a NEW destination, not Stopped.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));
        Assert.Equal(WalkState.Walking, h.Walker.State);
        h.Events.Clear();

        h.Walker.WalkTo(new RoomKey(1, 2), supersedeSilently: true);

        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Stopped);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Started);
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Equal(new RoomKey(1, 2), h.Walker.Destination);
    }

    [Fact]
    public void Walker_TrackerEntersLostMidStep_FailsCleanly()
    {
        // Lost clears the tracker's CurrentRoom — re-planning isn't
        // possible (no source to plan from). Walker fails with a
        // clear reason and goes Idle (vs sitting silently).
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));

        // OnGraphReloaded resets the tracker straight to Unknown
        // (currentRoom cleared), which surfaces to the walker as a
        // not-Confirmed transition that lacks a usable anchor. The
        // walker treats any non-Confirmed mid-step transition the
        // same — try to re-plan from CurrentRoom; if null, fail.
        h.Tracker.OnGraphReloaded();

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events,
            e => e.Kind == WalkEventKind.Failed
                 && e.Detail.Contains("walker can't continue"));
    }

    [Fact]
    public void Walker_TrackerEntersSuspectMidStep_RepeatedSuspect_FailsAfterCap()
    {
        // Re-plan cap (MaxReplansPerWalk = 2) — after that many
        // Suspect-driven re-plans the walker gives up cleanly rather
        // than ping-ponging forever when the user keeps typing manual
        // movements that knock the tracker off the rails.
        Harness h = NewHarness(AmbiguousGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));

        // Repeatedly knock the tracker into Suspect after each re-plan
        // — same ambiguous "Hall" observation each round. Three rounds
        // total (initial + cap of 2 re-plans).
        for (int i = 0; i < 3; i++)
        {
            h.Tracker.NoteRoomObserved(new RoomObservation("Hall",
                new HashSet<Direction> { Direction.S }));
        }

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events,
            e => e.Kind == WalkEventKind.Failed
                 && e.Detail.Contains("walker can't continue"));
    }

    // ----- pause / resume -------------------------------------------

    [Fact]
    public void CoordinatorPause_DuringWalk_HoldsWalker()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));
        int sentBeforePause = h.Sent.Count;

        h.Coordinator.AssertGate("user");

        Assert.Equal(WalkState.Paused, h.Walker.State);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Paused);

        // Tracker confirming step 1 while paused must NOT send step 2.
        h.Tracker.NoteMoveSent(Direction.N);
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));

        Assert.Equal(sentBeforePause, h.Sent.Count);
        Assert.Equal(WalkState.Paused, h.Walker.State);
    }

    // A Flee monster and a real HealthManager over a real walk. A walk the user has
    // paused is idle (user, 2026-10-10): no run. One a fight is holding is running:
    // the run goes out, and forwards it is the walk's own next step.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FleeFromMonster_AWalkTheUserPaused_IsIdle_OneHeldByAFightRuns(bool userPaused)
    {
        Harness h = NewHarness();
        MudPlay.Models.Profile.CombatSettings combat = new()
        {
            RunDirection = MudPlay.Models.Profile.RunDirection.Forward, RunDistance = 1, BreakBeforeFleeing = false,
        };
        using MudPlay.Game.Health.HealthManager health = new(
            new MudPlay.Game.PlayerState(), h.Coordinator,
            readSettings: () => new MudPlay.Models.Profile.HealthSettings(),
            isEnabled: () => true,
            readHangupCommand: null,
            getActiveMovementEngine: () => h.Walker.State != WalkState.Idle ? h.Walker : null,
            getLastSentDirection: null,
            readCombatSettings: () => combat,
            readGeneralSettings: null,
            hasEngageableHostiles: null);
        health.IsNavigationPausedByUser = () => h.Coordinator.IsGateAsserted(MovementCoordinator.UserGate);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));                           // N, N; the first is in flight

        h.Coordinator.AssertGate(userPaused ? MovementCoordinator.UserGate : MovementCoordinator.CombatGate);
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        health.NoteRoomChanged(new RoomKey(1, 2));
        int sentBefore = h.Sent.Count;

        MudPlay.Game.Health.FleeOutcome outcome = health.FleeFromMonster("ogre (#7) is here, relationship Flee", () => true);

        if (userPaused)
        {
            Assert.Equal(MudPlay.Game.Health.FleeOutcome.Paused, outcome);
            Assert.Equal(sentBefore, h.Sent.Count);
            Assert.False(health.IsFleeing);
            // No flee took the walk over: lifting the pause sends its next step.
            h.Coordinator.ClearGate(MovementCoordinator.UserGate);
            Assert.Equal(WalkState.Walking, h.Walker.State);
            Assert.Equal(sentBefore + 1, h.Sent.Count);
            return;
        }

        Assert.Equal(MudPlay.Game.Health.FleeOutcome.Started, outcome);
        Assert.Equal(sentBefore + 1, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[^1]));   // B → C, the way the walk goes
    }

    // A walk lives through a drop of the line. One a flee had paused when the line
    // dropped is handed back at the first game prompt of the next stay: the run is
    // over at once, nothing is sent offline, and then the walk goes on.
    [Fact]
    public void AFleeCutByADisconnect_ThePausedWalkGoesOnAfterTheReconnect()
    {
        Harness h = NewHarness();
        MudPlay.Models.Profile.CombatSettings combat = new()
        {
            RunDirection = MudPlay.Models.Profile.RunDirection.Forward, RunDistance = 1, BreakBeforeFleeing = false,
        };
        MudPlay.Game.PlayerState state = new();
        using MudPlay.Game.Health.HealthManager health = new(
            state, h.Coordinator,
            readSettings: () => new MudPlay.Models.Profile.HealthSettings(),
            isEnabled: () => true,
            readHangupCommand: null,
            getActiveMovementEngine: () => h.Walker.State != WalkState.Idle ? h.Walker : null,
            getLastSentDirection: null,
            readCombatSettings: () => combat,
            readGeneralSettings: null,
            hasEngageableHostiles: null);
        state.MaxHp = 200;
        state.Hp = 200;
        state.HasPromptData = true;                                   // and it stays set through a disconnect
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));
        h.Coordinator.AssertGate(MovementCoordinator.CombatGate);
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        health.NoteRoomChanged(new RoomKey(1, 2));
        Assert.Equal(MudPlay.Game.Health.FleeOutcome.Started,
            health.FleeFromMonster("ogre (#7) is here, relationship Flee", () => true));
        h.Coordinator.ClearGate(MovementCoordinator.CombatGate);
        Assert.Equal(WalkState.Paused, h.Walker.State);               // held by the flee
        int sentBefore = h.Sent.Count;

        health.EndFlee("disconnected", handBackLiveEngine: true);    // the line drops mid-run

        Assert.False(health.IsFleeInFlight);
        health.Evaluate();
        Assert.Equal(WalkState.Paused, h.Walker.State);               // nothing resumes offline
        Assert.Equal(sentBefore, h.Sent.Count);

        h.Tracker.SetLocated(new RoomKey(1, 2));                      // back in the game, same room
        health.NoteInGamePrompt();

        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Equal(sentBefore + 1, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[^1]));   // on to C
    }

    // A forward flee walks the walk's next steps. Asked in a room the walk came into
    // while paused (a fight there, or the flee itself pausing it as the move
    // confirms), the step that carried it in is still the one at the index: counted,
    // it sent the flee off with the direction just walked.
    [Theory]
    [InlineData(true)]    // paused by a gate before the move landed
    [InlineData(false)]   // paused by the flee before the walker saw the arrival
    public void ForwardPeek_InARoomEnteredWhilePaused_LeavesOutTheStepJustWalked(bool pausedByAGate)
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));                           // N, N; the first is in flight

        if (pausedByAGate) h.Coordinator.AssertGate(MovementCoordinator.CombatGate);
        else h.Walker.PauseForFlee("a Flee monster");
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));

        Assert.Equal(new RoomKey(1, 2), h.Tracker.State.CurrentRoom?.Key);
        Assert.Equal(WalkState.Paused, h.Walker.State);
        Assert.Equal(new[] { Direction.N }, h.Walker.PeekPlannedDirections(2));   // only B → C is left
    }

    // 1/1 ──N── 1/2 ──N── 1/3 ──N── 1/4
    private const string FourRoomLineJson = """
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
            "N": "1/4", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "D",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/3", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // A later leg of the same run: the earlier leg walked a step of the path the
    // index knows nothing of, and the walk no longer stands where its paused step
    // landed. Peeked from the index, the leg was planned two rooms behind itself.
    [Fact]
    public void ForwardPeek_ForALaterLegOfARun_StartsAfterTheStepsItHasWalked()
    {
        Harness h = NewHarness(FourRoomLineJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 4));                           // N, N, N; the first is in flight
        h.Walker.PauseForFlee("low HP");
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        Assert.Equal(new[] { Direction.N, Direction.N }, h.Walker.PeekPlannedDirections(3));   // leg 1: B → C → D

        h.Walker.SendBacktrackMove(Direction.N);
        h.Tracker.NoteRoomObserved(new RoomObservation("C",
            new HashSet<Direction> { Direction.N, Direction.S }));

        Assert.Equal(new RoomKey(1, 3), h.Tracker.State.CurrentRoom?.Key);
        Assert.Equal(new[] { Direction.N }, h.Walker.PeekPlannedDirections(3));                // leg 2: only C → D is left
    }

    // The other ordering: the walker saw the arrival, advanced and sent the next
    // step. That step has not landed, so it is still the one ahead.
    [Fact]
    public void ForwardPeek_WithTheNextStepInFlight_StartsWithThatStep()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));
        Assert.Equal(new[] { Direction.N, Direction.N }, h.Walker.PeekPlannedDirections(2));

        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));

        Assert.Equal(new[] { Direction.N }, h.Walker.PeekPlannedDirections(2));
    }

    [Fact]
    public void CoordinatorResume_AfterPause_ResumesWalk()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));
        h.Coordinator.AssertGate("user");
        h.Coordinator.ClearGate("user");

        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Resumed);
    }

    [Fact]
    public void Resume_AfterPipelinedAdvanceDuringPause_AdvancesIndex_SendsNextStep()
    {
        // Live bug: user pauses mid-walk, server's response to the in-
        // flight step lands during the pause, OnTrackerStateChanged
        // bails because State != Walking, so _index doesn't advance.
        // On resume the walker re-sent _path[_index] — the SAME
        // direction the player just executed — and the player drifted
        // one or more rooms past the path's planned route.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));      // 2-step path: N, N
        Assert.Single(h.Sent);                   // step 1 sent

        h.Coordinator.AssertGate("user");        // pause mid-step

        // Pipelined response to step 1 lands during pause — tracker
        // confirms B (1/2) but walker's OnTrackerStateChanged bails.
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        Assert.Single(h.Sent);                   // still just step 1

        h.Coordinator.ClearGate("user");         // resume

        // Reconciliation: tracker is at 1/2 which matches _path[0]'s
        // ExpectedTarget → _index advances to 1, walker sends step 2.
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("n\r", System.Text.Encoding.Latin1.GetString(h.Sent[1]));
        Assert.Equal(WalkState.Walking, h.Walker.State);
    }

    [Fact]
    public void Resume_AfterRefusalDuringPause_RePlansInsteadOfBlindlyResending()
    {
        // Live bug: a move refused while paused (a wall the graph THINKS
        // exists but the live server just refused) reverts the tracker to
        // the step's SOURCE room — same as before the move was ever sent.
        // OnTrackerStateChanged bails on that reverting transition (it gates
        // on State == Walking), so the walker never sees it happen. On
        // resume, the old code's only defense — "does the graph list this
        // exit?" — can't tell a genuinely-refused direction from a
        // never-attempted one, so it blindly resent the exact same doomed
        // move with no retry cap, once per pause/resume cycle, forever
        // (report paradigm-20260901-091527: five minutes of "There is no
        // exit in that direction!" on repeat). Re-planning instead routes
        // through TryReplanOrFail, which is bounded and raises Retrying —
        // that's the observable signal the fix took the safe path.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));      // 2-step path: N, N
        Assert.Single(h.Sent);                   // step 1 ("n") sent

        h.Coordinator.AssertGate("Combat");      // pause mid-step

        // The server refused the in-flight move while paused — same effect
        // MovementRefusalDetector produces on "There is no exit in that
        // direction!": the pending move drops and confidence reverts to
        // Confirmed at the room the move was sent FROM.
        h.Tracker.NoteMoveBlocked();

        h.Coordinator.ClearGate("Combat");       // resume

        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Retrying);
    }

    [Fact]
    public void Resume_AfterRefusalDuringPause_LeansOnRmBeforeReplanning()
    {
        // Same trigger as Resume_AfterRefusalDuringPause_RePlansInsteadOfBlindlyResending,
        // but rm is available this time and resolves to a DIFFERENT room than the
        // tracker's stale belief — mirroring a name-ambiguous mis-anchor (report
        // paradigm-20260901-100523). The replan must use the corrected room, not
        // the stale one.
        Harness h = NewHarness(wireRecovery: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));      // 2-step path: N, N
        Assert.Single(h.Sent);

        h.Coordinator.AssertGate("Combat");      // pause mid-step
        h.Tracker.NoteMoveBlocked();             // refused while paused, reverts to 1/1

        List<string> resyncCalls = new();
        h.Gate!.TryResyncOnce = (reason, onResolved, _) =>
        {
            resyncCalls.Add(reason);
            h.Tracker.SetLocated(new RoomKey(1, 2));   // rm's authoritative correction
            onResolved(new RoomKey(1, 2));
            return true;
        };

        h.Coordinator.ClearGate("Combat");       // resume

        Assert.Single(resyncCalls);
        // Replanned from the corrected room (1/2): a single hop (N, 1/2 → 1/3)
        // suffices, not another full N,N from the stale 1/1 belief.
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("n\r", System.Text.Encoding.Latin1.GetString(h.Sent[1]));
        Assert.Equal(WalkState.Walking, h.Walker.State);
    }

    // The stretch of river in report paradigm-20261009-082958, as the Paradigm 1.9.1
    // data has it. 14/3952 is another "Alba River" showing north and southeast, so
    // 14/3946's display alone doesn't say which of the two it is.
    private const string AlbaRiverGraphJson = """
        [
          { "Map Number": 14, "Room Number": 3944, "Name": "Alba River, Under a Stone Bridge",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "14/3945", "SE": "14/3805", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 14, "Room Number": 3945, "Name": "Alba River",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "14/3946", "SE": "14/3944", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 14, "Room Number": 3946, "Name": "Alba River",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "14/3947", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "14/3945", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 14, "Room Number": 3947, "Name": "Alba River",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "14/3948", "S": "14/3946", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 14, "Room Number": 3952, "Name": "Alba River",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "14/3953", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "14/3951", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private static readonly HashSet<Direction> AlbaRiver3945Exits = new() { Direction.NW, Direction.SE };
    private static readonly HashSet<Direction> AlbaRiver3946Exits = new() { Direction.N, Direction.SE };

    // Report paradigm-20261009-082958: the walk was a room further up the river than
    // the tracker had it. A fight held it in "Alba River" 14/3945; the room shown
    // during the fight was 14/3946 (north, southeast), which made the tracker Suspect
    // with the walker paused and deaf to it. When the fight ended the walk sent the
    // step planned from 14/3945, northwest, into a wall, and a refusal of a move sent
    // from Suspect wakes nothing: it sat there until the player typed `rm`. The resume
    // now asks where we are first and plans from the answer.
    [Fact]
    public void Resume_TrackerWentSuspectWhilePausedBetweenSteps_AsksRmInsteadOfSendingTheStep()
    {
        Harness h = HeldSuspectAtAlbaRiver(out LocateAsks rm, out Action fightEnds);

        fightEnds();

        Assert.Single(rm.Asked);
        Assert.Equal(new[] { "nw" }, SentText(h));
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Retrying && e.Detail.Contains("while paused"));

        // `Location: 14,3946`: the walk goes on north from there.
        rm.Answer(h, new RoomKey(14, 3946));

        Assert.Equal(new[] { "nw", "n" }, SentText(h));
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    // The game's locator (`rm`, `sys st`) as the walker sees it: every ask is kept
    // for the test to answer, fail or leave hanging. Off, there is nobody to ask.
    private sealed class LocateAsks
    {
        public bool Available { get; set; } = true;
        public List<(Action<RoomKey> Resolved, Action Failed)> Asked { get; } = new();

        public bool Ask(string reason, Action<RoomKey> onResolved, Action onFailed)
        {
            if (!Available) return false;
            Asked.Add((onResolved, onFailed));
            return true;
        }

        // The resolver locates the tracker before it calls back.
        public void Answer(Harness h, RoomKey key)
        {
            h.Tracker.SetLocated(key);
            Asked[^1].Resolved(key);
        }
    }

    // The report's fight: the walk up the river stands in 14/3945 with its next
    // step held (a monster in the room), paused for the fight, and the room shown
    // during it is 14/3946, which leaves the tracker Suspect on 14/3945.
    private Harness HeldSuspectAtAlbaRiver(out LocateAsks rm, out Action fightEnds)
    {
        Harness h = NewHarness(AlbaRiverGraphJson, wireRecovery: true);
        rm = new LocateAsks();
        h.Gate!.TryResyncOnce = rm.Ask;
        bool monsterInRoom = false;
        h.Walker.SetMoveReadyCheck(() => !monsterInRoom);
        h.Tracker.SetLocated(new RoomKey(14, 3944));
        h.Walker.WalkTo(new RoomKey(14, 3947));      // NW, NW, N
        Assert.Equal(new[] { "nw" }, SentText(h));

        monsterInRoom = true;
        h.Tracker.NoteRoomObserved(new RoomObservation("Alba River", AlbaRiver3945Exits));
        Assert.Equal(new RoomKey(14, 3945), h.Tracker.State.CurrentRoom?.Key);
        h.Coordinator.AssertGate(MovementCoordinator.CombatGate, "test", "daggerfish");
        Assert.Equal(new[] { "nw" }, SentText(h));

        h.Tracker.NoteRoomObserved(new RoomObservation("Alba River", AlbaRiver3946Exits));
        Assert.Equal(RoomConfidence.Suspect, h.Tracker.State.Confidence);

        fightEnds = () =>
        {
            monsterInRoom = false;
            h.Coordinator.ClearGate(MovementCoordinator.CombatGate, "test", "room cleared");
        };
        return h;
    }

    // Other gates come and go while the `rm` is out (a sneak settling right behind
    // the fight). Each resume used to ask again and spend a re-plan, and the third
    // failed the walk. The one ask stands and the walk waits for its answer.
    [Fact]
    public void Resume_GatesFlapWhileTheRmIsOut_AsksOnceAndWaits()
    {
        Harness h = HeldSuspectAtAlbaRiver(out LocateAsks rm, out Action fightEnds);
        fightEnds();

        for (int i = 0; i < 3; i++)
        {
            h.Coordinator.AssertGate(MovementCoordinator.PartyWaitGate, "test", "flap");
            h.Coordinator.ClearGate(MovementCoordinator.PartyWaitGate, "test", "flap");
        }

        Assert.Single(rm.Asked);
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Equal(new[] { "nw" }, SentText(h));

        rm.Answer(h, new RoomKey(14, 3946));
        Assert.Equal(new[] { "nw", "n" }, SentText(h));
    }

    // A pause that is still up when the answer comes: the re-plan starts paused and
    // goes on when the gate clears.
    [Fact]
    public void Resume_PausedAgainWhenTheRmAnswers_GoesOnWhenThePauseEnds()
    {
        Harness h = HeldSuspectAtAlbaRiver(out LocateAsks rm, out Action fightEnds);
        fightEnds();

        h.Coordinator.AssertGate(MovementCoordinator.AcquisitionGate, "test", "loot");
        rm.Answer(h, new RoomKey(14, 3946));
        Assert.Equal(new[] { "nw" }, SentText(h));

        h.Coordinator.ClearGate(MovementCoordinator.AcquisitionGate, "test", "done");

        Assert.Single(rm.Asked);
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Equal(new[] { "nw", "n" }, SentText(h));
    }

    // The answer belongs to the walk that asked. Stopped meanwhile, the walker
    // stays stopped.
    [Fact]
    public void RmAnswerAfterTheWalkWasStopped_DoesNotStartItAgain()
    {
        Harness h = HeldSuspectAtAlbaRiver(out LocateAsks rm, out Action fightEnds);
        fightEnds();

        h.Walker.Stop();
        rm.Answer(h, new RoomKey(14, 3946));

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Equal(new[] { "nw" }, SentText(h));
    }

    // And with another walk begun meanwhile, that walk keeps its own destination.
    [Fact]
    public void RmAnswerAfterAnotherWalkBegan_LeavesThatWalkAlone()
    {
        Harness h = HeldSuspectAtAlbaRiver(out LocateAsks rm, out Action fightEnds);
        fightEnds();

        h.Tracker.SetLocated(new RoomKey(14, 3946));
        h.Walker.WalkTo(new RoomKey(14, 3944));      // SE, SE
        Assert.Equal(new[] { "nw", "se" }, SentText(h));
        rm.Asked[0].Resolved(new RoomKey(14, 3946));

        Assert.Equal(new RoomKey(14, 3944), h.Walker.Destination);
        Assert.Equal(new[] { "nw", "se" }, SentText(h));
    }

    // The room the tracker holds is shown again before the fight ends: the doubt
    // settled itself, so nothing is asked and the step goes out.
    [Fact]
    public void Resume_SuspectSettledBeforeThePauseEnded_SendsTheStepWithoutAsking()
    {
        Harness h = HeldSuspectAtAlbaRiver(out LocateAsks rm, out Action fightEnds);
        h.Tracker.NoteRoomObserved(new RoomObservation("Alba River", AlbaRiver3945Exits));
        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);

        fightEnds();

        Assert.Empty(rm.Asked);
        Assert.Equal(new[] { "nw", "nw" }, SentText(h));
    }

    // A realm with nobody to ask (Stock without sysop powers). A re-plan would start
    // from the room the tracker already holds and send the same step, so the resume
    // sends it as it always did, and neither it nor the stall watchdog spends a
    // re-plan or disturbs the walk. The watchdog waits once more, and then lets be.
    [Fact]
    public void SuspectWithNobodyToAsk_ResumeAndWatchdogLeaveTheWalkAsItWas()
    {
        Harness h = HeldSuspectAtAlbaRiver(out LocateAsks rm, out Action fightEnds);
        rm.Available = false;

        fightEnds();

        Assert.Equal(new[] { "nw", "nw" }, SentText(h));
        Assert.Equal(1, Watchdogs(h));
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Retrying);

        // The game refuses the move; from Suspect that changes nothing.
        h.Tracker.NoteMoveBlocked();
        h.WalkTimers.RunDue(AutoWalkManager.StallWatchdogInterval);
        Assert.Equal(1, Watchdogs(h));                      // one more wait
        h.WalkTimers.RunDue(AutoWalkManager.StallWatchdogInterval);
        Assert.Equal(0, Watchdogs(h));                      // and then it lets be

        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Equal(new[] { "nw", "nw" }, SentText(h));
        Assert.Equal(TierLevel.Tier1, h.Gate!.CurrentTier);
        Assert.DoesNotContain(h.Events, e => e.Kind is WalkEventKind.Retrying or WalkEventKind.Failed);
    }

    // The ask at the resume didn't come off (the resolver throttles one `rm` every
    // two seconds), so the step went out from the room in doubt and was refused: the
    // report's strand by another road. The stall watchdog asks, once, and the walk
    // goes on from the answer.
    [Fact]
    public void StallWatchdog_MoveSentFromSuspectAndRefused_AsksRmOnce()
    {
        Harness h = HeldSuspectAtAlbaRiver(out LocateAsks rm, out Action fightEnds);
        rm.Available = false;
        fightEnds();
        Assert.Equal(new[] { "nw", "nw" }, SentText(h));
        h.Tracker.NoteMoveBlocked();
        Assert.Equal(RoomConfidence.Suspect, h.Tracker.State.Confidence);

        rm.Available = true;
        h.WalkTimers.RunDue(AutoWalkManager.StallWatchdogInterval);
        h.WalkTimers.RunDue(AutoWalkManager.StallWatchdogInterval);

        Assert.Single(rm.Asked);
        Assert.Equal(new[] { "nw", "nw" }, SentText(h));

        rm.Answer(h, new RoomKey(14, 3946));
        Assert.Equal(new[] { "nw", "nw", "n" }, SentText(h));
        Assert.Equal(WalkState.Walking, h.Walker.State);
    }

    // The `rm` asked at the resume went unanswered (a confusion fumble eats it): the
    // walk re-plans from the room it holds, the same step goes out and is refused,
    // and the watchdog's ask is the second and last.
    [Fact]
    public void RmUnansweredAtTheResume_WatchdogAsksAgainAfterTheRefusal()
    {
        Harness h = HeldSuspectAtAlbaRiver(out LocateAsks rm, out Action fightEnds);
        fightEnds();
        rm.Asked[0].Failed();
        Assert.Equal(new[] { "nw", "nw" }, SentText(h));

        h.Tracker.NoteMoveBlocked();
        h.WalkTimers.RunDue(AutoWalkManager.StallWatchdogInterval);

        Assert.Equal(2, rm.Asked.Count);
        rm.Answer(h, new RoomKey(14, 3946));
        Assert.Equal(new[] { "nw", "nw", "n" }, SentText(h));
    }

    // The same fight, with the pause landing while the step into 14/3945 was still
    // in flight. The resume used to count that step as arrived from the Suspect
    // tracker's room and send the next one from it.
    [Fact]
    public void Resume_TrackerWentSuspectWhilePausedMidStep_AsksRmInsteadOfSendingTheNextStep()
    {
        Harness h = NewHarness(AlbaRiverGraphJson, wireRecovery: true);
        List<string> rmReasons = new();
        h.Gate!.TryResyncOnce = (reason, _, _) => { rmReasons.Add(reason); return true; };
        h.Tracker.SetLocated(new RoomKey(14, 3944));
        h.Walker.WalkTo(new RoomKey(14, 3947));
        Assert.Equal(new[] { "nw" }, SentText(h));

        h.Coordinator.AssertGate(MovementCoordinator.CombatGate, "test", "daggerfish");
        h.Tracker.NoteRoomObserved(new RoomObservation("Alba River", AlbaRiver3945Exits));
        h.Tracker.NoteRoomObserved(new RoomObservation("Alba River", AlbaRiver3946Exits));
        Assert.Equal(RoomConfidence.Suspect, h.Tracker.State.Confidence);

        h.Coordinator.ClearGate(MovementCoordinator.CombatGate, "test", "room cleared");

        Assert.Single(rmReasons);
        Assert.Equal(new[] { "nw" }, SentText(h));
    }

    // Where the same report's walk went wrong to begin with, replayed through the
    // fix that shipped after it was captured (3.153.19). An `rm` was asked from
    // Ghille Village, Docks 14/3799, and the walk's first `w` went out behind it.
    // The answer, `Location: 14,3799`, re-located the tracker and dropped that move,
    // so the walk sent `w` again: one `w` too many, refused four rooms on at
    // 14/3805 while a `nw` was pending, which was then sent twice in its turn.
    [Fact]
    public void RmAnsweredBehindTheWalksFirstMove_DoesNotSendTheMoveAgain()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Info.json"), "[{\"Legit\":2}]");   // a ParaMud set
        const string docksJson = """
            [
              { "Map Number": 14, "Room Number": 3799, "Name": "Ghille Village, Docks",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "0", "W": "14/3800",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "14/3798", "D": "0" },
              { "Map Number": 14, "Room Number": 3800, "Name": "Ghille Pond, Near Docks",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "14/3802", "E": "14/3799", "W": "14/3803",
                "NE": "0", "NW": "14/3801", "SE": "0", "SW": "14/3813", "U": "0", "D": "0" },
              { "Map Number": 14, "Room Number": 3803, "Name": "Ghille Pond",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "14/3801", "S": "14/3813", "E": "14/3800", "W": "14/3804",
                "NE": "0", "NW": "14/3806", "SE": "14/3802", "SW": "14/3812", "U": "0", "D": "0" }
            ]
            """;
        Harness h = NewHarness(docksJson, wireRecovery: true);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        MessageRouter router = new();
        MudPlay.Services.Patterns.DefaultPatterns.Seed(router);
        using ParadigmPositionResolver resolver = new(router, h.Tracker, h.Gate!, cache, log: null, schedule: new ManualTimers().Arm);
        List<byte[]> rmSent = new();
        resolver.SetWireSender(rmSent.Add);
        h.Tracker.SetLocated(new RoomKey(14, 3799));

        Assert.True(resolver.TryRequestResync("step 44 landed at 14/3799 on resume (expected 14/4944)"));
        h.Walker.WalkTo(new RoomKey(14, 3803));      // W, W
        Assert.Equal(new[] { "w" }, SentText(h));

        new ServerFeed(router).Line("Location: 14,3799");

        Assert.Equal(new[] { "w" }, SentText(h));
        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);

        h.Tracker.NoteRoomObserved(new RoomObservation("Ghille Pond, Near Docks",
            new HashSet<Direction> { Direction.S, Direction.E, Direction.W, Direction.NW, Direction.SW }));
        Assert.Equal(new RoomKey(14, 3800), h.Tracker.State.CurrentRoom?.Key);
        Assert.Equal(new[] { "w", "w" }, SentText(h));
    }

    [Fact]
    public void RemainingRoomKeys_TrimsToCurrentRoom_WhilePausedMidStep()
    {
        // Report 203928: stepping into a room that starts combat pauses the
        // walker before the move-confirming exits line lands, so
        // OnTrackerStateChanged bails without advancing _index. The drawn
        // walk-to route then kept looping back through the room just entered
        // until combat ended and the walk resumed. RemainingRoomKeys must
        // trim to the CURRENT room even while paused with a stale _index.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));      // 2-step path: A→N→B→N→C

        h.Coordinator.AssertGate("Combat");      // combat pauses the walker
        Assert.Equal(WalkState.Paused, h.Walker.State);

        // The in-flight step resolves during the pause — tracker confirms B
        // (1/2) but the walker leaves _index pointing at the leg just walked.
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));

        // Overlay starts at B (current) and shows ONLY the untraversed leg to
        // C — not [B, B, C], which would redraw the leg already walked.
        Assert.Equal(
            new[] { new RoomKey(1, 2), new RoomKey(1, 3) },
            h.Walker.RemainingRoomKeys);
    }

    [Fact]
    public void RemainingRoomKeys_RendersFullOutAndBackDetour_NotJustStraightLine()
    {
        // Report paradigm-20260818-000725: on a lever-vault route the planned
        // path loops out to a lever room and back, so the current room recurs
        // later in the path. The old trim scanned the whole path for the current
        // room and matched that return leg, collapsing the entire detour out of
        // the drawn line + the ETA (which then oscillated). The overlay must
        // render the full out-and-back round-trip.
        Harness h = NewHarness(LeverDetourGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 2));

        // Path from 1/2 to 1/3: E→1/4, pull lever, W→1/2, N→1/3. The walker
        // stands at 1/2 (current) with _index at the outbound E step.
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));

        // Full detour: out to the lever room (1/4), back to 1/2, then on to 1/3
        // — NOT the collapsed straight line [1/2, 1/3].
        Assert.Equal(
            new[]
            {
                new RoomKey(1, 2), new RoomKey(1, 4), new RoomKey(1, 2), new RoomKey(1, 3),
            },
            h.Walker.RemainingRoomKeys);
    }

    // Report paradigm-20261007-202509: a route that goes out to turn a statue and
    // comes back passes the same room twice. A pause between steps there (a held
    // cast going out) resumed at the LATER visit, so the walk skipped the statue
    // and was refused at the exit it opens. Standing where the walk left off, a
    // resume carries on from that step.
    [Fact]
    public void Resume_OnARouteThatComesBackThroughThisRoom_DoesNotSkipTheDetour()
    {
        Harness h = NewHarness(LeverDetourGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 2));

        // Held before the first step can go: 1/2 is both where the walk stands and
        // where the return leg from the lever room ends.
        h.Coordinator.AssertGate("user");
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));   // E→1/4, pull lever, W→1/2, N→1/3
        Assert.Empty(h.Sent);

        h.Coordinator.ClearGate("user");

        Assert.Equal("e\r", System.Text.Encoding.Latin1.GetString(Assert.Single(h.Sent)));
        Assert.Equal(WalkState.Walking, h.Walker.State);
    }

    [Fact]
    public void Resume_StepStillInFlight_DoesNotResend_ConfirmsOnArrival()
    {
        // Pause + resume with the in-flight move's confirmation not yet
        // landed (tracker still Pending on it, no room arrival during the
        // pause). The old walker blindly re-sent _path[_index] on resume —
        // a duplicate move on the wire that wedged the tracker's pending
        // queue. The party-split (chime) teleport hit this: the PartyInvite
        // reform gate asserts then clears mid-teleport (followers relay
        // through and rejoin faster than the destination room render lands),
        // so resume fired before arrival confirmed and the teleport re-sent —
        // re-firing the reform (spamming @join at already-rejoined members)
        // and stranding the walk. Fix: keep the step in flight; the resumed
        // tracker events confirm it and advance.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));      // 2-step path: N, N
        Assert.Single(h.Sent);                   // step 1 sent; tracker Pending

        h.Coordinator.AssertGate("user");
        h.Coordinator.ClearGate("user");

        // No arrival during the pause — the step is still in flight, so
        // resume must NOT re-send it.
        Assert.Single(h.Sent);
        Assert.Equal(WalkState.Walking, h.Walker.State);

        // The destination render finally lands — the walker advances and
        // sends the next step, so the walk continues rather than stalling.
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("n\r", System.Text.Encoding.Latin1.GetString(h.Sent[1]));
    }

    [Fact]
    public void Resume_PlayerAtOffPathRoom_TriggersReplan()
    {
        // User pauses, types a manual movement that takes them off
        // the planned path, resumes. The reconciliation can't find
        // the current room in _path[_index..] AND it doesn't match
        // _path[_index-1]'s target either → re-plan.
        //
        // Fixture: 1/1 has an extra W exit to a sibling that's not
        // on the path to 1/3. Walker plans 1/1→N→1/2→N→1/3, hits
        // pause after step 1 sent, player jumps to a sibling 1/4
        // during pause via manual W.
        const string SideExitJson = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "A",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/2", "S": "0", "E": "0", "W": "1/4",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "B",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/3", "S": "1/1", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "C",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/2", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 4, "Name": "Sidetrack",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "1/1", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        Harness h = NewHarness(SideExitJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));      // path: 1/1 → N → 1/2 → N → 1/3
        Assert.Single(h.Sent);

        h.Coordinator.AssertGate("user");

        // Manual W during pause → tracker at 1/4 (Sidetrack), which is
        // not anywhere on the remaining path.
        h.Tracker.NoteMoveSent(Direction.W);
        h.Tracker.NoteRoomObserved(new RoomObservation("Sidetrack",
            new HashSet<Direction> { Direction.E }));

        h.Coordinator.ClearGate("user");

        // Off-path → re-plan emitted as Retrying. WalkTo (re-plan)
        // restarts from 1/4, finds a new path: E (back to 1/1), N, N.
        Assert.Contains(h.Events,
            e => e.Kind == WalkEventKind.Retrying
                 && e.Detail.Contains("re-planning"));
        Assert.Equal(WalkState.Walking, h.Walker.State);
    }

    [Fact]
    public void WalkTo_WhileCoordinatorPaused_StartsInPausedState()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Coordinator.AssertGate("user");

        h.Walker.WalkTo(new RoomKey(1, 3));

        Assert.Equal(WalkState.Paused, h.Walker.State);
        Assert.Empty(h.Sent);                                // nothing on wire
    }

    // ----- stop / supersede -----------------------------------------

    [Fact]
    public void Stop_DuringWalk_GoesIdleAndFiresStopped()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));

        h.Walker.Stop();

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Stopped);
    }

    // A genuine external walk (the default supersede) Stops loudly, so a driving
    // reroute knows the user / another engine took over; the contrast is
    // WalkTo_SupersedeSilently_RedirectsWithoutStopped.
    [Fact]
    public void WalkTo_DuringActiveWalk_SupersedesPrior()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));

        h.Walker.WalkTo(new RoomKey(1, 2));

        Assert.Equal(2, h.Events.Count(e => e.Kind == WalkEventKind.Started));
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Stopped);
    }

    [Fact]
    public void WalkTo_MidStep_DefersUntilTrackerSettles_ThenPlansFromSettledRoom()
    {
        // Live bug: user clicks "walk to" mid-step. The old walk's
        // in-flight move was still on the wire — the new walk planned
        // from the stale source and immediately sent its first step,
        // server processed BOTH moves in sequence, walker saw two
        // confirmations (Pending → Pending → Confirmed) and only knew
        // how to handle the final Confirmed which landed one room past
        // the planned target → desync Failed → walker idle.
        //
        // Fix: when the tracker is Pending (in-flight queue not empty)
        // at WalkTo time, defer planning until the next Confirmed.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));      // path: N, N
        Assert.Single(h.Sent);                   // step 1 sent; tracker Pending
        int beforeSecondCall = h.Sent.Count;

        // Mid-step second WalkTo — tracker is still Pending (awaiting
        // confirmation for step 1's N). Walker must NOT send a fresh
        // first step yet — that would interleave with the old reply.
        h.Walker.WalkTo(new RoomKey(1, 2));

        Assert.Equal(beforeSecondCall, h.Sent.Count);
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Contains(h.Events,
            e => e.Kind == WalkEventKind.Started && e.Detail.Contains("deferred"));

        // Old in-flight step lands — tracker → Confirmed at 1/2. The
        // deferred plan fires, replans from 1/2 to 1/2 (already at dest)
        // → Finished.
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));

        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
        Assert.Equal(WalkState.Idle, h.Walker.State);
    }

    [Fact]
    public void WalkTo_MidStep_DeferredPlanReissuesWalkFromSettledRoom()
    {
        // Same as above but the new destination ISN'T the in-flight
        // landing — the deferred plan must build a fresh path from the
        // settled room and send its first step.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));      // path: N, N
        Assert.Single(h.Sent);

        // Mid-step: user wants to go BACK to 1/1 instead.
        h.Walker.WalkTo(new RoomKey(1, 1));
        Assert.Single(h.Sent);                   // deferred, no fresh send

        // Old in-flight settles at 1/2 — deferred plan kicks in,
        // BFS from 1/2 to 1/1 = [S], walker sends "s".
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("s\r", System.Text.Encoding.Latin1.GetString(h.Sent[1]));
        Assert.Equal(WalkState.Walking, h.Walker.State);
    }

    [Fact]
    public void WalkTo_Deferred_WatchdogFiresWhenTrackerNeverSettles_PlansFromLastKnownRoom()
    {
        // The tracker-Pending deferral must not hang forever when the in-flight
        // move is refused with no room redisplay — the tracker never reaches
        // Confirmed, so the dispatch it waits on never fires. The watchdog force-
        // plans from the last-known room instead (reports paradigm-20260810-201953
        // / -202239: "Walking, step 1 of 0" stuck with no feedback).
        List<System.Action> scheduled = new();
        Harness h = NewHarness();
        h.Walker.SetVoyageScheduler((_, cb) => { scheduled.Add(cb); return new NoopDisposable(); });
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));      // sends N; tracker now Pending
        Assert.Single(h.Sent);

        h.Walker.WalkTo(new RoomKey(1, 2));      // deferred — tracker still Pending
        Assert.Single(h.Sent);                   // nothing sent while deferred
        Assert.Contains(h.Events,
            e => e.Kind == WalkEventKind.Started && e.Detail.Contains("deferred"));
        // Two timers are armed by now: step 1's in-flight stall watchdog, then the
        // deferral's own deadline. This test is about the latter.
        Assert.Equal(2, scheduled.Count);

        // The Confirmed transition never arrives — fire the deferral's watchdog.
        scheduled[^1]();

        // Force-planned from the last-known room 1/1 (the refused move never
        // moved us): BFS 1/1 -> 1/2 = [N], so a fresh step goes out instead of
        // hanging.
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("n\r", System.Text.Encoding.Latin1.GetString(h.Sent[1]));
        Assert.Equal(WalkState.Walking, h.Walker.State);
    }

    // ----- avoided rooms --------------------------------------------

    [Fact]
    public void Walker_RespectsRoomFilter_FromConstructor()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), LineGraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();

        // Avoid the only intermediate hop.
        SimpleFilter filter = new();
        filter.Avoided.Add(new RoomKey(1, 2));

        AutoWalkManager walker = new(graph, bfs, tracker, coord, filter: filter);
        List<WalkEvent> events = new();
        walker.Event += events.Add;

        tracker.SetLocated(new RoomKey(1, 1));
        bool ok = walker.WalkTo(new RoomKey(1, 3));

        Assert.False(ok);
        // Avoiding the only intermediate walls off the destination — the walker
        // names the offending avoid room rather than a bare "no path".
        Assert.Contains(events, e => e.Kind == WalkEventKind.Failed
            && e.Detail == "only route is blocked by user set avoid in room (1/2)");
    }

    private sealed class SimpleFilter : IRoomFilter
    {
        public HashSet<RoomKey> Avoided { get; } = new();
        public bool IsAvoided(RoomKey key) => Avoided.Contains(key);
    }

    // ----- door-aware walks -----------------------------------------

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

    private sealed class FakeDoorEnqueuer
    {
        public List<(Direction Direction, int StatReq, bool CanBash, int KeyItemId, string Sender, Action<DoorOpenResult> Reply)> Calls { get; } = new();
        public void Enqueue(Direction direction, int statReq, bool canBash, int keyItemId, string sender, Action<DoorOpenResult> reply)
            => Calls.Add((direction, statReq, canBash, keyItemId, sender, reply));
    }

    [Fact]
    public void Walker_DoorExit_RoutesThroughDoorEnqueuer_BeforeMoveBytes()
    {
        Harness h = NewHarness(DoorGraphJson);
        FakeDoorEnqueuer door = new();
        h.Walker.SetDoorEnqueuer(door.Enqueue);

        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));

        // Door enqueue fired; no move bytes from the walker yet.
        Assert.Single(door.Calls);
        Assert.Equal(Direction.E, door.Calls[0].Direction);
        Assert.Equal("walker",    door.Calls[0].Sender);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Walker_DoorOpenSucceeds_SendsMoveBytesAndAdvances()
    {
        Harness h = NewHarness(DoorGraphJson);
        FakeDoorEnqueuer door = new();
        h.Walker.SetDoorEnqueuer(door.Enqueue);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));

        // Manager reports the door open — walker sends the move.
        door.Calls[0].Reply(DoorOpenResult.Opened.Instance);

        Assert.Single(h.Sent);
        Assert.Equal("e\r", Encoding.Latin1.GetString(h.Sent[0]));

        h.Tracker.NoteRoomObserved(new RoomObservation("Foyer",
            new HashSet<Direction> { Direction.W }));

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
    }

    // Report paradigm-20260928-125823: a monster crept in right after the bash opened the
    // door. The crossing now waits on the coordinator like any step, and crosses the
    // now-open door without opening it again.
    [Fact]
    public void Walker_DoorOpened_WhilePaused_CrossesOnResume_WithoutReopening()
    {
        Harness h = NewHarness(DoorGraphJson);
        FakeDoorEnqueuer door = new();
        h.Walker.SetDoorEnqueuer(door.Enqueue);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));

        h.Coordinator.AssertGate(MovementCoordinator.CombatGate, "test", "monster crept in");
        door.Calls[0].Reply(DoorOpenResult.Opened.Instance);
        Assert.Empty(h.Sent);

        h.Coordinator.ClearGate(MovementCoordinator.CombatGate, "test", "killed it");
        Assert.Single(h.Sent);
        Assert.Equal("e\r", Encoding.Latin1.GetString(h.Sent[0]));
        Assert.Single(door.Calls);
    }

    [Fact]
    public void Walker_DoorOpenFails_FailsTheWalk()
    {
        Harness h = NewHarness(DoorGraphJson);
        FakeDoorEnqueuer door = new();
        h.Walker.SetDoorEnqueuer(door.Enqueue);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));

        door.Calls[0].Reply(new DoorOpenResult.Failed("bash exhausted"));

        Assert.Empty(h.Sent);
        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    // The Ancient Coliseum: from the Viewing Stands the Arena is one step down
    // through a door needing 301 Strength or Picklocks, or three steps east (a door
    // anyone bashes), down and west. wayRound: whether that east door exists.
    private static string ColiseumJson(bool wayRound = true) => $$"""
        [
          { "Map Number": 3, "Room Number": 592, "Name": "Viewing Stands",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "{{(wayRound ? "3/593 (Door [21 picklocks/strength])" : "0")}}", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "3/595 (Door [301 picklocks/strength])" },
          { "Map Number": 3, "Room Number": 593, "Name": "Wide Passage",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "3/592 (Door [21 picklocks/strength])",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "3/594" },
          { "Map Number": 3, "Room Number": 594, "Name": "Preparation Chamber",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "3/595",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "3/593", "D": "0" },
          { "Map Number": 3, "Room Number": 595, "Name": "Arena",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "3/594", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "3/592 (Door [201 picklocks/strength])", "D": "0" }
        ]
        """;

    // The picks ran out on the door down. The walk used to fail there, and whatever
    // sent it (Auto-Lair, every two seconds) sent it again through the same door. It
    // now gives the door up for the trip and goes round, east first.
    [Fact]
    public void Walker_DoorBeatsTheCharacter_GoesRound_AndNeverPlansThroughItAgain()
    {
        Harness h = NewHarness(ColiseumJson());
        FakeDoorEnqueuer door = new();
        h.Walker.SetDoorEnqueuer(door.Enqueue);
        h.Tracker.SetLocated(new RoomKey(3, 592));
        h.Walker.WalkTo(new RoomKey(3, 595));
        Assert.Equal(Direction.D, Assert.Single(door.Calls).Direction);

        door.Calls[0].Reply(new DoorOpenResult.Failed("pick exhausted; no viable fallback verb", Unopenable: true));

        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Contains((new RoomKey(3, 592), new RoomKey(3, 595)), h.Walker.AbandonedDoors);
        Assert.Equal(2, door.Calls.Count);
        Assert.Equal(Direction.E, door.Calls[1].Direction);
        Assert.Contains(h.Walker.DoorsWalkedRound, d => d.Contains("3/595") && d.Contains("couldn't be opened earlier"));

        // A re-plan from the same room later in the trip (the east door turned out
        // not to be where the walk thought) still keeps off the door down.
        door.Calls[1].Reply(new DoorOpenResult.NotHere("'bash e' had no effect"));
        Assert.Equal(3, door.Calls.Count);
        Assert.Equal(Direction.E, door.Calls[2].Direction);

        // The next trip starts with no door given up on.
        h.Walker.Stop("test");
        h.Walker.WalkTo(new RoomKey(3, 595));
        Assert.Empty(h.Walker.AbandonedDoors);
        Assert.Equal(Direction.D, door.Calls[^1].Direction);
    }

    [Fact]
    public void Walker_DoorBeatsTheCharacter_WithNoOtherWay_FailsNamingTheDoor()
    {
        Harness h = NewHarness(ColiseumJson(wayRound: false));
        FakeDoorEnqueuer door = new();
        h.Walker.SetDoorEnqueuer(door.Enqueue);
        h.Tracker.SetLocated(new RoomKey(3, 592));
        h.Walker.WalkTo(new RoomKey(3, 595));

        door.Calls[0].Reply(new DoorOpenResult.Failed("pick exhausted; no viable fallback verb", Unopenable: true));

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Single(door.Calls);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Failed
            && e.Detail.Contains("couldn't open the door down from 3/592 (Viewing Stands)")
            && e.Detail.Contains("no other way to 3/595"));
        // The trip is over, and its doors go with it, so silent legs of a journey
        // begun afterwards don't inherit them.
        Assert.Empty(h.Walker.AbandonedDoors);
    }

    // A loop hands its approach walk the doors its run gave up on.
    [Fact]
    public void Walker_RefuseDoorsOnNextWalk_KeepsThatWalkOffThem()
    {
        Harness h = NewHarness(ColiseumJson());
        FakeDoorEnqueuer door = new();
        h.Walker.SetDoorEnqueuer(door.Enqueue);
        h.Tracker.SetLocated(new RoomKey(3, 592));

        h.Walker.RefuseDoorsOnNextWalk(new[] { (new RoomKey(3, 592), new RoomKey(3, 595)) });
        h.Walker.WalkTo(new RoomKey(3, 595));

        Assert.Equal(Direction.E, Assert.Single(door.Calls).Direction);
    }

    // The gaol of report paradigm-20260924-053941: a door southeast out of the
    // first room, and a plain southeast exit out of the room behind it.
    private const string DoorThenPlainGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Gaol",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "1/2 (Door)", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Dungeon",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "1/1 (Door)", "SE": "1/3", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "1/2", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // The door manager found the door isn't in the room we're standing in (we were
    // already through it). That is no failure of the walk: it re-plans from where the
    // tracker now is, and from there southeast is a plain exit.
    [Fact]
    public void Walker_DoorNotHere_ReplansFromTheRoomWeAreIn_InsteadOfFailing()
    {
        Harness h = NewHarness(DoorThenPlainGraphJson);
        FakeDoorEnqueuer door = new();
        h.Walker.SetDoorEnqueuer(door.Enqueue);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));
        Assert.Equal(Direction.SE, Assert.Single(door.Calls).Direction);

        // The room behind the door shows up while the door request is still out.
        h.Tracker.NoteRoomObserved(new RoomObservation("Dungeon",
            new HashSet<Direction> { Direction.NW, Direction.SE }));
        Assert.Equal(new RoomKey(1, 2), h.Tracker.State.CurrentRoom?.Key);
        Assert.Empty(h.Sent);

        door.Calls[0].Reply(new DoorOpenResult.NotHere("'bash se' had no effect"));

        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Single(door.Calls);
        Assert.Equal("se\r", Encoding.Latin1.GetString(Assert.Single(h.Sent)));

        h.Tracker.NoteRoomObserved(new RoomObservation("Vault",
            new HashSet<Direction> { Direction.NW }));
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
    }

    // A pause that comes and goes while a door is being opened (a party @wait / @ok
    // in the same second) must leave the door request alone. No move has gone out,
    // so reading the still-unmoved tracker as a refused move replanned the walk —
    // asking rm first — underneath a door manager that was about to send the move.
    [Fact]
    public void Walker_ResumeWhileDoorIsOpening_WaitsForTheDoor_DoesNotReplan()
    {
        Harness h = NewHarness(DoorGraphJson);
        FakeDoorEnqueuer door = new();
        int stopCalls = 0;
        h.Walker.SetDoorEnqueuer(door.Enqueue);
        h.Walker.SetDoorStopper(() => stopCalls++);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));
        Assert.Single(door.Calls);

        h.Coordinator.AssertGate(MovementCoordinator.CombatGate, "test", "a member asked us to wait");
        h.Coordinator.ClearGate(MovementCoordinator.CombatGate, "test", "all ok");

        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Retrying);
        Assert.Equal(0, stopCalls);
        Assert.Single(door.Calls);
        Assert.Empty(h.Sent);

        door.Calls[0].Reply(DoorOpenResult.Opened.Instance);
        Assert.Equal("e\r", Encoding.Latin1.GetString(Assert.Single(h.Sent)));
    }

    [Fact]
    public void Walker_DoorAlreadyOpen_SkipsFsm_SendsMoveDirectly()
    {
        // Live bug: room display shows "open door east" (already
        // open), walker still routed through the door FSM and
        // burned a bash attempt that came back "The door is already
        // open." Fix: pre-check tracker.State.OpenDoorDirections.
        Harness h = NewHarness(DoorGraphJson);
        FakeDoorEnqueuer door = new();
        h.Walker.SetDoorEnqueuer(door.Enqueue);

        // Seed the tracker with an observation marking E as already
        // open. This fires the SetLocated path which records the
        // current room; then we feed an explicit observation so
        // OpenDoorDirections gets populated.
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Tracker.NoteRoomObserved(new RoomObservation(
            "Outside",
            new HashSet<Direction> { Direction.E },
            new HashSet<Direction> { Direction.E }));

        h.Walker.WalkTo(new RoomKey(1, 2));

        // Door enqueuer NOT called — walker skipped the FSM.
        Assert.Empty(door.Calls);
        Assert.Single(h.Sent);
        Assert.Equal("e\r", Encoding.Latin1.GetString(h.Sent[0]));
    }

    [Fact]
    public void Walker_NewWalk_MidDoorFsm_StopsDownstreamDoorManager()
    {
        // Live bug: user queues a walk-to A, walker hits a door and
        // calls _doorEnqueuer (DoorOpenManager goes WaitingBash). User
        // queues walk-to B mid-FSM. Without the stopper, the next
        // door enqueue from walk B sits in DoorOpenManager.Queue
        // forever because TryStartNext bails on non-Idle state.
        Harness h = NewHarness(DoorGraphJson);
        FakeDoorEnqueuer door = new();
        int stopCalls = 0;
        h.Walker.SetDoorEnqueuer(door.Enqueue);
        h.Walker.SetDoorStopper(() => stopCalls++);

        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));
        Assert.Single(door.Calls);

        // Re-issue the walk before the door reply lands.
        h.Walker.WalkTo(new RoomKey(1, 2));

        Assert.Equal(1, stopCalls);
    }

    [Fact]
    public void Walker_NewWalk_NoDoorInFlight_DoesNotCallDoorStopper()
    {
        Harness h = NewHarness(DoorGraphJson);
        FakeDoorEnqueuer door = new();
        int stopCalls = 0;
        h.Walker.SetDoorEnqueuer(door.Enqueue);
        h.Walker.SetDoorStopper(() => stopCalls++);

        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));
        // Door open succeeds — _awaitingDoorOpen returns to false.
        door.Calls[0].Reply(DoorOpenResult.Opened.Instance);
        h.Tracker.NoteRoomObserved(new RoomObservation("Foyer",
            new HashSet<Direction> { Direction.W }));

        // New walk starts after the door FSM is back at rest — no
        // teardown needed.
        h.Walker.WalkTo(new RoomKey(1, 1));

        Assert.Equal(0, stopCalls);
    }

    [Fact]
    public void Walker_Stop_MidDoorFsm_StopsDoorManager()
    {
        Harness h = NewHarness(DoorGraphJson);
        FakeDoorEnqueuer door = new();
        int stopCalls = 0;
        h.Walker.SetDoorEnqueuer(door.Enqueue);
        h.Walker.SetDoorStopper(() => stopCalls++);

        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));
        Assert.Single(door.Calls);

        h.Walker.Stop();

        Assert.Equal(1, stopCalls);
    }

    [Fact]
    public void Walker_DoorExit_PathHasOnlyMoveStep()
    {
        Harness h = NewHarness(DoorGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));

        // Door handling is now runtime, not path-expansion. The
        // path is just MoveStep; the walker routes through
        // DoorOpenManager at step-send time.
        Assert.Single(h.Walker.Steps);
        Assert.IsType<MoveStep>(h.Walker.Steps[0]);
    }

    // ----- hidden-exit reveal ----------------------------------------

    // 1/1 → E is a graph-hidden exit to 1/2 ("(Hidden)" → SearchableHidden).
    private const string HiddenExitGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Outside",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/2 (Hidden)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Cave",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private sealed class FakeHiddenEnqueuer
    {
        public List<(Direction Direction, string Sender, Action<HiddenSearchResult> Reply)> Calls { get; } = new();
        public void Enqueue(Direction direction, string sender, Action<HiddenSearchResult> reply)
            => Calls.Add((direction, sender, reply));
    }

    [Fact]
    public void Walker_HiddenExit_NotYetRevealed_RoutesThroughSearchEnqueuer()
    {
        Harness h = NewHarness(HiddenExitGraphJson);
        FakeHiddenEnqueuer hidden = new();
        h.Walker.SetHiddenSearchEnqueuer(hidden.Enqueue);

        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));

        // No observation fed — ObservedExitDirections is null, so the
        // walker searches to uncover the hidden exit before moving.
        Assert.Single(hidden.Calls);
        Assert.Equal(Direction.E, hidden.Calls[0].Direction);
        Assert.Equal("walker",    hidden.Calls[0].Sender);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Walker_HiddenExit_AlreadyRevealed_SkipsSearch_SendsMoveDirectly()
    {
        // Live bug: a manual `sea e` uncovered the east exit, but on the
        // next walk pass the walker still re-fired `sea e` even though the
        // display already listed it. Fix: pre-check ObservedExitDirections
        // and send the cardinal move directly (mirrors the open-door
        // pre-check).
        Harness h = NewHarness(HiddenExitGraphJson);
        FakeHiddenEnqueuer hidden = new();
        h.Walker.SetHiddenSearchEnqueuer(hidden.Enqueue);

        h.Tracker.SetLocated(new RoomKey(1, 1));
        // Live "Obvious exits:" already lists E — the exit is uncovered.
        h.Tracker.NoteRoomObserved(new RoomObservation(
            "Outside", new HashSet<Direction> { Direction.E }));

        h.Walker.WalkTo(new RoomKey(1, 2));

        // Search enqueuer NOT called — walker went straight to the move.
        Assert.Empty(hidden.Calls);
        Assert.Single(h.Sent);
        Assert.Equal("e\r", Encoding.Latin1.GetString(h.Sent[0]));
    }

    // ----- text exits (commit 4) -------------------------------------

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
    public void Walker_TextExit_SendsFirstTextCommand_NotCardinal()
    {
        Harness h = NewHarness(TextExitGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));

        Assert.Single(h.Sent);
        Assert.Equal("borrow skiff\r", Encoding.Latin1.GetString(h.Sent[0]));
    }

    // Report paradigm-20261004-201232: a flee's way back was a text exit in the
    // northwest slot, and the bare "nw" it sent got "There is no exit in that
    // direction!" every time.
    [Fact]
    public void SendBacktrackMove_AcrossATextExit_SendsItsCommand_NotTheDirection()
    {
        Harness h = NewHarness(TextExitGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));

        h.Walker.SendBacktrackMove(Direction.S);

        Assert.Single(h.Sent);
        Assert.Equal("borrow skiff\r", Encoding.Latin1.GetString(h.Sent[0]));
        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);
    }

    [Fact]
    public void SendBacktrackMove_WithThePlaceLost_SendsTheBareDirection()
    {
        // A recovery backtrack runs while the tracker doesn't know the room, so the
        // room it last believed in can't supply an exit command.
        Harness h = NewHarness(TextExitGraphJson);

        h.Walker.SendBacktrackMove(Direction.S);

        Assert.Single(h.Sent);
        Assert.Equal("s\r", Encoding.Latin1.GetString(h.Sent[0]));
    }

    [Fact]
    public void Walker_TextExit_StepDisplaysCommand_NotDirection()
    {
        // The step shown in the Navigation right-rail must read the actual
        // command the exit uses ("borrow skiff"), not the cardinal the
        // exit happens to sit on ("south").
        Harness h = NewHarness(TextExitGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));

        Assert.Single(h.Walker.Steps);
        MoveStep step = Assert.IsType<MoveStep>(h.Walker.Steps[0]);
        Assert.Equal(Direction.S, step.Direction);   // still south under the hood
        Assert.Equal("borrow skiff", step.Display);   // but shows the command
    }

    [Fact]
    public void Walker_TextExit_RoomLandsAtTarget_AdvancesPath()
    {
        Harness h = NewHarness(TextExitGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));

        h.Tracker.NoteRoomObserved(new RoomObservation("Pier",
            new HashSet<Direction> { Direction.N }));

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
    }

    // ----- teleport exits (commit 5) --------------------------------

    // Source room (1/10) CMD=100 + (Item: 474) on a SW exit → Teleport.
    private const string TeleportGraphJson = """
        [
          { "Map Number": 1, "Room Number": 10, "Name": "Grove",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "CMD": 100,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "7/131 (Item: 474)",
            "U": "0", "D": "0" },
          { "Map Number": 7, "Room Number": 131, "Name": "Stone Arch",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0",
            "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void Walker_TeleportExit_NoResolver_FailsWithReason()
    {
        Harness h = NewHarness(TeleportGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 10));
        // No SetTeleportResolver — walker can't dispatch.
        h.Walker.WalkTo(new RoomKey(7, 131));

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events,
            e => e.Kind == WalkEventKind.Failed && e.Detail.Contains("teleport"));
    }

    [Fact]
    public void Walker_TeleportExit_WithResolver_SendsKeyword()
    {
        Harness h = NewHarness(TeleportGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 10));
        h.Walker.SetTeleportResolver((src, dst) =>
            src.Equals(new RoomKey(1, 10)) && dst.Equals(new RoomKey(7, 131))
                ? "go arch"
                : null);
        h.Walker.WalkTo(new RoomKey(7, 131));

        Assert.Single(h.Sent);
        Assert.Equal("go arch\r", Encoding.Latin1.GetString(h.Sent[0]));
    }

    [Fact]
    public void Walker_TeleportExit_PartyLeaderWithFollowers_SendsAtPartyFirst()
    {
        Harness h = NewHarness(TeleportGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 10));
        h.Walker.SetTeleportResolver((_, _) => "go arch");
        h.Walker.SetPartyLeaderCheck(() => true);
        h.Walker.WalkTo(new RoomKey(7, 131));

        // Two payloads: .@party broadcast first, then self.
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal(".@party go arch\r", Encoding.Latin1.GetString(h.Sent[0]));
        Assert.Equal("go arch\r",         Encoding.Latin1.GetString(h.Sent[1]));
    }

    [Fact]
    public void Walker_TeleportExit_SoloChar_SkipsAtPartyBroadcast()
    {
        Harness h = NewHarness(TeleportGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 10));
        h.Walker.SetTeleportResolver((_, _) => "go arch");
        h.Walker.SetPartyLeaderCheck(() => false);
        h.Walker.WalkTo(new RoomKey(7, 131));

        Assert.Single(h.Sent);
        Assert.Equal("go arch\r", Encoding.Latin1.GetString(h.Sent[0]));
    }

    [Fact]
    public void Walker_TeleportPartySplit_GateAssertClearMidTeleport_DoesNotResend()
    {
        // Report 171842: a chime-teleport reform asserts the PartyInvite gate
        // the instant the leader teleports, and the followers relay through and
        // rejoin so fast the gate clears before the destination room render
        // lands. The walker's resume then re-sent the in-flight teleport —
        // re-teleporting and re-firing the reform (spamming @join at the
        // already-rejoined group) and stranding the walk. The in-flight guard
        // keeps the step pending across the assert/clear so the teleport fires
        // exactly once and the walk continues on arrival.
        Harness h = NewHarness(TeleportGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 10));
        h.Walker.SetTeleportResolver((_, _) => "go arch");
        h.Walker.SetPartyLeaderCheck(() => true);

        int splitFires = 0;
        h.Walker.SetPartySplitHandler(() =>
        {
            splitFires++;
            // Simulate the reform racing the arrival: gate asserts (invite
            // hold) then clears (members rejoin) before the teleport confirms.
            h.Coordinator.AssertGate(MovementCoordinator.PartyInviteGate);
            h.Coordinator.ClearGate(MovementCoordinator.PartyInviteGate);
        });

        h.Walker.WalkTo(new RoomKey(7, 131));

        // Teleport fired exactly once — .@party relay + self keyword — and the
        // reform handler ran once, with no duplicate re-send on gate resume.
        Assert.Equal(1, splitFires);
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal(".@party go arch\r", Encoding.Latin1.GetString(h.Sent[0]));
        Assert.Equal("go arch\r",         Encoding.Latin1.GetString(h.Sent[1]));
        Assert.Equal(WalkState.Walking, h.Walker.State);

        // The destination render finally lands — the walk advances to its
        // destination rather than stalling on the in-flight teleport.
        h.Tracker.NoteRoomObserved(new RoomObservation("Stone Arch",
            new HashSet<Direction>()));
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
    }

    // ----- multi-action hidden exits (commit 6) ---------------------

    // Room 1/1 → N exit is multi-action with two prereq commands in
    // the same row (E and W exit fields carry the Action#1 and #2
    // entries respectively, both targeting "the N exit of this room").
    private const string MultiActionGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Chamber",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2 (Hidden/Needs 2 Actions, specific order)",
            "S": "0",
            "E": "Action#1 [on the N exit of this room]: pull lever, move lever",
            "W": "Action#2 [on the N exit of this room]: push button",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void Walker_MultiActionExit_SendsActionsThenCardinalMove()
    {
        Harness h = NewHarness(MultiActionGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));

        // 3 payloads: pull lever, push button, then "n".
        Assert.Equal(3, h.Sent.Count);
        Assert.Equal("pull lever\r",  Encoding.Latin1.GetString(h.Sent[0]));
        Assert.Equal("push button\r", Encoding.Latin1.GetString(h.Sent[1]));
        Assert.Equal("n\r",           Encoding.Latin1.GetString(h.Sent[2]));
    }

    // A reveal that rolls: the room's command chain opens its own hidden N exit
    // through a `testskill`, which can miss and leave the exit shut.
    private const string RolledRevealGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Ruin Entrance",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "CMD": 1422,
            "N": "1/2 (Hidden/Needs 1 Actions, any order)",
            "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Ruin",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "CMD": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;
    private const string RolledRevealTbInfoJson = """
        [ { "Number": 1422,
            "Action": "clear rubble:testskill strength 0 1423:remoteaction 1 1840 0 0\n",
            "Called From": "Room 1/1" } ]
        """;

    private static List<string> SentText(Harness h) =>
        h.Sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();

    // The roll missed, the exit stayed hidden and the move bonked: the command goes
    // out again with the move, on a budget of its own. Before, one retry and two
    // replans were all a reveal got, and a short run of bad rolls failed the walk.
    [Fact]
    public void RolledReveal_MoveBonks_SendsTheRevealAgain_UntilTheWayOpens()
    {
        Harness h = NewHarness(RolledRevealGraphJson, tbinfoJson: RolledRevealTbInfoJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));
        Assert.Equal(new[] { "clear rubble", "n" }, SentText(h));

        for (int miss = 1; miss <= 5; miss++)
        {
            h.Tracker.NoteMoveBlocked();
            Assert.Equal(WalkState.Walking, h.Walker.State);
            Assert.Equal(2 * (miss + 1), h.Sent.Count);
            Assert.Equal(new[] { "clear rubble", "n" }, SentText(h).TakeLast(2));
        }
        Assert.DoesNotContain(h.Events, e => e.Kind is WalkEventKind.Failed or WalkEventKind.Retrying);

        h.Tracker.NoteRoomObserved(new RoomObservation("Ruin", new HashSet<Direction> { Direction.S }));
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
    }

    // The retries are capped: past the cap the step is an ordinary blocked step.
    [Fact]
    public void RolledReveal_PastTheCap_FallsBackToTheBlockedStepHandling()
    {
        Harness h = NewHarness(RolledRevealGraphJson, tbinfoJson: RolledRevealTbInfoJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));

        for (int miss = 0; miss < SpecialExitDispatch.RolledRevealRetryCap; miss++)
            h.Tracker.NoteMoveBlocked();
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Retrying);
        Assert.Equal(2 * (SpecialExitDispatch.RolledRevealRetryCap + 1), h.Sent.Count);

        h.Tracker.NoteMoveBlocked();                      // the cap is spent
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Retrying);
    }

    // A reveal that never opens ends the walk on the one cap, however the walk gets
    // there: a replan from the room the reveal is in leads straight back to the same
    // step, and must not hand it a fresh ten tries.
    [Fact]
    public void RolledReveal_ThatNeverOpens_KeepsItsCapAcrossReplans_AndFails()
    {
        Harness h = NewHarness(RolledRevealGraphJson, tbinfoJson: RolledRevealTbInfoJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));

        for (int i = 0; i < 100 && h.Walker.State != WalkState.Idle; i++)
            h.Tracker.NoteMoveBlocked();

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Failed);
        int reveals = SentText(h).Count(c => c == "clear rubble");
        // The first try, the ten re-sends, and one more for each ordinary retry and
        // replan the blocked-step handling then spends: well short of a second ten.
        Assert.InRange(reveals, 1 + SpecialExitDispatch.RolledRevealRetryCap,
            SpecialExitDispatch.RolledRevealRetryCap + 8);
    }

    // A room command that reveals with no `testskill` in its chain doesn't roll.
    [Fact]
    public void UnrolledRoomCommandReveal_MoveBonks_KeepsTheOrdinaryRetry()
    {
        Harness h = NewHarness(RolledRevealGraphJson, tbinfoJson: """
            [ { "Number": 1422, "Action": "pull chain:remoteaction 1 1840 0 0\n", "Called From": "Room 1/1" } ]
            """);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));
        Assert.Equal(new[] { "pull chain", "n" }, SentText(h));

        h.Tracker.NoteMoveBlocked();

        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Retrying);
    }

    // A lever exit carries no roll, so a bonk behind it keeps the ordinary handling.
    [Fact]
    public void LeverExit_MoveBonks_KeepsTheOrdinaryRetry()
    {
        Harness h = NewHarness(MultiActionGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));

        h.Tracker.NoteMoveBlocked();

        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Retrying);
    }

    // Cross-room multi-action — the gated exit (1/2's N → 1/9) needs a command
    // typed in room 1/5, one E hop off the host room. The round-trip host↔issue
    // is routable, so the walker walks to the lever room, pulls it, walks back,
    // and crosses. Start is 1/1 (one N hop into the host 1/2).
    //
    //   1/1 ──N──▶ 1/2 ──N (Hidden/Needs 1 Actions)──▶ 1/9
    //              │E
    //              ▼
    //             1/5  (D slot carries Action#1 for 1/2's N exit)
    private const string MultiActionCrossRoomJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Hub",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/9 (Hidden/Needs 1 Actions, specific order)", "S": "1/1", "E": "1/5", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Lever",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0",
            "D": "Action#1 [on the N exit of room 1/2]: pull lever" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void Walker_MultiActionExit_CrossRoom_WalksActsThenCrosses()
    {
        Harness h = NewHarness(MultiActionCrossRoomJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 9));

        // Step 0: n → 1/2 (into the hub).
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[^1]));
        h.Tracker.NoteRoomObserved(new RoomObservation("Hub",
            new HashSet<Direction> { Direction.S, Direction.E }));

        // Step 1: e → 1/5 (detour to the lever room).
        Assert.Equal("e\r", Encoding.Latin1.GetString(h.Sent[^1]));
        h.Tracker.NoteRoomObserved(new RoomObservation("Lever",
            new HashSet<Direction> { Direction.W }));

        // Step 2: the fire-and-forget prerequisite command.
        Assert.Equal("pull lever\r", Encoding.Latin1.GetString(h.Sent[^1]));
        h.Prompt.Append(Encoding.Latin1.GetBytes("[HP=100]: "));

        // Step 3: w → 1/2 (walk back to the host room).
        Assert.Equal("w\r", Encoding.Latin1.GetString(h.Sent[^1]));
        h.Tracker.NoteRoomObserved(new RoomObservation("Hub",
            new HashSet<Direction> { Direction.S, Direction.E }));

        // Step 4: n → 1/9, the primed cross. SkipSpecialDispatch means it goes
        // out as a plain cardinal — NOT re-running the multi-action dispatch
        // (which would re-issue "pull lever").
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[^1]));
        h.Tracker.NoteRoomObserved(new RoomObservation("Vault",
            new HashSet<Direction> { Direction.S }));

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);

        // Exactly one "pull lever" — the prereq isn't double-issued on the cross.
        Assert.Equal(1, h.Sent.Count(b => Encoding.Latin1.GetString(b) == "pull lever\r"));
        Assert.Equal(new[] { "n\r", "e\r", "pull lever\r", "w\r", "n\r" },
            h.Sent.Select(b => Encoding.Latin1.GetString(b)).ToArray());
    }

    // Cross-room multi-action whose command room (1/7) is isolated — nothing
    // connects to it, so the host→issue detour is unroutable. The action data
    // lives in 1/7's row but references "the N exit of room 1/3", so the command
    // must be typed in 1/7 (a room the walker can't reach). Expansion truncates
    // and the walker fails cleanly rather than crossing an un-primed exit.
    private const string MultiActionCrossRoomUnroutableJson = """
        [
          { "Map Number": 1, "Room Number": 3, "Name": "Switch",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2 (Hidden/Needs 1 Actions, any order)", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/3", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 7, "Name": "Faraway",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0",
            "W": "Action#1 [on the N exit of room 1/3]: pull lever",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void Walker_MultiActionExit_CrossRoom_UnroutableDetour_FailsCleanly()
    {
        Harness h = NewHarness(MultiActionCrossRoomUnroutableJson);
        h.Tracker.SetLocated(new RoomKey(1, 3));
        h.Walker.WalkTo(new RoomKey(1, 2));

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Empty(h.Sent);
    }

    // ----- trapped exits ---------------------------------------------

    private const string TrapGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Safe",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2 (Trap)", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Pit",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private sealed class FakeTrapEnqueuer
    {
        public List<(string Direction, string Sender, Action<string> Reply)> Calls { get; } = new();
        public void Enqueue(string direction, string sender, Action<string> reply)
            => Calls.Add((direction, sender, reply));
    }

    [Fact]
    public void Walker_TrappedExit_RoutesThroughTrapEnqueuer_BeforeMoveBytes()
    {
        Harness h = NewHarness(TrapGraphJson);
        FakeTrapEnqueuer trap = new();
        h.Walker.SetTrapEnqueuer(trap.Enqueue);

        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));

        // Trap enqueue happened; no move bytes yet.
        Assert.Single(trap.Calls);
        Assert.Equal("north", trap.Calls[0].Direction);
        Assert.Equal("walker", trap.Calls[0].Sender);
        Assert.Empty(h.Sent);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.DisarmingTrap);
    }

    [Fact]
    public void Walker_TrapDisarmSuccess_SendsMoveBytesAndAdvances()
    {
        Harness h = NewHarness(TrapGraphJson);
        FakeTrapEnqueuer trap = new();
        h.Walker.SetTrapEnqueuer(trap.Enqueue);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));

        // Fire the disarm-success reply.
        trap.Calls[0].Reply("Trap to the north disarmed.");

        Assert.Single(h.Sent);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));

        // Confirm the move.
        h.Tracker.NoteRoomObserved(new RoomObservation("Pit",
            new HashSet<Direction> { Direction.S }));

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
    }

    // A party member's answer arrives in the { } of a remote reply.
    [Theory]
    [InlineData("{Trap to the north disarmed.}")]
    [InlineData("{No trap to the north to disarm.}")]
    [InlineData("{No trap to the north to disarm (failed 5 times; taking it as clear).}")]
    public void Walker_ARemoteClearReply_CrossesTheExit(string reply)
    {
        Harness h = NewHarness(TrapGraphJson);
        FakeTrapEnqueuer trap = new();
        h.Walker.SetTrapEnqueuer(trap.Enqueue);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));

        trap.Calls[0].Reply(reply);

        Assert.Equal("n\r", Encoding.Latin1.GetString(Assert.Single(h.Sent)));
    }

    // Nobody in the party took the trap: the same position as nobody being able.
    [Fact]
    public void Walker_TrapNobodyTook_IsWalkedThrough()
    {
        Harness h = NewHarness(TrapGraphJson);
        FakeTrapEnqueuer trap = new();
        h.Walker.SetTrapEnqueuer(trap.Enqueue);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));

        trap.Calls[0].Reply(MudPlay.Game.TrapReply.Unanswered);

        Assert.Equal("n\r", Encoding.Latin1.GetString(Assert.Single(h.Sent)));
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    [Fact]
    public void Walker_TrapDisarmFailure_FailsTheWalk()
    {
        Harness h = NewHarness(TrapGraphJson);
        FakeTrapEnqueuer trap = new();
        h.Walker.SetTrapEnqueuer(trap.Enqueue);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));

        trap.Calls[0].Reply("Couldn't disarm the trap to the north (5 attempts).");

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Empty(h.Sent);                                 // never sent the move
    }

    [Fact]
    public void Walker_TrapStopped_CancelsWalk()
    {
        Harness h = NewHarness(TrapGraphJson);
        FakeTrapEnqueuer trap = new();
        h.Walker.SetTrapEnqueuer(trap.Enqueue);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));

        trap.Calls[0].Reply("Trap flow stopped.");

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Stopped);
        Assert.Empty(h.Sent);
    }

    // ----- unbound wire sender --------------------------------------

    [Fact]
    public void NoWireSender_SuppressesTheSendAndLogsIt()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), LineGraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        LogService log = new();
        AutoWalkManager walker = new(graph, bfs, tracker, coord, log: log);

        tracker.SetLocated(new RoomKey(1, 1));
        walker.WalkTo(new RoomKey(1, 3));

        Assert.Contains(log.Snapshot(), e => e.Severity == LogSeverity.Warn
            && e.Source == "Walker" && e.Message.StartsWith("wire sender not bound; suppressed", StringComparison.Ordinal));
    }

    [Fact]
    public void SendBacktrackMove_WithNoActivePlan_DoesNotThrow()
    {
        // Tier-3 health-recovery backtracks route through the same WriteBytes
        // choke point as planned moves, but with no walk plan in flight
        // (_path == null). Regression: the step-counter log line used to
        // deref _path! and crash with a NullReferenceException.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 2));

        h.Walker.SendBacktrackMove(Direction.S);

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Single(h.Sent);
        Assert.Equal("s\r", Encoding.Latin1.GetString(h.Sent[0]));
    }

    // ----- item-gated blocked-route naming --------------------------
    //
    // 1/1 ──N (Item: 474)── 1/2. CMD=0 keeps the exit an Item gate rather
    // than promoting it to a teleport, so a character lacking item 474 has
    // its only route blocked by that possession requirement.
    private const string ItemGateGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "1/2 (Item: 474)", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1 (Item: 474)", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Build a walker whose only route to the destination is an Item-gated exit
    // the character can't satisfy (carries nothing). The MovementFilter reports
    // its inventory as known-and-empty so the Item gate fires at plan time.
    private (AutoWalkManager Walker, List<WalkEvent> Events) NewItemGatedWalker(
        Func<int, string?>? nameResolver)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), ItemGateGraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        ProfileService profile = new();
        MovementFilter filter = new(profile)
        {
            InventoryReadyProbe = () => true,
            ItemCarriedProbe = _ => false,          // carries nothing → item 474 missing
        };
        AutoWalkManager walker = new(graph, bfs, tracker, coord, filter);
        var events = new List<WalkEvent>();
        walker.Event += events.Add;
        if (nameResolver is not null) walker.SetItemNameResolver(nameResolver);
        tracker.SetLocated(new RoomKey(1, 1));
        return (walker, events);
    }

    [Fact]
    public void BlockedRoute_ItemGate_NamesMissingItem_WhenResolverWired()
    {
        (AutoWalkManager walker, List<WalkEvent> events) =
            NewItemGatedWalker(id => id == 474 ? "obsidian key" : null);

        Assert.False(walker.WalkTo(new RoomKey(1, 2)));

        WalkEvent failed = events.Single(e => e.Kind == WalkEventKind.Failed);
        Assert.Contains("obsidian key", failed.Detail);
        Assert.Contains("a required item to go obtain (obsidian key)", failed.Detail);
    }

    // A walk the user chose to take THROUGH a gate/hazard keeps that choice when a
    // mid-walk desync recovery re-plans it — the resume used to re-plan with default
    // flags, put the gate back, and fail "all routes blocked" (report
    // paradigm-20260924-120529, a "cross unprotected" hazard walk).
    [Fact]
    public void ResumeAfterRecovery_KeepsTheWalksThroughGatesChoice()
    {
        (AutoWalkManager walker, List<WalkEvent> events) = NewItemGatedWalker(nameResolver: null);
        walker.SetWireSender(_ => { });

        Assert.True(walker.WalkTo(new RoomKey(1, 2),
            planThroughAcquirableGates: true, armItemAcquisition: false));
        walker.PauseForRecovery("tracker desync");
        walker.ResumeAfterRecovery(new RoomKey(1, 1));

        Assert.DoesNotContain(events, e => e.Kind == WalkEventKind.Failed);
        Assert.Equal(WalkState.Walking, walker.State);
    }

    // 1/1 → E → 1/2 → E → 1/3, where 1/2 casts a damaging spell on entry that item
    // 42 negates. The character carries nothing.
    private const string HazardLineGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Bank", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/2", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "River", "Spell": 700,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/3", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Far Bank", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // What the hazard-line walker put on the wire.
    private readonly List<byte[]> _hazardWalkerSent = new();

    private (AutoWalkManager Walker, List<WalkEvent> Events, MovementFilter Filter) NewHazardLineWalker()
    {
        string set = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(set);
        File.WriteAllText(Path.Combine(set, "Rooms.json"), HazardLineGraphJson);
        File.WriteAllText(Path.Combine(set, "Spells.json"), """[ { "Number": 700, "Abil-0": 1, "AbilVal-0": 25 } ]""");
        File.WriteAllText(Path.Combine(set, "Items.json"), """[ { "Number": 42, "NegateSpell-0": 700 } ]""");
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomHazardIndex hazards = new(cache);
        hazards.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementFilter filter = new(new ProfileService())
        {
            InventoryReadyProbe = () => true,
            ItemCarriedProbe = _ => false,
            Hazards = hazards,
            RoomEntrySpellProbe = key => graph.GetRoom(key)?.Spell ?? 0,
        };
        AutoWalkManager walker = new(graph, bfs, tracker, new MovementCoordinator(), filter);
        walker.SetWireSender(_hazardWalkerSent.Add);
        var events = new List<WalkEvent>();
        walker.Event += events.Add;
        tracker.SetLocated(new RoomKey(1, 1));
        return (walker, events, filter);
    }

    // Report paradigm-20261004-204354: a stash transfer plans through gates, and
    // that plan took a hazard room nothing was going to counter.
    [Fact]
    public void ThroughGatesWalk_NobodyPicked_WontCrossAnUncounteredHazard()
    {
        (AutoWalkManager walker, List<WalkEvent> events, _) = NewHazardLineWalker();

        Assert.False(walker.WalkTo(new RoomKey(1, 3), planThroughAcquirableGates: true));

        Assert.Contains(events, e => e.Kind == WalkEventKind.Failed);
        Assert.Empty(_hazardWalkerSent);
    }

    [Fact]
    public void ThroughGatesWalk_ThatWillObtainTheCounter_PlansThroughTheHazard()
    {
        (AutoWalkManager walker, List<WalkEvent> events, MovementFilter filter) = NewHazardLineWalker();
        filter.HazardProvisionProbe = _ => new[] { 42 };

        Assert.True(walker.WalkTo(new RoomKey(1, 3), planThroughAcquirableGates: true));

        Assert.DoesNotContain(events, e => e.Kind == WalkEventKind.Failed);
    }

    [Fact]
    public void ThroughGatesWalk_TheUserPicked_CrossesTheHazard()
    {
        (AutoWalkManager walker, List<WalkEvent> events, _) = NewHazardLineWalker();

        Assert.True(walker.WalkTo(new RoomKey(1, 3),
            planThroughAcquirableGates: true, armItemAcquisition: false, pickedRoute: true,
            agreedHazardRooms: new[] { new RoomKey(1, 2) }));

        Assert.DoesNotContain(events, e => e.Kind == WalkEventKind.Failed);
        Assert.Equal(WalkState.Walking, walker.State);
    }

    [Fact]
    public void BlockedRoute_ItemGate_FallsBackToGeneric_WhenNoResolver()
    {
        (AutoWalkManager walker, List<WalkEvent> events) = NewItemGatedWalker(nameResolver: null);

        Assert.False(walker.WalkTo(new RoomKey(1, 2)));

        WalkEvent failed = events.Single(e => e.Kind == WalkEventKind.Failed);
        Assert.Contains("a required item to go obtain", failed.Detail);
        Assert.DoesNotContain("(", failed.Detail);            // no name parenthetical
    }

    // ----- two-route diagnostic: name the acquirable front door ------
    //
    // 1/1 has TWO routes to 1/4:
    //   • E ──(Level: 40 to 0)──► 1/4            (1 hop, level-gated backdoor)
    //   • N ──(Item: 474)──► 1/2 ── E ──► 1/4    (2 hops, item-gated front door)
    // A level-22 character carrying nothing can walk neither. The shorter
    // route is what an ignore-ALL-gates probe surfaces, so the pre-fix
    // diagnostic blamed "a level requirement" — a portal the crosser was
    // never going to take. The fix re-probes with only the acquirable gates
    // suspended first, so the item route (the real front door) is described.
    private const string TwoRouteGateGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "1/2 (Item: 474)", "S": "0", "E": "1/4 (Level: 40 to 0)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1 (Item: 474)", "E": "1/4", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "C",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void BlockedRoute_TwoRoutes_NamesAcquirableGate_NotShorterLevelGate()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), TwoRouteGateGraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        ProfileService profile = new();
        MovementFilter filter = new(profile)
        {
            LevelProvider = () => 22,               // below the 40+ portal floor
            InventoryReadyProbe = () => true,
            ItemCarriedProbe = _ => false,          // lacks item 474
        };
        AutoWalkManager walker = new(graph, bfs, tracker, coord, filter);
        var events = new List<WalkEvent>();
        walker.Event += events.Add;
        walker.SetItemNameResolver(id => id == 474 ? "gate key" : null);
        tracker.SetLocated(new RoomKey(1, 1));

        Assert.False(walker.WalkTo(new RoomKey(1, 4)));

        WalkEvent failed = events.Single(e => e.Kind == WalkEventKind.Failed);
        Assert.Contains("a required item to go obtain (gate key)", failed.Detail);
        Assert.DoesNotContain("a level requirement", failed.Detail);
    }

    // The only route is level-gated — the failure names the barrier room + its
    // level window, not a bare "a level requirement" (user feedback: "say
    // specifically the gate and room").
    private const string SingleLevelGateGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/4 (Level: 40 to 0)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Marble Passage",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void BlockedRoute_LevelGate_NamesGateRoomAndLevel()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), SingleLevelGateGraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        ProfileService profile = new();
        MovementFilter filter = new(profile)
        {
            LevelProvider = () => 22,               // below the 40+ floor on 1/4
            InventoryReadyProbe = () => true,
            ItemCarriedProbe = _ => false,
        };
        AutoWalkManager walker = new(graph, bfs, tracker, coord, filter);
        var events = new List<WalkEvent>();
        walker.Event += events.Add;
        tracker.SetLocated(new RoomKey(1, 1));

        Assert.False(walker.WalkTo(new RoomKey(1, 4)));

        WalkEvent failed = events.Single(e => e.Kind == WalkEventKind.Failed);
        Assert.Contains("1/4 (Marble Passage) needs level 40+", failed.Detail);
    }

    // ----- boss "stop before" ---------------------------------------

    [Fact]
    public void WalkTo_NonFlaggedRoom_UnaffectedByStopBefore()
    {
        // A different room is flagged; the walk to 1/3 runs the full two hops.
        Harness h = NewHarness();
        h.Walker.SetBossStopRooms(() => new HashSet<RoomKey> { new(1, 1) });
        h.Tracker.SetLocated(new RoomKey(1, 1));

        h.Walker.WalkTo(new RoomKey(1, 3));
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        Assert.Equal(2, h.Sent.Count);   // stepped into 1/2 and on toward 1/3

        h.Tracker.NoteRoomObserved(new RoomObservation("C",
            new HashSet<Direction> { Direction.S }));
        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
    }
    // ----- gate-item hold --------------------------------------------
    //
    // Report paradigm-20260911-100708: a gated route is planned as if the gate item
    // were in hand, but the first step goes out synchronously while the acquisition
    // detour's redirect is still queued — so the walk sent the opener and the move,
    // watched both fail, and only then got superseded. We know the inventory, so
    // hold instead. Held ONLY while something is actually fetching the item, so a
    // gate nothing can source still fails the normal way.

    [Fact]
    public void GatedStep_ItemBeingAcquired_HoldsInsteadOfSending()
    {
        Harness h = NewHarness(ItemGatedLineJson);
        h.Walker.SetGateItemHoldProbe(id => id == 807);
        h.Tracker.SetLocated(new RoomKey(1, 1));

        bool started = h.Walker.WalkTo(new RoomKey(1, 2));

        Assert.True(started);                       // the walk is armed...
        Assert.Empty(h.Sent);                       // ...but nothing went on the wire
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    [Fact]
    public void GatedStep_NothingAcquiringIt_StillSends()
    {
        Harness h = NewHarness(ItemGatedLineJson);
        h.Walker.SetGateItemHoldProbe(_ => false);   // no acquisition in flight
        h.Tracker.SetLocated(new RoomKey(1, 1));

        h.Walker.WalkTo(new RoomKey(1, 2));

        // Attempt-and-fail is the correct fallback: holding here would park the
        // walk forever on a gate nobody is working on.
        Assert.NotEmpty(h.Sent);
    }

    [Fact]
    public void UngatedStep_IsNeverHeld()
    {
        Harness h = NewHarness();                    // plain 1/1 ─N─ 1/2 ─N─ 1/3
        h.Walker.SetGateItemHoldProbe(_ => true);    // would hold anything gated
        h.Tracker.SetLocated(new RoomKey(1, 1));

        h.Walker.WalkTo(new RoomKey(1, 3));

        Assert.NotEmpty(h.Sent);
    }

    // Once the item lands the probe stops holding, and the next dispatch crosses.
    [Fact]
    public void HeldStep_ResumesOnceTheItemIsAcquired()
    {
        Harness h = NewHarness(ItemGatedLineJson);
        bool acquiring = true;
        h.Walker.SetGateItemHoldProbe(id => acquiring && id == 807);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));
        Assert.Empty(h.Sent);

        acquiring = false;                           // orb in hand, need resolved
        // Re-issued, as the path-item routers do when their detour hands back.
        h.Walker.WalkTo(new RoomKey(1, 2));

        Assert.Equal("n\r", System.Text.Encoding.Latin1.GetString(Assert.Single(h.Sent)));
    }

    // The party is asked who holds a route item when the walk announces its items,
    // and the answer may turn the walk aside for a shop. The announce comes before
    // the first step, so the hold it raises keeps that step back (report
    // paradigm-20261009-011133: four rooms toward the gate, then back for the item).
    [Fact]
    public void HoldRaisedByTheItemAnnounce_KeepsTheFirstStepBack()
    {
        Harness h = NewHarness(ItemGatedLineJson);
        List<int> announced = new();
        h.Walker.SetPathItemAnnouncer(ids =>
        {
            announced.AddRange(ids);
            h.Coordinator.AssertGate(MovementCoordinator.PartyItemCountGate, "test", "asking the party");
        });
        h.Tracker.SetLocated(new RoomKey(1, 1));

        bool started = h.Walker.WalkTo(new RoomKey(1, 2));

        Assert.True(started);
        Assert.Equal(new[] { 807 }, announced);
        Assert.Empty(h.Sent);
        Assert.Equal(WalkState.Paused, h.Walker.State);
        Assert.Single(h.Events, e => e.Kind == WalkEventKind.Paused);   // said once, not twice

        h.Coordinator.ClearGate(MovementCoordinator.PartyItemCountGate, "test", "the count is in");

        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.NotEmpty(h.Sent);
    }

    // A key gate is NOT a carry gate — pick and bash open it — so ExitGateItems
    // omits it. But a key with an acquisition behind it still has to hold, or the
    // door FSM arrives first and burns its one use-key attempt on a key that hasn't
    // been fetched yet, failing the walk while the summon is still en route
    // (report paradigm-20260911-103315).
    [Fact]
    public void KeyGatedStep_KeyBeingAcquired_Holds()
    {
        Harness h = NewHarness(KeyGatedLineJson);
        h.Walker.SetGateItemHoldProbe(id => id == 806);
        h.Tracker.SetLocated(new RoomKey(1, 1));

        h.Walker.WalkTo(new RoomKey(1, 2));

        Assert.Empty(h.Sent);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    // Every other key door is untouched: with nothing fetching the key the step
    // goes out and the door FSM picks, bashes or fails exactly as before.
    [Fact]
    public void KeyGatedStep_NothingAcquiringTheKey_StillSends()
    {
        Harness h = NewHarness(KeyGatedLineJson);
        h.Walker.SetGateItemHoldProbe(_ => false);
        h.Tracker.SetLocated(new RoomKey(1, 1));

        h.Walker.WalkTo(new RoomKey(1, 2));

        Assert.NotEmpty(h.Sent);
    }

    // ----- teleports on automatic walks -------------------------------
    //
    // A fork where both routes reach 7/131: three ordinary steps north, or one
    // teleport hop southwest. The route search counts the teleport as a single step,
    // so it is the shortest route, which is why an automatic walk needs telling.
    //
    // 1/10 Grove --N-- 1/11 --N-- 1/12 --N-- 7/131 Stone Arch
    //   \__ SW (CMD 100 teleport) ______________/
    private const string TeleportVsWalkGraphJson = """
        [
          { "Map Number": 1, "Room Number": 10, "Name": "Grove",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "CMD": 100,
            "N": "1/11", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "7/131 (Item: 474)",
            "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 11, "Name": "Trail",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/12", "S": "1/10", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 12, "Name": "Ridge",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "7/131", "S": "1/11", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 7, "Room Number": 131, "Name": "Stone Arch",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/12", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private static Harness ArmTeleport(Harness h)
    {
        h.Walker.SetTeleportResolver((_, _) => "go arch");
        h.Tracker.SetLocated(new RoomKey(1, 10));
        return h;
    }

    private static readonly (RoomKey From, RoomKey To) Arch = (new RoomKey(1, 10), new RoomKey(7, 131));

    private static IReadOnlySet<(RoomKey From, RoomKey To)> Allow(params (RoomKey From, RoomKey To)[] teleports) =>
        new HashSet<(RoomKey From, RoomKey To)>(teleports);

    // A walk the client starts on its own states no teleport preference. It used to
    // take whichever teleport was on the shortest route with nobody asked; now it
    // uses only the ones the user allowed, and with none allowed it goes on foot.
    [Fact]
    public void AutomaticWalk_TeleportNotAllowed_GoesOnFoot()
    {
        Harness h = ArmTeleport(NewHarness(TeleportVsWalkGraphJson));
        h.Walker.SetAutomaticWalkTeleports(() => Allow());

        h.Walker.WalkTo(new RoomKey(7, 131));

        Assert.Single(h.Sent);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));
    }

    [Fact]
    public void AutomaticWalk_TeleportAllowed_TakesIt()
    {
        Harness h = ArmTeleport(NewHarness(TeleportVsWalkGraphJson));
        h.Walker.SetAutomaticWalkTeleports(() => Allow(Arch));

        h.Walker.WalkTo(new RoomKey(7, 131));

        Assert.Single(h.Sent);
        Assert.Equal("go arch\r", Encoding.Latin1.GetString(h.Sent[0]));
    }

    // Allowing some other teleport doesn't open this one: each is its own choice.
    [Fact]
    public void AutomaticWalk_AnotherTeleportAllowed_StillGoesOnFoot()
    {
        Harness h = ArmTeleport(NewHarness(TeleportVsWalkGraphJson));
        h.Walker.SetAutomaticWalkTeleports(() => Allow((new RoomKey(1, 11), new RoomKey(7, 131))));

        h.Walker.WalkTo(new RoomKey(7, 131));

        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));
    }

    // When the only way there is a teleport the walk may not use, it doesn't take it
    // quietly: it fails, says it was refused as an automatic walk, and names the
    // line to tick as Settings → Teleports shows it.
    [Fact]
    public void AutomaticWalk_OnlyRouteIsATeleportItMayNotUse_FailsAndNamesTheLineToTick()
    {
        Harness h = ArmTeleport(NewHarness(TeleportGraphJson));
        h.Walker.SetAutomaticWalkTeleports(() => Allow());

        h.Walker.WalkTo(new RoomKey(7, 131));

        Assert.Empty(h.Sent);
        Assert.Equal(WalkState.Idle, h.Walker.State);
        WalkEvent failed = Assert.Single(h.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Contains("this is an automatic walk", failed.Detail);
        Assert.Contains("Tick \"Grove (1/10) → Stone Arch (7/131)\" on Settings → Teleports", failed.Detail);
    }

    // A two-way spot is one line on the tab, listed from either end: the walk names
    // it as the tab does, not by the direction it wanted to take it.
    [Fact]
    public void AutomaticWalk_RefusedTeleport_IsNamedByItsLineOnTheTeleportsTab()
    {
        Harness h = ArmTeleport(NewHarness(TeleportGraphJson));
        h.Walker.SetAutomaticWalkTeleports(() => Allow());
        (RoomKey From, RoomKey To) back = (Arch.To, Arch.From);
        h.Walker.SetTeleportChoices(() => new[]
        {
            new TeleportChoice(Arch.To, "Stone Arch", Arch.From, "Grove", TwoWay: true, "go arch", "", 0,
                new[] { back, Arch }),
        });

        h.Walker.WalkTo(new RoomKey(7, 131));

        WalkEvent failed = Assert.Single(h.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Contains("Tick \"Stone Arch (7/131) ⇄ Grove (1/10)\"", failed.Detail);
    }

    // Two teleports in a row, neither allowed: no one box opens the route, and the
    // walk says so and names both instead of pointing at the first.
    //
    // 1/10 Grove --SW (CMD 100)--> 7/131 Stone Arch --N (CMD 101)--> 7/140 Far Gate
    private const string TwoTeleportsGraphJson = """
        [
          { "Map Number": 1, "Room Number": 10, "Name": "Grove",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "CMD": 100,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "7/131 (Item: 474)",
            "U": "0", "D": "0" },
          { "Map Number": 7, "Room Number": 131, "Name": "Stone Arch",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "CMD": 101,
            "N": "7/140 (Item: 474)", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0",
            "U": "0", "D": "0" },
          { "Map Number": 7, "Room Number": 140, "Name": "Far Gate",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0",
            "U": "0", "D": "0" }
        ]
        """;

    private const string TwoTeleportsTbinfoJson = """
        [ { "Number": 100, "Action": "go arch:teleport 131 7\n" },
          { "Number": 101, "Action": "go gate:teleport 140 7\n" } ]
        """;

    [Fact]
    public void AutomaticWalk_TwoRefusedTeleportsInARow_SaysNoSingleLineOpensARoute()
    {
        Harness h = ArmTeleport(NewHarness(TwoTeleportsGraphJson, tbinfoJson: TwoTeleportsTbinfoJson));
        h.Walker.SetAutomaticWalkTeleports(() => Allow());

        h.Walker.WalkTo(new RoomKey(7, 140));

        WalkEvent failed = Assert.Single(h.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Contains("uses 2 teleports it isn't allowed; ticking any one of them alone opens no route", failed.Detail);
        Assert.Contains("\"Grove (1/10) → Stone Arch (7/131)\"; \"Stone Arch (7/131) → Far Gate (7/140)\"", failed.Detail);
    }

    // With one of the two already ticked, the other is the one box that opens it.
    [Fact]
    public void AutomaticWalk_OneOfTwoTeleportsAllowed_NamesTheOther()
    {
        Harness h = ArmTeleport(NewHarness(TwoTeleportsGraphJson, tbinfoJson: TwoTeleportsTbinfoJson));
        h.Walker.SetAutomaticWalkTeleports(() => Allow(Arch));

        h.Walker.WalkTo(new RoomKey(7, 140));

        WalkEvent failed = Assert.Single(h.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Contains("Tick \"Stone Arch (7/131) → Far Gate (7/140)\" on Settings → Teleports", failed.Detail);
    }

    // A walk the user started says which route it wants (the route cards pass it),
    // and the list is never read for it, whatever it holds.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UserWalk_KeepsItsOwnChoice_WhateverIsAllowed(bool archAllowed)
    {
        IReadOnlySet<(RoomKey From, RoomKey To)> allowed = archAllowed ? Allow(Arch) : Allow();

        Harness teleportPick = ArmTeleport(NewHarness(TeleportVsWalkGraphJson));
        teleportPick.Walker.SetAutomaticWalkTeleports(() => allowed);
        teleportPick.Walker.WalkTo(new RoomKey(7, 131), preferTeleportFree: false);   // "Teleport"
        Assert.Equal("go arch\r", Encoding.Latin1.GetString(teleportPick.Sent[0]));

        Harness plainCommit = ArmTeleport(NewHarness(TeleportVsWalkGraphJson));
        plainCommit.Walker.SetAutomaticWalkTeleports(() => allowed);
        plainCommit.Walker.WalkTo(new RoomKey(7, 131), preferTeleportFree: true);     // any other card
        Assert.Equal("n\r", Encoding.Latin1.GetString(plainCommit.Sent[0]));

        Harness walkIt = ArmTeleport(NewHarness(TeleportVsWalkGraphJson));
        walkIt.Walker.SetAutomaticWalkTeleports(() => allowed);
        walkIt.Walker.WalkTo(new RoomKey(7, 131), avoidTeleports: true, preferTeleportFree: true);   // "Walk it"
        Assert.Equal("n\r", Encoding.Latin1.GetString(walkIt.Sent[0]));

        // The only route being a teleport, the user's plain commit still takes it.
        Harness onlyWay = ArmTeleport(NewHarness(TeleportGraphJson));
        onlyWay.Walker.SetAutomaticWalkTeleports(() => allowed);
        onlyWay.Walker.WalkTo(new RoomKey(7, 131), preferTeleportFree: true);
        Assert.Equal("go arch\r", Encoding.Latin1.GetString(onlyWay.Sent[0]));
    }

    // The list is read as each walk starts, so a change reaches the next walk
    // without a reconnect.
    [Fact]
    public void AutomaticWalkTeleports_AreReadAtTheStartOfEachWalk()
    {
        IReadOnlySet<(RoomKey From, RoomKey To)> allowed = Allow(Arch);
        Harness h = ArmTeleport(NewHarness(TeleportVsWalkGraphJson));
        h.Walker.SetAutomaticWalkTeleports(() => allowed);

        h.Walker.WalkTo(new RoomKey(7, 131));
        Assert.Equal("go arch\r", Encoding.Latin1.GetString(h.Sent[0]));

        allowed = Allow();
        h.Walker.Stop("test");
        h.Sent.Clear();
        h.Tracker.SetLocated(new RoomKey(1, 10));
        h.Walker.WalkTo(new RoomKey(7, 131));

        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));
    }

    // A loop walks to its nearest room before it starts lapping. Started by the user
    // that walk is the user's own and keeps the shortest route; started by an event,
    // a remote command or a sweep it is an automatic walk and uses only what is allowed.
    [Theory]
    [InlineData(true, "go arch\r")]
    [InlineData(false, "n\r")]
    public void LoopApproach_IsAnAutomaticWalk_OnlyWhenTheUserDidNotStartIt(bool userStarted, string firstSend)
    {
        Harness h = ArmTeleport(NewHarness(TeleportVsWalkGraphJson));
        h.Walker.SetAutomaticWalkTeleports(() => Allow());
        LoopRunner runner = new(h.Tracker, h.Coordinator, graph: h.Graph, bfs: h.Bfs,
            walker: h.Walker, postToUi: a => a());
        runner.SetWireSender(b => h.Sent.Add(b));
        Loop loop = new("arch", new[] { new RoomKey(7, 131), new RoomKey(1, 12) });

        Assert.True(runner.Start(loop, userStarted));

        Assert.Equal(LoopState.Approaching, runner.State);
        Assert.Equal(firstSend, Encoding.Latin1.GetString(h.Sent[0]));
    }

    // The user asked to go to the loop, not for what the run does once there: after
    // the loop is reached, the walk back to it from a detour is an automatic walk. It
    // is not asked about, and uses only the teleports allowed to automatic walks.
    [Fact]
    public void UserStartedLoop_OnceReached_WalksBackAsAnAutomaticWalk()
    {
        Harness h = ArmTeleport(NewHarness(TeleportVsWalkGraphJson));
        h.Walker.SetAutomaticWalkTeleports(() => Allow());
        LoopRunner runner = new(h.Tracker, h.Coordinator, graph: h.Graph, bfs: h.Bfs,
            walker: h.Walker, postToUi: a => a());
        runner.SetWireSender(b => h.Sent.Add(b));
        Loop loop = new("arch", new[] { new RoomKey(7, 131), new RoomKey(1, 12) });
        h.Tracker.SetLocated(new RoomKey(1, 12));           // standing on the loop
        Assert.True(runner.Start(loop, userStarted: true));
        Assert.Equal(LoopState.Running, runner.State);

        // A detour took the character off to 1/10; the loop resumes from there.
        h.Tracker.SetLocated(new RoomKey(1, 10));
        h.Sent.Clear();
        Assert.True(runner.ResumeAfterDetour(loop));

        Assert.Equal(LoopState.Approaching, runner.State);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));
    }

    // ----- a loop started from off it: a walk-to first, then the loop --------

    private (LoopRunner Runner, LoopWalkHandoff Handoff, Harness H, Loop Loop) LoopHandoffAtTheGrove()
    {
        Harness h = ArmTeleport(NewHarness(TeleportVsWalkGraphJson));
        LoopRunner runner = new(h.Tracker, h.Coordinator, graph: h.Graph, bfs: h.Bfs,
            walker: h.Walker, postToUi: a => a());
        runner.SetWireSender(b => h.Sent.Add(b));
        LoopWalkHandoff handoff = new(runner, post: a => a());
        h.Walker.Event += handoff.OnWalkerEvent;
        runner.Event += handoff.OnLoopEvent;
        return (runner, handoff, h, new Loop("arch", new[] { new RoomKey(7, 131), new RoomKey(1, 12) }));
    }

    // Where a start from off the loop joins it is the destination of the walk-to.
    [Fact]
    public void NearestRoomOf_IsTheLoopRoomAStartWouldWalkTo_AndNothingWhenStandingOnIt()
    {
        var (runner, _, _, loop) = LoopHandoffAtTheGrove();

        Assert.Equal(new RoomKey(7, 131), runner.NearestRoomOf(loop, new RoomKey(1, 10)));   // one teleport hop
        Assert.Equal(new RoomKey(1, 12), runner.NearestRoomOf(loop, new RoomKey(1, 11)));
        Assert.Null(runner.NearestRoomOf(loop, new RoomKey(1, 12)));
        Assert.Equal(LoopState.Idle, runner.State);
    }

    // The loop runner is not involved until the walk-to arrives; then it has the run.
    [Fact]
    public void LoopHandoff_TheLoopStartsWhenTheWalkArrives()
    {
        var (runner, handoff, h, loop) = LoopHandoffAtTheGrove();
        RoomKey entry = new(7, 131);

        handoff.Begin(loop, entry);
        handoff.NoteWalkCommitted(entry);
        Assert.Equal(LoopState.Idle, runner.State);
        Assert.Same(loop, handoff.Pending);

        h.Tracker.SetLocated(entry);                          // the walk-to got there
        handoff.OnWalkerEvent(new WalkEvent(WalkEventKind.Finished, "reached", entry));

        Assert.Null(handoff.Pending);
        Assert.Equal(LoopState.Running, runner.State);
    }

    // A walk finishing somewhere else on the way (a stage of a longer trip) isn't the arrival.
    [Fact]
    public void LoopHandoff_AWalkFinishingElsewhere_IsNotTheArrival()
    {
        var (runner, handoff, _, loop) = LoopHandoffAtTheGrove();
        handoff.Begin(loop, new RoomKey(7, 131));

        handoff.OnWalkerEvent(new WalkEvent(WalkEventKind.Finished, "reached", new RoomKey(1, 11)));

        Assert.Same(loop, handoff.Pending);
        Assert.Equal(LoopState.Idle, runner.State);
    }

    [Fact]
    public void LoopHandoff_IsDropped_WhenTheWalkFails_OrGoesSomewhereElse_OrIsCancelled()
    {
        var (runner, handoff, _, loop) = LoopHandoffAtTheGrove();
        RoomKey entry = new(7, 131);

        handoff.Begin(loop, entry);
        handoff.OnWalkerEvent(new WalkEvent(WalkEventKind.Failed, "no path", entry));
        Assert.Null(handoff.Pending);

        handoff.Begin(loop, entry);
        handoff.NoteWalkCommitted(new RoomKey(1, 11));        // the user picked a walk that stops short
        Assert.Null(handoff.Pending);

        handoff.Begin(loop, entry);
        handoff.Cancel("cancelled at the route cards");
        Assert.Null(handoff.Pending);

        // Nothing is waiting, so a later arrival there starts nothing.
        handoff.OnWalkerEvent(new WalkEvent(WalkEventKind.Finished, "reached", entry));
        Assert.Equal(LoopState.Idle, runner.State);
    }

    // Another loop starting first supersedes the one that was waiting.
    [Fact]
    public void LoopHandoff_IsDropped_WhenAnotherLoopStarts()
    {
        var (runner, handoff, h, loop) = LoopHandoffAtTheGrove();
        handoff.Begin(loop, new RoomKey(7, 131));

        h.Tracker.SetLocated(new RoomKey(1, 12));
        Assert.True(runner.Start(new Loop("other", new[] { new RoomKey(1, 12), new RoomKey(1, 11) })));

        Assert.Null(handoff.Pending);
    }

    // ----- the list of teleports, built from the game data ----------------

    // A teleport to somewhere that can also be walked to is a shortcut: nothing lies
    // beyond it that can't be reached on foot.
    [Fact]
    public void TeleportCatalog_ListsAShortcut_WithNothingBeyondIt()
    {
        Harness h = NewHarness(TeleportVsWalkGraphJson);

        TeleportChoice choice = Assert.Single(TeleportCatalog.Build(h.Graph, (_, _) => "go arch"));

        Assert.Equal((new RoomKey(1, 10), "Grove"), (choice.From, choice.FromName));
        Assert.Equal((new RoomKey(7, 131), "Stone Arch"), (choice.To, choice.ToName));
        Assert.False(choice.TwoWay);
        Assert.Equal(0, choice.RoomsBeyond);
        Assert.Equal(new[] { Arch }, choice.Exits);
        Assert.Equal("go arch", choice.Commands);
    }

    // A room only a teleport reaches is counted as lying beyond it.
    [Fact]
    public void TeleportCatalog_CountsTheRoomsOnlyATeleportReaches()
    {
        const string json = """
            [
              { "Map Number": 1, "Room Number": 10, "Name": "Grove", "CMD": 100,
                "N": "1/11", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0",
                "SW": "7/131 (Item: 474)", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 11, "Name": "Trail",
                "N": "0", "S": "1/10", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 7, "Room Number": 131, "Name": "Stone Arch, Threshold",
                "N": "7/132", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 7, "Room Number": 132, "Name": "Stone Arch, Inner Ring",
                "N": "0", "S": "7/131", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        Harness h = NewHarness(json);

        TeleportChoice choice = Assert.Single(TeleportCatalog.Build(h.Graph, (_, _) => null));

        Assert.Equal("Stone Arch", choice.Area);
        Assert.Equal(2, choice.RoomsBeyond);
        Assert.Equal(string.Empty, choice.Commands);
    }

    // A teleport and the one straight back are one spot: one line, allowed together.
    [Fact]
    public void TeleportCatalog_ATeleportAndItsWayBack_AreOneSpot()
    {
        const string json = """
            [
              { "Map Number": 1, "Room Number": 10, "Name": "Grove", "CMD": 100,
                "N": "0", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0",
                "SW": "7/131 (Item: 474)", "U": "0", "D": "0" },
              { "Map Number": 7, "Room Number": 131, "Name": "Stone Arch", "CMD": 101,
                "N": "0", "S": "0", "E": "0", "W": "0", "NE": "1/10 (Item: 474)", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        Harness h = NewHarness(json, tbinfoJson: """
            [ { "Number": 100, "Action": "go arch:teleport 131 7\n" },
              { "Number": 101, "Action": "go grove:teleport 10 1\n" } ]
            """);

        TeleportChoice choice = Assert.Single(TeleportCatalog.Build(h.Graph,
            (from, _) => from.Map == 1 ? "go arch" : "go grove"));

        Assert.True(choice.TwoWay);
        Assert.Equal("go arch / go grove", choice.Commands);
        Assert.Equal(new[] { Arch, (Arch.To, Arch.From) }, choice.Exits);

        MudPlay.ViewModels.Settings.TeleportChoiceViewModel row = new(choice, allowed: false, changed: () => { });
        Assert.Equal("Grove (1/10) ⇄ Stone Arch (7/131)", row.Title);
        Assert.True(row.Matches("7/131"));
        Assert.True(row.Matches("go grove"));
        Assert.False(row.Matches("vortex"));
    }

    [Fact]
    public void TeleportCatalog_KeysRoundTrip_AndBadOnesAreDropped()
    {
        string key = TeleportCatalog.KeyOf(Arch.From, Arch.To);
        Assert.Equal("1/10>7/131", key);

        IReadOnlySet<(RoomKey From, RoomKey To)> parsed =
            TeleportCatalog.ParseKeys(new[] { key, "nonsense", "1/10>", "" });

        Assert.Equal(new[] { Arch }, parsed);
    }

}
