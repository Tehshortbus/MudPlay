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
    bool IsFollowDrag = false,
    // True when a walker/loop/auto-lair called NoteMoveSent directly (as opposed to
    // a manually-typed move only observed via the wire echo). RoomTracker reads this
    // off a refused move's head entry to tell EngineRecoveryGate whether a refusal is
    // evidence the ENGINE's own plan is wrong, versus a benign manually-mistyped
    // exit that happens to arrive while an engine is attached — escalating recovery
    // on the latter would walk the character off for no reason.
    bool IsEngineIssued = false)
{
    // Cardinal-only shorthand for the common case.
    public static PendingMove FromDirection(Direction d, DateTimeOffset when, bool isEngineIssued = false) =>
        new(d, null, when, IsEngineIssued: isEngineIssued);

    // Text-exit move that doesn't map to a cardinal.
    public static PendingMove FromCommand(string command, DateTimeOffset when) =>
        new(null, command, when);

    // A leader-follow drag — a party follower dragged one room in the leader's
    // direction. Predicts like a cardinal move, but flagged so the tracker's
    // passive-re-look guard (which assumes a real move is SLOWER than a stray
    // same-room redisplay) does not discard its legitimately-instant arrival: the
    // game drags a follower with no round-trip, and only ever redisplays on a real
    // arrival, so a fast redisplay after a drag is never a re-look (see RoomTracker).
    public static PendingMove FromFollowDrag(Direction d, DateTimeOffset when) =>
        new(d, null, when, IsFollowDrag: true);
}
