using System;
using System.Collections.Generic;
using MudPlay.Game;
using MudPlay.Game.Spells;
using Xunit;

namespace MudPlay.Tests;

public sealed class ManaRegenRerollerTests
{
    // Drives the real AbilBreakdownParser so the reroller sees the same
    // BreakdownParsed events it will in production. Config / affordability are
    // mutable so a test can flip them mid-cycle.
    private sealed class Harness
    {
        public readonly AbilBreakdownParser Parser = new();
        // The game's output, fed the way the client receives it.
        public readonly ServerFeed Feed = new();
        public ManaRegenRerollConfig Config = new(Threshold: 5, Cap: 3);
        public bool CanAfford = true;
        public bool UseTickMonitor;   // false = Paradigm (abil 145); true = Stock (tick)
        public bool InCombat;
        public StockManaRollContext? Stock;
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public int AbilQueries;
        public readonly List<string> Recasts = new();
        public readonly ManaRegenReroller Reroller;

        public Harness()
        {
            Parser.AttachLineExtractor(Feed.Lines);
            Reroller = new ManaRegenReroller(
                Parser,
                () => Config,
                () => AbilQueries++,
                Recasts.Add,
                () => CanAfford,
                () => UseTickMonitor,
                inCombat: () => InCombat,
                stockContext: () => Stock,
                clock: () => Now);
        }

        public void Tick(int delta, PlayerPosition position = PlayerPosition.Standing, bool reachedMax = false) =>
            Reroller.OnManaTickObserved(new RegenSample(Now, delta, TimeSpan.FromSeconds(30), position), reachedMax);

        // Replay one abil-145 block whose spells: slice rolled `roll`, then the
        // prompt that flushes it — the parser fires BreakdownParsed on flush.
        public void FeedRoll(int roll)
        {
            Feed.Line($"spells:   ManaRegen(145)   {roll}");
            Feed.Prompt();
        }
    }

    [Fact]
    public void LandingWhileIdleOpensCycleAndQueriesAbil()
    {
        Harness h = new();

        h.Reroller.OnRollSpellLanded("ntap");

        Assert.True(h.Reroller.CycleActive);
        Assert.Equal(0, h.Reroller.RerollsUsed);
        Assert.Equal(1, h.AbilQueries);
        Assert.Empty(h.Recasts);
    }

    [Fact]
    public void RollAtOrAboveThresholdAcceptsWithoutRecast()
    {
        Harness h = new();               // threshold 5

        h.Reroller.OnRollSpellLanded("ntap");
        h.FeedRoll(7);

        Assert.Empty(h.Recasts);
        Assert.False(h.Reroller.CycleActive);
    }

    // A mid-fight reroll is a between-round cast that turns combat off — breaking a
    // running room spell (report paradigm-20260924-123009). Hold it until the fight
    // ends, then resume on the heartbeat.
    [Fact]
    public void RerollInCombat_IsHeldUntilTheFightEnds()
    {
        Harness h = new() { InCombat = true };

        h.Reroller.OnRollSpellLanded("flux");
        h.FeedRoll(2);                   // 2 < 5, but we're fighting

        Assert.Empty(h.Recasts);
        Assert.True(h.Reroller.WaitingForCombat);
        h.Reroller.OnRecoveryTick();     // still fighting
        Assert.Empty(h.Recasts);

        h.InCombat = false;
        h.Reroller.OnRecoveryTick();
        Assert.Equal(new[] { "flux" }, h.Recasts);
        Assert.False(h.Reroller.WaitingForCombat);
        Assert.Equal(1, h.Reroller.RerollsUsed);
    }

    [Fact]
    public void RollBelowThresholdRecastsOnce()
    {
        Harness h = new();

        h.Reroller.OnRollSpellLanded("ntap");
        h.FeedRoll(2);                   // 2 < 5

        Assert.Equal(new[] { "ntap" }, h.Recasts);
        Assert.Equal(1, h.Reroller.RerollsUsed);
        Assert.True(h.Reroller.CycleActive);   // still waiting for the recast to land
    }

