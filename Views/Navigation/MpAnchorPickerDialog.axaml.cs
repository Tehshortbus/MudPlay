using Avalonia.Controls;

namespace FujinTerm.Views.Navigation;

// Modeless picker shown when the .mp importer found multiple candidate start
// rooms tied on closure score. See
// ViewModels.Navigation.MpAnchorPickerDialogViewModel.
public partial class MpAnchorPickerDialog : Window
{
    public MpAnchorPickerDialog()
    {
        InitializeComponent();
    }
}
