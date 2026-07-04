using Avalonia.Controls;
using Avalonia.Input;
using FujinTerm.ViewModels.Settings;

namespace FujinTerm.Views.Settings;

public sealed partial class EventsSectionView : UserControl
{
    public EventsSectionView() => InitializeComponent();

    // Double-click a row → open the edit dialog. Same pattern as the
    // other table-style picker views (ProfilePicker, etc.). The
    // command also drives the toolbar button so this is purely a UX
    // shortcut, not a separate flow.
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is EventsSectionViewModel vm
            && vm.ModifyCommand.CanExecute(null))
        {
            vm.ModifyCommand.Execute(null);
        }
    }
}
