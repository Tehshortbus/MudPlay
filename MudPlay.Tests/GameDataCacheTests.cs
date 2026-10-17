using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MudPlay.Game;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// GameDataCache uses an isolated temp root per test so
// the suite doesn't leak fixtures into the user's real Data folder.
public sealed class GameDataCacheTests : IDisposable
{
    private readonly string _root;

    public GameDataCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-gdc-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // A raw, unescaped 0x1E control byte inside a JSON string — the same shape of
    // corruption a pre-fix MDB import (broken multi-page LVAL memo reader) wrote
    // into real game-data tables. Invalid per the JSON spec; System.Text.Json
    // throws JsonReaderException on it rather than silently accepting it.
    private static readonly string MalformedJson = "[{\"Name\":\"Go" + (char)0x1E + "blin\"}]";

    private GameDataCache NewCache() => new GameDataCache(_root);

    // A field the row must have.
    private static GameValue Field(GameRow? row, string name) =>
        row!.Value.TryGetProperty(name, out GameValue value) ? value : throw new KeyNotFoundException(name);

    private static string? FirstName(GameTable? table) => Field(table!.Rows[0], "Name").GetString();

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

    // The set-load log says what the set's tables hold once its subscribers have
    // read what they need, table by table.
    [Fact]
    public void SwitchSet_LogsWhatTheTablesItsSubscribersLoadedHold()
    {
        SeedSet("alpha", ("Monsters", "[{\"Number\":1,\"Name\":\"Goblin\"}]"), ("Items", "[]"));
        GameDataCache cache = NewCache();
        LogService log = new();
        cache.Log = log;
        cache.ActiveSetChanged += _ =>
        {
            cache.GetTable("Monsters");
            cache.GetTable("Items");
        };

        cache.SwitchSet("alpha");

        LogEntry held = Assert.Single(log.Snapshot(), e => e.Message.StartsWith("Tables held", StringComparison.Ordinal));
        Assert.Equal(LogSeverity.Info, held.Severity);
        Assert.Equal("Tables held for 'alpha': Items 0.0 MB, Monsters 0.0 MB (0.0 MB in all).", held.Message);
    }

    [Fact]
    public void SwitchSet_EvictsTablesFromPriorSet()
    {
        SeedSet("alpha", ("Monsters", "[{\"Id\":1}]"));
        SeedSet("beta",  ("Items",    "[{\"Id\":2}]"));

        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        _ = cache.GetTable("Monsters"); // loads + caches

        Assert.True(cache.IsTableLoaded("Monsters"));

        cache.SwitchSet("beta");

        Assert.False(cache.IsTableLoaded("Monsters"));
        Assert.Equal("beta", cache.ActiveSet);
    }

    [Fact]
    public void ReloadActiveSet_EvictsTables_RefiresEvent_AndRereadsFreshJson()
    {
        string dir = SeedSet("alpha", ("Monsters", "[{\"Number\":1,\"Name\":\"Goblin\"}]"));
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        _ = cache.GetTable("Monsters");                       // load + cache
        Assert.True(cache.IsTableLoaded("Monsters"));

        List<string?> fired = new();
        cache.ActiveSetChanged += s => fired.Add(s);

        // Re-import the SAME set: rewrite the table on disk under the same name.
        File.WriteAllText(Path.Combine(dir, "Monsters.json"), "[{\"Number\":1,\"Name\":\"Orc\"}]");
        cache.ReloadActiveSet();

        Assert.False(cache.IsTableLoaded("Monsters"));                        // evicted
        Assert.Equal(new[] { "alpha" }, fired);                  // consumers notified, same name
        Assert.Equal("alpha", cache.ActiveSet);                  // still active
        // Next read re-parses the fresh JSON, not the stale cached doc.
        Assert.Equal("Orc", FirstName(cache.GetTable("Monsters")));
    }

    [Fact]
    public void ReloadActiveSet_NoActiveSet_IsNoOp()
    {
        GameDataCache cache = NewCache();
        List<string?> fired = new();
        cache.ActiveSetChanged += s => fired.Add(s);

        cache.ReloadActiveSet();

        Assert.Empty(fired);
        Assert.Null(cache.ActiveSet);
    }

    [Fact]
    public void GetTable_ReturnsNull_WhenNoSetActive()
    {
        SeedSet("alpha", ("Monsters", "[]"));
        GameDataCache cache = NewCache();

        Assert.Null(cache.GetTable("Monsters"));
    }

    [Fact]
    public void GetTable_ReturnsNull_WhenTableMissing()
    {
        SeedSet("alpha", ("Monsters", "[]"));
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");

        Assert.Null(cache.GetTable("DefinitelyNotATable"));
    }

    [Fact]
    public void GetTable_LazyLoadsAndCaches()
    {
        SeedSet("alpha", ("Monsters", "[{\"Name\":\"Goblin\"}]"));
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");

        GameTable? first = cache.GetTable("Monsters");
        GameTable? second = cache.GetTable("Monsters");

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal("Goblin", FirstName(first));
    }

