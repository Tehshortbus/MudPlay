using System;
using System.Text;
using Avalonia.Threading;
using MudPlay.Game.Inventory;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Map;

// The minimal surface the walker needs to hand a pyramid-climb destination off to
// the solver. Parallel to IMazeSolver — kept tiny so AutoWalkManager takes no hard
// dependency on the solver internals.
public interface IPyramidSolver
{
    // True when this destination is a Great Pyramid room the solver can climb to
    // (wire bound, leader/solo, not already mid-climb).
    bool CanSolve(RoomKey destination);

    // Take over navigation to destination. Returns true when the solver accepted
    // the job (it surfaces the outcome through the walker's Event).
    bool TryBegin(RoomKey destination);
}

// Drives the party leader up the Great Pyramid puzzle to 12/2085 — the case normal
// routing can't handle, because the floors are disconnected clusters joined only by
// sphinx `remoteaction` teleports BfsMapper never plans through (see
// GAME_MECHANICS.md "Great Pyramid puzzle climb" and PyramidScript).
//
// The route is a canned per-floor script (PyramidScript) that also names the room
// each step starts from. The solver sends one move and waits for the room tracker to
// say where it led — the arrival, a refusal, or a room it didn't expect — before the
// next. Waiting costs nothing: the game holds each further command until the last
// move's own delay has run out, on both realms, so sending ahead only queues moves
// that can no longer be taken back when one of them fails.
//
// F1 is timed and F2's room spells hurt more the longer you stand in them, so those
// two walk on through fights and rests; F3/F4/F5 hold for everything the walker
// would. It stops at 12/2085 — the `e` sphinx into the Tomb, Pharaoh Rastep, and the
// Dao Lord are player-handled.
//
// Shut F3 doors go to the shared door manager; the golden lion key is waited for in
// the floating key's room and asked for when a member picked it up.
//
// It drives the LEADER only; heals and the floating-key kill stay party-handled. A
// scatter (landing back in a Scorched Cavern / desert room), a death, or a position
// it can't recover halts the climb and reports through the walker like any other
// route failure.
public sealed class PyramidSolver : IPyramidSolver, IDisposable
{
    private const string LogSource = "Pyramid";

    // After an arrival on a held floor the next step waits this long, so the room's
    // occupants and a hold cast on a member are read — and their gates raised —
    // before we walk on. F4 waits longer: a member left behind on the footpath can't
    // simply follow. F1/F2 don't wait at all.
    private static readonly TimeSpan PacedDwell = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Floor4Dwell = TimeSpan.FromMilliseconds(1000);

    // A push block draws no line the pusher is known to see, so the step after it
    // simply follows a beat later.
    private static readonly TimeSpan ActionSettle = TimeSpan.FromMilliseconds(PyramidScript.ActionMillis);

    // While held (a gate, a held member) the solver re-checks on this interval; a
    // gate change re-checks at once. A member's hold wears off on a private line we
    // never see, so it is taken as gone after the cap.
    private static readonly TimeSpan HoldRecheck = TimeSpan.FromMilliseconds(1000);
    private const int HoldCapTicks = 20;       // ~20 s: hold person #66 (Dur 4) has worn off by now

    // A refused move is tried again after this, up to the cap.
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(1000);
    private const int MaxMoveRetries = 5;

    // A gate that stays shut sends us back to its block to push again, this often.
    private const int MaxGateRewinds = 2;

    // A move that draws neither an arrival nor a refusal within this is stalled.
    // Paradigm stretches it for a heavy character's slow hop.
    private static readonly TimeSpan MinStall = TimeSpan.FromSeconds(4);
    private const double StallHops = 3;

    // With nothing to ask, a stalled move is taken as landed (the game answers
    // every refused move with a line). That is a guess, so only this many per climb.
    private const int MaxAssumedLandings = 5;

    // A throttled `rm` is asked once more after the resolver's own spacing.
    private static readonly TimeSpan ResyncRetryDelay = TimeSpan.FromMilliseconds(2200);

    // A tracker still mid-move with nothing of ours in flight (a hand-typed step)
    // gets this many short waits to settle before we ask where we are.
    private const int MaxPendingWaits = 6;

    // A sphinx that never opens the ceiling within this window gets its `ask`
    // re-sent (a dropped line), up to a small retry cap, before the climb gives up.
    private static readonly TimeSpan SphinxTimeout = TimeSpan.FromSeconds(4);
    private const int MaxSphinxRetries = 3;

    // A door we can't open ourselves is watched once a second. Its opening line
    // re-checks at once; a `look` every few seconds catches one that was missed.
    // In the dark neither shows, so the move itself is tried on the same beat.
    private static readonly TimeSpan DoorWatch = TimeSpan.FromMilliseconds(1000);
    private const int DoorLookEvery = 5;
    private const int MaxDoorWatchTicks = 180;   // ~3 min of a door that never opens

    // The door manager gets this many goes at one door before the climb gives up.
    private static readonly TimeSpan DoorRetryDelay = TimeSpan.FromMilliseconds(1500);
    private const int MaxDoorOpenAttempts = 3;

    // In the floating key's room with no key in hand: this long for the kill and the
    // pickup to finish (fights and pickups hold the climb on their own gates, so
    // this only counts idle seconds), then out and back in to respawn a key that
    // didn't drop, this many times.
    private const int KeyWaitTicks = 8;
    private const int KeyGiveTicks = 6;
    private const int MaxKeyRespawns = 3;

    // Whole-climb runaway guard — the real climb is ~230 commands; well past that
    // means something is going round in circles.
    private const int MaxTotalSends = 700;

    private enum Phase { Idle, Climbing, AwaitingMove, AwaitingSphinx, AwaitingDoor, AwaitingKey, Held, Resyncing, Done }

    // One move outside the script: the entry `up`, or a step back toward a block.
    private readonly record struct Detour(Direction Dir, int From, int To);

    private readonly RoomTracker _tracker;
    private readonly AutoWalkManager _walker;
    private readonly LogService? _log;
    private readonly Func<bool> _isParadigm;
    private readonly Func<InventorySnapshot> _snapshot;
    private readonly Func<int> _quickness;
    private readonly Func<bool> _canDrive;      // leader or solo — else the solver must not steer
    private readonly Func<string?> _leaderName; // for the F3 @party give consolidation
    private readonly Func<bool> _enabled;       // Settings → Other master toggle
    private readonly MovementCoordinator? _coordinator;
    private readonly Func<string, bool> _isPartyMember; // is this name a party member (for hold detection)
    // Asks the game where we are (Paradigm `rm`, a sysop's locate): reason, answer,
    // no-answer. False when it couldn't ask.
    private readonly Func<string, Action<RoomKey>, Action, bool>? _askPosition;
    // The shared door manager's Enqueue: direction, stat requirement, bashable, key
    // item, sender, reply.
    private readonly Action<Direction, int, bool, int, string, Action<DoorOpenResult>>? _openDoor;
    // The door manager's stop. The manager goes on at a door until it opens or the
    // room changes, so a climb that ends with its door still being worked stops it.
    private readonly Action? _stopDoor;
    private readonly Func<int, bool>? _holdsItem;
    // True when the auto-engine behind a movement gate is switched on (see
    // PyramidRunThrough).
    private readonly Func<string, bool>? _gateEngineOn;
    private readonly Action<Action> _post;

