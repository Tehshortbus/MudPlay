using System.Collections.Concurrent;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A set switch is one step to every other thread: the old set's tables go and the new
// set's name arrives together. Taken apart (tables dropped, then the name changed,
// then the count moved again) it let a lookup running in between read the old set's
// file and leave that table in place for the whole of the new set's load.
public sealed class GameDataCacheSwitchTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);
    private static readonly string[] Tables = { "Monsters", "Items", "Spells", "Shops" };
    private readonly string _root;

    public GameDataCacheSwitchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-gdc-switch-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // Every table of the set holds one row named after the set.
    private void SeedSet(string set)
    {
        string dir = Path.Combine(_root, set);
        Directory.CreateDirectory(dir);
        foreach (string table in Tables)
            File.WriteAllText(Path.Combine(dir, table + ".json"), "[{\"Number\":1,\"Name\":\"" + set + "\"}]");
    }

    private static string? FirstName(GameTable? table) =>
        table is not null && table.Rows[0].TryGetProperty("Name", out GameValue name) ? name.GetString() : null;

    // The count moves once for a switch and once for a re-import: there is no load in
    // between for a lookup to belong to. What a handler sees is the load that stays.
    [Fact]
    public void ASwitchIsOneLoad_AndHandlersSeeIt()
    {
        SeedSet("alpha");
        SeedSet("beta");
        GameDataCache cache = new(_root);
        List<GameDataCache.Load> seen = new();
        cache.ActiveSetChanged += _ => seen.Add(cache.ActiveLoad);
        long start = cache.ActiveLoad.Generation;

        cache.SwitchSet("alpha");
        Assert.Equal(new GameDataCache.Load("alpha", start + 1), cache.ActiveLoad);
        cache.SwitchSet("beta");
        Assert.Equal(new GameDataCache.Load("beta", start + 2), cache.ActiveLoad);
        cache.ReloadActiveSet();
        Assert.Equal(new GameDataCache.Load("beta", start + 3), cache.ActiveLoad);
        cache.SwitchSet(null);
        Assert.Equal(new GameDataCache.Load(null, start + 4), cache.ActiveLoad);

        Assert.Equal(
            new[]
            {
                new GameDataCache.Load("alpha", start + 1), new GameDataCache.Load("beta", start + 2),
                new GameDataCache.Load("beta", start + 3), new GameDataCache.Load(null, start + 4),
            },
            seen);
    }

    // Lookups on four threads while the set goes alpha, beta, none, round and round.
    // Whatever the interleaving: no lookup throws, and once a switch has returned, the
    // tables the cache serves are that set's, then and for as long as the set stays.
    [Fact]
    public void LookupsRacingSwitches_NeverThrow_AndNeverLeaveAnotherSetsTableServed()
    {
        SeedSet("alpha");
        SeedSet("beta");
        GameDataCache cache = new(_root);
        ConcurrentQueue<Exception> faults = new();
        int stop = 0;
        Thread[] lookups = Enumerable.Range(0, 4).Select(n => new Thread(() =>
        {
            int next = n;
            while (Volatile.Read(ref stop) == 0)
            {
                try { cache.GetTable(Tables[next++ % Tables.Length]); }
                catch (Exception ex) { faults.Enqueue(ex); }
            }
        })).ToArray();
        foreach (Thread thread in lookups) thread.Start();

        List<string> wrong = new();
        void Check(string set, int round, string when)
        {
            foreach (string table in Tables)
                if (FirstName(cache.GetTable(table)) is var name && name != set)
                    wrong.Add($"round {round}, {when}: {table} of '{set}' is '{name ?? "(none)"}'");
        }

        string?[] order = { "alpha", "beta", null };
        string? active = null;
        for (int round = 0; round < 900 && wrong.Count == 0; round++)
        {
            // A table put in place late in the last load would still be there now.
            if (active is not null) Check(active, round, "before the next switch");
            active = order[round % order.Length];
            cache.SwitchSet(active);
            if (active is not null) Check(active, round, "after the switch");
        }
        Volatile.Write(ref stop, 1);
        foreach (Thread thread in lookups) Assert.True(thread.Join(Limit), "a lookup thread never stopped");

        Assert.Empty(faults);
        Assert.Empty(wrong);
    }
}
