using System.Text;

namespace FujinTerm.Terminal;

// Re-emits a Cell[] row as plain text plus inline ANSI SGR escapes, so a
// captured log file rendered through less -R, a modern terminal, or any
// ANSI-aware viewer shows the same colours the user saw live. Mirrors the
// run-batching Controls.CellRowDisplay does for on-screen rendering — same
// logical groupings, different output medium.
public static class AnsiEncoder
{
    // Encode cells as ANSI text. Always ends with ESC [ 0 m so the output
    // doesn't bleed colour into whatever follows it in the same file / pipe.
    public static string EncodeRow(ReadOnlySpan<Cell> cells)
    {
        StringBuilder sb = new(cells.Length + 32);
        if (cells.Length == 0) return string.Empty;

        CellAttributes? lastAttr = null;

        for (int i = 0; i < cells.Length; i++)
        {
            CellAttributes attr = cells[i].Attr;
            if (lastAttr is null || !attr.Equals(lastAttr.Value))
            {
                EmitSgr(sb, attr);
                lastAttr = attr;
            }
            sb.Append(cells[i].Char);
        }

        sb.Append("\x1b[0m");
        return sb.ToString();
    }

    private static void EmitSgr(StringBuilder sb, CellAttributes attr)
    {
        sb.Append("\x1b[0");   // reset before re-applying the run's attrs

        if ((attr.Flags & CellFlags.Bold)      != 0) sb.Append(";1");
        if ((attr.Flags & CellFlags.Underline) != 0) sb.Append(";4");
        if ((attr.Flags & CellFlags.Reverse)   != 0) sb.Append(";7");
        if ((attr.Flags & CellFlags.Concealed) != 0) sb.Append(";8");

        AppendColor(sb, attr.Foreground, isBackground: false);
        AppendColor(sb, attr.Background, isBackground: true);

        sb.Append('m');
    }

    private static void AppendColor(StringBuilder sb, TerminalColor color, bool isBackground)
    {
        switch (color.Kind)
        {
            case ColorKind.Indexed:
                sb.Append(isBackground ? ";48;5;" : ";38;5;").Append(color.Value);
                break;
            case ColorKind.Rgb:
                uint rgb = color.Value;
                byte r = (byte)((rgb >> 16) & 0xFF);
                byte g = (byte)((rgb >> 8)  & 0xFF);
                byte b = (byte)( rgb        & 0xFF);
                sb.Append(isBackground ? ";48;2;" : ";38;2;")
                  .Append(r).Append(';').Append(g).Append(';').Append(b);
                break;
            // Default: emit nothing — the reset already restored default.
        }
    }
}
