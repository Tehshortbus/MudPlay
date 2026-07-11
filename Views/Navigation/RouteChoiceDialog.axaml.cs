using Avalonia.Controls;

namespace FujinTerm.Views.Navigation;

// Modeless free-vs-direct route picker. Shown when a user-initiated walk found a
// shorter route through an acquirable gate. See
// ViewModels.Navigation.RouteChoiceDialogViewModel.
public partial class RouteChoiceDialog : Window
{
    public RouteChoiceDialog()
    {
        InitializeComponent();
    }
}
