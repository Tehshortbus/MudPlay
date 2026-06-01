using FujinTerm.Game.GameData;
using Xunit;

namespace FujinTerm.Tests;

public sealed class ClassTitleTableTests
{
    [Theory]
    [InlineData("Magebane",          "Witchunter")]      // distinctive Witchunter title
    [InlineData("Heroine",           "Warrior")]         // female side of "Hero|Heroine"
    [InlineData("Hero",              "Warrior")]         // male side of the same entry
    [InlineData("High Druid",        "Druid")]           // two-word title
    [InlineData("Acolyte",           "Cleric")]
    [InlineData("Pastor",            "Priest")]
    [InlineData("Mercenary",         "Warrior")]
    [InlineData("Pickpocket",        "Thief")]
    public void LookupClasses_ReturnsExpectedClass_ForDistinctiveTitle(string title, string expectedClass)
    {
        Assert.Contains(expectedClass, ClassTitleTable.LookupClasses(title));
    }

    [Fact]
    public void Apprentice_AppearsInEveryClass_ForLevel1()
    {
        // "Apprentice" is the level-1 title for all 15 classes.
        IReadOnlyList<string> hits = ClassTitleTable.LookupClasses("Apprentice");
        Assert.Equal(15, hits.Count);
    }

    [Fact]
    public void LookupLevelRange_ReturnsCorrectBand_ForUniqueTitle()
    {
        // Magebane is Witchunter levels 15-19 (zero-indexed positions 14-18).
        (int Min, int Max)? range = ClassTitleTable.LookupLevelRange("Magebane");
        Assert.Equal((15, 19), range);
    }

    [Fact]
    public void LookupLevelRange_SpansAllClasses_ForSharedLevel1Title()
    {
        // Apprentice = level 1 in every class.
        (int Min, int Max)? range = ClassTitleTable.LookupLevelRange("Apprentice");
        Assert.Equal((1, 1), range);
    }

    [Fact]
    public void LookupLevelRange_ReturnsNull_ForUnknownTitle()
    {
        Assert.Null(ClassTitleTable.LookupLevelRange("Definitely Not A Class Title"));
        Assert.Null(ClassTitleTable.LookupLevelRange(null));
        Assert.Null(ClassTitleTable.LookupLevelRange(""));
    }

    [Fact]
    public void FormatLevelRange_ShowsSingleLevel_WhenMinEqualsMax()
    {
        Assert.Equal("7",    ClassTitleTable.FormatLevelRange((7, 7)));
        Assert.Equal("7-10", ClassTitleTable.FormatLevelRange((7, 10)));
    }
}
