namespace FujinTerm.Game.Map;

/// <summary>
/// One move command in flight — sent to the server, awaiting the room
/// observation that confirms or refutes the landing. The tracker queues
/// these so multiple back-to-back moves (faster than the BBS round-trip)
/// can each be matched against an arriving observation in order.
/// </summary>
/// <remarks>
/// <para>
/// Carries both a cardinal <see cref="Direction"/> (for graph-based
/// landing prediction) and the verbatim <see cref="Command"/> string
/// (for replay of text-exit moves like <c>"go path"</c>). Cardinal
/// moves leave <see cref="Command"/> as <c>null</c>; the replayer
/// regenerates the canonical short form from the direction.
/// </para>
/// </remarks>
public readonly record struct PendingMove(
    Direction? Cardinal,
    string? Command,
    DateTimeOffset SentAt)
{
    /// <summary>Cardinal-only shorthand for the common case.</summary>
    public static PendingMove FromDirection(Direction d, DateTimeOffset when) =>
        new(d, null, when);

    /// <summary>Text-exit move that doesn't map to a cardinal.</summary>
    public static PendingMove FromCommand(string command, DateTimeOffset when) =>
        new(null, command, when);
}
