using System.IO;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Load + lookup coverage for <see cref="TBInfoStore"/>. Commit 5
/// (teleport handler) layers the Action-chain directive parser on
/// top; commit 1 just verifies the raw rows round-trip.
/// </summary>
public sealed class TBInfoStoreTests : IDisposable
{
    private readonly string _root;

    public TBInfoStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-tbinfo-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // Note: NUL-sentinel handling is exercised below via a programmatic
    // fixture (NulSentinelAction_NormalisedToNull) because embedding a
    // raw NUL byte in a C# raw-string literal would propagate into the
    // source file and corrupt grep / build tooling.
    private const string TBInfoJson = """
        [
          { "Number": 5, "LinkTo": 6,
            "Action": "boat:7\npassage:7\nskiff:7\ncharge:7\n",
            "Called From": "Monster #61" },
          { "Number": 6, "LinkTo": 0,
            "Action": "",
            "Called From": "Textblock #5" },
          { "Number": 7, "LinkTo": 0,
            "Action": "",
            "Called From": "Textblock #5" },
          { "Number": 997, "LinkTo": 0,
            "Action": "roll dice:price 10000 1560:random 998\n",
            "Called From": "Room 1/2, Room 6/1333, Room 1/2337" }
        ]
        """;

    private TBInfoStore NewStore(string json = TBInfoJson)
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
    public void Load_ParsesEveryRow()
    {
        TBInfoStore store = NewStore();
        Assert.Equal(4, store.EntryCount);
    }

    [Fact]
    public void GetEntry_ByNumber_ReturnsMatchingRow()
    {
        TBInfoStore store = NewStore();
        TBInfoEntry? e = store.GetEntry(5);
        Assert.NotNull(e);
        Assert.Equal(5,  e!.Number);
        Assert.Equal(6,  e.LinkTo);
        Assert.NotNull(e.Action);
        Assert.Contains("boat:7", e.Action);
        Assert.Equal("Monster #61", e.CalledFrom);
    }

    [Fact]
    public void NulSentinelAction_NormalisedToNull()
    {
        // The MDB exports the empty-action sentinel as a single NUL
        // byte. JSON encodes it as the  escape; the loader
        // collapses it to a C# null on Action so consumers don't
        // carry the sentinel through the directive parser.
        string json = "[ { \"Number\": 42, \"LinkTo\": 0, \"Action\": \"\\u0000\", \"Called From\": \"Textblock #41\" } ]";
        TBInfoStore store = NewStore(json);

        TBInfoEntry? e = store.GetEntry(42);
        Assert.NotNull(e);
        Assert.Null(e!.Action);
    }

    [Fact]
    public void GetEntry_UnknownNumber_ReturnsNull()
    {
        TBInfoStore store = NewStore();
        Assert.Null(store.GetEntry(9999));
    }

    [Fact]
    public void ActiveSetChanged_ToNull_ClearsStore()
    {
        TBInfoStore store = NewStore();
        Assert.True(store.EntryCount > 0);

        store.OnActiveSetChanged(null);

        Assert.Equal(0, store.EntryCount);
        Assert.Null(store.ActiveSet);
    }

    [Fact]
    public void EntriesEnumeration_ReturnsAllLoaded()
    {
        TBInfoStore store = NewStore();
        var numbers = store.Entries.Select(e => e.Number).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { 5, 6, 7, 997 }, numbers);
    }
}
