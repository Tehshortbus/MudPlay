using FujinTerm.Game;
using FujinTerm.Game.Health;
using FujinTerm.Models.Settings;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="DeathFloorTracer"/> slow-vs-overkill classification and the
/// death-floor refine writeback. A slow death (gradual bleed to the floor)
/// refines <see cref="BbsProfile.PlayerDiesAtHp"/>; an overkill (killed from
/// positive HP, or a big single drop past the floor) is discarded.
/// </summary>
public sealed class DeathFloorTracerTests
{
    private sealed class Harness : IDisposable
    {
        public PlayerState State { get; } = new();
        public LogService Log { get; } = new();
        public BbsProfile? Bbs { get; set; } = new() { Name = "TestBBS", PlayerDiesAtHp = -25 };
        public int SaveCount { get; private set; }
        public DeathFloorTracer Tracer { get; }

        public Harness()
        {
            Tracer = new DeathFloorTracer(
                State,
                resolveBbs: () => Bbs,
                saveBbs: _ => SaveCount++,
                log: Log);
        }

        // Latest observed max HP the classifier's big-step guard divides by.
        public void SetMaxHp(int maxHp) => State.MaxHp = maxHp;

        // Feed a sequence of HP readings in order (each fires PlayerState.Hp's
        // PropertyChanged exactly as PromptParser would). The default Hp starts
        // at 0, so begin descents from a positive value to establish a prior.
        public void Feed(params int[] hps)
        {
            foreach (int hp in hps) State.Hp = hp;
        }

        public void Dispose() => Tracer.Dispose();
    }

    // ----- slow deaths refine the floor ------------------------------

    [Fact]
    public void SlowDeath_BledToFloor_RefinesToMeasuredHp()
    {
        using Harness h = new();
        h.SetMaxHp(200);
        // Alive, then dropped and bleeding gradually (steps of 4) to -20.
        h.Feed(40, 0, -4, -8, -12, -16, -20);
        h.Tracer.RecordDeath();

        Assert.Equal(-20, h.Bbs!.PlayerDiesAtHp);   // refined from the -25 seed
        Assert.Equal(1, h.SaveCount);
    }

    [Fact]
    public void SlowDeath_DeeperThanSeed_LearnsDeeperFloor()
    {
        using Harness h = new();
        h.SetMaxHp(200);
        h.Feed(30, 0, -8, -16, -24, -32, -40);      // bleed steps of 8, dies at -40
        h.Tracer.RecordDeath();

        Assert.Equal(-40, h.Bbs!.PlayerDiesAtHp);
        Assert.Equal(1, h.SaveCount);
    }

    [Fact]
    public void SlowDeath_MatchingCurrentFloor_ConfirmsWithoutSaving()
    {
        using Harness h = new();
        h.SetMaxHp(200);
        h.Feed(40, 0, -5, -10, -15, -20, -25);      // dies exactly at the -25 seed
        h.Tracer.RecordDeath();

        Assert.Equal(-25, h.Bbs!.PlayerDiesAtHp);
        Assert.Equal(0, h.SaveCount);               // no change → no persist
    }

    // ----- overkills are discarded -----------------------------------

    [Fact]
    public void Overkill_KilledFromPositiveHp_DoesNotRefine()
    {
        using Harness h = new();
        h.SetMaxHp(200);
        // Never entered the bleeding-out band — last reading is positive.
        h.Feed(150, 80);
        h.Tracer.RecordDeath();

        Assert.Equal(-25, h.Bbs!.PlayerDiesAtHp);
        Assert.Equal(0, h.SaveCount);
    }

    [Fact]
    public void Overkill_SinglePlungeIntoBand_NoGradualBleed_DoesNotRefine()
    {
        using Harness h = new();
        h.SetMaxHp(200);
        // One blow from +80 straight to -60 — no in-band bleed tick observed.
        h.Feed(150, 80, -60);
        h.Tracer.RecordDeath();

        Assert.Equal(-25, h.Bbs!.PlayerDiesAtHp);
        Assert.Equal(0, h.SaveCount);
    }

    [Fact]
    public void Overkill_BigHitWhileDropped_BlowsPastFloor_DoesNotRefine()
    {
        using Harness h = new();
        h.SetMaxHp(200);
        // Bleeding gently (steps of 3), then a 64-HP blow blows past the floor.
        // 64 > 10% of 200 max HP → flagged a combat hit, discarded.
        h.Feed(40, 0, -3, -6, -70);
        h.Tracer.RecordDeath();

        Assert.Equal(-25, h.Bbs!.PlayerDiesAtHp);
        Assert.Equal(0, h.SaveCount);
    }

    // ----- guards & lifecycle ----------------------------------------

    [Fact]
    public void MaxHpUnknown_CannotBoundBleedSteps_DoesNotRefine()
    {
        using Harness h = new();
        // MaxHp left at 0 (never observed) — the big-step guard can't run.
        h.Feed(40, 0, -5, -10, -15, -20);
        h.Tracer.RecordDeath();

        Assert.Equal(-25, h.Bbs!.PlayerDiesAtHp);
        Assert.Equal(0, h.SaveCount);
    }

    [Fact]
    public void AutoRefineOff_PinsManualValue()
    {
        using Harness h = new();
        h.Bbs!.AutoRefineDeathFloor = false;
        h.SetMaxHp(200);
        h.Feed(40, 0, -4, -8, -12, -16, -20);       // a clean slow death
        h.Tracer.RecordDeath();

        Assert.Equal(-25, h.Bbs.PlayerDiesAtHp);
        Assert.Equal(0, h.SaveCount);
    }

    [Fact]
    public void NoActiveBbs_DoesNotThrowOrSave()
    {
        using Harness h = new();
        h.Bbs = null;
        h.SetMaxHp(200);
        h.Feed(40, 0, -4, -8, -12, -16, -20);
        h.Tracer.RecordDeath();                      // must not throw

        Assert.Equal(0, h.SaveCount);
    }

    [Fact]
    public void HealingMidBleed_ResetsDescent_ThenPositiveDeathDiscarded()
    {
        using Harness h = new();
        h.SetMaxHp(200);
        // Bled into the band, then a heal lifts HP positive (descent resets),
        // then killed from positive HP — no measurable bleed remains.
        h.Feed(40, 0, -5, -10, 50, 20);
        h.Tracer.RecordDeath();

        Assert.Equal(-25, h.Bbs!.PlayerDiesAtHp);
        Assert.Equal(0, h.SaveCount);
    }

    [Fact]
    public void SecondDeath_WithoutNewReadings_DoesNotRefineAgain()
    {
        using Harness h = new();
        h.SetMaxHp(200);
        h.Feed(40, 0, -4, -8, -12, -16, -20);
        h.Tracer.RecordDeath();                      // refines once
        Assert.Equal(1, h.SaveCount);

        // Descent was reset by the first death; a second RecordDeath with no
        // fresh trajectory has nothing to measure.
        h.Tracer.RecordDeath();
        Assert.Equal(1, h.SaveCount);
        Assert.Equal(-20, h.Bbs!.PlayerDiesAtHp);
    }
}
