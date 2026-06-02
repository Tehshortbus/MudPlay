using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels;

/// <summary>
/// XAML converter that returns a brush tinted to the
/// <see cref="PartyRank"/> value, so the PartyWindow's per-row rank
/// chip can render Front / Mid / Back with distinct colours at a
/// glance:
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>Front</b> — accent red (puts them in melee range).</item>
///   <item><b>Mid</b>   — accent amber (general baseline rank).</item>
///   <item><b>Back</b>  — accent cyan (casters / archers, back-of-line).</item>
/// </list>
/// Single static instance — no per-binding state.
/// </remarks>
public sealed class RankBrushConverter : IValueConverter
{
    public static RankBrushConverter Instance { get; } = new();

    // Pre-baked at type init so per-render conversions are cheap.
    private static readonly IBrush FrontBrush =
        new ImmutableSolidColorBrush(Color.FromRgb(0xE0, 0x6D, 0x6D)); // accent red soft
    private static readonly IBrush MidBrush =
        new ImmutableSolidColorBrush(Color.FromRgb(0xE0, 0xB0, 0x4E)); // accent amber soft
    private static readonly IBrush BackBrush =
        new ImmutableSolidColorBrush(Color.FromRgb(0x4E, 0xB0, 0xE0)); // accent cyan soft

    public object Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => value is PartyRank r ? r switch
        {
            PartyRank.Front => FrontBrush,
            PartyRank.Back  => BackBrush,
            _               => MidBrush,
        } : MidBrush;

    public object ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => throw new System.NotSupportedException();
}
