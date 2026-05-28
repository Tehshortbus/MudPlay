namespace FujinTerm.Game;

/// <summary>
/// One classified chat / realm-event line. Emitted by <see cref="ChatRouter"/>
/// and consumed by <see cref="ChatHistoryStore"/>, the
/// ConversationWindow, and any future logging / alerting subsystem.
/// </summary>
/// <param name="Timestamp">Wall-clock time the originating line was emitted.</param>
/// <param name="Channel">Which channel the line is classified into.</param>
/// <param name="Speaker">
/// Player name when known. <c>null</c> for outgoing telepaths (the echo
/// gives the recipient instead), broadcasts (operator's name lives in
/// <see cref="Speaker"/>), realm-events with no named subject, or own
/// yells where the speaker is the local player. Specific semantics per
/// channel are documented inline in <see cref="ChatRouter"/>.
/// </param>
/// <param name="Message">
/// The message text. For realm events this is a short verb phrase
/// (<c>"entered the Realm"</c> / <c>"left the Realm"</c> / <c>"disconnected"</c>).
/// </param>
/// <param name="RawText">
/// The original <see cref="Terminal.LineExtractor.EmittedLine"/>.Text so
/// downstream consumers can fall back to the verbatim form when the
/// classified split isn't sufficient.
/// </param>
public readonly record struct ChatLogEntry(
    DateTimeOffset Timestamp,
    ChatChannel Channel,
    string? Speaker,
    string Message,
    string RawText);
