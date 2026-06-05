using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class LoopRunnerTests : IDisposable
{
    private readonly string _root;

    public LoopRunnerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-looprunner-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "C",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private sealed class Harness : IDisposable
    {
        public required RoomTracker Tracker { get; init; }
        public required MovementCoordinator Coordinator { get; init; }
        public required LoopRunner Runner { get; init; }
        public List<byte[]> Sent { get; } = new();
        public List<LoopEvent> Events { get; } = new();
        public void Dispose() { }
    }

    private Harness NewHarness()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        MovementCoordinator coord = new();
        LoopRunner runner = new(tracker, coord);
        Harness h = new() { Tracker = tracker, Coordinator = coord, Runner = runner };
        runner.SetWireSender(b => h.Sent.Add(b));
        runner.Event += e => h.Events.Add(e);
        return h;
    }

    private static Loop TwoStepLoop() =>
        new("test", new LoopStep[]
        {
            new MoveLoopStep(Direction.N),
            new MoveLoopStep(Direction.N),
        });

    [Fact]
    public void Start_EmptyLoop_ReturnsFalse()
    {
        Harness h = NewHarness();
        Loop empty = new("empty", Array.Empty<LoopStep>());
        Assert.False(h.Runner.Start(empty));
    }

    [Fact]
    public void Start_SendsFirstStep()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));

        h.Runner.Start(TwoStepLoop());

        Assert.Equal(LoopState.Running, h.Runner.State);
        Assert.Single(h.Sent);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[0]));
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Started);
    }

    [Fact]
    public void WrapsAtEnd_AndFiresRepeatStarted()
    {
        // Loops are circular by definition (schema v2 — IsCircular
        // dropped). On reaching the last step we wrap back to step 0
        // and fire RepeatStarted; the runner never produces a
        // Finished event.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(TwoStepLoop());

        // step 1: N → land at B
        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        // step 2: N → land at C
        h.Tracker.NoteRoomObserved(new RoomObservation("C",
            new HashSet<Direction> { Direction.S }));

        // C → ??? — but the loop is circular so it tries to send step 1
        // again, predicting from C. C has only S, not N. Should fail.
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.RepeatStarted);
    }

    [Fact]
    public void Stop_DuringRun_GoesIdle()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(TwoStepLoop());
        h.Runner.Stop();
        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Stopped);
    }

    [Fact]
    public void CoordinatorPause_DuringRun_HoldsRunner()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Runner.Start(TwoStepLoop());
        int sentBefore = h.Sent.Count;

        h.Coordinator.AssertGate("user");
        Assert.Equal(LoopState.Paused, h.Runner.State);

        h.Tracker.NoteRoomObserved(new RoomObservation("B",
            new HashSet<Direction> { Direction.N, Direction.S }));
        // Confirmation arrived while paused — must not send next step.
        Assert.Equal(sentBefore, h.Sent.Count);
    }

    [Fact]
    public void CommandStep_DelayMsGreaterZero_WaitsForTimer()
    {
        // Schema v2: DelayMs > 0 starts a real timer; the next step
        // doesn't go out until the timer elapses. The test seam
        // FireDelayForTests simulates the tick.
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Loop loop = new("with-cmd", new LoopStep[]
        {
            new CommandLoopStep("dep 100", 500),
            new MoveLoopStep(Direction.N),
        });
        h.Runner.Start(loop);

        // Command sent; move NOT yet sent — delay timer pending.
        Assert.Single(h.Sent);
        Assert.Equal("dep 100\r", Encoding.Latin1.GetString(h.Sent[0]));

        // Simulate the delay elapsing.
        h.Runner.FireDelayForTests();

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));
    }

    [Fact]
    public void CommandStep_DelayMsZero_WaitsForPrompt()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Loop loop = new("with-cmd", new LoopStep[]
        {
            new CommandLoopStep("ask barmaid pie"),
            new MoveLoopStep(Direction.N),
        });
        h.Runner.Start(loop);

        Assert.Single(h.Sent);
        Assert.Equal("ask barmaid pie\r", Encoding.Latin1.GetString(h.Sent[0]));

        h.Runner.FirePromptForTests();
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("n\r", Encoding.Latin1.GetString(h.Sent[1]));
    }

    [Fact]
    public void MissingExit_FailsRun()
    {
        Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 3));  // C has no N exit
        Loop loop = new("bad", new LoopStep[]
        {
            new MoveLoopStep(Direction.N),
        });
        h.Runner.Start(loop);

        Assert.Equal(LoopState.Idle, h.Runner.State);
        Assert.Contains(h.Events, e => e.Kind == LoopEventKind.Failed);
    }
}
