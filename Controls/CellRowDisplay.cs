using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FujinTerm.Terminal;

namespace FujinTerm.Controls;

/// <summary>
/// Renders one <c>Cell[]</c> row (from <see cref="ScrollbackBuffer"/>) using
/// the same per-cell <see cref="DrawingContext.FillRectangle"/> + per-glyph
/// approach as the live <see cref="TerminalControl"/>. ANSI background
/// colours fill the full cell rectangle so coloured-space art (BBS
/// balloons / banners / box art) renders identically to the live canvas.
/// </summary>
/// <remarks>
/// <para>
/// Why this is a custom <see cref="Control"/> rather than a
/// <see cref="Avalonia.Controls.SelectableTextBlock"/> + <see cref="Documents.Run"/>
/// inlines: <c>Run.Background</c> only paints within the run's glyph
/// bounds. For a row of spaces with a coloured background, the painted
/// area is the x-height of the space glyph, which leaves the bottom of
/// each cell unpainted — and the balloon ends up as horizontal stripes
/// instead of a filled circle. <c>FillRectangle</c> per cell-run fixes
/// that.
/// </para>
/// <para>
/// Trade-off: this loses native text selection on the row. Export
/// (which uses <see cref="ViewModels.BackscrollRowViewModel.PlainText"/>)
/// and search continue to work since they read the raw <c>Cell[]</c>
/// directly, not the rendered text.
/// </para>
/// </remarks>
public sealed class CellRowDisplay : Control
{
    public static readonly StyledProperty<Cell[]?> RowProperty =
        AvaloniaProperty.Register<CellRowDisplay, Cell[]?>(nameof(Row));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<CellRowDisplay, FontFamily>(
            nameof(FontFamily),
            new FontFamily("avares://FujinTerm/Assets/Fonts/Mx437_IBM_VGA_8x16.ttf#Mx437 IBM VGA 8x16"));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<CellRowDisplay, double>(nameof(FontSize), 14.0);

    private Typeface _typeface;
    private double _cellW = 8;
    private double _cellH = 16;
    private bool _metricsValid;

    /// <summary>The cell row to render.</summary>
    public Cell[]? Row
    {
        get => GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    static CellRowDisplay()
    {
        AffectsRender<CellRowDisplay>(RowProperty, FontFamilyProperty, FontSizeProperty);
        AffectsMeasure<CellRowDisplay>(RowProperty, FontFamilyProperty, FontSizeProperty);
    }

    public CellRowDisplay()
    {
        _typeface = new Typeface(FontFamily);

        // Pixel-snap our bounds so the FillRectangles below land on integer
        // device pixels — without this, Avalonia anti-aliases the run-edges
        // and adjacent same-colour rows bleed into each other.
        UseLayoutRounding = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty)
        {
            _metricsValid = false;
        }
    }

    private void EnsureMetrics()
    {
        if (_metricsValid) return;
        _typeface = new Typeface(FontFamily);
        FormattedText probe = new("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typeface, FontSize, Brushes.White);
        _cellW = Math.Max(1, Math.Round(probe.WidthIncludingTrailingWhitespace));
        _cellH = Math.Max(1, Math.Round(probe.Height));
        _metricsValid = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureMetrics();
        int cols = Row?.Length ?? 80;
        return new Size(_cellW * cols, _cellH);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        Cell[]? cells = Row;
        if (cells is null || cells.Length == 0) return;

        EnsureMetrics();

        // Paint to the full allocated row height — Bounds.Height is what the
        // ListBox actually gave us, which may exceed _cellH if a sibling
        // control in the row template (the timestamp TextBlock) reports a
        // taller natural height. Without this, coloured-space art leaves a
        // few pixels of black between rows and BBS balloons / banners show
        // as horizontal bars instead of solid shapes. Rounded up so adjacent
        // rows meet exactly with no anti-aliased gap.
        double paintH = Bounds.Height > 0 ? Math.Ceiling(Bounds.Height) : _cellH;

        int i = 0;
        while (i < cells.Length)
        {
            CellAttributes attr = cells[i].Attr;
            int runStart = i;
            do { i++; } while (i < cells.Length && cells[i].Attr.Equals(attr));
            DrawRun(context, cells, runStart, i, attr, paintH);
        }
    }

    private void DrawRun(DrawingContext context, Cell[] cells, int x0, int x1, CellAttributes attr, double paintH)
    {
        bool reverse  = (attr.Flags & CellFlags.Reverse)  != 0;
        bool bold     = (attr.Flags & CellFlags.Bold)     != 0;
        bool concealed = (attr.Flags & CellFlags.Concealed) != 0;
        bool underline = (attr.Flags & CellFlags.Underline) != 0;

        TerminalColor fgColor = reverse ? attr.Background : attr.Foreground;
        TerminalColor bgColor = reverse ? attr.Foreground : attr.Background;

        uint fgArgb = reverse
            ? AnsiPalette.ResolveBackground(fgColor)
            : AnsiPalette.ResolveForeground(fgColor, bold);
        uint bgArgb = reverse
            ? AnsiPalette.ResolveForeground(bgColor, bold)
            : AnsiPalette.ResolveBackground(bgColor);

        IBrush fg = new SolidColorBrush(ToColor(fgArgb));
        double left = x0 * _cellW;
        double width = (x1 - x0) * _cellW;

        // Background — paint when non-default OR reverse video flipped it.
        // Skipping default backgrounds lets the row sit on the chrome bg
        // (which is the host ListBox's surface) — matches TerminalControl.
        if (bgArgb != AnsiPalette.DefaultBackgroundArgb)
        {
            IBrush bg = new SolidColorBrush(ToColor(bgArgb));
            context.FillRectangle(bg, new Rect(left, 0, width, paintH));
        }

        if (concealed) return;

        Typeface tf = bold ? new Typeface(FontFamily, FontStyle.Normal, FontWeight.Bold) : _typeface;
        for (int i = x0; i < x1; i++)
        {
            char ch = cells[i].Char;
            if (ch == ' ') continue;
            FormattedText ft = new(ch.ToString(), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, tf, FontSize, fg);
            context.DrawText(ft, new Point(i * _cellW, 0));
        }

        if (underline)
        {
            double y = paintH - 1;
            context.DrawLine(new Pen(fg, 1), new Point(left, y), new Point(left + width, y));
        }
    }

    private static Color ToColor(uint argb)
        => Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8)  & 0xFF),
            (byte)( argb        & 0xFF));
}
