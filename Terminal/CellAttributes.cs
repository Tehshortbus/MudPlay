namespace FujinTerm.Terminal;

// Per-cell visual flags (the SGR "styles" — bold, underline, etc.). Stored as
// bit-flags so multiple can be combined.
[Flags]
public enum CellFlags : byte
{
    None      = 0,
    Bold      = 1 << 0,
    Underline = 1 << 1,
    Blink     = 1 << 2,
    Reverse   = 1 << 3, // Foreground and background swapped on render.
    Concealed = 1 << 4, // Glyph is not drawn (only background fills).
}

// Everything that describes "what a cell looks like" minus the actual
// character: foreground and background colors plus a set of style flags. Two
// cells with equal attributes can be drawn as a single horizontal run, which
// the renderer uses as a batching optimization.
public readonly record struct CellAttributes(
    TerminalColor Foreground,
    TerminalColor Background,
    CellFlags Flags)
{
    public static readonly CellAttributes Default =
        new(TerminalColor.Default, TerminalColor.Default, CellFlags.None);

    public CellAttributes WithForeground(TerminalColor c) => this with { Foreground = c };
    public CellAttributes WithBackground(TerminalColor c) => this with { Background = c };

    // Set or clear a single flag, leaving the others alone.
    public CellAttributes WithFlag(CellFlags f, bool on) =>
        this with { Flags = on ? (Flags | f) : (Flags & ~f) };
}

// One position in the screen grid: the character to display plus its styling.
public readonly record struct Cell(char Char, CellAttributes Attr)
{
    // An empty space with default colors — used to clear cells.
    public static readonly Cell Blank = new(' ', CellAttributes.Default);
}
