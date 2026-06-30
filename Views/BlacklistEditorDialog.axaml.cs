using Avalonia.Controls;
using FujinTerm.Models.Profile;
using FujinTerm.ViewModels;

namespace FujinTerm.Views;

public partial class BlacklistEditorDialog : Window
{
    public BlacklistEditorDialog()
    {
        InitializeComponent();

        // Sync the list's multi-selection into the VM's SelectedEntries so
        // Remove acts on every Ctrl-/Shift-highlighted row, not just the
        // keyboard-focused one. Avalonia exposes SelectedItems as a
        // non-bindable IList, so the sync is imperative.
        EntriesList.SelectionChanged += (_, _) =>
        {
            if (DataContext is not BlacklistEditorDialogViewModel vm) return;
            vm.SelectedEntries.Clear();
            if (EntriesList.SelectedItems is not { } selected) return;
            foreach (object? item in selected)
            {
                if (item is BlacklistedRoom room) vm.SelectedEntries.Add(room);
            }
        };
    }
}
