using System.Threading.Tasks;
using Avalonia.Threading;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Ground-truth position recovery through the game's own sysop room dump. When
// the recovery gate is about to start reversing moves to work out where we are
// — or the tracker has given up and gone Lost — one `sys st` prints the room's
// true map/room number, and SetLocated turns that straight into a confirmed
// position. That replaces the whole backtrack escalation and the "I am here"
// map click at the end of it.
//
// Same shape as ParadigmPositionResolver (ask → authoritative key → SetLocated
// → re-anchor the gate), with two differences: the answer arrives through
// SysStatusProbe's own request/response rather than a router pattern, and the
// capability is a per-BBS user flag rather than a realm.
//
// Every decline returns false or raises LocateFailed, so the caller falls
// through to exactly what it did before — capability off or auto-disabled,
// throttled, suppressed, the probe came back empty, or the room named isn't in
// the active graph. A confident wrong answer is worse than no answer.
public sealed class SysopPositionResolver : IDisposable
{
    private const string LogSource = "SysopLocate";

    // Minimum spacing between SELF-triggered locates. An oscillating tracker can
    // flip Lost repeatedly; without this it would become a command loop.
    private static readonly TimeSpan MinLocateInterval = TimeSpan.FromSeconds(15);

    // Recovery callers are NOT throttled. The stall watchdog escalates every 10s,
    // so consecutive stalls land inside any window worth setting — a throttle
    // longer than the gap between the events it serves denies every locate after
    // the first, and the fallback is a backtrack that cannot converge among
    // identically-named rooms, so the sweep dies. It needs no window of its own
    // either: in-flight coalescing below means a second ask can't start until the
    // first resolves or times out, the gate allows one locate per escalation, and
    // escalations are already paced by that same watchdog.

    // How long a locate may sit queued behind unconfirmed movement before we
    // give up on it. Bounded because a caller that got `true` has paused an
    // engine on the promise of an answer.
    private static readonly TimeSpan DeferralWindow = TimeSpan.FromSeconds(8);

    private readonly SysStatusProbe _probe;
    private readonly RoomGraphManager _graph;
    private readonly RoomTracker _tracker;
    private readonly Func<bool>? _suppressed;
    private readonly Action<Action> _post;
    private readonly LogService? _log;
    private readonly DispatcherTimer? _deferral;

    private bool _inFlight;
    private string? _deferredReason;
    private DateTimeOffset _lastRequestAtUtc = DateTimeOffset.MinValue;
    private bool _disposed;

    // Fires when a locate can't produce a usable room. The recovery gate
    // subscribes and falls back to its heuristic ladder.
    public event Action? LocateFailed;

    // Fires with the authoritative key once the tracker has been located there.
    // Raised after SetLocated so a subscriber re-anchoring off it sees a tracker
    // that already agrees.
    public event Action<RoomKey>? PositionResolved;

    // ----- bug-report surface ----------------------------------------
    public bool RequestInFlight => _inFlight;
    public bool LocateDeferred => _deferredReason is not null;
    public string LastOutcome { get; private set; } = "(never asked)";

    public SysopPositionResolver(
        SysStatusProbe probe,
        RoomGraphManager graph,
        RoomTracker tracker,
        Func<bool>? suppressed = null,
        LogService? log = null,
        Action<Action>? post = null)
        : this(probe, graph, tracker, suppressed, log, post, useTimer: true) { }

    internal SysopPositionResolver(
        SysStatusProbe probe,
        RoomGraphManager graph,
        RoomTracker tracker,
        Func<bool>? suppressed,
        LogService? log,
        Action<Action>? post,
        bool useTimer)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(tracker);

        _probe = probe;
        _graph = graph;
        _tracker = tracker;
        _suppressed = suppressed;
        _log = log;
        _post = post ?? (a => a());
        _tracker.StateChanged += OnTrackerStateChanged;

