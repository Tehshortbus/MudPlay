using System;
using System.IO;
using System.Linq;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The nav-seed ledger: additive (new bundled loops/favourites land), respects
// deletions (anything already offered is never re-added), and migrates a pre-ledger
// set (old binary marker) by marking everything currently bundled as already-applied
// so no shipped item — including ones the user deleted — is resurrected. Driven
// through the path-injected NavSeedBootstrapper.Apply so nothing touches
// the real data root.
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

    private void BundleRevision(string relPath, int revision, string body)
    {
        File.WriteAllText(Path.Combine(Bundle, "Loops", relPath.Replace('/', Path.DirectorySeparatorChar)), body);
        File.WriteAllText(Path.Combine(Bundle, "LoopRevisions.json"), "{\"" + relPath + "\":" + revision + "}");
    }

    // A corrected packaged loop replaces the set's copy once; the old copy is kept
    // beside it, and a later edit by the user isn't overwritten again.
    [Fact]
    public void RevisedLoop_ReplacesTheSetsCopy_OncePerRevision()
    {
        BundleLoop("Zone/A.loop");
        BundleLoop("Zone/B.loop");
        Apply();
        string a = Path.Combine(LoopsDest, "Zone", "A.loop");
        File.WriteAllText(a, "my-edit");

        BundleRevision("Zone/A.loop", 1, "corrected");
        Apply();

        Assert.Equal("corrected", File.ReadAllText(a));
        Assert.Equal("my-edit", File.ReadAllText(a + ".bak"));
        Assert.Equal("loop-body", File.ReadAllText(Path.Combine(LoopsDest, "Zone", "B.loop")));

        File.WriteAllText(a, "edited-again");
        Apply();
        Assert.Equal("edited-again", File.ReadAllText(a));
    }

    [Fact]
    public void RevisedLoop_TheUserDeleted_StaysDeleted()
    {
        BundleLoop("Zone/A.loop");
        Apply();
        string a = Path.Combine(LoopsDest, "Zone", "A.loop");
        File.Delete(a);

        BundleRevision("Zone/A.loop", 1, "corrected");
        Apply();

        Assert.False(File.Exists(a));
    }

    [Fact]
    public void FreshSet_GetsTheCorrectedLoop_WithNoBackup()
    {
        BundleLoop("Zone/A.loop");
        BundleRevision("Zone/A.loop", 1, "corrected");

        Apply();

        string a = Path.Combine(LoopsDest, "Zone", "A.loop");
        Assert.Equal("corrected", File.ReadAllText(a));
        Assert.False(File.Exists(a + ".bak"));
    }

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

    // ----- Restore defaults (asked for by hand) -------------------------

    private string BundleLoops => Path.Combine(Bundle, "Loops");

    // The pass that runs by itself never brings a deleted loop back; the button does,
    // and the pass after it still doesn't undo or double anything.
    [Fact]
    public void RestoreLoops_PutsBackWhatWasDeleted_AndLeavesTheRestAsItIs()
    {
        BundleLoop("Zone/A.loop");
        BundleLoop("Zone/B.loop");
        Apply();
        File.Delete(Path.Combine(LoopsDest, "Zone", "A.loop"));               // deleted
        File.WriteAllText(Path.Combine(LoopsDest, "Zone", "B.loop"), "edited by the user");
        File.WriteAllText(Path.Combine(LoopsDest, "Mine.loop"), "the user's own");

        Assert.Equal(new[] { Path.Combine("Zone", "A.loop") }, NavSeedBootstrapper.MissingLoops(BundleLoops, LoopsDest));
        Assert.Equal(1, NavSeedBootstrapper.RestoreLoops(BundleLoops, LoopsDest));

        Assert.Equal("loop-body", File.ReadAllText(Path.Combine(LoopsDest, "Zone", "A.loop")));
        Assert.Equal("edited by the user", File.ReadAllText(Path.Combine(LoopsDest, "Zone", "B.loop")));
        Assert.Equal("the user's own", File.ReadAllText(Path.Combine(LoopsDest, "Mine.loop")));
        Assert.Equal(0, NavSeedBootstrapper.RestoreLoops(BundleLoops, LoopsDest));   // nothing left

        Apply();                                                              // the next activate
        Assert.Empty(Directory.GetFiles(LoopsDest, "*.bak", SearchOption.AllDirectories));
        Assert.Equal(3, Directory.GetFiles(LoopsDest, "*.loop", SearchOption.AllDirectories).Length);
    }

    // A packaged loop the user moved to a folder of their own is still theirs: the
    // bundle's copy is not put beside it, which would list the loop twice.
    [Fact]
    public void RestoreLoops_LeavesALoopTheUserMovedToAnotherFolder()
    {
        BundleLoop("Zone/A.loop");
        Apply();
        Directory.CreateDirectory(Path.Combine(LoopsDest, "Favourites"));
        File.Move(Path.Combine(LoopsDest, "Zone", "A.loop"), Path.Combine(LoopsDest, "Favourites", "A.loop"));

        Assert.Empty(NavSeedBootstrapper.MissingLoops(BundleLoops, LoopsDest));
        Assert.Equal(0, NavSeedBootstrapper.RestoreLoops(BundleLoops, LoopsDest));
        Assert.False(File.Exists(Path.Combine(LoopsDest, "Zone", "A.loop")));
    }

    // A set that was never seeded (no Loops folder at all) gets the whole bundle.
    [Fact]
    public void RestoreLoops_OnASetWithNoLoopsFolder_CopiesEveryPackagedLoop()
    {
        BundleLoop("Zone/A.loop");
        BundleLoop("B.loop");

        Assert.Equal(2, NavSeedBootstrapper.RestoreLoops(BundleLoops, LoopsDest));
        Assert.True(File.Exists(Path.Combine(LoopsDest, "Zone", "A.loop")));
        Assert.True(File.Exists(Path.Combine(LoopsDest, "B.loop")));
    }
}
