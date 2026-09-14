using System.Collections.Generic;
using System.IO;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class RoomTooltipBuilderTests : IDisposable
{
    private readonly string _root;
    private readonly string _setName;

    public RoomTooltipBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-tooltip-tests-" + Path.GetRandomFileName());
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

    // FormatLairRegen is shared by the map tooltip and the Room Info panel, so
    // both surfaces show the identical "Max Regen: N @ time" line.
    [Theory]
    [InlineData(2, 5, "Max Regen: 2 @ 4m 30s")]   // Delay 5 → (5-1)m 30s
    [InlineData(3, 1, "Max Regen: 3 @ 30s")]      // Delay 1 → 30s
    [InlineData(1, 0, "Max Regen: 1")]            // no Delay → count only
    public void FormatLairRegen_FormatsCountAndTime(int max, int delay, string expected)
        => Assert.Equal(expected, RoomTooltipBuilder.FormatLairRegen(max, delay));

    [Fact]
    public void FormatLairRegen_NoLair_ReturnsEmpty()
        => Assert.Equal(string.Empty, RoomTooltipBuilder.FormatLairRegen(null, 5));

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

        // Lair-tag members render under the "Lair:" line (the Max-N moves to the
        // Max Regen line below).
        Assert.Contains("Lair: Sewer Rat(#100), Sewer Snake(#101)", text);  // record numbers appended
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
    public void RoomLightSummary_SignedOffset_WithPhrase_ForDarkRoom()
    {
        var (graph, _) = NewGraph();
        Room dark = graph.GetRoom(new RoomKey(1, 2))!;      // Light = -180

        // The ROOM INFO panel's "Room Illu" line: the room alone (no player light),
        // "Room Illu: <signed value> - <phrase>".
        string summary = RoomTooltipBuilder.BuildRoomLightSummary(dark);

        Assert.StartsWith("Room Illu: -180", summary);
        Assert.Contains("very dark", summary);
    }

    [Fact]
    public void LightSummary_ShowsBaseValue_ForFullyLitRoom()
    {
        var (graph, _) = NewGraph();
        Room lit = graph.GetRoom(new RoomKey(1, 1))!;       // Light = 0

        // Light 0 isn't "no illu" — it's the base value 0, which still shows a line
        // reading "You can see."
        string room = RoomTooltipBuilder.BuildRoomLightSummary(lit);
        Assert.StartsWith("Room Illu: 0", room);
        Assert.Contains("You can see.", room);

        string player = RoomTooltipBuilder.BuildPlayerLightSummary(lit, playerIllu: 200);
        Assert.StartsWith("Your Illu: +200", player);
        Assert.Contains("You can see.", player);
    }

    [Fact]
    public void PlayerLightSummary_FoldsPlayerIllu_IntoEffectiveValue()
    {
        var (graph, _) = NewGraph();
        Room dark = graph.GetRoom(new RoomKey(1, 2))!;      // Light = -180

        // "Your Illu" folds carried illumination into the room's light: -180 + 100
        // = -80, which lands in the dimly-lit band.
        string summary = RoomTooltipBuilder.BuildPlayerLightSummary(dark, playerIllu: 100);

        Assert.StartsWith("Your Illu: -80", summary);
        Assert.Contains("dimly lit", summary);
    }

    [Fact]
    public void RoomLightSummary_OmitsPhrase_WhenRequested()
    {
        var (graph, _) = NewGraph();
        Room dark = graph.GetRoom(new RoomKey(1, 2))!;      // Light = -180

        // With the player carrying light the phrase moves to Your Illu, so Room
        // Illu shows just its value.
        string summary = RoomTooltipBuilder.BuildRoomLightSummary(dark, includePhrase: false);

        Assert.Equal("Room Illu: -180", summary);
    }

    [Fact]
    public void PlayerLightSummary_YouCanSee_OnlyWhenFullyLit()
    {
        var (graph, _) = NewGraph();
        Room dark = graph.GetRoom(new RoomKey(1, 2))!;      // Light = -180

        // Only fully-lit (V >= 0) reads "You can see."; the darker bands keep their
        // game phrase. -180 + 200 = +20 (fully lit) → "You can see."; -180 + 40 =
        // -140 (barely visible) keeps its line.
        Assert.Contains("You can see.", RoomTooltipBuilder.BuildPlayerLightSummary(dark, playerIllu: 200));
        string barely = RoomTooltipBuilder.BuildPlayerLightSummary(dark, playerIllu: 40);
        Assert.StartsWith("Your Illu: -140", barely);
        Assert.Contains("barely visible", barely);
        Assert.DoesNotContain("You can see.", barely);
    }

    [Fact]
    public void Build_ExitsList_ListsAllDirections_WithDestinationNames()
    {
        var (graph, cache) = NewGraph();
        Room room = graph.GetRoom(new RoomKey(1, 1))!;
        string text = RoomTooltipBuilder.Build(room, graph, cache);

        Assert.Contains("Obvious exits:", text);
        Assert.Contains("north → North Square (1/2)", text);
        Assert.Contains("east → Inn (1/3) (Door: any picklocks/strength)", text);
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
    public void Build_Placed_IncludesSummonedBossNotInLairTag()
    {
        // Live repro: 1/1678 Darkwood Forest, Webbed Clearing has no
        // lair tag entry for "giant spider" (Monster 52), but the
        // monster's "Summoned By" reads "Room 1/1678" — a "Room" token, so it
        // renders under the "Placed:" line (a placed boss / fixture).
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

        Assert.Contains("Placed: giant spider(#52)", text);   // boss spawn, with record number
    }

    [Fact]
    public void Build_SeparatesPlacedAssignedAndLair()
    {
        // One room hosting all three kinds: a "Room" token (placed boss), a
        // non-lair "Group:" token (assigned roam), and a lair-tag member. Each
        // lands under its own labelled line.
        const string rooms = """
            [
              { "Map Number": 2, "Room Number": 50, "Name": "Crossroads",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "(Max 1): 300", "Delay": 5, "CMD": 0,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        const string monsters = """
            [
              { "Number": 100, "Name": "ogre chief",  "Summoned By": "Room 2/50" },
              { "Number": 200, "Name": "grey wolf",   "Summoned By": "Group: 2/50" },
              { "Number": 300, "Name": "cave lizard", "Summoned By": "[9-9-9][2]Group(lair): 2/50" }
            ]
            """;
        string setRoot = Path.Combine(_root, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"),    rooms);
        File.WriteAllText(Path.Combine(setRoot, "Monsters.json"), monsters);
        GameDataCache cache = new(_root);
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        MonsterSpawnIndex spawnIndex = new(cache);

        Room room = graph.GetRoom(new RoomKey(2, 50))!;
        string text = RoomTooltipBuilder.Build(room, graph, cache, tbinfo: null, spawnIndex: spawnIndex);

        Assert.Contains("Placed: ogre chief(#100)", text);
        Assert.Contains("Assigned: grey wolf(#200)", text);
        Assert.Contains("Lair: cave lizard(#300)", text);
        // The Group(lair) token must NOT leak the lair member into Placed/Assigned.
        Assert.DoesNotContain("Placed: cave lizard", text);
        Assert.DoesNotContain("Assigned: cave lizard", text);
    }

    [Fact]
    public void Build_FloorItems_ListsRoomsPlacedItems()
    {
        // The bogwood box (item 3796) is placed on room 14/10415's floor via the
        // room's Placed column; the tooltip must list it (report: the item record
        // named the room but the room tooltip didn't show the item).
        const string rooms = """
            [
              { "Map Number": 14, "Room Number": 10415, "Name": "Damp Chamber, Platform",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 5, "CMD": 0, "Placed": "3796",
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        const string items = """
            [ { "Number": 3796, "Name": "bogwood box" } ]
            """;
        string setRoot = Path.Combine(_root, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"), rooms);
        File.WriteAllText(Path.Combine(setRoot, "Items.json"), items);
        File.WriteAllText(Path.Combine(setRoot, "TBInfo.json"), "[]");
        GameDataCache cache = new(_root);
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        TBInfoStore tb = new(cache);
        tb.OnActiveSetChanged(_setName);
        RoomFloorItemIndex floor = new(cache, tb);

        Room room = graph.GetRoom(new RoomKey(14, 10415))!;
        string text = RoomTooltipBuilder.Build(room, graph, cache, floorItems: floor);

        Assert.Contains("Floor items: bogwood box(#3796)", text);
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
    public void Build_MultiActionHiddenExit_TbInfoFallback_SurfacesKeywords()
    {
        // v1.11p map 9 / room 1012's north exit is "(Hidden/Needs 1
        // Actions, any order)" but no Action#N cell pairs with it —
        // the unlock keywords live in TBInfo CMD 1422 as a stack of
        // `<keyword>:testskill ...:remoteaction ...` lines. The
        // tooltip must fall back to those keywords so the user
        // doesn't see a bare "(MultiActionHidden)".
        const string fallbackRooms = """
            [
              { "Map Number": 9, "Room Number": 1012, "Name": "Crumbling Ruin, Entrance",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 5, "CMD": 1422,
                "N": "9/1013 (Hidden/Needs 1 Actions, any order)",
                "S": "9/1011", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 9, "Room Number": 1011, "Name": "Dirt Path",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 5, "CMD": 0,
                "N": "9/1012", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 9, "Room Number": 1013, "Name": "Crumbling Ruin",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 0, "CMD": 0,
                "N": "0", "S": "9/1012", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        const string tbInfoRows = """
            [
              { "Number": 1422,
                "Action": "clear rubble:testskill strength 0 1423:remoteaction 1012 1840 0 0\nmove rubble:testskill strength 0 1423:remoteaction 1012 1840 0 0\npush rubble:testskill strength 0 1423:remoteaction 1012 1840 0 0",
                "Called From": "Room 9/1012" }
            ]
            """;
        string setRoot = Path.Combine(_root, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"),  fallbackRooms);
        File.WriteAllText(Path.Combine(setRoot, "TBInfo.json"), tbInfoRows);
        GameDataCache cache = new(_root);
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        TBInfoStore tbinfo = new(cache);
        tbinfo.OnActiveSetChanged(_setName);

        Room room = graph.GetRoom(new RoomKey(9, 1012))!;
        string text = RoomTooltipBuilder.Build(room, graph, cache, tbinfo: tbinfo);

        // Inline summary should be "Needs 1 action" reparsed from the
        // raw modifier, not the bare enum name.
        Assert.Contains("Needs 1 action", text);
        Assert.DoesNotContain("(MultiActionHidden)", text);
        // TBInfo keyword fallback rendered as one indented line under
        // the exit.
        Assert.Contains("Try: clear rubble / move rubble / push rubble", text);
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
    public void Build_PricedRoomCommands_SurfaceCost_TieredBribeShowsCeiling()
    {
        // A gambling command (single price) and the jail bribe-guard (six
        // escalating prices) both carry a `price` the tooltip previously dropped.
        // The dice cost surfaces flat; the bribe surfaces as its ceiling with the
        // "takes the most you can afford" caveat. No spell catalog is passed, so
        // bribe-guard's cast lines don't surface as a teleport — it lands on the
        // standalone priced line instead.
        const string cmdRooms = """
            [
              { "Map Number": 1, "Room Number": 2, "Name": "Jail Cell",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 5, "CMD": 997,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        const string cmdTbinfo = """
            [
              { "Number": 997, "LinkTo": 0,
                "Action": "roll dice:price 10000 1560:random 998\nbribe guard:cast 5432:cast 5434:price 100:price 1000:price 10000:price 100000:price 1000000:price 10000000\n",
                "Called From": "Room 1/2" }
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

        Room room = graph.GetRoom(new RoomKey(1, 2))!;
        string text = RoomTooltipBuilder.Build(room, graph, cache, tbinfo);

        Assert.Contains("roll dice — costs 100 Gold", text);
        Assert.Contains("bribe guard — costs up to 10 Runic (takes the most you can afford)", text);
    }

    [Fact]
    public void Build_SummonRoomCommand_NamesTheMonster()
    {
        // Live repro (report paradigm-20260911-010954): 8/461 "Black Steel Gate"
        // has CMD 863 = "touch statue:summon 347" / "move statue:summon 347".
        // The obsidian statue drops the gate key that opens the door south, and
        // the tooltip showed nothing at all — `summon` was not in the recognised
        // directive vocabulary. Both synonyms collapse onto one row.
        const string cmdRooms = """
            [
              { "Map Number": 8, "Room Number": 461, "Name": "Black Steel Gate",
                "Light": -200, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 5, "CMD": 863,
                "N": "8/460", "S": "8/462 (Key: 806 [or 101 picklocks])", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 8, "Room Number": 460, "Name": "Large Hole",
                "Light": -200, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 5, "CMD": 0,
                "N": "0", "S": "8/461", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 8, "Room Number": 462, "Name": "Town Gates, Archway",
                "Light": -200, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 5, "CMD": 0,
                "N": "8/461", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        const string cmdTbinfo = """
            [
              { "Number": 863, "LinkTo": 0,
                "Action": "touch statue:summon 347\nmove statue:summon 347\n",
                "Called From": "Room 8/461" }
            ]
            """;
        const string monsterRows = """
            [ { "Number": 347, "Name": "obsidian statue" } ]
            """;
        const string itemRows = """
            [ { "Number": 806, "Name": "gate key" } ]
            """;
        string setRoot = Path.Combine(_root, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"),    cmdRooms);
        File.WriteAllText(Path.Combine(setRoot, "TBInfo.json"),   cmdTbinfo);
        File.WriteAllText(Path.Combine(setRoot, "Monsters.json"), monsterRows);
        File.WriteAllText(Path.Combine(setRoot, "Items.json"),    itemRows);
        GameDataCache cache = new(_root);
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        TBInfoStore tbinfo = new(cache);
        tbinfo.OnActiveSetChanged(_setName);

        Room room = graph.GetRoom(new RoomKey(8, 461))!;
        string text = RoomTooltipBuilder.Build(room, graph, cache, tbinfo);

        Assert.Contains("Room commands:", text);
        Assert.Contains("touch statue / move statue — summons obsidian statue", text);
        // The door it guards still names its real key.
        Assert.Contains("Key: gate key", text);
    }

    // The healer's command is literally `summon healer`, so a keyword sharing its
    // first word with a directive must not be mistaken for one — what separates
    // them is the argument (`summon 787` is a directive, `summon healer` is typed).
    // The charge and the effect belong on ONE row; emitting both a "what it does"
    // row and a separate "what it costs" row would list the command twice.
    [Fact]
    public void Build_PricedSummonCommand_MergesEffectAndCostOnOneRow()
    {
        const string cmdRooms = """
            [
              { "Map Number": 15, "Room Number": 908, "Name": "Red House Room",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 5, "CMD": 1602,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        const string cmdTbinfo = """
            [
              { "Number": 1602, "LinkTo": 0,
                "Action": "summon healer:checkitem 1008:nomonsters:price 10000 318:summon 787\n",
                "Called From": "Room 15/908" }
            ]
            """;
        const string monsterRows = """
            [ { "Number": 787, "Name": "mercenary healer" } ]
            """;
        string setRoot = Path.Combine(_root, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"),    cmdRooms);
        File.WriteAllText(Path.Combine(setRoot, "TBInfo.json"),   cmdTbinfo);
        File.WriteAllText(Path.Combine(setRoot, "Monsters.json"), monsterRows);
        GameDataCache cache = new(_root);
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        TBInfoStore tbinfo = new(cache);
        tbinfo.OnActiveSetChanged(_setName);

        Room room = graph.GetRoom(new RoomKey(15, 908))!;
        string text = RoomTooltipBuilder.Build(room, graph, cache, tbinfo);

        Assert.Contains("summon healer — summons mercenary healer — costs 100 Gold", text);
        // Exactly one row for the command — no bare "summon healer — costs …" twin.
        Assert.Single(
            text.Split('\n'),
            line => line.TrimStart().StartsWith("summon healer", StringComparison.Ordinal));
    }

    // The MegaMUD export repeats a gated chain once per race/class under the
    // trainer's keyword, and those continuation lines LEAD with `check class` —
    // 157 of them in the Paradigm set. "class" is not a number, so an
    // argument-is-numeric test alone would admit `check class` as a player command
    // and fold it into the trainer's row. A directive's argument can also be
    // another directive head, which is what separates it from `summon healer`.
    [Fact]
    public void Build_DirectiveLedContinuationLines_AreNotPlayerCommands()
    {
        const string cmdRooms = """
            [
              { "Map Number": 16, "Room Number": 2667, "Name": "Meditation Chamber",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 5, "CMD": 2903,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        const string cmdTbinfo = """
            [
              { "Number": 2903, "LinkTo": 0,
                "Action": "give crane totem to kuel:check class:class 15:race 1:levelcheck:takeitem 1276 2552:learnspell 838:text 2904\ncheck class:class 15:race 2:levelcheck:takeitem 1276 2552:learnspell 838:text 2904\ncheck class:class 15:race 3:levelcheck:takeitem 1276 2552:learnspell 838:text 2904\n",
                "Called From": "Room 16/2667" }
            ]
            """;
        const string itemRows = """
            [ { "Number": 1276, "Name": "crane totem" } ]
            """;
        const string spellRows = """
            [ { "Number": 838, "Name": "form of the crane" } ]
            """;
        string setRoot = Path.Combine(_root, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"),  cmdRooms);
        File.WriteAllText(Path.Combine(setRoot, "TBInfo.json"), cmdTbinfo);
        File.WriteAllText(Path.Combine(setRoot, "Items.json"),  itemRows);
        File.WriteAllText(Path.Combine(setRoot, "Spells.json"), spellRows);
        GameDataCache cache = new(_root);
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        TBInfoStore tbinfo = new(cache);
        tbinfo.OnActiveSetChanged(_setName);

        Room room = graph.GetRoom(new RoomKey(16, 2667))!;
        string text = RoomTooltipBuilder.Build(room, graph, cache, tbinfo);

        Assert.Contains("give crane totem to kuel — teaches form of the crane", text);
        Assert.DoesNotContain("check class", text);
    }

    // 14/6275's CMD 5728 writes two lines for "destroy portal": the attempt summons
    // the guardian, and the line for after you've beaten it awards the quest
    // ability. One command, two outcome branches — it must read as one row at the
    // consequential effect (the summon), not as two commands.
    [Fact]
    public void Build_KeywordWithSeveralOutcomeLines_RendersOnceAtBestEffect()
    {
        const string cmdRooms = """
            [
              { "Map Number": 14, "Room Number": 6275, "Name": "Portal Chamber",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 5, "CMD": 5728,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        const string cmdTbinfo = """
            [
              { "Number": 5728, "LinkTo": 0,
                "Action": "destroy portal:minlevel 60:testability 126 35:summon 2264:text 4503\ndemolish portal:minlevel 60:testability 126 35:summon 2264:text 4503\ndestroy portal:minlevel 60:checkability 126 36:giveability 126 37:text 5729\ndemolish portal:minlevel 60:checkability 126 36:giveability 126 37:text 5729\n",
                "Called From": "Room 14/6275" }
            ]
            """;
        const string monsterRows = """
            [ { "Number": 2264, "Name": "portal guardian" } ]
            """;
        string setRoot = Path.Combine(_root, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"),    cmdRooms);
        File.WriteAllText(Path.Combine(setRoot, "TBInfo.json"),   cmdTbinfo);
        File.WriteAllText(Path.Combine(setRoot, "Monsters.json"), monsterRows);
        GameDataCache cache = new(_root);
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        TBInfoStore tbinfo = new(cache);
        tbinfo.OnActiveSetChanged(_setName);

        Room room = graph.GetRoom(new RoomKey(14, 6275))!;
        string text = RoomTooltipBuilder.Build(room, graph, cache, tbinfo);

        // Both synonyms on one row, at the summon — and no second "grants an
        // ability" row for the same pair of commands.
        Assert.Contains("destroy portal / demolish portal — summons portal guardian", text);
        Assert.DoesNotContain("grants an ability", text);
    }

    // 7/142's CMD 4703 teleports on the first visit and, on a repeat, only awards
    // the ability. The teleport resolver already lists those keywords, so the
    // effect pass must not list them again as an ability grant.
    [Fact]
    public void Build_KeywordAlreadyShownAsTeleport_IsNotAlsoAnEffectRow()
    {
        const string cmdRooms = """
            [
              { "Map Number": 7, "Room Number": 142, "Name": "Gem Alcove",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 5, "CMD": 4703,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 7, "Room Number": 20, "Name": "Hidden Vault",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 0, "CMD": 0,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        const string cmdTbinfo = """
            [
              { "Number": 4703, "LinkTo": 0,
                "Action": "touch gem:message 290:teleport 20 7:minlevel 12:giveability 129 2\nmove gem:message 290:teleport 20 7:minlevel 12:giveability 129 2\ntouch gem:minlevel 12:checkability 129 1:giveability 129 2\nmove gem:minlevel 12:checkability 129 1:giveability 129 2\n",
                "Called From": "Room 7/142" }
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

        Room room = graph.GetRoom(new RoomKey(7, 142))!;
        string text = RoomTooltipBuilder.Build(room, graph, cache, tbinfo);

        Assert.Contains("touch gem / move gem → Hidden Vault (7/20)", text);
        Assert.DoesNotContain("grants an ability", text);
    }

    [Fact]
    public void Build_EffectRoomCommands_NameSpellItemAndAbility()
    {
        // The other directive families a room CMD can end on. `learnspell` is the
        // spell-trainer hall, `giveability` the quest-flag award (unnameable — no
        // shipped table indexes ability ids), `roomitem` a floor drop to `get`, and
        // a bare `takeitem` a hand-over with nothing returned. A summon outranks a
        // room-item drop on the same line, so "touch hammer" reads as the hydra.
        const string cmdRooms = """
            [
              { "Map Number": 3, "Room Number": 632, "Name": "Iceforge",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 5, "CMD": 476,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        const string cmdTbinfo = """
            [
              { "Number": 476, "LinkTo": 0,
                "Action": "touch hammer:roomitem 767 1114:clearitem 767:summon 316\ngive crane totem:checkitem 1276:takeitem 1276:learnspell 838\nbreak apparatus:failability 132:giveability 132 2\npull lever:roomitem 767 1114\nhand over totem:takeitem 1276\n",
                "Called From": "Room 3/632" }
            ]
            """;
        const string monsterRows = """
            [ { "Number": 316, "Name": "frost hydra" } ]
            """;
        const string itemRows = """
            [ { "Number": 767, "Name": "frozen hydra" }, { "Number": 1276, "Name": "crane totem" } ]
            """;
        const string spellRows = """
            [ { "Number": 838, "Name": "form of the crane" } ]
            """;
        string setRoot = Path.Combine(_root, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"),    cmdRooms);
        File.WriteAllText(Path.Combine(setRoot, "TBInfo.json"),   cmdTbinfo);
        File.WriteAllText(Path.Combine(setRoot, "Monsters.json"), monsterRows);
        File.WriteAllText(Path.Combine(setRoot, "Items.json"),    itemRows);
        File.WriteAllText(Path.Combine(setRoot, "Spells.json"),   spellRows);
        GameDataCache cache = new(_root);
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        TBInfoStore tbinfo = new(cache);
        tbinfo.OnActiveSetChanged(_setName);

        Room room = graph.GetRoom(new RoomKey(3, 632))!;
        string text = RoomTooltipBuilder.Build(room, graph, cache, tbinfo);

        Assert.Contains("touch hammer — summons frost hydra", text);
        Assert.Contains("give crane totem — teaches form of the crane", text);
        Assert.Contains("break apparatus — grants an ability", text);
        Assert.Contains("pull lever — drops frozen hydra in the room", text);
        Assert.Contains("hand over totem — takes crane totem", text);
    }

    [Fact]
    public void Build_LevelGatedExit_RendersFriendlyLabel()
    {
        // Form A — exit-direction gate "(Level: 40 to 0)" means Level 40+.
        const string lvlRooms = """
            [
              { "Map Number": 1, "Room Number": 100, "Name": "Forbidden Stair",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 5, "CMD": 0,
                "N": "1/101 (Level: 40 to 0)", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 101, "Name": "Upper Sanctum",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 0, "CMD": 0,
                "N": "0", "S": "1/100", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        string setRoot = Path.Combine(_root, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"), lvlRooms);
        GameDataCache cache = new(_root);
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);

        Room room = graph.GetRoom(new RoomKey(1, 100))!;
        string text = RoomTooltipBuilder.Build(room, graph, cache);

        Assert.Contains("north → Upper Sanctum (1/101) (Level 40+)", text);
    }

    [Fact]
    public void Build_RoomCmdTeleport_WithMinLevel_RendersGateOnCommandLine()
    {
        // Form B — CMD teleport with a "minlevel 20" gate (room 3/613
        // "go vortex"). The "Room commands:" line surfaces "Level 20+".
        const string cmdRooms = """
            [
              { "Map Number": 1, "Room Number": 613, "Name": "Swirling Chamber",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 5, "CMD": 9001,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 3, "Room Number": 669, "Name": "Vortex Landing",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 0, "CMD": 0,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        const string cmdTbinfo = """
            [
              { "Number": 9001, "LinkTo": 0,
                "Action": "go vortex:adddelay 5:minlevel 20 1220:message 1205:teleport 669 3:message 1221\n",
                "Called From": "Room 1/613" }
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

        Room room = graph.GetRoom(new RoomKey(1, 613))!;
        string text = RoomTooltipBuilder.Build(room, graph, cache, tbinfo);

        Assert.Contains("go vortex → Vortex Landing (3/669) (Level 20+)", text);

        // The floor is rendered ONCE. A teleport reads its level off the same
        // directive the requirement row does, so appending the requirement's
        // copy too gave "(Level 20+) — Level 20+".
        Assert.DoesNotContain("— Level 20+", text);
        Assert.Equal(1, CountOccurrences(text, "Level 20+"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Fact]
    public void Build_RoomCmdCastTeleport_RandomRange_ListsEveryLandingRoom()
    {
        // Live repro: map 1 rooms 178-180 (CMD 9115) fire "jump west" /
        // "jump east", both casting spell 923 "bridge jump" — a random
        // teleport into one of rooms 2590-2594. The room-commands block
        // surfaces the keywords + every possible landing room so the
        // walker can flag post-jump position uncertainty.
        const string castRooms = """
            [
              { "Map Number": 1, "Room Number": 178, "Name": "Bridge",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 5, "CMD": 9115,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "1/179", "NW": "0", "SE": "0", "SW": "1/177", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2590, "Name": "Murky River",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 0, "CMD": 0,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2591, "Name": "Murky River",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 0, "CMD": 0,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2592, "Name": "Murky River",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 0, "CMD": 0,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2593, "Name": "Murky River",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 0, "CMD": 0,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2594, "Name": "Murky River",
                "Light": 0, "Shop": 0, "Spell": 0, "Lair": "", "Delay": 0, "CMD": 0,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        const string castTbinfo = """
            [
              { "Number": 9115, "LinkTo": 0,
                "Action": "jump west:message 2664:cast 923\njump east:message 2664:cast 923\n",
                "Called From": "Room 1/178, Room 1/179, Room 1/180" }
            ]
            """;
        const string castSpells = """
            [
              { "Number": 923, "Name": "bridge jump", "Short": "bjump",
                "MinBase": 2590, "MaxBase": 2594,
                "Abil-0": 140, "AbilVal-0": 0, "Abil-1": 141, "AbilVal-1": 1 }
            ]
            """;
        string setRoot = Path.Combine(_root, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"),  castRooms);
        File.WriteAllText(Path.Combine(setRoot, "TBInfo.json"), castTbinfo);
        File.WriteAllText(Path.Combine(setRoot, "Spells.json"), castSpells);
        GameDataCache cache = new(_root);
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        TBInfoStore tbinfo = new(cache);
        tbinfo.OnActiveSetChanged(_setName);
        Game.Spells.KnownSpellCatalog catalog = new(cache);

        Room room = graph.GetRoom(new RoomKey(1, 178))!;
        string text = RoomTooltipBuilder.Build(room, graph, cache, tbinfo,
            spawnIndex: null, spellCatalog: catalog);

        Assert.Contains("Room commands:", text);
        Assert.Contains("jump west / jump east → one of 5 rooms (random):", text);
        Assert.Contains("Murky River (1/2590)", text);
        Assert.Contains("Murky River (1/2594)", text);
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
    public void Build_FieldOrder_NameMonstersRegenExitsLight()
    {
        var (graph, cache) = NewGraph();
        Room dark = graph.GetRoom(new RoomKey(1, 2))!;     // dark lair room
        string text = RoomTooltipBuilder.Build(dark, graph, cache);

        // Sequential order: Name → Lair line → Max Regen (right beneath the Lair
        // line) → exits → Room Light → light desc. 1/2's monsters are lair-tag
        // members, so they render under "Lair:"; it has no Shop / Spell.
        int posName   = text.IndexOf("North Square (1/2)");
        int posAlso   = text.IndexOf("Lair:");
        int posRegen  = text.IndexOf("Max Regen: 2");
        int posExits  = text.IndexOf("Obvious exits:");
        int posRLight = text.IndexOf("Room Light: -180");
        int posDesc   = text.IndexOf("very dark");

        Assert.True(posName < posAlso);
        Assert.True(posAlso < posRegen);     // Max Regen sits directly below the Lair line
        Assert.True(posRegen < posExits);    // ...and above the exits block
        Assert.True(posExits < posRLight);
        Assert.True(posRLight < posDesc);
    }

    // ----- Door / key pick+bash requirement in the exit hint --------

    [Fact]
    public void FormatExitHint_Door_PicklocksAndStrength_SurfacesRequirement()
    {
        Assert.True(RoomExit.TryParseWire("9/177 (Door [11 picklocks/strength])", out RoomExit exit));
        Assert.Equal("Door: 11 picklocks/strength", RoomTooltipBuilder.FormatExitHint(exit, data: null));
    }

    [Fact]
    public void FormatExitHint_Door_PicklocksOnly_OmitsStrength()
    {
        Assert.True(RoomExit.TryParseWire("3/14 (Door [50 picklocks])", out RoomExit exit));
        Assert.Equal("Door: 50 picklocks", RoomTooltipBuilder.FormatExitHint(exit, data: null));
    }

    [Fact]
    public void FormatExitHint_Door_NoRequirement_RendersAny()
    {
        // A bare door anyone can bash / pick reads "any" rather than showing nothing.
        Assert.True(RoomExit.TryParseWire("1/2666 (Door)", out RoomExit exit));
        Assert.Equal("Door: any picklocks/strength", RoomTooltipBuilder.FormatExitHint(exit, data: null));
    }

    [Fact]
    public void FormatExitHint_Door_AnyPicklocksStrength_RendersAny()
    {
        Assert.True(RoomExit.TryParseWire("12/51 (Door [any picklocks/strength])", out RoomExit exit));
        Assert.Equal("Door: any picklocks/strength", RoomTooltipBuilder.FormatExitHint(exit, data: null));
    }

    [Fact]
    public void FormatExitHint_KeyLocked_WithPicklockAlt_AppendsAlternative()
    {
        Assert.True(RoomExit.TryParseWire("1/1224 (Key: 172 [or 100 picklocks])", out RoomExit exit));
        // No Items table supplied → the key falls back to "#172"; the pick alt tails on.
        Assert.Equal("Key: #172, or 100 picklocks", RoomTooltipBuilder.FormatExitHint(exit, data: null));
    }

    [Fact]
    public void FormatExitHint_KeyLocked_NoPicklockAlt_JustKey()
    {
        Assert.True(RoomExit.TryParseWire("1/1224 (Key: 172)", out RoomExit exit));
        Assert.Equal("Key: #172", RoomTooltipBuilder.FormatExitHint(exit, data: null));
    }

    // 8/462's north gate records "Key: 1" — a game-data typo, since item ids start
    // at 9 — alongside the 31 picklocks/strength that actually opens it. With the
    // Items table loaded and no such row, naming a key would send the reader after
    // an item that doesn't exist (report paradigm-20260911-010954).
    [Fact]
    public void FormatExitHint_KeyLocked_UnresolvableKeyWithStatAlt_ReadsAsDoor()
    {
        using var set = new TempGameDataSet("""[ { "Number": 806, "Name": "gate key" } ]""");
        Assert.True(RoomExit.TryParseWire(
            "8/461 (Key: 1 [or 31 picklocks/strength])", out RoomExit exit));
        Assert.Equal("Door: 31 picklocks/strength",
            RoomTooltipBuilder.FormatExitHint(exit, set.Cache));
    }

    // The same door's southbound side names a key that DOES exist — only the
    // unresolvable id is suppressed, never a real one.
    [Fact]
    public void FormatExitHint_KeyLocked_ResolvableKey_StillNamesIt()
    {
        using var set = new TempGameDataSet("""[ { "Number": 806, "Name": "gate key" } ]""");
        Assert.True(RoomExit.TryParseWire(
            "8/462 (Key: 806 [or 101 picklocks])", out RoomExit exit));
        Assert.Equal("Key: gate key, or 101 picklocks",
            RoomTooltipBuilder.FormatExitHint(exit, set.Cache));
    }

    // A typo'd key with NO stat alternative stays impassable, so the raw id is kept
    // — it is the only signal that the door's data is broken.
    [Fact]
    public void FormatExitHint_KeyLocked_UnresolvableKeyNoStatAlt_KeepsRawId()
    {
        using var set = new TempGameDataSet("""[ { "Number": 806, "Name": "gate key" } ]""");
        Assert.True(RoomExit.TryParseWire("8/461 (Key: 1)", out RoomExit exit));
        Assert.Equal("Key: #1", RoomTooltipBuilder.FormatExitHint(exit, set.Cache));
    }

    // A throwaway single-table set, so the key-resolution tests can distinguish
    // "the Items table says no such id" from "no data is loaded at all".
    private sealed class TempGameDataSet : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "mudplay-hint-" + Guid.NewGuid().ToString("N"));

        public GameDataCache Cache { get; }

        public TempGameDataSet(string itemsJson)
        {
            const string setName = "alpha";
            string setRoot = Path.Combine(_root, setName);
            Directory.CreateDirectory(setRoot);
            File.WriteAllText(Path.Combine(setRoot, "Items.json"), itemsJson);
            Cache = new GameDataCache(_root);
            Cache.SwitchSet(setName);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }
}
