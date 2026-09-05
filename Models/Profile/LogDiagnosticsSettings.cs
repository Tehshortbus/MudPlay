namespace MudPlay.Models.Profile;

// Per-character log-diagnostic switches. Persisted as the "LogDiagnostics"
// entry in CharacterProfile.Settings.
//
// Char-tier (not Global) — different characters can carry different diagnostic
// states. Debug + Combat tracing default ON so a fresh character's Program Log
// already carries the decision-trail a bug report needs (the common case was a
// capture with both off and nothing to diagnose). On-disk log generation and
// hop-timing stay off — those are heavier, opt-in troubleshooting affordances.
// The live mirror is Services.LogDiagnosticState; AppServices reads this section
// on profile load and writes it back when a Log-pane toggle flips.
public sealed class LogDiagnosticsSettings
{
    // Gate for the generation-gated Debug channel. Default on.
    public bool Debug { get; set; } = true;

    // Gate for the generation-gated Combat channel. Default on.
    public bool Combat { get; set; } = true;

    // Gate for the on-disk diagnostic files (program / memory / combat-trace
    // writers under Data/Logs). Default off. When on, the client generates all
    // three for the session.
    public bool AutoCollect { get; set; }

    // Gate for the navigation hop-timing calibration trace. Default off. When
    // on, HopTimingCalibrator emits one Info line per confirmed room hop.
    public bool HopTiming { get; set; }

    // Gate for Game.MessageCandidateWatcher. Default on — the point of the
    // feature is catching silent Messages-catalogue misses, matching Debug/
    // Combat's "default to visibility" rationale.
    public bool CaptureUnrecognizedMessages { get; set; } = true;
}
