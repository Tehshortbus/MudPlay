namespace FujinTerm.Services;

// Severity tags carried by every LogEntry. The values are ordered loosely by
// "urgency" but consumers should not rely on the numeric comparison
// (filtering uses set membership instead).
//
// Info / Warn / Error are always recorded. Debug and Combat are
// generation-gated — LogService.Debug and LogService.Combat no-op unless the
// matching per-character diagnostic toggle is on (see LogDiagnosticState).
public enum LogSeverity
{
    // Verbose cross-engine diagnostic detail. Generation-gated on the Debug toggle.
    Debug,

    // General informational message (connect, disconnect, profile loaded, etc.). Always recorded.
    Info,

    // Recoverable anomaly worth surfacing but not breaking work. Always recorded.
    Warn,

    // Failure that the user should see immediately. Always recorded.
    Error,

    // Combat-engine decision trace. Generation-gated on the Combat toggle; carries its own log chip.
    Combat,
}
