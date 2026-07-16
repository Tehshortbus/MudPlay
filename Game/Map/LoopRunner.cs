using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Avalonia.Threading;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// Executes a saved Loop against the wire. Sibling of AutoWalkManager — shares the
// same MovementCoordinator for pause gates, the same RoomTracker for move
// confirmation, and the same EngineRecoveryGate for tier-1/2/3 location recovery.
// Operates on LoopSteps (which include CommandLoopStep.DelayMs pauses the walker
// doesn't need) and supports circular loops that restart at the top after the
// last step.
public sealed class LoopRunner : IRecoverableEngine
{
    private readonly RoomTracker _tracker;
    private readonly MovementCoordinator _coordinator;
    private readonly WirePromptScanner? _promptScanner;
    private readonly LogService? _log;

    // Marshals a resume-triggered step dispatch onto the next UI tick. The
    // coordinator fires PauseStateChanged synchronously mid server-line burst
    // (a Combat gate clearing on a room re-display); a LATER line in the SAME
    // burst can assert another gate — a party @wait — that must re-pause us
    // before the next move leaves. Posting past the burst lets that @wait land
    // first (see DeferResumeDispatch). Defaults to Dispatcher.UIThread.Post;
    // tests inject a synchronous or manually-drained variant.
    private readonly Action<Action> _postToUi;
    private readonly EngineRecoveryGate? _recovery;
    private readonly BfsMapper? _bfs;
    private readonly AutoWalkManager? _walker;
    // Path filter used by the runner's BFS calls (rotation + closest-waypoint
    // pick). When set this is typically AppServices.Movement; changes to its
    // avoided-rooms list arrive via NotifyAvoidedChanged.
    private readonly IRoomFilter? _filter;
    private Action<byte[]>? _wireSender;
    private Action? _preMoveHook;
    private Action<RoomKey>? _approachLightHook;

    // (source, dest) → teleport keyword resolver, mirroring the walker's
    // AutoWalkManager.SetTeleportResolver. Lets the circuit cross a
    // RoomExitHint.Teleport exit with the same keyword the walker would use. Null
    // until wired.
    private Func<RoomKey, RoomKey, string?>? _teleportResolver;

    // True when the local character should relay a teleport keyword to party
    // followers (`.@party <kw>`). Mirrors the walker's
    // AutoWalkManager.SetPartyLeaderCheck. Null until wired.
    private Func<bool>? _isLeaderWithFollowers;

    // Fired after a leading character crosses a party-splitting CMD teleport so
    // the party engine reforms the dissolved group. Mirrors the walker's
    // AutoWalkManager.SetPartySplitHandler. Null until wired.
    private Action? _onLeaderPartySplit;

    // Door-open enqueuer — the runner calls this when a circuit step crosses a
    // closed Door / KeyLocked exit, mirroring the walker's DoorOpenManager
    // integration so a loop can bash/pick/key its way through instead of failing
    // the lap. Null until wired (unit harnesses leave it unbound and keep the
    // fail-loudly path). _doorStopAll drains the FSM when a run is torn down
    // mid-open; _awaitingDoorOpen gates the tracker handler so the FSM's own
    // bash/pick re-observations don't get mistaken for a landing.
    private Action<Direction, int, bool, int, string, Action<DoorOpenResult>>? _doorEnqueuer;
    private Action? _doorStopAll;
    private bool _awaitingDoorOpen;

    // Hidden-exit reveal enqueuer — mirrors the door integration above for a
    // SearchableHidden exit crossed mid-circuit: fire the shared sea <dir> retry
    // loop and cross from OnHiddenRevealReply once the exit appears, rather than
    // failing the whole lap. Null until wired (unit harnesses leave it unbound and
    // keep the fail-loudly path). _hiddenSearchStopAll drains the FSM on teardown;
    // _awaitingHiddenReveal gates the tracker handler so the sea re-observations
    // of the source room aren't mistaken for a landing.
    private Action<Direction, string, Action<HiddenSearchResult>>? _hiddenSearchEnqueuer;
    private Action? _hiddenSearchStopAll;
    private bool _awaitingHiddenReveal;

    private Loop? _loop;
    private int _index;

    // Runtime expansion of _loop's waypoints into the flat LoopStep sequence the
    // runner executes. Recomputed in Start after the rotation is committed; rebuilt
    // by NotifyAvoidedChanged when the filter changes. Always non-null while a loop
    // is active.
    private List<LoopStep> _expandedSteps = new();
    private bool _stepInFlight;
    private bool _awaitingPromptForCommand;
    private RoomKey? _expectedMoveTarget;

    // True when the runner flipped to LoopState.Paused while still in the approach
    // phase (the walker was driving us toward the loop's entry waypoint). Tells the
    // resume handler to transition back to LoopState.Approaching instead of trying
    // to send the loop's first step before the walker has actually finished.
    private bool _pausedFromApproach;

    // Set when the walker fires Finished for the approach while the runner is still
    // parked in the paused-from-approach window (its own resume handler ran before
    // ours in the coordinator's subscriber list, so it completed the walk and reset
    // to Idle before we could hand off). Buffers the arrival so the resume path
    // enters the circle instead of restoring Approaching and waiting for a Finished
    // that will never re-fire. (Live bug: loop "walks to the first room then just
    // sits there" until a second Run click.)
    private bool _approachFinishedWhilePaused;

    // Bounded auto-recovery counter. When a mid-circuit step blocks at its source
    // room, or the recovery gate hands back a room that isn't the step's expected
    // target, the runner re-determines its position and reroutes onto the nearest
    // loop segment (see EnterRecovery) instead of failing straight to Idle. This
    // caps how many consecutive recoveries we attempt before giving up; it resets
    // to 0 on any forward progress (AdvanceStep) and on a fresh (non-recovery)
    // Start, so a healthy loop always has the full budget.
    private int _recoverAttempts;
    private const int MaxRecoverAttempts = 3;

