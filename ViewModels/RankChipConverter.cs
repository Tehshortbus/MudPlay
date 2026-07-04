using System.Globalization;
using Avalonia.Data.Converters;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels;

// XAML converter that renders a PartyRank as the short one-letter chip label
// the PartyWindow draws on the self row (Front → "F", Mid → "M", Back →
// "B"). Single static instance — no per-binding state.
public sealed class RankChipConverter : IValueConverter
{
    public static RankChipConverter Instance { get; } = new();

    public object Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => value is PartyRank r ? r switch
        {
            PartyRank.Front => "F",
            PartyRank.Back  => "B",
            _               => "M",
        } : "M";

    public object ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => throw new System.NotSupportedException();
}
