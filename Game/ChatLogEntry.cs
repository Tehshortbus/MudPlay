namespace FujinTerm.Game;

// One classified chat / realm-event line. Emitted by ChatRouter and consumed
// by ChatHistoryStore, the ConversationWindow, and any logging / alerting
// subsystem.
//
// Timestamp: wall-clock time the originating line was emitted.
// Channel: which channel the line is classified into.
// Speaker: player name when known. null for outgoing telepaths (the echo gives
//   the recipient instead), broadcasts (operator's name lives in Speaker),
//   realm-events with no named subject, or own yells where the speaker is the
//   local player. Specific semantics per channel are documented inline in
//   ChatRouter.
// Message: the message text. For realm events this is a short verb phrase
//   ("entered the Realm" / "left the Realm" / "disconnected").
// RawText: the original LineExtractor.EmittedLine.Text so downstream consumers
//   can fall back to the verbatim form when the classified split isn't
//   sufficient.
public readonly record struct ChatLogEntry(
    DateTimeOffset Timestamp,
    ChatChannel Channel,
    string? Speaker,
    string Message,
    string RawText);
