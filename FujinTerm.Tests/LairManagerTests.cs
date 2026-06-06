using System.IO;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 7.18 — LairManager CRUD lifecycle, working against an isolated
/// per-test BBS subfolder so the suite never touches user data. Mirrors
/// <see cref="LoopManagerTests"/>' isolation pattern.
/// </summary>
public sealed class LairManagerTests : IDisposable
{
    private readonly string _bbs;

    public LairManagerTests()
    {
        string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
        _bbs = "test-lair-" + suffix;
    }

    public void Dispose()
    {
        try
        {
            string bbsFolder = AppPaths.BbsFolder(_bbs);
            if (Directory.Exists(bbsFolder)) Directory.Delete(bbsFolder, recursive: true);
        }
        catch { /* best-effort */ }
    }

    private static LairSetup NewSetup(string name, params (int map, int room)[] markers) =>
        new(name, markers.Select(m => new LairMarker(m.map, m.room)));

    // ----- LoadAll lifecycle ----------------------------------------

    [Fact]
    public void LoadAll_UnknownBbs_LeavesEmpty()
    {
        LairManager m = new();
        m.LoadAll(_bbs);
        Assert.Empty(m.Setups);
        Assert.Equal(_bbs, m.BbsName);
    }

    [Fact]
    public void LoadAll_Null_ClearsBbsAndSetups()
    {
        LairManager m = new();
        m.LoadAll(_bbs);
        m.LoadAll(null);
        Assert.Null(m.BbsName);
        Assert.Empty(m.Setups);
    }

    // ----- Save / round-trip ----------------------------------------

    [Fact]
    public void Save_AddsSetupToCatalogueAndPersistsToBbsLoopsFolderWithLairSuffix()
    {
        LairManager m = new();
        m.LoadAll(_bbs);
        m.Save(NewSetup("Sewer rats", (5, 100), (5, 101)));

        Assert.Single(m.Setups);
        Assert.Equal("Sewer rats", m.Setups[0].Name);
        Assert.Equal(2, m.Setups[0].MarkerCount);

        // Lair setups live in the shared Loops/ folder with the
        // .lair.json suffix as the schema discriminator. Plain .json
        // files in the same folder belong to LoopManager.
        string expectedPath = Path.Combine(
            AppPaths.BbsLoopsFolder(_bbs), "Sewer rats" + LairManager.LairFileSuffix);
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void Save_RoundTripsAcrossLoadAll()
    {
        LairManager m1 = new();
        m1.LoadAll(_bbs);
        LairSetup setup = new("Mixed", new[]
        {
            new LairMarker(1, 10),
            new LairMarker(1, 11, overrideRespawnSeconds: 90),
            new LairMarker(1, 12, overrideRespawnSeconds: null, skip: true),
        })
        {
            Notes = "test notes",
        };
        m1.Save(setup);

        LairManager m2 = new();
        m2.LoadAll(_bbs);
        LairSetup? round = m2.Get("Mixed");

        Assert.NotNull(round);
        Assert.Equal(1, round!.SchemaVersion);
        Assert.Equal("test notes", round.Notes);
        Assert.Equal(3, round.Markers.Count);
        Assert.Null(round.Markers[0].OverrideRespawnSeconds);
        Assert.False(round.Markers[0].Skip);
        Assert.Equal(90, round.Markers[1].OverrideRespawnSeconds);
        Assert.True(round.Markers[2].Skip);
    }

    [Fact]
    public void Save_NoBbsBound_IsNoOp()
    {
        LairManager m = new();
        m.Save(NewSetup("orphan", (1, 1)));
        Assert.Empty(m.Setups);
    }

    [Fact]
    public void Save_RequiresName()
    {
        LairManager m = new();
        m.LoadAll(_bbs);
        Assert.Throws<ArgumentException>(() => m.Save(new LairSetup()));
    }

    // ----- Delete ----------------------------------------------------

    [Fact]
    public void Delete_RemovesFromCatalogueAndDisk()
    {
        LairManager m = new();
        m.LoadAll(_bbs);
        m.Save(NewSetup("doomed", (1, 1)));

        bool removed = m.Delete("doomed");

        Assert.True(removed);
        Assert.Empty(m.Setups);
        Assert.False(File.Exists(Path.Combine(
            AppPaths.BbsLoopsFolder(_bbs), "doomed" + LairManager.LairFileSuffix)));
    }

    [Fact]
    public void Delete_UnknownName_ReturnsFalse()
    {
        LairManager m = new();
        m.LoadAll(_bbs);
        Assert.False(m.Delete("nope"));
    }

    // ----- Ordering + events ----------------------------------------

    [Fact]
    public void Setups_OrderedAlphabetically()
    {
        LairManager m = new();
        m.LoadAll(_bbs);
        m.Save(NewSetup("zeta",   (1, 1)));
        m.Save(NewSetup("alpha",  (1, 2)));
        m.Save(NewSetup("MIDDLE", (1, 3)));

        Assert.Equal(new[] { "alpha", "MIDDLE", "zeta" },
                     m.Setups.Select(s => s.Name));
    }

    [Fact]
    public void SetupsChanged_FiresOnLoadSaveDelete()
    {
        LairManager m = new();
        int fires = 0;
        m.SetupsChanged += () => fires++;

        m.LoadAll(_bbs);
        Assert.Equal(1, fires);

        m.Save(NewSetup("a", (1, 1)));
        Assert.Equal(2, fires);

        m.Delete("a");
        Assert.Equal(3, fires);
    }

    [Fact]
    public void LoadAll_MigratesLegacyLairsFolder_IntoSharedLoopsFolder()
    {
        // Pre-existing legacy file under Data/BBS/{bbs}/Lairs/<name>.json
        // — the format we shipped before the storage unification.
        string legacy = AppPaths.LegacyBbsLairsFolder(_bbs);
        Directory.CreateDirectory(legacy);
        const string legacyJson = """
            {
              "SchemaVersion": 1,
              "Name": "Old Setup",
              "Notes": "from before the move",
              "Markers": [
                { "Map": 1, "Room": 1, "OverrideRespawnSeconds": null, "Skip": false }
              ]
            }
            """;
        File.WriteAllText(Path.Combine(legacy, "Old Setup.json"), legacyJson);

        LairManager m = new();
        m.LoadAll(_bbs);

        // File migrated into the shared folder under the new suffix.
        Assert.True(File.Exists(Path.Combine(
            AppPaths.BbsLoopsFolder(_bbs), "Old Setup" + LairManager.LairFileSuffix)));
        // Legacy folder cleaned up.
        Assert.False(Directory.Exists(legacy));
        // Setup loaded and accessible.
        LairSetup? round = m.Get("Old Setup");
        Assert.NotNull(round);
        Assert.Equal("from before the move", round!.Notes);
        Assert.Single(round.Markers);
    }

    [Fact]
    public void Save_NameWithIllegalFilenameChars_StillRoundTrips()
    {
        LairManager m1 = new();
        m1.LoadAll(_bbs);
        m1.Save(NewSetup("setup/with:bad*chars", (1, 1)));

        LairManager m2 = new();
        m2.LoadAll(_bbs);
        Assert.Single(m2.Setups);
        Assert.Equal("setup/with:bad*chars", m2.Setups[0].Name);
    }
}
