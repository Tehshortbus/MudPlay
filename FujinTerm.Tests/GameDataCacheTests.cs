using System.IO;
using System.Linq;
using System.Text.Json;
using FujinTerm.Game;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="GameDataCache"/> uses an isolated temp root per test so
/// the suite doesn't leak fixtures into the user's real Data folder.
/// </summary>
public sealed class GameDataCacheTests : IDisposable
{
    private readonly string _root;

    public GameDataCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-gdc-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private GameDataCache NewCache() => new GameDataCache(_root);

    private string SeedSet(string setName, params (string Table, string Json)[] tables)
    {
        string dir = Path.Combine(_root, setName);
        Directory.CreateDirectory(dir);
        foreach (var (table, json) in tables)
            File.WriteAllText(Path.Combine(dir, table + ".json"), json);
        return dir;
    }

    [Fact]
    public void AvailableSets_EnumeratesAlphabetically()
    {
        SeedSet("zzz-set");
        SeedSet("aaa-set");

        GameDataCache cache = NewCache();

        Assert.Equal(new[] { "aaa-set", "zzz-set" }, cache.AvailableSets.ToArray());
    }

    [Fact]
    public void SwitchSet_FiresActiveSetChanged_OnTransitionOnly()
    {
        SeedSet("alpha");
        GameDataCache cache = NewCache();

        List<string?> fired = new();
        cache.ActiveSetChanged += s => fired.Add(s);

        cache.SwitchSet("alpha");
        cache.SwitchSet("alpha");          // no-op
        cache.SwitchSet("ALPHA");          // case-insensitive — still no-op
        cache.SwitchSet(null);
        cache.SwitchSet(string.Empty);     // collapses to null — no-op

        Assert.Equal(new[] { "alpha", null }, fired);
    }

    [Fact]
    public void SwitchSet_EvictsTablesFromPriorSet()
    {
        SeedSet("alpha", ("Monsters", "[{\"Id\":1}]"));
        SeedSet("beta",  ("Items",    "[{\"Id\":2}]"));

        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        _ = cache.GetRawTable("Monsters"); // loads + caches

        Assert.Contains("Monsters", cache.LoadedTables);

        cache.SwitchSet("beta");

        Assert.Empty(cache.LoadedTables);
        Assert.Equal("beta", cache.ActiveSet);
    }

    [Fact]
    public void GetRawTable_ReturnsNull_WhenNoSetActive()
    {
        SeedSet("alpha", ("Monsters", "[]"));
        GameDataCache cache = NewCache();

        Assert.Null(cache.GetRawTable("Monsters"));
    }

    [Fact]
    public void GetRawTable_ReturnsNull_WhenTableMissing()
    {
        SeedSet("alpha", ("Monsters", "[]"));
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");

        Assert.Null(cache.GetRawTable("DefinitelyNotATable"));
    }

    [Fact]
    public void GetRawTable_LazyLoadsAndCaches()
    {
        SeedSet("alpha", ("Monsters", "[{\"Name\":\"Goblin\"}]"));
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");

        JsonDocument? first = cache.GetRawTable("Monsters");
        JsonDocument? second = cache.GetRawTable("Monsters");

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal("Goblin", first!.RootElement[0].GetProperty("Name").GetString());
    }

    [Fact]
    public void GetRawTable_IsCaseInsensitive()
    {
        SeedSet("alpha", ("Monsters", "[]"));
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");

        Assert.NotNull(cache.GetRawTable("monsters"));
        Assert.NotNull(cache.GetRawTable("MONSTERS"));
    }

    [Fact]
    public void EvictTable_DropsCachedDoc()
    {
        SeedSet("alpha", ("Monsters", "[]"));
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        _ = cache.GetRawTable("Monsters");

        Assert.True(cache.EvictTable("Monsters"));
        Assert.Empty(cache.LoadedTables);

        // Subsequent eviction is a no-op return value.
        Assert.False(cache.EvictTable("Monsters"));
    }

