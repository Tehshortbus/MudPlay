using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Map;

// Drives a "winch" MultiActionHidden crossing: send the winch's pull command,
// recognise its result line, and only report Turned (safe to move) once the gate
// it controls actually reads open. Mirrors DoorOpenManager's shape — one request
// in flight, FIFO queue, a single terminal WinchResult via callback.
//
// Winch mechanic (CONFIRMED, Paradigm — see GAME_MECHANICS "Winch gates"):
//   - `pull winch` yields ONE of two lines:
//       success: "You heave mightily on the winch, and it begins to turn!"
//       failure: "You heave mightily on the winch, but it does not budge."
//   - It can "not budge" several times before it turns — so failure is a retry,
//     not a give-up (paced by PullRetryDelay so a per-pull wind-up isn't spammed).
//   - After it turns the gate opens on a short DELAY, and there is NO "the gate
//     opens" line — the gate only reads "open gate <dir>" in a room re-display. So
//     on success we poll a bare `l` (look) until the gate direction shows open
//     (RoomDisplayParser routes "open gate <dir>" into OpenDoorDirections, which the
//     _isGateOpen probe reads), THEN report Turned. Moving before then just bonks
//     "The gate is closed!" — which MovementRefusalDetector already reverts, but
//     that would thrash pull↔move, so we wait for the gate instead.
//   - With a monster in the room the pull is refused outright ("You cannot do
//     that while there are enemies present!"). That is neither result line, so the
//     request is handed back as RoomNotEmpty for the engine to hold.
//   - A drawbridge-type winch breaks that poll: its exit is phrased "lowered
//     drawbridge <dir>" in the exits list (report paradigm-20260906-202008), which
//     never matches the door/gate open|closed wording _isGateOpen looks for, so the
//     poll would exhaust its cap and fail even though the bridge is down. It does
//     broadcast its own explicit line though ("The wooden drawbridge lowers with a
//     heavy thud!") — we treat that as authoritative and skip the poll entirely.
//
// The move itself is sent by the engine (walker / loop) in its OnWinchReply, exactly
// as it sends the cardinal after a door opens.
public sealed class WinchManager : IDisposable
{
    public const string LogCategory = "Winch";

    private readonly MessageRouter _router;
    // True when the gate for `dir` currently reads open (OpenDoorDirections). Read
    // off the live RoomTracker state after each poll `l` re-displays the room.
    private readonly Func<Direction, bool> _isGateOpen;
    // Schedules a one-shot after a delay; returns a handle to cancel it. AppServices
    // wires the UI-thread timer. Null collapses the paced retry / poll to
    // synchronous immediate action.
    private readonly Func<TimeSpan, Action, IDisposable>? _scheduleDelay;
    private readonly LogService? _log;
    private readonly IDisposable _turnedSub;
    private readonly IDisposable _budgeSub;
    private readonly IDisposable _drawbridgeSub;
    private readonly IDisposable _enemiesSub;
    private readonly WireSender _wire = new();
    private bool _disposed;

    private readonly Queue<WinchRequest> _queue = new();
    private WinchRequest? _current;
    private WinchState _state = WinchState.Idle;
    private int _pullAttempts;
    private int _gatePolls;
    // Set when the drawbridge's own open-confirmation line arrives for the current
    // pull attempt — lets OnWinchTurned skip the gate poll entirely for it.
    private bool _drawbridgeLowered;
    private IDisposable? _timer;
    // True while the movement engines hold for a rest or a meditate. Unset → never.
    private Func<bool>? _restHold;

    // A winch may need several pulls before it winds up; cap so a genuinely stuck
    // winch fails instead of pulling forever.
    private const int PullAttemptCap = 10;
    // Poll the gate this many times after it turns before giving up.
    private const int GateOpenPollCap = 8;
    private static readonly TimeSpan PullRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GateOpenPollInterval = TimeSpan.FromSeconds(2);
    // No result line for a pull within this window → treat as a miss and re-pull.
    private static readonly TimeSpan PullResponseTimeout = TimeSpan.FromSeconds(8);

