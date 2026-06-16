using System;
using FujinTerm.Game;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Alignment staleness. <see cref="AlignmentTracker"/> flags the local
/// character's alignment stale on "A dark cloud passes over you" and clears it
/// once our own row is re-observed by a <c>who</c> (matched on given name,
/// case-insensitively); another player's row leaves the flag set.
/// </summary>
public sealed class AlignmentTrackerTests
{
    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    private static (MessageRouter Router, PlayerDatabase Db, AlignmentTracker Tracker) Build(string name)
    {
        var router = new MessageRouter();
        DefaultPatterns.Seed(router);
        var stats = new PlayerStats { Name = name };
        var db = new PlayerDatabase();   // parameterless ctor = in-memory, no disk
        var tracker = new AlignmentTracker(router, stats, db);
        return (router, db, tracker);
    }

    [Fact]
    public void DarkCloud_SetsStale_AndRaisesChanged()
    {
        (MessageRouter router, _, AlignmentTracker tracker) = Build("Fujin WuzHere");
        int fired = 0;
        tracker.StaleChanged += () => fired++;

        Assert.False(tracker.IsStale);
        router.Dispatch(Line("A dark cloud passes over you."));

        Assert.True(tracker.IsStale);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Who_ObservingSelf_ClearsStale()
    {
        (MessageRouter router, PlayerDatabase db, AlignmentTracker tracker) = Build("Fujin WuzHere");
        router.Dispatch(Line("A dark cloud passes over you."));
        Assert.True(tracker.IsStale);

        // Our own row re-observed — given-name "Fujin" matches case-insensitively.
        db.RecordObservation("fujin Someone", null, null, "Outlaw", "Apprentice", null, null, DateTime.UtcNow);

        Assert.False(tracker.IsStale);
    }

    [Fact]
    public void Who_ObservingOtherPlayer_LeavesStale()
    {
        (MessageRouter router, PlayerDatabase db, AlignmentTracker tracker) = Build("Fujin WuzHere");
        router.Dispatch(Line("A dark cloud passes over you."));
        Assert.True(tracker.IsStale);

        db.RecordObservation("Raijin StormBringer", null, null, "Saint", "Warrior", null, null, DateTime.UtcNow);

        Assert.True(tracker.IsStale);
    }
}
