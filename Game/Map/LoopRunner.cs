using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Avalonia.Threading;
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
    private readonly BfsMapper? _bfs;
    private readonly AutoWalkManager? _walker;
    /// <summary>
    /// Path filter used by the runner's BFS calls (rotation +
    /// closest-waypoint pick). When set this is typically
    /// <c>AppServices.Movement</c>; changes to its avoided-rooms list
    /// arrive via <see cref="NotifyAvoidedChanged"/>.
    /// </summary>
    private readonly IRoomFilter? _filter;
    private Action<byte[]>? _wireSender;

    private Loop? _loop;
    private int _index;
    private bool _stepInFlight;
    private bool _awaitingPromptForCommand;
    private RoomKey? _expectedMoveTarget;

    /// <summary>
    /// Waypoint the walker is currently approaching during
    /// <see cref="LoopState.Approaching"/>. Null when not approaching.
    /// </summary>
    private RoomKey? _approachTarget;

    /// <summary>
    /// Set true the first time we begin the circle in a given Start
    /// session so <see cref="LoopEventKind.ReachedFirstWaypoint"/>
    /// only fires once per session (not on every wrap).
    /// </summary>
    private bool _firstWaypointReached;

    /// <summary>
    /// Wall-clock anchor for the current lap. Set on
    /// <see cref="LoopReachedFirstWaypoint"/> and refreshed on every
    /// wrap so <see cref="CurrentLapTime"/> reads correctly.
    /// </summary>
    private DateTimeOffset _lapStartedAt;

    private readonly List<TimeSpan> _lapDurations = new();
    private const int MaxLapHistory = 10;

    /// <summary>
    /// Custom-command delay timer state. <see cref="_delayTimer"/> is
    /// lazily constructed on first delay use; <see cref="_delayRemaining"/>
    /// tracks the time left when the timer is stopped by a pause so
    /// resume continues from where it left off rather than restarting
    /// the full duration.
    /// </summary>
    private DispatcherTimer? _delayTimer;
    private TimeSpan _delayRemaining;
    private long _delayStartTimestamp;

    public LoopState State { get; private set; } = LoopState.Idle;

    public Loop? CurrentLoop => _loop;
    public int CurrentIndex => _index;

    /// <summary>Waypoint the walker is approaching, or null when not in <see cref="LoopState.Approaching"/>.</summary>
    public RoomKey? ApproachTarget => _approachTarget;

    /// <summary>Total steps in the rotated circle. 0 when no loop is active.</summary>
    public int StepCount => _loop?.Steps.Count ?? 0;

    /// <summary>
    /// Time elapsed in the current lap. Zero when not running. Computed
    /// on each read so VM bindings can poll via a periodic tick.
    /// </summary>
    public TimeSpan CurrentLapTime
    {
        get
        {
            if (State != LoopState.Running) return TimeSpan.Zero;
            if (_lapStartedAt == default) return TimeSpan.Zero;
            return DateTimeOffset.UtcNow - _lapStartedAt;
        }
    }

    /// <summary>
    /// Mean of the last <see cref="MaxLapHistory"/> completed laps.
    /// <see cref="TimeSpan.Zero"/> when no lap has completed yet.
    /// </summary>
    public TimeSpan AverageLapTime
    {
        get
        {
            if (_lapDurations.Count == 0) return TimeSpan.Zero;
            long totalTicks = 0;
            foreach (TimeSpan t in _lapDurations) totalTicks += t.Ticks;
            return TimeSpan.FromTicks(totalTicks / _lapDurations.Count);
        }
    }

    /// <summary>Read-only window onto the rolling lap-time history (oldest first).</summary>
    public IReadOnlyList<TimeSpan> LapHistory => _lapDurations;

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
        RoomGraphManager? graph = null, EngineRecoveryGate? recovery = null,
        BfsMapper? bfs = null, AutoWalkManager? walker = null,
        IRoomFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(coordinator);
        _tracker = tracker;
        _coordinator = coordinator;
        _promptScanner = promptScanner;
        _log = log;
        _graph = graph;
        _recovery = recovery;
        _bfs = bfs;
        _walker = walker;
        _filter = filter;

        _tracker.StateChanged += OnTrackerStateChanged;
        _coordinator.PauseStateChanged += OnPauseChanged;
        if (_promptScanner is not null)
            _promptScanner.PromptObserved += OnPromptObserved;
        if (_walker is not null)
            _walker.Event += OnWalkerEvent;
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

        if (State is LoopState.Running or LoopState.Paused or LoopState.Approaching)
            Stop("superseded by new loop");

        _loop = loop;
        _index = 0;
        _stepInFlight = false;
        _awaitingPromptForCommand = false;
        _expectedMoveTarget = null;
        _firstWaypointReached = false;
        _lapDurations.Clear();
        _approachTarget = null;

        Raise(new LoopEvent(LoopEventKind.Started, loop.Name));

        RoomKey? currentKey = _tracker.State.CurrentRoom?.Key;

        // Decision: do we need an approach walk, or can we begin the
        // circle immediately?
        //   - Legacy v1 loop (no UserWaypoints) → no approach available;
        //     trust that the user is at the right starting position.
        //   - Player already at a waypoint → rotate the loop so that
        //     waypoint is first, no approach.
        //   - Player elsewhere AND walker bound AND graph available →
        //     pick the closest waypoint, walker drives the approach.
        //   - Walker missing (unit tests) → begin circle from wherever
        //     they are and let the runner fail-or-recover.

        if (loop.UserWaypoints.Count == 0)
        {
            BeginCircle();
            return true;
        }

        if (currentKey is { } here && loop.UserWaypoints.Any(w => w.Equals(here)))
        {
            RotateLoopTo(here);
            BeginCircle();
            return true;
        }

        if (_walker is null || _bfs is null || currentKey is null)
        {
            BeginCircle();
            return true;
        }

        RoomKey? closest = PickClosestWaypoint(currentKey.Value, loop.UserWaypoints);
        if (closest is null)
        {
            // No reachable waypoint — bail; gate would fail us anyway.
            Raise(new LoopEvent(LoopEventKind.Failed,
                $"no reachable waypoint from {currentKey}"));
            Reset();
            return false;
        }

        _approachTarget = closest;
        State = LoopState.Approaching;
        _log?.Info("LoopRunner",
            $"approach: walking from {currentKey} → {closest} (closest of {loop.UserWaypoints.Count} waypoints)");
        _walker.WalkTo(closest.Value);
        return true;
    }

    /// <summary>
    /// Pick the user-waypoint with the shortest BFS path from
    /// <paramref name="from"/>. Returns null when no waypoint is
    /// reachable (disconnected graph, all waypoints behind avoided
    /// rooms, etc.).
    /// </summary>
    private RoomKey? PickClosestWaypoint(RoomKey from, IReadOnlyList<RoomKey> waypoints)
    {
        if (_bfs is null) return waypoints.Count > 0 ? waypoints[0] : null;
        RoomKey? best = null;
        int bestLen = int.MaxValue;
        foreach (RoomKey w in waypoints)
        {
            if (w.Equals(from)) return w;
            IReadOnlyList<Direction>? path = _bfs.FindPath(from, w, _filter);
            if (path is null) continue;
            if (path.Count < bestLen) { best = w; bestLen = path.Count; }
        }
        return best;
    }

    /// <summary>
    /// Rotate the loop's <see cref="Loop.Steps"/> so the circle begins
    /// at <paramref name="waypoint"/> instead of
    /// <c>UserWaypoints[0]</c>. Re-runs the BFS expansion against the
    /// rotated waypoint order so the closing leg ends back at
    /// <paramref name="waypoint"/>. No-op when UserWaypoints is empty
    /// (legacy v1 loop) or the waypoint isn't in the list.
    /// </summary>
    private void RotateLoopTo(RoomKey waypoint)
    {
        if (_loop is null || _bfs is null) return;
        if (_loop.UserWaypoints.Count == 0) return;

        int k = -1;
        for (int i = 0; i < _loop.UserWaypoints.Count; i++)
        {
            if (_loop.UserWaypoints[i].Equals(waypoint)) { k = i; break; }
        }
        if (k <= 0) return;     // not found, or already at index 0 — no rotation needed

        // Build the rotated waypoint list and re-expand into Steps.
        // Note we mutate the in-memory loop only — the on-disk file
        // stays in its canonical (waypoint-0-first) form.
        var rotated = new List<RoomKey>(_loop.UserWaypoints.Count);
        for (int i = 0; i < _loop.UserWaypoints.Count; i++)
        {
            rotated.Add(_loop.UserWaypoints[(k + i) % _loop.UserWaypoints.Count]);
        }

        var steps = new List<LoopStep>();
        for (int i = 0; i < rotated.Count - 1; i++)
        {
            IReadOnlyList<Direction>? path = _bfs.FindPath(rotated[i], rotated[i + 1], _filter);
            if (path is null) continue;
            foreach (Direction d in path) steps.Add(new MoveLoopStep(d));
        }
        // Closing leg.
        IReadOnlyList<Direction>? closing = _bfs.FindPath(rotated[^1], rotated[0], _filter);
        if (closing is not null) foreach (Direction d in closing) steps.Add(new MoveLoopStep(d));

        _loop.Steps = steps;
        _log?.Info("LoopRunner",
            $"rotated loop '{_loop.Name}' to start at waypoint {waypoint} (index {k})");
    }

    /// <summary>
    /// Common entry into the circle phase — called either immediately
    /// from <see cref="Start"/> (player already at waypoint / legacy
    /// loop) or after walker-driven approach completes. Attaches the
    /// recovery gate, fires
    /// <see cref="LoopEventKind.ReachedFirstWaypoint"/> once per
    /// session, anchors lap timing, and pushes the first step.
    /// </summary>
    private void BeginCircle()
    {
        if (_loop is null) return;

        State = LoopState.Running;
        _recovery?.Attach(this);

        if (!_firstWaypointReached)
        {
            _firstWaypointReached = true;
            _lapStartedAt = DateTimeOffset.UtcNow;
            Raise(new LoopEvent(LoopEventKind.ReachedFirstWaypoint, _loop.Name));
        }

        if (_coordinator.IsPaused)
        {
            State = LoopState.Paused;
            Raise(new LoopEvent(LoopEventKind.Paused, "coordinator paused"));
            return;
        }

        SendNextStep();
    }

    private void OnWalkerEvent(WalkEvent e)
    {
        if (State != LoopState.Approaching) return;
        if (_approachTarget is null) return;

        switch (e.Kind)
        {
            case WalkEventKind.Finished:
                // Walker arrived at the chosen waypoint.
                RoomKey target = _approachTarget.Value;
                _approachTarget = null;
                RotateLoopTo(target);
                BeginCircle();
                break;
            case WalkEventKind.Failed:
                // Walker gave up (tier-3 abort, blocked, no path, etc.).
                Raise(new LoopEvent(LoopEventKind.Failed,
                    $"approach failed: {e.Detail}"));
                Reset();
                break;
        }
    }

    public void Stop(string reason = "user stop")
    {
        if (State == LoopState.Idle) return;
        string? name = _loop?.Name;
        // If we're approaching, stop the walker too. The walker's own
        // Reset on stop detaches the recovery gate, so no gate cleanup
        // is needed on our side for the approach phase.
        if (State == LoopState.Approaching) _walker?.Stop("loop stopped");
        Reset();
        Raise(new LoopEvent(LoopEventKind.Stopped, $"{name}: {reason}"));
    }

    /// <summary>
    /// Avoided-rooms list mutated mid-loop. Stop the current run and
    /// re-Start with the same loop so the new filter applies to every
    /// BFS call (closest-waypoint pick + rotation + walker approach).
    /// The user effectively re-routes the loop without losing the
    /// definition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No-op when the runner is idle. Loops without UserWaypoints
    /// (legacy v1 loaded from disk) can't be re-expanded — those
    /// retain their original cached steps, so this method only
    /// triggers a re-Start when the loop has UserWaypoints to
    /// rotate from.
    /// </para>
    /// <para>
    /// Side effects of the Stop+Start cycle: the lap-history clears,
    /// <see cref="LoopEventKind.ReachedFirstWaypoint"/> fires again
    /// once the new approach (if any) settles. Same Stopped /
    /// Started event sequence the UI already handles for a user
    /// click on Stop + Run.
    /// </para>
    /// </remarks>
    public void NotifyAvoidedChanged()
    {
        if (State == LoopState.Idle) return;
        if (_loop is null) return;
        if (_loop.UserWaypoints.Count == 0) return;

        Loop snapshot = _loop;
        _log?.Info("LoopRunner",
            $"avoid-list changed; re-routing loop '{snapshot.Name}'");
        Stop("avoided-rooms changed; re-routing");
        Start(snapshot);
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
            // Record the just-completed lap's duration into the rolling
            // history (capped at MaxLapHistory) so AverageLapTime stays
            // bounded in memory across long-running sessions.
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeSpan lapTime = now - _lapStartedAt;
            _lapDurations.Add(lapTime);
            if (_lapDurations.Count > MaxLapHistory) _lapDurations.RemoveAt(0);
            _lapStartedAt = now;
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
        byte[] bytes = Encoding.Latin1.GetBytes(step.Command + "\r");
        Write(bytes, $"command '{step.Command}'");

        if (step.DelayMs > 0)
        {
            // Wait the user-specified duration before advancing. The
            // timer pauses + resumes with the coordinator's pause
            // state so a rest-block doesn't burn the delay window.
            _awaitingPromptForCommand = false;
            StartDelay(TimeSpan.FromMilliseconds(step.DelayMs));
        }
        else
        {
            // 0 means "advance on the next prompt" — same contract
            // CommandStep on AutoWalkManager uses.
            _awaitingPromptForCommand = true;
        }
    }

    // ----- custom-command delay timer --------------------------------

    private void StartDelay(TimeSpan duration)
    {
        _delayRemaining = duration;
        StartOrResumeDelayTimer();
    }

    private void StartOrResumeDelayTimer()
    {
        if (_delayRemaining <= TimeSpan.Zero)
        {
            OnDelayElapsed();
            return;
        }
        _delayTimer ??= new DispatcherTimer();
        _delayTimer.Tick -= OnDelayTick;
        _delayTimer.Tick += OnDelayTick;
        _delayTimer.Interval = _delayRemaining;
        _delayStartTimestamp = Stopwatch.GetTimestamp();
        _delayTimer.Start();
    }

    private void PauseDelayTimer()
    {
        if (_delayTimer is null || !_delayTimer.IsEnabled) return;
        _delayTimer.Stop();
        TimeSpan elapsed = Stopwatch.GetElapsedTime(_delayStartTimestamp);
        _delayRemaining -= elapsed;
        if (_delayRemaining < TimeSpan.Zero) _delayRemaining = TimeSpan.Zero;
    }

    private void StopDelayTimer()
    {
        if (_delayTimer is null) return;
        _delayTimer.Stop();
        _delayTimer.Tick -= OnDelayTick;
        _delayRemaining = TimeSpan.Zero;
    }

    private void OnDelayTick(object? sender, EventArgs e) => OnDelayElapsed();

    private void OnDelayElapsed()
    {
        _delayTimer?.Stop();
        _delayRemaining = TimeSpan.Zero;
        if (State != LoopState.Running) return;
        _stepInFlight = false;
        AdvanceStep();
    }

    /// <summary>Test seam — pretend the custom-command delay just elapsed.</summary>
    internal void FireDelayForTests() => OnDelayElapsed();

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
                // A custom-command delay timer in flight pauses with
                // the coordinator — resume picks up from the remaining
                // time, not the full duration.
                PauseDelayTimer();
                Raise(new LoopEvent(LoopEventKind.Paused, "coordinator paused"));
            }
            return;
        }
        if (State == LoopState.Paused)
        {
            State = LoopState.Running;
            Raise(new LoopEvent(LoopEventKind.Resumed, "coordinator resumed"));
            // If a delay was in flight, continue it from the remaining
            // time. Otherwise fall through to SendNextStep.
            if (_delayRemaining > TimeSpan.Zero)
            {
                StartOrResumeDelayTimer();
                return;
            }
            _stepInFlight = false;
            _awaitingPromptForCommand = false;
            SendNextStep();
        }
    }

    private void Reset()
    {
        _recovery?.Detach();
        StopDelayTimer();
        _loop = null;
        _index = 0;
        _stepInFlight = false;
        _awaitingPromptForCommand = false;
        _expectedMoveTarget = null;
        _approachTarget = null;
        _firstWaypointReached = false;
        _lapDurations.Clear();
        _lapStartedAt = default;
        State = LoopState.Idle;
    }

    private void Raise(LoopEvent evt) => Event?.Invoke(evt);
}

public enum LoopState
{
    Idle = 0,
    Running = 1,
    Paused = 2,
    /// <summary>
    /// Walker is driving the player from their current room to the
    /// loop's chosen starting waypoint. Loop runner has nothing on
    /// the wire yet; transitions to <see cref="Running"/> when the
    /// walker fires <c>Finished</c>.
    /// </summary>
    Approaching = 3,
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
    /// <summary>
    /// Fired once per loop session at the moment the runner begins
    /// the circle (either immediately on Start if the player is
    /// already at a waypoint, or after the walker-driven approach
    /// completes). Consumers anchor lap stats, fire <c>@reset</c>
    /// to the party, etc. on this event rather than on
    /// <see cref="Started"/> so the timing reflects the actual loop
    /// start, not the approach walk.
    /// </summary>
    ReachedFirstWaypoint = 8,
}

public readonly record struct LoopEvent(LoopEventKind Kind, string Detail);
