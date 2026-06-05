using System.Collections.Generic;
using System.Text;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

/// <summary>
/// Executes a saved <see cref="Loop"/> against the wire. Sibling of
/// <see cref="AutoWalkManager"/> — shares the same
/// <see cref="MovementCoordinator"/> for pause gates, the same
/// <see cref="RoomTracker"/> for move confirmation, and the same
/// <see cref="EngineRecoveryGate"/> for tier-1/2/3 location recovery.
/// Operates on <see cref="LoopStep"/>s (which include
/// <see cref="CommandLoopStep.DelayMs"/> pauses the walker doesn't
/// need) and supports circular loops that restart at the top after
/// the last step.
/// </summary>
public sealed class LoopRunner : IRecoverableEngine
{
    private readonly RoomTracker _tracker;
    private readonly MovementCoordinator _coordinator;
    private readonly WirePromptScanner? _promptScanner;
    private readonly LogService? _log;
    private readonly EngineRecoveryGate? _recovery;
    private Action<byte[]>? _wireSender;

    private Loop? _loop;
    private int _index;
    private bool _stepInFlight;
    private bool _awaitingPromptForCommand;
    private RoomKey? _expectedMoveTarget;

    public LoopState State { get; private set; } = LoopState.Idle;

    public Loop? CurrentLoop => _loop;
    public int CurrentIndex => _index;

    private readonly RoomGraphManager? _graph;

    // ----- IRecoverableEngine ----------------------------------------

    public string Name => "LoopRunner";

    public Direction? PeekNextPlannedDirection()
    {
        if (_loop is null || _index >= _loop.Steps.Count) return null;
        return _loop.Steps[_index] is MoveLoopStep move ? move.Direction : (Direction?)null;
    }

    public void SendBacktrackMove(Direction direction)
    {
        // Tier-3 backtrack: send a single direction without advancing
        // our own loop index. The tracker still records the move so its
        // FSM stays in sync with the observation it'll receive.
        _tracker.NoteMoveSent(direction);
        byte[] bytes = AutoWalkManager.EncodeMove(direction);
        Write(bytes, $"tier3 backtrack {direction}");
    }

    public void PauseForRecovery(string reason)
    {
        if (State != LoopState.Running) return;
        State = LoopState.Paused;
        Raise(new LoopEvent(LoopEventKind.Paused, $"recovery: {reason}"));
    }

    public void ResumeAfterRecovery(RoomKey recoveredAnchor)
    {
        if (State != LoopState.Paused) return;
        if (_loop is null) return;

        // Engine policy for loops: if the recovered anchor matches the
        // step's expected target, advance. Otherwise the loop is
        // desynced — fail rather than blindly continuing.
        if (_expectedMoveTarget is { } expected && recoveredAnchor.Equals(expected))
        {
            State = LoopState.Running;
            _stepInFlight = false;
            Raise(new LoopEvent(LoopEventKind.Resumed,
                $"recovered at expected target {recoveredAnchor}"));
            AdvanceStep();
            return;
        }

        Raise(new LoopEvent(LoopEventKind.Failed,
            $"step {_index + 1} desynced (recovered at {recoveredAnchor}, expected {_expectedMoveTarget})"));
        Reset();
    }

    public void AbortFromRecoveryFailure(string detail)
    {
        Raise(new LoopEvent(LoopEventKind.Failed, $"tier3 recovery failed: {detail}"));
        Reset();
    }

    // ----- public surface --------------------------------------------

    /// <summary>
    /// Resolves the active loop's <see cref="MoveLoopStep"/>s into a
    /// list of room keys starting at <paramref name="source"/>. Used
    /// by the Navigation map renderer (loop-path overlay + sequence
    /// numbers). Returns empty when no loop is active.
    /// </summary>
    public IReadOnlyList<RoomKey> ResolveLoopRoomKeys(RoomKey source)
    {
        if (_loop is null || _graph is null) return Array.Empty<RoomKey>();
        var keys = new List<RoomKey> { source };
        RoomKey here = source;
        foreach (LoopStep step in _loop.Steps)
        {
            if (step is not MoveLoopStep move) continue;
            Room? room = _graph.GetRoom(here);
            if (room is null) break;
            if (!room.Exits.TryGetValue(move.Direction, out RoomExit exit)) break;
            here = exit.Target;
            keys.Add(here);
        }
        return keys;
    }

