using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Gmcp;

// The server's room data as a locator, asked ahead of Paradigm's `rm` and a sysop's
// `sys st`: where the recovery gate, a walk's one-shot re-fix or the tracker with no
// engine attached would ask the game where we are, the room the server has us in
// answers, with nothing sent (GmcpIntegration.ServerRoom says when it can). The
// asker sets itself up to wait only once its ask returns, as it does for an `rm`
// whose reply is a round trip away, so the answer comes a tick later.
public sealed class GmcpPositionLocator
{
    private const string LogSource = "GMCP";

    private readonly GmcpIntegration _gmcp;
    private readonly RoomTracker _tracker;
    private readonly EngineRecoveryGate _gate;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private readonly LogService? _log;

    // How many moves the tracker had counted when the server's room data last came.
    private int _movesAtRoomData;

    // The last room the server's data placed us in, and why.
    public (RoomKey Room, string Reason, DateTimeOffset At)? LastLocate { get; private set; }

    // For the bug report.
    public string Describe() => LastLocate is { } last
        ? $"Server room data last placed us at {last.Room}, {last.At:HH:mm:ss}: {last.Reason}"
        : "Server room data has not placed us this run";

    public GmcpPositionLocator(GmcpIntegration gmcp, RoomTracker tracker, EngineRecoveryGate gate,
        Func<TimeSpan, Action, IDisposable> schedule, LogService? log = null)
    {
        _gmcp = gmcp;
        _tracker = tracker;
        _gate = gate;
        _schedule = schedule;
        _log = log;
        _movesAtRoomData = tracker.MovesSentCount;
        gmcp.State.Changed += package =>
        {
            if (package == "Anomaly.Room.Info") _movesAtRoomData = _tracker.MovesSentCount;
        };
    }

    // The gate's own asks (TryResync, TryResyncForced), answered as an `rm` reply is.
    public bool TryLocate(string reason) => Ask(reason, key =>
    {
        if (key is { } room) _gate.NoteAuthoritativePosition(room);
        else _gate.OnAuthoritativeResyncFailed();
    });

    // A walk's one-shot re-fix (EngineRecoveryGate.TryResyncOnce).
    public bool TryLocateOnce(string reason, Action<RoomKey> onResolved, Action onFailed) => Ask(reason, key =>
    {
        if (key is { } room) onResolved(room);
        else onFailed();
    });

    // The tracker's ask with no engine attached (RoomTracker.RequestAuthoritativeResync).
    public bool TryLocateTracker(string reason) => Ask(reason, _ => { });

    private bool Ask(string reason, Action<RoomKey?> answer)
    {
        if (_gmcp.ServerRoom() is null) return false;
        // The server sends its room data only on a room change, so data older than a
        // move still unanswered can't say whether that move landed: the link may have
        // died with its answer on the way (report paradigm-20261031-051153). The game
        // is asked instead, and its answer comes after the move's.
        if (_tracker.MoveAwaitingAnswer && _tracker.MovesSentCount != _movesAtRoomData)
        {
            _log?.Info(LogSource, $"Server room data is older than a move still unanswered; asking the game ({reason}).");
            return false;
        }
        DateTimeOffset? moveBeforeAsk = _tracker.LastMoveSentAt;
        _schedule(TimeSpan.Zero, () => answer(Locate(reason, moveBeforeAsk)));
        return true;
    }

    private RoomKey? Locate(string reason, DateTimeOffset? moveBeforeAsk)
    {
        if (_gmcp.ServerRoom() is not { } key)
        {
            _log?.Warn(LogSource, $"Server room data was gone before it could place us ({reason}).");
            return null;
        }
        // A move sent after the ask, still unanswered, from the room the server has us
        // in hasn't landed yet: locating would drop it and leave the tracker sure of a
        // room it is walking out of. It is kept, as for an `rm` asked before the move.
        if (_tracker.State.Confidence == RoomConfidence.Pending && _tracker.LastMoveSentAt != moveBeforeAsk
            && _tracker.State.CurrentRoom?.Key == key)
            _log?.Info(LogSource, $"Server room data names {key}, the room a move sent since is leaving; the move is kept ({reason}).");
        else
            _tracker.SetLocated(key, reason: "server room data");
        LastLocate = (key, reason, DateTimeOffset.Now);
        _log?.Info(LogSource, $"Server room data placed us at {key}: {reason}.");
        return key;
    }
}