    [Fact]
    public void RerollCounterSurvivesTheContinuationLanding()
    {
        Harness h = new();

        h.Reroller.OnRollSpellLanded("ntap");
        h.FeedRoll(2);                   // reroll #1, recast fired
        h.Reroller.OnRollSpellLanded("ntap");   // the recast landed (continuation)

        Assert.Equal(1, h.Reroller.RerollsUsed);   // not reset by the continuation
        Assert.Equal(2, h.AbilQueries);            // queried again
    }

    [Fact]
    public void RerollsUpToCapThenAcceptsWhateverLanded()
    {
        Harness h = new();               // threshold 5, cap 3

        // Initial landing, then keep feeding bad rolls and replaying each
        // recast's landing until the cycle closes.
        h.Reroller.OnRollSpellLanded("ntap");
        int guard = 0;
        while (h.Reroller.CycleActive && guard++ < 10)
        {
            h.FeedRoll(1);               // always below threshold
            if (h.Reroller.CycleActive)
                h.Reroller.OnRollSpellLanded("ntap");   // that recast landed
        }

        Assert.Equal(3, h.Recasts.Count);          // exactly cap rerolls
        Assert.False(h.Reroller.CycleActive);       // accepted on the cap
    }

    [Fact]
    public void PausesAtManaFloorInsteadOfGivingUp()
    {
        Harness h = new() { CanAfford = false };   // threshold 5, cap 3

        h.Reroller.OnRollSpellLanded("ntap");
        h.FeedRoll(1);                   // below threshold but can't pay to reroll now

        Assert.Empty(h.Recasts);                   // didn't recast — out of mana
        Assert.True(h.Reroller.CycleActive);       // cycle SUSPENDED, not ended
        Assert.True(h.Reroller.WaitingForMana);
        Assert.Equal(0, h.Reroller.RerollsUsed);   // the floored attempt wasn't spent
    }

    [Fact]
    public void ResumesRerollingOnceManaRecovers()
    {
        Harness h = new() { CanAfford = false };

        h.Reroller.OnRollSpellLanded("ntap");
        h.FeedRoll(1);                   // paused at the floor
        Assert.True(h.Reroller.WaitingForMana);

        h.Reroller.OnRecoveryTick();     // still can't afford — stays paused
        Assert.Empty(h.Recasts);
        Assert.True(h.Reroller.WaitingForMana);

        h.CanAfford = true;              // meditation refilled the pool
        h.Reroller.OnRecoveryTick();     // now it resumes the next reroll

        Assert.Equal(new[] { "ntap" }, h.Recasts);
        Assert.Equal(1, h.Reroller.RerollsUsed);
        Assert.False(h.Reroller.WaitingForMana);
        Assert.True(h.Reroller.CycleActive);
    }

    [Fact]
    public void RecoveryTickNoOpsWhenNotWaitingForMana()
    {
        Harness h = new();

        h.Reroller.OnRecoveryTick();                 // idle cycle
        Assert.Empty(h.Recasts);

        h.Reroller.OnRollSpellLanded("ntap");        // awaiting abil, not mana
        h.Reroller.OnRecoveryTick();
        Assert.Empty(h.Recasts);
    }

    [Fact]
    public void NullThresholdDisablesRerollingEntirely()
    {
        Harness h = new() { Config = new ManaRegenRerollConfig(Threshold: null, Cap: 3) };

        h.Reroller.OnRollSpellLanded("ntap");

        Assert.Equal(0, h.AbilQueries);            // no abil read at all
        Assert.False(h.Reroller.CycleActive);
        Assert.Empty(h.Recasts);
    }

