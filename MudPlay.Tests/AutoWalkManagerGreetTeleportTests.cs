using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Walker-integration coverage for the NPC ask-transport ("greet teleport").
// Unlike an ordinary CMD teleport it can SILENTLY FAIL — a class-gated transport
// sits behind a `testskill` skill roll the client doesn't model (issue #455: the
// bard-only barmaid), and a failed roll leaves the character exactly where they
// were, sometimes with no fresh room render at all. So the step can't fire-and-
// trust: it verifies it actually landed in the destination and re-asks until it
// does. The fixture is a tavern whose only egress to the bard guild is the
// barmaid's ask-transport.
public sealed class AutoWalkManagerGreetTeleportTests : IDisposable
{
    private readonly string _root;

    public AutoWalkManagerGreetTeleportTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-walker-greet-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // 1/1(Home) ─N─ 1/2(Tavern, NPC 248 = the barmaid).  The bard guild (1/391) is
    // reachable ONLY by asking the barmaid — no cardinal leads there.
    private const string Rooms = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Home", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "NPC": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Tavern", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "NPC": 248,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 391, "Name": "Bard Guild", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "NPC": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "1/2", "D": "0" }
        ]
        """;

    private const string Monsters = """
        [
          { "Number": 248, "Name": "the barmaid", "GreetTXT": 40 }
        ]
        """;

    // greet 40 → keyword `adventure` → block 41: a class-12 (bard) only transport
    // gated behind a `testskill` roll, landing in 1/391. The class gate keeps the
    // edge routable only for bards; the testskill is NOT modelled — the walker
    // reacts to whether the ask actually moved it.
    private const string TbInfo = """
        [
          { "Number": 40, "LinkTo": 0, "Action": "adventure:41\n", "Called From": "Monster #248" },
          { "Number": 41, "LinkTo": 0,
            "Action": "class 12:testskill 5 50 999:teleport 391 1:message 1\n", "Called From": "" }
        ]
        """;

    private sealed class Harness : IDisposable
    {
        public required RoomTracker Tracker { get; init; }
        public required MovementCoordinator Coordinator { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public List<byte[]> Sent { get; } = new();
        public List<WalkEvent> Events { get; } = new();
        public string LastSent => Encoding.Latin1.GetString(Sent[^1]);
        public int AskCount => Sent.Count(b => Encoding.Latin1.GetString(b) == "ask barmaid adventure\r");

        // Fake scheduler shared by the greet-teleport re-ask watchdog (same
        // injection point the boat voyage uses) — captures the armed deadline so
        // the test drives the wall-clock backstop by hand.
        public Action? PendingDeadline;
        public void FireDeadline() => PendingDeadline?.Invoke();

        public void Dispose() { }

        public sealed class FakeTimerHandle(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }

    private Harness NewHarness()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), Rooms);
        File.WriteAllText(Path.Combine(_root, "alpha", "Monsters.json"), Monsters);
        File.WriteAllText(Path.Combine(_root, "alpha", "TBInfo.json"), TbInfo);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        TBInfoStore store = new(cache);
        store.OnActiveSetChanged("alpha");
        RoomGraphManager graph = new(cache, log: null, tbinfo: store);
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        AutoWalkManager walker = new(graph, bfs, tracker, coord);

        Harness h = new() { Tracker = tracker, Coordinator = coord, Walker = walker };
        walker.SetWireSender(b => h.Sent.Add(b));
        walker.Event += evt => h.Events.Add(evt);
        walker.SetVoyageScheduler((_, cb) =>
        {
            h.PendingDeadline = cb;
            return new Harness.FakeTimerHandle(() =>
            {
                if (ReferenceEquals(h.PendingDeadline, cb)) h.PendingDeadline = null;
            });
        });
        tracker.StateChanged += _ => { };   // ensure the event has a live invocation list
        return h;
    }

    private static RoomObservation Obs(string name, params Direction[] exits)
        => new(name, new HashSet<Direction>(exits));

    // Walk to the tavern, then send the ask-transport, so both retry tests start
    // from the same point: the barmaid asked, watchdog armed, awaiting arrival.
    private static void WalkToBarmaidAndAsk(Harness h)
    {
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 391)));

        // Land leg to the tavern.
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));
        h.Tracker.NoteRoomObserved(Obs("Tavern", Direction.S));

        // The ask-transport is on the wire; the walker is now verifying the landing.
        Assert.Equal("ask barmaid adventure\r", h.LastSent);
        Assert.Equal(1, h.AskCount);
        Assert.Equal(WalkState.Walking, h.Walker.State);
    }

    [Fact]
    public void GreetTeleport_LandsFirstTry_CompletesWithoutReAsking()
    {
        Harness h = NewHarness();
        WalkToBarmaidAndAsk(h);

        // The transport worked — the bard guild renders, so the walk finishes with
        // no re-ask.
        h.Tracker.NoteRoomObserved(Obs("Bard Guild", Direction.U));

        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Equal(1, h.AskCount);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    [Fact]
    public void GreetTeleport_SilentRollFail_ReAsksViaWatchdog_ThenLands()
    {
        Harness h = NewHarness();
        WalkToBarmaidAndAsk(h);

        // The skill roll failed and left us put WITHOUT re-rendering the room, so
        // no tracker transition ever comes — the walker would hang forever if it
        // only reacted to observations. The watchdog fires and re-asks.
        h.FireDeadline();
        Assert.Equal("ask barmaid adventure\r", h.LastSent);
        Assert.Equal(2, h.AskCount);
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);

        // A second roll also fails silently — keeps re-asking, doesn't give up.
        h.FireDeadline();
        Assert.Equal(3, h.AskCount);

        // The roll finally passes and the bard guild renders — walk completes.
        h.Tracker.NoteRoomObserved(Obs("Bard Guild", Direction.U));
        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    [Fact]
    public void GreetTeleport_SourceRedisplayWhileWaiting_DoesNotDerailWalk_ThenWatchdogReAsks()
    {
        Harness h = NewHarness();
        WalkToBarmaidAndAsk(h);

        // The failed roll re-rendered the SAME source room. The tracker treats that
        // as a passive re-look (a move is still pending), so it neither advances nor
        // fails — the walker must stay put, still awaiting the transport, WITHOUT
        // mistaking the redisplay for a blocked-exit walk failure.
        h.Tracker.NoteRoomObserved(Obs("Tavern", Direction.S));
        Assert.Equal(WalkState.Walking, h.Walker.State);
        Assert.Equal(1, h.AskCount);                                 // no premature re-ask
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);

        // The watchdog is what recognises the stall and re-asks.
        h.FireDeadline();
        Assert.Equal(2, h.AskCount);

        // Next attempt lands.
        h.Tracker.NoteRoomObserved(Obs("Bard Guild", Direction.U));
        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
    }

    [Fact]
    public void GreetTeleport_DeadlineAfterLanding_IsHarmlessNoOp()
    {
        Harness h = NewHarness();
        WalkToBarmaidAndAsk(h);

        // Landing completes the step and cancels the watchdog; a stray deadline
        // fire afterward must not re-ask or re-fail.
        h.Tracker.NoteRoomObserved(Obs("Bard Guild", Direction.U));
        Assert.Equal(WalkState.Idle, h.Walker.State);

        h.FireDeadline();   // cancelled — no-op
        Assert.Equal(1, h.AskCount);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
    }
}
