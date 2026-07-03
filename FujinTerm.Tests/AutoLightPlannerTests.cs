using System.Collections.Generic;
using FujinTerm.Game.Inventory;
using FujinTerm.Game.Light;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="AutoLightPlanner"/> — the pure ready/buy/reorder/none decision the
/// auto-light engine acts on. Exercises the three triggers (dwindling readied
/// light, dark route with / without a covering light on hand, lit route) and the
/// preferred-vs-auto light selection.
/// </summary>
public sealed class AutoLightPlannerTests
{
    // torch: 100 illu, 800 UseCount → 80 readied → 40 min burn.
    private static readonly LightItem Torch = new(1, "torch", Strength: 100, UseCount: 800);
    // lantern: 175 illu, 2400 UseCount → 240 readied → 2 h burn.
    private static readonly LightItem Lantern = new(2, "lantern", Strength: 175, UseCount: 2400);

    private static readonly IReadOnlyList<LightItem> Catalogue = new[] { Torch, Lantern };

    // A dark route whose darkest room sits at the given light offset. NeededLightStrength
    // is set consistent with 0 baseline illu so NeedsLight is true; the planner recomputes
    // the gap itself from wornIllu + DarkestRoomLight.
    private static RouteLightScan Dark(int darkestLight) => new(
        RoomCount: 1, DarkestRoomLight: darkestLight, DarkestRoom: new RoomKey(1, 1),
        NeededLightStrength: LightModel.IlluGapToSee(0, darkestLight));

    private static readonly RouteLightScan Lit = new(
        RoomCount: 3, DarkestRoomLight: 0, DarkestRoom: new RoomKey(1, 1), NeededLightStrength: 0);

    private static AutoLightSettings Settings(int carry = 12, int reorder = 60, string? preferred = null) =>
        new() { CarryHours = carry, ReorderThresholdMinutes = reorder, PreferredLightName = preferred };

    [Fact]
    public void LitRoute_NothingReadied_DoesNothing()
    {
        AutoLightPlan plan = AutoLightPlanner.Plan(
            Lit, wornIllu: 0, readied: null,
            carriedLights: System.Array.Empty<LightItem>(), Catalogue, Settings());

        Assert.Equal(AutoLightAction.None, plan.Action);
    }

    [Fact]
    public void DarkRoute_CarryingCoveringLight_ReadiesIt()
    {
        // -300 room, 0 worn illu → need 150 illu. Carried lantern (175) covers.
        AutoLightPlan plan = AutoLightPlanner.Plan(
            Dark(-300), wornIllu: 0, readied: null,
            carriedLights: new[] { Lantern }, Catalogue, Settings());

        Assert.Equal(AutoLightAction.Ready, plan.Action);
        Assert.Equal("lantern", plan.LightName);
        Assert.Equal(0, plan.BuyCount);
    }

    [Fact]
    public void DarkRoute_AutoPicks_WeakestCoveringCarriedLight()
    {
        // -160 room, 0 worn illu → need only 10 illu. Torch (100) already covers,
        // so auto readies the weaker torch, not the lantern.
        AutoLightPlan plan = AutoLightPlanner.Plan(
            Dark(-160), wornIllu: 0, readied: null,
            carriedLights: new[] { Lantern, Torch }, Catalogue, Settings());

        Assert.Equal(AutoLightAction.Ready, plan.Action);
        Assert.Equal("torch", plan.LightName);
    }

    [Fact]
    public void DarkRoute_NoCoveringCarry_ProvisionsFromCatalogue()
    {
        // Need 150 illu; only a torch (100) on hand → buy the weakest covering
        // catalogue light (lantern), CarryHours 12 / 2 h burn → 6 copies.
        AutoLightPlan plan = AutoLightPlanner.Plan(
            Dark(-300), wornIllu: 0, readied: null,
            carriedLights: new[] { Torch }, Catalogue, Settings(carry: 12));

        Assert.Equal(AutoLightAction.Buy, plan.Action);
        Assert.Equal("lantern", plan.LightName);
        Assert.Equal(6, plan.BuyCount);
    }

    [Fact]
    public void DarkRoute_ReadiedLightAlreadyCovers_DoesNothing()
    {
        // Lantern (175) readied and healthy (100 min left, above the 60-min
        // reorder threshold) covers the -300 room (need 150) → leave it lit
        // rather than re-ready a carried light on every hop of the dark run.
        ReadiedLight lit = new("lantern", Readied: 200);

        AutoLightPlan plan = AutoLightPlanner.Plan(
            Dark(-300), wornIllu: 0, readied: lit,
            carriedLights: new[] { Torch }, Catalogue, Settings());

        Assert.Equal(AutoLightAction.None, plan.Action);
    }

    [Fact]
    public void DarkRoute_ReadiedLightTooWeak_ReadiesACoveringCarry()
    {
        // Torch (100) readied but the -300 room needs 150 → the lit light doesn't
        // cover, so the guard stays out of the way and a carried lantern readies.
        // Reorder off so the dwindling torch doesn't shadow the coverage decision.
        ReadiedLight lit = new("torch", Readied: 60);

        AutoLightPlan plan = AutoLightPlanner.Plan(
            Dark(-300), wornIllu: 0, readied: lit,
            carriedLights: new[] { Lantern }, Catalogue, Settings(reorder: 0));

        Assert.Equal(AutoLightAction.Ready, plan.Action);
        Assert.Equal("lantern", plan.LightName);
    }