    public WinchState CurrentState => _state;
    public string? CurrentDirection => _current is { } c ? DirectionShort(c.Direction) : null;
    public int QueueDepth => _queue.Count;

    public WinchManager(
        MessageRouter router,
        Func<Direction, bool> isGateOpen,
        Func<TimeSpan, Action, IDisposable>? scheduleDelay = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(isGateOpen);
        _router = router;
        _isGateOpen = isGateOpen;
        _scheduleDelay = scheduleDelay;
        _log = log;

        _turnedSub = _router.Subscribe(KnownPatterns.WinchTurned, OnWinchTurned);
        _budgeSub = _router.Subscribe(KnownPatterns.WinchWontBudge, OnWinchWontBudge);
        _drawbridgeSub = _router.Subscribe(KnownPatterns.WinchDrawbridgeLowered, OnDrawbridgeLowered);
        _enemiesSub = _router.Subscribe(KnownPatterns.WinchEnemiesPresent, OnEnemiesPresent);
    }

    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // Pulls wait out a rest hold. Whether a pull stands a resting character up on
    // Paradigm isn't known (on Stock a room command doesn't; GAME_MECHANICS "What
    // ends a rest"), and nothing can cross the gate while the walk is held anyway.
    public void SetRestHold(Func<bool> isHeld) => _restHold = isHeld;

    // The rest hold changed: a pull that was waiting on it goes out once it's over.
    public void NotifyRestHoldChanged()
    {
        if (_disposed || _state != WinchState.HeldForRest || _current is null) return;
        if (_restHold?.Invoke() == true) return;
        _log?.Info(LogCategory, "rest over — pulling the winch.");
        SendPull();
    }

    // Whether an exit's prerequisite is a winch this FSM handles — a same-room
    // MultiActionHidden whose (only) action command is a `pull <something> winch`.
    // Cross-room remote-action exits are pre-linearised elsewhere and never a winch.
    public static bool IsWinchExit(RoomExit exit)
    {
        if (exit.Hint != RoomExitHint.MultiActionHidden || exit.MultiAction is not { } ma) return false;
        if (ma.HasRemoteActions) return false;
        foreach (ExitAction a in ma.Actions)
            if (a.Commands.Count > 0 && LooksLikeWinchPull(a.Commands[0])) return true;
        return false;
    }

    // The winch pull command an exit carries, or null when it isn't a winch.
    public static string? PullCommand(RoomExit exit)
    {
        if (exit.MultiAction is not { } ma) return null;
        foreach (ExitAction a in ma.Actions)
            if (a.Commands.Count > 0 && LooksLikeWinchPull(a.Commands[0])) return a.Commands[0];
        return null;
    }

