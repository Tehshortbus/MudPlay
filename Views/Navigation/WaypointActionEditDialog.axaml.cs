using Avalonia.Controls;

namespace FujinTerm.Views.Navigation;

/// <summary>
/// Modeless per-row action editor spawned by the Loop editor's ✎
/// button. Lets the user attach a free-form command + delay to a
/// single waypoint without leaving the editor.
/// </summary>
public partial class WaypointActionEditDialog : Window
{
    public WaypointActionEditDialog()
    {
        InitializeComponent();
    }
}
