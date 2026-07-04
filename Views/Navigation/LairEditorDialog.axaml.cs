using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FujinTerm.ViewModels.Navigation;

namespace FujinTerm.Views.Navigation;

// Modeless editor for a saved Models.Profile.LairSetup. Spawned by
// NavigationViewModel.EditSetup or the Manage dialog's New Lair / Edit Lair buttons.
public partial class LairEditorDialog : Window
{
    public LairEditorDialog()
    {
        InitializeComponent();
    }

    // Enter on the add-room TextBox commits the highlighted (or top) dropdown row
    // via AddMarkerCommand. We mark the event handled so the dialog's Save button
    // (IsDefault="True") doesn't dismiss the window — the user wanted Enter to add
    // a row, not save.
    private void OnAddRoomKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is not LairEditorDialogViewModel vm) return;
        if (vm.AddMarkerCommand.CanExecute(null))
            vm.AddMarkerCommand.Execute(null);
        e.Handled = true;
    }

    // Click any dropdown row → commit it immediately. Sets the VM's
    // SelectedSearchResult before invoking Add so the command uses the clicked row.
    private void OnAddRoomResultClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: RoomSearchResult result }) return;
        if (DataContext is not LairEditorDialogViewModel vm) return;
        vm.SelectedSearchResult = result;
        if (vm.AddMarkerCommand.CanExecute(null))
            vm.AddMarkerCommand.Execute(null);
        e.Handled = true;
    }
}
