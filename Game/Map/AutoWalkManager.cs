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
    private Func<RoomKey, RoomKey, string?>? _teleportResolver;
    private Func<bool>? _isLeaderWithFollowers;
    private Action? _preMoveHook;
    private Action<IReadOnlyList<int>>? _pathItemAnnouncer;
    private readonly LogService? _log;

    private List<WalkStep>? _path;
    private int _index;                                      // index of the *next* step to send
    private RoomKey? _expectedAfterCurrentMove;
    private RoomKey? _destination;
    private bool _stepInFlight;
    private bool _awaitingPromptForCommand;
    private bool _awaitingTrapDisarm;
    private int _retryCount;
    private const int MaxRetriesPerStep = 1;

    /// <summary>
    /// Counter for mid-walk re-plans triggered by tracker entering
    /// Suspect/Lost mid-step (typically caused by the user manually
    /// typing a movement at the terminal during a walk). Reset on
    /// every Confirmed step advance; capped to prevent infinite
    /// ping-pong when the user keeps interleaving typed movement.
    /// </summary>
    private int _replanCount;
    private const int MaxReplansPerWalk = 2;

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

    /// <summary>
    /// Remaining walk path as a sequence of room keys — current
    /// room followed by each subsequent <see cref="MoveStep"/>'s
    /// <see cref="MoveStep.ExpectedTarget"/>. The map renderer
    /// (PR 7.x walk-path overlay) draws this as a blue polyline so
    /// the user can see exactly where the walker is heading.
    /// </summary>
    public IReadOnlyList<RoomKey> RemainingRoomKeys
    {
        get
        {
            if (_path is null || State == WalkState.Idle)
                return Array.Empty<RoomKey>();

            var keys = new List<RoomKey>(_path.Count - _index + 1);
            if (_tracker.State.CurrentRoom is { } current) keys.Add(current.Key);
            for (int i = _index; i < _path.Count; i++)
            {
                if (_path[i] is MoveStep move) keys.Add(move.ExpectedTarget);
            }
            return keys;
        }
    }

    public event Action<WalkEvent>? Event;

    // ----- IRecoverableEngine ----------------------------------------

    public string Name => "Walker";

    public Direction? PeekNextPlannedDirection()
    {
        if (_path is null || _index >= _path.Count) return null;
        return _path[_index] is MoveStep move ? move.Direction : (Direction?)null;
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
    /// Bind the trap-disarm enqueuer (PR 7.22). Production wires this
    /// to <see cref="Game.TrapDisarmManager.Enqueue"/> so trapped exits
    /// route through the search-then-disarm flow before the move goes
    /// out. Tests pass a capture-and-fire delegate.
    /// </summary>
    /// <remarks>
    /// Signature: <c>(direction, sender, reply)</c>. The walker passes
    /// the lowercase direction word, the literal string
    /// <c>"walker"</c>, and a reply callback that resumes the walk on
    /// success or aborts it on failure.
    /// </remarks>
    public void SetTrapEnqueuer(Action<string, string, Action<string>> enqueuer)
    {
        ArgumentNullException.ThrowIfNull(enqueuer);
        _trapEnqueuer = enqueuer;
    }

    /// <summary>
    /// Gate for trapped-exit handling. Returns <c>true</c> when the walker
    /// should route a <see cref="RoomExitHint.Trap"/> exit through the trap
    /// enqueuer — i.e. Settings → Other "Utilize disarm traps if able" is on
    /// AND the local character has the Traps skill. Returns <c>false</c> to
    /// walk straight through the trap without attempting a disarm. When left
    /// unset the walker defaults to attempting the disarm (preserves the
    /// pre-toggle behavior for any consumer that never wires a gate).
    /// </summary>
    public void SetTrapDisarmGate(Func<bool> gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _shouldDisarmTrap = gate;
    }

    /// <summary>
    /// Party-delegation enqueuer — the walker calls this when the local
    /// character can't disarm a trap but a capable party member can. It
    /// broadcasts <c>@trap &lt;dir&gt;</c> on say and resumes the walk on
    /// the member's say reply via the same <see cref="OnTrapReply"/>
    /// callback the local path uses. Bound to
    /// <see cref="Game.TrapDelegationManager.Delegate"/>. The two paths
    /// share the resume callback but keep their signal SOURCES distinct —
    /// local keys on the game's first-person disarm signals, delegation on
    /// the member's say reply.
    /// </summary>
    public void SetTrapDelegator(Action<string, Action<string>> delegator)
    {
        ArgumentNullException.ThrowIfNull(delegator);
        _trapDelegator = delegator;
    }

    /// <summary>
    /// Gate for the party-delegation branch. Returns <c>true</c> when the
    /// "Utilize disarm traps if able" toggle is on, the LOCAL character
    /// can't disarm, AND at least one party member can — i.e. the "if
    /// able" clause is satisfied by party ability rather than our own.
    /// </summary>
    public void SetTrapDelegateGate(Func<bool> gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _canDelegateTrap = gate;
    }

    /// <summary>
    /// Delegation teardown — bound to
    /// <see cref="Game.TrapDelegationManager.Cancel"/>. Called from
    /// <see cref="Reset"/> when a walk is superseded mid-delegation so a
    /// later stray say reply can't resume a dead walk.
    /// </summary>
    public void SetTrapDelegateStopper(Action stopAll)
    {
        ArgumentNullException.ThrowIfNull(stopAll);
        _trapDelegateStopAll = stopAll;
    }

    /// <summary>
    /// Door-open enqueuer — the walker calls this when stepping toward
    /// a <see cref="RoomExitHint.Door"/> exit, passes the direction +
    /// the door's stat requirement + bashable flag, and resumes the
    /// move on the callback's terminal <see cref="DoorOpenResult"/>.
    /// MainWindowVM binds this to <see cref="DoorOpenManager.Enqueue"/>.
    /// </summary>
    public void SetDoorEnqueuer(Action<Direction, int, bool, int, string, Action<DoorOpenResult>> enqueuer)
    {
        ArgumentNullException.ThrowIfNull(enqueuer);
        _doorEnqueuer = enqueuer;
    }

    /// <summary>
    /// Door-FSM teardown — bound to <see cref="DoorOpenManager.StopAll"/>.
    /// Called from <see cref="Reset"/> when a walk is superseded while
    /// the walker is mid-door-FSM. Without this, the new walk's
    /// follow-up <c>_doorEnqueuer</c> call sits in the door manager's
    /// queue because <c>TryStartNext</c> bails on non-Idle state and
    /// the walker stalls indefinitely.
    /// </summary>
    public void SetDoorStopper(Action stopAll)
    {
        ArgumentNullException.ThrowIfNull(stopAll);
        _doorStopAll = stopAll;
    }

    /// <summary>
    /// Hidden-exit reveal enqueuer — walker calls this for
    /// <see cref="RoomExitHint.SearchableHidden"/> exits to fire the
    /// <c>sea &lt;dir&gt;</c> retry loop until the exit appears on
    /// the room display. MainWindowVM binds this to
    /// <see cref="HiddenExitRevealManager.Enqueue"/>.
    /// </summary>
    public void SetHiddenSearchEnqueuer(Action<Direction, string, Action<HiddenSearchResult>> enqueuer)
    {
        ArgumentNullException.ThrowIfNull(enqueuer);
        _hiddenSearchEnqueuer = enqueuer;
    }

    /// <summary>
    /// Hidden-search teardown — bound to <see cref="HiddenExitRevealManager.StopAll"/>.
    /// Same stale-state cleanup rationale as <see cref="SetDoorStopper"/>.
    /// </summary>
    public void SetHiddenSearchStopper(Action stopAll)
    {
        ArgumentNullException.ThrowIfNull(stopAll);
        _hiddenSearchStopAll = stopAll;
    }

    /// <summary>
    /// Teleport-keyword resolver — given (source room, destination
    /// room) the walker calls this to look up the verbatim command
    /// it should send (from the source room's CMD chain in
    /// <see cref="Services.TBInfoStore"/>). Bound by MainWindowVM.
    /// </summary>
    public void SetTeleportResolver(Func<RoomKey, RoomKey, string?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _teleportResolver = resolver;
    }

    /// <summary>
    /// Predicate the walker uses to decide whether to prefix a
    /// teleport with <c>.@party &lt;cmd&gt;</c> so followers come
    /// along. Returns <c>true</c> when the local character is party
    /// leader AND there's at least one follower.
    /// </summary>
    public void SetPartyLeaderCheck(Func<bool> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        _isLeaderWithFollowers = check;
    }

    /// <summary>
    /// Pre-move stealth hook (PR 4.b) — invoked by the walker
    /// immediately before each move's bytes go out, AFTER any
    /// door / trap / hidden / multi-action pre-steps, so <c>sn</c> is
    /// the last command before the move and the move itself is sneaked.
    /// MainWindowVM / AppServices binds this to
    /// <see cref="Game.Stealth.StealthManager.RequestPreMoveStealth"/>.
    /// Non-blocking: the hook fires and the move bytes follow without
    /// waiting for the sneak ACK (sneak carries through the move).
    /// </summary>
    public void SetPreMoveHook(Action hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        _preMoveHook = hook;
    }

    /// <summary>
    /// Planned-route item-requirement announcer (PR B). Invoked once at
    /// walk-start with every item id gating an <c>(Item: N)</c> /
    /// <c>(Ticket: N)</c> exit along the freshly-planned path — the items
    /// the character must be carrying to complete the route. Bound to
    /// <see cref="PathItemDemandTracker.OnPathItemsRequired"/>, which posts a
    /// need for each one we lack so auto-search arms until it's found. Only
    /// exits with a possession gate are reported; door / key / trap / hidden
    /// exits have their own FSMs and aren't item-possession problems.
    /// </summary>
    public void SetPathItemAnnouncer(Action<IReadOnlyList<int>> announcer)
    {
        ArgumentNullException.ThrowIfNull(announcer);
        _pathItemAnnouncer = announcer;
    }

    /// <summary>
    /// Test seam — pretend the wire prompt scanner just fired, so the
    /// pending command step can advance without a real telnet client.
    /// No-op when no command step is in flight.
    /// </summary>
    internal void FirePromptForTests()
    {
        if (_awaitingPromptForCommand) OnPromptObservedCore();
    }

    /// <summary>
    /// When non-null, the user requested a walk while the tracker still
    /// had pipelined moves outstanding (Confidence == Pending). Planning
    /// is deferred until the tracker reaches Confirmed; the next
    /// confirmation in <see cref="OnTrackerStateChanged"/> picks this
    /// up and runs <see cref="WalkToImmediate"/> against the actually-
    /// settled current room. Cleared by <see cref="Reset"/> so a Stop
    /// or supersede invalidates the deferral.
    /// </summary>
    private RoomKey? _deferredWalkTarget;

    public bool WalkTo(RoomKey destination)
    {
        if (State is WalkState.Walking or WalkState.Paused)
            Stop(reason: "superseded by new walk");

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
            _destination = destination;       // populated so status surfaces show the target
            State = WalkState.Walking;
            Raise(new WalkEvent(WalkEventKind.Started,
                "deferred — waiting for in-flight moves to settle",
                destination));
            return true;
        }

        return WalkToImmediate(destination);
    }

    private bool WalkToImmediate(RoomKey destination)
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
            // Distinguish "all routes blocked by a level requirement"
            // from a genuinely disconnected target: re-probe with the
            // exit-level gates ignored. A path that appears only when
            // gates are off means every route there is level-gated
            // beyond the player — surface that reason so the user
            // understands why we won't move.
            IReadOnlyList<Direction>? ungated =
                _bfs.FindPath(source.Key, destination, _filter, ignoreExitGates: true);
            string reason = ungated is { Count: > 0 }
                ? "all routes blocked by a level requirement"
                : "no path";
            Raise(new WalkEvent(WalkEventKind.Failed, reason, destination));
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
        _recovery?.Attach(this);

        int moveCount = expanded.Count(s => s is MoveStep);
        int actionCount = expanded.Count - moveCount;
        string detail = actionCount > 0
            ? $"{moveCount} move(s), {actionCount} action(s)"
            : $"{moveCount} step(s)";
        Raise(new WalkEvent(WalkEventKind.Started, detail, destination));

        // Announce the items this route demands so the demand-driven
        // auto-search can arm for anything we're not carrying. Best-effort:
        // walks the graph along the planned directions from the source room.
        AnnouncePlannedItemRequirements(source.Key, path);

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
    // every possession-gated exit (Item / Ticket) crossed. The result is the
    // set of items the route requires the character to carry; the demand
    // tracker decides which are missing. Cheap (one dictionary lookup per hop)
    // and side-effect-free — skipped entirely when no announcer is bound.
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
            cur = exit.Target;
        }

        if (required is not null) _pathItemAnnouncer(required);
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

        // Trapped exits — route through TrapDisarmManager (PR 7.22)
        // before the move bytes go out. The walker waits for the trap
        // reply; the actual move bytes are sent from OnTrapReply.
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
        // path (KeyItemId > 0) tries bash/pick first to save key
        // charges (per MudProxy 1280-1322) and falls back to the
        // single-shot `use <keyName> <dir>` + `open <dir>` sequence
        // when no stat-alt is viable or both verbs exhaust.
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

        // Synchronous special exits — MultiActionHidden (same-room),
        // Text `(Text: ...)`, and Teleport `(Item: N)` — share one
        // emission path with the loop runner via SpecialExitDispatch so
        // both engines cross them identically. The async door/hidden
        // hints are NOT covered here; they fall through to their own
        // FSMs below.
        SpecialExitSend sync = SpecialExitDispatch.TrySendSynchronous(
            exit, step.Direction, _tracker.State.CurrentRoom,
            _tracker, _recovery,
            emitMove: EmitMoveBytes,
            writeAux: WriteBytes,
            _teleportResolver, _isLeaderWithFollowers,
            out string? syncFail);
        if (sync == SpecialExitSend.Sent) return;
        if (sync == SpecialExitSend.Failed)
        {
            Raise(new WalkEvent(WalkEventKind.Failed, syncFail!, _destination));
            Reset();
            return;
        }

        // SearchableHidden — `(Hidden)` modifier. Send `sea <dir>`
        // until the exit appears in the room tracker's CurrentRoom,
        // then send the cardinal move. Capped by
        // Settings.Other.MaxHiddenSearchAttempts.
        if (exit.Hint == RoomExitHint.SearchableHidden && _hiddenSearchEnqueuer is not null)
        {
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
        _awaitingPromptForCommand = true;

        byte[] bytes = Encoding.Latin1.GetBytes(step.Command + "\r");
        WriteBytes(bytes, $"command '{step.Command}'");
    }

    /// <summary>
    /// Emit a move (cardinal direction, text-exit command, or teleport
    /// keyword) — fires the pre-move stealth hook (so <c>sn</c> is the
    /// last command before the move) then writes the move bytes. Every
    /// move-byte send routes through here so the choke point stays
    /// single; non-move sends (multi-action prerequisites, the teleport
    /// <c>.@party</c> relay) call <see cref="WriteBytes"/> directly.
    /// </summary>
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
        _log?.Info("Walker", $"step {_index + 1}/{_path!.Count}: {reasonForLog}");
    }

    private void OnTrackerStateChanged(RoomTransition transition)
    {
        // Deferred-plan dispatch — a WalkTo arrived while the tracker
        // still had pipelined moves outstanding. The walker has been
        // sitting in Walking state with _path == null waiting for a
        // Confirmed observation. Now we have one — plan + send from
        // the actually-settled current room.
        if (_deferredWalkTarget is { } deferred
            && transition.NewConfidence == RoomConfidence.Confirmed
            && State == WalkState.Walking
            && _path is null)
        {
            _deferredWalkTarget = null;
            WalkToImmediate(deferred);
            return;
        }

        if (State != WalkState.Walking) return;
        if (!_stepInFlight) return;
        if (_path is null || _index >= _path.Count) return;
        if (_path[_index] is not MoveStep) return;

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
            Raise(new WalkEvent(WalkEventKind.Failed,
                $"step {_index + 1} blocked after retries", _destination));
            Reset();
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
        // Re-plan caps avoid infinite ping-pong when manual user
        // typing keeps interfering with the walker's expectations.
        if (_replanCount >= MaxReplansPerWalk
            || _destination is not { } dest
            || _tracker.State.CurrentRoom is not { } here)
        {
            Raise(new WalkEvent(WalkEventKind.Failed,
                $"tracker entered {newConfidence} mid-step; walker can't continue",
                _destination));
            Reset();
            return;
        }

        _replanCount++;
        _stepInFlight = false;
        Raise(new WalkEvent(WalkEventKind.Retrying,
            $"tracker entered {newConfidence} mid-step; re-planning from {here.Key} (attempt {_replanCount}/{MaxReplansPerWalk})",
            _destination));
        // Re-source the path from the tracker's best-guess current
        // room. WalkTo handles the existing Walking state by stopping
        // and re-planning.
        WalkTo(dest);
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
            if (!TryReconcileIndexAfterResume())
            {
                TryReplanOrFail(RoomConfidence.Suspect);
                return;
            }

            // Reconciliation may have completed the walk; only fire
            // the next step if we're still walking.
            if (State == WalkState.Walking) SendNextStep();
        }
    }

    /// <summary>
    /// Reconcile <c>_index</c> with the tracker's current room after a
    /// pause. Returns true when the walker can resume safely from its
    /// new index (whether or not the index moved). Returns false when
    /// the player ended up at a room that isn't on the remaining path
    /// AND can't legally take the next planned step — the caller should
    /// re-plan rather than blindly re-sending a stale step direction.
    /// </summary>
    private bool TryReconcileIndexAfterResume()
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

        // No forward match. If the next planned step's direction
        // doesn't even exist as an exit from the player's current room,
        // they're off the path — re-plan. The "exit exists" check is a
        // cheap proxy for "the planned route still works from here";
        // imperfect cases (exit exists but leads somewhere unrelated)
        // fall through to the normal mid-step desync handling in
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
        // Drop a pending party-delegation watch so a stray say reply can't
        // resume a superseded walk. Harmless when the trap was local-only.
        if (_awaitingTrapDisarm)    _trapDelegateStopAll?.Invoke();

        _path = null;
        _index = 0;
        _expectedAfterCurrentMove = null;
        _destination = null;
        _stepInFlight = false;
        _awaitingPromptForCommand = false;
        _awaitingTrapDisarm = false;
        _awaitingDoorOpen = false;
        _awaitingHiddenReveal = false;
        _deferredWalkTarget = null;
        _retryCount = 0;
        _replanCount = 0;
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
    DisarmingTrap = 8,
}

public readonly record struct WalkEvent(WalkEventKind Kind, string Detail, RoomKey? Destination);
