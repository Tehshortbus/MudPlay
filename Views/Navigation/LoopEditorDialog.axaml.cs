using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FujinTerm.ViewModels.Navigation;

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

    /// <summary>
    /// Enter while focus is on the add-room TextBox commits the
    /// highlighted (or top) search result via
    /// <see cref="LoopEditorDialogViewModel.AddWaypointCommand"/>.
    /// We <c>e.Handled = true</c> so the dialog's Save button
    /// (<c>IsDefault="True"</c>) doesn't grab the keypress and
    /// dismiss the window — the user wanted Enter to add a row,
    /// not save.
    /// </summary>
    private void OnAddRoomKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is not LoopEditorDialogViewModel vm) return;
        if (vm.AddWaypointCommand.CanExecute(null))
            vm.AddWaypointCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>
    /// Click any result row in the dropdown → commit it immediately.
    /// The PointerPressed handler sets the VM's
    /// <see cref="LoopEditorDialogViewModel.SelectedSearchResult"/>
    /// before invoking Add so the command uses the clicked row, not
    /// whatever the ListBox highlighted last.
    /// </summary>
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