    // Waypoint the walker is currently approaching during LoopState.Approaching.
    // Null when not approaching.
    private RoomKey? _approachTarget;

    // Room the rotated circle begins (and ends) at. Set when the runner picks the
    // entry waypoint — either immediately in Start for player-already-at-waypoint /
    // approach cases, or after the legacy / no-waypoints branch leaves it null.
    // Used by the Navigation overlay as the source for rendering the full cycle so
    // the visible polyline stays anchored to the cycle itself instead of shifting
    // under the player as they walk.
    private RoomKey? _circleStartRoom;

    // Set true the first time we begin the circle in a given Start session so
    // LoopEventKind.ReachedFirstWaypoint only fires once per session (not on every
    // wrap).
    private bool _firstWaypointReached;

    // One-shot, set by ResumeAfterDetour. An auto-deposit / bank detour fully
    // Stop()s the loop (clearing _firstWaypointReached) then re-Starts it to walk
    // the same circuit — but that is a continuation of the same hunting session,
    // not a new one, so BeginCircle must NOT re-fire ReachedFirstWaypoint (whose
    // side effect is a session-stats reset + a party @reset broadcast). Consumed
    // in BeginCircle, cleared on Reset.
    private bool _suppressFirstWaypointEvent;

    // Wall-clock anchor for the current lap. Set on LoopReachedFirstWaypoint and
    // refreshed on every wrap so CurrentLapTime reads correctly.
    private DateTimeOffset _lapStartedAt;

    private readonly List<TimeSpan> _lapDurations = new();
    private const int MaxLapHistory = 10;

    // Total laps completed this run. Distinct from _lapDurations.Count, which is
    // capped at MaxLapHistory — the UI's lap counter needs the true running total
    // so it keeps climbing past the 10th lap instead of freezing.
    private int _completedLaps;

    // Custom-command delay timer state. _delayTimer is lazily constructed on first
    // delay use; _delayRemaining tracks the time left when the timer is stopped by
    // a pause so resume continues from where it left off rather than restarting the
    // full duration.
    private DispatcherTimer? _delayTimer;
    private TimeSpan _delayRemaining;
    private long _delayStartTimestamp;

    public LoopState State { get; private set; } = LoopState.Idle;

    public Loop? CurrentLoop => _loop;
    public int CurrentIndex => _index;

    // Loop the user has "loaded" (staged) but not yet started — the Manage dialog's
    // Load action records it here. Distinct from CurrentLoop (which is only set
    // while a run is live): a staged loop sits idle until something begins it. The
    // toolbar Start button reads this to run the staged loop without reopening the
    // Manage window. Null until the user stages one.
    public Loop? StagedLoop { get; private set; }

    // Remember loop as the staged loop (see StagedLoop) without starting movement.
    // Idempotent — re-staging simply replaces the remembered loop.
    public void Stage(Loop loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        StagedLoop = loop;
    }

    // Waypoint the walker is approaching, or null when not in LoopState.Approaching.
    public RoomKey? ApproachTarget => _approachTarget;

    // Room the running cycle begins + ends at (the rotation entry). Stable from the
    // moment the rotation is computed (during Start for v2 loops with UserWaypoints)
    // until the runner resets. Null for legacy v1 loops where the cycle has no
    // canonical start anchor.
    public RoomKey? CircleStartRoom => _circleStartRoom;

    // Total steps in the rotated circle. 0 when no loop is active.
    public int StepCount => _expandedSteps.Count;

    // Read-only view of the runtime-expanded step sequence. Used by the CURRENT NAV
    // pane to render per-step rows. Empty between runs.
    public IReadOnlyList<LoopStep> ExpandedSteps => _expandedSteps;

    // Time elapsed in the current lap. Zero when not running. Computed on each read
    // so VM bindings can poll via a periodic tick.
    public TimeSpan CurrentLapTime
    {
        get
        {
            if (State != LoopState.Running) return TimeSpan.Zero;
            if (_lapStartedAt == default) return TimeSpan.Zero;
            return DateTimeOffset.UtcNow - _lapStartedAt;
        }
    }

    // Mean of the last MaxLapHistory completed laps. TimeSpan.Zero when no lap has
    // completed yet.
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

    // Read-only window onto the rolling lap-time history (oldest first).
    public IReadOnlyList<TimeSpan> LapHistory => _lapDurations;

    // Laps completed this run — the true running total (unlike LapHistory.Count,
    // which caps at MaxLapHistory). The lap the walker is currently on is this + 1.
    public int CompletedLaps => _completedLaps;

    private readonly RoomGraphManager? _graph;

    // ----- IRecoverableEngine ----------------------------------------

    public string Name => "LoopRunner";

    // A flee retreats toward the room the circuit began at. Null for legacy v1
    // loops with no canonical circle anchor — flee then inverts the last move.
    public RoomKey? JourneyOrigin => _circleStartRoom;

    public Direction? PeekNextPlannedDirection()
    {
        if (_loop is null || _index >= _expandedSteps.Count) return null;
        return _expandedSteps[_index] is MoveLoopStep move ? move.Direction : (Direction?)null;
    }

