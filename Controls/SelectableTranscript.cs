using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using FujinTerm.Terminal;
using FujinTerm.ViewModels;

namespace FujinTerm.Controls;

// One SelectableTextBlock that renders the body of the Backscroll transcript —
// every row's cells inline as coloured Runs, separated by LineBreaks. Native
// drag-to-select spans rows and Ctrl+C copies the multi-line range. Timestamps
// live in the parallel TimestampGutter so the user's selection can't
// accidentally include them.
//
// Rebuilding all Inlines on every CollectionChanged scales linearly with row
// count. For the Backscroll's default 10k-row cap that's fine on modern
// hardware; revisit if it becomes a bottleneck.
public sealed class SelectableTranscript : SelectableTextBlock
{
    public static readonly StyledProperty<System.Collections.IList?> RowsProperty =
        AvaloniaProperty.Register<SelectableTranscript, System.Collections.IList?>(nameof(Rows));

    public System.Collections.IList? Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    // Per-row absolute character offset within the rendered text. Built each
    // Rebuild so the view-model can scroll a Find-next match into view + set
    // selection at the matched substring.
    public IReadOnlyList<int> RowCharOffsets => _rowOffsets;
    private int[] _rowOffsets = Array.Empty<int>();

    private INotifyCollectionChanged? _observed;
    // Coalesces a burst of CollectionChanged events into a single Rebuild
    // per dispatcher tick. BackscrollViewModel.RefreshLiveTail replaces up
    // to ~25 rows per screen update, each firing CollectionChanged; without
    // coalescing each fire walks every row in the buffer (up to 10k) to
    // rebuild the inline runs — quadratic on screen-update bursts.
    private bool _rebuildScheduled;
    // True while the user is dragging out a selection (pointer down). A live
    // rebuild reassigns the whole Inlines collection, which re-lays-out the
    // text and resets the in-progress selection — so we hold rebuilds while
    // the user is selecting (or has a live selection) and flush once idle.
    private bool _pointerDown;
    private bool _pendingRebuild;

    static SelectableTranscript()
    {
        RowsProperty.Changed.AddClassHandler<SelectableTranscript>((c, e) =>
            c.OnRowsChanged(e.OldValue as System.Collections.IList,
                            e.NewValue as System.Collections.IList));
    }

    public SelectableTranscript()
    {
        UseLayoutRounding = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        Background = Brushes.Transparent;
        Focusable = true;
        TextWrapping = TextWrapping.NoWrap;
        SelectionBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x4F, 0x8F, 0xD0));
    }

    private void OnRowsChanged(System.Collections.IList? oldRows, System.Collections.IList? newRows)
    {
        if (_observed is not null)
        {
            _observed.CollectionChanged -= OnCollectionChanged;
            _observed = null;
        }
        if (newRows is INotifyCollectionChanged incc)
        {
            incc.CollectionChanged += OnCollectionChanged;
            _observed = incc;
        }
        Rebuild();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleRebuild();

    private void ScheduleRebuild()
    {
        if (_rebuildScheduled) return;
        _rebuildScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _rebuildScheduled = false;
            // Don't blow away an in-progress / live selection — hold the latest
            // content and rebuild once the user finishes (see FlushPendingRebuild).
            if (IsInteracting) { _pendingRebuild = true; return; }
            Rebuild();
        });
    }

    // The user is mid-interaction when the pointer is down or a non-empty
    // selection exists; rebuilding under either resets the selection.
    private bool IsInteracting => _pointerDown || SelectionStart != SelectionEnd;

    private void FlushPendingRebuild()
    {
        if (_pendingRebuild && !IsInteracting)
        {
            _pendingRebuild = false;
            Rebuild();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _pointerDown = true;
        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _pointerDown = false;
        FlushPendingRebuild();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // When a selection collapses (e.g. a single click, or Go-to-live) flush
        // any content that arrived while the selection was held.
        if (change.Property == SelectionStartProperty || change.Property == SelectionEndProperty)
            FlushPendingRebuild();
    }

    private void Rebuild()
    {
        InlineCollection inlines = new();
        System.Collections.IList? rows = Rows;
        if (rows is null || rows.Count == 0)
        {
            _rowOffsets = Array.Empty<int>();
            Inlines = inlines;
            return;
        }

        int[] offsets = new int[rows.Count];
        int charOffset = 0;

        for (int r = 0; r < rows.Count; r++)
        {
            offsets[r] = charOffset;
            if (rows[r] is not BackscrollRowViewModel row) continue;

            Cell[] cells = row.Cells;
            int end = cells.Length;
            while (end > 0 && cells[end - 1].Char == ' ' && IsPlainBackground(cells[end - 1].Attr))
                end--;

            int i = 0;
            while (i < end)
            {
                CellAttributes attr = cells[i].Attr;
                int runStart = i;
                do { i++; } while (i < end && cells[i].Attr.Equals(attr));
                inlines.Add(BuildRun(cells, runStart, i, attr));
                charOffset += i - runStart;
            }

            if (r < rows.Count - 1)
            {
                inlines.Add(new LineBreak());
                charOffset += 1;
            }
        }

        _rowOffsets = offsets;
        Inlines = inlines;
    }

    private static bool IsPlainBackground(CellAttributes attr)
        => AnsiPalette.ResolveBackground(attr.Background) == AnsiPalette.DefaultBackgroundArgb;

    private static Run BuildRun(Cell[] cells, int x0, int x1, CellAttributes attr)
    {
        StringBuilder sb = new(x1 - x0);
        for (int i = x0; i < x1; i++) sb.Append(cells[i].Char);
        Run run = new(sb.ToString());

        bool reverse = (attr.Flags & CellFlags.Reverse) != 0;
        bool bold = (attr.Flags & CellFlags.Bold) != 0;
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

        run.Foreground = concealed ? BrushFor(0u) : BrushFor(fgArgb);

        if (bgArgb != AnsiPalette.DefaultBackgroundArgb)
            run.Background = BrushFor(bgArgb);

        if (bold) run.FontWeight = FontWeight.Bold;
        if (underline) run.TextDecorations = Avalonia.Media.TextDecorations.Underline;
        return run;
    }

    // Brushes are immutable here, and a transcript rebuild creates a run per
    // colour-contiguous span across up to 10k rows — caching by ARGB collapses
    // tens of thousands of per-rebuild allocations down to the ~handful of
    // distinct palette colours. UI-thread only, so no locking needed.
    private static readonly Dictionary<uint, IBrush> _brushCache = new();

    private static IBrush BrushFor(uint argb)
    {
        if (_brushCache.TryGetValue(argb, out IBrush? brush)) return brush;
        brush = new SolidColorBrush(ToColor(argb));
        _brushCache[argb] = brush;
        return brush;
    }

    private static Color ToColor(uint argb)
        => Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF));
}
