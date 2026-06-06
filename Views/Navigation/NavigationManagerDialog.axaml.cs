using Avalonia.Controls;

namespace FujinTerm.Views.Navigation;

/// <summary>
/// Modeless dialog hosting saved Loops + Auto-Lair markers for
/// rename/delete/unmark actions. Surfaced from the NavigationWindow's
/// "Manage" chip in the action row.
/// </summary>
public partial class NavigationManagerDialog : Window
{
    public NavigationManagerDialog()
    {
        InitializeComponent();
    }
}
