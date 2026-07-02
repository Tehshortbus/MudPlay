namespace FujinTerm.Services;

/// <summary>
/// Live per-character diagnostic switches that gate generation of the
/// <see cref="LogSeverity.Debug"/> and <see cref="LogSeverity.Combat"/> log
/// channels. Surfaced as the two generation toggles in the Log pane.
/// </summary>
/// <remarks>
/// <para>
/// This is the in-memory source of truth. <c>AppServices</c> mirrors it to the
/// Char-tier <c>LogDiagnosticsSettings</c> section: it applies the persisted
/// values on <c>ProfileLoaded</c>, resets to off on <c>ProfileClosed</c>, and
/// writes back on <see cref="Changed"/>. Both flags default off — verbose
/// tracing burns IO and isn't an everyday affordance — and a fresh character
/// (no saved section) reads off.
/// </para>
/// <para>
/// <see cref="DebugDiagnostics"/> gates the cross-engine Debug traces; every
/// <c>_log?.Debug(...)</c> site emits only while it's on.
/// <see cref="CombatDiagnostics"/> gates the combat-decision channel AND
/// <see cref="Game.Combat.RoundDamageTracker"/>'s per-round trace file under
/// <c>Data/Logs/combat-*.log</c>.
/// </para>
/// <para>
/// Lives under <c>AppServices.LogDiagnostics</c> and is wired into
/// <see cref="LogService.Diagnostics"/> so the service can gate emission at the
/// source without coupling to the Log pane's UI layer.
/// </para>
/// </remarks>
public sealed class LogDiagnosticState
{
    private bool _debugDiagnostics;
    private bool _combatDiagnostics;

    /// <summary>
    /// Master toggle for the generation-gated Debug channel. Off by
    /// default; flip on to make every <c>_log?.Debug(...)</c> site across
    /// the engines start emitting, flip off again for normal play.
    /// </summary>
    public bool DebugDiagnostics
    {
        get => _debugDiagnostics;
        set
        {
            if (_debugDiagnostics == value) return;
            _debugDiagnostics = value;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Master toggle for the combat-decision channel + the per-round trace
    /// file. Off by default; flip on while troubleshooting a combat or
    /// healing engine, flip off again for normal play.
    /// </summary>
    public bool CombatDiagnostics
    {
        get => _combatDiagnostics;
        set
        {
            if (_combatDiagnostics == value) return;
            _combatDiagnostics = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Fires after any flag change so observers (the LogPane VM
    /// mirroring state across windows; AppServices persisting the change to
    /// the active character) can refresh.</summary>
    public event Action? Changed;
}
