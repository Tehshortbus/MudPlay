using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Covers TeleportMazeSolver end-to-end against a synthetic random-teleport
// pocket: relocalize-by-look-sweep then delegate a plain walk, reshuffle to jump
// plain-disconnected components, enter the pocket from outside, and the
// maze-room-only / wire-bound / not-already-active CanSolve gate.
//
// The pocket has TWO plain-connected components linked only by cast-teleport
// exits — component A {1/10, 1/11, 1/12} and component B {1/30, 1/31} — so a
// landing in A has no plain route to a goal in B and must reshuffle. Every
// corridor's 1x2 signature (own exit mask + each neighbour's mask read by a
// `look`) is unique, so a landing always relocalizes to an exact room.
//
// The solver runs on every realm — the CanSolve gate is maze-room-only /
// wire-bound / not-already-active (no realm check).
public sealed class TeleportMazeSolverTests : IDisposable
{
    private readonly string _root;

    public TeleportMazeSolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-mazesolver-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // Random teleport 596 (Abil 140 value 0, range 10..21) — casts on every
    // pocket exit and on the one-way mouth.
    private const string Spells = """
        [
          { "Number": 596, "Name": "warp", "Short": "wrp",
            "MinBase": 10, "MaxBase": 21,
            "Abil-0": 140, "AbilVal-0": 0, "Abil-1": 141, "AbilVal-1": 1 }
        ]
        """;

    // Two random teleports with DIFFERENT landing pools, mirroring the asylum's
    // "asylum" (596) vs "cell" (597) split: 596 lands in the relocatable corridor
    // 1/20..1/21 (both reach the goal); 597 lands in 1/40..1/41 (rooms that don't
    // exist in the pocket, so they're never relocatable — the reshuffle chooser
    // must reject it).
    private const string ChoiceSpells = """
        [
          { "Number": 596, "Name": "asylum", "Short": "asy",
            "MinBase": 20, "MaxBase": 21,
            "Abil-0": 140, "AbilVal-0": 0, "Abil-1": 141, "AbilVal-1": 1 },
          { "Number": 597, "Name": "cell", "Short": "cel",
            "MinBase": 40, "MaxBase": 41,
            "Abil-0": 140, "AbilVal-0": 0, "Abil-1": 141, "AbilVal-1": 1 }
        ]
        """;

