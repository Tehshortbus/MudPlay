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
    public void Snapshot_IsScrollbackHistoryOnly()
    {
        TerminalEmulator emu = WithScrolledOffHistory(12);
        int scrolledOff = emu.Screen.Scrollback.Count;
        Assert.True(scrolledOff > 0, "test setup should scroll some rows off the top");

        BackscrollViewModel vm = new(emu);

        // The window mirrors the scrollback ring exactly — history that scrolled
        // off the top — and never includes the still-on-screen lines.
        Assert.Equal(scrolledOff, vm.Rows.Count);
        Assert.DoesNotContain(vm.Rows, r => r.PlainText.Contains("line011"));
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
}
