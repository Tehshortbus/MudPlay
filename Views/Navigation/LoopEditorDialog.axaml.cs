using Avalonia.Controls;

namespace FujinTerm.Views.Navigation;

/// <summary>
/// Modeless edit dialog for an existing
/// <see cref="Game.Map.Loop"/>. Hosted by
/// <see cref="Services.DialogService"/>; surfaced from the Navigation
/// pane's per-loop right-click "Edit…" menu item.
/// </summary>
public partial class LoopEditorDialog : Window
{
    public LoopEditorDialog()
    {
        InitializeComponent();
    }
}
