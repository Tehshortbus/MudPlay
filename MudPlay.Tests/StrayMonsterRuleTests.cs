using System.Collections.Generic;
using System.Text.Json;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A monster flagged in play that's only listed as a Group: spawn in rooms whose spawn
// slot another NPC holds can never appear (Cygani, #543, under Aiken's Magic Shoppe).
// Anything the rule can't read keeps the monster in play.
public sealed class StrayMonsterRuleTests
{
    private static Room MakeRoom(int map, int room, int npc = 0, string? lair = null) => new()
    {
        Key = new RoomKey(map, room),
        Name = "room",
        Exits = RoomExitSet.Empty,
        Npc = npc,
        RawLairTag = lair,
    };

    private static readonly Dictionary<RoomKey, Room> Rooms = new()
    {
        [new RoomKey(1, 398)] = MakeRoom(1, 398, npc: 22),                 // Magic Shoppe: Aiken
        [new RoomKey(6, 590)] = MakeRoom(6, 590),                          // open field, no NPC
        [new RoomKey(1, 527)] = MakeRoom(1, 527, npc: 22, lair: "(Max 1): 47,[2-16-16-1]"),
    };

    private static Room? Get(RoomKey key) => Rooms.TryGetValue(key, out Room? r) ? r : null;

    private static bool Stray(string summonedBy, int number = 543, int inGame = 1)
    {
        GameRow doc = TestRows.Row(
            $"{{\"Number\":{number},\"In Game\":{inGame},\"Summoned By\":{JsonSerializer.Serialize(summonedBy)}}}");
        return StrayMonsterRule.IsOutOfPlay(doc, Get);
    }

    [Fact]
    public void GroupOnlyIntoAnotherNpcsRoom_IsOutOfPlay() => Assert.True(Stray("Group: 1/398"));

    [Theory]
    [InlineData("Group: 6/590")]                          // a room with no NPC draws from its group
    [InlineData("Group: 1/398,Group: 6/590")]             // one real spawn room is enough
    [InlineData("Group: 1/527")]                          // a lair room
    [InlineData("Group: 9/9")]                            // a room not in the graph
    [InlineData("Room 1/398,Group: 1/398")]               // placed (Aiken's own record)
    [InlineData("[2-16-16][1]Group(lair): 1/527")]        // lair spawn
    [InlineData("Spell #120,Group: 1/398")]               // summoned
    [InlineData("Textblock #863")]
    [InlineData("oup: 1/398")]                            // unreadable token
    [InlineData("")]                                      // no tokens at all
    public void AnythingElse_StaysInPlay(string summonedBy) => Assert.False(Stray(summonedBy));

    // The room's own NPC listed under its group isn't a stray: it's what spawns there.
    [Fact]
    public void TheRoomsOwnNpc_StaysInPlay() => Assert.False(Stray("Group: 1/398", number: 22));

    [Fact]
    public void FlaggedOutOfPlay_IsOutOfPlay_WithOrWithoutARoomGraph()
    {
        GameRow doc = TestRows.Row("{\"Number\":7,\"In Game\":0,\"Summoned By\":\"Group: 6/590\"}");
        Assert.True(StrayMonsterRule.IsOutOfPlay(doc, Get));
        Assert.True(StrayMonsterRule.IsOutOfPlay(doc, null));
    }

    [Fact]
    public void NoRoomGraph_OnlyTheFlagCounts()
    {
        GameRow doc = TestRows.Row("{\"Number\":543,\"In Game\":1,\"Summoned By\":\"Group: 1/398\"}");
        Assert.False(StrayMonsterRule.IsOutOfPlay(doc, null));
    }
}