    [Fact]
    public void ThresholdClearedMidCycleAcceptsTheStandingRoll()
    {
        Harness h = new();

        h.Reroller.OnRollSpellLanded("ntap");      // opened while threshold=5
        h.Config = h.Config with { Threshold = null };
        h.FeedRoll(1);                             // would reroll, but now disabled

        Assert.Empty(h.Recasts);
        Assert.False(h.Reroller.CycleActive);
    }

    [Fact]
    public void UnrelatedAbilCodeIsIgnoredWhileAwaiting()
    {
        Harness h = new();

        h.Reroller.OnRollSpellLanded("ntap");
        h.Feed.Line("worn:     HPRegen(123)                0040");
        h.Feed.Prompt();   // flushes a code-123 breakdown

        // The reroller ignored the non-145 read and is still awaiting ours.
        Assert.True(h.Reroller.CycleActive);
        Assert.Empty(h.Recasts);

        h.FeedRoll(2);                             // the real 145 read arrives
        Assert.Equal(new[] { "ntap" }, h.Recasts);
    }

    [Fact]
    public void BreakdownWithNoActiveCycleIsIgnored()
    {
        Harness h = new();

        h.FeedRoll(1);                             // no landing preceded it

        Assert.Empty(h.Recasts);
        Assert.False(h.Reroller.CycleActive);
    }

    [Fact]
    public void NegativeRollBelowThresholdStillRerolls()
    {
        // A bad nature-tap / mana-flux roll subtracts from the regen rate.
        Harness h = new() { Config = new ManaRegenRerollConfig(Threshold: 0, Cap: 3) };

        h.Reroller.OnRollSpellLanded("flux");
        h.FeedRoll(-50);                           // -50 < 0

        Assert.Equal(new[] { "flux" }, h.Recasts);
    }

    [Fact]
    public void FreshCycleAfterAcceptZeroesTheRerollCounter()
    {
        Harness h = new();

        h.Reroller.OnRollSpellLanded("ntap");
        h.FeedRoll(1);                             // reroll #1
        h.Reroller.OnRollSpellLanded("ntap");
        h.FeedRoll(9);                             // accept → cycle closes
        Assert.False(h.Reroller.CycleActive);

        h.Reroller.OnRollSpellLanded("ntap");      // brand-new cycle
        Assert.Equal(0, h.Reroller.RerollsUsed);
    }

    [Fact]
    public void ResetAbandonsAnInProgressCycle()
    {
        Harness h = new();

        h.Reroller.OnRollSpellLanded("ntap");
        h.Reroller.Reset();

        Assert.False(h.Reroller.CycleActive);
        h.FeedRoll(1);                             // read arrives after reset — ignored
        Assert.Empty(h.Recasts);
    }

    [Fact]
    public void DisposeUnsubscribesFromTheParser()
    {
        Harness h = new();

        h.Reroller.OnRollSpellLanded("ntap");
        h.Reroller.Dispose();
        h.FeedRoll(1);                             // parser fires, but reroller is detached

        Assert.Empty(h.Recasts);
        Assert.Equal(1, h.AbilQueries);            // only the pre-dispose query
    }

    // ----- IsRollSpell classifier -----------------------------------
    // The single source of truth for "this pick reroll-eligible?": a code-145
    // ability whose stored AbilVal is 0. Shared by the landing classifier and
    // the Spells tab range readout, so pin the exact signature.

    private static SpellFormulaInput FormulaWith(params SpellAbility[] abilities)
        => new() { Abilities = abilities };

    [Fact]
    public void IsRollSpell_TrueForCode145WithZeroValue()
        => Assert.True(ManaRegenReroller.IsRollSpell(
            FormulaWith(new SpellAbility(145, 0))));

    [Fact]
    public void IsRollSpell_FalseForFixedRegenBonus()
        // AbilVal != 0 is a flat +N regen buff, not a roll — no reroll.
        => Assert.False(ManaRegenReroller.IsRollSpell(
            FormulaWith(new SpellAbility(145, 12))));

