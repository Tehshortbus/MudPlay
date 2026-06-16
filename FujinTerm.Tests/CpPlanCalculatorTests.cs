using System.Collections.Generic;
using FujinTerm.Game;
using FujinTerm.Game.Calculators;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 10.7 — <see cref="CpPlanCalculator"/> walks the planned levels from a
/// raw-base baseline, computing each level's CP spend, the running CP balance,
/// and race-max clamping. Pins the cost math, clamping, cumulative carryover,
/// and the overspend trim (edited cell first, else most-raised).
/// </summary>
public sealed class CpPlanCalculatorTests
{
    private static CpPlanEntry Uniform(int level, int value) =>
        new(level, value, value, value, value, value, value);

    // baseline all-40, race min 40 / max 100, no starting CP.
    private static readonly CpPlanEntry Baseline = Uniform(1, 40);
    private static readonly CpPlanEntry RaceMin = Uniform(0, 40);
    private static readonly CpPlanEntry RaceMax = Uniform(0, 100);

    private static IReadOnlyList<CpRowResult> Compute(params CpPlanEntry[] rows) =>
        CpPlanCalculator.Compute(Baseline, rows, RaceMin, RaceMax, initialCp: 0, RealmType.Stock);

    [Fact]
    public void SingleLevel_OnePointRaise_CostsOne()
    {
        // Raise STR 40 → 41 at level 2. First point above the race min costs 1.
        CpRowResult r = Compute(new CpPlanEntry(2, 41, 40, 40, 40, 40, 40))[0];

        Assert.Equal(41, r.Strength);
        Assert.Equal(10, r.CpEarnedTotal);   // 0 unspent + 10 gained at level 2
        Assert.Equal(9, r.CpLeft);           // 10 earned - 1 spent
    }

    [Fact]
    public void TargetBelowPrevious_ClampsUp_ZeroCost()
    {
        // You can't untrain — a target below the baseline clamps back up to it.
        CpRowResult r = Compute(new CpPlanEntry(2, 35, 40, 40, 40, 40, 40))[0];

        Assert.Equal(40, r.Strength);
        Assert.Equal(10, r.CpLeft);          // nothing spent → full earned remains
    }

    [Fact]
    public void TargetAboveRaceMax_ClampsDown_WhenAffordable()
    {
        // Plenty of CP, so the race max (not affordability) is the binding limit.
        CpRowResult r = CpPlanCalculator.Compute(
            Baseline, new[] { new CpPlanEntry(2, 150, 40, 40, 40, 40, 40) },
            RaceMin, RaceMax, initialCp: 100_000, RealmType.Stock)[0];

        Assert.Equal(100, r.Strength);       // clamped to race max
    }

    [Fact]
    public void Overspend_ClampsMostRaisedStat_FloorsLeftAtZero()
    {
        // STR 40 → 60 costs 30 (40-49 @1 = 10, 50-59 @2 = 20) but only 10 CP is
        // available → STR clamps to the highest affordable value (50, cost 10),
        // and CP Left floors at 0 (never negative).
        CpRowResult r = Compute(new CpPlanEntry(2, 60, 40, 40, 40, 40, 40))[0];

        Assert.Equal(50, r.Strength);
        Assert.Equal(10, r.CpEarnedTotal);
        Assert.Equal(0, r.CpLeft);
    }

    [Fact]
    public void Overspend_TrimsTheRaisedStat_LeavesOthersAlone()
    {
        // STR bumped past budget while INT was a small, affordable raise: the
        // most-raised stat (STR) is trimmed, INT stays.
        CpRowResult r = Compute(new CpPlanEntry(2, 90, 41, 40, 40, 40, 40))[0];

        Assert.Equal(41, r.Intellect);       // small INT raise preserved
        Assert.True(r.Strength < 90 && r.Strength > 40);  // STR trimmed to fit
        Assert.Equal(0, r.CpLeft);
    }

    [Fact]
    public void Overspend_TrimsThePreferredCell_NotTheMostRaised()
    {
        // STR raised more than INT, but the user just edited INT — so INT (the
        // preferred cell) is trimmed to fit, leaving the larger STR raise intact.
        // Budget: 10 CP at level 2. STR 40→45 costs 5; INT must fit in the rest.
        IReadOnlyList<CpRowResult> rows = CpPlanCalculator.Compute(
            Baseline, new[] { new CpPlanEntry(2, 45, 49, 40, 40, 40, 40) },
            RaceMin, RaceMax, initialCp: 0, RealmType.Stock, CpStat.Intellect);

        Assert.Equal(45, rows[0].Strength);          // larger raise preserved
        Assert.True(rows[0].Intellect < 49);         // edited cell trimmed to fit
        Assert.Equal(0, rows[0].CpLeft);
    }

    [Fact]
    public void MultipleLevels_EarnedAndLeftAccumulate()
    {
        // L2 spends nothing (banks 10); L3 raises STR 40→41 (cost 1).
        IReadOnlyList<CpRowResult> rows = Compute(
            Uniform(2, 40),                              // no raise
            new CpPlanEntry(3, 41, 40, 40, 40, 40, 40)); // +1 STR

        Assert.Equal(10, rows[0].CpEarnedTotal);
        Assert.Equal(10, rows[0].CpLeft);            // banked

        Assert.Equal(20, rows[1].CpEarnedTotal);     // 10 + 10 gained at level 3
        Assert.Equal(19, rows[1].CpLeft);            // 20 earned - 1 spent
    }

    [Fact]
    public void ClampRowToBudget_TrimsToAvailableCp_AutoTrainPath()
    {
        // The auto-train engine budgets a single level's row against live unspent
        // CP directly (no level-gain added). STR 40→60 costs 30 but only 10 CP is
        // available → STR clamps to 50 (cost 10), used == 10.
        int[] prev = { 40, 40, 40, 40, 40, 40 };
        int[] target = { 60, 40, 40, 40, 40, 40 };
        int[] min = { 40, 40, 40, 40, 40, 40 };
        int[] max = { 100, 100, 100, 100, 100, 100 };

        int[] clamped = CpPlanCalculator.ClampRowToBudget(
            prev, target, min, max, available: 10, RealmType.Stock, preferredTrim: null, out int used);

        Assert.Equal(50, clamped[0]);
        Assert.Equal(10, used);
    }

    [Fact]
    public void InitialCp_SeedsEarnedTotal()
    {
        IReadOnlyList<CpRowResult> rows = CpPlanCalculator.Compute(
            Baseline, new[] { Uniform(2, 40) }, RaceMin, RaceMax, initialCp: 7, RealmType.Stock);

        Assert.Equal(17, rows[0].CpEarnedTotal);   // 7 unspent + 10 gained at level 2
        Assert.Equal(17, rows[0].CpLeft);          // nothing spent
    }
}
