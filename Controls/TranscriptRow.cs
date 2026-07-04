using System.Collections.Generic;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using FujinTerm.Terminal;
using FujinTerm.ViewModels;

namespace FujinTerm.Controls;

// Renders one Backscroll row's cells as coloured inline Runs inside a
// SelectableTextBlock, so within-row drag-select + Ctrl+C copy work natively.
// One instance per visible row: the Backscroll's ListBox virtualizes, so only
// the ~screenful of rows actually on screen ever build inlines — which is why
// a 10k-row transcript opens instantly instead of laying out one giant text
// block. Timestamps live in the sibling gutter column of the row template so
// the character selection here never picks them up.
public sealed class TranscriptRow : SelectableTextBlock
{
    public static readonly StyledProperty<BackscrollRowViewModel?> RowProperty =
        AvaloniaProperty.Register<TranscriptRow, BackscrollRowViewModel?>(nameof(Row));

    public BackscrollRowViewModel? Row
    {
        get => GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    static TranscriptRow()
    {
        RowProperty.Changed.AddClassHandler<TranscriptRow>((c, _) => c.Rebuild());
    }

    public TranscriptRow()
    {
        UseLayoutRounding = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        Background = Brushes.Transparent;
        TextWrapping = TextWrapping.NoWrap;
        SelectionBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x4F, 0x8F, 0xD0));
    }

    private void Rebuild()
    {
        InlineCollection inlines = new();
        if (Row is not { } row) { Inlines = inlines; return; }

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
        }

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

    // Palette colours repeat across every row, so caching brushes by ARGB
    // collapses per-row allocations down to the handful of distinct terminal
    // colours. UI-thread only, so no locking needed.
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
