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
            services.Walker.WalkTo(destination);
            return;
        }

        RouteChoice? choice = RouteChoicePlanner.Evaluate(
            services.Bfs, services.Movement, services.RoomGraph, source.Key, destination);
        if (choice is null)
        {
            // No shorter gated route (or it needs nothing acquirable) — just walk
            // the free-preferring route.
            services.Walker.WalkTo(destination);
            return;
        }

        var vm = new RouteChoiceDialogViewModel(choice, DestinationLabel(services, destination), services.ItemNames.GetName);
        RouteChoiceResult? result = await services.Dialogs
            .OpenWindowAsync<RouteChoiceDialogViewModel, RouteChoiceResult?>(vm);

        switch (result)
        {
            case RouteChoiceResult.Free:
                services.Walker.WalkTo(destination);
                break;
            case RouteChoiceResult.Gated:
                services.Walker.WalkTo(destination, planThroughAcquirableGates: true);
                break;
            // null → cancelled: walk nothing.
        }
    }

    private static string DestinationLabel(AppServices services, RoomKey destination) =>
        services.RoomGraph.GetRoom(destination)?.Name is { Length: > 0 } name
            ? $"{name} ({destination})"
            : destination.ToString();
}
