using Avalonia.Controls;
using Avalonia.Data;
using FujinTerm.ViewModels.GameData.Tables;

namespace FujinTerm.Views.GameData.Tables;

/// <summary>
/// Code-behind for <see cref="GameDataTableSectionView"/>. Populates the
/// <see cref="DataGrid"/>'s columns from the bound view-model's
/// <see cref="GameDataTableSectionViewModel.Columns"/> list — each VM
/// supplies a different ordered list, so the columns can't be authored
/// in XAML and must be rebuilt when the DataContext is wired up.
/// </summary>
/// <remarks>
/// No hand-written <c>InitializeComponent</c> here: the Avalonia name
/// generator owns that method (per <c>AvaloniaNameGeneratorBehavior =
/// InitializeComponent</c>) so the <c>x:Name="RowsGrid"</c> field gets
/// populated. Overriding it manually short-circuits the generator and
/// leaves x:Name fields null — which is how this view first shipped
/// and crashed every section open with an NRE on <c>RowsGrid.Columns</c>.
/// </remarks>
public partial class GameDataTableSectionView : UserControl
{
    private bool _columnsBuilt;

    public GameDataTableSectionView()
    {
        InitializeComponent();
        // Either trigger can fire first depending on layout timing;
        // guard via _columnsBuilt so the second is a no-op.
        DataContextChanged   += (_, _) => { TryBuildColumns(); WireAddRemoveButtons(); };
        AttachedToVisualTree += (_, _) => { TryBuildColumns(); WireAddRemoveButtons(); };

        // Double-click any row → invoke the section's OpenEditCommand
        // with the row as the argument. Sections that don't expose an
        // editor (e.g. read-only Info tab) leave the command null and
        // the double-click is a no-op.
        RowsGrid.DoubleTapped += (_, _) =>
        {
            if (DataContext is IEditableTableSectionViewModel editable
                && RowsGrid.SelectedItem is GameDataRow row
                && editable.OpenEditCommand is { } cmd
                && cmd.CanExecute(row))
            {
                cmd.Execute(row);
            }
        };
    }

    /// <summary>
    /// Conditionally surface the Add / Remove buttons next to the
    /// search filter when the section exposes those commands. Sections
    /// that don't (MDB-derived read-only tabs) leave both null and
    /// the buttons stay hidden. Command wired imperatively rather
    /// than via XAML binding because <see cref="IEditableTableSectionViewModel"/>'s
    /// optional members aren't surfaced by compiled bindings.
    /// </summary>
    private void WireAddRemoveButtons()
    {
        if (DataContext is not IEditableTableSectionViewModel editable) return;

        if (editable.AddCommand is { } add)
        {
            AddButton.Command   = add;
            AddButton.IsVisible = true;
        }
        if (editable.RemoveCommand is { } remove)
        {
            RemoveButton.Command   = remove;
            RemoveButton.IsVisible = true;
        }
    }

    private void TryBuildColumns()
    {
        if (_columnsBuilt) return;
        if (DataContext is not GameDataTableSectionViewModel vm) return;

        RowsGrid.Columns.Clear();
        int index = 0;
        foreach (string column in vm.Columns)
        {
            // Bind each data column to its positional cell on the row —
            // GameDataRow.Cells is ordered to match Columns, so the
            // indexer round-trip is stable. CustomSortComparer handles
            // numeric columns properly (cell values are strings, so the
            // DataGrid's default sort would treat EXP as
            // "0, 1, 10, 100, 11, 2…").
            RowsGrid.Columns.Add(new DataGridTextColumn
            {
                Header             = column,
                Binding            = new Binding($"Cells[{index}].Value"),
                Width              = DataGridLength.Auto,
                CustomSortComparer = new NumericAwareCellComparer(index),
            });
            index++;
        }
        // Trailing virtual "Use" column — shows which tier (Def / Glob /
        // BBS / Char) owns the row's current values. Bound to the
        // GameDataRow.UseLabel computed property rather than a cell.
        // Skipped for engine-backed sections (Macros / Triggers / Aliases
        // / Players) where every row lives at one tier and the badge
        // would always read the same.
        if (vm.ShowUseColumn)
        {
            RowsGrid.Columns.Add(new DataGridTextColumn
            {
                Header  = GameDataTableSectionViewModel.UseColumnName,
                Binding = new Binding(nameof(GameDataRow.UseLabel)),
                Width   = DataGridLength.Auto,
            });
        }
        _columnsBuilt = true;
    }
}
