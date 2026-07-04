using System.Collections.Generic;

namespace FujinTerm.Game.Calculators;

// Result of CharacterCalculator.AggregateEquipmentStats: the summed Totals plus
// a per-stat list of which items contributed what, so the Workshop can render
// both the bonus column and the hover-tooltip item breakdown from a single pass.
public sealed class EquipmentStatBreakdown
{
    // Summed bonuses across all contributing sources.
    public EquipmentStatSummary Totals { get; } = new();

    // Per-stat list of contributing items. Key = stat display name
    // (e.g. "Armour Class", "Dodge").
    public Dictionary<string, List<StatContribution>> PerStatSources { get; } = new();
}
