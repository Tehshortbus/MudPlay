using System.IO;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// GhRoomLabelStore is realm-tier (roomba.json in the realm's folder) — every
// character in a realm shares one gang house. Tests point the store at a scratch
// realm folder through OnRealmChanged so Persist/Load round-trips through real
// disk, mirroring RoomBlacklistStoreTests.
public sealed class GhRoomLabelStoreTests : IDisposable
{
    private readonly string _realm = Path.Combine(Path.GetTempPath(), "gh-test-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_realm)) Directory.Delete(_realm, recursive: true);
        }
        catch { /* best-effort */ }
    }

    private GhRoomLabelStore NewPinnedStore()
    {
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhRoomLabelStore store = new(profile);
        store.OnRealmChanged(_realm);
        return store;
    }

    [Fact]
    public void SearchesPerRoom_DefaultsToThree_WhenUnset()
    {
        GhRoomLabelStore store = NewPinnedStore();
        Assert.Equal(3, store.SearchesPerRoom);
        Assert.Equal(GhRoomLabelStore.DefaultSearchesPerRoom, store.SearchesPerRoom);
    }

    [Fact]
    public void SetSearchesPerRoom_PersistsAndReadsBack()
    {
        GhRoomLabelStore store = NewPinnedStore();
        store.SetSearchesPerRoom(1);
        Assert.Equal(1, store.SearchesPerRoom);
    }

    [Fact]
    public void SetSearchesPerRoom_ClampsBelowOneToOne()
    {
        GhRoomLabelStore store = NewPinnedStore();
        store.SetSearchesPerRoom(0);
        Assert.Equal(1, store.SearchesPerRoom);
    }

    [Fact]
    public void SearchForHidden_DefaultsOff_AndPersistsWhenSet()
    {
        GhRoomLabelStore store = NewPinnedStore();
        Assert.False(store.SearchForHidden);   // hidden-item search is opt-in

        store.SetSearchForHidden(true);
        Assert.True(store.SearchForHidden);

        store.SetSearchForHidden(false);
        Assert.False(store.SearchForHidden);
    }

    [Fact]
    public void SetSearchesPerRoom_FiresChanged()
    {
        GhRoomLabelStore store = NewPinnedStore();
        int fires = 0;
        store.Changed += () => fires++;

        store.SetSearchesPerRoom(4);

        Assert.Equal(1, fires);
    }

    [Fact]
    public void MergeSyncLabels_AdoptsAbsent_KeepsLocal_AndKeepsCatchAllFlags()
    {
        GhRoomLabelStore store = NewPinnedStore();
        // A local label the sync must not clobber, itself a catch-all.
        store.SetLabel(new RoomKey(1, 100),
            new List<GhCategoryRule> { GhCategoryRule.ForItemType(9) }, isCatchAll: true);

        int fires = 0;
        store.Changed += () => fires++;

        int adopted = store.MergeSyncLabels(new List<GhRoomLabel>
        {
            // Same room as the local one — must be skipped (add-if-absent).
            new GhRoomLabel(1, 100) { Rules = new() { GhCategoryRule.ForItemType(1) } },
            // New room, also a catch-all — adopted AS ONE. Catch-alls are an overflow
            // chain now, so a synced one joins the chain rather than being demoted:
            // a single overflow room is the bottleneck in a house that's filling up.
            new GhRoomLabel(1, 200) { IsCatchAll = true, Rules = new() { GhCategoryRule.ForWornSlot(2) } },
            // New room, ordinary.
            new GhRoomLabel(1, 300) { Rules = new() { GhCategoryRule.ForItemType(7) } },
        });

        Assert.Equal(2, adopted);       // 200 + 300; 100 skipped
        Assert.Equal(1, fires);          // one Changed for the whole merge

        Assert.True(store.TryGetLabel(new RoomKey(1, 100), out GhRoomLabel local));
        Assert.Equal(9, local.Rules[0].ItemType);   // local kept, not clobbered
        Assert.True(local.IsCatchAll);               // local keeps its flag

        Assert.True(store.TryGetLabel(new RoomKey(1, 200), out GhRoomLabel adopted200));
        Assert.True(adopted200.IsCatchAll);          // joins the chain, not demoted
        Assert.Equal(2, adopted200.Rules[0].Worn);
    }

    [Fact]
    public void SetSearchesPerRoom_WithoutARealm_IsNoOp()
    {
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhRoomLabelStore store = new(profile);   // no OnRealmChanged

        store.SetSearchesPerRoom(1);

        Assert.Equal(GhRoomLabelStore.DefaultSearchesPerRoom, store.SearchesPerRoom);
    }

    [Fact]
    public void Settings_SurviveAcrossStoreInstances_ForTheSameRealm()
    {
        GhRoomLabelStore first = NewPinnedStore();
        first.SetSearchesPerRoom(7);
        first.SetSearchForHidden(true);

        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhRoomLabelStore second = new(profile);
        second.OnRealmChanged(_realm);

        Assert.Equal(7, second.SearchesPerRoom);
        Assert.True(second.SearchForHidden);
    }

    [Fact]
    public void OnRealmChanged_MigratesLegacyPerCharacterData_Once()
    {
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        profile.Current!.GhRoomLabels = new List<GhRoomLabel> { new(1, 100) };
        profile.Current!.GhSearchesPerRoom = 9;
        profile.Current!.GhSearchForHidden = true;

        GhRoomLabelStore store = new(profile);
        store.OnRealmChanged(_realm);

        Assert.Single(store.Labels);
        Assert.Equal(9, store.SearchesPerRoom);
        Assert.True(store.SearchForHidden);
        // Legacy character-tier fields are cleared once migrated.
        Assert.Null(profile.Current!.GhRoomLabels);
        Assert.Null(profile.Current!.GhSearchesPerRoom);
        Assert.Null(profile.Current!.GhSearchForHidden);
    }

    [Fact]
    public void OnRealmChanged_DoesNotOverwriteExistingRealmData_WithLegacyData()
    {
        // A second character in the same realm already labeled rooms; a stale
        // legacy per-character field on THIS profile must not clobber the
        // realm's real data.
        GhRoomLabelStore seed = NewPinnedStore();
        seed.SetLabel(new RoomKey(1, 1), new List<GhCategoryRule>(), isCatchAll: false);

        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        profile.Current!.GhRoomLabels = new List<GhRoomLabel> { new(9, 999) };

        GhRoomLabelStore store = new(profile);
        store.OnRealmChanged(_realm);

        Assert.Single(store.Labels);
        Assert.True(store.TryGetLabel(new RoomKey(1, 1), out _));
    }
}
