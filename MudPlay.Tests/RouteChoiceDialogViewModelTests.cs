using System.Collections.Generic;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

// The picker's requirement line promises a source tail — "(ask <giver>)", else
// "(buy at <shop>)", else "(dropped by <monster>)" — only for the single-item
// gate kinds a walk actually auto-sources (Item, Ticket, single-counter
// hazard). Keys and any-of hazard counters never post a single auto-obtain
// path-item need, so they must not carry a tail even when a resolver would name
// one. These pin that kind-gating, the give-over-shop-over-drop precedence, and
// the no-resolver fallback, plus the select-to-preview / Go interaction.
public sealed class RouteChoiceDialogViewModelTests
{
    private static readonly IReadOnlyList<RoomKey> FreeLine =
        new[] { new RoomKey(1, 1), new RoomKey(1, 2), new RoomKey(1, 9) };
    private static readonly IReadOnlyList<RoomKey> GatedLine =
        new[] { new RoomKey(1, 1), new RoomKey(1, 9) };

    private static RouteChoice Choice(params RouteRequirement[] reqs) =>
        new(FreeStepCount: 5, GatedStepCount: 2, reqs, FreeLine, GatedLine);

    // Sole-route variant: no gate-free detour (empty FreePath), so the picker
    // collapses the send-it/acquire split to the single acquire card.
    private static RouteChoice SoleChoice(params RouteRequirement[] reqs) =>
        new(FreeStepCount: 0, GatedStepCount: 2, reqs, System.Array.Empty<RoomKey>(), GatedLine);