    [Fact]
    public void IsRollSpell_FalseForManaHotCodes()
        // Chaos surge (heal-mana / HP-regen codes, no 145) recasts on expiry.
        => Assert.False(ManaRegenReroller.IsRollSpell(
            FormulaWith(new SpellAbility(150, 0), new SpellAbility(123, 0))));

    [Fact]
    public void IsRollSpell_TrueWhenRollSlotSitsAmongOthers()
        => Assert.True(ManaRegenReroller.IsRollSpell(
            FormulaWith(new SpellAbility(7, 3), new SpellAbility(145, 0))));

    // ----- Stock (tick-monitor) path -------------------------------------

    // A level-30 Stock mage with 80 INT and MageryLVL 3: base (meditate) tick
    // (50 × 80 × 5) / 1650 = 12. A flux roll r pays (100 + r) × 12 / 100 per natural
    // tick, truncated: tick 18 ⇔ roll 50..58, tick 17 ⇔ roll 42..49.
    private static StockManaRollContext StockMage(bool belowMax = true) => new(
        new Game.Calculators.ManaRegenBreakpointCalculator.Inputs(
            Level: 30, MageryType: 1, Intellect: 80, Willpower: 0, MageryLevel: 3,
            GearRegenPercent: 0, Realm: RealmType.Stock),
        RollMin: -65, RollMax: 173, MeditateTick: 12, ManaBelowMax: belowMax);

    private static Harness StockHarness(int threshold) => new()
    {
        UseTickMonitor = true, Stock = StockMage(), Config = new(Threshold: threshold, Cap: 3),
    };

    [Fact]
    public void StockLandingArmsTickWaitAndNeverQueriesAbil()
    {
        Harness h = StockHarness(50);

        h.Reroller.OnRollSpellLanded("flux");

        Assert.True(h.Reroller.CycleActive);
        Assert.Equal(0, h.AbilQueries);   // no abil 145 on Stock
        Assert.Empty(h.Recasts);
    }

    [Fact]
    public void Stock_ReadsTheRollBackOffTheTick_AndAcceptsAGoodOne()
    {
        Harness h = StockHarness(50);

        h.Reroller.OnRollSpellLanded("flux");
        h.Tick(18);                                  // roll 50..58

        Assert.Empty(h.Recasts);
        Assert.False(h.Reroller.CycleActive);
        Assert.Equal("50..58 (tick 18)", h.Reroller.LastObservedText);
    }

    [Fact]
    public void Stock_RerollsARollDefinitelyBelowTheThreshold()
    {
        Harness h = StockHarness(50);

        h.Reroller.OnRollSpellLanded("flux");
        h.Tick(17);                                  // roll 42..49 < 50

        Assert.Equal(new[] { "flux" }, h.Recasts);
    }

    // Truncation leaves a band; one straddling the threshold counts as good (user,
    // 2026-09-28: only reroll a roll that's definitely below).
    [Fact]
    public void Stock_ABandStraddlingTheThreshold_IsAccepted()
    {
        Harness h = StockHarness(55);

        h.Reroller.OnRollSpellLanded("flux");
        h.Tick(18);                                  // 50..58 straddles 55

        Assert.Empty(h.Recasts);
    }

    // A meditate tick pays the unscaled base; every other one lands with the natural
    // tick, so a bigger meditating jump is base + natural.
    [Fact]
    public void Stock_Meditating_SeparatesTheNaturalTickFromTheBase()
    {
        Harness h = StockHarness(50);

        h.Reroller.OnRollSpellLanded("flux");
        h.Tick(12, PlayerPosition.Meditating);       // the meditate tick alone
        Assert.Empty(h.Recasts);
        Assert.True(h.Reroller.CycleActive);
        h.Tick(12 + 17, PlayerPosition.Meditating);  // base + natural 17

        Assert.Equal(new[] { "flux" }, h.Recasts);
    }

