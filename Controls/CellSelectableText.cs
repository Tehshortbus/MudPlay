using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using FujinTerm.Terminal;

namespace FujinTerm.Controls;

/// <summary>
/// Selectable, drag-to-copy renderer for one <see cref="Cell"/>[] row.
/// Inherits <see cref="SelectableTextBlock"/> for the native selection /
/// Ctrl+C / copy-on-clipboard behaviour, and rebuilds its
/// <see cref="TextBlock.Inlines"/> from the bound <see cref="Cells"/> array
/// so colours, bold, and underline still render on a per-run basis.
/// </summary>
/// <remarks>
/// Trade-off: <see cref="Run.Background"/> only paints behind glyph ink,
/// so coloured-space box-art rows don't fill the whole cell rectangle the
/// way a per-cell <c>FillRectangle</c> would. Acceptable here because the
/// Backscroll window's primary value is reading + copying text, and
/// selection + clipboard support beat pixel-perfect ANSI box-art rendering
/// for that use case.
/// </remarks>
public sealed class CellSelectableText : SelectableTextBlock
{
    public static readonly StyledProperty<Cell[]?> CellsProperty =
        AvaloniaProperty.Register<CellSelectableText, Cell[]?>(nameof(Cells));

    public Cell[]? Cells
    {
        get => GetValue(CellsProperty);
        set => SetValue(CellsProperty, value);
    }

    static CellSelectableText()
    {
        CellsProperty.Changed.AddClassHandler<CellSelectableText>((c, _) => c.Rebuild());
    }

    public CellSelectableText()
    {
        // Pixel-snap + aliased so the Mx437 bitmap font at its native 16-pt
        // cell stays crisp instead of being anti-aliased into mush.
        UseLayoutRounding = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    private void Rebuild()
    {
        InlineCollection inlines = Inlines ??= new InlineCollection();
        inlines.Clear();

        Cell[]? cells = Cells;
        if (cells is null || cells.Length == 0) return;

        // Trim trailing spaces with default attributes — they're padding
        // from the terminal grid and just balloon the row width.
        int end = cells.Length;
        while (end > 0 && cells[end - 1].Char == ' ' && IsPlainBackground(cells[end - 1].Attr))
        {
            end--;
        }
        if (end == 0) return;

        int i = 0;
        while (i < end)
        {
            CellAttributes attr = cells[i].Attr;
            int runStart = i;
            do { i++; } while (i < end && cells[i].Attr.Equals(attr));
            inlines.Add(BuildRun(cells, runStart, i, attr));
        }
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

        run.Foreground = concealed
            ? new SolidColorBrush(Color.FromArgb(0, 0, 0, 0))
            : new SolidColorBrush(ToColor(fgArgb));

        if (bgArgb != AnsiPalette.DefaultBackgroundArgb)
        {
            run.Background = new SolidColorBrush(ToColor(bgArgb));
        }

        if (bold) run.FontWeight = FontWeight.Bold;
        if (underline) run.TextDecorations = Avalonia.Media.TextDecorations.Underline;

        return run;
    }

    private static Color ToColor(uint argb)
        => Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF));
}
