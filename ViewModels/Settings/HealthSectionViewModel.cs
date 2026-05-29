using Avalonia.Controls;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Health" tab — bespoke two-column layout (HP left, Mana / Kai right)
/// with a Percentage / Value mode picker per column, so the user can
/// express a threshold either way depending on what they're tuning
/// against. Wiring lands in Phase 13 PR 13.B (HealthManager) for the
/// rest / hang / run knobs and PR 13.D (CastingDirector) for the
/// heal-cast thresholds.
/// </summary>
public sealed class HealthSectionViewModel : SettingsSectionViewModel
{
    private Control? _view;

    public override string Id => "health";
    public override string Title => "Health";

    /// <summary>Phase tag shown under the tab header.</summary>
    public string PhaseTag => "Phase 13 PR 13.B (HealthManager) + 13.D (CastingDirector heal thresholds)";

    /// <summary>Description shown under the phase tag.</summary>
    public string Description =>
        "Two parallel columns — HP on the left, Mana / Kai on the right — each with its own Percentage / Value " +
        "mode picker so the user can specify thresholds either as a fraction of max or as an absolute number. " +
        "Rest / hang / run flow into HealthManager; heal-cast thresholds flow into CastingDirector.";

    public override Control View => _view ??= new HealthSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "Health", "HP", "Mana", "Kai",
        "Rest max", "Rest if below", "Heal rest", "Heal combat",
        "Run if below", "Hang up if below", "Bless if above",
        "Use meditate ability", "Meditate before resting",
        "Pre-rest", "Post-rest", "Pre-meditate", "Post-meditate",
    };
}
