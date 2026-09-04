using System.Linq;
using System.Threading.Tasks;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.ViewModels.Navigation;

// Shared entry point for user-initiated walks that should offer a route choice.
// Automated walks (event scripts, death recovery, loops, deposits, party
// comeback, trainer routing) bypass this and call Walker.WalkTo directly — they
// default to the free-preferring, teleport-allowed route with no prompt.
//
// The flow: resolve the current room, then check two forks in priority order.
// First the walk-vs-teleport fork — a shorter route that teleports where a
// walking route also exists — because a teleport can drop the crosser somewhere
// lethal only the user's character knowledge can judge. Failing that, the
// free-vs-direct item-gate fork — a shorter route that crosses an acquirable
// gate. Neither fork → plain walk. The picker's answer commits the chosen route;
// cancel walks nothing.
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

        // Walk-vs-teleport fork takes precedence over the item-gate fork: if the
        // shortest route teleports and a pure-walking route also exists, let the
        // user weigh the teleport's shortcut against its danger. A teleport can
        // drop the crosser somewhere lethal (a damaging plane, water with no
        // boat), survivable or not depending on the character — a call the client
        // can't make, so we surface it rather than silently taking the shortcut.
        RouteChoice? teleport = RouteChoicePlanner.EvaluateTeleport(
            services.Bfs, services.Movement, services.RoomGraph, source.Key, destination);
        if (teleport is not null)
        {
            await RunPickerAsync(services, destination, source.Key, teleport, previewSink);
            return;
        }

        // Trap-avoid fork: the shortest route crosses a trap and a trap-free route to
        // the same room exists. Surface the clean detour so the user can take it
        // instead of trusting the step-time disarm — a disarm can fail (no picks, no
        // party disarmer) and some traps aren't worth the hit even when disarmable.
        RouteChoice? trapAvoid = RouteChoicePlanner.EvaluateTrapAvoid(
            services.Bfs, services.Movement, services.RoomGraph, source.Key, destination);
        if (trapAvoid is not null)
        {
            await RunPickerAsync(services, destination, source.Key, trapAvoid, previewSink);
            return;
        }

        RouteChoice? choice = RouteChoicePlanner.Evaluate(
            services.Bfs, services.Movement, services.RoomGraph, source.Key, destination);
        if (choice is null)
        {
            // No shorter gated route. If instead the route is fully blocked but the
            // destination is physically reachable up to an obstacle, offer to run to
            // the blocked room; otherwise walk the free route (which reports its own
            // failure if it can't).
            if (RouteChoicePlanner.PlanBlocked(
                    services.Bfs, services.Movement, services.RoomGraph, source.Key, destination)
                is { } blocked)
            {
                await RunPickerAsync(services, destination, source.Key,
                    BuildBlockedChoice(services, source.Key, destination, blocked), previewSink);
                return;
            }
            CommitWalk(services, destination, gated: false);
            return;
        }

        // Sole route (no gate-free alternative) whose gates are item/ticket/key,
        // not a hazard. When every gate is a single item/ticket the user flagged
        // AutoObtainForPath, the acquisition pipeline can source it all: arm and
        // cross, no prompt. Otherwise a gate the client can't auto-source is on the
        // route — a door key (never auto-sourced), or an unflagged item — so
        // surface the picker rather than silently walking a plain route that fails
        // in place. The user SEES the requirement and chooses: Go walks the route
        // and halts at the gate so they clear it by hand (open the door, summon /
        // kill for the key), Cancel walks nothing. Any auto-sourceable item on the
        // same route is still fetched en route (Go arms acquisition), leaving only
        // the manual gate. Hazard-only sole routes skip this and fall through to the
        // picker below (carry / buy / use a counter).
        if (!choice.HasFreeRoute
            && choice.Requirements.Any(r => r.Kind != RouteRequirementKind.HazardProtection))
        {
            if (services.ShouldAutoObtainSoleRoute(choice.Requirements))
            {
                CommitWalk(services, destination, gated: true);
                return;
            }
            await RunPickerAsync(services, destination, source.Key, choice, previewSink);
            return;
        }

        await RunPickerAsync(services, destination, source.Key, choice, previewSink);
    }

    // Build the picker, draw the previewed route while it's open, and commit the
    // chosen route. Shared by the item-gate and teleport forks — the commit
    // branches on the choice kind: an item-gate choice picks free / acquire / send
    // it, a teleport choice picks walk (refuse teleports) / teleport (allow them).
    private static async Task RunPickerAsync(
        AppServices services,
        RoomKey destination,
        RoomKey source,
        RouteChoice choice,
        Action<IReadOnlyList<RoomKey>?>? previewSink)
    {
        // Approximate arrival ETA for each route — realm-aware per-hop travel plus
        // a lair-fight dwell for each lair the walker steps into when auto-combat is
        // on, the same estimate the live walk-status label surfaces. Empty free
        // path (sole route) estimates to zero, so the picker shows a bare step count
        // there.
        TimeSpan freeEta = RouteEtaEstimator.Estimate(
            choice.FreePath, services.AutoLair.TravelCostModel,
            services.RoomGraph.GetRoom, includeLairDwell: services.IsAutoCombatEnabled);
        TimeSpan gatedEta = RouteEtaEstimator.Estimate(
            choice.GatedPath, services.AutoLair.TravelCostModel,
            services.RoomGraph.GetRoom, includeLairDwell: services.IsAutoCombatEnabled);

        // A route that crosses a SURVIVABLE hazard the player can't currently pass —
        // whether the hazard is the only gate (sole hazard) or the route ALSO has a
        // hard gate past it (a keyed door: a "mixed" route). Either way the picker
        // offers to obtain the counter / cross unprotected; the mixed case just also
        // names the hard gate it stops at. Never for a grave hazard (a drown / freeze
        // death, a forced teleport) — UnprotectedHazardsAllSurvivable gates that.
        bool crossesHazard = !choice.HasFreeRoute && choice.Kind != RouteChoiceKind.Teleport
            && choice.Requirements.Any(r => r.Kind == RouteRequirementKind.HazardProtection);
        bool crossesSurvivableHazard = crossesHazard
            && services.UnprotectedHazardsAllSurvivable(choice.GatedPath);
        bool mixedHazard = crossesSurvivableHazard
            && choice.Requirements.Any(r => r.Kind != RouteRequirementKind.HazardProtection);

        // Resolve which counter the run would obtain and how (floor grab / free give /
        // shop buy / drop hunt), so the picker can offer "obtain then cross" and Go
        // forces that counter through the acquire pipeline — an explicit pick, so no
        // AutoObtainForPath flag is needed. Run for ANY hazard (survivable OR grave —
        // a grave hazard's only safe crossing IS obtaining the counter); only the
        // HAZARD requirements are sourced this way — a hard gate (a door key) is never
        // auto-fetched.
        List<int> floorCounters = new();     // grabbed in place with a `get`
        List<int> detourCounters = new();    // sourced via the give/shop/drop pipeline
        List<string> hazardSources = new();
        // The specific counter resolved per hazard requirement (which item, and how) —
        // so the picker's requirement line names the exact one it'll obtain ("log raft
        // (buy at Pier)") instead of the whole any-of list.
        Dictionary<RouteRequirement, (int ItemId, string Source)> resolvedCounters = new();
        if (crossesHazard)
            foreach (RouteRequirement req in choice.Requirements)
            {
                if (req.Kind != RouteRequirementKind.HazardProtection) continue;
                // A counter already chosen for an earlier hazard that also appears in
                // THIS hazard's any-of set covers it too — both FCCO slide rooms accept
                // rope-and-grapple OR climbing harness, so one rope answers both. Skip
                // the redundant second source rather than buying a rope AND a harness.
                if (req.ItemIds.Any(id => floorCounters.Contains(id) || detourCounters.Contains(id)))
                    continue;
                if (services.ResolveHazardCounter(req.ItemIds, source, destination) is { } r)
                {
                    List<int> bucket = r.OnFloor ? floorCounters : detourCounters;
                    if (!bucket.Contains(r.ItemId)) bucket.Add(r.ItemId);
                    if (!hazardSources.Contains(r.Source)) hazardSources.Add(r.Source);
                    resolvedCounters[req] = (r.ItemId, r.Source);
                }
            }
        string? hazardCounterSource = hazardSources.Count > 0
            ? string.Join("; ", hazardSources) : null;
        bool hazardObtain = hazardCounterSource is not null;

        // For a mixed route with no sourceable counter, the base card walks to the
        // hazard's edge and stops (the user then fetches a counter / clears the hard
        // gate themselves), rather than crossing the hazard blindly.
        RoomKey? hazardEdge = mixedHazard && !hazardObtain
            ? services.HazardApproachRoom(choice.GatedPath)
            : null;

        var vm = new RouteChoiceDialogViewModel(
            choice,
            DestinationLabel(services, destination),
            services.ItemNames.GetName,
            // Name the NPC / room the run would ask for a free deterministic give,
            // when one exists. This preempts the shop and drop tails (the give
            // router stands both down), so the "ask X" tail matches the run.
            itemId => services.PathItemGiveName(itemId, source, destination),
            // No free give but a shop stocks the gate item and it's flagged
            // buy-if-needed: name the shop the run would detour to buy at. Resolved
            // from this walk's source/destination so the "buy at X" tail matches
            // the actual detour.
            itemId => services.PathItemShopName(itemId, source, destination),
            // No give or shop but a flagged monster drops it: name the lair the
            // run would reroute to hunt, so the picker previews the hunt option
            // (which otherwise only surfaces as a prompt once the walk starts).
            itemId => services.PathItemDropName(itemId, source),
            freeEta,
            gatedEta,
            hazardCounterSource,
            crossesSurvivableHazard,
            // The specific counter the run resolved for a hazard requirement (item id +
            // "buy at Pier" / "ask X" / …), so its requirement clause names that one
            // rather than the whole any-of set.
            req => resolvedCounters.TryGetValue(req, out (int ItemId, string Source) v)
                ? v : ((int, string)?)null);

        // Draw the selected route's line while the picker is open; clear it when
        // the picker closes so a committed walk's live path isn't double-drawn and
        // a cancel leaves no stale preview behind.
        if (previewSink is not null)
        {
            vm.PreviewRequested += r => previewSink(r switch
            {
                RouteChoiceResult.Free => choice.FreePath,
                // Both direct choices trace the same physical gated line.
                RouteChoiceResult.Gated => choice.GatedPath,
                RouteChoiceResult.GatedNoAcquire => choice.GatedPath,
                _ => null,
            });
            // A pre-selected route (trap-avoid defaults to the trap-free line) draws
            // its preview on open, now that the sink is subscribed.
            vm.RaiseSelectionPreview();
        }

        // Details… opens the shared route-details browse window for the selected
        // route's polyline — the same window the CURRENT NAV panel uses, with each
        // room's monsters, hazards, and item gates linked to their records. Both
        // direct choices trace the same physical gated line.
        string detailsTitle = $"Route → {DestinationLabel(services, destination)}";
        vm.ShowDetailsRequested += r => RouteDetailsLauncher.Open(
            services, detailsTitle,
            r == RouteChoiceResult.Free ? choice.FreePath : choice.GatedPath);

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

        if (choice.Kind == RouteChoiceKind.Blocked)
        {
            // Go = "run to the blocked room anyway": a plain walk to the furthest
            // reachable room, landing the walker adjacent to the obstacle. Cancel /
            // any other result walks nothing.
            if (result == RouteChoiceResult.Gated && choice.StopRoom is { } stop)
                CommitWalk(services, stop, gated: false);
            return;
        }

        if (choice.Kind == RouteChoiceKind.Teleport)
        {
            switch (result)
            {
                case RouteChoiceResult.Free:
                    // "Walk it" — refuse the teleport shortcut, plan the safe route.
                    CommitWalk(services, destination, gated: false, avoidTeleports: true);
                    break;
                case RouteChoiceResult.Gated:
                    // "Teleport" — allow the shortcut, the walker's default.
                    CommitWalk(services, destination, gated: false);
                    break;
                // null → cancelled: walk nothing.
            }
            return;
        }

        if (choice.Kind == RouteChoiceKind.TrapAvoid)
        {
            switch (result)
            {
                case RouteChoiceResult.Free:
                    // "Avoid traps" — plan the trap-free route (refuse trapped exits).
                    CommitWalk(services, destination, gated: false, avoidTraps: true);
                    break;
                case RouteChoiceResult.Gated:
                    // "Cross traps" — the walker's default plan, disarming at step time.
                    CommitWalk(services, destination, gated: false);
                    break;
                // null → cancelled: walk nothing.
            }
            return;
        }

        switch (result)
        {
            case RouteChoiceResult.Free:
                CommitWalk(services, destination, gated: false);
                break;
            case RouteChoiceResult.Gated when hazardEdge is { } edge:
                // Mixed route, no sourceable counter: the base card walks to the
                // hazard's edge and stops (a plain gate-free walk to the room just
                // short of the river), so the user can fetch a counter / clear the
                // hard gate by hand from there — rather than crossing blindly.
                CommitWalk(services, edge, gated: false);
                break;
            case RouteChoiceResult.Gated:
                // Hazard "obtain then cross". A counter already on the floor is
                // grabbed in place; the rest are forced through the acquire pipeline
                // (give/shop/drop) even when unflagged — the explicit pick is the
                // consent. The walk still plans gated (the counter isn't carried
                // yet at plan time) and crosses safely once it's in hand. A SOLE
                // route (the gate is unavoidable) also plans avoidTraps, so the
                // forced crossing takes the fewest-traps approach the planner chose.
                foreach (int id in floorCounters)
                    if (services.ItemNames.GetName(id) is { Length: > 0 } n)
                        services.SendGameCommand($"get {n}");
                if (detourCounters.Count > 0)
                    services.ForcePathObtain(detourCounters);
                CommitWalk(services, destination, gated: true, avoidTraps: !choice.HasFreeRoute);
                break;
            case RouteChoiceResult.GatedNoAcquire:
                // "Send it": walk the gated route but don't arm acquisition — the
                // user asserts they'll clear the gates without provisioning. A sole
                // route still avoids traps, matching the planner's chosen approach.
                CommitWalk(services, destination, gated: true,
                    armAcquisition: false, avoidTraps: !choice.HasFreeRoute);
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
        AppServices services, RoomKey destination, bool gated,
        bool armAcquisition = true, bool avoidTeleports = false, bool avoidTraps = false)
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
            destination,
            planThroughAcquirableGates: gated,
            armItemAcquisition: armAcquisition,
            avoidTeleports: avoidTeleports,
            avoidTraps: avoidTraps);
    }

    private static string DestinationLabel(AppServices services, RoomKey destination) =>
        services.RoomGraph.GetRoom(destination)?.Name is { Length: > 0 } name
            ? $"{name} ({destination})"
            : destination.ToString();

    // Turn a "run to the blocked room" plan into a Blocked RouteChoice: the
    // reachable prefix as the previewed path, and the obstacle named via the shared
    // describer (which room, which way, the key / picklocks-strength it needs) so
    // the picker states exactly what's in the way.
    private static RouteChoice BuildBlockedChoice(
        AppServices services, RoomKey source, RoomKey destination, BlockedRoutePlan plan)
    {
        string reason = BlockedExitDescriber.Describe(
            plan.StopRoom, plan.BlockDir, plan.BlockExit,
            key => services.RoomGraph.GetRoom(key)?.Name,
            services.ItemNames.GetName);
        return new RouteChoice(
            FreeStepCount: 0,
            GatedStepCount: plan.Preview.Count > 0 ? plan.Preview.Count - 1 : 0,
            Requirements: Array.Empty<RouteRequirement>(),
            FreePath: Array.Empty<RoomKey>(),
            GatedPath: plan.Preview,
            Kind: RouteChoiceKind.Blocked,
            StopRoom: plan.StopRoom,
            BlockedReason: reason);
    }
}
