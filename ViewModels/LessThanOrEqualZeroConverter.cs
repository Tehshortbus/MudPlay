using System.Globalization;
using Avalonia.Data.Converters;

namespace FujinTerm.ViewModels;

/// <summary>
/// Inverse of <see cref="GreaterThanZeroConverter"/> — returns
/// <c>true</c> when the bound numeric is at or below zero. Used to
/// show the PartyWindow's MA placeholder Border for rows where
/// <c>BaselineMp</c> is 0; together with the GreaterThanZero converter
/// on the real MA grid, exactly one of the two renders per row and
/// the column past MA stays aligned across rows.
/// </summary>
public sealed class LessThanOrEqualZeroConverter : IValueConverter
{
    public static LessThanOrEqualZeroConverter Instance { get; } = new();

    public object Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            int    i => i <= 0,
            long   l => l <= 0,
            double d => d <= 0,
            float  f => f <= 0,
            _        => true,
        };

    public object ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => throw new System.NotSupportedException();
}
