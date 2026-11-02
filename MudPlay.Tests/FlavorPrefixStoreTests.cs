using System.IO;
using MudPlay.Game.Combat;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// FlavorPrefixStore — the per-set editable vocabulary of monster flavor adjectives.
// Round-trips write to a unique set folder under the resolved GameDataRoot and clean
// up in Dispose, mirroring the other per-set store tests.
public sealed class FlavorPrefixStoreTests : IDisposable
{
    private readonly string _setName = "test-flavor-" + Guid.NewGuid().ToString("N")[..12];

    public void Dispose()
    {
        try
        {
            string setFolder = Path.Combine(AppPaths.GameDataRoot, _setName);
            if (Directory.Exists(setFolder)) Directory.Delete(setFolder, recursive: true);
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public void NewStore_CarriesBuiltInDefaults()
    {
        FlavorPrefixStore store = new();
        foreach (string p in MonsterFlavorPrefixes.DefaultPrefixes)
            Assert.True(store.IsPrefix(p), $"expected default '{p}'");
        Assert.True(store.IsPrefix("LARGE"));       // case-insensitive
        Assert.False(store.IsPrefix("stinking"));
    }

    [Fact]
    public void NoActiveSet_AddStaysInMemory_NoFileWritten()
    {
        FlavorPrefixStore store = new();     // never Load'd → ActiveSet null
        Assert.True(store.Add("stinking"));
        Assert.True(store.IsPrefix("stinking"));
        Assert.False(File.Exists(AppPaths.FlavorPrefixesFile(_setName)));
    }

    [Fact]
    public void Add_PersistsPerSet_AndReloads()
    {
        FlavorPrefixStore a = new();
        a.Load(_setName);
        Assert.True(a.Add("stinking"));
        Assert.True(File.Exists(AppPaths.FlavorPrefixesFile(_setName)));

        FlavorPrefixStore b = new();
        b.Load(_setName);
        Assert.True(b.IsPrefix("stinking"));
        Assert.True(b.IsPrefix("large"));    // defaults are stored whole, not as a delta
    }

    [Fact]
    public void Remove_PersistsRemoval()
    {
        FlavorPrefixStore a = new();
        a.Load(_setName);
        Assert.True(a.Remove("large"));

        FlavorPrefixStore b = new();
        b.Load(_setName);
        Assert.False(b.IsPrefix("large"));
        Assert.True(b.IsPrefix("nasty"));    // other defaults survive
    }

    [Fact]
    public void Add_Duplicate_ReturnsFalse_CaseInsensitiveAndTrimmed()
    {
        FlavorPrefixStore store = new();
        Assert.False(store.Add("Large"));    // already a default
        Assert.False(store.Add("  large "));
        Assert.False(store.Add(""));
    }

    [Fact]
    public void ResetToDefaults_RestoresBuiltIns_DropsCustom()
    {
        FlavorPrefixStore store = new();
        store.Load(_setName);
        store.Add("stinking");
        store.Remove("large");

        store.ResetToDefaults();

        Assert.True(store.IsPrefix("large"));
        Assert.False(store.IsPrefix("stinking"));

        // Persisted — a fresh load sees the reset list.
        FlavorPrefixStore reloaded = new();
        reloaded.Load(_setName);
        Assert.True(reloaded.IsPrefix("large"));
        Assert.False(reloaded.IsPrefix("stinking"));
    }

    [Fact]
    public void Changed_FiresOnLoadAddRemove()
    {
        FlavorPrefixStore store = new();
        int count = 0;
        store.Changed += () => count++;
        store.Load(_setName);         // 1
        store.Add("stinking");        // 2
        store.Remove("stinking");     // 3
        Assert.Equal(3, count);
    }

    // A hand edit's null among the set's prefixes is no prefix; the rest load.
    [Fact]
    public void ANullInTheFile_IsPassedOver()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.FlavorPrefixesFile(_setName))!);
        File.WriteAllText(AppPaths.FlavorPrefixesFile(_setName), """[ null, "stinking" ]""");

        FlavorPrefixStore store = new();
        store.Load(_setName);

        Assert.True(store.IsPrefix("stinking"));
    }
}
