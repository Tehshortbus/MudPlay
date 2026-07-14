namespace FujinTerm.Models.Profile;

// Per-character log-diagnostic switches. Persisted as the "LogDiagnostics"
// entry in CharacterProfile.Settings.
//
// Char-tier (not Global) — different characters can carry different diagnostic
// states. All flags default off: verbose Debug + Combat tracing and on-disk
// log generation are troubleshooting affordances, not an everyday cost, so a
// fresh character (no saved section) reads off. The live mirror is
// Services.LogDiagnosticState; AppServices reads this section on profile load
// and writes it back when a Log-pane toggle flips.
public sealed class LogDiagnosticsSettings
{
    // Gate for the generation-gated Debug channel. Default off.
    public bool Debug { get; set; }

    // Gate for the generation-gated Combat channel. Default off.
    public bool Combat { get; set; }

    // Gate for the on-disk diagnostic files (program / memory / combat-trace
    // writers under Data/Logs). Default off. When on, the client generates all
    // three for the session.
    public bool AutoCollect { get; set; }
}