    [Fact]
    public void CarryItemGate_WithShop_GetsBuyTail()
    {
        var choice = Choice(new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 5 }));

        var vm = new RouteChoiceDialogViewModel(
            choice, "Bank (1/9)", id => id == 5 ? "a raft" : null,
            shopBuyPhraseForItem: id => id == 5 ? "buy at General Store" : null);

        Assert.Equal("Requires a raft (buy at General Store)", vm.RequirementSummary);
    }

    [Fact]
    public void TicketGate_WithShop_GetsBuyTail()
    {
        var choice = Choice(new RouteRequirement(RouteRequirementKind.Ticket, new[] { 7 }));

        var vm = new RouteChoiceDialogViewModel(
            choice, "Docks (1/9)", id => id == 7 ? "a ferry ticket" : null,
            shopBuyPhraseForItem: id => id == 7 ? "buy at Ticket Booth" : null);

        Assert.Equal("Requires a ferry ticket (buy at Ticket Booth)", vm.RequirementSummary);
    }

    // A door the crosser can neither pick nor bash needs its key, and a key with a
    // reliable source is fetched like any other gate item (report
    // paradigm-20261007-192215: a shop-sold skeleton key was never bought). The
    // clause names where it comes from, a free hand-over ahead of a purchase.
    [Fact]
    public void DoorKeyGate_GetsTheSourceTail_WhenOneResolves()
    {
        var choice = Choice(new RouteRequirement(RouteRequirementKind.DoorKey, new[] { 9 }));

        var bought = new RouteChoiceDialogViewModel(
            choice, "Vault (1/9)", id => "the skeleton key",
            shopBuyPhraseForItem: id => "buy at Thieves' Guild");
        Assert.Equal("Requires the skeleton key (buy at Thieves' Guild)", bought.RequirementSummary);

        var given = new RouteChoiceDialogViewModel(
            choice, "Vault (1/9)", id => "the iron key",
            giveNameForItem: id => "a gatekeeper", shopBuyPhraseForItem: id => "buy at Locksmith");
        Assert.Equal("Requires the iron key (ask a gatekeeper)", given.RequirementSummary);
    }

    [Fact]
    public void DoorKeyGate_WithNoSource_NamesTheKeyAlone()
    {
        var choice = Choice(new RouteRequirement(RouteRequirementKind.DoorKey, new[] { 9 }));

        var vm = new RouteChoiceDialogViewModel(choice, "Vault (1/9)", id => "the iron key");

        Assert.Equal("Requires the iron key", vm.RequirementSummary);
    }

    [Fact]
    public void HazardGate_NeverGetsBuyTail()
    {
        var choice = Choice(new RouteRequirement(
            RouteRequirementKind.HazardProtection, new[] { 11, 12 }));

        var vm = new RouteChoiceDialogViewModel(
            choice, "Flooded hall (1/9)",
            id => id == 11 ? "a fish-helm" : "a waterskin",
            id => "General Store");

        Assert.Equal("Requires a fish-helm or a waterskin", vm.RequirementSummary);
    }

    [Fact]
    public void HazardGate_NoShop_GetsDropTail()
    {
        // A single-counter hazard whose item no shop sells but a monster drops:
        // the picker previews the hunt the walk would reroute to run.
        var choice = Choice(new RouteRequirement(
            RouteRequirementKind.HazardProtection, new[] { 42 }));

        var vm = new RouteChoiceDialogViewModel(
            choice, "Sunbaked dune (1/9)",
            id => id == 42 ? "a waterskin" : null,
            shopBuyPhraseForItem: id => null,          // no shop stocks it
            dropNameForItem: id => id == 42 ? "a sand nomad" : null);

        Assert.Equal("Requires a waterskin (dropped by a sand nomad)", vm.RequirementSummary);
    }

    [Fact]
    public void SoleHazard_WithObtainableCounter_OffersObtainThenCross()
    {
        // The caller resolved a sourceable counter for the any-of hazard set, so
        // Go fetches it then crosses and a "cross unprotected" escape is offered.
        var choice = SoleChoice(new RouteRequirement(
            RouteRequirementKind.HazardProtection, new[] { 11, 12 }));

        var vm = new RouteChoiceDialogViewModel(
            choice, "Frozen cavern (10/297)",
            id => id == 11 ? "rope and grapple" : "climbing harness",
            hazardCounterSource: "grab from the floor here", hazardSurvivable: true);

        Assert.True(vm.HazardObtain);
        Assert.True(vm.ShowSendItCard);
        Assert.Contains("Obtain, then cross", vm.GatedSummary);
        // The footnote is now one uniform line (the counter source it used to name
        // lives on the cards / Details… now).
        Assert.Contains("Details", vm.Footnote);
        Assert.Contains("take the damage", vm.SendItSummary);
        Assert.Equal("Requires rope and grapple or climbing harness", vm.RequirementSummary);
    }

    [Fact]
    public void SoleHazard_NoObtainableCounter_KeepsManualCounterWording()
    {
        // No counter resolved (source null) — the picker stays the pre-feature
        // "carry, buy, or use a counter" with no send-it escape.
        var choice = SoleChoice(new RouteRequirement(
            RouteRequirementKind.HazardProtection, new[] { 11, 12 }));

        var vm = new RouteChoiceDialogViewModel(
            choice, "Frozen cavern (10/297)",
            id => id == 11 ? "rope and grapple" : "climbing harness");

        Assert.False(vm.HazardObtain);
        Assert.False(vm.ShowSendItCard);
        // The manual-counter wording now lives on the Free card ("a hazard you must
        // counter"); the footnote is the uniform preview/Details line.
        Assert.Contains("you must counter", vm.FreeSummary);
    }

    [Fact]
    public void CarryItemGate_ShopWinsOverDrop_WhenBothResolve()
    {
        // Shop and drop both name a source — the buy tail wins (cheap,
        // deterministic; the routers are shop-first mutually exclusive).
        var choice = Choice(new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 5 }));

        var vm = new RouteChoiceDialogViewModel(
            choice, "Bank (1/9)", id => "a raft",
            shopBuyPhraseForItem: id => "buy at General Store",
            dropNameForItem: id => "a river troll");

        Assert.Equal("Requires a raft (buy at General Store)", vm.RequirementSummary);
    }

    [Fact]
    public void CarryItemGate_WithGive_GetsAskTail()
    {
        // A deterministic textblock giver hands the gate item over for free — the
        // picker names the giver the run will ask.
        var choice = Choice(new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 5 }));

        var vm = new RouteChoiceDialogViewModel(
            choice, "Bank (1/9)", id => "a bloodstone orb",
            giveNameForItem: id => id == 5 ? "Gnome Commander" : null);

        Assert.Equal("Requires a bloodstone orb (ask Gnome Commander)", vm.RequirementSummary);
    }

    [Fact]
    public void CarryItemGate_GiveWinsOverShopAndDrop_WhenAllResolve()
    {
        // Give, shop, and drop all name a source — the free give tail wins,
        // mirroring the routers' give-first precedence (the give stands the shop
        // and drop routers down, so the picker must promise the give).
        var choice = Choice(new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 5 }));

        var vm = new RouteChoiceDialogViewModel(
            choice, "Bank (1/9)", id => "a bloodstone orb",
            giveNameForItem: id => "Gnome Commander",
            shopBuyPhraseForItem: id => "buy at General Store",
            dropNameForItem: id => "a river troll");

        Assert.Equal("Requires a bloodstone orb (ask Gnome Commander)", vm.RequirementSummary);
    }

    [Fact]
    public void HazardGate_AnyOf_NeverGetsDropTail()
    {
        // Two-item hazard group: an any-of counter posts no single auto-obtain
        // need, so no drop tail even when a resolver would name a dropper.
        var choice = Choice(new RouteRequirement(
            RouteRequirementKind.HazardProtection, new[] { 11, 12 }));

        var vm = new RouteChoiceDialogViewModel(
            choice, "Flooded hall (1/9)",
            id => id == 11 ? "a fish-helm" : "a waterskin",
            shopBuyPhraseForItem: id => null,
            dropNameForItem: id => "a deep one");

        Assert.Equal("Requires a fish-helm or a waterskin", vm.RequirementSummary);
    }

    [Fact]
    public void NoShopResolver_LeavesRequirementsPlain()
    {
        var choice = Choice(new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 5 }));

        var vm = new RouteChoiceDialogViewModel(
            choice, "Bank (1/9)", id => "a raft");

        Assert.Equal("Requires a raft", vm.RequirementSummary);
    }

    [Fact]
    public void CarryItemGate_ShopResolvesNull_StaysPlain()
    {
        var choice = Choice(new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 5 }));

        // Item flagged/looked-up but no reachable shop stocks it → no tail.
        var vm = new RouteChoiceDialogViewModel(
            choice, "Bank (1/9)", id => "a raft", id => null);

        Assert.Equal("Requires a raft", vm.RequirementSummary);
    }

    // ----- Select-to-preview / Go interaction -----------------------------

    private static RouteChoiceDialogViewModel PickerVm() =>
        new(Choice(new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 5 })),
            "Bank (1/9)", id => "a raft");

    [Fact]
    public void NoSelection_GoDisabled_NothingPreviewed()
    {
        var vm = PickerVm();
        bool previewed = false;
        vm.PreviewRequested += _ => previewed = true;

        Assert.Null(vm.SelectedRoute);
        Assert.False(vm.GoCommand.CanExecute(null));
        Assert.False(vm.IsFreeSelected);
        Assert.False(vm.IsGatedSelected);
        Assert.False(previewed);
    }

    [Fact]
    public void SelectFree_PreviewsFreeLine_EnablesGo_NoWalkYet()
    {
        var vm = PickerVm();
        RouteChoiceResult? previewed = null;
        int previewCount = 0;
        RouteChoiceResult? closed = null;
        bool closeFired = false;
        vm.PreviewRequested += r => { previewed = r; previewCount++; };
        vm.CloseRequested += r => { closed = r; closeFired = true; };

        vm.SelectFreeCommand.Execute(null);

        Assert.Equal(RouteChoiceResult.Free, vm.SelectedRoute);
        Assert.True(vm.IsFreeSelected);
        Assert.False(vm.IsGatedSelected);
        Assert.Equal(RouteChoiceResult.Free, previewed);
        Assert.Equal(1, previewCount);
        Assert.True(vm.GoCommand.CanExecute(null));
        // Selecting previews only — the dialog stays open until Go.
        Assert.False(closeFired);
        Assert.Null(closed);
    }

    [Fact]
    public void SwitchSelection_RepreviewsAndFlipsHighlight()
    {
        var vm = PickerVm();
        var previews = new List<RouteChoiceResult?>();
        vm.PreviewRequested += r => previews.Add(r);

        vm.SelectFreeCommand.Execute(null);
        vm.SelectGatedCommand.Execute(null);

        Assert.Equal(RouteChoiceResult.Gated, vm.SelectedRoute);
        Assert.False(vm.IsFreeSelected);
        Assert.True(vm.IsGatedSelected);
        Assert.Equal(
            new RouteChoiceResult?[] { RouteChoiceResult.Free, RouteChoiceResult.Gated },
            previews);
    }

    [Fact]
    public void Go_ClosesWithSelectedRoute()
    {
        var vm = PickerVm();
        RouteChoiceResult? closed = null;
        int closeCount = 0;
        vm.CloseRequested += r => { closed = r; closeCount++; };

        vm.SelectGatedCommand.Execute(null);
        vm.GoCommand.Execute(null);

        Assert.Equal(RouteChoiceResult.Gated, closed);
        Assert.Equal(1, closeCount);
    }

    [Fact]
    public void Cancel_ClosesWithNull_EvenAfterASelection()
    {
        var vm = PickerVm();
        RouteChoiceResult? closed = RouteChoiceResult.Free;
        bool closeFired = false;
        vm.CloseRequested += r => { closed = r; closeFired = true; };

        vm.SelectFreeCommand.Execute(null);
        vm.CancelCommand.Execute(null);

        Assert.True(closeFired);
        Assert.Null(closed);
    }

    // ----- Send-it (direct — no acquire) third option ----------------------

    [Fact]
    public void SendItCard_ShownWhenFreeRouteExists()
    {
        var vm = PickerVm();
        Assert.True(vm.HasFreeRoute);
        Assert.True(vm.ShowSendItCard);
    }

    [Fact]
    public void SendItCard_HiddenForSoleRoute()
    {
        // No gate-free detour: the acquire/send-it split collapses, so only the
        // single acquire card is offered (chunk-4 flag logic owns the sole case).
        var vm = new RouteChoiceDialogViewModel(
            SoleChoice(new RouteRequirement(RouteRequirementKind.HazardProtection, new[] { 42 })),
            "Sunbaked dune (1/9)", id => "a waterskin");

        Assert.False(vm.HasFreeRoute);
        Assert.False(vm.ShowSendItCard);
    }

    [Fact]
    public void SelectSendIt_PreviewsGatedLine_EnablesGo_NoWalkYet()
    {
        var vm = PickerVm();
        RouteChoiceResult? previewed = null;
        bool closeFired = false;
        vm.PreviewRequested += r => previewed = r;
        vm.CloseRequested += _ => closeFired = true;

        vm.SelectSendItCommand.Execute(null);

        Assert.Equal(RouteChoiceResult.GatedNoAcquire, vm.SelectedRoute);
        Assert.True(vm.IsSendItSelected);
        Assert.False(vm.IsGatedSelected);
        Assert.False(vm.IsFreeSelected);
        Assert.Equal(RouteChoiceResult.GatedNoAcquire, previewed);
        Assert.True(vm.GoCommand.CanExecute(null));
        Assert.False(closeFired);
    }

    [Fact]
    public void SelectSendIt_NoOp_WhenNoFreeRoute()
    {
        var vm = new RouteChoiceDialogViewModel(
            SoleChoice(new RouteRequirement(RouteRequirementKind.HazardProtection, new[] { 42 })),
            "Sunbaked dune (1/9)", id => "a waterskin");
        bool previewed = false;
        vm.PreviewRequested += _ => previewed = true;

        vm.SelectSendItCommand.Execute(null);

        Assert.Null(vm.SelectedRoute);
        Assert.False(vm.IsSendItSelected);
        Assert.False(previewed);
    }

    [Fact]
    public void Go_ClosesWithGatedNoAcquire_AfterSendIt()
    {
        var vm = PickerVm();
        RouteChoiceResult? closed = null;
        vm.CloseRequested += r => closed = r;

        vm.SelectSendItCommand.Execute(null);
        vm.GoCommand.Execute(null);

        Assert.Equal(RouteChoiceResult.GatedNoAcquire, closed);
    }

    // ----- Sole-route wording (no gate-free detour) ------------------------

    [Fact]
    public void SoleDoorKeyRoute_UsesGateGenericWording_NotHazard()
    {
        // A locked-door sole route reaches the picker (a key is never auto-sourced).
        // The wording must not call it a "hazard" — it's a gate cleared by hand.
        var vm = new RouteChoiceDialogViewModel(
            SoleChoice(new RouteRequirement(RouteRequirementKind.DoorKey, new[] { 9 })),
            "Vault (1/9)", id => "the iron key");

        Assert.False(vm.HasFreeRoute);
        Assert.False(vm.ShowSendItCard);
        // Heading is now the uniform "Route to …"; the gate-not-hazard distinction
        // lives in the card wording (FreeSummary), asserted below.
        Assert.DoesNotContain("hazard", vm.FreeSummary, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gate", vm.FreeSummary, System.StringComparison.OrdinalIgnoreCase);
        // The key names itself with no source tail (keys aren't sourced).
        Assert.Equal("Requires the iron key", vm.RequirementSummary);
    }

    [Fact]
    public void SoleHazardRoute_KeepsHazardWording()
    {
        var vm = new RouteChoiceDialogViewModel(
            SoleChoice(new RouteRequirement(RouteRequirementKind.HazardProtection, new[] { 42 })),
            "Sunbaked dune (1/9)", id => "a waterskin");

        Assert.False(vm.HasFreeRoute);
        // Hazard wording moved from the (now-uniform) heading into the card text.
        Assert.Contains("hazard", vm.FreeSummary, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SwitchAcrossAllThree_RepreviewsEachSelection()
    {
        var vm = PickerVm();
        var previews = new List<RouteChoiceResult?>();
        vm.PreviewRequested += r => previews.Add(r);

        vm.SelectFreeCommand.Execute(null);
        vm.SelectGatedCommand.Execute(null);
        vm.SelectSendItCommand.Execute(null);

        Assert.Equal(RouteChoiceResult.GatedNoAcquire, vm.SelectedRoute);
        Assert.True(vm.IsSendItSelected);
        Assert.Equal(
            new RouteChoiceResult?[]
            {
                RouteChoiceResult.Free,
                RouteChoiceResult.Gated,
                RouteChoiceResult.GatedNoAcquire,
            },
            previews);
    }

    [Fact]
    public void Summaries_DistinguishAcquireFromSendIt()
    {
        var vm = PickerVm();
        Assert.Contains("acquire", vm.GatedSummary, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("send it", vm.SendItSummary, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Summaries_ShowApproxEta_WhenSupplied()
    {
        var choice = Choice(new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 5 }));
        var vm = new RouteChoiceDialogViewModel(
            choice, "Bank (1/9)", id => "a raft",
            freeEta: System.TimeSpan.FromSeconds(65),
            gatedEta: System.TimeSpan.FromSeconds(20));

        Assert.Contains("(~1m 5s)", vm.FreeSummary);
        Assert.Contains("(~20s)", vm.GatedSummary);
        Assert.Contains("(~20s)", vm.SendItSummary);   // send-it walks the gated line
    }

    [Fact]
    public void Summaries_OmitEta_WhenNotSupplied()
    {
        var vm = PickerVm();      // no ETA args → bare step counts
        Assert.DoesNotContain("(~", vm.FreeSummary);
        Assert.DoesNotContain("(~", vm.GatedSummary);
        Assert.DoesNotContain("(~", vm.SendItSummary);
    }

    // ----- Teleport fork: walk vs teleport --------------------------------

    private static RouteChoice TeleportChoice() =>
        new(FreeStepCount: 8, GatedStepCount: 2,
            System.Array.Empty<RouteRequirement>(), FreeLine, GatedLine,
            RouteChoiceKind.Teleport, "Silver River (12/34)");

    [Fact]
    public void TeleportChoice_WordsCardsAsWalkVsTeleport_HidesSendIt()
    {
        var vm = new RouteChoiceDialogViewModel(TeleportChoice(), "Silver River (12/34)", id => null);

        Assert.True(vm.IsTeleportChoice);
        Assert.True(vm.HasFreeRoute);
        Assert.False(vm.ShowSendItCard);            // no acquisition to skip
        // Teleport wording is on the cards now, not the uniform heading.
        Assert.Contains("Walk it", vm.FreeSummary);
        Assert.Contains("Teleport", vm.GatedSummary);
        Assert.Empty(vm.RequirementSummary);

        // The gated card's detail line names the landing and warns of the danger.
        Assert.Equal(vm.TeleportCaveat, vm.GatedDetail);
        Assert.Contains("Silver River", vm.GatedDetail);
        Assert.Contains("deadly", vm.GatedDetail, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TeleportChoice_FreeIsWalk_GatedIsTeleport_GoCommits()
    {
        var vm = new RouteChoiceDialogViewModel(TeleportChoice(), "Silver River (12/34)", id => null);
        var previews = new List<RouteChoiceResult?>();
        RouteChoiceResult? closed = null;
        vm.PreviewRequested += r => previews.Add(r);
        vm.CloseRequested += r => closed = r;

        vm.SelectFreeCommand.Execute(null);         // "Walk it"
        vm.SelectGatedCommand.Execute(null);        // "Teleport"
        vm.GoCommand.Execute(null);

        Assert.Equal(RouteChoiceResult.Gated, closed);
        Assert.Equal(
            new RouteChoiceResult?[] { RouteChoiceResult.Free, RouteChoiceResult.Gated },
            previews);
    }

    // The fewest-traps route on the walk-vs-teleport fork (user, 2026-10-20): a third
    // card, worded as the trap-avoid fork words its own, naming the teleport it takes;
    // picking it previews that route and Go commits it.
    [Fact]
    public void TeleportChoice_WithTrapAvoidRoute_OffersItAsAThirdCard()
    {
        IReadOnlyList<RoomKey> trapFree = new[] { new RoomKey(1, 1), new RoomKey(1, 5), new RoomKey(1, 6), new RoomKey(1, 9) };
        RouteChoice choice = TeleportChoice() with { TrapAvoidPath = trapFree, TrapAvoidTrapCount = 0, GatedTrapCount = 2 };
        var vm = new RouteChoiceDialogViewModel(choice, "Silver River (12/34)", id => null,
            teleportsOn: r => r == RouteChoiceResult.TrapAvoid ? "the teleport to Silver River (12/34)" : null);
        var previews = new List<RouteChoiceResult?>();
        RouteChoiceResult? closed = null;
        vm.PreviewRequested += r => previews.Add(r);
        vm.CloseRequested += r => closed = r;

        Assert.True(vm.ShowTrapAvoidCard);
        Assert.Equal("Avoid traps — 3 steps, trap-free — takes the teleport to Silver River (12/34)", vm.TrapAvoidSummary);
        Assert.Contains("crosses 2 traps", vm.TrapAvoidDetail);
        Assert.DoesNotContain("Costs", vm.TrapAvoidDetail);   // no paid transport on it
        Assert.Null(vm.SelectedRoute);              // nothing picked for the user

        vm.SelectTrapAvoidCommand.Execute(null);
        Assert.True(vm.IsTrapAvoidSelected);
        vm.GoCommand.Execute(null);

        Assert.Equal(RouteChoiceResult.TrapAvoid, closed);
        Assert.Equal(new RouteChoiceResult?[] { RouteChoiceResult.TrapAvoid }, previews);
    }

    // A fewest-traps route through a paid NPC transport states its fare, as the
    // teleport card states its own.
    [Fact]
    public void TeleportChoice_TrapAvoidRouteThroughAPaidTransport_StatesItsFare()
    {
        IReadOnlyList<RoomKey> trapFree = new[] { new RoomKey(1, 1), new RoomKey(1, 5), new RoomKey(1, 9) };
        RouteChoice choice = TeleportChoice() with { TrapAvoidPath = trapFree, TrapAvoidFareCopper = 500, GatedTrapCount = 1 };

        var vm = new RouteChoiceDialogViewModel(choice, "Silver River (12/34)", id => null);

        Assert.EndsWith($" Costs {MudPlay.Game.Cash.CurrencyFormat.Full(500)} per person.", vm.TrapAvoidDetail);
    }

    [Fact]
    public void TeleportChoice_WithoutTrapAvoidRoute_HasNoThirdCard()
    {
        var vm = new RouteChoiceDialogViewModel(TeleportChoice(), "Silver River (12/34)", id => null);

        Assert.False(vm.ShowTrapAvoidCard);
        vm.SelectTrapAvoidCommand.Execute(null);
        Assert.Null(vm.SelectedRoute);
    }

    // ----- Avoid-override fork: the card names the rooms ------------------

    // Report paradigm-20261007-215302: the card said "3 rooms you marked Avoid" and
    // nothing else, and the rooms were on another map than the one being looked at.
    [Fact]
    public void AvoidOverride_NamesTheAvoidedRoomsItCrosses()
    {
        RouteChoice choice = new(FreeStepCount: 180, GatedStepCount: 142,
            System.Array.Empty<RouteRequirement>(), FreeLine, GatedLine,
            RouteChoiceKind.AvoidOverride,
            AvoidedRoomCount: 3,
            AvoidedRoomNames: new[] { "Black Wasteland (3/740)", "Black Wasteland (3/669)", "Black Wasteland (3/756)" });

        var vm = new RouteChoiceDialogViewModel(choice, "Lake of Fire (9/1427)", id => "");

        Assert.Contains("3 rooms you marked Avoid", vm.GatedSummary);
        Assert.Contains("Black Wasteland (3/740), Black Wasteland (3/669), Black Wasteland (3/756)", vm.AvoidCaveat);
    }

    [Fact]
    public void AvoidAlternativeCard_NamesTheAvoidedRoomsItCrosses()
    {
        RouteChoice choice = new(FreeStepCount: 0, GatedStepCount: 4,
            System.Array.Empty<RouteRequirement>(), System.Array.Empty<RoomKey>(), GatedLine,
            AvoidAlternativePath: FreeLine, AvoidAlternativeCount: 1,
            AvoidAlternativeNames: new[] { "Fungus Forest (7/246)" });

        var vm = new RouteChoiceDialogViewModel(choice, "Lake of Fire (9/1427)", id => "");

        Assert.True(vm.ShowAvoidAltCard);
        Assert.Contains("Fungus Forest (7/246)", vm.AvoidAltDetail);
    }

    // ----- Boss-room fork: around, up to it and wait, or through ----------

    [Fact]
    public void BossRoom_ShowsThreeCards_AndPreSelectsWalkUpAndWait()
    {
        RouteChoice choice = new(FreeStepCount: 9, GatedStepCount: 5,
            System.Array.Empty<RouteRequirement>(), FreeLine, GatedLine,
            RouteChoiceKind.BossRoom,
            BossRoom: new RoomKey(12, 2247),
            BossWaitPath: new[] { new RoomKey(1, 1), new RoomKey(1, 2), new RoomKey(1, 3) },
            BossRoomLabel: "rakshasha sage's room (12/2247)");

        var vm = new RouteChoiceDialogViewModel(choice, "Secret Library (12/2248)", id => "");

        Assert.True(vm.IsBossRoomChoice);
        Assert.StartsWith("Walk around rakshasha sage's room (12/2247)", vm.FreeSummary);
        Assert.StartsWith("Walk up to rakshasha sage's room (12/2247) and wait — 2 steps", vm.GatedSummary);
        Assert.Contains("Press Play", vm.GatedDetail);
        Assert.True(vm.ShowSendItCard);
        Assert.StartsWith("Walk through rakshasha sage's room (12/2247) without stopping", vm.SendItSummary);
        Assert.Contains("stop-before mark stays set", vm.SendItDetail);
        Assert.Equal(RouteChoiceResult.Gated, vm.SelectedRoute);
    }

    // ----- Trap-avoid fork -----------------------------------------------

    private static RouteChoice TrapAvoidChoice(int freeTraps = 0, int gatedTraps = 2) =>
        new(FreeStepCount: 3, GatedStepCount: 1,
            System.Array.Empty<RouteRequirement>(), FreeLine, GatedLine,
            RouteChoiceKind.TrapAvoid,
            FreeTrapCount: freeTraps, GatedTrapCount: gatedTraps);

    [Fact]
    public void TrapAvoid_PreSelectsFreeRoute_HidesSendIt_ShowsTrapCaveat()
    {
        var vm = new RouteChoiceDialogViewModel(TrapAvoidChoice(), "Deep Cavern (1/9)", id => "");

        Assert.True(vm.IsTrapAvoidChoice);
        Assert.False(vm.ShowSendItCard);                         // plain two-way fork
        Assert.Equal(RouteChoiceResult.Free, vm.SelectedRoute);  // safer route pre-selected
        Assert.True(vm.IsFreeSelected);
        Assert.True(vm.GoCommand.CanExecute(null));              // Go enabled on open
        Assert.Equal(vm.TrapCaveat, vm.GatedDetail);            // gated sub-line = trap caveat
        Assert.Contains("trap-free", vm.FreeSummary);           // 0 free traps → "trap-free"
        Assert.Contains("2 traps", vm.GatedSummary);            // shortest crosses 2
    }

    [Fact]
    public void TrapAvoid_FewerButNotZeroTraps_StatesBothCounts()
    {
        // The fewest-traps route still crosses an unavoidable trap — it must NOT claim
        // "trap-free", and both counts show.
        var vm = new RouteChoiceDialogViewModel(
            TrapAvoidChoice(freeTraps: 1, gatedTraps: 3), "Deep Cavern (1/9)", id => "");

        Assert.Contains("1 trap", vm.FreeSummary);
        Assert.DoesNotContain("trap-free", vm.FreeSummary);
        Assert.Contains("3 traps", vm.GatedSummary);
    }

    [Fact]
    public void TrapAvoid_RaiseSelectionPreview_FiresFreePreview()
    {
        var vm = new RouteChoiceDialogViewModel(TrapAvoidChoice(), "Deep Cavern (1/9)", id => "");
        RouteChoiceResult? previewed = null;
        bool fired = false;
        vm.PreviewRequested += r => { previewed = r; fired = true; };

        vm.RaiseSelectionPreview();

        Assert.True(fired);
        Assert.Equal(RouteChoiceResult.Free, previewed);
    }

    // A sole SURVIVABLE-damage hazard with no sourceable counter (the Silver River
    // with no reachable raft): the only card is "cross unprotected — take the
    // damage"; the obtain/route card is hidden (nothing to obtain).
    [Fact]
    public void SoleHazard_Survivable_NoCounter_OffersCrossUnprotectedOnly()
    {
        var choice = SoleChoice(new RouteRequirement(RouteRequirementKind.HazardProtection, new[] { 690, 691 }));

        var vm = new RouteChoiceDialogViewModel(
            choice, "A Silvery Stream (1/2409)", id => "a raft",
            hazardCounterSource: null, hazardSurvivable: true);

        Assert.True(vm.ShowSendItCard);
        Assert.False(vm.ShowGatedCard);
        Assert.Contains("Cross unprotected", vm.SendItSummary);
        Assert.False(vm.HazardObtain);
    }

    // A sole survivable hazard WITH a sourceable counter offers both: "obtain, then
    // cross" and "cross unprotected".
    [Fact]
    public void SoleHazard_Survivable_WithCounter_OffersObtainAndCrossUnprotected()
    {
        var choice = SoleChoice(new RouteRequirement(RouteRequirementKind.HazardProtection, new[] { 690, 691 }));

        var vm = new RouteChoiceDialogViewModel(
            choice, "A Silvery Stream (1/2409)", id => "a raft",
            hazardCounterSource: "buy at Pier", hazardSurvivable: true);

        Assert.True(vm.HazardObtain);
        Assert.True(vm.ShowGatedCard);
        Assert.True(vm.ShowSendItCard);
        Assert.Contains("Obtain, then cross", vm.GatedSummary);
        Assert.Contains("Cross unprotected", vm.SendItSummary);
    }

    // The resolved counter is named with how it'll be obtained, and the rest of the
    // any-of set follows, so a player already carrying a canoe sees it would do.
    [Fact]
    public void ResolvedHazardCounter_NamesTheAlternatives()
    {
        var req = new RouteRequirement(RouteRequirementKind.HazardProtection, new[] { 690, 691, 1181, 3609 });
        var names = new Dictionary<int, string>
        {
            [690] = "log raft", [691] = "wooden skiff", [1181] = "silverbark canoe", [3609] = "river punt",
        };

        var vm = new RouteChoiceDialogViewModel(
            SoleChoice(req), "A Silvery Stream (1/2409)", id => names[id],
            hazardCounterSource: "buy at Pier", hazardSurvivable: true,
            resolvedHazardCounter: r => (690, "buy at Pier"));

        Assert.Equal(
            "Requires log raft (buy at Pier), or wooden skiff, silverbark canoe or river punt",
            vm.RequirementSummary);
    }

    // A route that crosses the river (any boat) and the lake (raft or skiff) only
    // accepts what works on both.
    [Fact]
    public void RouteSubstitutes_IntersectAcrossTheRoutesHazards()
    {
        var river = new RouteRequirement(RouteRequirementKind.HazardProtection, new[] { 690, 691, 1181, 3609 });
        var lake = new RouteRequirement(RouteRequirementKind.HazardProtection, new[] { 690, 691 });

        Assert.Equal(new[] { 690, 691, 1181, 3609 }, RouteChoicePrompt.RouteSubstitutes(new[] { river }, 690));
        Assert.Equal(new[] { 690, 691 }, RouteChoicePrompt.RouteSubstitutes(new[] { river, lake }, 690));
        Assert.Equal(new[] { 42 }, RouteChoicePrompt.RouteSubstitutes(new[] { river }, 42));
    }

    // A sole GRAVE hazard (a drown / freeze death) NEVER offers "cross unprotected",
    // even when a counter can be sourced — a counter is the only safe way past.
    [Fact]
    public void SoleHazard_Grave_NeverOffersCrossUnprotected()
    {
        var choice = SoleChoice(new RouteRequirement(RouteRequirementKind.HazardProtection, new[] { 55 }));

        var withCounter = new RouteChoiceDialogViewModel(
            choice, "Sunken Vault (1/9)", id => "a fish-helm",
            hazardCounterSource: "buy at Docks", hazardSurvivable: false);
        Assert.False(withCounter.ShowSendItCard);
        Assert.True(withCounter.ShowGatedCard);   // "obtain, then cross" still offered

        var noCounter = new RouteChoiceDialogViewModel(
            choice, "Sunken Vault (1/9)", id => "a fish-helm",
            hazardCounterSource: null, hazardSurvivable: false);
        Assert.False(noCounter.ShowSendItCard);
        Assert.True(noCounter.ShowGatedCard);      // "walk to the hazard and stop"
    }

    // The one card that crosses Crystal Lake's teleporting rooms: the walk to the
    // room that sends you on to the Bloodwood Weald, offered to a character with
    // the level and the boat. One card, saying plainly what it is; nothing to
    // obtain, nothing to search for, no "take the damage".
    [Fact]
    public void CrossingToTheRoomThatTeleportsOn_IsOneCard_ThatSaysWhatItIs()
    {
        var choice = SoleChoice(
            new RouteRequirement(RouteRequirementKind.HazardProtection, new[] { 690, 691 }) { NoProtection = true })
            with
            {
                UnprotectedRoomNames = new[] { "Crystal Lake (17/1448)", "Crystal Lake (17/1456)" },
                CrossingGoalSpell = "bloodwood weald temp",
            };

        var vm = new RouteChoiceDialogViewModel(
            choice, "Crystal Lake (17/1457)", id => id == 690 ? "log raft" : "wooden skiff",
            hazardCounterSource: null, hazardSurvivable: false);

        Assert.True(vm.ShowGatedCard);
        Assert.False(vm.HazardObtain);
        Assert.False(vm.ShowSearchCard);
        Assert.False(vm.ShowSendItCard);
        Assert.StartsWith("Cross the teleport rooms — ", vm.GatedSummary);
        Assert.Equal("No way round — the only way to this room is across rooms that teleport at random", vm.FreeSummary);
        Assert.Equal(
            "Crosses 2 teleporting rooms to reach the room whose spell (bloodwood weald temp) teleports you on: "
            + "Crystal Lake (17/1448), Crystal Lake (17/1456). You have what the crossing asks (the level, and "
            + "log raft or wooden skiff); each of those rooms can still move you, so the walk may be thrown off "
            + "its route. Nothing is fetched.",
            vm.RequirementSummary);
    }

    // A place only the lake reaches (Stock's White Forest) is walked to across the
    // teleporting rooms, and its card, when another gate on the way puts one up,
    // says how many rooms it crosses and which, with no room that "teleports you
    // on" to reach: the place is an ordinary one.
    [Fact]
    public void CrossingToAPlaceOnlyTheLakeReaches_NamesTheRooms_AndNoRoomThatTeleportsOn()
    {
        string[] rooms = Enumerable.Range(1, 8).Select(n => $"Crystal Lake (17/{n})").ToArray();
        var choice = SoleChoice(
            new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 5 }),
            new RouteRequirement(RouteRequirementKind.HazardProtection, new[] { 690, 691 }) { NoProtection = true })
            with { UnprotectedRoomNames = rooms };

        var vm = new RouteChoiceDialogViewModel(
            choice, "White Forest (17/1642)",
            id => id switch { 5 => "a rope", 690 => "log raft", _ => "wooden skiff" },
            hazardCounterSource: null, hazardSurvivable: false);

        Assert.Equal(
            "Requires a rope. Crosses 8 teleporting rooms: Crystal Lake (17/1), Crystal Lake (17/2), "
            + "Crystal Lake (17/3), Crystal Lake (17/4), Crystal Lake (17/5), Crystal Lake (17/6) and 2 more. "
            + "You have what the crossing asks (the level, and log raft or wooden skiff); each of those rooms can "
            + "still move you, so the walk may be thrown off its route. Nothing is fetched.",
            vm.RequirementSummary);
        Assert.DoesNotContain("teleports you on", vm.RequirementSummary);
    }

    // A MIXED route — a survivable hazard (raft) AND a hard gate past it (a door key)
    // — with no sourceable counter: base card "walk to the hazard and stop" + "cross
    // unprotected", and the heading names both the hazard and the gate.
    [Fact]
    public void MixedHazard_NoCounter_OffersWalkToEdgeAndCrossUnprotected()
    {
        var choice = SoleChoice(
            new RouteRequirement(RouteRequirementKind.HazardProtection, new[] { 690, 691 }),
            new RouteRequirement(RouteRequirementKind.DoorKey, new[] { 757 }));

        var vm = new RouteChoiceDialogViewModel(
            choice, "The Iceforge (3/632)",
            id => id == 757 ? "the dragon key" : "a raft",
            hazardCounterSource: null, hazardSurvivable: true);

        Assert.False(vm.HazardObtain);
        Assert.True(vm.ShowGatedCard);
        Assert.True(vm.ShowSendItCard);
        Assert.Contains("Walk to the hazard and stop", vm.GatedSummary);
        Assert.Contains("Cross unprotected", vm.SendItSummary);
        Assert.Contains("crosses a hazard, then a gate", vm.FreeSummary);
        Assert.Contains("the dragon key", vm.RequirementSummary);
    }

    // A mixed route WITH a sourceable counter: "obtain, then cross" + "cross
    // unprotected" — the obtain card is offered on a mixed route too.
    [Fact]
    public void MixedHazard_WithCounter_OffersObtainAndCrossUnprotected()
    {
        var choice = SoleChoice(
            new RouteRequirement(RouteRequirementKind.HazardProtection, new[] { 690, 691 }),
            new RouteRequirement(RouteRequirementKind.DoorKey, new[] { 757 }));

        var vm = new RouteChoiceDialogViewModel(
            choice, "The Iceforge (3/632)",
            id => id == 757 ? "the dragon key" : "a raft",
            hazardCounterSource: "buy at Pier", hazardSurvivable: true);

        Assert.True(vm.HazardObtain);
        Assert.True(vm.ShowGatedCard);
        Assert.True(vm.ShowSendItCard);
        Assert.Contains("Obtain, then cross", vm.GatedSummary);
        Assert.Contains("Cross unprotected", vm.SendItSummary);
    }

    // The idle path opens the picker in a "Calculating…" state (From/To heading, no
    // cards) while route planning runs off-thread, then calls Populate to fill the
    // cards. Pin that the calculating constructor hides the options with the right
    // heading, and that Populate reveals them with the choice's card content.
    [Fact]
    public void CalculatingConstructor_HidesOptions_ThenPopulateReveals()
    {
        var vm = new RouteChoiceDialogViewModel("The Iceforge (3/632)", "Pier (1/9)");

        Assert.True(vm.IsCalculating);
        Assert.False(vm.ShowOptions);
        Assert.Equal("From Pier (1/9) to The Iceforge (3/632)", vm.Heading);

        var choice = SoleChoice(
            new RouteRequirement(RouteRequirementKind.HazardProtection, new[] { 690 }));
        vm.Populate(
            choice, id => "a raft",
            hazardCounterSource: "buy at Pier", hazardSurvivable: true);

        Assert.False(vm.IsCalculating);
        Assert.True(vm.ShowOptions);
        // Heading is set by the constructor, not Populate — it must survive the fill.
        Assert.Equal("From Pier (1/9) to The Iceforge (3/632)", vm.Heading);
        Assert.Contains("Obtain, then cross", vm.GatedSummary);
        Assert.Contains("Cross unprotected", vm.SendItSummary);
    }

    // Regression: a Blocked choice returns early inside Populate. The idle path can
    // route Blocked through Populate onto a pre-opened calc-state VM, so that early
    // return must still leave the calculating state — otherwise the picker sticks on
    // "Calculating…" and the "run to the block" card never appears (Go stays disabled).
    [Fact]
    public void Blocked_Populate_LeavesCalculatingState()
    {
        var vm = new RouteChoiceDialogViewModel("Dest (1/9)", "Src (1/1)");
        Assert.True(vm.IsCalculating);

        var blocked = Choice() with
        {
            Kind = RouteChoiceKind.Blocked,
            BlockedReason = "a locked door",
        };
        vm.Populate(blocked, id => null);

        Assert.False(vm.IsCalculating);
        Assert.True(vm.ShowOptions);
        Assert.Contains("Run to the blocked room", vm.GatedSummary);
    }

    // The reason IS computed but the blocked card had no place to show it — a
    // blocked route has no FreeCard. It must surface as the gated card's subtext
    // (report paradigm-20260914-201123: "run to the blocked room anyway" with no
    // clue what was blocking).
    [Fact]
    public void Blocked_SurfacesTheReason_AsTheGatedCardSubtext()
    {
        var vm = new RouteChoiceDialogViewModel("Dest (12/2371)", "Src (1/1678)");

        var blocked = Choice() with
        {
            Kind = RouteChoiceKind.Blocked,
            BlockedReason = "a level gate Teleport from 1/2681 (Redstone Tunnel, Dead-End) (Level 40+)",
        };
        vm.Populate(blocked, id => null);

        Assert.True(vm.IsBlockedChoice);
        Assert.Contains("Level 40+", vm.GatedDetail);
        Assert.Contains("level gate", vm.GatedDetail);
    }

    private static string? RouteItem(int id) => id switch
    {
        807 => "bloodstone orb",
        815 => "amber talisman",
        _ => null,
    };

    // The shortcut card named only the item that makes it a shortcut. Its route
    // still crosses the orb gate the long route does, and has to say so (reports
    // paradigm-20261008-173911, paradigm-20261008-174023).
    [Fact]
    public void ShortcutCard_ListsEverythingItsRouteNeeds()
    {
        RouteRequirement orb = new(RouteRequirementKind.CarryItem, new[] { 807 });
        RouteRequirement talisman = new(RouteRequirementKind.CarryItem, new[] { 815 });
        RouteChoice choice = SoleChoice(orb) with
        {
            GatedStepCount = 246,
            ShortcutPath = GatedLine,
            ShortcutStepCount = 156,
            ShortcutItems = new[] { 815 },
            ShortcutRequirements = new[] { talisman, orb },
        };

        var vm = new RouteChoiceDialogViewModel(
            choice, "Dest (1/9)", RouteItem,
            giveNameForItem: id => id == 807 ? "gnome commander" : null);

        Assert.Equal("Requires bloodstone orb (ask gnome commander)", vm.RequirementSummary);
        Assert.StartsWith("Shortcut via amber talisman — saves 90 rooms", vm.ShortcutSummary);
        // Items only: the main card's walk asks the gnome commander, this card's
        // walk fetches nothing but the talisman, so it promises no source.
        Assert.StartsWith("Requires amber talisman; bloodstone orb. ", vm.ShortcutDetail);
    }

    // A card's walk takes the teleport its route takes, so the card names it.
    [Fact]
    public void ACardWhoseRouteTeleports_SaysWhereTo()
    {
        RouteChoice choice = Choice() with { Kind = RouteChoiceKind.AvoidOverride, AvoidedRoomCount = 1 };

        var vm = new RouteChoiceDialogViewModel(
            choice, "Dest (1/9)", RouteItem,
            teleportsOn: r => r == RouteChoiceResult.Gated ? "the teleport to Stone Tunnel, Hole Up (2/1306)" : null);

        Assert.EndsWith(" — takes the teleport to Stone Tunnel, Hole Up (2/1306)", vm.GatedSummary);
        Assert.DoesNotContain("teleport", vm.FreeSummary);
    }

    // "Walk to the hazard and stop" and "walk to the shop and stop" walk somewhere
    // else, on foot, so the teleport on the route drawn is not theirs to announce.
    // Nor is a blocked route's "run to the block".
    [Fact]
    public void ACardThatStopsShort_WalksNoneOfTheRoutesDrawn()
    {
        RouteChoice choice = Choice();

        Assert.Same(GatedLine, RouteChoicePrompt.RouteACardWalks(choice, RouteChoiceResult.Gated, gatedStopsShort: false));
        Assert.Null(RouteChoicePrompt.RouteACardWalks(choice, RouteChoiceResult.Gated, gatedStopsShort: true));
        // The other cards of that route still cross it whole.
        Assert.Same(GatedLine, RouteChoicePrompt.RouteACardWalks(choice, RouteChoiceResult.GatedNoAcquire, gatedStopsShort: true));
        Assert.Same(FreeLine, RouteChoicePrompt.RouteACardWalks(choice, RouteChoiceResult.Free, gatedStopsShort: true));

        RouteChoice blocked = choice with { Kind = RouteChoiceKind.Blocked };
        Assert.Null(RouteChoicePrompt.RouteACardWalks(blocked, RouteChoiceResult.Gated, gatedStopsShort: false));
    }

    // The walk-or-teleport cards are already about the teleport.
    [Fact]
    public void TheTeleportFork_GetsNoSecondTeleportNote()
    {
        RouteChoice choice = Choice() with { Kind = RouteChoiceKind.Teleport, TeleportLanding = "Vault (1/9)" };

        var vm = new RouteChoiceDialogViewModel(
            choice, "Dest (1/9)", RouteItem, teleportsOn: _ => "the teleport to Vault (1/9)");

        Assert.DoesNotContain("takes the teleport", vm.GatedSummary);
    }

    // Report stock-20261128-014442: the cards never showed a sys goto shortcut, and a
    // picked route was walked with one swapped in. The jump is a card of its own,
    // previewed and picked like any other; the walk on from the landing is drawn.
    private static SysGotoRouteCard RhudaurJump() => new(
        new MudPlay.Models.Profile.SysopGotoLocation { Name = "rhudaur", Map = 2, Room = 2519 },
        new RoomKey(2, 2519),
        new[] { new RoomKey(2, 2519), new RoomKey(2, 2520), new RoomKey(1, 9) },
        LandingLabel: "Rhudaur, Massive Doors (2/2519)");

    [Fact]
    public void ASysGotoShortcut_IsACardOfItsOwn()
    {
        RouteChoice choice = Choice() with { SysGoto = RhudaurJump() };
        var vm = new RouteChoiceDialogViewModel(choice, "Dest (1/9)", RouteItem);
        List<RouteChoiceResult?> previews = new();
        RouteChoiceResult? closed = null;
        vm.PreviewRequested += previews.Add;
        vm.CloseRequested += r => closed = r;

        Assert.True(vm.ShowSysGotoCard);
        Assert.StartsWith("Sys goto rhudaur — 3 steps", vm.SysGotoSummary);
        Assert.Contains("Jumps to Rhudaur, Massive Doors (2/2519)", vm.SysGotoDetail);
        Assert.Contains("then walks the rest (2 steps)", vm.SysGotoDetail);

        vm.SelectSysGotoCommand.Execute(null);
        Assert.True(vm.IsSysGotoSelected);
        Assert.Equal(new RouteChoiceResult?[] { RouteChoiceResult.SysGoto }, previews);
        vm.GoCommand.Execute(null);
        Assert.Equal(RouteChoiceResult.SysGoto, closed);

        Assert.False(new RouteChoiceDialogViewModel(Choice(), "Dest (1/9)", RouteItem).ShowSysGotoCard);
    }

    // A jump can get past a block, so the blocked card carries it too, and its route
    // is the walk on from the landing on any fork.
    [Fact]
    public void ABlockedRoute_StillOffersTheJump()
    {
        RouteChoice blocked = Choice() with { Kind = RouteChoiceKind.Blocked, SysGoto = RhudaurJump() };

        Assert.True(new RouteChoiceDialogViewModel(blocked, "Dest (1/9)", RouteItem).ShowSysGotoCard);
        Assert.Same(blocked.SysGoto!.Path, RouteChoicePrompt.RouteACardWalks(blocked, RouteChoiceResult.SysGoto, gatedStopsShort: false));
    }
}