    public IReadOnlyList<Direction> PeekPlannedDirections(int count)
    {
        int n = _expandedSteps.Count;
        if (count < 1 || _loop is null || n == 0) return Array.Empty<Direction>();
        var dirs = new List<Direction>(count);
        // Loops are circular — wrap around the circuit to fill the count. Stop at
        // the first command / delay step: a forward flee sends plain cardinals
        // only and can't run a custom-command step mid-escape.
        for (int k = 0; k < n && dirs.Count < count; k++)
        {
            if (_expandedSteps[(_index + k) % n] is not MoveLoopStep move) break;
            dirs.Add(move.Direction);
        }
        return dirs;
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

        // Desync: the gate recovered us to a real room that isn't the step's
        // expected target. Rather than fail to Idle, reroute the loop from where we
        // actually ended up (the gate call is terminal — FinishTier3Success does
        // nothing after this, so detaching + re-planning here is safe).
        _log?.Warn("LoopRunner",
            $"ResumeAfterRecovery: desync at step {_index + 1} — recovered at {recoveredAnchor} but expected {_expectedMoveTarget}; rerouting from re-determined room");
        EnterRecovery($"step {_index + 1} desynced (recovered at {recoveredAnchor})");
    }

    public void AbortFromRecoveryFailure(string detail)
    {
        _log?.Warn("LoopRunner",
            $"AbortFromRecoveryFailure: loop='{_loop?.Name ?? "?"}' at step {_index + 1}; {detail}");
        RaiseAfterReset(new LoopEvent(LoopEventKind.Failed, $"tier3 recovery failed: {detail}"));
    }

    // ----- public surface --------------------------------------------

    // Resolves the active loop's MoveLoopSteps into a list of room keys starting at
    // source. Used by the Navigation map renderer (loop-path overlay + sequence
    // numbers). Returns empty when no loop is active.
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

    // Bytes sent by the runner — captured for tests when no wire is bound.
    public IReadOnlyList<byte[]> LastSentForTests => _sent;
    private readonly List<byte[]> _sent = new();

    public event Action<LoopEvent>? Event;

    public LoopRunner(RoomTracker tracker, MovementCoordinator coordinator,
        WirePromptScanner? promptScanner = null, LogService? log = null,
        RoomGraphManager? graph = null, EngineRecoveryGate? recovery = null,
        BfsMapper? bfs = null, AutoWalkManager? walker = null,
        IRoomFilter? filter = null, Action<Action>? postToUi = null)
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
        _postToUi = postToUi ?? (a => Dispatcher.UIThread.Post(a));

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

    // Pre-move stealth hook — invoked immediately before each loop move's bytes go
    // out so sn is the last command before the move and the circuit is walked under
    // sneak. Mirrors AutoWalkManager.SetPreMoveHook; AppServices binds both to
    // Game.Stealth.StealthManager.RequestPreMoveStealth.
    public void SetPreMoveHook(Action hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        _preMoveHook = hook;
    }

    // Predictive auto-light hook — invoked the instant a circuit step commits, with
    // the room about to be entered, before any crossing bytes go out. Mirrors
    // AutoWalkManager.SetApproachLightHook; AppServices binds both to
    // AutoLightProvisioner.OnApproachingRoom so a loop lap lights a dark room ahead
    // of the step exactly like a walk-to does.
    public void SetApproachLightHook(Action<RoomKey> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        _approachLightHook = hook;
    }

    // Wire the teleport-keyword resolver so circuit steps can cross
    // RoomExitHint.Teleport exits. Mirrors AutoWalkManager.SetTeleportResolver;
    // AppServices binds both to the same TBInfo-backed resolver.
    public void SetTeleportResolver(Func<RoomKey, RoomKey, string?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _teleportResolver = resolver;
    }

    // Wire the party-leader check so a leading character relays the teleport keyword
    // to followers before crossing. Mirrors AutoWalkManager.SetPartyLeaderCheck.
    public void SetPartyLeaderCheck(Func<bool> isLeaderWithFollowers)
    {
        ArgumentNullException.ThrowIfNull(isLeaderWithFollowers);
        _isLeaderWithFollowers = isLeaderWithFollowers;
    }

    // Wire the party-split-teleport handler so a leading character reforms the
    // party after a party-splitting CMD teleport. Mirrors
    // AutoWalkManager.SetPartySplitHandler.
    public void SetPartySplitHandler(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _onLeaderPartySplit = handler;
    }

    // Door-open enqueuer — mirrors AutoWalkManager.SetDoorEnqueuer. AppServices
    // binds both engines to the same DoorOpenManager.Enqueue so a loop crosses a
    // closed door with the same bash / pick / key flow the walker uses.
    public void SetDoorEnqueuer(Action<Direction, int, bool, int, string, Action<DoorOpenResult>> enqueuer)
    {
        ArgumentNullException.ThrowIfNull(enqueuer);
        _doorEnqueuer = enqueuer;
    }

    // Door-FSM teardown — mirrors AutoWalkManager.SetDoorStopper. Called from
    // Reset / recovery when a loop is superseded mid-door-FSM so a stale queued
    // request can't fire a stray verb in a room we've since left.
    public void SetDoorStopper(Action stopAll)
    {
        ArgumentNullException.ThrowIfNull(stopAll);
        _doorStopAll = stopAll;
    }

    // Hidden-exit reveal enqueuer — mirrors AutoWalkManager.SetHiddenSearchEnqueuer.
    // MainWindowVM binds both engines to the same HiddenExitRevealManager so a loop
    // uncovers a SearchableHidden exit with the same sea <dir> retry loop the walker
    // uses instead of failing the lap.
    public void SetHiddenSearchEnqueuer(Action<Direction, string, Action<HiddenSearchResult>> enqueuer)
    {
        ArgumentNullException.ThrowIfNull(enqueuer);
        _hiddenSearchEnqueuer = enqueuer;
    }

