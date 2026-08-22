namespace MudPlay.Game.Map;

// Drives a localizing walk: ask the locator which exit splits the surviving
// candidates furthest, send it, fold the landing back in, repeat.
//
// A pump rather than a loop, because its hosts are event-driven — the gate
// sends and returns, and the landing arrives later on a room-observed
// event. Begin/OnLanding return null while the walk is still going and an
// outcome when it is done.
//
// Sending is injected, so the same walk serves an attached engine
// (SendBacktrackMove) and an engine-less driver (the gated wire sender)
// without either being named here.
public sealed class LocatorWalk
{
    private readonly RoomLocator _locator;
    private readonly FootprintMatcher _matcher;
    private readonly Action<Direction> _send;
    private readonly int _budget;

    private RoomObservation _here;
    private Direction _lastSent;
    private bool _active;

    public LocatorWalk(
        RoomLocator locator,
        FootprintMatcher matcher,
        Action<Direction> send,
        int budget = RoomLocator.DefaultBudget)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(send);
        if (budget < 0) throw new ArgumentOutOfRangeException(nameof(budget));
        _locator = locator;
        _matcher = matcher;
        _send = send;
        _budget = budget;
    }

    // True while a move is outstanding and OnLanding is owed.
    public bool IsActive => _active;

    // Moves completed (landed) so far this walk — while a move is
    // outstanding this reads one behind the number of sends.
    public int Steps { get; private set; }

    // Seed from the current display and take the first step if one is
    // needed. Returns an outcome when no walking is required at all.
    public LocateOutcome? Begin(RoomObservation here)
    {
        _here = here;
        Steps = 0;
        _active = false;
        _matcher.Reset(_locator.Seed(here));
        return Advance();
    }

    // Fold one landing into the candidate set and take the next step.
    //
    // A landing that arrives with no move outstanding is ignored by
    // design, not an error: MudPlay genuinely emits passive room
    // re-displays with no move behind them (see RoomTracker's
    // IsRepeatRedisplayWithoutMove), and a caller can't tell those apart
    // from a real landing before calling in. Null in that case means
    // "nothing to do" — it is NOT the same null as "a move was sent, pump
    // me again"; IsActive tells the two apart.
    public LocateOutcome? OnLanding(RoomObservation landed)
    {
        if (!_active) return null;
        _active = false;
        _matcher.Step(_lastSent, landed);
        Steps++;
        _here = landed;
        return Advance();
    }

    // Settle, or send the next splitting step. Null means a move went out.
    private LocateOutcome? Advance()
    {
        if (_matcher.Candidates.Count == 0) return LocateOutcome.Unknown(Steps);
        if (_matcher.IsConverged)
        {
            foreach (RoomKey only in _matcher.Candidates)
                return LocateOutcome.Converged(only, Steps);
        }
        if (Steps >= _budget) return LocateOutcome.Ambiguous(_matcher.Candidates.Count, Steps);

        // Candidates is IReadOnlySet<RoomKey>, which already satisfies the
        // IReadOnlyCollection parameter — no copy, no System.Linq needed.
        Direction? next = _locator.ChooseSplittingExit(_matcher.Candidates, _here);
        // Nothing left to learn by moving — stop rather than wander.
        if (next is not { } dir) return LocateOutcome.Ambiguous(_matcher.Candidates.Count, Steps);

        _lastSent = dir;
        _active = true;
        _send(dir);
        return null;
    }
}
