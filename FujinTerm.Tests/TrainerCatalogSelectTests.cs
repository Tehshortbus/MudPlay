using System.Collections.Generic;
using FujinTerm.Game.GameData;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 10.8 — <see cref="TrainerCatalog.SelectNearest"/> picks the trainer the
/// auto-train coordinator should walk to: serves the level (MMUD off-by-one
/// gate), class-ok, allowed, reachable, nearest by the injected distance metric.
/// </summary>
public sealed class TrainerCatalogSelectTests
{
    // Universal trainers; ranges chosen so coverage is 0–9 then 11–19, leaving
    // level 10 as a deliberate gap (mirrors a real quest-gated level).
    private static readonly TrainerShop Newhaven  = new(1, "Newhaven",   1, 100, "", 1, 3, 0, 0);   // serves 0–2
    private static readonly TrainerShop Silver    = new(2, "Silvermere", 1, 200, "", 3, 10, 0, 0);  // serves 2–9
    private static readonly TrainerShop Aldreth   = new(3, "Aldreth",    1, 300, "", 12, 20, 0, 0); // serves 11–19
    private static readonly TrainerShop MageGuild = new(4, "Mage Guild", 1, 400, "", 1, 99, 7, 0);  // class 7 only

    private static readonly IReadOnlyList<TrainerShop> All = new[] { Newhaven, Silver, Aldreth, MageGuild };

    [Fact]
    public void PicksLevelAppropriateUniversalTrainer()
    {
        // Level 5: Silvermere serves (3–10); Newhaven + Aldreth don't; Mage
        // Guild is class-7 only.
        TrainerShop? pick = TrainerCatalog.SelectNearest(
            All, level: 5, classNumber: 0, disabled: new HashSet<string>(), distance: _ => 10);

        Assert.Equal(Silver.Number, pick!.Value.Number);
    }

    [Fact]
    public void PrefersTheNearestReachable()
    {
        // Level 2 serves Newhaven + Silvermere (+ Mage Guild for class 7) — pick
        // the closest by the injected distance.
        TrainerShop? pick = TrainerCatalog.SelectNearest(
            All, level: 2, classNumber: 7, disabled: new HashSet<string>(),
            distance: t => t.Number == MageGuild.Number ? 2 : 20);

        Assert.Equal(MageGuild.Number, pick!.Value.Number);
    }

    [Fact]
    public void SkipsDisallowedAndUnreachable()
    {
        // Level 1: only Newhaven (universal) + Mage Guild (class 7) serve it.
        // Newhaven disallowed, Mage Guild unreachable → nothing qualifies.
        TrainerShop? pick = TrainerCatalog.SelectNearest(
            All, level: 1, classNumber: 7, disabled: new HashSet<string> { Newhaven.RowKey },
            distance: t => t.Number == MageGuild.Number ? (int?)null : 5);

        Assert.Null(pick);
    }

    [Fact]
    public void GapLevelClassGated_ReturnsNull()
    {
        // Level 10 is the coverage gap: no universal trainer serves it, and the
        // only one that spans it (Mage Guild) is class-7 — so a class-3 character
        // has no trainer (a quest-gated level).
        TrainerShop? pick = TrainerCatalog.SelectNearest(
            All, level: 10, classNumber: 3, disabled: new HashSet<string>(), distance: _ => 1);

        Assert.Null(pick);
    }

    [Theory]
    [InlineData(0, 7, true)]    // universal Training Room serves every class
    [InlineData(0, 0, true)]    // universal, even when class is unknown (0)
    [InlineData(7, 7, true)]    // guild serves its own class
    [InlineData(7, 3, false)]   // guild hidden from other classes
    public void ServesClass_GatesGuildsButNotUniversal(int classRest, int classNumber, bool expected)
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
        // Two universal guilds both teach up to level 5; the lower markup wins.
        TrainerShop dear  = new(1, "Dear",  1, 100, "", 1, 20, 0, 9999);
        TrainerShop cheap = new(2, "Cheap", 1, 200, "", 1, 20, 0, 300);
        var all = new[] { dear, cheap };

        Assert.Equal(300, TrainerCatalog.CheapestMarkup(all, targetLevel: 5, classNumber: 3));
    }

    [Fact]
    public void CheapestMarkup_IgnoresTrainersOutOfRangeOrOffClass()
    {
        // Reaching level 15: only Aldreth (serves reaching 12–20) qualifies; the
        // class-7 Mage Guild is hidden from a class-3 char.
        Assert.Equal(0, TrainerCatalog.CheapestMarkup(All, targetLevel: 15, classNumber: 3));
    }

    [Fact]
    public void CheapestMarkup_ReturnsNullWhenNoTrainerServes()
    {
        // Reaching level 11 for a class-3 char: Silvermere tops out at 10 and
        // Aldreth doesn't start until 12 — a quest-gated seam with no trainer.
        Assert.Null(TrainerCatalog.CheapestMarkup(All, targetLevel: 11, classNumber: 3));
    }
}
