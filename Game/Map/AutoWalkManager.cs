using System.Collections.Generic;
using System.Linq;
using System.Text;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Walk-to engine — drives the wire one step at a time, waits for the
// appropriate confirmation (room change for moves; next prompt for
// command steps), and gates on MovementCoordinator so any pause source
// halts the walk mid-route.
//
// A WalkStep is either a move OR an inline command step (door opens
// today; lever pulls / button presses when game data describes them).
// The path is expanded via RemoteActionPathExpander at WalkTo time.
//
// Confirmation:
//   MoveStep    — waits for RoomTracker.StateChanged with the tracker
//                 Confirmed at the predicted target. Blocked-at-source
//                 retries once.
//   CommandStep — waits for the next WirePromptScanner.PromptObserved
//                 firing after the command goes out. No retry; the next
//                 move step will detect a stuck door via its own
//                 blocked-retry path.
public sealed class AutoWalkManager : IRecoverableEngine
{
    private readonly RoomGraphManager _graph;
    private readonly BfsMapper _bfs;
    private readonly RoomTracker _tracker;
    private readonly MovementCoordinator _coordinator;
    private readonly IRoomFilter? _filter;
    private readonly WirePromptScanner? _promptScanner;
    private readonly EngineRecoveryGate? _recovery;
    private Action<byte[]>? _wireSender;
    private Action<string, string, Action<string>>? _trapEnqueuer;
    private Func<bool>? _shouldDisarmTrap;
    private Action<string, Action<string>>? _trapDelegator;
    private Func<bool>? _canDelegateTrap;
    private Action? _trapDelegateStopAll;
    private Action<Direction, int, bool, int, string, Action<DoorOpenResult>>? _doorEnqueuer;
    private Action? _doorStopAll;
    private bool _awaitingDoorOpen;
    private Action<Direction, string, Action<HiddenSearchResult>>? _hiddenSearchEnqueuer;
    private Action? _hiddenSearchStopAll;
    private bool _awaitingHiddenReveal;
    private Action<Direction, string, bool, string, Action<WinchResult>>? _winchEnqueuer;
    private Action? _winchStopAll;
    private bool _awaitingWinch;
    private Func<RoomKey, RoomKey, string?>? _teleportResolver;
    private Func<bool>? _isLeaderWithFollowers;
    // True while the character is Confused (ConditionTracker.IsConfused). Read
    // by TryReplanOrFail — see MaxReplansPerWalk below. Null until wired.
    private Func<bool>? _isConfused;
    // True when ANY nav engine is driving (loop / auto-lair / point-to-point walk).
    // The abandoned-combat halt asserts a coordinator-wide gate, so it must fire
    // for a running loop too, not only when this point-to-point walker is active.
    private Func<bool>? _isAnyEngineActive;
    private Action? _onLeaderPartySplit;
    private Action? _onPartySplitAbort;
    private Action? _preMoveHook;
    private Action<RoomKey>? _approachRoomHook;
    private Action<IReadOnlyList<int>>? _pathItemAnnouncer;
    private Action<IReadOnlyList<RoomKey>>? _routeAnnouncer;
    private Func<RoomKey, IReadOnlyList<int>>? _hazardItemResolver;
    private Func<int, string?>? _itemNameResolver;
    // Boss rooms flagged "stop before" on the Bosses tab. A walk-to whose
    // destination is one of these halts one room short (loop / auto-lair engines
    // are unaffected — they never route through here). Resolved live so realm /
    // edit changes take effect without re-wiring.
    private Func<IReadOnlySet<RoomKey>>? _bossStopRooms;
    private IMazeSolver? _mazeSolver;
    private IPyramidSolver? _pyramidSolver;
    private BoatRoutePlanner? _boatPlanner;
    private Func<TimeSpan, Action, IDisposable>? _scheduleDelay;
    private readonly LogService? _log;

    private List<WalkStep>? _path;
    private int _index;                                      // index of the *next* step to send
    private RoomKey? _expectedAfterCurrentMove;
    private RoomKey? _destination;
    private RoomKey? _origin;                                // room this walk was planned from (flee anchor)
    private bool _stepInFlight;
    private bool _awaitingPromptForCommand;
    private bool _awaitingTrapDisarm;
    private bool _abandonHold;                               // AbandonedCombat gate is ours to release
    private int _retryCount;
    private const int MaxRetriesPerStep = 1;

    // Boat voyage in flight: the BoatStep is on the wire and we're waiting for
    // the arrival port to confirm. The sail spans intermediate ship / transit
    // rooms that aren't in the graph, so the tracker churns until it re-anchors
    // at the port. Because the walker is otherwise purely event-driven — and a
    // captain who silently refuses boarding emits NO further observation — the
    // voyage carries a wall-clock deadline: SendBoatStep sizes it from the
    // passage's transit-spell rounds (VoyageRounds * 3s + a small landing buffer)
    // and arms OnBoatDeadline through the injected scheduler. Whichever fires
    // first wins — an early arrival observation completes the step and cancels the
    // timer; the deadline is the authoritative backstop that completes-if-landed
    // or fails-out otherwise. _sailingEta / _sailingPlace feed the nav bar's
    // "Sailing the high seas…" countdown while the voyage is in flight.
    private bool _awaitingBoatArrival;
    private IDisposable? _boatTimer;
    // One-shot settle after an abandoned-combat halt: holds the AbandonedCombat
    // gate a beat past the Combat-gate clear so a monster that follows us out has
    // time to arrive and re-assert Combat (report stock-20260731-010401).
    private IDisposable? _abandonSettle;
    // How long to hold after the Combat gate clears on an abandon — a followed
    // monster's arrival lands within ~1s in practice.
    private static readonly TimeSpan AbandonSettleWindow = TimeSpan.FromMilliseconds(1000);
    private DateTimeOffset _sailingEta;
    private string? _sailingPlace;

    // Greet-teleport (ask-transport) in flight: an `ask <noun> <keyword>` that
    // ports the asker (GAME_MECHANICS "greet teleport"). Unlike an ordinary
    // teleport it can SILENTLY FAIL — a class-gated transport (issue #455: the
    // bard-only barmaid) sits behind a `testskill` skill roll the client doesn't
    // model, and a failed roll leaves the character exactly where they were,
    // sometimes with no fresh room render at all. So this step can't just fire and
    // trust the next observation: it verifies it actually landed in the
    // destination and, if not, re-asks — driven by a wall-clock watchdog so the
    // no-render case is caught too — until the destination confirms. The class
    // gate guarantees only an eligible class reaches this step, so the roll will
    // eventually pass; there's no wrong-class character to spin forever here.
    private bool _awaitingGreetTeleport;
    private string? _greetTeleportCommand;    // the `ask <noun> <keyword>` to re-send
    private RoomKey _greetTeleportSource;      // room the transport must move us out of
    private IDisposable? _greetTeleportTimer;
    private int _greetTeleportAttempts;
    private static readonly TimeSpan GreetTeleportRetryInterval = TimeSpan.FromSeconds(3);

    // A spell round is 3 real seconds (see GAME_MECHANICS "Timing & rounds"), so a
    // voyage's summed transit-spell rounds convert to wall-clock at this rate; the
    // buffer covers the board-cast + landing-render slop past that summed duration.
    private const int SpellRoundSeconds = 3;
    private const int BoatArrivalBufferSeconds = 3;

    // A boat hop weighs this many land hops when the planner's stitched route is
    // compared against a pure land route — the sail itself is one party-split
    // teleport, but it carries fixed board / transit / disembark overhead, so it
    // must beat walking by a clear margin to be worth splitting the party for.
    private const int BoatHopWeight = 4;

    // Counter for mid-walk re-plans triggered by tracker entering
    // Suspect/Lost mid-step (typically caused by the user manually typing
    // a movement at the terminal during a walk). Reset on every Confirmed
    // step advance; capped to prevent infinite ping-pong when the user
    // keeps interleaving typed movement.
    private int _replanCount;
    private const int MaxReplansPerWalk = 2;

    // Set only while TryReplanOrFail re-issues the walk to the SAME destination
    // after a mid-step tracker surprise. The re-plan reuses the WalkTo entry,
    // whose supersede branch would otherwise Stop() the in-flight walk and raise
    // a Stopped event — which downstream reroute FSMs (AutoDepositManager, the
    // shop routers) read as an external abort and tear themselves down, even
    // though the walker is about to keep heading to the very same room. This
    // flag tells the supersede branch to Reset() silently instead: no Stopped,
    // no party-split abort. The re-plan still surfaces Retrying → Started/Failed.
    private bool _replanningInPlace;

    public IReadOnlyList<byte[]> LastSentForTests => _sentForTests;
    private readonly List<byte[]> _sentForTests = new();

    public WalkState State { get; private set; } = WalkState.Idle;

    // Current walk's destination room (null when Idle).
    public RoomKey? Destination => _destination;

    // True while a sea-captain sailing is between boarding and landing. The nav
    // bar reads it to swap the walk status line for the "Sailing the high seas…"
    // countdown; false the instant the arrival port confirms (or the voyage fails).
    public bool IsSailing => _awaitingBoatArrival;

    // Wall-clock instant the in-flight sail is expected to land — the nav bar
    // counts down to it. Meaningful only while IsSailing; default otherwise.
    public DateTimeOffset SailingArrivalEta => _sailingEta;

    // The `secure passage to <place>` destination of the in-flight sail, for the
    // nav countdown label. Null when not sailing.
    public string? SailingDestinationName => _sailingPlace;

    // Total steps in the current expanded path (0 when Idle).
    public int StepCount => _path?.Count ?? 0;

    // Index of the next step to send (0..StepCount).
    public int CurrentStepIndex => _index;

    // Read-only snapshot of the current path — used by the Navigation
    // right rail to render the step list (with the current step
    // highlighted and completed ones struck through).
    public IReadOnlyList<WalkStep> Steps => _path is null
        ? (IReadOnlyList<WalkStep>)Array.Empty<WalkStep>()
        : _path;

    // Remaining walk path as a sequence of room keys — current room
    // followed by each subsequent MoveStep's ExpectedTarget. The map
    // renderer draws this as a blue polyline so the user can see exactly
    // where the walker is heading.
    public IReadOnlyList<RoomKey> RemainingRoomKeys
    {
        get
        {
            if (_path is null || State == WalkState.Idle)
                return Array.Empty<RoomKey>();

            var keys = new List<RoomKey>(_path.Count - _index + 1);

            int start = _index;
            if (_tracker.State.CurrentRoom is { } current)
            {
                keys.Add(current.Key);

                // Trim the display past the leg already walked. While the
                // walker is paused (combat, resting, user gate),
                // OnTrackerStateChanged bails without advancing _index, so the
                // index keeps pointing at a step whose ExpectedTarget the player
                // has already reached — the drawn line would loop back through
                // the room just entered until the walk resumes and
                // TryReconcileIndexAfterResume fast-forwards _index. If the step
                // AT _index is exactly that already-reached step, skip it so the
                // overlay starts at the CURRENT room, even mid-combat.
                //
                // Only _path[_index] is checked — the stale index lags by at most
                // one completed move. Scanning further ahead (the old behaviour)
                // would, on a go-act-return detour (RemoteActionPathExpander),
                // match the return leg's arrival back at the current room and
                // wrongly trim the whole out-and-back detour out of the drawn
                // line and the ETA — leaving the route to render only straight-
                // line segments that redraw as the walker loops out and back.
                if (_index < _path.Count
                    && _path[_index] is MoveStep atIndex
                    && atIndex.ExpectedTarget.Equals(current.Key))
                {
                    start = _index + 1;
                }
            }

            for (int i = start; i < _path.Count; i++)
            {
                if (_path[i] is MoveStep move) keys.Add(move.ExpectedTarget);
            }
            return keys;
        }
    }

    public event Action<WalkEvent>? Event;

    // The most recent walk event, retained after it fires so a bug report can
    // read why the last walk stopped/failed (the Detail reason) without having
    // subscribed to Event live. Null until the first walk event.
    public WalkEvent? LastEvent { get; private set; }

    // ----- IRecoverableEngine ----------------------------------------

    public string Name => "Walker";

    // The room BFS planned this walk from — a flee retreats toward it. On a
    // ResumeAfterRecovery re-plan this becomes the room we resumed at, so each
    // leg's flee anchors on that leg's own start. Null while Idle.
    public RoomKey? JourneyOrigin => _origin;

    public Direction? PeekNextPlannedDirection()
    {
        if (_path is null || _index >= _path.Count) return null;
        return _path[_index] is MoveStep move ? move.Direction : (Direction?)null;
    }

    public IReadOnlyList<Direction> PeekPlannedDirections(int count)
    {
        if (count < 1 || _path is null) return Array.Empty<Direction>();
        var dirs = new List<Direction>(count);
        for (int i = _index; i < _path.Count && dirs.Count < count; i++)
        {
            // Stop at the first command / action step — a forward flee sends
            // plain cardinals only, so we can't cross a lever / door step here.
            if (_path[i] is not MoveStep move) break;
            dirs.Add(move.Direction);
        }
        return dirs;
    }

    public void SendBacktrackMove(Direction direction)
    {
        // Tier-3 reverse-walk send. Don't advance _index; the gate
        // tracks its own progress against ExecutedSinceAnchor.
        _tracker.NoteMoveSent(direction);
        byte[] bytes = EncodeMove(direction);
        EmitMoveBytes(bytes, $"tier3 backtrack {direction}");
    }

    public void PauseForRecovery(string reason)
    {
        if (State != WalkState.Walking) return;
        State = WalkState.Paused;
        Raise(new WalkEvent(WalkEventKind.Paused, $"recovery: {reason}", _destination));
    }

    public void ResumeAfterRecovery(RoomKey recoveredAnchor)
    {
        if (State != WalkState.Paused) return;
        if (_destination is not { } dest) return;

        // Engine policy for walks: re-plan from the recovered anchor.
        // This consumes one of our replan budget slots — if the
        // recovered room isn't where we need to be, BFS will produce
        // a fresh path or surface "no path".
        State = WalkState.Walking;
        _stepInFlight = false;
        Raise(new WalkEvent(WalkEventKind.Resumed,
            $"recovered at {recoveredAnchor}; re-planning toward {dest}", dest));
        WalkToImmediate(dest);
    }

    public void AbortFromRecoveryFailure(string detail)
    {
        Raise(new WalkEvent(WalkEventKind.Failed,
            $"tier3 recovery failed: {detail}", _destination));
        Reset();
    }

    // ----- ctor ------------------------------------------------------

