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

    private const string ItemExitRooms = """
        [
          { "Map Number": 6, "Room Number": 79, "Name": "Rocky Path, Narrow Cliff",
            "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 5, "CMD": 0,
            "N": "6/78", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "6/80 (Item: 191)" },
          { "Map Number": 6, "Room Number": 78, "Name": "Rocky Path",
            "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 0, "CMD": 0,
            "N": "0", "S": "6/79", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 6, "Room Number": 80, "Name": "Rocky Path, Overhang",
            "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 0, "CMD": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "6/79", "D": "0" }
        ]
        """;

    private const string ItemExitItems = """
        [
          { "Number": 191, "Name": "rope and grapple" }
        ]
        """;

    [Fact]
    public void Build_ItemHintExit_ResolvesItemName()
    {
        // Live repro: 6/79 down "(Item: 191)" should render the item
        // name from Items.json, not just the bare hint.
        string setRoot = Path.Combine(_root, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"), ItemExitRooms);
        File.WriteAllText(Path.Combine(setRoot, "Items.json"), ItemExitItems);
        GameDataCache cache = new(_root);
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);

        Room room = graph.GetRoom(new RoomKey(6, 79))!;
        string text = RoomTooltipBuilder.Build(room, graph, cache);

        Assert.Contains("(Item: rope and grapple)", text);
        Assert.DoesNotContain("(Item)", text);
    }

    [Fact]
    public void Build_TrapExit_WithDamage_RendersDamageInline()
    {
        // Live repro: 2/1106 NW exit is "(Trap, 36 damage)" — the
        // tooltip should surface the damage figure so the user knows
        // what they're risking before walking into it.
        const string trapRooms = """
            [
              { "Map Number": 2, "Room Number": 1106, "Name": "Hillside Path, Guard Post",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 5, "CMD": 0,
                "NW": "2/1105 (Trap, 36 damage)", "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 2, "Room Number": 1105, "Name": "Hillside Path",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 0, "CMD": 0,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "2/1106", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        string setRoot = Path.Combine(_root, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"), trapRooms);
        GameDataCache cache = new(_root);
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);

        Room room = graph.GetRoom(new RoomKey(2, 1106))!;
        string text = RoomTooltipBuilder.Build(room, graph, cache);

        Assert.Contains("(Trap: 36 dmg)", text);
        Assert.DoesNotContain(" (Trap)", text);
    }

    [Fact]
    public void Build_AlsoHere_IncludesSummonedBossNotInLairTag()
    {
        // Live repro: 1/1678 Darkwood Forest, Webbed Clearing has no
        // lair tag entry for "giant spider" (Monster 52), but the
        // monster's "Summoned By" reads "Room 1/1678". The tooltip's
        // Also Here line used to omit the boss entirely.
        const string spawnRooms = """
            [
              { "Map Number": 1, "Room Number": 1678, "Name": "Darkwood Forest, Webbed Clearing",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 5, "CMD": 0,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        const string spawnMonsters = """
            [
              { "Number": 52, "Name": "giant spider", "RegenTime": 14, "Summoned By": "Room 1/1678" }
            ]
            """;
        string setRoot = Path.Combine(_root, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"),    spawnRooms);
        File.WriteAllText(Path.Combine(setRoot, "Monsters.json"), spawnMonsters);
        GameDataCache cache = new(_root);
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        MonsterSpawnIndex spawnIndex = new(cache);

        Room room = graph.GetRoom(new RoomKey(1, 1678))!;
        string text = RoomTooltipBuilder.Build(room, graph, cache, tbinfo: null, spawnIndex: spawnIndex);

        Assert.Contains("Also Here: giant spider", text);
    }

    [Fact]
    public void Build_MultiActionHiddenExit_RendersRequiredCommands()
    {
        // Live repro: room 10/271 west "(Hidden/Needs 1 Actions, any order)"
        // pairs with an action cell on the E field
        //   "Action [on the W exit of this room]: say 'Temar Eldanti', say Temar Eldanti, speak Temar Eldanti"
        // so the W exit unlocks once any one of those three phrases is
        // spoken. The tooltip used to just say "(MultiActionHidden)"
        // with no hint as to which phrase to type.
        const string multiActionRooms = """
            [
              { "Map Number": 10, "Room Number": 271, "Name": "Ancient Keep, Throne Room",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 5, "CMD": 0,
                "N": "10/270",
                "S": "0",
                "E": "Action [on the W exit of this room]: say 'Temar Eldanti', say Temar Eldanti, speak Temar Eldanti",
                "W": "10/272 (Hidden/Needs 1 Actions, any order)",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 10, "Room Number": 270, "Name": "Ancient Keep, Entrance",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 0, "CMD": 0,
                "N": "0", "S": "10/271", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 10, "Room Number": 272, "Name": "Huge Passage",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 0, "CMD": 0,
                "N": "0", "S": "0", "E": "10/271", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        string setRoot = Path.Combine(_root, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"), multiActionRooms);
        GameDataCache cache = new(_root);
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);

        Room room = graph.GetRoom(new RoomKey(10, 271))!;
        string text = RoomTooltipBuilder.Build(room, graph, cache);

        Assert.Contains("Needs 1 action: say 'Temar Eldanti' / say Temar Eldanti / speak Temar Eldanti", text);
        Assert.DoesNotContain("(MultiActionHidden)", text);
        // Per-step breakdown beneath the exit line — names the trigger
        // location (same room) + the alternative commands. Separate
        // surface from the inline summary so a glance at the tooltip
        // tells the user where to go without re-parsing the parens.
        Assert.Contains("1. here: say 'Temar Eldanti' / say Temar Eldanti / speak Temar Eldanti", text);
    }

    [Fact]
    public void Build_MultiActionHiddenExit_RemoteTrigger_NamesSourceRoom()
    {
        // When the action data lives in a DIFFERENT room than the
        // exit it unlocks, the per-step breakdown should call that
        // out so the user knows where to go and execute the command.
        // E.g. a "pull lever" in room 9/870 unlocking 9/1012's east
        // exit. The breakdown reads "at {dest name} ({key}): pull lever"
        // rather than the same-room "here:" prefix.
        const string remoteActionRooms = """
            [
              { "Map Number": 9, "Room Number": 1012, "Name": "Vault Door",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 5, "CMD": 0,
                "N": "0", "S": "0",
                "E": "9/1013 (Hidden/Needs 1 Actions, any order)",
                "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 9, "Room Number": 870, "Name": "Lever Room",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 5, "CMD": 0,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "Action [on the E exit of room 9/1012]: pull lever",
                "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 9, "Room Number": 1013, "Name": "Treasure Vault",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 0, "CMD": 0,
                "N": "0", "S": "0", "E": "0", "W": "9/1012",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        string setRoot = Path.Combine(_root, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"), remoteActionRooms);
        GameDataCache cache = new(_root);
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);

        Room room = graph.GetRoom(new RoomKey(9, 1012))!;
        string text = RoomTooltipBuilder.Build(room, graph, cache);

        // Inline summary still names the action; remote breakdown lands
        // on its own line with the source-room name + key.
        Assert.Contains("Needs 1 action: pull lever", text);
        Assert.Contains("1. at Lever Room (9/870): pull lever", text);
    }

    [Fact]
    public void Build_RoomCmdTeleport_SurfacesKeywordsGroupedByDestination()
    {
        // Live repro: room 1/1182 has CMD 4087 whose TBInfo Action chain
        // is "use chime:...:teleport 65 1:...\nring chime:...:teleport 65 1:...".
        // Both keywords land at 1/65; the tooltip should list them on
        // a single line grouped by destination so the user can see how
        // to bypass the door north.
        const string cmdRooms = """
            [
              { "Map Number": 1, "Room Number": 1182, "Name": "Slum Street",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 5, "CMD": 4087,
                "N": "1/65 (Door)", "S": "0", "E": "1/1183", "W": "1/1181",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 65, "Name": "Strange Mansion, Entrance",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 0, "CMD": 0,
                "N": "0", "S": "1/1182 (Door)", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 1181, "Name": "Slum Street",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 0, "CMD": 0,
                "N": "0", "S": "0", "E": "1/1182", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 1183, "Name": "Slum Street, Intersection",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 0, "CMD": 0,
                "N": "0", "S": "0", "E": "0", "W": "1/1182",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        const string cmdTbinfo = """
            [
              { "Number": 4087, "LinkTo": 0,
                "Action": "use chime:message 3177:teleport 65 1:message 837\nring chime:message 3177:teleport 65 1:message 837\n\n",
                "Called From": "Room 1/1182" }
            ]
            """;
        string setRoot = Path.Combine(_root, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"),  cmdRooms);
        File.WriteAllText(Path.Combine(setRoot, "TBInfo.json"), cmdTbinfo);
        GameDataCache cache = new(_root);
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        TBInfoStore tbinfo = new(cache);
        tbinfo.OnActiveSetChanged(_setName);

        Room room = graph.GetRoom(new RoomKey(1, 1182))!;
        string text = RoomTooltipBuilder.Build(room, graph, cache, tbinfo);

        Assert.Contains("Room commands:", text);
        Assert.Contains("use chime / ring chime → Strange Mansion, Entrance (1/65)", text);
    }

    [Fact]
    public void Build_TextHintExit_RendersCommandAlternatives()
    {
        // Live repro: 1/1824 south "(Text: go crack, enter crack, go path)"
        // should surface the actual alternatives rather than the bare
        // "(Text)" hint name.
        const string textRooms = """
            [
              { "Map Number": 1, "Room Number": 1824, "Name": "Grassy Cove",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 5, "CMD": 0,
                "N": "0", "S": "1/1823 (Text: go crack, enter crack, go path)", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 1823, "Name": "Stony Crack",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 0, "CMD": 0,
                "N": "1/1824", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        string setRoot = Path.Combine(_root, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"), textRooms);
        GameDataCache cache = new(_root);
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);

        Room room = graph.GetRoom(new RoomKey(1, 1824))!;
        string text = RoomTooltipBuilder.Build(room, graph, cache);

        Assert.Contains("(Text: go crack, enter crack, go path)", text);
    }

    [Fact]
    public void Build_ItemHintExit_NoItemsTable_FallsBackToIdNumber()
    {
        string setRoot = Path.Combine(_root, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"), ItemExitRooms);
        // Deliberately no Items.json — the lookup misses but the row
        // shape stays informative via the id fallback.
        GameDataCache cache = new(_root);
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);

        Room room = graph.GetRoom(new RoomKey(6, 79))!;
        string text = RoomTooltipBuilder.Build(room, graph, cache);

        Assert.Contains("(Item: #191)", text);
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
