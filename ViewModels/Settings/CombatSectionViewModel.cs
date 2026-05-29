using Avalonia.Controls;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Combat" tab — bespoke layout (was a generic stub). Weapon Combat
/// table at the top, then Options, then a five-row Spell Combat table
/// whose mana column is governed by a Percentage / Value toggle (same
/// convention as the Health tab's HP / MA columns).
/// </summary>
public sealed class CombatSectionViewModel : SettingsSectionViewModel
{
    private Control? _view;

    public override string Id => "combat";
    public override string Title => "Combat";

    public string PhaseTag => "Phase 13 PR 13.A (CombatManager)";

    public string Description =>
        "Auto-attack engine config. Weapon Combat picks the items for each role; Options governs target gating " +
        "and bash / flee behaviour; Spell Combat picks the per-role damage / debuff spells. Min-mana thresholds " +
        "in Spell Combat respect the Percentage / Value toggle at the top of that section — same convention as " +
        "the Health tab.";

    public override Control View => _view ??= new CombatSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "Combat", "Weapon", "Normal weapon", "Alternate weapon",
        "BS weapon", "BS weapon off-hand", "Off-hand",
        "Do BS attacks", "Don't BS if multi-attack", "Run if BS fails",
        "Attack all monsters", "Polite attacks",
        "Min monsters", "Max monsters", "Run distance",
        "Multi-attack", "Debuff single target", "Debuff AOE",
        "Normal attack spell", "Alternate attack spell",
        "Min enemies", "Max consecutive casts", "Minimum mana per cast",
    };
}
