using System;
using System.IO;
using System.Linq;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// The nav-seed ledger: additive (new bundled loops/favourites land), respects
/// deletions (anything already offered is never re-added), and migrates a pre-ledger
/// set (old binary marker) by marking everything currently bundled as already-applied
/// so no shipped item — including ones the user deleted — is resurrected. Driven
/// through the path-injected <see cref="NavSeedBootstrapper.Apply"/> so nothing touches
/// the real data root.
/// </summary>
public sealed class NavSeedBootstrapperTests : IDisposable
{
    private readonly string _root;

    public NavSeedBootstrapperTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-navseed-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
    }

    private string Bundle       => Path.Combine(_root, "bundle");
    private string LoopsDest    => Path.Combine(_root, "dest", "Loops");
    private string FavDest      => Path.Combine(_root, "dest", "Favorites.json");
    private string LedgerPath   => Path.Combine(_root, "ledger.json");
    private string LegacyMarker => Path.Combine(_root, ".nav-seeded");

    private void BundleLoop(string relPath)
    {
        string p = Path.Combine(Bundle, "Loops", relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "loop-body");
    }

    private void BundleFavorite(string label, string folder)
    {
        string json =
            "{\"Favorites\":[{\"Map\":\"1\",\"Room\":\"100\",\"Label\":\"" + label +
            "\",\"Folder\":\"" + folder + "\"}],\"FavoriteFolders\":[\"" + folder + "\"]}";
        Directory.CreateDirectory(Bundle);
        File.WriteAllText(Path.Combine(Bundle, "Favorites.json"), json);
    }

    private void Apply() =>
        NavSeedBootstrapper.Apply("stock", Bundle, LoopsDest, FavDest, LedgerPath, LegacyMarker, "testset");

    [Fact]
    public void FreshApply_CopiesLoopsAndFavourites_AndRecordsLedger()
    {
        BundleLoop("Zone/A.loop");
        BundleLoop("Zone/B.loop");
        BundleFavorite("Home", "Town");

        Apply();

        Assert.True(File.Exists(Path.Combine(LoopsDest, "Zone", "A.loop")));
        Assert.True(File.Exists(Path.Combine(LoopsDest, "Zone", "B.loop")));
        Assert.True(File.Exists(FavDest));
        Assert.Contains("Home", File.ReadAllText(FavDest));
        Assert.True(File.Exists(LedgerPath), "ledger should be written");
        Assert.Contains("Zone/A.loop", File.ReadAllText(LedgerPath));  // '/'-normalised identity
    }

    [Fact]
    public void ReApply_AfterDeletingASeededLoop_DoesNotResurrectIt()
    {
        BundleLoop("Zone/A.loop");
        BundleLoop("Zone/B.loop");
        Apply();                                              // both land, ledger records both

        File.Delete(Path.Combine(LoopsDest, "Zone", "A.loop"));   // user deletes A
        Apply();                                              // re-run (e.g. next activate)

        Assert.False(File.Exists(Path.Combine(LoopsDest, "Zone", "A.loop")));  // stays deleted
        Assert.True(File.Exists(Path.Combine(LoopsDest, "Zone", "B.loop")));
    }

    [Fact]
    public void ReApply_WithANewBundledLoop_AddsOnlyTheNewOne()
    {
        BundleLoop("Zone/A.loop");
        Apply();
        File.Delete(Path.Combine(LoopsDest, "Zone", "A.loop"));   // delete the original

        BundleLoop("Zone/C.loop");                               // a later build ships a new loop
        Apply();

        Assert.True(File.Exists(Path.Combine(LoopsDest, "Zone", "C.loop")), "new loop should be added");
        Assert.False(File.Exists(Path.Combine(LoopsDest, "Zone", "A.loop")), "deleted loop stays gone");
    }

    [Fact]
    public void Migration_FromLegacyMarker_MarksAllApplied_AndCopiesNothing()
    {
        BundleLoop("Zone/A.loop");
        BundleFavorite("Home", "Town");
        File.WriteAllText(LegacyMarker, "realm=stock\n");         // pre-ledger set

        Apply();

        Assert.False(File.Exists(Path.Combine(LoopsDest, "Zone", "A.loop")), "nothing re-added on migration");
        Assert.False(File.Exists(LegacyMarker), "legacy marker retired");
        Assert.True(File.Exists(LedgerPath), "ledger seeded");
        Assert.Contains("Zone/A.loop", File.ReadAllText(LedgerPath));

        // And a subsequent apply still adds nothing (everything already offered).
        Apply();
        Assert.False(File.Exists(Path.Combine(LoopsDest, "Zone", "A.loop")));
    }
}
