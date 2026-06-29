using System.Collections.Generic;
using System.IO;
using System.Linq;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 11 (Part B) — <see cref="GameDataSetManager"/> copy / move / delete
/// operations over the shared per-set loop library. Isolated via unique
/// GUID-suffixed set names under <see cref="AppPaths.GameDataRoot"/>
/// (AppPaths caches its root at static-init, so we can't sandbox it) —
/// Dispose deletes them so nothing leaks into the user's real Data/ tree.
/// </summary>
public sealed class GameDataSetManagerTests : IDisposable
{
    private readonly List<string> _createdSets = new();

    public void Dispose()
    {
        foreach (string set in _createdSets)
        {
            try
            {
                string dir = Path.Combine(AppPaths.GameDataRoot, set);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ----- fixture ---------------------------------------------------

    private string NewSetName()
    {
        string name = "test-gdset-" + Guid.NewGuid().ToString("N").Substring(0, 12);
        _createdSets.Add(name);
        return name;
    }

    /// <summary>Create the set directory so it counts as "exists".</summary>
    private string CreateSet()
    {
        string name = NewSetName();
        Directory.CreateDirectory(AppPaths.GameDataSetDir(name));
        return name;
    }

    /// <summary>Drop a file into the set's shared Loops/ folder.</summary>
    private static void SeedLoop(string setName, string fileName, string content = "x")
    {
        string loops = AppPaths.GameDataSetLoopsFolder(setName);
        Directory.CreateDirectory(loops);
        File.WriteAllText(Path.Combine(loops, fileName), content);
    }

    private static GameDataSetManager NewManager(
        GameDataCache cache, Action? reload = null, Action<string>? onDeleted = null) =>
        new(cache, reload ?? (() => { }), onDeleted ?? (_ => { }));

    // ----- Copy ------------------------------------------------------

    [Fact]
    public void CopyLoops_CopiesFilesAndLeavesSourceIntact()
    {
        GameDataCache cache = new();
        string src = CreateSet();
        string dst = CreateSet();
        SeedLoop(src, "a.loop");
        SeedLoop(src, "b.lair");

        GameDataSetManager.OpResult result = NewManager(cache).CopyLoops(src, dst);

        Assert.True(result.Ok);
        Assert.True(File.Exists(Path.Combine(AppPaths.GameDataSetLoopsFolder(dst), "a.loop")));
        Assert.True(File.Exists(Path.Combine(AppPaths.GameDataSetLoopsFolder(dst), "b.lair")));
        // Source untouched on a copy.
        Assert.True(File.Exists(Path.Combine(AppPaths.GameDataSetLoopsFolder(src), "a.loop")));
    }

    [Fact]
    public void CopyLoops_PreservesNestedSubdirectories()
    {
        GameDataCache cache = new();
        string src = CreateSet();
        string dst = CreateSet();
        string nested = Path.Combine(AppPaths.GameDataSetLoopsFolder(src), "Town", "Inner");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "deep.loop"), "x");

        GameDataSetManager.OpResult result = NewManager(cache).CopyLoops(src, dst);

        Assert.True(result.Ok);
        Assert.True(File.Exists(Path.Combine(
            AppPaths.GameDataSetLoopsFolder(dst), "Town", "Inner", "deep.loop")));
    }

    // ----- Move ------------------------------------------------------

    [Fact]
    public void MoveLoops_MovesFilesAndRemovesSourceLibrary()
    {
        GameDataCache cache = new();
        string src = CreateSet();
        string dst = CreateSet();
        SeedLoop(src, "a.loop");

        GameDataSetManager.OpResult result = NewManager(cache).MoveLoops(src, dst);

        Assert.True(result.Ok);
        Assert.True(File.Exists(Path.Combine(AppPaths.GameDataSetLoopsFolder(dst), "a.loop")));
        // Source loop library removed entirely on a move.
        Assert.False(Directory.Exists(AppPaths.GameDataSetLoopsFolder(src)));
    }

    // ----- validation guards ----------------------------------------

    [Fact]
    public void CopyLoops_SameSet_Fails()
    {
        GameDataCache cache = new();
        string s = CreateSet();
        SeedLoop(s, "a.loop");

        GameDataSetManager.OpResult result = NewManager(cache).CopyLoops(s, s);

        Assert.False(result.Ok);
    }

    [Fact]
    public void CopyLoops_EmptySource_Fails()
    {
        GameDataCache cache = new();
        string dst = CreateSet();

        GameDataSetManager.OpResult result = NewManager(cache).CopyLoops("", dst);

        Assert.False(result.Ok);
    }

    [Fact]
    public void CopyLoops_MissingDestination_Fails()
    {
        GameDataCache cache = new();
        string src = CreateSet();
        SeedLoop(src, "a.loop");
        string ghost = NewSetName(); // never created on disk

        GameDataSetManager.OpResult result = NewManager(cache).CopyLoops(src, ghost);

        Assert.False(result.Ok);
    }

    [Fact]
    public void CopyLoops_SourceHasNoLoops_Fails()
    {
        GameDataCache cache = new();
        string src = CreateSet(); // exists but empty Loops/
        string dst = CreateSet();

        GameDataSetManager.OpResult result = NewManager(cache).CopyLoops(src, dst);

        Assert.False(result.Ok);
    }

    // ----- active-set reload ----------------------------------------

    [Fact]
    public void CopyLoops_IntoActiveSet_FiresReload()
    {
        GameDataCache cache = new();
        string src = CreateSet();
        string dst = CreateSet();
        SeedLoop(src, "a.loop");
        cache.SwitchSet(dst);

        int reloads = 0;
        NewManager(cache, reload: () => reloads++).CopyLoops(src, dst);

        Assert.Equal(1, reloads);
    }

    [Fact]
    public void CopyLoops_UnrelatedSets_DoesNotReload()
    {
        GameDataCache cache = new();
        string src = CreateSet();
        string dst = CreateSet();
        string other = CreateSet();
        SeedLoop(src, "a.loop");
        cache.SwitchSet(other);

        int reloads = 0;
        NewManager(cache, reload: () => reloads++).CopyLoops(src, dst);

        Assert.Equal(0, reloads);
    }

    // ----- Delete ----------------------------------------------------

    [Fact]
    public void DeleteSet_RemovesFolderFromDisk()
    {
        GameDataCache cache = new();
        string set = CreateSet();
        SeedLoop(set, "a.loop");

        GameDataSetManager.OpResult result = NewManager(cache).DeleteSet(set);

        Assert.True(result.Ok);
        Assert.False(Directory.Exists(AppPaths.GameDataSetDir(set)));
    }

    [Fact]
    public void DeleteSet_FiresOnSetDeletedCallback()
    {
        GameDataCache cache = new();
        string set = CreateSet();

        string? deleted = null;
        NewManager(cache, onDeleted: s => deleted = s).DeleteSet(set);

        Assert.Equal(set, deleted);
    }

    [Fact]
    public void DeleteSet_OfActiveSet_SwitchesCacheToNull()
    {
        GameDataCache cache = new();
        string set = CreateSet();
        cache.SwitchSet(set);
        Assert.Equal(set, cache.ActiveSet);

        NewManager(cache).DeleteSet(set);

        Assert.Null(cache.ActiveSet);
    }

    [Fact]
    public void DeleteSet_NonActiveSet_LeavesActiveUnchanged()
    {
        GameDataCache cache = new();
        string active = CreateSet();
        string doomed = CreateSet();
        cache.SwitchSet(active);

        NewManager(cache).DeleteSet(doomed);

        Assert.Equal(active, cache.ActiveSet);
    }

    [Fact]
    public void DeleteSet_MissingSet_FailsAndDoesNotFireCallback()
    {
        GameDataCache cache = new();
        string ghost = NewSetName(); // never created

        bool fired = false;
        GameDataSetManager.OpResult result =
            NewManager(cache, onDeleted: _ => fired = true).DeleteSet(ghost);

        Assert.False(result.Ok);
        Assert.False(fired);
    }
}
