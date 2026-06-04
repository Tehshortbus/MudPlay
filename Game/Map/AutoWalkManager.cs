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
    private Action<string, string, Action<string>>? _trapEnqueuer;
    private Action<Direction, int, bool, int, string, Action<DoorOpenResult>>? _doorEnqueuer;
    private bool _awaitingDoorOpen;
    private Action<Direction, string, Action<HiddenSearchResult>>? _hiddenSearchEnqueuer;
    private bool _awaitingHiddenReveal;
    private Func<RoomKey, RoomKey, string?>? _teleportResolver;
    private Func<bool>? _isLeaderWithFollowers;
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

        // Trapped exits — route through TrapDisarmManager (PR 7.22)
        // before the move bytes go out. The walker waits for the trap
        // reply; the actual move bytes are sent from OnTrapReply.
        if (exit.Hint == RoomExitHint.Trap && _trapEnqueuer is not null)
        {
            _awaitingTrapDisarm = true;
            string dirWord = DirectionWord(step.Direction);
            Raise(new WalkEvent(WalkEventKind.DisarmingTrap,
                $"trap on {dirWord}", _destination));
            _log?.Info("Walker", $"step {_index + 1}/{_path!.Count}: disarm trap {dirWord}");
            _trapEnqueuer(dirWord, "walker", OnTrapReply);
            return;
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
                byte[] preBytes = EncodeMove(step.Direction);
                WriteBytes(preBytes, $"move {step.Direction} (door pre-open)");
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

        // MultiActionHidden — `(Hidden, Needs N Actions, ...)`. Execute
        // the prerequisite commands in StepNumber order, then send the
        // cardinal move. Same-room actions only for v1; cross-row
        // remote-action data fails the walk with a clear reason
        // (cross-room expander is a follow-up — the data parser
        // already preserves RemoteSourceRoom so the future expander
        // can route through it).
        if (exit.Hint == RoomExitHint.MultiActionHidden && exit.MultiAction is { } maData)
        {
            if (maData.HasRemoteActions)
            {
                Raise(new WalkEvent(WalkEventKind.Failed,
                    "multi-action exit requires actions in a different room — cross-room expander not yet wired",
                    _destination));
                Reset();
                return;
            }
            if (maData.Actions.Count < maData.RequiredActionCount)
            {
                Raise(new WalkEvent(WalkEventKind.Failed,
                    $"multi-action exit needs {maData.RequiredActionCount} action(s) but data has {maData.Actions.Count}",
                    _destination));
                Reset();
                return;
            }

            // Fire each action's first alternative in order, then
            // send the cardinal move. Each command goes out
            // immediately; no per-command response wait. The
            // server's verb-by-verb echo provides the round-robin.
            foreach (ExitAction action in maData.Actions)
            {
                if (action.Commands.Count == 0) continue;
                string cmd = action.Commands[0];
                byte[] cmdBytes = Encoding.Latin1.GetBytes(cmd + "\r");
                WriteBytes(cmdBytes, $"multi-action #{action.StepNumber}: '{cmd}'");
            }
            _tracker.NoteMoveSent(step.Direction);
            byte[] moveBytes = EncodeMove(step.Direction);
            WriteBytes(moveBytes, $"move {step.Direction} (post-multi-action)");
            return;
        }

        // Text exits — `(Text: cmd1, cmd2, ...)` modifier. Any one of
        // the alternatives moves the player (no follow-up cardinal).
        // We send the first; future PRs may choose smarter (e.g.
        // shortest, or last-known-good).
        if (exit.Hint == RoomExitHint.Text && exit.TextCommands is { Count: > 0 } cmds)
        {
            string textCmd = cmds[0];
            _tracker.NoteMoveSent(textCmd, cardinal: step.Direction);
            byte[] textBytes = Encoding.Latin1.GetBytes(textCmd + "\r");
            WriteBytes(textBytes, $"text-exit '{textCmd}' → {exit.Target}");
            return;
        }

        // Teleport exits — `(Item: N)` modifier on a room whose CMD
        // is non-zero. The CMD indexes a TBInfo Action chain whose
        // matching `teleport <room> <map>` directive identifies the
        // keyword(s) the player types. Party-breaking — leader
        // broadcasts via `.@party <keyword>` so followers come along
        // before the leader teleports.
        if (exit.Hint == RoomExitHint.Teleport)
        {
            Room? source = _tracker.State.CurrentRoom;
            string? keyword = (source is not null && _teleportResolver is not null)
                ? _teleportResolver(source.Key, exit.Target)
                : null;
            if (keyword is null)
            {
                Raise(new WalkEvent(WalkEventKind.Failed,
                    "no teleport keyword resolved (TBInfo entry missing or not for this destination)",
                    _destination));
                Reset();
                return;
            }

            if (_isLeaderWithFollowers?.Invoke() == true)
            {
                byte[] partyBytes = Encoding.Latin1.GetBytes($".@party {keyword}\r");
                WriteBytes(partyBytes, $"teleport party-relay '.@party {keyword}'");
            }

            _tracker.NoteMoveSent(keyword, cardinal: step.Direction);
            byte[] tpBytes = Encoding.Latin1.GetBytes(keyword + "\r");
            WriteBytes(tpBytes, $"teleport '{keyword}' → {exit.Target}");
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

        byte[] bytes = EncodeMove(step.Direction);
        WriteBytes(bytes, $"move {step.Direction} → {exit.Target}");
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
                byte[] bytes = EncodeMove(step.Direction);
                WriteBytes(bytes, $"move {step.Direction} (post-hidden-reveal)");
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
                byte[] bytes = EncodeMove(step.Direction);
                WriteBytes(bytes, $"move {step.Direction} (post-door)");
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
        byte[] bytes = EncodeMove(step.Direction);
        WriteBytes(bytes, $"move {step.Direction} (post-disarm)");
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

        if (transition.NewConfidence != RoomConfidence.Confirmed) return;

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
        _awaitingTrapDisarm = false;
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
    DisarmingTrap = 8,
}

public readonly record struct WalkEvent(WalkEventKind Kind, string Detail, RoomKey? Destination);
