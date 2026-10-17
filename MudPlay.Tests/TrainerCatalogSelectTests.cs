using System.Collections.Generic;
using MudPlay.Game.GameData;
using Xunit;

namespace MudPlay.Tests;

// TrainerCatalog.SelectNearest picks the trainer the
// auto-train coordinator should walk to: serves the level (MMUD off-by-one
// gate), class-ok, allowed, reachable, nearest by the injected distance metric.
public sealed class TrainerCatalogSelectTests
{
    // Universal trainers; ranges chosen so coverage is 0–9 then 11–19, leaving
    // level 10 as a deliberate gap (mirrors a real quest-gated level).
    private static readonly TrainerShop Newhaven  = new(1, "Newhaven",   1, 100, "", 1, 3, 0, 0);   // serves 0–2
    private static readonly TrainerShop Silver    = new(2, "Silvermere", 1, 200, "", 3, 10, 0, 0);  // serves 2–9
    private static readonly TrainerShop Aldreth   = new(3, "Aldreth",    1, 300, "", 12, 20, 0, 0); // serves 11–19
    private static readonly TrainerShop MageTrainer = new(4, "Mage Trainer", 1, 400, "", 1, 99, 7, 0);  // class 7 only

    private static readonly IReadOnlyList<TrainerShop> All = new[] { Newhaven, Silver, Aldreth, MageTrainer };

    [Fact]
    public void PicksLevelAppropriateUniversalTrainer()
    {
        // Level 5: Silvermere serves (3–10); Newhaven + Aldreth don't; the Mage
        // Trainer is class-7 only.
        TrainerShop? pick = TrainerCatalog.SelectNearest(
            All, level: 5, classNumber: 0, disabled: new HashSet<string>(), distance: _ => 10);

        Assert.Equal(Silver.Number, pick!.Value.Number);
    }

    [Fact]
    public void PrefersTheNearestReachable()
    {
        // Level 2 serves Newhaven + Silvermere (+ Mage Trainer for class 7) — pick
        // the closest by the injected distance.
        TrainerShop? pick = TrainerCatalog.SelectNearest(
            All, level: 2, classNumber: 7, disabled: new HashSet<string>(),
            distance: t => t.Number == MageTrainer.Number ? 2 : 20);

        Assert.Equal(MageTrainer.Number, pick!.Value.Number);
    }

    [Fact]
    public void SkipsDisallowedAndUnreachable()
    {
        // Level 1: only Newhaven (universal) + Mage Trainer (class 7) serve it.
        // Newhaven disallowed, Mage Trainer unreachable → nothing qualifies.
        TrainerShop? pick = TrainerCatalog.SelectNearest(
            All, level: 1, classNumber: 7, disabled: new HashSet<string> { Newhaven.RowKey },
            distance: t => t.Number == MageTrainer.Number ? (int?)null : 5);

        Assert.Null(pick);
    }

    [Fact]
    public void GapLevelClassGated_ReturnsNull()
    {
        // Level 10 is the coverage gap: no universal trainer serves it, and the
        // only one that spans it (Mage Trainer) is class-7 — so a class-3 character
        // has no trainer (a quest-gated level).
        TrainerShop? pick = TrainerCatalog.SelectNearest(
            All, level: 10, classNumber: 3, disabled: new HashSet<string>(), distance: _ => 1);

        Assert.Null(pick);
    }

    [Theory]
    [InlineData(0, 7, true)]    // universal Training Room serves every class
    [InlineData(0, 0, true)]    // universal, even when class is unknown (0)
    [InlineData(7, 7, true)]    // class trainer serves its own class
    [InlineData(7, 3, false)]   // class trainer hidden from other classes
    public void ServesClass_GatesClassTrainersButNotUniversal(int classRest, int classNumber, bool expected)
    {
        // Drives the Settings → Auto-Trainer view filter (a Mystic shouldn't see
        // other classes' super trainers).
        TrainerShop shop = new(1, "Trainer", 1, 100, "", 1, 99, classRest, 0);
        Assert.Equal(expected, shop.ServesClass(classNumber));
    }

    // ----- CheapestMarkup (drives the Level Projection train-cost column) --

    [Fact]
    public void CheapestMarkup_PicksLowestMarkupAmongServingTrainers()
    {
        // Two universal trainers both teach up to level 5; the lower markup wins.
        TrainerShop dear  = new(1, "Dear",  1, 100, "", 1, 20, 0, 9999);
        TrainerShop cheap = new(2, "Cheap", 1, 200, "", 1, 20, 0, 300);
        var all = new[] { dear, cheap };

        Assert.Equal(300, TrainerCatalog.CheapestMarkup(all, targetLevel: 5, classNumber: 3));
    }

    [Fact]
    public void CheapestMarkup_IgnoresTrainersOutOfRangeOrOffClass()
    {
        // Reaching level 15: only Aldreth (serves reaching 12–20) qualifies; the
        // class-7 Mage Trainer is hidden from a class-3 char.
        Assert.Equal(0, TrainerCatalog.CheapestMarkup(All, targetLevel: 15, classNumber: 3));
    }

    [Fact]
    public void CheapestMarkup_ReturnsNullWhenNoTrainerServes()
    {
        // Reaching level 11 for a class-3 char: Silvermere tops out at 10 and
        // Aldreth doesn't start until 12 — a quest-gated seam with no trainer.
        Assert.Null(TrainerCatalog.CheapestMarkup(All, targetLevel: 11, classNumber: 3));
    }

