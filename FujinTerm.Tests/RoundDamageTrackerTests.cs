using FujinTerm.Game;
using FujinTerm.Game.Combat;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 9.0c — <see cref="RoundDamageTracker"/> round-boundary detection,
/// damage aggregation, ring-buffer behaviour, and the
/// <see cref="RoundDamageTracker.RoundComplete"/> emit contract.
/// </summary>
public sealed class RoundDamageTrackerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public PlayerState State { get; } = new();
        public LogService Log { get; } = new();
        public RoundDamageTracker Tracker { get; }
        public List<RoundSummary> Completed { get; } = new();

        public Harness(Func<bool>? shouldWriteTrace = null)
        {
            DefaultPatterns.Seed(Router);
            Tracker = new RoundDamageTracker(Router, State, Log, shouldWriteTrace);
            Tracker.RoundComplete += Completed.Add;
        }

        public void Feed(string line)
        {
            LineExtractor.EmittedLine emitted = new(
                line, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false);
            Router.Dispatch(emitted);
        }

        public void Dispose() => Tracker.Dispose();
    }

    // ----- damage accumulation ---------------------------------------

    [Fact]
    public void Fresh_NoCompletedRounds()
    {
        using Harness h = new();
        Assert.Empty(h.Completed);
        Assert.Equal(0, h.Tracker.RoundCount);
        Assert.Empty(h.Tracker.Recent);
    }

    [Fact]
    public void UserHit_StartsRound_NotYetClosed()
    {
        using Harness h = new();
        h.Feed("Fujin hits a giant rat for 5 damage!");
        // Round in flight — no close yet.
        Assert.Empty(h.Completed);
    }

    [Fact]
    public void CombatOff_ClosesRound_EmitsSummary()
    {
        using Harness h = new();
        h.State.Hp = 100;
        h.State.Ma = 50;
        h.Feed("Fujin hits a giant rat for 5 damage!");
        h.Feed("The giant rat bites you for 3 damage!");
        h.State.Hp = 97;        // post-damage snapshot
        h.Feed("*Combat Off*");

        Assert.Single(h.Completed);
        RoundSummary s = h.Completed[0];
        Assert.Equal(1, s.RoundNumber);
        Assert.Equal(5, s.DamageDealt);
        Assert.Equal(3, s.DamageTaken);
        Assert.Equal(2, s.Hits);
        Assert.Equal(0, s.Misses);
        Assert.Equal(100, s.HpBefore);
        Assert.Equal(97,  s.HpAfter);
    }

    [Fact]
    public void MultipleHits_Aggregate()
    {
        using Harness h = new();
        h.Feed("Fujin hits a giant rat for 5 damage!");
        h.Feed("Fujin hits a giant rat for 7 damage!");
        h.Feed("Fujin hits a giant rat for 3 damage!");
        h.Feed("*Combat Off*");

        Assert.Single(h.Completed);
        Assert.Equal(15, h.Completed[0].DamageDealt);
        Assert.Equal(3,  h.Completed[0].Hits);
    }

    [Fact]
    public void MissesCounted_NoDamageContribution()
    {
        using Harness h = new();
        h.Feed("Fujin hits a giant rat for 5 damage!");
        h.Feed("The giant rat swings at you");          // miss
        h.Feed("The giant rat swings at you");          // miss
        h.Feed("*Combat Off*");

        Assert.Single(h.Completed);
        Assert.Equal(5, h.Completed[0].DamageDealt);
        Assert.Equal(0, h.Completed[0].DamageTaken);
        Assert.Equal(1, h.Completed[0].Hits);
        Assert.Equal(2, h.Completed[0].Misses);
    }

    // ----- round-counter monotonicity --------------------------------

    [Fact]
    public void TwoSeparateCombats_TwoRoundsWithIncrementingNumbers()
    {
        using Harness h = new();

        // Fight 1
        h.Feed("Fujin hits a giant rat for 5 damage!");
        h.Feed("*Combat Off*");

        // Fight 2
        h.Feed("Fujin hits a goblin for 8 damage!");
        h.Feed("*Combat Off*");

        Assert.Equal(2, h.Completed.Count);
        Assert.Equal(1, h.Completed[0].RoundNumber);
        Assert.Equal(2, h.Completed[1].RoundNumber);
        Assert.Equal(5, h.Completed[0].DamageDealt);
        Assert.Equal(8, h.Completed[1].DamageDealt);
    }

    // ----- Reset path -------------------------------------------------

    [Fact]
    public void Reset_ClearsRing_ResetsCounter()
    {
        using Harness h = new();
        h.Feed("Fujin hits a giant rat for 5 damage!");
        h.Feed("*Combat Off*");
        Assert.Equal(1, h.Tracker.RoundCount);
        Assert.Single(h.Tracker.Recent);

        h.Tracker.Reset();
        Assert.Equal(0, h.Tracker.RoundCount);
        Assert.Empty(h.Tracker.Recent);

        // Next round starts at #1 again.
        h.Feed("Fujin hits a goblin for 8 damage!");
        h.Feed("*Combat Off*");
        Assert.Equal(2, h.Completed.Count);
        Assert.Equal(1, h.Completed[1].RoundNumber);
    }

    [Fact]
    public void Reset_MidRound_AbandonsAccumulation()
    {
        // Reset while a round is open should drop the partial state.
        using Harness h = new();
        h.Feed("Fujin hits a giant rat for 5 damage!");
        h.Tracker.Reset();
        h.Feed("*Combat Off*");
        Assert.Empty(h.Completed);
    }

    // ----- Recent ring ------------------------------------------------

    [Fact]
    public void Recent_PreservesInsertionOrder()
    {
        using Harness h = new();
        for (int i = 0; i < 3; i++)
        {
            h.Feed($"Fujin hits a goblin for {i + 1} damage!");
            h.Feed("*Combat Off*");
        }

        IReadOnlyList<RoundSummary> recent = h.Tracker.Recent;
        Assert.Equal(3, recent.Count);
        Assert.Equal(1, recent[0].DamageDealt);
        Assert.Equal(2, recent[1].DamageDealt);
        Assert.Equal(3, recent[2].DamageDealt);
    }

    [Fact]
    public void Recent_TrimmedToRingCapacity()
    {
        using Harness h = new();
        for (int i = 0; i < RoundDamageTracker.RingCapacity + 10; i++)
        {
            h.Feed($"Fujin hits a goblin for {i + 1} damage!");
            h.Feed("*Combat Off*");
        }

        Assert.Equal(RoundDamageTracker.RingCapacity, h.Tracker.Recent.Count);
        // Newest entry is the last one fed (50 +10 - 1 = damage 60).
        Assert.Equal(RoundDamageTracker.RingCapacity + 10,
                     h.Tracker.Recent[^1].DamageDealt);
        // Oldest survivor: rounds 1..10 fell off; round 11 (damage 11)
        // is the new head.
        Assert.Equal(11, h.Tracker.Recent[0].DamageDealt);
    }

    // ----- shouldWriteTrace gate -------------------------------------

    [Fact]
    public void TraceWriter_Closed_WhenShouldWriteTraceReturnsFalse()
    {
        // We can't easily assert the file isn't created without poking
        // the filesystem. Instead, exercise the toggle-off path: start
        // off, close a round, then... no exceptions, no file. The flip
        // to ON is covered by the manual smoke test in the PR.
        using Harness h = new(shouldWriteTrace: () => false);
        h.Feed("Fujin hits a goblin for 1 damage!");
        h.Feed("*Combat Off*");
        Assert.Single(h.Completed);
    }
}
