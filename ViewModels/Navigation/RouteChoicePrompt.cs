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
    public static async Task WalkAsync(AppServices services, RoomKey destination)
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

        var vm = new RouteChoiceDialogViewModel(
            choice,
            DestinationLabel(services, destination),
            services.ItemNames.GetName,
            // Name the shop the run would detour to buy a gate item, when it will
            // (item flagged buy-if-needed + a reachable shop stocks it). Resolved
            // from this walk's source/destination so the "buy at X" tail matches
            // the actual detour.
            itemId => services.PathItemShopName(itemId, source.Key, destination));
        RouteChoiceResult? result = await services.Dialogs
            .OpenWindowAsync<RouteChoiceDialogViewModel, RouteChoiceResult?>(vm);

        switch (result)
        {
            case RouteChoiceResult.Free:
                CommitWalk(services, destination, gated: false);
                break;
            case RouteChoiceResult.Gated:
                CommitWalk(services, destination, gated: true);
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
    private static void CommitWalk(AppServices services, RoomKey destination, bool gated)
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
        services.Walker.WalkTo(destination, planThroughAcquirableGates: gated);
    }

    private static string DestinationLabel(AppServices services, RoomKey destination) =>
        services.RoomGraph.GetRoom(destination)?.Name is { Length: > 0 } name
            ? $"{name} ({destination})"
            : destination.ToString();
}
