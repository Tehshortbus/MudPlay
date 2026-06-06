using System.IO;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class ItemNameStoreTests : IDisposable
{
    private readonly string _root;

    public ItemNameStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-itemnames-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string ItemsJson = """
        [
          { "Number": 172,  "Name": "black star key",   "Encum": 5,  "ItemType": 7 },
          { "Number": 1980, "Name": "ancient brass key", "Encum": 5,  "ItemType": 7 },
          { "Number": 1720, "Name": "white dragon boots","Encum": 65, "ItemType": 0 }
        ]
        """;

    private ItemNameStore NewStore()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), ItemsJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        ItemNameStore store = new(cache);
        store.OnActiveSetChanged("alpha");
        return store;
    }

    [Fact]
    public void Load_IndexesAllRows()
    {
        ItemNameStore s = NewStore();
        Assert.Equal(3, s.EntryCount);
    }

    [Fact]
    public void GetName_KnownItemId_ReturnsName()
    {
        ItemNameStore s = NewStore();
        Assert.Equal("black star key", s.GetName(172));
        Assert.Equal("ancient brass key", s.GetName(1980));
    }

    [Fact]
    public void GetName_UnknownItemId_ReturnsNull()
    {
        ItemNameStore s = NewStore();
        Assert.Null(s.GetName(9999));
    }

    [Fact]
    public void ActiveSetChanged_ToNull_ClearsStore()
    {
        ItemNameStore s = NewStore();
        Assert.True(s.EntryCount > 0);

        s.OnActiveSetChanged(null);

        Assert.Equal(0, s.EntryCount);
        Assert.Null(s.GetName(172));
    }
}
