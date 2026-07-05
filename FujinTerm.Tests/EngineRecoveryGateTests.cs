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

        public Direction? PeekNextPlannedDirection() => null;
        public void SendBacktrackMove(Direction direction) { }
        public void PauseForRecovery(string reason) { }
        public void ResumeAfterRecovery(RoomKey recoveredAnchor) { }

        public void AbortFromRecoveryFailure(string detail)
        {
            AbortCount++;
            _gate.Detach();
        }
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
}
