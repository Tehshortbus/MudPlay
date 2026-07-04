namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character log-diagnostic generation switches. Persisted as the
/// <c>"LogDiagnostics"</c> entry in <see cref="CharacterProfile.Settings"/>.
/// </summary>
/// <remarks>
/// Char-tier (not Global) — different characters can carry different diagnostic
/// states. Both flags default off: verbose Debug + Combat tracing is a
/// troubleshooting affordance, not an everyday cost, so a fresh character (no
/// saved section) reads off. The live mirror is
/// <see cref="Services.LogDiagnosticState"/>; <c>AppServices</c> reads this
/// section on profile load and writes it back when a Log-pane toggle flips.
/// </remarks>
public sealed class LogDiagnosticsSettings
{
    /// <summary>Gate for the generation-gated Debug channel. Default off.</summary>
    public bool Debug { get; set; }

    /// <summary>
    /// Gate for the generation-gated Combat channel + the
    /// <see cref="Game.Combat.RoundDamageTracker"/> per-round trace file.
    /// Default off.
    /// </summary>
    public bool Combat { get; set; }
}
