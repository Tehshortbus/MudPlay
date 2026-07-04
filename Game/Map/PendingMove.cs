namespace FujinTerm.Game.Map;

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
    DateTimeOffset SentAt)
{
    // Cardinal-only shorthand for the common case.
    public static PendingMove FromDirection(Direction d, DateTimeOffset when) =>
        new(d, null, when);

    // Text-exit move that doesn't map to a cardinal.
    public static PendingMove FromCommand(string command, DateTimeOffset when) =>
        new(null, command, when);
}
