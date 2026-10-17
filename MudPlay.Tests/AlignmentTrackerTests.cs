using System;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Alignment staleness. AlignmentTracker flags the local
// character's alignment stale on "A dark cloud passes over you" and clears it
// once our own row is re-observed by a who (matched on given name,
// case-insensitively); another player's row leaves the flag set.
public sealed class AlignmentTrackerTests
{
    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    private static (MessageRouter Router, PlayerDatabase Db, AlignmentTracker Tracker) Build(string name)
    {
        var router = new MessageRouter();
        DefaultPatterns.Seed(router);
        var stats = new PlayerStats { Name = name };
        var db = new PlayerDatabase(new ProfileService(), () => null);   // parameterless ctor = in-memory, no disk
        var tracker = new AlignmentTracker(router, stats, db);
        return (router, db, tracker);
    }

    [Fact]
    public void DarkCloud_SetsStale_AndRaisesChanged()
    {
        (MessageRouter router, _, AlignmentTracker tracker) = Build("MudPlay WuzHere");
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
        (MessageRouter router, PlayerDatabase db, AlignmentTracker tracker) = Build("MudPlay WuzHere");
        router.Dispatch(Line("A dark cloud passes over you."));
        Assert.True(tracker.IsStale);

        // Our own row re-observed — given-name "MudPlay" matches case-insensitively.
        db.RecordObservation("mudplay Someone", null, null, "Outlaw", "Apprentice", null, null, DateTime.UtcNow);

        Assert.False(tracker.IsStale);
    }

    [Fact]
    public void Who_ObservingOtherPlayer_LeavesStale()
    {
        (MessageRouter router, PlayerDatabase db, AlignmentTracker tracker) = Build("MudPlay WuzHere");
        router.Dispatch(Line("A dark cloud passes over you."));
        Assert.True(tracker.IsStale);

        db.RecordObservation("Raijin StormBringer", null, null, "Saint", "Warrior", null, null, DateTime.UtcNow);

        Assert.True(tracker.IsStale);
    }

    // Our own alignment is our row in the (per-realm) players list: the saved row
    // counts, and a `who` that shows us rewrites it.
    [Fact]
    public void SelfAlignment_ReadsOurRow_AndAWhoUpdatesIt()
    {
        var router = new MessageRouter();
        DefaultPatterns.Seed(router);
        var db = new PlayerDatabase(new ProfileService(), () => null);
        var tracker = new AlignmentTracker(router, new PlayerStats { Name = "Fujin" }, db);
        Assert.Null(tracker.SelfAlignment);

        db.RecordObservation("Fujin", "Paladin", "Kang", "Villain", "Chosen", null, null, DateTime.UtcNow.AddHours(-9));
        Assert.Equal("Villain", tracker.SelfAlignment);

        db.RecordObservation("Fujin", "Paladin", "Kang", "Saint", "Chosen", null, null, DateTime.UtcNow);
        Assert.Equal("Saint", tracker.SelfAlignment);
    }

    // A dark cloud while Good leaves us Neutral at best, so Good-only gear is out at
    // once and a `who` is worth sending; from Neutral or worse it changes nothing.
    [Fact]
    public void DarkCloud_WhileGood_ReadsNeutral_UntilTheNextWho()
    {
        (MessageRouter router, PlayerDatabase db, AlignmentTracker tracker) = Build("Fujin");
        db.RecordObservation("Fujin", "Paladin", "Kang", "Saint", null, null, null, DateTime.UtcNow);
        int left = 0;
        tracker.LeftGood += () => left++;

        router.Dispatch(Line("A dark cloud passes over you."));
        Assert.Equal("Neutral", tracker.SelfAlignment);
        Assert.Equal(1, left);

        router.Dispatch(Line("A dark cloud passes over you."));
        Assert.Equal(1, left);                            // already out of Good

        db.RecordObservation("Fujin", "Paladin", "Kang", "Seedy", null, null, null, DateTime.UtcNow);
        Assert.Equal("Seedy", tracker.SelfAlignment);     // the `who` says where we landed
    }

    [Fact]
    public void DarkCloud_WhenNotGood_ChangesNothing()
    {
        (MessageRouter router, PlayerDatabase db, AlignmentTracker tracker) = Build("Fujin");
        db.RecordObservation("Fujin", "Priest", "Halfling", "Villain", null, null, null, DateTime.UtcNow);
        int left = 0;
        tracker.LeftGood += () => left++;

        router.Dispatch(Line("A dark cloud passes over you."));

        Assert.Equal("Villain", tracker.SelfAlignment);
        Assert.Equal(0, left);
    }

    // Paradigm's `pro`: "EPs:" is our exact alignment (its title from the band
    // thresholds) and wins until a newer `who`; "Min. EPs:" is the mineps floor.
    [Fact]
    public void ParadigmPro_SetsOurAlignmentFromTheEvilPoints()
    {
        (MessageRouter router, PlayerDatabase db, AlignmentTracker tracker) = Build("Fujin");
        db.RecordObservation("Fujin", "Paladin", "Kang", "Seedy", null, null, null, DateTime.UtcNow.AddHours(-1));

        router.Dispatch(Line("EPs:                  -15.066666"));
        router.Dispatch(Line("Min. EPs:             -199"));

        Assert.Equal("Neutral", tracker.SelfAlignment);
        Assert.Equal(-15.066666, tracker.EvilPoints!.Value, 5);
        Assert.Equal(-199, tracker.MinEvilPoints);

        db.RecordObservation("Fujin", "Paladin", "Kang", "Good", null, null, null, DateTime.UtcNow);
        Assert.Equal("Good", tracker.SelfAlignment);      // a newer `who` wins

        router.Dispatch(Line("Minimum EPs set to 300"));   // `set mineps 300`
        Assert.Equal(300, tracker.MinEvilPoints);
    }

    // Stock shows only the title, so a refused evil-only item narrows the band; an
    // evil gain (dark cloud) outdates the refusal.
    [Fact]
    public void EvilOnlyRefusal_NarrowsTheRange_UntilADarkCloud()
    {
        (MessageRouter router, PlayerDatabase db, AlignmentTracker tracker) = Build("Fujin");
        db.RecordObservation("Fujin", "Warrior", "Human", "Villain", null, null, null, DateTime.UtcNow);
        int refreshed = 0;
        tracker.Refreshed += () => refreshed++;

        Assert.Null(tracker.SelfEvilPoints(RealmType.Stock)!.Value.MeetsEvilOnly(200));
        tracker.NoteEvilOnlyRefused(200, "crimson blood robes");
        Assert.False(tracker.SelfEvilPoints(RealmType.Stock)!.Value.MeetsEvilOnly(200));
        Assert.Equal(1, refreshed);

        router.Dispatch(Line("A dark cloud passes over you."));
        Assert.Null(tracker.SelfEvilPoints(RealmType.Stock)!.Value.MeetsEvilOnly(200));
        Assert.Equal(2, refreshed);
    }

    // Paradigm's exact number, and after a dark cloud only a floor.
    [Fact]
    public void ParadigmPro_GivesTheExactRange_ThenAFloorAfterADarkCloud()
    {
        (MessageRouter router, _, AlignmentTracker tracker) = Build("Fujin");
        router.Dispatch(Line("EPs:                  150"));
        Assert.Equal(EvilPointRange.Exact(150), tracker.SelfEvilPoints(RealmType.ParaMud));

        router.Dispatch(Line("A dark cloud passes over you."));
        Assert.Equal(new EvilPointRange(150, double.PositiveInfinity), tracker.SelfEvilPoints(RealmType.ParaMud));
    }
}
