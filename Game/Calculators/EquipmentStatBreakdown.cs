using System.Collections.Generic;

namespace FujinTerm.Game.Calculators;

/// <summary>
/// Result of <see cref="CharacterCalculator.AggregateEquipmentStats"/>: the
/// summed <see cref="Totals"/> plus a per-stat list of which items contributed
/// what, so the Workshop can render both the bonus column and the hover-tooltip
/// item breakdown from a single pass.
/// </summary>
public sealed class EquipmentStatBreakdown
{
    /// <summary>Summed bonuses across all contributing sources.</summary>
    public EquipmentStatSummary Totals { get; } = new();

    /// <summary>
    /// Per-stat list of contributing items. Key = stat display name
    /// (e.g. <c>"Armour Class"</c>, <c>"Dodge"</c>).
    /// </summary>
    public Dictionary<string, List<StatContribution>> PerStatSources { get; } = new();
}
