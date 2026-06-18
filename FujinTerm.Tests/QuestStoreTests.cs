using System.Collections.Generic;
using System.IO;
using System.Linq;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 10.10 — QuestStore resolution precedence: per-set overlay wins over the
/// universal seed, which wins over a blank auto-draft. Also exercises the
/// (flag, step) identity, the visibility default, and the load fall-throughs
/// (missing / malformed overlay). Uses a unique scratch set under the shared
/// AppPaths.DataRoot plus a temp seed file so runs don't collide with real
/// user data or the shared Global seed.
/// </summary>
public sealed class QuestStoreTests : IDisposable
{
    private readonly string _scratchSet;
    private readonly string _seedPath;

    public QuestStoreTests()
    {
        _scratchSet = "quest-test-" + Path.GetRandomFileName();
        _seedPath = Path.Combine(Path.GetTempPath(), "questseed-" + Path.GetRandomFileName() + ".json");
    }

    public void Dispose()
    {
        try
        {
            string folder = AppPaths.GameDataSetDir(_scratchSet);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch { /* best-effort */ }

        try { if (File.Exists(_seedPath)) File.Delete(_seedPath); }
        catch { /* best-effort */ }
    }

    // Seed must be written before the store is constructed — the ctor loads it.
    private void WriteSeed(params QuestDefinition[] defs) =>
        JsonStore.Save(_seedPath, defs.ToList());

    private void WriteOverlay(params QuestDefinition[] defs) =>
        JsonStore.Save(AppPaths.QuestsFile(_scratchSet), defs.ToList());

    [Fact]
    public void Resolve_UnknownQuest_ReturnsBlankDraft()
    {
        QuestStore store = new(seedPath: _seedPath);   // no seed file on disk
        store.OnActiveSetChanged(_scratchSet);         // no overlay file either

        QuestDefinition q = store.Resolve(126, 4);

        Assert.Equal(126, q.Flag);
        Assert.Equal(4, q.Step);
        Assert.Equal(string.Empty, q.Name);
        Assert.True(q.Visible);
        Assert.Null(q.Steps);
    }

    [Fact]
    public void Resolve_SeedOnly_ReturnsSeedValue()
    {
        WriteSeed(new QuestDefinition(50, 1, "Newbie Quest", steps: "Go talk to the elder"));
        QuestStore store = new(seedPath: _seedPath);
        store.OnActiveSetChanged(_scratchSet);

        QuestDefinition q = store.Resolve(50, 1);

        Assert.Equal("Newbie Quest", q.Name);
        Assert.Equal("Go talk to the elder", q.Steps);
    }

    [Fact]
    public void Resolve_OverlayWins_OverSeed()
    {
        WriteSeed(new QuestDefinition(50, 1, "Seed Name", steps: "seed steps"));
        WriteOverlay(new QuestDefinition(50, 1, "User Name", steps: "user steps"));
        QuestStore store = new(seedPath: _seedPath);
        store.OnActiveSetChanged(_scratchSet);

        QuestDefinition q = store.Resolve(50, 1);

        Assert.Equal("User Name", q.Name);
        Assert.Equal("user steps", q.Steps);
    }

    [Fact]
    public void OnActiveSetChanged_Null_DropsOverlay_FallsBackToSeed()
    {
        WriteSeed(new QuestDefinition(50, 1, "Seed Name"));
        WriteOverlay(new QuestDefinition(50, 1, "User Name"));
        QuestStore store = new(seedPath: _seedPath);
        store.OnActiveSetChanged(_scratchSet);
        Assert.Equal("User Name", store.Resolve(50, 1).Name);

        store.OnActiveSetChanged(null);

        Assert.Null(store.ActiveSet);
        Assert.Equal("Seed Name", store.Resolve(50, 1).Name);
    }

    [Fact]
    public void OnActiveSetChanged_MissingOverlayFile_FallsToSeed()
    {
        WriteSeed(new QuestDefinition(50, 1, "Seed Name"));
        QuestStore store = new(seedPath: _seedPath);

        store.OnActiveSetChanged(_scratchSet);   // set has no quests.json yet

        Assert.Equal("Seed Name", store.Resolve(50, 1).Name);
    }

    [Fact]
    public void OnActiveSetChanged_MalformedOverlay_LeavesOverlayEmpty()
    {
        WriteSeed(new QuestDefinition(50, 1, "Seed Name"));
        Directory.CreateDirectory(AppPaths.GameDataSetDir(_scratchSet));
        File.WriteAllText(AppPaths.QuestsFile(_scratchSet), "{ not valid json ]");

        QuestStore store = new(seedPath: _seedPath);
        store.OnActiveSetChanged(_scratchSet);   // must not throw

        Assert.Equal("Seed Name", store.Resolve(50, 1).Name);
    }

    [Fact]
    public void Resolve_VisibleDefaultsTrue_WhenSeedOmitsField()
    {
        // Hand-written JSON with no Visible property — the model initializer
        // should leave it shown rather than defaulting bool to false.
        File.WriteAllText(_seedPath, "[{\"Flag\":50,\"Step\":1,\"Name\":\"NoVisible\"}]");
        QuestStore store = new(seedPath: _seedPath);
        store.OnActiveSetChanged(_scratchSet);

        QuestDefinition q = store.Resolve(50, 1);

        Assert.True(q.Visible);
        Assert.Equal("NoVisible", q.Name);
    }

    [Fact]
    public void Resolve_StepIsPartOfIdentity_DistinguishesQuestsUnderSameFlag()
    {
        WriteSeed(
            new QuestDefinition(126, 4, "1st Good Alignment"),
            new QuestDefinition(126, 7, "2nd Good Alignment"));
        QuestStore store = new(seedPath: _seedPath);
        store.OnActiveSetChanged(_scratchSet);

        Assert.Equal("1st Good Alignment", store.Resolve(126, 4).Name);
        Assert.Equal("2nd Good Alignment", store.Resolve(126, 7).Name);
        Assert.Equal(string.Empty, store.Resolve(126, 5).Name);   // unnamed step → draft
    }

    [Fact]
    public void Resolve_OverlayCanHideQuest()
    {
        WriteSeed(new QuestDefinition(50, 1, "Seed Name", visible: true));
        WriteOverlay(new QuestDefinition(50, 1, "User Name", visible: false));
        QuestStore store = new(seedPath: _seedPath);
        store.OnActiveSetChanged(_scratchSet);

        Assert.False(store.Resolve(50, 1).Visible);
    }
}
