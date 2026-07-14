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

    [Fact]
    public void BuildLine_TrimDisabled_KeepsTrailingBlanks()
    {
        // A soft-wrapped fragment is full to the right margin; a trailing space
        // at the wrap point must survive so the stitched line reads "power of",
        // not "powerof".
        Cell[] cells = MakeRow("power ", paddedTo: 80);

        LineExtractor.EmittedLine line =
            LineExtractor.BuildLine(cells, FixedNow, isPromptLine: false, trimTrailingBlanks: false);

        Assert.Equal(80, line.Text.Length);
        Assert.Equal("power ", line.Text[..6]);
    }

    // ----- Soft-wrap coalescing (event path through the emulator) -----------

    [Fact]
    public void WrappedLongLine_EmitsSingleJoinedLine()
    {
        // A gossip longer than the 80-column terminal: the client wraps it
        // mid-word, but the emitted line must be the whole logical message so
        // the chat pattern matches all of it (the reported truncation bug).
        string body = "Phrixas gossips: \"every class sucks at these levels, "
                    + "unless you have the power of luxury farming every rare item\"";
        Assert.True(body.Length > 80);

        List<string> emitted = FeedThroughEmulator(body + "\r\n");

        Assert.Single(emitted);
        Assert.Equal(body, emitted[0]);
    }

    [Fact]
    public void WrapLandingOnSpace_PreservesSpace()
    {
        // Force column 81 (0-based 80) to fall exactly on a space so the wrap
        // splits at a word boundary; the space must survive the stitch.
        string head = new string('a', 80);        // fills the row; 81st char wraps
        string full = head + " tail";             // char 81 is the space
        List<string> emitted = FeedThroughEmulator(full + "\r\n");

        Assert.Single(emitted);
        Assert.Equal(full, emitted[0]);
    }

    [Fact]
    public void ThreeRowWrap_JoinsAllFragments()
    {
        string body = new string('x', 200);        // spans three 80-column rows
        List<string> emitted = FeedThroughEmulator(body + "\r\n");

        Assert.Single(emitted);
        Assert.Equal(body, emitted[0]);
    }

    [Fact]
    public void HardLineBreaks_AreNotJoined()
    {
        // Two short server lines separated by CRLF must stay two emitted lines —
        // only right-margin wraps coalesce, never real line feeds.
        List<string> emitted = FeedThroughEmulator("first line\r\nsecond line\r\n");

        Assert.Equal(new[] { "first line", "second line" }, emitted);
    }

    // Drive raw bytes through a real 80-column emulator + LineExtractor and
    // collect the text of every non-prompt line the extractor emits.
    private static List<string> FeedThroughEmulator(string text)
    {
        TerminalEmulator emulator = new(80, 25);
        LineExtractor extractor = new(emulator);
        List<string> lines = new();
        extractor.LineEmitted += l => { if (!l.IsPromptLine) lines.Add(l.Text); };
        emulator.Feed(System.Text.Encoding.Latin1.GetBytes(text));
        return lines;
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
