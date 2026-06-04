using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

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
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-walker-tests-" + Path.GetRandomFileName());
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

    private sealed class Harness : IDisposable
    {
        public required RoomGraphManager Graph { get; init; }
        public required BfsMapper Bfs { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required MovementCoordinator Coordinator { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public List<byte[]> Sent { get; } = new();
        public List<WalkEvent> Events { get; } = new();
        public void Dispose() { /* nothing to dispose */ }
    }

    private Harness NewHarness(string json = LineGraphJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        AutoWalkManager walker = new(graph, bfs, tracker, coord);
        Harness h = new()
        {
            Graph = graph,
            Bfs = bfs,
            Tracker = tracker,
            Coordinator = coord,
            Walker = walker,
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
    public void BlockedTwice_AbortsWithFailed()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));

        h.Tracker.NoteMoveBlocked();
        // The retry above sent step #2. Block again.
        h.Tracker.NoteMoveBlocked();

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    // ----- desync ---------------------------------------------------

    [Fact]
    public void DesyncedLanding_FailsAndStops()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 3));

        // Server reports a room that's neither expected nor source.
        // 1/3 itself (we expected 1/2) is the cleanest "wrong place".
        h.Tracker.NoteMoveSent(Direction.N);
        h.Tracker.NoteRoomObserved(new RoomObservation("C",
            new HashSet<Direction> { Direction.S }));

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Failed);
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
        Assert.Contains(events, e => e.Kind == WalkEventKind.Failed && e.Detail == "no path");
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

    // Cross-room multi-action — same shape but the Action#N cell
    // references room 1/2 instead of "this room". Walker should
    // fail fast with the cross-room reason.
    private const string MultiActionCrossRoomJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Chamber",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2 (Hidden/Needs 1 Actions, any order)",
            "S": "0",
            "E": "Action#1 [on the N exit of room 1/3]: pull lever",
            "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Switch",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2 (Hidden/Needs 1 Actions, any order)", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void Walker_MultiActionExit_CrossRoom_FailsWithReason()
    {
        Harness h = NewHarness(MultiActionCrossRoomJson);
        h.Tracker.SetLocated(new RoomKey(1, 3));
        h.Walker.WalkTo(new RoomKey(1, 2));

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events,
            e => e.Kind == WalkEventKind.Failed && e.Detail.Contains("cross-room"));
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
}
