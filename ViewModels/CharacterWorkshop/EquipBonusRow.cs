namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// One aggregated equipment-bonus row in the Character Info section's Equipment
/// Bonuses box: a stat label, its net bonus value, and a newline-joined tooltip
/// listing which worn items contributed what (empty when there's nothing to
/// break down).
/// </summary>
/// <param name="Stat">Display label, e.g. <c>"Armour Class"</c>.</param>
/// <param name="Value">Net bonus string, e.g. <c>"+15"</c> or <c>"5.0"</c>.</param>
/// <param name="Tooltip">
/// Per-item contribution lines joined by newlines; <c>null</c> when there's only
/// one trivial source worth no hover detail.
/// </param>
public sealed record EquipBonusRow(string Stat, string Value, string? Tooltip)
{
    /// <summary>
    /// True when the net bonus is a penalty. <see cref="Value"/> is always
    /// signed (<c>"+0;-0"</c> / <c>"+0.#;-0.#"</c>) and zero rows are filtered
    /// out, so a leading minus is an unambiguous negative — it drives the red
    /// vs. neutral coloring in the Equipment Bonuses list.
    /// </summary>
    public bool IsNegative => Value.StartsWith('-');
}
