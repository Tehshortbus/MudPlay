using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Walker-integration coverage for boat travel: when a destination is only (or
// more cheaply) reachable by a sea-captain sailing, the walker plans a
// BoatRoutePlan, walks the land leg to the dock, sends the passage keyword as a
// single BoatStep, waits for the arrival port to confirm (tolerating transit
// churn), then walks the land leg from the port. The fixture is a mainland dock
// whose sailing lands on an otherwise-unreachable island — so the only route to
// the island town is the boat.
public sealed class AutoWalkManagerBoatTests : IDisposable
{
    private readonly string _root;

    public AutoWalkManagerBoatTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-walker-boat-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // Mainland: 1/1(Home) ─N─ 1/2(Pier, CMD 100).  The pier's sailing lands on
    // an island the mainland can't reach on foot: arrival 1/10 ─N─ 1/11(town).
    private const string Rooms = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Home", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Pier", "CMD": 100,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 10, "Name": "Isle Port", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/11", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 11, "Name": "Isle Town", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/10", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private const string TbInfo = """
        [
          { "Number": 100, "LinkTo": 0,
            "Action": "secure passage to islea:minlevel 1 0:price 100 0:random 200:text 0\n",
            "Called From": "Room 1/2" },
          { "Number": 200, "LinkTo": 0, "Action": "100:cast 600\n", "Called From": "rndm" }
        ]
        """;

    // A trip leg (Dur 20 spell rounds) that EndCasts to an instantaneous disembark
    // (Abil 140 → room 10) — so the voyage's summed transit duration is 20 rounds,
    // which the walker times as 20*3 + 3-buffer = 63 wall-clock seconds.
    private const string Spells = """
        [
          { "Number": 600, "Name": "isle trip", "MinBase": 0, "MaxBase": 0, "Dur": 20,
            "Abil-0": 151, "AbilVal-0": 601 },
          { "Number": 601, "Name": "disembark isle a", "MinBase": 0, "MaxBase": 0,
            "Abil-0": 140, "AbilVal-0": 10 }
        ]
        """;

    private sealed class Harness : IDisposable
    {
        public required RoomTracker Tracker { get; init; }
        public required MovementCoordinator Coordinator { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public List<byte[]> Sent { get; } = new();
        public List<WalkEvent> Events { get; } = new();
        public string Wire => string.Concat(Sent.Select(b => Encoding.Latin1.GetString(b)));

        // Fake voyage scheduler: captures the armed deadline + its delay so the
        // test drives the wall-clock backstop by hand (FireBoatDeadline) instead
        // of waiting real seconds. A cancelled timer (walk reset / early arrival)
        // drops the pending callback so a later fire is a no-op.
        public Action? PendingDeadline;
        public TimeSpan LastVoyageDelay;
        public void FireBoatDeadline() => PendingDeadline?.Invoke();

        public void Dispose() { }

        public sealed class FakeTimerHandle(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }

    private Harness NewHarness(bool leaderWithFollowers = false)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), Rooms);
        File.WriteAllText(Path.Combine(_root, "alpha", "TBInfo.json"), TbInfo);
        File.WriteAllText(Path.Combine(_root, "alpha", "Spells.json"), Spells);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        TBInfoStore store = new(cache);
        store.OnActiveSetChanged("alpha");
        KnownSpellCatalog catalog = new(cache);
        RoomGraphManager graph = new(cache, log: null, tbinfo: store, spellCatalog: catalog);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        AutoWalkManager walker = new(graph, bfs, tracker, coord);
        walker.SetBoatPlanner(new BoatRoutePlanner(graph, bfs));

        Harness h = new() { Tracker = tracker, Coordinator = coord, Walker = walker };
        walker.SetWireSender(b => h.Sent.Add(b));
        walker.Event += evt => h.Events.Add(evt);
        walker.SetVoyageScheduler((delay, cb) =>
        {
            h.LastVoyageDelay = delay;
            h.PendingDeadline = cb;
            return new Harness.FakeTimerHandle(() =>
            {
                if (ReferenceEquals(h.PendingDeadline, cb)) h.PendingDeadline = null;
            });
        });
        tracker.StateChanged += _ => { };   // ensure the event has a live invocation list

        if (leaderWithFollowers)
        {
            walker.SetPartyLeaderCheck(() => true);
            walker.SetPartySplitHandler(() => h.Sent.Add(Encoding.Latin1.GetBytes("<reform>")));
        }
        return h;
    }

    private static RoomObservation Obs(string name, params Direction[] exits)
        => new(name, new HashSet<Direction>(exits));

    [Fact]
    public void WalkTo_IslandReachableOnlyByBoat_WalksToDock_ThenSailsThenLands()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));

        // Only route to the island town is the pier's sailing.
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 11)));

        // Step 1: land leg to the dock (1/1 → 1/2).
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));
        h.Tracker.NoteRoomObserved(Obs("Pier", Direction.S));

        // Step 2: the BoatStep put the passage keyword on the wire.
        Assert.Equal("secure passage to islea\r", Encoding.Latin1.GetString(h.Sent[1]));
        Assert.Equal(WalkState.Walking, h.Walker.State);

        // Arrival port confirms — voyage completes, next land leg fires (1/10 → 1/11).
        h.Tracker.NoteRoomObserved(Obs("Isle Port", Direction.N));
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[2]));

        // Island town confirms — walk finished.
        h.Tracker.NoteRoomObserved(Obs("Isle Town", Direction.S));
        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    [Fact]
    public void BoatVoyage_TransitChurnBeforeArrival_KeepsWaiting_ThenCompletes()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 11));
        h.Tracker.NoteRoomObserved(Obs("Pier", Direction.S));   // at dock; boat sent

        // A buff-locked transit room the graph doesn't know — the tracker churns
        // to Suspect. The voyage must keep waiting, not fail or re-send.
        int before = h.Sent.Count;
        h.Tracker.NoteRoomObserved(Obs("Open Sea", Direction.N, Direction.S, Direction.E, Direction.W));
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Equal(before, h.Sent.Count);                     // nothing re-sent mid-transit
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);

        // Now the port renders — arrival completes and the land leg fires.
        h.Tracker.NoteRoomObserved(Obs("Isle Port", Direction.N));
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[^1]));
        h.Tracker.NoteRoomObserved(Obs("Isle Town", Direction.S));
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
    }

    [Fact]
    public void BoatVoyage_ArrivalNeverMatches_FailsOutAtDeadline()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 11));
        h.Tracker.NoteRoomObserved(Obs("Pier", Direction.S));   // at dock; boat sent

        // Transit churn never matches the port — the voyage keeps waiting, no
        // fail. Then the wall-clock backstop fires from where we're still NOT at
        // the arrival room, so the deadline fails the voyage out (captain refused
        // boarding, or arrival mismatch) — the event-driven walker's only backstop
        // against a port that never comes.
        h.Tracker.NoteRoomObserved(Obs("Home", Direction.N));
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);

        h.FireBoatDeadline();

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    [Fact]
    public void BoatStep_Sizes_VoyageDelay_FromTransitRounds_And_ExposesSailingState()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 11));
        h.Tracker.NoteRoomObserved(Obs("Pier", Direction.S));   // at dock; boat sent

        // 20 transit rounds * 3s + 3s buffer = 63 wall-clock seconds.
        Assert.Equal(TimeSpan.FromSeconds(63), h.LastVoyageDelay);

        // Mid-sail state feeds the nav "Sailing the high seas…" countdown label.
        Assert.True(h.Walker.IsSailing);
        Assert.Equal("islea", h.Walker.SailingDestinationName);
        Assert.True(h.Walker.SailingArrivalEta > DateTimeOffset.UtcNow);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Sailing);

        // Landing clears the sailing state so the label reverts to walk status.
        h.Tracker.NoteRoomObserved(Obs("Isle Port", Direction.N));
        Assert.False(h.Walker.IsSailing);
        Assert.Null(h.Walker.SailingDestinationName);
    }

    [Fact]
    public void BoatStep_AsLeaderWithFollowers_RelaysPartySplit()
    {
        Harness h = NewHarness(leaderWithFollowers: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Walker.WalkTo(new RoomKey(1, 11));
        h.Tracker.NoteRoomObserved(Obs("Pier", Direction.S));   // at dock; boat sent

        // Leader path: `.@party <keyword>` relay, then the keyword itself, then
        // the reform handler fires (recorded as "<reform>" on the wire capture).
        Assert.Contains(".@party secure passage to islea\r", h.Wire);
        Assert.Contains("secure passage to islea\r", h.Wire);
        Assert.Contains("<reform>", h.Wire);
    }

    [Fact]
    public void WalkTo_DestinationOnPier_DoesNotBoat_WhenLandRouteWins()
    {
        // Walking 1/1 → 1/2 (the pier itself) is one land hop; the boat route to
        // 1/2 doesn't exist (the pier is a dock, not an arrival), so the walker
        // must take the plain land step and never emit the passage keyword.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));

        Assert.True(h.Walker.WalkTo(new RoomKey(1, 2)));
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));
        Assert.DoesNotContain("secure passage", h.Wire);
    }

    // The party's purses are checked at the dock before the verb goes out
    // (PartyTollGate.BeforeSailing): held under its gate, the verb waits, and goes
    // when the check lets the sailing through.
    [Fact]
    public void BoatStep_WaitsForThePartysCheck_AndSailsWhenItIsLetThrough()
    {
        Harness h = NewHarness();
        bool held = true;
        List<long> faresAsked = new();
        h.Walker.SetSailingCheck((passage, _) =>
        {
            faresAsked.Add(passage.FareCopper);
            if (!held) return PartyTollGate.StepVerdict.Go;
            h.Coordinator.AssertGate(MovementCoordinator.PartyTollGate, "test", "asking the party's purses");
            return PartyTollGate.StepVerdict.Hold;
        });
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 11)));
        h.Tracker.NoteRoomObserved(Obs("Pier", Direction.S));

        Assert.Equal(new long[] { 100 }, faresAsked);
        Assert.DoesNotContain("secure passage", h.Wire);

        held = false;
        h.Coordinator.ClearGate(MovementCoordinator.PartyTollGate, "test", "the party's purses are read");

        Assert.EndsWith("secure passage to islea\r", h.Wire);
        Assert.Equal(WalkState.Walking, h.Walker.State);
    }

    // A sailing the party can't all pay is not boarded: the verb never goes out,
    // and with no other way the walk ends naming the sailing.
    [Fact]
    public void BoatStep_ClosedToTheParty_IsNeverBoarded_AndTheWalkEndsNamingTheSailing()
    {
        Harness h = NewHarness();
        h.Walker.SetSailingCheck((_, _) => PartyTollGate.StepVerdict.Closed);
        List<string> notices = new();
        h.Walker.SetUnpaidCrossingHandler((_, reason) => notices.Add(reason));
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 11)));
        h.Tracker.NoteRoomObserved(Obs("Pier", Direction.S));

        Assert.DoesNotContain("secure passage", h.Wire);
        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains("the boat 'secure passage to islea'", Assert.Single(notices));
        Assert.EndsWith("the party can't all pay it", notices[0]);
    }
}