    // ----- SelectNearestForStats — CP allocation, class-only, no level band -----
    // `train stats` isn't level-band gated (only `train` is), so CP allocation picks by
    // class regardless of the trainer's MinLVL/MaxLVL.

    [Fact]
    public void SelectNearestForStats_IgnoresLevelBand()
    {
        // Level 10 is the deliberate gap — no trainer serves it, so SelectNearest
        // (level-band gated) returns null. CP allocation isn't band-gated, so
        // SelectNearestForStats still picks the nearest class-ok trainer to apply stats.
        Assert.Null(TrainerCatalog.SelectNearest(
            All, level: 10, classNumber: 0, disabled: new HashSet<string>(), distance: _ => 5));

        TrainerShop? pick = TrainerCatalog.SelectNearestForStats(
            All, classNumber: 0, disabled: new HashSet<string>(),
            distance: t => t.Number == Newhaven.Number ? 1 : 20);
        Assert.Equal(Newhaven.Number, pick!.Value.Number);
    }

    [Fact]
    public void SelectNearestForStats_RespectsClass()
    {
        // The class-7 Mage Trainer is never picked for a class-3 character, band or not.
        TrainerShop? pick = TrainerCatalog.SelectNearestForStats(
            new[] { MageTrainer }, classNumber: 3, disabled: new HashSet<string>(), distance: _ => 1);
        Assert.Null(pick);
    }

    [Fact]
    public void SelectNearestForStats_HonorsDisabledAndReachability()
    {
        // Newhaven disabled, Silvermere unreachable → nothing qualifies even though both
        // are class-ok for CP.
        TrainerShop? pick = TrainerCatalog.SelectNearestForStats(
            new[] { Newhaven, Silver }, classNumber: 0,
            disabled: new HashSet<string> { Newhaven.RowKey },
            distance: t => t.Number == Silver.Number ? (int?)null : 3);
        Assert.Null(pick);
    }

    [Fact]
    public void SelectNearestForStats_StandingInAValidTrainer_PicksItInPlace()
    {
        // Distance 0 (you're standing in it) is the nearest — selected, so CP applies in
        // place with no walk.
        TrainerShop? pick = TrainerCatalog.SelectNearestForStats(
            All, classNumber: 0, disabled: new HashSet<string>(),
            distance: t => t.Number == Silver.Number ? 0 : 9);
        Assert.Equal(Silver.Number, pick!.Value.Number);
    }

    // ----- RankCandidates — the ranking the selectors pick from and the log shows -----

    // Shaped on report paradigm-20260927-030929: a Gypsy at level 1 with the universal
    // Training Room in two towns (one disabled) and the pricier Gypsy class trainer.
    private static readonly TrainerShop GypsyRoom   = new(21, "Gypsy Training Room", 1, 384, "", 1, 10, 10, 300);
    private static readonly TrainerShop UniSilver   = new(38, "Training Room", 1, 1376, "", 1, 10, 0, 0);
    private static readonly TrainerShop UniNewhaven = new(38, "Training Room", 1, 2147, "", 1, 10, 0, 0);

    [Fact]
    public void RankCandidates_OrdersUsableByDistanceThenListsRuledOut()
    {
        var ranked = TrainerCatalog.RankCandidates(
            new[] { GypsyRoom, UniSilver, UniNewhaven }, level: 1, classNumber: 10,
            disabled: new HashSet<string> { UniNewhaven.RowKey },
            distance: t => t.Room switch { 384 => 30, 1376 => 25, _ => 2 });

        Assert.Equal(new[] { 1376, 384, 2147 }, ranked.Select(c => c.Trainer.Room));
        Assert.Equal(TrainerSkip.Disabled, ranked[2].Skip);
        Assert.Equal(UniSilver, TrainerCatalog.FirstUsable(ranked));
        Assert.Equal(
            "Training Room 1/1376 (25 steps), Gypsy Training Room 1/384 (30 steps), Training Room 1/2147 (disabled)",
            TrainerCatalog.DescribeCandidates(ranked));
    }

    [Fact]
    public void SelectNearest_EqualDistance_PrefersTheCheaperTrainer()
    {
        // Same walk either way — the lower markup is the cheaper lesson, whatever
        // order the Shops table lists them in.
        TrainerShop? pick = TrainerCatalog.SelectNearest(
            new[] { GypsyRoom, UniSilver }, level: 1, classNumber: 10,
            disabled: new HashSet<string>(), distance: _ => 12);

        Assert.Equal(UniSilver, pick);
    }

    [Fact]
    public void RankCandidates_AllRuledOut_SelectsNothingButStillExplains()
    {
        var ranked = TrainerCatalog.RankCandidates(
            new[] { GypsyRoom, UniNewhaven }, level: 1, classNumber: 10,
            disabled: new HashSet<string> { UniNewhaven.RowKey }, distance: _ => null);

        Assert.Null(TrainerCatalog.FirstUsable(ranked));
        Assert.Equal("Gypsy Training Room 1/384 (no path), Training Room 1/2147 (disabled)",
            TrainerCatalog.DescribeCandidates(ranked));
    }
}
