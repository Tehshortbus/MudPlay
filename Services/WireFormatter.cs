using System.Text;

namespace FujinTerm.Services;

// Byte → printable-text converters used by the Wire Inspector's two panes.
// Pure functions; no state.
public static class WireFormatter
{
    // Render bytes with non-printables made visible as caret-style markers
    // (ESC → ^[, CR → ^M, etc.) but newline bytes preserved as actual line
    // breaks for readability. Latin-1 maps the high half (0x80–0xFF) straight to
    // U+0080–U+00FF; the terminal already speaks Latin-1, so this matches what
    // the user sees.
    public static string RenderRaw(ReadOnlySpan<byte> bytes)
    {
        // Two bytes can grow to three chars max ("^[" markers + payload), so
        // pre-size generously to skip StringBuilder regrowth in the hot path.
        StringBuilder sb = new(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            switch (b)
            {
                case 0x0A:               // LF — keep the line break visible.
                    sb.Append('\n');
                    break;
                case 0x0D:               // CR — show + collapse so we don't double-break on CRLF.
                    sb.Append("^M");
                    break;
                case < 0x20 or 0x7F:     // Other C0 controls + DEL.
                    sb.Append('^');
                    sb.Append((char)(b == 0x7F ? '?' : b + 0x40));
                    break;
                default:
                    sb.Append((char)b);
                    break;
            }
        }
        return sb.ToString();
    }

    // Render bytes as readable text for at-a-glance debugging: ANSI CSI escape
    // sequences are dropped, the backspace overstrike MajorMUD uses to highlight
    // an exit's first letter (F<BS>o… → o…) is collapsed, and CR is removed while
    // LF is kept as a line break. So an "Obvious exits:" line that arrives as
    // nF<BS>orth, …<CR> renders as "north, …". Remaining C0 controls + DEL stay
    // as caret markers so a stray byte doesn't silently hide a problem. Not a
    // full VT100 parser.
    public static string RenderStripped(ReadOnlySpan<byte> bytes)
    {
        StringBuilder sb = new(bytes.Length);
        int i = 0;
        while (i < bytes.Length)
        {
            byte b = bytes[i];

            // CSI: ESC '[' params [intermediates] final — drop the whole run.
            if (b == 0x1B && i + 1 < bytes.Length && bytes[i + 1] == (byte)'[')
            {
                i += 2;
                while (i < bytes.Length && bytes[i] is >= 0x30 and <= 0x3F) i++; // params 0-9 : ; < = > ?
                while (i < bytes.Length && bytes[i] is >= 0x20 and <= 0x2F) i++; // intermediates
                if (i < bytes.Length && bytes[i] is >= 0x40 and <= 0x7E) i++;    // final byte
                continue;
            }

            switch (b)
            {
                case 0x08:               // BS — overstrike: erase the char it backed over.
                    if (sb.Length > 0) sb.Length--;
                    break;
                case 0x0D:               // CR — drop; the paired LF supplies the break.
                    break;
                case 0x0A:               // LF — keep the line break.
                    sb.Append('\n');
                    break;
                case < 0x20 or 0x7F:     // Other C0 controls + DEL — keep visible.
                    sb.Append('^');
                    sb.Append((char)(b == 0x7F ? '?' : b + 0x40));
                    break;
                default:
                    sb.Append((char)b);
                    break;
            }
            i++;
        }
        return sb.ToString();
    }
}