    // A tick mid gear-set swap can't be matched to one set's +ManaRgn% and stats.
    [Fact]
    public void Stock_ATickMidGearSwap_IsSkipped()
    {
        Harness h = StockHarness(50);
        h.Stock = StockMage() with { GearSettled = false };

        h.Reroller.OnRollSpellLanded("flux");
        h.Tick(17);
        Assert.Empty(h.Recasts);
        Assert.True(h.Reroller.CycleActive);

        h.Stock = StockMage();
        h.Tick(17);
        Assert.Equal(new[] { "flux" }, h.Recasts);
    }

    [Fact]
    public void Stock_ATickThatFilledThePool_IsNotRead()
    {
        Harness h = StockHarness(50);

        h.Reroller.OnRollSpellLanded("flux");
        h.Tick(3, reachedMax: true);

        Assert.Empty(h.Recasts);
        Assert.True(h.Reroller.CycleActive);
    }

    // A roll so bad the natural tick pays 0 shows no jump at all.
    [Fact]
    public void Stock_NoNaturalTickWithManaBelowMax_ReadsAsZero()
    {
        // Base 1: (21 × 30 × 3) / 1650. Tick 0 ⇔ roll ≤ -1.
        Harness h = new()
        {
            UseTickMonitor = true, Config = new(Threshold: 10, Cap: 3),
            Stock = new(new Game.Calculators.ManaRegenBreakpointCalculator.Inputs(
                    Level: 1, MageryType: 1, Intellect: 30, Willpower: 0, MageryLevel: 1,
                    GearRegenPercent: 0, Realm: RealmType.Stock),
                RollMin: -65, RollMax: 173, MeditateTick: 1, ManaBelowMax: true),
        };

        h.Reroller.OnRollSpellLanded("flux");
        h.Now += TimeSpan.FromSeconds(30);
        h.Reroller.OnRecoveryTick();
        Assert.Empty(h.Recasts);                     // not yet
        h.Now += TimeSpan.FromSeconds(11);
        h.Reroller.OnRecoveryTick();

        Assert.Equal(new[] { "flux" }, h.Recasts);
    }

    [Fact]
    public void StockTickIgnoredWhenNoCycleAwaiting()
    {
        Harness h = StockHarness(50);

        // A tick with no roll-spell landing in flight is a normal regen tick — ignored.
        h.Tick(2);

        Assert.Empty(h.Recasts);
        Assert.False(h.Reroller.CycleActive);
    }

    [Fact]
    public void StockOnlyFirstTickPerLandingIsJudged()
    {
        Harness h = StockHarness(50);

        h.Reroller.OnRollSpellLanded("flux");
        h.Tick(18);   // accepted → cycle closes
        h.Tick(5);    // a later low tick must not re-open / reroll

        Assert.Empty(h.Recasts);
        Assert.False(h.Reroller.CycleActive);
    }

    [Fact]
    public void RollsForTick_IsTheExactBandOfRollsPayingThatTick()
    {
        StockManaRollContext c = StockMage();
        Assert.Equal((50, 58), Game.Calculators.ManaRegenBreakpointCalculator.RollsForTick(c.Inputs, 18, -65, 173));
        Assert.Equal((-65, -59), Game.Calculators.ManaRegenBreakpointCalculator.RollsForTick(c.Inputs, 4, -65, 173));
        Assert.Null(Game.Calculators.ManaRegenBreakpointCalculator.RollsForTick(c.Inputs, 40, -65, 173));
    }

    // ----- Unlimited ("reroll infinite") --------------------------------

    [Fact]
    public void UnlimitedKeepsRerollingPastAFiniteCap()
    {
        // Same always-bad-roll setup as RerollsUpToCapThenAcceptsWhateverLanded, but
        // Unlimited ignores the cap — it never accepts a below-threshold roll, so the
        // only thing that stops it here is our loop guard.
        Harness h = new() { Config = new ManaRegenRerollConfig(Threshold: 5, Cap: 3, Unlimited: true) };

        h.Reroller.OnRollSpellLanded("flux");
        int guard = 0;
        while (h.Reroller.CycleActive && guard++ < 10)
        {
            h.FeedRoll(1);
            if (h.Reroller.CycleActive) h.Reroller.OnRollSpellLanded("flux");
        }

        Assert.Equal(10, h.Recasts.Count);          // blew past cap 3 — still rerolling
        Assert.True(h.Reroller.CycleActive);
    }