    [Fact]
    public void GetTable_IsCaseInsensitive()
    {
        SeedSet("alpha", ("Monsters", "[]"));
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");

        Assert.NotNull(cache.GetTable("monsters"));
        Assert.NotNull(cache.GetTable("MONSTERS"));
    }

    // ----- Malformed table JSON (corrupted MDB import) ---------------------

    [Fact]
    public void GetTable_MalformedJson_ReturnsNullInsteadOfThrowing()
    {
        SeedSet("alpha", ("Monsters", MalformedJson));
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");

        Assert.Null(cache.GetTable("Monsters"));
        Assert.False(cache.IsTableLoaded("Monsters"));
    }

    [Fact]
    public void GetTable_MalformedJson_DoesNotReReadFileOnEachCall()
    {
        string dir = SeedSet("alpha", ("Monsters", MalformedJson));
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");

        Assert.Null(cache.GetTable("Monsters"));

        // Fix the file on disk without reloading the set — the cache should keep
        // reporting the earlier failure rather than transparently pick up the fix
        // (that requires an explicit Reload / re-import, same as any other table).
        File.WriteAllText(Path.Combine(dir, "Monsters.json"), "[{\"Name\":\"Goblin\"}]");
        Assert.Null(cache.GetTable("Monsters"));
    }

    [Fact]
    public void ReloadActiveSet_ClearsFailedTable_SoAFixedReimportIsPickedUp()
    {
        string dir = SeedSet("alpha", ("Monsters", MalformedJson));
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        Assert.Null(cache.GetTable("Monsters"));

        File.WriteAllText(Path.Combine(dir, "Monsters.json"), "[{\"Name\":\"Goblin\"}]");
        cache.ReloadActiveSet();

        Assert.Equal("Goblin", FirstName(cache.GetTable("Monsters")));
    }

    [Fact]
    public void GetTable_OneMalformedTable_DoesNotAffectOthers()
    {
        SeedSet("alpha",
            ("Monsters", MalformedJson),
            ("Items",    "[{\"Name\":\"Sword\"}]"));
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");

        Assert.Null(cache.GetTable("Monsters"));
        Assert.Equal("Sword", FirstName(cache.GetTable("Items")));
    }

    [Fact]
    public void GetTable_MalformedJson_FiresTableParseFailedOnce()
    {
        SeedSet("alpha", ("Monsters", MalformedJson));
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");

        List<string> failed = new();
        cache.TableParseFailed += failed.Add;

        Assert.Null(cache.GetTable("Monsters"));   // first lookup — parse fails → notifies
        Assert.Null(cache.GetTable("Monsters"));   // second — short-circuits, no re-notify

        Assert.Equal(new[] { "Monsters" }, failed);   // fired exactly once, with the table name
    }

    [Fact]
    public void EvictTable_LeavesTheTableForTheNextIdleSweep()
    {
        SeedSet("alpha", ("Monsters", "[]"));
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        _ = cache.GetTable("Monsters");

        Assert.True(cache.EvictTable("Monsters"));
        Assert.True(cache.IsTableLoaded("Monsters"));

        cache.EvictIdle(GameDataCache.IdleEvictAfter);
        Assert.False(cache.IsTableLoaded("Monsters"));

        // Subsequent eviction is a no-op return value.
        Assert.False(cache.EvictTable("Monsters"));
    }

    // A table another reader still uses survives an index builder's eviction: read
    // after it, it counts as in use again.
    [Fact]
    public void EvictTable_then_a_read_keeps_the_table()
    {
        SeedSet("alpha", ("Monsters", "[]"));
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        _ = cache.GetTable("Monsters");

        cache.EvictTable("Monsters");
        _ = cache.GetTable("Monsters");
        cache.EvictIdle(GameDataCache.IdleEvictAfter);

        Assert.True(cache.IsTableLoaded("Monsters"));
    }

    [Fact]
    public void EvictTable_LeavesHeldRowsReadable()
    {
        // A background build can still be walking rows it got before another thread
        // evicted the table; eviction must not pull the buffer out from under it.
        SeedSet("alpha", ("Monsters", """[{"Number":7,"Name":"orc"}]"""));
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        GameRow? row = cache.FindRowByNumber("Monsters", 7);

        cache.EvictTable("Monsters");

        Assert.Equal("orc", Field(row, "Name").GetString());
    }

    [Fact]
    public void EvictIdle_DropsOnlyTablesNotReadWithinTheWindow()
    {
        SeedSet("alpha", ("Monsters", "[]"), ("Rooms", "[]"));
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        _ = cache.GetTable("Monsters");
        _ = cache.GetTable("Rooms");

        cache.EvictIdle(TimeSpan.FromHours(1));
        Assert.True(cache.IsTableLoaded("Monsters") && cache.IsTableLoaded("Rooms"));

        cache.EvictIdle(TimeSpan.Zero);
        Assert.False(cache.IsTableLoaded("Monsters") || cache.IsTableLoaded("Rooms"));
        Assert.NotNull(cache.GetTable("Rooms"));   // a later read re-parses
    }

