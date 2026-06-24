using FujinTerm.Game;
using FujinTerm.Game.Combat;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Phase 11 — <see cref="CombatSessionTracker"/> classification + aggregation.
/// Lines route through the real <see cref="DefaultPatterns"/> seed and a real
/// <see cref="RoundDamageTracker"/> so the new <c>UserMisses</c> / <c>UserDodges</c>
/// patterns, the "You"-only offensive filter, the dodge/mob-miss de-dupe, and
/// the <c>RoundComplete</c> per-round path are all exercised end-to-end.
/// </summary>
public sealed class CombatSessionTrackerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public PlayerState State { get; } = new();
        public RoundDamageTracker Rounds { get; }
        public CombatSessionTracker Tracker { get; }
        public int ChangedCount { get; private set; }

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Rounds = new RoundDamageTracker(Router, State);
            Tracker = new CombatSessionTracker(Router, Rounds);
            Tracker.Changed += () => ChangedCount++;
        }

        public void Feed(string line)
        {
            LineExtractor.EmittedLine emitted = new(
                line, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false);
            Router.Dispatch(emitted);
        }

        public CombatSessionStats Stats => Tracker.Snapshot();

        public void Dispose()
        {
            Tracker.Dispose();
            Rounds.Dispose();
        }
    }

    // ----- fresh state ------------------------------------------------

    [Fact]
    public void Fresh_AllZero()
    {
        using Harness h = new();
        CombatSessionStats s = h.Stats;
        Assert.Equal(0, s.TotalSwings);
        Assert.Equal(0, s.IncomingAttacks);
        Assert.Equal(0, s.RoundsWithDamage);
        Assert.Equal(0, s.PhysicalMaxDamage);
        Assert.Equal(0d, s.HitPercent);
        Assert.Equal(0d, s.DodgePercent);
    }

    // ----- offensive classification -----------------------------------

    [Fact]
    public void PlainHit_CountsAsHit_WithDamageExtent()
    {
        using Harness h = new();
        h.Feed("You slash the kobold for 8 damage!");

        CombatSessionStats s = h.Stats;
        Assert.Equal(1, s.Hits);
        Assert.Equal(0, s.Crits);
        Assert.Equal(0, s.Backstabs);
        Assert.Equal(8, s.PhysicalMinDamage);
        Assert.Equal(8, s.PhysicalMaxDamage);
        Assert.Equal(100d, s.HitPercent);
    }

    [Fact]
    public void CriticallyKeyword_CountsAsCrit()
    {
        using Harness h = new();
        h.Feed("You critically slash the kobold for 24 damage!");

        CombatSessionStats s = h.Stats;
        Assert.Equal(0, s.Hits);
        Assert.Equal(1, s.Crits);
        Assert.Equal(100d, s.CritPercent);
        Assert.Equal(24, s.PhysicalMaxDamage);
    }

    [Fact]
    public void SurpriseKeyword_CountsAsBackstab_AndTracksOwnExtent()
    {
        using Harness h = new();
        h.Feed("You surprise punch kobold thief for 12 damage!");

        CombatSessionStats s = h.Stats;
        Assert.Equal(0, s.Hits);
        Assert.Equal(1, s.Backstabs);
        Assert.Equal(12, s.BackstabMinDamage);
        Assert.Equal(12, s.BackstabMaxDamage);
        Assert.Equal(12d, s.BackstabAvgDamage);
        // A backstab is still a landed physical swing.
        Assert.Equal(1, s.LandedSwings);
        Assert.Equal(12, s.PhysicalMinDamage);
    }

    [Fact]
    public void OtherPlayersHit_IsIgnored()
    {
        using Harness h = new();
        h.Feed("Raijin slashes the kobold for 9 damage!");
        Assert.Equal(0, h.Stats.Hits);
        Assert.Equal(0, h.Stats.LandedSwings);
    }

    [Fact]
    public void Miss_CountsTowardSwingDenominator()
    {
        using Harness h = new();
        h.Feed("You slash the kobold for 8 damage!");
        h.Feed("You swing at the kobold, but miss!");

        CombatSessionStats s = h.Stats;
        Assert.Equal(1, s.Hits);
        Assert.Equal(1, s.Misses);
        Assert.Equal(2, s.TotalSwings);
        Assert.Equal(50d, s.HitPercent);
        Assert.Equal(50d, s.MissPercent);
    }

    [Fact]
    public void PhysicalExtent_SpansAllLandedCategories()
    {
        using Harness h = new();
        h.Feed("You slash the kobold for 8 damage!");
        h.Feed("You critically slash the kobold for 24 damage!");
        h.Feed("You surprise punch kobold thief for 12 damage!");

        CombatSessionStats s = h.Stats;
        Assert.Equal(3, s.LandedSwings);
        Assert.Equal(8, s.PhysicalMinDamage);
        Assert.Equal(24, s.PhysicalMaxDamage);
        Assert.Equal(44, s.PhysicalTotalDamage);
    }

    // ----- defensive: incoming attacks --------------------------------

    [Fact]
    public void MobHit_Counts()
    {
        using Harness h = new();
        h.Feed("The giant rat bites you for 3 damage!");
        Assert.Equal(1, h.Stats.MobHits);
    }

    [Fact]
    public void MobMiss_Counts_AsPlainMiss()
    {
        using Harness h = new();
        h.Feed("The giant rat swings at you");
        CombatSessionStats s = h.Stats;
        Assert.Equal(1, s.MobMisses);
        Assert.Equal(0, s.Dodges);
    }

    [Fact]
    public void Dodge_CountsAsDodge_NotPlainMiss()
    {
        using Harness h = new();
        h.Feed("The kobold thief lunges at you, but you dodge!");

        CombatSessionStats s = h.Stats;
        Assert.Equal(1, s.Dodges);
        Assert.Equal(0, s.MobMisses);   // de-duped: the same line satisfies MobMisses
    }

    [Fact]
    public void DodgePercent_OverAllIncomingAttacks()
    {
        using Harness h = new();
        h.Feed("The giant rat bites you for 3 damage!");          // mob hit
        h.Feed("The giant rat swings at you");                    // plain miss
        h.Feed("The kobold thief lunges at you, but you dodge!"); // dodge

        CombatSessionStats s = h.Stats;
        Assert.Equal(3, s.IncomingAttacks);
        Assert.Equal(1, s.Dodges);
        Assert.Equal(100d / 3d, s.DodgePercent, 3);
    }

    // ----- per-round damage (RoundComplete) ---------------------------

    [Fact]
    public void RoundComplete_FeedsPerRoundDamageExtent()
    {
        using Harness h = new();

        h.Feed("You slash the kobold for 5 damage!");
        h.Feed("*Combat Off*");                       // closes round → DamageDealt 5

        h.Feed("You slash the goblin for 7 damage!");
        h.Feed("*Combat Off*");                       // closes round → DamageDealt 7

        CombatSessionStats s = h.Stats;
        Assert.Equal(2, s.RoundsWithDamage);
        Assert.Equal(5, s.RoundMinDamage);
        Assert.Equal(7, s.RoundMaxDamage);
        Assert.Equal(6d, s.RoundAvgDamage);
    }

    [Fact]
    public void PureDefenceRound_DoesNotDragRoundMinToZero()
    {
        using Harness h = new();
        // A round where we dealt no damage (only took a hit) must not register
        // as a 0-damage round.
        h.Feed("The giant rat bites you for 3 damage!");
        h.Feed("*Combat Off*");
        Assert.Equal(0, h.Stats.RoundsWithDamage);

        h.Feed("You slash the kobold for 5 damage!");
        h.Feed("*Combat Off*");
        Assert.Equal(1, h.Stats.RoundsWithDamage);
        Assert.Equal(5, h.Stats.RoundMinDamage);
    }

    // ----- reset ------------------------------------------------------

    [Fact]
    public void Reset_ZeroesEverything()
    {
        using Harness h = new();
        h.Feed("You slash the kobold for 8 damage!");
        h.Feed("You swing at the kobold, but miss!");
        h.Feed("The giant rat bites you for 3 damage!");
        h.Feed("The kobold thief lunges at you, but you dodge!");

        h.Tracker.Reset();

        CombatSessionStats s = h.Stats;
        Assert.Equal(0, s.TotalSwings);
        Assert.Equal(0, s.IncomingAttacks);
        Assert.Equal(0, s.Hits);
        Assert.Equal(0, s.Dodges);
    }

    // ----- change notification ----------------------------------------

    [Fact]
    public void Changed_FiresOnObservation()
    {
        using Harness h = new();
        Assert.Equal(0, h.ChangedCount);
        h.Feed("You slash the kobold for 8 damage!");
        Assert.True(h.ChangedCount >= 1);
    }
}
