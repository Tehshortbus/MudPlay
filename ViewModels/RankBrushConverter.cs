using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels;

/// <summary>
/// XAML converter that returns a saturated accent brush tinted to the
/// <see cref="PartyRank"/> value, so the PartyWindow's per-row rank
/// chip can render Front / Mid / Back with distinct colours at a
/// glance:
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>Front</b> — bright red (puts them in melee range).</item>
///   <item><b>Mid</b>   — bright amber (general baseline rank).</item>
///   <item><b>Back</b>  — bright cyan (casters / archers, back-of-line).</item>
/// </list>
/// <para>
/// Used as BOTH border and foreground on the rank chip (chip background
/// is the dark <c>ChromeBgElevBrush</c>, matching the StatusChip strip
/// on the same row). Bright accent on dark bg gives readable contrast
/// regardless of which rank renders — the earlier "white text on amber
/// fill" combination was unreadable.
/// </para>
/// Single static instance — no per-binding state.
/// </remarks>
public sealed class RankBrushConverter : IValueConverter
{
    public static RankBrushConverter Instance { get; } = new();

    // Pre-baked at type init so per-render conversions are cheap.
    // Colours chosen to be bright enough to read on the dark elevated
    // chip background while still landing in the conventional
    // melee=red / mid=amber / back=cyan palette.
    private static readonly IBrush FrontBrush =
        new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)); // bright red
    private static readonly IBrush MidBrush =
        new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x57)); // bright amber
    private static readonly IBrush BackBrush =
        new ImmutableSolidColorBrush(Color.FromRgb(0x5E, 0xC8, 0xF0)); // bright cyan

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
