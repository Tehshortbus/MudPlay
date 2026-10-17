using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Leaderboard;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Clients on one realm each hold a copy of the realm's files. Each used to write the
// whole file from its copy, so the last to save threw away what the others had
// recorded. Each store now re-reads the file when another client wrote it, so a
// change is made to what the last character recorded.
public sealed class SharedRealmFilesTests : IDisposable
{
    private readonly string _realm =
        Path.Combine(AppPaths.BbsDir, "test-shared-" + Guid.NewGuid().ToString("N").Substring(0, 12), "realm");
    private readonly string _legacySet = "test-sharedset-" + Guid.NewGuid().ToString("N").Substring(0, 12);

    public SharedRealmFilesTests() => Directory.CreateDirectory(_realm);

    public void Dispose()
    {
        foreach (string folder in new[] { Path.GetDirectoryName(_realm)!, AppPaths.GameDataSetDir(_legacySet) })
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    private PlayerDatabase NewPlayers()
    {
        ProfileService profile = TestProfiles.Service();
        PlayerDatabase db = new(profile, () => _realm);
        profile.LoadDefaultProfile();
        return db;
    }

    [Fact]
    public void Players_WhatOneCharacterLearned_SurvivesAnothersSave()
    {
        PlayerDatabase first = NewPlayers();
        PlayerDatabase second = NewPlayers();
        DateTime now = DateTime.UtcNow;

        first.RecordLevel("Bob Smith", 40, now);
        second.RecordGreeted("Alice Jones", now);       // on top of the first's, not over it
        first.RecordLevel("Carol Reed", 12, now);

        PlayerDatabase restarted = NewPlayers();
        Assert.Equal(40, restarted.Find("Bob")?.Level);
        Assert.NotNull(restarted.Find("Alice"));
        Assert.Equal(12, restarted.Find("Carol")?.Level);

        // The second client sees the first's on its next poll.
        Assert.Null(second.Find("Carol"));
        Assert.True(second.TakeInOutsideChanges());
        Assert.Equal(12, second.Find("Carol")?.Level);
        Assert.False(second.TakeInOutsideChanges());
    }

    [Fact]
    public void Players_LegacyDuplicatesInTheFile_CollapseOnLoad_KeepingNewest()
    {
        // A players file written under the old layout has a row per (given, family)
        // pair, so "Debbie Par" and "Debbie Schwartz" are the same player twice. Loading
        // it keeps the newer LastSeen as canonical but preserves the older FirstSeen.
        DateTime older = new(2026, 5, 30, 0, 0, 0, DateTimeKind.Utc);
        DateTime newer = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        JsonStore.Save(AppPaths.RealmPlayersFile(_realm), new List<PlayerObservation>
        {
            new("Debbie", "Schwartz", "Mage", "Elf", "Good", null, null, null,
                FirstSeenUtc: older, LastSeenUtc: older),
            new("Debbie", "Par", null, null, null, null, null, null,
                FirstSeenUtc: newer, LastSeenUtc: newer),
        });

        PlayerDatabase db = NewPlayers();

        PlayerRecord r = Assert.Single(db.Players);
        Assert.Equal("Par", r.FamilyName);     // newer wins for the volatile column
        Assert.Equal(older, r.FirstSeenUtc);   // older preserves "known since"
        Assert.Equal(newer, r.LastSeenUtc);
    }

    private GhItemLocationStore NewSightings()
    {
        GhItemLocationStore store = new(new ItemNameStore(new GameDataCache()));
        store.OnRealmChanged(_realm);
        return store;
    }

    [Fact]
    public void RoombaSightings_FromTwoCharacters_BothStay_AndTheLatestLookAtARoomStands()
    {
        GhItemLocationStore first = NewSightings();
        GhItemLocationStore second = NewSightings();

        first.RecordRoom(new RoomKey(1, 10), new[] { "torch" });
        second.RecordRoom(new RoomKey(1, 20), new[] { "rope" });
        second.RecordRoom(new RoomKey(1, 10), new[] { "lantern" });   // looked at room 10 after the first did

        GhItemLocationStore restarted = NewSightings();
        Assert.Contains(restarted.FindSightings("rope"), s => s.Room == 20);
        Assert.Contains(restarted.FindSightings("lantern"), s => s.Room == 10);
        Assert.Empty(restarted.FindSightings("torch"));
    }

    private static LeaderboardSnapshot Snap(DateTimeOffset at, long topExp) =>
        new(at, 10, new List<LeaderboardEntry> { new(1, "Hero", "Bard", "None", topExp) });

    [Fact]
    public void Leaderboard_CapturesFromTwoCharacters_BothStay()
    {
        LeaderboardSnapshotStore first = new(); first.OnRealmChanged(_realm);
        LeaderboardSnapshotStore second = new(); second.OnRealmChanged(_realm);
        DateTimeOffset t = DateTimeOffset.UtcNow;

        first.Add(Snap(t.AddHours(-2), 1000));
        second.Add(Snap(t.AddHours(-1), 2000));

        LeaderboardSnapshotStore restarted = new(); restarted.OnRealmChanged(_realm);
        Assert.Equal(2, restarted.Snapshots.Count);
        Assert.True(first.TakeInOutsideChanges());
        Assert.Equal(2, first.Snapshots.Count);
    }

    private MessageCandidateStore NewLines(string? legacySet = null)
    {
        MessageCandidateStore store = new();
        store.Load(_realm, legacySet);
        return store;
    }

    [Fact]
    public void UnrecognizedLines_AreMergedOnSave_LatestSightingAndRemovalsKept()
    {
        MessageCandidateStore first = NewLines();
        MessageCandidateStore second = NewLines();
        DateTimeOffset t = DateTimeOffset.UtcNow;

        first.RecordSighting("A strange hum fills the air.", t);
        first.RecordSighting("The floor shakes.", t);
        first.Save();
        second.RecordSighting("A strange hum fills the air.", t.AddMinutes(5));   // seen later by the other
        second.RecordSighting("You smell smoke.", t.AddMinutes(5));
        second.Save();

        // The first client turns one line into a real message, then saves.
        Assert.True(first.TakeInOutsideChanges());
        first.Remove(MessageCandidateRecord.ComputeId("The floor shakes."));
        first.Save();

        MessageCandidateStore restarted = NewLines();
        Assert.Equal(
            new[] { "A strange hum fills the air.", "You smell smoke." },
            restarted.Candidates.Select(c => c.RawText).OrderBy(x => x));
        Assert.Equal(t.AddMinutes(5),
            restarted.Candidates.Single(c => c.RawText.StartsWith("A strange")).LastSeenAt);
    }

    // The lines used to be kept with the game-data set. A realm with no file takes a
    // copy of its set's, once.
    [Fact]
    public void UnrecognizedLines_RealmWithNone_TakesACopyOfItsGameDataSets()
    {
        Directory.CreateDirectory(AppPaths.GameDataSetDir(_legacySet));
        JsonStore.Save(AppPaths.LegacySetMessageCandidatesFile(_legacySet), new List<MessageCandidateRecord>
        {
            new(MessageCandidateRecord.ComputeId("An old line."), "An old line.",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Occurrences: 3, Dismissed: false, Map: null, Room: null),
        });

        MessageCandidateStore store = NewLines(_legacySet);

        Assert.Equal("An old line.", Assert.Single(store.Candidates).RawText);
        Assert.True(File.Exists(AppPaths.RealmMessageCandidatesFile(_realm)));
    }
}
