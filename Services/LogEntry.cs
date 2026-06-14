namespace FujinTerm.Services;

/// <summary>
/// One row in the <see cref="LogService"/> ring buffer.
/// </summary>
/// <param name="Timestamp">Wall-clock time the entry was recorded.</param>
/// <param name="Severity">Severity tag for filtering / color-coding.</param>
/// <param name="Source">
/// Short subsystem tag (e.g. <c>"Telnet"</c>, <c>"Parser"</c>, <c>"Profile"</c>,
/// or one of the Phase 9 categories: <c>"Combat"</c>, <c>"CombatGate"</c>,
/// <c>"RoomClassifier"</c>, <c>"Casting"</c>, etc.). Lets the log pane group /
/// filter by producer.
/// </param>
/// <param name="Message">
/// Raw message body. May contain ANSI escape sequences — the log pane renders
/// these inline.
/// </param>
/// <param name="Context">
/// Optional raw wire line (or other source-string) that produced the entry —
/// preserves the exact text for the LogPane's double-click-to-copy and the
/// Phase 9 sub-G fix-dialog. Phase 9
/// <see cref="Game.Combat.RoomEntityClassifier"/> emits <c>Warn</c> rows on
/// Unknown entities with the parsed "Also Here" line carried here so the user
/// can double-click to copy + paste-fix the underlying game data. Null when
/// the producer has nothing meaningful to expose (most log lines).
/// </param>
public readonly record struct LogEntry(
    DateTimeOffset Timestamp,
    LogSeverity Severity,
    string Source,
    string Message,
    string? Context = null);
