using Avalonia.Controls;
using FujinTerm.Views.GameData;

namespace FujinTerm.ViewModels.GameData;

// Stub section rendered where the real editor isn't wired yet. Carries the section's
// title + phase tag + description so the user sees the full sidebar and hovering each
// entry explains what it hosts.
public sealed class PlaceholderGameDataSectionViewModel : GameDataSectionViewModel
{
    private Control? _view;

    public override string Id { get; }
    public override string Title { get; }

    // Phase / PR tag shown on the placeholder body.
    public string PhaseTag { get; }

    // One-line summary of what the real editor will host.
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