    [Fact]
    public void PreferredLight_IsUsedAsIs_EvenWhenTooWeak()
    {
        // Need 150 illu but the user explicitly prefers a torch (100). Carried →
        // ready the torch anyway (explicit pick wins over coverage).
        AutoLightPlan plan = AutoLightPlanner.Plan(
            Dark(-300), wornIllu: 0, readied: null,
            carriedLights: new[] { Torch }, Catalogue, Settings(preferred: "torch"));

        Assert.Equal(AutoLightAction.Ready, plan.Action);
        Assert.Equal("torch", plan.LightName);
    }

    [Fact]
    public void PreferredLight_NotCarried_BuysThePreferred()
    {
        // Prefer lantern, none carried → buy the lantern (not an auto-picked light).
        AutoLightPlan plan = AutoLightPlanner.Plan(
            Dark(-300), wornIllu: 0, readied: null,
            carriedLights: new[] { Torch }, Catalogue, Settings(carry: 12, preferred: "lantern"));

        Assert.Equal(AutoLightAction.Buy, plan.Action);
        Assert.Equal("lantern", plan.LightName);
        Assert.Equal(6, plan.BuyCount);
    }

    [Fact]
    public void ProvisioningOff_DarkRoute_ReadiesCoveringCarry()
    {
        // CarryHours 0 (provisioning off) still readies a carried light that covers.
        AutoLightPlan plan = AutoLightPlanner.Plan(
            Dark(-300), wornIllu: 0, readied: null,
            carriedLights: new[] { Lantern }, Catalogue, Settings(carry: 0));

        Assert.Equal(AutoLightAction.Ready, plan.Action);
        Assert.Equal("lantern", plan.LightName);
    }

    [Fact]
    public void ProvisioningOff_NoCoveringCarry_ReadiesStrongestAsFallback()
    {
        // Need 150 illu, only a torch (100) on hand, provisioning off → ready the
        // torch as a best-effort partial (no buy possible).
        AutoLightPlan plan = AutoLightPlanner.Plan(
            Dark(-300), wornIllu: 0, readied: null,
            carriedLights: new[] { Torch }, Catalogue, Settings(carry: 0));

        Assert.Equal(AutoLightAction.Ready, plan.Action);
        Assert.Equal("torch", plan.LightName);
        Assert.Contains("best carried", plan.Reason);
    }

    [Fact]
    public void ProvisioningOff_NothingCarried_DoesNothing()
    {
        AutoLightPlan plan = AutoLightPlanner.Plan(
            Dark(-300), wornIllu: 0, readied: null,
            carriedLights: System.Array.Empty<LightItem>(), Catalogue, Settings(carry: 0));

        Assert.Equal(AutoLightAction.None, plan.Action);
    }

    [Fact]
    public void WornIllu_ShrinksNeed_LetsAWeakLightCover()
    {
        // -300 room but +250 worn illu → need only 100 illu. Carried torch (100)
        // now covers, so it readies rather than buying.
        AutoLightPlan plan = AutoLightPlanner.Plan(
            Dark(-300), wornIllu: 250, readied: null,
            carriedLights: new[] { Torch }, Catalogue, Settings());

        Assert.Equal(AutoLightAction.Ready, plan.Action);
        Assert.Equal("torch", plan.LightName);
    }

    [Fact]
    public void Reorder_ReadiedLightBelowThreshold_RestocksSameLight()
    {
        // Lantern readied at 100 points → 50 min left, below the 60-min threshold →
        // restock the lantern even though the route is lit. Distinct from a
        // route-dark Buy so the engine can latch it once per readied instance.
        ReadiedLight low = new("lantern", Readied: 100);

        AutoLightPlan plan = AutoLightPlanner.Plan(
            Lit, wornIllu: 0, readied: low,
            carriedLights: System.Array.Empty<LightItem>(), Catalogue, Settings(carry: 12, reorder: 60));

        Assert.Equal(AutoLightAction.Reorder, plan.Action);
        Assert.Equal("lantern", plan.LightName);
        Assert.Equal(6, plan.BuyCount);
    }

    [Fact]
    public void Reorder_ReadiedLightAboveThreshold_DoesNotFire()
    {
        // 200 points → 100 min left, above 60 → no reorder; lit route → nothing.
        ReadiedLight healthy = new("lantern", Readied: 200);

        AutoLightPlan plan = AutoLightPlanner.Plan(
            Lit, wornIllu: 0, readied: healthy,
            carriedLights: System.Array.Empty<LightItem>(), Catalogue, Settings(carry: 12, reorder: 60));

        Assert.Equal(AutoLightAction.None, plan.Action);
    }

    [Fact]
    public void Reorder_ThresholdZero_NeverFires()
    {
        ReadiedLight low = new("lantern", Readied: 10);

        AutoLightPlan plan = AutoLightPlanner.Plan(
            Lit, wornIllu: 0, readied: low,
            carriedLights: System.Array.Empty<LightItem>(), Catalogue, Settings(carry: 12, reorder: 0));

        Assert.Equal(AutoLightAction.None, plan.Action);
    }

    [Fact]
    public void Reorder_ProvisioningOff_DoesNotRestock()
    {
        // Even a nearly-dead readied light doesn't reorder when provisioning is off.
        ReadiedLight low = new("lantern", Readied: 10);

        AutoLightPlan plan = AutoLightPlanner.Plan(
            Lit, wornIllu: 0, readied: low,
            carriedLights: System.Array.Empty<LightItem>(), Catalogue, Settings(carry: 0, reorder: 60));

        Assert.Equal(AutoLightAction.None, plan.Action);
    }
}