    // Whether a prerequisite command operates a winch (pull/turn/move/push winch) —
    // used both to detect a same-room winch exit and to flag a cross-room detour's
    // pull step for result-aware retry.
    public static bool IsWinchPullCommand(string command) =>
        command.Contains("winch", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeWinchPull(string command) => IsWinchPullCommand(command);

    // waitForGate = true (same-room winch): after it turns, poll a look until the
    // gate direction reads open, then report Turned. waitForGate = false (cross-room
    // detour, direction irrelevant): report Turned the instant it begins to turn —
    // the caller walks to the gate room afterward, which covers the open delay.
    public void Enqueue(Direction direction, string pullCommand, bool waitForGate,
        string sender, Action<WinchResult> reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if ((_current is { } cur && cur.Direction == direction)
            || _queue.Any(q => q.Direction == direction))
        {
            _log?.Info(LogCategory,
                $"winch {DirectionShort(direction)} already in progress — ignoring duplicate (sender={sender}).");
            return;
        }
        _queue.Enqueue(new WinchRequest(direction, pullCommand, waitForGate, sender, reply));
        _log?.Info(LogCategory, $"winch {DirectionShort(direction)} queued (sender={sender}, waitForGate={waitForGate}, depth={_queue.Count}).");
        TryStartNext();
    }

    public void StopAll()
    {
        CancelTimer();
        if (_current is { } cur)
        {
            cur.Reply(new WinchResult.Failed("winch flow stopped"));
            _current = null;
        }
        while (_queue.Count > 0)
            _queue.Dequeue().Reply(new WinchResult.Failed("winch flow stopped"));
        _state = WinchState.Idle;
        _pullAttempts = 0;
        _gatePolls = 0;
        _log?.Info(LogCategory, "winch flow stopped — queue drained.");
    }

    private void TryStartNext()
    {
        if (_state != WinchState.Idle || _queue.Count == 0) return;
        _current = _queue.Dequeue();
        _pullAttempts = 0;
        _gatePolls = 0;
        SendPull();
    }

    private void SendPull()
    {
        if (_current is not { } cur) return;
        if (_restHold?.Invoke() == true)
        {
            CancelTimer();
            _state = WinchState.HeldForRest;
            _log?.Info(LogCategory, $"'{cur.PullCommand}' held — resting; pulling once the rest is over.");
            return;
        }
        _pullAttempts++;
        _drawbridgeLowered = false;
        _state = WinchState.WaitingPull;
        _wire.Send(cur.PullCommand);
        _log?.Info(LogCategory, $"'{cur.PullCommand}' (attempt {_pullAttempts}/{PullAttemptCap}).");
        Arm(PullResponseTimeout, OnPullTimeout);
    }

    private void OnWinchWontBudge(MatchResult _)
    {
        if (_state != WinchState.WaitingPull || _current is null) return;
        if (_pullAttempts >= PullAttemptCap)
        {
            FailCurrent($"winch would not budge after {_pullAttempts} pulls");
            return;
        }
        // Pace the re-pull — the winch winds up over a few pulls, so a delay avoids
        // spamming "does not budge" faster than it can turn. Null scheduler
        // re-pulls immediately.
        _log?.Info(LogCategory, "winch would not budge — re-pulling.");
        Schedule(PullRetryDelay, SendPull);
    }

    // The pull was refused for a monster in the room. Pulling again is refused
    // again, so the request goes back to the engine, which holds the step until the
    // room is clear. The line isn't the winch's own, so it only counts while a pull
    // is the thing awaiting an answer.
    private void OnEnemiesPresent(MatchResult _)
    {
        if (_state != WinchState.WaitingPull || _current is not { } cur) return;
        CancelTimer();
        _log?.Info(LogCategory, $"'{cur.PullCommand}' refused — a monster is in the room; handing the step back.");
        cur.Reply(WinchResult.RoomNotEmpty.Instance);
        Reset();
    }

    private void OnWinchTurned(MatchResult _)
    {
        if (_state != WinchState.WaitingPull || _current is null) return;
        // Pull-only (cross-room detour): the gate is in another room, so there's
        // nothing to poll here — report Turned and let the caller walk to it.
        if (!_current.WaitForGate)
        {
            _log?.Info(LogCategory, "winch began to turn (pull-only — gate is in another room).");
            SucceedCurrent();
            return;
        }
        // A drawbridge-type winch already told us it's down — its exit never reads
        // as "open" in the exits list, so the poll below would just exhaust its cap.
        if (_drawbridgeLowered)
        {
            _log?.Info(LogCategory, "winch began to turn — drawbridge already confirmed lowered, skipping gate poll.");
            SucceedCurrent();
            return;
        }
        _log?.Info(LogCategory, "winch began to turn — waiting for the gate to open.");
        _state = WinchState.WaitingGateOpen;
        _gatePolls = 0;
        PollGate();
    }

    // The drawbridge's own broadcast can arrive before or after the "begins to
    // turn" line, so handle both orderings: stash it while still WaitingPull (read
    // by OnWinchTurned above), or succeed immediately if we're already polling.
    private void OnDrawbridgeLowered(MatchResult _)
    {
        if (_current is null) return;
        if (_state == WinchState.WaitingPull)
        {
            _drawbridgeLowered = true;
            return;
        }
        if (_state == WinchState.WaitingGateOpen && _current.WaitForGate)
        {
            _log?.Info(LogCategory, "drawbridge lowered — gate confirmed open, skipping remaining polls.");
            SucceedCurrent();
        }
    }

    // The gate opens a beat after the winch turns and only shows in a room
    // re-display, so poll a look and check OpenDoorDirections. Report Turned the
    // moment the gate reads open; give up after the cap so a winch whose gate
    // never opens fails instead of polling forever.
    private void PollGate()
    {
        if (_current is not { } cur) return;
        if (_isGateOpen(cur.Direction))
        {
            _log?.Info(LogCategory, $"gate {DirectionShort(cur.Direction)} is open — ready to move.");
            SucceedCurrent();
            return;
        }
        if (_gatePolls >= GateOpenPollCap)
        {
            FailCurrent($"gate {DirectionShort(cur.Direction)} never opened after the winch turned");
            return;
        }
        _gatePolls++;
        _wire.Send("l");   // force a room re-display so OpenDoorDirections refreshes
        Schedule(GateOpenPollInterval, PollGate);
    }

    private void OnPullTimeout()
    {
        if (_disposed || _current is null || _state != WinchState.WaitingPull) return;
        if (_pullAttempts >= PullAttemptCap)
        {
            FailCurrent($"winch drew no response after {_pullAttempts} pulls");
            return;
        }
        _log?.Info(LogCategory, $"no winch response in {PullResponseTimeout.TotalSeconds:0}s — re-pulling.");
        SendPull();
    }

    private void SucceedCurrent()
    {
        if (_current is not { } cur) return;
        CancelTimer();
        cur.Reply(WinchResult.Turned.Instance);
        Reset();
    }

    private void FailCurrent(string reason)
    {
        if (_current is not { } cur) return;
        CancelTimer();
        _log?.Warn(LogCategory, $"winch {DirectionShort(cur.Direction)} failed: {reason}.");
        cur.Reply(new WinchResult.Failed(reason));
        Reset();
    }

    private void Reset()
    {
        CancelTimer();
        _current = null;
        _state = WinchState.Idle;
        _pullAttempts = 0;
        _gatePolls = 0;
        _drawbridgeLowered = false;
        TryStartNext();
    }

    // Schedule the paced follow-up, or run it now when no scheduler is wired.
    private void Schedule(TimeSpan delay, Action action)
    {
        CancelTimer();
        if (_scheduleDelay is { } sched) _timer = sched(delay, action);
        else action();
    }

    // Arm the response watchdog (no-op without a scheduler).
    private void Arm(TimeSpan delay, Action action)
    {
        CancelTimer();
        _timer = _scheduleDelay?.Invoke(delay, action);
    }

    private void CancelTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelTimer();
        _turnedSub.Dispose();
        _budgeSub.Dispose();
        _drawbridgeSub.Dispose();
        _enemiesSub.Dispose();
    }

    internal static string DirectionShort(Direction d) => d switch
    {
        Direction.N => "n", Direction.S => "s", Direction.E => "e", Direction.W => "w",
        Direction.NE => "ne", Direction.NW => "nw", Direction.SE => "se", Direction.SW => "sw",
        Direction.U => "u", Direction.D => "d", _ => "?",
    };

    public enum WinchState
    {
        Idle,
        // Sent the pull command; awaiting "begins to turn" / "does not budge".
        WaitingPull,
        // Winch turned; polling the gate direction until it reads open.
        WaitingGateOpen,
        // The next pull is waiting for a rest hold to end.
        HeldForRest,
    }

    private sealed record WinchRequest(
        Direction Direction, string PullCommand, bool WaitForGate, string Sender, Action<WinchResult> Reply);
}
