namespace FujinTerm.Terminal;

/// <summary>
/// How a <see cref="TerminalColor"/> should be interpreted.
/// </summary>
public enum ColorKind : byte
{
    /// <summary>Use the configured default foreground/background.</summary>
    Default,
    /// <summary>Index into the 256-color xterm palette (0–255).</summary>
    Indexed,
    /// <summary>Direct 24-bit RGB triple stored in the low 24 bits.</summary>
    Rgb,
}

/// <summary>
/// A single color value as set by an SGR escape. Stored compactly as a
/// kind tag plus a 32-bit value: index for palette colors, packed RGB for
/// truecolor, ignored for Default.
/// </summary>
public readonly record struct TerminalColor(ColorKind Kind, uint Value)
{
    public static readonly TerminalColor Default = new(ColorKind.Default, 0);

    /// <summary>Build a palette-indexed color (clamped to a byte).</summary>
    public static TerminalColor Indexed(int idx) => new(ColorKind.Indexed, (uint)(idx & 0xFF));

    /// <summary>Build a 24-bit RGB color.</summary>
    public static TerminalColor Rgb(byte r, byte g, byte b) =>
        new(ColorKind.Rgb, ((uint)r << 16) | ((uint)g << 8) | b);
}
