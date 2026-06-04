using Avalonia.Controls;

namespace FujinTerm.Views.Navigation;

/// <summary>
/// Modeless candidate picker for <see cref="ViewModels.Navigation.AmbiguousLocationDialogViewModel"/>.
/// Hosted by <see cref="Services.DialogService"/>; result-shape matches
/// the registered <c>OpenWindowAsync</c> contract.
/// </summary>
public partial class AmbiguousLocationDialog : Window
{
    public AmbiguousLocationDialog()
    {
        InitializeComponent();
    }
}
