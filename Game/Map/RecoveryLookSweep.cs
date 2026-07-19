using System.Text;
using Avalonia.Threading;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// Move-free "look sweep" for location recovery. Standing in a room whose
// name+exits alone can't pin us on the map (name-ambiguous twins like the
// dozens of identical Darkwood Forest rooms), the driver sends a `look <dir>`
// at each of the room's own exits and collects the neighbour room's rendered
// name+exits. The current room's exits plus each peeked neighbour's
// observation form a far wider fingerprint than the room alone — enough to
// break twins the graph can't otherwise separate (two identical rooms differ
// in the exits of the rooms AROUND them).
//
// The player never moves: `look <dir>` is peek-suppressed by
// OutboundMovementObserver (which fires NoteLookSent), so the tracker won't
// read the peeked render as a step. This is the general-graph analogue of the
// asylum's 1x2 relocalize, carved down to just the sweep — the caller
// (EngineRecoveryGate) owns what to do with the neighbours it hands back.
//
// Single-threaded: Begin / OnRoomObserved / the timer tick all run on the UI
// thread (Dispatcher-marshalled upstream), same as the asylum solver.
public sealed class RecoveryLookSweep : IDisposable
{
    private const string LogSource = "RecoveryGate";

    // Per-look deadline: if a peek never renders (a lost packet, a room that
    // won't display), skip that arm rather than hang the whole recovery.
    private static readonly TimeSpan LookTimeout = TimeSpan.FromSeconds(3);

    private readonly LogService? _log;
    private readonly DispatcherTimer? _lookTimeout;

    private Action<byte[]>? _wireSender;
    private bool _disposed;

    private bool _active;
    private Direction _currentLookDir;
    private readonly Queue<Direction> _lookQueue = new();
    private readonly Dictionary<Direction, RoomObservation> _neighbours = new();
    private Action<IReadOnlyDictionary<Direction, RoomObservation>>? _onComplete;

    public RecoveryLookSweep(LogService? log = null) : this(log, useTimer: true) { }

    // Test seam: useTimer false swaps the DispatcherTimer out so tests drive the
    // per-look timeout via FireLookTimeoutForTests instead of a wall clock.
    internal RecoveryLookSweep(LogService? log, bool useTimer)
    {
        _log = log;
        if (useTimer)
        {
            _lookTimeout = new DispatcherTimer(DispatcherPriority.Background) { Interval = LookTimeout };
            _lookTimeout.Tick += (_, _) => OnLookTimeout();
        }
    }

    // True while a sweep is mid-flight (looks queued or awaiting a render).
    public bool Active => _active;

    // Available once the main-window VM supplies the EngineSendGate-wrapped
    // SendUserInput. Without it the driver can't peek, so the gate skips the
    // sweep and falls back to move-only narrowing.
    public bool CanSweep => _wireSender is not null;

    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Begin peeking every exit in ownExits. onComplete fires once each exit has
    // rendered (or timed out), carrying the neighbours that resolved. Returns
    // false when a sweep can't start — no wire sender, already sweeping, or the
    // room has no exits to peek — so the caller narrows by movement alone.
    public bool Begin(IReadOnlySet<Direction> ownExits,
        Action<IReadOnlyDictionary<Direction, RoomObservation>> onComplete)
    {
        ArgumentNullException.ThrowIfNull(ownExits);
        ArgumentNullException.ThrowIfNull(onComplete);
        if (_wireSender is null || _active) return false;

        _lookQueue.Clear();
        _neighbours.Clear();
        // Cardinal directions only (N..D), enum order — matches how the graph
        // encodes exits and how the asylum sweep peeks.
        for (int d = (int)Direction.N; d <= (int)Direction.D; d++)
            if (ownExits.Contains((Direction)d))
                _lookQueue.Enqueue((Direction)d);

        if (_lookQueue.Count == 0) return false;

        _active = true;
        _onComplete = onComplete;
        _log?.Log(LogSeverity.Info, LogSource,
            $"look-sweep: peeking {_lookQueue.Count} neighbour(s)");
        SendNextLook();
        return true;
    }

    // Fed every parsed room display (RoomDisplayParser.RoomParsed, which fires
    // BEFORE the tracker's peek-suppression). While sweeping, the render is the
    // neighbour revealed by the `look <dir>` we just sent; correlate it to the
    // direction we're currently peeking and advance to the next look.
    public void OnRoomObserved(RoomObservation obs)
    {
        if (!_active) return;
        _neighbours[_currentLookDir] = obs;
        _lookTimeout?.Stop();
        SendNextLook();
    }

    // Abandon an in-flight sweep (engine detached, graph reload, recovery
    // superseded). Drops all scratch without invoking the completion callback.
    public void Cancel()
    {
        if (!_active) return;
        _active = false;
        _onComplete = null;
        _lookQueue.Clear();
        _neighbours.Clear();
        _lookTimeout?.Stop();
    }

    private void SendNextLook()
    {
        if (_lookQueue.Count == 0)
        {
            Complete();
            return;
        }

        _currentLookDir = _lookQueue.Dequeue();
        _wireSender?.Invoke(Encoding.Latin1.GetBytes("look " + _currentLookDir.ToLongName() + "\r"));
        _lookTimeout?.Stop();
        _lookTimeout?.Start();
    }

    private void OnLookTimeout()
    {
        _lookTimeout?.Stop();
        if (!_active) return;
        // This peek never rendered — skip the arm and move on. Recovery is
        // best-effort: a missing neighbour is one fewer constraint, not a
        // failed sweep.
        _log?.Log(LogSeverity.Debug, LogSource,
            $"look-sweep: {_currentLookDir.ToLongName()} did not render; skipping");
        SendNextLook();
    }

    private void Complete()
    {
        _active = false;
        var result = new Dictionary<Direction, RoomObservation>(_neighbours);
        Action<IReadOnlyDictionary<Direction, RoomObservation>>? cb = _onComplete;
        _onComplete = null;
        _lookQueue.Clear();
        _neighbours.Clear();
        _log?.Log(LogSeverity.Info, LogSource,
            $"look-sweep complete: {result.Count} neighbour(s) resolved");
        cb?.Invoke(result);
    }

    internal void FireLookTimeoutForTests() => OnLookTimeout();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lookTimeout?.Stop();
    }
}
