using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FujinTerm.Game.Map;
using FujinTerm.Game.Spells;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

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
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-mazesolver-tests-" + Path.GetRandomFileName());
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
        public List<byte[]> Sent { get; } = new();
        public List<WalkEvent> Events { get; } = new();
        public List<string> SentText => Sent.Select(b => Encoding.Latin1.GetString(b)).ToList();
        public void Dispose() => Solver.Dispose();
    }

    private Harness NewHarness(string rooms = SplitMaze, bool bindWire = true, string spells = Spells)
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
        // Synchronous post + no dispatcher timers: Start() runs inline in TryBegin
        // and the settle / look timers are driven manually via test seams.
        TeleportMazeSolver solver = new(index, graph, tracker, bfs, walker,
            log: null, useTimer: false, post: a => a());

        Harness h = new()
        {
            Graph = graph, Bfs = bfs, Tracker = tracker,
            Walker = walker, Index = index, Solver = solver,
        };
        walker.SetWireSender(h.Sent.Add);
        walker.SetMazeSolver(solver);
        walker.Event += h.Events.Add;
        if (bindWire) solver.SetWireSender(h.Sent.Add);
        return h;
    }

    private static RoomObservation Asylum(params Direction[] exits)
        => new("Warped Asylum", new HashSet<Direction>(exits));

    private static RoomObservation PaddedCell(params Direction[] exits)
        => new("Padded Cell", new HashSet<Direction>(exits));

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
        h.Solver.FireSettleForTests();

        // Look sweep of 1/31's exits (enum order E, D): peek each neighbour.
        h.Solver.OnRoomObserved(Asylum(Direction.W, Direction.D));  // look E → 1/30
        h.Solver.OnRoomObserved(Asylum(Direction.E, Direction.D));  // look D → 1/10

        // Relocalized to 1/31 → plain route E to 1/30 → the solver drives the step
        // itself: the cardinal move, then a self-look to render the landing.
        Assert.Equal(new RoomKey(1, 31), h.Tracker.State.CurrentRoom!.Key);
        Assert.Contains("e\r", h.SentText);
        Assert.Equal("look\r", h.SentText[^1]);

        // Feed 1/30's self-look landing render, then settle → the step verifies by
        // name + exit-mask and finishes on the goal.
        h.Solver.OnRoomObserved(Asylum(Direction.W, Direction.D));  // 1/30's display
        h.Solver.FireSettleForTests();

        Assert.False(h.Solver.Active);
        Assert.Equal(0, h.Solver.Attempts);
        Assert.Equal(new RoomKey(1, 30), h.Tracker.State.CurrentRoom!.Key);
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    [Fact]
    public void DrivesIntoDeadEndGoal_RecognizesArrivalByName()
    {
        // The goal cell is a dead-end Padded Cell whose 1x2 signature collides
        // with a sibling cell, so the index omits it (non-relocatable). Reaching
        // it must work off the driven step's name + mask check — NOT a look-sweep
        // relocalize, which would fail to identify the cell and walk back out (the
        // "made it to the old man then walked out" report).
        Harness h = NewHarness(DeadEndMaze);
        // A teleport dropped us in 1/11 (Lost); the goal 1/12 hangs plain-south.

        Assert.True(h.Walker.WalkTo(new RoomKey(1, 12)));
        Assert.True(h.Solver.Active);

        // Self-look renders 1/11 (own exits N, S); settle.
        h.Solver.OnRoomObserved(Asylum(Direction.N, Direction.S));
        h.Solver.FireSettleForTests();

        // Look sweep of 1/11 (enum order N, S).
        h.Solver.OnRoomObserved(Asylum(Direction.S, Direction.E));  // look N → 1/10
        h.Solver.OnRoomObserved(PaddedCell(Direction.N));           // look S → 1/12

        // Relocalized to 1/11 → plain route S to the cell → solver drives the step.
        Assert.Equal(new RoomKey(1, 11), h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal("s\r", h.SentText[^2]);
        Assert.Equal("look\r", h.SentText[^1]);
        int sentBeforeArrival = h.Sent.Count;

        // The step lands in the Padded Cell. Its name matches the goal, so the
        // solver finishes — without emitting any further look-sweep or reshuffle.
        h.Solver.OnRoomObserved(PaddedCell(Direction.N));
        h.Solver.FireSettleForTests();

        Assert.False(h.Solver.Active);
        Assert.Equal(0, h.Solver.Attempts);
        Assert.Equal(new RoomKey(1, 12), h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(sentBeforeArrival, h.Sent.Count);   // no walk-back-out
        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
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
        h.Solver.FireSettleForTests();

        // Look sweep of 1/10 (E, D).
        h.Solver.OnRoomObserved(Asylum(Direction.W, Direction.S, Direction.D));  // look E → 1/11
        h.Solver.OnRoomObserved(Asylum(Direction.W, Direction.D));               // look D → 1/30

        // Relocalized to 1/10, no plain route to 1/30 → reshuffle down the cast exit.
        Assert.Equal(new RoomKey(1, 10), h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(1, h.Solver.Attempts);
        Assert.Contains("d\r", h.SentText);

        // The reshuffle teleported us into 1/30 (component B). Settle, then relocalize.
        h.Solver.OnRoomObserved(Asylum(Direction.W, Direction.D));  // 1/30's own display
        h.Solver.FireSettleForTests();

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
        h.Solver.FireSettleForTests();

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
        h.Solver.FireSettleForTests();
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
