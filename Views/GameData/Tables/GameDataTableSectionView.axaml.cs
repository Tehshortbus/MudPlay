using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using FujinTerm.ViewModels.GameData.Tables;

namespace FujinTerm.Views.GameData.Tables;

/// <summary>
/// Code-behind for <see cref="GameDataTableSectionView"/>. Populates the
/// <see cref="DataGrid"/>'s columns from the bound view-model's
/// <see cref="GameDataTableSectionViewModel.Columns"/> list — each VM
/// supplies a different ordered list, so the columns can't be authored
/// in XAML and must be rebuilt when the DataContext changes.
/// </summary>
public partial class GameDataTableSectionView : UserControl
{
    public GameDataTableSectionView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => RebuildColumns();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void RebuildColumns()
    {
        RowsGrid.Columns.Clear();
        if (DataContext is not GameDataTableSectionViewModel vm) return;

        int index = 0;
        foreach (string column in vm.Columns)
        {
            // Bind each column to its positional cell on the row —
            // GameDataRow.Cells is ordered to match Columns, so the
            // indexer round-trip is stable.
            RowsGrid.Columns.Add(new DataGridTextColumn
            {
                Header  = column,
                Binding = new Binding($"Cells[{index}].Value"),
                Width   = DataGridLength.Auto,
            });
            index++;
        }
    }
}
