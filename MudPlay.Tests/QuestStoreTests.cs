using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// QuestStore resolution precedence: the game-data set's overlay wins over the
// universal seed, which wins over a blank auto-draft. Also exercises the (flag,
// step) identity, the visibility default, and the load fall-throughs (missing /
// malformed overlay). Uses a unique scratch set (and, for the per-realm file, a
// scratch BBS folder) plus a temp seed file so runs don't collide with each other
// or the shared Global seed.
public sealed class QuestStoreTests : IDisposable
{
    private readonly string _scratchBbs;
    private readonly string _scratchSet = "quest-test-set-" + Path.GetRandomFileName();
    private readonly string _seedPath;

    public QuestStoreTests()
    {
        _scratchBbs = "quest-test-" + Path.GetRandomFileName();
        _seedPath = Path.Combine(Path.GetTempPath(), "questseed-" + Path.GetRandomFileName() + ".json");
    }

    public void Dispose()
    {
        try
        {
            string folder = AppPaths.BbsFolder(_scratchBbs);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch { /* best-effort */ }

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

    // ----- Save -----------------------------------------------------------

    [Fact]
    public void Save_PersistsUserDelta_AndSurvivesReload()
    {
        QuestStore store = new(seedPath: _seedPath);
        store.OnActiveSetChanged(_scratchSet);

        store.Save([new QuestDefinition(50, 1, "User Name", steps: "user steps")]);

        // A fresh store reading the written overlay sees the edit.
        QuestStore reloaded = new(seedPath: _seedPath);
        reloaded.OnActiveSetChanged(_scratchSet);
        QuestDefinition q = reloaded.Resolve(50, 1);
        Assert.Equal("User Name", q.Name);
        Assert.Equal("user steps", q.Steps);
    }

    [Fact]
    public void Save_DropsBlankDraft_KeepingOverlayDelta()
    {
        QuestStore store = new(seedPath: _seedPath);
        store.OnActiveSetChanged(_scratchSet);

        // A pure default (no name, visible, no steps) is redundant — not frozen in.
        store.Save([new QuestDefinition(126, 4)]);

        Assert.False(File.Exists(AppPaths.QuestsFile(_scratchSet)) &&
                     JsonStore.Load<List<QuestDefinition>>(AppPaths.QuestsFile(_scratchSet))!.Count > 0);
        Assert.Equal(string.Empty, store.Resolve(126, 4).Name);
    }

    [Fact]
    public void Save_DropsDefMatchingSeed_SoLaterSeedChangesFlowThrough()
    {
        WriteSeed(new QuestDefinition(50, 1, "Seed Name", steps: "seed steps"));
        QuestStore store = new(seedPath: _seedPath);
        store.OnActiveSetChanged(_scratchSet);

        // Re-save the seed value unchanged: must not freeze it into the overlay.
        store.Save([store.Resolve(50, 1)]);

        // A store pointed at a *changed* seed (same set) now resolves the new seed,
        // proving the overlay didn't shadow it.
        WriteSeed(new QuestDefinition(50, 1, "Updated Seed", steps: "new steps"));
        QuestStore reloaded = new(seedPath: _seedPath);
        reloaded.OnActiveSetChanged(_scratchSet);
        Assert.Equal("Updated Seed", reloaded.Resolve(50, 1).Name);
    }

    [Fact]
    public void Save_HidingSeededQuest_IsWritten()
    {
        WriteSeed(new QuestDefinition(50, 1, "Seed Name", visible: true));
        QuestStore store = new(seedPath: _seedPath);
        store.OnActiveSetChanged(_scratchSet);

        store.Save([new QuestDefinition(50, 1, "Seed Name", visible: false)]);

        QuestStore reloaded = new(seedPath: _seedPath);
        reloaded.OnActiveSetChanged(_scratchSet);
        Assert.False(reloaded.Resolve(50, 1).Visible);
    }

    [Fact]
    public void Save_NormalizesNameAndBlankSteps()
    {
        QuestStore store = new(seedPath: _seedPath);
        store.OnActiveSetChanged(_scratchSet);

        store.Save([new QuestDefinition(50, 1, "  Trimmed  ", steps: "   ")]);

        QuestDefinition q = store.Resolve(50, 1);
        Assert.Equal("Trimmed", q.Name);
        Assert.Null(q.Steps);
    }

    [Fact]
    public void Save_NoActiveSet_IsNoOp()
    {
        QuestStore store = new(seedPath: _seedPath);   // never OnActiveSetChanged

        store.Save([new QuestDefinition(50, 1, "User Name")]);

        // Nowhere to write: the set opened afterwards has nothing of it.
        QuestStore later = new(seedPath: _seedPath);
        later.OnActiveSetChanged(_scratchSet);
        Assert.NotEqual("User Name", later.Resolve(50, 1).Name);
        Assert.False(File.Exists(AppPaths.QuestsFile(_scratchSet)));
    }

    [Fact]
    public void Save_PersistsRewardOverride_AndSurvivesReload()
    {
        QuestStore store = new(seedPath: _seedPath);
        store.OnActiveSetChanged(_scratchSet);

        // A reward the give-chain crawl can't see (5th-tier alignment weapon) — the
        // user corrects it in the editor and it must round-trip through the overlay.
        store.Save([new QuestDefinition(128, 5, rewards: "Darkbone Staff")]);

        QuestStore reloaded = new(seedPath: _seedPath);
        reloaded.OnActiveSetChanged(_scratchSet);
        Assert.Equal("Darkbone Staff", reloaded.Resolve(128, 5).Rewards);
    }

    [Fact]
    public void Save_NormalizesBlankRewards_ToNull()
    {
        QuestStore store = new(seedPath: _seedPath);
        store.OnActiveSetChanged(_scratchSet);

        store.Save([new QuestDefinition(50, 1, "Name", rewards: "   ")]);

        Assert.Null(store.Resolve(50, 1).Rewards);
    }

    [Fact]
    public void Save_PersistsRequiredLevelOverride_AndSurvivesReload()
    {
        QuestStore store = new(seedPath: _seedPath);
        store.OnActiveSetChanged(_scratchSet);

        // A level gate the give-chain crawl misses (the DaoLord sunstone-wristband
        // quest's level-20 requirement) — the user supplies it in the editor and it must
        // round-trip through the overlay.
        store.Save([new QuestDefinition(50, 1, requiredLevel: 20)]);

        QuestStore reloaded = new(seedPath: _seedPath);
        reloaded.OnActiveSetChanged(_scratchSet);
        Assert.Equal(20, reloaded.Resolve(50, 1).RequiredLevel);
    }

    [Fact]
    public void Save_RequiredLevelMatchingSeed_IsDroppedSoLaterSeedChangesFlowThrough()
    {
        // A seed already carrying the gate is the baseline: re-saving the same level is
        // not a user delta, so the overlay doesn't freeze it and a later seed change still
        // flows through (the delta-only contract, mirrored from name/steps/rewards).
        WriteSeed(new QuestDefinition(50, 1, "Seed Name", requiredLevel: 20));
        QuestStore store = new(seedPath: _seedPath);
        store.OnActiveSetChanged(_scratchSet);

        store.Save([store.Resolve(50, 1)]);   // re-save the seed value unchanged

        WriteSeed(new QuestDefinition(50, 1, "Seed Name", requiredLevel: 25));
        QuestStore reloaded = new(seedPath: _seedPath);
        reloaded.OnActiveSetChanged(_scratchSet);
        Assert.Equal(25, reloaded.Resolve(50, 1).RequiredLevel);
    }

    // ----- manual quests + blocking (add / remove a quest) -----------------

    [Fact]
    public void Save_ManualQuest_PersistsVerbatim_AndIsListedByManualQuests()
    {
        QuestStore store = new(seedPath: _seedPath);
        store.OnActiveSetChanged(_scratchSet);

        // A user-added quest the crawl never produces — no seed/crawl baseline, so it must
        // persist verbatim (not be delta-dropped) and be enumerable for the journal/editor.
        int flag = QuestDefinition.ManualFlagBase;
        store.Save([new QuestDefinition(flag, 0, "Hand-Made Quest",
            steps: "[] do the thing", rewards: "a cookie", requiredLevel: 12)]);

        QuestStore reloaded = new(seedPath: _seedPath);
        reloaded.OnActiveSetChanged(_scratchSet);

        QuestDefinition q = Assert.Single(reloaded.ManualQuests());
        Assert.Equal(flag, q.Flag);
        Assert.Equal("Hand-Made Quest", q.Name);
        Assert.Equal("[] do the thing", q.Steps);
        Assert.Equal("a cookie", q.Rewards);
        Assert.Equal(12, q.RequiredLevel);
    }

    [Fact]
    public void Save_WhollyBlankManualQuest_IsDropped()
    {
        QuestStore store = new(seedPath: _seedPath);
        store.OnActiveSetChanged(_scratchSet);

        // An "Add Quest" row the user never filled in carries nothing worth keeping.
        store.Save([new QuestDefinition(QuestDefinition.ManualFlagBase, 0)]);

        Assert.Empty(store.ManualQuests());
    }

    [Fact]
    public void Save_BlockedCrawledQuest_PersistsAndUnblockingDrops()
    {
        QuestStore store = new(seedPath: _seedPath);
        store.OnActiveSetChanged(_scratchSet);

        // Blocking a (default-baseline) crawled quest is a real delta — it must be written.
        store.Save([new QuestDefinition(32, 0, blocked: true)]);

        QuestStore reloaded = new(seedPath: _seedPath);
        reloaded.OnActiveSetChanged(_scratchSet);
        Assert.True(reloaded.Resolve(32, 0).Blocked);

        // Un-blocking returns the quest to its baseline, so the row drops back out.
        reloaded.Save([new QuestDefinition(32, 0, blocked: false)]);
        QuestStore again = new(seedPath: _seedPath);
        again.OnActiveSetChanged(_scratchSet);
        Assert.False(again.Resolve(32, 0).Blocked);
    }

    [Fact]
    public void ManualQuests_ExcludesCrawledFlags()
    {
        // A normal (sub-ManualFlagBase) flag in the overlay is a crawled-quest delta, not a
        // manual quest — ManualQuests must not return it.
        WriteOverlay(new QuestDefinition(50, 1, "Crawled Override"));
        QuestStore store = new(seedPath: _seedPath);
        store.OnActiveSetChanged(_scratchSet);

        Assert.Empty(store.ManualQuests());
    }

    // ----- eligibility overrides (class restriction + show-when-ineligible) -----

    [Fact]
    public void Save_PersistsClassRestrict_AndSurvivesReload()
    {
        QuestStore store = new(seedPath: _seedPath);
        store.OnActiveSetChanged(_scratchSet);

        // The hand-set class lock for a quest the crawl can't detect (Magebane → Witchunter)
        // must round-trip through the overlay, order-insensitively.
        store.Save([new QuestDefinition(50, 1, "Witchunter Quest") { ClassRestrict = [8, 3] }]);

        QuestStore reloaded = new(seedPath: _seedPath);
        reloaded.OnActiveSetChanged(_scratchSet);
        List<int>? restrict = reloaded.Resolve(50, 1).ClassRestrict;
        Assert.NotNull(restrict);
        Assert.Equal([3, 8], restrict!.OrderBy(x => x).ToList());
    }

    [Fact]
    public void Save_ReSavingSeedClassRestrict_IsDroppedAsDelta()
    {
        // A seed already carrying the same lock is the baseline (order-insensitive), so
        // re-saving it isn't a user delta and mustn't freeze into the overlay.
        WriteSeed(new QuestDefinition(50, 1, "Seed Name") { ClassRestrict = [3, 8] });
        QuestStore store = new(seedPath: _seedPath);
        store.OnActiveSetChanged(_scratchSet);

        store.Save([new QuestDefinition(50, 1, "Seed Name") { ClassRestrict = [8, 3] }]);

        WriteSeed(new QuestDefinition(50, 1, "Seed Name") { ClassRestrict = [3, 8, 9] });
        QuestStore reloaded = new(seedPath: _seedPath);
        reloaded.OnActiveSetChanged(_scratchSet);
        Assert.Equal([3, 8, 9], reloaded.Resolve(50, 1).ClassRestrict!.OrderBy(x => x).ToList());
    }

    // The guides were kept per realm for a while. A set takes each realm's old file
    // in once: the first whole (over the set's own, older file), a later one only
    // for the quests the set still lacks.
    [Fact]
    public void Set_TakesInEachRealmsOldGuides_Once()
    {
        WriteOverlay(new QuestDefinition(50, 1, "Stale set name"));
        string realmA = Path.Combine(AppPaths.BbsFolder(_scratchBbs), "Realms", "A");
        string realmB = Path.Combine(AppPaths.BbsFolder(_scratchBbs), "Realms", "B");
        JsonStore.Save(AppPaths.LegacyRealmQuestsFile(realmA), new List<QuestDefinition>
        {
            new(50, 1, "Realm A name"),
        });
        JsonStore.Save(AppPaths.LegacyRealmQuestsFile(realmB), new List<QuestDefinition>
        {
            new(50, 1, "Realm B name"), new(60, 1, "Only in B"),
        });

        QuestStore store = new(seedPath: _seedPath);
        store.OnActiveSetChanged(_scratchSet, realmA);
        Assert.Equal("Realm A name", store.Resolve(50, 1).Name);

        store.OnActiveSetChanged(_scratchSet, realmB);
        Assert.Equal("Realm A name", store.Resolve(50, 1).Name);
        Assert.Equal("Only in B", store.Resolve(60, 1).Name);

        // An edit made since is not undone by loading realm A again.
        store.Save([new QuestDefinition(50, 1, "Edited since"), new QuestDefinition(60, 1, "Only in B")]);
        store.OnActiveSetChanged(_scratchSet, realmA);
        Assert.Equal("Edited since", store.Resolve(50, 1).Name);
    }
}