    /// <summary>Bytes sent by the runner — captured for tests when no wire is bound.</summary>
    public IReadOnlyList<byte[]> LastSentForTests => _sent;
    private readonly List<byte[]> _sent = new();

    public event Action<LoopEvent>? Event;

    public LoopRunner(RoomTracker tracker, MovementCoordinator coordinator,
        WirePromptScanner? promptScanner = null, LogService? log = null,
        RoomGraphManager? graph = null, EngineRecoveryGate? recovery = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(coordinator);
        _tracker = tracker;
        _coordinator = coordinator;
        _promptScanner = promptScanner;
        _log = log;
        _graph = graph;
        _recovery = recovery;

        _tracker.StateChanged += OnTrackerStateChanged;
        _coordinator.PauseStateChanged += OnPauseChanged;
        if (_promptScanner is not null)
            _promptScanner.PromptObserved += OnPromptObserved;
    }

    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    /// <summary>
    /// Start running <paramref name="loop"/>. If a loop is already
    /// running, it is stopped first. Returns false when the loop is
    /// empty.
    /// </summary>
    public bool Start(Loop loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        if (loop.Steps.Count == 0) return false;

        if (State is LoopState.Running or LoopState.Paused) Stop("superseded by new loop");

        _loop = loop;
        _index = 0;
        _stepInFlight = false;
        _awaitingPromptForCommand = false;
        _expectedMoveTarget = null;
        State = LoopState.Running;
        _recovery?.Attach(this);
        Raise(new LoopEvent(LoopEventKind.Started, loop.Name));

        if (_coordinator.IsPaused)
        {
            State = LoopState.Paused;
            Raise(new LoopEvent(LoopEventKind.Paused, "coordinator paused"));
            return true;
        }

        SendNextStep();
        return true;
    }

    public void Stop(string reason = "user stop")
    {
        if (State == LoopState.Idle) return;
        string? name = _loop?.Name;
        Reset();
        Raise(new LoopEvent(LoopEventKind.Stopped, $"{name}: {reason}"));
    }

    /// <summary>Test seam — pretend the prompt scanner fired so command steps can advance.</summary>
    internal void FirePromptForTests()
    {
        if (_awaitingPromptForCommand) OnPromptObservedCore();
    }

    // ----- internals -------------------------------------------------

    private void SendNextStep()
    {
        if (_loop is null || State != LoopState.Running) return;
        if (_stepInFlight) return;

        // Tier-3 gate may have escalated; if so don't queue a new step.
        if (_recovery is not null && !_recovery.MayProceedWithPlannedStep()) return;

        // All loops are circular by definition — every lap wraps back
        // to step 0. The runner has no "Finished" end-condition; it
        // runs until the user Stops or the recovery gate aborts it.
        if (_index >= _loop.Steps.Count)
        {
            _index = 0;
            Raise(new LoopEvent(LoopEventKind.RepeatStarted, _loop.Name));
        }

        LoopStep step = _loop.Steps[_index];
        switch (step)
        {
            case MoveLoopStep move:    SendMove(move);    break;
            case CommandLoopStep cmd:  SendCommand(cmd);  break;
        }
    }

    private void SendMove(MoveLoopStep step)
    {
        // Predict the expected landing from the tracker's current room.
        if (_tracker.State.CurrentRoom is not { } current
            || !current.Exits.TryGetValue(step.Direction, out RoomExit exit))
        {
            Raise(new LoopEvent(LoopEventKind.Failed,
                $"no exit {step.Direction} from {_tracker.State.CurrentRoom?.Key.ToString() ?? "(unknown)"}"));
            Reset();
            return;
        }

        _expectedMoveTarget = exit.Target;
        _stepInFlight = true;
        _tracker.NoteMoveSent(step.Direction);
        _recovery?.NoteEngineStepSent(step.Direction);

        byte[] bytes = AutoWalkManager.EncodeMove(step.Direction);
        Write(bytes, $"move {step.Direction} → {exit.Target}");
    }

    private void SendCommand(CommandLoopStep step)
    {
        _stepInFlight = true;
        _awaitingPromptForCommand = step.DelayMs == 0;

        byte[] bytes = Encoding.Latin1.GetBytes(step.Command + "\r");
        Write(bytes, $"command '{step.Command}'");

        if (step.DelayMs > 0)
        {
            // PR 7.16 doesn't ship a real delay timer — the runner
            // advances immediately, matching the "command step with
            // explicit delay" contract documented on CommandLoopStep.
            // Phase 13 wiring will introduce a coroutine-style timer.
            // For now we advance the step on the next dispatcher tick
            // to keep the FSM honest.
            _stepInFlight = false;
            AdvanceStep();
        }
    }

