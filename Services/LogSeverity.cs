namespace FujinTerm.Services;

/// <summary>
/// Severity tags carried by every <see cref="LogEntry"/>. The values are
/// ordered loosely by "urgency" but consumers should not rely on the numeric
/// comparison (filtering uses set membership instead).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Info"/> / <see cref="Warn"/> / <see cref="Error"/> are always
/// recorded. <see cref="Debug"/> and <see cref="Combat"/> are
/// <i>generation-gated</i> — <see cref="LogService.Debug(string, string)"/> and
/// <see cref="LogService.Combat(string, string)"/> no-op unless the matching
/// per-character diagnostic toggle is on (see <see cref="LogDiagnosticState"/>).
/// </para>
/// </remarks>
public enum LogSeverity
{
    /// <summary>Verbose cross-engine diagnostic detail. Generation-gated on the Debug toggle.</summary>
    Debug,

    /// <summary>General informational message (connect, disconnect, profile loaded, etc.). Always recorded.</summary>
    Info,

    /// <summary>Recoverable anomaly worth surfacing but not breaking work. Always recorded.</summary>
    Warn,

    /// <summary>Failure that the user should see immediately. Always recorded.</summary>
    Error,

    /// <summary>Combat-engine decision trace. Generation-gated on the Combat toggle; carries its own log chip.</summary>
    Combat,
}
