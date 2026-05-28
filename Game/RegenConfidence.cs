namespace FujinTerm.Game;

/// <summary>
/// Trust level the UI / automation can place in a
/// <see cref="RegenStat"/>'s estimate. Thresholds are empirical — pin
/// them here so consumers (status-bar tinting, HealthManager projections)
/// all share one definition.
/// </summary>
public enum RegenConfidence
{
    /// <summary>0–2 observed samples — UI shows the seed value, dimmed.</summary>
    Low,

    /// <summary>3–9 observed samples — usable, no UI badge.</summary>
    Medium,

    /// <summary>10+ observed samples — trusted, full-opacity display.</summary>
    High,
}
