using System.Text;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="WireFormatter"/> — raw vs stripped rendering of wire bytes for
/// the Wire Inspector. Focus: the stripped pane drops CR markers, collapses
/// the backspace overstrike MajorMUD uses to highlight an exit's first
/// letter, and removes CSI escapes while keeping LF breaks.
/// </summary>
public sealed class WireFormatterTests
{
    private static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);

    [Fact]
    public void RenderRaw_KeepsCrAsCaretMarker_AndLfAsBreak()
    {
        string raw = WireFormatter.RenderRaw(Latin1("hi\r\nthere"));
        Assert.Equal("hi^M\nthere", raw);
    }

    [Fact]
    public void RenderRaw_KeepsBackspaceAsCaretMarker()
    {
        // Raw pane is for byte-level debugging — the overstrike stays visible.
        Assert.Equal("nF^Horth", WireFormatter.RenderRaw(Latin1("nF\borth")));
    }

    [Fact]
    public void RenderStripped_RemovesCr()
    {
        Assert.Equal("hi\nthere", WireFormatter.RenderStripped(Latin1("hi\r\nthere")));
        Assert.DoesNotContain("^M", WireFormatter.RenderStripped(Latin1("a\r\nb\r\n")));
    }

    [Fact]
    public void RenderStripped_CollapsesBackspaceOverstrike()
    {
        // F<BS>o means the 'o' overwrites the highlighted 'F'.
        Assert.Equal("north", WireFormatter.RenderStripped(Latin1("nF\borth")));
    }

    [Fact]
    public void RenderStripped_ObviousExitsLine_RendersClean()
    {
        byte[] line = Latin1("Obvious exits: nF\borth, eI\bast, wW\best, dO\bown\r");
        Assert.Equal("Obvious exits: north, east, west, down",
            WireFormatter.RenderStripped(line));
    }

    [Fact]
    public void RenderStripped_RemovesCsiEscapes()
    {
        Assert.Equal("red next", WireFormatter.RenderStripped(Latin1("red \x1b[0mnext")));
    }
}
