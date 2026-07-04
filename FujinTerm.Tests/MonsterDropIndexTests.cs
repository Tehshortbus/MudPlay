using System.IO;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class MonsterDropIndexTests : IDisposable
{
    private readonly string _root;

    public MonsterDropIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-monsterdrop-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // thug (10) drops 175 (5%) at 1/100 & 1/101. goblin (20) drops 175 (30%)
    // and 176 (10%) at 1/200 — so 175 has two droppers. orc (40) drops 64 (no
    // pct) with a Summoned By carrying a malformed token ("oup: 1/400"), a
    // slash-less token ("Group: 102", skipped), and a clean "1/401". rat (30)
    // drops nothing (all zero slots) with a spawn room — proves a non-dropper
    // is never spawn-indexed and the 0 slot never becomes "item 0".
    private const string MonstersJson = """
        [
          { "Number": 10, "Name": "thug",   "DropItem-0": 175, "DropItem%-0": 5,
            "Summoned By": "Group: 1/100,Group(lair): 1/101" },
          { "Number": 20, "Name": "goblin", "DropItem-0": 175, "DropItem%-0": 30,
            "DropItem-1": 176, "DropItem%-1": 10, "Summoned By": "Group: 1/200" },
          { "Number": 40, "Name": "orc",    "DropItem-0": 64,  "DropItem%-0": 0,
            "Summoned By": "oup: 1/400,Group: 102,Group: 1/401" },
          { "Number": 30, "Name": "rat",    "DropItem-0": 0,   "DropItem%-0": 0,
            "Summoned By": "Group: 1/300" }
        ]
        """;

    private MonsterDropIndex NewIndex(string set = "alpha", string json = MonstersJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, set));
        File.WriteAllText(Path.Combine(_root, set, "Monsters.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet(set);
        MonsterDropIndex index = new(cache);
        index.OnActiveSetChanged(set);
        return index;
    }

    [Fact]
    public void Load_IndexesDistinctDroppedItems()
    {
        MonsterDropIndex s = NewIndex();
        // 175, 176, 64 — three distinct dropped ids (0 slots skipped).
        Assert.Equal(3, s.ItemCount);
    }

    [Fact]
    public void DroppersOf_SingleDropper_ReturnsThatMonster()
    {
        MonsterDropIndex s = NewIndex();
        MonsterDropIndex.MonsterDrop drop = Assert.Single(s.DroppersOf(176));
        Assert.Equal(20, drop.MonsterId);
        Assert.Equal("goblin", drop.MonsterName);
        Assert.Equal(10, drop.DropPercent);
    }

    [Fact]
    public void DroppersOf_ItemFromTwoMonsters_ReturnsBoth()
    {
        MonsterDropIndex s = NewIndex();
        Assert.Equal(new[] { 10, 20 }, s.DroppersOf(175).Select(d => d.MonsterId).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void DroppersOf_ZeroDropPercent_Preserved()
    {
        MonsterDropIndex s = NewIndex();
        MonsterDropIndex.MonsterDrop drop = Assert.Single(s.DroppersOf(64));
        Assert.Equal(0, drop.DropPercent);
    }

    [Fact]
    public void DroppersOf_UnDroppedItem_ReturnsEmpty()
    {
        MonsterDropIndex s = NewIndex();
        Assert.Empty(s.DroppersOf(9999));
        Assert.False(s.AnyMonsterDrops(9999));
    }

    [Fact]
    public void AnyMonsterDrops_DroppedItem_True()
    {
        MonsterDropIndex s = NewIndex();
        Assert.True(s.AnyMonsterDrops(175));
    }

    [Fact]
    public void ZeroSlot_IsNotIndexedAsItem()
    {
        MonsterDropIndex s = NewIndex();
        // The 0 sentinel must never become a dropped "item 0".
        Assert.False(s.AnyMonsterDrops(0));
    }

    [Fact]
    public void SpawnRoomsOf_ParsesSummonedByTokens()
    {
        MonsterDropIndex s = NewIndex();
        Assert.Equal(new[] { new RoomKey(1, 100), new RoomKey(1, 101) }, s.SpawnRoomsOf(10).ToArray());
    }

    [Fact]
    public void SpawnRoomsOf_SkipsMalformedTokens()
    {
        MonsterDropIndex s = NewIndex();
        // "oup: 1/400" still yields 1/400; the slash-less "102" is skipped.
        Assert.Equal(new[] { new RoomKey(1, 400), new RoomKey(1, 401) }, s.SpawnRoomsOf(40).ToArray());
    }

    [Fact]
    public void NonDropper_IsNotSpawnIndexed()
    {
        MonsterDropIndex s = NewIndex();
        // The rat drops nothing, so its spawn room is never indexed.
        Assert.Empty(s.SpawnRoomsOf(30));
    }

    [Fact]
    public void ActiveSetChanged_ToNull_ClearsIndex()
    {
        MonsterDropIndex s = NewIndex();
        Assert.True(s.ItemCount > 0);

        s.OnActiveSetChanged(null);

        Assert.Equal(0, s.ItemCount);
        Assert.False(s.AnyMonsterDrops(175));
        Assert.Empty(s.SpawnRoomsOf(10));
    }
}
