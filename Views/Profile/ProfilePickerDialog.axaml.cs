using Avalonia.Controls;
using Avalonia.Input;
using FujinTerm.ViewModels.Profile;

namespace FujinTerm.Views.Profile;

public partial class ProfilePickerDialog : Window
{
    public ProfilePickerDialog()
    {
        InitializeComponent();
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ProfilePickerDialogViewModel vm && vm.HasSelection)
            vm.OpenCommand.Execute(null);
    }
}
