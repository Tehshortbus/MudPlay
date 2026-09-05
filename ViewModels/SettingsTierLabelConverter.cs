using System.Globalization;
using Avalonia.Data.Converters;
using MudPlay.Services;

namespace MudPlay.ViewModels;

// Renders a SettingsTier as its long MegaMUD-parity picker label ("Installed
// defaults", "Only for this character", …) for the Game Data edit dialogs' Use
// combo box, so the dropdown reads in plain language instead of the enum name.
public sealed class SettingsTierLabelConverter : IValueConverter
{
    public static SettingsTierLabelConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SettingsTier tier ? tier.ToPickerLabel() : (value?.ToString() ?? string.Empty);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
