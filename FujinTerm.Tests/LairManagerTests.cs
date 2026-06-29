using System.IO;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 7.18 — LairManager CRUD lifecycle, working against an isolated
/// per-test game-data set folder so the suite never touches user data.
/// Mirrors <see cref="LoopManagerTests"/>' isolation pattern.
/// </summary>
public sealed class LairManagerTests : IDisposable
{
    private readonly string _setName;

    public LairManagerTests()
    {
        string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
        _setName = "test-lair-" + suffix;
    }

    public void Dispose()
    {
        // Lairs now persist under the game-data set's Loops/ folder; the
        // legacy-migration test also seeds a BBS-tree Lairs/ folder under
        // the same name. Clean up both so nothing leaks into real Data/.
        try
        {
            string setFolder = Path.Combine(AppPaths.GameDataRoot, _setName);
            if (Directory.Exists(setFolder)) Directory.Delete(setFolder, recursive: true);
        }
        catch { /* best-effort */ }
        try
        {
            string bbsFolder = AppPaths.BbsFolder(_setName);
            if (Directory.Exists(bbsFolder)) Directory.Delete(bbsFolder, recursive: true);
        }
        catch { /* best-effort */ }
    }

    private static LairSetup NewSetup(string name, params (int map, int room)[] markers) =>
        new(name, markers.Select(m => new LairMarker(m.map, m.room)));

    // ----- LoadAll lifecycle ----------------------------------------

    [Fact]
    public void LoadAll_UnknownSet_LeavesEmpty()
    {
        LairManager m = new();
        m.LoadAll(_setName);
        Assert.Empty(m.Setups);
        Assert.Equal(_setName, m.SetName);
    }

    [Fact]
    public void LoadAll_Null_ClearsSetAndSetups()
    {
        LairManager m = new();
        m.LoadAll(_setName);
        m.LoadAll(null);
        Assert.Null(m.SetName);
        Assert.Empty(m.Setups);
    }

    // ----- Save / round-trip ----------------------------------------

    [Fact]
    public void Save_AddsSetupToCatalogueAndPersistsToSetLoopsFolderWithLairSuffix()
    {
        LairManager m = new();
        m.LoadAll(_setName);
        m.Save(NewSetup("Sewer rats", (5, 100), (5, 101)));

        Assert.Single(m.Setups);
        Assert.Equal("Sewer rats", m.Setups[0].Name);
        Assert.Equal(2, m.Setups[0].MarkerCount);

        // Lair setups live in the shared Loops/ folder with the
        // .lair.json suffix as the schema discriminator. Plain .json
        // files in the same folder belong to LoopManager.
        string expectedPath = Path.Combine(
            AppPaths.GameDataSetLoopsFolder(_setName), "Sewer rats" + LairManager.LairFileSuffix);
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void Save_RoundTripsAcrossLoadAll()
    {
        LairManager m1 = new();
        m1.LoadAll(_setName);
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
        m2.LoadAll(_setName);
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
    public void Save_NoSetActive_IsNoOp()
    {
        LairManager m = new();
        m.Save(NewSetup("orphan", (1, 1)));
        Assert.Empty(m.Setups);
    }

    [Fact]
    public void Save_RequiresName()
    {
        LairManager m = new();
        m.LoadAll(_setName);
        Assert.Throws<ArgumentException>(() => m.Save(new LairSetup()));
    }

    // ----- Delete ----------------------------------------------------

    [Fact]
    public void Delete_RemovesFromCatalogueAndDisk()
    {
        LairManager m = new();
        m.LoadAll(_setName);
        m.Save(NewSetup("doomed", (1, 1)));

        bool removed = m.Delete("doomed");

        Assert.True(removed);
        Assert.Empty(m.Setups);
        Assert.False(File.Exists(Path.Combine(
            AppPaths.GameDataSetLoopsFolder(_setName), "doomed" + LairManager.LairFileSuffix)));
    }

    [Fact]
    public void Delete_UnknownName_ReturnsFalse()
    {
        LairManager m = new();
        m.LoadAll(_setName);
        Assert.False(m.Delete("nope"));
    }

    // ----- Ordering + events ----------------------------------------

    [Fact]
    public void Setups_OrderedAlphabetically()
    {
        LairManager m = new();
        m.LoadAll(_setName);
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

        m.LoadAll(_setName);
        Assert.Equal(1, fires);

        m.Save(NewSetup("a", (1, 1)));
        Assert.Equal(2, fires);

        m.Delete("a");
        Assert.Equal(3, fires);
    }

    [Fact]
    public void LoadAll_MigratesIntermediateLairJsonSuffix_ToFinalLairSuffix()
    {
        // The first storage-unification commit shipped with .lair.json
        // as the suffix; the subsequent commit shortened it to .lair.
        // Users in the transition window get their files renamed
        // transparently on the next LoadAll.
        string loops = AppPaths.GameDataSetLoopsFolder(_setName);
        Directory.CreateDirectory(loops);
        const string intermediate = """
            {
              "SchemaVersion": 1,
              "Name": "Transition",
              "Notes": "",
              "Markers": [ { "Map": 1, "Room": 1, "OverrideRespawnSeconds": null, "Skip": false } ]
            }
            """;
        File.WriteAllText(Path.Combine(loops, "Transition" + LairManager.LegacyLairFileSuffix),
            intermediate);

        LairManager m = new();
        m.LoadAll(_setName);

        // Renamed to the final suffix.
        Assert.True(File.Exists(Path.Combine(loops, "Transition" + LairManager.LairFileSuffix)));
        Assert.False(File.Exists(Path.Combine(loops, "Transition" + LairManager.LegacyLairFileSuffix)));
        // And accessible.
        Assert.NotNull(m.Get("Transition"));
    }

    [Fact]
    public void LoadAll_MigratesLegacyLairsFolder_IntoSharedLoopsFolder()
    {
        // Pre-existing legacy file under Data/BBS/{bbs}/Lairs/<name>.json
        // — the format we shipped before the storage unification.
        string legacy = AppPaths.LegacyBbsLairsFolder(_setName);
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
        m.LoadAll(_setName);

        // File migrated into the shared folder under the new suffix.
        Assert.True(File.Exists(Path.Combine(
            AppPaths.GameDataSetLoopsFolder(_setName), "Old Setup" + LairManager.LairFileSuffix)));
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
        m1.LoadAll(_setName);
        m1.Save(NewSetup("setup/with:bad*chars", (1, 1)));

        LairManager m2 = new();
        m2.LoadAll(_setName);
        Assert.Single(m2.Setups);
        Assert.Equal("setup/with:bad*chars", m2.Setups[0].Name);
    }
}
