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
}
