using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Walker-integration coverage for the sys-goto routing shortcut: when a
// destination is only (or more cheaply) reachable by a `sys goto` jump, the
// walker plans a SysGotoStep, fires the jump, waits for the landing room to
// confirm (tolerating churn), then walks the land leg from the landing. The
// fixture's goal is reachable ONLY via the jump — the source has no land route to
// it — so the jump is the sole crossing.
public sealed class AutoWalkManagerSysGotoTests : IDisposable
{
    private readonly string _root;

    public AutoWalkManagerSysGotoTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-walker-sysgoto-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    //   1/1(Home) — isolated (no land route onward)
    //   landing 1/10(Newhaven) ─N─ 1/11(Goal)
    private const string Rooms = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Home", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 10, "Name": "Newhaven", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/11", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 11, "Name": "Goal", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/10", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private sealed class Harness : IDisposable
    {
        public RoomTracker Tracker = null!;
        public MovementCoordinator Coordinator = null!;
        public AutoWalkManager Walker = null!;
        public readonly List<byte[]> Sent = new();
        public readonly List<WalkEvent> Events = new();
        public string? FiredGoto;
        public Action? PendingDeadline;
        public void FireDeadline() => PendingDeadline?.Invoke();
        public void Dispose() { }

        public sealed class FakeTimerHandle(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }

    private Harness NewHarness(IReadOnlyList<SysopGotoLocation> locations, int? level = 50)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), Rooms);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache, log: null);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        AutoWalkManager walker = new(graph, bfs, tracker, coord);

        Harness h = new() { Tracker = tracker, Coordinator = coord, Walker = walker };
        walker.SetSysGotoPlanner(
            new SysopGotoRoutePlanner(graph, bfs, () => locations, () => level),
            loc => { h.FiredGoto = loc.Name; });   // record the jump instead of hitting the wire
        walker.SetWireSender(b => h.Sent.Add(b));
        walker.Event += evt => h.Events.Add(evt);
        walker.SetVoyageScheduler((delay, cb) =>
        {
            h.PendingDeadline = cb;
            return new Harness.FakeTimerHandle(() =>
            {
                if (ReferenceEquals(h.PendingDeadline, cb)) h.PendingDeadline = null;
            });
        });
        tracker.StateChanged += _ => { };
        return h;
    }

    private static RoomObservation Obs(string name, params Direction[] exits)
        => new(name, new HashSet<Direction>(exits));

    private static SysopGotoLocation Loc(string name, int map, int room, int minLevel = 0) =>
        new() { Name = name, Map = map, Room = room, MinLevel = minLevel };

    [Fact]
    public void WalkTo_GoalReachableOnlyBySysGoto_FiresJump_ThenWalksLandingLeg()
    {
        Harness h = NewHarness(new[] { Loc("newhaven", 1, 10) });
        h.Tracker.SetLocated(new RoomKey(1, 1));

        Assert.True(h.Walker.WalkTo(new RoomKey(1, 11)));

        // Step 1: the SysGotoStep fired the jump (recorded, not on the wire), and no
        // move byte has gone out yet — we're awaiting the landing.
        Assert.Equal("newhaven", h.FiredGoto);
        Assert.Empty(h.Sent);
        Assert.Equal(WalkState.Walking, h.Walker.State);

        // Landing confirms (the manager's resync would SetLocated 1/10 in production;
        // here we simulate the render) → the land leg fires (1/10 → 1/11).
        h.Tracker.NoteRoomObserved(Obs("Newhaven", Direction.N));
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[^1]));

        // Goal confirms — walk finished.
        h.Tracker.NoteRoomObserved(Obs("Goal", Direction.S));
        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    [Fact]
    public void SysGoto_LandingNeverConfirms_FailsOutAtDeadline()
    {
        Harness h = NewHarness(new[] { Loc("newhaven", 1, 10) });
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 11));
        Assert.Equal("newhaven", h.FiredGoto);

        // The landing render never matches; the wall-clock backstop fires from a
        // room that isn't the landing → the jump fails out.
        h.FireDeadline();
        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    // Report stock-20261122-124831: the tracker kept the room left behind, and the loop
    // walked on from it.
    [Fact]
    public void AJumpToAnUnknownRoom_DropsTheOldRoom_UntilADisplayPlacesUs()
    {
        Harness h = NewHarness(new[] { Loc("newhaven", 1, 10) });
        h.Tracker.SetLocated(new RoomKey(1, 1));

        h.Tracker.NoteJumpedToUnknownRoom("sys goto landed elsewhere");
        Assert.Null(h.Tracker.State.CurrentRoom);

        h.Tracker.NoteRoomObserved(Obs("Goal", Direction.S));
        Assert.Equal(new RoomKey(1, 11), h.Tracker.State.CurrentRoom?.Key);
    }

    [Fact]
    public void WalkTo_NoUsableLocation_DoesNotJump()
    {
        // Power off / empty table → no jump planned, and with no land route the walk
        // can't proceed (never fires a goto).
        Harness h = NewHarness(Array.Empty<SysopGotoLocation>());
        h.Tracker.SetLocated(new RoomKey(1, 1));

        h.Walker.WalkTo(new RoomKey(1, 11));
        Assert.Null(h.FiredGoto);
    }
}
