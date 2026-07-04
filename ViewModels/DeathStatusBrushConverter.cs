using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels;

// XAML converter mapping a DeathRecoveryStatus to a stoplight brush for the
// Workshop DEATH grid: Active → red, Partial → amber, Recovered → green.
// Single static instance — no per-binding state.
public sealed class DeathStatusBrushConverter : IValueConverter
{
    public static DeathStatusBrushConverter Instance { get; } = new();

    private static readonly IBrush ActiveBrush =
        new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)); // bright red
    private static readonly IBrush PartialBrush =
        new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x57)); // bright amber
    private static readonly IBrush RecoveredBrush =
        new ImmutableSolidColorBrush(Color.FromRgb(0x6B, 0xD6, 0x8A)); // bright green

    public object Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => value is DeathRecoveryStatus s ? s switch
        {
            DeathRecoveryStatus.Recovered => RecoveredBrush,
            DeathRecoveryStatus.Partial   => PartialBrush,
            _                             => ActiveBrush,
        } : ActiveBrush;

    public object ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => throw new System.NotSupportedException();
}
