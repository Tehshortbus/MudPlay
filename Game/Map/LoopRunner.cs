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
    private Action? _preMoveHook;

    /// <summary>
    /// (source, dest) → teleport keyword resolver, mirroring the
    /// walker's <see cref="AutoWalkManager.SetTeleportResolver"/>. Lets
    /// the circuit cross a <see cref="RoomExitHint.Teleport"/> exit with
    /// the same keyword the walker would use. Null until wired.
    /// </summary>
    private Func<RoomKey, RoomKey, string?>? _teleportResolver;

    /// <summary>
    /// True when the local character should relay a teleport keyword to
    /// party followers (`.@party &lt;kw&gt;`). Mirrors the walker's
    /// <see cref="AutoWalkManager.SetPartyLeaderCheck"/>. Null until wired.
    /// </summary>
    private Func<bool>? _isLeaderWithFollowers;

    private Loop? _loop;
    private int _index;

    /// <summary>
    /// Runtime expansion of <see cref="_loop"/>'s waypoints into the
    /// flat <see cref="LoopStep"/> sequence the runner executes.
    /// Recomputed in <see cref="Start"/> after the rotation is
    /// committed; rebuilt by <see cref="NotifyAvoidedChanged"/> when
    /// the filter changes. Always non-null while a loop is active.
    /// </summary>
    private List<LoopStep> _expandedSteps = new();
    private bool _stepInFlight;
    private bool _awaitingPromptForCommand;
    private RoomKey? _expectedMoveTarget;

    /// <summary>
    /// True when the runner flipped to <see cref="LoopState.Paused"/>
    /// while still in the approach phase (the walker was driving us
    /// toward the loop's entry waypoint). Tells the resume handler to
    /// transition back to <see cref="LoopState.Approaching"/> instead
    /// of trying to send the loop's first step before the walker has
    /// actually finished.
    /// </summary>
    private bool _pausedFromApproach;

    /// <summary>
    /// Waypoint the walker is currently approaching during
    /// <see cref="LoopState.Approaching"/>. Null when not approaching.
    /// </summary>
    private RoomKey? _approachTarget;

    /// <summary>
    /// Room the rotated circle begins (and ends) at. Set when the
    /// runner picks the entry waypoint — either immediately in
    /// <see cref="Start"/> for player-already-at-waypoint / approach
    /// cases, or after the legacy / no-waypoints branch leaves it null.
    /// Used by the Navigation overlay as the source for rendering the
    /// full cycle so the visible polyline stays anchored to the cycle
    /// itself instead of shifting under the player as they walk.
    /// </summary>
    private RoomKey? _circleStartRoom;

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

    /// <summary>
    /// Loop the user has "loaded" (staged) but not yet started — the
    /// Manage dialog's Load action records it here. Distinct from
    /// <see cref="CurrentLoop"/> (which is only set while a run is live):
    /// a staged loop sits idle until something begins it. The toolbar
    /// Start button reads this to run the staged loop without reopening
    /// the Manage window. Null until the user stages one.
    /// </summary>
    public Loop? StagedLoop { get; private set; }

    /// <summary>
    /// Remember <paramref name="loop"/> as the staged loop (see
    /// <see cref="StagedLoop"/>) without starting movement. Idempotent —
    /// re-staging simply replaces the remembered loop.
    /// </summary>
    public void Stage(Loop loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        StagedLoop = loop;
    }

    /// <summary>Waypoint the walker is approaching, or null when not in <see cref="LoopState.Approaching"/>.</summary>
    public RoomKey? ApproachTarget => _approachTarget;

    /// <summary>
    /// Room the running cycle begins + ends at (the rotation entry).
    /// Stable from the moment the rotation is computed (during
    /// <see cref="Start"/> for v2 loops with UserWaypoints) until the
    /// runner resets. Null for legacy v1 loops where the cycle has no
    /// canonical start anchor.
    /// </summary>
    public RoomKey? CircleStartRoom => _circleStartRoom;

    /// <summary>Total steps in the rotated circle. 0 when no loop is active.</summary>
    public int StepCount => _expandedSteps.Count;

    /// <summary>
    /// Read-only view of the runtime-expanded step sequence. Used by
    /// the CURRENT NAV pane to render per-step rows. Empty between
    /// runs.
    /// </summary>
    public IReadOnlyList<LoopStep> ExpandedSteps => _expandedSteps;

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
        if (_loop is null || _index >= _expandedSteps.Count) return null;
        return _expandedSteps[_index] is MoveLoopStep move ? move.Direction : (Direction?)null;
    }

    public void SendBacktrackMove(Direction direction)
    {
        // Tier-3 backtrack: send a single direction without advancing
        // our own loop index. The tracker still records the move so its
        // FSM stays in sync with the observation it'll receive.
        _tracker.NoteMoveSent(direction);
        byte[] bytes = AutoWalkManager.EncodeMove(direction);
        _preMoveHook?.Invoke();
        Write(bytes, $"tier3 backtrack {direction}");
    }

    public void PauseForRecovery(string reason)
    {
        if (State != LoopState.Running) return;
        _log?.Warn("LoopRunner",
            $"PauseForRecovery: gate took over at step {_index + 1}; reason={reason}");
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
            _log?.Info("LoopRunner",
                $"ResumeAfterRecovery: recovered at expected target {recoveredAnchor}; resuming step {_index + 1}");
            State = LoopState.Running;
            _stepInFlight = false;
            Raise(new LoopEvent(LoopEventKind.Resumed,
                $"recovered at expected target {recoveredAnchor}"));
            AdvanceStep();
            return;
        }

        _log?.Warn("LoopRunner",
            $"ResumeAfterRecovery: desync at step {_index + 1} — recovered at {recoveredAnchor} but expected {_expectedMoveTarget}; failing loop");
        Raise(new LoopEvent(LoopEventKind.Failed,
            $"step {_index + 1} desynced (recovered at {recoveredAnchor}, expected {_expectedMoveTarget})"));
        Reset();
    }

    public void AbortFromRecoveryFailure(string detail)
    {
        _log?.Warn("LoopRunner",
            $"AbortFromRecoveryFailure: loop='{_loop?.Name ?? "?"}' at step {_index + 1}; {detail}");
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
        foreach (LoopStep step in _expandedSteps)
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
    /// Pre-move stealth hook (PR 4.b) — invoked immediately before each
    /// loop move's bytes go out so <c>sn</c> is the last command before
    /// the move and the circuit is walked under sneak. Mirrors
    /// <see cref="AutoWalkManager.SetPreMoveHook"/>; AppServices binds
    /// both to <see cref="Game.Stealth.StealthManager.RequestPreMoveStealth"/>.
    /// </summary>
    public void SetPreMoveHook(Action hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        _preMoveHook = hook;
    }

    /// <summary>
    /// Wire the teleport-keyword resolver so circuit steps can cross
    /// <see cref="RoomExitHint.Teleport"/> exits. Mirrors
    /// <see cref="AutoWalkManager.SetTeleportResolver"/>; AppServices
    /// binds both to the same TBInfo-backed resolver.
    /// </summary>
    public void SetTeleportResolver(Func<RoomKey, RoomKey, string?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _teleportResolver = resolver;
    }

    /// <summary>
    /// Wire the party-leader check so a leading character relays the
    /// teleport keyword to followers before crossing. Mirrors
    /// <see cref="AutoWalkManager.SetPartyLeaderCheck"/>.
    /// </summary>
    public void SetPartyLeaderCheck(Func<bool> isLeaderWithFollowers)
    {
        ArgumentNullException.ThrowIfNull(isLeaderWithFollowers);
        _isLeaderWithFollowers = isLeaderWithFollowers;
    }

    /// <summary>
    /// Start running <paramref name="loop"/>. If a loop is already
    /// running, it is stopped first. Returns false when the loop is
    /// empty.
    /// </summary>
    public bool Start(Loop loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        if (loop.Waypoints.Count < 2)
        {
            _log?.Warn("LoopRunner",
                $"Start refused: loop '{loop.Name}' has {loop.Waypoints.Count} waypoint(s); need ≥2 for a cycle");
            return false;
        }

        if (State is LoopState.Running or LoopState.Paused or LoopState.Approaching)
        {
            _log?.Info("LoopRunner",
                $"Start: superseding active loop '{_loop?.Name ?? "?"}' (state={State}) with '{loop.Name}'");
            Stop("superseded by new loop");
        }
        else
        {
            _log?.Info("LoopRunner",
                $"Start: loop='{loop.Name}' waypoints={loop.Waypoints.Count} from={_tracker.State.CurrentRoom?.Key.ToString() ?? "(unknown)"}");
        }

        _loop = loop;
        _index = 0;
        _stepInFlight = false;
        _awaitingPromptForCommand = false;
        _expectedMoveTarget = null;
        _firstWaypointReached = false;
        _lapDurations.Clear();
        _approachTarget = null;
        _circleStartRoom = null;
        _expandedSteps = new List<LoopStep>();

        RoomKey? currentKey = _tracker.State.CurrentRoom?.Key;

        // Decision: do we need an approach walk, or can we begin the
        // circle immediately?
        //   - Player already at a waypoint → rotate the loop so that
        //     waypoint is first, no approach.
        //   - Player elsewhere AND walker bound AND graph available →
        //     pick the closest waypoint, walker drives the approach,
        //     loop steps are rotated + expanded UP FRONT so the
        //     approach-preview overlay can render the upcoming cycle.
        //   - Walker missing (unit tests) or no graph → expand from
        //     waypoint 0 and let the runner fail-or-recover.

        // Started is raised AFTER each branch commits its state +
        // rotation + expansion + (where applicable) State transition.
        // Subscribers like NavigationViewModel.RefreshLoopOverlays read
        // runner.State / CircleStartRoom / ExpandedSteps in their
        // handler; if we raised before the commit they'd see the prior
        // (Idle) shape and the approach-phase preview overlay would
        // render empty.

        if (currentKey is { } here && loop.Waypoints.Any(w => w.Key.Equals(here)))
        {
            _log?.Info("LoopRunner",
                $"Start branch=at-waypoint: player already at {here}; no approach needed");
            RotateLoopTo(here);
            _circleStartRoom = here;
            ExpandSteps();
            Raise(new LoopEvent(LoopEventKind.Started, loop.Name));
            BeginCircle();
            return true;
        }

        if (_walker is null || _bfs is null || currentKey is null)
        {
            _log?.Info("LoopRunner",
                $"Start branch=no-walker: walker={_walker is not null} bfs={_bfs is not null} currentKey={currentKey?.ToString() ?? "(null)"}; expanding from waypoint 0");
            ExpandSteps();
            Raise(new LoopEvent(LoopEventKind.Started, loop.Name));
            BeginCircle();
            return true;
        }

        RoomKey? closest = PickClosestWaypoint(currentKey.Value, loop.Waypoints);
        if (closest is null)
        {
            // No reachable waypoint — bail; gate would fail us anyway.
            _log?.Warn("LoopRunner",
                $"Start failed: no reachable waypoint from {currentKey} (graph disconnected, all behind avoided rooms, or filter excludes them)");
            Raise(new LoopEvent(LoopEventKind.Failed,
                $"no reachable waypoint from {currentKey}"));
            Reset();
            return false;
        }

        // Rotate + expand UP FRONT — the cycle's entry is committed at
        // the moment we pick the closest waypoint. Doing it here (vs
        // after the walker finishes) means ResolveLoopRoomKeys(closest)
        // produces the correct cycle for the approach-preview overlay,
        // and the eventual hand-off into Running needs no further
        // mutation.
        RotateLoopTo(closest.Value);
        _circleStartRoom = closest;
        _approachTarget  = closest;
        ExpandSteps();
        State = LoopState.Approaching;
        Raise(new LoopEvent(LoopEventKind.Started, loop.Name));
        _log?.Info("LoopRunner",
            $"approach: walking from {currentKey} → {closest} (closest of {loop.Waypoints.Count} waypoints)");
        _walker.WalkTo(closest.Value);
        return true;
    }

    /// <summary>
    /// Pick the user-waypoint with the shortest BFS path from
    /// <paramref name="from"/>. Returns null when no waypoint is
    /// reachable (disconnected graph, all waypoints behind avoided
    /// rooms, etc.).
    /// </summary>
    private RoomKey? PickClosestWaypoint(RoomKey from, IReadOnlyList<LoopWaypoint> waypoints)
    {
        if (_bfs is null) return waypoints.Count > 0 ? waypoints[0].Key : null;
        RoomKey? best = null;
        int bestLen = int.MaxValue;
        foreach (LoopWaypoint w in waypoints)
        {
            RoomKey key = w.Key;
            if (key.Equals(from)) return key;
            IReadOnlyList<Direction>? path = _bfs.FindPath(from, key, _filter);
            if (path is null) continue;
            if (path.Count < bestLen) { best = key; bestLen = path.Count; }
        }
        return best;
    }

    /// <summary>
    /// Rotate the loop's <see cref="Loop.Waypoints"/> so the circle
    /// begins at <paramref name="waypoint"/> instead of
    /// <c>Waypoints[0]</c>. No-op when Waypoints is empty or the
    /// target isn't in the list. The runtime step list is rebuilt
    /// separately by <see cref="ExpandSteps"/>.
    /// </summary>
    private void RotateLoopTo(RoomKey waypoint)
    {
        if (_loop is null) return;
        if (_loop.Waypoints.Count == 0) return;

        int k = -1;
        for (int i = 0; i < _loop.Waypoints.Count; i++)
        {
            if (_loop.Waypoints[i].Key.Equals(waypoint)) { k = i; break; }
        }
        if (k <= 0) return;     // not found, or already at index 0 — no rotation needed

        // Build the rotated waypoint list. We mutate the in-memory
        // loop only — the on-disk file stays in its canonical
        // (waypoint-0-first) form.
        var rotated = new List<LoopWaypoint>(_loop.Waypoints.Count);
        for (int i = 0; i < _loop.Waypoints.Count; i++)
        {
            rotated.Add(_loop.Waypoints[(k + i) % _loop.Waypoints.Count]);
        }
        _loop.Waypoints = rotated;
        _log?.Info("LoopRunner",
            $"rotated loop '{_loop.Name}' to start at waypoint {waypoint} (index {k})");
    }

    /// <summary>
    /// (Re)compute <see cref="_expandedSteps"/> from the loop's
    /// current waypoint order + the active filter. Called after every
    /// rotation and on every avoid-list change.
    /// </summary>
    private void ExpandSteps()
    {
        if (_loop is null || _bfs is null)
        {
            _expandedSteps = new List<LoopStep>();
            return;
        }
        (IReadOnlyList<LoopStep> steps,
         IReadOnlyList<(RoomKey From, RoomKey To)> unreachable)
                = LoopExpander.Expand(_loop.Waypoints, _bfs, _filter);
        _expandedSteps = new List<LoopStep>(steps);
        _log?.Info("LoopRunner",
            $"expand: loop='{_loop.Name}' waypoints={_loop.Waypoints.Count} → {steps.Count} step(s), {unreachable.Count} unreachable segment(s)");
        if (unreachable.Count > 0)
        {
            foreach ((RoomKey from, RoomKey to) in unreachable)
                _log?.Warn("LoopRunner", $"expand unreachable: {from} → {to} (BFS found no path)");
        }
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
        _log?.Info("LoopRunner",
            $"BeginCircle: loop='{_loop.Name}' start={_circleStartRoom?.ToString() ?? "(none)"} steps={_expandedSteps.Count}");

        if (!_firstWaypointReached)
        {
            _firstWaypointReached = true;
            _lapStartedAt = DateTimeOffset.UtcNow;
            Raise(new LoopEvent(LoopEventKind.ReachedFirstWaypoint, _loop.Name));
        }

        if (_coordinator.IsPaused)
        {
            State = LoopState.Paused;
            _log?.Info("LoopRunner", "BeginCircle: coordinator paused on entry; holding before first step");
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
                // Walker arrived at the chosen waypoint. Rotation
                // already happened in Start — just hand off into the
                // circle.
                _log?.Info("LoopRunner",
                    $"approach finished at {_approachTarget}; entering circle");
                _approachTarget = null;
                BeginCircle();
                break;
            case WalkEventKind.Failed:
                // Walker gave up (tier-3 abort, blocked, no path, etc.).
                _log?.Warn("LoopRunner",
                    $"approach to {_approachTarget} failed: {e.Detail}");
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
        _log?.Info("LoopRunner",
            $"Stop: loop='{name ?? "?"}' state={State} reason={reason}");
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
        if (_loop.Waypoints.Count == 0) return;

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
        if (_index >= _expandedSteps.Count)
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

        LoopStep step = _expandedSteps[_index];
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
            _log?.Warn("LoopRunner",
                $"SendMove fail: no exit {step.Direction} from {_tracker.State.CurrentRoom?.Key.ToString() ?? "(unknown)"} on step {_index + 1}/{_expandedSteps.Count}");
            Raise(new LoopEvent(LoopEventKind.Failed,
                $"no exit {step.Direction} from {_tracker.State.CurrentRoom?.Key.ToString() ?? "(unknown)"}"));
            Reset();
            return;
        }

        // Set the landing prediction first so OnTrackerStateChanged
        // confirms the step regardless of HOW we cross the exit (plain
        // cardinal, text command, teleport keyword, or post-action
        // cardinal). _stepInFlight gates the confirmation handler.
        _expectedMoveTarget = exit.Target;
        _stepInFlight = true;

        // Door / KeyLocked: a loop circuit has no door-open FSM (that
        // stays walker-owned). If the latest room observation shows the
        // door already open, cross with the plain cardinal. Otherwise
        // fail loudly rather than sending a cardinal into a closed door
        // and desyncing — the user can re-run once the door is dealt
        // with, or route the loop through the walker's approach phase.
        if (exit.Hint is RoomExitHint.Door or RoomExitHint.KeyLocked)
        {
            if (_tracker.State.OpenDoorDirections is { } openDoors
                && openDoors.Contains(step.Direction))
            {
                EmitCardinal(step.Direction, exit.Target, "door pre-open");
                return;
            }
            FailStep($"closed door {step.Direction} mid-circuit — loops don't open doors (run a walk-to through it first)");
            return;
        }

        // SearchableHidden likewise has no reveal FSM in the circuit.
        if (exit.Hint == RoomExitHint.SearchableHidden)
        {
            FailStep($"hidden exit {step.Direction} mid-circuit — loops don't reveal hidden exits (walk through it first)");
            return;
        }

        // Synchronous special exits (Text / Teleport / same-room
        // MultiActionHidden) share the walker's emission path so both
        // engines cross them identically — the fix that makes a circuit
        // send "borrow skiff" for a Text exit instead of the cardinal.
        SpecialExitSend sync = SpecialExitDispatch.TrySendSynchronous(
            exit, step.Direction, _tracker.State.CurrentRoom,
            _tracker, _recovery,
            emitMove: (b, msg) => { _preMoveHook?.Invoke(); Write(b, msg); },
            writeAux: Write,
            _teleportResolver, _isLeaderWithFollowers,
            out string? syncFail);
        if (sync == SpecialExitSend.Sent) return;
        if (sync == SpecialExitSend.Failed)
        {
            FailStep(syncFail!);
            return;
        }

        // Plain passage — the cardinal.
        EmitCardinal(step.Direction, exit.Target, null);
    }

    /// <summary>
    /// Emit a plain cardinal move for the circuit, notifying the tracker
    /// + recovery gate and firing the pre-move stealth hook. <paramref name="note"/>
    /// annotates the wire reason (e.g. "door pre-open"); null for an
    /// ordinary passage.
    /// </summary>
    private void EmitCardinal(Direction direction, RoomKey target, string? note)
    {
        _tracker.NoteMoveSent(direction);
        _recovery?.NoteEngineStepSent(direction);
        byte[] bytes = AutoWalkManager.EncodeMove(direction);
        _preMoveHook?.Invoke();
        string reason = note is null
            ? $"move {direction} → {target}"
            : $"move {direction} ({note})";
        Write(bytes, reason);
    }

    /// <summary>Fail the active circuit with <paramref name="reason"/> and reset.</summary>
    private void FailStep(string reason)
    {
        _log?.Warn("LoopRunner", $"SendMove fail at step {_index + 1}/{_expandedSteps.Count}: {reason}");
        Raise(new LoopEvent(LoopEventKind.Failed, reason));
        Reset();
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
        if (_loop is null || _index >= _expandedSteps.Count) return;
        if (_expandedSteps[_index] is not MoveLoopStep) return;

        // Suspect / Lost / Unknown are real confidence drops we forward
        // to the recovery gate. Pending is the normal Confirmed →
        // Pending transition that fires synchronously from our own
        // _tracker.NoteMoveSent inside SendMove — escalating on it
        // would spuriously bump every step into Tier2 because the
        // handler runs before the confirmation observation arrives.
        if (t.NewConfidence is RoomConfidence.Suspect
                            or RoomConfidence.Lost
                            or RoomConfidence.Unknown)
        {
            _log?.Info("LoopRunner",
                $"step {_index + 1}: tracker confidence={t.NewConfidence} mid-step; forwarding to recovery gate");
            _recovery?.NoteSuspectedMismatch(
                $"tracker {t.NewConfidence} mid-step {_index + 1}");
            return;
        }

        if (t.NewConfidence != RoomConfidence.Confirmed) return;
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
            _log?.Warn("LoopRunner",
                $"step {_index + 1} blocked at source {key}; expected {_expectedMoveTarget}; failing loop");
            Raise(new LoopEvent(LoopEventKind.Failed,
                $"step {_index + 1} blocked"));
            Reset();
        }
        else
        {
            // Confirmed elsewhere — flag the mismatch to the gate. If
            // tier 2 is happy (1-of-1 anchor, etc.) keep going; if it
            // escalates to tier 3 the gate will pause us.
            _log?.Warn("LoopRunner",
                $"step {_index + 1} landed at {key} (expected {_expectedMoveTarget}); graph data may be stale on this exit");
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
        Raise(new LoopEvent(LoopEventKind.StepCompleted, $"{_index}/{_expandedSteps.Count}"));
        SendNextStep();
    }

    private void OnPauseChanged(bool isPaused)
    {
        if (isPaused)
        {
            if (State == LoopState.Running)
            {
                _log?.Info("LoopRunner",
                    $"coordinator paused at step {_index + 1}/{_expandedSteps.Count}");
                State = LoopState.Paused;
                // A custom-command delay timer in flight pauses with
                // the coordinator — resume picks up from the remaining
                // time, not the full duration.
                PauseDelayTimer();
                Raise(new LoopEvent(LoopEventKind.Paused, "coordinator paused"));
            }
            else if (State == LoopState.Approaching)
            {
                // Approach phase: the walker handles its own pause via
                // the coordinator gate, but the runner state has to
                // flip too — otherwise RunStopLabel keeps reporting
                // "Pause" instead of "Run" (since Approaching means
                // "in flight"), and the resume branch in RunStop
                // (which only fires for State==Paused) is unreachable.
                _log?.Info("LoopRunner",
                    $"coordinator paused during approach to {_approachTarget?.ToString() ?? "(?)"}");
                _pausedFromApproach = true;
                State = LoopState.Paused;
                Raise(new LoopEvent(LoopEventKind.Paused, "coordinator paused (approach)"));
            }
            return;
        }
        if (State == LoopState.Paused && _pausedFromApproach)
        {
            // Walker is still mid-approach (or finished it during the
            // pause window) — clear our local pause flag and put the
            // runner back into Approaching so OnWalkerEvent.Finished
            // still hands off into BeginCircle correctly. Don't send
            // any loop steps; the walker owns the wire until it's
            // done with the approach.
            _log?.Info("LoopRunner", "coordinator resumed during approach");
            _pausedFromApproach = false;
            State = LoopState.Approaching;
            Raise(new LoopEvent(LoopEventKind.Resumed, "coordinator resumed (approach)"));
            return;
        }
        if (State == LoopState.Paused)
        {
            _log?.Info("LoopRunner",
                $"coordinator resumed at step {_index + 1}/{_expandedSteps.Count}");
            State = LoopState.Running;
            Raise(new LoopEvent(LoopEventKind.Resumed, "coordinator resumed"));
            // If a delay was in flight, continue it from the remaining
            // time. Otherwise fall through to SendNextStep.
            if (_delayRemaining > TimeSpan.Zero)
            {
                StartOrResumeDelayTimer();
                return;
            }
            // Overshoot guard: while paused we ignore tracker events,
            // so an in-flight move whose confirmation arrived during
            // the pause window leaves _index stale. Detect that here:
            // if the tracker is now Confirmed at the expected target,
            // the step actually completed — advance the index before
            // SendNextStep so we don't re-send the same direction and
            // walk one extra room (the user-reported "overshoot").
            if (_stepInFlight
                && _expectedMoveTarget is { } expected
                && _tracker.State.Confidence == RoomConfidence.Confirmed
                && _tracker.State.CurrentRoom?.Key.Equals(expected) == true)
            {
                _log?.Info("LoopRunner",
                    $"resume: step {_index + 1} completed during pause (tracker at {expected}); advancing");
                _stepInFlight = false;
                _expectedMoveTarget = null;
                AdvanceStep();
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
        _expandedSteps = new List<LoopStep>();
        _stepInFlight = false;
        _awaitingPromptForCommand = false;
        _expectedMoveTarget = null;
        _approachTarget = null;
        _circleStartRoom = null;
        _firstWaypointReached = false;
        _pausedFromApproach = false;
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
