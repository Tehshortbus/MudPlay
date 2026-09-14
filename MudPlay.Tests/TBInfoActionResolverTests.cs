using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins TBInfoActionResolver's keyword enumeration: remoteaction lever/unlock
// keywords vs. the item-yielding room-action keywords (the Dwarven Mines
// "mine ore" gather commands, which the remoteaction filter deliberately skips).
public sealed class TBInfoActionResolverTests : IDisposable
{
    private readonly string _root;

    public TBInfoActionResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-action-resolver-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private TBInfoStore NewStore(string json)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "TBInfo.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        TBInfoStore store = new(cache);
        store.OnActiveSetChanged("alpha");
        return store;
    }

    // The real Paradigm Copper Mine "mine ore" chain (TBInfo 1061): a checkitem
    // (pickaxe) + testskill gate ending in a `random` block that yields ore.
    private const string MineOreJson = """
        [ { "Number": 1061, "LinkTo": 0,
            "Action": "mine ore:checkitem 936 1608:adddelay 5:testskill strength 0 1062:random 1064\nmine vein:checkitem 936 1608:adddelay 5:testskill strength 0 1062:random 1064\nmine copper vein:checkitem 936 1608:adddelay 5:testskill strength 0 1062:random 1064\n",
            "Called From": "Room 6/1664" } ]
        """;

    [Fact]
    public void RoomActionKeywords_SurfaceMineGatherCommands()
    {
        TBInfoStore store = NewStore(MineOreJson);

        string[] kws = TBInfoActionResolver.EnumerateRoomActionKeywords(store, 1061).ToArray();

        Assert.Equal(new[] { "mine ore", "mine vein", "mine copper vein" }, kws);
    }

    [Fact]
    public void RoomActionKeywords_DirectGiveitem_AlsoSurfaces()
    {
        // A variant that gives the ore directly rather than via a random block.
        const string json = """
            [ { "Number": 200, "LinkTo": 0,
                "Action": "mine ore:checkitem 936 1608:giveitem 3533\ngather ore:checkitem 936 1608:giveitem 3533\n",
                "Called From": "Room 6/1900" } ]
            """;
        TBInfoStore store = NewStore(json);

        string[] kws = TBInfoActionResolver.EnumerateRoomActionKeywords(store, 200).ToArray();

        Assert.Equal(new[] { "mine ore", "gather ore" }, kws);
    }

    [Fact]
    public void RoomActionKeywords_SkipTeleportAndRemoteaction()
    {
        // A teleport line and a remoteaction line are surfaced by their own
        // resolvers, so the room-action enumerator must not double-list them —
        // only the giveitem line qualifies.
        const string json = """
            [ { "Number": 300, "LinkTo": 0,
                "Action": "go hole:teleport 487 2\npull lever:remoteaction 1012 1840 0 0\ntake gem:giveitem 555\n",
                "Called From": "Room 1/10" } ]
            """;
        TBInfoStore store = NewStore(json);

        string[] kws = TBInfoActionResolver.EnumerateRoomActionKeywords(store, 300).ToArray();

        Assert.Equal(new[] { "take gem" }, kws);
    }

    [Fact]
    public void RemoteActionKeywords_IgnoreMineGatherLines()
    {
        // The remoteaction enumerator (used for exit-unlock fallbacks) must NOT
        // pick up the mine/gather lines — they carry no remoteaction directive.
        TBInfoStore store = NewStore(MineOreJson);

        Assert.Empty(TBInfoActionResolver.EnumerateRemoteActionKeywords(store, 1061));
    }

    [Fact]
    public void CommandRequirements_SinglePrice_ParsesCost_SkipsFailTextblock()
    {
        // The real Paradigm dice game (TB#997): "price 10000 1560" — 10000 copper
        // is the cost, 1560 is the can't-afford textblock, not a second price.
        const string json = """
            [ { "Number": 400, "LinkTo": 0,
                "Action": "roll dice:price 10000 1560:random 998\nplay dice:price 10000 1560:random 998\n",
                "Called From": "Room 1/2" } ]
            """;
        TBInfoStore store = NewStore(json);

        var cmds = TBInfoActionResolver.EnumerateCommandRequirements(store, 400).ToArray();

        Assert.Equal(2, cmds.Length);
        Assert.Equal("roll dice", cmds[0].Keyword);
        Assert.Equal(10000L, cmds[0].MaxCopper);
        Assert.False(cmds[0].Tiered);
    }

    [Fact]
    public void CommandRequirements_BribeGuard_IsTiered_MaxIsCeiling()
    {
        // The jail bribe-guard's six escalating prices (1 gold → 10 runic): the
        // charge is the largest tier the player can afford, so only the ceiling
        // (10,000,000 copper = 10 runic) is meaningful, and Tiered is set.
        const string json = """
            [ { "Number": 500, "LinkTo": 0,
                "Action": "bribe guard:cast 5432:cast 5434:price 100:price 1000:price 10000:price 100000:price 1000000:price 10000000\n",
                "Called From": "Room 1/541" } ]
            """;
        TBInfoStore store = NewStore(json);

        var pc = Assert.Single(TBInfoActionResolver.EnumerateCommandRequirements(store, 500).ToArray());

        Assert.Equal("bribe guard", pc.Keyword);
        Assert.Equal(10000000L, pc.MaxCopper);
        Assert.True(pc.Tiered);
    }

    [Fact]
    public void CommandRequirements_NoPriceNoLevel_YieldsNothing()
    {
        TBInfoStore store = NewStore(MineOreJson);
        Assert.Empty(TBInfoActionResolver.EnumerateCommandRequirements(store, 1061));
    }

    [Fact]
    public void CommandRequirements_Passage_CarriesBothFareAndLevelFloor()
    {
        // Paradigm's Blackwater Harbor wharf (CMD 4986): the captain charges a
        // fare AND refuses you under a level. Surfacing only the fare hid why
        // the sailing would be refused.
        const string json = """
            [ { "Number": 4986, "LinkTo": 0,
                "Action": "secure passage to albion:minlevel 50 3887:price 2000000 3890:random 4987\n",
                "Called From": "Room 14/759" } ]
            """;
        TBInfoStore store = NewStore(json);

        var pc = Assert.Single(TBInfoActionResolver.EnumerateCommandRequirements(store, 4986).ToArray());

        Assert.Equal("secure passage to albion", pc.Keyword);
        Assert.Equal(2000000L, pc.MaxCopper);
        Assert.Equal(50, pc.MinLevel);   // 3887 is the refusal textblock, not the level
    }

    [Fact]
    public void CommandRequirements_LevelFloorAlone_StillYields()
    {
        // A free but level-gated command used to be dropped for having no price
        // to show, so the tooltip said nothing about why it would refuse. The
        // bare `minlevel 20` form (no trailing textblock) has to parse too.
        const string json = """
            [ { "Number": 9801, "LinkTo": 0,
                "Action": "dive sinkhole:minlevel 20:message 8702:cast 5100\n",
                "Called From": "Room 12/1075" } ]
            """;
        TBInfoStore store = NewStore(json);

        var pc = Assert.Single(TBInfoActionResolver.EnumerateCommandRequirements(store, 9801).ToArray());

        Assert.Equal("dive sinkhole", pc.Keyword);
        Assert.Equal(0L, pc.MaxCopper);
        Assert.False(pc.Tiered);
        Assert.Equal(20, pc.MinLevel);
    }
}