    // 1/1 Courtyard ──N── 1/2 Gate  (overworld)
    //   │ S (cast 596, one-way pocket mouth)
    //   ▼
    // Component A: 1/10 ─E─ 1/11 ─S─ 1/12   (plain corridors)
    // Component B: 1/30 ─W─ 1/31            (plain corridors, holds the goal 1/30)
    // A and B connect ONLY through the cast-teleport D exits (reshuffle).
    private const string SplitMaze = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Courtyard",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "1/10 (Cast: pre-0, post-596)", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Gate",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 10, "Name": "Warped Asylum",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/11", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "1/30 (Cast: pre-0, post-596)" },
          { "Map Number": 1, "Room Number": 11, "Name": "Warped Asylum",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/12", "E": "0", "W": "1/10",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "1/30 (Cast: pre-0, post-596)" },
          { "Map Number": 1, "Room Number": 12, "Name": "Warped Asylum",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "1/11", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "1/30 (Cast: pre-0, post-596)" },
          { "Map Number": 1, "Room Number": 30, "Name": "Warped Asylum",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/31",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "1/10 (Cast: pre-0, post-596)" },
          { "Map Number": 1, "Room Number": 31, "Name": "Warped Asylum",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/30", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "1/10 (Cast: pre-0, post-596)" }
        ]
        """;

    // A pocket whose goal is a dead-end "Padded Cell" cell, mirroring the asylum's
    // old-man room. Two sibling cells (1/12, 1/22) share an identical 1x2
    // signature ({N} exit, parent mask {N,S}) so the index omits BOTH as
    // non-relocatable — the solver can only arrive by driving the plain step and
    // matching the cell's name, never by relocalizing onto it.
    //
    //            1/1 Courtyard ─N─ 1/2 Gate         (overworld)
    //              │ S (cast 596, one-way mouth)
    //              ▼
    //   1/10 WA ─S─ 1/11 WA ─S─ 1/12 Padded Cell   (goal, dead-end)
    //    │ E(cast)   (landing)
    //    ▼
    //   1/13 WA ─S─ 1/20 WA ─S─ 1/22 Padded Cell   (sibling cell → signature clash)
    //    │ W(cast)
    private const string DeadEndMaze = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Courtyard",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "1/10 (Cast: pre-0, post-596)", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Gate",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 10, "Name": "Warped Asylum",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/11", "E": "1/13 (Cast: pre-0, post-596)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 11, "Name": "Warped Asylum",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "1/10", "S": "1/12", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 12, "Name": "Padded Cell",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "1/11", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 13, "Name": "Warped Asylum",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/20", "E": "0", "W": "1/10 (Cast: pre-0, post-596)",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 20, "Name": "Warped Asylum",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "1/13", "S": "1/22", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 22, "Name": "Padded Cell",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "1/20", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // A pocket whose decision room 1/10 offers TWO reshuffle exits firing
    // DIFFERENT spells: D fires 596 (pool 1/20..1/21, relocatable corridor that
    // reaches the goal 1/30) and U fires 597 (pool 1/40..1/41, rooms absent from
    // the pocket → never relocatable). 1/10 has no plain route to the goal, so it
    // must reshuffle — and the chooser must walk D (the useful pool), not U.
    //
    //   1/1 Courtyard ─N─ 1/2 Gate                 (overworld)
    //     │ S (cast 596, one-way mouth)
    //     ▼
    //   1/10 WA ─E─ 1/11 WA
    //     │ D(cast 596 → 1/20..1/21)   │ U(cast 597 → 1/40..1/41, dead pool)
    //     ▼                            ▼
    //   1/20 WA ─E─ 1/21 WA ─S─ 1/30 WA(goal)     1/12 Padded Cell (U nominal target)
    private const string ChoiceMaze = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Courtyard",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "1/10 (Cast: pre-0, post-596)", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Gate",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 10, "Name": "Warped Asylum",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/11", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0",
            "U": "1/12 (Cast: pre-0, post-597)", "D": "1/20 (Cast: pre-0, post-596)" },
          { "Map Number": 1, "Room Number": 11, "Name": "Warped Asylum",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/10",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 12, "Name": "Padded Cell",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 20, "Name": "Warped Asylum",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/21", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 21, "Name": "Warped Asylum",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/30", "E": "0", "W": "1/20",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 30, "Name": "Warped Asylum",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "1/21", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private sealed class Harness : IDisposable
    {
        public required RoomGraphManager Graph { get; init; }
        public required BfsMapper Bfs { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public required TeleportMazeIndex Index { get; init; }
        public required TeleportMazeSolver Solver { get; init; }
        public required MovementCoordinator Coord { get; init; }
        // The solver's settle and look-timeout timers.
        public required ManualTimers SolverTimers { get; init; }
        // The game's lines, through the router the `rm` resolver reads.
        public required ServerFeed Server { get; init; }
        public int ManualMoves;
        public List<byte[]> Sent { get; } = new();
        public List<WalkEvent> Events { get; } = new();
        // Door-open requests the solver made via its openDoor delegate (when the
        // harness wired one). Each carries the callback the test invokes to report
        // open success/failure.
        public List<(Direction Dir, Action<bool> Done)> DoorOpens { get; init; } = new();
        // Times the solver stopped the door FSM.
        public int DoorStops;
        public List<string> SentText => Sent.Select(b => Encoding.Latin1.GetString(b)).ToList();
        public void Dispose() => Solver.Dispose();
    }

    private Harness NewHarness(string rooms = SplitMaze, bool bindWire = true, string spells = Spells,
        bool isParadigm = false, bool wireDoorOpener = false)
    {
        string dir = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Rooms.json"), rooms);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), spells);
        File.WriteAllText(Path.Combine(dir, "TBInfo.json"), "[]");

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        TBInfoStore tbinfo = new(cache);
        tbinfo.OnActiveSetChanged("alpha");
        KnownSpellCatalog catalog = new(cache);
        RoomGraphManager graph = new(cache, log: null, tbinfo, catalog);
        graph.OnActiveSetChanged("alpha");

        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        AutoWalkManager walker = new(graph, bfs, tracker, coord);
        TeleportMazeIndex index = new(graph);
        // When asked, wire an openDoor delegate that records each request so a test
        // can resolve it (open success/failure) — mirrors AppServices delegating to
        // the shared door FSM. Left null otherwise (closed-door peeks just skip).
        var doorOpens = new List<(Direction Dir, Action<bool> Done)>();
        Action<Direction, Action<bool>>? opener = wireDoorOpener
            ? (dir, done) => doorOpens.Add((dir, done))
            : null;
        // Synchronous post: Start() runs inline in TryBegin.
        Harness? made = null;
        MessageRouter router = new();
        MudPlay.Services.Patterns.DefaultPatterns.Seed(router);
        ParadigmPositionResolver resolver = new(router, tracker, new EngineRecoveryGate(graph, tracker), cache,
            schedule: new ManualTimers().Arm);
        ManualTimers solverTimers = new();
        TeleportMazeSolver solver = new(index, graph, tracker, bfs, walker,
            log: null, post: a => a(), schedule: solverTimers.Arm,
            isParadigm: isParadigm ? () => true : null, paradigmResolver: resolver, openDoor: opener, coordinator: coord,
            stopDoor: () => made!.DoorStops++);

        Harness h = made = new()
        {
            Graph = graph, Bfs = bfs, Tracker = tracker,
            Walker = walker, Index = index, Solver = solver, DoorOpens = doorOpens, Coord = coord,
            SolverTimers = solverTimers, Server = new ServerFeed(router),
        };
        tracker.ManualMoveObserved += _ => h.ManualMoves++;
        walker.SetWireSender(h.Sent.Add);
        walker.SetMazeSolver(solver);
        walker.Event += h.Events.Add;
        // Through the outbound observer, as in the app: a move the solver didn't
        // announce to the tracker first would be read as hand-typed.
        OutboundMovementObserver outbound = new(tracker);
        if (bindWire)
            solver.SetWireSender(bytes =>
            {
                h.Sent.Add(bytes);
                outbound.ObserveOutbound(bytes);
            });
        return h;
    }

    private static RoomObservation Asylum(params Direction[] exits)
        => new("Warped Asylum", new HashSet<Direction>(exits));

    private static RoomObservation PaddedCell(params Direction[] exits)
        => new("Padded Cell", new HashSet<Direction>(exits));

    // A Warped Asylum room where one exit is a CLOSED door/gate (still a real exit,
    // but flagged closed on the obvious-exits line so the solver opens it first).
    private static RoomObservation AsylumClosed(Direction closed, params Direction[] exits)
        => new("Warped Asylum", new HashSet<Direction>(exits),
               OpenDoorDirections: null,
               ClosedDoorDirections: new HashSet<Direction> { closed });

    // The game's answer to `rm`, read by the position resolver the solver listens to.
    private static void FeedRm(Harness h, RoomKey key) =>
        h.Server.Line($"Location: {key.Map},{key.Room}");

    [Fact]
    public void RelocalizesAndDrivesFinalWalk_WhenPlainRouteExists()
    {
        Harness h = NewHarness();
        // A teleport dropped us in 1/31 (tracker Lost). Goal is plain-adjacent 1/30.

        Assert.True(h.Walker.WalkTo(new RoomKey(1, 30)));   // walker hands off to the solver
        Assert.True(h.Solver.Active);

        // The solver forces a self-look to render the landing (brief mode gives no
        // exits on entry); feed 1/31's own display, then settle onto it.
        h.Solver.OnRoomObserved(Asylum(Direction.E, Direction.D));  // 1/31's self-look render
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);

        // Look sweep of 1/31's exits (enum order E, D): peek each neighbour.
        h.Solver.OnRoomObserved(Asylum(Direction.W, Direction.D));  // look E → 1/30
        h.Solver.OnRoomObserved(Asylum(Direction.E, Direction.D));  // look D → 1/10

        // Relocalized to 1/31 → a plain route to the goal exists, so we're in a
        // solvable room: the solver drives the step E with NO per-step re-location
        // (no self-look after the move) — the last wire byte is the move, not `look`.
        Assert.Equal(new RoomKey(1, 31), h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal("e\r", h.SentText[^1]);

        // One settle tick (pure pacing) finishes the one-step route deterministically.
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);

        Assert.False(h.Solver.Active);
        Assert.Equal(0, h.Solver.Attempts);
        Assert.Equal(new RoomKey(1, 30), h.Tracker.State.CurrentRoom!.Key);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    [Fact]
    public void ClosedDoorPeek_OpensBarrierBeforeLooking_ThenRelocalizes()
    {
        // A teleport dropped us in 1/31 (tracker Lost) whose EAST exit is a CLOSED
        // door. Peeking it with `look east` would be refused at the shut door — so
        // the solver must open the barrier first, then look, then relocalize.
        Harness h = NewHarness(wireDoorOpener: true);

        Assert.True(h.Walker.WalkTo(new RoomKey(1, 30)));
        Assert.True(h.Solver.Active);

        // Self-look renders 1/31; its E exit is flagged "closed door".
        h.Solver.OnRoomObserved(AsylumClosed(Direction.E, Direction.E, Direction.D));
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);

        // The sweep reaches E first, sees a closed door, and requests an open —
        // WITHOUT sending the peek yet.
        (Direction Dir, Action<bool> Done) req = Assert.Single(h.DoorOpens);
        Assert.Equal(Direction.E, req.Dir);
        Assert.DoesNotContain("look east\r", h.SentText);

        // Barrier opens → the deferred peek now goes out.
        req.Done(true);
        Assert.Contains("look east\r", h.SentText);

        // Feed the two neighbour renders (E → 1/30, D → 1/10); the signature
        // resolves to 1/31 and the solver drives the plain route to the goal.
        h.Solver.OnRoomObserved(Asylum(Direction.W, Direction.D));   // look east → 1/30
        h.Solver.OnRoomObserved(Asylum(Direction.E, Direction.D));   // look down → 1/10

        Assert.Equal(new RoomKey(1, 31), h.Tracker.State.CurrentRoom!.Key);
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);
        Assert.False(h.Solver.Active);
        Assert.Equal(new RoomKey(1, 30), h.Tracker.State.CurrentRoom!.Key);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    [Fact]
    public void ClosedDoorPeek_OpenFails_SkipsPeekInsteadOfFailingSolve()
    {
        // The reported bug: a closed door on a peek direction failed the whole solve
        // out. Now an open-failure just skips that neighbour and carries on — the
        // solve is NOT abandoned on one shut barrier.
        Harness h = NewHarness(wireDoorOpener: true);

        Assert.True(h.Walker.WalkTo(new RoomKey(1, 30)));
        Assert.True(h.Solver.Active);

        h.Solver.OnRoomObserved(AsylumClosed(Direction.E, Direction.E, Direction.D));
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);

        (Direction Dir, Action<bool> Done) req = Assert.Single(h.DoorOpens);
        Assert.Equal(Direction.E, req.Dir);

        // The open fails — the neighbour stays opaque. The solver skips E and peeks
        // the next exit (D) rather than failing the solve.
        req.Done(false);
        Assert.Contains("look down\r", h.SentText);
        Assert.True(h.Solver.Active);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    // Stop with the peek's door still being opened: the door FSM goes on bashing
    // until the door opens or the room changes, so it is stopped with the solve.
    [Fact]
    public void Cancel_WithAPeekDoorBeingOpened_StopsTheDoorFsm()
    {
        Harness h = NewHarness(wireDoorOpener: true);
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 30)));
        h.Solver.OnRoomObserved(AsylumClosed(Direction.E, Direction.E, Direction.D));
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);
        Assert.Single(h.DoorOpens);

        h.Solver.Cancel("test");

        Assert.False(h.Solver.Active);
        Assert.Equal(1, h.DoorStops);
    }

    [Fact]
    public void FastWalk_MultiStep_SendsAllMoves_NoInterleavedRelocalize()
    {
        // Once relocalized into a solvable room, the whole plain route is a verified-
        // unhindered corridor, so the solver paces out EVERY move with no `look` /
        // `rm` between them. Goal 1/12 is a 2-step plain route from 1/10 (E then S).
        Harness h = NewHarness(SplitMaze);

        Assert.True(h.Walker.WalkTo(new RoomKey(1, 12)));
        Assert.True(h.Solver.Active);

        // Self-look renders 1/10 (own exits E, D); settle, then sweep to relocalize.
        h.Solver.OnRoomObserved(Asylum(Direction.E, Direction.D));
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);
        h.Solver.OnRoomObserved(Asylum(Direction.W, Direction.S, Direction.D));  // look E → 1/11
        // Snapshot before the final look reply, which resolves the sweep and fires
        // the first drive move synchronously — everything from here on is the drive.
        int afterSweep = h.Sent.Count;
        h.Solver.OnRoomObserved(Asylum(Direction.W, Direction.D));               // look D → 1/30

        // Relocalized to 1/10 → 2-step plain route; first move E goes out at once,
        // with no self-look after it.
        Assert.Equal(new RoomKey(1, 10), h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal("e\r", h.SentText[^1]);

        // Pacing tick → the second move S. No `look` / `rm` interleaved between them.
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);
        Assert.Equal("s\r", h.SentText[^1]);

        // Final pacing tick → deterministic arrival at the goal cell 1/12.
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);

        Assert.False(h.Solver.Active);
        Assert.Equal(new RoomKey(1, 12), h.Tracker.State.CurrentRoom!.Key);
        // The only bytes sent from the resolve onward were the two cardinal moves.
        List<string> drive = h.SentText.Skip(afterSweep).ToList();
        Assert.Equal(new[] { "e\r", "s\r" }, drive);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    [Fact]
    public void Paradigm_RelocalizesViaRm_NeverLooks()
    {
        // On Paradigm the game answers `rm` with the player's authoritative
        // (map,room), so the solver relocalizes with `rm` ALONE — never a `look`.
        // Pacing (report 152718): after a move it waits out the teleport's own
        // redisplay (a settle window), THEN fires a single `rm`; firing it the
        // instant the move was sent raced the round-time and desynced the replies.
        Harness h = NewHarness(isParadigm: true);
        // A teleport dropped us in 1/31 (tracker Lost). Goal is plain-adjacent 1/30.

        Assert.True(h.Walker.WalkTo(new RoomKey(1, 30)));
        Assert.True(h.Solver.Active);

        // Post-teleport the solver does NOT fire `rm` yet — it settles first. No
        // `rm` and no `look` on the wire until the redisplay quiets.
        Assert.DoesNotContain("rm\r", h.SentText);
        Assert.DoesNotContain("look\r", h.SentText);

        // The teleport redisplays the room; the settle then elapses → a single `rm`.
        h.Solver.OnRoomObserved(Asylum(Direction.E, Direction.D));
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);
        Assert.Equal("rm\r", h.SentText[^1]);
        Assert.DoesNotContain("look\r", h.SentText);

        // The game's `Location:` reply re-anchors to 1/31 (what ParadigmPositionResolver
        // does off the rm reply, then fires PositionResolved) → a plain route to the
        // goal exists, so we're in a solvable room: the solver drives the route with
        // NO per-step `rm`. The first move E goes out at once — not a second `rm`.
        FeedRm(h, new RoomKey(1, 31));

        Assert.DoesNotContain(h.SentText, s => s.StartsWith("look"));
        Assert.Equal("e\r", h.SentText[^1]);
        Assert.Equal(1, h.SentText.Count(s => s == "rm\r"));   // only the landing `rm`

        // One pacing tick finishes the one-step route deterministically — no more `rm`.
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);

        Assert.False(h.Solver.Active);
        Assert.Equal(0, h.Solver.Attempts);
        Assert.Equal(1, h.SentText.Count(s => s == "rm\r"));   // still just the landing `rm`
        Assert.DoesNotContain(h.SentText, s => s.StartsWith("look"));
        Assert.Equal(new RoomKey(1, 30), h.Tracker.State.CurrentRoom!.Key);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    [Fact]
    public void Paradigm_RmTimeout_RetriesRm_NeverLooks()
    {
        // A dropped `rm` reply must not hang the solve, and must NOT fall back to a
        // look: the resync deadline just re-sends `rm` (up to the retry cap).
        Harness h = NewHarness(isParadigm: true);

        Assert.True(h.Walker.WalkTo(new RoomKey(1, 30)));

        // Settle onto the teleport's redisplay → the first `rm`.
        h.Solver.OnRoomObserved(Asylum(Direction.E, Direction.D));
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);
        Assert.Equal("rm\r", h.SentText[^1]);
        Assert.Equal(1, h.SentText.Count(s => s == "rm\r"));

        // No Location: reply — the deadline elapses and `rm` is re-sent, never a look.
        h.SolverTimers.RunDue(TeleportMazeSolver.LookTimeout);
        Assert.Equal("rm\r", h.SentText[^1]);
        Assert.Equal(2, h.SentText.Count(s => s == "rm\r"));
        Assert.DoesNotContain(h.SentText, s => s.StartsWith("look"));

        // The retry's reply finally arrives → we're in a solvable room, so the solver
        // drives the plain route with NO per-step `rm` and finishes on one pacing tick.
        FeedRm(h, new RoomKey(1, 31));                             // relocalized → step E
        Assert.Equal("e\r", h.SentText[^1]);
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);                             // pacing → arrive goal 1/30

        Assert.False(h.Solver.Active);
        Assert.DoesNotContain(h.SentText, s => s.StartsWith("look"));
        Assert.Equal(new RoomKey(1, 30), h.Tracker.State.CurrentRoom!.Key);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    [Fact]
    public void DrivesIntoDeadEndGoal_DeterministicArrival()
    {
        // The goal cell is a dead-end Padded Cell whose 1x2 signature collides with
        // a sibling cell, so the index omits it (non-relocatable). The fast walk
        // reaches it by driving the plain step deterministically — it never tries to
        // relocalize onto the cell, so it can't mis-ID and walk back out (the "made
        // it to the old man then walked out" report).
        Harness h = NewHarness(DeadEndMaze);
        // A teleport dropped us in 1/11 (Lost); the goal 1/12 hangs plain-south.

        Assert.True(h.Walker.WalkTo(new RoomKey(1, 12)));
        Assert.True(h.Solver.Active);

        // Self-look renders 1/11 (own exits N, S); settle.
        h.Solver.OnRoomObserved(Asylum(Direction.N, Direction.S));
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);

        // Look sweep of 1/11 (enum order N, S).
        h.Solver.OnRoomObserved(Asylum(Direction.S, Direction.E));  // look N → 1/10
        h.Solver.OnRoomObserved(PaddedCell(Direction.N));           // look S → 1/12

        // Relocalized to 1/11 → plain route S to the cell → solver drives the step
        // with no self-look after it.
        Assert.Equal(new RoomKey(1, 11), h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal("s\r", h.SentText[^1]);
        int sentBeforeArrival = h.Sent.Count;

        // One pacing tick finishes on the goal cell — no further sends, no reshuffle.
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);

        Assert.False(h.Solver.Active);
        Assert.Equal(0, h.Solver.Attempts);
        Assert.Equal(new RoomKey(1, 12), h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(sentBeforeArrival, h.Sent.Count);   // no walk-back-out
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    [Fact]
    public void UserPause_HoldsTheNextMove_AndResumeSendsIt()
    {
        // Same 2-step drive as the fast-walk test (E then S from 1/10 to 1/12).
        Harness h = NewHarness(SplitMaze);
        int changes = 0;
        h.Solver.StateChanged += () => changes++;

        Assert.True(h.Walker.WalkTo(new RoomKey(1, 12)));
        Assert.Equal(1, changes);                        // started
        h.Solver.OnRoomObserved(Asylum(Direction.E, Direction.D));
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);
        h.Solver.OnRoomObserved(Asylum(Direction.W, Direction.S, Direction.D));
        h.Solver.OnRoomObserved(Asylum(Direction.W, Direction.D));
        Assert.Equal("e\r", h.SentText[^1]);

        // Paused between the two moves: the pacing tick sends nothing.
        h.Coord.AssertGate(MovementCoordinator.UserGate, "test");
        int sent = h.Sent.Count;
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);
        Assert.Equal(sent, h.Sent.Count);
        Assert.True(h.Solver.IsHeld);
        Assert.True(h.Solver.Active);
        Assert.Equal(2, changes);                        // went into the hold

        // Resume: the held move goes out, and the solve finishes as usual.
        h.Coord.ClearGate(MovementCoordinator.UserGate, "test");
        Assert.False(h.Solver.IsHeld);
        Assert.Equal("s\r", h.SentText[^1]);
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);

        Assert.False(h.Solver.Active);
        Assert.Equal(new RoomKey(1, 12), h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(4, changes);                        // out of the hold, then finished
    }

    [Fact]
    public void Cancel_WhileHeld_DropsTheHeldMove()
    {
        Harness h = NewHarness(SplitMaze);
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 12)));
        h.Solver.OnRoomObserved(Asylum(Direction.E, Direction.D));
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);
        h.Solver.OnRoomObserved(Asylum(Direction.W, Direction.S, Direction.D));
        h.Solver.OnRoomObserved(Asylum(Direction.W, Direction.D));
        h.Coord.AssertGate(MovementCoordinator.UserGate, "test");
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);
        Assert.True(h.Solver.IsHeld);

        h.Solver.Cancel("stopped");
        int sent = h.Sent.Count;
        h.Coord.ClearGate(MovementCoordinator.UserGate, "test");

        Assert.False(h.Solver.Active);
        Assert.Equal(sent, h.Sent.Count);
    }

    [Fact]
    public void ItsOwnMoves_AreNeverReadAsHandTyped()
    {
        Harness h = NewHarness(SplitMaze);
        Assert.True(h.Walker.WalkTo(new RoomKey(1, 12)));
        h.Solver.OnRoomObserved(Asylum(Direction.E, Direction.D));
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);
        h.Solver.OnRoomObserved(Asylum(Direction.W, Direction.S, Direction.D));
        h.Solver.OnRoomObserved(Asylum(Direction.W, Direction.D));
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);

        Assert.False(h.Solver.Active);
        Assert.Equal(0, h.ManualMoves);
    }

    [Fact]
    public void ReshufflesToJumpComponents_WhenGoalUnreachable()
    {
        Harness h = NewHarness();
        // Teleport dropped us in 1/10 (component A); the goal 1/30 is in B, so no
        // plain route exists and the solver must reshuffle to re-teleport.

        Assert.True(h.Walker.WalkTo(new RoomKey(1, 30)));

        // Self-look renders 1/10 (brief mode gives no exits on entry); settle.
        h.Solver.OnRoomObserved(Asylum(Direction.E, Direction.D));  // 1/10's self-look render
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);

        // Look sweep of 1/10 (E, D).
        h.Solver.OnRoomObserved(Asylum(Direction.W, Direction.S, Direction.D));  // look E → 1/11
        h.Solver.OnRoomObserved(Asylum(Direction.W, Direction.D));               // look D → 1/30

        // Relocalized to 1/10, no plain route to 1/30 → reshuffle down the cast exit.
        Assert.Equal(new RoomKey(1, 10), h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(1, h.Solver.Attempts);
        Assert.Contains("d\r", h.SentText);

        // The reshuffle teleported us into 1/30 (component B). Settle, then relocalize.
        h.Solver.OnRoomObserved(Asylum(Direction.W, Direction.D));  // 1/30's own display
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);

        h.Solver.OnRoomObserved(Asylum(Direction.E, Direction.D));  // look W → 1/31
        h.Solver.OnRoomObserved(Asylum(Direction.E, Direction.D));  // look D → 1/10

        // Relocalized onto the goal itself → done.
        Assert.False(h.Solver.Active);
        Assert.Equal(new RoomKey(1, 30), h.Tracker.State.CurrentRoom!.Key);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
    }

    [Fact]
    public void Reshuffle_PrefersSpellWhosePoolCanReachTheGoal()
    {
        // 1/10 has two reshuffle exits firing different spells: D → 596 (pool
        // 1/20..1/21, both relocatable and plain-connected to the goal) and U →
        // 597 (pool 1/40..1/41, rooms absent from the pocket → never relocatable).
        // The chooser must walk D. Picking U (the old dirs[0] behaviour, if U sorts
        // first) dumps into a dead pool and the solve spirals — the 80-try failure.
        Harness h = NewHarness(ChoiceMaze, spells: ChoiceSpells);
        // A teleport dropped us in 1/10 (Lost); the goal 1/30 is only reachable
        // through a 596 landing, so 1/10 must reshuffle.

        Assert.True(h.Walker.WalkTo(new RoomKey(1, 30)));
        Assert.True(h.Solver.Active);

        // Self-look renders 1/10 (own exits E, U, D); settle.
        h.Solver.OnRoomObserved(Asylum(Direction.E, Direction.U, Direction.D));
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);

        // Look sweep of 1/10 (enum order E, U, D): peek each neighbour.
        h.Solver.OnRoomObserved(Asylum(Direction.W));      // look E → 1/11
        h.Solver.OnRoomObserved(PaddedCell());             // look U → 1/12 (dead-end cell, no exits)
        h.Solver.OnRoomObserved(Asylum(Direction.E));      // look D → 1/20

        // Relocalized to 1/10, no plain route to 1/30 → reshuffle. The chooser
        // walks D (596, whose pool reaches the goal), never U (597, a dead pool).
        Assert.Equal(new RoomKey(1, 10), h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(1, h.Solver.Attempts);
        Assert.Contains("d\r", h.SentText);
        Assert.DoesNotContain("u\r", h.SentText);
    }

    [Fact]
    public void EntersFromOutside_RoutesToMouthThenCrosses()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 2));   // Confirmed in the overworld, outside the pocket

        Assert.True(h.Walker.WalkTo(new RoomKey(1, 30)));

        // Solver routes to the mouth room 1/1 (walker steps S). Confirm the step.
        Assert.Contains("s\r", h.SentText);
        int sentBeforeArrival = h.Sent.Count;
        h.Tracker.NoteRoomObserved(new RoomObservation("Courtyard",
            new HashSet<Direction> { Direction.N, Direction.S }));

        // Reaching the mouth fires the cross — the entrance move S plus a self-look
        // to force the teleport landing's full render — and the solver moves into
        // its post-teleport settle window.
        Assert.True(h.Sent.Count > sentBeforeArrival);
        Assert.Equal("s\r", Encoding.Latin1.GetString(h.Sent[^2]));      // the crossing move
        Assert.Equal("look\r", Encoding.Latin1.GetString(h.Sent[^1]));   // self-look for the landing
        Assert.Equal("Settling", h.Solver.PhaseName);

        // Teleport drops us in 1/30. Feed the self-look render, settle, relocalize,
        // land on goal → done.
        h.Solver.OnRoomObserved(Asylum(Direction.W, Direction.D));
        h.SolverTimers.RunDue(TeleportMazeSolver.SettleWindow);
        h.Solver.OnRoomObserved(Asylum(Direction.E, Direction.D));  // look W → 1/31
        h.Solver.OnRoomObserved(Asylum(Direction.E, Direction.D));  // look D → 1/10

        Assert.False(h.Solver.Active);
        Assert.Equal(new RoomKey(1, 30), h.Tracker.State.CurrentRoom!.Key);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
    }

    [Fact]
    public void CanSolve_False_ForNonMazeRoom()
    {
        Harness h = NewHarness();
        Assert.True(h.Solver.Enabled);   // runs on every realm (Paradigm included)
        Assert.False(h.Solver.CanSolve(new RoomKey(1, 1)));   // the mouth room is outside
        Assert.False(h.Solver.CanSolve(new RoomKey(1, 2)));   // overworld
        Assert.True(h.Solver.CanSolve(new RoomKey(1, 30)));   // a pocket member
    }

    [Fact]
    public void CanSolve_False_WhenNoWireSenderBound()
    {
        Harness h = NewHarness(bindWire: false);
        Assert.False(h.Solver.CanSolve(new RoomKey(1, 30)));
    }

    [Fact]
    public void CanSolve_False_WhileAlreadySolving()
    {
        Harness h = NewHarness();
        h.Solver.OnRoomObserved(Asylum(Direction.E, Direction.D));  // seed a landing display

        Assert.True(h.Solver.TryBegin(new RoomKey(1, 30)));         // enters the look sweep
        Assert.True(h.Solver.Active);
        Assert.False(h.Solver.CanSolve(new RoomKey(1, 30)));        // re-entry refused
        Assert.False(h.Solver.TryBegin(new RoomKey(1, 30)));
    }
}
