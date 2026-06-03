using System.Collections.Generic;
using System.Linq;
using System.Text;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

/// <summary>
/// Walk-to engine — drives the wire one step at a time, waits for
/// the appropriate confirmation (room change for moves; next prompt
/// for command steps), and gates on <see cref="MovementCoordinator"/>
/// so any pause source halts the walk mid-route.
/// </summary>
/// <remarks>
/// <para>
/// PR 7.7 shipped the walk-to base with a direction-only path. PR 7.7b
/// teaches the walker about <see cref="WalkStep"/> — moves AND inline
/// command steps (door opens today; lever pulls / button presses when
/// game data describes them). The path is expanded via
/// <see cref="RemoteActionPathExpander"/> at <see cref="WalkTo"/> time.
/// </para>
/// <para>
/// Confirmation:
/// <list type="bullet">
///   <item><see cref="MoveStep"/> — waits for
///         <see cref="RoomTracker.StateChanged"/> with
///         <c>NewConfidence == Located</c> at the predicted target.
///         Blocked-at-source retries once.</item>
///   <item><see cref="CommandStep"/> — waits for the next
///         <c>WirePromptScanner.PromptObserved</c> firing after the
///         command goes out. No retry; the next move step will detect
///         a stuck door via its own blocked-retry path.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class AutoWalkManager
{
    private readonly RoomGraphManager _graph;
    private readonly BfsMapper _bfs;
    private readonly RoomTracker _tracker;
    private readonly MovementCoordinator _coordinator;
    private readonly IRoomFilter? _filter;
    private readonly WirePromptScanner? _promptScanner;
    private Action<byte[]>? _wireSender;
    private readonly LogService? _log;

    private List<WalkStep>? _path;
    private int _index;                                      // index of the *next* step to send
    private RoomKey? _expectedAfterCurrentMove;
    private RoomKey? _destination;
    private bool _stepInFlight;
    private bool _awaitingPromptForCommand;
    private int _retryCount;
    private const int MaxRetriesPerStep = 1;

    public IReadOnlyList<byte[]> LastSentForTests => _sentForTests;
    private readonly List<byte[]> _sentForTests = new();

    public WalkState State { get; private set; } = WalkState.Idle;

    /// <summary>Current walk's destination room (null when Idle).</summary>
    public RoomKey? Destination => _destination;

    /// <summary>Total steps in the current expanded path (0 when Idle).</summary>
    public int StepCount => _path?.Count ?? 0;

    /// <summary>Index of the next step to send (0..StepCount).</summary>
    public int CurrentStepIndex => _index;

    /// <summary>
    /// Read-only snapshot of the current path — used by the
    /// Navigation right rail to render the step list (with the
    /// current step highlighted and completed ones struck through).
    /// </summary>
    public IReadOnlyList<WalkStep> Steps => _path is null
        ? (IReadOnlyList<WalkStep>)Array.Empty<WalkStep>()
        : _path;

    public event Action<WalkEvent>? Event;

    public AutoWalkManager(
        RoomGraphManager graph,
        BfsMapper bfs,
        RoomTracker tracker,
        MovementCoordinator coordinator,
        IRoomFilter? filter = null,
        LogService? log = null,
        WirePromptScanner? promptScanner = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(coordinator);

        _graph = graph;
        _bfs = bfs;
        _tracker = tracker;
        _coordinator = coordinator;
        _filter = filter;
        _log = log;
        _promptScanner = promptScanner;

        _tracker.StateChanged += OnTrackerStateChanged;
        _coordinator.PauseStateChanged += OnCoordinatorPauseChanged;
        if (_promptScanner is not null)
            _promptScanner.PromptObserved += OnPromptObserved;
    }

    /// <summary>
    /// Bind the wire sender after construction (PartyPoller /
    /// AutoPartyManager pattern). MainWindowViewModel binds this once
    /// the TelnetClient is up.
    /// </summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    internal void SetWireSenderForTests(Action<byte[]> sender) => SetWireSender(sender);

    /// <summary>
    /// Test seam — pretend the wire prompt scanner just fired, so the
    /// pending command step can advance without a real telnet client.
    /// No-op when no command step is in flight.
    /// </summary>
    internal void FirePromptForTests()
    {
        if (_awaitingPromptForCommand) OnPromptObservedCore();
    }

    public bool WalkTo(RoomKey destination)
    {
        if (State is WalkState.Walking or WalkState.Paused)
            Stop(reason: "superseded by new walk");

        Room? source = _tracker.State.CurrentRoom;
        if (source is null)
        {
            Raise(new WalkEvent(WalkEventKind.Failed, "no known source room", destination));
            return false;
        }

        if (_graph.GetRoom(destination) is null)
        {
            Raise(new WalkEvent(WalkEventKind.Failed, "destination not in active graph", destination));
            return false;
        }

        if (source.Key.Equals(destination))
        {
            Raise(new WalkEvent(WalkEventKind.Finished, "already at destination", destination));
            return true;
        }

        IReadOnlyList<Direction>? path = _bfs.FindPath(source.Key, destination, _filter);
        if (path is null || path.Count == 0)
        {
            Raise(new WalkEvent(WalkEventKind.Failed, "no path", destination));
            return false;
        }

        IReadOnlyList<WalkStep> expanded =
            RemoteActionPathExpander.Expand(_graph, source.Key, path);
        if (expanded.Count == 0)
        {
            Raise(new WalkEvent(WalkEventKind.Failed, "path expansion empty", destination));
            return false;
        }

        _path = new List<WalkStep>(expanded);
        _index = 0;
        _destination = destination;
        _retryCount = 0;
        _stepInFlight = false;
        _awaitingPromptForCommand = false;
        State = WalkState.Walking;

        int moveCount = expanded.Count(s => s is MoveStep);
        int actionCount = expanded.Count - moveCount;
        string detail = actionCount > 0
            ? $"{moveCount} move(s), {actionCount} action(s)"
            : $"{moveCount} step(s)";
        Raise(new WalkEvent(WalkEventKind.Started, detail, destination));

        if (_coordinator.IsPaused)
        {
            State = WalkState.Paused;
            Raise(new WalkEvent(WalkEventKind.Paused, "coordinator paused", destination));
            return true;
        }

        SendNextStep();
        return true;
    }

    public void Stop(string reason = "user stop")
    {
        if (State == WalkState.Idle) return;
        RoomKey? dest = _destination;
        Reset();
        Raise(new WalkEvent(WalkEventKind.Stopped, reason, dest));
    }

    public void Pause() => _coordinator.AssertGate(MovementCoordinator.UserGate);
    public void Resume() => _coordinator.ClearGate(MovementCoordinator.UserGate);

    // ----- internals -------------------------------------------------

    private void SendNextStep()
    {
        if (_path is null || _index >= _path.Count) return;
        if (_stepInFlight) return;

        WalkStep step = _path[_index];
        switch (step)
        {
            case MoveStep move:
                SendMoveStep(move);
                break;
            case CommandStep command:
                SendCommandStep(command);
                break;
        }
    }

    private void SendMoveStep(MoveStep step)
    {
        // Predict the expected landing so we can validate via tracker.
        Room? current = _tracker.State.CurrentRoom;
        if (current is null
            || !current.Exits.TryGetValue(step.Direction, out RoomExit exit))
        {
            Raise(new WalkEvent(WalkEventKind.Failed, "step source has no matching exit", _destination));
            Reset();
            return;
        }

        _expectedAfterCurrentMove = exit.Target;
        _stepInFlight = true;

        byte[] bytes = EncodeMove(step.Direction);
        WriteBytes(bytes, $"move {step.Direction} → {exit.Target}");
    }

    private void SendCommandStep(CommandStep step)
    {
        _stepInFlight = true;
        _awaitingPromptForCommand = true;

        byte[] bytes = Encoding.Latin1.GetBytes(step.Command + "\r");
        WriteBytes(bytes, $"command '{step.Command}'");
    }

    private void WriteBytes(byte[] bytes, string reasonForLog)
    {
        _sentForTests.Add(bytes);
        if (_wireSender is null)
            _log?.Warn("Walker", $"wire sender not bound; suppressed: {reasonForLog}");
        else
            _wireSender(bytes);
        _log?.Info("Walker", $"step {_index + 1}/{_path!.Count}: {reasonForLog}");
    }

    private void OnTrackerStateChanged(RoomTransition transition)
    {
        if (State != WalkState.Walking) return;
        if (!_stepInFlight) return;
        if (_path is null || _index >= _path.Count) return;
        if (_path[_index] is not MoveStep) return;

        if (transition.NewConfidence != RoomConfidence.Located) return;

        RoomKey? newKey = transition.NewRoom?.Key;
        if (newKey is null) return;

        if (newKey.Value.Equals(_expectedAfterCurrentMove))
        {
            _stepInFlight = false;
            _retryCount = 0;
            AdvanceStep();
            return;
        }

        Room? sourceForCurrentStep = transition.PreviousRoom;
        if (sourceForCurrentStep is not null
            && newKey.Value.Equals(sourceForCurrentStep.Key))
        {
            if (_retryCount < MaxRetriesPerStep)
            {
                _retryCount++;
                _stepInFlight = false;
                Raise(new WalkEvent(WalkEventKind.Retrying,
                    $"step {_index + 1} blocked; retry {_retryCount}", _destination));
                SendNextStep();
                return;
            }
            Raise(new WalkEvent(WalkEventKind.Failed,
                $"step {_index + 1} blocked after retries", _destination));
            Reset();
            return;
        }

        Raise(new WalkEvent(WalkEventKind.Failed,
            $"unexpected landing {newKey} (wanted {_expectedAfterCurrentMove})",
            _destination));
        Reset();
    }

    private void OnPromptObserved(PromptObservation _) => OnPromptObservedCore();

    private void OnPromptObservedCore()
    {
        if (State != WalkState.Walking) return;
        if (!_awaitingPromptForCommand) return;

        _awaitingPromptForCommand = false;
        _stepInFlight = false;
        AdvanceStep();
    }

    private void AdvanceStep()
    {
        if (_path is null) return;

        _index++;
        Raise(new WalkEvent(WalkEventKind.StepCompleted,
            $"{_index}/{_path.Count}", _destination));

        if (_index >= _path.Count)
        {
            RoomKey? dest = _destination;
            Reset();
            Raise(new WalkEvent(WalkEventKind.Finished, "destination reached", dest));
            return;
        }

        SendNextStep();
    }

    private void OnCoordinatorPauseChanged(bool isPaused)
    {
        if (isPaused)
        {
            if (State == WalkState.Walking)
            {
                State = WalkState.Paused;
                Raise(new WalkEvent(WalkEventKind.Paused, "coordinator paused", _destination));
            }
            return;
        }

        if (State == WalkState.Paused)
        {
            State = WalkState.Walking;
            Raise(new WalkEvent(WalkEventKind.Resumed, "coordinator resumed", _destination));
            _stepInFlight = false;
            _awaitingPromptForCommand = false;
            SendNextStep();
        }
    }

    private void Reset()
    {
        _path = null;
        _index = 0;
        _expectedAfterCurrentMove = null;
        _destination = null;
        _stepInFlight = false;
        _awaitingPromptForCommand = false;
        _retryCount = 0;
        State = WalkState.Idle;
    }

    private void Raise(WalkEvent evt) => Event?.Invoke(evt);

    public static byte[] EncodeMove(Direction dir)
    {
        string cmd = dir switch
        {
            Direction.N  => "n",
            Direction.S  => "s",
            Direction.E  => "e",
            Direction.W  => "w",
            Direction.NE => "ne",
            Direction.NW => "nw",
            Direction.SE => "se",
            Direction.SW => "sw",
            Direction.U  => "u",
            Direction.D  => "d",
            _ => throw new ArgumentOutOfRangeException(nameof(dir), dir, "unknown direction"),
        };
        return Encoding.Latin1.GetBytes(cmd + "\r");
    }
}

public enum WalkState
{
    Idle = 0,
    Walking = 1,
    Paused = 2,
}

public enum WalkEventKind
{
    Started = 0,
    StepCompleted = 1,
    Paused = 2,
    Resumed = 3,
    Retrying = 4,
    Stopped = 5,
    Finished = 6,
    Failed = 7,
}

public readonly record struct WalkEvent(WalkEventKind Kind, string Detail, RoomKey? Destination);
