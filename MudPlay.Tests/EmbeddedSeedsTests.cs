using System;
using System.IO;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// The seed data ships EMBEDDED in the assembly (Defaults/*.seed.json), so a
/// self-contained single-file build carries it inside the exe and replacing just
/// the binary refreshes the seeds. AppPaths.ExtractEmbeddedSeeds is the launch-time
/// step that materializes them to disk; these pin that it produces the full set and
/// the shipped content (a regression guard against the csproj reverting to loose
/// Content, which would silently drop the embedded resources).
/// </summary>
public sealed class EmbeddedSeedsTests : IDisposable
{
    private readonly string _dir;

    public EmbeddedSeedsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mudplay-seeds-" + Path.GetRandomFileName());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp cleanup */ }
    }

    [Fact]
    public void ExtractEmbeddedSeeds_WritesEveryShippedSeedFile()
    {
        AppPaths.ExtractEmbeddedSeeds(_dir);

        foreach (string name in new[]
        {
            "MonsterOverlay.stock.seed.json", "MonsterOverlay.paradigm.seed.json",
            "ItemOverlay.stock.seed.json",    "ItemOverlay.paradigm.seed.json",
            "Messages.stock.seed.json",       "Messages.paradigm.seed.json",
            "MonsterMessages.seed.json",      "BossDefs.seed.json",
            "QuestDefs.seed.json",            "Triggers.seed.json",
        })
        {
            string path = Path.Combine(_dir, name);
            Assert.True(File.Exists(path), $"embedded seed not materialized: {name}");
            Assert.True(new FileInfo(path).Length > 0, $"materialized seed is empty: {name}");
        }
    }

    [Fact]
    public void ExtractEmbeddedSeeds_CarriesShippedContent_GuardianIsFriend()
    {
        // Proves the embedded pipeline delivers the actual shipped values — the
        // ganghouse guardian default that a stale copy-if-missing bootstrap froze
        // out of existing installs (elite amber guardian ships as Friend).
        AppPaths.ExtractEmbeddedSeeds(_dir);

        string json = File.ReadAllText(Path.Combine(_dir, "MonsterOverlay.paradigm.seed.json"));
        int at = json.IndexOf("elite amber guardian", StringComparison.Ordinal);
        Assert.True(at >= 0, "elite amber guardian missing from the embedded paradigm overlay seed");
        // Its Relationship follows the Name within the same small record object.
        string window = json.Substring(at, Math.Min(160, json.Length - at));
        Assert.Contains("\"Friend\"", window);
    }

    [Fact]
    public void ExtractEmbeddedNavSeed_UnzipsEachRealmTree()
    {
        // nav-seed ships as an embedded zip per realm; the extract must reconstruct
        // the Loops tree + Favorites.json under nav-seed/{realm}/ (portable unzip).
        AppPaths.ExtractEmbeddedNavSeed(_dir);

        foreach (string realm in new[] { "stock", "paradigm" })
        {
            string realmDir = Path.Combine(_dir, "nav-seed", realm);
            Assert.True(File.Exists(Path.Combine(realmDir, "Favorites.json")),
                $"{realm} Favorites.json not unzipped");
            string loops = Path.Combine(realmDir, "Loops");
            Assert.True(Directory.Exists(loops), $"{realm} Loops/ not unzipped");
            Assert.NotEmpty(Directory.EnumerateFiles(loops, "*.loop", SearchOption.AllDirectories));
        }
    }
}
