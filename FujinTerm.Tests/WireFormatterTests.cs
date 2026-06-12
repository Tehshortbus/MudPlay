using System.Text;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="WireFormatter"/> — raw vs stripped rendering of wire bytes for
/// the Wire Inspector. Focus: the stripped pane drops the "^M" CR marker
/// while preserving the line break each carriage return implies.
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
    public void RenderStripped_Crlf_CollapsesToSingleBreak()
    {
        string s = WireFormatter.RenderStripped(Latin1("hi\r\nthere"));
        Assert.Equal("hi\nthere", s);
    }

    [Fact]
    public void RenderStripped_BareCr_BecomesBreak()
    {
        string s = WireFormatter.RenderStripped(Latin1("hi\rthere"));
        Assert.Equal("hi\nthere", s);
    }

    [Fact]
    public void RenderStripped_NoCrMarkerRemains()
    {
        string s = WireFormatter.RenderStripped(Latin1("a\r\nb\rc\r\n"));
        Assert.DoesNotContain("^M", s);
        Assert.Equal("a\nb\nc\n", s);
    }

    [Fact]
    public void RenderStripped_RemovesCsiAndCrTogether()
    {
        // ESC[0m colour reset followed by CRLF — both vanish, break stays.
        string s = WireFormatter.RenderStripped(Latin1("red\x1b[0m\r\nnext"));
        Assert.Equal("red\nnext", s);
    }
}
