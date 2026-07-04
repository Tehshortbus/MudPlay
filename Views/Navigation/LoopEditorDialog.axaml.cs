using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FujinTerm.ViewModels.Navigation;

namespace FujinTerm.Views.Navigation;

// Modeless edit dialog for an existing Game.Map.Loop. Hosted by
// Services.DialogService; surfaced from the Navigation pane's per-loop
// right-click "Edit…" menu item.
public partial class LoopEditorDialog : Window
{
    public LoopEditorDialog()
    {
        InitializeComponent();
    }

    // Enter while focus is on the add-room TextBox commits the highlighted (or
    // top) search result via AddWaypointCommand. We set e.Handled = true so the
    // dialog's Save button (IsDefault="True") doesn't grab the keypress and
    // dismiss the window — the user wanted Enter to add a row, not save.
    private void OnAddRoomKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is not LoopEditorDialogViewModel vm) return;
        if (vm.AddWaypointCommand.CanExecute(null))
            vm.AddWaypointCommand.Execute(null);
        e.Handled = true;
    }

    // Click any result row in the dropdown → commit it immediately. The
    // PointerPressed handler sets the VM's SelectedSearchResult before invoking
    // Add so the command uses the clicked row, not whatever the ListBox
    // highlighted last.
    private void OnAddRoomResultClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: RoomSearchResult result }) return;
        if (DataContext is not LoopEditorDialogViewModel vm) return;
        vm.SelectedSearchResult = result;
        if (vm.AddWaypointCommand.CanExecute(null))
            vm.AddWaypointCommand.Execute(null);
        e.Handled = true;
    }
}
