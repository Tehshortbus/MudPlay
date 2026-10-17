using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The walk a route card starts is the route the card showed, for the whole trip. The
// walker plans one leg at a time from what it is told, so these pin what a journey
// keeps across its legs (a re-plan, a detour to fetch an item, a restart after an
// errand): which gates the route went round (report paradigm-20261008-173911) and
// how it takes teleports (report paradigm-20261008-174236).
public sealed class AutoWalkManagerPickedRouteTests : IDisposable
{
    private readonly string _root;

    public AutoWalkManagerPickedRouteTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-walker-pickedroute-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // The talisman exit (815) is one hop from Hills to Lake; the way round is three.
    // Past the lake both cross the orb gate (807) to Dest, and the Giver stands
    // beyond it.
    //   1/1 Hills ─E(815)─ 1/4 Lake ─E(807)─ 1/9 Dest ─E─ 1/10 Giver
    //     └N─ 1/2 ─N─ 1/3 ─E─┘
    private const string GatedRooms = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Hills", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/4 (Item: 815)", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Tunnel", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Tunnel End", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "1/4", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Lake", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9 (Item: 807)", "W": "1/3", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Dest", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/10", "W": "1/4", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 10, "Name": "Giver", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/9", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // The same, with a door as the first step of the way round and that way one
    // room shorter: 1/1 ─N(Door)─ 1/2 ─E─ 1/4.
    private const string GatedRoomsWithDoor = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Hills", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2 (Door)", "S": "0", "E": "1/4 (Item: 815)", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Tunnel", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1 (Door)", "E": "1/4", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Lake", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9 (Item: 807)", "W": "1/2", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Dest", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/10", "W": "1/4", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 10, "Name": "Giver", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/9", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Three steps north on foot, or one teleport hop, from Grove to the Stone Arch;
    // the Shop is a room off the trail, three steps on foot from the Grove by way of
    // the Trail, or two by the teleport and back down from the Arch.
    //   1/10 Grove ─N─ 1/11 Trail ─N─ 1/12 Ridge ─N─ 7/131 Stone Arch ─E─ 1/20 Shop
    //     └─ SW (CMD 100 teleport) ──────────────────────┘        1/12 Ridge ─E─ 1/20
    private const string TeleportRooms = """
        [
          { "Map Number": 1, "Room Number": 10, "Name": "Grove", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "CMD": 100,
            "N": "1/11", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "7/131 (Item: 474)", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 11, "Name": "Trail", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/12", "S": "1/10", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 12, "Name": "Ridge", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "7/131", "S": "1/11", "E": "1/20", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 7, "Room Number": 131, "Name": "Stone Arch", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/12", "E": "1/20", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 20, "Name": "Shop", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/12", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Two ways from Start to Dest, each through a hazard room (spell 700) the
    // crosser holds no counter for: the River, two steps, and the Swamp, three by way
    // of the Camp. From the Camp it is the other way about: the Swamp is two steps,
    // the River three.
    //   1/1 Start ─E─ 1/2 River ─E─ 1/9 Dest
    //     └S─ 1/5 Camp ─E─ 1/6 Swamp ─N─┘
    private const string HazardRooms = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "Spell": 0, "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/5", "E": "1/2", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "River", "Spell": 700, "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/1", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Camp", "Spell": 0, "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/1", "S": "0", "E": "1/6", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 6, "Name": "Swamp", "Spell": 700, "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/9", "S": "0", "E": "0", "W": "1/5", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Dest", "Spell": 0, "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/6", "E": "0", "W": "1/2", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private static readonly RoomKey Start = new(1, 1), River = new(1, 2), Camp = new(1, 5), Swamp = new(1, 6);
    private static readonly RoomKey Hills = new(1, 1), Lake = new(1, 4), Dest = new(1, 9), Giver = new(1, 10);
    private static readonly RoomKey Grove = new(1, 10), Trail = new(1, 11), Arch = new(7, 131), Shop = new(1, 20);
    private static readonly int[] Talisman = { 815 };

    private sealed class Harness
    {
        public required BfsMapper Bfs { get; init; }
        public required RoomGraphManager Graph { get; init; }
        public required GameDataCache Cache { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required MovementFilter Filter { get; init; }
        public required MovementCoordinator Coordinator { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public List<string> Sent { get; } = new();
        public List<WalkEvent> Events { get; } = new();

        // The walk planned last, as "3 step(s)" or "0 move(s), 1 action(s)".
        public string LastPlan => Events.Last(e => e.Kind == WalkEventKind.Started).Detail;
        public int Plans => Events.Count(e => e.Kind == WalkEventKind.Started);
    }

    // paused: the walker plans and waits, so a test reads the plan and lets it go
    // with Play. carriesEverything: the teleport fixture's exit is an item-use one,
    // shut to a crosser without its item, and those tests aren't about item gates.
    private Harness NewHarness(string rooms, RoomKey at, bool carriesEverything = false, bool paused = true)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), rooms);
        File.WriteAllText(Path.Combine(_root, "alpha", "Lairs.json"), "[]");
        // Spell 700 is a room hazard that item 42 counters.
        File.WriteAllText(Path.Combine(_root, "alpha", "Spells.json"), """[ { "Number": 700, "Abil-0": 1, "AbilVal-0": 25 } ]""");
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), """[ { "Number": 42, "NegateSpell-0": 700 } ]""");
        File.WriteAllText(Path.Combine(_root, "alpha", "TBInfo.json"),
            """[ { "Number": 100, "Action": "go arch:teleport 131 7\n" } ]""");
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        TBInfoStore tbinfo = new(cache);
        tbinfo.OnActiveSetChanged("alpha");
        RoomGraphManager graph = new(cache, log: null, tbinfo);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        BfsMapper bfs = new(graph);
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        MovementFilter filter = new(profile)
        {
            InventoryReadyProbe = () => true,
            ItemCarriedProbe = _ => carriesEverything,
        };
        RoomHazardIndex hazards = new(cache);
        hazards.OnActiveSetChanged("alpha");
        filter.Hazards = hazards;
        filter.RoomEntrySpellProbe = key => graph.GetRoom(key)?.Spell ?? 0;
        AutoWalkManager walker = new(graph, bfs, tracker, coordinator, filter);
        Harness h = new()
        {
            Bfs = bfs, Graph = graph, Cache = cache, Tracker = tracker, Filter = filter,
            Coordinator = coordinator, Walker = walker,
        };
        walker.SetWireSender(b => h.Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        walker.SetTeleportResolver((_, _) => "go arch");
        walker.Event += h.Events.Add;
        tracker.SetLocated(at);
        if (paused) coordinator.AssertGate(MovementCoordinator.UserGate);
        return h;
    }

    private static void Play(Harness h) => h.Coordinator.ClearGate(MovementCoordinator.UserGate);

    private static bool CardWalk(Harness h, RoomKey to) =>
        h.Walker.WalkTo(to, planThroughAcquirableGates: true, pickedRoute: true, keepGatesClosedFor: Talisman);

    // ----- the gates a picked route goes round ------------------------------

    [Fact]
    public void GatedPick_WalksTheRouteItsCardShowed_NotTheShortcutItWentRound()
    {
        Harness h = NewHarness(GatedRooms, Hills);
        RouteChoice card = RouteChoicePlanner.Evaluate(h.Bfs, h.Filter, h.Graph, Hills, Dest)!;
        Assert.Equal(4, card.GatedStepCount);                     // round the talisman exit
        Assert.Equal(Talisman, card.ClosedGateItems);

        h.Walker.WalkTo(Dest, planThroughAcquirableGates: true, pickedRoute: true,
            keepGatesClosedFor: card.ClosedGateItems);

        Assert.Equal("4 step(s)", h.LastPlan);
        Play(h);
        Assert.Equal(new[] { "n" }, h.Sent);
    }

    // What the walker did before it was told: every gate open, so straight at the
    // talisman exit, which the crosser can't pass.
    [Fact]
    public void GatedWalk_ToldNothing_TakesTheShortcut()
    {
        Harness h = NewHarness(GatedRooms, Hills);

        h.Walker.WalkTo(Dest, planThroughAcquirableGates: true, pickedRoute: true);

        Assert.Equal("2 step(s)", h.LastPlan);
    }

    // The first report's own path: the step is refused, the retry too, and the walk
    // "hands off to replan". The re-plan is the walk's own and keeps its route.
    [Fact]
    public void AReplanInPlace_KeepsTheClosedGates()
    {
        Harness h = NewHarness(GatedRooms, Hills, paused: false);
        CardWalk(h, Dest);
        Assert.Equal(new[] { "n" }, h.Sent);

        h.Tracker.NoteMoveBlocked();   // the one retry a step gets
        h.Tracker.NoteMoveBlocked();   // out of retries: re-plan from here

        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Equal(2, h.Plans);
        Assert.Equal("4 step(s)", h.LastPlan);
        Assert.Equal(Talisman, h.Walker.Journey?.ClosedGates);
    }

    // Started while a move is still on the wire, the walk plans only once the room
    // settles. What it was told has to be there when it does.
    [Fact]
    public void ADeferredStart_PlansWithTheClosedGates()
    {
        Harness h = NewHarness(GatedRooms, Hills);
        h.Tracker.NoteMoveSent(Direction.N);

        CardWalk(h, Dest);
        Assert.Contains("deferred", h.LastPlan);

        h.Tracker.NoteMoveBlocked();   // the move never happened; still in the Hills

        Assert.Equal("4 step(s)", h.LastPlan);
    }

    // A detour leg is a walk of its own that states nothing. It plans through gates
    // while an item is still owed, and must still go round the ones the route did.
    [Fact]
    public void DetourLeg_KeepsTheJourneysClosedGates()
    {
        Harness h = NewHarness(GatedRooms, Hills);
        CardWalk(h, Dest);

        h.Walker.WalkTo(Giver, planThroughAcquirableGates: true, supersedeSilently: true);

        Assert.Equal("5 step(s)", h.LastPlan);                    // not 3, through the talisman exit
        Assert.Equal(Talisman, h.Walker.Journey?.ClosedGates);
        Assert.False(h.Walker.LegIsToJourneyGoal);
    }

    [Fact]
    public void ANewWalk_StartsWithoutTheLastJourneysClosedGates()
    {
        Harness h = NewHarness(GatedRooms, Hills);
        CardWalk(h, Dest);

        h.Walker.WalkTo(Giver, planThroughAcquirableGates: true);

        Assert.Equal("3 step(s)", h.LastPlan);
        Assert.Null(h.Walker.Journey?.ClosedGates);
        Assert.Equal(Giver, h.Walker.Journey?.Destination);
    }

    // ----- a reset inside the walker never ends the journey -----------------

    // A door, hidden-exit or winch manager told to stop answers its caller on the
    // spot. That answer used to come back into the walker as the walk failing, in
    // the middle of the redirect that had asked for the stop: a Failed raised for
    // the journey's destination, which ended the journey, so the leg being planned
    // went through every gate.
    [Fact]
    public void ASilentRedirectDuringADoorAttempt_RaisesNothing_AndKeepsTheJourney()
    {
        Harness h = NewHarness(GatedRoomsWithDoor, Hills, paused: false);
        List<Action<DoorOpenResult>> pending = new();
        h.Walker.SetDoorEnqueuer((_, _, _, _, _, reply) => pending.Add(reply));
        h.Walker.SetDoorStopper(() =>
        {
            foreach (Action<DoorOpenResult> reply in pending.ToArray())
                reply(new DoorOpenResult.Failed("door flow stopped"));
            pending.Clear();
        });
        CardWalk(h, Dest);
        Assert.Equal("3 step(s)", h.LastPlan);                    // N through the door, E, E
        Assert.Single(pending);

        h.Walker.WalkTo(Giver, planThroughAcquirableGates: true, supersedeSilently: true);

        Assert.DoesNotContain(h.Events, e => e.Kind is WalkEventKind.Failed or WalkEventKind.Stopped);
        Assert.Equal(Talisman, h.Walker.Journey?.ClosedGates);
        Assert.Equal("4 step(s)", h.LastPlan);                    // still round the talisman exit
    }

    // A stop the user asks for still stops the door attempt, and says Stopped once.
    [Fact]
    public void AStopDuringADoorAttempt_IsOneStopped_NotAFailure()
    {
        Harness h = NewHarness(GatedRoomsWithDoor, Hills, paused: false);
        Action<DoorOpenResult>? pending = null;
        int stops = 0;
        h.Walker.SetDoorEnqueuer((_, _, _, _, _, reply) => pending = reply);
        h.Walker.SetDoorStopper(() => { stops++; pending?.Invoke(new DoorOpenResult.Failed("door flow stopped")); });
        CardWalk(h, Dest);

        h.Walker.Stop();

        Assert.Equal(1, stops);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Single(h.Events, e => e.Kind == WalkEventKind.Stopped);
        Assert.Null(h.Walker.Journey);
    }

    // ----- where a journey ends ---------------------------------------------

    [Fact]
    public void TheJourneyEnds_WhenALegToItsDestinationArrives()
    {
        Harness h = NewHarness(TeleportRooms, Trail, carriesEverything: true, paused: false);
        h.Walker.WalkTo(new RoomKey(1, 12), preferTeleportFree: true);
        Assert.NotNull(h.Walker.Journey);
        Assert.True(h.Walker.LegIsToJourneyGoal);

        h.Tracker.NoteRoomObserved(new RoomObservation("Ridge",
            new HashSet<Direction> { Direction.N, Direction.S, Direction.E }));

        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
        Assert.Null(h.Walker.Journey);
    }

    [Fact]
    public void TheJourneyEnds_WhenALegToItsDestinationFails()
    {
        Harness h = NewHarness(GatedRooms, Hills);

        h.Walker.WalkTo(Dest, preferTeleportFree: true);          // gates live: no route

        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Null(h.Walker.Journey);
    }

    // The leg out to the giver ends there (or fails), and the journey goes on.
    [Fact]
    public void ASideTripArrivingOrFailing_LeavesTheJourneyStanding()
    {
        Harness h = NewHarness(TeleportRooms, Trail, carriesEverything: true);
        h.Walker.WalkTo(Arch, preferTeleportFree: false);

        h.Walker.WalkTo(Trail, supersedeSilently: true);          // already there: finishes at once
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
        Assert.Equal(Arch, h.Walker.Journey?.Destination);

        h.Walker.WalkTo(new RoomKey(9, 999), supersedeSilently: true);   // no such room: fails
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Equal(Arch, h.Walker.Journey?.Destination);
    }

    // A walk to a stop-before boss room goes to the room one short of it, and that
    // is the room a detour router hands back as "where the walk was going". A leg
    // there is a leg to the journey's destination: it takes the journey's rules, and
    // its arrival is the journey's end.
    [Fact]
    public void ALegToTheRoomShortOfAStopBeforeDestination_IsALegToTheDestination()
    {
        Harness h = NewHarness(TeleportRooms, Trail, carriesEverything: true);
        RoomKey ridge = new(1, 12);
        h.Walker.SetBossStopRooms(() => new HashSet<RoomKey> { Arch });
        h.Walker.WalkTo(Arch, preferTeleportFree: true);
        Assert.Equal(ridge, h.Walker.Destination);
        Assert.Equal(Arch, h.Walker.Journey?.Destination);

        h.Walker.WalkTo(ridge, supersedeSilently: true);
        Assert.True(h.Walker.LegIsToJourneyGoal);

        Play(h);
        h.Tracker.NoteRoomObserved(new RoomObservation("Ridge",
            new HashSet<Direction> { Direction.N, Direction.S, Direction.E }));

        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
        Assert.Null(h.Walker.Journey);
    }

    // Idle between two legs (at the giver, waiting on the hand-over) there is no walk
    // for a Stop to stop, and the journey still has to end with it.
    [Fact]
    public void StopWhileIdleBetweenLegs_EndsTheJourney()
    {
        Harness h = NewHarness(TeleportRooms, Trail, carriesEverything: true);
        h.Walker.WalkTo(Arch, preferTeleportFree: false);
        h.Walker.WalkTo(Trail, supersedeSilently: true);
        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.NotNull(h.Walker.Journey);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Stopped);

        h.Walker.Stop("user stop");

        Assert.Null(h.Walker.Journey);
        // Said out loud, for the coordinator waiting to issue the next leg: an idle
        // walker raises nothing on its own, and the leg went out after the Stop.
        WalkEvent stopped = Assert.Single(h.Events, e => e.Kind == WalkEventKind.Stopped);
        Assert.Equal(Arch, stopped.Destination);
        Assert.Equal("user stop", stopped.Detail);
    }

    // With no journey standing there is nothing to stop and nothing is said.
    [Fact]
    public void StopWhileIdleWithNoJourney_RaisesNothing()
    {
        Harness h = NewHarness(TeleportRooms, Trail, carriesEverything: true);

        h.Walker.Stop();

        Assert.Empty(h.Events);
    }

    // A silent walk with no journey standing is nobody's leg: it is its own journey,
    // and a report captured during it says so.
    [Fact]
    public void ASilentWalkWithNoJourneyStanding_IsAJourneyOfItsOwn()
    {
        Harness h = NewHarness(TeleportRooms, Grove, carriesEverything: true);

        h.Walker.WalkTo(Arch, supersedeSilently: true, preferTeleportFree: true);

        Assert.Equal(new WalkJourney(Arch, PreferTeleportFree: true), h.Walker.Journey);
        Assert.True(h.Walker.LegIsToJourneyGoal);
    }

    // ----- teleports on the legs of a walk the user started -----------------

    private static IReadOnlySet<(RoomKey From, RoomKey To)> NoTeleports() => new HashSet<(RoomKey, RoomKey)>();

    // The user picked a route that teleports. A detour router takes the walk out to
    // a giver and back; the leg back states no preference, and used to be planned
    // by the automatic-walk setting as if the client had started it.
    [Fact]
    public void TheLegOnToTheDestination_TakesTeleportsAsTheJourneyDoes()
    {
        Harness h = NewHarness(TeleportRooms, Grove, carriesEverything: true);
        h.Walker.SetAutomaticWalkTeleports(NoTeleports);
        h.Walker.WalkTo(Arch, preferTeleportFree: false);

        h.Walker.WalkTo(Arch, supersedeSilently: true);
        Play(h);

        Assert.Equal(new[] { "go arch" }, h.Sent);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    // A side trip of that journey is the user's walk (the automatic-walk list
    // doesn't apply), but no card showed its route: it goes on foot when it can.
    // The teleport the card named was agreed for the journey's own route.
    [Fact]
    public void ASideTripOfAJourneyThatTeleports_GoesOnFootWhenItCan()
    {
        Harness h = NewHarness(TeleportRooms, Grove, carriesEverything: true);
        h.Walker.WalkTo(Arch, preferTeleportFree: false);

        h.Walker.WalkTo(Shop, supersedeSilently: true);

        Assert.Equal("3 step(s)", h.LastPlan);                    // N, N, E; not the teleport and E
        Play(h);
        Assert.Equal(new[] { "n" }, h.Sent);
    }

    // On foot when it can, not on foot or nothing: with no walking route the side
    // trip takes the teleport, whatever the automatic-walk list says.
    [Fact]
    public void ASideTripWithNoWayOnFoot_TakesTheTeleport()
    {
        Harness h = NewHarness(TeleportRooms, Grove, carriesEverything: true);
        h.Walker.SetAutomaticWalkTeleports(NoTeleports);
        h.Filter.MarkAvoided(Trail);
        h.Walker.WalkTo(Arch, preferTeleportFree: false);

        h.Walker.WalkTo(Shop, supersedeSilently: true);
        Play(h);

        Assert.Equal(new[] { "go arch" }, h.Sent);
    }

    // A walk the client started on its own has no such preference to hand on, so its
    // detour legs follow the setting as before.
    [Fact]
    public void DetourLegOfAnAutomaticWalk_StillFollowsTheSetting()
    {
        Harness h = NewHarness(TeleportRooms, Grove, carriesEverything: true);
        h.Walker.SetAutomaticWalkTeleports(NoTeleports);
        h.Walker.WalkTo(Arch);

        h.Walker.WalkTo(Arch, supersedeSilently: true);
        Play(h);

        Assert.Equal(new[] { "n" }, h.Sent);
    }

    // Stopped, the journey is over: a later silent walk is nobody's detour leg.
    [Fact]
    public void AStoppedJourney_HandsNothingOn()
    {
        Harness h = NewHarness(TeleportRooms, Grove, carriesEverything: true);
        h.Walker.SetAutomaticWalkTeleports(NoTeleports);
        h.Walker.WalkTo(Arch, preferTeleportFree: false);
        h.Walker.Stop();

        h.Walker.WalkTo(Arch, supersedeSilently: true);
        Play(h);

        Assert.Equal(new[] { "n" }, h.Sent);
        Assert.Null(h.Walker.Journey?.PreferTeleportFree);
    }

    // ----- journeys nobody's walk begins, and journeys picked back up --------

    // A Shortcut card picked without the item sends the walker to the item's source
    // through the detour walk, which starts no journey. Declared, it is the user's
    // trip: off the automatic-walk list, and it replaces the journey of the card walk
    // it supersedes.
    [Fact]
    public void ADeclaredJourney_ReplacesTheOneStanding_AndItsSilentLegsFollowIt()
    {
        Harness h = NewHarness(TeleportRooms, Grove, carriesEverything: true);
        h.Walker.SetAutomaticWalkTeleports(NoTeleports);
        h.Walker.WalkTo(Shop, planThroughAcquirableGates: true, pickedRoute: true, keepGatesClosedFor: Talisman);

        h.Walker.BeginJourney(new WalkJourney(Arch, PreferTeleportFree: false));
        Assert.Equal(Arch, h.Walker.Journey?.Destination);
        Assert.Null(h.Walker.Journey?.ClosedGates);

        h.Walker.WalkTo(Arch, supersedeSilently: true);
        Play(h);

        Assert.True(h.Walker.LegIsToJourneyGoal);
        Assert.Equal(new[] { "go arch" }, h.Sent);
    }

    // An errand (a sell detour, a flee) stops the walk and starts it again. Started
    // again from a bare destination it had forgotten its route.
    [Fact]
    public void AnErrandRestart_PicksTheJourneyBackUp_WithItsRoute()
    {
        Harness h = NewHarness(GatedRooms, Hills);
        LoopRunner loops = new(h.Tracker, h.Coordinator, graph: h.Graph, bfs: h.Bfs, walker: h.Walker);
        AutoLairManager lair = new(h.Walker, h.Tracker, h.Graph, h.Bfs, new LairTimerStore(h.Cache, h.Graph, h.Tracker));
        CardWalk(h, Dest);

        DetourResume resume = DetourResume.Snapshot(h.Walker, loops, lair, includeWalk: true);
        resume.Stop(h.Walker, loops, lair, "sell detour");
        Assert.Null(h.Walker.Journey);
        h.Walker.WalkTo(Hills, planThroughAcquirableGates: true);   // the errand's own walk
        resume.Resume(h.Walker, loops, lair);

        Assert.Equal(Dest, resume.WalkDestination);
        Assert.Equal("4 step(s)", h.LastPlan);                    // round the talisman exit, as before
        Assert.Equal(Talisman, h.Walker.Journey?.ClosedGates);
        Assert.True(h.Walker.Journey?.PickedRoute);
    }

    // Caught on a side trip, the walk to pick back up is the journey's, not the trip
    // to the giver the walker happened to be on.
    [Fact]
    public void AnErrandCatchingASideTrip_ResumesToTheJourneysDestination()
    {
        Harness h = NewHarness(GatedRooms, Hills);
        LoopRunner loops = new(h.Tracker, h.Coordinator, graph: h.Graph, bfs: h.Bfs, walker: h.Walker);
        AutoLairManager lair = new(h.Walker, h.Tracker, h.Graph, h.Bfs, new LairTimerStore(h.Cache, h.Graph, h.Tracker));
        CardWalk(h, Dest);
        h.Walker.WalkTo(Giver, planThroughAcquirableGates: true, supersedeSilently: true);

        DetourResume resume = DetourResume.Snapshot(h.Walker, loops, lair, includeWalk: true);

        Assert.Equal(Dest, resume.WalkDestination);
    }

    // The teleport preference comes back with it too.
    [Fact]
    public void ResumeJourney_KeepsHowItTakesTeleports()
    {
        Harness h = NewHarness(TeleportRooms, Grove, carriesEverything: true);
        h.Walker.SetAutomaticWalkTeleports(NoTeleports);
        h.Walker.WalkTo(Arch, preferTeleportFree: false);
        WalkJourney journey = h.Walker.Journey!;
        h.Walker.Stop();

        h.Walker.ResumeJourney(journey);
        Play(h);

        Assert.Equal(new[] { "go arch" }, h.Sent);
    }

    // ----- hazard rooms: agreed to on a card, room by room ------------------

    // What "cross unprotected" on the River card commits: through the gates, the
    // picked route, and the one hazard room on it.
    private static bool PickTheRiverCard(Harness h) =>
        h.Walker.WalkTo(Dest, planThroughAcquirableGates: true, armItemAcquisition: false,
            pickedRoute: true, agreedHazardRooms: new[] { River });

    [Fact]
    public void ThePickedCardsHazardRoom_IsTheOneTheWalkEnters()
    {
        Harness h = NewHarness(HazardRooms, Start);

        PickTheRiverCard(h);

        Assert.Equal("2 step(s)", h.LastPlan);
        Play(h);
        Assert.Equal(new[] { "e" }, h.Sent);
    }

    // An errand (a sell detour, a flee, a party comeback) stops the walk and leaves
    // the character somewhere else. From the Camp the shortest way to Dest is through
    // the Swamp, a hazard room nobody was shown. The walk picked back up keeps it
    // closed and goes back round by the River it was agreed to cross.
    [Fact]
    public void AnErrandRestartFromOffTheRoute_KeepsAHazardNobodyAgreedToClosed()
    {
        Harness h = NewHarness(HazardRooms, Start);
        PickTheRiverCard(h);
        WalkJourney journey = h.Walker.Journey!;
        h.Walker.Stop();
        h.Tracker.SetLocated(Camp);

        h.Walker.ResumeJourney(journey, planThroughAcquirableGates: true);

        Assert.Equal("3 step(s)", h.LastPlan);                    // N, E, E by the River; not E, N by the Swamp
        Play(h);
        Assert.Equal(new[] { "n" }, h.Sent);
    }

    // A picked walk that names no rooms agreed to none: there is no "any hazard on
    // the route" to fall back on. Every way to Dest crosses a hazard, so it never
    // starts.
    [Fact]
    public void APickedWalkThatNamesNoHazardRooms_EntersNone()
    {
        Harness h = NewHarness(HazardRooms, Start);

        Assert.False(h.Walker.WalkTo(Dest, planThroughAcquirableGates: true, armItemAcquisition: false, pickedRoute: true));

        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Empty(h.Sent);
    }

    // Nor does its journey carry any across a restart: from the Camp every way to
    // Dest crosses a hazard, so the walk fails where it stands.
    [Fact]
    public void AnErrandRestartOfAJourneyThatNamesNoRooms_EntersNoHazardRoom()
    {
        Harness h = NewHarness(HazardRooms, Start);
        var journey = new WalkJourney(Dest, ThroughGates: true, PickedRoute: true);
        h.Tracker.SetLocated(Camp);

        Assert.False(h.Walker.ResumeJourney(journey, planThroughAcquirableGates: true));

        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Started);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Empty(h.Sent);
    }

    // A re-plan of the walk itself, thrown off its route, opens no more than the
    // restart does.
    [Fact]
    public void AReplanFromOffTheRoute_OpensOnlyTheAgreedHazardRoom()
    {
        Harness h = NewHarness(HazardRooms, Start);
        PickTheRiverCard(h);
        h.Walker.PauseForRecovery("tracker desync");
        h.Tracker.SetLocated(Camp);

        h.Walker.ResumeAfterRecovery(Camp);

        Assert.Equal("3 step(s)", h.LastPlan);
    }

    // A gated walk nobody was shown a card for (a sole route whose items are all
    // fetched for it) agrees to no hazard room, from the first plan on.
    [Fact]
    public void AGatedWalkWithNoCard_EntersNoHazardRoom()
    {
        Harness h = NewHarness(HazardRooms, Start);

        Assert.False(h.Walker.WalkTo(Dest, planThroughAcquirableGates: true));

        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    // "Walk it, don't teleport" is a refusal, and holds on a side trip as well: with
    // no way there on foot the side trip fails, where one of any other walk the user
    // started would take the teleport.
    [Fact]
    public void ASideTripOfAWalkItJourney_NeverTeleports()
    {
        Harness h = NewHarness(TeleportRooms, Grove, carriesEverything: true);
        h.Walker.WalkTo(Arch, avoidTeleports: true, preferTeleportFree: true);
        h.Filter.MarkAvoided(Trail);
        h.Events.Clear();

        h.Walker.WalkTo(Shop, supersedeSilently: true);

        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Empty(h.Sent);
    }

    // ----- what the pick ordered fetched, and the trades its card named -------
    //
    // A trade hands an item of the user's over, so the rule these pin is that it is
    // read off the journey of the card that named it: there for that journey's legs
    // and restarts, and for no walk before, beside or after it.

    private const int GlowingKey = 808, OpalBrooch = 811, Session = 3;

    private static JourneyFetch TradeFetch() =>
        new(new[] { 807, GlowingKey }, new[] { (GlowingKey, OpalBrooch) }, Session);

    // The read AppServices.AgreedTradeFor makes: a picked route's fetch, and nothing else.
    private static int? AgreedTrade(Harness h) =>
        h.Walker.Journey is { PickedRoute: true, Fetch: { } fetch } ? fetch.AgreedTradeFor(GlowingKey, Session) : null;

    private static bool TradeCardWalk(Harness h, RoomKey to) =>
        h.Walker.WalkTo(to, planThroughAcquirableGates: true, pickedRoute: true, keepGatesClosedFor: Talisman,
            fetch: TradeFetch());

    [Fact]
    public void TheCardsTrade_IsInForceForItsJourney_ThroughASideTripAndBack()
    {
        Harness h = NewHarness(GatedRooms, Hills);
        Assert.Null(AgreedTrade(h));

        TradeCardWalk(h, Dest);
        Assert.Equal(OpalBrooch, AgreedTrade(h));
        Assert.True(h.Walker.Journey!.Fetch!.Fetches(807));

        h.Walker.WalkTo(Giver, planThroughAcquirableGates: true, supersedeSilently: true);
        Assert.Equal(OpalBrooch, AgreedTrade(h));

        h.Walker.WalkTo(Dest, planThroughAcquirableGates: true, supersedeSilently: true);
        Assert.Equal(OpalBrooch, AgreedTrade(h));
    }

    // The give failed, the door was found open, the walk arrived. Its journey is
    // over, and whatever walks next through that door reads no agreement.
    [Fact]
    public void TheWalkArrives_ItsTradeIsGone_AndALaterWalkReadsNone()
    {
        Harness h = NewHarness(TeleportRooms, Trail, carriesEverything: true, paused: false);
        RoomKey ridge = new(1, 12);
        h.Walker.WalkTo(ridge, planThroughAcquirableGates: true, pickedRoute: true, preferTeleportFree: true,
            fetch: TradeFetch());
        Assert.Equal(OpalBrooch, AgreedTrade(h));

        h.Tracker.NoteRoomObserved(new RoomObservation("Ridge",
            new HashSet<Direction> { Direction.N, Direction.S, Direction.E }));
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
        Assert.Null(h.Walker.Journey);
        Assert.Null(AgreedTrade(h));

        // A trainer trip, a stash transfer, a loop's approach: through gates, to the
        // same room or another.
        h.Walker.WalkTo(Arch, planThroughAcquirableGates: true);
        Assert.Null(h.Walker.Journey?.Fetch);
        Assert.Null(AgreedTrade(h));
        h.Walker.WalkTo(ridge, planThroughAcquirableGates: true);
        Assert.Null(AgreedTrade(h));
        // Nor a detour leg made for that later walk.
        h.Walker.WalkTo(Shop, planThroughAcquirableGates: true, supersedeSilently: true);
        Assert.Null(AgreedTrade(h));
    }

    [Fact]
    public void AStop_EvenIdleBetweenLegs_EndsTheTrade()
    {
        Harness h = NewHarness(TeleportRooms, Trail, carriesEverything: true);
        h.Walker.WalkTo(Arch, planThroughAcquirableGates: true, pickedRoute: true, preferTeleportFree: true,
            fetch: TradeFetch());
        h.Walker.WalkTo(Trail, supersedeSilently: true);          // at the giver: arrives at once
        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Equal(OpalBrooch, AgreedTrade(h));

        h.Walker.Stop("user stop");

        Assert.Null(AgreedTrade(h));
        // The leg the detour would have gone on with finds nothing.
        h.Walker.WalkTo(Arch, planThroughAcquirableGates: true, supersedeSilently: true);
        Assert.Null(AgreedTrade(h));
    }

    [Fact]
    public void TheWalkFails_OrAnotherWalkReplacesIt_TheTradeIsGone()
    {
        Harness h = NewHarness(GatedRooms, Hills);
        TradeCardWalk(h, Dest);

        h.Walker.WalkTo(Giver, planThroughAcquirableGates: true);     // any walk that isn't its leg
        Assert.Null(AgreedTrade(h));

        h.Walker.WalkTo(Dest, pickedRoute: true, fetch: TradeFetch());   // gates live: no route
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Null(AgreedTrade(h));
    }

    // Picking the card while another walk is under way stops that walk first. The
    // order is the new walk's, so the old walk's Stopped can't take it away.
    [Fact]
    public void ACardPickedDuringAnotherWalk_KeepsItsOrder()
    {
        Harness h = NewHarness(GatedRooms, Hills);
        h.Walker.WalkTo(Giver, planThroughAcquirableGates: true);

        TradeCardWalk(h, Dest);

        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Stopped);
        Assert.Equal(OpalBrooch, AgreedTrade(h));
        Assert.True(h.Walker.Journey!.Fetch!.Fetches(807));
    }

    // A walk no card was shown for is not a picked route: it fetches, and trades
    // nothing, whatever its fetch was handed.
    [Fact]
    public void AWalkNoCardWasShownFor_FetchesAndNeverTrades()
    {
        Harness h = NewHarness(GatedRooms, Hills);

        h.Walker.WalkTo(Dest, planThroughAcquirableGates: true, fetch: TradeFetch());

        Assert.True(h.Walker.Journey!.Fetch!.Fetches(GlowingKey));
        Assert.Null(AgreedTrade(h));
    }

    // An errand that stopped the walk picks its journey back up, order and all; the
    // errand's own walk in between carries none of it.
    [Fact]
    public void AnErrandRestart_PicksTheOrderBackUp_AndTheErrandsWalkCarriesNone()
    {
        Harness h = NewHarness(GatedRooms, Hills);
        TradeCardWalk(h, Dest);
        WalkJourney journey = h.Walker.Journey!;

        h.Walker.WalkTo(Giver, planThroughAcquirableGates: true);     // the errand's walk
        Assert.Null(AgreedTrade(h));

        h.Walker.ResumeJourney(journey);
        Assert.Equal(OpalBrooch, AgreedTrade(h));
        Assert.Same(journey.Fetch, h.Walker.Journey?.Fetch);
    }

    // Once the key is in hand there is nothing left to trade for, and a trade is
    // not made on another connection than the one it was agreed on.
    [Fact]
    public void TheTrade_EndsWithTheKeyInHand_AndWithTheConnection()
    {
        JourneyFetch fetch = TradeFetch();
        Assert.Equal(OpalBrooch, fetch.AgreedTradeFor(GlowingKey, Session));
        Assert.Null(fetch.AgreedTradeFor(807, Session));              // fetched, never traded for
        Assert.Null(fetch.AgreedTradeFor(GlowingKey, Session + 1));

        fetch.DropCovered(id => id == GlowingKey);

        Assert.Null(fetch.AgreedTradeFor(GlowingKey, Session));
        Assert.True(fetch.Fetches(807));
        Assert.True(fetch.HasItems);
    }

    // A trade for a key the journey isn't fetching is no agreement at all.
    [Fact]
    public void ATradeForAKeyNotBeingFetched_IsNotInForce()
    {
        var fetch = new JourneyFetch(new[] { 807 }, new[] { (GlowingKey, OpalBrooch) }, Session);

        Assert.Null(fetch.AgreedTradeFor(GlowingKey, Session));
    }
}
