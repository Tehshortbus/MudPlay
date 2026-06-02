using System.Globalization;
using Avalonia.Data.Converters;

namespace FujinTerm.ViewModels;

/// <summary>
/// XAML converter that returns <c>true</c> when the bound numeric is
/// strictly greater than zero. Used to hide the MA bar / numeric on the
/// PartyWindow's warrior rows where <c>BaselineMp</c> is 0 (no mana
/// pool), keeping the row compact.
/// </summary>
public sealed class GreaterThanZeroConverter : IValueConverter
{
    public static GreaterThanZeroConverter Instance { get; } = new();

    public object Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            int    i => i > 0,
            long   l => l > 0,
            double d => d > 0,
            float  f => f > 0,
            _        => false,
        };

    public object ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => throw new System.NotSupportedException();
}
