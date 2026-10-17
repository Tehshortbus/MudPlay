using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// Follower side of the Bossing set's "keep on between bosses" option. A follower is
// carried along by the game's follow and has no route of its own, so the only way
// to know whether the party is heading for another boss is to ask the leader's
// client: this telepaths it @path and reports whether what it says it's doing ends
// at a boss room.
//
// The cycle (user, 2026-10-03), run once per boss room left:
//   1. Ask. A walk to a boss room keeps the gear on; any other destination takes
//      it off. Either ends the cycle.
//   2. "Not moving", or no reply inside ReplyTimeout: nothing is decided. The gear
//      stays on until we next change rooms (NoteMoved), then ask once more.
//   3. That second answer stands. "Not moving" again, or no reply inside
//      ReplyTimeout, takes the gear off.
// Nothing is re-asked on a timer: the next question is at the next boss room.
public sealed class LeaderBossTravelProbe : IDisposable
{
    public const string LogCategory = "BossTravel";

    internal static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(15);

    private readonly PathReplyTracker _replies;
    private readonly Func<string?> _leaderGivenName;
    private readonly Func<PathReport, bool> _headsToBoss;
    private readonly Func<RoomKey, bool> _isBossRoom;
    private readonly Func<bool> _stillNeeded;
    private readonly Func<TimeSpan, Action, IDisposable>? _schedule;
    private readonly LogService? _log;

    private Action<byte[]>? _wireSender;
    private IDisposable? _timer;
    private bool _awaiting;
    // The first ask got "not moving" or no reply; ask again when the party moves.
    private bool _waitingForLeaderToMove;
    // The question out is that second ask, so its outcome is final.
    private bool _confirming;
    private bool _disposed;

    // Whether the leader's trip ends at a boss room. Raised once per question.
    public event Action<bool>? Resolved;

    // The last question's outcome, for the bug report.
    public string LastOutcome { get; private set; } = "(never asked)";

    // leaderGivenName: the leader we're following, or null when we lead / are solo.
    // headsToBoss: whether a leader's @path report describes a trip to a boss room.
    // stillNeeded: the gear is still being kept on, so an answer still matters.
    // schedule: the one-shot timer that ends the wait for an answer.
    public LeaderBossTravelProbe(
        PathReplyTracker replies,
        Func<string?> leaderGivenName,
        Func<PathReport, bool> headsToBoss,
        Func<RoomKey, bool> isBossRoom,
        Func<bool> stillNeeded,
        Func<TimeSpan, Action, IDisposable>? schedule = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(replies);
        ArgumentNullException.ThrowIfNull(leaderGivenName);
        ArgumentNullException.ThrowIfNull(headsToBoss);
        ArgumentNullException.ThrowIfNull(isBossRoom);
        ArgumentNullException.ThrowIfNull(stillNeeded);
        _replies = replies;
        _leaderGivenName = leaderGivenName;
        _headsToBoss = headsToBoss;
        _isBossRoom = isBossRoom;
        _stillNeeded = stillNeeded;
        _schedule = schedule;
        _log = log;
        _replies.PathReported += OnPathReported;
        _replies.GotoReported += OnGotoReported;
        _replies.IdleReported += OnIdleReported;
    }

    public void SetWireSender(Action<byte[]> sender) => _wireSender = sender;

    // True when there's a leader to ask.
    public bool HasLeader => _leaderGivenName() is { Length: > 0 };

    // The master switch (true = off): off, the leader is not asked.
    public Func<bool>? MasterSwitchOff { get; set; }

    // Ask the leader where it's going. False when there's nobody to ask. A question
    // already out is left to run.
    public bool Ask()
    {
        if (_disposed || _leaderGivenName() is not { Length: > 0 } leader) return false;
        if (MasterSwitchOff?.Invoke() == true) return false;
        if (_awaiting) return true;
        _awaiting = true;
        byte[] bytes = Encoding.Latin1.GetBytes($"/{leader} @path\r");
        _wireSender?.Invoke(bytes);
        _log?.Info(LogCategory, $"asked {leader} where the party is heading (@path)");
        Arm(ReplyTimeout, OnTimeout);
        return true;
    }

    private void OnPathReported(string sender, PathReport report)
    {
        if (!IsLeader(sender) || !(_awaiting || _stillNeeded())) return;
        Resolve(_headsToBoss(report), report.Destination is { } d ? $"{sender} is walking to {d}"
            : report.LoopName is { } l ? $"{sender} is on loop '{l}' (only a walk-to keeps the set on)"
            : $"{sender} reported no destination");
    }

    // The leader accepted an @goto: that's its new destination, asked for or not.
    private void OnGotoReported(string sender, RoomKey destination)
    {
        if (!IsLeader(sender) || !(_awaiting || _stillNeeded())) return;
        Resolve(_isBossRoom(destination), $"{sender} accepted a walk to {destination}");
    }

    private void OnIdleReported(string sender)
    {
        if (!IsLeader(sender) || !_awaiting) return;
        if (_confirming)
        {
            Resolve(false, $"{sender} is moving without a walk-to");
            return;
        }
        WaitForTheNextMove($"{sender} isn't moving yet");
    }

    private void WaitForTheNextMove(string why)
    {
        _awaiting = false;
        CancelTimer();
        _waitingForLeaderToMove = true;
        LastOutcome = $"{why} — asking again when the party moves";
        _log?.Info(LogCategory, LastOutcome);
    }

    // We changed rooms. Following, that means the leader moved: if it was idle when
    // asked, this is the moment to ask where it's going.
    public void NoteMoved()
    {
        if (!_waitingForLeaderToMove) return;
        // Left waiting, so the question is asked at the first move after the
        // switch is back on, and whoever is waiting on the answer still gets one.
        if (MasterSwitchOff?.Invoke() == true) return;
        _waitingForLeaderToMove = false;
        if (!_stillNeeded()) return;
        _confirming = true;
        if (!Ask()) _confirming = false;
    }

    private bool IsLeader(string sender) =>
        _leaderGivenName() is { Length: > 0 } leader
        && string.Equals(leader, sender, StringComparison.OrdinalIgnoreCase);

    private void OnTimeout()
    {
        if (!_awaiting) return;
        if (_confirming)
            Resolve(false, $"no @path answer in {ReplyTimeout.TotalSeconds:0}s after asking again");
        else
            WaitForTheNextMove($"no @path answer in {ReplyTimeout.TotalSeconds:0}s");
    }

    private void Resolve(bool headingToBoss, string what)
    {
        _awaiting = false;
        _confirming = false;
        _waitingForLeaderToMove = false;
        CancelTimer();
        LastOutcome = $"{what} — {(headingToBoss ? "a boss room" : "not a boss room")}";
        _log?.Info(LogCategory, LastOutcome);
        Resolved?.Invoke(headingToBoss);
    }

    private void Arm(TimeSpan delay, Action action)
    {
        CancelTimer();
        _timer = _schedule?.Invoke(delay, action);
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
        _replies.PathReported -= OnPathReported;
        _replies.GotoReported -= OnGotoReported;
        _replies.IdleReported -= OnIdleReported;
    }
}
