using System;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Game.Quests;
using FujinTerm.ViewModels.CharacterWorkshop;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="QuestTextFormatter"/>'s data-independent logic: the multi-part band
/// title ("Good 1" from the GoodQuest flag), the checkbox-markdown parser that
/// classifies a step line as a tickable checkbox or a plain context label, and the
/// <c>(map/room)</c> coordinate splitter that isolates clickable walk-to links from
/// surrounding prose. The cache-dependent <see cref="QuestTextFormatter.StepLines"/>
/// is covered by the crawler's step-range tests; here we pin the pure string behaviour.
/// </summary>
public sealed class QuestTextFormatterTests
{
    [Fact]
    public void FallbackTitle_MultiPartBand_DropsQuestSuffixAndAppendsOrdinal()
    {
        // Flag 126 → "GoodQuest"; band 1 → "Good 1".
        var q = new CrawledQuest(126, 10, 10, Array.Empty<QuestBonus>(), Array.Empty<int>(), BandOrdinal: 1);
        Assert.Equal("Good 1", QuestTextFormatter.FallbackTitle(q));
    }

    [Fact]
    public void FallbackTitle_SinglePart_UsesFlagNameVerbatim()
    {
        var q = new CrawledQuest(126, 0, 0, Array.Empty<QuestBonus>(), Array.Empty<int>());
        Assert.Equal("GoodQuest", QuestTextFormatter.FallbackTitle(q));
    }

    [Theory]
    [InlineData("[] do thing", true, "do thing")]
    [InlineData("[ ] do thing", true, "do thing")]
    [InlineData("[x] done thing", true, "done thing")]
    [InlineData("[X] done thing", true, "done thing")]
    [InlineData("plain label line", false, "plain label line")]
    public void ParseStepLines_ClassifiesCheckboxVsLabel(string line, bool checkable, string text)
    {
        (bool Checkable, string Text) row = Assert.Single(QuestTextFormatter.ParseStepLines(line));
        Assert.Equal(checkable, row.Checkable);
        Assert.Equal(text, row.Text);
    }

    [Fact]
    public void ParseStepLines_SkipsBlankLines()
    {
        var rows = QuestTextFormatter.ParseStepLines("[] a\n\n   \n[] b").ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("a", rows[0].Text);
        Assert.Equal("b", rows[1].Text);
    }

    [Fact]
    public void SplitRoomLinks_IsolatesCoordinateAsLinkBetweenProseRuns()
    {
        var segs = QuestTextFormatter.SplitRoomLinks("sit throne @ (5/297) receive crown").ToList();
        Assert.Equal(3, segs.Count);
        Assert.Equal(("sit throne @ ", (RoomKey?)null), segs[0]);
        Assert.Equal(("(5/297)", (RoomKey?)new RoomKey(5, 297)), segs[1]);
        Assert.Equal((" receive crown", (RoomKey?)null), segs[2]);
    }

    [Fact]
    public void SplitRoomLinks_LeadingCoordinateEmitsNoEmptyProseRun()
    {
        var segs = QuestTextFormatter.SplitRoomLinks("(1/3) go north").ToList();
        Assert.Equal(2, segs.Count);
        Assert.Equal(("(1/3)", (RoomKey?)new RoomKey(1, 3)), segs[0]);
        Assert.Equal((" go north", (RoomKey?)null), segs[1]);
    }

    [Fact]
    public void SplitRoomLinks_MultipleCoordinates_EachBecomesItsOwnLink()
    {
        var segs = QuestTextFormatter.SplitRoomLinks("(1/2) then (3/4)").ToList();
        Assert.Equal(new RoomKey?[] { new RoomKey(1, 2), null, new RoomKey(3, 4) },
                     segs.Select(s => s.Room).ToArray());
    }

    [Fact]
    public void SplitRoomLinks_NoCoordinate_ReturnsSinglePlainRun()
    {
        var seg = Assert.Single(QuestTextFormatter.SplitRoomLinks("just plain prose"));
        Assert.Equal(("just plain prose", (RoomKey?)null), seg);
    }

    [Fact]
    public void SplitRoomLinks_OverRangeCoordinate_StaysFoldedInProse()
    {
        // A number too large for int isn't a real room — it must not become a link,
        // and the whole token stays in the prose run.
        var seg = Assert.Single(QuestTextFormatter.SplitRoomLinks("go (99999999999/3)"));
        Assert.Null(seg.Room);
        Assert.Equal("go (99999999999/3)", seg.Text);
    }

    [Fact]
    public void SplitRoomLinks_EmptyInput_ReturnsNoSegments()
    {
        Assert.Empty(QuestTextFormatter.SplitRoomLinks(string.Empty));
    }
}
