using System.Globalization;
using Avalonia.Data.Converters;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// XAML converter for the Show-password ToggleButton — feeds the right
/// <c>PasswordChar</c> into the TextBox: <c>'\0'</c> (no masking) when
/// the toggle is on, <c>'•'</c> when it's off.
/// </summary>
public sealed class PasswordCharConverter : IValueConverter
{
    public static readonly PasswordCharConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? '\0' : '•';

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
