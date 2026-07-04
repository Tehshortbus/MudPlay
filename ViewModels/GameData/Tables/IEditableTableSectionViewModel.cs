using System.Windows.Input;

namespace FujinTerm.ViewModels.GameData.Tables;

// Implemented by section view-models that surface a per-row edit dialog. The shared
// GameDataTableSectionView wires the DataGrid's row double-tap to OpenEditCommand, passing the
// bound GameDataRow as the parameter. Sections without an editor (read-only tabs — Info, raw
// MDB browsing tabs before edit dialogs land for them) simply don't implement this interface
// and the double-tap is a no-op.
public interface IEditableTableSectionViewModel
{
    // The command fired when a row is double-tapped.
    ICommand? OpenEditCommand { get; }

    // Optional Add button next to the search filter — opens the edit dialog for a fresh row.
    // null when the section can't add rows (MDB-derived tables where the MDB is the source).
    ICommand? AddCommand => null;

    // Optional Remove button next to the search filter — deletes the currently-selected row.
    // null when the section can't remove rows. View binds IsEnabled to the selected-row
    // presence so the button greys out before invocation.
    ICommand? RemoveCommand => null;
}
