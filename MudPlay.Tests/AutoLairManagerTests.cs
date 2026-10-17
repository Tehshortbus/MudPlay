using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// AutoLairManager: marker CRUD, the Start gates and dispatch, Pause/Resume and
// supersede, the Engaging exits, the door / wait-room choice, failed-entry retry
// and backoff, and the master-switch freeze. Headless: drives the graph + tracker
// directly with manual timers, no Telnet socket.
public sealed class AutoLairManagerTests : IDisposable
{
    private readonly string _root;

    public AutoLairManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-autolair-tests-" + Path.GetRandomFileName());
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

    // The entry and engage windows are each the newest timer the manager armed
    // beside its once-a-second scheduler.
    private static TimeSpan EngageWindow(Harness h) =>
        h.LairTimers.PendingDelays.Last(d => d != AutoLairManager.SchedulerInterval);

    private static void RunEntry(Harness h) =>
        h.LairTimers.RunDue(h.LairTimers.PendingDelays.Last(d => d != AutoLairManager.SchedulerInterval));

    private sealed class Harness : IDisposable
    {
        public required RoomTracker Tracker { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public required AutoLairManager Roam { get; init; }
        public required LairTimerStore Timers { get; init; }
        public required MovementCoordinator Coordinator { get; init; }
        // The manager's scheduler, entry, engage and retry timers.
        public required ManualTimers LairTimers { get; init; }
        // The program log, with the Debug channel on.
        public required LogService Log { get; init; }

        // The map searches that found no route to a lair, as the log tells them.
        public int NoRouteSearches => Log.Snapshot().Count(e => e.Message.StartsWith("searched the map for a route", StringComparison.Ordinal));
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
        ManualTimers lairTimers = new();
        LogService log = new() { Diagnostics = new LogDiagnosticState { DebugDiagnostics = true } };
        AutoLairManager roam = new(walker, tracker, graph, bfs, timers, log, coordinator: coord, schedule: lairTimers.Arm);
        return new Harness
        {
            Tracker = tracker,
            Walker = walker,
            Roam = roam,
            Timers = timers,
            Coordinator = coord,
            LairTimers = lairTimers,
            Log = log,
        };
    }

    // ----- marker CRUD ----------------------------------------------

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

    // ----- self-supersede reschedule loop --------------------------

    [Fact]
    public void Resume_WhileWalking_DoesNotSelfSupersedeIntoRescheduleLoop()
    {
        // Regression: Resume drops LastDecision then re-dispatches. The
        // re-issued WalkTo supersedes the still-in-flight walk, which raises
        // Stopped("superseded by new walk") synchronously — re-entering the
        // walker-event handler before the dispatch returns. That self-inflicted
        // stop must NOT be read as an external displacement: doing so nulled the
        // target the dispatch had just set, so the next 1 s scheduler tick
        // re-dispatched → superseded → Stopped → rescheduled, a ~1/sec loop that
        // never entered the lair. After Resume the session must still hold its
        // target and keep walking to the wait-room.
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 3));

        Assert.True(h.Roam.Start());
        Assert.Equal(new RoomKey(1, 1), h.Roam.CurrentTarget);
        Assert.Equal(WalkState.Walking, h.Walker.State);

        h.Roam.Pause();
        h.Roam.Resume();

