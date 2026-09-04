using Avalonia.Controls;

namespace MudPlay.Views.Navigation;

// Modeless, read-only browse window for the current route's full step plan (with
// each lair room's monsters as record links). See
// ViewModels.Navigation.RouteDetailsDialogViewModel.
public partial class RouteDetailsDialog : Window
{
    public RouteDetailsDialog()
    {
        InitializeComponent();
    }
}
