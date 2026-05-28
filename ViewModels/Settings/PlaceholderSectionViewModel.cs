using Avalonia.Controls;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// Stub section rendered until the owning phase's PR ships the real
/// editor. Carries the section's title + phase tag so the user sees
/// the full sidebar from day one ("Health — wired in Phase 4 PR 4.8").
/// </summary>
public sealed class PlaceholderSectionViewModel : SettingsSectionViewModel
{
    private Control? _view;

    public override string Id { get; }
    public override string Title { get; }

    /// <summary>Phase tag for the placeholder body ("Phase 4 PR 4.2", "Phase 12", etc.).</summary>
    public string PhaseTag { get; }

    /// <summary>One-line summary of what the real editor will host.</summary>
    public string Description { get; }

    public PlaceholderSectionViewModel(string id, string title, string phaseTag, string description)
    {
        Id = id;
        Title = title;
        PhaseTag = phaseTag;
        Description = description;
    }

    public override Control View => _view ??= new PlaceholderSectionView { DataContext = this };
}
