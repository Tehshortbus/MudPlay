namespace FujinTerm.Game;

// Trust level the UI / automation can place in a RegenStat's estimate.
// Thresholds are empirical — pin them here so consumers (status-bar tinting,
// HealthManager projections) all share one definition.
public enum RegenConfidence
{
    // 0–2 observed samples — UI shows the seed value, dimmed.
    Low,

    // 3–9 observed samples — usable, no UI badge.
    Medium,

    // 10+ observed samples — trusted, full-opacity display.
    High,
}
