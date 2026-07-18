using System.IO;
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
}
