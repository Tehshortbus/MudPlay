using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Game.Quests;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A set imported again over the active one keeps its name: GameDataCache.ReloadActiveSet
// drops the tables and raises ActiveSetChanged with the same name. An index that
// remembered only the name of the set it was built from kept answering from the old
// tables until a real switch. Each index here is built, the set's files are replaced,
// the set is re-ingested, and the index must answer from the new files.
public sealed class IndexesAfterReimportTests : IDisposable
{
    private const string Set = "alpha";
    private readonly string _root;

    public IndexesAfterReimportTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-reimport-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, Set));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private void Write(string table, string json) =>
        File.WriteAllText(Path.Combine(_root, Set, table + ".json"), json);

    private GameDataCache Cache()
    {
        GameDataCache cache = new(_root);
        cache.SwitchSet(Set);
        return cache;
    }

    [Fact]
    public void MonsterSpawnIndex_AnswersFromTheReimportedMonsters()
    {
        Write("Monsters", """[ { "Number": 1, "Name": "orc", "Summoned By": "Room 1/1" } ]""");
        GameDataCache cache = Cache();
        MonsterSpawnIndex spawns = new(cache);
        Assert.Equal(new[] { 1 }, spawns.PlacedMonsterIdsAt(new RoomKey(1, 1)));

        Write("Monsters", """[ { "Number": 2, "Name": "troll", "Summoned By": "Room 2/2" } ]""");
        cache.ReloadActiveSet();

        Assert.Empty(spawns.PlacedMonsterIdsAt(new RoomKey(1, 1)));
        Assert.Equal(new[] { 2 }, spawns.PlacedMonsterIdsAt(new RoomKey(2, 2)));
        Assert.Equal(new[] { 2 }, spawns.MonsterIdsSummonedAt(new RoomKey(2, 2)));
    }

    // The warm-up the app starts on every ActiveSetChanged must be the rebuild.
    [Fact]
    public void MonsterSpawnIndex_WarmAfterAReimportRebuilds()
    {
        Write("Monsters", """[ { "Number": 1, "Name": "orc", "Summoned By": "Group: 1/1" } ]""");
        GameDataCache cache = Cache();
        MonsterSpawnIndex spawns = new(cache);
        spawns.Warm();

        Write("Monsters", "[]");
        cache.ReloadActiveSet();
        spawns.Warm();
        // The file gone: an answer now can only come from the warm-up's build.
        File.Delete(Path.Combine(_root, Set, "Monsters.json"));
        cache.EvictIdle(TimeSpan.Zero);

        Assert.Empty(spawns.AssignedMonsterIdsAt(new RoomKey(1, 1)));
    }

    [Fact]
    public void ItemSourceIndex_AnswersFromTheReimportedTables()
    {
        Write("Items", """[ { "Number": 25, "Name": "Bloodstone Orb" }, { "Number": 26, "Name": "Gate Key" } ]""");
        Write("TBInfo", """[ { "Number": 5, "Action": "get orb:giveitem 25", "Called From": "Room 3/606" } ]""");
        GameDataCache cache = Cache();
        TBInfoStore tb = new(cache);
        tb.OnActiveSetChanged(Set);
        cache.ActiveSetChanged += tb.OnActiveSetChanged;
        ItemSourceIndex sources = new(cache, tb);
        ItemGiver before = Assert.Single(sources.GiversOf(25));
        Assert.Equal((3, 606), (before.Map, before.Room));

        Write("TBInfo", """[ { "Number": 5, "Action": "get key:giveitem 26", "Called From": "Room 4/707" } ]""");
        cache.ReloadActiveSet();

        Assert.Empty(sources.GiversOf(25));
        ItemGiver after = Assert.Single(sources.GiversOf(26));
        Assert.Equal((4, 707), (after.Map, after.Room));
    }

    [Fact]
    public void RoomFloorItemIndex_AnswersFromTheReimportedRooms()
    {
        Write("TBInfo", "[]");
        Write("Rooms", """[ { "Map Number": 14, "Room Number": 10415, "Name": "Cache", "Placed": "3796" } ]""");
        GameDataCache cache = Cache();
        TBInfoStore tb = new(cache);
        tb.OnActiveSetChanged(Set);
        cache.ActiveSetChanged += tb.OnActiveSetChanged;
        RoomFloorItemIndex floor = new(cache, tb);
        Assert.Equal(new[] { 3796 }, floor.FloorItemsOf(new RoomKey(14, 10415)));

        Write("Rooms", """[ { "Map Number": 14, "Room Number": 10416, "Name": "Cache", "Placed": "73" } ]""");
        cache.ReloadActiveSet();

        Assert.Empty(floor.FloorItemsOf(new RoomKey(14, 10415)));
        Assert.Equal(new[] { 73 }, floor.FloorItemsOf(new RoomKey(14, 10416)));
    }

    [Fact]
    public void EncumbranceDebuffIndex_AnswersFromTheReimportedSpells()
    {
        Write("Spells", """[ { "Number": 1, "Name": "burden", "Abil-0": 96, "AbilVal-0": -10 } ]""");
        GameDataCache cache = Cache();
        EncumbranceDebuffIndex debuffs = new(cache);
        Assert.True(debuffs.LowersMaxEncumbrance(1));

        Write("Spells", """[ { "Number": 2, "Name": "burden", "Abil-0": 96, "AbilVal-0": -10 } ]""");
        cache.ReloadActiveSet();

        Assert.False(debuffs.LowersMaxEncumbrance(1));
        Assert.True(debuffs.LowersMaxEncumbrance(2));
    }

    [Fact]
    public void SpellAilmentIndex_AnswersFromTheReimportedSpells()
    {
        Write("Spells", """[ { "Number": 1, "Name": "venom", "Abil-0": 19, "AbilVal-0": 5 } ]""");
        GameDataCache cache = Cache();
        SpellAilmentIndex ailments = new(cache);
        Assert.True(ailments.Applies(1, "poison"));

        Write("Spells", """[ { "Number": 1, "Name": "dazzle", "Abil-0": 107, "AbilVal-0": 5 } ]""");
        cache.ReloadActiveSet();

        Assert.False(ailments.Applies(1, "poison"));
        Assert.True(ailments.Applies(1, "blind"));
    }

    // No Invalidate call: the index has to notice the re-import itself.
    [Fact]
    public void CureSpellIndex_AnswersFromTheReimportedSpells()
    {
        Write("Spells", """[ { "Number": 1, "Name": "cure poison", "Abil-0": 20, "AbilVal-0": 0 } ]""");
        GameDataCache cache = Cache();
        CureSpellIndex cures = new(cache);
        Assert.Equal(new[] { 1 }, cures.AllCures().Select(c => c.Number));

        Write("Spells", """[ { "Number": 7, "Name": "freedom", "Abil-0": 81, "AbilVal-0": 0 } ]""");
        cache.ReloadActiveSet();

        Assert.Equal(new[] { 7 }, cures.AllCures().Select(c => c.Number));
    }

    [Fact]
    public void QuestFlagIndex_AnswersFromTheReimportedTextblocks()
    {
        Write("TBInfo", """[ { "Number": 100, "Action": "ask good:giveability 200 1\n", "Called From": "Room 7/1008" } ]""");
        GameDataCache cache = Cache();
        QuestFlagIndex flags = new(cache);
        Assert.True(flags.IsQuestFlag(200));

        Write("TBInfo", """[ { "Number": 100, "Action": "ask good:giveability 201 1\n", "Called From": "Room 7/1008" } ]""");
        cache.ReloadActiveSet();

        Assert.False(flags.IsQuestFlag(200));
        Assert.True(flags.IsQuestFlag(201));
        Assert.Equal(201, Assert.Single(flags.Entries).Flag);
    }

    // The cache's load moves before the handlers that reload the textblock store run.
    // An index asked in that moment (here by a handler ahead of the store's; in the
    // app by a warm-up of the last load that only now gets to run) builds from the
    // new load's tables and the LAST load's textblocks. It may answer with that, but
    // must not keep it as the new load's.
    [Fact]
    public void ItemSourceIndex_AskedBeforeTheTextblockStoreReloads_DoesNotKeepWhatItBuilt()
    {
        Write("Items", """[ { "Number": 25, "Name": "Bloodstone Orb" }, { "Number": 26, "Name": "Gate Key" } ]""");
        Write("TBInfo", """[ { "Number": 5, "Action": "get orb:giveitem 25", "Called From": "Room 3/606" } ]""");
        GameDataCache cache = Cache();
        TBInfoStore tb = new(cache);
        tb.OnActiveSetChanged(Set);
        ItemSourceIndex sources = new(cache, tb);
        int asked = 0;
        cache.ActiveSetChanged += _ =>
        {
            asked++;
            sources.GiversOf(25);
        };
        cache.ActiveSetChanged += tb.OnActiveSetChanged;
        Assert.Single(sources.GiversOf(25));

        Write("TBInfo", """[ { "Number": 5, "Action": "get key:giveitem 26", "Called From": "Room 4/707" } ]""");
        cache.ReloadActiveSet();

        Assert.Equal(1, asked);
        Assert.Empty(sources.GiversOf(25));
        ItemGiver after = Assert.Single(sources.GiversOf(26));
        Assert.Equal((4, 707), (after.Map, after.Room));
    }

    [Fact]
    public void RoomFloorItemIndex_AskedBeforeTheTextblockStoreReloads_DoesNotKeepWhatItBuilt()
    {
        Write("Rooms", "[]");
        Write("TBInfo", """[ { "Number": 5, "Action": "make emblem:roomitem 474 1", "Called From": "Room 7/1008" } ]""");
        GameDataCache cache = Cache();
        TBInfoStore tb = new(cache);
        tb.OnActiveSetChanged(Set);
        RoomFloorItemIndex floor = new(cache, tb);
        int asked = 0;
        cache.ActiveSetChanged += _ =>
        {
            asked++;
            floor.FloorItemsOf(new RoomKey(7, 1008));
        };
        cache.ActiveSetChanged += tb.OnActiveSetChanged;
        Assert.Equal(new[] { 474 }, floor.FloorItemsOf(new RoomKey(7, 1008)));

        Write("TBInfo", """[ { "Number": 5, "Action": "make relic:roomitem 812 1", "Called From": "Room 3/500" } ]""");
        cache.ReloadActiveSet();

        Assert.Equal(1, asked);
        Assert.Empty(floor.FloorItemsOf(new RoomKey(7, 1008)));
        Assert.Equal(new[] { 812 }, floor.FloorItemsOf(new RoomKey(3, 500)));
    }

    // The same moment met by a real switch to another set.
    [Fact]
    public void IndexesAskedMidSwitch_DoNotKeepTheLastSetsTextblocksForTheNewSet()
    {
        Write("Items", """[ { "Number": 25, "Name": "Bloodstone Orb" } ]""");
        Write("Rooms", "[]");
        Write("TBInfo", """[ { "Number": 5, "Action": "get orb:giveitem 25\nmake emblem:roomitem 474 1", "Called From": "Room 3/606" } ]""");
        string beta = Path.Combine(_root, "beta");
        Directory.CreateDirectory(beta);
        File.WriteAllText(Path.Combine(beta, "Items.json"), """[ { "Number": 25, "Name": "Bloodstone Orb" } ]""");
        File.WriteAllText(Path.Combine(beta, "Rooms.json"), "[]");
        File.WriteAllText(Path.Combine(beta, "TBInfo.json"), "[]");
        GameDataCache cache = Cache();
        TBInfoStore tb = new(cache);
        tb.OnActiveSetChanged(Set);
        ItemSourceIndex sources = new(cache, tb);
        RoomFloorItemIndex floor = new(cache, tb);
        cache.ActiveSetChanged += _ =>
        {
            sources.GiversOf(25);
            floor.FloorItemsOf(new RoomKey(3, 606));
        };
        cache.ActiveSetChanged += tb.OnActiveSetChanged;
        Assert.Single(sources.GiversOf(25));
        Assert.Equal(new[] { 474 }, floor.FloorItemsOf(new RoomKey(3, 606)));

        cache.SwitchSet("beta");

        Assert.Empty(sources.GiversOf(25));
        Assert.Empty(floor.FloorItemsOf(new RoomKey(3, 606)));
    }

}
