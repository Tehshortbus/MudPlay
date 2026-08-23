using System.IO;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// Regression coverage for <see cref="EngineRecoveryGate"/>'s terminal
/// tier-3 failure path, where the aborting engine synchronously detaches
/// the gate mid-call.
/// </summary>
public sealed class EngineRecoveryGateTests : IDisposable
{
    private readonly string _root;

    public EngineRecoveryGateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-recoverygate-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Void",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private (RoomGraphManager Graph, RoomTracker Tracker) NewGraphAndTracker()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return (graph, new RoomTracker(graph));
    }

    // Faithful stand-in for the real engines: AbortFromRecoveryFailure
    // resets the engine, and that reset detaches from the gate (which nulls
    // the gate's _engine). That synchronous re-entrancy is the exact shape
    // that used to make FailTier3 crash.
    private sealed class DetachOnAbortEngine : IRecoverableEngine
    {
        private readonly EngineRecoveryGate _gate;
        public DetachOnAbortEngine(EngineRecoveryGate gate) => _gate = gate;

        public string Name => "FakeEngine";
        public int AbortCount { get; private set; }

        public RoomKey? JourneyOrigin => null;
        public Direction? PeekNextPlannedDirection() => null;
        public IReadOnlyList<Direction> PeekPlannedDirections(int count) => Array.Empty<Direction>();
        public void SendBacktrackMove(Direction direction) { }
        public void PauseForRecovery(string reason) { }
        public void ResumeAfterRecovery(RoomKey recoveredAnchor) { }

        public void AbortFromRecoveryFailure(string detail)
        {
            AbortCount++;
            _gate.Detach();
        }
    }

    // Records the gate's callbacks so the Paradigm resync path can be asserted.
    private sealed class RecordingEngine : IRecoverableEngine
    {
        public string Name => "Rec";
        public List<string> Pauses { get; } = new();
        public List<RoomKey> Resumes { get; } = new();
        public List<Direction> Backtracks { get; } = new();
        public int AbortCount { get; private set; }

        // Lets a test drive the tier-2 "planned direction not available" path.
        public Direction? NextPlanned { get; set; }

        public RoomKey? JourneyOrigin => null;
        public Direction? PeekNextPlannedDirection() => NextPlanned;
        public IReadOnlyList<Direction> PeekPlannedDirections(int count) => Array.Empty<Direction>();
        public void SendBacktrackMove(Direction direction) => Backtracks.Add(direction);
        public void PauseForRecovery(string reason) => Pauses.Add(reason);
        public void ResumeAfterRecovery(RoomKey recoveredAnchor) => Resumes.Add(recoveredAnchor);
        public void AbortFromRecoveryFailure(string detail) => AbortCount++;
    }

    // Two same-named rooms so name+exits is never graph-unique — the Darkwood
    // Forest shape where the tier-3 heuristic can't converge (or converges to
    // the wrong room). 1/1 "Maze" has only a N exit; 1/2 "Maze" only S.
    private const string AmbiguousGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Maze",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Maze",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private (RoomGraphManager Graph, RoomTracker Tracker) NewAmbiguousGraphAndTracker()
    {
        Directory.CreateDirectory(Path.Combine(_root, "maze"));
        File.WriteAllText(Path.Combine(_root, "maze", "Rooms.json"), AmbiguousGraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("maze");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("maze");
        return (graph, new RoomTracker(graph));
    }

    // A pair of name-identical "Fork" twins (1/2, 1/3) with the SAME exit set
    // {N,E} — indistinguishable by name+exits — but WHOSE NEIGHBOURS DIFFER:
    // 1/2's exits lead to Alpha/Beta, 1/3's to Gamma/Delta. A move-free
    // look-sweep of the fork reads those neighbours and breaks the twin without
    // walking a single reverse step. 1/1 "Start" (N→1/2) is the unique room the
    // player predicted-lands the fork from.
    private const string TwinNeighbourGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Fork",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/10", "S": "0", "E": "1/11", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Fork",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/12", "S": "0", "E": "1/13", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 10, "Name": "Alpha",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 11, "Name": "Beta",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 12, "Name": "Gamma",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/3", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 13, "Name": "Delta",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // "Fork" twins {N,S} whose SOUTH neighbours differ (1/2 S→SouthA, 1/3
    // S→SouthB). Here a look-sweep is unavailable (no sweep injected), so the
    // gate must reverse-walk: undo one southbound step and read the room it
    // lands in to break the twin.
    private const string TwinSouthGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Fork",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/1", "S": "1/20", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Fork",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/1", "S": "1/21", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 20, "Name": "SouthA",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 21, "Name": "SouthB",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/3", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Two identically-named, identically-exited "Twin" dead ends whose own
    // single exit (N) leads to two DIFFERENT rooms that are themselves
    // name+exit identical ("Dead", no exits) — so a forward locator walk can
    // take a step (N is usable) but that step teaches it nothing (both
    // targets look the same), and the landing room has no further exits to
    // try. Used to exercise the Ambiguous outcome with a real, non-zero
    // candidate count and step count.
    private const string TwinDeadEndGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Twin",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/10", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Twin",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/11", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 10, "Name": "Dead",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 11, "Name": "Dead",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Three name+exit-identical "Fox" rooms (1/2, 1/3, 1/4) whose north exit
    // ALL lead to the same shared room (Mid) — uninformative, ties every
    // candidate — and whose east exit leads to East1 for 1/2 and 1/3 (twins
    // on east too) but East2 for 1/4. An in-place look-sweep of Fox's own
    // exits (peeking both north and east) reads the real east neighbour as
    // East1 and drops 1/4 with zero movement, leaving {1/2, 1/3}. Within that
    // pair every exit ties (they're full twins), so a forward walk that
    // correctly starts from the intersected pair has no informative
    // direction and takes the first listed one (north). A forward walk that
    // instead re-seeds fresh from all three would see 1/4 diverge on east
    // (two shapes) while north stays uninformative (one shape) and pick east
    // as the falsely "best" splitting exit instead.
    private const string SweepPreserveGraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Fox",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/20", "S": "0", "E": "1/30", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Fox",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/20", "S": "0", "E": "1/30", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Fox",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/20", "S": "0", "E": "1/31", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 20, "Name": "Mid",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 30, "Name": "East1",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 31, "Name": "East2",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Two "Crypt" twins (1/2, 1/3) that are indistinguishable from what a
    // display actually shows: both list only a north exit. Each ALSO has a
    // second exit (1/2's west, 1/3's east), but both are (Hidden) —
    // RoomLocator's own displayed-mask index (built from what "Obvious
    // exits:" would print) excludes them, so both twins share the SAME
    // displayed bucket {N}. Their FULL graph masks differ from each other
    // AND from {N} exactly ({N,W} vs {N,E}) — RoomTracker's own exact
    // (name, full-mask) search finds neither for a bare {N} observation, and
    // its door-tolerant superset fallback finds BOTH (not a 1-of-1), so a
    // fresh {N}-only "Crypt" observation genuinely lands Lost — with
    // LastAcceptedObservation left pointing at that exact, graph-findable
    // display rather than something the tracker also couldn't resolve. A
    // seed built from either twin's full mask instead would exact-miss both
    // displayed buckets and fall through to the superset search, which
    // (wrongly) narrows to just the ONE twin whose hidden exit matches —
    // silent, zero-verification overcommitment. North leads the twins to
    // differently-named dead ends (Foo / Bar), so a real move can break them
    // once the seed is genuinely {1/2, 1/3}.
    private const string HiddenExitCryptGraphJson = """
        [
          { "Map Number": 1, "Room Number": 2, "Name": "Crypt",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/20", "S": "0", "E": "0", "W": "1/22 (Hidden)",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Crypt",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/21", "S": "0", "E": "1/23 (Hidden)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 20, "Name": "Foo",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 21, "Name": "Bar",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 22, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 23, "Name": "Sanctum",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private (RoomGraphManager Graph, RoomTracker Tracker) NewGraphAndTracker(string set, string json)
    {
        Directory.CreateDirectory(Path.Combine(_root, set));
        File.WriteAllText(Path.Combine(_root, set, "Rooms.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet(set);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(set);
        return (graph, new RoomTracker(graph));
    }

    private static RoomObservation Obs(string name, params Direction[] exits)
        => new(name, new HashSet<Direction>(exits));

    // Park the tracker in Suspect at the ambiguous fork 1/2, with 1/2 preserved
    // as its best-guess current room. Predicted-lands the fork from unique 1/1
    // (so CurrentRoom is set to a name-ambiguous room), then feeds an
    // off-graph observation so ReconcileFromConfirmed drops to Suspect while
    // keeping 1/2. This is the exact shape the gate must NOT short-circuit via
    // TryTrustConfirmedTracker — Suspect, not Confirmed — so the tier-3
    // footprint actually runs.
    private static void ParkSuspectAtFork(RoomTracker tracker, params Direction[] forkExits)
    {
        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteMoveSent(Direction.N);
        tracker.NoteRoomObserved(new RoomObservation("Fork", new HashSet<Direction>(forkExits)));
        tracker.NoteRoomObserved(Obs("Nowhere", Direction.W));
    }

    // Lands the tracker at `landing` via a predicted move from `start`
    // (Confirmed), then knocks it OFF Confirmed WITHOUT losing `landing` as
    // RoomTracker.LastAcceptedObservation: send a second move in a direction
    // `landing`'s room doesn't actually have. RoomTracker can't resolve a
    // predicted target for that, so NoteRoomObserved falls back to a fresh
    // graph search on `landing`'s still-ambiguous name+exits (the "Neither
    // predicted nor refused" path) — Suspect, CurrentRoom preserved at
    // `landing`, and `landing` itself recorded as the last accepted
    // observation. Unlike feeding an unrelated off-graph observation to
    // force the same confidence drop (see ParkSuspectAtFork, still used by
    // the reverse-walk tests that don't care what LastAcceptedObservation
    // ends up as), this leaves it pointing at something a forward walk can
    // actually seed from — exactly as if the wire had genuinely kept
    // showing `landing` right up to the point recovery needs it.
    private static void ParkSuspectAt(
        RoomTracker tracker, RoomKey start, Direction toLanding, RoomObservation landing, Direction unresolvableDirection)
    {
        tracker.SetLocated(start);
        tracker.NoteMoveSent(toLanding);
        tracker.NoteRoomObserved(landing);
        tracker.NoteMoveSent(unresolvableDirection);
        tracker.NoteRoomObserved(landing);
    }

    private static RecoveryLookSweep RecordingSweep(out List<string> wire)
    {
        var captured = new List<string>();
        wire = captured;
        var sweep = new RecoveryLookSweep(log: null, useTimer: false);
        sweep.SetWireSender(b => captured.Add(Encoding.Latin1.GetString(b)));
        return sweep;
    }

    [Fact]
    public void NoteSuspectedMismatch_WithTryResyncTrue_PausesAndHoldsTier()
    {
        (RoomGraphManager graph, RoomTracker tracker) = NewGraphAndTracker();
        var gate = new EngineRecoveryGate(graph, tracker) { TryResync = _ => true };
        var engine = new RecordingEngine();
        gate.Attach(engine);

        gate.NoteSuspectedMismatch("drift");

        // Fast-path: paused, awaiting the rm reply, tier NOT advanced to Tier2.
        Assert.Single(engine.Pauses);
        Assert.True(gate.AwaitingAuthoritativeResync);
        Assert.Equal(TierLevel.Tier1, gate.CurrentTier);
        // Steps are held while awaiting.
        Assert.False(gate.MayProceedWithPlannedStep());
    }

    [Fact]
    public void NoteAuthoritativePosition_InGraph_AnchorsAndResumes()
    {
        (RoomGraphManager graph, RoomTracker tracker) = NewGraphAndTracker();
        var gate = new EngineRecoveryGate(graph, tracker) { TryResync = _ => true };
        var engine = new RecordingEngine();
        gate.Attach(engine);
        gate.NoteSuspectedMismatch("drift");   // → awaiting

        gate.NoteAuthoritativePosition(new RoomKey(1, 1));

        Assert.False(gate.AwaitingAuthoritativeResync);
        Assert.Equal(new RoomKey(1, 1), gate.Anchor);
        Assert.Equal(TierLevel.Tier1, gate.CurrentTier);
        Assert.Equal(new RoomKey(1, 1), Assert.Single(engine.Resumes));
        Assert.Equal(0, engine.AbortCount);
    }

    [Fact]
    public void NoteAuthoritativePosition_OutOfGraph_FallsBackToHeuristicBacktrack()
    {
        (RoomGraphManager graph, RoomTracker tracker) = NewGraphAndTracker();
        var gate = new EngineRecoveryGate(graph, tracker) { TryResync = _ => true };
        var engine = new RecordingEngine();
        gate.Attach(engine);                    // anchor seeds null (tracker Unknown)
        gate.NoteSuspectedMismatch("drift");    // → awaiting, paused

        // Reported room isn't in the graph → can't anchor → heuristic fallback.
        // With a null anchor, tier-3 fails immediately and aborts the engine.
        gate.NoteAuthoritativePosition(new RoomKey(9, 999));

        Assert.False(gate.AwaitingAuthoritativeResync);
        Assert.Equal(TierLevel.Tier3, gate.CurrentTier);
        Assert.Equal(1, engine.AbortCount);
    }

    [Fact]
    public void NoteSuspectedMismatch_WithTryResyncFalse_KeepsHeuristicLadder()
    {
        (RoomGraphManager graph, RoomTracker tracker) = NewGraphAndTracker();
        var gate = new EngineRecoveryGate(graph, tracker) { TryResync = _ => false };
        var engine = new RecordingEngine();
        gate.Attach(engine);

        gate.NoteSuspectedMismatch("drift");

        // Stock behaviour untouched: Tier1 → Tier2, no pause, not awaiting.
        Assert.Equal(TierLevel.Tier2, gate.CurrentTier);
        Assert.Empty(engine.Pauses);
        Assert.False(gate.AwaitingAuthoritativeResync);
    }

    [Fact]
    public void OnAuthoritativeResyncFailed_WhenNotAwaiting_IsNoOp()
    {
        (RoomGraphManager graph, RoomTracker tracker) = NewGraphAndTracker();
        var gate = new EngineRecoveryGate(graph, tracker);
        var engine = new RecordingEngine();
        gate.Attach(engine);

        gate.OnAuthoritativeResyncFailed();

        Assert.Empty(engine.Pauses);
        Assert.Equal(0, engine.AbortCount);
        Assert.Equal(TierLevel.Tier1, gate.CurrentTier);
    }

    [Fact]
    public void FailTier3_EngineDetachesDuringAbort_ReportsEngineNameWithoutThrowing()
    {
        (RoomGraphManager graph, RoomTracker tracker) = NewGraphAndTracker();
        var gate = new EngineRecoveryGate(graph, tracker);
        var engine = new DetachOnAbortEngine(gate);

        RecoveryFailedEvent? failed = null;
        gate.RecoveryFailed += e => failed = e;

        // Attach while the tracker is at Unknown (no observation) → no
        // current room → the anchor seeds null.
        gate.Attach(engine);
        Assert.Null(gate.Anchor);

        // Pad the executed-step history past the tier-2 budget so the next
        // suspected mismatch escalates straight to tier 3. With a null
        // anchor, tier 3 fails immediately — the path that used to NRE when
        // FailTier3 read _engine.Name after the abort had already detached.
        for (int i = 0; i < EngineRecoveryGate.Tier2StepBudget; i++)
            gate.NoteEngineStepSent(Direction.N);

        gate.NoteSuspectedMismatch("forced tier-3 with null anchor");

        Assert.Equal(1, engine.AbortCount);
        Assert.NotNull(failed);
        Assert.Equal("FakeEngine", failed!.Value.EngineName);
        Assert.Null(gate.AttachedEngine);
    }

    // Regression: a Confirmed tracker at a name-ambiguous room must NOT trigger
    // the heuristic reverse-walk (which fails → "Lost" dialog). The tier-2
    // step-budget escalation should short-circuit to a re-anchor + resume.
    [Fact]
    public void EscalateOnBudget_ConfirmedTracker_ReanchorsInsteadOfBacktrack()
    {
        (RoomGraphManager graph, RoomTracker tracker) = NewAmbiguousGraphAndTracker();
        var gate = new EngineRecoveryGate(graph, tracker);
        var engine = new RecordingEngine();

        tracker.SetLocated(new RoomKey(1, 1));   // Confirmed at ambiguous 1/1
        gate.Attach(engine);                     // anchor seeds 1/1

        // Fill the executed history past the tier-2 budget so the mismatch
        // escalates straight to tier 3.
        for (int i = 0; i < EngineRecoveryGate.Tier2StepBudget; i++)
            gate.NoteEngineStepSent(Direction.N);

        gate.NoteSuspectedMismatch("budget exceeded with confirmed tracker");

        // Trusted the tracker: resumed at 1/1, no backtrack, no abort, Tier1.
        Assert.Equal(new RoomKey(1, 1), Assert.Single(engine.Resumes));
        Assert.Empty(engine.Backtracks);
        Assert.Equal(0, engine.AbortCount);
        Assert.Equal(TierLevel.Tier1, gate.CurrentTier);
        Assert.Equal(new RoomKey(1, 1), gate.Anchor);
    }

    // Regression for the reported failure: in tier 2 the engine's next planned
    // direction isn't an exit of the observed (Confirmed) room — the exact
    // trigger from the Darkwood Forest "Lost" report. The gate must re-anchor
    // to the confirmed key and resume, not reverse-walk into the "Lost" dialog.
    [Fact]
    public void MayProceed_PlannedDirUnavailable_ConfirmedTracker_ReanchorsNotLost()
    {
        (RoomGraphManager graph, RoomTracker tracker) = NewAmbiguousGraphAndTracker();
        var gate = new EngineRecoveryGate(graph, tracker);
        var engine = new RecordingEngine { NextPlanned = Direction.E };   // 1/1 has no E exit

        tracker.SetLocated(new RoomKey(1, 1));   // Confirmed at 1/1 (exits: N only)
        gate.Attach(engine);                     // anchor 1/1
        gate.NoteSuspectedMismatch("drift");     // Tier1 → Tier2

        bool proceed = gate.MayProceedWithPlannedStep();

        Assert.False(proceed);                   // current stale step held
        Assert.Equal(new RoomKey(1, 1), Assert.Single(engine.Resumes));
        Assert.Empty(engine.Backtracks);
        Assert.Equal(0, engine.AbortCount);
        Assert.Equal(TierLevel.Tier1, gate.CurrentTier);
    }

    // ----- tier-3 footprint orchestration ----------------------------

    // Lit recovery: standing on an ambiguous fork, a move-free look-sweep of the
    // fork's own exits reads its neighbours and breaks the twin outright — no
    // reverse step needed. The tracker's preserved guess (1/2) is overridden by
    // the spatial evidence, which resolves to 1/3.
    [Fact]
    public void Tier3_InPlaceLookSweep_BreaksTwin_WithoutBacktrack()
    {
        (RoomGraphManager graph, RoomTracker tracker) = NewGraphAndTracker("twinnbr", TwinNeighbourGraphJson);
        var gate = new EngineRecoveryGate(graph, tracker);
        var engine = new RecordingEngine();
        RecoveryLookSweep sweep = RecordingSweep(out List<string> wire);

        ParkSuspectAtFork(tracker, Direction.N, Direction.E);
        Assert.Equal(RoomConfidence.Suspect, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 2), tracker.State.CurrentRoom!.Key);

        gate.Attach(engine);                     // anchor seeds 1/2
        gate.SetLookSweepForTests(sweep);
        for (int i = 0; i < EngineRecoveryGate.Tier2StepBudget; i++)
            gate.NoteEngineStepSent(Direction.N);

        gate.NoteSuspectedMismatch("budget exceeded, suspect at ambiguous fork");

        // In-place sweep started immediately — first look already on the wire,
        // no reverse move sent.
        Assert.Equal("look north\r", Assert.Single(wire));
        Assert.Empty(engine.Backtracks);

        // Feed the two peeked neighbours matching the OTHER twin (1/3).
        gate.OnRoomObserved(Obs("Gamma", Direction.S));
        Assert.Equal("look east\r", wire[1]);
        gate.OnRoomObserved(Obs("Delta", Direction.W));

        // Sweep converged the footprint to 1/3 without a single backtrack.
        Assert.Empty(engine.Backtracks);
        Assert.Equal(new RoomKey(1, 3), Assert.Single(engine.Resumes));
        Assert.Equal(0, engine.AbortCount);
        Assert.Equal(TierLevel.Tier1, gate.CurrentTier);
        Assert.Equal(new RoomKey(1, 3), gate.Anchor);
        // Re-confirmed the tracker at the resolved room (off its stale 1/2 guess).
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 3), tracker.State.CurrentRoom!.Key);
    }

    // Lit recovery with no look-sweep available (headless / no wire): the gate
    // falls back to the reverse-walk — undoes one southbound step and reads the
    // room it lands in, which breaks the twin via the temporal footprint.
    [Fact]
    public void Tier3_ReverseStep_ConvergesFootprint_NoSweep()
    {
        (RoomGraphManager graph, RoomTracker tracker) = NewGraphAndTracker("twinsouth", TwinSouthGraphJson);
        var gate = new EngineRecoveryGate(graph, tracker);
        var engine = new RecordingEngine();

        ParkSuspectAtFork(tracker, Direction.N, Direction.S);
        Assert.Equal(RoomConfidence.Suspect, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 2), tracker.State.CurrentRoom!.Key);

        gate.Attach(engine);                     // anchor seeds 1/2; no sweep injected
        for (int i = 0; i < EngineRecoveryGate.Tier2StepBudget; i++)
            gate.NoteEngineStepSent(Direction.N);

        gate.NoteSuspectedMismatch("budget exceeded, no sweep");

        // No sweep → in-place narrowing can't help → reverse the last step
        // (reverse of N = S) and await its landing.
        Assert.Equal(Direction.S, Assert.Single(engine.Backtracks));
        Assert.Empty(engine.Resumes);

        // The reverse-S landing renders SouthA — only twin 1/2 leads there, so
        // the player is now physically at SouthA (1/20). The footprint tracks
        // the CURRENT room, which is the reverse-hop's target, not the fork.
        gate.OnRoomObserved(Obs("SouthA", Direction.N));

        Assert.Equal(Direction.S, Assert.Single(engine.Backtracks));   // just the one reverse
        Assert.Equal(new RoomKey(1, 20), Assert.Single(engine.Resumes));
        Assert.Equal(0, engine.AbortCount);
        Assert.Equal(TierLevel.Tier1, gate.CurrentTier);
        Assert.Equal(new RoomKey(1, 20), gate.Anchor);
        // Re-confirmed the tracker where the player now stands (SouthA).
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 20), tracker.State.CurrentRoom!.Key);
    }

    // Dark recovery: a room too dark to display can't be look-swept (nothing
    // renders), so the gate must skip the sweep entirely and reverse-walk on the
    // fact-of-movement alone — no `look` bytes ever hit the wire.
    [Fact]
    public void Tier3_DarkRoom_SkipsLookSweep_ReverseWalksInstead()
    {
        (RoomGraphManager graph, RoomTracker tracker) = NewGraphAndTracker("twinnbr-dark", TwinNeighbourGraphJson);
        var gate = new EngineRecoveryGate(graph, tracker);
        var engine = new RecordingEngine();
        RecoveryLookSweep sweep = RecordingSweep(out List<string> wire);

        ParkSuspectAtFork(tracker, Direction.N, Direction.E);
        tracker.NoteDarkRoomEntered();           // flag the fork as unseeable
        Assert.True(tracker.IsInDarkRoom);
        Assert.Equal(RoomConfidence.Suspect, tracker.State.Confidence);

        gate.Attach(engine);
        gate.SetLookSweepForTests(sweep);
        for (int i = 0; i < EngineRecoveryGate.Tier2StepBudget; i++)
            gate.NoteEngineStepSent(Direction.N);

        gate.NoteSuspectedMismatch("budget exceeded, dark room");

        // No look-sweep in the dark — went straight to the reverse-walk.
        Assert.Empty(wire);
        Assert.Equal(Direction.S, Assert.Single(engine.Backtracks));
        Assert.Equal(TierLevel.Tier3, gate.CurrentTier);
        Assert.Equal(0, engine.AbortCount);
    }

    // Combat gating (lit): a hostile in the recovery room holds the look-sweep —
    // no peeks go out — until a combat tick reports the room clear, at which
    // point the sweep resumes and the twin is broken.
    [Fact]
    public void Tier3_CombatGate_HoldsLookSweep_UntilClear()
    {
        (RoomGraphManager graph, RoomTracker tracker) = NewGraphAndTracker("twinnbr-combat", TwinNeighbourGraphJson);
        var gate = new EngineRecoveryGate(graph, tracker);
        var engine = new RecordingEngine();
        RecoveryLookSweep sweep = RecordingSweep(out List<string> wire);

        bool hostiles = true;
        gate.SetCombatGate(() => hostiles);

        ParkSuspectAtFork(tracker, Direction.N, Direction.E);
        gate.Attach(engine);
        gate.SetLookSweepForTests(sweep);
        for (int i = 0; i < EngineRecoveryGate.Tier2StepBudget; i++)
            gate.NoteEngineStepSent(Direction.N);

        gate.NoteSuspectedMismatch("budget exceeded, hostiles present");

        // Room is hot — sweep is held, nothing peeked yet.
        Assert.Empty(wire);
        Assert.Equal(TierLevel.Tier3, gate.CurrentTier);

        // A combat tick while still fighting keeps holding.
        gate.OnCombatTick();
        Assert.Empty(wire);

        // Room clears; the next tick releases the held sweep.
        hostiles = false;
        gate.OnCombatTick();
        Assert.Equal("look north\r", Assert.Single(wire));

        // Sweep now resolves the twin normally.
        gate.OnRoomObserved(Obs("Alpha", Direction.S));
        gate.OnRoomObserved(Obs("Beta", Direction.W));

        Assert.Empty(engine.Backtracks);
        Assert.Equal(new RoomKey(1, 2), Assert.Single(engine.Resumes));
        Assert.Equal(0, engine.AbortCount);
        Assert.Equal(TierLevel.Tier1, gate.CurrentTier);
    }

    // ----- forward locator walk (no executed history to reverse) -----

    // The reported bug: a party follower's engine never sends a move while
    // it's following (PartyFollowerMovementGate holds it), so when tier 3
    // kicks in after following ends, _executedSinceAnchor is empty.
    // AdvanceReverseWalk used to fail terminally right there, having sent
    // nothing. It must now hand off to a forward locator walk instead.
    [Fact]
    public void Tier3_NoExecutedHistory_WalksForwardInsteadOfFailing()
    {
        (RoomGraphManager graph, RoomTracker tracker) = NewGraphAndTracker("fwd-nohist", TwinNeighbourGraphJson);
        var gate = new EngineRecoveryGate(graph, tracker);
        var engine = new RecordingEngine();

        ParkSuspectAt(tracker, new RoomKey(1, 1), Direction.N, Obs("Fork", Direction.N, Direction.E), Direction.S);
        gate.Attach(engine);   // anchor seeds the fork; NO NoteEngineStepSent calls

        gate.NoteSuspectedMismatch("resumed after following, no executed history");
        engine.NextPlanned = Direction.S;   // not an exit of the fork {N,E}
        bool proceed = gate.MayProceedWithPlannedStep();

        Assert.False(proceed);
        Assert.Equal(TierLevel.Tier3, gate.CurrentTier);
        // The bug: this used to be empty and the engine aborted having tried
        // nothing. Now the forward walk's own splitting move went out.
        Assert.NotEmpty(engine.Backtracks);
        Assert.Equal(0, engine.AbortCount);
    }

    // Same zero-history trigger, but this time the forward walk's landing
    // converges: the tracker resumes at the recovered room via the engine's
    // normal ResumeAfterRecovery callback, same as a reverse-walk success.
    [Fact]
    public void Tier3_ForwardWalk_Converges_ResumesEngineAtRecoveredRoom()
    {
        (RoomGraphManager graph, RoomTracker tracker) = NewGraphAndTracker("fwd-converge", TwinNeighbourGraphJson);
        var gate = new EngineRecoveryGate(graph, tracker);
        var engine = new RecordingEngine();

        ParkSuspectAt(tracker, new RoomKey(1, 1), Direction.N, Obs("Fork", Direction.N, Direction.E), Direction.S);
        gate.Attach(engine);

        gate.NoteSuspectedMismatch("resumed after following, no executed history");
        engine.NextPlanned = Direction.S;
        gate.MayProceedWithPlannedStep();

        // North splits the twins furthest (Alpha vs Gamma) — that's the move
        // the forward walk sends first.
        Assert.Equal(Direction.N, Assert.Single(engine.Backtracks));

        // Landing renders Alpha — only the 1/2 twin's north neighbour is
        // Alpha, so this converges the walk onto Alpha (1/10), the room the
        // character now physically stands in.
        gate.OnRoomObserved(Obs("Alpha", Direction.S));

        Assert.Equal(new RoomKey(1, 10), Assert.Single(engine.Resumes));
        Assert.Equal(0, engine.AbortCount);
        Assert.Equal(TierLevel.Tier1, gate.CurrentTier);
        Assert.Equal(new RoomKey(1, 10), gate.Anchor);
    }

    // The forward walk can also fail to converge — its own landing leaves
    // more than one candidate standing with nothing left to try. The failure
    // reason must carry the REAL candidate count, not a canned message.
    [Fact]
    public void Tier3_ForwardWalk_Ambiguous_FailsWithTruthfulCandidateCount()
    {
        (RoomGraphManager graph, RoomTracker tracker) = NewGraphAndTracker("fwd-ambiguous", TwinDeadEndGraphJson);
        var gate = new EngineRecoveryGate(graph, tracker);
        var engine = new RecordingEngine();

        ParkSuspectAt(tracker, new RoomKey(1, 1), Direction.N, Obs("Twin", Direction.N), Direction.S);

        gate.Attach(engine);
        gate.NoteSuspectedMismatch("resumed after following, no executed history");
        engine.NextPlanned = Direction.S;   // not an exit of Twin {N}
        gate.MayProceedWithPlannedStep();

        RecoveryFailedEvent? failed = null;
        gate.RecoveryFailed += e => failed = e;

        // Twin's only exit (N) leads two candidates to two name+exit
        // identical "Dead" rooms — the step is taken but teaches nothing,
        // and Dead has no further exits to try.
        Assert.Equal(Direction.N, Assert.Single(engine.Backtracks));
        gate.OnRoomObserved(Obs("Dead"));

        Assert.Equal(1, engine.AbortCount);
        Assert.NotNull(failed);
        Assert.Contains("2", failed!.Value.Detail);
    }

    // LocatorWalk has no dead-reckoning mode — it narrows only by reading a
    // rendered display. A dark landing while the forward walk is active must
    // fail cleanly rather than mis-route into the reverse-walk's blind-step
    // path (which would leave LocatorWalk's own bookkeeping desynced from
    // the shared _tier3 matcher).
    [Fact]
    public void Tier3_ForwardWalk_DarkLanding_FailsExplicitly()
    {
        (RoomGraphManager graph, RoomTracker tracker) = NewGraphAndTracker("fwd-dark", TwinNeighbourGraphJson);
        var gate = new EngineRecoveryGate(graph, tracker);
        var engine = new RecordingEngine();

        ParkSuspectAt(tracker, new RoomKey(1, 1), Direction.N, Obs("Fork", Direction.N, Direction.E), Direction.S);
        gate.Attach(engine);
        gate.NoteSuspectedMismatch("resumed after following, no executed history");
        engine.NextPlanned = Direction.W;   // not an exit of the fork {N,E}
        gate.MayProceedWithPlannedStep();

        // Forward walk's own north move went out.
        Assert.Equal(Direction.N, Assert.Single(engine.Backtracks));

        RecoveryFailedEvent? failed = null;
        gate.RecoveryFailed += e => failed = e;

        // RoomTracker only dead-reckons a dark landing from a Confirmed/
        // Pending anchor (NoteMoveSentCore's policy) — exactly the trust
        // tier-3 recovery itself doesn't have, since EscalateToTier3 only
        // ever reaches the backtrack ladder when the tracker is NOT
        // Confirmed (a Confirmed tracker short-circuits via
        // TryTrustConfirmedTracker instead). Re-confirm the SAME fork room
        // here purely to arm that one dead-reckon — standing in for
        // whatever independently re-confirms position mid-recovery in a
        // live session (e.g. a later Confirmed re-anchor elsewhere) — so the
        // dark landing this asserts on can actually reach the gate.
        tracker.SetLocated(new RoomKey(1, 2));
        tracker.NoteMoveSent(Direction.N);
        tracker.NoteDarkRoomEntered();

        Assert.True(tracker.IsInDarkRoom);
        Assert.Equal(1, engine.AbortCount);
        Assert.NotNull(failed);
        Assert.Contains("dark", failed!.Value.Detail);
    }

    // Pins Finding 1's fix: an in-place look-sweep ahead of the forward-walk
    // handoff narrows _tier3 with zero movement, and that narrowing must
    // survive the handoff rather than being discarded by a plain re-seed.
    [Fact]
    public void Tier3_ForwardWalk_PreservesSweepNarrowing_InsteadOfDiscardingIt()
    {
        (RoomGraphManager graph, RoomTracker tracker) = NewGraphAndTracker("fwd-sweep-preserve", SweepPreserveGraphJson);
        var gate = new EngineRecoveryGate(graph, tracker);
        var engine = new RecordingEngine();
        RecoveryLookSweep sweep = RecordingSweep(out List<string> wire);

        ParkSuspectAt(tracker, new RoomKey(1, 1), Direction.N, Obs("Fox", Direction.N, Direction.E), Direction.S);

        gate.Attach(engine);                     // anchor seeds one of the 3 Fox twins
        gate.SetLookSweepForTests(sweep);
        gate.NoteSuspectedMismatch("resumed after following, no executed history");
        engine.NextPlanned = Direction.S;         // not an exit of Fox {N,E}
        gate.MayProceedWithPlannedStep();

        // In-place sweep of the fork peeks both exits before any move.
        Assert.Equal("look north\r", Assert.Single(wire));
        gate.OnRoomObserved(Obs("Mid"));
        Assert.Equal("look east\r", wire[1]);
        gate.OnRoomObserved(Obs("East1"));

        // The sweep alone narrowed 3 candidates to 2 (the 1/4 twin's east
        // neighbour is East2, not East1) with zero movement. Preserved, the
        // surviving pair ties on every exit (true twins) so the walk just
        // takes the first listed direction, north. Discarded — re-seeding
        // fresh from all 3 — east would falsely look like the best
        // splitting exit, since only there does the (wrongly re-included)
        // third twin diverge.
        Assert.Equal(Direction.N, Assert.Single(engine.Backtracks));
    }

    // Unit-level coverage of the gate's own null-anchor handling: given an
    // engine that IS attached while the tracker is genuinely Lost (a room
    // whose exact (name, full-mask) identity the graph doesn't have, and
    // whose door-tolerant superset search comes back ambiguous rather than
    // 1-of-1 — RoomTracker.LandFromCandidateSearch's zero-candidate branch
    // lands at Lost with CurrentRoom null — Attach then seeds _anchor from
    // that null CurrentRoom, so it's null too), reverse-walk has nothing to
    // walk back to AND nothing to seed a footprint from; the only thing
    // left is RoomTracker's own LastAcceptedObservation — the same display
    // that put it in Lost in the first place, which the tracker's exact
    // search couldn't resolve but RoomLocator's displayed-mask index can.
    // That must still send a move, not declare defeat having tried nothing.
    //
    // NOT a claim that this alone fixes "leave a party, instantly lost":
    // AutoWalkManager and LoopRunner both refuse to Attach in the first
    // place when CurrentRoom is null, so in production this branch is only
    // reached by an engine that attached BEFORE going Lost. Whether an
    // engine should attach anyway on a null CurrentRoom is a separate,
    // out-of-scope question.
    [Fact]
    public void Tier3_GenuinelyLostTracker_WalksForwardFromCachedObservation()
    {
        (RoomGraphManager graph, RoomTracker tracker) = NewGraphAndTracker("fwd-lost", HiddenExitCryptGraphJson);
        var gate = new EngineRecoveryGate(graph, tracker) { TryResync = _ => true };
        var engine = new RecordingEngine();

        // A fresh (Unknown) tracker fed the twins' shared {N}-only display:
        // neither twin's FULL mask is exactly {N} (each hides a second
        // exit), so the exact search finds 0; the superset fallback finds
        // BOTH (not 1-of-1), so neither re-anchors — genuinely Lost.
        tracker.NoteRoomObserved(Obs("Crypt", Direction.N));
        Assert.Equal(RoomConfidence.Lost, tracker.State.Confidence);
        Assert.Null(tracker.State.CurrentRoom);
        Assert.Equal("Crypt", tracker.LastAcceptedObservation?.Name);

        gate.Attach(engine);
        Assert.Null(gate.Anchor);   // seeded from the null CurrentRoom

        gate.NoteSuspectedMismatch("resumed after following, genuinely lost");
        gate.OnAuthoritativeResyncFailed();   // reaches EscalateToTier3 without needing CurrentRoom

        Assert.Equal(TierLevel.Tier3, gate.CurrentTier);
        // The bug: this used to fail on the null-anchor guard with zero
        // moves sent. Now the forward walk picks up from RoomTracker's own
        // record of the last thing it genuinely saw.
        Assert.NotEmpty(engine.Backtracks);
        Assert.Equal(0, engine.AbortCount);
    }

    // The forward walk seeds RoomLocator from RoomTracker's own
    // LastAcceptedObservation — what the wire actually displayed — not a
    // synthesized full graph mask. Each Crypt twin's second exit is
    // (Hidden), never shown in "Obvious exits:", so a display of either
    // twin shows north only. Feeding RoomLocator that genuinely-displayed
    // set finds both twins (honestly ambiguous, since nothing distinguishes
    // them from what's visible) and sends a real move to break them;
    // feeding it a full mask that includes an undiscovered hidden exit
    // would exact-miss RoomLocator's own index, fall through to the
    // superset search, and wrongly commit to a single twin with zero
    // verification.
    [Fact]
    public void Tier3_ForwardWalk_SeedsFromDisplayedExits_NotAFullGraphMask()
    {
        (RoomGraphManager graph, RoomTracker tracker) = NewGraphAndTracker("fwd-displayed-exits", HiddenExitCryptGraphJson);
        var gate = new EngineRecoveryGate(graph, tracker) { TryResync = _ => true };
        var engine = new RecordingEngine();

        // Fresh (Unknown) tracker, same genuinely-Lost shape as above: the
        // {N}-only display matches neither twin's full mask exactly, and
        // the superset fallback finds both, so RoomTracker can't resolve it
        // either — but records it as LastAcceptedObservation regardless.
        tracker.NoteRoomObserved(Obs("Crypt", Direction.N));
        Assert.Null(tracker.State.CurrentRoom);

        gate.Attach(engine);   // CurrentRoom null -> anchor null
        Assert.Null(gate.Anchor);

        gate.NoteSuspectedMismatch("resumed after following, no executed history");
        gate.OnAuthoritativeResyncFailed();

        // Both twins share the displayed exit set {N}, so RoomLocator's
        // exact bucket finds both — genuinely ambiguous from what's
        // visible. North (the only exit the walk even knows about) splits
        // them (Foo vs Bar), so a real move goes out rather than silently
        // committing to one twin.
        Assert.Equal(Direction.N, Assert.Single(engine.Backtracks));
        Assert.Empty(engine.Resumes);
    }

    // A player-typed `look <dir>` peek at any time, independent of
    // recovery, parses through RoomDisplayParser.RoomParsed exactly like a
    // real landing would — but RoomTracker.NoteRoomObserved drops it as a
    // preview (RoomTracker.IsPeekSuppressed's underlying flag) rather than
    // accepting it as our own room, so it never reaches
    // LastAcceptedObservation. A forward walk reading that property is
    // immune by construction: there's nothing for the gate to guess wrong.
    [Fact]
    public void PeekedNeighbour_NeverBecomesForwardWalkSeed()
    {
        (RoomGraphManager graph, RoomTracker tracker) = NewGraphAndTracker("fwd-peek-immune", TwinNeighbourGraphJson);
        var gate = new EngineRecoveryGate(graph, tracker);
        var engine = new RecordingEngine();

        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteMoveSent(Direction.N);
        tracker.NoteRoomObserved(Obs("Fork", Direction.N, Direction.E));   // Confirmed @ Fork
        gate.Attach(engine);

        // A player types `look west` well after the move already landed —
        // no move is pending, so RoomTracker has nothing to match the
        // render against and drops it outright as a peek.
        tracker.NoteLookSent();
        tracker.NoteRoomObserved(Obs("Some Distant Room", Direction.W));
        Assert.Equal("Fork", tracker.LastAcceptedObservation?.Name);   // untouched

        // Knock the tracker off Confirmed without touching
        // LastAcceptedObservation: send a second move in a direction Fork
        // doesn't have and re-observe Fork (RoomTracker's own "Neither
        // predicted nor refused" fallback — see ParkSuspectAt).
        tracker.NoteMoveSent(Direction.S);
        tracker.NoteRoomObserved(Obs("Fork", Direction.N, Direction.E));
        Assert.Equal(RoomConfidence.Suspect, tracker.State.Confidence);

        gate.NoteSuspectedMismatch("resumed after following, no executed history");
        engine.NextPlanned = Direction.W;   // not an exit of the fork {N,E}
        gate.MayProceedWithPlannedStep();

        // Had the peek become the seed, the walk would seed from "Some
        // Distant Room" — absent from the graph, so RoomLocator.Seed finds
        // nothing and no move goes out at all. It seeds from Fork instead
        // and sends the same north-splitting move as the other
        // zero-history tests.
        Assert.Equal(Direction.N, Assert.Single(engine.Backtracks));
    }

    // The regression this round exists to kill. RoomTracker.IsPeekSuppressed()
    // is non-consuming, and NoteRoomObserved deliberately keeps a peek window
    // armed when a pending move's genuine confirming render arrives while a
    // `look` is queued right behind it — a fast typist hits this every time
    // (RoomTracker.cs's ObservationLooksLikePendingMoveOutcome guard, cited
    // against report paradigm-20260813-201720 "move + look too fast"). A
    // gate that gated its OWN cache on IsPeekSuppressed() (rather than
    // reading RoomTracker.LastAcceptedObservation) would have WRONGLY
    // skipped this genuine landing, since the flag reads "armed" for the
    // whole window regardless of which observation arrives inside it.
    [Fact]
    public void GenuineLandingInsideArmedPeekWindow_StillSeedsForwardWalk()
    {
        (RoomGraphManager graph, RoomTracker tracker) = NewGraphAndTracker("fwd-fast-typist", TwinNeighbourGraphJson);
        var gate = new EngineRecoveryGate(graph, tracker);
        var engine = new RecordingEngine();

        tracker.SetLocated(new RoomKey(1, 1));
        gate.Attach(engine);

        tracker.NoteMoveSent(Direction.N);   // Pending, predicted target = Fork (1/2)
        tracker.NoteLookSent();              // fast typist: look queued right behind the move
        Assert.True(tracker.IsPeekSuppressed());

        // The server answers commands in order, so the move's own
        // confirming render arrives first. RoomTracker recognizes it as the
        // pending move's outcome (not the peek) and accepts it, even though
        // the peek window is still armed and unresolved.
        tracker.NoteRoomObserved(Obs("Fork", Direction.N, Direction.E));
        Assert.True(tracker.IsPeekSuppressed());   // still armed — the peek itself hasn't arrived
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 2), tracker.State.CurrentRoom!.Key);
        Assert.Equal("Fork", tracker.LastAcceptedObservation?.Name);

        // Knock off Confirmed the same way the other forward-walk tests do.
        tracker.NoteMoveSent(Direction.S);
        tracker.NoteRoomObserved(Obs("Fork", Direction.N, Direction.E));
        Assert.Equal(RoomConfidence.Suspect, tracker.State.Confidence);

        gate.NoteSuspectedMismatch("resumed after following, no executed history");
        engine.NextPlanned = Direction.W;   // not an exit of the fork {N,E}
        gate.MayProceedWithPlannedStep();

        Assert.Equal(Direction.N, Assert.Single(engine.Backtracks));
    }

    // The sibling terminal branch: _anchor is non-null (survives from an
    // earlier strict 1-of-1) but RoomTracker.State.CurrentRoom has since
    // gone null — "went Lost mid-flight" rather than "never had an
    // anchor". OnGraphReloaded is a clean, realistic way to produce this:
    // it nulls CurrentRoom without a strict Confirmed transition, so
    // nothing refreshes (or clears) the gate's stale anchor. Reverse-walk
    // still can't seed a footprint without a current room; the forward
    // walk only needs the cached observation, same as the null-anchor case.
    [Fact]
    public void Tier3_StaleAnchor_CurrentRoomNowNull_WalksForwardFromCachedObservation()
    {
        (RoomGraphManager graph, RoomTracker tracker) = NewGraphAndTracker("fwd-stale-anchor", TwinNeighbourGraphJson);
        var gate = new EngineRecoveryGate(graph, tracker) { TryResync = _ => true };
        var engine = new RecordingEngine();

        tracker.SetLocated(new RoomKey(1, 1));   // Confirmed at unique Start
        gate.Attach(engine);
        Assert.Equal(new RoomKey(1, 1), gate.Anchor);

        // Active set reloads mid-session: CurrentRoom drops to null, but
        // this isn't a strict-1-of-1 Confirmed transition, so the gate's
        // anchor is never told to update — it stays stale at Start.
        tracker.OnGraphReloaded();
        Assert.Null(tracker.State.CurrentRoom);
        Assert.Equal(new RoomKey(1, 1), gate.Anchor);

        // The next thing the wire shows is the ambiguous Fork — from
        // Unknown (post-reload), RoomTracker lands Suspect with CurrentRoom
        // still null (ambiguous, no prior anchor room to preserve) but
        // records Fork as LastAcceptedObservation regardless.
        tracker.NoteRoomObserved(Obs("Fork", Direction.N, Direction.E));
        Assert.Equal(RoomConfidence.Suspect, tracker.State.Confidence);
        Assert.Null(tracker.State.CurrentRoom);

        gate.NoteSuspectedMismatch("resumed after following, stale anchor");
        gate.OnAuthoritativeResyncFailed();

        Assert.Equal(TierLevel.Tier3, gate.CurrentTier);
        Assert.NotEmpty(engine.Backtracks);
        Assert.Equal(0, engine.AbortCount);
    }
}
