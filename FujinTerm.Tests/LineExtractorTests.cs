using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Deterministic tests for <see cref="LineExtractor"/>'s pure cell-row →
/// EmittedLine conversion. The event-driven path (subscribing to the
/// scrollback) gets exercised indirectly by integration through the
/// emulator; these unit tests pin the conversion logic itself.
/// </summary>
public sealed class LineExtractorTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BuildLine_PlainAscii_PreservesCharacters()
    {
        Cell[] cells = MakeRow("Hello, world!", paddedTo: 80);

        LineExtractor.EmittedLine line = LineExtractor.BuildLine(cells, FixedNow, isPromptLine: false);

        Assert.Equal("Hello, world!", line.Text);
        Assert.Equal(13, line.Attributes.Length);
        Assert.False(line.IsPromptLine);
        Assert.Equal(FixedNow, line.Timestamp);
    }

    [Fact]
    public void BuildLine_TrailingSpacesWithDefaultBackground_AreTrimmed()
    {
        // 80-column row that holds "OK" in the first two columns and blanks
        // through the rest. The blank tail should be stripped because none
        // of it was ever written to with a non-default background.
        Cell[] cells = MakeRow("OK", paddedTo: 80);

        LineExtractor.EmittedLine line = LineExtractor.BuildLine(cells, FixedNow, isPromptLine: false);

        Assert.Equal("OK", line.Text);
        Assert.Equal(2, line.Attributes.Length);
    }

    [Fact]
    public void BuildLine_TrailingSpacesWithNonDefaultBackground_AreKept()
    {
        // A coloured-background highlight bar (cyan) extending to the right
        // edge is part of the line's visual identity — keep it. Real BBSes
        // use this for status bars / coloured separators.
        Cell[] cells = new Cell[20];
        CellAttributes plain   = CellAttributes.Default;
        CellAttributes onCyan  = CellAttributes.Default
            .WithBackground(TerminalColor.Indexed(6));   // cyan
        for (int i = 0; i < 5;  i++) cells[i] = new Cell("HELLO"[i], plain);
        for (int i = 5; i < 20; i++) cells[i] = new Cell(' ',        onCyan);

        LineExtractor.EmittedLine line = LineExtractor.BuildLine(cells, FixedNow, isPromptLine: false);

        Assert.Equal(20, line.Text.Length);
        Assert.Equal("HELLO" + new string(' ', 15), line.Text);
    }

    [Fact]
    public void BuildLine_EmptyRow_ProducesEmptyLine()
    {
        Cell[] cells = MakeRow(string.Empty, paddedTo: 80);

        LineExtractor.EmittedLine line = LineExtractor.BuildLine(cells, FixedNow, isPromptLine: false);

        Assert.Equal(string.Empty, line.Text);
        Assert.Empty(line.Attributes);
    }

    [Fact]
    public void BuildLine_AttributesAlignedWithText_OneAttrPerCharacter()
    {
        // Mix three SGR runs in one row: red "ERR" + default " " + green "OK".
        Cell[] cells = new Cell[10];
        CellAttributes red   = CellAttributes.Default.WithForeground(TerminalColor.Indexed(1));
        CellAttributes plain = CellAttributes.Default;
        CellAttributes green = CellAttributes.Default.WithForeground(TerminalColor.Indexed(2));
        cells[0] = new Cell('E', red);
        cells[1] = new Cell('R', red);
        cells[2] = new Cell('R', red);
        cells[3] = new Cell(' ', plain);
        cells[4] = new Cell('O', green);
        cells[5] = new Cell('K', green);
        // Trailing blanks (default bg → trimmed).
        for (int i = 6; i < 10; i++) cells[i] = new Cell(' ', plain);

        LineExtractor.EmittedLine line = LineExtractor.BuildLine(cells, FixedNow, isPromptLine: false);

        Assert.Equal("ERR OK", line.Text);
        Assert.Equal(6, line.Attributes.Length);
        Assert.Equal(red,   line.Attributes[0]);
        Assert.Equal(red,   line.Attributes[2]);
        Assert.Equal(plain, line.Attributes[3]);
        Assert.Equal(green, line.Attributes[4]);
        Assert.Equal(green, line.Attributes[5]);
    }

    [Fact]
    public void BuildLine_IsPromptLineFlag_Roundtrips()
    {
        Cell[] cells = MakeRow("HP=100/100 MA=50/50:", paddedTo: 80);

        LineExtractor.EmittedLine line = LineExtractor.BuildLine(cells, FixedNow, isPromptLine: true);

        Assert.True(line.IsPromptLine);
    }

    /// <summary>
    /// Helper: build a row whose first <paramref name="text"/>.Length cells
    /// hold <paramref name="text"/> at default attributes, then pad blanks
    /// to <paramref name="paddedTo"/>. The pads use default background so
    /// trim drops them.
    /// </summary>
    private static Cell[] MakeRow(string text, int paddedTo)
    {
        Cell[] cells = new Cell[paddedTo];
        for (int i = 0; i < text.Length; i++) cells[i] = new Cell(text[i], CellAttributes.Default);
        for (int i = text.Length; i < paddedTo; i++) cells[i] = Cell.Blank;
        return cells;
    }
}
