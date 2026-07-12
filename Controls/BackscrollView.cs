using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using FujinTerm.Terminal;

namespace FujinTerm.Controls;

// Virtualized, canvas-drawn transcript for the Backscroll window. Replaces the
// single SelectableTextBlock that rendered every captured row (up to the full
// ~4000-row ring) at once: that block recomputed hit-test + highlight geometry
// across all rows on each pointer move, so drag-selecting was O(rows) per move
// and effectively unusable on a deep history. This control draws only the rows
// currently in view (O(viewport)) and owns its own (row, col) selection model,
// so selecting and copying stay flat no matter how deep the scrollback runs.
//
// Rendering mirrors TerminalControl: the same aliased CP437 cell grid, the same
// per-run background fill + per-cell glyph draw, the same AnsiPalette colour
// resolution — so history looks identical to the live screen. Scrolling is
// handed to the host ScrollViewer through ILogicalScrollable, which keeps only
// the visible slice realized (the control reports Extent/Viewport/Offset and
// interprets the offset itself rather than being translated as one tall bitmap).
public sealed class BackscrollView : Control, ILogicalScrollable
{
    // Timestamp gutter: "HH:mm:ss" is 8 chars, drawn left of the transcript in
    // muted grey and excluded from selection/copy (matches the old sibling
    // gutter column). GutterGap separates it from the first transcript cell.
    private const int TimestampChars = 8;
    private const double GutterGap = 12;

