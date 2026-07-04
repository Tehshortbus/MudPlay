namespace FujinTerm.Terminal;

// How a TerminalColor should be interpreted.
public enum ColorKind : byte
{
    // Use the configured default foreground/background.
    Default,
    // Index into the 256-color xterm palette (0–255).
    Indexed,
    // Direct 24-bit RGB triple stored in the low 24 bits.
    Rgb,
}

// A single color value as set by an SGR escape. Stored compactly as a kind
// tag plus a 32-bit value: index for palette colors, packed RGB for
// truecolor, ignored for Default.
public readonly record struct TerminalColor(ColorKind Kind, uint Value)
{
    public static readonly TerminalColor Default = new(ColorKind.Default, 0);

    // Build a palette-indexed color (clamped to a byte).
    public static TerminalColor Indexed(int idx) => new(ColorKind.Indexed, (uint)(idx & 0xFF));

    // Build a 24-bit RGB color.
    public static TerminalColor Rgb(byte r, byte g, byte b) =>
        new(ColorKind.Rgb, ((uint)r << 16) | ((uint)g << 8) | b);
}