    private void Write(byte[] bytes, string reason)
    {
        _sent.Add(bytes);
        if (_wireSender is null)
            _log?.Warn("LoopRunner", $"wire not bound; suppressed: {reason}");
        else
            _wireSender(bytes);
        _log?.Info("LoopRunner", $"step {_index + 1}: {reason}");
    }

    private void OnTrackerStateChanged(RoomTransition t)
    {
        if (State != LoopState.Running || !_stepInFlight) return;
        if (_loop is null || _index >= _loop.Steps.Count) return;
        if (_loop.Steps[_index] is not MoveLoopStep) return;

        if (t.NewConfidence != RoomConfidence.Confirmed)
        {
            // Engine-level tier-2 mismatch — let the gate decide whether
            // to keep watching or escalate to tier 3. While the gate is
            // in tier 2 the engine keeps executing (next SendNextStep
            // proceeds); on tier-3 escalation the gate will call back
            // through PauseForRecovery + SendBacktrackMove.
            _recovery?.NoteSuspectedMismatch(
                $"tracker {t.NewConfidence} mid-step {_index + 1}");
            return;
        }
        if (t.NewRoom?.Key is not { } key) return;

        if (key.Equals(_expectedMoveTarget))
        {
            _stepInFlight = false;
            AdvanceStep();
        }
        else if (t.PreviousRoom is not null
            && key.Equals(t.PreviousRoom.Key))
        {
            // Blocked at source — fail the loop. The walker has a
            // single-retry policy; for loop runs we prefer to bail
            // and surface than to silently retry forever.
            Raise(new LoopEvent(LoopEventKind.Failed,
                $"step {_index + 1} blocked"));
            Reset();
        }
        else
        {
            // Confirmed elsewhere — flag the mismatch to the gate. If
            // tier 2 is happy (1-of-1 anchor, etc.) keep going; if it
            // escalates to tier 3 the gate will pause us.
            _recovery?.NoteSuspectedMismatch(
                $"step {_index + 1} landed at {key} (expected {_expectedMoveTarget})");
        }
    }

    private void OnPromptObserved(PromptObservation _) => OnPromptObservedCore();

    private void OnPromptObservedCore()
    {
        if (State != LoopState.Running) return;
        if (!_awaitingPromptForCommand) return;

        _awaitingPromptForCommand = false;
        _stepInFlight = false;
        AdvanceStep();
    }

    private void AdvanceStep()
    {
        _index++;
        Raise(new LoopEvent(LoopEventKind.StepCompleted, $"{_index}/{_loop!.Steps.Count}"));
        SendNextStep();
    }

    private void OnPauseChanged(bool isPaused)
    {
        if (isPaused)
        {
            if (State == LoopState.Running)
            {
                State = LoopState.Paused;
                Raise(new LoopEvent(LoopEventKind.Paused, "coordinator paused"));
            }
            return;
        }
        if (State == LoopState.Paused)
        {
            State = LoopState.Running;
            Raise(new LoopEvent(LoopEventKind.Resumed, "coordinator resumed"));
            _stepInFlight = false;
            _awaitingPromptForCommand = false;
            SendNextStep();
        }
    }

    private void Reset()
    {
        _recovery?.Detach();
        _loop = null;
        _index = 0;
        _stepInFlight = false;
        _awaitingPromptForCommand = false;
        _expectedMoveTarget = null;
        State = LoopState.Idle;
    }

    private void Raise(LoopEvent evt) => Event?.Invoke(evt);
}

public enum LoopState
{
    Idle = 0,
    Running = 1,
    Paused = 2,
}

public enum LoopEventKind
{
    Started = 0,
    StepCompleted = 1,
    Paused = 2,
    Resumed = 3,
    RepeatStarted = 4,
    Stopped = 5,
    Failed = 7,
    // 6 (Finished) retired in schema v2 — loops are circular by
    // definition and never end on their own; only Stop / Failed
    // remove them from running state.
}

public readonly record struct LoopEvent(LoopEventKind Kind, string Detail);
