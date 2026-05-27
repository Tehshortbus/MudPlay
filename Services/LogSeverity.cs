namespace FujinTerm.Services;

/// <summary>
/// Severity tags carried by every <see cref="LogEntry"/>. The values are
/// ordered loosely by "urgency" but consumers should not rely on the numeric
/// comparison (filtering uses set membership instead).
/// </summary>
public enum LogSeverity
{
    /// <summary>Verbose diagnostic detail. Hidden by default in the log pane.</summary>
    Debug,

    /// <summary>General informational message (connect, disconnect, profile loaded, etc.).</summary>
    Info,

    /// <summary>Recoverable anomaly worth surfacing but not breaking work.</summary>
    Warn,

    /// <summary>Failure that the user should see immediately.</summary>
    Error,

    /// <summary>Server-originated game text routed to the log (e.g., system broadcasts).</summary>
    GameMsg,

    /// <summary>Command issued to the server (by the user or by automation).</summary>
    Cmd,
}