    private static readonly IBrush GutterBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
    // Translucent overlay drawn over the selected columns — tints without
    // hiding the per-cell ANSI colours underneath.
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x33, 0x66, 0xCC));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<BackscrollView, FontFamily>(nameof(FontFamily), new FontFamily("monospace"));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<BackscrollView, double>(nameof(FontSize), 16);

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

    private IReadOnlyList<ScrollbackBuffer.Row> _rows = [];
    private Typeface _typeface;
    private double _cellW = 8;
    private double _cellH = 16;
    private double _gutterWidth;
    private int _maxCols;

    // ILogicalScrollable state.
    private Size _extent;
    private Size _viewport;
    private Vector _offset;
    private bool _canHScroll;
    private bool _canVScroll;

    // Selection anchors in (row, col) space. Null when nothing is selected;
    // equal anchor/caret means a bare caret (no visible range).
    private TextPos? _anchor;
    private TextPos? _caret;
    private bool _dragging;

    static BackscrollView()
    {
        FontFamilyProperty.Changed.AddClassHandler<BackscrollView>((c, _) => c.RecalculateMetrics());
        FontSizeProperty.Changed.AddClassHandler<BackscrollView>((c, _) => c.RecalculateMetrics());
    }

    public BackscrollView()
    {
        Focusable = true;
        ClipToBounds = true;
        // Bitmap-style CP437 face needs aliased text/edges or coloured pixels
        // fringe at glyph boundaries — same treatment as TerminalControl.
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Alias);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        _typeface = new Typeface(FontFamily);
        RecalculateMetrics();
    }

    // Height of one row in pixels — the window uses it to scroll a Find hit
    // into view.
    public double LineHeight => _cellH;

    // Top pixel (in content space) of the given row.
    public double RowTop(int row) => row * _cellH;

    // Swap in a fresh frozen snapshot. Clears any selection and recomputes the
    // scroll extent.
    public void SetRows(IReadOnlyList<ScrollbackBuffer.Row> rows)
    {
        _rows = rows ?? [];
        _anchor = null;
        _caret = null;
        _offset = default;
        RecomputeContentWidth();
        UpdateExtent();
        InvalidateMeasure();
        InvalidateVisual();
    }

    // Select the [col, col+length) span on row (used by Find-next), leaving the
    // scroll to the caller.
    public void SelectMatch(int row, int col, int length)
    {
        if ((uint)row >= (uint)_rows.Count) return;
        _anchor = new TextPos(row, col);
        _caret = new TextPos(row, col + length);
        InvalidateVisual();
    }

    // Text of the current selection, one line per row, trailing padding
    // trimmed. Empty when nothing is selected.
    public string SelectedText
    {
        get
        {
            if (NormalizedSelection() is not { } s) return string.Empty;

            StringBuilder sb = new();
            for (int r = s.Start.Row; r <= s.End.Row; r++)
            {
                Cell[] cells = _rows[r].Cells;
                int rowLen = TrimmedLength(cells);
                int c0 = r == s.Start.Row ? s.Start.Col : 0;
                int c1 = r == s.End.Row ? s.End.Col : rowLen;
                c0 = Math.Clamp(c0, 0, cells.Length);
                c1 = Math.Clamp(c1, 0, cells.Length);
                if (r > s.Start.Row) sb.Append('\n');
                for (int i = c0; i < c1; i++) sb.Append(cells[i].Char);
            }
            return sb.ToString();
        }
    }

    private void RecalculateMetrics()
    {
        _typeface = new Typeface(FontFamily);
        // Snap the cell box to whole pixels so adjacent cell fills meet exactly
        // and glyph advances stay grid-aligned (matches TerminalControl).
        FormattedText probe = new("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typeface, FontSize, Brushes.White);
        _cellW = Math.Max(1, Math.Round(probe.WidthIncludingTrailingWhitespace));
        _cellH = Math.Max(1, Math.Round(probe.Height));
        _gutterWidth = TimestampChars * _cellW + GutterGap;
        UpdateExtent();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void RecomputeContentWidth()
    {
        int max = 0;
        foreach (ScrollbackBuffer.Row row in _rows)
        {
            int len = TrimmedLength(row.Cells);
            if (len > max) max = len;
        }
        _maxCols = max;
    }

    private void UpdateExtent()
    {
        Size extent = new(_gutterWidth + _maxCols * _cellW, _rows.Count * _cellH);
        bool changed = extent != _extent;
        _extent = extent;
        _offset = ClampOffset(_offset);
        if (changed) RaiseScrollInvalidated(EventArgs.Empty);
    }

    private Vector ClampOffset(Vector v)
    {
        double maxX = Math.Max(0, _extent.Width - _viewport.Width);
        double maxY = Math.Max(0, _extent.Height - _viewport.Height);
        return new Vector(Math.Clamp(v.X, 0, maxX), Math.Clamp(v.Y, 0, maxY));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Logical scrolling: the control is viewport-sized; scrollbars come from
        // Extent/Viewport, not from a giant desired size.
        double w = double.IsInfinity(availableSize.Width) ? _extent.Width : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? _extent.Height : availableSize.Height;
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_viewport != finalSize)
        {
            _viewport = finalSize;
            UpdateExtent();
        }
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        if (_rows.Count == 0 || _cellH <= 0) return;

        double offY = _offset.Y;
        double offX = _offset.X;
        int first = Math.Max(0, (int)Math.Floor(offY / _cellH));
        int last = Math.Min(_rows.Count, (int)Math.Ceiling((offY + _viewport.Height) / _cellH));
        (TextPos Start, TextPos End)? sel = NormalizedSelection();

        double transcriptWidth = Math.Max(0, _viewport.Width - _gutterWidth);

        for (int i = first; i < last; i++)
        {
            double y = i * _cellH - offY;
            ScrollbackBuffer.Row row = _rows[i];

            // Gutter timestamp — fixed at the left edge, never scrolls sideways.
            string ts = row.Timestamp.ToLocalTime().ToString("HH:mm:ss");
            context.DrawText(
                new FormattedText(ts, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, GutterBrush),
                new Point(0, y));

            // Transcript region clipped so a long row / horizontal scroll can't
            // spill into the gutter.
            using (context.PushClip(new Rect(_gutterWidth, 0, transcriptWidth, _viewport.Height)))
            {
                double originX = _gutterWidth - offX;
                DrawRowCells(context, row.Cells, originX, y);
                if (sel is { } s && i >= s.Start.Row && i <= s.End.Row)
                    DrawSelection(context, row.Cells, i, s, originX, y);
            }
        }
    }

    private void DrawRowCells(DrawingContext context, Cell[] cells, double originX, double y)
    {
        int end = TrimmedLength(cells);
        int x = 0;
        while (x < end)
        {
            CellAttributes attr = cells[x].Attr;
            int runStart = x;
            do { x++; } while (x < end && cells[x].Attr.Equals(attr));
            DrawRun(context, cells, runStart, x, attr, originX, y);
        }
    }

    private void DrawRun(DrawingContext context, Cell[] cells, int x0, int x1, CellAttributes attr, double originX, double y)
    {
        bool reverse = (attr.Flags & CellFlags.Reverse) != 0;
        bool bold = (attr.Flags & CellFlags.Bold) != 0;

        TerminalColor fgColor = reverse ? attr.Background : attr.Foreground;
        TerminalColor bgColor = reverse ? attr.Foreground : attr.Background;
        uint fgArgb = reverse
            ? AnsiPalette.ResolveBackground(fgColor)
            : AnsiPalette.ResolveForeground(fgColor, bold);
        uint bgArgb = reverse
            ? AnsiPalette.ResolveForeground(bgColor, bold)
            : AnsiPalette.ResolveBackground(bgColor);

        double left = originX + x0 * _cellW;
        double width = (x1 - x0) * _cellW;
        // The whole control is already black; only paint a run background when
        // it differs from the default (reverse video, explicit bg colour).
        if (bgArgb != AnsiPalette.DefaultBackgroundArgb)
            context.FillRectangle(BrushFor(bgArgb), new Rect(left, y, width, _cellH));

        if ((attr.Flags & CellFlags.Concealed) != 0) return;

        IBrush fg = BrushFor(fgArgb);
        Typeface typeface = bold ? new Typeface(FontFamily, FontStyle.Normal, FontWeight.Bold) : _typeface;
        for (int i = x0; i < x1; i++)
        {
            char ch = cells[i].Char;
            if (ch == ' ') continue;
            context.DrawText(
                new FormattedText(ch.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, FontSize, fg),
                new Point(originX + i * _cellW, y));
        }

        if ((attr.Flags & CellFlags.Underline) != 0)
            context.FillRectangle(fg, new Rect(left, y + _cellH - 1, width, 1));
    }

    private void DrawSelection(DrawingContext context, Cell[] cells, int rowIndex, (TextPos Start, TextPos End) s, double originX, double y)
    {
        int rowLen = TrimmedLength(cells);
        int c0 = rowIndex == s.Start.Row ? s.Start.Col : 0;
        int c1 = rowIndex == s.End.Row ? s.End.Col : rowLen;
        c0 = Math.Clamp(c0, 0, cells.Length);
        c1 = Math.Clamp(c1, 0, cells.Length);
        if (c1 <= c0) return;
        double left = originX + c0 * _cellW;
        double width = (c1 - c0) * _cellW;
        context.FillRectangle(SelectionBrush, new Rect(left, y, width, _cellH));
    }

    // ----- Selection input ----------------------------------------------

    private TextPos HitTest(Point p)
    {
        int rowMax = Math.Max(0, _rows.Count - 1);
        int row = Math.Clamp((int)Math.Floor((p.Y + _offset.Y) / _cellH), 0, rowMax);
        int rowLen = _rows.Count == 0 ? 0 : TrimmedLength(_rows[row].Cells);
        int col = Math.Clamp((int)Math.Round((p.X - _gutterWidth + _offset.X) / _cellW), 0, rowLen);
        return new TextPos(row, col);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_rows.Count == 0) return;
        PointerPoint pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsLeftButtonPressed) return;

        Focus();
        TextPos pos = HitTest(pt.Position);
        // Shift+click extends the existing selection instead of restarting it —
        // lets a selection span more than one viewport (click, scroll, shift+click).
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _anchor is not null)
        {
            _caret = pos;
        }
        else
        {
            _anchor = pos;
            _caret = pos;
        }
        _dragging = true;
        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        _caret = HitTest(e.GetPosition(this));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    private (TextPos Start, TextPos End)? NormalizedSelection()
    {
        if (_anchor is not { } a || _caret is not { } c || a == c) return null;
        return a.CompareTo(c) <= 0 ? (a, c) : (c, a);
    }

    // Drop trailing plain-background spaces so a selection to end-of-line — and
    // the copied text — doesn't drag a tail of blanks.
    private static int TrimmedLength(Cell[] cells)
    {
        int end = cells.Length;
        while (end > 0 && cells[end - 1].Char == ' '
               && AnsiPalette.ResolveBackground(cells[end - 1].Attr.Background) == AnsiPalette.DefaultBackgroundArgb)
            end--;
        return end;
    }

    // Palette colours repeat across every row, so caching brushes by ARGB
    // collapses per-row allocations to the handful of distinct terminal
    // colours. UI-thread only, so no locking needed.
    private static readonly Dictionary<uint, IBrush> _brushCache = new();

    private static IBrush BrushFor(uint argb)
    {
        if (_brushCache.TryGetValue(argb, out IBrush? brush)) return brush;
        (byte r, byte g, byte b) = AnsiPalette.ToRgb(argb);
        brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        _brushCache[argb] = brush;
        return brush;
    }

    // ----- ILogicalScrollable -------------------------------------------

    public bool CanHorizontallyScroll
    {
        get => _canHScroll;
        set { _canHScroll = value; InvalidateMeasure(); }
    }

    public bool CanVerticallyScroll
    {
        get => _canVScroll;
        set { _canVScroll = value; InvalidateMeasure(); }
    }

    public bool IsLogicalScrollEnabled => true;

    public Size ScrollSize => new(_cellW * 3, _cellH);

    public Size PageScrollSize => new(_viewport.Width, _viewport.Height);

    public Size Extent => _extent;

    public Size Viewport => _viewport;

    public Vector Offset
    {
        get => _offset;
        set
        {
            Vector clamped = ClampOffset(value);
            if (clamped == _offset) return;
            _offset = clamped;
            InvalidateVisual();
            RaiseScrollInvalidated(EventArgs.Empty);
        }
    }

    public event EventHandler? ScrollInvalidated;

    public void RaiseScrollInvalidated(EventArgs e) => ScrollInvalidated?.Invoke(this, e);

    public bool BringIntoView(Control target, Rect targetRect) => false;

    public Control? GetControlInDirection(NavigationDirection direction, Control? from) => null;

    private readonly record struct TextPos(int Row, int Col) : IComparable<TextPos>
    {
        public int CompareTo(TextPos other) =>
            Row != other.Row ? Row.CompareTo(other.Row) : Col.CompareTo(other.Col);
    }
}
