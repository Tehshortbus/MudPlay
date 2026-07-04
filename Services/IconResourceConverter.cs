using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace FujinTerm.Services;

// XAML value converter — looks up a string resource key (e.g. "IconPlug") in the
// application's resource dictionary and returns the matching StreamGeometry. Used
// by the dynamic toolbar and the Settings → Toolbar list editor so the icon data
// lives in a single place (Themes/Icons.axaml) while every row resolves its
// glyph by catalogue key.
public sealed class IconResourceConverter : IValueConverter
{
    public static IconResourceConverter Instance { get; } = new();

    public object? Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || string.IsNullOrEmpty(key)) return null;
        if (Application.Current is null) return null;
        return Application.Current.Resources.TryGetResource(key, null, out object? res) ? res : null;
    }

    public object? ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => throw new System.NotSupportedException();
}
