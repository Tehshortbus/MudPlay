using System.Windows.Input;

namespace MudPlay.ViewModels.GameData.Tables;

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

    // Optional Dismiss button next to Remove — a softer "decided, stop tracking" action
    // distinct from a hard Remove. Only the Unrecognized Lines tab uses it (sticky-dismiss
    // a candidate so its recurrences are ignored); every other section leaves it null.
    ICommand? DismissCommand => null;
    string? DismissLabel => null;

    // Optional secondary action, rendered as a button at the far right of the toolbar row
    // (opposite the Add / Remove group). Its label comes from ExportLabel. null when the
    // section offers no such action — the Incomplete Messages tab uses it for "Upload edits".
    ICommand? ExportCommand => null;
    string? ExportLabel => null;

    // Optional test-only action, rendered at the far right (left of Export). Unlike Export
    // its visibility follows the live ShowSimulate flag (refreshed via INotifyPropertyChanged
    // on the "ShowSimulate" property), so a diagnostic toggle can reveal/hide it while the
    // window is open. Only the Unrecognized Lines tab uses it — a "Simulate entry" button
    // gated by the Log pane's Simulate dropdown; every other section leaves it null/false.
    ICommand? SimulateCommand => null;
    string? SimulateLabel => null;
    bool ShowSimulate => false;
}