        if (useTimer)
        {
            // One-shot, armed only while a locate is queued behind movement.
            // DispatcherTimer fires on the UI thread, so the tracker mutations
            // downstream stay single-threaded like every other engine send.
            _deferral = new DispatcherTimer(DispatcherPriority.Background) { Interval = DeferralWindow };
            _deferral.Tick += (_, _) => OnDeferralExpired();
        }
    }

    // Ask the game where we actually are. Returns true when a probe is in
    // flight (or queued behind movement) — the caller should pause and wait for
    // exactly one of PositionResolved / LocateFailed. Returns false when no
    // probe can be started, so the caller keeps its existing path.
    // forRecovery: this ask is a recovery escalation, not the resolver noticing
    // the tracker went Lost on its own. Two guards written for the self-triggered
    // case are wrong for it, and both fire exactly when the locate matters most:
    //
    //   * queuing behind an unconfirmed move — the gate only ever asks BECAUSE a
    //     move is stuck unconfirmed, so waiting means waiting out the very thing
    //     we're trying to resolve;
    //   * the 15s throttle — the stall watchdog escalates every 10s, so a second
    //     stall always lands inside that window and would be denied.
    //
    // Both fall through to a footprint backtrack, which cannot converge in a
    // gang house of identically-named rooms — so the sweep dies. PendingRespawn
    // still defers either way: after a death the respawn room's own observation
    // is imminent and authoritative.
    public bool TryRequestLocate(string reason, bool forRecovery = false)
    {
        if (_disposed) return false;

        if (_suppressed?.Invoke() == true)
        {
            _log?.Log(LogSeverity.Debug, LogSource, $"locate suppressed; reason: {reason}");
            return false;
        }
        if (!_probe.Available)
        {
            _log?.Log(LogSeverity.Debug, LogSource,
                $"locate skipped — sysop status unavailable; reason: {reason}");
            return false;
        }

        // Coalesce: a second caller rides the answer the first one is waiting
        // for, the same way SysStatusProbe shares one in-flight query.
        if (_inFlight || _deferredReason is not null) return true;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!forRecovery && now - _lastRequestAtUtc < MinLocateInterval)
        {
            _log?.Log(LogSeverity.Info, LogSource,
                $"locate throttled ({(now - _lastRequestAtUtc).TotalSeconds:F0}s since last); reason: {reason}");
            return false;
        }

        // Never ask while movement is unconfirmed. SetLocated clears the
        // tracker's pending step, so an answer landing mid-move would throw
        // away the confirmation it is still waiting for — and a dump that
        // arrives before the move executes describes the room we just left.
        // Queue behind the movement instead. Post-death PendingRespawn is the
        // same case: let the respawn room's observation land first, and only
        // spend a command if that leaves us unresolved.
        bool movementUnsettled = _tracker.State.Confidence == RoomConfidence.PendingRespawn
            || (!forRecovery && _tracker.State.Confidence == RoomConfidence.Pending);
        if (movementUnsettled)
        {
            _deferredReason = reason;
            _deferral?.Stop();
            _deferral?.Start();
            _log?.Log(LogSeverity.Info, LogSource,
                $"locate queued behind unconfirmed movement ({_tracker.State.Confidence}); reason: {reason}");
            return true;
        }

        SendProbe(reason);
        return true;
    }

    // One-shot variant that mirrors ParadigmPositionResolver.RequestResyncOnce: a
    // caller (a loop blocked at source, an @where re-fix) gets its OWN onResolved /
    // onFailed invoked exactly once for this locate, on top of the gate's global
    // re-anchor consumers. Returns false when no locate can start (capability off /
    // throttled / suppressed) so the caller falls back immediately. Throttled by
    // default (forRecovery: false) — the heavier `sys st` shouldn't fire as freely
    // as Paradigm's one-line `rm`.
    public bool RequestLocateOnce(string reason, Action<RoomKey> onResolved, Action onFailed,
        bool forRecovery = false)
    {
        ArgumentNullException.ThrowIfNull(onResolved);
        ArgumentNullException.ThrowIfNull(onFailed);
        if (!TryRequestLocate(reason, forRecovery)) return false;

        Action<RoomKey>? resolved = null;
        Action? failed = null;
        resolved = key =>
        {
            PositionResolved -= resolved;
            LocateFailed -= failed;
            onResolved(key);
        };
        failed = () =>
        {
            PositionResolved -= resolved;
            LocateFailed -= failed;
            onFailed();
        };
        PositionResolved += resolved;
        LocateFailed += failed;
        return true;
    }

    private void SendProbe(string reason)
    {
        _inFlight = true;
        _lastRequestAtUtc = DateTimeOffset.UtcNow;
        _log?.Log(LogSeverity.Info, LogSource, $"probing `sys st` for ground truth; reason: {reason}");
        _ = RunProbeAsync(reason);
    }

    private async Task RunProbeAsync(string reason)
    {
        SysRoomStatus? status;
        try
        {
            status = await _probe.QueryAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Fire-and-forget: an escaped exception here would be unobserved and
            // would strand a caller that paused an engine waiting on us, so
            // surface it and take the failure path.
            _log?.Log(LogSeverity.Warn, LogSource, $"probe threw: {ex.Message}");
            status = null;
        }

        // The probe's completion can land on a timer thread; everything below
        // touches the tracker.
        _post(() => CompleteProbe(status, reason));
    }

    private void CompleteProbe(SysRoomStatus? status, string reason)
    {
        if (_disposed) return;
        _inFlight = false;

        if (status is null)
        {
            LastOutcome = "no room block came back";
            _log?.Log(LogSeverity.Warn, LogSource,
                $"`sys st` produced no room block for '{reason}'; falling back");
            LocateFailed?.Invoke();
            return;
        }

        RoomKey key = status.Room;
        if (_graph.GetRoom(key) is null)
        {
            // The game is right and our map is wrong — almost always the active
            // game-data set doesn't cover this room. Locating anyway would put
            // the tracker somewhere that doesn't exist.
            LastOutcome = $"{key} is not in the active game-data set";
            _log?.Log(LogSeverity.Warn, LogSource,
                $"`sys st` reported {key}, absent from the active graph; falling back");
            LocateFailed?.Invoke();
            return;
        }

        LastOutcome = $"located {key}";
        _log?.Log(LogSeverity.Info, LogSource,
            $"`sys st` ground truth → {key}; locating tracker (reason: {reason})");
        _tracker.SetLocated(key);
        PositionResolved?.Invoke(key);
    }

    private void OnTrackerStateChanged(RoomTransition t)
    {
        if (_disposed) return;

        if (_deferredReason is { } deferred
            && t.NewConfidence is not (RoomConfidence.Pending or RoomConfidence.PendingRespawn))
        {
            _deferredReason = null;
            _deferral?.Stop();
            SendProbe(deferred);
            return;
        }

        // The tracker gave up. Nothing short of a confirming observation or the
        // user clicking "I am here" resolves Lost on its own, so spend one
        // command on the answer. Edge-triggered — a tracker that churns within
        // Lost asks once, and the throttle covers the rest.
        if (t.NewConfidence == RoomConfidence.Lost && t.PreviousConfidence != RoomConfidence.Lost)
            TryRequestLocate("tracker lost");
    }

    private void OnDeferralExpired()
    {
        _deferral?.Stop();
        if (_deferredReason is not { } reason) return;
        _deferredReason = null;
        LastOutcome = "movement never settled";
        _log?.Log(LogSeverity.Warn, LogSource,
            $"movement still unconfirmed after {DeferralWindow.TotalSeconds:F0}s; abandoning the locate for '{reason}'");
        LocateFailed?.Invoke();
    }

    // ----- test seams ------------------------------------------------
    internal void FireDeferralExpiryForTests() => OnDeferralExpired();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tracker.StateChanged -= OnTrackerStateChanged;
        _deferral?.Stop();
    }
}
