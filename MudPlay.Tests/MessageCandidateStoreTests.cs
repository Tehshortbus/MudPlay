using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class MessageCandidateStoreTests
{
    [Fact]
    public void RecordSighting_NewText_CreatesRecord_ReturnsIsNewTrue()
    {
        MessageCandidateStore store = new();
        DateTimeOffset t = DateTimeOffset.UtcNow;

        (MessageCandidateRecord record, bool isNew) = store.RecordSighting("A new line", t);

        Assert.True(isNew);
        Assert.Equal("A new line", record.RawText);
        Assert.Equal(1, record.Occurrences);
        Assert.Equal(t, record.FirstSeenAt);
        Assert.Equal(t, record.LastSeenAt);
        Assert.False(record.Dismissed);
        Assert.Single(store.Candidates);
    }

    [Fact]
    public void RecordSighting_RepeatText_BumpsOccurrences_ReturnsIsNewFalse()
    {
        MessageCandidateStore store = new();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        store.RecordSighting("Same line", t0);

        DateTimeOffset t1 = t0.AddSeconds(5);
        (MessageCandidateRecord record, bool isNew) = store.RecordSighting("Same line", t1);

        Assert.False(isNew);
        Assert.Equal(2, record.Occurrences);
        Assert.Equal(t0, record.FirstSeenAt);
        Assert.Equal(t1, record.LastSeenAt);
        Assert.Single(store.Candidates);
    }

    [Fact]
    public void Dismiss_FreezesTheRecord_RecurrenceDoesNotBump()
    {
        MessageCandidateStore store = new();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        (MessageCandidateRecord created, _) = store.RecordSighting("Boring line", t0);

        store.Dismiss(created.Id);
        Assert.True(store.Candidates[0].Dismissed);
        Assert.True(store.IsDismissed("Boring line"));

        // A later recurrence of a dismissed line is ignored entirely — no bump,
        // no duplicate, still dismissed (final "decided, stop tracking" verdict).
        (MessageCandidateRecord record, bool isNew) = store.RecordSighting("Boring line", t0.AddMinutes(1));
        Assert.False(isNew);
        Assert.True(record.Dismissed);
        Assert.Equal(1, record.Occurrences);
        Assert.Single(store.Candidates);
    }

    [Fact]
    public void RecordSighting_StoresLocation_AndKeepsFirstSightingOnBump()
    {
        MessageCandidateStore store = new();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;

        (MessageCandidateRecord created, _) = store.RecordSighting("Located line", t0, map: 7, room: 100);
        Assert.Equal(7, created.Map);
        Assert.Equal(100, created.Room);

        // A recurrence elsewhere keeps the first sighting's location — the record
        // shows where the line was FIRST noticed, not wherever it last recurred.
        (MessageCandidateRecord bumped, _) = store.RecordSighting("Located line", t0.AddSeconds(5), map: 9, room: 200);
        Assert.Equal(7, bumped.Map);
        Assert.Equal(100, bumped.Room);
    }

    [Fact]
    public void Remove_DeletesRecord()
    {
        MessageCandidateStore store = new();
        (MessageCandidateRecord created, _) = store.RecordSighting("Gone soon", DateTimeOffset.UtcNow);

        store.Remove(created.Id);

        Assert.Empty(store.Candidates);
    }

    [Fact]
    public void Contains_ReflectsCurrentText()
    {
        MessageCandidateStore store = new();
        Assert.False(store.Contains("Not staged"));

        store.RecordSighting("Now staged", DateTimeOffset.UtcNow);
        Assert.True(store.Contains("Now staged"));
    }
}
