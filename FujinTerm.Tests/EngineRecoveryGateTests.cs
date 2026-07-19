using System.IO;
using System.Text;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

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
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-recoverygate-tests-" + Path.GetRandomFileName());
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
}
