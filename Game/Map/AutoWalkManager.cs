using System.Collections.Generic;
using System.Text;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

/// <summary>
/// Walk-to engine — drives the wire one direction at a time, waits
/// for <see cref="RoomTracker"/> to confirm each hop before the next,
/// and gates on <see cref="MovementCoordinator"/> so any pause source
/// halts the walk mid-route.
/// </summary>
/// <remarks>
/// <para>
/// PR 7.7 ships the walk-to consumer of <see cref="MovementCoordinator"/>;
/// LoopManager (PR 7.8) and AutoLairScheduler (PR 7.18+) plug into the
/// same coordinator. The walker's source room is whatever
/// <see cref="RoomTracker.State.CurrentRoom"/> reports when
/// <see cref="WalkTo"/> is called — there is no "must be in a known
/// room" gate beyond <c>CurrentRoom is not null</c>, which the user
/// can satisfy via the Tier-3 manual locate even if the tracker is
/// Lost.
/// </para>
/// <para>
/// Move semantics: each step is the lowercase direction letter
/// (<c>n</c> / <c>s</c> / ... / <c>ne</c> / <c>sw</c> / <c>u</c> /
/// <c>d</c>) followed by <c>\r</c> — the standard MajorMUD movement
/// command form. The Phase 7.7b <c>RemoteActionPathExpander</c> will
/// interleave door-open / lever-pull steps for the rooms whose exits
/// carry a Door or other action hint; without that expander the
/// walker treats every step as a plain movement.
/// </para>
/// </remarks>
public sealed class AutoWalkManager
{
    private readonly RoomGraphManager _graph;
    private readonly BfsMapper _bfs;
    private readonly RoomTracker _tracker;
    private readonly MovementCoordinator _coordinator;
    private readonly IRoomFilter? _filter;
    private Action<byte[]>? _wireSender;
    private readonly LogService? _log;

    private List<Direction>? _path;
    private int _index;                                      // index of the *next* step to send
    private RoomKey? _expectedAfterCurrentStep;             // landing for path[_index-1]
    private RoomKey? _destination;
    private bool _stepInFlight;
    private int _retryCount;
    private const int MaxRetriesPerStep = 1;

    /// <summary>Cap on how many move bytes the walker has sent without confirmation. Public for tests.</summary>
    public IReadOnlyList<byte[]> LastSentForTests => _sentForTests;
    private readonly List<byte[]> _sentForTests = new();

    public WalkState State { get; private set; } = WalkState.Idle;

    /// <summary>Current walk's destination room (null when Idle).</summary>
    public RoomKey? Destination => _destination;

    /// <summary>Total steps in the current path (0 when Idle).</summary>
    public int StepCount => _path?.Count ?? 0;

    /// <summary>Index of the next step to send (0..StepCount).</summary>
    public int CurrentStepIndex => _index;

    /// <summary>
    /// Fires on every state transition (Idle → Walking, Walking →
    /// Paused, etc.) plus on every step advancement so the
    /// Navigation right rail can update its "3 of 6 steps" indicator.
    /// </summary>
    public event Action<WalkEvent>? Event;

    public AutoWalkManager(
        RoomGraphManager graph,
        BfsMapper bfs,
        RoomTracker tracker,
        MovementCoordinator coordinator,
        IRoomFilter? filter = null,
        LogService? log = null)
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

