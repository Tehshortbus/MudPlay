namespace FujinTerm.Services;

/// <summary>
/// Session-only diagnostic switches surfaced in the Log pane menu.
/// Distinct from per-character settings — these don't persist; they
/// reset to off on every app launch because verbose tracing burns IO
/// and isn't an everyday-use affordance.
/// </summary>
/// <remarks>
/// <para>
/// Today's single switch — <see cref="CombatDiagnostics"/> — is the
/// umbrella for everything combat-related: verbose Debug emission
/// from Combat / CombatGate / RoomClassifier / Casting / Cash /
/// Stealth categories AND <see cref="Game.Combat.RoundDamageTracker"/>'s
/// per-round trace file under <c>Data/Logs/combat-*.log</c>. Per user
/// direction we collapsed the six per-category toggles + the trace
/// toggle that previously lived under Settings → Other into this
/// single Log-menu knob.
/// </para>
/// <para>
/// Lives under <see cref="AppServices.LogDiagnostics"/> so any consumer
/// (RoundDamageTracker today; future verbose emitters tomorrow) can
/// read the live value without coupling to the Log pane's UI layer.
/// </para>
/// </remarks>
public sealed class LogDiagnosticState
{
    private bool _combatDiagnostics;

    /// <summary>
    /// Master toggle for combat-related verbose emission + the per-
    /// round trace file. Off by default; flip on while troubleshooting
    /// a combat or healing engine, flip off again for normal play.
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

    /// <summary>Fires after any flag change so observers (e.g. the
    /// LogPane VM mirroring the master state across windows) can
    /// refresh their bound view.</summary>
    public event Action? Changed;
}