    // ----- ReconsiderActiveRoll (reroll the buff that's already up) ------

    // Drive a cap-0 cycle so a bad roll is remembered in LastObservedValue with the
    // cycle closed — the state a config bump acts on.
    private static Harness WithAcceptedBadRoll(int roll)
    {
        Harness h = new() { Config = new ManaRegenRerollConfig(Threshold: 0, Cap: 0) };
        h.Reroller.OnRollSpellLanded("flux");
        h.FeedRoll(roll);                            // cap 0 → accepted immediately
        Assert.False(h.Reroller.CycleActive);
        Assert.Empty(h.Recasts);
        return h;
    }

    [Fact]
    public void ReconsiderActiveRoll_StagesRerollAfterCapRaised()
    {
        Harness h = WithAcceptedBadRoll(-2);         // -2 < threshold 0, remembered
        Assert.Equal(-2, h.Reroller.LastObservedValue);

        h.Config = h.Config with { Cap = 20 };       // user bumps 0 → 20
        h.Reroller.ReconsiderActiveRoll("flux");

        Assert.Equal(new[] { "flux" }, h.Recasts);   // rerolled the live -2
        Assert.True(h.Reroller.CycleActive);
        Assert.Equal(1, h.Reroller.RerollsUsed);
    }

    [Fact]
    public void ReconsiderActiveRoll_InfiniteRerollsEvenWithCapZero()
    {
        Harness h = WithAcceptedBadRoll(-2);
        h.Config = h.Config with { Unlimited = true };   // infinite on, cap still 0
        h.Reroller.ReconsiderActiveRoll("flux");
        Assert.Equal(new[] { "flux" }, h.Recasts);
    }

    [Fact]
    public void ReconsiderActiveRoll_NoOpWhenActiveRollAlreadyClearsThreshold()
    {
        Harness h = WithAcceptedBadRoll(5);          // 5 >= threshold 0 — a good roll
        h.Config = h.Config with { Cap = 20 };
        h.Reroller.ReconsiderActiveRoll("flux");
        Assert.Empty(h.Recasts);                     // nothing to improve
    }

    [Fact]
    public void ReconsiderActiveRoll_NoOpWhenNoRollObservedYet()
    {
        Harness h = new() { Config = new ManaRegenRerollConfig(Threshold: 0, Cap: 20) };
        h.Reroller.ReconsiderActiveRoll("flux");     // never saw a roll
        Assert.Empty(h.Recasts);
        Assert.Null(h.Reroller.LastObservedValue);
    }

    [Fact]
    public void ReconsiderActiveRoll_NoOpWhileACycleIsAlreadyInFlight()
    {
        Harness h = new() { Config = new ManaRegenRerollConfig(Threshold: 5, Cap: 3) };
        h.Reroller.OnRollSpellLanded("flux");        // cycle open, awaiting the abil read
        h.Reroller.ReconsiderActiveRoll("flux");
        Assert.Empty(h.Recasts);                     // don't double-drive an in-flight cycle
    }

    [Fact]
    public void ReconsiderActiveRoll_SuspendsAtManaFloorThenResumes()
    {
        Harness h = WithAcceptedBadRoll(-2);
        h.Config = h.Config with { Cap = 20 };
        h.CanAfford = false;

        h.Reroller.ReconsiderActiveRoll("flux");
        Assert.Empty(h.Recasts);                     // can't pay yet
        Assert.True(h.Reroller.WaitingForMana);

        h.CanAfford = true;
        h.Reroller.OnRecoveryTick();
        Assert.Equal(new[] { "flux" }, h.Recasts);   // fired once mana recovered
    }
}
