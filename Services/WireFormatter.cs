using System.Text;
using System.Text.RegularExpressions;

namespace FujinTerm.Services;

/// <summary>
/// Byte → printable-text converters used by the Wire Inspector's two panes.
/// Pure functions; no state.
/// </summary>
public static partial class WireFormatter
{
    /// <summary>
    /// Render <paramref name="bytes"/> with non-printables made visible as
    /// caret-style markers (<c>ESC</c> → <c>^[</c>, <c>CR</c> → <c>^M</c>,
    /// etc.) but newline bytes preserved as actual line breaks for readability.
    /// Latin-1 maps the high half (0x80–0xFF) straight to U+0080–U+00FF; the
    /// terminal already speaks Latin-1, so this matches what the user sees.
    /// </summary>
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

    /// <summary>
    /// Render <paramref name="bytes"/> with ANSI CSI escape sequences removed
    /// (anything matching <c>ESC '[' params final-byte</c>). Other control
    /// characters are kept as caret markers so a stray CR doesn't silently
    /// hide a problem. Not a full VT100 parser — the inspector only needs
    /// "readable text" for at-a-glance debugging.
    /// </summary>
    public static string RenderStripped(ReadOnlySpan<byte> bytes)
    {
        string raw = RenderRaw(bytes);
        // The CSI pattern lives in caret form in `raw` ("^[[...m" etc.) because
        // RenderRaw substituted ESC. Strip the caret-form too.
        return CaretCsi().Replace(raw, "");
    }

    // ESC '[' params [intermediates] final.
    // After RenderRaw, ESC has become the two-char sequence "^[" — match that.
    [GeneratedRegex(@"\^\[\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled)]
    private static partial Regex CaretCsi();
}
