using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// PR 7.7 walker coverage — single-shot walk happy path, retry on
/// blocked move, abort on desync, pause/resume via coordinator,
/// destination-equals-source short-circuit, and superseding starts.
/// </summary>
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
    //
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
        public void Dispose() { /* nothing to dispose */ }
    }

    private Harness NewHarness(string json = LineGraphJson, bool wireRecovery = false)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), json);
        // TBInfo so the teleport fixture's CMD-100 room legitimately teleports to
        // 7/131 — the Item→Teleport promotion is TBInfo-gated on the CMD actually
        // teleporting to the exit's target (only rooms with CMD 100 + an exit to
        // 7/131, i.e. the teleport tests, are affected).
        File.WriteAllText(Path.Combine(_root, "alpha", "TBInfo.json"),
            """[ { "Number": 100, "Action": "go arch:teleport 131 7\n" } ]""");
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
        AutoWalkManager walker = new(graph, bfs, tracker, coord, recovery: gate);
        Harness h = new()
        {
            Graph = graph,
            Bfs = bfs,
            Tracker = tracker,
            Coordinator = coord,
            Walker = walker,
            Gate = gate,
        };
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
    public void WalkTo_LoudSupersede_RaisesStopped()
    {
        // The contrast: a genuine external walk (default supersede) still Stops
        // loudly, so a driving reroute knows the user / another engine took over.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));
        Assert.Equal(WalkState.Walking, h.Walker.State);
        h.Events.Clear();

        h.Walker.WalkTo(new RoomKey(1, 2));

        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Stopped);
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
        Assert.Single(scheduled);                // the watchdog was armed

        // The Confirmed transition never arrives — fire the watchdog.
        scheduled[0]();

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

    // ----- door-aware walks (PR 7.7b) -------------------------------

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
        h.Walker.FirePromptForTests();

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

    // ----- trapped exits (PR 7.22) -----------------------------------

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

    [Fact]
    public void Walker_TrapDisarmFailure_FailsTheWalk()
    {
        Harness h = NewHarness(TrapGraphJson);
        FakeTrapEnqueuer trap = new();
        h.Walker.SetTrapEnqueuer(trap.Enqueue);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));

        trap.Calls[0].Reply("Couldn't find trap to the north (20 attempts).");

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

    [Fact]
    public void Walker_TrappedExit_WithoutEnqueuerBound_SendsMoveDirectly()
    {
        // No trap enqueuer wired — walker falls back to the regular
        // path. This is the "until production wires TrapDisarmManager"
        // safety net.
        Harness h = NewHarness(TrapGraphJson);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 2));

        Assert.Single(h.Sent);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));
    }

    // ----- unbound wire sender --------------------------------------

    [Fact]
    public void NoWireSender_RecordsButSuppressesSend()
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
        AutoWalkManager walker = new(graph, bfs, tracker, coord);

        tracker.SetLocated(new RoomKey(1, 1));
        walker.WalkTo(new RoomKey(1, 3));

        // LastSentForTests captures bytes even with no wire bound, so
        // tests can validate the wire payload without a network.
        Assert.Single(walker.LastSentForTests);
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
    public void WalkTo_StopBeforeBossRoom_HaltsOneRoomShort()
    {
        // 1/1 → 1/2 → 1/3, with 1/3 flagged "stop before". A walk to 1/3 must be
        // re-pointed to 1/2 and finish there without ever stepping into 1/3.
        Harness h = NewHarness();
        h.Walker.SetBossStopRooms(() => new HashSet<RoomKey> { new(1, 3) });
        h.Tracker.SetLocated(new RoomKey(1, 1));

        h.Walker.WalkTo(new RoomKey(1, 3));
        Assert.Single(h.Sent);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));   // first hop toward 1/2

        // Arrive at 1/2 — the walk finishes here; no second N into 1/3.
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Single(h.Sent);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
    }

    [Fact]
    public void WalkTo_StopBeforeBossRoom_Adjacent_FinishesWithoutStepping()
    {
        // Standing next to the boss room (1/2, boss at 1/3): "one room short" is
        // the source itself, so the walk finishes immediately with nothing sent.
        Harness h = NewHarness();
        h.Walker.SetBossStopRooms(() => new HashSet<RoomKey> { new(1, 3) });
        h.Tracker.SetLocated(new RoomKey(1, 2));

        Assert.True(h.Walker.WalkTo(new RoomKey(1, 3)));
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
        Assert.Empty(h.Sent);
    }

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
}
