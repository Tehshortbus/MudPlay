using Avalonia.Controls;

namespace FujinTerm.Views.Navigation;

/// <summary>
/// Modeless picker shown when the .mp importer found multiple
/// candidate start rooms tied on closure score. See
/// <see cref="ViewModels.Navigation.MpAnchorPickerDialogViewModel"/>.
/// </summary>
public partial class MpAnchorPickerDialog : Window
{
    public MpAnchorPickerDialog()
    {
        InitializeComponent();
    }
}
