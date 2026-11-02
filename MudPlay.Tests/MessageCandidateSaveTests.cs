using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The unrecognized-line queue is written on a delay and off the UI thread, and kept
// to MaxPending pending lines. A grind that repeats a staged line used to rewrite the
// whole file on the UI thread with every repeat, thousands of times a night, and one
// of those writes froze the client for 5.3 s (reports Hang-20261021-035337,
// Hang-20261021-065221).
public sealed class MessageCandidateSaveTests : IDisposable
{
    private readonly string _bbs =
        Path.Combine(AppPaths.BbsDir, "test-candsave-" + Guid.NewGuid().ToString("N").Substring(0, 12));
    private readonly ManualTimers _timers = new();

    public MessageCandidateSaveTests() => ScratchRoot.Require();

    private string Realm(string name)
    {
        string folder = Path.Combine(_bbs, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_bbs)) Directory.Delete(_bbs, recursive: true); }
        catch { /* best-effort */ }
    }

    private MessageCandidateStore NewStore(string realm)
    {
        MessageCandidateStore store = new(arm: _timers.Arm);
        store.Load(realm);
        return store;
    }

    private static List<MessageCandidateRecord> OnFile(string realm) =>
        JsonStore.Load<List<MessageCandidateRecord>>(AppPaths.RealmMessageCandidatesFile(realm)) ?? [];

    [Fact]
    public async Task Sightings_AreWrittenTogether_SaveDelayAfterTheFirst()
    {
        string realm = Realm("realm");
        MessageCandidateStore store = NewStore(realm);
        DateTimeOffset t = DateTimeOffset.UtcNow;

        store.RecordSighting("You clench a fist and point a finger at angry fugitive.", t);
        store.RecordSighting("You clench a fist and point a finger at angry fugitive.", t.AddSeconds(5));
        store.RecordSighting("The zephyr dissipates, its debris falling to the ground.", t.AddSeconds(9));

        // Nothing written yet, and one save waits for all three.
        Assert.False(File.Exists(AppPaths.RealmMessageCandidatesFile(realm)));
        Assert.Equal(new[] { MessageCandidateStore.SaveDelay }, _timers.PendingDelays);

        _timers.Advance(MessageCandidateStore.SaveDelay);
        await store.WhenSavedAsync();

        List<MessageCandidateRecord> saved = OnFile(realm);
        Assert.Equal(2, saved.Count);
        Assert.Equal(2, saved.Single(c => c.RawText.StartsWith("You clench")).Occurrences);
        Assert.Equal(0, _timers.Pending);

        // The next change arms a new save.
        store.RecordSighting("The zephyr dissipates, its debris falling to the ground.", t.AddSeconds(40));
        Assert.Equal(1, _timers.Pending);
    }

    // Each deferred save hands its list to the write lane while the lane may still be
    // finishing the one before. A hand-off landing as the lane stopped used to start
    // no lane, and no deferred save was written again for the session.
    [Fact]
    public async Task BackToBackDeferredSaves_EveryOneLands()
    {
        string realm = Realm("realm");
        MessageCandidateStore store = NewStore(realm);
        DateTimeOffset t = DateTimeOffset.UtcNow;

        for (int i = 0; i < 60; i++)
        {
            store.RecordSighting($"A strange rune number {i} glows.", t.AddSeconds(i));
            _timers.Advance(MessageCandidateStore.SaveDelay);
        }
        await store.WhenSavedAsync();

        Assert.Equal(60, OnFile(realm).Count);
    }

    // A deferred write overtaken by Save, and then by another client's write, must
    // not put its older list back over theirs.
    [Fact]
    public async Task OvertakenDeferredWrite_DoesNotPutAnOlderListBack()
    {
        string realm = Realm("realm");
        MessageCandidateStore first = NewStore(realm);
        MessageCandidateStore second = new(arm: new ManualTimers().Arm);
        second.Load(realm);
        DateTimeOffset t = DateTimeOffset.UtcNow;

        first.RecordSighting("The floor shakes.", t);
        _timers.Advance(MessageCandidateStore.SaveDelay);   // handed to the lane
        first.Save();                                        // and written at once
        second.RecordSighting("You smell smoke.", t.AddSeconds(1));
        second.Save();
        await first.WhenSavedAsync();

        Assert.Equal(new[] { "The floor shakes.", "You smell smoke." },
            OnFile(realm).Select(c => c.RawText).OrderBy(x => x));
    }

    // A file grown far past the cap loses its oldest pending lines in one pass at
    // load, and the trimmed list goes out with one deferred save.
    [Fact]
    public async Task Load_OfAFilePastTheCap_TrimsTheOldestPendingLines_AndSavesOnce()
    {
        string realm = Realm("realm");
        DateTimeOffset t = DateTimeOffset.UtcNow;
        List<MessageCandidateRecord> big = new();
        for (int i = 0; i < MessageCandidateStore.MaxPending + 25; i++)
        {
            string text = $"Old line {i}.";
            big.Add(new(MessageCandidateRecord.ComputeId(text), text, t.AddMinutes(i), t.AddMinutes(i),
                Occurrences: 1, Dismissed: false));
        }
        big.Add(new(MessageCandidateRecord.ComputeId("A verdict."), "A verdict.", t.AddDays(-9), t.AddDays(-9),
            Occurrences: 1, Dismissed: true));
        JsonStore.Save(AppPaths.RealmMessageCandidatesFile(realm), big);

        MessageCandidateStore store = NewStore(realm);

        Assert.Equal(MessageCandidateStore.MaxPending, store.Candidates.Count(c => !c.Dismissed));
        Assert.False(store.Contains("Old line 24."));
        Assert.True(store.Contains("Old line 25."));
        Assert.True(store.IsDismissed("A verdict."));
        Assert.Equal(1, _timers.Pending);

        _timers.Advance(MessageCandidateStore.SaveDelay);
        await store.WhenSavedAsync();
        Assert.Equal(MessageCandidateStore.MaxPending + 1, OnFile(realm).Count);
    }

    [Fact]
    public void Save_WritesWhatWaits_AtOnce_AndDropsTheDeferredSave()
    {
        string realm = Realm("realm");
        MessageCandidateStore store = NewStore(realm);

        store.RecordSighting("A strange hum fills the air.", DateTimeOffset.UtcNow);
        store.Save();

        Assert.Equal("A strange hum fills the air.", Assert.Single(OnFile(realm)).RawText);
        Assert.Equal(0, _timers.Pending);
    }

    // A profile or realm switch reloads the store: what the realm being left had
    // waiting goes to that realm's file, not the next realm's.
    [Fact]
    public void Load_OfAnotherRealm_WritesTheFirstRealmsWaitingLines_ToItsOwnFile()
    {
        string first = Realm("first");
        string second = Realm("second");
        MessageCandidateStore store = NewStore(first);

        store.RecordSighting("The floor shakes.", DateTimeOffset.UtcNow);
        store.Load(second);
        _timers.RunAll();

        Assert.Equal("The floor shakes.", Assert.Single(OnFile(first)).RawText);
        Assert.Empty(store.Candidates);
        Assert.False(File.Exists(AppPaths.RealmMessageCandidatesFile(second)));
    }

    // Past MaxPending a new line pushes out the pending line seen longest ago; a
    // dismissed line is the user's verdict and stays.
    [Fact]
    public void NewLine_PastTheCap_PushesOutThePendingLineSeenLongestAgo()
    {
        MessageCandidateStore store = new(arm: _timers.Arm);
        DateTimeOffset t = DateTimeOffset.UtcNow;
        (MessageCandidateRecord dismissed, _) = store.RecordSighting("Dismissed long ago.", t.AddDays(-30));
        store.Dismiss(dismissed.Id);
        store.RecordSighting("Seen longest ago.", t.AddDays(-20));
        for (int i = 1; i < MessageCandidateStore.MaxPending; i++)
            store.RecordSighting($"Line {i}.", t.AddSeconds(i));
        Assert.True(store.Contains("Seen longest ago."));

        store.RecordSighting("One line too many.", t.AddDays(1));

        Assert.False(store.Contains("Seen longest ago."));
        Assert.True(store.IsDismissed("Dismissed long ago."));
        Assert.True(store.Contains("One line too many."));
        Assert.Equal(MessageCandidateStore.MaxPending, store.Candidates.Count(c => !c.Dismissed));
    }

    // Every candidate is keyed by its Id, and a field a file leaves out reads back
    // null. One with its line gets back the Id the line makes; one with no line is
    // dropped, and the queue still merges another client's file.
    [Fact]
    public void AFileEntryWithNoId_GetsItsLinesId_AndOneWithNoLineIsDropped()
    {
        string realm = Realm("Main");
        File.WriteAllText(AppPaths.RealmMessageCandidatesFile(realm), """
            [ { "RawText": "A strange line.", "FirstSeenAt": "2026-11-01T00:00:00+00:00", "LastSeenAt": "2026-11-01T00:00:00+00:00", "Occurrences": 2 },
              { "Id": "abc", "Occurrences": 1 }, null ]
            """);

        MessageCandidateStore store = NewStore(realm);
        File.SetLastWriteTimeUtc(AppPaths.RealmMessageCandidatesFile(realm), DateTime.UtcNow.AddSeconds(5));
        Assert.True(store.TakeInOutsideChanges());

        MessageCandidateRecord kept = Assert.Single(store.Candidates);
        Assert.Equal(MessageCandidateRecord.ComputeId("A strange line."), kept.Id);
        Assert.Equal(2, kept.Occurrences);
    }
}
