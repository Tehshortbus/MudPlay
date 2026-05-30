using System.Collections;
using System.Globalization;
using FujinTerm.ViewModels.GameData.Tables;

namespace FujinTerm.Views.GameData.Tables;

/// <summary>
/// Per-column <see cref="IComparer"/> for the Game Data Browser
/// <c>DataGrid</c>. Avalonia's <c>DataGridColumn.CustomSortComparer</c>
/// receives full row items (<see cref="GameDataRow"/>) — this comparer
/// pulls one column's cell value from each row and compares them
/// numerically when both parse as numbers, else falls back to a
/// case-insensitive string compare. Without this every column sorts
/// lexicographically — clicking EXP would order rows as
/// <c>0, 1, 10, 100, 11, 12, …</c>.
/// </summary>
internal sealed class NumericAwareCellComparer : IComparer
{
    private readonly int _columnIndex;

    public NumericAwareCellComparer(int columnIndex)
    {
        _columnIndex = columnIndex;
    }

    public int Compare(object? x, object? y)
    {
        string? a = ExtractCell(x);
        string? b = ExtractCell(y);

        // Treat null / empty as "less than" populated values so blank
        // cells cluster at the top on ascending sort.
        bool aEmpty = string.IsNullOrEmpty(a);
        bool bEmpty = string.IsNullOrEmpty(b);
        if (aEmpty && bEmpty) return 0;
        if (aEmpty) return -1;
        if (bEmpty) return 1;

        // Numeric-aware: if both parse as numbers, compare numerically.
        // InvariantCulture so "1,000" / "1.5" aren't culture-dependent.
        if (double.TryParse(a, NumberStyles.Any, CultureInfo.InvariantCulture, out double na) &&
            double.TryParse(b, NumberStyles.Any, CultureInfo.InvariantCulture, out double nb))
        {
            return na.CompareTo(nb);
        }

        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private string? ExtractCell(object? rowObject)
    {
        if (rowObject is not GameDataRow row) return null;
        if (_columnIndex < 0 || _columnIndex >= row.Cells.Count) return null;
        return row.Cells[_columnIndex].Value;
    }
}