    // Hidden-search teardown — mirrors AutoWalkManager.SetHiddenSearchStopper.
    // Same stale-state cleanup rationale as SetDoorStopper.
    public void SetHiddenSearchStopper(Action stopAll)
    {
        ArgumentNullException.ThrowIfNull(stopAll);
        _hiddenSearchStopAll = stopAll;
    }

    // Start running loop. If a loop is already running, it is stopped first. Returns
    // false when the loop is empty.
    public bool Start(Loop loop) => StartInternal(loop, isRecovery: false);

    // Resume a loop after an auto-deposit / bank detour that Stop()ed it for its
    // own walk. Re-plans from the current room exactly like a fresh Start, but
    // suppresses the one-shot ReachedFirstWaypoint event — the session began at
    // the user's original Start, so the session-stats reset and party @reset
    // broadcast wired to that event must not re-fire on a mid-session detour.
    public bool ResumeAfterDetour(Loop loop) => StartInternal(loop, isRecovery: false, suppressFirstWaypointEvent: true);

    // Shared engine for both a fresh user Start and an auto-recovery reroute. On a
    // recovery reroute (isRecovery) we deliberately keep the session-scoped state —
    // the bounded _recoverAttempts budget and the lap history / first-waypoint flag
    // — so the reroute continues the same lap instead of re-arming ReachedFirstWaypoint
    // (which would re-fire the party @reset side effect on every recovery). EnterRecovery
    // has already detached the gate + cleared the in-flight step by the time we land here.
    private bool StartInternal(Loop loop, bool isRecovery, bool suppressFirstWaypointEvent = false)
    {
        ArgumentNullException.ThrowIfNull(loop);
        if (loop.Waypoints.Count < 2)
        {
            _log?.Warn("LoopRunner",
                $"Start refused: loop '{loop.Name}' has {loop.Waypoints.Count} waypoint(s); need ≥2 for a cycle");
            return false;
        }

        if (isRecovery)
        {
            _log?.Info("LoopRunner",
                $"recovery reroute: re-planning loop '{loop.Name}' from {_tracker.State.CurrentRoom?.Key.ToString() ?? "(unknown)"}");
        }
        else
        {
            _recoverAttempts = 0;
            if (State is LoopState.Running or LoopState.Paused
                       or LoopState.Approaching or LoopState.Recovering)
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
        }

        _loop = loop;
        _index = 0;
        _stepInFlight = false;
        _awaitingPromptForCommand = false;
        _expectedMoveTarget = null;
        _approachTarget = null;
        _circleStartRoom = null;
        _expandedSteps = new List<LoopStep>();
        if (!isRecovery)
        {
            _firstWaypointReached = false;
            _lapDurations.Clear();
            _completedLaps = 0;
        }
        // Set after any supersede-Stop above (which routes through Reset and would
        // otherwise clear it) so the one-shot survives to BeginCircle.
        _suppressFirstWaypointEvent = suppressFirstWaypointEvent;

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
            RaiseAfterReset(new LoopEvent(LoopEventKind.Failed,
                $"no reachable waypoint from {currentKey}"));
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

    // Pick the user-waypoint with the shortest BFS path from from. Returns null when
    // no waypoint is reachable (disconnected graph, all waypoints behind avoided
    // rooms, etc.).
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

    // Rotate the loop's Waypoints so the circle begins at waypoint instead of
    // Waypoints[0]. No-op when Waypoints is empty or the target isn't in the list.
    // The runtime step list is rebuilt separately by ExpandSteps.
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

    // (Re)compute _expandedSteps from the loop's current waypoint order + the
    // active filter. Called after every rotation and on every avoid-list change.
    private void ExpandSteps()
    {
        if (_loop is null || _bfs is null)
        {
            _expandedSteps = new List<LoopStep>();
            return;
        }
        // Route-scoped @wealth warm-up: probe the party only when a leg of the
        // cycle actually crosses a toll. LoopExpander is a pure helper (no
        // side effects), so the probe lives here — one debounced round-trip
        // covers every toll leg in the expansion.
        if (_filter is not null)
        {
            IReadOnlyList<LoopWaypoint> wps = _loop.Waypoints;
            for (int i = 0; i < wps.Count; i++)
                _filter.WarmForRoute(_bfs, wps[i].Key, wps[(i + 1) % wps.Count].Key);
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

    // Common entry into the circle phase — called either immediately from Start
    // (player already at waypoint / legacy loop) or after walker-driven approach
    // completes. Attaches the recovery gate, fires ReachedFirstWaypoint once per
    // session, anchors lap timing, and pushes the first step.
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
            if (_suppressFirstWaypointEvent)
                _suppressFirstWaypointEvent = false;   // consume the detour-resume suppression
            else
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
        // The runner cares about walker events during two shapes of "approach in
        // flight": the live LoopState.Approaching, and the paused-from-approach
        // window where a gate flipped us to Paused while the walker was still
        // driving. In the latter, if the walker's own resume handler ran before
        // ours it completes the walk and fires Finished while we're still Paused —
        // dropping it here strands the run, so buffer it for the resume path.
        bool approaching = State == LoopState.Approaching;
        bool pausedMidApproach = State == LoopState.Paused && _pausedFromApproach;
        if (!approaching && !pausedMidApproach) return;
        if (_approachTarget is null) return;

        switch (e.Kind)
        {
            case WalkEventKind.Finished:
                _approachTarget = null;
                if (pausedMidApproach)
                {
                    // Coordinator is already unpaused (the walker only finishes
                    // while Walking), but our OnPauseChanged resume hasn't run yet.
                    // Buffer the arrival; the resume branch enters the circle.
                    _log?.Info("LoopRunner",
                        "approach finished during pause window; deferring circle entry to resume");
                    _approachFinishedWhilePaused = true;
                    break;
                }
                // Walker arrived at the chosen waypoint. Rotation already happened
                // in Start — just hand off into the circle.
                _log?.Info("LoopRunner", "approach finished; entering circle");
                BeginCircle();
                break;
            case WalkEventKind.Failed:
                // Walker gave up (tier-3 abort, blocked, no path, etc.).
                _log?.Warn("LoopRunner",
                    $"approach failed: {e.Detail}");
                RaiseAfterReset(new LoopEvent(LoopEventKind.Failed,
                    $"approach failed: {e.Detail}"));
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

    // Avoided-rooms list mutated mid-loop. Stop the current run and re-Start with
    // the same loop so the new filter applies to every BFS call (closest-waypoint
    // pick + rotation + walker approach). The user effectively re-routes the loop
    // without losing the definition.
    //
    // No-op when the runner is idle. Loops without UserWaypoints (legacy v1 loaded
    // from disk) can't be re-expanded — those retain their original cached steps,
    // so this method only triggers a re-Start when the loop has UserWaypoints to
    // rotate from.
    //
    // Side effects of the Stop+Start cycle: the lap-history clears,
    // ReachedFirstWaypoint fires again once the new approach (if any) settles. Same
    // Stopped / Started event sequence the UI already handles for a user click on
    // Stop + Run.
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

    // Test seam — pretend the prompt scanner fired so command steps can advance.
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
            _completedLaps++;
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
            RaiseAfterReset(new LoopEvent(LoopEventKind.Failed,
                $"no exit {step.Direction} from {_tracker.State.CurrentRoom?.Key.ToString() ?? "(unknown)"}"));
            return;
        }

        // Set the landing prediction first so OnTrackerStateChanged
        // confirms the step regardless of HOW we cross the exit (plain
        // cardinal, text command, teleport keyword, or post-action
        // cardinal). _stepInFlight gates the confirmation handler.
        _expectedMoveTarget = exit.Target;
        _stepInFlight = true;

        // Predictive auto-light: light a carried light NOW if the room this lap step
        // enters reads dark, before any crossing bytes (door / cardinal / special)
        // go out — so the `use` lands ahead of the move and the room is lit on
        // arrival. No-op for a seeable / unmapped target.
        _approachLightHook?.Invoke(exit.Target);

        // Door / KeyLocked: if the latest room observation already shows the
        // door open, cross with the plain cardinal. Otherwise route through the
        // same DoorOpenManager the walker uses (bash / pick / key) and cross
        // from OnDoorReply once it opens — a loop should traverse a closed door
        // mid-circuit rather than detach the whole lap. Only when an enqueuer is
        // bound; unit harnesses without one keep the fail-loudly path rather
        // than sending a cardinal into a shut door and desyncing.
        if (exit.Hint is RoomExitHint.Door or RoomExitHint.KeyLocked)
        {
            if (_tracker.State.OpenDoorDirections is { } openDoors
                && openDoors.Contains(step.Direction))
            {
                EmitCardinal(step.Direction, exit.Target, "door pre-open");
                return;
            }
            if (_doorEnqueuer is not null)
            {
                _awaitingDoorOpen = true;
                _log?.Info("LoopRunner",
                    $"step {_index + 1}/{_expandedSteps.Count}: opening door {step.Direction}"
                    + (exit.StatRequirement > 0
                        ? $" (req {exit.StatRequirement}, canBash {exit.CanBash})"
                        : "")
                    + (exit.KeyItemId > 0 ? $" (key {exit.KeyItemId})" : ""));
                _doorEnqueuer(step.Direction, exit.StatRequirement, exit.CanBash, exit.KeyItemId, "loop", OnDoorReply);
                return;
            }
            FailStep($"closed door {step.Direction} mid-circuit — no door-open flow bound");
            return;
        }

        // SearchableHidden: route through the shared HiddenExitRevealManager —
        // the same sea <dir> reveal FSM the walker uses — and cross from
        // OnHiddenRevealReply once the exit appears, so a loop uncovers a hidden
        // exit mid-circuit rather than failing the lap. Pre-check the live room
        // first (a prior sea may have revealed it) to skip wasted round-trips,
        // mirroring the door pre-open check above. Only when an enqueuer is bound;
        // unit harnesses without one keep the fail-loudly path.
        if (exit.Hint == RoomExitHint.SearchableHidden)
        {
            if (_tracker.State.ObservedExitDirections is { } observedExits
                && observedExits.Contains(step.Direction))
            {
                EmitCardinal(step.Direction, exit.Target, "hidden already revealed");
                return;
            }
            if (_hiddenSearchEnqueuer is not null)
            {
                _awaitingHiddenReveal = true;
                _log?.Info("LoopRunner",
                    $"step {_index + 1}/{_expandedSteps.Count}: revealing hidden exit {step.Direction}");
                _hiddenSearchEnqueuer(step.Direction, "loop", OnHiddenRevealReply);
                return;
            }
            FailStep($"hidden exit {step.Direction} mid-circuit — no hidden-reveal flow bound");
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
            out string? syncFail,
            onLeaderPartySplitTeleport: _onLeaderPartySplit);
        if (sync == SpecialExitSend.Sent) return;
        if (sync == SpecialExitSend.Failed)
        {
            FailStep(syncFail!);
            return;
        }

        // Plain passage — the cardinal.
        EmitCardinal(step.Direction, exit.Target, null);
    }

    // Emit a plain cardinal move for the circuit, notifying the tracker + recovery
    // gate and firing the pre-move stealth hook. note annotates the wire reason
    // (e.g. "door pre-open"); null for an ordinary passage.
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

    // Fail the active circuit with reason and reset.
    private void FailStep(string reason)
    {
        _log?.Warn("LoopRunner", $"SendMove fail at step {_index + 1}/{_expandedSteps.Count}: {reason}");
        RaiseAfterReset(new LoopEvent(LoopEventKind.Failed, reason));
    }

    // Terminal callback from DoorOpenManager for a closed-door circuit step.
    // Mirrors AutoWalkManager.OnDoorReply: on success re-fetch the exit (the
    // step index hasn't advanced) and cross with the cardinal; on failure fail
    // the lap. The _awaitingDoorOpen flag is cleared here before EmitCardinal so
    // the arrival transition it triggers lands in OnTrackerStateChanged normally.
    private void OnDoorReply(DoorOpenResult result)
    {
        if (!_awaitingDoorOpen) return;
        _awaitingDoorOpen = false;

        switch (result)
        {
            case DoorOpenResult.Opened:
                if (_loop is null || State != LoopState.Running
                    || _index >= _expandedSteps.Count
                    || _expandedSteps[_index] is not MoveLoopStep step)
                {
                    return;
                }
                if (_tracker.State.CurrentRoom is not { } current
                    || !current.Exits.TryGetValue(step.Direction, out RoomExit exit))
                {
                    FailStep($"post-door-open: no exit {step.Direction} from {_tracker.State.CurrentRoom?.Key.ToString() ?? "(unknown)"}");
                    return;
                }
                _expectedMoveTarget = exit.Target;
                _stepInFlight = true;
                EmitCardinal(step.Direction, exit.Target, "post-door");
                return;

            case DoorOpenResult.Failed failed:
                FailStep($"door open failed: {failed.Reason}");
                return;
        }
    }

    // Terminal callback from HiddenExitRevealManager for a searchable-hidden
    // circuit step. Mirrors AutoWalkManager.OnHiddenRevealReply and OnDoorReply
    // above: on reveal re-fetch the exit (the step index hasn't advanced) and
    // cross with the cardinal; on failure fail the lap. _awaitingHiddenReveal is
    // cleared before EmitCardinal so the arrival transition lands normally.
    private void OnHiddenRevealReply(HiddenSearchResult result)
    {
        if (!_awaitingHiddenReveal) return;
        _awaitingHiddenReveal = false;

        switch (result)
        {
            case HiddenSearchResult.Revealed:
                if (_loop is null || State != LoopState.Running
                    || _index >= _expandedSteps.Count
                    || _expandedSteps[_index] is not MoveLoopStep step)
                {
                    return;
                }
                if (_tracker.State.CurrentRoom is not { } current
                    || !current.Exits.TryGetValue(step.Direction, out RoomExit exit))
                {
                    FailStep($"post-hidden-reveal: no exit {step.Direction} from {_tracker.State.CurrentRoom?.Key.ToString() ?? "(unknown)"}");
                    return;
                }
                _expectedMoveTarget = exit.Target;
                _stepInFlight = true;
                EmitCardinal(step.Direction, exit.Target, "post-hidden-reveal");
                return;

            case HiddenSearchResult.Failed failed:
                FailStep($"hidden reveal failed: {failed.Reason}");
                return;
        }
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

    // Test seam — pretend the custom-command delay just elapsed.
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
        // While recovering we're waiting for the tracker to (re)confirm a room so
        // we can reroute onto the nearest loop segment. Handle that before the
        // normal running-step confirmation logic (which gates on Running).
        if (State == LoopState.Recovering)
        {
            OnRecoveringTransition(t);
            return;
        }
        if (State != LoopState.Running || !_stepInFlight) return;
        if (_loop is null || _index >= _expandedSteps.Count) return;
        if (_expandedSteps[_index] is not MoveLoopStep) return;

        // A door or hidden-reveal sub-FSM owns this step until its reply fires the
        // cardinal. Its bash / pick / sea output re-observes the current (source)
        // room; acting on that transition here would treat the in-progress step as
        // blocked-at-source and spuriously enter recovery. The FSM clears its
        // await flag before emitting the real move, so the genuine arrival still
        // lands here.
        if (_awaitingDoorOpen || _awaitingHiddenReveal) return;

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

        // Suspect / Lost / Unknown already returned above, so NewConfidence is
        // now Confirmed or Pending.
        if (t.NewRoom?.Key is not { } key) return;

        if (key.Equals(_expectedMoveTarget))
        {
            // Arrived at the step's target. Confirmed is the clean case;
            // Pending means we're physically at the target but the tracker's
            // pending queue still carries a stale entry (a phantom duplicate /
            // an unconsumed echo). The loop only ever has one move in flight,
            // so any queue residue at the target is spurious — the step
            // completed either way. Advance instead of hanging in Pending
            // forever waiting for a Confirmed the wedged queue will never
            // deliver (the multi-minute silent loop stall).
            _stepInFlight = false;
            AdvanceStep();
            return;
        }

        // Below only makes sense once the move has resolved to a Confirmed
        // room. A Pending transition that isn't at the target is just the
        // in-flight posture — the synchronous Confirmed → Pending fired by our
        // own NoteMoveSent inside SendMove (still at source), or a mid-flight
        // redisplay — so wait for the real confirmation rather than treating it
        // as a landing.
        if (t.NewConfidence != RoomConfidence.Confirmed) return;

        if (t.PreviousRoom is not null
            && key.Equals(t.PreviousRoom.Key))
        {
            // Blocked at source — the move didn't take (a mob in the way, lag, a
            // transient obstruction). Instead of failing straight to Idle, enter
            // bounded auto-recovery: re-determine where we are and reroute onto the
            // loop from there. Since we're confirmed back at the source (which is on
            // the loop), the reroute re-sends this step; a persistent block trips
            // the MaxRecoverAttempts cap and finally surfaces as Failed.
            _log?.Warn("LoopRunner",
                $"step {_index + 1} blocked at source {key}; expected {_expectedMoveTarget}; entering recovery");
            EnterRecovery($"step {_index + 1} blocked at {key}");
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

    // Auto-recovery entry: a mid-circuit step landed somewhere we didn't plan for
    // (blocked at the source room, or the recovery gate handed back a room that
    // isn't the step's expected target). Rather than fail to Idle, re-determine
    // where we actually are and reroute onto the nearest loop segment. When the
    // tracker already knows the room we reroute immediately; when it's unsure we
    // send a bare `look` and let the echo (re)confirm the room in
    // OnRecoveringTransition. Bounded by MaxRecoverAttempts so a persistent block
    // eventually surfaces as Failed instead of looping forever.
    private void EnterRecovery(string reason)
    {
        if (_loop is null)
        {
            RaiseAfterReset(new LoopEvent(LoopEventKind.Failed, reason));
            return;
        }

        _recoverAttempts++;
        if (_recoverAttempts > MaxRecoverAttempts)
        {
            _log?.Warn("LoopRunner",
                $"recovery exhausted after {MaxRecoverAttempts} attempts; failing loop. last={reason}");
            RaiseAfterReset(new LoopEvent(LoopEventKind.Failed,
                $"recovery exhausted: {reason}"));
            return;
        }

        // Drop the gate + any in-flight step; we're re-planning from scratch.
        _recovery?.Detach();
        StopDelayTimer();
        if (_awaitingDoorOpen) { _doorStopAll?.Invoke(); _awaitingDoorOpen = false; }
        if (_awaitingHiddenReveal) { _hiddenSearchStopAll?.Invoke(); _awaitingHiddenReveal = false; }
        _stepInFlight = false;
        _awaitingPromptForCommand = false;
        _expectedMoveTarget = null;
        _approachTarget = null;
        State = LoopState.Recovering;
        Raise(new LoopEvent(LoopEventKind.Paused, $"recovering: {reason}"));

        // Tracker already sure of the room → reroute now. Issuing a `look` here
        // would race the reroute's first move: the echo re-prints the current room
        // and would trip the tracker into Suspect right after we send that move.
        if (_tracker.State.Confidence == RoomConfidence.Confirmed
            && _tracker.State.CurrentRoom is not null)
        {
            _log?.Warn("LoopRunner",
                $"recovery {_recoverAttempts}/{MaxRecoverAttempts}: {reason}; rerouting from {_tracker.State.CurrentRoom.Key}");
            RerouteFromCurrentRoom();
            return;
        }

        // Position unknown — ask the game to re-print the room and wait for the
        // tracker to (re)confirm before rerouting. Bare `look` has no target, so
        // the outbound peek-suppression pattern won't fire on it.
        _log?.Warn("LoopRunner",
            $"recovery {_recoverAttempts}/{MaxRecoverAttempts}: {reason}; issuing look to re-determine room");
        Write(Encoding.Latin1.GetBytes("look\r"), "recovery look");
    }

    // Reroute the active loop from wherever the tracker now says we are — picks the
    // closest waypoint, re-approaches if needed, and continues the circle. Reuses
    // Start's planning; isRecovery keeps the bounded budget + lap continuity.
    private void RerouteFromCurrentRoom()
    {
        if (_loop is null) return;
        StartInternal(_loop, isRecovery: true);
    }

    // Tracker transitions arriving while State == Recovering: once it firmly
    // (re)confirms a room, reroute onto the nearest loop segment from there.
    private void OnRecoveringTransition(RoomTransition t)
    {
        if (t.NewConfidence != RoomConfidence.Confirmed) return;
        if (t.NewRoom is null) return;
        _log?.Info("LoopRunner",
            $"recovery: re-determined room {t.NewRoom.Key}; rerouting");
        RerouteFromCurrentRoom();
    }

    private void AdvanceStep()
    {
        // Forward progress → refresh the recovery budget so an unrelated block
        // later in the lap gets the full retry allowance again.
        _recoverAttempts = 0;
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
            _pausedFromApproach = false;
            if (_approachFinishedWhilePaused)
            {
                // The walker completed the approach during the pause window (its
                // resume handler fired Finished before ours). Enter the circle now
                // instead of restoring Approaching — the walker is done and won't
                // re-fire Finished. This is the fix for the "loop walks to the
                // first room then sits idle until a second Run" bug.
                _approachFinishedWhilePaused = false;
                _log?.Info("LoopRunner",
                    "coordinator resumed; approach already finished, entering circle");
                BeginCircle();
                return;
            }
            // Walker is still mid-approach — put the runner back into Approaching
            // so OnWalkerEvent.Finished still hands off into BeginCircle correctly.
            // Don't send any loop steps; the walker owns the wire until it's done.
            _log?.Info("LoopRunner", "coordinator resumed during approach");
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
            // Arrived-while-paused guard: while paused we ignore tracker events,
            // so an in-flight move whose arrival landed during the pause window
            // leaves _index stale. Detect it by position, not posture — if the
            // tracker's current room is already the step's expected target, the
            // move physically completed, so advance the index before SendNextStep
            // rather than re-sending the same direction and walking one extra room
            // (the user-reported "overshoot"). Accept Pending as well as Confirmed:
            // when the arrival confirmed with a stale entry still in the pending
            // queue (a leftover from a just-stopped loop / an unconsumed echo) the
            // tracker lands CurrentRoom at the target but holds Pending posture,
            // and the OnTrackerStateChanged advance that normally handles this was
            // skipped because it fired while we were Paused. Without accepting the
            // Pending-at-target case here the loop falls through to the "still in
            // flight, awaiting confirmation" return below and hangs in the cleared
            // room until a manual redisplay flushes the queue.
            if (_stepInFlight
                && _expectedMoveTarget is { } expected
                && _tracker.State.Confidence is RoomConfidence.Confirmed or RoomConfidence.Pending
                && _tracker.State.CurrentRoom?.Key.Equals(expected) == true)
            {
                _log?.Info("LoopRunner",
                    $"resume: step {_index + 1} arrived during pause (tracker at {expected}, {_tracker.State.Confidence}); advancing");
                // Defer the advance+send so a same-burst re-pause aborts it. Keep
                // the in-flight flags set until the deferred body runs — if the
                // send is aborted, the overshoot guard must re-fire on the next
                // resume rather than falling through and re-sending the completed
                // move.
                DeferResumeDispatch(() =>
                {
                    _stepInFlight = false;
                    _expectedMoveTarget = null;
                    AdvanceStep();
                });
                return;
            }
            // A move was already on the wire when the pause hit and its
            // confirmation hasn't landed yet (the overshoot guard above didn't
            // fire, so the tracker is still Pending on it). Re-sending it here
            // would put a second copy of the same move on the wire AND a phantom
            // duplicate in the tracker's pending queue — the queue never empties,
            // the tracker sticks in Pending-at-target, and the loop hangs on a
            // Confirmed it will never get. Keep the step in flight instead; now
            // that we're Running again the resumed tracker events confirm it and
            // advance us. Refusals don't hit this — a bonked move fires
            // NoteMoveBlocked, which drops its pending entry and re-Confirms.
            if (_stepInFlight && _tracker.State.Confidence == RoomConfidence.Pending)
            {
                _log?.Info("LoopRunner",
                    $"resume: step {_index + 1} still in flight (tracker Pending); awaiting confirmation, not re-sending");
                return;
            }
            // Defer the send so a same-burst re-pause (a party @wait telepath
            // arriving after the Combat gate cleared in the same server-line
            // burst) lands first and aborts the leaked move.
            DeferResumeDispatch(() =>
            {
                _stepInFlight = false;
                _awaitingPromptForCommand = false;
                SendNextStep();
            });
        }
    }

    // Marshal a resume-triggered step dispatch onto the next UI tick and re-check
    // posture before it fires. PauseStateChanged runs synchronously inside a
    // server-line burst; posting past the burst lets any later gate-assert in the
    // same burst re-pause us first. The State re-check makes that re-pause abort
    // the send rather than leak a move past the new gate.
    private void DeferResumeDispatch(Action send)
    {
        _postToUi(() =>
        {
            if (State != LoopState.Running) return;
            send();
        });
    }

    private void Reset()
    {
        _recovery?.Detach();
        StopDelayTimer();
        // Drain a door FSM that was opening on our behalf — otherwise its
        // internal state sticks and the next run's enqueue sits in the queue
        // forever (DoorOpenManager.TryStartNext bails on non-Idle state).
        // Clearing _awaitingDoorOpen also makes any late OnDoorReply a no-op.
        if (_awaitingDoorOpen) _doorStopAll?.Invoke();
        _awaitingDoorOpen = false;
        // Same for a hidden-reveal FSM opening on our behalf.
        if (_awaitingHiddenReveal) _hiddenSearchStopAll?.Invoke();
        _awaitingHiddenReveal = false;
        _loop = null;
        _index = 0;
        _expandedSteps = new List<LoopStep>();
        _stepInFlight = false;
        _awaitingPromptForCommand = false;
        _expectedMoveTarget = null;
        _approachTarget = null;
        _circleStartRoom = null;
        _firstWaypointReached = false;
        _suppressFirstWaypointEvent = false;
        _pausedFromApproach = false;
        _approachFinishedWhilePaused = false;
        _recoverAttempts = 0;
        _lapDurations.Clear();
        _completedLaps = 0;
        _lapStartedAt = default;
        State = LoopState.Idle;
    }

    private void Raise(LoopEvent evt) => Event?.Invoke(evt);

    // Terminal-failure raise: Reset() to Idle FIRST, then raise. Consumers that
    // re-read runner state inside the event handler (NavigationViewModel does so
    // synchronously) must see the final Idle state — otherwise a Failed raised
    // while still Running pins the Nav "Looping/moving" chip and Reset() fires no
    // follow-up event to clear it. Mirrors Stop's reset-then-raise ordering.
    // Callers build the LoopEvent as the argument, so its Detail (which reads
    // live step state like _index) is frozen before Reset() wipes that state.
    private void RaiseAfterReset(LoopEvent evt)
    {
        Reset();
        Raise(evt);
    }
}

public enum LoopState
{
    Idle = 0,
    Running = 1,
    Paused = 2,
    // Walker is driving the player from their current room to the loop's chosen
    // starting waypoint. Loop runner has nothing on the wire yet; transitions to
    // Running when the walker fires Finished.
    Approaching = 3,
    // Transient auto-recovery: a mid-circuit step didn't land where planned, so
    // the runner is re-determining its position (immediately from a confirmed
    // room, or after a bare `look`) before rerouting onto the nearest loop segment.
    // Treated as an active, in-flight state everywhere (never Idle); resolves back
    // into Approaching / Running via StartInternal, or fails after MaxRecoverAttempts.
    Recovering = 4,
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
    // Fired once per loop session at the moment the runner begins the circle
    // (either immediately on Start if the player is already at a waypoint, or after
    // the walker-driven approach completes). Consumers anchor lap stats, fire
    // @reset to the party, etc. on this event rather than on Started so the timing
    // reflects the actual loop start, not the approach walk.
    ReachedFirstWaypoint = 8,
}

public readonly record struct LoopEvent(LoopEventKind Kind, string Detail);
