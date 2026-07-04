using Avalonia.Controls;
using Avalonia.Input;
using FujinTerm.ViewModels;
using FujinTerm.ViewModels.Navigation;

namespace FujinTerm.Views;

// Modeless "Center on…" picker. Reuses the room-search behaviour
// from the Navigation rail's main search box — coordinate input
// OR substring match, live results dropdown, Enter on the textbox
// commits the highlighted (or top) row without dismissing the
// dialog through the default Center button.
public partial class ManualCenterDialog : Window
{
    public ManualCenterDialog()
    {
        InitializeComponent();
        Opened += (_, _) => QueryBox.Focus();
    }

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is not ManualCenterDialogViewModel vm) return;
        if (!vm.CommitCommand.CanExecute(null)) return;
        vm.CommitCommand.Execute(null);
        e.Handled = true;
    }

    private void OnResultClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: RoomSearchResult row }) return;
        if (DataContext is not ManualCenterDialogViewModel vm) return;
        vm.SelectedSearchResult = row;
        if (vm.CommitCommand.CanExecute(null))
            vm.CommitCommand.Execute(null);
        e.Handled = true;
    }
}
