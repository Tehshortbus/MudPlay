using System.Globalization;
using Avalonia.Data.Converters;

namespace FujinTerm.ViewModels.Settings;

// XAML converter that returns true when the bound StubFieldKind equals the
// instance's expected kind. One static instance per kind so the view can switch a
// row's visible control without a verbose multi-template selector.
public sealed class KindEqualsConverter : IValueConverter
{
    private readonly StubFieldKind _expected;
    private KindEqualsConverter(StubFieldKind expected) => _expected = expected;

    public static KindEqualsConverter Check        { get; } = new(StubFieldKind.Check);
    public static KindEqualsConverter Numeric      { get; } = new(StubFieldKind.Numeric);
    public static KindEqualsConverter Combo        { get; } = new(StubFieldKind.Combo);
    public static KindEqualsConverter AutoComplete { get; } = new(StubFieldKind.AutoComplete);
    public static KindEqualsConverter Text         { get; } = new(StubFieldKind.Text);
    public static KindEqualsConverter Slider       { get; } = new(StubFieldKind.Slider);
    public static KindEqualsConverter Button       { get; } = new(StubFieldKind.Button);
    public static KindEqualsConverter Note         { get; } = new(StubFieldKind.Note);

    public object Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => value is StubFieldKind k && k == _expected;

    public object ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => throw new System.NotSupportedException();
}
