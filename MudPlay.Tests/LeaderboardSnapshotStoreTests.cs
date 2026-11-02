using System.Collections.Generic;
using System.IO;
using MudPlay.Game.Leaderboard;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// LeaderboardSnapshotStore: per-BBS capture history, newest-first, bounded by
// MaxSnapshots. Mirrors RoomBlacklistStore's disk round-trip + Changed-event
// contract; these pin a scratch BBS under the shared DataRoot and clean it up.
public sealed class LeaderboardSnapshotStoreTests : IDisposable
{
    private readonly string _scratchBbs = "lb-test-" + Path.GetRandomFileName();
    private static readonly DateTimeOffset T = new(2026, 7, 20, 20, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try
        {
            string folder = AppPaths.BbsFolder(_scratchBbs);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    private static LeaderboardSnapshot Snap(DateTimeOffset at, int requested, long exp)
        => new(at, requested, new[] { new LeaderboardEntry(1, "Salty Exp", "Bard", "None", exp) });

    private static LeaderboardSnapshot WideSnap(DateTimeOffset at, int requested, int count)
    {
        var entries = new List<LeaderboardEntry>(count);
        for (int i = 0; i < count; i++)
            entries.Add(new LeaderboardEntry(i + 1, $"Hero{i}", "Bard", "None", 100_000 - i));
        return new LeaderboardSnapshot(at, requested, entries);
    }

    [Fact]
    public void Add_InsertsNewestFirst_AndFiresChanged()
    {
        var store = new LeaderboardSnapshotStore();
        int fires = 0;
        store.Changed += () => fires++;

        store.Add(Snap(T.AddHours(-1), 100, 1000));
        store.Add(Snap(T, 100, 2000));

        Assert.Equal(2, fires);
        Assert.Equal(2, store.Snapshots.Count);
        Assert.Equal(2000, store.Snapshots[0].Entries[0].Experience); // newest first
    }

    [Fact]
    public void Add_TrimsBeyondMaxSnapshots()
    {
        var store = new LeaderboardSnapshotStore();
        for (int i = 0; i < 60; i++)
            store.Add(Snap(T.AddMinutes(i), 100, 1000 + i));

        // MaxSnapshots caps the history at 5; the tail (oldest) falls off.
        Assert.Equal(5, store.Snapshots.Count);
        Assert.Equal(1059, store.Snapshots[0].Entries[0].Experience); // most recent retained
    }

    [Fact]
    public void Add_PinsWidestBoard_AgainstRingEviction()
    {
        var store = new LeaderboardSnapshotStore();

        // The board of record: a wide "top 100" capture.
        store.Add(WideSnap(T, 100, 10));

        // A long run of small, changing "top" views — enough to push past the ring
        // bound several times over. Each differs (rising exp) so none is discarded.
        for (int i = 0; i < 12; i++)
            store.Add(Snap(T.AddMinutes(i + 1), 0, 1000 + i));

        Assert.Equal(5, store.Snapshots.Count);
        // The wide board survives the churn — a stream of small views can't evict it,
        // so the merged display never shrinks back down to the small view.
        Assert.Contains(store.Snapshots, s => s.Entries.Count == 10);
    }

    // The class column is worth reading off every capture, kept in the history or not.
    [Fact]
    public void Captured_IsRaisedForEveryListing_EvenOneTheHistoryDiscards()
    {
        LeaderboardSnapshotStore store = new();
        List<LeaderboardSnapshot> captured = new();
        store.Captured += captured.Add;

        store.Add(Snap(T, 10, 1_000));
        store.Add(Snap(T.AddMinutes(5), 10, 1_000));     // nothing moved: not kept

        Assert.Single(store.Snapshots);
        Assert.Equal(2, captured.Count);
        Assert.Equal("Bard", captured[1].Entries[0].Class);
    }

    [Fact]
    public void Add_DiscardsRecapture_WithNoExperienceOrRosterChange()
    {
        var store = new LeaderboardSnapshotStore();
        int fires = 0;
        store.Changed += () => fires++;

        store.Add(Snap(T.AddHours(-1), 100, 1000));
        store.Add(Snap(T, 100, 1000)); // identical roster + exp, later timestamp

        // The unchanged re-capture is dropped: history and Changed both stay at one,
        // and the original (older) capture stands so a later real change diffs over
        // its true elapsed time.
        Assert.Single(store.Snapshots);
        Assert.Equal(1, fires);
        Assert.Equal(T.AddHours(-1), store.Snapshots[0].CapturedAtUtc);
    }

    [Fact]
    public void Add_KeepsRecapture_WhenRosterChanges()
    {
        var store = new LeaderboardSnapshotStore();
        LeaderboardSnapshot first = new(T.AddHours(-1), 100,
            new[] { new LeaderboardEntry(1, "Salty Exp", "Bard", "None", 1000) });
        // Same experience total, but a different hero holds the rank — a roster swap.
        LeaderboardSnapshot second = new(T, 100,
            new[] { new LeaderboardEntry(1, "Fresh Blood", "Bard", "None", 1000) });

        store.Add(first);
        store.Add(second);

        Assert.Equal(2, store.Snapshots.Count);
    }

    [Fact]
    public void Add_KeepsRecapture_WhenClassChanges()
    {
        var store = new LeaderboardSnapshotStore();
        LeaderboardSnapshot first = new(T.AddHours(-1), 100,
            new[] { new LeaderboardEntry(1, "Salty Exp", "Bard", "None", 1000) });
        // Same name + exp but a different class — the fingerprint of a reroll
        // reusing the name, so the capture is kept.
        LeaderboardSnapshot second = new(T, 100,
            new[] { new LeaderboardEntry(1, "Salty Exp", "Mage", "None", 1000) });

        store.Add(first);
        store.Add(second);

        Assert.Equal(2, store.Snapshots.Count);
    }

    [Fact]
    public void Clear_EmptiesHistory_AndFires()
    {
        var store = new LeaderboardSnapshotStore();
        store.Add(Snap(T, 100, 1000));
        int fires = 0;
        store.Changed += () => fires++;

        store.Clear();

        Assert.Empty(store.Snapshots);
        Assert.Equal(1, fires);
    }

    [Fact]
    public void Persist_ReloadRoundTrip_PreservesEntriesAndRequestedCount()
    {
        var store = new LeaderboardSnapshotStore();
        store.OnRealmChanged(AppPaths.BbsFolder(_scratchBbs));
        store.Add(Snap(T, 250, 1234567));

        Assert.True(File.Exists(AppPaths.RealmLeaderboardFile(AppPaths.BbsFolder(_scratchBbs))));

        var reloaded = new LeaderboardSnapshotStore();
        reloaded.OnRealmChanged(AppPaths.BbsFolder(_scratchBbs));

        Assert.Single(reloaded.Snapshots);
        LeaderboardSnapshot snap = reloaded.Snapshots[0];
        Assert.Equal(250, snap.RequestedCount);
        Assert.Equal(T, snap.CapturedAtUtc);
        Assert.Single(snap.Entries);
        Assert.Equal(1234567, snap.Entries[0].Experience);
        Assert.Equal("Salty Exp", snap.Entries[0].Name);
    }

    [Fact]
    public void OnRealmChanged_Blank_ClearsInMemory()
    {
        var store = new LeaderboardSnapshotStore();
        store.OnRealmChanged(AppPaths.BbsFolder(_scratchBbs));
        store.Add(Snap(T, 100, 1000));
        Assert.Single(store.Snapshots);

        store.OnRealmChanged(null);
        Assert.Empty(store.Snapshots);
    }

    // A hero is keyed by first name, worked out from Name: a hand-edited history's
    // capture with no rows, or a row with no name, is dropped and the rest loads.
    [Fact]
    public void AHistoryWithMissingRowsOrNames_LoadsTheRest()
    {
        string realm = AppPaths.BbsFolder(_scratchBbs);
        Directory.CreateDirectory(realm);
        File.WriteAllText(AppPaths.RealmLeaderboardFile(realm), """
            [ { "CapturedAtUtc": "2026-07-20T20:00:00+00:00", "RequestedCount": 100,
                "Entries": [ { "Rank": 1, "Name": "Salty Exp", "Class": "Bard", "Guild": "None", "Experience": 1000 },
                             { "Rank": 2, "Class": "Bard", "Experience": 900 } ] },
              { "CapturedAtUtc": "2026-07-20T19:00:00+00:00", "RequestedCount": 100 } ]
            """);
        var store = new LeaderboardSnapshotStore();

        store.OnRealmChanged(realm);
        store.Add(Snap(T.AddHours(1), 100, 2000));

        Assert.Equal(2, store.Snapshots.Count);
        Assert.Equal("Salty", Assert.Single(store.Snapshots[1].Entries).FirstName);
    }
}
