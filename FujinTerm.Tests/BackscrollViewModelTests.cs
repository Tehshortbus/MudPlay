using System.Linq;
using System.Text;
using FujinTerm.Terminal;
using FujinTerm.ViewModels;
using Xunit;

namespace FujinTerm.Tests;

// Pins the freeze-at-open contract: the backscroll window shows a snapshot taken
// when it opened and never mutates while more output streams in (that live-append
// was what lagged badly when following a fast party leader). The ring buffer keeps
// capturing behind the frozen window, so reopening catches up with nothing missed.
public class BackscrollViewModelTests
{
    private static void Feed(TerminalEmulator emu, string text)
        => emu.Feed(Encoding.Latin1.GetBytes(text));

    // A short screen so a handful of fed lines are enough to scroll rows off the
    // top into the ScrollbackBuffer.
    private static TerminalEmulator WithScrolledOffHistory(int lineCount, int rows = 5)
    {
        TerminalEmulator emu = new(80, rows);
        for (int i = 0; i < lineCount; i++) Feed(emu, $"line{i:D3}\r\n");
        return emu;
    }

    [Fact]
    public void Snapshot_IsHistoryFollowedByLiveScreen()
    {
        TerminalEmulator emu = WithScrolledOffHistory(12);
        int scrolledOff = emu.Screen.Scrollback.Count;
        Assert.True(scrolledOff > 0, "test setup should scroll some rows off the top");

        BackscrollViewModel vm = new(emu);

        // The snapshot is the scrolled-off history plus a one-shot copy of the
        // still-on-screen rows, so the transcript ends where the live terminal
        // sits. Both the earliest history line and the last on-screen line show.
        Assert.True(vm.Rows.Count > scrolledOff,
            "snapshot must append the on-screen rows after the scrolled-off history");
        Assert.Contains(vm.Rows, r => r.PlainText.Contains("line000"));
        Assert.Contains(vm.Rows, r => r.PlainText.Contains("line011"));
    }

    [Fact]
    public void OpenSnapshot_DoesNotChangeWhenMoreOutputArrives()
    {
        TerminalEmulator emu = WithScrolledOffHistory(12);
        BackscrollViewModel vm = new(emu);

        int frozenCount = vm.Rows.Count;
        string[] frozen = vm.Rows.Select(r => r.PlainText).ToArray();

        // Stream a lot more output while the window is "open".
        for (int i = 0; i < 40; i++) Feed(emu, $"post{i:D3}\r\n");

        Assert.Equal(frozenCount, vm.Rows.Count);
        Assert.Equal(frozen, vm.Rows.Select(r => r.PlainText).ToArray());
        Assert.DoesNotContain(vm.Rows, r => r.PlainText.Contains("post"));
    }

    [Fact]
    public void Ring_KeepsCapturingBehindFrozenWindow()
    {
        TerminalEmulator emu = WithScrolledOffHistory(12);
        BackscrollViewModel vm = new(emu);
        int before = emu.Screen.Scrollback.Count;

        for (int i = 0; i < 40; i++) Feed(emu, $"post{i:D3}\r\n");

        Assert.True(emu.Screen.Scrollback.Count > before,
            "the ring must keep capturing while the frozen window is open");
        Assert.DoesNotContain(vm.Rows, r => r.PlainText.Contains("post"));
    }

    [Fact]
    public void Reopen_PicksUpEverythingSinceWithNothingMissed()
    {
        TerminalEmulator emu = WithScrolledOffHistory(12);
        BackscrollViewModel first = new(emu);
        Assert.DoesNotContain(first.Rows, r => r.PlainText.Contains("post"));

        for (int i = 0; i < 40; i++) Feed(emu, $"post{i:D3}\r\n");

        BackscrollViewModel reopened = new(emu);
        // post000 and post030 have long since scrolled off the 5-row screen into
        // the ring; the last handful (post039) may still be on-screen, so the
        // scrollback-only snapshot need not contain them.
        Assert.Contains(reopened.Rows, r => r.PlainText.Contains("post000"));
        Assert.Contains(reopened.Rows, r => r.PlainText.Contains("post030"));
    }

    // Find Next walks the newest match first and works upward toward the oldest,
    // matching the window's newest-at-bottom orientation, then wraps back to the
    // newest after passing the top.
    [Fact]
    public void FindNext_WalksNewestToOldest_ThenWraps()
    {
        TerminalEmulator emu = new(80, 5);
        Feed(emu, "zzz first\r\n");
        Feed(emu, "filler a\r\n");
        Feed(emu, "zzz second\r\n");
        Feed(emu, "filler b\r\n");
        Feed(emu, "zzz third\r\n");
        Feed(emu, "filler c\r\n");

        BackscrollViewModel vm = new(emu);

        int landed = -1;
        vm.FindMatchRequested += (row, _, _) => landed = row;
        vm.SearchText = "zzz";

        List<string> visited = new();
        for (int i = 0; i < 4; i++)
        {
            vm.FindNextCommand.Execute(null);
            visited.Add(vm.Rows[landed].PlainText);
        }

        Assert.Equal(3, vm.MatchCount);
        Assert.Contains("third", visited[0]);   // newest match first
        Assert.Contains("second", visited[1]);
        Assert.Contains("first", visited[2]);    // oldest match last
        Assert.Contains("third", visited[3]);    // wraps back to the newest
    }
}
