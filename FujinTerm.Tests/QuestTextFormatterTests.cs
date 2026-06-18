using System;
using System.Linq;
using FujinTerm.Game.Quests;
using FujinTerm.ViewModels.CharacterWorkshop;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="QuestTextFormatter"/>'s data-independent logic: the multi-part band
/// title ("Good 1" from the GoodQuest flag) and the checkbox-markdown parser that
/// classifies a step line as a tickable checkbox or a plain context label. The
/// cache-dependent <see cref="QuestTextFormatter.StepLines"/> is covered by the
/// crawler's step-range tests; here we pin the pure string behaviour.
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
}
