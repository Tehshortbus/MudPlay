using System.IO;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class TBInfoTeleportResolverTests : IDisposable
{
    private readonly string _root;

    public TBInfoTeleportResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-teleport-tests-" + Path.GetRandomFileName());
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

    [Fact]
    public void Resolve_SingleTeleportChain_ReturnsKeyword()
    {
        const string json = """
            [ { "Number": 100, "LinkTo": 0,
                "Action": "go hole:message 767:teleport 487 2:message 768\n",
                "Called From": "Room 1/10" } ]
            """;
        TBInfoStore store = NewStore(json);

        string? kw = TBInfoTeleportResolver.Resolve(store, 100, new RoomKey(2, 487));
        Assert.Equal("go hole", kw);
    }

    [Fact]
    public void Resolve_MultipleAlternatives_ReturnsFirstMatching()
    {
        const string json = """
            [ { "Number": 100, "LinkTo": 0,
                "Action": "go hole:message 767:teleport 487 2:message 768\nenter hole:message 767:teleport 487 2:message 768\ncrawl hole:message 767:teleport 487 2:message 768\n",
                "Called From": "Room 1/10" } ]
            """;
        TBInfoStore store = NewStore(json);

        string? kw = TBInfoTeleportResolver.Resolve(store, 100, new RoomKey(2, 487));
        Assert.Equal("go hole", kw);                              // first line wins
    }

    [Fact]
    public void Resolve_DestinationMismatch_ReturnsNull()
    {
        const string json = """
            [ { "Number": 100, "LinkTo": 0,
                "Action": "go hole:teleport 999 9\n",
                "Called From": "Room 1/10" } ]
            """;
        TBInfoStore store = NewStore(json);

        Assert.Null(TBInfoTeleportResolver.Resolve(store, 100, new RoomKey(2, 487)));
    }

    [Fact]
    public void Resolve_NonTeleportEntry_ReturnsNull()
    {
        // Mini-game entry — has prices/random but no teleport directive.
        const string json = """
            [ { "Number": 997, "LinkTo": 0,
                "Action": "roll dice:price 10000 1560:random 998\n",
                "Called From": "Room 1/2" } ]
            """;
        TBInfoStore store = NewStore(json);

        Assert.Null(TBInfoTeleportResolver.Resolve(store, 997, new RoomKey(1, 998)));
    }

    [Fact]
    public void Resolve_UnknownCmd_ReturnsNull()
    {
        const string json = """ [] """;
        TBInfoStore store = NewStore(json);

        Assert.Null(TBInfoTeleportResolver.Resolve(store, 9999, new RoomKey(1, 1)));
    }

    [Fact]
    public void Resolve_CmdZero_ReturnsNull()
    {
        const string json = """ [] """;
        TBInfoStore store = NewStore(json);

        Assert.Null(TBInfoTeleportResolver.Resolve(store, 0, new RoomKey(1, 1)));
    }

    [Fact]
    public void Enumerate_MinLevelDirective_SurfacedOnTuple()
    {
        // Room 3/613 "go vortex" — minlevel 20 gate before the teleport.
        const string json = """
            [ { "Number": 100, "LinkTo": 0,
                "Action": "go vortex:adddelay 5:minlevel 20 1220:message 1205:teleport 669 3:message 1221\n",
                "Called From": "Room 1/613" } ]
            """;
        TBInfoStore store = NewStore(json);

        (string keyword, RoomKey dest, int minLevel) =
            Assert.Single(TBInfoTeleportResolver.EnumerateTeleports(store, 100));
        Assert.Equal("go vortex", keyword);
        Assert.Equal(new RoomKey(3, 669), dest);
        Assert.Equal(20, minLevel);
    }

    [Fact]
    public void Enumerate_NoMinLevel_ReportsZero()
    {
        const string json = """
            [ { "Number": 100, "LinkTo": 0,
                "Action": "go hole:message 767:teleport 487 2:message 768\n",
                "Called From": "Room 1/10" } ]
            """;
        TBInfoStore store = NewStore(json);

        (_, _, int minLevel) =
            Assert.Single(TBInfoTeleportResolver.EnumerateTeleports(store, 100));
        Assert.Equal(0, minLevel);
    }
}
