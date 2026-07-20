using System.Linq;
using System.Threading.Tasks;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Navigation;

// Shared entry point for user-initiated walks that should offer a free-vs-direct
// route choice. Automated walks (event scripts, death recovery, loops, deposits,
// party comeback, trainer routing) bypass this and call Walker.WalkTo directly —
// they default to the free-preferring route with no prompt.
//
// The flow: resolve the current room, ask RouteChoicePlanner whether a shorter
// gated route exists, and only pop the picker when it does. No fork → plain walk.
// The picker's answer selects the free route or the gated route (which arms the
// item-acquisition pipeline for anything missing); cancel walks nothing.
public static class RouteChoicePrompt
{
    // previewSink: optional map-preview channel. When the user selects a route in
    // the picker (before committing), it's called with that route's RoomKey line
    // so the caller can draw it; called with null when the picker closes (the
    // committed walk then draws its own live path). Callers without a map (e.g.
    // the navigation-manager list) pass none and the picker just works Go-only.
    public static async Task WalkAsync(
        AppServices services,
        RoomKey destination,
        Action<IReadOnlyList<RoomKey>?>? previewSink = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        Room? source = services.RoomTracker.State.CurrentRoom;
        if (source is null)
        {
            // No confident source room — let the walker plan and report the
            // "no known source" failure itself rather than second-guessing here.
            CommitWalk(services, destination, gated: false);
            return;
        }

        RouteChoice? choice = RouteChoicePlanner.Evaluate(
            services.Bfs, services.Movement, services.RoomGraph, source.Key, destination);
        if (choice is null)
        {
            // No shorter gated route (or it needs nothing acquirable) — just walk
            // the free-preferring route.
            CommitWalk(services, destination, gated: false);
            return;
        }

        // Sole route (no gate-free alternative) whose gates are item/ticket, not a
        // hazard: no picker — the item's AutoObtainForPath flag decides. Flagged
        // arms the acquisition pipeline and crosses the gate; unflagged walks the
        // plain route, whose BFS fails in place naming the missing item. Hazard
        // sole routes fall through to the picker (carry / buy / use a counter).
        if (!choice.HasFreeRoute
            && choice.Requirements.Any(r => r.Kind != RouteRequirementKind.HazardProtection))
        {
            bool arm = services.ShouldAutoObtainSoleRoute(choice.Requirements);
            CommitWalk(services, destination, gated: arm);
            return;
        }

        var vm = new RouteChoiceDialogViewModel(
            choice,
            DestinationLabel(services, destination),
            services.ItemNames.GetName,
            // Name the shop the run would detour to buy a gate item, when it will
            // (item flagged buy-if-needed + a reachable shop stocks it). Resolved
            // from this walk's source/destination so the "buy at X" tail matches
            // the actual detour.
            itemId => services.PathItemShopName(itemId, source.Key, destination),
            // No shop sells it but a flagged monster drops it: name the lair the
            // run would reroute to hunt, so the picker previews the hunt option
            // (which otherwise only surfaces as a prompt once the walk starts).
            itemId => services.PathItemDropName(itemId, source.Key));

        // Draw the selected route's line while the picker is open; clear it when
        // the picker closes so a committed walk's live path isn't double-drawn and
        // a cancel leaves no stale preview behind.
        if (previewSink is not null)
            vm.PreviewRequested += r => previewSink(r switch
            {
                RouteChoiceResult.Free => choice.FreePath,
                // Both direct choices trace the same physical gated line.
                RouteChoiceResult.Gated => choice.GatedPath,
                RouteChoiceResult.GatedNoAcquire => choice.GatedPath,
                _ => null,
            });

        RouteChoiceResult? result;
        try
        {
            result = await services.Dialogs
                .OpenWindowAsync<RouteChoiceDialogViewModel, RouteChoiceResult?>(vm);
        }
        finally
        {
            previewSink?.Invoke(null);
        }

        switch (result)
        {
            case RouteChoiceResult.Free:
                CommitWalk(services, destination, gated: false);
                break;
            case RouteChoiceResult.Gated:
                CommitWalk(services, destination, gated: true);
                break;
            case RouteChoiceResult.GatedNoAcquire:
                // "Send it": walk the gated route but don't arm acquisition — the
                // user asserts they'll clear the gates without provisioning.
                CommitWalk(services, destination, gated: true, armAcquisition: false);
                break;
            // null → cancelled: walk nothing (and leave any manual pause intact —
            // the user backed out, so nothing changed).
        }
    }

    // Start the walk, first lifting any lingering manual pause. A user picking a
    // fresh destination is an explicit "go here now" that outranks a mid-walk
    // Pause: without clearing the UserGate the new walk would immediately re-pause
    // (AutoWalkManager.WalkToImmediate honours the coordinator's paused state), so
    // the destination changed but the walker stayed frozen. Engine waits (Combat /
    // rest / party) are left asserted and re-pause on their own if still relevant.
    private static void CommitWalk(
        AppServices services, RoomKey destination, bool gated, bool armAcquisition = true)
    {
        // Abandon a paused walk-in-progress BEFORE clearing the gate. Clearing
        // UserGate synchronously resumes a Paused walker (OnCoordinatorPauseChanged
        // → SendNextStep), which would fire one stale step toward the OLD
        // destination before we redirect. Stopping first leaves the walker Idle so
        // the gate clear has nothing to resume, and WalkTo plans the new route
        // cleanly.
        if (services.Walker.State == WalkState.Paused)
            services.Walker.Stop("superseded by new user walk-to");
        services.MovementCoordinator.ClearGate(
            MovementCoordinator.UserGate, nameof(RouteChoicePrompt));
        services.Walker.WalkTo(
            destination, planThroughAcquirableGates: gated, armItemAcquisition: armAcquisition);
    }

    private static string DestinationLabel(AppServices services, RoomKey destination) =>
        services.RoomGraph.GetRoom(destination)?.Name is { Length: > 0 } name
            ? $"{name} ({destination})"
            : destination.ToString();
}
