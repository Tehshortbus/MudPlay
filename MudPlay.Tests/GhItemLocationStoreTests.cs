using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// GhItemLocationStore is realm-tier (roomba_items.json in the realm's folder), fed by
// GhSweepManager's room-observation ledger and read by RoombaQueryHandler. It
// tracks sightings per (item, room) — a gang house can stock the same item in
// several rooms at once, and @roomba needs every one of them, not just
// whichever room was scanned most recently. Most tests here run with no
// game-data set loaded, so ItemNameStore.FindByName always misses —
// FindSightings falls through to the exact / unique-substring name match. The
// ToSyncRecords/MergeSyncRecords tests need a real resolvable item number, so
// those load a tiny scratch game-data set (see NewPinnedStoreWithItems).
public sealed class GhItemLocationStoreTests : IDisposable
{
    private readonly string _realm = Path.Combine(Path.GetTempPath(), "gh-items-test-" + Path.GetRandomFileName());
    private readonly string _gameDataRoot = Path.Combine(
        Path.GetTempPath(), "mudplay-gh-items-gamedata-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_realm)) Directory.Delete(_realm, recursive: true);
        }
        catch { /* best-effort */ }
        try { Directory.Delete(_gameDataRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    private GhItemLocationStore NewPinnedStore()
    {
        ItemNameStore itemNames = new(new GameDataCache());
        GhItemLocationStore store = new(itemNames);
        store.OnRealmChanged(_realm);
        return store;
    }

    // A pinned store backed by a real item table (torch=1, long sword=2), for
    // tests exercising the item-number resolution ToSyncRecords/MergeSyncRecords
    // depend on. Returns the ItemNameStore too, so a second store instance can
    // share the same resolved names (a merge across two "clients" on the same
    // realm's game data).
    private (GhItemLocationStore store, ItemNameStore itemNames) NewPinnedStoreWithItems()
    {
        Directory.CreateDirectory(Path.Combine(_gameDataRoot, "alpha"));
        File.WriteAllText(Path.Combine(_gameDataRoot, "alpha", "Items.json"), """
            [
              { "Number": 1, "Name": "torch", "ItemType": 0, "Encum": 1 },
              { "Number": 2, "Name": "long sword", "ItemType": 1, "Encum": 5 }
            ]
            """);
        GameDataCache cache = new(_gameDataRoot);
        cache.SwitchSet("alpha");
        ItemNameStore itemNames = new(cache);
        itemNames.OnActiveSetChanged("alpha");

        GhItemLocationStore store = new(itemNames);
        store.OnRealmChanged(_realm);
        return (store, itemNames);
    }

    [Fact]
    public void FindSightings_UnknownItem_ReturnsEmpty()
    {
        GhItemLocationStore store = NewPinnedStore();
        Assert.Empty(store.FindSightings("long sword"));
    }

    [Fact]
    public void RecordRoom_ThenFindSightings_ExactMatch()
    {
        GhItemLocationStore store = NewPinnedStore();
        store.RecordRoom(new RoomKey(1, 100), new[] { "long sword" });

        GhItemSighting sighting = Assert.Single(store.FindSightings("long sword"));
        Assert.Equal(1, sighting.Map);
        Assert.Equal(100, sighting.Room);
    }

    [Fact]
    public void RecordRoom_StripsLeadingCount()
    {
        GhItemLocationStore store = NewPinnedStore();
        store.RecordRoom(new RoomKey(1, 100), new[] { "3 torch" });

        GhItemSighting sighting = Assert.Single(store.FindSightings("torch"));
        Assert.Equal("torch", sighting.ItemName);
        Assert.Equal(3, sighting.Quantity);
    }

    // The core fix: the same item stocked in two different rooms must surface
    // as two separate sightings, not collapse to whichever was scanned last.
    [Fact]
    public void RecordRoom_SameItemInDifferentRooms_TracksBothSeparately()
    {
        GhItemLocationStore store = NewPinnedStore();
        store.RecordRoom(new RoomKey(1, 100), new[] { "4 torch" });
        store.RecordRoom(new RoomKey(1, 200), new[] { "2 torch" });

        var sightings = store.FindSightings("torch");

        Assert.Equal(2, sightings.Count);
        Assert.Contains(sightings, s => s.Room == 100 && s.Quantity == 4);
        Assert.Contains(sightings, s => s.Room == 200 && s.Quantity == 2);
    }

    [Fact]
    public void RecordRoom_ReturnsSightingsOrderedByMapThenRoom()
    {
        GhItemLocationStore store = NewPinnedStore();
        store.RecordRoom(new RoomKey(1, 300), new[] { "torch" });
        store.RecordRoom(new RoomKey(1, 100), new[] { "torch" });
        store.RecordRoom(new RoomKey(1, 200), new[] { "torch" });

        var sightings = store.FindSightings("torch");

        Assert.Equal(new[] { 100, 200, 300 }, sightings.Select(s => s.Room).ToArray());
    }

    // Re-scanning a room whose floor changed must drop that room's stale entry
    // for whatever is no longer there, without touching the SAME item's
    // sighting in a different, unrelated room.
    [Fact]
    public void RecordRoom_ReScanningRoomWithoutItem_DropsOnlyThatRoomsStaleSighting()
    {
        GhItemLocationStore store = NewPinnedStore();
        store.RecordRoom(new RoomKey(1, 100), new[] { "torch" });
        store.RecordRoom(new RoomKey(1, 200), new[] { "torch" });

        // Room 100 re-scanned; the torch is gone (someone picked it up).
        store.RecordRoom(new RoomKey(1, 100), Array.Empty<string>());

        GhItemSighting sighting = Assert.Single(store.FindSightings("torch"));
        Assert.Equal(200, sighting.Room);   // room 100's stale entry is gone, 200's survives
    }

    [Fact]
    public void RecordRoom_ReScanningRoomWithNewFloor_ReplacesOldItemWithNewOne()
    {
        GhItemLocationStore store = NewPinnedStore();
        store.RecordRoom(new RoomKey(1, 100), new[] { "torch" });

        store.RecordRoom(new RoomKey(1, 100), new[] { "long sword" });

        Assert.Empty(store.FindSightings("torch"));
        Assert.Single(store.FindSightings("long sword"));
    }

    [Fact]
    public void FindSightings_UniqueSubstring_Matches()
    {
        GhItemLocationStore store = NewPinnedStore();
        store.RecordRoom(new RoomKey(1, 100), new[] { "long sword" });

        GhItemSighting sighting = Assert.Single(store.FindSightings("sword"));
        Assert.Equal("long sword", sighting.ItemName);
    }

    // A loose query can match a whole family of similarly-named items (e.g.
    // "severed head of goru-nezar" and "severed head of darksong" both contain
    // "severed" and "head"); FindSightings returns every match rather than
    // refusing just because more than one distinct item matched — the caller
    // (RoombaQueryHandler) groups by item name and reports each.
    [Fact]
    public void FindSightings_AmbiguousSubstring_ReturnsAllMatchingItems()
    {
        GhItemLocationStore store = NewPinnedStore();
        store.RecordRoom(new RoomKey(1, 100), new[] { "long sword" });
        store.RecordRoom(new RoomKey(1, 200), new[] { "short sword" });

        var sightings = store.FindSightings("sword");

        Assert.Equal(2, sightings.Count);
        Assert.Contains(sightings, s => s.ItemName == "long sword");
        Assert.Contains(sightings, s => s.ItemName == "short sword");
    }

    [Fact]
    public void FindSightings_NoMatchAtAll_ReturnsEmpty()
    {
        GhItemLocationStore store = NewPinnedStore();
        store.RecordRoom(new RoomKey(1, 100), new[] { "long sword" });

        Assert.Empty(store.FindSightings("dagger"));
    }

    [Fact]
    public void RecordRoom_WithoutARealm_IsNoOp()
    {
        ItemNameStore itemNames = new(new GameDataCache());
        GhItemLocationStore store = new(itemNames);   // no OnRealmChanged

        store.RecordRoom(new RoomKey(1, 100), new[] { "torch" });

        Assert.Empty(store.FindSightings("torch"));
    }

    [Fact]
    public void Sightings_SurviveAcrossStoreInstances_ForTheSameRealm()
    {
        GhItemLocationStore first = NewPinnedStore();
        first.RecordRoom(new RoomKey(1, 100), new[] { "torch" });
        first.RecordRoom(new RoomKey(1, 200), new[] { "torch" });

        ItemNameStore itemNames = new(new GameDataCache());
        GhItemLocationStore second = new(itemNames);
        second.OnRealmChanged(_realm);

        Assert.Equal(2, second.FindSightings("torch").Count);
    }

    [Fact]
    public void OnRealmChanged_ClearingPin_ResetsInMemoryState()
    {
        GhItemLocationStore store = NewPinnedStore();
        store.RecordRoom(new RoomKey(1, 100), new[] { "torch" });

        store.OnRealmChanged(null);

        Assert.Empty(store.FindSightings("torch"));
    }

    [Fact]
    public void ToSyncRecords_ResolvesItemNumberAndQuantity()
    {
        (GhItemLocationStore store, _) = NewPinnedStoreWithItems();
        store.RecordRoom(new RoomKey(1, 100), new[] { "3 torch" });

        var records = store.ToSyncRecords();

        GhItemSyncRecord r = Assert.Single(records);
        Assert.Equal(1, r.Map);
        Assert.Equal(100, r.Room);
        Assert.Equal(1, r.ItemNumber);   // torch's Number in the scratch item table
        Assert.Equal(3, r.Quantity);
    }

    [Fact]
    public void ToSyncRecords_IncludesEveryRoomForTheSameItem()
    {
        (GhItemLocationStore store, _) = NewPinnedStoreWithItems();
        store.RecordRoom(new RoomKey(1, 100), new[] { "torch" });
        store.RecordRoom(new RoomKey(1, 200), new[] { "torch" });

        var records = store.ToSyncRecords();

        Assert.Equal(2, records.Count);
        Assert.Contains(records, r => r.Room == 100);
        Assert.Contains(records, r => r.Room == 200);
    }

    [Fact]
    public void ToSyncRecords_SkipsUnresolvableItemName()
    {
        (GhItemLocationStore store, _) = NewPinnedStoreWithItems();
        store.RecordRoom(new RoomKey(1, 100), new[] { "unknown widget" });

        Assert.Empty(store.ToSyncRecords());
    }

    [Fact]
    public void MergeSyncRecords_AdoptsUnknownItem()
    {
        (GhItemLocationStore store, ItemNameStore itemNames) = NewPinnedStoreWithItems();
        var incoming = new[] { new GhItemSyncRecord(1, 100, 2, 5, DateTimeOffset.Now) };

        int applied = store.MergeSyncRecords(incoming);

        Assert.Equal(1, applied);
        GhItemSighting sighting = Assert.Single(store.FindSightings("long sword"));
        Assert.Equal(100, sighting.Room);
        Assert.Equal(5, sighting.Quantity);
    }

    // A room the merge names that we have no prior entry for is always
    // adopted, regardless of how old the incoming sighting is — there's
    // nothing of ours to lose by learning about a room we've never scanned.
    [Fact]
    public void MergeSyncRecords_NewRoomForKnownItem_IsAlwaysAdopted()
    {
        (GhItemLocationStore store, _) = NewPinnedStoreWithItems();
        store.RecordRoom(new RoomKey(1, 100), new[] { "torch" });   // now, room 100 only
        var oldSightingOfADifferentRoom =
            new[] { new GhItemSyncRecord(1, 200, 1, 1, DateTimeOffset.Now.AddDays(-3)) };

        int applied = store.MergeSyncRecords(oldSightingOfADifferentRoom);

        Assert.Equal(1, applied);
        Assert.Equal(2, store.FindSightings("torch").Count);   // both rooms now tracked
    }

    [Fact]
    public void MergeSyncRecords_NewerIncomingSightingForSameRoom_Overwrites()
    {
        (GhItemLocationStore store, _) = NewPinnedStoreWithItems();
        store.RecordRoom(new RoomKey(1, 100), new[] { "torch" });   // now, quantity 1
        var newer = new[] { new GhItemSyncRecord(1, 100, 1, 9, DateTimeOffset.Now.AddMinutes(5)) };

        int applied = store.MergeSyncRecords(newer);

        Assert.Equal(1, applied);
        GhItemSighting sighting = Assert.Single(store.FindSightings("torch"));
        Assert.Equal(9, sighting.Quantity);
    }

    [Fact]
    public void MergeSyncRecords_OlderIncomingSightingForSameRoom_IsIgnored()
    {
        (GhItemLocationStore store, _) = NewPinnedStoreWithItems();
        store.RecordRoom(new RoomKey(1, 100), new[] { "torch" });   // now, quantity 1
        var older = new[] { new GhItemSyncRecord(1, 100, 1, 9, DateTimeOffset.Now.AddMinutes(-5)) };

        int applied = store.MergeSyncRecords(older);

        Assert.Equal(0, applied);
        GhItemSighting sighting = Assert.Single(store.FindSightings("torch"));
        Assert.Equal(1, sighting.Quantity);   // unchanged
    }

    [Fact]
    public void MergeSyncRecords_UnresolvableItemNumber_IsSkipped()
    {
        (GhItemLocationStore store, _) = NewPinnedStoreWithItems();
        var incoming = new[] { new GhItemSyncRecord(1, 100, 9999, 1, DateTimeOffset.Now) };

        int applied = store.MergeSyncRecords(incoming);

        Assert.Equal(0, applied);
    }

    [Fact]
    public void LatestSeenAt_EmptyStore_IsNull()
    {
        GhItemLocationStore store = NewPinnedStore();
        Assert.Null(store.LatestSeenAt);
    }

    // The Roomba-tab freshness readout is the newest SeenAt across the whole log,
    // regardless of which item or room it belongs to — a later sighting in ANY
    // room advances it.
    [Fact]
    public void LatestSeenAt_ReturnsNewestSightingAcrossAllRoomsAndItems()
    {
        (GhItemLocationStore store, _) = NewPinnedStoreWithItems();
        DateTimeOffset older = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        DateTimeOffset newer = new(2026, 6, 2, 9, 30, 0, TimeSpan.Zero);
        store.MergeSyncRecords(new[]
        {
            new GhItemSyncRecord(1, 100, 1, 4, older),   // torch, room 100
            new GhItemSyncRecord(1, 200, 2, 1, newer),   // long sword, room 200 — the newest
        });

        Assert.Equal(newer, store.LatestSeenAt);
    }
}
