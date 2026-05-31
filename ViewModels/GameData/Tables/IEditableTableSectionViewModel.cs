using System.Windows.Input;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Implemented by section view-models that surface a per-row edit
/// dialog. The shared <c>GameDataTableSectionView</c> wires the
/// DataGrid's row double-tap to <see cref="OpenEditCommand"/>, passing
/// the bound <see cref="GameDataRow"/> as the parameter. Sections
/// without an editor (read-only tabs — Info, raw MDB browsing tabs
/// before edit dialogs land for them) simply don't implement this
/// interface and the double-tap is a no-op.
/// </summary>
public interface IEditableTableSectionViewModel
{
    /// <summary>The command fired when a row is double-tapped.</summary>
    ICommand? OpenEditCommand { get; }

    /// <summary>
    /// Optional Add button next to the search filter — opens the edit
    /// dialog for a fresh row. <c>null</c> when the section can't
    /// add rows (MDB-derived tables where the MDB is the source).
    /// </summary>
    ICommand? AddCommand => null;

    /// <summary>
    /// Optional Remove button next to the search filter — deletes the
    /// currently-selected row. <c>null</c> when the section can't
    /// remove rows. View binds <c>IsEnabled</c> to the selected-row
    /// presence so the button greys out before invocation.
    /// </summary>
    ICommand? RemoveCommand => null;
}
