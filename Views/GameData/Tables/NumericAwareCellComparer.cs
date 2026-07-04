using System.Collections;
using System.Globalization;
using FujinTerm.ViewModels.GameData.Tables;

namespace FujinTerm.Views.GameData.Tables;

// Per-column IComparer for the Game Data Browser DataGrid. Avalonia's
// DataGridColumn.CustomSortComparer receives full row items (GameDataRow) — this
// comparer pulls one column's cell value from each row and compares them
// numerically when both parse as numbers, else falls back to a case-insensitive
// string compare. Without this every column sorts lexicographically — clicking
// EXP would order rows as 0, 1, 10, 100, 11, 12, ….
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

        // Leading-number fallback: cells that are numeric-with-a-suffix
        // ("2hp@90s") or a numeric list ("10/25/5") still sort by their
        // leading value rather than lexically (so "10…" doesn't sort
        // before "2…"). Only kicks in when both sides start with a digit;
        // purely-text cells ("Lawful Good") fall through to string compare.
        if (TryLeadingNumber(a, out double la) && TryLeadingNumber(b, out double lb))
        {
            int cmp = la.CompareTo(lb);
            if (cmp != 0) return cmp;
        }

        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    // Parse the leading run of digits (optionally signed) from a cell value.
    private static bool TryLeadingNumber(string? s, out double value)
    {
        value = 0;
        if (string.IsNullOrEmpty(s)) return false;
        int i = 0;
        if (s[0] is '-' or '+') i++;
        int start = i;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        if (i == start) return false;                 // no digits after the optional sign
        return double.TryParse(s.AsSpan(0, i), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private string? ExtractCell(object? rowObject)
    {
        if (rowObject is not GameDataRow row) return null;
        if (_columnIndex < 0 || _columnIndex >= row.Cells.Count) return null;
        return row.Cells[_columnIndex].Value;
    }
}
