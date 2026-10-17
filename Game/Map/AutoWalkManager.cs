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

    // What routes are planned with: the movement filter, and for an automatic walk
    // the teleports it may use on top (_walkFilter, set while such a walk is planned
    // and under way).
    private IRoomFilter? _walkFilter;
    private IRoomFilter? Filter => _walkFilter ?? _filter;
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
    // Doors the trip under way tried and couldn't open, each by the room it leaves
    // and the room it leads to. Every plan the trip makes from then on refuses them
    // (AbandonedDoorsFilter); a new trip starts with none.
    private readonly HashSet<(RoomKey From, RoomKey To)> _abandonedDoors = new();
    // Doors the last plan walked round though one step through them was shorter,
    // each with the reason, for the log line and the bug report.
    private readonly List<string> _doorsWalkedRound = new();
    public IReadOnlyCollection<(RoomKey From, RoomKey To)> AbandonedDoors => _abandonedDoors;
    public IReadOnlyList<string> DoorsWalkedRound => _doorsWalkedRound;
    // Doors a loop run gave up on, handed over for the approach walk it is about to
    // start, which would otherwise plan through them afresh. Taken by the next walk.
    private (RoomKey From, RoomKey To)[]? _carriedAbandonedDoors;
    public void RefuseDoorsOnNextWalk(IReadOnlyCollection<(RoomKey From, RoomKey To)> doors) =>
        _carriedAbandonedDoors = doors.ToArray();
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
    // StealthManager.ReadyToMoveSneaking — false = hold the step (a sneak is settling).
    private Func<bool>? _moveReadyCheck;
    public void SetMoveReadyCheck(Func<bool> check) => _moveReadyCheck = check;
    private Action<RoomKey>? _approachRoomHook;
    private Action<IReadOnlyList<int>>? _pathItemAnnouncer;
    private Action<IReadOnlyList<RoomKey>>? _routeAnnouncer;
    private Func<RoomKey, IReadOnlyList<int>>? _hazardItemResolver;
    private Func<int, bool>? _doorKeySummonable;
    private Func<int, bool>? _gateItemHoldProbe;
    private Func<int, string?>? _itemNameResolver;
    // Boss rooms flagged "stop before" on the Bosses tab. A walk-to whose
    // destination is one of these halts one room short (loop / auto-lair engines
    // are unaffected — they never route through here). Resolved live so realm /
    // edit changes take effect without re-wiring.
    private Func<IReadOnlySet<RoomKey>>? _bossStopRooms;
    private IMazeSolver? _mazeSolver;
    private IPyramidSolver? _pyramidSolver;
    private BoatRoutePlanner? _boatPlanner;
    private SysopGotoRoutePlanner? _sysGotoPlanner;
    // Fires a routed sys-goto jump (SysopGotoManager.FireForRoute). Left unset on
    // realms / profiles without the power — the walker then just plans land routes.
    private Action<Models.Profile.SysopGotoLocation>? _sysGotoFire;
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
    // Sys-goto shortcut in flight: a SysGotoStep fired `sys goto <name>` and we're
    // waiting for the landing room to confirm. Like a boat there's no graph edge to
    // the arrival, so the tracker re-anchors when the forced room-display renders
    // (SysopGotoManager's resync commits SetLocated there); a wall-clock backstop
    // catches the case where the landing never matches. Combat isn't handled here —
    // the coordinator pause already stalls the walker before the step fires.
    private bool _awaitingSysGotoArrival;
    private IDisposable? _sysGotoTimer;
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
    // ports the asker (GAME_MECHANICS "Greet teleports — an NPC transports a
    // player who asks"). Unlike an ordinary teleport it can SILENTLY FAIL — a
    // class-gated transport (the bard-only barmaid) sits behind a `testskill` skill roll the client doesn't
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

    // A sys-goto jump weighs this many land hops when its landing→goal leg is
    // compared against a pure land route. The jump is instant, but firing it (a
    // possible `break`, the teleport, the forced room render + resync) is real
    // overhead, so the shortcut must save at least this many walked rooms to be
    // worth taking. Lighter than a boat (no party split), heavier than one step.
    private const int SysGotoHopWeight = 3;
    // The forced landing render + name-matched resync arrive within a couple of
    // seconds; give a generous backstop before failing the jump out.
    private static readonly TimeSpan SysGotoArrivalWindow = TimeSpan.FromSeconds(6);

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

    // The ask TryReplanOrFail has out with the game (rm, sys st) for where we are;
    // null when none is. While it is out nothing is in flight and the walk waits
    // for the answer: a resume must neither send from the room in doubt nor ask
    // again (each ask spends a re-plan, and two gates flapping inside one round
    // trip failed the walk). Reset drops it, which is what makes a late answer to
    // a stopped or superseded walk a no-op.
    private object? _locateAsk;

    // The stall watchdog's one extra wait for the step in flight, taken when its
    // ask didn't go out (the resolver's throttle may only have been in the way).
    private bool _stallWatchdogRearmed;


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

    // The room the move on the wire is entering, while one is (also while paused mid-move).
    public RoomKey? InFlightMoveTarget =>
        _stepInFlight && _path is { } p && _index < p.Count && p[_index] is MoveStep m ? m.ExpectedTarget : null;

    public Direction? PeekNextPlannedDirection()
    {
        if (_path is null || _index >= _path.Count) return null;
        return _path[_index] is MoveStep move ? move.Direction : (Direction?)null;
    }

    public Direction? PlannedDirectionFrom(RoomKey room)
    {
        if (_path is null || _origin is not { } here) return null;
        foreach (WalkStep step in _path)
        {
            switch (step)
            {
                case MoveStep move:
                    if (here.Equals(room)) return move.Direction;
                    here = move.ExpectedTarget;
                    break;
                case BoatStep boat:
                    here = boat.Passage.ArrivalRoom;
                    break;
                case SysGotoStep:
                    return null;
            }
        }
        return null;
    }

    public IReadOnlyList<Direction> PeekPlannedDirections(int count)
    {
        if (count < 1 || _path is null) return Array.Empty<Direction>();
        var dirs = new List<Direction>(count);
        // See LoopRunner.PeekPlannedDirections: a step whose move landed while we
        // were paused has been walked, though the index still points at it.
        bool landed = _stepInFlight
            && _index < _path.Count
            && _path[_index] is MoveStep { ExpectedTarget: var target }
            && _tracker.State.CurrentRoom?.Key.Equals(target) == true;
        int from = _index + (landed ? 1 : 0);
        // A later leg of a flee that already holds the walk: its earlier legs have
        // walked steps the index knows nothing of. The path goes on from the step
        // that leads into the room we stand in. Only moves are looked through: a
        // flee sends nothing else.
        if (_fleeHolding && _tracker.State.CurrentRoom?.Key is { } here)
        {
            for (int i = _index; i < _path.Count && _path[i] is MoveStep ahead; i++)
            {
                if (!ahead.ExpectedTarget.Equals(here)) continue;
                from = i + 1;
                break;
            }
        }
        for (int i = from; i < _path.Count && dirs.Count < count; i++)
        {
            // Stop at the first command / action step — a forward flee sends
            // plain cardinals only, so we can't cross a lever / door step here.
            // A teleport MoveStep is the same case: it's crossed by that exit's
            // own command, not by sending "teleport" at the game.
            if (_path[i] is not MoveStep move || !move.Direction.IsCardinal()) break;
            dirs.Add(move.Direction);
        }
        return dirs;
    }

    public void SendBacktrackMove(Direction direction)
    {
        // Tier-3 reverse-walk send. Don't advance _index; the gate
        // tracks its own progress against ExecutedSinceAnchor.
        //
        // A bare direction arrives with no RoomExit beside it, so this can only
        // ever cross a cardinal — callers must keep Direction.Teleport out. It's
        // deliberately not guarded here: EncodeMove throwing is how the one caller
        // that got this wrong was found at all (Crash-20260908-181131).
        (byte[] bytes, string what) = SpecialExitDispatch.EncodeBacktrack(_tracker, direction);
        EmitMoveBytes(bytes, what);
    }

    // See LoopRunner.PauseForFlee — the same hold against a gate clearing mid-flee.
    public void PauseForFlee(string reason)
    {
        if (State is not (WalkState.Walking or WalkState.Paused)) return;
        _fleeHolding = true;
        PauseForRecovery(reason);
    }

    // A walk re-plans to its destination from wherever the run landed, forward or
    // back, so carryOnFromHere changes nothing here.
    public void ResumeAfterFlee(RoomKey landedAt, bool carryOnFromHere = false)
    {
        _fleeHolding = false;
        ResumeAfterRecovery(landedAt);
    }

    private bool _fleeHolding;

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
        // Keep the walk's planning flags, as the in-place replan does. A bare
        // WalkToImmediate(dest) reverts to defaults, so a route the user chose to
        // take through a hazard ("cross unprotected") re-planned with the hazard
        // gate back on and failed "all routes blocked by a room hazard you can't
        // survive" after a mid-walk desync (report paradigm-20260924-120529).
        // Args evaluate before WalkToImmediate's Reset clears the fields.
        WalkToImmediate(dest,
            planThroughAcquirableGates: _activeThroughGates,
            armItemAcquisition: _activeArmAcquisition,
            avoidTeleports: _activeAvoidTeleports,
            avoidTraps: _activeAvoidTraps,
            ignoreAvoids: _activeIgnoreAvoids,
            preferTeleportFree: _activePreferTeleportFree,
            pickedRoute: _activePickedRoute,
            automaticTeleports: _activeAutomaticTeleports);
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
        _tracker.CommandMoveRefused += OnCommandMoveRefused;
        _tracker.CastCrossingStarted += OnCastCrossingStarted;
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

    // Bind the sys-goto shortcut planner + its fire action. The planner weighs a
    // `sys goto` jump against the land route; the fire action puts the chosen jump
    // on the wire (via SysopGotoManager). Both unset → no sys-goto shortcuts, land
    // routes only. Combat gating is the coordinator's (the walker stalls paused).
    public void SetSysGotoPlanner(SysopGotoRoutePlanner planner, Action<Models.Profile.SysopGotoLocation> fire)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(fire);
        _sysGotoPlanner = planner;
        _sysGotoFire = fire;
    }

    // Bind the voyage scheduler — a one-shot wall-clock timer the boat step uses
    // to time the sail from boarding to landing (see the _boatTimer field). It's
    // injected rather than a raw timer so the Game/Map layer stays UI-free:
    // AppServices wires a UI-thread one-shot (DispatcherTimer). The callback must land on the same thread the walker runs
    // on (the UI thread in production). Left unset on realms with no docks — a
    // voyage then relies purely on the arrival observation.
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
    // TrapDisarmManager.Enqueue, which disarms directly before the move goes out.
    //
    // Signature: (direction, sender, reply). The walker passes the
    // lowercase direction word, the literal string "walker", and a reply
    // callback that resumes the walk on success or aborts it on failure.
    public void SetTrapEnqueuer(Action<string, string, Action<string>> enqueuer)
    {
        ArgumentNullException.ThrowIfNull(enqueuer);
        _trapEnqueuer = enqueuer;
    }

    // Gate for disarming a trapped exit ourselves. Returns true when the
    // walker should route a Trap exit through the trap enqueuer — i.e.
    // Settings → Other "Utilize self or party members to disarm traps" is on
    // AND the local character can disarm. When false the walker tries party
    // delegation (SetTrapDelegateGate), else walks straight through. When
    // left unset the walker defaults to attempting the disarm.
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
    // "Utilize self or party members to disarm traps" toggle is on, the
    // LOCAL character can't disarm, AND at least one party member can.
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

    // Probe for "is this locked door's key worth fetching?". A key gate is
    // deliberately NOT a possession gate — pick and bash are the usual openers, and
    // a key we simply lack fails the exit in place rather than sending the walk off
    // to find one. The one exception is a key whose whole acquisition chain is
    // deterministic: a room command that summons a monster dropping it at 100%, an
    // NPC that hands it over, or a shop that sells it. The probe answers only for
    // those, so a low-drop lair key (the black star key, 1-10% off lair cultists)
    // stays unannounced and keeps failing in place.
    // Bound to AppServices' key-source lookup; unbound means no key is fetchable.
    public void SetDoorKeySourceProbe(Func<int, bool> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _doorKeySummonable = probe;
    }

    // Probe for "wait for this gate item rather than attempt the crossing" —
    // true when the item isn't carried AND a fulfiller is actively acquiring it.
    // Both halves matter: without the first we'd hold on a gate we can already
    // cross, and without the second we'd park a walk forever on a gate nothing can
    // source instead of failing it the normal way. Bound to AppServices; unbound
    // means never hold, which is the pre-existing attempt-and-fail behaviour.
    public void SetGateItemHoldProbe(Func<int, bool> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _gateItemHoldProbe = probe;
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

        IReadOnlyList<Direction>? path = _bfs.FindPath(source, destination, Filter);
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

    // Carries the route picker's "route through avoided rooms" choice (true only when
    // the user chose to override their own avoid list for this walk) across the deferral.
    private bool _deferredWalkIgnoreAvoids;

    // Carries the prefer-walking-over-teleport preference (true for every user-picked route
    // except an explicit "Teleport" choice) across the deferral.
    private bool _deferredWalkPreferTeleportFree;

    // Carries "the user looked at this route and chose it" across the deferral.
    private bool _deferredWalkPickedRoute;

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

    // Bounds the wait on a step that went out but never confirmed. Without it the
    // walker sits in Walking with _stepInFlight set forever: the tracker holds the
    // move Pending, nothing re-drives the step, and only a hand-typed move breaks
    // it (reports stock-20260914-000112 / -000155, where a displaced move echo left
    // the landing unrecognised in an identically-named grid). LoopRunner has carried
    // the same watchdog since its own in-flight hang; this is the walker's copy.
    private IDisposable? _stallWatchdog;

    // Long enough that a slow-but-normal confirm is never cut short, short enough
    // that a wedged walk self-heals instead of stranding the character.
    internal static readonly TimeSpan StallWatchdogInterval = TimeSpan.FromSeconds(10);

    // The active walk's planning flags, captured when a route is committed in
    // WalkToImmediate and reset in Reset(). A mid-walk replan (TryReplanOrFail)
    // must re-issue WalkTo with these, or a no-teleport (or gate-planned) walk
    // silently reverts to the defaults and takes a teleport it was told to avoid.
    private bool _activeAvoidTeleports;
    // True when the active walk prefers a pure-walking route and takes a teleport only when
    // walking is impossible. Preserved across re-plans so a walk that started on foot never
    // silently switches to a teleport a re-plan happens to find shorter (report: search-en-route
    // found the raft, then the re-plan pivoted onto the Black Wastelands vortex).
    private bool _activePreferTeleportFree;
    private bool _activeAvoidTraps;
    // True when the active walk overrides the user's avoid list (the picker's
    // "route through avoided rooms" choice). A mid-walk replan must keep it, or the
    // walk would re-honour the avoids it was told to cross and fail in place.
    private bool _activeIgnoreAvoids;
    private bool _activeThroughGates;
    private bool _activeArmAcquisition = true;
    private bool _activePickedRoute;

    // The trip under way, kept past each leg's own Reset (see WalkJourney). A walk
    // that takes the walker over silently is a leg of the journey standing: a detour
    // router's trip to a giver or a shop, and the leg from there on to where the
    // journey was going. Those are WalkTo calls of their own that state nothing, so
    // without this the leg after a route card's "ask the gnome commander" planned
    // through the very gate the card went round, and followed the automatic-walk
    // teleport setting on a walk the user started. Any other walk begins a journey
    // of its own. It ends when a leg to its destination arrives or fails, when the
    // walker is stopped, or when EndJourney says so; an internal Reset never ends it.
    private WalkJourney? _journey;
    // An owned leg isn't reported (see WalkJourney.OwnedLeg): to everything that
    // would save the journey and resume it later, no journey is under way.
    public WalkJourney? Journey => _journey is { OwnedLeg: true } ? null : _journey;

    // Where a journey to a stop-before boss room actually walks to (one room short).
    // A leg there is a leg to the journey's destination.
    private RoomKey? _journeyStopsAt;

    // Whether the leg under way is one to the journey's destination, so that its
    // arriving or failing ends the journey. Outlives Reset: the failure of a leg is
    // raised on either side of one, and a re-plan keeps the leg it re-plans.
    private bool _legToJourneyGoal;
    public bool LegIsToJourneyGoal => _legToJourneyGoal;

    private bool IsJourneyGoal(RoomKey destination) =>
        _journey is { } journey
        && (journey.Destination.Equals(destination) || destination.Equals(_journeyStopsAt));

    // Declares the journey a run of silent legs belongs to when no walk of the
    // walker's own begins it: a Shortcut card sends the walker to the item's source
    // first, through the detour walk, and that trip is the user's as much as a walk
    // straight to the destination. Replaces whatever journey was standing.
    public void BeginJourney(WalkJourney journey)
    {
        ArgumentNullException.ThrowIfNull(journey);
        _journey = journey;
        _journeyStopsAt = null;
        _legToJourneyGoal = false;
        _log?.Info("Walker", $"journey to {journey.Destination} begun: {journey.Describe()}");
    }

    // Ends the journey with no walk to stop: the walker sits idle between a
    // journey's legs (at a giver, waiting on the hand-over), and whatever halts
    // things then has no Stopped event to end it with.
    public void EndJourney()
    {
        _journey = null;
        _journeyStopsAt = null;
        _legToJourneyGoal = false;
    }

    // Picks a journey back up after an errand stopped its walk (a sell detour, a
    // flee, a party comeback): a walk to its destination by its own rules, so a
    // route that went round a gate still does. An errand that restarts through the
    // acquirable gates, to get back from wherever it ended, says so.
    //
    // The restart plans from wherever the errand ended, on a route nobody was shown.
    // It may enter the hazard rooms the user agreed to on the card, and no others: a
    // journey that names none (it was shown no card) has no hazard consent to carry.
    public bool ResumeJourney(WalkJourney journey, bool planThroughAcquirableGates = false)
    {
        ArgumentNullException.ThrowIfNull(journey);
        return WalkTo(
            journey.Destination,
            planThroughAcquirableGates: planThroughAcquirableGates || journey.ThroughGates,
            armItemAcquisition: journey.ArmAcquisition,
            avoidTeleports: journey.AvoidTeleports,
            avoidTraps: journey.AvoidTraps,
            ignoreAvoids: journey.IgnoreAvoids,
            preferTeleportFree: journey.PreferTeleportFree,
            pickedRoute: journey.PickedRoute,
            keepGatesClosedFor: journey.ClosedGates,
            agreedHazardRooms: journey.AgreedHazardRooms,
            fetch: journey.Fetch);
    }

    // The teleports a walk the client starts on its own may use, as (room, landing)
    // pairs. A walk the user starts states its own preference (the route cards pass
    // theirs, and a user's "Teleport" pick is the consent) and never reads this; a
    // walk nobody chose a route for states none, and used to take whichever vortex
    // was on the shortest route with nobody asked. Read as each such walk starts and
    // kept for the whole walk, so a change of the setting reaches the next walk and
    // never a route under way. Unwired, nothing is refused.
    private Func<IReadOnlySet<(RoomKey From, RoomKey To)>?>? _automaticWalkTeleports;

    // Wired by AppServices from Settings → Teleports (TeleportSettings.AutomaticWalkTeleports).
    public void SetAutomaticWalkTeleports(Func<IReadOnlySet<(RoomKey From, RoomKey To)>?>? allowed) =>
        _automaticWalkTeleports = allowed;

    // The setting as it stands now, for the bug report. Null: nothing is refused.
    public IReadOnlySet<(RoomKey From, RoomKey To)>? AutomaticWalkTeleports => _automaticWalkTeleports?.Invoke();

    // What a walk the client starts on its own is planned with: the movement filter
    // and the automatic-walk teleport list on top. For an engine that has to know
    // the route such a walk will take before it issues one: Auto-Lair waits in the
    // last room before the lair on it.
    public IRoomFilter? AutomaticWalkFilter() =>
        _automaticWalkTeleports?.Invoke() is { } allowed ? new AutomaticWalkTeleportFilter(_filter, allowed) : _filter;

    private IReadOnlySet<(RoomKey From, RoomKey To)>? _deferredWalkAutomaticTeleports;
    private IReadOnlySet<(RoomKey From, RoomKey To)>? _activeAutomaticTeleports;

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
        bool supersedeSilently = false,
        // ignoreAvoids: when true, plan through (and into) rooms the user marked
        // "avoid". The route picker's "route through avoided rooms" choice passes
        // true; every other caller keeps the default (false) so the avoid list is
        // honoured as before.
        bool ignoreAvoids = false,
        // preferTeleportFree: plan the pure-walking route when one exists, falling back to a
        // teleport hop only when walking is impossible, so a walk never silently teleports
        // on a re-plan. A walk the user started says which it is: every route-card commit
        // passes true but an explicit "Teleport" pick, which passes false. Null is a walk
        // nobody chose a route for, which follows the automatic-walk setting.
        bool? preferTeleportFree = null,
        // pickedRoute: the user was shown this route and chose it (the route picker's
        // commit), so a through-gates plan may cross a hazard room whose counter is
        // neither carried nor arranged — picking it was the consent. Every other
        // through-gates walk (a bank run, a trainer trip, a stash transfer) keeps such
        // a room closed: nobody agreed to walk into it.
        bool pickedRoute = false,
        // keepGatesClosedFor: gate items a through-gates plan must not count as in
        // hand. The picked route goes round the exits that need them (an optional
        // shortcut's item, which is never fetched), so a plan that assumed them
        // carried would take the shortcut and stop at its gate.
        IReadOnlyCollection<int>? keepGatesClosedFor = null,
        // agreedHazardRooms: with pickedRoute, the uncountered hazard rooms on the
        // route the user was shown. They are the only ones the plan (and each
        // re-plan, and a restart after an errand) may enter uncountered; left null
        // or empty, it enters none.
        IReadOnlyCollection<RoomKey>? agreedHazardRooms = null,
        // fetch: what this walk is to fetch on its way and the trades agreed for it.
        // Held on the journey this walk begins, so it is there for that journey's
        // legs and restarts and for no other walk.
        JourneyFetch? fetch = null,
        // ownedLeg: this walk is one leg of something that ends when its walk is
        // taken; its journey is not reported through Journey (WalkJourney.OwnedLeg).
        bool ownedLeg = false)
    {
        // A silent walk while a journey stands is one of its legs, and is planned by
        // the journey's rules where it states none of its own.
        if (supersedeSilently && !_replanningInPlace && _journey is { } standing)
        {
            bool toGoal = IsJourneyGoal(destination);
            if (toGoal)
            {
                // The leg on to where the journey was going is the journey's route.
                avoidTeleports |= standing.AvoidTeleports;
                avoidTraps |= standing.AvoidTraps;
                ignoreAvoids |= standing.IgnoreAvoids;
                preferTeleportFree ??= standing.PreferTeleportFree;
            }
            else if (standing.PreferTeleportFree is not null)
            {
                // A side trip (to a giver, a shop) is the user's walk, so the
                // automatic-walk list doesn't apply; but no card showed its route, so
                // it goes on foot unless that is impossible. The teleport a card named
                // was agreed for the journey's own route only, and "Walk it, don't
                // teleport" is a refusal that holds for the side trip too.
                preferTeleportFree ??= true;
                avoidTeleports |= standing.AvoidTeleports;
            }
            _log?.Info("Walker",
                $"leg to {destination} of the journey to {standing.Destination} ({(toGoal ? "its destination" : "a side trip")}): "
                + (avoidTeleports ? "never teleports"
                    : preferTeleportFree switch
                    {
                        true => "on foot unless walking is impossible",
                        false => "the picked route's teleports",
                        null => "teleports by the automatic-walk setting",
                    })
                + (standing.ClosedGates is { Count: > 0 } closed
                    ? $", going round the gates that need item(s) {string.Join("/", closed)}" : ""));
        }

        // Settled once per walk, before the Reset below clears the walk it replaces:
        // an in-place re-plan keeps the list its walk began with, a fresh automatic
        // walk reads the setting, and a walk that states a preference has none.
        IReadOnlySet<(RoomKey From, RoomKey To)>? automaticTeleports =
            _replanningInPlace ? _activeAutomaticTeleports
            : preferTeleportFree is null ? _automaticWalkTeleports?.Invoke()
            : null;
        bool preferFree = preferTeleportFree ?? false;
        // The boss-room choice belongs to the walk it was made for. A walk's own
        // re-plan keeps it and so does a detour that takes the walker over silently
        // and hands it back; any other new walk starts without one.
        if (!_replanningInPlace)
        {
            if (_pendingBossRule is not null) _bossRule = _pendingBossRule;
            else if (!supersedeSilently) _bossRule = null;
            _pendingBossRule = null;
            if (!supersedeSilently) _stopShortOf = null;
        }
        if (preferTeleportFree is null && !_replanningInPlace && automaticTeleports is not null)
            _log?.Info("Walker",
                $"automatic walk to {destination}: may use {automaticTeleports.Count} allowed teleport(s) (Settings → Teleports)");

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

        // After the Stop above, which ends the journey it supersedes. A silent walk
        // with no journey standing is nobody's leg, so it is a journey of its own
        // (the walk on from a token's landing).
        if (!_replanningInPlace)
        {
            if (!supersedeSilently || _journey is null)
            {
                _journey = new WalkJourney(
                    destination, planThroughAcquirableGates, armItemAcquisition, avoidTeleports, avoidTraps,
                    ignoreAvoids, preferTeleportFree, pickedRoute,
                    keepGatesClosedFor is { Count: > 0 } ? keepGatesClosedFor : null,
                    pickedRoute ? agreedHazardRooms : null,
                    fetch,
                    ownedLeg);
                _journeyStopsAt = null;
                _abandonedDoors.Clear();
            }
            if (_carriedAbandonedDoors is { } carried)
            {
                _abandonedDoors.UnionWith(carried);
                _carriedAbandonedDoors = null;
            }
            _legToJourneyGoal = IsJourneyGoal(destination);
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
            _deferredWalkIgnoreAvoids = ignoreAvoids;
            _deferredWalkPreferTeleportFree = preferFree;
            _deferredWalkAutomaticTeleports = automaticTeleports;
            _deferredWalkPickedRoute = pickedRoute;
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

        return WalkToImmediate(destination, planThroughAcquirableGates, armItemAcquisition, avoidTeleports, avoidTraps, ignoreAvoids, preferFree, pickedRoute, automaticTeleports);
    }

    private bool WalkToImmediate(
        RoomKey destination,
        bool planThroughAcquirableGates = false,
        bool armItemAcquisition = true,
        bool avoidTeleports = false,
        bool avoidTraps = false,
        bool ignoreAvoids = false,
        bool preferTeleportFree = false,
        bool pickedRoute = false,
        IReadOnlySet<(RoomKey From, RoomKey To)>? automaticTeleports = null)
    {
        // Callers may arrive here from the WalkTo entry (Idle) OR from
        // the deferred dispatch in OnTrackerStateChanged (Walking with
        // _path == null). Either way the next few branches need a
        // clean slate — Reset takes us to Idle and clears any stale
        // _destination so failures don't leave the walker stuck.
        Reset();
        // Innermost, so the teleport and boss-room rules of the walk wrap it as they
        // wrap the movement filter.
        IRoomFilter? doorsRefused = _abandonedDoors.Count == 0 ? null : new AbandonedDoorsFilter(_filter, _abandonedDoors);
        _walkFilter = automaticTeleports is null
            ? doorsRefused
            : new AutomaticWalkTeleportFilter(doorsRefused ?? _filter, automaticTeleports);
        _bossRuleActive = _bossRule is { } bossRule && bossRule.IsFor(destination);
        if (_bossRuleActive && _bossRule!.WalkAround is { } aroundRooms)
            _walkFilter = new WalkAroundRoomsFilter(_walkFilter ?? _filter, aroundRooms);

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
        // A walk told to go in (SetBossRoomRule's enterDestination) keeps its room.
        RoomKey requested = destination;
        if (_bossRuleActive && _bossRule!.EntersDestination)
        {
            if (_bossStopRooms?.Invoke().Contains(destination) == true && !source.Key.Equals(destination))
                _log?.Info("Walk",
                    $"stop-before boss room {destination.Map}/{destination.Room}: this walk was told to enter it, walking in");
        }
        else
        {
            destination = ApplyStopBefore(source.Key, destination);
            if (!destination.Equals(requested))
            {
                _stopShortOf = (requested, destination);
                // A detour router resumes to where the walker was heading, which is
                // this room, not the boss room the journey names.
                if (_legToJourneyGoal) _journeyStopsAt = destination;
            }
        }
        if (_bossRuleActive) _bossRule!.NoteWalkedTo(requested, destination);

        if (source.Key.Equals(destination))
        {
            Raise(new WalkEvent(WalkEventKind.Finished, "already at destination", destination, TakeStopShortOf(destination)));
            return true;
        }

        // Route-scoped @wealth warm-up: probes the party only when this walk's
        // tolls-permitted route actually crosses a toll (no-op otherwise).
        Filter?.WarmForRoute(_bfs, source.Key, destination);

        // The route picker's "direct" choice plans as if every acquirable gate
        // item were already carried — suspend those gates for the FindPath +
        // Expand pass so BFS returns the gated shortcut rather than the free
        // detour. Level / toll / class gates stay active regardless. Disposed
        // before any stepping so the live filter re-gates for mid-walk replans.
        // A route nobody picked keeps an uncountered hazard room closed, and every
        // leg of a journey keeps the gates its picked route goes round closed.
        // A picked route whose journey names the hazard rooms the user agreed to opens
        // those rooms and no others: agreeing to the river on the card is not
        // agreeing to whatever hazard lies on a re-plan from somewhere else. There is
        // no way to open them all: a picked walk that names none, or whose journey
        // has ended under it (a profile load mid-walk), enters none.
        IReadOnlyCollection<int>? closedGates = _journey?.ClosedGates;
        IReadOnlyCollection<RoomKey>? agreedHazards = pickedRoute ? _journey?.AgreedHazardRooms : null;
        IDisposable? gateScope = !planThroughAcquirableGates ? null
            : closedGates is not null || agreedHazards is { Count: > 0 }
                ? Filter?.SuspendAcquirableGatesExcept(
                    closedGates ?? Array.Empty<int>(),
                    keepUncounteredHazards: true,
                    openHazardRooms: agreedHazards)
            : Filter?.SuspendAcquirableGatesButUncounteredHazards();
        IReadOnlyList<Direction>? path;
        IReadOnlyList<WalkStep> expanded;
        List<UnroutableLeverLeg> unroutable = new();
        BoatRoutePlan? boatPlan = null;
        SysopGotoRoutePlan? sysGotoPlan = null;
        try
        {
            // preferTeleportFree: try the pure-walking route FIRST and only fall back to a
            // teleport hop when walking is genuinely impossible — so a walk that didn't begin
            // on a teleport (every user-picked route except an explicit "Teleport" choice)
            // never silently switches to a vortex on a mid-walk re-plan. A hard avoidTeleports
            // still refuses teleports outright with no fallback.
            path = _bfs.FindPath(source.Key, destination, Filter,
                refuseTeleports: avoidTeleports || preferTeleportFree, avoidTraps: avoidTraps, ignoreAvoids: ignoreAvoids);
            if (path is null && preferTeleportFree && !avoidTeleports)
                path = _bfs.FindPath(source.Key, destination, Filter,
                    refuseTeleports: false, avoidTraps: avoidTraps, ignoreAvoids: ignoreAvoids);

            // A sea-captain sailing can beat (or replace) the land route. Weigh
            // the boat's stitched land-legs against the pure land route; the
            // planner returns a plan only when it wins by the boat-overhead
            // margin, or when there's no land route at all and a sail is the
            // sole crossing.
            int? landHops = path is { Count: > 0 } ? path.Count : (int?)null;
            boatPlan = ChooseBoatRoute(source.Key, destination, landHops);
            // A sailing whose fare our own purse can't pay, or that the party in tow
            // can't all be got aboard (PartyTollGate), is not set out for, sole
            // crossing or not: the captain leaves whoever can't pay on the dock, so
            // the walk would only stand there. Kept to name it if nothing else gets
            // there. One gated on level still sails with its warning.
            BoatRoutePlan? unpaidBoat = null;
            if (boatPlan is { } gatedBoat && gatedBoat.Block.HasFlag(ExitBlockReason.Fare))
            {
                unpaidBoat = gatedBoat;
                boatPlan = null;
            }

            // A sys-goto jump can also beat (or replace) the land route — weigh it
            // the same way. When BOTH a boat and a jump qualify, keep the one with
            // the cheaper weighted land-hop cost so we never build two shortcuts.
            sysGotoPlan = ChooseSysGotoRoute(source.Key, destination, landHops);
            if (boatPlan is { } b && sysGotoPlan is { } s)
            {
                if (s.LandHops + SysGotoHopWeight < b.LandHops + BoatHopWeight) boatPlan = null;
                else sysGotoPlan = null;
            }

            if (boatPlan is { } chosen)
            {
                LogPaidCrossings(source.Key, chosen.ToDock, destination);
                LogBoatFare(chosen.Passage, destination);
                LogPaidCrossings(chosen.Passage.ArrivalRoom, chosen.FromArrival, destination);
                expanded = BuildBoatWalk(source.Key, chosen);
            }
            else if (sysGotoPlan is { } chosenGoto)
            {
                expanded = BuildSysGotoWalk(chosenGoto);
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

                // An automatic walk stopped by a teleport it may not use says which
                // one, so the user knows what to tick (or that the trip can't be made).
                if (_walkFilter is AutomaticWalkTeleportFilter teleports
                    && DescribeRefusedTeleport(source.Key, destination, teleports) is { } refused)
                {
                    Raise(new WalkEvent(WalkEventKind.Failed, refused, destination));
                    return false;
                }

                if (unpaidBoat is { } sail)
                {
                    BoatPassage passage = sail.Passage;
                    bool partys = !PaidCrossingDescriber.OwnPurseShortOfFare(Filter, passage.ArrivalRoom, passage.FareCopper);
                    string cantSail = "all routes blocked by "
                        + PaidCrossingDescriber.DescribeBoat(in passage, RoomNameOf) + " " + PaidCrossingDescriber.WhoCantPay(partys)
                        + (PaidCrossingDescriber.PurseForFare(Filter, passage.ArrivalRoom, passage.FareCopper) is { } purse
                            ? $": {purse}" : string.Empty);
                    FailUnpaid(destination, cantSail);
                    return false;
                }

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
                // The rooms closed to routes stay closed for that probe: acquiring
                // nothing opens them, and with them open the shortest way cut
                // across the lake and the lake was named for a walk whose real
                // want was a door key. They are opened only when nothing else
                // gets there, so that the lake is named when it is the reason.
                IReadOnlyList<Direction>? describePath;
                using (Filter?.SuspendAcquirableGatesButUnprotectableHazards())
                    describePath = _bfs.FindPath(source.Key, destination, Filter);
                if (describePath is null || describePath.Count == 0)
                    using (Filter?.SuspendAcquirableGates())
                        describePath = _bfs.FindPath(source.Key, destination, Filter);
                if (describePath is null || describePath.Count == 0)
                    describePath =
                        _bfs.FindPath(source.Key, destination, Filter, ignoreExitGates: true);

                // DescribeBlockedRoute runs with gating restored (the suspension
                // scope has closed), so DescribeExitBlock reports the real
                // acquirable-gate reasons on the front-door route's hops.
                bool unpaid = false;
                string reason = describePath is { Count: > 0 }
                    ? DescribeBlockedRoute(source.Key, describePath, out unpaid)
                    : DescribeNoPlainRoute(source.Key, destination);
                if (unpaid) FailUnpaid(destination, reason);
                else Raise(new WalkEvent(WalkEventKind.Failed, reason, destination));
                return false;
            }
            else
            {
                // A lever exit out of the room we're in that already shows open is
                // walked straight through rather than detoured to its levers.
                IReadOnlySet<Direction>? openHere = _tracker.State.CurrentRoom?.Key.Equals(source.Key) == true
                    ? _tracker.ShownOpenExits()
                    : null;
                LogPaidCrossings(source.Key, path, destination);
                expanded = RemoteActionPathExpander.Expand(_graph, source.Key, path, _bfs, Filter, _log, openHere, unroutable);
            }
        }
        finally { gateScope?.Dispose(); }

        // A lever the route's exit needs sits somewhere the crosser can't walk to.
        // Name the exit, the room and what's in the way, ahead of the generic
        // failures below (which read "path expansion empty" when the exit is the
        // walk's first step). The innermost exit comes first when the lever sits
        // behind another lever exit.
        if (unroutable.Count > 0 && boatPlan is null
            && !RemoteActionPathExpander.ReachesDestination(expanded, destination))
        {
            string reason = DescribeUnroutableLever(unroutable[0]);
            Raise(new WalkEvent(WalkEventKind.Failed, reason, destination));
            return false;
        }

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
        _activePreferTeleportFree = preferTeleportFree;
        _activeAutomaticTeleports = automaticTeleports;
        _activeAvoidTraps = avoidTraps;
        _activeIgnoreAvoids = ignoreAvoids;
        _activeThroughGates = planThroughAcquirableGates;
        _activeArmAcquisition = armItemAcquisition;
        _activePickedRoute = pickedRoute;
        _origin = source.Key;
        _retryCount = 0;
        _revealRetries = 0;
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
        NoteDoorsWalkedRound(source.Key, boatPlan is null && sysGotoPlan is null ? path : null);
        if (boatPlan is null && sysGotoPlan is null && !_replanningInPlace && path is not null)
            NoteLakeCrossing(source.Key, destination, path);

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

        // The announce comes before the first step so that a hold it raises (the
        // party being asked who holds a route item) keeps that step back: the
        // answer may turn the walk aside for a shop.
        if (_coordinator.IsPaused)
        {
            // A gate the announce itself asserted has paused us and said so already.
            if (State != WalkState.Paused)
            {
                State = WalkState.Paused;
                Raise(new WalkEvent(WalkEventKind.Paused, "coordinator paused", destination));
            }
            return true;
        }

        SendNextStep();
        return true;
    }

    // Walk the graph along the planned directions, collecting the item ids of
    // every possession-gated exit crossed AND the mandatory counter item of every
    // hazard room entered. The result is the set of items the route requires the
    // character to carry; the demand tracker decides which are missing. Cheap
    // (one dictionary lookup per hop) and side-effect-free — skipped entirely
    // when no announcer is bound.
    private void AnnouncePlannedItemRequirements(RoomKey source, IReadOnlyList<Direction> path)
    {
        if (_pathItemAnnouncer is null) return;

        var required = new List<int>();
        RoomKey cur = source;
        foreach (Direction dir in path)
        {
            Room? room = _graph.GetRoom(cur);
            if (room is null || !room.Exits.TryGetValue(dir, out RoomExit exit))
                break;
            ExitGateItems.Collect(in exit, required);
            // ExitGateItems excludes key gates on purpose (a key is one of several
            // openers). Re-admit exactly the key whose acquisition chain is
            // deterministic end to end — see SetDoorKeySourceProbe.
            //
            // Unlike an Item/Ticket gate, crossing a key door does NOT imply
            // needing the key: pick and bash open it too. So the live filter has to
            // agree the door is actually impassable first — a thief who can pick
            // 8/461's south gate must not be sent to fight the obsidian statue for
            // a key they don't need. The planning pass has already released its
            // gate suspension by this point, so the filter reads true here.
            if (exit.Hint == RoomExitHint.KeyLocked && exit.KeyItemId > 0
                && _doorKeySummonable is { } summonable && summonable(exit.KeyItemId)
                && Filter?.DescribeExitBlock(in exit).HasFlag(ExitBlockReason.LockedDoor) == true
                && !required.Contains(exit.KeyItemId))
                required.Add(exit.KeyItemId);
            // The hazard sits on the room being entered, so resolve the hop's
            // target — a free route never crosses hazard rooms (the filter
            // blocks them), so this only fires on a chosen gated route.
            if (_hazardItemResolver is { } hazardOf)
                foreach (int itemId in hazardOf(exit.Target))
                    if (itemId > 0 && !required.Contains(itemId))
                        required.Add(itemId);
            cur = exit.Target;
        }

        if (required.Count > 0) _pathItemAnnouncer(required);
    }

    // Says which doors the plan steps round: a door out of a room on the route into
    // a room further along it, which the filter keeps routes off (it can't be opened,
    // or only at poor odds) or which this trip already gave up on. A route three
    // steps long beside a one-step door otherwise looks like a planning mistake, in
    // the log and to whoever reads a bug report. Doors off the route aren't looked
    // at: a door nobody was going to use isn't one that was avoided.
    private void NoteDoorsWalkedRound(RoomKey source, IReadOnlyList<Direction>? path)
    {
        _doorsWalkedRound.Clear();
        if (path is null || Filter is not { } filter) return;

        IReadOnlyList<RoomKey> route = ExpandRouteKeys(source, path);
        Dictionary<RoomKey, int> lastVisit = new(route.Count);
        for (int i = 0; i < route.Count; i++) lastVisit[route[i]] = i;

        for (int i = 0; i < route.Count - 1; i++)
        {
            if (_graph.GetRoom(route[i]) is not { } room) continue;
            foreach ((Direction dir, RoomExit exit) in room.Exits)
            {
                if (exit.Hint is not (RoomExitHint.Door or RoomExitHint.KeyLocked)) continue;
                if (exit.Target.Equals(route[i + 1])) continue;   // the step the plan takes
                if (!lastVisit.TryGetValue(exit.Target, out int ahead) || ahead <= i) continue;
                string? why = _abandonedDoors.Contains((room.Key, exit.Target))
                    ? "couldn't be opened earlier on this trip"
                    : filter.DescribeDoorRefusal(in exit);
                if (why is null) continue;
                _doorsWalkedRound.Add(
                    $"the door {dir.ToLongName()} from {room.Key} ({room.Name}) to {exit.Target}: {why}");
            }
        }
        foreach (string door in _doorsWalkedRound)
            _log?.Info("Walker", $"walk to {_destination}: going round {door}");
    }

    // A plain route that enters rooms closed to routes is BfsMapper.FindCrossing's:
    // a place the map reaches no other way, crossed to by a crosser who meets the
    // rooms' terms. Said once at plan time, since nothing else in the log tells that
    // walk from one that went round. A walk that starts inside such a room is only
    // being planned out of it, and the room that teleports on arrival is the route
    // card's own crossing, which the pick already logged.
    private void NoteLakeCrossing(RoomKey source, RoomKey destination, IReadOnlyList<Direction> path)
    {
        if (_log is null || Filter is not { } filter
            || filter.IsClosedToRoutes(source) || filter.TeleportsOnArrival(destination)) return;
        List<RoomKey> closed = ExpandRouteKeys(source, path).Skip(1).Where(filter.IsClosedToRoutes).ToList();
        if (closed.Count == 0 || filter.CrossingTerms(closed[0]) is not { } terms) return;

        string items = string.Join(" or ", terms.Items.Select(id => _itemNameResolver?.Invoke(id) ?? $"item #{id}"));
        _log.Info("Walker",
            $"walk to {destination}: crosses {closed.Count} teleporting room(s), {closed[0]} to {closed[^1]}; "
            + $"allowed because no other way there exists and the character is level {terms.MinLevel}+ with {items} in the pack");
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
        // the sail is then the sole crossing, so surfacing it beats a bare "no
        // path": the caller sails it with a warning that a member may be refused at
        // the dock, unless it is our own purse that can't pay the fare, when it
        // names the sailing in the walk's failure. With a land route in hand, a
        // gated boat is skipped so we never split the party for a crossing a member
        // can't make.
        if (_boatPlanner.TryPlan(source, destination, Filter, allowGated: landHops is null)
            is not { } plan)
            return null;

        // A land route exists — the boat must beat it by the overhead margin.
        if (landHops is { } hops && plan.LandHops + BoatHopWeight >= hops)
            return null;

        // Committing to a fare- or level-gated sailing primes the party's wealth
        // / level readings (async), so a re-plan gates on fresh numbers rather
        // than stale ones — the same best-effort warm a toll on a land route gets.
        if (plan.Passage.FareCopper > 0 || plan.Passage.MinLevel > 0)
            Filter?.WarmForBoat();

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
            steps.AddRange(RemoteActionPathExpander.Expand(_graph, source, plan.ToDock, _bfs, Filter, _log));
        steps.Add(new BoatStep(plan.Passage));
        if (plan.FromArrival.Count > 0)
            steps.AddRange(RemoteActionPathExpander.Expand(
                _graph, plan.Passage.ArrivalRoom, plan.FromArrival, _bfs, Filter, _log));
        return steps;
    }

    // A sys-goto jump can shortcut the land route: fire `sys goto <name>` from
    // wherever we stand and walk the (shorter) leg from the landing. Take it only
    // when it beats the land route by the overhead margin — or when there's no land
    // route at all and the jump is the sole crossing. Level-gated locations are
    // already excluded at planning time (unknown level → excluded).
    private SysopGotoRoutePlan? ChooseSysGotoRoute(RoomKey source, RoomKey destination, int? landHops)
    {
        if (_sysGotoPlanner is null || _sysGotoFire is null) return null;
        if (_sysGotoPlanner.TryPlan(source, destination, Filter) is not { } plan) return null;
        if (landHops is { } hops && plan.LandHops + SysGotoHopWeight >= hops) return null;
        return plan;
    }

    // Expand a sys-goto plan into one ordered step list: the SysGotoStep jump
    // (fired from the current room — no leg before it), then the land leg from the
    // landing room to the goal, expanded like any land route so doors / traps /
    // hidden exits along it are handled.
    private IReadOnlyList<WalkStep> BuildSysGotoWalk(SysopGotoRoutePlan plan)
    {
        List<WalkStep> steps = new() { new SysGotoStep(plan.Location) };
        if (plan.FromArrival.Count > 0)
            steps.AddRange(RemoteActionPathExpander.Expand(
                _graph, plan.LandingRoom, plan.FromArrival, _bfs, Filter, _log));
        return steps;
    }

    // Name why the only route to the destination is blocked. The gates-ignored
    // path (the one that surfaced when we re-probed) is exactly the set of
    // exits the crosser can't clear, so classifying each hop and unioning the
    // reasons tells the user the real obstacle — a locked door, a missing item,
    // a level window, a toll, a class hall, or a room hazard — instead of the
    // old fixed "level, toll, or class" line that misnamed a key-door block.
    //
    // unpaid: a toll or fare the crosser can't pay is among the reasons.
    private string DescribeBlockedRoute(RoomKey source, IReadOnlyList<Direction> ungatedPath, out bool unpaid)
    {
        unpaid = false;
        ExitBlockReason reasons = ExitBlockReason.None;
        // The first toll and the first paid transport that turn the crosser away,
        // kept whole so the message names the room, the way, the price and how
        // the purse stands against it.
        (RoomKey From, Direction Dir, RoomExit Exit)? tollGate = null, fareGate = null;
        List<int> missingItems = new();
        // The first level-gated hop's target + window, so the message can name the
        // actual barrier room and level instead of a bare "a level requirement".
        (RoomKey Room, int Min, int Max)? levelGate = null;
        // The first door hop (locked or plain), kept whole so the message can name
        // the room it's in, the direction, and the key / picklocks-strength it needs
        // — directional, from the blocking room's own exit, so it can't be confused
        // with the far side (which may have a different requirement entirely).
        (RoomKey From, Direction Dir, RoomExit Exit)? doorGate = null;
        // The rooms on the way that no route enters whatever is carried (Crystal
        // Lake's sea rooms): how many, and the first.
        (RoomKey First, int Count)? closedRooms = null;
        RoomKey cur = source;
        foreach (Direction dir in ungatedPath)
        {
            Room? room = _graph.GetRoom(cur);
            if (room is null || !room.Exits.TryGetValue(dir, out RoomExit exit))
                break;
            if (Filter is { } f)
            {
                ExitBlockReason hop = f.DescribeExitBlock(in exit);
                reasons |= hop;
                if (hop.HasFlag(ExitBlockReason.Hazard) && f.IsClosedToRoutes(exit.Target))
                    closedRooms = (closedRooms?.First ?? exit.Target, (closedRooms?.Count ?? 0) + 1);
                if (hop.HasFlag(ExitBlockReason.Item)) ExitGateItems.Collect(in exit, missingItems);
                if (hop.HasFlag(ExitBlockReason.Level) && levelGate is null)
                    levelGate = (exit.Target, exit.MinLevel, exit.MaxLevel);
                if ((hop.HasFlag(ExitBlockReason.LockedDoor) || hop.HasFlag(ExitBlockReason.Door))
                    && doorGate is null)
                    doorGate = (cur, dir, exit);
                if (hop.HasFlag(ExitBlockReason.Toll)) tollGate ??= (cur, dir, exit);
                if (hop.HasFlag(ExitBlockReason.Fare)) fareGate ??= (cur, dir, exit);
            }
            cur = exit.Target;
        }
        // Nothing else on the way matters once it runs into these: no item, key or
        // level opens them, so they are the whole of the reason.
        if (closedRooms is { } closed && Filter is { } filter)
            return DescribeClosedRooms(source, cur, closed.First, closed.Count, filter);
        unpaid = tollGate is not null || fareGate is not null;
        return FormatBlockReasons(reasons, missingItems, levelGate, doorGate, tollGate, fareGate);
    }

    // Why a walk that would have to go through rooms closed to routes has no route,
    // in one line. Mostly: only typed moves go into them (user, 2026-10-10). Where
    // the map has no other way there, a crossing exists on terms (BfsMapper.
    // FindCrossing), so the line says what the crosser lacks; and for the room that
    // teleports on arrival, that the crossing is a route card's, on a walk the user
    // starts.
    private string DescribeClosedRooms(RoomKey source, RoomKey destination, RoomKey first, int count, IRoomFilter filter)
    {
        if (filter.IsClosedToRoutes(destination) || !_bfs.IsCutOffByClosedRooms(source, destination, filter)
            || filter.CrossingTerms(first) is not { } terms)
            return $"no route: the way there crosses {count} teleporting room(s), from {NameRoom(first)} on, "
                + "and only typed moves go into those";

        // No count here: the crossing that would be made takes the fewest such rooms,
        // which is not the way this probe came.
        string rooms = $"the teleporting rooms from {NameRoom(first)} on";

        string items = string.Join(" or ", terms.Items.Select(id => _itemNameResolver?.Invoke(id) ?? $"item #{id}"));
        string asks = $"level {terms.MinLevel} and {items} in your pack";
        if (!terms.Met)
        {
            List<string> missing = new();
            if (!terms.LevelMet) missing.Add($"level {terms.MinLevel}");
            if (!terms.ItemHeld) missing.Add(items);
            return $"no route: the only way there is across {rooms}, which takes {asks} (missing: {string.Join(" and ", missing)})";
        }
        return filter.TeleportsOnArrival(destination)
            ? $"no route: the only way there is across {rooms}, a crossing offered only on the route card of a walk you start yourself"
            : $"no route: the only way there is across {rooms}, and no crossing of them could be planned";
    }

    // Why an automatic walk has no route, and which box would give it one (user,
    // 2026-10-10: "tell them it was refused because this is an automatic walk, and
    // no routes are avail, recommend which gates to check"). The route the walk
    // would take with the allow-list lifted shows the teleports the list refuses.
    // One of them alone may be enough, since another way round can need only that
    // one, so each is tried by itself before all of them are named together. Null
    // when lifting the list finds no route either: the walk is blocked by something
    // else and the usual wording names that.
    private string? DescribeRefusedTeleport(RoomKey source, RoomKey destination, AutomaticWalkTeleportFilter teleports)
    {
        IReadOnlyList<Direction>? open = _bfs.FindPath(source, destination, _filter);
        if (open is null) return null;
        List<(RoomKey From, RoomKey To)> refused = new();
        RoomKey at = source;
        foreach (Direction dir in open)
        {
            if (_graph.GetRoom(at) is not { } room || !room.Exits.TryGetValue(dir, out RoomExit exit)) break;
            if (AutomaticWalkTeleportFilter.IsTeleport(in exit) && teleports.IsExitRefused(at, in exit)
                && !refused.Contains((at, exit.Target)))
                refused.Add((at, exit.Target));
            at = exit.Target;
        }
        if (refused.Count == 0) return null;

        const string Where = "Settings → Teleports (Allow automatic walks to use the following teleports)";
        const string Why = "no route: this is an automatic walk, and ";
        foreach ((RoomKey From, RoomKey To) exit in refused)
        {
            (string title, IReadOnlyList<(RoomKey From, RoomKey To)> exits) = TeleportLine(exit);
            if (refused.Count > 1 && _bfs.FindPath(source, destination, teleports.AlsoAllowing(exits)) is null) continue;
            return $"{Why}the way there uses a teleport it isn't allowed. Tick \"{title}\" on {Where} to open it.";
        }
        string all = string.Join("; ", refused.Select(exit => $"\"{TeleportLine(exit).Title}\"").Distinct());
        // Only the teleports on that one route were tried, so that is all it claims.
        return $"{Why}the shortest way there uses {refused.Count} teleports it isn't allowed; ticking any one of them "
            + $"alone opens no route. It needs all of these on {Where}: {all}.";
    }

    // The teleport spots as Settings → Teleports lists them (AppServices.TeleportChoices).
    private Func<IReadOnlyList<TeleportChoice>>? _teleportChoices;
    public void SetTeleportChoices(Func<IReadOnlyList<TeleportChoice>> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        _teleportChoices = choices;
    }

    // The Settings → Teleports line a teleport belongs to, by its on-screen title,
    // and the exits that line's box allows (a two-way spot is one line, listed from
    // either end). Unwired, or for a teleport the list doesn't hold, the title is
    // built the way the list builds a one-way line's.
    private (string Title, IReadOnlyList<(RoomKey From, RoomKey To)> Exits) TeleportLine((RoomKey From, RoomKey To) exit)
    {
        if (_teleportChoices?.Invoke().FirstOrDefault(c => c.Exits.Contains(exit)) is { } line)
            return (line.Title, line.Exits);
        string title = TeleportChoice.TitleOf(
            exit.From, _graph.GetRoom(exit.From)?.Name ?? "?", exit.To, _graph.GetRoom(exit.To)?.Name ?? "?", twoWay: false);
        return (title, new[] { exit });
    }

    // "the exit east of 14/10218 (Small Chamber) is opened from 14/10329 (Central
    // Water Main), which can't be reached from 14/10218: all routes blocked by a
    // room hazard you can't survive — walk there first" — the same gate wording a
    // blocked walk gets, for the leg to a lever room. Walking to that room directly
    // puts the obstacle on the route itself, where the route picker offers its
    // usual ways across.
    private string DescribeUnroutableLever(UnroutableLeverLeg leg)
    {
        IReadOnlyList<Direction>? probe;
        using (Filter?.SuspendAcquirableGates())
            probe = _bfs.FindPath(leg.From, leg.To, Filter);
        if (probe is null || probe.Count == 0)
            probe = _bfs.FindPath(leg.From, leg.To, Filter, ignoreExitGates: true);
        string why = probe is { Count: > 0 }
            ? DescribeBlockedRoute(leg.From, probe, out _)
            : DescribeNoPlainRoute(leg.From, leg.To);

        string exit = $"the exit {leg.GateDir.ToLongName()} of {NameRoom(leg.GateRoom)}";
        return leg.To.Equals(leg.GateRoom)
            ? $"{exit} can't be walked back to from {NameRoom(leg.From)}, where it's opened: {why}"
            : $"{exit} is opened from {NameRoom(leg.To)}, which can't be reached from {NameRoom(leg.From)}: {why} — "
              + $"walk to {NameRoom(leg.To)} first";
    }

    private string NameRoom(RoomKey key) =>
        _graph.GetRoom(key)?.Name is { Length: > 0 } name ? $"{key} ({name})" : key.ToString();

    // No gated route resolved even with gates ignored. Before reporting a bare
    // "no path", check whether the ONLY thing walling the destination off is a
    // user-set avoid — if lifting the avoids opens a route, name the offending
    // room so the user knows their own avoid is the block, not a map dead-end.
    private string DescribeNoPlainRoute(RoomKey source, RoomKey destination)
    {
        if (_bfs.FirstAvoidBlockingRoute(source, destination, Filter) is { } blocked)
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
        return _bfs.FirstAvoidBlockingRoute(source, destination, Filter);
    }

    private string FormatBlockReasons(ExitBlockReason reasons, IReadOnlyList<int> missingItems,
        (RoomKey Room, int Min, int Max)? levelGate,
        (RoomKey From, Direction Dir, RoomExit Exit)? doorGate,
        (RoomKey From, Direction Dir, RoomExit Exit)? tollGate,
        (RoomKey From, Direction Dir, RoomExit Exit)? fareGate)
    {
        // Classification came up empty (e.g. a bare IRoomFilter with no gate
        // model) — keep a truthful generic line rather than inventing a cause.
        if (reasons == ExitBlockReason.None)
            return "all routes blocked by an exit requirement";

        List<string> parts = new();
        if (reasons.HasFlag(ExitBlockReason.Level)) parts.Add(DescribeLevelGate(levelGate));
        if (reasons.HasFlag(ExitBlockReason.Toll))
            parts.Add(DescribeUnpaidCrossing(tollGate) ?? "a toll you can't afford");
        if (reasons.HasFlag(ExitBlockReason.Fare))
            parts.Add(DescribeUnpaidCrossing(fareGate) ?? "a paid transport a party member can't afford");
        if (reasons.HasFlag(ExitBlockReason.Class)) parts.Add("a class restriction");
        if (reasons.HasFlag(ExitBlockReason.Race)) parts.Add("a race restriction");
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

    // "a toll east from 1/1381 (Town Gates, Inner Bailey) (5 gold) you can't pay: you
    // carry 0 copper, 5 gold short" — the crossing, its price and the purse it was
    // judged on, since a purse the client has wrong is the usual reason this
    // surprises. Null when the filter in use says nothing of purses.
    private string? DescribeUnpaidCrossing((RoomKey From, Direction Dir, RoomExit Exit)? gate)
    {
        if (gate is not { } g) return null;
        RoomExit exit = g.Exit;
        return PaidCrossingDescriber.DescribeUnpaid(g.From, g.Dir, in exit, Filter, RoomNameOf);
    }

    private string? RoomNameOf(RoomKey key) => _graph.GetRoom(key)?.Name;

    // The tolls and fares a planned route pays, with the purse they were allowed on:
    // the line that shows a walk went through a toll on a coin count that was wrong
    // (report paradigm-20261010-145529, where nothing in the log said why).
    private void LogPaidCrossings(RoomKey source, IReadOnlyList<Direction> path, RoomKey destination)
    {
        if (_log is null || Filter is not { } filter) return;
        string? first = null;
        int crossings = 0;
        RoomKey cur = source;
        foreach (Direction dir in path)
        {
            if (_graph.GetRoom(cur) is not { } room || !room.Exits.TryGetValue(dir, out RoomExit exit)) break;
            if (filter.DescribePurseFor(in exit) is { } purse)
            {
                first ??= $"{PaidCrossingDescriber.Describe(cur, dir, in exit, RoomNameOf)}: {purse}";
                crossings++;
            }
            cur = exit.Target;
        }
        if (first is null) return;
        _log.Info("Walker", $"walk to {destination} pays {first}"
            + (crossings > 1 ? $" (and {crossings - 1} more toll(s) or fare(s) further on)" : string.Empty));
    }

    // The same line for a sailing the route boards.
    private void LogBoatFare(in BoatPassage passage, RoomKey destination)
    {
        if (_log is null || passage.FareCopper <= 0) return;
        if (PaidCrossingDescriber.PurseForFare(Filter, passage.ArrivalRoom, passage.FareCopper) is not { } purse) return;
        _log.Info("Walker", $"walk to {destination} pays {PaidCrossingDescriber.DescribeBoat(in passage, RoomNameOf)}: {purse}");
    }

    // A walk with no route for want of a toll or fare says so where the user is
    // looking, not only on the Navigation window's chip. (The log line is
    // Raise's, as for every failed walk.)
    private void FailUnpaid(RoomKey destination, string reason)
    {
        LastUnpaidFailure = destination;
        _unpaidCrossingHandler?.Invoke(destination, reason);
        Raise(new WalkEvent(WalkEventKind.Failed, reason, destination));
    }

    // Where the walk just asked for was going, when it had no route for want of a
    // toll or fare; null once another walk is asked for. A purse can pay tomorrow
    // what it can't today, so a caller that writes a place off when a walk to it
    // can't start reads this first, and doesn't.
    public RoomKey? LastUnpaidFailure { get; private set; }

    // Told when a walk has no route for want of a toll or fare, with the walk's
    // destination and the reason it failed with.
    private Action<RoomKey, string>? _unpaidCrossingHandler;
    public void SetUnpaidCrossingHandler(Action<RoomKey, string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _unpaidCrossingHandler = handler;
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
    // committing to walk it. avoidTeleports plans it as WalkTo(avoidTeleports: true)
    // does, for a caller that will walk it that way and is bounding its length.
    public IReadOnlyList<RoomKey>? TryComputeRouteKeys(RoomKey from, RoomKey to, bool avoidTeleports = false)
    {
        if (_graph.GetRoom(from) is null || _graph.GetRoom(to) is null) return null;
        if (from.Equals(to)) return new[] { from };

        IReadOnlyList<Direction>? path = _bfs.FindPath(from, to, Filter, refuseTeleports: avoidTeleports);
        if (path is null || path.Count == 0) return null;
        return ExpandRouteKeys(from, path);
    }

    // willResume: the caller stops the walk only to take it up again itself (a sell
    // trip, a flee), which the Stopped event says (WalkEvent.WillResume).
    public void Stop(string reason = "user stop", bool willResume = false)
    {
        if (State == WalkState.Idle)
        {
            // No walk to stop, but a journey may be standing between two of its legs
            // (at a giver, waiting on the hand-over). The coordinator about to issue
            // the next leg hears of a stop only through this event, so it is raised
            // for the journey: without it the leg went out after the user's Stop.
            if (_journey is { } standing)
                Raise(new WalkEvent(WalkEventKind.Stopped, reason, standing.Destination, WillResume: willResume));
            return;
        }
        RoomKey? dest = _destination;
        Reset();
        // Free any party-reform gate this walk was holding so a stopped user
        // isn't pinned by an in-progress chime-teleport re-invite.
        _onPartySplitAbort?.Invoke();
        Raise(new WalkEvent(WalkEventKind.Stopped, reason, dest, WillResume: willResume));
    }

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
        // scheduler wired → release immediately.
        if (_abandonSettle is not null) return;   // settle already pending
        if (_scheduleDelay is null) { ReleaseAbandonHold(); return; }
        _abandonSettle = _scheduleDelay(AbandonSettleWindow, ReleaseAbandonHold);
    }

    // Settle elapsed with the room still clear of hostiles — no follower engaged, so
    // drop the abandon hold and let the route resume.
    // Reset States: release the abandon-combat hold even with the walker idle (a
    // loop can leave it asserted, and Stop is a no-op when Idle).
    public void ReleaseAbandonedCombatHold() => ReleaseAbandonHold();

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

        // Don't attempt a crossing we already know we can't make. A gated route is
        // planned as if every gate item were in hand (that's the point — the
        // acquisition runs first), but the first step goes out synchronously here
        // while a fulfiller's detour redirect is still queued on the dispatcher. So
        // the walk would send the opener and the move, watch both fail, and only
        // then be superseded: `rub bloodstone orb` while carrying no orb, then a
        // bonk on the hidden exit (report paradigm-20260911-100708). We know the
        // inventory, so hold the step instead and let the detour take the wheel.
        //
        // Held only while someone is actually fetching the item — the probe requires
        // an in-flight acquisition, so a gate nothing can source still goes out and
        // fails the normal way rather than parking the walk forever.
        if (HeldForGateItem(_path[_index])) return;

        // A room command that only works in an empty room waits for the room to
        // clear instead of going out to be refused.
        if (HeldForEmptyRoom(_path[_index], alreadySent: false)) return;
        if (HaltsBeforeBossRoom(_path[_index])) return;

        WalkStep step = _path[_index];
        // Auto-sneak wants a sneak in place before we step; it holds the coordinator
        // meanwhile and the resume re-drives this step.
        if (step is MoveStep && _moveReadyCheck?.Invoke() == false) return;
        // The ready check sends the pre-step gear, and a swap that asserts and clears
        // its gate in one go re-enters this method through the resume, which sends the
        // step. Sending it again here put the move on the wire twice.
        if (_stepInFlight || State != WalkState.Walking) return;
        // The purses were read while a step was in flight or the walk was held.
        if (_tollCheckOwed)
        {
            _tollCheckOwed = false;
            if (_destination is { } onward && ClosedTollAhead())
            {
                _log?.Info("Walker", $"step {_index + 1}: a toll further on isn't taken with the party; re-planning without it");
                ReplanInPlace(onward);
                return;
            }
        }
        // Last, with nothing left that could keep the step back: a toll the party is
        // let through is taken as paid from that moment (PartyTollGate.BeforeTollStep).
        if (TurnedAsideAtToll(step)) return;
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
            case SysGotoStep sysGoto:
                SendSysGotoStep(sysGoto);
                break;
        }
    }

    // Asked before a step through a toll exit, with the room, the way and the exit
    // (PartyTollGate.BeforeTollStep): a leader's party is checked there, every
    // member's purse, before anyone is charged. Hold means a movement gate is up
    // and the resume re-drives this step; Closed means the party can't be taken
    // through, and the walk plans again with that exit shut to it.
    // The last argument is what the rest of the route will charge us ourselves
    // (OwnChargesPastTheStepDue).
    private Func<RoomKey, Direction, RoomExit, long, PartyTollGate.StepVerdict>? _tollStepCheck;
    public void SetTollStepCheck(Func<RoomKey, Direction, RoomExit, long, PartyTollGate.StepVerdict> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        _tollStepCheck = check;
    }

    // The same for the verb that boards a sailing (PartyTollGate.BeforeSailing).
    private Func<BoatPassage, long, PartyTollGate.StepVerdict>? _sailingCheck;
    public void SetSailingCheck(Func<BoatPassage, long, PartyTollGate.StepVerdict> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        _sailingCheck = check;
    }

    // What the route past the step due will charge us ourselves, in copper: every
    // toll and fare still ahead on the path as planned, a sailing's included. The
    // party's check keeps that back before it hands coin over at this crossing,
    // or we would pay a member through one toll and stand at the next unable to
    // cross. Only the path's own steps count: a toll on a branch not taken is not on it.
    private long OwnChargesPastTheStepDue()
    {
        if (_path is not { } path || _index >= path.Count) return 0;
        RoomKey? at = path[_index] switch
        {
            MoveStep due => due.ExpectedTarget,
            BoatStep due => due.Passage.ArrivalRoom,
            _ => null,
        };
        long copper = 0;
        for (int i = _index + 1; i < path.Count; i++)
        {
            switch (path[i])
            {
                case CommandStep:
                    break;
                case BoatStep boat:
                    copper += Math.Max(0, boat.Passage.FareCopper);
                    at = boat.Passage.ArrivalRoom;
                    break;
                case SysGotoStep jump:
                    at = jump.LandingRoom;
                    break;
                case MoveStep move:
                    if (at is { } from && _graph.GetRoom(from) is { } room
                        && room.Exits.TryGetValue(move.Direction, out RoomExit exit))
                        copper += PaidCrossingDescriber.CostOf(in exit);
                    at = move.ExpectedTarget;
                    break;
            }
        }
        return copper;
    }

    private bool TurnedAsideAtToll(WalkStep step)
    {
        PartyTollGate.StepVerdict verdict;
        string toll;
        switch (step)
        {
            case MoveStep move when _tollStepCheck is { } check
                && _tracker.State.CurrentRoom is { } room
                && room.Exits.TryGetValue(move.Direction, out RoomExit exit)
                && PaidCrossingDescriber.Charges(in exit):
                verdict = check(room.Key, move.Direction, exit, OwnChargesPastTheStepDue());
                toll = PaidCrossingDescriber.Describe(room.Key, move.Direction, in exit, RoomNameOf);
                break;
            case BoatStep boat when _sailingCheck is { } check && boat.Passage.FareCopper > 0:
                BoatPassage passage = boat.Passage;
                verdict = check(passage, OwnChargesPastTheStepDue());
                toll = PaidCrossingDescriber.DescribeBoat(in passage, RoomNameOf);
                break;
            default:
                return false;
        }
        switch (verdict)
        {
            case PartyTollGate.StepVerdict.Go:
                return false;
            case PartyTollGate.StepVerdict.Hold:
                return true;
        }
        if (_destination is not { } dest) return false;
        // Counted against the walk's re-plans: a plan that came back through the
        // same crossing would otherwise turn here for ever. The turns have a count
        // of their own as a backstop: the re-plan sends its first step before it
        // hands the re-plan count back, so a check that closes a crossing the
        // planner still routes through would recurse without one. In the app both
        // answers come from PartyTollGate's one judgement, so only a check wired
        // apart from the filter gets there. Both counts start again at each step
        // that lands (ClearReplanCounts): a walk turns at as many crossings as it meets.
        if (_replanCount >= MaxReplansPerWalk || _crossingTurns >= MaxReplansPerWalk)
        {
            FailUnpaid(dest, $"stopped at {toll}: the party can't all pay it");
            Reset();
            return true;
        }
        _replanCount++;
        _crossingTurns++;
        _log?.Info("Walker", $"step {_index + 1}: {toll} isn't taken with the party; re-planning without it");
        ReplanInPlace(dest);
        return true;
    }

    private int _crossingTurns;

    private void ClearReplanCounts()
    {
        _replanCount = 0;
        _crossingTurns = 0;
    }

    // The party's purses, asked for as this walk was planned, have been read
    // (PartyWealthTracker.RoundSettled). If what is left of the route goes through a
    // toll that is now closed to the party, the walk plans again from where it
    // stands instead of walking up to the gate to turn there: at once between
    // steps, and when the step in flight has landed otherwise.
    public void ReplanIfATollAheadIsClosed()
    {
        if (_path is null || _destination is not { } dest) return;
        if (State != WalkState.Walking || _stepInFlight)
        {
            _tollCheckOwed = true;
            return;
        }
        _tollCheckOwed = false;
        if (!ClosedTollAhead()) return;
        _log?.Info("Walker", $"step {_index + 1}: a toll further on isn't taken with the party; re-planning without it");
        ReplanInPlace(dest);
    }

    private bool _tollCheckOwed;

    private bool ClosedTollAhead()
    {
        if (_path is not { } path || Filter is not { } filter || _tracker.State.CurrentRoom is not { } here) return false;
        RoomKey at = here.Key;
        for (int i = _index; i < path.Count; i++)
        {
            if (path[i] is CommandStep) continue;
            // A sailing or a jump lands somewhere the exits don't say: nothing past it is read.
            if (path[i] is BoatStep boat)
                return boat.Passage.FareCopper > 0
                    && filter.DescribeBoatBlock(boat.Passage).HasFlag(ExitBlockReason.Fare);
            if (path[i] is not MoveStep move
                || _graph.GetRoom(at) is not { } room || !room.Exits.TryGetValue(move.Direction, out RoomExit exit))
                return false;
            if (PaidCrossingDescriber.Charges(in exit)
                && (filter.DescribeExitBlock(in exit) & (ExitBlockReason.Toll | ExitBlockReason.Fare)) != 0)
                return true;
            at = exit.Target;
        }
        return false;
    }

    // True when the step's exit demands an item we lack AND an acquisition for it
    // is already under way, so the crossing should wait rather than be attempted
    // and fail. Only a MoveStep can carry a gate (a CommandStep is the opener
    // itself, already past this decision); an unresolvable exit or an unbound probe
    // never holds.
    private bool HeldForGateItem(WalkStep step)
    {
        if (_gateItemHoldProbe is not { } held) return false;
        if (step is not MoveStep move) return false;
        if (_tracker.State.CurrentRoom is not { } room) return false;
        if (!room.Exits.TryGetValue(move.Direction, out RoomExit exit)) return false;

        foreach (int itemId in ExitGateItems.Of(in exit))
            if (held(itemId)) return HoldStep(move.Direction, itemId);

        // ExitGateItems omits key gates by design (pick and bash open them too), but
        // a key that IS being fetched has to hold as well — otherwise the door FSM
        // arrives first and burns its one use-key attempt on a key we don't have
        // yet, failing the whole walk while the summon that produces it is still on
        // its way. Only a key with an acquisition behind it reaches here; every
        // other key door still opens, picks, bashes or fails exactly as before
        // (report paradigm-20260911-103315: `get key` / `use gate key s` sent at the
        // Black Steel Gate instead of waiting for the statue's key).
        if (exit.Hint == RoomExitHint.KeyLocked && exit.KeyItemId > 0 && held(exit.KeyItemId))
            return HoldStep(move.Direction, exit.KeyItemId);

        return false;
    }

    // Debug, not Info: a held step re-evaluates on every dispatch attempt, so this
    // can repeat a few times while the detour settles.
    private bool HoldStep(Direction dir, int itemId)
    {
        _log?.Log(LogSeverity.Debug, "Walker",
            $"step {_index + 1}/{_path!.Count}: holding {dir} — gate item {itemId} is being acquired");
        return true;
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

    private void ArmStallWatchdog(string why)
    {
        _stallWatchdog?.Dispose();
        _stallWatchdog = _scheduleDelay?.Invoke(StallWatchdogInterval, OnStallWatchdogElapsed);
        _log?.Debug("Walker",
            $"stall watchdog armed ({StallWatchdogInterval.TotalSeconds:F0}s): {why}");
    }

    private void DisarmStallWatchdog()
    {
        _stallWatchdog?.Dispose();
        _stallWatchdog = null;
    }

    private void OnStallWatchdogElapsed()
    {
        _stallWatchdog?.Dispose();
        _stallWatchdog = null;
        // Only act if we're genuinely still wedged. A step that confirmed normally
        // already advanced us and re-armed for its successor; a pause or a reset left
        // nothing in flight. Escalate as STALLED rather than as a mismatch: tier 2
        // watches for a 1-of-1 over the engine's next few steps, and a wedged walker
        // has none — reporting a mismatch would park us there with nothing left to
        // re-arm the watchdog. The recovery gate resyncs (rm on Paradigm, footprint
        // backtrack on stock) and then advances or reroutes from ground truth.
        if (State != WalkState.Walking || !_stepInFlight) return;
        if (_tracker.State.Confidence == RoomConfidence.Pending)
        {
            _log?.Warn("Walker",
                $"step {_index + 1} in-flight stall: move Pending, unconfirmed for {StallWatchdogInterval.TotalSeconds:F0}s — escalating to recovery");
            _recovery?.NoteEngineStalled(
                $"walk step {_index + 1} in-flight stall (move interrupted, never confirmed)");
            return;
        }

        // A move sent while the tracker was Suspect arms no Pending, so when the
        // game refuses it nothing changes that anyone is listening for, and the
        // branch above never sees it (report paradigm-20261009-082958: the walk
        // stood at the wall until the player typed `rm`). Ask the game where we
        // are and re-plan from the answer. Only that: with nobody to ask this
        // leaves everything as it was rather than hand a stock realm's walk to
        // the reverse-walk on the strength of a timer.
        if (!IsPlainMoveSentFromSuspect()) return;
        _log?.Warn("Walker",
            $"step {_index + 1} in-flight stall: sent with the tracker Suspect, nothing for {StallWatchdogInterval.TotalSeconds:F0}s — asking the game where we are");
        if (TryReplanOrFail(RoomConfidence.Suspect, "and the step went unanswered", onlyIfAsked: true)) return;
        if (_stallWatchdogRearmed)
        {
            _log?.Info("Walker", $"step {_index + 1}: still couldn't ask the game where we are; leaving the step in flight");
            return;
        }
        _stallWatchdogRearmed = true;
        ArmStallWatchdog($"step {_index + 1}: couldn't ask the game where we are, one more wait");
    }

    // The step in flight is a bare move (no boat, jump, greet, door, winch, trap or
    // hidden-exit wait owns it, each of which churns the tracker on its own and has
    // its own clock), the tracker is Suspect, and the recovery gate isn't already
    // dealing with it.
    private bool IsPlainMoveSentFromSuspect() =>
        _tracker.State.Confidence == RoomConfidence.Suspect
        && _path is { } path && _index < path.Count && path[_index] is MoveStep
        && !(_awaitingBoatArrival || _awaitingSysGotoArrival || _awaitingGreetTeleport
             || _awaitingDoorOpen || _awaitingWinch || _awaitingTrapDisarm || _awaitingHiddenReveal)
        && _recovery is { AwaitingAuthoritativeResync: false, CurrentTier: not TierLevel.Tier3 };

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

        // An exit whose spell puts us on the routed side only with an item in hand:
        // without it the step goes through all the same and lands somewhere else.
        // The planner closes it, but a walk planned through gates has it open (the
        // item was to be fetched on the way), and a re-plan after the wrong landing
        // keeps that choice: the walk came round to the exit and stepped through
        // again, for good. So at the exit itself, the inventory decides. Not while
        // an acquisition is still under way (HeldForGateItem holds the step then),
        // nor while the inventory is unread.
        if (exit.CastGateItemId > 0 && _tracker.HoldsItem(exit.CastGateItemId) == false)
        {
            string obstacle = BlockedExitDescriber.Describe(current.Key, step.Direction, in exit,
                k => _graph.GetRoom(k)?.Name, id => _itemNameResolver?.Invoke(id));
            _log?.Info("Walker", $"step {_index + 1}/{_path!.Count}: not taking {obstacle}");
            Raise(new WalkEvent(WalkEventKind.Failed, $"stopped at {obstacle}", _destination));
            Reset();
            return;
        }

        // An exit whose spell teleports us on ends in its landing, not in the room it
        // names (and the route beyond is planned from).
        _expectedAfterCurrentMove = exit.Landing;
        _stepInFlight = true;
        _stallWatchdogRearmed = false;
        ArmStallWatchdog($"step {_index + 1} sent ({step.Direction})");

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
            string dirWord = step.Direction.ToLongName();
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
                + (exit.KeyItemId > 0 ? $" (key {exit.KeyItemId})" : "")
                // A poor-odds lock is only on a plan that found no short way round it.
                + (Filter?.IsPoorOddsDoor(in exit) == true && Filter.DescribeDoorRefusal(in exit) is { } odds
                    ? $": no way round within {DoorPolicy.PoorPickDetourSteps} extra steps; {odds}" : ""));
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
                // skill roll and leave us put — don't just trust the
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
        // wire path sees Pending before any landing
        // observation arrives.
        _tracker.NoteMoveSent(step.Direction);
                _recovery?.NoteEngineStepSent(step.Direction);

        byte[] bytes = EncodeMove(step.Direction);
        EmitMoveBytes(bytes, $"move {step.Direction} → {exit.Target}"
            + (exit.CastLandings is null ? "" : $", whose spell teleports us on to {exit.Landing}"));
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

            case HiddenSearchResult.LeftRoom left:
                // A move already on the wire landed mid-search — we're not where the
                // search assumed. Re-plan from here on the walk's bounded replan
                // budget (which leans on rm first) instead of failing the walk.
                _log?.Info("Walker",
                    $"hidden-exit search interrupted — moved {left.SearchedIn} → {left.NowIn}; replanning");
                _stepInFlight = false;
                TryReplanOrFail(RoomConfidence.Confirmed);
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
                // Same as LoopRunner.OnDoorReply: re-drive the step so the ready check
                // (re-sneak after the bash) and a combat pause come first, crossing the
                // now-open door from SendMoveStep's pre-open branch.
                _tracker.NoteNamedDoorOpened(step.Direction);
                if (_tracker.State.OpenDoorDirections?.Contains(step.Direction) == true)
                {
                    DisarmStallWatchdog();
                    _stepInFlight = false;
                    SendNextStep();
                    return;
                }
                _tracker.NoteMoveSent(step.Direction);
                _recovery?.NoteEngineStepSent(step.Direction);
                byte[] bytes = EncodeMove(step.Direction);
                EmitMoveBytes(bytes, $"move {step.Direction} (post-door)");
                return;

            case DoorOpenResult.Failed failed:
                if (failed.Unopenable && TryGoRoundDoor(failed.Reason)) return;
                Raise(new WalkEvent(WalkEventKind.Failed,
                    $"door open failed: {failed.Reason}", _destination));
                Reset();
                return;

            case DoorOpenResult.NotHere notHere:
                // The step was planned from a room we aren't standing in (a move
                // already on the wire landed, or the tracker was a room behind).
                // Nothing is wrong with the door, so re-plan on the walk's bounded
                // replan budget (which leans on rm first) instead of failing the walk.
                _log?.Info("Walker",
                    $"step {_index + 1}: door isn't here ({notHere.Reason}); re-checking the room and replanning");
                DisarmStallWatchdog();
                _stepInFlight = false;
                TryReplanOrFail(RoomConfidence.Suspect);
                return;
        }
    }

    // The door on the step in flight beat this character: every verb it had ran out
    // (the pick cap), or it had none. Failing the walk there left whoever issued it
    // to send the same walk again, through the same door, for as long as it kept
    // trying (Auto-Lair retries every two seconds). The door is given up on for the
    // rest of the trip and the walk re-planned from here, which goes round it. The
    // re-plan is not charged to the walk's desync budget: each one closes a door for
    // good, so they can't ping-pong. False when the walk has to fail as before (the
    // step can't be read back); true when it went round, or failed here because the
    // door was the only way.
    private bool TryGoRoundDoor(string reason)
    {
        if (_destination is not { } dest || _path is null || _index >= _path.Count
            || _path[_index] is not MoveStep step
            || _tracker.State.CurrentRoom is not { } here
            || !here.Exits.TryGetValue(step.Direction, out RoomExit exit))
            return false;

        _abandonedDoors.Add((here.Key, exit.Target));
        string door = $"the door {step.Direction.ToLongName()} from {here.Key} ({here.Name})";
        IRoomFilter roundIt = new AbandonedDoorsFilter(_filter, _abandonedDoors);
        IReadOnlyList<Direction>? round;
        // The rooms nothing makes safe stay closed, as they do for the re-plan this
        // looks ahead to: a way round across them would be announced and not taken.
        using (_activeThroughGates ? roundIt.SuspendAcquirableGatesButUnprotectableHazards() : null)
            round = _bfs.FindPath(here.Key, dest, roundIt, ignoreAvoids: _activeIgnoreAvoids);
        if (round is null)
        {
            Raise(new WalkEvent(WalkEventKind.Failed,
                $"couldn't open {door} ({reason}), and there is no other way to {dest}", _destination));
            Reset();
            return true;
        }

        _log?.Info("Walker",
            $"walk to {dest}: gave up on {door} ({reason}); going round it, {round.Count} step(s) from here, "
            + "and not planning through it again this trip");
        DisarmStallWatchdog();
        _stepInFlight = false;
        Raise(new WalkEvent(WalkEventKind.Retrying,
            $"couldn't open {door}; going round it", _destination));
        ReplanInPlace(dest);
        return true;
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

            case WinchResult.RoomNotEmpty:
                // Back to an unsent step: the hold clears the room and the step is
                // driven again, winch and all.
                _stepInFlight = false;
                if (_path is not null && _index < _path.Count
                    && HeldForEmptyRoom(_path[_index], alreadySent: false))
                    return;
                Raise(new WalkEvent(WalkEventKind.Failed,
                    "winch failed: it can't be pulled with a monster in the room, and none is listed here", _destination));
                Reset();
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

        TrapReplyOutcome? outcome = TrapReply.Read(reply);

        // Stopped externally — bail without moving.
        if (outcome == TrapReplyOutcome.Stopped)
        {
            Raise(new WalkEvent(WalkEventKind.Stopped,
                "trap disarm cancelled", _destination));
            Reset();
            return;
        }

        // Asked of the party and nobody took it: the same position as having nobody
        // able, so the exit is walked through with the trap still set.
        if (outcome == TrapReplyOutcome.Unanswered)
        {
            _log?.Info("Walker", "no party member took the trap: walking through it");
        }
        else if (outcome != TrapReplyOutcome.Clear)
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
            _roomActionSent?.Invoke(step.Command);
            return;
        }

        _awaitingPromptForCommand = true;
        byte[] bytes = Encoding.Latin1.GetBytes(step.Command + "\r");
        WriteBytes(bytes, $"command '{step.Command}'");
        _roomActionSent?.Invoke(step.Command);
    }

    // A room command step (lever, winch, remote action) went out. Room actions end a
    // sneak (GAME_MECHANICS "Sneaking — commands, equip order, and the sneak state
    // machine", its What ends a sneak list); the walk sends them anyway, and this
    // lets the stealth engine re-sneak before the next move.
    private Action<string>? _roomActionSent;

    public void SetRoomActionHook(Action<string> hook) => _roomActionSent = hook;

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

        // A level-gated sole-crossing sail still boards — the captain refuses only
        // the under-level members at the dock and leaves them behind. Warn so the
        // user knows a member may not make the crossing, rather than the walk
        // silently splitting the party a head short. (One whose fare we or the
        // party can't pay never gets here: WalkToImmediate fails the walk naming it.)
        ExitBlockReason gate = Filter?.DescribeBoatBlock(passage) ?? ExitBlockReason.None;
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

    // Put a sys-goto jump on the wire. Fires `sys goto <name>` through
    // SysopGotoManager (verbatim send + bare Enter + landing resync) — no combat
    // gate here, the coordinator's pause already stalled us out of any fight before
    // this step ran. Like a boat there's no graph edge to the landing, so we DON'T
    // hand the tracker a pending move; HandleSysGotoTransition completes the step
    // when the landing room confirms (the manager's resync commits SetLocated
    // there), with OnSysGotoDeadline as the wall-clock backstop.
    private void SendSysGotoStep(SysGotoStep step)
    {
        _stepInFlight = true;
        _awaitingSysGotoArrival = true;

        _log?.Info("Walker",
            $"step {_index + 1}/{_path!.Count}: sys goto '{step.Location.Name}' "
            + $"→ landing {step.LandingRoom} (jump, then walk from there).");

        _sysGotoFire?.Invoke(step.Location);

        // Arm the backstop last, after the jump bytes are out. An early landing
        // observation disposes it in HandleSysGotoTransition; first to fire wins.
        _sysGotoTimer?.Dispose();
        _sysGotoTimer = _scheduleDelay?.Invoke(SysGotoArrivalWindow, OnSysGotoDeadline);
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
        bool ignoreAvoids = _deferredWalkIgnoreAvoids;
        bool preferTeleportFree = _deferredWalkPreferTeleportFree;
        bool pickedRoute = _deferredWalkPickedRoute;
        IReadOnlySet<(RoomKey From, RoomKey To)>? automaticTeleports = _deferredWalkAutomaticTeleports;
        _deferredWalkAutomaticTeleports = null;
        _deferredWalkPickedRoute = false;
        _deferredWalkTarget = null;
        _deferredWalkThroughGates = false;
        _deferredWalkArmAcquisition = true;
        _deferredWalkAvoidTeleports = false;
        _deferredWalkAvoidTraps = false;
        _deferredWalkIgnoreAvoids = false;
        _deferredWalkPreferTeleportFree = false;
        _deferredWalkTimer?.Dispose();
        _deferredWalkTimer = null;
        WalkToImmediate(deferred, throughGates, armAcquisition, avoidTeleports, avoidTraps, ignoreAvoids, preferTeleportFree, pickedRoute, automaticTeleports);
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

        // A sys-goto jump likewise owns every transition until the landing room
        // confirms — the forced room render + resync churn the tracker, so intercept
        // before the generic MoveStep / recovery paths would read it as a desync.
        if (_awaitingSysGotoArrival && _path[_index] is SysGotoStep sysGotoStep)
        {
            HandleSysGotoTransition(transition, sysGotoStep);
            return;
        }

        // A greet teleport owns its step until it verifies the arrival room (or
        // re-asks after a failed skill roll). Intercept here so the generic
        // MoveStep recovery/replan paths — which would fail the walk after a
        // bounded retry — never see a merely-unlucky roll.
        if (_awaitingGreetTeleport)
        {
            HandleGreetTeleportTransition(transition);
            return;
        }

        if (_path[_index] is not MoveStep) return;

        // A door / trap / hidden-exit sub-FSM owns this step until its own
        // reply callback (OnDoorReply / OnTrapReply / OnHiddenRevealReply)
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
                TryReplanOrFail(transition.NewConfidence);   // no gate bound
            return;
        }

        if (transition.NewConfidence != RoomConfidence.Confirmed) return;

        RoomKey? newKey = transition.NewRoom?.Key;
        if (newKey is null) return;

        if (newKey.Value.Equals(_expectedAfterCurrentMove))
        {
            _stepInFlight = false;
            _retryCount = 0;
            ClearReplanCounts();
            AdvanceStep();
            return;
        }

        Room? sourceForCurrentStep = transition.PreviousRoom;
        if (sourceForCurrentStep is not null
            && newKey.Value.Equals(sourceForCurrentStep.Key))
        {
            // A monster came in after the step's empty-room command was judged
            // clear to send, and the command came back refused.
            if (_path is not null && _index < _path.Count
                && HeldForEmptyRoom(_path[_index], alreadySent: true))
            {
                _stepInFlight = false;
                return;
            }

            if (TryResendRolledReveal(sourceForCurrentStep)) return;

            // A toll or fare the purse doesn't cover is refused again on a retry, and
            // a re-plan now goes round it: the walker stood at a toll gate sending
            // the same step six times (report paradigm-20261010-145529).
            if (_path is { } steps && _index < steps.Count && steps[_index] is MoveStep refused
                && sourceForCurrentStep.Exits.TryGetValue(refused.Direction, out RoomExit refusedExit)
                && Filter is { } gates
                && (gates.DescribeExitBlock(in refusedExit) & (ExitBlockReason.Toll | ExitBlockReason.Fare)) != 0)
            {
                _log?.Info("Walker",
                    $"step {_index + 1} refused at {PaidCrossingDescriber.Describe(sourceForCurrentStep.Key, refused.Direction, in refusedExit, RoomNameOf)}; "
                    + "re-planning without it");
                TryReplanOrFail(RoomConfidence.Confirmed);
                return;
            }

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

    // Returns false only for an onlyIfAsked caller whose ask didn't go out.
    //
    // when words the moment for the Retrying / Failed text ("mid-step", "while
    // paused"), read after "tracker entered <confidence>".
    //
    // onlyIfAsked is for a caller that has a way of its own to carry on: a re-plan
    // from the room the tracker already holds would only spend a slot and send the
    // same step, so it is worth doing only when the game can be asked where we are.
    // With nobody to ask, or no re-plan left to spend on asking, nothing is counted,
    // changed or failed.
    private bool TryReplanOrFail(RoomConfidence newConfidence, string when = "mid-step", bool onlyIfAsked = false)
    {
        // A block landing while the character is Confused is the movement-fumble
        // mechanic (GAME_MECHANICS "Confusion fumbles — actions fail and must be
        // re-sent"), not a genuine mapping/graph problem — mirrors
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
            if (onlyIfAsked) return false;
            Raise(new WalkEvent(WalkEventKind.Failed,
                $"tracker entered {newConfidence} {when}; walker can't continue",
                _destination));
            Reset();
            return true;
        }

        int replansBefore = _replanCount;
        bool inFlightBefore = _stepInFlight;
        if (!confused) _replanCount++;
        _stepInFlight = false;
        WalkEvent retrying = new(WalkEventKind.Retrying,
            $"tracker entered {newConfidence} {when}; re-planning from {here.Key} (attempt {_replanCount}/{MaxReplansPerWalk})",
            _destination);
        if (!onlyIfAsked) Raise(retrying);

        // Lean on Paradigm's authoritative rm before trusting the tracker's belief
        // and replanning from it — a mid-step desync is exactly what a name-
        // ambiguous zone (many identically-named rooms sharing an exit pattern)
        // can produce, and rm hard-locates the tracker independent of that name+
        // exit matching instead of replanning from the same wrong room repeatedly
        // (LoopRunner.EnterRecovery carries the identical rationale; report
        // paradigm-20260901-100523). _stepInFlight is already false above, so
        // rm's own reentrant tracker relocate can't be mistaken for an in-flight
        // step's arrival by OnTrackerStateChanged — it's a clean no-op there,
        // leaving ReplanInPlace as the only thing that actually replans. Stock realms /
        // no rm reply fall through to exactly the prior behavior.
        //
        // The ask is marked before it goes out, as an answer can come back inside
        // the call.
        object ask = new();
        _locateAsk = ask;
        if (_recovery?.TryResyncOnce?.Invoke(
                $"walker desync {when} (tracker {newConfidence})",
                _ => OnLocateAnswered(),
                OnLocateAnswered) == true)
        {
            if (onlyIfAsked) Raise(retrying);
            _log?.Info("Walker",
                $"step {_index + 1}: tracker {newConfidence} {when} (holding {here.Key}); asked the game where we are, re-planning from its answer");
            return true;
        }

        _locateAsk = null;
        if (onlyIfAsked)
        {
            _replanCount = replansBefore;
            _stepInFlight = inFlightBefore;
            return false;
        }

        ReplanOnceThePurseIsRead();
        return true;

        // The answer belongs to the walk that asked. A stop, or another walk begun
        // since, drops the ask (Reset): re-planning then would set a stopped walker
        // walking again, or turn a new walk back to the old destination. A pause
        // keeps it, and the re-plan then starts paused.
        void OnLocateAnswered()
        {
            if (!ReferenceEquals(_locateAsk, ask)) return;
            ReplanOnceThePurseIsRead();
        }

        // A refused toll has just asked for the inventory, and what can be planned
        // depends on the answer: planned ahead of it every toll is closed, and a
        // walk the purse could pay for stops. The re-plan waits for the read on the
        // locate's terms (the same ask, dropped the same way), for no longer than
        // the read's own bound.
        void ReplanOnceThePurseIsRead()
        {
            _locateAsk = ask;
            if (_purseReadWait?.Invoke(OnPurseAnswered) == true)
            {
                _log?.Info("Walker",
                    $"step {_index + 1}: re-plan waits for the inventory read the refused toll asked for");
                return;
            }
            OnPurseAnswered();
        }

        void OnPurseAnswered()
        {
            if (!ReferenceEquals(_locateAsk, ask)) return;
            _locateAsk = null;
            ReplanInPlace(dest);
        }
    }

    // Asked before a re-plan: if an inventory read a refused toll asked for is still
    // unanswered, takes the re-plan to run when it answers and returns true
    // (OwedPurseRead.WaitForAnswer). Unset, nothing waits.
    private Func<Action, bool>? _purseReadWait;
    public void SetPurseReadWait(Func<Action, bool> wait)
    {
        ArgumentNullException.ThrowIfNull(wait);
        _purseReadWait = wait;
    }

    // Re-source the path from the tracker's best-guess current room. WalkTo
    // handles the existing Walking state by clearing it — silently, since
    // _replanningInPlace suppresses the supersede Stopped that would otherwise
    // abort a driving reroute.
    //
    // WalkTo's own Reset() zeroes _replanCount as part of that clear — it has no
    // way to distinguish "a fresh user-initiated walk" from "this walk replanning
    // itself", and the latter must NOT lose the count that makes MaxReplansPerWalk
    // mean anything. Capture it now and restore it after the call, or the cap never
    // actually accumulates: every replan attempt walks in seeing _replanCount back
    // at 0, so a persistently blocked exit (not just an unlucky fumble streak) would
    // retry through this path forever instead of failing once the budget is
    // genuinely spent. The rolled-reveal count is carried over for the same reason:
    // a replan from the room the reveal is in leads straight back to that step, and
    // a fresh count each time would multiply its cap by the replans.
    private void ReplanInPlace(RoomKey dest)
    {
        int replanCount = _replanCount;
        int revealRetries = _revealRetries;
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
                avoidTraps: _activeAvoidTraps,
                ignoreAvoids: _activeIgnoreAvoids,
                preferTeleportFree: _activePreferTeleportFree,
                pickedRoute: _activePickedRoute);
        }
        finally
        {
            _replanCount = replanCount;
            _revealRetries = revealRetries;
            _replanningInPlace = false;
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
            ClearReplanCounts();
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
            ClearReplanCounts();
            AdvanceStep();
            return;
        }

        Raise(new WalkEvent(WalkEventKind.Failed,
            $"boat '{boat.Passage.Keyword}' never reached {arrival} "
            + "(captain refused boarding, or arrival mismatch)", _destination));
        Reset();
    }

    // Own every transition while a sys-goto jump is in flight. Complete the step
    // once the tracker confirms the landing room (SysopGotoManager's name-matched
    // resync commits SetLocated there off the forced room render); otherwise the
    // churn is transit — keep waiting for the backstop.
    private void HandleSysGotoTransition(RoomTransition transition, SysGotoStep step)
    {
        RoomKey landing = step.LandingRoom;

        if (transition.NewConfidence == RoomConfidence.Confirmed
            && transition.NewRoom?.Key is { } here && here.Equals(landing))
        {
            _log?.Info("Walker", $"sys goto '{step.Location.Name}' landed at {landing}.");
            _sysGotoTimer?.Dispose();
            _sysGotoTimer = null;
            _awaitingSysGotoArrival = false;
            _stepInFlight = false;
            _retryCount = 0;
            ClearReplanCounts();
            AdvanceStep();
            return;
        }

        _log?.Info("Walker",
            $"sys goto '{step.Location.Name}' transit (tracker {transition.NewConfidence}); awaiting {landing}.");
    }

    // The jump's wall-clock backstop fired. If the tracker has re-anchored at the
    // landing room by now, an arrival observation may just not have matched yet —
    // complete the step. Otherwise the jump never landed (refused / arrival never
    // rendered), so fail out rather than leave the walk wedged.
    private void OnSysGotoDeadline()
    {
        _sysGotoTimer?.Dispose();
        _sysGotoTimer = null;

        if (!_awaitingSysGotoArrival) return;                 // already completed early
        if (_path is null || _index >= _path.Count) return;
        if (_path[_index] is not SysGotoStep step) return;

        RoomKey landing = step.LandingRoom;
        if (_tracker.State.CurrentRoom?.Key is { } here && here.Equals(landing))
        {
            _log?.Info("Walker",
                $"sys goto '{step.Location.Name}' deadline: already at {landing}; completing step.");
            _awaitingSysGotoArrival = false;
            _stepInFlight = false;
            _retryCount = 0;
            ClearReplanCounts();
            AdvanceStep();
            return;
        }

        Raise(new WalkEvent(WalkEventKind.Failed,
            $"sys goto '{step.Location.Name}' never reached {landing} "
            + "(jump refused, or landing never rendered)", _destination));
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
            ClearReplanCounts();
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

    // The step in flight opened its exit with a room command that rolls against a
    // stat (`clear rubble` and its like) and the move bonked: the roll missed and
    // the exit is still hidden. The same command is the only way through, so it is
    // sent again on a budget of its own; a missed roll is neither a blocked exit nor
    // a stale map, and the one retry and two replans those get ran out on a short
    // run of bad rolls. Past the cap the ordinary blocked handling takes the step.
    private int _revealRetries;

    // Diagnostics: rolled-reveal re-sends spent on the step in flight.
    public int RolledRevealRetries => _revealRetries;

    private bool TryResendRolledReveal(Room source)
    {
        if (_path is null || _index >= _path.Count
            || _path[_index] is not MoveStep { SkipSpecialDispatch: false } step) return false;
        if (!source.Exits.TryGetValue(step.Direction, out RoomExit exit)
            || SpecialExitDispatch.RolledRevealCommand(exit) is not { } reveal) return false;
        if (_revealRetries >= SpecialExitDispatch.RolledRevealRetryCap)
        {
            _log?.Info("Walker",
                $"step {_index + 1}: '{reveal}' still hasn't opened the way {step.Direction} after "
                + $"{_revealRetries} more tries — treating the step as blocked");
            return false;
        }

        _revealRetries++;
        _stepInFlight = false;
        _log?.Info("Walker",
            $"step {_index + 1}: '{reveal}' didn't open the way {step.Direction} (its roll can miss) — "
            + $"sending it again, retry {_revealRetries} of {SpecialExitDispatch.RolledRevealRetryCap}");
        SendNextStep();
        return true;
    }

    // The NPC's script refused the ask outright (alignment, level, missing item) —
    // not a failed skill roll, so re-asking can never succeed. Stand down before the
    // tracker's revert lands; the generic blocked path then retries once and replans.
    private void OnCommandMoveRefused()
    {
        if (!_awaitingGreetTeleport) return;
        _log?.Info("Walker",
            $"greet teleport '{_greetTeleportCommand}' refused by the NPC; not re-asking.");
        ClearGreetTeleportWait();
    }

    // Our step in flight has gone through an exit whose spell teleports everyone on
    // by themselves: the followers are dragged in behind us and dropped from the
    // party one by one as the spell takes them (GAME_MECHANICS "Jungle to the Lost
    // City: the Vine Bridge trap and the golden idol"). There is no keyword to
    // relay; the party only has to be re-formed where it lands.
    //
    // Announced here, once the crossing is seen, and not when the step's bytes go
    // out: a step the game refused or dropped splits nobody, and a regroup hold put
    // up for it ran its full window and then uninvited followers who had never left.
    //
    // Ours when the step in flight is the one expected to end in that landing,
    // whatever the walk's run state: a hold that lands between the step going out
    // and the crossing showing (a party member's @wait, a held cast) leaves the
    // walk Paused with the step still in flight, and the split is no less ours.
    private void OnCastCrossingStarted(RoomKey landing)
    {
        if (!_stepInFlight || _expectedAfterCurrentMove is not { } expected || !expected.Equals(landing)) return;
        if (_isLeaderWithFollowers?.Invoke() != true) return;
        _log?.Info("Walker", $"step {_index + 1}: the exit's spell is taking the party through one by one — re-forming it on landing");
        _onLeaderPartySplit?.Invoke();
    }

    // ----- Stopping before a boss room on the way ----------------------------
    //
    // ApplyStopBefore covers a walk that ENDS in a boss room flagged "stop before
    // entering". A walk the user starts that only passes through one used to walk
    // straight in (report paradigm-20261007-224918). The route card settles how such
    // a walk goes (RouteChoicePrompt), and that choice rides with the walk here:
    // rooms to keep out of the route altogether, and rooms to stop one short of.
    // The stop is a pause on the user's own gate, so Resume walks on through.
    private sealed class BossRoomRule(
        RoomKey destination, IReadOnlySet<RoomKey>? walkAround, IReadOnlySet<RoomKey>? haltBefore,
        bool entersDestination)
    {
        private RoomKey? _walkedTo;

        public IReadOnlySet<RoomKey>? WalkAround { get; } = walkAround;
        public IReadOnlySet<RoomKey>? HaltBefore { get; } = haltBefore;
        // The walk was told to end inside its destination even when that room is
        // marked stop-before, so it isn't moved one room short.
        public bool EntersDestination { get; } = entersDestination;
        // Boss rooms this walk no longer stops for: stopped at once already, or
        // beside the room the walk began in.
        public HashSet<RoomKey> Passed { get; } = new();

        // The walk's own re-plans name where it is headed, which a stop-before
        // destination moved one room short of what was asked for.
        public bool IsFor(RoomKey target) => target.Equals(destination) || target.Equals(_walkedTo);
        public void NoteWalkedTo(RoomKey requested, RoomKey actual)
        {
            if (requested.Equals(destination)) _walkedTo = actual;
        }
    }

    private BossRoomRule? _bossRule;
    private BossRoomRule? _pendingBossRule;

    // The boss room this walk was asked for and the room one short of it the walk
    // was moved to. Kept apart from the walk's own state because that is cleared on
    // every re-plan and again just before arrival is announced; a new walk drops it.
    private (RoomKey Requested, RoomKey Actual)? _stopShortOf;

    // The room that was asked for, when dest is where a stop-before walk ended.
    private RoomKey? TakeStopShortOf(RoomKey? dest)
    {
        if (_stopShortOf is not { } s || !s.Actual.Equals(dest)) return null;
        _stopShortOf = null;
        return s.Requested;
    }
    private bool _bossRuleActive;
    private Action<RoomKey>? _bossRoomHalt;

    // The boss room the walk is paused beside, until a step goes out again.
    public RoomKey? HaltedBeforeBossRoom { get; private set; }

    // For the bug report: what this walk does about stop-before boss rooms.
    public string BossRoomRuleSummary =>
        !_bossRuleActive || _bossRule is not { } rule ? "none (walks through)"
        : rule.EntersDestination && rule.HaltBefore is null && rule.WalkAround is null
            ? "told to enter its destination even if it is a stop-before boss room"
        : rule.WalkAround is { } around ? $"walking around {around.Count} stop-before boss room(s)"
        : $"pausing before {rule.HaltBefore?.Count ?? 0} stop-before boss room(s); "
            + $"not stopping again for: {(rule.Passed.Count == 0 ? "(none)" : string.Join(", ", rule.Passed))}";

    // Called when the walk reaches the room before a boss room it was told to stop
    // for. The handler pauses movement and tells the user; if it leaves the walk
    // running, the walk goes on in.
    public void SetBossRoomHaltHandler(Action<RoomKey> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _bossRoomHalt = handler;
    }

    // How the next WalkTo to destination treats boss rooms flagged "stop before
    // entering": walkAround keeps them out of every plan the walk makes, haltBefore
    // pauses the walk one room short of each. enterDestination ends the walk inside
    // destination when that room is itself flagged, instead of one room short of
    // it. All unset clears any earlier choice.
    // A boss room beside the room we stand in is never stopped for: walking on from
    // there is the go-ahead to enter.
    public void SetBossRoomRule(RoomKey destination, IReadOnlySet<RoomKey>? walkAround, IReadOnlySet<RoomKey>? haltBefore,
        bool enterDestination = false)
    {
        if (walkAround is null && haltBefore is null && !enterDestination)
        {
            _pendingBossRule = null;
            _bossRule = null;
            return;
        }
        BossRoomRule rule = new(destination, walkAround, haltBefore, enterDestination);
        if (haltBefore is not null && _tracker.State.CurrentRoom is { } here)
        {
            if (haltBefore.Contains(here.Key)) rule.Passed.Add(here.Key);
            foreach (RoomExit exit in here.Exits.Values)
                if (haltBefore.Contains(exit.Target)) rule.Passed.Add(exit.Target);
        }
        _pendingBossRule = rule;
    }

    private bool HaltsBeforeBossRoom(WalkStep step)
    {
        HaltedBeforeBossRoom = null;
        if (!_bossRuleActive || _bossRule is not { HaltBefore: { } stopRooms } rule) return false;
        if (step is not MoveStep move
            || _tracker.State.CurrentRoom is not { } here
            || !here.Exits.TryGetValue(move.Direction, out RoomExit exit))
            return false;
        if (!stopRooms.Contains(exit.Target) || !rule.Passed.Add(exit.Target)) return false;

        _log?.Info("Walker",
            $"step {_index + 1}: the next room ({exit.Target}) is a boss room marked stop-before — pausing here ({here.Key})");
        _bossRoomHalt?.Invoke(exit.Target);
        if (!_coordinator.IsPaused) return false;
        HaltedBeforeBossRoom = exit.Target;
        return true;
    }

    // ----- A room command that only works in an empty room -------------------
    //
    // Some room commands carry a `nomonsters` condition (`go hole:nomonsters …`):
    // with any monster in the room the game refuses them, a few without printing a
    // word. Retrying at once is refused again, and replanning finds the same step,
    // so the walk used to burn its retries and stall (report
    // paradigm-20261007-194430: a murderer followed the party into the Dark Alley and
    // `go hole` was refused). The condition is in the game data, so the step is held
    // before it is sent: the room is cleared meanwhile, even with Auto-Combat off,
    // and the command goes out once the room roster shows no monster.
    private readonly EmptyRoomCommandHold _emptyRoom = new();

    // True while a room command waits for the room to be cleared. Combat reads it
    // as a force-clear, so the room is fought with Auto-Combat off.
    public bool AwaitingEmptyRoom => _emptyRoom.Active;

    // What the walk is standing on right now, for a bug report: each answer it
    // has sent for and not had. A walk that "just sits there" is in one of these.
    public IReadOnlyList<string> DescribeWaits()
    {
        List<string> waits = new();
        if (_stepInFlight) waits.Add("a step is in flight (sent, its room not seen yet)");
        if (_awaitingPromptForCommand) waits.Add("the prompt after a command step");
        if (_awaitingDoorOpen) waits.Add("the door handler");
        if (_awaitingHiddenReveal) waits.Add("the hidden-exit search");
        if (_awaitingWinch) waits.Add("the winch handler");
        if (_awaitingTrapDisarm) waits.Add("the trap disarm");
        if (_awaitingBoatArrival) waits.Add("a boat's arrival");
        if (_awaitingSysGotoArrival) waits.Add("a sys goto landing");
        if (_awaitingGreetTeleport) waits.Add("a greet teleport");
        if (_emptyRoom.Active) waits.Add("an empty room for a room command");
        if (_abandonHold) waits.Add("holds the AbandonedCombat gate");
        if (_retryCount > 0) waits.Add($"{_retryCount} retry(ies) of the step");
        return waits;
    }

    // See EmptyRoomCommandHold.SetHooks.
    public void SetRoomClearHooks(Func<bool> roomHasMonster, Action requestRoomClear, Action abortPartyReform,
        Func<Room, string, bool> commandNeedsEmptyRoom)
        => _emptyRoom.SetHooks(roomHasMonster, requestRoomClear, abortPartyReform, commandNeedsEmptyRoom);

    // The room commands a step sends from the room we stand in.
    private IEnumerable<string> RoomCommandsOf(WalkStep step)
    {
        switch (step)
        {
            case MoveStep { SkipSpecialDispatch: false } move
                when _tracker.State.CurrentRoom is { } room
                     && room.Exits.TryGetValue(move.Direction, out RoomExit exit):
                return SpecialExitDispatch.RoomCommandsFor(exit, move.Direction, room, _tracker, _teleportResolver);
            case CommandStep command:
                return [command.Command];
            case BoatStep boat:
                return [boat.Passage.Keyword];
            default:
                return [];
        }
    }

    // True when the step must wait: one of its commands needs an empty room and a
    // monster is here. Puts the hold up the first time. alreadySent marks a command
    // that went out and came back refused.
    private bool HeldForEmptyRoom(WalkStep step, bool alreadySent)
    {
        if (_emptyRoom.BlockedCommand(_tracker.State.CurrentRoom, RoomCommandsOf(step)) is not { } command)
        {
            _emptyRoom.End();
            return false;
        }
        if (_emptyRoom.Active && !alreadySent) return true;
        _log?.Info("Walker", alreadySent
            ? $"step {_index + 1}: '{command}' was refused with a monster here — clearing the room, then trying it again"
            : $"step {_index + 1}: '{command}' only works in an empty room and a monster is here — clearing the room first");
        _emptyRoom.Begin(_scheduleDelay, OnEmptyRoomWaitElapsed, alreadySent);
        return true;
    }

    // The room roster changed. Once no monster is left, the held command goes out.
    public void NoteRoomObserved()
    {
        if (!_emptyRoom.Active || _emptyRoom.RoomHasMonster) return;
        _emptyRoom.End();
        _log?.Info("Walker", $"step {_index + 1}: the room is clear — sending the room command");
        SendNextStep();
    }

    private void OnEmptyRoomWaitElapsed()
    {
        if (!_emptyRoom.Active) return;
        // A fight or a rest is holding the walk and will re-drive the step when it
        // ends; the limit is for a monster nothing is doing anything about.
        if (_coordinator.IsPaused)
        {
            _emptyRoom.Begin(_scheduleDelay, OnEmptyRoomWaitElapsed, alreadySent: false);
            return;
        }
        _emptyRoom.End();
        Raise(new WalkEvent(WalkEventKind.Failed,
            "a room command on the way only works in an empty room, and a monster is still here", _destination));
        Reset();
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
            ClearReplanCounts();
            AdvanceStep();
            return;
        }

        RetryGreetTeleport("no arrival within the retry window");
    }

    // Re-send the `ask <noun> <keyword>` and re-arm the watchdog — keeps asking
    // until the destination confirms. The class gate on the edge guarantees only a
    // class that CAN pass the roll ever reaches this step, so the retry converges
    // rather than spinning on a character who can never succeed. A held
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
        _revealRetries = 0;
        Raise(new WalkEvent(WalkEventKind.StepCompleted,
            $"{_index}/{_path.Count}", _destination));

        if (_index >= _path.Count)
        {
            RoomKey? dest = _destination;
            Reset();
            Raise(new WalkEvent(WalkEventKind.Finished, "destination reached", dest, TakeStopShortOf(dest)));
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

        if (State == WalkState.Paused && _fleeHolding)
        {
            _log?.Info("Walker", "coordinator resumed, but a flee holds the walk until it lands");
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

            // Same for a sys-goto jump in flight when the pause hit: don't re-fire it
            // on resume (that re-teleports) — keep awaiting the landing, or complete
            // now if the landing already confirmed while paused.
            if (_awaitingSysGotoArrival)
            {
                RoomKey? landing = (_path is { } gp && _index < gp.Count && gp[_index] is SysGotoStep gs)
                    ? gs.LandingRoom : (RoomKey?)null;
                if (landing is { } land
                    && _tracker.State.CurrentRoom?.Key is { } here2 && here2.Equals(land))
                {
                    _log?.Info("Walker", "resume: sys goto already landed while paused; completing step.");
                    _sysGotoTimer?.Dispose();
                    _sysGotoTimer = null;
                    _awaitingSysGotoArrival = false;
                    _stepInFlight = false;
                    AdvanceStep();
                }
                else
                {
                    _log?.Info("Walker",
                        "resume: sys goto still in flight; awaiting landing, not re-sending.");
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
                // Bound the wait: if whatever interrupted us swallowed the move, this
                // confirmation never arrives and the walk would hang indefinitely.
                ArmStallWatchdog($"resume with step {_index + 1} still in flight (Pending)");
                return;
            }

            // A door or winch request was mid-flight when the pause hit. No move has
            // gone out for this step — the request's own reply sends it — so the
            // tracker rightly still reads the source room.
            // The reconciliation below would take that for a move refused while
            // paused and replan (asking rm first) while the sub-FSM kept running:
            // its reply then sent the move behind the rm, the rm's answer named the
            // room being left, and the replan bashed a door that wasn't there
            // (report paradigm-20260924-053941, an @wait / @ok in the middle of a
            // bash). Wait for the reply instead. Only these two: each has a watchdog
            // of its own, so the wait is bounded. A hidden-exit search or a trap
            // handed to a party member has none, and for those the replan below is
            // what restarts one whose answer was lost.
            if (_stepInFlight && (_awaitingDoorOpen || _awaitingWinch))
            {
                _log?.Info("Walker",
                    $"resume: step {_index + 1} has a door or winch request in flight; awaiting its reply, not replanning or resending");
                return;
            }

            // A re-plan is waiting on the game's answer to where we are, and another
            // gate came and went inside that round trip (a sneak settling right
            // behind combat clearing does it within a tenth of a second). The
            // answer re-plans the walk; anything done here would send from the room
            // in doubt, or ask again and spend a second re-plan on the same doubt.
            if (_locateAsk is not null)
            {
                _log?.Info("Walker",
                    $"resume: already asking the game where we are; step {_index + 1} waits for the answer");
                return;
            }

            bool hadStepInFlight = _stepInFlight;
            _stepInFlight = false;
            _awaitingPromptForCommand = false;

            // The tracker lost its place while we were paused: a room display that
            // wasn't the room it held arrived mid-pause, and OnTrackerStateChanged
            // (gated on State == Walking) never passed that on. The next step is
            // planned from a room we may not be standing in, and sending it can't
            // even fail cleanly: a move sent from Suspect arms no Pending, so the
            // game's refusal changes nothing the walker reads (report
            // paradigm-20261009-082958: a move into a wall after a fight, then
            // nothing until the player typed `rm`). Where the game can be asked
            // where we are, ask first and re-plan from the answer. Where it can't,
            // a re-plan would start from this same room and send this same step,
            // so the resume carries on below as it always has.
            if (_tracker.State.Confidence == RoomConfidence.Suspect)
            {
                if (TryReplanOrFail(RoomConfidence.Suspect, "while paused", onlyIfAsked: true)) return;
                _log?.Info("Walker",
                    $"resume: the tracker went Suspect while paused (holding {_tracker.State.CurrentRoom?.Key.ToString() ?? "(no room)"}) "
                    + $"and the game couldn't be asked where we are; carrying on with step {_index + 1}");
            }

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

        // Still standing where the walk left off: nothing landed during the pause,
        // so there is nothing to fast-forward. This has to be settled before the
        // forward scan. A route that comes back through this room (the go-act-return
        // detour out to a lever or a statue and back) has a later step that also
        // ends here, and matching that one skipped the whole detour, its action
        // included: the walk went straight to the exit the action opens and was
        // refused there (report paradigm-20261007-202509: a held cast paused the walk
        // on the way to `turn statue`, and it resumed 43 steps on, past the statue).
        bool stillHere = RoomBeforeStep(_index) is { } standing && standing.Equals(hereKey);

        // Did the player reach one or more upcoming MoveStep targets
        // during the pause? Walk forward looking for the first match —
        // that's where they landed. (If the path revisits the same room
        // later, we conservatively assume the earliest matching step;
        // a manual long-traverse would surface as off-path further down.)
        for (int i = _index; !stillHere && i < _path.Count; i++)
        {
            // The step in flight may end somewhere other than the room the path
            // names for it: an exit whose spell teleports us on ends in its landing.
            bool inFlightLanded = i == _index && hadStepInFlight
                && _expectedAfterCurrentMove is { } landing && landing.Equals(hereKey);
            if (_path[i] is MoveStep move && (move.ExpectedTarget.Equals(hereKey) || inFlightLanded))
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

    // The room the walk stands in before step index: where the last move before it
    // lands, or the room the walk was planned from when no move comes before it.
    // Command steps in between don't change the room.
    private RoomKey? RoomBeforeStep(int index)
    {
        if (_path is null) return null;
        for (int i = Math.Min(index, _path.Count) - 1; i >= 0; i--)
            if (_path[i] is MoveStep move) return move.ExpectedTarget;
        return _origin;
    }

    private void Reset()
    {
        _tollCheckOwed = false;
        LastUnpaidFailure = null;
        _fleeHolding = false;
        _recovery?.Detach();
        // Drain downstream FSMs that were running on our behalf — if a
        // walk is superseded mid-door-open or mid-hidden-search, the
        // manager keeps its internal state (WaitingBash / Searching /
        // etc.) and the next walk's enqueue call sits in its queue
        // forever (TryStartNext bails on non-Idle state).
        //
        // The flags are cleared BEFORE the stoppers run. A stopper answers its
        // caller on the spot ("door flow stopped"), and with the flag still set
        // that answer came back in here as the walk failing: a Failed raised from
        // inside a silent redirect or a re-plan, which a detour router takes for
        // its walk dying and which read as the end of the journey. Cleared first,
        // OnDoorReply and the rest drop the answer, and the reset says nothing.
        bool stopDoor = _awaitingDoorOpen, stopHidden = _awaitingHiddenReveal,
            stopWinch = _awaitingWinch, stopTrap = _awaitingTrapDisarm;
        _awaitingDoorOpen = false;
        _awaitingHiddenReveal = false;
        _awaitingWinch = false;
        _awaitingTrapDisarm = false;
        if (stopDoor)   _doorStopAll?.Invoke();
        if (stopHidden) _hiddenSearchStopAll?.Invoke();
        if (stopWinch)  _winchStopAll?.Invoke();
        // Drop a pending party-delegation watch so a stray say reply can't
        // resume a superseded walk. Harmless when the trap was local-only.
        if (stopTrap)   _trapDelegateStopAll?.Invoke();

        _path = null;
        _index = 0;
        _expectedAfterCurrentMove = null;
        _destination = null;
        _origin = null;
        _stepInFlight = false;
        _emptyRoom.End();
        _bossRuleActive = false;
        HaltedBeforeBossRoom = null;
        _awaitingPromptForCommand = false;
        _boatTimer?.Dispose();
        _boatTimer = null;
        _awaitingBoatArrival = false;
        _sailingPlace = null;
        _sysGotoTimer?.Dispose();
        _sysGotoTimer = null;
        _awaitingSysGotoArrival = false;
        ClearGreetTeleportWait();
        DisarmStallWatchdog();
        _deferredWalkTimer?.Dispose();
        _deferredWalkTimer = null;
        _deferredWalkTarget = null;
        _deferredWalkThroughGates = false;
        _deferredWalkArmAcquisition = true;
        _deferredWalkAvoidTeleports = false;
        _deferredWalkAvoidTraps = false;
        _deferredWalkIgnoreAvoids = false;
        _deferredWalkPreferTeleportFree = false;
        _deferredWalkPickedRoute = false;
        _deferredWalkAutomaticTeleports = null;
        _activeAutomaticTeleports = null;
        _walkFilter = null;
        _activePickedRoute = false;
        _activeAvoidTeleports = false;
        _activePreferTeleportFree = false;
        _activeAvoidTraps = false;
        _activeIgnoreAvoids = false;
        _activeThroughGates = false;
        _activeArmAcquisition = true;
        _retryCount = 0;
        _replanCount = 0;
        // Kept through a re-plan in place, whose first step can be the very
        // crossing it was made to avoid (TurnedAsideAtToll).
        if (!_replanningInPlace) _crossingTurns = 0;
        _locateAsk = null;
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

    // How a walk began and ended goes in the program log from here, whoever is
    // listening. It used to be logged by the Navigation window's view-model, so
    // with that window closed a walk that failed left no line saying why.
    private void LogLifecycle(WalkEvent evt)
    {
        string verb;
        switch (evt.Kind)
        {
            case WalkEventKind.Started: verb = "started"; break;
            case WalkEventKind.Resumed: verb = "resumed"; break;
            case WalkEventKind.Paused: verb = "paused"; break;
            case WalkEventKind.Stopped: verb = "stopped"; break;
            case WalkEventKind.Finished: verb = "arrived"; break;
            case WalkEventKind.Failed: verb = "FAILED"; break;
            default: return;   // per-step events are the listeners' to report
        }
        _log?.Info("Walker", $"walk {verb}"
            + (evt.Destination is { } to
                ? $" (to {to}{(_graph.GetRoom(to)?.Name is { Length: > 0 } name ? " - " + name : string.Empty)})"
                : string.Empty)
            + (string.IsNullOrWhiteSpace(evt.Detail) ? string.Empty : $": {evt.Detail}"));
    }

    private void Raise(WalkEvent evt)
    {
        // A stop ends the journey whichever leg it catches. Arriving or failing ends
        // it only on a leg to its destination: a side trip that arrives or fails
        // leaves the journey standing for the leg that follows. Read from the leg
        // itself, not the event's room, which a stop-before boss room re-points.
        bool endsJourney = evt.Kind == WalkEventKind.Stopped
            || (evt.Kind is WalkEventKind.Finished or WalkEventKind.Failed && _legToJourneyGoal);
        if (endsJourney) EndJourney();
        LastEvent = evt;
        LogLifecycle(evt);
        Event?.Invoke(evt);
        // The doors this trip gave up on go with it, after the listeners had their
        // look at them: a Shortcut journey's silent legs, begun with no walk of the
        // walker's own to clear them, would otherwise inherit the last trip's.
        // A listener that started a new journey from the event keeps its own.
        if (endsJourney && _journey is null) _abandonedDoors.Clear();
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

// Requested is set on a Finished whose walk was asked for a boss room marked "stop
// before entering" and ended one room short of it: Destination is where the walk
// ended, Requested the room that was asked for. A caller waiting on its own room
// matches either.
//
// WillResume is set on a Stopped whose caller takes the walk up again itself.
public readonly record struct WalkEvent(
    WalkEventKind Kind, string Detail, RoomKey? Destination, RoomKey? Requested = null, bool WillResume = false);
