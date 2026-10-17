using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// GameDataCache reads a table file once however many threads ask for the table while
// the read runs (a set load starts background builds that want the tables the loading
// thread is reading), tells one load of a set from the next, and remembers what each
// table's file looked like when it was read.
public sealed class GameDataCacheSharedReadTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);
    private readonly string _root;

    public GameDataCacheSharedReadTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-gdc-shared-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private string Seed(string set, string table, string json)
    {
        string dir = Path.Combine(_root, set);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, table + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string Monsters(string name) => "[{\"Number\":1,\"Name\":\"" + name + "\"}]";

    private static string? FirstName(GameTable? table) =>
        table!.Rows[0].TryGetProperty("Name", out GameValue name) ? name.GetString() : null;

    // A lookup on its own thread, so the test can see that it is blocked.
    private sealed class Lookup
    {
        private readonly Thread _thread;
        public GameTable? Table { get; private set; }
        public Exception? Failure { get; private set; }

        public Lookup(GameDataCache cache, string table)
        {
            _thread = new Thread(() =>
            {
                try { Table = cache.GetTable(table); }
                catch (Exception ex) { Failure = ex; }
            });
            _thread.Start();
        }

        public void WaitUntilBlocked()
        {
            long deadline = Environment.TickCount64 + (long)Limit.TotalMilliseconds;
            while ((_thread.ThreadState & ThreadState.WaitSleepJoin) == 0)
            {
                Assert.True(_thread.IsAlive, "the lookup returned instead of waiting for the read in flight");
                Assert.True(Environment.TickCount64 < deadline, "the lookup never waited");
                Thread.Sleep(1);
            }
        }

        public void Join() => Assert.True(_thread.Join(Limit), "the lookup never returned");
    }

    [Fact]
    public void ActiveLoad_MovesWithEveryLoadOfASet_AndWithNothingElse()
    {
        Seed("alpha", "Monsters", Monsters("orc"));
        Seed("beta", "Monsters", Monsters("troll"));
        GameDataCache cache = new(_root);
        GameDataCache.Load none = cache.ActiveLoad;
        Assert.Null(none.Set);

        cache.SwitchSet("alpha");
        GameDataCache.Load first = cache.ActiveLoad;
        Assert.Equal("alpha", first.Set);
        Assert.NotEqual(none, first);

        // Reading, marking done and the idle sweep leave the load what it was.
        cache.GetTable("Monsters");
        cache.EvictTable("Monsters");
        cache.EvictIdle(TimeSpan.Zero);
        cache.GetTable("Monsters");
        cache.SwitchSet("alpha");
        Assert.Equal(first, cache.ActiveLoad);

        // A re-import over the active set keeps the name and is a new load.
        cache.ReloadActiveSet();
        GameDataCache.Load reimported = cache.ActiveLoad;
        Assert.Equal("alpha", reimported.Set);
        Assert.NotEqual(first, reimported);

        // Away and back is a new load of the same name.
        cache.SwitchSet("beta");
        cache.SwitchSet("alpha");
        Assert.Equal("alpha", cache.ActiveLoad.Set);
        Assert.NotEqual(reimported, cache.ActiveLoad);
    }

    [Fact]
    public void FileStampAsRead_IsTheFileAsTheCacheReadIt()
    {
        string path = Seed("alpha", "Monsters", Monsters("orc"));
        DateTime written = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");

        // Nothing is known of a table not read in this load, or with no file.
        Assert.Null(cache.FileStampAsRead("Monsters"));
        Assert.Null(cache.FileStampAsRead("Items"));
        cache.GetTable("Items");
        Assert.Null(cache.FileStampAsRead("Items"));

        cache.GetTable("Monsters");
        GameDataCache.TableFileStamp read = new(new FileInfo(path).Length, written);
        Assert.Equal(read, cache.FileStampAsRead("Monsters"));
        Assert.Equal(read, cache.FileStampAsRead("monsters"));

        // The file changing on disk changes nothing: the cache has not read it again.
        File.WriteAllText(path, Monsters("a much longer name"));
        DateTime rewritten = new(2026, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, rewritten);
        Assert.Equal(read, cache.FileStampAsRead("Monsters"));

        // Dropped by the idle sweep: still the file the dropped table came from.
        cache.EvictIdle(TimeSpan.Zero);
        Assert.False(cache.IsTableLoaded("Monsters"));
        Assert.Equal(read, cache.FileStampAsRead("Monsters"));

        // Read again after the drop: the file as this read found it.
        cache.GetTable("Monsters");
        GameDataCache.TableFileStamp reread = new(new FileInfo(path).Length, rewritten);
        Assert.NotEqual(read, reread);
        Assert.Equal(reread, cache.FileStampAsRead("Monsters"));

        // A new load of the set starts with nothing known.
        cache.ReloadActiveSet();
        Assert.Null(cache.FileStampAsRead("Monsters"));
        cache.GetTable("Monsters");
        Assert.Equal(reread, cache.FileStampAsRead("Monsters"));
        cache.SwitchSet(null);
        Assert.Null(cache.FileStampAsRead("Monsters"));
    }

    // Read, dropped by the idle sweep, and the file gone when the table is asked for
    // again: there is no table, so no stamp of one either.
    [Fact]
    public void FileStampAsRead_IsForgottenWhenTheFileIsGoneAtTheNextRead()
    {
        string path = Seed("alpha", "Monsters", Monsters("orc"));
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        cache.GetTable("Monsters");
        Assert.NotNull(cache.FileStampAsRead("Monsters"));

        cache.EvictIdle(TimeSpan.Zero);
        File.Delete(path);
        // Still the dropped table's until the cache looks again.
        Assert.NotNull(cache.FileStampAsRead("Monsters"));
        Assert.Null(cache.GetTable("Monsters"));

        Assert.Null(cache.FileStampAsRead("Monsters"));
    }

    // The same when the file is there and no longer parses.
    [Fact]
    public void FileStampAsRead_IsForgottenWhenTheNextReadDoesNotParse()
    {
        string path = Seed("alpha", "Monsters", Monsters("orc"));
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        cache.GetTable("Monsters");
        Assert.NotNull(cache.FileStampAsRead("Monsters"));

        cache.EvictIdle(TimeSpan.Zero);
        File.WriteAllText(path, "[{\"Name\":\"Go" + (char)0x1E + "blin\"}]");
        Assert.Null(cache.GetTable("Monsters"));

        Assert.Null(cache.FileStampAsRead("Monsters"));
        // And it comes back with the table once the file is mended and the set re-read.
        File.WriteAllText(path, Monsters("troll"));
        cache.ReloadActiveSet();
        cache.GetTable("Monsters");
        Assert.Equal(new FileInfo(path).Length, cache.FileStampAsRead("Monsters")!.Value.Length);
    }

    // A table claimed from a prewarm carries the stamp of the file the prewarm read,
    // not of the file on disk when it was claimed.
    [Fact]
    public async Task FileStampAsRead_OfAPrewarmedTable_IsTheFileThePrewarmRead()
    {
        string path = Seed("alpha", "Monsters", Monsters("orc"));
        DateTime written = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);
        GameDataCache.TableFileStamp prewarmed = new(new FileInfo(path).Length, written);
        GameDataCache cache = new(_root);

        await cache.PrewarmAsync("alpha", new[] { "Monsters" });
        File.WriteAllText(path, Monsters("a much longer name"));
        cache.SwitchSet("alpha");

        Assert.Equal("orc", FirstName(cache.GetTable("Monsters")));
        Assert.Equal(prewarmed, cache.FileStampAsRead("Monsters"));
    }
}
