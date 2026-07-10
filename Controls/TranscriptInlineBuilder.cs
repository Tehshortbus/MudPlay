using System.Collections.Generic;
using System.Text;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using FujinTerm.Terminal;

namespace FujinTerm.Controls;

// Turns a Backscroll row's Cell[] into coloured inline Runs. Shared by the
// Backscroll window, which renders the whole transcript into one
// SelectableTextBlock so native drag-select spans lines and Ctrl+C copies the
// exact character range. Trailing plain-background spaces are dropped so a
// selection that runs to end-of-line doesn't drag a tail of blanks.
public static class TranscriptInlineBuilder
{
    // Append one row's runs to inlines and return the plain-text length that was
    // emitted (the trimmed cell count) — the caller uses it to track per-line
    // character offsets for find-highlighting.
    public static int AppendRow(InlineCollection inlines, Cell[] cells)
    {
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
        return end;
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