        Assert.Equal(new RoomKey(1, 1), h.Roam.CurrentTarget);
        Assert.Equal(new RoomKey(1, 2), h.Roam.CurrentWaitRoom);
        Assert.NotNull(h.Roam.LastDecision);
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Equal(new RoomKey(1, 2), h.Walker.Destination);
    }

    [Fact]
    public void Stop_ClearsEntryArrivalLatch()
    {
        // CurrentEntryArrivalAt is part of the public surface so the
        // CURRENT NAV countdown can render against it. Stop() must
        // wipe the latch so a stale value doesn't leak into the idle render.
        // Reach Waiting with the latch set: a failed entry, then the retry that
        // waits it out in place.
        (Harness h, DoorCalls doors) = NewColiseumHarness(wayRound: false, stats: null);
        using (h)
        {
            Assert.True(h.Roam.Start());
            h.Tracker.NoteRoomObserved(new RoomObservation("Viewing Stands",
                new HashSet<Direction> { Direction.S, Direction.D }));
            doors.Calls[0].Reply(new DoorOpenResult.Failed("waitingopen timed out with no response"));
            h.LairTimers.RunDue(AutoLairManager.RetryBase);
            Assert.Equal(AutoLairPhase.Waiting, h.Roam.Phase);
            Assert.NotNull(h.Roam.CurrentEntryArrivalAt);

            h.Roam.Stop();

            Assert.Null(h.Roam.CurrentEntryArrivalAt);
        }
    }

    // ----- leaving Engaging on the fight ending ----------------------
    // Report stock-20260908-192900: Engaging was bound to a fixed 30s timer, so a
    // fight that finished in 10s still parked the scheduler for the remaining 20.
    // The Combat gate clearing is the reliable end-of-combat signal on stock —
    // CombatStateTracker clears it only when a room re-display shows no engageable
    // monster left. The raw `*Combat Off*` line is not usable here: the server
    // emits one on every cast and once per strike for non-sustaining attacks.

    private static Harness Engaging(Harness h)
    {
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 1));
        h.Roam.Mark(new RoomKey(1, 3));
        h.Roam.Start();
        h.Tracker.SetLocated(h.Roam.CurrentTarget!.Value);   // walked into the lair
        Assert.Equal(AutoLairPhase.Engaging, h.Roam.Phase);
        return h;
    }

    [Fact]
    public void Engaging_CombatClearedAfterAFight_LeavesForTheNextLair()
    {
        using Harness h = Engaging(NewHarness());

        h.Coordinator.AssertGate(MovementCoordinator.CombatGate, "test", "fight on");
        Assert.Equal(AutoLairPhase.Engaging, h.Roam.Phase);   // still fighting

        h.Coordinator.ClearGate(MovementCoordinator.CombatGate, "test", "room cleared");

        Assert.NotEqual(AutoLairPhase.Engaging, h.Roam.Phase);
    }

    [Fact]
    public void Engaging_BeforeAnyFight_StaysPut()
    {
        // Entering a lair asserts nothing until a monster is seen, so a gate that
        // was never asserted means combat hasn't started — not that it's over.
        // Acting on it would walk straight back out of every lair on arrival.
        using Harness h = Engaging(NewHarness());

        h.Coordinator.AssertGate(MovementCoordinator.SearchGate, "test", "unrelated");
        h.Coordinator.ClearGate(MovementCoordinator.SearchGate, "test", "unrelated");

        Assert.Equal(AutoLairPhase.Engaging, h.Roam.Phase);
    }

    [Fact]
    public void Engaging_WaitsForLootBeforeLeaving()
    {
        // The kill's drops are still being collected — leaving now abandons the
        // loot we just fought for.
        using Harness h = Engaging(NewHarness());

        h.Coordinator.AssertGate(MovementCoordinator.CombatGate, "test", "fight on");
        h.Coordinator.AssertGate(MovementCoordinator.AcquisitionGate, "test", "gets pending");
        h.Coordinator.ClearGate(MovementCoordinator.CombatGate, "test", "room cleared");

        Assert.Equal(AutoLairPhase.Engaging, h.Roam.Phase);   // held for the loot

        h.Coordinator.ClearGate(MovementCoordinator.AcquisitionGate, "test", "gets-confirmed");

        Assert.NotEqual(AutoLairPhase.Engaging, h.Roam.Phase);
    }

    // Frozen by the master switch, the scheduler stands down too: its engage
    // window does not call the lair empty, and the Combat gate being parked is
    // not read as the fight being over. It carries on when the freeze lifts.
    [Fact]
    public void Engaging_FrozenByTheMasterSwitch_DoesNotFinishOnItsTimerOrOnAParkedGate()
    {
        using Harness h = Engaging(NewHarness());
        h.Coordinator.AssertGate(MovementCoordinator.CombatGate, "test", "fight on");
        h.Coordinator.AssertGate(MovementCoordinator.AutoAllGate, "test", "master switch off");
        h.Coordinator.ParkHoldsForMasterSwitch();

        h.LairTimers.RunDue(EngageWindow(h));
        Assert.Equal(AutoLairPhase.Engaging, h.Roam.Phase);

        h.Coordinator.RestoreHoldsAfterMasterSwitch();
        h.Coordinator.ClearGate(MovementCoordinator.AutoAllGate, "test", "master switch on");
        Assert.Equal(AutoLairPhase.Engaging, h.Roam.Phase);   // the fight is still on

        h.Coordinator.ClearGate(MovementCoordinator.CombatGate, "test", "killed");
        Assert.NotEqual(AutoLairPhase.Engaging, h.Roam.Phase);
    }

    // The window passed over while frozen keeps running: its next tick after the
    // freeze lifts calls the empty lair.
    [Fact]
    public void Engaging_WindowPassedOverWhileFrozen_EndsTheEngagementOnItsNextTick()
    {
        using Harness h = Engaging(NewHarness());
        TimeSpan window = EngageWindow(h);
        h.Coordinator.AssertGate(MovementCoordinator.AutoAllGate, "test", "master switch off");

        h.LairTimers.RunDue(window);
        Assert.Equal(AutoLairPhase.Engaging, h.Roam.Phase);

        h.Coordinator.ClearGate(MovementCoordinator.AutoAllGate, "test", "master switch on");
        Assert.Equal(AutoLairPhase.Engaging, h.Roam.Phase);
        h.LairTimers.RunDue(window);

        Assert.NotEqual(AutoLairPhase.Engaging, h.Roam.Phase);
    }

    [Fact]
    public void Engaging_EmptyLair_UsesAShortWindowNotTheEngageTimeout()
    {
        // Walking into a lair that hasn't respawned used to cost the full 30s
        // engage timeout — most of the idle time in report stock-20260908-192900.
        // Entering already burned the lair's timer, so there's nothing to gain by
        // standing in an empty room.
        using Harness h = Engaging(NewHarness());

        Assert.True(EngageWindow(h) < TimeSpan.FromSeconds(h.Roam.EngageTimeoutSeconds));

        h.LairTimers.RunDue(EngageWindow(h));

        Assert.NotEqual(AutoLairPhase.Engaging, h.Roam.Phase);
    }

    [Fact]
    public void Engaging_FightStarts_WidensTheWindowToTheEngageTimeout()
    {
        // Once there IS a fight, the short empty-lair window must not cut it off —
        // the kill gets the full engage budget.
        using Harness h = Engaging(NewHarness());
        TimeSpan empty = EngageWindow(h);

        h.Coordinator.AssertGate(MovementCoordinator.CombatGate, "test", "fight on");

        Assert.True(EngageWindow(h) > empty);
        Assert.Equal(TimeSpan.FromSeconds(h.Roam.EngageTimeoutSeconds), EngageWindow(h));
        Assert.Equal(AutoLairPhase.Engaging, h.Roam.Phase);
    }

    // ----- doors: the wait room and a lair no walk can get into -----------

    // The Ancient Coliseum with the street south of it. From Outside (3/589) the
    // Arena (3/595) is north then one step down through a door needing 301, or
    // north, east (a door needing 21), down and west. A second marker far down
    // the street (3/584) keeps the run at two lairs.
    private static string ColiseumJson(bool wayRound)
    {
        static string Room(int number, string name, params (string Dir, string Cell)[] exits)
        {
            string cells = string.Join(", ", new[] { "N", "S", "E", "W", "NE", "NW", "SE", "SW", "U", "D" }
                .Select(d => $"\"{d}\": \"{exits.FirstOrDefault(e => e.Dir == d).Cell ?? "0"}\""));
            return $"{{ \"Map Number\": 3, \"Room Number\": {number}, \"Name\": \"{name}\", "
                + $"\"Light\": 0, \"Shop\": 0, \"Lair\": \"\", \"Delay\": 0, {cells} }}";
        }

        List<string> rooms = new()
        {
            Room(589, "Outside Coliseum", ("N", "3/592"), ("S", "3/588")),
            Room(592, "Viewing Stands", ("S", "3/589"), ("D", "3/595 (Door [301 picklocks/strength])"),
                ("E", wayRound ? "3/593 (Door [21 picklocks/strength])" : "0")),
            Room(593, "Wide Passage", ("W", "3/592 (Door [21 picklocks/strength])"), ("D", "3/594")),
            Room(594, "Preparation Chamber", ("W", "3/595"), ("U", "3/593")),
            Room(595, "Arena", ("E", "3/594"), ("U", "3/592 (Door [201 picklocks/strength])")),
        };
        for (int n = 588; n >= 584; n--)
            rooms.Add(Room(n, $"Street {n}", ("N", $"3/{n + 1}"), ("S", n > 584 ? $"3/{n - 1}" : "0")));
        return "[" + string.Join(",\n", rooms) + "]";
    }

    private sealed class DoorCalls
    {
        public List<(Direction Dir, Action<DoorOpenResult> Reply)> Calls { get; } = new();
    }

    // Stats the movement filter reads that a test can change mid-run; null for
    // either is a stat screen not yet read.
    private sealed class LiveStats
    {
        public int? Strength { get; set; }
        public int? Picklocks { get; set; }
    }

    // stats: the character's Strength and Picklocks as the movement filter reads
    // them; null leaves the walker with no filter, as before any stat screen. live
    // gives a filter whose stats the test sets later instead. marked: the lairs,
    // the Arena and the far street by default.
    private (Harness H, DoorCalls Doors) NewColiseumHarness(
        bool wayRound, (int Strength, int Picklocks)? stats, LiveStats? live = null, RoomKey[]? marked = null)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), ColiseumJson(wayRound));
        File.WriteAllText(Path.Combine(_root, "alpha", "Lairs.json"), "[]");
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        MovementFilter? filter = null;
        if (stats is { } s)
        {
            ProfileService profile = TestProfiles.Service();
            profile.LoadDefaultProfile();
            filter = new MovementFilter(profile)
            {
                StrengthProvider = () => s.Strength,
                PicklocksProvider = () => s.Picklocks,
                MaxBashableStrengthProvider = () => 200,
            };
        }
        else if (live is not null)
        {
            ProfileService profile = TestProfiles.Service();
            profile.LoadDefaultProfile();
            filter = new MovementFilter(profile)
            {
                StrengthProvider = () => live.Strength,
                PicklocksProvider = () => live.Picklocks,
                MaxBashableStrengthProvider = () => 200,
            };
        }
        AutoWalkManager walker = new(graph, bfs, tracker, coord, filter: filter);
        walker.SetWireSender(_ => { });
        DoorCalls doors = new();
        walker.SetDoorEnqueuer((dir, _, _, _, _, reply) => doors.Calls.Add((dir, reply)));
        LairTimerStore timers = new(cache, graph, tracker);
        ManualTimers lairTimers = new();
        LogService log = new() { Diagnostics = new LogDiagnosticState { DebugDiagnostics = true } };
        AutoLairManager roam = new(walker, tracker, graph, bfs, timers, log, coordinator: coord, schedule: lairTimers.Arm);
        foreach (RoomKey lair in marked ?? new[] { new RoomKey(3, 595), new RoomKey(3, 584) })
            roam.Mark(lair);
        tracker.SetLocated(new RoomKey(3, 589));
        return (new Harness
        {
            Tracker = tracker, Walker = walker, Roam = roam, Timers = timers, Coordinator = coord, LairTimers = lairTimers,
            Log = log,
        }, doors);
    }

    // User, 2026-10-10: a wait room can be any room that isn't the lair, ideally the
    // one a step away. A step away on the route the walk will take: with the door
    // down closed to this character, that is the Preparation Chamber on the way
    // round, where the search with no filter chose the Viewing Stands above the door.
    [Theory]
    [InlineData(120, 0)]     // can't open the door down at all
    [InlineData(120, 303)]   // could only pick it, at 3% a try
    public void WaitRoom_IsTheLastRoomOnTheRouteTheWalkWillTake(int strength, int picklocks)
    {
        (Harness h, _) = NewColiseumHarness(wayRound: true, (strength, picklocks));
        using (h)
        {
            Assert.True(h.Roam.Start());

            Assert.Equal(new RoomKey(3, 595), h.Roam.CurrentTarget);
            Assert.Equal(new RoomKey(3, 594), h.Roam.CurrentWaitRoom);
            Assert.Equal(new RoomKey(3, 594), h.Walker.Destination);
        }
    }

    [Fact]
    public void WaitRoom_BesideADoorTheCharacterOpens_IsStillTheRoomAtTheDoor()
    {
        // Picklocks 400 against 301: a pick that can't miss.
        (Harness h, _) = NewColiseumHarness(wayRound: true, (120, 400));
        using (h)
        {
            Assert.True(h.Roam.Start());
            Assert.Equal(new RoomKey(3, 592), h.Roam.CurrentWaitRoom);
        }
    }

    [Fact]
    public void Lair_NoRouteLeadsTo_IsLeftOutOfTheRun_NotWalkedAtAndFailed()
    {
        (Harness h, DoorCalls doors) = NewColiseumHarness(wayRound: false, (120, 0));
        using (h)
        {
            Assert.True(h.Roam.Start());

            Assert.Equal(new RoomKey(3, 584), h.Roam.CurrentTarget);
            Assert.Empty(doors.Calls);
        }
    }

    // The report (user, 2026-10-10): the picks at the door were sent, failed, and
    // the character then stood there. The walk into the lair failed with the run in
    // Entering, where its retry dispatched nothing. Now the run takes the door the
    // walk gave up on, leaves the lair only that door leads to, and goes on to the
    // next one. A door the character cannot realistically open (no pick or bash can
    // work, or a pick under 25% a try) is out for the whole run.
    [Theory]
    [InlineData(120, 0)]     // can't open the door down at all
    [InlineData(120, 303)]   // could only pick it, at 3% a try
    public void WalkIntoTheLairFails_AtADoorTheCharacterCannotOpen_TheRunMovesOn_AndNeverAsksForThatDoorAgain(
        int strength, int picklocks)
    {
        LiveStats stats = new();
        (Harness h, DoorCalls doors) = NewColiseumHarness(wayRound: false, stats: null, live: stats);
        using (h)
        {
            Assert.True(h.Roam.Start());
            Assert.Equal(new RoomKey(3, 595), h.Roam.CurrentTarget);
            Assert.Equal(new RoomKey(3, 592), h.Roam.CurrentWaitRoom);

            // Arriving at the wait room with the lair due goes straight in, to the door.
            h.Tracker.NoteRoomObserved(new RoomObservation("Viewing Stands",
                new HashSet<Direction> { Direction.S, Direction.D }));
            Assert.Equal(AutoLairPhase.Entering, h.Roam.Phase);
            Assert.Equal(Direction.D, Assert.Single(doors.Calls).Dir);

            // The stat screen is read while the picks are going.
            stats.Strength = strength;
            stats.Picklocks = picklocks;
            doors.Calls[0].Reply(new DoorOpenResult.Failed("pick exhausted; no viable fallback verb", Unopenable: true));

            Assert.Equal(AutoLairPhase.Approaching, h.Roam.Phase);
            Assert.Contains((new RoomKey(3, 592), new RoomKey(3, 595)), h.Roam.AbandonedDoors);
            Assert.Contains("couldn't open the door down", h.Roam.LastWalkerFailure);

            h.LairTimers.RunDue(AutoLairManager.RetryBase);

            Assert.Equal(new RoomKey(3, 584), h.Roam.CurrentTarget);
            Assert.Equal(WalkState.Walking, h.Walker.State);
            Assert.Equal(new RoomKey(3, 585), h.Walker.Destination);
            Assert.Single(doors.Calls);
        }
    }

    // User, 2026-10-10: "try again on next visit". A door the picks ran out on at fair
    // odds (25% or better) was bad luck, and so was one failed with the stats unread
    // (no chance to compute): it is out for the walk that failed only, and the lair's
    // next visit tries it again.
    [Theory]
    [InlineData(120, 400)]   // a pick that opens it every time
    [InlineData(120, 326)]   // exactly 26%
    [InlineData(null, null)] // stat screen never read
    public void WalkIntoTheLairFails_AtAFairOddsDoor_TheDoorIsAskedForAgainOnTheNextVisit(
        int? strength, int? picklocks)
    {
        LiveStats stats = new();
        (Harness h, DoorCalls doors) = NewColiseumHarness(wayRound: false, stats: null, live: stats);
        using (h)
        {
            Assert.True(h.Roam.Start());
            h.Tracker.NoteRoomObserved(new RoomObservation("Viewing Stands",
                new HashSet<Direction> { Direction.S, Direction.D }));
            Assert.Equal(Direction.D, Assert.Single(doors.Calls).Dir);

            stats.Strength = strength;
            stats.Picklocks = picklocks;
            doors.Calls[0].Reply(new DoorOpenResult.Failed("pick exhausted; no viable fallback verb", Unopenable: true));

            Assert.Equal(AutoLairPhase.Approaching, h.Roam.Phase);
            Assert.Empty(h.Roam.AbandonedDoors);

            h.LairTimers.RunDue(AutoLairManager.RetryBase);

            Assert.Equal(new RoomKey(3, 595), h.Roam.CurrentTarget);
            Assert.Equal(new RoomKey(3, 592), h.Roam.CurrentWaitRoom);
            Assert.Equal(AutoLairPhase.Waiting, h.Roam.Phase);
            RunEntry(h);
            Assert.Equal(AutoLairPhase.Entering, h.Roam.Phase);
            Assert.Equal(2, doors.Calls.Count);
            Assert.Equal(Direction.D, doors.Calls[1].Dir);
        }
    }

    // The scheduler asks once a second for every marker, and a search that finds no
    // route floods the whole graph: the verdict stands until the room, the doors
    // given up on, or the stats the door rule reads change.
    [Fact]
    public void LairNoRouteLeadsTo_IsSearchedForOnce_NotOnEveryTick()
    {
        LiveStats stats = new() { Strength = 120, Picklocks = 0 };
        (Harness h, _) = NewColiseumHarness(wayRound: false, stats: null, live: stats);
        using (h)
        {
            Assert.True(h.Roam.Start());
            Assert.Equal(1, h.NoRouteSearches);

            for (int i = 0; i < 10; i++) h.LairTimers.RunDue(AutoLairManager.SchedulerInterval);
            Assert.Equal(1, h.NoRouteSearches);

            // The stats the door rule reads change (still no way in): looked at again.
            stats.Strength = 121;
            h.LairTimers.RunDue(AutoLairManager.SchedulerInterval);
            Assert.Equal(2, h.NoRouteSearches);
            h.LairTimers.RunDue(AutoLairManager.SchedulerInterval);
            Assert.Equal(2, h.NoRouteSearches);

            // So is a different room.
            h.Tracker.SetLocated(new RoomKey(3, 588));
            h.LairTimers.RunDue(AutoLairManager.SchedulerInterval);
            Assert.Equal(3, h.NoRouteSearches);
            h.LairTimers.RunDue(AutoLairManager.SchedulerInterval);
            Assert.Equal(3, h.NoRouteSearches);
        }
    }

    // The way in to the Arena is 3/592 → east, 3/593 → down, 3/594 → west, with the
    // door from 3/592 straight down impossible for this character. With 3/593, 3/594
    // and 3/595 all lairs, the nearest room on the route that isn't one is 3/592,
    // the start of the way round (the walk in then goes east, down, west), where the
    // lair's own neighbour rule chose it from behind the door that can't be opened.
    [Fact]
    public void WaitRoom_WhenTheNaturalOneIsAMarkedLair_IsTheNearestUnmarkedRoomOnTheRoute()
    {
        RoomKey[] lairs = { new(3, 593), new(3, 594), new(3, 595) };
        (Harness h, _) = NewColiseumHarness(wayRound: true, (120, 0), marked: lairs);
        using (h)
        {
            (RoomKey? waitRoom, int? hops) =
                h.Roam.PickWaitRoom(new RoomKey(3, 589), new RoomKey(3, 595), h.Walker.AutomaticWalkFilter());

            Assert.Equal(new RoomKey(3, 592), waitRoom);
            Assert.Equal(1, hops);
        }
    }

    [Fact]
    public void WaitRoom_WhenTheNaturalOneIsAMarkedLair_IsAnUnmarkedRoomBetweenOnTheRoute()
    {
        RoomKey[] lairs = { new(3, 594), new(3, 595) };
        (Harness h, _) = NewColiseumHarness(wayRound: true, (120, 0), marked: lairs);
        using (h)
        {
            (RoomKey? waitRoom, int? hops) =
                h.Roam.PickWaitRoom(new RoomKey(3, 589), new RoomKey(3, 595), h.Walker.AutomaticWalkFilter());

            // 3/593, a step before the lair at 3/594, not 3/592 beside the lair.
            Assert.Equal(new RoomKey(3, 593), waitRoom);
            Assert.Equal(2, hops);
        }
    }

    // A failed entry for a reason that may pass is retried without end, and each
    // further failure at the same lair waits twice as long, from 2 s to a cap of
    // 60 s, until an entry works.
    [Fact]
    public void FailedEntries_ToTheSameLair_BackOffToTheCap_UntilOneWorks()
    {
        (Harness h, DoorCalls doors) = NewColiseumHarness(wayRound: false, stats: null);
        using (h)
        {
            Assert.True(h.Roam.Start());
            h.Tracker.NoteRoomObserved(new RoomObservation("Viewing Stands",
                new HashSet<Direction> { Direction.S, Direction.D }));

            int[] expected = { 2, 4, 8, 16, 32, 60, 60 };
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(AutoLairPhase.Entering, h.Roam.Phase);
                Assert.Equal(i + 1, doors.Calls.Count);
                doors.Calls[i].Reply(new DoorOpenResult.Failed("waitingopen timed out with no response"));

                Assert.Contains(TimeSpan.FromSeconds(expected[i]), h.LairTimers.PendingDelays);
                h.LairTimers.RunDue(TimeSpan.FromSeconds(expected[i]));
                RunEntry(h);
            }

            // This entry works: the door opens and the character steps into the Arena.
            Assert.Equal(AutoLairPhase.Entering, h.Roam.Phase);
            doors.Calls[^1].Reply(DoorOpenResult.Opened.Instance);
            h.Tracker.NoteRoomObserved(new RoomObservation("Arena",
                new HashSet<Direction> { Direction.E, Direction.U }));

            Assert.Equal(AutoLairPhase.Engaging, h.Roam.Phase);
        }
    }

    // A walk into the lair that fails for a reason that may pass (not a door that
    // beat the character) has the retry set the entry up again, where it used to
    // leave the run in Entering with nothing to wake it.
    [Fact]
    public void WalkIntoTheLairFails_ForAPassingReason_TheRetrySetsTheEntryUpAgain()
    {
        (Harness h, DoorCalls doors) = NewColiseumHarness(wayRound: false, stats: null);
        using (h)
        {
            Assert.True(h.Roam.Start());
            h.Tracker.NoteRoomObserved(new RoomObservation("Viewing Stands",
                new HashSet<Direction> { Direction.S, Direction.D }));
            doors.Calls[0].Reply(new DoorOpenResult.Failed("waitingopen timed out with no response"));
            Assert.Equal(AutoLairPhase.Approaching, h.Roam.Phase);
            Assert.Empty(h.Roam.AbandonedDoors);

            h.LairTimers.RunDue(AutoLairManager.RetryBase);

            // Standing in the wait room already, so it waits out the entry step
            // there and goes in on the entry timer.
            Assert.Equal(new RoomKey(3, 595), h.Roam.CurrentTarget);
            Assert.Equal(new RoomKey(3, 592), h.Roam.CurrentWaitRoom);
            Assert.Equal(AutoLairPhase.Waiting, h.Roam.Phase);
        }
    }

    // The entry timer's interval is the whole wait. A tick that lands while the
    // master switch has the run frozen is not acted on, and left at that the
    // entry came one more full wait after the switch was back on. It is owed
    // instead, and made as the freeze lifts.
    [Fact]
    public void Waiting_EntryComesDueWhileFrozen_GoesInAsTheFreezeLifts()
    {
        (Harness h, DoorCalls doors) = NewColiseumHarness(wayRound: false, stats: null);
        using (h)
        {
            Assert.True(h.Roam.Start());
            h.Tracker.NoteRoomObserved(new RoomObservation("Viewing Stands",
                new HashSet<Direction> { Direction.S, Direction.D }));
            doors.Calls[0].Reply(new DoorOpenResult.Failed("waitingopen timed out with no response"));
            h.LairTimers.RunDue(AutoLairManager.RetryBase);
            Assert.Equal(AutoLairPhase.Waiting, h.Roam.Phase);

            h.Coordinator.AssertGate(MovementCoordinator.AutoAllGate, "test", "master switch off");
            RunEntry(h);
            Assert.Equal(AutoLairPhase.Waiting, h.Roam.Phase);

            h.Coordinator.ClearGate(MovementCoordinator.AutoAllGate, "test", "master switch on");
            Assert.Equal(AutoLairPhase.Entering, h.Roam.Phase);
        }
    }

    // The entry came due while frozen, and the user then walked into the lair by
    // hand and a fight began. The gate change that lifts the freeze settles the
    // owed entry (nothing to do: no longer waiting) and must still be read for
    // the fight, or the fight's end is never seen as one.
    [Fact]
    public void Waiting_EntryOwed_ThenWalkedInByHandWhileFrozen_TheFreezeLiftingIsStillReadForTheFight()
    {
        (Harness h, DoorCalls doors) = NewColiseumHarness(wayRound: false, stats: null);
        using (h)
        {
            Assert.True(h.Roam.Start());
            h.Tracker.NoteRoomObserved(new RoomObservation("Viewing Stands",
                new HashSet<Direction> { Direction.S, Direction.D }));
            doors.Calls[0].Reply(new DoorOpenResult.Failed("waitingopen timed out with no response"));
            h.LairTimers.RunDue(AutoLairManager.RetryBase);
            h.Coordinator.AssertGate(MovementCoordinator.AutoAllGate, "test", "master switch off");
            RunEntry(h);

            h.Tracker.SetLocated(new RoomKey(3, 595));   // in the lair, by the user's own steps
            h.Coordinator.AssertGate(MovementCoordinator.CombatGate, "test", "fight on");
            h.Coordinator.ClearGate(MovementCoordinator.AutoAllGate, "test", "master switch on");
            Assert.Equal(AutoLairPhase.Engaging, h.Roam.Phase);

            h.Coordinator.ClearGate(MovementCoordinator.CombatGate, "test", "killed");
            Assert.NotEqual(AutoLairPhase.Engaging, h.Roam.Phase);
        }
    }
}
