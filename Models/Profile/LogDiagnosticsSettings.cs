namespace FujinTerm.Models.Profile;

// Per-character log-diagnostic generation switches. Persisted as the
// "LogDiagnostics" entry in CharacterProfile.Settings.
//
// Char-tier (not Global) — different characters can carry different diagnostic
// states. Both flags default off: verbose Debug + Combat tracing is a
// troubleshooting affordance, not an everyday cost, so a fresh character (no
// saved section) reads off. The live mirror is Services.LogDiagnosticState;
// AppServices reads this section on profile load and writes it back when a
// Log-pane toggle flips.
public sealed class LogDiagnosticsSettings
{
    // Gate for the generation-gated Debug channel. Default off.
    public bool Debug { get; set; }

    // Gate for the generation-gated Combat channel + the
    // Game.Combat.RoundDamageTracker per-round trace file. Default off.
    public bool Combat { get; set; }
}
