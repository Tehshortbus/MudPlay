using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace FujinTerm.ViewModels;

/// <summary>
/// Maps <see cref="BackscrollRowViewModel.IsFindMatch"/> to a row background
/// brush — translucent yellow tint when the row is the current "Find next"
/// hit, transparent otherwise. Used by the row template so the highlight
/// is visually distinct from any text selection the user has drawn.
/// </summary>
public sealed class FindMatchBackgroundConverter : IValueConverter
{
    public static readonly FindMatchBackgroundConverter Instance = new();

    // Soft yellow at ~28% alpha — readable across both white-on-black and
    // ANSI-coloured rows without blowing out the underlying text colours.
    private static readonly IBrush MatchBrush =
        new SolidColorBrush(Color.FromArgb(0x48, 0xFF, 0xE5, 0x4F));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? MatchBrush : Brushes.Transparent;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
