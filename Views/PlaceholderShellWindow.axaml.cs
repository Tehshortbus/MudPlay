using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FujinTerm.Views;

// Reusable placeholder window for panels not yet wired to real data. The
// skeleton's actual per-window layout (tabs, sections, lists, canvases)
// lands when that panel is implemented; until then the menu / toolbar
// entries already open something so the surface contract holds.
public partial class PlaceholderShellWindow : Window
{
    public PlaceholderShellWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Configure the placeholder. Called once before Show(owner).
    // panelName: short panel name shown in the header strip and as the window title.
    // phaseTag: identifier shown as an amber chip beside the name.
    // headline: one-line summary of what the panel will do once wired.
    // description: multi-line description of the panel's eventual contents.
    public void Configure(string panelName, string phaseTag, string headline, string description)
    {
        Title = panelName;
        this.FindControl<TextBlock>("PanelLabel")!.Text       = panelName;
        this.FindControl<TextBlock>("PhaseTagLabel")!.Text    = phaseTag;
        this.FindControl<TextBlock>("HeadlineLabel")!.Text    = headline;
        this.FindControl<TextBlock>("DescriptionLabel")!.Text = description;
    }
}