    private IDisposable? _settleTimer;
    // Arms one tick after the delay; disposing what it returns cancels it.
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;

    private Action<byte[]>? _wireSender;
    private LineExtractor? _lines;
    private bool _disposed;

    private Phase _phase = Phase.Idle;
    private RoomKey _goal;
    private PyramidFloor _floor = PyramidFloor.None;
    private int _stepIndex;
    private int _sphinxRetries;
    private int _totalSends;

    // The one move on the wire whose outcome we're waiting for, and the room it
    // should lead to.
    private bool _moveInFlight;
    private int _flightTarget;

    // The floor whose sphinx has opened its ceiling and whose `up` is still to take.
    private PyramidFloor _ceilingOpenOn = PyramidFloor.None;

    private readonly Queue<Detour> _detour = new();
    private int _moveRetries;
    private int _gateRewinds;
    private int _assumedLandings;
    private int _pendingWaits;
    private bool _resyncRetried;
    private string? _holdReason;

    // Set when the settle timer ticks — the one continuation to run then. Keeps the
    // "what happens next" explicit per schedule instead of guessing from phase.
    private Action? _settleCont;

    // The F3 door being worked: which step it belongs to, whether a move into it has
    // been refused, how long it has been watched, and the door manager's goes at it.
    private int _doorStep = -1;
    private bool _doorRefused;
    private int _doorWatchTicks;
    private int _doorOpenAttempts;
    private string? _doorFailure;
    // A request is with the door manager; its reply carries this ticket.
    private bool _doorOpening;
    private int _doorTicket;

    // The golden lion key: seen going into our own pack, or which member picked it
    // up (they are asked to hand it over).
    private bool _selfTookKey;
    private string? _keyGrabber;
    private bool _keyGiveAsked;
    private int _keyWaitTicks;
    private int _keyRespawns;

    // Party members currently held by an undead-priest hold person (more than one
    // can be held at once). A member is added on the cast, removed on a
    // cure/freedom cast naming them, or force-cleared after the duration cap (their
    // natural wear-off is a private line we never see). On the floors we wait out
    // holds (F3/F4), a non-empty set blocks stepping.
    private readonly HashSet<string> _heldMembers = new(StringComparer.OrdinalIgnoreCase);

    // The leader (us) can be held too — set on our own "Your legs are paralyzed!",
    // cleared on "You can move again!" (both are lines we DO see for ourselves).
    private bool _selfHeld;
    private int _selfHeldTicks;

    // Re-check ticks a party-member hold has persisted; at the cap we assume it wore
    // off (we can't see a member's private wear-off) and clear the held set.
    private int _holdTicks;

    // ----- bug-report surface ----------------------------------------
    public bool Active { get; private set; }
    public RoomKey? Goal => Active ? _goal : (RoomKey?)null;
    public string FloorName => _floor.ToString();
    public string PhaseName => _phase.ToString();
    public int StepsDriven => _totalSends;
    public int ScriptStep => _stepIndex + 1;
    public int ScriptSteps => PyramidScript.Steps(_floor).Count;
    // The room the next move is scripted to start from, to set against the tracker's
    // room in a report.
    public RoomKey? ExpectedRoom => ExpectedRoomNumber() is var room and > 0
        ? new RoomKey(PyramidScript.PyramidMap, room)
        : null;
    public bool Enabled => _enabled();
    // Waiting on a gate or a held member rather than walking.
    public bool IsHeld => Active && _phase == Phase.Held;
    public string? HoldReasonText => IsHeld ? _holdReason : null;
    public RoomKey? MoveInFlightTo => _moveInFlight ? new RoomKey(PyramidScript.PyramidMap, _flightTarget) : null;
    public int MoveRetries => _moveRetries;
    public int GateRewinds => _gateRewinds;
    public int AssumedLandings => _assumedLandings;
    public bool DoorWithManager => _doorOpening;
    public int DoorWatchSeconds => _doorWatchTicks;
    public string KeyStatus => HaveKey() ? "in hand"
        : _keyGrabber is { } g ? $"picked up by {g}" + (_keyGiveAsked ? " (hand-over asked)" : "")
        : "not seen";
    public int KeyRespawns => _keyRespawns;

    // Raised when the climb starts, ends, or goes into or out of a hold, so the
    // toolbar's run-state follows it.
    public event Action? StateChanged;

    // ----- Navigation surface ----------------------------------------
    // The walker is idle while the climb runs, so the Navigation window had nothing
    // to draw and read "Idle" (report paradigm-20261007-234656). These give it the
    // same two things a walk does: the rooms still ahead, and where it is headed.

    private readonly Func<DateTimeOffset> _now;
    private DateTimeOffset? _floor1Since;

