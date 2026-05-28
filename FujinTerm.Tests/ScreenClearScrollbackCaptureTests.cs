using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Regression: when a server clears the screen via CSI ED (2J or partial),
/// the rows being cleared must land in the scrollback first if they had
/// content. Without this fix, BBS welcome banners and any other absolute-
/// positioned art that gets <c>2J</c>'d before the next redraw never makes
/// it into the backscroll export.
/// </summary>
public sealed class ScreenClearScrollbackCaptureTests
{
    private static void WriteRow(TerminalScreen screen, int y, string text)
    {
        for (int x = 0; x < text.Length && x < screen.Cols; x++)
            screen.Put(x, y, new Cell(text[x], default));
    }

    [Fact]
    public void ClearAll_CapturesNonBlankRowsToScrollback()
    {
        TerminalScreen screen = new(80, 25);
        WriteRow(screen, 0, "Welcome to PlayPen BBS");
        WriteRow(screen, 5, "Lines in Use: 86");
        // Row 10 stays blank.

        screen.ClearAll(default);

        // Two rows had content; both should be in scrollback.
        Assert.Equal(2, screen.Scrollback.Count);

        string first = new(screen.Scrollback[0].Cells.Select(c => c.Char).ToArray());
        string second = new(screen.Scrollback[1].Cells.Select(c => c.Char).ToArray());
        Assert.StartsWith("Welcome to PlayPen BBS", first.TrimEnd());
        Assert.StartsWith("Lines in Use: 86",        second.TrimEnd());
    }

    [Fact]
    public void ClearAll_BlankScreen_AppendsNothing()
    {
        TerminalScreen screen = new(80, 25);
        screen.ClearAll(default);

        Assert.Equal(0, screen.Scrollback.Count);
    }

    [Fact]
    public void ClearRowsInclusive_CapturesOnlyTheGivenRange()
    {
        TerminalScreen screen = new(80, 25);
        WriteRow(screen, 1, "above the range");
        WriteRow(screen, 4, "in the range");
        WriteRow(screen, 10, "below the range");

        screen.ClearRowsInclusive(3, 6, default);

        // Only row 4 fell in the range.
        Assert.Equal(1, screen.Scrollback.Count);
        string captured = new(screen.Scrollback[0].Cells.Select(c => c.Char).ToArray());
        Assert.StartsWith("in the range", captured.TrimEnd());
    }

    [Fact]
    public void ClearRow_SingleRowClear_DoesNotCapture()
    {
        // Single-row clears (CSI K) come from cursor-positioning artefacts —
        // status-line rewrites, backspace overstrike, user-input echo. They
        // would over-capture noise.
        TerminalScreen screen = new(80, 25);
        WriteRow(screen, 0, "user-typed line");

        screen.ClearRow(0, 0, screen.Cols - 1, default);

        Assert.Equal(0, screen.Scrollback.Count);
    }

    [Fact]
    public void ScreenRedrawSequence_PreservesEveryFrame()
    {
        // Simulates the BBS welcome-banner-then-Who's-Online pattern:
        // server draws frame A, sends 2J, draws frame B, sends 2J, draws frame C.
        // Each frame should survive in the scrollback (frame C is now on screen).
        TerminalScreen screen = new(80, 25);

        WriteRow(screen, 0, "FRAME A");
        screen.ClearAll(default);

        WriteRow(screen, 0, "FRAME B");
        screen.ClearAll(default);

        WriteRow(screen, 0, "FRAME C");
        // Frame C still on screen, not yet in scrollback.

        Assert.Equal(2, screen.Scrollback.Count);
        string a = new(screen.Scrollback[0].Cells.Select(c => c.Char).ToArray());
        string b = new(screen.Scrollback[1].Cells.Select(c => c.Char).ToArray());
        Assert.StartsWith("FRAME A", a.TrimEnd());
        Assert.StartsWith("FRAME B", b.TrimEnd());
    }
}
