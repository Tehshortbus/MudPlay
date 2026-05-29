using Avalonia.Controls;
using FujinTerm.Views.GameData;

namespace FujinTerm.ViewModels.GameData;

/// <summary>
/// Stub section rendered until the owning phase's PR ships the real
/// editor. Carries the section's title + phase tag + description so the
/// user sees the full sidebar from day one and hovering each entry
/// makes it clear when it lands.
/// </summary>
public sealed class PlaceholderGameDataSectionViewModel : GameDataSectionViewModel
{
    private Control? _view;

    public override string Id { get; }
    public override string Title { get; }

    /// <summary>Phase / PR tag shown on the placeholder body.</summary>
    public string PhaseTag { get; }

    /// <summary>One-line summary of what the real editor will host.</summary>
    public string Description { get; }

    public PlaceholderGameDataSectionViewModel(string id, string title, string phaseTag, string description)
    {
        Id = id;
        Title = title;
        PhaseTag = phaseTag;
        Description = description;
    }

    public override Control View => _view ??= new PlaceholderGameDataSectionView { DataContext = this };
}
