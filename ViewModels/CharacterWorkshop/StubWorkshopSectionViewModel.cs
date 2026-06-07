using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// Placeholder section — renders a "wired in PR X" banner so the user
/// sees the full Workshop nav from day one even when individual
/// sections haven't shipped engines yet.
/// </summary>
public sealed class StubWorkshopSectionViewModel : WorkshopSectionViewModel
{
    private Control? _view;

    public override string Id { get; }
    public override string Title { get; }
    public string PhaseTag { get; }
    public string Description { get; }

    public StubWorkshopSectionViewModel(string id, string title, string phaseTag, string description)
    {
        Id = id;
        Title = title;
        PhaseTag = phaseTag;
        Description = description;
    }

    public override Control View => _view ??= BuildView();

    private Control BuildView()
    {
        StackPanel panel = new()
        {
            Margin = new Avalonia.Thickness(22),
            Spacing = 10,
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        panel.Children.Add(new TextBlock
        {
            Text = Title,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = PhaseTag,
            FontSize = 11,
            Opacity = 0.7,
        });
        panel.Children.Add(new TextBlock
        {
            Text = Description,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Opacity = 0.85,
        });
        return panel;
    }
}
