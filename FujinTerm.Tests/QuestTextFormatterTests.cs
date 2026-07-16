using System;
using System.IO;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Game.Quests;
using FujinTerm.Services;
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

    // ----- Step: seed-style auto-draft rendering -----------------------------
    //
    // Step resolves item / monster names off the cache; with no active set those
    // lookups fall back to "#id" / "monster #id", which is enough to pin the
    // seed-style shape (room-link-first, backtick command, kill/obtain narration,
    // trailing item note) without loading a real game-data set.
    private static readonly GameDataCache NoSet = new(Path.GetTempPath());

    private static QuestStep MakeStep(int order, string? command, string? location,
        int[]? required = null, int[]? turnIn = null, int[]? granted = null) =>
        new(order, command, location,
            required ?? Array.Empty<int>(), turnIn ?? Array.Empty<int>(), granted ?? Array.Empty<int>());

    [Fact]
    public void Step_CommandWithRoom_RendersRoomLinkThenBacktickCommand()
    {
        Assert.Equal("(9/1259) `ask old man phoenix`",
            QuestTextFormatter.Step(NoSet, MakeStep(1, "ask old man phoenix", "Room 9/1259")));
    }

    [Fact]
    public void Step_MonsterSourcedGrant_NarratesKillWithDrop()
    {
        Assert.Equal("kill monster #39 (#100)",
            QuestTextFormatter.Step(NoSet, MakeStep(2, null, "Monster #39", granted: new[] { 100 })));
    }

    [Fact]
    public void Step_CommandlessGrant_NarratesObtain()
    {
        Assert.Equal("obtain #100",
            QuestTextFormatter.Step(NoSet, MakeStep(3, null, "Textblock #5", granted: new[] { 100 })));
    }

    [Fact]
    public void Step_MultiRoomLocation_ListsEveryRoomAsItsOwnLink()
    {
        Assert.Equal("(5/294) (16/368) (1/527)",
            QuestTextFormatter.Step(NoSet, MakeStep(1, null, "Room 5/294, Room 16/368, Room 1/527")));
    }

    [Fact]
    public void Step_RequiredItem_TrailsAsRequiredNote()
    {
        Assert.Equal("(6/1357) `ask martok forge` (#100 required)",
            QuestTextFormatter.Step(NoSet, MakeStep(4, "ask martok forge", "Room 6/1357", required: new[] { 100 })));
    }

    [Fact]
    public void Step_TurnInItem_TrailsAsTurnInNote()
    {
        Assert.Equal("(1/1333) `ask annora head` (turn in #100)",
            QuestTextFormatter.Step(NoSet, MakeStep(5, "ask annora head", "Room 1/1333", turnIn: new[] { 100 })));
    }

    [Fact]
    public void Step_CommandGrant_TrailsAsGetNote()
    {
        Assert.Equal("(9/1259) `ask smith key` (get #100)",
            QuestTextFormatter.Step(NoSet, MakeStep(6, "ask smith key", "Room 9/1259", granted: new[] { 100 })));
    }

    [Fact]
    public void Step_NoRoomCommandOrItems_FallsBackToStepOrdinal()
    {
        Assert.Equal("Step 7",
            QuestTextFormatter.Step(NoSet, MakeStep(7, null, "Textblock #5")));
    }
}
