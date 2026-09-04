using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace MudPlay.Controls;

// A two-thumb range slider over a fixed [Minimum, Maximum] axis, splitting the track
// into THREE coloured bands: Lower band [min → LowerValue], Middle [LowerValue →
// UpperValue], Upper [UpperValue → max]. The two draggable thumbs set the two
// boundaries at once, so all three colours live on one control — used by the route
// Details window to set the green / amber / red Hits-You-% thresholds. Values snap to
// whole units and the thumbs can't cross (a MinimumGap keeps every band non-empty).
// Generic + self-contained: the three band brushes are properties, so the colours are
// owned by the caller, not baked in.
public sealed class BandRangeSlider : Control
{
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<BandRangeSlider, double>(nameof(Minimum), 0d);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<BandRangeSlider, double>(nameof(Maximum), 100d);

    public static readonly StyledProperty<double> LowerValueProperty =
        AvaloniaProperty.Register<BandRangeSlider, double>(
            nameof(LowerValue), 15d, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> UpperValueProperty =
        AvaloniaProperty.Register<BandRangeSlider, double>(
            nameof(UpperValue), 45d, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    // Smallest span each band may shrink to — keeps the thumbs from crossing or
    // stacking so all three colours stay visible and orderable.
    public static readonly StyledProperty<double> MinimumGapProperty =
        AvaloniaProperty.Register<BandRangeSlider, double>(nameof(MinimumGap), 1d);

    public static readonly StyledProperty<IBrush> LowerBrushProperty =
        AvaloniaProperty.Register<BandRangeSlider, IBrush>(
            nameof(LowerBrush), new SolidColorBrush(Color.Parse("#5FB562")));

    public static readonly StyledProperty<IBrush> MiddleBrushProperty =
        AvaloniaProperty.Register<BandRangeSlider, IBrush>(
            nameof(MiddleBrush), new SolidColorBrush(Color.Parse("#D8B23A")));

    public static readonly StyledProperty<IBrush> UpperBrushProperty =
        AvaloniaProperty.Register<BandRangeSlider, IBrush>(
            nameof(UpperBrush), new SolidColorBrush(Color.Parse("#E06060")));

    public static readonly StyledProperty<IBrush> ThumbBrushProperty =
        AvaloniaProperty.Register<BandRangeSlider, IBrush>(
            nameof(ThumbBrush), new SolidColorBrush(Color.Parse("#F2F2F2")));

    public static readonly StyledProperty<IBrush> ThumbBorderBrushProperty =
        AvaloniaProperty.Register<BandRangeSlider, IBrush>(
            nameof(ThumbBorderBrush), new SolidColorBrush(Color.Parse("#20242B")));

    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double LowerValue { get => GetValue(LowerValueProperty); set => SetValue(LowerValueProperty, value); }
    public double UpperValue { get => GetValue(UpperValueProperty); set => SetValue(UpperValueProperty, value); }
    public double MinimumGap { get => GetValue(MinimumGapProperty); set => SetValue(MinimumGapProperty, value); }
    public IBrush LowerBrush { get => GetValue(LowerBrushProperty); set => SetValue(LowerBrushProperty, value); }
    public IBrush MiddleBrush { get => GetValue(MiddleBrushProperty); set => SetValue(MiddleBrushProperty, value); }
    public IBrush UpperBrush { get => GetValue(UpperBrushProperty); set => SetValue(UpperBrushProperty, value); }
    public IBrush ThumbBrush { get => GetValue(ThumbBrushProperty); set => SetValue(ThumbBrushProperty, value); }
    public IBrush ThumbBorderBrush { get => GetValue(ThumbBorderBrushProperty); set => SetValue(ThumbBorderBrushProperty, value); }

    private const double ThumbRadius = 7;
    private const double TrackHeight = 6;

    private enum Grip { None, Lower, Upper }
    private Grip _drag = Grip.None;

    static BandRangeSlider()
    {
        AffectsRender<BandRangeSlider>(
            MinimumProperty, MaximumProperty, LowerValueProperty, UpperValueProperty,
            LowerBrushProperty, MiddleBrushProperty, UpperBrushProperty,
            ThumbBrushProperty, ThumbBorderBrushProperty);
    }

    public BandRangeSlider() => Cursor = new Cursor(StandardCursorType.Hand);

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 240 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? 2 * ThumbRadius + 6 : availableSize.Height;
        return new Size(w, h);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        PointerPoint p = e.GetCurrentPoint(this);
        if (!p.Properties.IsLeftButtonPressed) return;

        // Grab the nearer thumb; when both sit on the same spot, the click side
        // decides so a stacked pair is still separable.
        double lx = ValueToX(LowerValue), ux = ValueToX(UpperValue);
        _drag = Math.Abs(p.Position.X - lx) <= Math.Abs(p.Position.X - ux) ? Grip.Lower : Grip.Upper;
        e.Pointer.Capture(this);
        MoveTo(p.Position.X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag == Grip.None) return;
        MoveTo(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Grip.None) return;
        _drag = Grip.None;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void MoveTo(double x)
    {
        double gap = Math.Max(0, MinimumGap);
        double v = Math.Round(XToValue(x));
        if (_drag == Grip.Lower)
            LowerValue = Math.Clamp(v, Minimum, Math.Max(Minimum, UpperValue - gap));
        else if (_drag == Grip.Upper)
            UpperValue = Math.Clamp(v, Math.Min(Maximum, LowerValue + gap), Maximum);
    }

    private double Span => Math.Max(1e-6, Maximum - Minimum);
    private double TrackLeft => ThumbRadius;
    private double TrackRight => Math.Max(ThumbRadius, Bounds.Width - ThumbRadius);
    private double TrackWidth => Math.Max(1e-6, TrackRight - TrackLeft);

    private double ValueToX(double v)
        => TrackLeft + (Math.Clamp(v, Minimum, Maximum) - Minimum) / Span * TrackWidth;

    private double XToValue(double x)
        => Minimum + Math.Clamp((x - TrackLeft) / TrackWidth, 0, 1) * Span;

    public override void Render(DrawingContext context)
    {
        // A transparent fill makes the whole control hit-testable, so a click
        // anywhere on the row grabs the nearer thumb (not only the 6px track).
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        double cy = Bounds.Height / 2;
        double top = cy - TrackHeight / 2;
        double lx = ValueToX(LowerValue), ux = ValueToX(UpperValue);

        // Lower + upper bands get rounded outer ends; the middle band is drawn last,
        // squared, so it covers the inner rounding for crisp band boundaries.
        context.DrawRectangle(LowerBrush, null,
            new RoundedRect(new Rect(TrackLeft, top, Math.Max(0, lx - TrackLeft), TrackHeight), 3));
        context.DrawRectangle(UpperBrush, null,
            new RoundedRect(new Rect(ux, top, Math.Max(0, TrackRight - ux), TrackHeight), 3));
        context.FillRectangle(MiddleBrush, new Rect(lx, top, Math.Max(0, ux - lx), TrackHeight));

        var pen = new Pen(ThumbBorderBrush, 1.5);
        context.DrawEllipse(ThumbBrush, pen, new Point(lx, cy), ThumbRadius, ThumbRadius);
        context.DrawEllipse(ThumbBrush, pen, new Point(ux, cy), ThumbRadius, ThumbRadius);
    }
}
