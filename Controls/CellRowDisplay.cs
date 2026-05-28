using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using FujinTerm.Terminal;

namespace FujinTerm.Controls;

/// <summary>
/// Renders one <c>Cell[]</c> row (from <see cref="ScrollbackBuffer"/>) as a
/// single text line with one <see cref="Run"/> per attribute-run so ANSI
/// colours survive into the Backscroll window. Selectable so the user can
/// copy text out without losing the visual layout.
/// </summary>
/// <remarks>
/// Grouping cells by attribute matches the renderer in <c>TerminalControl</c>
/// — same batching rule, different output medium. The implementation lives
/// here rather than on the existing control because BackscrollWindow doesn't
/// need the live-cursor / focus / input concerns the live terminal has.
/// </remarks>
public sealed class CellRowDisplay : SelectableTextBlock
{
    public static readonly StyledProperty<Cell[]?> RowProperty =
        AvaloniaProperty.Register<CellRowDisplay, Cell[]?>(nameof(Row));

    /// <summary>The cell row to render. Setting it rebuilds the inlines.</summary>
    public Cell[]? Row
    {
        get => GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    static CellRowDisplay()
    {
        RowProperty.Changed.AddClassHandler<CellRowDisplay>((view, _) => view.Rebuild());
    }

    private void Rebuild()
    {
        Inlines?.Clear();

        Cell[]? cells = Row;
        if (cells is null || cells.Length == 0) return;

        Inlines ??= new InlineCollection();

        int i = 0;
        while (i < cells.Length)
        {
            CellAttributes attr = cells[i].Attr;
            int start = i;
            do { i++; } while (i < cells.Length && cells[i].Attr.Equals(attr));

            int len = i - start;
            char[] chars = new char[len];
            for (int j = 0; j < len; j++) chars[j] = cells[start + j].Char;
            string text = new(chars);

            bool bold     = (attr.Flags & CellFlags.Bold) != 0;
            bool reverse  = (attr.Flags & CellFlags.Reverse) != 0;
            bool underline = (attr.Flags & CellFlags.Underline) != 0;
            bool concealed = (attr.Flags & CellFlags.Concealed) != 0;

            uint fgArgb = AnsiPalette.ResolveForeground(attr.Foreground, bold);
            uint bgArgb = AnsiPalette.ResolveBackground(attr.Background);
            if (reverse) (fgArgb, bgArgb) = (bgArgb, fgArgb);

            Run run = new(concealed ? new string(' ', len) : text)
            {
                Foreground = new SolidColorBrush(ToColor(fgArgb)),
            };

            // Only paint a background when it's non-default, otherwise the row
            // sits on the parent's surface and inherits the chrome bg.
            if (bgArgb != AnsiPalette.DefaultBackgroundArgb)
            {
                run.Background = new SolidColorBrush(ToColor(bgArgb));
            }

            if (underline)
                run.TextDecorations = Avalonia.Media.TextDecorations.Underline;

            Inlines.Add(run);
        }
    }

    private static Color ToColor(uint argb)
        => Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8)  & 0xFF),
            (byte)( argb        & 0xFF));
}
