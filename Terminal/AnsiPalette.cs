namespace FujinTerm.Terminal;

// Color resolver: turns a logical TerminalColor (default / indexed / RGB)
// into a concrete 32-bit ARGB value the renderer can blit.
//
// Owns the standard 16-color and 256-color xterm palettes, plus the
// "default" foreground/background fallbacks used when a cell hasn't been
// touched by an SGR sequence.
public static class AnsiPalette
{
    // Classic Windows-console / CGA palette. Dim entries are 0x80 channels, bright entries
    // are pure 0xFF, "white" (idx 7) is 0xC0C0C0. Saturated and high-contrast,
    // which is what BBS art was originally authored against on MS-DOS.
    public static readonly uint[] Default16 =
    {
        // Normal intensity (SGR 30–37, 40–47):
        0xFF000000, 0xFF800000, 0xFF008000, 0xFF808000,
        0xFF000080, 0xFF800080, 0xFF008080, 0xFFC0C0C0,
        // Bright / bold (SGR 90–97, 100–107 / "bold + index"):
        0xFF808080, 0xFFFF0000, 0xFF00FF00, 0xFFFFFF00,
        0xFF0000FF, 0xFFFF00FF, 0xFF00FFFF, 0xFFFFFFFF,
    };

    // Color used when a cell's foreground is "default".
    public const uint DefaultForegroundArgb = 0xFFC0C0C0;
    // Color used when a cell's background is "default".
    public const uint DefaultBackgroundArgb = 0xFF000000;

    // Lazily-built once per process; covers the full xterm 256-color palette.
    private static readonly uint[] s_xterm256 = BuildXterm256();

    // Look up an entry in the 256-color xterm palette.
    public static uint Indexed256(int idx) => s_xterm256[idx & 0xFF];

    // Map a logical foreground color to its final 32-bit ARGB value. When the
    // cell is bold, indexed colors 0–7 are promoted to their "bright"
    // counterparts 8–15 — matches what xterm and BBS clients do.
    public static uint ResolveForeground(TerminalColor color, bool bold) =>
        color.Kind switch
        {
            ColorKind.Default => bold ? Default16[15] : DefaultForegroundArgb,
            ColorKind.Indexed => ResolveIndexedForeground((int)color.Value, bold),
            ColorKind.Rgb     => 0xFF000000u | color.Value,
            _ => DefaultForegroundArgb,
        };

    // Map a logical background color to its ARGB value.
    public static uint ResolveBackground(TerminalColor color) =>
        color.Kind switch
        {
            ColorKind.Default => DefaultBackgroundArgb,
            ColorKind.Indexed => Indexed256((int)color.Value),
            ColorKind.Rgb     => 0xFF000000u | color.Value,
            _ => DefaultBackgroundArgb,
        };

    private static uint ResolveIndexedForeground(int idx, bool bold)
    {
        // Classic terminal behavior: bold + a base color (0–7) means "use
        // the bright variant" rather than literally a heavier weight.
        if (bold && idx < 8) idx += 8;
        return Indexed256(idx);
    }

    // Split an ARGB uint into (R, G, B) bytes for the renderer.
    public static (byte R, byte G, byte B) ToRgb(uint argb) =>
        ((byte)((argb >> 16) & 0xFF),
         (byte)((argb >> 8) & 0xFF),
         (byte)(argb & 0xFF));

    // Construct the full xterm 256-color palette:
    //   0–15    : the basic 16 ANSI colors (above).
    //   16–231  : a 6×6×6 RGB cube with the canonical xterm intensity steps.
    //   232–255 : a 24-step grayscale ramp.
    private static uint[] BuildXterm256()
    {
        var t = new uint[256];
        for (int i = 0; i < 16; i++) t[i] = Default16[i];

        // The 6 levels each channel can take in the cube. Note the gap from
        // 0 → 95 — that's exactly how xterm defines it.
        ReadOnlySpan<byte> levels = stackalloc byte[] { 0, 95, 135, 175, 215, 255 };
        for (int r = 0; r < 6; r++)
        for (int g = 0; g < 6; g++)
        for (int b = 0; b < 6; b++)
        {
            int idx = 16 + 36 * r + 6 * g + b;
            t[idx] = Pack(levels[r], levels[g], levels[b]);
        }

        // Grayscale ramp: 8, 18, 28, ..., 238 (24 evenly spaced grays).
        for (int i = 0; i < 24; i++)
        {
            byte v = (byte)(8 + i * 10);
            t[232 + i] = Pack(v, v, v);
        }
        return t;
    }

    // Pack RGB bytes into a fully-opaque ARGB uint.
    private static uint Pack(byte r, byte g, byte b) =>
        0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
}
