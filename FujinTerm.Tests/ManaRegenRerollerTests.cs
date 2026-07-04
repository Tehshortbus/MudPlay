using System.Collections.Generic;
using FujinTerm.Game;
using FujinTerm.Game.Spells;
using Xunit;

namespace FujinTerm.Tests;

public sealed class ManaRegenRerollerTests
{
    // Drives the real AbilBreakdownParser so the reroller sees the same
    // BreakdownParsed events it will in production. Config / affordability are
    // mutable so a test can flip them mid-cycle.
    private sealed class Harness
    {
        public readonly AbilBreakdownParser Parser = new();
        public ManaRegenRerollConfig Config = new(Threshold: 5, Cap: 3);
        public bool CanAfford = true;
        public int AbilQueries;
        public readonly List<string> Recasts = new();
        public readonly ManaRegenReroller Reroller;

        public Harness()
        {
            Reroller = new ManaRegenReroller(
                Parser,
                () => Config,
                () => AbilQueries++,
                Recasts.Add,
                () => CanAfford);
        }

        // Replay one abil-145 block whose spells: slice rolled `roll`, then the
        // prompt that flushes it — the parser fires BreakdownParsed on flush.
        public void FeedRoll(int roll)
        {
            Parser.FeedTestLine($"spells:   ManaRegen(145)   {roll}");
            Parser.FeedTestLine("", isPromptLine: true);
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
    public void StopsWhenTheNextCastWouldBreachTheManaFloor()
    {
        Harness h = new() { CanAfford = false };

        h.Reroller.OnRollSpellLanded("ntap");
        h.FeedRoll(1);                   // below threshold but can't pay to reroll

        Assert.Empty(h.Recasts);
        Assert.False(h.Reroller.CycleActive);
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
        h.Parser.FeedTestLine("worn:     HPRegen(123)                0040");
        h.Parser.FeedTestLine("", isPromptLine: true);   // flushes a code-123 breakdown

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
}