    // Time left on floor 1 before the party is scattered: five minutes from the
    // firepit `up` (PyramidScript.Floor1Budget). Null off floor 1, and for a climb
    // picked up part-way along it, whose clock started before we were watching.
    public TimeSpan? Floor1TimeLeft
    {
        get
        {
            if (!Active || _floor != PyramidFloor.F1 || _floor1Since is not { } since) return null;
            TimeSpan left = PyramidScript.Floor1Budget - (_now() - since);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    // The current floor's script as the Current Nav list words a walk's steps:
    // "west", "push block", "ask sphinx fire, then up", "open door north".
    public IReadOnlyList<string> FloorStepLabels
    {
        get
        {
            if (!Active) return Array.Empty<string>();
            List<string> labels = new();
            foreach (PyramidStep step in PyramidScript.Steps(_floor))
                labels.Add(step.Kind switch
                {
                    PyramidStepKind.PushBlock => "push block",
                    PyramidStepKind.AskSphinx => $"ask sphinx {step.Word}, then {step.Dir.ToLongName()}",
                    PyramidStepKind.Door      => $"door {step.Dir.ToLongName()}",
                    PyramidStepKind.KeyDoor   => $"golden lion key door {step.Dir.ToLongName()}",
                    _                         => step.Dir.ToLongName(),
                });
            return labels;
        }
    }

    // The rooms the script still has to pass, from the step at hand to the goal (or
    // the top), for the map's route line. A step that doesn't move (a block pushed,
    // a sphinx asked) repeats its room and is listed once.
    public IReadOnlyList<RoomKey> RemainingRoomKeys
    {
        get
        {
            List<RoomKey> keys = new();
            if (!Active) return keys;
            bool done = false;
            void Add(int room)
            {
                if (done || room <= 0) return;
                RoomKey key = new(PyramidScript.PyramidMap, room);
                if (keys.Count == 0 || !keys[^1].Equals(key)) keys.Add(key);
                if (key.Equals(_goal)) done = true;
            }

            if (_tracker.State.CurrentRoom is { } here) keys.Add(here.Key);
            PyramidFloor floor = _floor == PyramidFloor.Firepit ? PyramidFloor.F1 : _floor;
            int from = _floor == PyramidFloor.Firepit ? 0 : _stepIndex;
            for (; floor is >= PyramidFloor.F1 and <= PyramidFloor.F5 && !done; floor++, from = 0)
            {
                IReadOnlyList<int>? rooms = PyramidScript.FromRooms(floor);
                if (rooms is null) break;
                for (int i = from; i < rooms.Count; i++) Add(rooms[i]);
                Add(PyramidScript.EndRoom(floor));
            }
            return keys;
        }
    }

    // On F1 or F2, the floors that are run through. Raised true as the climb comes
    // onto them and false as it leaves them or ends: the auto-engines it would
    // otherwise walk away from are switched off for exactly that stretch.
    public bool IsRunningThrough { get; private set; }
    public event Action<bool>? RunThroughChanged;

    private void UpdateRunThrough()
    {
        bool now = Active && PyramidScript.KeepsMoving(_floor);
        if (now == IsRunningThrough) return;
        IsRunningThrough = now;
        _log?.Log(LogSeverity.Info, LogSource, now
            ? $"{_floor}: running through — combat, nuke, rest, get, search, hide and light autos go off until floor 3"
            : "run-through over — the autos the climb switched off come back on");
        RunThroughChanged?.Invoke(now);
    }

    public PyramidSolver(
        RoomTracker tracker,
        AutoWalkManager walker,
        Func<InventorySnapshot> snapshot,
        Func<int> quickness,
        LogService? log = null,
        Func<bool>? isParadigm = null,
        Func<bool>? canDrive = null,
        Func<string?>? leaderName = null,
        Func<bool>? enabled = null,
        MovementCoordinator? coordinator = null,
        Func<string, bool>? isPartyMember = null,
        Func<string, Action<RoomKey>, Action, bool>? askPosition = null,
        Action<Direction, int, bool, int, string, Action<DoorOpenResult>>? openDoor = null,
        Func<int, bool>? holdsItem = null,
        Func<string, bool>? gateEngineOn = null,
        Action? stopDoor = null,
        Func<DateTimeOffset>? now = null,
        Action<Action>? post = null,
        Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _schedule = schedule ?? DispatcherTicks.Once;
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(walker);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(quickness);

        _tracker = tracker;
        _walker = walker;
        _snapshot = snapshot;
        _quickness = quickness;
        _log = log;
        _post = post ?? (a => Dispatcher.UIThread.Post(a));
        _isParadigm = isParadigm ?? (() => false);
        _canDrive = canDrive ?? (() => true);
        _leaderName = leaderName ?? (() => null);
        _enabled = enabled ?? (() => true);
        _coordinator = coordinator;
        _isPartyMember = isPartyMember ?? (_ => false);
        _askPosition = askPosition;
        _openDoor = openDoor;
        _stopDoor = stopDoor;
        _holdsItem = holdsItem;
        _gateEngineOn = gateEngineOn;

        _tracker.StateChanged += OnTrackerStateChanged;
        _tracker.MoveBlocked += OnTrackerMoveBlocked;
        _tracker.PlayerDeathObserved += OnPlayerDied;
        if (_coordinator is not null) _coordinator.GatesChanged += OnGatesChanged;
    }

    // Main-window VM supplies the EngineSendGate-wrapped SendUserInput. Without it
    // the solver can't drive, so CanSolve stays false.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Broadcast feed: the sphinx "concealed passage" cue, the golden-lion-key
    // pickup, and the scatter room name all arrive as plain lines.
    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    public bool CanSolve(RoomKey destination)
        => Enabled && _wireSender is not null && !Active && _canDrive()
           && destination.Map == PyramidScript.PyramidMap
           && PyramidScript.FloorOf(destination.Map, destination.Room) is not PyramidFloor.None;

    public bool TryBegin(RoomKey destination)
    {
        if (!CanSolve(destination)) return false;

        _goal = destination;
        _phase = Phase.Idle;
        _floor = PyramidFloor.None;
        _floor1Since = null;
        _stepIndex = 0;
        _sphinxRetries = 0;
        _totalSends = 0;
        _settleCont = null;
        _doorStep = -1;
        _doorOpening = false;
        _doorTicket++;
        _selfTookKey = false;
        _keyGrabber = null;
        _keyGiveAsked = false;
        _keyWaitTicks = 0;
        _keyRespawns = 0;
        _heldMembers.Clear();
        _selfHeld = false;
        _selfHeldTicks = 0;
        _holdTicks = 0;
        _moveInFlight = false;
        _ceilingOpenOn = PyramidFloor.None;
        _detour.Clear();
        _moveRetries = 0;
        _gateRewinds = 0;
        _assumedLandings = 0;
        _pendingWaits = 0;
        _resyncRetried = false;
        _holdReason = null;
        Active = true;
        _log?.Log(LogSeverity.Info, LogSource, $"engaging pyramid solver for {destination.Map}/{destination.Room}");
        StateChanged?.Invoke();
        // Defer off the walker's call stack — TryBegin runs inside WalkToImmediate.
        _post(Start);
        return true;
    }

    // ----- start / pre-flight ----------------------------------------

    private void Start()
    {
        if (!Active) return;

        RoomState st = _tracker.State;
        int room = st.Confidence == RoomConfidence.Confirmed && st.CurrentRoom is { } cur ? cur.Key.Room : -1;
        PyramidFloor at = room >= 0 ? PyramidScript.FloorOf(PyramidScript.PyramidMap, room) : PyramidFloor.None;

        // The climb is only launched from the firepit or an already-entered floor.
        // Anywhere else, the walker was expected to route us to the firepit first;
        // fail loudly rather than blast moves from an unknown spot.
        if (at == PyramidFloor.None)
        {
            FailSolve("not at the firepit or on a pyramid floor — route to 12/1239 first");
            return;
        }

        // Pre-flight timer gate — only meaningful from the firepit / F1, before the
        // timed floor. Refuse a climb the leader can't finish in time.
        if (at is PyramidFloor.Firepit or PyramidFloor.F1)
        {
            InventorySnapshot snap = _snapshot();
            PyramidPreflightResult pre = PyramidPreflight.Evaluate(
                _isParadigm(), snap.Encumbrance.Percentage, snap.Encumbrance.Category, _quickness());
            if (!pre.Feasible)
            {
                FailSolve($"pre-flight: {pre.Reason}");
                return;
            }
            _log?.Log(LogSeverity.Info, LogSource, $"pre-flight ok: {pre.Reason}");
        }

        _floor = at;
        _stepIndex = 0;
        _log?.Log(LogSeverity.Info, LogSource, $"climb starts at 12/{room} ({at})");
        UpdateRunThrough();
        if (at == PyramidFloor.Firepit)
        {
            // Enter the pyramid: `up` casts the timer and drops us on F1.
            _log?.Log(LogSeverity.Info, LogSource, "entering pyramid from firepit (up)");
            _detour.Enqueue(new Detour(Direction.U, room, PyramidScript.FromRooms(PyramidFloor.F1)![0]));
        }
        Drive();
    }

    // ----- step driving ----------------------------------------------

    // Take the next step from where the tracker has us: hold if something says to,
    // re-anchor the script if the room isn't the one the step starts from, then send.
    private void Drive()
    {
        if (!Active) return;
        StopTimer();

        if (HoldReason() is { } hold)
        {
            EnterHold(hold);
            return;
        }
        LeaveHold();

        RoomState st = _tracker.State;
        if (st.Confidence == RoomConfidence.Confirmed && st.CurrentRoom is { } cur)
        {
            _pendingWaits = 0;
            // A floor whose script is spent is anchored too: that is what moves the
            // climb on to the floor its end room belongs to.
            bool here = cur.Key.Map == PyramidScript.PyramidMap && cur.Key.Room == ExpectedRoomNumber()
                && (_detour.Count > 0 || _stepIndex < PyramidScript.Steps(_floor).Count);
            PyramidFloor was = _floor;
            if (!here && !AnchorTo(cur.Key)) return;
            // A new floor can hold for things the one below walked through.
            if (_floor != was && HoldReason() is { } onArrival)
            {
                EnterHold(onArrival);
                return;
            }
            if (cur.Key == _goal)
            {
                Finish();
                return;
            }
        }
        else if (st.Confidence == RoomConfidence.Pending && ++_pendingWaits <= MaxPendingWaits)
        {
            // A move that isn't ours is still landing (a hand-typed step). Let it.
            ScheduleSettle(RetryDelay, Drive);
            return;
        }
        else
        {
            BeginResync($"position is {st.Confidence}");
            return;
        }

        if (_detour.Count > 0)
        {
            Detour d = _detour.Peek();
            SendStepMove(d.Dir, d.To);
            return;
        }

        if (_floor == PyramidFloor.F3 && !KeySettled(cur.Key.Room)) return;

        PyramidStep step = PyramidScript.Steps(_floor)[_stepIndex];
        switch (step.Kind)
        {
            case PyramidStepKind.Move:
                SendStepMove(step.Dir, RoomAfter(_stepIndex));
                break;

            case PyramidStepKind.PushBlock:
                _phase = Phase.Climbing;
                if (!CountSend()) return;
                _log?.Log(LogSeverity.Info, LogSource, $"{_floor} step {_stepIndex + 1}: push block at 12/{cur.Key.Room}");
                SendCommand("push block");
                ScheduleSettle(ActionSettle, AdvanceAndDrive);
                break;

            case PyramidStepKind.AskSphinx:
                // A hold that began after the sphinx answered left the way up open.
                if (_ceilingOpenOn == _floor) SendStepMove(Direction.U, PyramidScript.EndRoom(_floor));
                else BeginSphinx(step.Word!);
                break;

            case PyramidStepKind.Door:
            case PyramidStepKind.KeyDoor:
                DriveDoor(step, cur);
                break;
        }
    }

    // The room the next move starts from: the detour's when one is queued, the
    // script's otherwise, the floor's end room once its script is spent. 0 where the
    // floor has no script (the firepit, before the entry move is queued).
    private int ExpectedRoomNumber()
    {
        if (_detour.Count > 0) return _detour.Peek().From;
        if (PyramidScript.FromRooms(_floor) is not { } from) return 0;
        return _stepIndex < from.Count ? from[_stepIndex] : PyramidScript.EndRoom(_floor);
    }

    // The room step i's move leads to.
    private int RoomAfter(int i)
    {
        IReadOnlyList<int> from = PyramidScript.FromRooms(_floor)!;
        return i + 1 < from.Count ? from[i + 1] : PyramidScript.EndRoom(_floor);
    }

    // Put the script on the room we're actually in: its floor, and the step scripted
    // from it. Where a room recurs on the floor we were already driving, the
    // occurrence nearest the current step wins, earlier on a tie — being back in an
    // already-driven step's room means that step's move didn't land, and on F1 the
    // earlier pass is the one that still pushes the block. Returns false when the
    // climb ended here instead (the top, a scatter room, a room off the route).
    private bool AnchorTo(RoomKey key)
    {
        if (PyramidScript.IsScatterRoom(key.Map, key.Room))
        {
            FailSolve($"scattered to {key.Map}/{key.Room} — climb failed");
            return false;
        }
        PyramidFloor floor = PyramidScript.FloorOf(key.Map, key.Room);
        if (floor == PyramidFloor.Top)
        {
            Finish();
            return false;
        }
        if (PyramidScript.FromRooms(floor) is not { } from)
        {
            FailSolve($"left the pyramid at {key.Map}/{key.Room}");
            return false;
        }

        int near = floor == _floor ? _stepIndex : 0;
        int best = -1;
        for (int i = 0; i < from.Count; i++)
        {
            if (from[i] != key.Room) continue;
            if (best < 0 || Math.Abs(i - near) < Math.Abs(best - near)) best = i;
        }
        if (best < 0)
        {
            FailSolve($"12/{key.Room} is off the climb's route on {floor}");
            return false;
        }

        // Floor 1's clock starts with the firepit `up`, which is the move that
        // brought us onto it.
        if (floor == PyramidFloor.F1 && _floor == PyramidFloor.Firepit) _floor1Since = _now();
        if (floor != _floor)
            _log?.Log(LogSeverity.Info, LogSource,
                best == 0 ? $"driving {floor}" : $"driving {floor} from step {best + 1} (12/{key.Room})");
        else if (best != _stepIndex || _detour.Count > 0)
            _log?.Log(LogSeverity.Info, LogSource,
                $"{floor} step {_stepIndex + 1} expected 12/{ExpectedRoomNumber()} but the tracker has us at 12/{key.Room} — resuming at step {best + 1}");

        _floor = floor;
        _stepIndex = best;
        _detour.Clear();
        _moveRetries = 0;
        UpdateRunThrough();
        return true;
    }

    // Consume the current step and drive the next.
    private void AdvanceAndDrive()
    {
        _stepIndex++;
        Drive();
    }

    // ----- one move at a time ----------------------------------------

    private bool CountSend()
    {
        if (++_totalSends <= MaxTotalSends) return true;
        FailSolve("step budget exhausted");
        return false;
    }

    private void SendStepMove(Direction dir, int target)
    {
        if (!CountSend()) return;
        _moveInFlight = true;
        _flightTarget = target;
        _phase = Phase.AwaitingMove;
        _log?.Debug(LogSource, $"{_floor} step {_stepIndex + 1}: {dir.ToToken()} → 12/{target}");
        // Announce it as ours, so its echo isn't read as a hand-typed step that
        // pauses navigation.
        _tracker.NoteMoveSent(dir);
        Send(AutoWalkManager.EncodeMove(dir));
        if (_moveInFlight) ScheduleSettle(StallAfter(), OnMoveStalled);
    }

    private TimeSpan StallAfter()
    {
        if (!_isParadigm()) return MinStall;
        double hop = PyramidPreflight.PacedPerMoveMs(_snapshot().Encumbrance.Percentage, _quickness());
        return TimeSpan.FromMilliseconds(Math.Max(MinStall.TotalMilliseconds, hop * StallHops));
    }

    private void OnTrackerStateChanged(RoomTransition t)
    {
        if (!Active || _phase == Phase.Resyncing) return;
        // Between moves the next Drive reads the room for itself.
        if (!_moveInFlight || t.NewConfidence == RoomConfidence.Pending) return;

        if (t.NewConfidence == RoomConfidence.Confirmed && t.NewRoom is { } now)
        {
            if (now.Key.Map == PyramidScript.PyramidMap && now.Key.Room == _flightTarget)
            {
                OnMoveLanded();
                return;
            }
            // Back to sure of the room we sent it from: the move wasn't taken. A
            // refusal raises MoveBlocked next, which handles it; a move the game
            // dropped raises nothing more and is picked up when the stall wait ends.
            if (t.PreviousRoom is { } was && was.Key == now.Key) return;
            // Somewhere else. Drive re-anchors on it.
            _log?.Log(LogSeverity.Info, LogSource,
                $"{_floor} step {_stepIndex + 1}: headed for 12/{_flightTarget}, landed in {now.Key.Map}/{now.Key.Room}");
            _moveInFlight = false;
            Drive();
            return;
        }

        if (t.NewConfidence == RoomConfidence.PendingRespawn) return;   // OnPlayerDied ends the climb
        BeginResync($"the tracker went {t.NewConfidence} mid-move");
    }

    private void OnTrackerMoveBlocked()
    {
        if (!Active || _phase == Phase.Resyncing || !_moveInFlight) return;
        OnMoveRefused();
    }

    private void OnMoveLanded()
    {
        StopTimer();
        _moveInFlight = false;
        _moveRetries = 0;
        if (_detour.Count > 0) _detour.Dequeue();
        else _stepIndex++;

        // By the floor we landed on: the step up out of F2 arrives on F3.
        TimeSpan dwell = DwellFor(PyramidScript.FloorOf(PyramidScript.PyramidMap, _flightTarget));
        _phase = Phase.Climbing;
        if (dwell > TimeSpan.Zero) ScheduleSettle(dwell, Drive);
        else Drive();
    }

    private static TimeSpan DwellFor(PyramidFloor floor) => floor switch
    {
        PyramidFloor.F4 => Floor4Dwell,
        PyramidFloor.F3 or PyramidFloor.F5 => PacedDwell,
        _ => TimeSpan.Zero,
    };

    // The game refused the move (or dropped it). The tracker has un-counted it, so
    // we still stand where it was sent from.
    private void OnMoveRefused()
    {
        StopTimer();
        _moveInFlight = false;
        _phase = Phase.Climbing;

        // Held, paused, paralysed: Drive waits it out and sends the step again.
        if (HoldReason() is not null)
        {
            Drive();
            return;
        }

        var steps = PyramidScript.Steps(_floor);
        PyramidStep? step = _detour.Count == 0 && _stepIndex < steps.Count ? steps[_stepIndex] : null;
        // The way up shut again before we took it: the retry asks the sphinx afresh.
        if (step is { Kind: PyramidStepKind.AskSphinx }) _ceilingOpenOn = PyramidFloor.None;

        if (step is { Kind: PyramidStepKind.Move, Gate: true })
        {
            RewindToBlock();
            return;
        }
        if (step is { Kind: PyramidStepKind.Door or PyramidStepKind.KeyDoor })
        {
            // Shut after all (or shut again since it was last shown): work the door.
            _doorRefused = true;
            Drive();
            return;
        }

        if (++_moveRetries > MaxMoveRetries)
        {
            FailSolve($"{_floor} step {_stepIndex + 1}: the move out of 12/{ExpectedRoomNumber()} keeps being refused");
            return;
        }
        _log?.Log(LogSeverity.Info, LogSource,
            $"{_floor} step {_stepIndex + 1}: move refused — retry {_moveRetries}/{MaxMoveRetries}");
        ScheduleSettle(RetryDelay, Drive);
    }

    // The gate ahead is shut: its block's push didn't take. Walk the script
    // backwards to the block and play it forward again from the push.
    private void RewindToBlock()
    {
        var steps = PyramidScript.Steps(_floor);
        IReadOnlyList<int> from = PyramidScript.FromRooms(_floor)!;
        int push = _stepIndex - 1;
        while (push >= 0 && steps[push].Kind != PyramidStepKind.PushBlock) push--;
        if (push < 0 || ++_gateRewinds > MaxGateRewinds)
        {
            FailSolve($"the gate out of 12/{from[_stepIndex]} stays shut after pushing its block");
            return;
        }

        _log?.Log(LogSeverity.Warn, LogSource,
            $"{_floor} step {_stepIndex + 1}: gate out of 12/{from[_stepIndex]} is shut — back to the block at 12/{from[push]} to push again");
        _detour.Clear();
        for (int i = _stepIndex - 1; i > push; i--)
            _detour.Enqueue(new Detour(steps[i].Dir.Opposite(), from[i + 1], from[i]));
        _stepIndex = push;
        Drive();
    }

    private void OnMoveStalled()
    {
        if (!Active || !_moveInFlight) return;
        _log?.Log(LogSeverity.Warn, LogSource,
            $"{_floor} step {_stepIndex + 1}: no arrival or refusal for the move to 12/{_flightTarget}");
        BeginResync("a move drew no answer");
    }

    // ----- finding ourselves again -----------------------------------

    // We no longer know which room we're in. Ask the game where it can tell us
    // (Paradigm's `rm`, a sysop's locate); otherwise, or when that goes unanswered,
    // fall back on what the tracker and the script say.
    private void BeginResync(string why)
    {
        if (!Active) return;
        StopTimer();
        _phase = Phase.Resyncing;
        if (_askPosition is { } ask)
        {
            if (ask($"pyramid climb: {why}", OnPositionResolved, OnPositionUnresolved))
            {
                _log?.Log(LogSeverity.Info, LogSource, $"asking the game where we are ({why})");
                return;
            }
            // Paradigm always has `rm`; a refusal there is its spacing between asks.
            if (_isParadigm() && !_resyncRetried)
            {
                _resyncRetried = true;
                ScheduleSettle(ResyncRetryDelay, () => BeginResync(why));
                return;
            }
        }
        ResolveWithoutAsking(why);
    }

    private void OnPositionResolved(RoomKey key)
    {
        if (!Active || _phase != Phase.Resyncing) return;
        _resyncRetried = false;
        _moveInFlight = false;
        RoomState st = _tracker.State;
        if (st.Confidence != RoomConfidence.Confirmed || st.CurrentRoom?.Key != key)
            _tracker.SetLocated(key);
        _phase = Phase.Climbing;
        _log?.Log(LogSeverity.Info, LogSource, $"the game has us at {key.Map}/{key.Room}");
        Drive();
    }

    private void OnPositionUnresolved()
    {
        if (!Active || _phase != Phase.Resyncing) return;
        ResolveWithoutAsking("`rm` went unanswered");
    }

    private void ResolveWithoutAsking(string why)
    {
        _resyncRetried = false;
        RoomState st = _tracker.State;
        bool trackerSure = st.Confidence == RoomConfidence.Confirmed && st.CurrentRoom is not null;
        if (!trackerSure)
        {
            // The game answers every refused move with a line, so a move that drew
            // none went through; with none in flight we are where the script left us.
            int room = _moveInFlight ? _flightTarget : ExpectedRoomNumber();
            if (room <= 0 || ++_assumedLandings > MaxAssumedLandings)
            {
                FailSolve($"lost our place on {_floor} ({why})");
                return;
            }
            _log?.Log(LogSeverity.Warn, LogSource,
                $"{why} — taking 12/{room} as where we are ({_assumedLandings}/{MaxAssumedLandings})");
            _tracker.SetLocated(new RoomKey(PyramidScript.PyramidMap, room));
        }
        _moveInFlight = false;
        _phase = Phase.Climbing;
        Drive();
    }

    // ----- holds -----------------------------------------------------

    // Gates every floor waits on: the user's own pause, Auto-All, and the states in
    // which a move can't be made or wouldn't go where it's aimed.
    private static readonly string[] AlwaysGates =
    {
        MovementCoordinator.UserGate,
        MovementCoordinator.AutoAllGate,
        MovementCoordinator.HeldGate,
        MovementCoordinator.MortallyWoundedGate,
        MovementCoordinator.FearGate,
        MovementCoordinator.TooHeavyGate,
    };

    // Floors where the climb holds for everything the walker would. F1/F2 walk on
    // through party waits and the like, and through fights, rests, pickups and
    // searches unless the player has that engine switched on (PyramidRunThrough):
    // F1 is on a timer and standing in F2 hurts more the longer it lasts.
    private static bool HoldsForEverything(PyramidFloor f) => !PyramidScript.KeepsMoving(f);

    // Why the next step must wait, or null when it may go.
    private string? HoldReason()
    {
        if (_selfHeld) return "held";
        if (_coordinator is { } c)
        {
            foreach (string gate in AlwaysGates)
                if (c.IsGateAsserted(gate)) return gate;
            if (HoldsForEverything(_floor))
            {
                if (c.IsPaused) return string.Join(", ", c.AssertedGates);
            }
            else if (_gateEngineOn is { } engineOn)
            {
                // An engine the player switched back on for these floors is theirs
                // to have: the climb waits for it.
                foreach (string gate in c.AssertedGates)
                    if (engineOn(gate)) return gate;
            }
        }
        if (HoldsForEverything(_floor) && _heldMembers.Count > 0)
            return $"held member(s) {string.Join(", ", _heldMembers)}";
        return null;
    }

    private void EnterHold(string reason)
    {
        bool wasHeld = _phase == Phase.Held;
        if (!wasHeld || reason != _holdReason)
            _log?.Log(LogSeverity.Info, LogSource, $"{_floor} step {_stepIndex + 1}: holding — {reason}");
        _phase = Phase.Held;
        _holdReason = reason;

        // Our own wear-off line can be missed. Past the cap, stop taking our word for
        // it: the Held gate still holds us if the game does, and a move it refuses
        // says so.
        if (_selfHeld && ++_selfHeldTicks >= HoldCapTicks)
        {
            _log?.Log(LogSeverity.Info, LogSource, "our own hold assumed worn off after cap");
            _selfHeld = false;
            _selfHeldTicks = 0;
        }

        // A party member's hold wears off on a private line we never see, so after
        // the cap assume it's gone rather than wait on it forever.
        if (_heldMembers.Count > 0 && ++_holdTicks >= HoldCapTicks)
        {
            _log?.Log(LogSeverity.Info, LogSource,
                $"held member(s) [{string.Join(", ", _heldMembers)}] assumed freed after cap");
            _heldMembers.Clear();
            _holdTicks = 0;
        }
        ScheduleSettle(HoldRecheck, Drive);
        if (!wasHeld) StateChanged?.Invoke();
    }

    private void LeaveHold()
    {
        if (_heldMembers.Count == 0) _holdTicks = 0;
        if (_phase != Phase.Held) return;
        _log?.Log(LogSeverity.Info, LogSource, $"hold cleared ({_holdReason}) — climbing on");
        _phase = Phase.Climbing;
        _holdReason = null;
        StateChanged?.Invoke();
    }

    private void OnGatesChanged()
    {
        if (Active && _phase == Phase.Held) Drive();
    }

    private void OnPlayerDied()
    {
        if (Active) FailSolve("died during the climb");
    }

    // ----- settle timer ----------------------------------------------

    private void ScheduleSettle(TimeSpan interval, Action continuation)
    {
        _settleCont = continuation;
        _settleTimer?.Dispose();
        _settleTimer = _schedule(interval, OnSettleTick);
    }

    private void StopTimer()
    {
        _settleTimer?.Dispose();
        _settleTimer = null;
        _settleCont = null;
    }

    private void OnSettleTick()
    {
        _settleTimer = null;
        if (!Active) return;
        Action? cont = _settleCont;
        _settleCont = null;
        cont?.Invoke();
    }

    // ----- sphinx ascension ------------------------------------------

    private void BeginSphinx(string word)
    {
        if (!CountSend()) return;
        _phase = Phase.AwaitingSphinx;
        _sphinxRetries = 0;
        _log?.Log(LogSeverity.Info, LogSource, $"ask sphinx {word} → awaiting ceiling");
        SendCommand("ask sphinx " + word);
        ScheduleSettle(SphinxTimeout, OnSphinxTimeout);
    }

    // The "concealed passage opens in the ceiling" broadcast means the sphinx
    // accepted the word — safe to ascend.
    private void OnCeilingOpened()
    {
        if (!Active || _phase != Phase.AwaitingSphinx) return;
        StopTimer();
        _ceilingOpenOn = _floor;
        _log?.Log(LogSeverity.Info, LogSource, "ceiling opened → ascending (u)");
        _phase = Phase.Climbing;
        Drive();
    }

    private void OnSphinxTimeout()
    {
        if (!Active || _phase != Phase.AwaitingSphinx) return;
        string word = PyramidScript.Steps(_floor)[_stepIndex].Word ?? "";
        if (++_sphinxRetries > MaxSphinxRetries)
        {
            FailSolve($"sphinx never opened the ceiling for '{word}'");
            return;
        }
        _log?.Log(LogSeverity.Warn, LogSource, $"sphinx '{word}' silent; retry {_sphinxRetries}/{MaxSphinxRetries}");
        SendCommand("ask sphinx " + word);
        ScheduleSettle(SphinxTimeout, OnSphinxTimeout);
    }

    // ----- F3 doors --------------------------------------------------

    // Every F3 exit is a door on a timer that swings it open and shut. One shown
    // open is walked; a plain one that is shut goes to the door manager (which
    // bashes or picks it, resting between swings as HP needs); a 1000-picklock one
    // can only be waited for; the key door goes to the door manager with its key.
    private void DriveDoor(PyramidStep step, Room here)
    {
        if (_doorStep != _stepIndex)
        {
            _doorStep = _stepIndex;
            _doorRefused = false;
            _doorWatchTicks = 0;
            _doorOpenAttempts = 0;
            _doorFailure = null;
        }
        if (_doorOpening)
        {
            // Back here (a hold came and went) with the door manager still at it.
            _phase = Phase.AwaitingDoor;
            return;
        }

        Direction dir = step.Dir;
        RoomState st = _tracker.State;
        // A dark room lists no exits, so what the tracker holds about its doors is
        // left over from somewhere else.
        bool dark = _tracker.IsInDarkRoom;
        bool shownOpen = !dark && st.OpenDoorDirections?.Contains(dir) == true;
        bool shownShut = !dark && st.ClosedDoorDirections?.Contains(dir) == true;
        if (shownOpen || (!shownShut && !_doorRefused))
        {
            SendStepMove(dir, RoomAfter(_stepIndex));
            return;
        }

        bool ours = step.Kind == PyramidStepKind.KeyDoor || step.Bashable;
        if (ours && _openDoor is not null) OpenDoor(step, here);
        else WatchDoor(dir, here, dark);
    }

    private void OpenDoor(PyramidStep step, Room here)
    {
        Direction dir = step.Dir;
        if (!here.Exits.TryGetValue(dir, out RoomExit exit))
        {
            FailSolve($"12/{here.Key.Room} has no {dir.ToLongName()} exit in the game data");
            return;
        }
        if (++_doorOpenAttempts > MaxDoorOpenAttempts)
        {
            FailSolve(step.Kind == PyramidStepKind.KeyDoor
                ? $"the key door {dir.ToLongName()} out of 12/{here.Key.Room} stays locked ({_doorFailure}) — golden lion key: {KeyStatus}"
                : $"couldn't open the door {dir.ToLongName()} out of 12/{here.Key.Room} ({_doorFailure})");
            return;
        }

        _phase = Phase.AwaitingDoor;
        _doorOpening = true;
        int ticket = ++_doorTicket;
        _log?.Log(LogSeverity.Info, LogSource,
            $"F3 step {_stepIndex + 1}: opening the door {dir.ToLongName()} out of 12/{here.Key.Room}"
            + (exit.KeyItemId > 0 ? $" with key {exit.KeyItemId}" : "")
            + (_doorOpenAttempts > 1 ? $" (go {_doorOpenAttempts}/{MaxDoorOpenAttempts})" : ""));
        _openDoor!(dir, exit.StatRequirement, exit.CanBash, exit.KeyItemId, "pyramid",
            result => OnDoorReply(ticket, dir, result));
    }

    private void OnDoorReply(int ticket, Direction dir, DoorOpenResult result)
    {
        if (!Active || !_doorOpening || ticket != _doorTicket) return;
        _doorOpening = false;
        // NotHere (the door wasn't in the room we stood in) retries like a failure:
        // the next drive reads the room again before it asks for any door.
        string? failure = result switch
        {
            DoorOpenResult.Failed failed => failed.Reason,
            DoorOpenResult.NotHere notHere => notHere.Reason,
            _ => null,
        };
        if (failure is not null)
        {
            _doorFailure = failure;
            _log?.Log(LogSeverity.Warn, LogSource, $"door {dir.ToLongName()} didn't open: {failure}");
            _phase = Phase.Climbing;
            ScheduleSettle(DoorRetryDelay, Drive);
            return;
        }
        // The manager saw it open; the tracker hears it from us, as it does from the
        // walker. The move is still refused if the timer shuts it again first.
        _tracker.NoteNamedDoorOpened(dir);
        _doorRefused = false;
        _phase = Phase.Climbing;
        Drive();
    }

    private void WatchDoor(Direction dir, Room here, bool dark)
    {
        _phase = Phase.AwaitingDoor;
        if (_doorWatchTicks == 0)
            _log?.Log(LogSeverity.Info, LogSource,
                $"F3 step {_stepIndex + 1}: the door {dir.ToLongName()} out of 12/{here.Key.Room} is shut and not ours to open — waiting for its timer");
        if (++_doorWatchTicks > MaxDoorWatchTicks)
        {
            FailSolve($"the door {dir.ToLongName()} out of 12/{here.Key.Room} never opened");
            return;
        }
        if (_doorWatchTicks % DoorLookEvery == 0)
        {
            if (dark)
            {
                // Nothing to see: let the move ask.
                _doorRefused = false;
                Drive();
                return;
            }
            if (!CountSend()) return;
            SendCommand("look");
        }
        ScheduleSettle(DoorWatch, Drive);
    }

    // ----- F3 golden lion key ----------------------------------------

    private bool HaveKey()
        => _selfTookKey || _holdsItem?.Invoke(PyramidScript.GoldenLionKeyItem) == true;

    // Whether the step at hand may go ahead as far as the key is concerned. False
    // when it has scheduled a wait instead. The floating key's room is left only
    // once the key is in the leader's pack or the wait for it has run out; a room
    // later, a climb still without one turns back in, which is what respawns a key
    // that failed to drop.
    private bool KeySettled(int room)
    {
        int leave = PyramidScript.LeaveKeyRoomStep;
        if (_stepIndex == leave && room == PyramidScript.FloatingKeyRoom)
        {
            if (HaveKey()) return true;

            if (_keyGrabber is { } grabber)
            {
                // A member's client picked it up. Ask for it, and give the hand-over
                // a few seconds to show in our pack.
                if (!_keyGiveAsked && _leaderName() is { Length: > 0 } leader)
                {
                    _keyGiveAsked = true;
                    _keyWaitTicks = 0;
                    _log?.Log(LogSeverity.Info, LogSource, $"golden lion key went to {grabber} — asking for it");
                    SendCommand($"@party give golden lion key to {leader}");
                }
                if (++_keyWaitTicks > KeyGiveTicks) return true;
            }
            else
            {
                if (_keyWaitTicks == 0)
                    _log?.Log(LogSeverity.Info, LogSource, "no golden lion key yet — waiting in the floating key's room");
                // Nobody's client may be set to pick it up; ask for it ourselves, now
                // and once more for a kill that finishes late.
                if (_keyWaitTicks % (KeyWaitTicks / 2) == 0 && CountSend())
                    SendCommand("get golden lion key");
                if (++_keyWaitTicks > KeyWaitTicks) return true;
            }
            _phase = Phase.AwaitingKey;
            ScheduleSettle(HoldRecheck, Drive);
            return false;
        }

        if (_stepIndex == leave + 1 && !HaveKey() && _keyGrabber is null && _keyRespawns < MaxKeyRespawns)
        {
            _keyRespawns++;
            _keyWaitTicks = 0;
            _log?.Log(LogSeverity.Info, LogSource,
                $"left the floating key's room without a key — back in to respawn it ({_keyRespawns}/{MaxKeyRespawns})");
            _stepIndex = leave - 1;
        }
        return true;
    }

    // ----- feeds -----------------------------------------------------

    // Fed every parsed room display (RoomDisplayParser.RoomParsed). Catches a
    // scatter by room name.
    public void OnRoomObserved(RoomObservation obs)
    {
        if (Active && IsScatterName(obs.Name))
            FailSolve($"scattered to '{obs.Name}' — climb failed");
    }

    private void OnLine(LineExtractor.EmittedLine line)
    {
        if (!Active || line.IsPromptLine) return;
        string t = line.Text;

        if (_phase == Phase.AwaitingSphinx
            && t.Contains("concealed passage opens in the ceiling", StringComparison.OrdinalIgnoreCase))
        {
            OnCeilingOpened();
            return;
        }

        // Golden lion key pickup — ours, or a member's client getting there first.
        if (t.Contains("golden lion key", StringComparison.OrdinalIgnoreCase))
        {
            if (t.Contains("You took golden lion key", StringComparison.OrdinalIgnoreCase))
            {
                _selfTookKey = true;
                if (_phase == Phase.AwaitingKey) _post(Drive);
            }
            else if (Before(t, " picks up golden lion key") is { } grabber)
            {
                _keyGrabber = grabber;
                _keyWaitTicks = 0;
                if (_phase == Phase.AwaitingKey) _post(Drive);
            }
        }

        // A door changed while we watch one. The tracker reads the same line; look
        // again once it has.
        if (_phase == Phase.AwaitingDoor && !_doorOpening
            && t.Contains("door", StringComparison.OrdinalIgnoreCase))
            _post(Drive);

        // Leader (us) held by hold person — set on our own applied line, cleared on
        // our own wear-off (both are lines we see for ourselves).
        if (t.Contains("Your legs are paralyzed", StringComparison.OrdinalIgnoreCase))
        {
            _selfHeld = true;
            _selfHeldTicks = 0;
        }
        else if (t.Contains("You can move again", StringComparison.OrdinalIgnoreCase))
        {
            _selfHeld = false;
            if (_phase == Phase.Held) Drive();
        }

        // Party-member hold person — only tracked (and waited on) where we ride it
        // out: F3/F4. A cure/freedom cast naming the member frees them; a natural
        // wear-off is a private line we never see (the EnterHold cap covers it).
        if (_floor is PyramidFloor.F3 or PyramidFloor.F4)
        {
            if (After(t, "casts hold person on ") is { } held && _isPartyMember(held))
            {
                if (_heldMembers.Add(held))
                {
                    _holdTicks = 0;
                    _log?.Log(LogSeverity.Info, LogSource, $"party member '{held}' held — pausing until freed");
                }
            }
            else if (After(t, "casts freedom on ") is { } f1) ClearHeld(f1);
            else if (After(t, "casts cure paralysis on ") is { } f2) ClearHeld(f2);
        }

        // Scatter can also surface as the leader's move echo landing in the caverns
        // before a full room render.
        if (IsScatterName(t))
            FailSolve($"scattered ('{t.Trim()}') — climb failed");
    }

    private void ClearHeld(string member)
    {
        if (!_heldMembers.Remove(member)) return;
        _log?.Log(LogSeverity.Info, LogSource, $"party member '{member}' freed");
        if (_phase == Phase.Held) Drive();
    }

    // The target after a "…<marker><target>!" spell line (e.g. "casts hold person
    // on Jroc!" → "Jroc"), trailing punctuation stripped. Null when absent.
    private static string? After(string line, string marker)
    {
        int i = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        string rest = line[(i + marker.Length)..].Trim().TrimEnd('!', '.', ' ');
        return rest.Length > 0 ? rest : null;
    }

    // The actor before a "<name><marker>…" line (e.g. "Gronx picks up golden lion
    // key" → "Gronx"). Null when absent.
    private static string? Before(string line, string marker)
    {
        int i = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i <= 0) return null;
        string name = line[..i].Trim();
        return name.Length > 0 ? name : null;
    }

    private static bool IsScatterName(string s)
        => s.Contains("Scorched Cavern", StringComparison.OrdinalIgnoreCase)
        || s.Contains("Scorching Desert", StringComparison.OrdinalIgnoreCase);

    // ----- terminal transitions --------------------------------------

    // Stop or Reset States: drop the climb (its own moves and timers) where it stands.
    public void Cancel(string reason)
    {
        if (!Active) return;
        _log?.Log(LogSeverity.Info, LogSource, $"pyramid climb cancelled: {reason}");
        End(Phase.Idle);
    }

    private void Finish()
    {
        _log?.Log(LogSeverity.Info, LogSource, $"pyramid climb complete → {_goal.Map}/{_goal.Room}");
        RoomKey dest = _goal;
        End(Phase.Done);
        _walker.ReportPyramidSolveSucceeded(dest);
    }

    private void FailSolve(string reason)
    {
        _log?.Log(LogSeverity.Warn, LogSource, $"pyramid climb failed: {reason}");
        RoomKey dest = _goal;
        End(Phase.Idle);
        _walker.ReportPyramidSolveFailed(dest, reason);
    }

    private void End(Phase phase)
    {
        StopTimer();
        _heldMembers.Clear();
        _detour.Clear();
        bool doorInHand = _doorOpening;
        _doorOpening = false;
        _doorTicket++;
        // After the ticket moves on, so the manager's "stopped" answer is dropped.
        if (doorInHand) _stopDoor?.Invoke();
        _moveInFlight = false;
        _holdReason = null;
        _phase = phase;
        Active = false;
        UpdateRunThrough();
        StateChanged?.Invoke();
    }

    // ----- wire ------------------------------------------------------

    private void SendCommand(string cmd) => Send(Encoding.Latin1.GetBytes(cmd + "\r"));
    private void Send(byte[] bytes) => _wireSender?.Invoke(bytes);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _tracker.StateChanged -= OnTrackerStateChanged;
        _tracker.MoveBlocked -= OnTrackerMoveBlocked;
        _tracker.PlayerDeathObserved -= OnPlayerDied;
        if (_coordinator is not null) _coordinator.GatesChanged -= OnGatesChanged;
        _settleTimer?.Dispose();
        _settleTimer = null;
    }
}
