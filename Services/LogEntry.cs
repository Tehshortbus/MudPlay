namespace FujinTerm.Services;

// One row in the LogService ring buffer.
//   Timestamp — wall-clock time the entry was recorded.
//   Severity  — severity tag for filtering / color-coding.
//   Source    — short subsystem tag (e.g. "Telnet", "Parser", "Profile", or
//     one of the combat categories: "Combat", "CombatGate",
//     "RoomClassifier", "Casting", etc.). Lets the log pane group / filter by
//     producer.
//   Message   — raw message body. May contain ANSI escape sequences — the
//     log pane renders these inline.
//   Context   — optional raw wire line (or other source-string) that produced
//     the entry — preserves the exact text for the LogPane's
//     double-click-to-copy and the fix-dialog. Game.Combat.RoomEntityClassifier
//     emits Warn rows on Unknown entities with the parsed "Also Here" line
//     carried here so the user can double-click to copy + paste-fix the
//     underlying game data. Null when the producer has nothing meaningful to
//     expose (most log lines).
public readonly record struct LogEntry(
    DateTimeOffset Timestamp,
    LogSeverity Severity,
    string Source,
    string Message,
    string? Context = null);
