using MudPlay.Game.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class ClassTitleTableTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mudplay-titles-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // A set with the given Classes and Titles tables (null leaves the file out),
    // loaded by the cache as the client loads one, and the titles the client
    // builds from it.
    private ClassTitleTable TitlesOfSet(string classesJson, string? titlesJson)
    {
        string dir = Path.Combine(_root, "set");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Classes.json"), classesJson);
        if (titlesJson is not null) File.WriteAllText(Path.Combine(dir, "Titles.json"), titlesJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("set");
        return ClassTitleTable.ForSet(cache.GetTable("Titles"), cache.GetTable("Classes"));
    }

    private const string StockClasses =
        """[{"Number":1,"Name":"Warrior"},{"Number":8,"Name":"Thief"}]""";

    // Stock v1.11p's own Warrior and Thief rows (NMR 1.8.4 export).
    private const string StockTitles =
        """
        [{"Class":1,"Titles":"1-1:Apprentice,2-4:Warrior Novice,5-9:Grunt,10-14:Fighter,15-19:Veteran,20-24:Mercenary,25-29:Duelist,30-34:Dragoon,35-39:Gladiator,40-44:Myrmidon,45-49:Lord|Lady,50-54:Hero|Heroine,55-59:Weaponmaster|Weaponmistress,60-64:Warmonger,65-69:Warlord|Warlady,70-109:Dragonslayer"},
         {"Class":8,"Titles":"1-1:Apprentice,2-4:Thief Novice,5-9:Pickpocket,10-14:Cutpurse"}]
        """;

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
        Assert.Contains(expectedClass, ClassTitleTable.BuiltIn.LookupClasses(title));
    }

    [Fact]
    public void Apprentice_AppearsInEveryClass_ForLevel1()
    {
        // "Apprentice" is the level-1 title for all 15 classes.
        IReadOnlyList<string> hits = ClassTitleTable.BuiltIn.LookupClasses("Apprentice");
        Assert.Equal(15, hits.Count);
    }

    [Fact]
    public void LookupLevelRange_ReturnsCorrectBand_ForUniqueTitle()
    {
        Assert.Equal((15, 19), ClassTitleTable.BuiltIn.LookupLevelRange("Magebane"));
    }

    [Fact]
    public void LookupLevelRange_SpansAllClasses_ForSharedLevel1Title()
    {
        Assert.Equal((1, 1), ClassTitleTable.BuiltIn.LookupLevelRange("Apprentice"));
    }

    [Fact]
    public void LookupLevelRange_ReturnsNull_ForUnknownTitle()
    {
        Assert.Null(ClassTitleTable.BuiltIn.LookupLevelRange("Definitely Not A Class Title"));
        Assert.Null(ClassTitleTable.BuiltIn.LookupLevelRange(null));
        Assert.Null(ClassTitleTable.BuiltIn.LookupLevelRange(""));
    }

    [Fact]
    public void FormatLevelRange_ShowsSingleLevel_WhenMinEqualsMax()
    {
        Assert.Equal("7",    ClassTitleTable.FormatLevelRange((7, 7)));
        Assert.Equal("7-10", ClassTitleTable.FormatLevelRange((7, 10)));
    }

    // Paradigm's Thief title at 55-59 is "Master Rogue", one title. The old built-in
    // list had it as two, "Master" then "Rogue", which pushed every later Thief
    // title five levels on.
    [Fact]
    public void BuiltIn_MasterRogue_IsOneThiefTitle()
    {
        Assert.Equal((55, 59), ClassTitleTable.BuiltIn.LookupLevelRange("Master Rogue"));
        Assert.Equal((40, 44), ClassTitleTable.BuiltIn.LookupLevelRange("Rogue"));
        Assert.Null(ClassTitleTable.BuiltIn.LookupLevelRange("Master"));
    }

    // A Stock Warrior of level 75 to 109 is a Dragonslayer. The built-in list has
    // Paradigm's titles there ("High Warlord" from 75).
    [Fact]
    public void ASetsTitlesTable_GivesItsOwnTitles()
    {
        ClassTitleTable titles = TitlesOfSet(StockClasses, StockTitles);

        Assert.True(titles.FromSet);
        Assert.Equal(2, titles.ClassCount);
        Assert.Equal((70, 109), titles.LookupLevelRange("Dragonslayer"));
        Assert.Null(titles.LookupLevelRange("High Warlord"));
        Assert.Equal(new[] { "Warrior" }, titles.LookupClasses("Lady"));
        Assert.Equal(new[] { "Thief" }, titles.LookupClasses("Cutpurse"));
    }

    // A realm's own class (MudRev's Necrolyte) has titles only its set can give.
    [Fact]
    public void ASetsOwnClass_IsKnownByItsTitles()
    {
        ClassTitleTable titles = TitlesOfSet(
            """[{"Number":4,"Name":"Necrolyte"}]""",
            """[{"Class":4,"Titles":"1-1:Apprentice,2-4:Necrolyte Novice,5-9:Bonepicker"}]""");

        Assert.Equal(new[] { "Necrolyte" }, titles.LookupClasses("Bonepicker"));
        Assert.Equal((5, 9), titles.LookupLevelRange("Bonepicker"));
    }

    // An older export has no Titles table, and a class row the Classes table
    // doesn't name, or an empty cell (the export's lone NUL), reads as nothing.
    [Fact]
    public void ASetWithoutUsableTitles_FallsBackToTheBuiltInList()
    {
        Assert.Same(ClassTitleTable.BuiltIn, TitlesOfSet(StockClasses, titlesJson: null));
        Assert.Same(ClassTitleTable.BuiltIn, TitlesOfSet(StockClasses,
            """[{"Class":99,"Titles":"1-1:Apprentice"},{"Class":1,"Titles":"\u0000"}]"""));
    }

    // A comma inside a title stays part of it.
    [Fact]
    public void ACommaInsideATitle_StaysInTheTitle()
    {
        ClassTitleTable titles = TitlesOfSet(StockClasses,
            """[{"Class":1,"Titles":"1-1:Apprentice,2-9:Sword, Shield,10-14:Fighter"}]""");

        Assert.Equal((2, 9), titles.LookupLevelRange("Sword, Shield"));
        Assert.Equal((10, 14), titles.LookupLevelRange("Fighter"));
    }
}
