namespace FujinTerm.Services;

/// <summary>
/// One row in the <see cref="LogService"/> ring buffer.
/// </summary>
/// <param name="Timestamp">Wall-clock time the entry was recorded.</param>
/// <param name="Severity">Severity tag for filtering / color-coding.</param>
/// <param name="Source">
/// Short subsystem tag (e.g. <c>"Telnet"</c>, <c>"Parser"</c>, <c>"Profile"</c>).
/// Lets the log pane group / filter by producer.
/// </param>
/// <param name="Message">
/// Raw message body. May contain ANSI escape sequences — the log pane renders
/// these inline in Phase 1; no separate <c>LogRenderer</c> class is needed.
/// </param>
public readonly record struct LogEntry(
    DateTimeOffset Timestamp,
    LogSeverity Severity,
    string Source,
    string Message);
