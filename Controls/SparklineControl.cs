using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace FujinTerm.Controls;

// A minimal hand-drawn line chart for the Session Stats panel's kills/hour
// readout. Takes a numeric series via Samples (oldest → newest, left → right)
// and draws it as a single polyline auto-scaled to the control's bounds, with
// an optional translucent area fill underneath.
//
// Deliberately tiny and self-contained: no axes, labels, gridlines, or
// interaction — a sparkline, not a charting library. The series is normalised
// to its own min/max each render so it always fills the vertical space; a flat
// series pins to the centre line rather than dividing by zero. The data shape
// (a plain IReadOnlyList<double>) is generic on purpose — the window's VM owns
// "what's a kill rate"; this control only knows how to draw numbers. If the
// bound series implements INotifyCollectionChanged the control re-renders when
// it mutates, so a live ObservableCollection<double> animates as samples
// append; a re-assigned list is picked up via AffectsRender.
public sealed class SparklineControl : Control
{
    // The series to plot, oldest first. Fewer than two points draws nothing.
    public static readonly StyledProperty<IReadOnlyList<double>?> SamplesProperty =
        AvaloniaProperty.Register<SparklineControl, IReadOnlyList<double>?>(nameof(Samples));

    // Colour of the plotted line.
    public static readonly StyledProperty<IBrush> StrokeProperty =
        AvaloniaProperty.Register<SparklineControl, IBrush>(
            nameof(Stroke), new SolidColorBrush(Color.Parse("#7AB870")));

    // Line thickness, in device-independent pixels.
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<SparklineControl, double>(nameof(StrokeThickness), 1.5);

    // Optional brush for the area below the line. null (default) leaves the
    // area empty.
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<SparklineControl, IBrush?>(nameof(Fill));

    public IReadOnlyList<double>? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public IBrush Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    // The currently-subscribed series, so a live collection's mutations repaint.
    private INotifyCollectionChanged? _observed;

    static SparklineControl()
    {
        AffectsRender<SparklineControl>(StrokeProperty, StrokeThicknessProperty, FillProperty);
        SamplesProperty.Changed.AddClassHandler<SparklineControl>((c, e) =>
            c.OnSamplesChanged(e.NewValue as IReadOnlyList<double>));
    }

    private void OnSamplesChanged(IReadOnlyList<double>? newSamples)
    {
        if (_observed is not null)
        {
            _observed.CollectionChanged -= OnCollectionChanged;
            _observed = null;
        }
        if (newSamples is INotifyCollectionChanged incc)
        {
            incc.CollectionChanged += OnCollectionChanged;
            _observed = incc;
        }
        InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        IReadOnlyList<double>? samples = Samples;
        if (samples is null || samples.Count < 2 || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        // Inset top/bottom by the stroke radius so the line at the extremes
        // isn't clipped against the edge.
        double inset = Math.Max(StrokeThickness, 1.0);
        double plotH = Math.Max(bounds.Height - 2 * inset, 0);
        double plotW = bounds.Width;

        double min = samples[0], max = samples[0];
        for (int i = 1; i < samples.Count; i++)
        {
            double v = samples[i];
            if (v < min) min = v;
            if (v > max) max = v;
        }
        double range = max - min;

        // A flat series (range == 0) has no spread to normalise against, so it
        // sits on the centre line instead of dividing by zero.
        Point Project(int i, double v)
        {
            double x = plotW * i / (samples.Count - 1);
            double norm = range > 0 ? (v - min) / range : 0.5;
            double y = inset + (1 - norm) * plotH;
            return new Point(x, y);
        }

        if (Fill is { } fill)
        {
            StreamGeometry area = new();
            using (StreamGeometryContext g = area.Open())
            {
                g.BeginFigure(new Point(0, bounds.Height), isFilled: true);
                for (int i = 0; i < samples.Count; i++) g.LineTo(Project(i, samples[i]));
                g.LineTo(new Point(plotW, bounds.Height));
                g.EndFigure(true);
            }
            context.DrawGeometry(fill, null, area);
        }

        StreamGeometry line = new();
        using (StreamGeometryContext g = line.Open())
        {
            g.BeginFigure(Project(0, samples[0]), isFilled: false);
            for (int i = 1; i < samples.Count; i++) g.LineTo(Project(i, samples[i]));
            g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(Stroke, StrokeThickness)
        {
            LineJoin = PenLineJoin.Round,
            LineCap = PenLineCap.Round,
        }, line);
    }
}
