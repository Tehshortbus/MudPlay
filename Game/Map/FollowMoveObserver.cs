using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Map;

// Keeps a dragged party follower's map position live. In MajorMUD movement is
// leader-driven: when the leader walks, the game drags every follower one room
// in the same direction and prints " -- Following your Party leader <dir> --"
// just before the new room display. A follower sends no movement bytes of its
// own (PartyFollowerMovementGate holds its engines), so nothing else tells
// RoomTracker a move happened. Without that signal the tracker keeps its anchor
// on the room the follower was dragged OUT of, reads every new room display as a
// mismatch, racks up suspect strikes and falls to Lost within a few rooms — the
// "even after I set my location we instantly go Lost" symptom.
//
// This observer turns each drag line into the move the follower never typed:
// parse the direction and call RoomTracker.NoteMoveSent, exactly as the walker
// would for an engine step. The tracker then predicts the landing room from the
// leader's direction and re-confirms on the following room display, so a
// follower stays located while the leader walks the party around.
//
// The drag line is inherently follower-scoped — it only prints while the game is
// dragging us — so there is no party-role gate here.
public sealed class FollowMoveObserver : IDisposable
{
    private const string LogCategory = "FollowMove";

    private readonly RoomTracker _tracker;
    private readonly LogService? _log;
    private readonly IDisposable _sub;

    public FollowMoveObserver(MessageRouter router, RoomTracker tracker, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
        _log = log;
        _sub = router.Subscribe(KnownPatterns.PartyFollowMove, OnFollowMove);
    }

    private void OnFollowMove(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        if (!DirectionExtensions.TryFromLongName(result.Groups[0], out Direction direction)) return;

        _tracker.NoteMoveSent(direction);
        _log?.Info(LogCategory, $"Follow-drag {direction} — recorded as a move so the map stays located.");
    }

    public void Dispose() => _sub.Dispose();
}
