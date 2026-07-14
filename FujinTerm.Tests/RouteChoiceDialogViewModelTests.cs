using System.Collections.Generic;
using FujinTerm.Services;
using FujinTerm.ViewModels.Navigation;
using Xunit;

namespace FujinTerm.Tests;

// The picker's requirement line only promises "buy at <shop>" for the gate
// kinds a walk actually auto-buys — Item and Ticket. Keys and hazard counters
// never post a buy-triggering path-item need, so they must not carry the tail
// even when a shop resolver would name one. These pin that kind-gating and the
// no-resolver fallback.
public sealed class RouteChoiceDialogViewModelTests
{
    private static RouteChoice Choice(params RouteRequirement[] reqs) =>
        new(FreeStepCount: 5, GatedStepCount: 2, reqs);

    [Fact]
    public void CarryItemGate_WithShop_GetsBuyTail()
    {
        var choice = Choice(new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 5 }));

        var vm = new RouteChoiceDialogViewModel(
            choice, "Bank (1/9)", id => id == 5 ? "a raft" : null,
            id => id == 5 ? "General Store" : null);

        Assert.Equal("Requires a raft (buy at General Store)", vm.RequirementSummary);
    }

    [Fact]
    public void TicketGate_WithShop_GetsBuyTail()
    {
        var choice = Choice(new RouteRequirement(RouteRequirementKind.Ticket, new[] { 7 }));

        var vm = new RouteChoiceDialogViewModel(
            choice, "Docks (1/9)", id => id == 7 ? "a ferry ticket" : null,
            id => id == 7 ? "Ticket Booth" : null);

        Assert.Equal("Requires a ferry ticket (buy at Ticket Booth)", vm.RequirementSummary);
    }

    [Fact]
    public void DoorKeyGate_NeverGetsBuyTail_EvenIfShopResolves()
    {
        var choice = Choice(new RouteRequirement(RouteRequirementKind.DoorKey, new[] { 9 }));

        // A key is never bought on a path detour, so a resolver that names a shop
        // must be ignored for the DoorKey kind.
        var vm = new RouteChoiceDialogViewModel(
            choice, "Vault (1/9)", id => "the iron key", id => "Locksmith");

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
}