    public AutoWalkManager(
        RoomGraphManager graph,
        BfsMapper bfs,
        RoomTracker tracker,
        MovementCoordinator coordinator,
        IRoomFilter? filter = null,
        LogService? log = null,
        WirePromptScanner? promptScanner = null,
        EngineRecoveryGate? recovery = null)
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
        _recovery = recovery;

        _tracker.StateChanged += OnTrackerStateChanged;
        _coordinator.PauseStateChanged += OnCoordinatorPauseChanged;
        _coordinator.GatesChanged += OnGatesChangedForAbandon;
        if (_promptScanner is not null)
            _promptScanner.PromptObserved += OnPromptObserved;
    }

    // Bind the wire sender after construction (PartyPoller /
    // AutoPartyManager pattern). MainWindowViewModel binds this once the
    // TelnetClient is up.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    internal void SetWireSenderForTests(Action<byte[]> sender) => SetWireSender(sender);

    // Bind the random-teleport-maze solver. When a WalkTo targets a room inside a
    // teleport-maze pocket that normal routing can't reach (no known source, or
    // no plain route because BFS won't cross the cast-teleport exits), the walker
    // hands the job off to the solver instead of failing. The solver relocalizes
    // by look-sweep and drives the final leg back through WalkTo, or surfaces its
    // own failure via ReportMazeSolveFailed. Left unset on realms with no maze.
    public void SetMazeSolver(IMazeSolver solver)
    {
        ArgumentNullException.ThrowIfNull(solver);
        _mazeSolver = solver;
    }

    // The Great Pyramid climb is likewise not graph-routable — its floors are
    // disconnected clusters joined only by sphinx `remoteaction` teleports BFS
    // never plans through — so it plugs in the same no-route hand-off as the maze
    // solver, but on its own slot (a distinct destination range and driving model).
    // Left unset on realms without the pyramid.
    public void SetPyramidSolver(IPyramidSolver solver)
    {
        ArgumentNullException.ThrowIfNull(solver);
        _pyramidSolver = solver;
    }

    // Bind the boat-route planner. When a WalkTo's destination is reachable more
    // cheaply (or only) by a sea-captain sailing than by walking, the planner
    // stitches the two land legs around the boat hop and the walker inserts a
    // BoatStep. Left unset on realms with no docks — the walker just plans land
    // routes as before.
    public void SetBoatPlanner(BoatRoutePlanner planner)
    {
        ArgumentNullException.ThrowIfNull(planner);
        _boatPlanner = planner;
    }

    // Bind the voyage scheduler — a one-shot wall-clock timer the boat step uses
    // to time the sail from boarding to landing (see the _boatTimer field). It's
    // injected rather than a raw timer so the Game/Map layer stays UI-free:
    // production wires a UI-thread one-shot (DispatcherTimer), tests a fake clock
    // they fire by hand. The callback must land on the same thread the walker runs
    // on (the UI thread in production). Left unset on realms with no docks — a
    // voyage then relies purely on the arrival observation, which is fine for the
    // tests that never sail without wiring a scheduler.
    public void SetVoyageScheduler(Func<TimeSpan, Action, IDisposable> scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        _scheduleDelay = scheduler;
    }

    // The maze solver's failure channel — it can't reach the goal, so raise the
    // walker's own Failed event with the solver's reason. Routed through the
    // walker (not a solver-owned event) so every WalkTo caller sees a maze solve
    // give up exactly like any other route failure.
    internal void ReportMazeSolveFailed(RoomKey destination, string reason)
        => Raise(new WalkEvent(WalkEventKind.Failed, $"maze solve: {reason}", destination));

    // The maze solver's success channel — it drives the final in-pocket leg
    // itself (self-look-verified, ungated moves), so the walker never raised its
    // own Finished. Routed through the walker (not a solver-owned event) so every
    // WalkTo caller sees a maze solve arrive exactly like any other route.
    internal void ReportMazeSolveSucceeded(RoomKey destination)
        => Raise(new WalkEvent(WalkEventKind.Finished, "maze solve: arrived", destination));

    // The pyramid solver's failure / success channels, routed through the walker
    // (not a solver-owned event) so every WalkTo caller sees a pyramid climb give
    // up or arrive exactly like any other route. It drives the climb itself, so the
    // walker never raised its own Finished/Failed for the leg.
    internal void ReportPyramidSolveFailed(RoomKey destination, string reason)
        => Raise(new WalkEvent(WalkEventKind.Failed, $"pyramid climb: {reason}", destination));

    internal void ReportPyramidSolveSucceeded(RoomKey destination)
        => Raise(new WalkEvent(WalkEventKind.Finished, "pyramid climb: arrived", destination));

    // Bind the trap-disarm enqueuer. Production wires this to
    // TrapDisarmManager.Enqueue with trapKnown=true — a walker step only reaches
    // here on a RoomExitHint.Trap, so the trap is already known and disarms
    // directly (no confirming search) before the move goes out. Tests pass a
    // capture-and-fire delegate.
    //
    // Signature: (direction, sender, reply). The walker passes the
    // lowercase direction word, the literal string "walker", and a reply
    // callback that resumes the walk on success or aborts it on failure.
    public void SetTrapEnqueuer(Action<string, string, Action<string>> enqueuer)
    {
        ArgumentNullException.ThrowIfNull(enqueuer);
        _trapEnqueuer = enqueuer;
    }

    // Gate for trapped-exit handling. Returns true when the walker should
    // route a Trap exit through the trap enqueuer — i.e. Settings → Other
    // "Utilize disarm traps if able" is on AND the local character has the
    // Traps skill. Returns false to walk straight through the trap without
    // attempting a disarm. When left unset the walker defaults to
    // attempting the disarm.
    public void SetTrapDisarmGate(Func<bool> gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _shouldDisarmTrap = gate;
    }

    // Party-delegation enqueuer — the walker calls this when the local
    // character can't disarm a trap but a capable party member can. It
    // broadcasts @trap <dir> on say and resumes the walk on the member's
    // say reply via the same OnTrapReply callback the local path uses.
    // Bound to TrapDelegationManager.Delegate. The two paths share the
    // resume callback but keep their signal SOURCES distinct — local keys
    // on the game's first-person disarm signals, delegation on the
    // member's say reply.
    public void SetTrapDelegator(Action<string, Action<string>> delegator)
    {
        ArgumentNullException.ThrowIfNull(delegator);
        _trapDelegator = delegator;
    }

    // Gate for the party-delegation branch. Returns true when the
    // "Utilize disarm traps if able" toggle is on, the LOCAL character
    // can't disarm, AND at least one party member can — i.e. the "if
    // able" clause is satisfied by party ability rather than our own.
    public void SetTrapDelegateGate(Func<bool> gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _canDelegateTrap = gate;
    }

    // Delegation teardown — bound to TrapDelegationManager.Cancel. Called
    // from Reset when a walk is superseded mid-delegation so a later stray
    // say reply can't resume a dead walk.
    public void SetTrapDelegateStopper(Action stopAll)
    {
        ArgumentNullException.ThrowIfNull(stopAll);
        _trapDelegateStopAll = stopAll;
    }

    // Door-open enqueuer — the walker calls this when stepping toward a
    // Door exit, passes the direction + the door's stat requirement +
    // bashable flag, and resumes the move on the callback's terminal
    // DoorOpenResult. MainWindowVM binds this to DoorOpenManager.Enqueue.
    public void SetDoorEnqueuer(Action<Direction, int, bool, int, string, Action<DoorOpenResult>> enqueuer)
    {
        ArgumentNullException.ThrowIfNull(enqueuer);
        _doorEnqueuer = enqueuer;
    }

    // Door-FSM teardown — bound to DoorOpenManager.StopAll. Called from
    // Reset when a walk is superseded while the walker is mid-door-FSM.
    // Without this, the new walk's follow-up _doorEnqueuer call sits in
    // the door manager's queue because TryStartNext bails on non-Idle
    // state and the walker stalls indefinitely.
    public void SetDoorStopper(Action stopAll)
    {
        ArgumentNullException.ThrowIfNull(stopAll);
        _doorStopAll = stopAll;
    }

    // Hidden-exit reveal enqueuer — walker calls this for SearchableHidden
    // exits to fire the sea <dir> retry loop until the exit appears on the
    // room display. MainWindowVM binds this to
    // HiddenExitRevealManager.Enqueue.
    public void SetHiddenSearchEnqueuer(Action<Direction, string, Action<HiddenSearchResult>> enqueuer)
    {
        ArgumentNullException.ThrowIfNull(enqueuer);
        _hiddenSearchEnqueuer = enqueuer;
    }

    // Hidden-search teardown — bound to HiddenExitRevealManager.StopAll.
    // Same stale-state cleanup rationale as SetDoorStopper.
    public void SetHiddenSearchStopper(Action stopAll)
    {
        ArgumentNullException.ThrowIfNull(stopAll);
        _hiddenSearchStopAll = stopAll;
    }

    // Winch enqueuer — walker calls this for a MultiActionHidden winch exit to
    // pull the winch, wait for it to turn + the gate to open, then move. Bound by
    // MainWindowVM to WinchManager.Enqueue.
    public void SetWinchEnqueuer(Action<Direction, string, bool, string, Action<WinchResult>> enqueuer)
    {
        ArgumentNullException.ThrowIfNull(enqueuer);
        _winchEnqueuer = enqueuer;
    }

    // Winch teardown — bound to WinchManager.StopAll. Same stale-state cleanup
    // rationale as SetDoorStopper.
    public void SetWinchStopper(Action stopAll)
    {
        ArgumentNullException.ThrowIfNull(stopAll);
        _winchStopAll = stopAll;
    }

    // Teleport-keyword resolver — given (source room, destination room)
    // the walker calls this to look up the verbatim command it should
    // send (from the source room's CMD chain in TBInfoStore). Bound by
    // MainWindowVM.
    public void SetTeleportResolver(Func<RoomKey, RoomKey, string?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _teleportResolver = resolver;
    }

    // Predicate the walker uses to decide whether to prefix a teleport
    // with .@party <cmd> so followers come along. Returns true when the
    // local character is party leader AND there's at least one follower.
    public void SetPartyLeaderCheck(Func<bool> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        _isLeaderWithFollowers = check;
    }

    // Wire the Confused check (AppServices binds this to Conditions.IsConfused) so
    // TryReplanOrFail can tell a confusion fumble apart from a genuine block.
    // Mirrors LoopRunner.SetConfusedCheck.
    public void SetConfusedCheck(Func<bool> isConfused)
    {
        ArgumentNullException.ThrowIfNull(isConfused);
        _isConfused = isConfused;
    }

    // Predicate reporting whether ANY nav engine is driving (loop / auto-lair /
    // point-to-point walk) — MovementController.IsActive. Lets HaltForAbandonedCombat
    // fire for a running loop, not just a point-to-point walk. Until set, the halt
    // falls back to the walker's own state (legacy behaviour).
    public void SetAnyEngineActiveCheck(Func<bool> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        _isAnyEngineActive = check;
    }

    // Party-split-teleport handler — invoked right after the local (leading)
    // character crosses a party-splitting CMD teleport. The relay already sent
    // every follower through, but the teleport dissolved the follow chain;
    // AppServices binds this to AutoPartyManager.NotePartySplitTeleport so the
    // roster is re-invited + the movement gate held until the group reforms.
    public void SetPartySplitHandler(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _onLeaderPartySplit = handler;
    }

    // Party-reform abort — invoked when the user stops the walk. A party-
    // splitting teleport re-invites the group and holds the movement gate until
    // they rejoin; if the user stops mid-reform, that hold would otherwise pin
    // the gate until the members rejoin or the 90s window elapses. AppServices
    // binds this to AutoPartyManager.AbortReformWaits so a stop frees movement.
    public void SetPartySplitAbortHandler(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _onPartySplitAbort = handler;
    }

    // Pre-move stealth hook — invoked by the walker immediately before
    // each move's bytes go out, AFTER any door / trap / hidden /
    // multi-action pre-steps, so sn is the last command before the move
    // and the move itself is sneaked. MainWindowVM / AppServices binds
    // this to StealthManager.RequestPreMoveStealth. Non-blocking: the
    // hook fires and the move bytes follow without waiting for the sneak
    // ACK (sneak carries through the move).
    public void SetPreMoveHook(Action hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        _preMoveHook = hook;
    }

    // Predictive approach hook — invoked the instant the walker commits to a step,
    // with the room it's about to enter, BEFORE any door / trap / hidden / cardinal
    // bytes go out. AppServices binds this to the room-provisioners: auto-light
    // `use`s a carried light for a dark target, and the hazard-counter provisioner
    // `use`s a buff source for a checkspell hazard target — either way the `use`
    // precedes the move so the room is lit / survivable on arrival. No-op for a
    // benign or unmapped target; fires on every step (cheap) so each provisioner
    // owns its own decision.
    public void SetApproachRoomHook(Action<RoomKey> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        _approachRoomHook = hook;
    }

    // Planned-route item-requirement announcer. Invoked once at walk-start
    // with every item id gating an (Item: N) / (Ticket: N) exit along the
    // freshly-planned path — the items the character must be carrying to
    // complete the route. Bound to PathItemDemandTracker.OnPathItemsRequired,
    // which posts a need for each one we lack so auto-search arms until
    // it's found. Only exits with a possession gate are reported; door /
    // key / trap / hidden exits have their own FSMs and aren't
    // item-possession problems.
    public void SetPathItemAnnouncer(Action<IReadOnlyList<int>> announcer)
    {
        ArgumentNullException.ThrowIfNull(announcer);
        _pathItemAnnouncer = announcer;
    }

    // Hazard counter-item resolver. Given a room the route enters, returns the
    // item ids that make that room safe and MUST be carried (no in-group
    // substitute) — the RoomHazardIndex mandatory set. Folded into the same
    // walk-start item announce as the exit gates above so a route the user
    // chose to run through a hazard room (planThroughAcquirableGates) provisions
    // its counter the same way an Item/Ticket gate does. Any-of hazard groups
    // are deliberately omitted upstream; the route picker surfaces those.
    public void SetHazardItemResolver(Func<RoomKey, IReadOnlyList<int>> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _hazardItemResolver = resolver;
    }

    // Item-name lookup for the blocked-route diagnostic. Given an item id
    // gating an exit the crosser can't clear, returns the item's display name
    // so "all routes blocked by a required item you're missing" can name the
    // culprit ("... (obsidian key)"). Bound to the game-data item store in
    // MainWindowVM; when unset the message keeps its generic wording.
    public void SetItemNameResolver(Func<int, string?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _itemNameResolver = resolver;
    }

    // Boss "stop before" room set. Bound to BossStore in MainWindowVM / AppServices;
    // a walk-to targeting one of these rooms is re-pointed to the room one hop short
    // on the planned route. Resolved live (realm-filtered, honours edits). When
    // unset every walk targets its literal destination.
    public void SetBossStopRooms(Func<IReadOnlySet<RoomKey>> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _bossStopRooms = resolver;
    }

    // Re-point a walk that targets a "stop before" boss room to the room one hop
    // short on the shortest route (the same roomSeq[^2] idiom Auto-Lair uses to pick
    // a wait-room). Returns the destination unchanged when it isn't a flagged room,
    // when we're already inside it, or when no route can be planned — the normal walk
    // then handles or fails it. When the boss room is a single hop away the room one
    // short is the source itself, so the caller's already-at-destination check ends
    // the walk without a step.
    private RoomKey ApplyStopBefore(RoomKey source, RoomKey destination)
    {
        IReadOnlySet<RoomKey>? stopRooms = _bossStopRooms?.Invoke();
        if (stopRooms is null || !stopRooms.Contains(destination)) return destination;
        if (source.Equals(destination)) return destination;

        IReadOnlyList<Direction>? path = _bfs.FindPath(source, destination, _filter);
        if (path is null || path.Count == 0) return destination;

        IReadOnlyList<RoomKey> roomSeq = ReplayRooms(source, path);
        if (roomSeq.Count == 0) return destination;

        RoomKey before = roomSeq.Count == 1 ? source : roomSeq[^2];
        if (!before.Equals(destination))
            _log?.Info("Walk",
                $"stop-before boss room {destination.Map}/{destination.Room}: halting at {before.Map}/{before.Room}");
        return before;
    }

    // Replay directions from source through the graph, returning the rooms touched
    // (source excluded). Stops early if a step lands outside the graph.
    private IReadOnlyList<RoomKey> ReplayRooms(RoomKey source, IReadOnlyList<Direction> dirs)
    {
        List<RoomKey> rooms = new(dirs.Count);
        RoomKey cur = source;
        foreach (Direction d in dirs)
        {
            if (_graph.GetRoom(cur) is not Room room) break;
            if (!room.Exits.TryGetValue(d, out RoomExit exit)) break;
            cur = exit.Target;
            rooms.Add(cur);
        }
        return rooms;
    }

    // Planned-route room announcer. Invoked once at walk-start with the
    // ordered RoomKey sequence of the freshly-planned path (source first,
    // then each hop's target). Bound to the auto-light provisioner, which
    // scans the route for its darkest room and readies / provisions a
    // light that clears it before the character walks into the dark.
    // Best-effort and side-effect-free — skipped entirely when no
    // announcer is bound.
    public void SetRouteAnnouncer(Action<IReadOnlyList<RoomKey>> announcer)
    {
        ArgumentNullException.ThrowIfNull(announcer);
        _routeAnnouncer = announcer;
    }

    // Test seam — pretend the wire prompt scanner just fired, so the
    // pending command step can advance without a real telnet client.
    // No-op when no command step is in flight.
    internal void FirePromptForTests()
    {
        if (_awaitingPromptForCommand) OnPromptObservedCore();
    }

    // When non-null, the user requested a walk while the tracker still
    // had pipelined moves outstanding (Confidence == Pending). Planning is
    // deferred until the tracker reaches Confirmed; the next confirmation
    // in OnTrackerStateChanged picks this up and runs WalkToImmediate
    // against the actually-settled current room. Cleared by Reset so a
    // Stop or supersede invalidates the deferral.
    private RoomKey? _deferredWalkTarget;

    // Companion to _deferredWalkTarget: preserves the route picker's
    // "plan through acquirable gates" choice across the tracker-Pending
    // deferral so the deferred dispatch replans the same gated route.
    private bool _deferredWalkThroughGates;

    // Companion to _deferredWalkTarget: preserves the route picker's
    // "arm the item-acquisition pipeline" choice (false only for the
    // "direct — send it" mode) across the tracker-Pending deferral.
    private bool _deferredWalkArmAcquisition = true;

    // Companion to _deferredWalkTarget: preserves the teleport route
    // picker's "walk it, don't teleport" choice (true only when the
    // user chose the pure-walking route over a shorter teleport
    // shortcut) across the tracker-Pending deferral.
    private bool _deferredWalkAvoidTeleports;

    // Carries the route picker's "avoid traps" choice (true only when the user chose
    // the trap-free route over the shorter trapped one) across the deferral.
    private bool _deferredWalkAvoidTraps;

    // One-shot watchdog for the tracker-Pending deferral. A move the server
    // refuses with no room redisplay leaves the tracker stuck Pending, so the
    // Confirmed transition the deferral waits on never arrives and the walk would
    // sit in Walking forever with no feedback (report paradigm-20260810-201953).
    // When this fires the deferral is force-dispatched from the last-known room —
    // the refused move means we never actually left it — so WalkToImmediate plans
    // the route or fails with a real reason instead of hanging. Disposed on
    // dispatch / Reset.
    private IDisposable? _deferredWalkTimer;

    // How long to wait for an in-flight move to settle before treating the
    // deferral as stuck. Comfortably past a normal move's ~1-2s confirm so a
    // legitimately slow settle isn't cut short.
    private static readonly TimeSpan DeferredWalkTimeout = TimeSpan.FromSeconds(6);

    // The active walk's planning flags, captured when a route is committed in
    // WalkToImmediate and reset in Reset(). A mid-walk replan (TryReplanOrFail)
    // must re-issue WalkTo with these, or a no-teleport (or gate-planned) walk
    // silently reverts to the defaults and takes a teleport it was told to avoid.
    private bool _activeAvoidTeleports;
    private bool _activeAvoidTraps;
    private bool _activeThroughGates;
    private bool _activeArmAcquisition = true;

    // planThroughAcquirableGates: when true, BFS plans the route as if every
    // acquirable gate item (raft / ticket / door key / hazard counter) were
    // already carried — the route picker's "direct" choice. Default false
    // keeps every existing caller on the free-preferring route.
    //
    // armItemAcquisition: when true (default), the walk-start announce posts a
    // need for every gate item the route demands that we lack, arming the
    // shop / drop / party-share pipeline to source it. The route picker's
    // "direct — send it" choice passes false: it crosses the gates as-is
    // without provisioning, trusting the user to already hold what's needed.
    //
    // avoidTeleports: when true, BFS refuses item/CMD-cast teleport exits and
    // gateway portals, so the planned route is the pure-walking one. The
    // teleport route picker's "walk it, don't teleport" choice passes true;
    // every other caller keeps the default (false), which lets BFS take a
    // teleport hop as a normal short edge when it's the shortest route.
    //
    // avoidTraps: when true, BFS refuses trapped exits, so the planned route never
    // crosses a trap. The trap route picker's "avoid traps" choice passes true; every
    // other caller keeps the default (false), which lets BFS cross a trap as a normal
    // edge (the walker then disarms it at step time via TrapDisarmManager).
    // supersedeSilently: when this WalkTo interrupts an in-progress walk, clear
    // the old walk with a silent Reset instead of a loud Stopped. The path-item
    // acquisition routers pass true when they redirect the walk to a shop / giver /
    // bank en route: the redirect is their OWN doing, and the superseding Stopped
    // (subscribed to via Walker.Event) would otherwise fire back into the same
    // router's OnWalkEvent, which reads it as "user/another engine took over" and
    // abandons the detour it just armed. A genuine user/engine walk stays loud.
    public bool WalkTo(
        RoomKey destination,
        bool planThroughAcquirableGates = false,
        bool armItemAcquisition = true,
        bool avoidTeleports = false,
        bool avoidTraps = false,
        bool supersedeSilently = false)
    {
        if (State is WalkState.Walking or WalkState.Paused)
        {
            // Internal re-plan to the same destination, or a router's own detour
            // redirect: clear state silently so we don't emit a Stopped that reroute
            // FSMs mistake for an external abort. A genuine new WalkTo Stops loudly.
            if (_replanningInPlace || supersedeSilently)
                Reset();
            else
                Stop(reason: "superseded by new walk");
        }

        // In-flight moves still on the wire (typical when the user
        // clicks a new "walk to" before the current step has confirmed):
        // planning from tracker.CurrentRoom now would use a stale
        // source and our first send would interleave with the server's
        // pending reply. Defer until the tracker settles to Confirmed.
        if (_tracker.State.Confidence == RoomConfidence.Pending)
        {
            if (_graph.GetRoom(destination) is null)
            {
                Raise(new WalkEvent(WalkEventKind.Failed, "destination not in active graph", destination));
                return false;
            }
            _deferredWalkTarget = destination;
            _deferredWalkThroughGates = planThroughAcquirableGates;
            _deferredWalkArmAcquisition = armItemAcquisition;
            _deferredWalkAvoidTeleports = avoidTeleports;
            _deferredWalkAvoidTraps = avoidTraps;
            _destination = destination;       // populated so status surfaces show the target
            State = WalkState.Walking;
            // Watchdog: if the tracker never settles (the in-flight move was
            // refused with no room redisplay), force the deferral through from the
            // last-known room instead of hanging in Walking forever.
            _deferredWalkTimer?.Dispose();
            _deferredWalkTimer = _scheduleDelay?.Invoke(DeferredWalkTimeout, OnDeferredWalkDeadline);
            Raise(new WalkEvent(WalkEventKind.Started,
                "deferred — waiting for in-flight moves to settle",
                destination));
            return true;
        }

        return WalkToImmediate(destination, planThroughAcquirableGates, armItemAcquisition, avoidTeleports, avoidTraps);
    }

    private bool WalkToImmediate(
        RoomKey destination,
        bool planThroughAcquirableGates = false,
        bool armItemAcquisition = true,
        bool avoidTeleports = false,
        bool avoidTraps = false)
    {
        // Callers may arrive here from the WalkTo entry (Idle) OR from
        // the deferred dispatch in OnTrackerStateChanged (Walking with
        // _path == null). Either way the next few branches need a
        // clean slate — Reset takes us to Idle and clears any stale
        // _destination so failures don't leave the walker stuck.
        Reset();

        Room? source = _tracker.State.CurrentRoom;
        if (source is null)
        {
            // Tracker is Lost — but if the goal is inside a teleport-maze pocket
            // the solver can relocalize by look-sweep from where a teleport
            // dropped us, so hand off rather than fail. (TryBegin defers its work
            // off this call stack, so it won't re-enter WalkTo synchronously.)
            if (_mazeSolver is { } lostSolver
                && lostSolver.CanSolve(destination) && lostSolver.TryBegin(destination))
                return true;

            if (_pyramidSolver is { } lostPyramid
                && lostPyramid.CanSolve(destination) && lostPyramid.TryBegin(destination))
                return true;

            Raise(new WalkEvent(WalkEventKind.Failed, "no known source room", destination));
            return false;
        }

        if (_graph.GetRoom(destination) is null)
        {
            Raise(new WalkEvent(WalkEventKind.Failed, "destination not in active graph", destination));
            return false;
        }

        // Boss "stop before": re-point a walk targeting a flagged boss room to the
        // room one hop short, so the walker halts adjacent instead of stepping in
        // and tripping the spawn. Applied here so every WalkTo caller (map click,
        // GOTO, @goto, events, recovery) honours it; loop / auto-lair are untouched.
        destination = ApplyStopBefore(source.Key, destination);

        if (source.Key.Equals(destination))
        {
            Raise(new WalkEvent(WalkEventKind.Finished, "already at destination", destination));
            return true;
        }

        // Route-scoped @wealth warm-up: probes the party only when this walk's
        // tolls-permitted route actually crosses a toll (no-op otherwise).
        _filter?.WarmForRoute(_bfs, source.Key, destination);

        // The route picker's "direct" choice plans as if every acquirable gate
        // item were already carried — suspend those gates for the FindPath +
        // Expand pass so BFS returns the gated shortcut rather than the free
        // detour. Level / toll / class gates stay active regardless. Disposed
        // before any stepping so the live filter re-gates for mid-walk replans.
        IDisposable? gateScope = planThroughAcquirableGates
            ? _filter?.SuspendAcquirableGates()
            : null;
        IReadOnlyList<Direction>? path;
        IReadOnlyList<WalkStep> expanded;
        BoatRoutePlan? boatPlan = null;
        try
        {
            path = _bfs.FindPath(source.Key, destination, _filter,
                refuseTeleports: avoidTeleports, avoidTraps: avoidTraps);

            // A sea-captain sailing can beat (or replace) the land route. Weigh
            // the boat's stitched land-legs against the pure land route; the
            // planner returns a plan only when it wins by the boat-overhead
            // margin, or when there's no land route at all and a sail is the
            // sole crossing.
            boatPlan = ChooseBoatRoute(source.Key, destination,
                landHops: path is { Count: > 0 } ? path.Count : (int?)null);

            if (boatPlan is { } chosen)
            {
                expanded = BuildBoatWalk(source.Key, chosen);
            }
            else if (path is null || path.Count == 0)
            {
                // No plain route — but a teleport-maze goal has no plain route by
                // construction (BFS refuses the cast-teleport exits). Hand off to
                // the solver, which enters the pocket / reshuffles / relocalizes.
                if (_mazeSolver is { } mazeSolver
                    && mazeSolver.CanSolve(destination) && mazeSolver.TryBegin(destination))
                    return true;

                if (_pyramidSolver is { } pyramidSolver
                    && pyramidSolver.CanSolve(destination) && pyramidSolver.TryBegin(destination))
                    return true;

                // Name the obstacle on the route the crosser would actually
                // take, not on the shortest path with every gate wished away.
                // First re-probe with only the ACQUIRABLE gates suspended
                // (item / ticket / key-door / hazard) and level / toll / class
                // still active: any route that appears is one the crosser could
                // walk by acquiring something, so its blockers are the missing
                // key / item / counter — describe that. This matches the route
                // the picker identifies (e.g. a city front door gated on a key
                // you must fetch), instead of naming a shorter level-gated
                // portal the crosser was never going to use. Only when even that
                // finds nothing is the target walled by a non-acquirable gate —
                // fall back to the all-gates-ignored probe to name the level /
                // toll / class reason (or "no path" when truly disconnected).
                IReadOnlyList<Direction>? describePath;
                using (_filter?.SuspendAcquirableGates())
                    describePath = _bfs.FindPath(source.Key, destination, _filter);
                if (describePath is null || describePath.Count == 0)
                    describePath =
                        _bfs.FindPath(source.Key, destination, _filter, ignoreExitGates: true);

                // DescribeBlockedRoute runs with gating restored (the suspension
                // scope has closed), so DescribeExitBlock reports the real
                // acquirable-gate reasons on the front-door route's hops.
                string reason = describePath is { Count: > 0 }
                    ? DescribeBlockedRoute(source.Key, describePath)
                    : DescribeNoPlainRoute(source.Key, destination);
                Raise(new WalkEvent(WalkEventKind.Failed, reason, destination));
                return false;
            }
            else
            {
                expanded = RemoteActionPathExpander.Expand(_graph, source.Key, path, _bfs, _filter, _log);
            }
        }
        finally { gateScope?.Dispose(); }

        if (expanded.Count == 0)
        {
            Raise(new WalkEvent(WalkEventKind.Failed, "path expansion empty", destination));
            return false;
        }

        // A remote-action detour that couldn't be routed truncates the expansion
        // short of the destination. The expander solves nested action gates
        // recursively, so this now only fires for a route past the nesting-depth
        // cap, a lever-cycle, or a genuinely unroutable leg. Fail cleanly here —
        // the program log (Walker/Debug) names the exit that stopped it — rather
        // than walking the partial path and stranding, or mis-sending a bare move
        // the send-side rejects. Boat plans stitch their own arrival legs, so only
        // vet the plain expansion.
        if (boatPlan is null && !RemoteActionPathExpander.ReachesDestination(expanded, destination))
        {
            _log?.Debug("Walker",
                $"walk to {destination}: expansion truncated ({expanded.Count} step(s)) short of the destination — " +
                "a remote-action detour on the route could not be routed (see the detour lines above)");
            Raise(new WalkEvent(WalkEventKind.Failed,
                "route needs an action-gated exit the walker can't auto-solve (too deeply nested, a lever-cycle, or unroutable) — see the program log",
                destination));
            return false;
        }

        _path = new List<WalkStep>(expanded);
        _index = 0;
        _destination = destination;
        _activeAvoidTeleports = avoidTeleports;
        _activeAvoidTraps = avoidTraps;
        _activeThroughGates = planThroughAcquirableGates;
        _activeArmAcquisition = armItemAcquisition;
        _origin = source.Key;
        _retryCount = 0;
        _stepInFlight = false;
        _awaitingPromptForCommand = false;
        State = WalkState.Walking;
        _recovery?.Attach(this);

        int moveCount = expanded.Count(s => s is MoveStep);
        int actionCount = expanded.Count - moveCount;
        string detail = actionCount > 0
            ? $"{moveCount} move(s), {actionCount} action(s)"
            : $"{moveCount} step(s)";
        Raise(new WalkEvent(WalkEventKind.Started, detail, destination));

        // Announce the items this route demands so the demand-driven
        // auto-search can arm for anything we're not carrying, and the rooms it
        // crosses so the auto-light provisioner can ready / buy a light for the
        // darkest one. Best-effort: walks the graph along the planned
        // directions. Item announce is suppressed for the "direct — send it"
        // choice, which crosses the gates as-is without provisioning anything. A
        // boat route announces each land leg from its own origin (walk to the
        // dock, then walk from the arrival port).
        if (boatPlan is { } boat)
        {
            if (armItemAcquisition)
            {
                AnnouncePlannedItemRequirements(source.Key, boat.ToDock);
                AnnouncePlannedItemRequirements(boat.Passage.ArrivalRoom, boat.FromArrival);
            }
            AnnouncePlannedRoute(source.Key, boat.ToDock);
            AnnouncePlannedRoute(boat.Passage.ArrivalRoom, boat.FromArrival);
        }
        else if (path is not null)
        {
            if (armItemAcquisition)
                AnnouncePlannedItemRequirements(source.Key, path);
            AnnouncePlannedRoute(source.Key, path);
        }

        if (_coordinator.IsPaused)
        {
            State = WalkState.Paused;
            Raise(new WalkEvent(WalkEventKind.Paused, "coordinator paused", destination));
            return true;
        }

        SendNextStep();
        return true;
    }

    // Walk the graph along the planned directions, collecting the item id of
    // every possession-gated exit (Item / Ticket) crossed AND the mandatory
    // counter item of every hazard room entered. The result is the set of items
    // the route requires the character to carry; the demand tracker decides
    // which are missing. Cheap (one dictionary lookup per hop) and
    // side-effect-free — skipped entirely when no announcer is bound.
    private void AnnouncePlannedItemRequirements(RoomKey source, IReadOnlyList<Direction> path)
    {
        if (_pathItemAnnouncer is null) return;

        List<int>? required = null;
        RoomKey cur = source;
        foreach (Direction dir in path)
        {
            Room? room = _graph.GetRoom(cur);
            if (room is null || !room.Exits.TryGetValue(dir, out RoomExit exit))
                break;
            if (exit.KeyItemId > 0
                && exit.Hint is RoomExitHint.Item or RoomExitHint.Ticket)
                (required ??= new List<int>()).Add(exit.KeyItemId);
            // The hazard sits on the room being entered, so resolve the hop's
            // target — a free route never crosses hazard rooms (the filter
            // blocks them), so this only fires on a chosen gated route.
            if (_hazardItemResolver is { } hazardOf)
                foreach (int itemId in hazardOf(exit.Target))
                    if (itemId > 0)
                        (required ??= new List<int>()).Add(itemId);
            cur = exit.Target;
        }

        if (required is not null) _pathItemAnnouncer(required);
    }

    // Announce the freshly-planned route to any bound listener (the auto-light
    // provisioner scans it). Skipped entirely when no announcer is bound.
    private void AnnouncePlannedRoute(RoomKey source, IReadOnlyList<Direction> path)
    {
        if (_routeAnnouncer is null) return;
        _routeAnnouncer(ExpandRouteKeys(source, path));
    }

    // Walk the graph along a planned direction list, collecting the source and
    // every hop's target — the ordered RoomKeys the character will traverse. A hop
    // that can't be resolved (target outside the active graph) ends the walk early
    // so the returned route stays a contiguous prefix of the plan.
    private IReadOnlyList<RoomKey> ExpandRouteKeys(RoomKey source, IReadOnlyList<Direction> path)
    {
        List<RoomKey> route = new(path.Count + 1) { source };
        RoomKey cur = source;
        foreach (Direction dir in path)
        {
            Room? room = _graph.GetRoom(cur);
            if (room is null || !room.Exits.TryGetValue(dir, out RoomExit exit))
                break;
            route.Add(exit.Target);
            cur = exit.Target;
        }
        return route;
    }

    // Pick a boat route only when it beats the pure land route (or when no land
    // route exists and a sail is the sole crossing). The contained planner picks
    // the best passable sailing — filter-gated on each member's level + fare —
    // then we compare its stitched land-legs plus the boat's fixed overhead
    // against the land route's hop count, so a boat that saves nothing over
    // walking never splits the party. Null when no planner is bound (realms
    // without docks) or no sailing helps.
    private BoatRoutePlan? ChooseBoatRoute(RoomKey source, RoomKey destination, int? landHops)
    {
        if (_boatPlanner is null) return null;

        // Accept a fare- / level-gated sailing ONLY when there's no land route —
        // the sail is then the sole crossing, so surfacing it (and warning the
        // user a member may be refused at the dock) beats a bare "no path". With a
        // land route in hand, a gated boat is skipped so we never split the party
        // for a crossing a member can't make.
        if (_boatPlanner.TryPlan(source, destination, _filter, allowGated: landHops is null)
            is not { } plan)
            return null;

        // A land route exists — the boat must beat it by the overhead margin.
        if (landHops is { } hops && plan.LandHops + BoatHopWeight >= hops)
            return null;

        // Committing to a fare- or level-gated sailing primes the party's wealth
        // / level readings (async), so a re-plan gates on fresh numbers rather
        // than stale ones — the same best-effort warm a toll on a land route gets.
        if (plan.Passage.FareCopper > 0 || plan.Passage.MinLevel > 0)
            _filter?.WarmForBoat();

        return plan;
    }

    // Expand a boat plan into one ordered step list: the land leg to the dock,
    // the single BoatStep sail, then the land leg from the arrival port to the
    // goal. Each land leg runs through RemoteActionPathExpander so doors / traps
    // / hidden exits along the walk to the pier (or from the port) are handled
    // exactly as on any land route.
    private IReadOnlyList<WalkStep> BuildBoatWalk(RoomKey source, BoatRoutePlan plan)
    {
        List<WalkStep> steps = new();
        if (plan.ToDock.Count > 0)
            steps.AddRange(RemoteActionPathExpander.Expand(_graph, source, plan.ToDock, _bfs, _filter, _log));
        steps.Add(new BoatStep(plan.Passage));
        if (plan.FromArrival.Count > 0)
            steps.AddRange(RemoteActionPathExpander.Expand(
                _graph, plan.Passage.ArrivalRoom, plan.FromArrival, _bfs, _filter, _log));
        return steps;
    }

    // Name why the only route to the destination is blocked. The gates-ignored
    // path (the one that surfaced when we re-probed) is exactly the set of
    // exits the crosser can't clear, so classifying each hop and unioning the
    // reasons tells the user the real obstacle — a locked door, a missing item,
    // a level window, a toll, a class hall, or a room hazard — instead of the
    // old fixed "level, toll, or class" line that misnamed a key-door block.
    private string DescribeBlockedRoute(RoomKey source, IReadOnlyList<Direction> ungatedPath)
    {
        ExitBlockReason reasons = ExitBlockReason.None;
        List<int> missingItems = new();
        // The first level-gated hop's target + window, so the message can name the
        // actual barrier room and level instead of a bare "a level requirement".
        (RoomKey Room, int Min, int Max)? levelGate = null;
        // The first door hop (locked or plain), kept whole so the message can name
        // the room it's in, the direction, and the key / picklocks-strength it needs
        // — directional, from the blocking room's own exit, so it can't be confused
        // with the far side (which may have a different requirement entirely).
        (RoomKey From, Direction Dir, RoomExit Exit)? doorGate = null;
        RoomKey cur = source;
        foreach (Direction dir in ungatedPath)
        {
            Room? room = _graph.GetRoom(cur);
            if (room is null || !room.Exits.TryGetValue(dir, out RoomExit exit))
                break;
            if (_filter is { } f)
            {
                ExitBlockReason hop = f.DescribeExitBlock(in exit);
                reasons |= hop;
                if (hop.HasFlag(ExitBlockReason.Item)) CollectGateItems(in exit, missingItems);
                if (hop.HasFlag(ExitBlockReason.Level) && levelGate is null)
                    levelGate = (exit.Target, exit.MinLevel, exit.MaxLevel);
                if ((hop.HasFlag(ExitBlockReason.LockedDoor) || hop.HasFlag(ExitBlockReason.Door))
                    && doorGate is null)
                    doorGate = (cur, dir, exit);
            }
            cur = exit.Target;
        }
        return FormatBlockReasons(reasons, missingItems, levelGate, doorGate);
    }

    // No gated route resolved even with gates ignored. Before reporting a bare
    // "no path", check whether the ONLY thing walling the destination off is a
    // user-set avoid — if lifting the avoids opens a route, name the offending
    // room so the user knows their own avoid is the block, not a map dead-end.
    private string DescribeNoPlainRoute(RoomKey source, RoomKey destination)
    {
        if (_bfs.FirstAvoidBlockingRoute(source, destination, _filter) is { } blocked)
            return $"only route is blocked by user set avoid in room ({blocked.Map}/{blocked.Room})";
        return "no path";
    }

    // Reachability probe for callers (auto-train, auto-deposit) that need to
    // explain a WalkTo which returned false. Returns the first user-avoided room
    // blocking an otherwise-walkable route to `destination`, or null when the
    // block isn't an avoid (disconnected, or reachable). Read-only — starts no
    // walk; safe to call right after a failed WalkTo.
    public RoomKey? AvoidBlockingRouteTo(RoomKey destination)
    {
        if (_tracker.State.CurrentRoom?.Key is not { } source) return null;
        return _bfs.FirstAvoidBlockingRoute(source, destination, _filter);
    }

    // Gather the item ids an Item/Ticket/multi-action exit demands, so the
    // blocked-route message can name what the crosser lacks. Key-locked doors
    // report as LockedDoor (a key is one of several openers), not Item, so
    // they're handled by that branch — only pure possession gates land here.
    private static void CollectGateItems(in RoomExit exit, List<int> into)
    {
        switch (exit.Hint)
        {
            case RoomExitHint.Item:
            case RoomExitHint.Ticket:
                if (exit.KeyItemId > 0 && !into.Contains(exit.KeyItemId)) into.Add(exit.KeyItemId);
                break;
            case RoomExitHint.MultiActionHidden when exit.MultiAction is { } ma:
                foreach (ExitAction a in ma.Actions)
                    if (a.RequiredItemId > 0 && !into.Contains(a.RequiredItemId)) into.Add(a.RequiredItemId);
                break;
            case RoomExitHint.Teleport:
                // An item-use teleport (`use potion of levitation`) carries the item
                // it consumes as KeyItemId — name it so a blocked route says which
                // item to go obtain rather than a bare "no path".
                if (exit.KeyItemId > 0 && !into.Contains(exit.KeyItemId)) into.Add(exit.KeyItemId);
                break;
        }
    }

    private string FormatBlockReasons(ExitBlockReason reasons, IReadOnlyList<int> missingItems,
        (RoomKey Room, int Min, int Max)? levelGate,
        (RoomKey From, Direction Dir, RoomExit Exit)? doorGate)
    {
        // Classification came up empty (e.g. a bare IRoomFilter with no gate
        // model) — keep a truthful generic line rather than inventing a cause.
        if (reasons == ExitBlockReason.None)
            return "all routes blocked by an exit requirement";

        List<string> parts = new();
        if (reasons.HasFlag(ExitBlockReason.Level)) parts.Add(DescribeLevelGate(levelGate));
        if (reasons.HasFlag(ExitBlockReason.Toll)) parts.Add("a toll you can't afford");
        if (reasons.HasFlag(ExitBlockReason.Class)) parts.Add("a class restriction");
        // A locked or plain door blocks the same way to the user — name the one
        // barrier once, with its room / direction / key / skill, rather than two
        // generic lines.
        if (reasons.HasFlag(ExitBlockReason.LockedDoor) || reasons.HasFlag(ExitBlockReason.Door))
            parts.Add(DescribeDoorGate(doorGate));
        if (reasons.HasFlag(ExitBlockReason.Item)) parts.Add(DescribeMissingItems(missingItems));
        if (reasons.HasFlag(ExitBlockReason.Hazard)) parts.Add("a room hazard you can't survive");
        if (reasons.HasFlag(ExitBlockReason.Alignment)) parts.Add("an alignment-gated entrance a party member can't enter");
        return "all routes blocked by " + string.Join(" or ", parts);
    }

    // "a locked door south from 10/218 (Frozen Cavern) — needs the glass key, or 61
    // picklocks/strength" — names the barrier room, the way you're heading, and what
    // it takes to pass, so the user knows exactly which crossing is blocked (and
    // never mistakes it for the door's far side, which can differ).
    private string DescribeDoorGate((RoomKey From, Direction Dir, RoomExit Exit)? gate)
    {
        if (gate is not { } g) return "a locked door you can't open (no key, pick, or bash)";
        RoomExit exit = g.Exit;
        return BlockedExitDescriber.Describe(g.From, g.Dir, in exit,
            key => _graph.GetRoom(key)?.Name,
            id => _itemNameResolver?.Invoke(id));
    }

    // "a level requirement (1/1420 (Marble Passage) needs level 30+)" — names the
    // barrier room and its level window so the user knows exactly what/where.
    private string DescribeLevelGate((RoomKey Room, int Min, int Max)? gate)
    {
        if (gate is not { } g) return "a level requirement";
        string? name = _graph.GetRoom(g.Room)?.Name;
        string where = string.IsNullOrEmpty(name) ? g.Room.ToString() : $"{g.Room} ({name})";
        string need = g.Min > 0 && g.Max > 0 ? $"level {g.Min}-{g.Max}"
            : g.Min > 0 ? $"level {g.Min}+"
            : g.Max > 0 ? $"level {g.Max} or lower"
            : "a different level";
        return $"a level requirement ({where} needs {need})";
    }

    // Name the item(s) an Item-gated hop needs, when the game-data name resolver
    // is wired and a name is known; otherwise fall back to the generic phrasing.
    private string DescribeMissingItems(IReadOnlyList<int> missingItems)
    {
        if (_itemNameResolver is { } resolve && missingItems.Count > 0)
        {
            List<string> names = new();
            foreach (int id in missingItems)
            {
                string? name = resolve(id);
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
            if (names.Count == 1) return $"a required item to go obtain ({names[0]})";
            if (names.Count > 1) return "required items to go obtain (" + string.Join(", ", names) + ")";
        }
        return "a required item to go obtain";
    }

    // Expand the planned route between two known rooms into the ordered RoomKeys the
    // character would traverse. Uses the same BFS + movement filter WalkTo plans
    // with, so the result matches the walk a WalkTo(to) would take from `from`.
    // Null when either room is outside the active graph or no route exists.
    // Side-effect-free — nothing is sent and no walk state changes, so a caller can
    // inspect a leg (e.g. a reroute deciding whether it runs dark) without
    // committing to walk it.
    public IReadOnlyList<RoomKey>? TryComputeRouteKeys(RoomKey from, RoomKey to)
    {
        if (_graph.GetRoom(from) is null || _graph.GetRoom(to) is null) return null;
        if (from.Equals(to)) return new[] { from };

        IReadOnlyList<Direction>? path = _bfs.FindPath(from, to, _filter);
        if (path is null || path.Count == 0) return null;
        return ExpandRouteKeys(from, path);
    }

    public void Stop(string reason = "user stop")
    {
        if (State == WalkState.Idle) return;
        RoomKey? dest = _destination;
        Reset();
        // Free any party-reform gate this walk was holding so a stopped user
        // isn't pinned by an in-progress chime-teleport re-invite.
        _onPartySplitAbort?.Invoke();
        Raise(new WalkEvent(WalkEventKind.Stopped, reason, dest));
    }

    public void Pause() => _coordinator.AssertGate(MovementCoordinator.UserGate);
    public void Resume() => _coordinator.ClearGate(MovementCoordinator.UserGate);

    // A move already on the wire carried us out of a room where we'd engaged a
    // hostile (combat gate was held) before it died. The step can't be recalled,
    // but we must not keep walking the route deeper past a fight we committed to.
    // Halt on the engine-owned AbandonedCombat gate — NOT the manual User gate —
    // so this is an engine wait the walker manages itself, never a user pause the
    // toolbar/nav mistakes for a manual stop. It auto-releases the moment the
    // room is clear of hostiles (see OnGatesChangedForAbandon): if the monster
    // didn't follow, the Combat gate is already clearing this same tick and we
    // resume onward; if it followed, its arrival re-asserts Combat and that gate
    // holds us for the fight instead. Fired from
    // CombatStateTracker.EngagedTargetAbandoned. No-op only when NOTHING is
    // driving — a running loop / auto-lair pauses through the same coordinator gate
    // as a point-to-point walk. Gating this on the point-to-point walk state alone
    // let a loop sprint past a spawned/followed hostile it abandoned via an
    // in-flight step (report stock-20260731-010401).
    public void HaltForAbandonedCombat(string reason)
    {
        bool walking = State != WalkState.Idle;
        if (!walking && !(_isAnyEngineActive?.Invoke() ?? false)) return;
        _abandonHold = true;
        _coordinator.AssertGate(MovementCoordinator.AbandonedCombatGate, "AutoWalkManager", reason);
        // The Paused walk-event is point-to-point-walk chrome; a loop reports its
        // own hold via the coordinator gate, so only raise it when we're walking.
        if (walking) Raise(new WalkEvent(WalkEventKind.Paused, reason, _destination));
    }

    // Auto-release for the AbandonedCombat hold. The halt only ever fires from a
    // room that's clear of actionable hostiles (see CombatStateTracker), so the
    // Combat gate is cleared in the same observation right after we assert ours;
    // this handler catches the Combat-gate clear, holds a settle window (so a
    // monster a step behind us can catch up and re-assert Combat), then drops our
    // hold and resumes the onward route with no manual Resume. While the Combat
    // gate is still asserted we keep holding — a followed monster re-asserts Combat
    // and the fight takes precedence — so we never sprint away from an engaged fight.
    private void OnGatesChangedForAbandon()
    {
        if (!_abandonHold) return;
        if (_coordinator.AssertedGates.Contains(MovementCoordinator.CombatGate))
        {
            // A follower re-asserted Combat (or the clear hasn't landed yet) — that
            // gate owns the hold now; drop any pending settle-release.
            _abandonSettle?.Dispose();
            _abandonSettle = null;
            return;
        }
        // Combat cleared. Don't resume instantly — the monster we abandoned may be
        // one step behind; hold the settle so its follow-arrival can re-assert
        // Combat before the loop sprints on (report stock-20260731-010401). No
        // scheduler wired (unit tests) → release immediately (legacy behaviour).
        if (_abandonSettle is not null) return;   // settle already pending
        if (_scheduleDelay is null) { ReleaseAbandonHold(); return; }
        _abandonSettle = _scheduleDelay(AbandonSettleWindow, ReleaseAbandonHold);
    }

    // Settle elapsed with the room still clear of hostiles — no follower engaged, so
    // drop the abandon hold and let the route resume.
    private void ReleaseAbandonHold()
    {
        _abandonSettle?.Dispose();
        _abandonSettle = null;
        if (!_abandonHold) return;
        _abandonHold = false;
        _coordinator.ClearGate(MovementCoordinator.AbandonedCombatGate, "AutoWalkManager",
            "abandon settle elapsed — no follower engaged, resuming route");
    }

    // ----- internals -------------------------------------------------

    private void SendNextStep()
    {
        if (_path is null || _index >= _path.Count) return;
        if (_stepInFlight) return;

        // Never put a step on the wire while any gate is asserted. The pause
        // signal is async (OnCoordinatorPauseChanged), so without this guard a
        // step can slip out in the window between a gate asserting — e.g. combat
        // engaging a monster that just crept in — and the pause landing.
        if (_coordinator.IsPaused) return;

        // Tier-3 gate may have escalated; if so don't queue a new step.
        if (_recovery is not null && !_recovery.MayProceedWithPlannedStep()) return;

        WalkStep step = _path[_index];
        switch (step)
        {
            case MoveStep move:
                SendMoveStep(move);
                break;
            case CommandStep command:
                SendCommandStep(command);
                break;
            case BoatStep boat:
                SendBoatStep(boat);
                break;
        }
    }

    // Re-drive the current step after the engine send-gate that swallowed it
    // releases. A step put on the wire while the gate was locked is silently
    // dropped (EngineSendGate.WrapEngineSender no-ops when locked) yet still sets
    // _stepInFlight, so the walker sits Walking with the route drawn and nothing on
    // the wire, waiting on a confirmation that never comes (report
    // paradigm-20260813-063517: the auto-train loop-resume walk dropped its first
    // move behind the trainer-menu hold). The step index only advances on
    // confirmation, so clearing the in-flight flag and re-sending replays the SAME
    // step. No-op unless we're mid-walk with a step actually in flight.
    public void NudgeStalledStep()
    {
        if (State != WalkState.Walking || !_stepInFlight) return;
        _log?.Info("Walker", "engine send-gate released — re-driving the stalled step");
        _stepInFlight = false;
        SendNextStep();
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

        // Predictive room provisioning: light a carried light if the room we're
        // stepping into reads dark, and raise a checkspell hazard buff if it needs
        // one — before any crossing bytes (door / trap / hidden / cardinal) go out,
        // so the `use` lands ahead of the move and the room is lit / survivable on
        // arrival. No-op for a benign / unmapped target.
        _approachRoomHook?.Invoke(exit.Target);

        // Trapped exits — route through TrapDisarmManager before the move
        // bytes go out. The walker waits for the trap reply; the actual
        // move bytes are sent from OnTrapReply.
        if (exit.Hint == RoomExitHint.Trap && _trapEnqueuer is not null)
        {
            string dirWord = DirectionWord(step.Direction);
            if (_shouldDisarmTrap?.Invoke() ?? true)
            {
                // Local character has the Traps skill — disarm it ourselves.
                // The self path keys on the game's first-person disarm
                // signals (via TrapDisarmManager), never on say replies.
                _awaitingTrapDisarm = true;
                Raise(new WalkEvent(WalkEventKind.DisarmingTrap,
                    $"trap on {dirWord}", _destination));
                _log?.Info("Walker", $"step {_index + 1}/{_path!.Count}: disarm trap {dirWord}");
                _trapEnqueuer(dirWord, "walker", OnTrapReply);
                return;
            }

            // Local can't disarm — delegate to a capable party member when
            // one exists (the "if able" clause includes party ability). The
            // delegator broadcasts @trap on say and resumes us on the
            // member's say reply.
            if (_trapDelegator is not null && (_canDelegateTrap?.Invoke() ?? false))
            {
                _awaitingTrapDisarm = true;
                Raise(new WalkEvent(WalkEventKind.DisarmingTrap,
                    $"delegating trap on {dirWord} to party", _destination));
                _log?.Info("Walker",
                    $"step {_index + 1}/{_path!.Count}: delegate trap {dirWord} to party");
                _trapDelegator(dirWord, OnTrapReply);
                return;
            }

            // Disarm gated off (toggle disabled or nobody able) — step
            // through the trapped exit without a disarm attempt. Falls
            // through to the normal move emit below.
            _log?.Info("Walker",
                $"step {_index + 1}/{_path!.Count}: trap on {dirWord} — walking through (disarm disabled or unable)");
        }

        // Door / KeyLocked exits — route through DoorOpenManager to
        // bash/pick/open before the move bytes go out. The keyed-door
        // path (KeyItemId > 0) tries bash/pick first to save key charges
        // and falls back to the single-shot `use <keyName> <dir>` +
        // `open <dir>` sequence when no stat-alt is viable or both verbs
        // exhaust.
        if ((exit.Hint == RoomExitHint.Door || exit.Hint == RoomExitHint.KeyLocked)
            && _doorEnqueuer is not null)
        {
            // Pre-check: the latest room observation may have shown
            // "open door <dir>" — door is already open and the FSM
            // would just stall on the "is already open" response.
            // Skip straight to the cardinal move.
            if (_tracker.State.OpenDoorDirections is { } openDoors
                && openDoors.Contains(step.Direction))
            {
                _log?.Info("Walker",
                    $"step {_index + 1}/{_path!.Count}: door {step.Direction} already open — skipping FSM.");
                _tracker.NoteMoveSent(step.Direction);
                _recovery?.NoteEngineStepSent(step.Direction);
                byte[] preBytes = EncodeMove(step.Direction);
                EmitMoveBytes(preBytes, $"move {step.Direction} (door pre-open)");
                return;
            }
            _awaitingDoorOpen = true;
            _log?.Info("Walker",
                $"step {_index + 1}/{_path!.Count}: opening door {step.Direction}"
                + (exit.StatRequirement > 0
                    ? $" (req {exit.StatRequirement}, canBash {exit.CanBash})"
                    : "")
                + (exit.KeyItemId > 0 ? $" (key {exit.KeyItemId})" : ""));
            _doorEnqueuer(step.Direction, exit.StatRequirement, exit.CanBash, exit.KeyItemId, "walker", OnDoorReply);
            return;
        }

        // Winch MultiActionHidden — route through WinchManager: pull the winch,
        // wait for it to turn AND the gate to open, then move. Handled ahead of the
        // synchronous dispatch below so a winch never fires its move blindly (the
        // gate opens on a delay, so a blind move bonks "The gate is closed!"). Other
        // MultiActionHidden exits (levers etc.) stay synchronous.
        if (!step.SkipSpecialDispatch && _winchEnqueuer is not null
            && WinchManager.IsWinchExit(exit) && WinchManager.PullCommand(exit) is { } winchPull)
        {
            if (_tracker.State.OpenDoorDirections is { } openGate && openGate.Contains(step.Direction))
            {
                _log?.Info("Walker",
                    $"step {_index + 1}/{_path!.Count}: gate {step.Direction} already open — skipping winch FSM.");
                _tracker.NoteMoveSent(step.Direction);
                _recovery?.NoteEngineStepSent(step.Direction);
                EmitMoveBytes(EncodeMove(step.Direction), $"move {step.Direction} (gate pre-open)");
                return;
            }
            _awaitingWinch = true;
            _log?.Info("Walker", $"step {_index + 1}/{_path!.Count}: winching gate {step.Direction} ('{winchPull}').");
            _winchEnqueuer(step.Direction, winchPull, /*waitForGate:*/ true, "walker", OnWinchReply);
            return;
        }

        // Synchronous special exits — MultiActionHidden (same-room),
        // Text `(Text: ...)`, and Teleport `(Item: N)` — share one
        // emission path with the loop runner via SpecialExitDispatch so
        // both engines cross them identically. The async door/hidden
        // hints are NOT covered here; they fall through to their own
        // FSMs below.
        //
        // SkipSpecialDispatch marks the final cardinal of a cross-room
        // multi-action exit whose prerequisite commands the expander already
        // emitted as CommandSteps — dispatching multi-action logic again would
        // re-issue them, so cross it as a plain cardinal below.
        if (!step.SkipSpecialDispatch)
        {
            SpecialExitSend sync = SpecialExitDispatch.TrySendSynchronous(
                exit, step.Direction, _tracker.State.CurrentRoom,
                _tracker, _recovery,
                emitMove: EmitMoveBytes,
                writeAux: WriteBytes,
                _teleportResolver, _isLeaderWithFollowers,
                out string? syncFail,
                onLeaderPartySplitTeleport: _onLeaderPartySplit);
            if (sync == SpecialExitSend.Sent)
            {
                // A greet teleport (`ask <noun> <keyword>`) can silently fail its
                // skill roll and leave us put (issue #455) — don't just trust the
                // next observation; arm a verify-and-re-ask watchdog. Ordinary CMD
                // teleports (chime / boat / item-cast) stay fire-once here so their
                // party-split relay isn't re-fired.
                if (IsGreetTeleport(exit)) ArmGreetTeleportRetry(exit);
                return;
            }
            if (sync == SpecialExitSend.Failed)
            {
                _log?.Debug("Walker",
                    $"special-exit dispatch rejected step {_index + 1}/{_path!.Count} " +
                    $"({step.Direction} {exit.Hint} -> {exit.Target}): {syncFail}");
                Raise(new WalkEvent(WalkEventKind.Failed, syncFail!, _destination));
                Reset();
                return;
            }
        }

        // SearchableHidden — `(Hidden)` modifier. Send `sea <dir>`
        // until the exit appears in the room tracker's CurrentRoom,
        // then send the cardinal move. Capped by
        // Settings.Other.MaxHiddenSearchAttempts.
        if (exit.Hint == RoomExitHint.SearchableHidden && _hiddenSearchEnqueuer is not null)
        {
            // Pre-check: the latest room observation may already list this
            // direction as an obvious exit — a prior `sea` uncovered it, or
            // it simply isn't hidden in this room instance. Searching again is
            // wasted round-trips (mirrors the open-door pre-check above). Send
            // the cardinal move directly.
            if (_tracker.State.ObservedExitDirections is { } observedExits
                && observedExits.Contains(step.Direction))
            {
                _log?.Info("Walker",
                    $"step {_index + 1}/{_path!.Count}: hidden exit {step.Direction} already revealed — skipping search.");
                _tracker.NoteMoveSent(step.Direction);
                _recovery?.NoteEngineStepSent(step.Direction);
                byte[] revealedBytes = EncodeMove(step.Direction);
                EmitMoveBytes(revealedBytes, $"move {step.Direction} (hidden already revealed)");
                return;
            }
            _awaitingHiddenReveal = true;
            _log?.Info("Walker",
                $"step {_index + 1}/{_path!.Count}: revealing hidden exit {step.Direction}");
            _hiddenSearchEnqueuer(step.Direction, "walker", OnHiddenRevealReply);
            return;
        }

        // Inform the tracker before the bytes go out so a synchronous
        // wire path or test harness sees Pending before any landing
        // observation arrives.
        _tracker.NoteMoveSent(step.Direction);
                _recovery?.NoteEngineStepSent(step.Direction);

        byte[] bytes = EncodeMove(step.Direction);
        EmitMoveBytes(bytes, $"move {step.Direction} → {exit.Target}");
    }

    private void OnHiddenRevealReply(HiddenSearchResult result)
    {
        if (!_awaitingHiddenReveal) return;
        _awaitingHiddenReveal = false;

        switch (result)
        {
            case HiddenSearchResult.Revealed:
                if (_path is null || _index >= _path.Count
                    || _path[_index] is not MoveStep step)
                {
                    Reset();
                    return;
                }
                Room? current = _tracker.State.CurrentRoom;
                if (current is null
                    || !current.Exits.TryGetValue(step.Direction, out RoomExit exit))
                {
                    Raise(new WalkEvent(WalkEventKind.Failed,
                        "post-hidden-reveal: step source has no matching exit", _destination));
                    Reset();
                    return;
                }
                _tracker.NoteMoveSent(step.Direction);
                _recovery?.NoteEngineStepSent(step.Direction);
                byte[] bytes = EncodeMove(step.Direction);
                EmitMoveBytes(bytes, $"move {step.Direction} (post-hidden-reveal)");
                return;

            case HiddenSearchResult.Failed failed:
                Raise(new WalkEvent(WalkEventKind.Failed,
                    $"hidden exit search failed: {failed.Reason}", _destination));
                Reset();
                return;
        }
    }

    private void OnDoorReply(DoorOpenResult result)
    {
        if (!_awaitingDoorOpen) return;
        _awaitingDoorOpen = false;

        switch (result)
        {
            case DoorOpenResult.Opened:
                if (_path is null || _index >= _path.Count
                    || _path[_index] is not MoveStep step)
                {
                    Reset();
                    return;
                }
                Room? current = _tracker.State.CurrentRoom;
                if (current is null
                    || !current.Exits.TryGetValue(step.Direction, out RoomExit exit))
                {
                    Raise(new WalkEvent(WalkEventKind.Failed,
                        "post-door-open: step source has no matching exit", _destination));
                    Reset();
                    return;
                }
                _tracker.NoteMoveSent(step.Direction);
                _recovery?.NoteEngineStepSent(step.Direction);
                byte[] bytes = EncodeMove(step.Direction);
                EmitMoveBytes(bytes, $"move {step.Direction} (post-door)");
                return;

            case DoorOpenResult.Failed failed:
                Raise(new WalkEvent(WalkEventKind.Failed,
                    $"door open failed: {failed.Reason}", _destination));
                Reset();
                return;
        }
    }

    private void OnWinchReply(WinchResult result)
    {
        if (!_awaitingWinch) return;
        _awaitingWinch = false;

        switch (result)
        {
            case WinchResult.Turned:
                if (_path is null || _index >= _path.Count) { Reset(); return; }
                // Cross-room detour pull (a CommandStep): the winch turned in this
                // room; advance to the next detour step (walk toward the gate room),
                // exactly as a plain command completion would.
                if (_path[_index] is CommandStep)
                {
                    _stepInFlight = false;
                    AdvanceStep();
                    return;
                }
                if (_path[_index] is not MoveStep step)
                {
                    Reset();
                    return;
                }
                Room? current = _tracker.State.CurrentRoom;
                if (current is null
                    || !current.Exits.TryGetValue(step.Direction, out RoomExit exit))
                {
                    Raise(new WalkEvent(WalkEventKind.Failed,
                        "post-winch: step source has no matching exit", _destination));
                    Reset();
                    return;
                }
                _tracker.NoteMoveSent(step.Direction);
                _recovery?.NoteEngineStepSent(step.Direction);
                EmitMoveBytes(EncodeMove(step.Direction), $"move {step.Direction} (post-winch)");
                return;

            case WinchResult.Failed failed:
                Raise(new WalkEvent(WalkEventKind.Failed,
                    $"winch failed: {failed.Reason}", _destination));
                Reset();
                return;
        }
    }

    private void OnTrapReply(string reply)
    {
        if (!_awaitingTrapDisarm) return;
        _awaitingTrapDisarm = false;

        // Stopped externally — bail without moving.
        if (reply.Contains("flow stopped", StringComparison.OrdinalIgnoreCase))
        {
            Raise(new WalkEvent(WalkEventKind.Stopped,
                "trap disarm cancelled", _destination));
            Reset();
            return;
        }

        // Success message from the TrapDisarmManager:
        //   "Trap to the {direction} disarmed."
        bool disarmed = reply.Contains("disarmed", StringComparison.OrdinalIgnoreCase);
        if (!disarmed)
        {
            Raise(new WalkEvent(WalkEventKind.Failed,
                $"trap disarm failed: {reply}", _destination));
            Reset();
            return;
        }

        // Trap cleared — fire the actual move now. The walker's
        // _path[_index] is still the same MoveStep that triggered the
        // disarm flow.
        if (_path is null || _index >= _path.Count
            || _path[_index] is not MoveStep step)
        {
            Reset();
            return;
        }

        Room? current = _tracker.State.CurrentRoom;
        if (current is null
            || !current.Exits.TryGetValue(step.Direction, out RoomExit exit))
        {
            Raise(new WalkEvent(WalkEventKind.Failed,
                "post-disarm: step source has no matching exit", _destination));
            Reset();
            return;
        }

        _tracker.NoteMoveSent(step.Direction);
                _recovery?.NoteEngineStepSent(step.Direction);
        byte[] bytes = EncodeMove(step.Direction);
        EmitMoveBytes(bytes, $"move {step.Direction} (post-disarm)");
    }

    private static string DirectionWord(Direction dir) => dir switch
    {
        Direction.N  => "north",
        Direction.S  => "south",
        Direction.E  => "east",
        Direction.W  => "west",
        Direction.NE => "northeast",
        Direction.NW => "northwest",
        Direction.SE => "southeast",
        Direction.SW => "southwest",
        Direction.U  => "up",
        Direction.D  => "down",
        _ => "?",
    };

    private void SendCommandStep(CommandStep step)
    {
        _stepInFlight = true;

        // A cross-room detour's winch pull is a strength roll that can "not budge" —
        // route it through WinchManager so it re-pulls until the winch turns, then
        // advances (OnWinchReply's CommandStep branch), rather than firing once and
        // walking on. Pull-only (no gate poll — the detour walks to the gate room
        // next, covering the open delay). Falls back to fire-and-forget when unwired.
        if (step.IsWinchPull && _winchEnqueuer is not null)
        {
            _awaitingWinch = true;
            _log?.Info("Walker", $"detour winch pull ('{step.Command}') — re-pulling until it turns.");
            _winchEnqueuer(Direction.N, step.Command, /*waitForGate:*/ false, "walker", OnWinchReply);
            return;
        }

        _awaitingPromptForCommand = true;
        byte[] bytes = Encoding.Latin1.GetBytes(step.Command + "\r");
        WriteBytes(bytes, $"command '{step.Command}'");
    }

    // Put a sea-captain sailing on the wire. Like the chime teleport it splits
    // the party (leader `.@party <keyword>` relay, then every member types the
    // keyword), so it reuses the same leader-relay + reform hooks. Unlike a
    // MoveStep this one step spans many room changes — we DON'T hand the tracker
    // a pending move (there is no graph edge from the dock to the arrival port),
    // and instead own arrival detection in HandleBoatTransition off the tracker's
    // own re-anchor when the port finally renders.
    private void SendBoatStep(BoatStep step)
    {
        _stepInFlight = true;
        _awaitingBoatArrival = true;

        BoatPassage passage = step.Passage;

        // Size the sail from its transit-spell rounds — each round is 3s (see the
        // SpellRoundSeconds const) — plus a buffer for the board cast + landing
        // render past the summed transit duration. The nav bar counts down to this
        // ETA; OnBoatDeadline is the backstop that completes (if landed) or fails
        // the voyage out (captain refused boarding) when it fires.
        int voyageSeconds = passage.VoyageRounds * SpellRoundSeconds + BoatArrivalBufferSeconds;
        _sailingPlace = passage.Place;
        _sailingEta = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(voyageSeconds);

        // A gated sole-crossing sail still boards — the captain refuses only the
        // under-level / too-poor members at the dock and leaves them behind. Warn
        // so the user knows a member may not make the crossing, rather than the
        // walk silently splitting the party a head short.
        ExitBlockReason gate = _filter?.DescribeBoatBlock(passage) ?? ExitBlockReason.None;
        if (gate != ExitBlockReason.None)
            _log?.Warn("Walker",
                $"boat '{passage.Keyword}' is gated ({gate}) — a member may be refused "
                + "boarding at the dock and left behind; sailing anyway (sole crossing).");

        bool leaderRelay = _isLeaderWithFollowers?.Invoke() == true;
        _log?.Info("Walker",
            $"step {_index + 1}/{_path!.Count}: boat '{passage.Keyword}' "
            + $"(dock {passage.DockRoom} → arrive {passage.ArrivalRoom}); "
            + $"sail ~{voyageSeconds}s ({passage.VoyageRounds} spell-round(s))"
            + (leaderRelay ? " [party relay]" : ""));

        if (leaderRelay)
            WriteBytes(Encoding.Latin1.GetBytes($".@party {passage.Keyword}\r"),
                $"party relay boat '{passage.Keyword}'");

        EmitMoveBytes(Encoding.Latin1.GetBytes(passage.Keyword + "\r"), $"boat '{passage.Keyword}'");

        if (leaderRelay) _onLeaderPartySplit?.Invoke();

        // Arm the wall-clock backstop last, so the sail bytes are already out. An
        // early arrival observation disposes this in HandleBoatTransition; the one
        // that fires first wins.
        _boatTimer?.Dispose();
        _boatTimer = _scheduleDelay?.Invoke(TimeSpan.FromSeconds(voyageSeconds), OnBoatDeadline);

        Raise(new WalkEvent(WalkEventKind.Sailing,
            $"sailing to {passage.Place} (~{voyageSeconds}s)", _destination));
    }

    // Emit a move (cardinal direction, text-exit command, or teleport
    // keyword) — fires the pre-move stealth hook (so sn is the last
    // command before the move) then writes the move bytes. Every move-byte
    // send routes through here so the choke point stays single; non-move
    // sends (multi-action prerequisites, the teleport .@party relay) call
    // WriteBytes directly.
    private void EmitMoveBytes(byte[] bytes, string reasonForLog)
    {
        _preMoveHook?.Invoke();
        WriteBytes(bytes, reasonForLog);
    }

    private void WriteBytes(byte[] bytes, string reasonForLog)
    {
        _sentForTests.Add(bytes);
        if (_wireSender is null)
            _log?.Warn("Walker", $"wire sender not bound; suppressed: {reasonForLog}");
        else
            _wireSender(bytes);
        // Tier-3 recovery backtracks (SendBacktrackMove) route through here with no
        // active walk plan, so _path is null — the step counter only makes sense
        // when a planned path exists.
        string progress = _path is { } path ? $"step {_index + 1}/{path.Count}: " : string.Empty;
        _log?.Info("Walker", $"{progress}{reasonForLog}");
    }

    // Run the pending tracker-Pending deferral now (see _deferredWalkTarget). Called
    // on the Confirmed transition it was waiting for, on a pause-resume, or by the
    // watchdog when that transition never arrived. Captures the route picker's
    // flags, clears the deferral + its watchdog, and hands off to WalkToImmediate —
    // which plans from the current room or fails with a real reason. No-op if the
    // deferral was already consumed / superseded.
    private void DispatchDeferredWalk()
    {
        if (_deferredWalkTarget is not { } deferred
            || State != WalkState.Walking || _path is not null)
            return;

        bool throughGates = _deferredWalkThroughGates;
        bool armAcquisition = _deferredWalkArmAcquisition;
        bool avoidTeleports = _deferredWalkAvoidTeleports;
        bool avoidTraps = _deferredWalkAvoidTraps;
        _deferredWalkTarget = null;
        _deferredWalkThroughGates = false;
        _deferredWalkArmAcquisition = true;
        _deferredWalkAvoidTeleports = false;
        _deferredWalkAvoidTraps = false;
        _deferredWalkTimer?.Dispose();
        _deferredWalkTimer = null;
        WalkToImmediate(deferred, throughGates, armAcquisition, avoidTeleports, avoidTraps);
    }

    // Watchdog fire for a deferral whose Confirmed transition never arrived (the
    // server refused the in-flight move with no room redisplay). Force it through
    // from the last-known room so the walk fails with a reason (or plans) rather
    // than hanging silently in Walking.
    private void OnDeferredWalkDeadline()
    {
        _deferredWalkTimer?.Dispose();
        _deferredWalkTimer = null;
        if (_deferredWalkTarget is null || State != WalkState.Walking || _path is not null)
            return;
        _log?.Info("Walker",
            "deferred walk: in-flight move never settled — planning from the last-known room");
        DispatchDeferredWalk();
    }

    private void OnTrackerStateChanged(RoomTransition transition)
    {
        // Deferred-plan dispatch — a WalkTo arrived while the tracker
        // still had pipelined moves outstanding. The walker has been
        // sitting in Walking state with _path == null waiting for a
        // Confirmed observation. Now we have one — plan + send from
        // the actually-settled current room.
        if (transition.NewConfidence == RoomConfidence.Confirmed
            && _deferredWalkTarget is not null
            && State == WalkState.Walking
            && _path is null)
        {
            DispatchDeferredWalk();
            return;
        }

        if (State != WalkState.Walking) return;
        if (!_stepInFlight) return;
        if (_path is null || _index >= _path.Count) return;

        // A boat voyage owns every transition until the arrival port confirms —
        // the transit rooms churn the tracker (Suspect, passive redisplays), so
        // intercept here before the generic recovery / MoveStep paths would
        // mistake that churn for a mid-step desync.
        if (_awaitingBoatArrival && _path[_index] is BoatStep boatStep)
        {
            HandleBoatTransition(transition, boatStep);
            return;
        }

        // A greet teleport owns its step until it verifies the arrival room (or
        // re-asks after a failed skill roll). Intercept here so the generic
        // MoveStep recovery/replan paths — which would fail the walk after a
        // bounded retry — never see a merely-unlucky roll (issue #455).
        if (_awaitingGreetTeleport)
        {
            HandleGreetTeleportTransition(transition);
            return;
        }

        if (_path[_index] is not MoveStep) return;

        // A door / trap / hidden-exit sub-FSM owns this step until its own
        // reply callback (OnDoorReply / OnTrapReply / OnHiddenSearchReply)
        // fires the move and advances. While one is pending, the bash / pick /
        // search output re-observes the CURRENT room; letting the block below
        // act on that transition treats the still-in-progress step as
        // completed-or-blocked, clears _stepInFlight, and re-drives the step —
        // enqueuing a duplicate door request that later fires a stray verb in
        // the room we've since moved into. The sub-FSM clears its flag before
        // emitting the real move, so the genuine arrival transition still lands
        // here normally.
        if (_awaitingDoorOpen || _awaitingTrapDisarm || _awaitingHiddenReveal || _awaitingWinch)
            return;

        // Tracker lost confidence mid-step — defer to the
        // EngineRecoveryGate. The gate will either keep watching
        // (tier 2: 15-step budget + planned-direction-available
        // check) or escalate to tier-3 backtrack, calling back
        // through PauseForRecovery + SendBacktrackMove. Unknown
        // reaches us via OnGraphReloaded (active-set switched
        // mid-walk); treat the same way and let the gate decide.
        if (transition.NewConfidence is RoomConfidence.Suspect
                                     or RoomConfidence.Lost
                                     or RoomConfidence.Unknown)
        {
            if (_recovery is not null)
                _recovery.NoteSuspectedMismatch($"tracker {transition.NewConfidence} mid-step {_index + 1}");
            else
                TryReplanOrFail(transition.NewConfidence);   // legacy path when no gate is bound (tests)
            return;
        }

        if (transition.NewConfidence != RoomConfidence.Confirmed) return;

        RoomKey? newKey = transition.NewRoom?.Key;
        if (newKey is null) return;

        if (newKey.Value.Equals(_expectedAfterCurrentMove))
        {
            _stepInFlight = false;
            _retryCount = 0;
            _replanCount = 0;
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

            // MaxRetriesPerStep's tight budget (1) exists to fail fast on a
            // genuinely blocked exit, but it can't tell that apart from a run of
            // bad luck — two confusion fumbles on the same direction in a row
            // exhaust it just as fast as a real block, killing the whole walk
            // over what was really just an unlucky streak (report
            // paradigm-20260901-201514). Hand off to TryReplanOrFail instead of
            // failing outright: it already leans on rm before trusting the
            // tracker (the same fix applied to its other callers), so a fumble
            // streak gets a freshly re-verified room to replan from — and it
            // carries its own separate bounded budget (MaxReplansPerWalk), so a
            // truly blocked exit still fails cleanly, just one hop later.
            _log?.Info("Walker",
                $"step {_index + 1} blocked after {_retryCount} retries; handing off to replan");
            TryReplanOrFail(RoomConfidence.Confirmed);
            return;
        }

        // Unexpected landing while tracker is Confirmed — graph data
        // for the leg we just walked is stale / wrong (exit pointed
        // to a different room than reality). The tracker is sure
        // where we are; the gate has already refreshed its anchor to
        // the new location via its own subscription. We just need to
        // replan from the new room. We DON'T call
        // _recovery.NoteSuspectedMismatch here — that's for tracker-
        // uncertainty escalation (Suspect/Lost) and would spuriously
        // bump the gate back to tier 2 right after it had returned to
        // tier 1. The replan is a pure walker concern.
        _log?.Info("Walker",
            $"step {_index + 1} landed at {newKey} (expected {_expectedAfterCurrentMove}); replanning");
        TryReplanOrFail(RoomConfidence.Confirmed);
    }

    private void TryReplanOrFail(RoomConfidence newConfidence)
    {
        // A block landing while the character is Confused is the movement-fumble
        // mechanic (GAME_MECHANICS: "You fumble in confusion!" / "You convulse
        // violently!"), not a genuine mapping/graph problem — mirrors
        // LoopRunner.EnterRecovery's identical rationale. LoopRunner's own
        // recovery budget was exempted from this already (paradigm-20260902-
        // 113201), but it can hand off into this walker's separate replan budget
        // (e.g. via the blocked-at-source escape hatch above), which had no such
        // exemption: a short burst of confusion fumbles during that fallback
        // could still exhaust MaxReplansPerWalk just as fast as a real block and
        // fail the whole walk while the character was otherwise fine, just
        // waiting out the status effect (report paradigm-20260902-173754). Don't
        // count an attempt taken while confused — the replan below still fires,
        // so the step is retried the moment a move actually lands. A genuine
        // block hit right after confusion clears still gets the full budget.
        bool confused = _isConfused?.Invoke() == true;

        // Re-plan caps avoid infinite ping-pong when manual user
        // typing keeps interfering with the walker's expectations.
        if ((!confused && _replanCount >= MaxReplansPerWalk)
            || _destination is not { } dest
            || _tracker.State.CurrentRoom is not { } here)
        {
            Raise(new WalkEvent(WalkEventKind.Failed,
                $"tracker entered {newConfidence} mid-step; walker can't continue",
                _destination));
            Reset();
            return;
        }

        if (!confused) _replanCount++;
        _stepInFlight = false;
        Raise(new WalkEvent(WalkEventKind.Retrying,
            $"tracker entered {newConfidence} mid-step; re-planning from {here.Key} (attempt {_replanCount}/{MaxReplansPerWalk})",
            _destination));

        // Lean on Paradigm's authoritative rm before trusting the tracker's belief
        // and replanning from it — a mid-step desync is exactly what a name-
        // ambiguous zone (many identically-named rooms sharing an exit pattern)
        // can produce, and rm hard-locates the tracker independent of that name+
        // exit matching instead of replanning from the same wrong room repeatedly
        // (LoopRunner.EnterRecovery carries the identical rationale; report
        // paradigm-20260901-100523). _stepInFlight is already false above, so
        // rm's own reentrant tracker relocate can't be mistaken for an in-flight
        // step's arrival by OnTrackerStateChanged — it's a clean no-op there,
        // leaving DoReplan as the only thing that actually replans. Stock realms /
        // no rm reply fall through to exactly the prior behavior.
        if (_recovery?.TryResyncOnce?.Invoke(
                $"walker desync mid-step (tracker {newConfidence})",
                _ => DoReplan(),
                DoReplan) == true)
        {
            return;
        }

        DoReplan();

        void DoReplan()
        {
            // Re-source the path from the tracker's best-guess current
            // room. WalkTo handles the existing Walking state by clearing
            // it — silently, since _replanningInPlace suppresses the
            // supersede Stopped that would otherwise abort a driving reroute.
            //
            // WalkTo's own Reset() zeroes _replanCount as part of that clear —
            // it has no way to distinguish "a fresh user-initiated walk" from
            // "this walk replanning itself", and the latter must NOT lose the
            // count that makes MaxReplansPerWalk mean anything. Capture it now
            // and restore it after the call, or the cap never actually
            // accumulates: every replan attempt walks in seeing _replanCount
            // back at 0, so a persistently blocked exit (not just an unlucky
            // fumble streak) would retry through this path forever instead of
            // failing once the budget is genuinely spent.
            int replanCount = _replanCount;
            _replanningInPlace = true;
            try
            {
                // Preserve the walk's planning flags — a bare WalkTo(dest) reverts to
                // defaults, so a no-teleport walk would replan through a teleport.
                // (Args evaluate before WalkTo's internal Reset clears the fields.)
                WalkTo(dest,
                    planThroughAcquirableGates: _activeThroughGates,
                    armItemAcquisition: _activeArmAcquisition,
                    avoidTeleports: _activeAvoidTeleports,
                    avoidTraps: _activeAvoidTraps);
            }
            finally
            {
                _replanCount = replanCount;
                _replanningInPlace = false;
            }
        }
    }

    // A tracker transition arrived while a boat voyage is in flight. The sail
    // crosses intermediate ship / transit rooms that aren't in the graph, so the
    // tracker churns (Suspect, a passive dock redisplay) until it re-anchors at
    // the arrival port. Complete the step the moment it lands Confirmed there and
    // cancel the wall-clock backstop; every other observation is transit churn we
    // simply keep waiting through — OnBoatDeadline is the fail-out, not a hop cap,
    // so a captain who silently refuses boarding (no arrival, no further
    // observation) still ends the voyage when the deadline fires.
    private void HandleBoatTransition(RoomTransition transition, BoatStep boat)
    {
        RoomKey arrival = boat.Passage.ArrivalRoom;

        if (transition.NewConfidence == RoomConfidence.Confirmed
            && transition.NewRoom?.Key is { } landed
            && landed.Equals(arrival))
        {
            _log?.Info("Walker", $"boat '{boat.Passage.Keyword}' arrived at {arrival}.");
            _boatTimer?.Dispose();
            _boatTimer = null;
            _awaitingBoatArrival = false;
            _sailingPlace = null;
            _stepInFlight = false;
            _retryCount = 0;
            _replanCount = 0;
            AdvanceStep();
            return;
        }

        _log?.Info("Walker",
            $"boat '{boat.Passage.Keyword}' transit (tracker {transition.NewConfidence}); "
            + $"awaiting {arrival}.");
    }

    // The voyage's wall-clock backstop fired. If the tracker has re-anchored at
    // the arrival port by now, an arrival observation may just not have matched
    // yet (a late render) — complete the step. Otherwise the captain refused the
    // boarding (an under-level / too-poor / un-attuned member never left the dock)
    // or the arrival data never matched, so fail the voyage out rather than leave
    // the walk wedged on an arrival that won't come.
    private void OnBoatDeadline()
    {
        _boatTimer?.Dispose();
        _boatTimer = null;

        if (!_awaitingBoatArrival) return;                 // already completed early
        if (_path is null || _index >= _path.Count) return;
        if (_path[_index] is not BoatStep boat) return;

        RoomKey arrival = boat.Passage.ArrivalRoom;
        if (_tracker.State.CurrentRoom?.Key is { } here && here.Equals(arrival))
        {
            _log?.Info("Walker",
                $"boat '{boat.Passage.Keyword}' deadline: already at {arrival}; completing step.");
            _awaitingBoatArrival = false;
            _sailingPlace = null;
            _stepInFlight = false;
            _retryCount = 0;
            _replanCount = 0;
            AdvanceStep();
            return;
        }

        Raise(new WalkEvent(WalkEventKind.Failed,
            $"boat '{boat.Passage.Keyword}' never reached {arrival} "
            + "(captain refused boarding, or arrival mismatch)", _destination));
        Reset();
    }

    // A synthesised NPC ask-transport edge (GreetTeleportResolver → the graph's
    // Direction.Teleport slot, RawHint "greet teleport"). These carry their
    // `ask <noun> <keyword>` command baked into TextCommands and, unlike a chime /
    // item CMD teleport, can fail a skill roll — so they get the verify-and-re-ask
    // watchdog rather than fire-once dispatch.
    private static bool IsGreetTeleport(in RoomExit exit) =>
        exit.Hint == RoomExitHint.Teleport
        && string.Equals(exit.RawHint, "greet teleport", StringComparison.OrdinalIgnoreCase)
        && exit.TextCommands is { Count: > 0 };

    // Begin waiting on a greet teleport we just asked for: remember the command +
    // the room we must leave, and arm the re-ask watchdog. _expectedAfterCurrentMove
    // already holds the destination (set in SendMoveStep).
    private void ArmGreetTeleportRetry(in RoomExit exit)
    {
        _awaitingGreetTeleport = true;
        _greetTeleportCommand = exit.TextCommands![0];
        _greetTeleportSource = _tracker.State.CurrentRoom?.Key ?? default;
        _greetTeleportAttempts = 0;
        ArmGreetTeleportTimer();
    }

    private void ArmGreetTeleportTimer()
    {
        _greetTeleportTimer?.Dispose();
        _greetTeleportTimer = _scheduleDelay?.Invoke(GreetTeleportRetryInterval, OnGreetTeleportRetryDeadline);
    }

    private void ClearGreetTeleportWait()
    {
        _awaitingGreetTeleport = false;
        _greetTeleportCommand = null;
        _greetTeleportSource = default;
        _greetTeleportAttempts = 0;
        _greetTeleportTimer?.Dispose();
        _greetTeleportTimer = null;
    }

    // A room-change landed while a greet teleport was in flight. If it's the
    // destination the transport succeeded; if we're still in the source room the
    // skill roll failed and we re-ask; anywhere else the graph edge is wrong and we
    // replan.
    private void HandleGreetTeleportTransition(RoomTransition transition)
    {
        if (transition.NewConfidence != RoomConfidence.Confirmed) return;
        if (transition.NewRoom?.Key is not { } newKey) return;

        if (newKey.Equals(_expectedAfterCurrentMove))
        {
            ClearGreetTeleportWait();
            _stepInFlight = false;
            _retryCount = 0;
            _replanCount = 0;
            AdvanceStep();
            return;
        }

        if (newKey.Equals(_greetTeleportSource))
        {
            RetryGreetTeleport("still in the source room (transport roll failed)");
            return;
        }

        // Landed somewhere that is neither the source nor the destination — the
        // synthesised edge's target is stale. Stop re-asking and replan from where
        // we actually are.
        ClearGreetTeleportWait();
        _log?.Info("Walker",
            $"greet teleport landed at {newKey} (expected {_expectedAfterCurrentMove}); replanning");
        TryReplanOrFail(RoomConfidence.Confirmed);
    }

    // The re-ask watchdog fired. Catches the case a failed transport emits NO fresh
    // room render at all (so no transition ever reaches HandleGreetTeleportTransition):
    // if we're not in the destination yet, re-ask; if a late render already put us
    // there, complete the step.
    private void OnGreetTeleportRetryDeadline()
    {
        _greetTeleportTimer?.Dispose();
        _greetTeleportTimer = null;
        if (!_awaitingGreetTeleport) return;

        if (_tracker.State.CurrentRoom?.Key is { } here && here.Equals(_expectedAfterCurrentMove))
        {
            ClearGreetTeleportWait();
            _stepInFlight = false;
            _retryCount = 0;
            _replanCount = 0;
            AdvanceStep();
            return;
        }

        RetryGreetTeleport("no arrival within the retry window");
    }

    // Re-send the `ask <noun> <keyword>` and re-arm the watchdog — keeps asking
    // until the destination confirms. The class gate on the edge guarantees only a
    // class that CAN pass the roll ever reaches this step, so the retry converges
    // rather than spinning on a character who can never succeed (issue #455). A held
    // walk (combat pause) doesn't re-ask; the resume path re-arms the watchdog.
    //
    // The tracker is NOT re-notified: the original ask already enqueued one Pending
    // teleport move, and a failed transport leaves it in place (a same-room
    // redisplay is swallowed as a passive re-look, a silent fail renders nothing) —
    // so the single Pending survives every retry and the eventual arrival confirms
    // it in one step. Re-noting would pile up duplicate Pending moves and the
    // destination render would only dequeue one, never reaching Confirmed.
    private void RetryGreetTeleport(string reason)
    {
        if (!_awaitingGreetTeleport || _greetTeleportCommand is null) return;
        if (_coordinator.IsPaused || State != WalkState.Walking) return;

        _greetTeleportAttempts++;
        _log?.Info("Walker",
            $"greet teleport didn't arrive ({reason}); re-asking '{_greetTeleportCommand}' "
            + $"(attempt {_greetTeleportAttempts + 1}).");
        EmitMoveBytes(Encoding.Latin1.GetBytes(_greetTeleportCommand + "\r"),
            $"greet-teleport retry '{_greetTeleportCommand}' → {_expectedAfterCurrentMove}");
        ArmGreetTeleportTimer();
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

            // Stranded-deferred-walk dispatch: a WalkTo issued while a move was
            // still in flight parks the target in _deferredWalkTarget with no
            // plan yet, then waits for a Confirmed transition to plan + send.
            // If the coordinator paused (combat) before that transition landed,
            // it flipped us Walking → Paused, so OnTrackerStateChanged's deferred
            // dispatch (gated on State == Walking) skipped the Confirmed that
            // arrived while paused — leaving the target planned-but-unsent. Now
            // that we're Walking again, plan + send it here instead of hanging
            // until some unrelated tracker event (which the user only forces via
            // a manual redisplay). If the settle move is still Pending, stay
            // deferred: State is Walking again, so the next Confirmed dispatches.
            if (_deferredWalkTarget is not null && _path is null)
            {
                if (_tracker.State.Confidence == RoomConfidence.Confirmed)
                    DispatchDeferredWalk();
                return;
            }

            // Boat voyage in flight when the pause hit: the sail is a party-split
            // teleport mid-transit. Don't re-send it on resume (that re-teleports
            // and re-fires the reform) — keep waiting for the arrival port. If we
            // already reached the port while paused, complete the step here, since
            // OnTrackerStateChanged bailed on that arrival transition (it gates on
            // State == Walking).
            if (_awaitingBoatArrival)
            {
                RoomKey? arrival = (_path is { } bp && _index < bp.Count && bp[_index] is BoatStep bs)
                    ? bs.Passage.ArrivalRoom : (RoomKey?)null;
                if (arrival is { } port
                    && _tracker.State.CurrentRoom?.Key is { } here && here.Equals(port))
                {
                    _log?.Info("Walker", "resume: boat already arrived while paused; completing step.");
                    _boatTimer?.Dispose();
                    _boatTimer = null;
                    _awaitingBoatArrival = false;
                    _sailingPlace = null;
                    _stepInFlight = false;
                    AdvanceStep();
                }
                else
                {
                    _log?.Info("Walker",
                        "resume: boat voyage still in flight; awaiting arrival, not re-sending.");
                }
                return;
            }

            // A greet teleport (ask-transport) owns its own re-ask watchdog, which
            // holds off while paused. If we already landed while paused, complete
            // the step; otherwise re-arm the watchdog so it resumes re-asking.
            if (_awaitingGreetTeleport)
            {
                if (_tracker.State.CurrentRoom?.Key is { } here && here.Equals(_expectedAfterCurrentMove))
                {
                    _log?.Info("Walker", "resume: greet teleport already arrived while paused; completing step.");
                    ClearGreetTeleportWait();
                    _stepInFlight = false;
                    AdvanceStep();
                }
                else
                {
                    _log?.Info("Walker", "resume: greet teleport still pending; re-arming re-ask watchdog.");
                    ArmGreetTeleportTimer();
                }
                return;
            }

            // In-flight guard: a move was already on the wire when the pause
            // hit and its confirmation hasn't landed yet (tracker still Pending
            // on it). Re-sending it on resume would put a duplicate on the wire
            // AND wedge the tracker's pending queue — the walker would hang on a
            // Confirmed it never gets. This is the party-split (chime) teleport
            // case: the PartyInvite reform gate asserts then clears mid-teleport
            // (followers relay through and rejoin faster than the destination
            // room render lands), so resume fires before arrival confirms.
            // Re-sending re-teleported and re-fired the reform, spamming @join
            // at already-rejoined members and stranding the walk. Keep the step
            // in flight instead; the resumed tracker events confirm it and
            // advance us. (Mirrors the LoopRunner resume guard.)
            if (_stepInFlight && _tracker.State.Confidence == RoomConfidence.Pending)
            {
                _log?.Info("Walker",
                    $"resume: step {_index + 1} still in flight (tracker Pending); awaiting confirmation, not re-sending");
                return;
            }

            bool hadStepInFlight = _stepInFlight;
            _stepInFlight = false;
            _awaitingPromptForCommand = false;

            // While paused, OnTrackerStateChanged bailed on every room
            // arrival (it gates on State == Walking), so _index didn't
            // advance even though pipelined server responses may have
            // landed the player one or more rooms further along. Fast-
            // forward _index past any MoveStep whose ExpectedTarget the
            // player has already reached; if the player is somewhere
            // unrelated to the remaining path, re-plan instead of re-
            // sending a stale step that would overshoot. Live bug:
            // pause mid-walk → 2 pipelined moves resolve → resume → old
            // SendNextStep re-sent the just-completed step's direction
            // and the walker drifted off the path it had drawn.
            if (!TryReconcileIndexAfterResume(hadStepInFlight))
            {
                TryReplanOrFail(RoomConfidence.Suspect);
                return;
            }

            // Reconciliation may have completed the walk; only fire
            // the next step if we're still walking.
            if (State == WalkState.Walking) SendNextStep();
        }
    }

    // Reconcile _index with the tracker's current room after a pause.
    // Returns true when the walker can resume safely from its new index
    // (whether or not the index moved). Returns false when the player
    // ended up at a room that isn't on the remaining path AND can't
    // legally take the next planned step — the caller should re-plan
    // rather than blindly re-sending a stale step direction.
    //
    // hadStepInFlight: was a move already on the wire when the pause hit
    // (captured by the caller before clearing _stepInFlight). See its use
    // below.
    private bool TryReconcileIndexAfterResume(bool hadStepInFlight)
    {
        if (_path is null) return true;
        if (_tracker.State.CurrentRoom is not { } here) return true;
        RoomKey hereKey = here.Key;

        // Did the player reach one or more upcoming MoveStep targets
        // during the pause? Walk forward looking for the first match —
        // that's where they landed. (If the path revisits the same room
        // later, we conservatively assume the earliest matching step;
        // a manual long-traverse would surface as off-path further down.)
        for (int i = _index; i < _path.Count; i++)
        {
            if (_path[i] is MoveStep move && move.ExpectedTarget.Equals(hereKey))
            {
                _index = i + 1;
                _expectedAfterCurrentMove = null;
                Raise(new WalkEvent(WalkEventKind.StepCompleted,
                    $"{_index}/{_path.Count} (resume reconciliation)", _destination));
                if (_index >= _path.Count)
                {
                    RoomKey? dest = _destination;
                    Reset();
                    Raise(new WalkEvent(WalkEventKind.Finished,
                        "destination reached during pause", dest));
                }
                return true;
            }
        }

        // No forward match — the player isn't further along the path than
        // before the pause. If a move was in flight when the pause hit, its
        // absence from the forward scan above means it never landed:
        // MovementRefusalDetector still reverts it (NoteMoveBlocked fires even
        // while paused), OnTrackerStateChanged just doesn't react to that
        // transition (it gates on State == Walking) — so the tracker is back
        // at the step's SOURCE room, not somewhere the exit-existence check
        // below can distinguish from "never attempted yet". Trusting that
        // cheap graph-cached check here would resend the exact direction the
        // server just refused, and nothing remembers the refusal across the
        // next pause/resume cycle — a doomed retry loop that only ends by
        // luck (walker-side twin of the LoopRunner "refused while paused"
        // resume guard; report paradigm-20260901-091527, five minutes of
        // bonking the same wall). Force a re-plan instead.
        if (hadStepInFlight) return false;

        // No move was in flight (pause landed cleanly between steps). If the
        // next planned step's direction doesn't even exist as an exit from
        // the player's current room, they're off the path — re-plan. The
        // "exit exists" check is a cheap proxy for "the planned route still
        // works from here"; imperfect cases (exit exists but leads somewhere
        // unrelated) fall through to the normal mid-step desync handling in
        // OnTrackerStateChanged after the next send.
        if (_index >= _path.Count) return true;
        if (_path[_index] is not MoveStep nextMove) return true;
        return here.Exits.ContainsKey(nextMove.Direction);
    }

    private void Reset()
    {
        _recovery?.Detach();
        // Drain downstream FSMs that were running on our behalf — if a
        // walk is superseded mid-door-open or mid-hidden-search, the
        // manager keeps its internal state (WaitingBash / Searching /
        // etc.) and the next walk's enqueue call sits in its queue
        // forever (TryStartNext bails on non-Idle state). The stale-
        // callback case is also covered by clearing _awaitingDoorOpen /
        // _awaitingHiddenReveal so OnDoorReply / OnHiddenSearchReply
        // skip the late reply that arrives after StopAll.
        if (_awaitingDoorOpen)      _doorStopAll?.Invoke();
        if (_awaitingHiddenReveal)  _hiddenSearchStopAll?.Invoke();
        if (_awaitingWinch)         _winchStopAll?.Invoke();
        // Drop a pending party-delegation watch so a stray say reply can't
        // resume a superseded walk. Harmless when the trap was local-only.
        if (_awaitingTrapDisarm)    _trapDelegateStopAll?.Invoke();

        _path = null;
        _index = 0;
        _expectedAfterCurrentMove = null;
        _destination = null;
        _origin = null;
        _stepInFlight = false;
        _awaitingPromptForCommand = false;
        _awaitingTrapDisarm = false;
        _awaitingDoorOpen = false;
        _awaitingHiddenReveal = false;
        _awaitingWinch = false;
        _boatTimer?.Dispose();
        _boatTimer = null;
        _awaitingBoatArrival = false;
        _sailingPlace = null;
        ClearGreetTeleportWait();
        _deferredWalkTimer?.Dispose();
        _deferredWalkTimer = null;
        _deferredWalkTarget = null;
        _deferredWalkThroughGates = false;
        _deferredWalkArmAcquisition = true;
        _deferredWalkAvoidTeleports = false;
        _deferredWalkAvoidTraps = false;
        _activeAvoidTeleports = false;
        _activeAvoidTraps = false;
        _activeThroughGates = false;
        _activeArmAcquisition = true;
        _retryCount = 0;
        _replanCount = 0;
        // Drop any AbandonedCombat hold this walk was carrying so a stopped /
        // completed walk never strands the gate asserted (the auto-release only
        // fires on a Combat-gate transition, which may not come once we're Idle).
        if (_abandonHold)
        {
            _abandonSettle?.Dispose();
            _abandonSettle = null;
            _abandonHold = false;
            _coordinator.ClearGate(MovementCoordinator.AbandonedCombatGate, "AutoWalkManager", "walk reset");
        }
        State = WalkState.Idle;
    }

    private void Raise(WalkEvent evt)
    {
        LastEvent = evt;
        Event?.Invoke(evt);
    }

    internal static byte[] EncodeMove(Direction dir)
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
    DisarmingTrap = 8,
    Sailing = 9,
}

public readonly record struct WalkEvent(WalkEventKind Kind, string Detail, RoomKey? Destination);
