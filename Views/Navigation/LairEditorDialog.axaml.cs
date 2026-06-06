using Avalonia.Controls;

namespace FujinTerm.Views.Navigation;

/// <summary>
/// Modeless editor for a saved <see cref="Models.Profile.LairSetup"/>.
/// Hosted by <see cref="Services.DialogService"/>; surfaced from the
/// Navigation rail's per-setup right-click "Edit…" menu item + from
/// the Manager dialog's "New Lair…" button.
/// </summary>
public partial class LairEditorDialog : Window
{
    public LairEditorDialog()
    {
        InitializeComponent();
    }
}
