using System.Collections.Generic;
using System.IO;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class RoomTooltipBuilderTests : IDisposable
{
    private readonly string _root;
    private readonly string _setName;

    public RoomTooltipBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-tooltip-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        _setName = "set";
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // ----- Lair tag parsing -----------------------------------------

    [Theory]
    [InlineData("(Max 2): 1141,2175,2176,[5-6-8-2]", 2)]
    [InlineData("(Max 5): 53",                       5)]
    [InlineData("(Max 10): 1,2,3",                  10)]
    public void TryParseLairMax_PullsCount(string tag, int expected)
    {
        Assert.True(RoomTooltipBuilder.TryParseLairMax(tag, out int max));
        Assert.Equal(expected, max);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nothing here")]
    [InlineData("(Min 3)")]                   // wrong keyword
    public void TryParseLairMax_RejectsNonLair(string? tag)
    {
        Assert.False(RoomTooltipBuilder.TryParseLairMax(tag, out _));
    }

    [Fact]
    public void ParseLairTag_NMR183_HandlesTrailingGroupBracket()
    {
        RoomTooltipBuilder.ParseLairTag("(Max 2): 1141,2175,2176,[5-6-8-2]",
            out int? max, out IReadOnlyList<int> ids);
        Assert.Equal(2, max);
        Assert.Equal(new[] { 1141, 2175, 2176 }, ids);
    }

    [Fact]
    public void ParseLairTag_PreNMR183_NoTrailingBracket_ReadsAllIds()
    {
        RoomTooltipBuilder.ParseLairTag("(Max 3): 10,20,30",
            out int? max, out IReadOnlyList<int> ids);
        Assert.Equal(3, max);
        Assert.Equal(new[] { 10, 20, 30 }, ids);
    }

    // ----- Build ----------------------------------------------------

    private const string Rooms = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Town Gates",
            "Light": 0, "Shop": 5, "Spell": 0, "Lair": "", "Delay": 5,
            "N": "1/2", "S": "0", "E": "1/3 (Door)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "North Square",
            "Light": -180, "Shop": 0, "Spell": 0, "Lair": "(Max 2): 100,101", "Delay": 5,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Inn",
            "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "1/1 (Door)",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private const string Shops = """
        [
          { "Number": 5, "Name": "Silvermere Bank" }
        ]
        """;

    private const string Spells = """
        [
          { "Number": 42, "Name": "Heal" }
        ]
        """;

    private const string Monsters = """
        [
          { "Number": 100, "Name": "Sewer Rat" },
          { "Number": 101, "Name": "Sewer Snake" }
        ]
        """;

    private (RoomGraphManager Graph, GameDataCache Cache) NewGraph()
    {
        string setRoot = Path.Combine(_root, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"),    Rooms);
        File.WriteAllText(Path.Combine(setRoot, "Shops.json"),    Shops);
        File.WriteAllText(Path.Combine(setRoot, "Spells.json"),   Spells);
        File.WriteAllText(Path.Combine(setRoot, "Monsters.json"), Monsters);
        GameDataCache cache = new(_root);
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        return (graph, cache);
    }

    [Fact]
    public void Build_NameLine_IsFirst()
    {
        var (graph, cache) = NewGraph();
        Room room = graph.GetRoom(new RoomKey(1, 1))!;

        string text = RoomTooltipBuilder.Build(room, graph, cache);

        Assert.StartsWith("Town Gates (1/1)", text);
    }

    [Fact]
    public void Build_ResolvesShopName()
    {
        var (graph, cache) = NewGraph();
        Room room = graph.GetRoom(new RoomKey(1, 1))!;
        string text = RoomTooltipBuilder.Build(room, graph, cache);
        Assert.Contains("Shop: Silvermere Bank", text);
    }

    [Fact]
    public void Build_ResolvesLairMonsterNames_AndMaxRegen()
    {
        var (graph, cache) = NewGraph();
        Room room = graph.GetRoom(new RoomKey(1, 2))!;
        string text = RoomTooltipBuilder.Build(room, graph, cache);

        Assert.Contains("Also Here (2): Sewer Rat, Sewer Snake", text);
        Assert.Contains("Max Regen: 2 @ 4m 30s", text);     // Delay=5 → 4m 30s
    }

    [Fact]
    public void Build_LightDescription_RenderedForDarkRooms()
    {
        var (graph, cache) = NewGraph();
        Room room = graph.GetRoom(new RoomKey(1, 2))!;     // Light = -180
        string text = RoomTooltipBuilder.Build(room, graph, cache);

        Assert.Contains("very dark", text);
        Assert.Contains("Room Light: -180", text);
    }

    [Fact]
    public void Build_LightDescription_SkippedForLitRooms()
    {
        var (graph, cache) = NewGraph();
        Room room = graph.GetRoom(new RoomKey(1, 1))!;     // Light = 0
        string text = RoomTooltipBuilder.Build(room, graph, cache);

        Assert.DoesNotContain("dimly lit", text);
        Assert.DoesNotContain("pitch black", text);
        Assert.DoesNotContain("Room Light:", text);         // Light==0 omitted
    }

    [Fact]
    public void Build_ExitsList_ListsAllDirections_WithDestinationNames()
    {
        var (graph, cache) = NewGraph();
        Room room = graph.GetRoom(new RoomKey(1, 1))!;
        string text = RoomTooltipBuilder.Build(room, graph, cache);

        Assert.Contains("Obvious exits:", text);
        Assert.Contains("north → North Square (1/2)", text);
        Assert.Contains("east → Inn (1/3) (Door)", text);
    }

    [Fact]
    public void Build_NoGameDataCache_FallsBackToIdNumbers()
    {
        var (graph, _) = NewGraph();
        Room room = graph.GetRoom(new RoomKey(1, 1))!;
        string text = RoomTooltipBuilder.Build(room, graph, data: null);

        Assert.Contains("Shop: #5", text);
    }

    [Fact]
    public void Build_FieldOrder_NameAlsoHereBlankShopBlankExitsBlankLightLightDescMaxRegen()
    {
        var (graph, cache) = NewGraph();
        Room dark = graph.GetRoom(new RoomKey(1, 2))!;     // dark lair room
        string text = RoomTooltipBuilder.Build(dark, graph, cache);

        // Sequential order: Name → Also Here → exits → Room Light → light desc → Max Regen.
        // The light-description phrase now sits BELOW the numeric
        // "Room Light: -N" line (per the user's request); 1/2 has
        // no Shop / Spell, so no shop section.
        int posName   = text.IndexOf("North Square (1/2)");
        int posAlso   = text.IndexOf("Also Here (2):");
        int posExits  = text.IndexOf("Obvious exits:");
        int posRLight = text.IndexOf("Room Light: -180");
        int posDesc   = text.IndexOf("very dark");
        int posRegen  = text.IndexOf("Max Regen: 2");

        Assert.True(posName < posAlso);
        Assert.True(posAlso < posExits);
        Assert.True(posExits < posRLight);
        Assert.True(posRLight < posDesc);
        Assert.True(posDesc < posRegen);
    }
}
