using System.Globalization;
using Avalonia.Data.Converters;

namespace FujinTerm.ViewModels.Settings;

// XAML converter for the Show-password ToggleButton — feeds the right
// PasswordChar into the TextBox: '\0' (no masking) when the toggle is on,
// '•' when it's off.
public sealed class PasswordCharConverter : IValueConverter
{
    public static readonly PasswordCharConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? '\0' : '•';

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
