using System.Linq;
using MudPlay.Game.Emotes;
using Xunit;

namespace MudPlay.Tests;

// The emote segmentation: word shortcodes, classic emoticons (whitespace-bounded,
// case-sensitive), image emotes, and the literal fall-through for unknown codes.
public sealed class EmoteScannerTests
{
    private static readonly EmoteScanner S = EmoteScanner.BuiltIn;

    [Fact]
    public void PlainText_IsOneTextSegment()
    {
        var segs = S.Scan("just a normal line");
        var seg = Assert.Single(segs);
        Assert.Equal(EmoteSegmentKind.Text, seg.Kind);
        Assert.Equal("just a normal line", seg.Text);
    }

    [Fact]
    public void WordShortcode_BecomesEmoji()
    {
        var segs = S.Scan(":lol:");
        var seg = Assert.Single(segs);
        Assert.Equal(EmoteSegmentKind.Emoji, seg.Kind);
        Assert.Equal("😂", seg.Text);
    }

    [Fact]
    public void Emoticon_AfterSpace_BecomesEmoji()
    {
        var segs = S.Scan("hello :)");
        Assert.Equal(2, segs.Count);
        Assert.Equal("hello ", segs[0].Text);
        Assert.Equal(EmoteSegmentKind.Emoji, segs[1].Kind);
        Assert.Equal("🙂", segs[1].Text);
    }

    [Fact]
    public void Emoticon_MustBeWhitespaceBounded()
    {
        // No space around ":)" — it's part of a token, so it stays literal text.
        var segs = S.Scan("word:)word");
        var seg = Assert.Single(segs);
        Assert.Equal(EmoteSegmentKind.Text, seg.Kind);
        Assert.Equal("word:)word", seg.Text);
    }

    [Fact]
    public void ClockTime_IsNotConverted()
    {
        var seg = Assert.Single(S.Scan("raid at 8:00 pm"));
        Assert.Equal(EmoteSegmentKind.Text, seg.Kind);
    }

    [Fact]
    public void ImageEmote_CarriesAvaresPayload()
    {
        var seg = Assert.Single(S.Scan(":pepecry:"));
        Assert.Equal(EmoteSegmentKind.Image, seg.Kind);
        Assert.Contains("pepecry.png", seg.Payload);
        Assert.Equal("pepecry", seg.Shortcode);
    }

    [Fact]
    public void Shortcode_IsCaseInsensitive()
    {
        Assert.Equal(EmoteSegmentKind.Image, Assert.Single(S.Scan(":MonkaS:")).Kind);
    }

    [Fact]
    public void Emoticon_IsCaseSensitive_LowerDIsNotGrin()
    {
        // ":d" is neither the ":D" emoticon nor a closed ":word:" shortcode → literal.
        var seg = Assert.Single(S.Scan("nice :d"));
        Assert.Equal(EmoteSegmentKind.Text, seg.Kind);
    }

    [Fact]
    public void UnknownShortcode_StaysLiteral()
    {
        var seg = Assert.Single(S.Scan(":notarealone:"));
        Assert.Equal(EmoteSegmentKind.Text, seg.Kind);
        Assert.Equal(":notarealone:", seg.Text);
    }

    [Fact]
    public void MixedLine_SplitsInOrder()
    {
        var segs = S.Scan("gg :fire: :)");
        Assert.Equal(4, segs.Count);
        Assert.Equal("gg ", segs[0].Text);
        Assert.Equal("🔥", segs[1].Text);
        Assert.Equal(" ", segs[2].Text);
        Assert.Equal("🙂", segs[3].Text);
    }

    [Fact]
    public void Catalog_HasBundledPepeSet()
    {
        // All 20 bundled image emotes resolve to an Image emote.
        string[] pepe = { "monkas", "pepecry", "sadge", "sadge2", "pepejesus", "copium",
            "pepeclown", "pepeclown2", "pepehands", "sadgepray", "peperage", "pepeno",
            "pepeok", "pepethink", "pepedeath", "prayge", "pepecringe", "pepehmm",
            "clownge", "poggies" };
        foreach (string name in pepe)
        {
            Assert.True(EmoteCatalog.BuiltIn.TryGetShortcode(name, out var e), name);
            Assert.Equal(EmoteKind.Image, e.Kind);
        }
    }
}
