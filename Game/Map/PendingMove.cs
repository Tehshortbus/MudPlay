namespace MudPlay.Game.Map;

// One move command in flight — sent to the server, awaiting the room
// observation that confirms or refutes the landing. The tracker queues these so
// multiple back-to-back moves (faster than the BBS round-trip) can each be
// matched against an arriving observation in order.
//
// Carries both a cardinal Direction (for graph-based landing prediction) and
// the verbatim Command string (for replay of text-exit moves like "go path").
// Cardinal moves leave Command as null; the replayer regenerates the canonical
// short form from the direction.
public readonly record struct PendingMove(
    Direction? Cardinal,
    string? Command,
    DateTimeOffset SentAt,
    bool IsFollowDrag = false)
{
    // Cardinal-only shorthand for the common case.
    public static PendingMove FromDirection(Direction d, DateTimeOffset when) =>
        new(d, null, when);

    // A leader-follow drag — a party follower dragged one room in the leader's
    // direction. Predicts like a cardinal move, but flagged so the tracker's
    // passive-re-look guard (which assumes a real move is SLOWER than a stray
    // same-room redisplay) does not discard its legitimately-instant arrival: the
    // game drags a follower with no round-trip, and only ever redisplays on a real
    // arrival, so a fast redisplay after a drag is never a re-look (see RoomTracker).
    public static PendingMove FromFollowDrag(Direction d, DateTimeOffset when) =>
        new(d, null, when, IsFollowDrag: true);
}