    [Fact]
    public void EvictAll_ClearsEveryLoadedTable()
    {
        SeedSet("alpha",
            ("Monsters", "[]"),
            ("Items",    "[]"),
            ("Spells",   "[]"));
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        _ = cache.GetRawTable("Monsters");
        _ = cache.GetRawTable("Items");

        cache.EvictAll();

        Assert.Empty(cache.LoadedTables);
        // Active set is preserved.
        Assert.Equal("alpha", cache.ActiveSet);
    }

    [Fact]
    public void FindRowByName_ReturnsMatchingRow_CaseInsensitive()
    {
        SeedSet("alpha", ("Classes",
            "[{\"Name\":\"Warrior\",\"Abil-0\":31},{\"Name\":\"Thief\",\"Abil-0\":40}]"));
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");

        JsonElement? row = cache.FindRowByName("Classes", "thief");

        Assert.NotNull(row);
        Assert.Equal("Thief", row!.Value.GetProperty("Name").GetString());
        Assert.Equal(40, row.Value.GetProperty("Abil-0").GetInt32());
    }

    [Fact]
    public void FindRowByName_ReturnsNull_WhenNoMatchOrNoSet()
    {
        SeedSet("alpha", ("Classes", "[{\"Name\":\"Warrior\"}]"));
        GameDataCache cache = NewCache();

        // No active set yet.
        Assert.Null(cache.FindRowByName("Classes", "Warrior"));

        cache.SwitchSet("alpha");
        Assert.Null(cache.FindRowByName("Classes", "Ninja"));
        Assert.Null(cache.FindRowByName("MissingTable", "Warrior"));
    }

    [Fact]
    public void SwitchSet_UnknownSet_StillFlipsActiveSet()
    {
        // A profile may point at a set that hasn't been imported yet —
        // surfacing the mismatch in the UI beats silently falling back.
        GameDataCache cache = NewCache();
        List<string?> fired = new();
        cache.ActiveSetChanged += s => fired.Add(s);

        cache.SwitchSet("not-yet-imported");

        Assert.Equal("not-yet-imported", cache.ActiveSet);
        Assert.Single(fired);
        Assert.Null(cache.GetRawTable("Monsters"));
    }

    // ----- ActiveRealm (Info.Legit derivation, MMUD Explorer parity) -------

    [Fact]
    public void ActiveRealm_Legit2_IsParaMud()
    {
        // MMUD Explorer: tabInfo.Fields("Legit") = 2 → bGreaterMUD = True.
        SeedSet("para", ("Info", "[{\"Legit\":2}]"));
        GameDataCache cache = NewCache();
        cache.SwitchSet("para");

        Assert.Equal(RealmType.ParaMud, cache.ActiveRealm);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(3)]
    public void ActiveRealm_LegitNot2_IsStock(int legit)
    {
        SeedSet("stock", ("Info", $"[{{\"Legit\":{legit}}}]"));
        GameDataCache cache = NewCache();
        cache.SwitchSet("stock");

        Assert.Equal(RealmType.Stock, cache.ActiveRealm);
    }

    [Fact]
    public void ActiveRealm_MissingInfoTable_DefaultsToStock()
    {
        SeedSet("noinfo", ("Monsters", "[]"));
        GameDataCache cache = NewCache();
        cache.SwitchSet("noinfo");

        Assert.Equal(RealmType.Stock, cache.ActiveRealm);
    }

    [Fact]
    public void ActiveRealm_MissingLegitField_DefaultsToStock()
    {
        SeedSet("nolegit", ("Info", "[{\"Name\":\"SomeRealm\"}]"));
        GameDataCache cache = NewCache();
        cache.SwitchSet("nolegit");

        Assert.Equal(RealmType.Stock, cache.ActiveRealm);
    }

    [Fact]
    public void ActiveRealm_NoActiveSet_DefaultsToStock()
    {
        SeedSet("para", ("Info", "[{\"Legit\":2}]"));
        GameDataCache cache = NewCache();

        Assert.Equal(RealmType.Stock, cache.ActiveRealm);
    }
}
