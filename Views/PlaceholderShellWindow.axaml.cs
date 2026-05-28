using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FujinTerm.Views;

/// <summary>
/// Reusable placeholder window used by every later-phase panel's first
/// appearance in Phase 1 PR 1.1. The skeleton's actual per-window layout
/// (tabs, sections, lists, canvases) lands when the owning phase wires
/// real data; until then the menu / toolbar entries already open
/// <i>something</i> so the surface contract holds.
/// </summary>
public partial class PlaceholderShellWindow : Window
{
    public PlaceholderShellWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Configure the placeholder. Called once before <c>Show(owner)</c>.
    /// </summary>
    /// <param name="panelName">Short panel name shown in the header strip and as the window title.</param>
    /// <param name="phaseTag">Phase identifier shown as an amber chip beside the name (e.g. <c>"Phase 1 · PR 1.3"</c>).</param>
    /// <param name="headline">One-line summary of what the panel will do once wired.</param>
    /// <param name="description">Multi-line description of the panel's eventual contents.</param>
    public void Configure(string panelName, string phaseTag, string headline, string description)
    {
        Title = panelName;
        this.FindControl<TextBlock>("PanelLabel")!.Text       = panelName;
        this.FindControl<TextBlock>("PhaseTagLabel")!.Text    = phaseTag;
        this.FindControl<TextBlock>("HeadlineLabel")!.Text    = headline;
        this.FindControl<TextBlock>("DescriptionLabel")!.Text = description;
    }
}
