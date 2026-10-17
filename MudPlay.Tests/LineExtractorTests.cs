using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// LineExtractor: the pure cell-row → EmittedLine conversion, and the event
// path from the emulator's scrollback through the extractor.
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

        // Gossip is chat, so it arrives on the chat lane (LineEmitted withholds it).
        List<string> emitted = FeedThroughEmulator(body + "\r\n", chatLane: true);

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

    // A search of a room with no item cap is one line over a thousand rows. The
    // text and the colour of every cell must come through the stitch whole.
    [Fact]
    public void ThousandRowWrap_JoinsTextAndAttributesWhole()
    {
        string floor = HugeFloor.Line(4000);
        Assert.True(floor.Length > 80 * 800);

        TerminalEmulator emulator = new(80, 25);
        LineExtractor extractor = new(emulator);
        List<LineExtractor.EmittedLine> emitted = new();
        extractor.LineEmitted += emitted.Add;
        // Cyan from the first character, as the game colours a floor list.
        emulator.Feed(System.Text.Encoding.Latin1.GetBytes("\u001b[0;36m" + floor + "\r\n"));

        LineExtractor.EmittedLine line = Assert.Single(emitted);
        Assert.Equal(floor, line.Text);
        Assert.Equal(floor.Length, line.Attributes.Length);
        Assert.All(line.Attributes, a => Assert.Equal(line.Attributes[0], a));
        Assert.NotEqual(CellAttributes.Default, line.Attributes[0]);
    }

    [Fact]
    public void ALineItsSubscribersDwellOn_IsReportedAsSlow()
    {
        TerminalEmulator emulator = new(80, 25);
        ManualClock clock = new();
        LineExtractor extractor = new(emulator, clock.Timestamp);
        TimeSpan dwell = LineExtractor.SlowLineThreshold + TimeSpan.FromMilliseconds(50);
        extractor.LineEmitted += l =>
        {
            if (l.Text == "slow") clock.Advance(dwell);
            // A line read just short of the threshold is not slow.
            if (l.Text == "quick") clock.Advance(LineExtractor.SlowLineThreshold - TimeSpan.FromMilliseconds(1));
        };
        List<(string Text, TimeSpan Took)> slow = new();
        extractor.SlowLine += (l, took) => slow.Add((l.Text, took));

        emulator.Feed(System.Text.Encoding.Latin1.GetBytes("quick\r\nslow\r\n"));

        (string text, TimeSpan took) = Assert.Single(slow);
        Assert.Equal("slow", text);
        Assert.True(took >= LineExtractor.SlowLineThreshold);
        Assert.Equal(dwell, took);
    }

    // The slow line is reported as the chat subscriber saw it, so the log can say a
    // player's words were what it was reading without holding any of them.
    [Fact]
    public void ASlowChatLine_IsReportedAsChat()
    {
        TerminalEmulator emulator = new(80, 25);
        ManualClock clock = new();
        LineExtractor extractor = new(emulator, clock.Timestamp);
        extractor.ChatLineEmitted += _ =>
            clock.Advance(LineExtractor.SlowLineThreshold + TimeSpan.FromMilliseconds(50));
        List<(LineExtractor.EmittedLine Line, TimeSpan Took)> slow = new();
        extractor.SlowLine += (l, took) => slow.Add((l, took));

        emulator.Feed(System.Text.Encoding.Latin1.GetBytes("Phrixas telepaths: QZXJ\r\n"));

        (LineExtractor.EmittedLine line, TimeSpan took) = Assert.Single(slow);
        Assert.True(line.IsChat);
        Assert.Contains("chat line", LineExtractor.SlowLineNotice(line, took));
    }

    // A slow line may be a telepath or a row of the board's login. The notice gives
    // its length, its kind and the time it took, and not one character of it.
    [Theory]
    [InlineData(false, false, "-character line")]
    [InlineData(true, false, "-character chat line")]
    [InlineData(false, true, "-character prompt line")]
    public void TheSlowLineNotice_CarriesNoneOfTheLinesText(bool chat, bool prompt, string kind)
    {
        // Characters the notice's own wording never uses.
        const string text = "QZXJ#@~QZXJ_^|WVUK";
        LineExtractor.EmittedLine line = new(
            text, new CellAttributes[text.Length], FixedNow, IsPromptLine: prompt, IsChat: chat);

        string notice = LineExtractor.SlowLineNotice(line, TimeSpan.FromMilliseconds(2913));

        Assert.Equal($"the client stood still for 2913 ms reading a {text.Length}{kind}", notice);
        Assert.DoesNotContain(notice, c => text.Contains(c));
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
    // collect the text of every non-prompt line the extractor emits — from the
    // server-output lane, or the chat lane when chatLane is set.
    private static List<string> FeedThroughEmulator(string text, bool chatLane = false)
    {
        TerminalEmulator emulator = new(80, 25);
        LineExtractor extractor = new(emulator);
        List<string> lines = new();
        Action<LineExtractor.EmittedLine> collect = l => { if (!l.IsPromptLine) lines.Add(l.Text); };
        if (chatLane) extractor.ChatLineEmitted += collect;
        else extractor.LineEmitted += collect;
        emulator.Feed(System.Text.Encoding.Latin1.GetBytes(text));
        return lines;
    }

    // ----- Prompt/content split (PromptPrefix) -------------------------------

    // A "(Meditating)"/"(Resting)" tag on the prompt row sits AFTER "]:", not
    // inside the brackets (DefaultPatterns.StatusLine's stateb group) — the split
    // must consume it as part of the prompt half too, or a move typed while it's
    // showing splits as content " (Meditating) n" instead of "n" and
    // InboundMoveEchoScanner can't parse the echo, leaving RoomTracker stuck
    // treating the move's own landing as an ambiguous re-look (report
    // paradigm-20260913-210626: held Pending in an identically-named room until
    // a manual `rm`).
    [Fact]
    public void PromptSplit_TrailingMeditatingTag_ExcludedFromContent()
    {
        List<string> emitted = FeedThroughEmulator("[HP=100/MA=50]: (Meditating) n\r\n");

        string content = Assert.Single(emitted);
        Assert.Equal("n", content.Trim());
    }

    // A resting prompt with nothing typed after it must be a bare prompt row, not
    // a prompt plus a "(Resting)" content line. That stray content reaches
    // RoomTracker.NoteInboundMoveEcho, which latches _everReceivedEcho on ANY echo
    // — so a player who merely rests loses the timing fallback the re-look guard
    // depends on when the statline isn't actually echo-readable.
    [Fact]
    public void PromptSplit_BareRestingPrompt_EmitsNoContentLine()
    {
        Assert.Empty(FeedThroughEmulator("[HP=100/MA=50]: (Resting)\r\n"));
    }

    // The other shape the game prints the tag in — inside the brackets, before
    // "]:". Already covered by the prompt regex's [^\]]* body; pinned so tightening
    // that character class can't silently start leaking "(Resting) " into content.
    [Fact]
    public void PromptSplit_InBracketRestingTag_ExcludedFromContent()
    {
        List<string> emitted = FeedThroughEmulator("[HP=44/KAI=2 (Meditating) ]: n\r\n");

        string content = Assert.Single(emitted);
        Assert.Equal("n", content.Trim());
    }

    // Helper: build a row whose first text.Length cells
    // hold text at default attributes, then pad blanks
    // to paddedTo. The pads use default background so
    // trim drops them.
    private static Cell[] MakeRow(string text, int paddedTo)
    {
        Cell[] cells = new Cell[paddedTo];
        for (int i = 0; i < text.Length; i++) cells[i] = new Cell(text[i], CellAttributes.Default);
        for (int i = text.Length; i < paddedTo; i++) cells[i] = Cell.Blank;
        return cells;
    }
}