    [Fact]
    public async Task EvictIdle_DropsAnUnclaimedPrewarm()
    {
        string dir = SeedSet("beta", ("Rooms", "[{\"Name\":\"Old\"}]"));
        GameDataCache cache = NewCache();
        await cache.PrewarmAsync("beta", new[] { "Rooms" });

        cache.EvictIdle(TimeSpan.Zero);
        File.WriteAllText(Path.Combine(dir, "Rooms.json"), "[{\"Name\":\"New\"}]");
        cache.SwitchSet("beta");

        // Read fresh from disk — the dropped prewarm was not claimed.
        Assert.Equal("New", FirstName(cache.GetTable("Rooms")));
    }

    [Fact]
    public void FindRowByName_ReturnsMatchingRow_CaseInsensitive()
    {
        SeedSet("alpha", ("Classes",
            "[{\"Name\":\"Warrior\",\"Abil-0\":31},{\"Name\":\"Thief\",\"Abil-0\":40}]"));
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");

        GameRow? row = cache.FindRowByName("Classes", "thief");

        Assert.NotNull(row);
        Assert.Equal("Thief", Field(row, "Name").GetString());
        Assert.Equal(40, Field(row, "Abil-0").GetInt32());
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
    public void FindRowByName_ToleratesInventoryWhitespaceNoise()
    {
        // Inventory names come from the game's word-wrapped 'i' dump; a name split
        // across a wrap can arrive with a trailing / leading / doubled space. The
        // canonical game-data name has none, so the exact match must normalise the
        // key or a charged item silently resolves to nothing (report: @uses silvery
        // skullcap → "isn't a limited-use item").
        SeedSet("alpha", ("Items",
            "[{\"Number\":401,\"Name\":\"silvery skullcap\",\"UseCount\":3}]"));
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");

        foreach (string q in new[] { "silvery skullcap", "silvery skullcap ", " silvery skullcap", "silvery  skullcap", "SILVERY  SKULLCAP " })
        {
            GameRow? row = cache.FindRowByName("Items", q);
            Assert.True(row is not null, $"'{q}' should resolve to the skullcap row");
            Assert.Equal(401, Field(row, "Number").GetInt32());
        }
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
        Assert.Null(cache.GetTable("Monsters"));
    }

    // ----- PrewarmAsync (startup head-start) --------------------------------

    [Fact]
    public async Task PrewarmAsync_ThenSwitchSet_ClaimsPrewarmedDoc()
    {
        SeedSet("alpha", ("Monsters", "[{\"Name\":\"Goblin\"}]"));
        GameDataCache cache = NewCache();

        await cache.PrewarmAsync("alpha", new[] { "Monsters" });
        cache.SwitchSet("alpha");

        Assert.NotNull(cache.GetTable("Monsters"));
        // Claimed into the live cache, not left sitting in the prewarm bucket —
        // a second read must not re-parse the file.
        Assert.True(cache.IsTableLoaded("Monsters"));
    }

    [Fact]
    public async Task PrewarmAsync_WrongGuess_NeverActivated_LeavesRealCacheUntouched()
    {
        SeedSet("alpha", ("Monsters", "[]"));
        SeedSet("beta",  ("Monsters", "[]"));
        GameDataCache cache = NewCache();

        // Guessed "alpha" would load, but "beta" actually became active — the
        // wasted prewarm must not leak into beta's table cache.
        await cache.PrewarmAsync("alpha", new[] { "Monsters" });
        cache.SwitchSet("beta");

        Assert.False(cache.IsTableLoaded("Monsters"));
    }

    [Fact]
    public async Task PrewarmAsync_MissingTable_NoOp()
    {
        SeedSet("alpha");
        GameDataCache cache = NewCache();

        await cache.PrewarmAsync("alpha", new[] { "DefinitelyNotATable" });
        cache.SwitchSet("alpha");

        Assert.Null(cache.GetTable("DefinitelyNotATable"));
    }

    // Re-importing over the active set must not re-hand a stale prewarmed doc that
    // was parsed at startup but never claimed (see ReloadActiveSet).
    [Fact]
    public async Task ReloadActiveSet_DiscardsUnclaimedPrewarmForThatSet()
    {
        string dir = SeedSet("alpha", ("Monsters", "[{\"Number\":1,\"Name\":\"Goblin\"}]"));
        GameDataCache cache = NewCache();

        await cache.PrewarmAsync("alpha", new[] { "Monsters" });   // parsed, never claimed
        cache.SwitchSet("alpha");

        // Re-import rewrites the file; the stale prewarm must be dropped so the read
        // re-parses the fresh JSON rather than claiming the pre-import doc.
        File.WriteAllText(Path.Combine(dir, "Monsters.json"), "[{\"Number\":1,\"Name\":\"Orc\"}]");
        cache.ReloadActiveSet();

        Assert.Equal("Orc", FirstName(cache.GetTable("Monsters")));
    }

    // ----- ActiveRealm (Info.Legit derivation) ------------------------------

    [Fact]
    public void ActiveRealm_Legit2_IsParaMud()
    {
        // Info.Legit = 2 marks a Paradigm (GreaterMUD) data set.
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