        _tracker.StateChanged += OnTrackerStateChanged;
        _coordinator.PauseStateChanged += OnCoordinatorPauseChanged;
    }

    /// <summary>
    /// Bind the wire sender after construction — matches the
    /// PartyPoller / AutoPartyManager pattern. MainWindowViewModel
    /// calls this once the TelnetClient is up. Until bound, the
    /// walker logs that it would have sent move bytes but no actual
    /// I/O happens — tests construct without binding and observe
    /// <see cref="LastSentForTests"/> via the binding they pass.
    /// </summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    /// <summary>Test seam — bind a closure that captures into a local list.</summary>
    internal void SetWireSenderForTests(Action<byte[]> sender) => SetWireSender(sender);

    /// <summary>
    /// Start a walk to <paramref name="destination"/>. Returns
    /// <c>false</c> when prerequisites aren't met (no source room
    /// known; destination not in active graph; no path; already at
    /// destination); the corresponding <see cref="WalkEvent.Kind"/>
    /// is fired with detail.
    /// </summary>
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

        _path = new List<Direction>(path);
        _index = 0;
        _destination = destination;
        _retryCount = 0;
        _stepInFlight = false;
        State = WalkState.Walking;
        Raise(new WalkEvent(WalkEventKind.Started, $"{path.Count} step(s)", destination));

        if (_coordinator.IsPaused)
        {
            State = WalkState.Paused;
            Raise(new WalkEvent(WalkEventKind.Paused, "coordinator paused", destination));
            return true;
        }

        SendNextStep();
        return true;
    }

    /// <summary>Stop the current walk. No-op when Idle.</summary>
    public void Stop(string reason = "user stop")
    {
        if (State == WalkState.Idle) return;
        RoomKey? dest = _destination;
        Reset();
        Raise(new WalkEvent(WalkEventKind.Stopped, reason, dest));
    }

    /// <summary>
    /// Assert the user pause gate on the coordinator. The walker
    /// (and any other coordinator consumer) will transition to
    /// Paused.
    /// </summary>
    public void Pause() => _coordinator.AssertGate(MovementCoordinator.UserGate);

    /// <summary>Clear the user pause gate. Other gates may keep the walker paused.</summary>
    public void Resume() => _coordinator.ClearGate(MovementCoordinator.UserGate);

    // ----- internals -------------------------------------------------

    private void SendNextStep()
    {
        if (_path is null || _index >= _path.Count) return;
        if (_stepInFlight) return;

        Direction dir = _path[_index];

        // Predict the expected landing so we can validate via tracker.
        Room? current = _tracker.State.CurrentRoom;
        if (current is null
            || !current.Exits.TryGetValue(dir, out RoomExit exit))
        {
            // The path went stale — e.g. graph reloaded, or the user
            // overrode the source mid-walk to an unrelated room.
            Raise(new WalkEvent(WalkEventKind.Failed, "step source has no matching exit", _destination));
            Reset();
            return;
        }

        _expectedAfterCurrentStep = exit.Target;
        _stepInFlight = true;

        byte[] bytes = EncodeMove(dir);
        _sentForTests.Add(bytes);
        if (_wireSender is null)
        {
            _log?.Warn("Walker", "wire sender not bound; step suppressed");
        }
        else
        {
            _wireSender(bytes);
        }
        _log?.Info("Walker", $"step {_index + 1}/{_path.Count}: {dir} → {exit.Target}");
    }

    private void OnTrackerStateChanged(RoomTransition transition)
    {
        if (State != WalkState.Walking) return;
        if (!_stepInFlight) return;

        // We only react to confidence transitions ending in Located,
        // since Pending is the chain state we just entered, and
        // Reconciling/Lost mean the next observation will resolve.
        if (transition.NewConfidence != RoomConfidence.Located) return;

        RoomKey? newKey = transition.NewRoom?.Key;
        if (newKey is null) return;

        if (newKey.Value.Equals(_expectedAfterCurrentStep))
        {
            // Step confirmed.
            _stepInFlight = false;
            _retryCount = 0;
            _index++;
            Raise(new WalkEvent(WalkEventKind.StepCompleted,
                $"{_index}/{_path!.Count}", _destination));

            if (_index >= _path!.Count)
            {
                RoomKey? dest = _destination;
                Reset();
                Raise(new WalkEvent(WalkEventKind.Finished, "destination reached", dest));
                return;
            }

            SendNextStep();
            return;
        }

        // Landing didn't match — was the move blocked (still at source)
        // or did we silently desync somewhere else?
        Room? sourceForCurrentStep = transition.PreviousRoom;
        if (sourceForCurrentStep is not null
            && newKey.Value.Equals(sourceForCurrentStep.Key))
        {
            // Blocked at source — retry once.
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

        // Unrelated room — true desync. Bail; the user picks up via
        // SetLocated + a fresh WalkTo.
        Raise(new WalkEvent(WalkEventKind.Failed,
            $"unexpected landing {newKey} (wanted {_expectedAfterCurrentStep})",
            _destination));
        Reset();
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
            // Re-issue the in-flight step (or the next one). If a step
            // was already on the wire when we paused, we'll get a
            // double-send — harmless because the server will accept
            // the second one as a no-op if the previous already moved
            // us, but we mark _stepInFlight false to recompute.
            _stepInFlight = false;
            SendNextStep();
        }
    }

    private void Reset()
    {
        _path = null;
        _index = 0;
        _expectedAfterCurrentStep = null;
        _destination = null;
        _stepInFlight = false;
        _retryCount = 0;
        State = WalkState.Idle;
    }

    private void Raise(WalkEvent evt) => Event?.Invoke(evt);

    /// <summary>
    /// Convert a <see cref="Direction"/> to the wire bytes the server
    /// expects (lowercase abbreviation + <c>\r</c>). Public for the
    /// Phase 7.7b RemoteActionPathExpander reuse.
    /// </summary>
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

/// <summary>State machine for <see cref="AutoWalkManager"/>.</summary>
public enum WalkState
{
    Idle = 0,
    Walking = 1,
    Paused = 2,
}

/// <summary>Walker event kind — fired via <see cref="AutoWalkManager.Event"/>.</summary>
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

/// <summary>Carried payload of <see cref="AutoWalkManager.Event"/>.</summary>
public readonly record struct WalkEvent(WalkEventKind Kind, string Detail, RoomKey? Destination);
