using System.Linq;
using System.Threading.Tasks;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.ViewModels.Navigation;

// Shared entry point for user-initiated walks that should offer a route choice.
// Automated walks (event scripts, death recovery, loops, deposits, party
// comeback, trainer routing) bypass this and call Walker.WalkTo directly — they
// take the free-preferring route with no prompt, using only the teleports the
// user allowed automatic walks (Settings → Teleports).
//
// The flow: resolve the current room, then plan (PlanRouteChoice) — the forks in
// priority order are walk-vs-teleport, trap-avoid, avoid-override, then the
// free-vs-direct item-gate fork; failing all, a fully-blocked "run to the block"
// offer or a plain walk. A sole route whose gates are all auto-obtainable arms
// acquisition and walks with no prompt. Anything else surfaces the picker, whose
// answer commits the chosen route; cancel walks nothing.
//
// Planning runs off the UI thread when the walker is idle (so a big-graph plan
// doesn't freeze the app), and a slow plan surfaces a "Calculating…" window while
// it runs; see WalkAsync.
public static class RouteChoicePrompt
{
    // Program-log category for the route-pick decision trace. Nav lifecycle → Info
    // for the fork that actually fired (what the user sees), Debug for the plain
    // no-fork walk so an ordinary GOTO doesn't spam the log.
    private const string LogCat = "RoutePick";

    // The cards on screen. A change of game data to another set closes them: they
    // were planned on the last set's rooms, and a pick would walk to its numbers.
    // Touched on the UI thread only.
    private static readonly List<RouteChoiceDialogViewModel> OpenCards = new();

    private static Task<RouteChoiceResult?> OpenCard(AppServices services, RouteChoiceDialogViewModel card)
    {
        Task<RouteChoiceResult?> closed = services.Dialogs
            .OpenWindowAsync<RouteChoiceDialogViewModel, RouteChoiceResult?>(card);
        OpenCards.Add(card);
        _ = ForgetWhenClosed(card, closed);
        return closed;
    }

    private static async Task ForgetWhenClosed(RouteChoiceDialogViewModel card, Task<RouteChoiceResult?> closed)
    {
        try { await closed; }
        finally { OpenCards.Remove(card); }
    }

    // Close every card on screen without a pick, as its Cancel does.
    public static void CloseOpenCards()
    {
        foreach (RouteChoiceDialogViewModel card in OpenCards.ToArray()) card.Close();
    }

    // previewSink: optional map-preview channel. When the user selects a route in
    // the picker (before committing), it's called with that route's RoomKey line
    // so the caller can draw it; called with null when the picker closes (the
    // committed walk then draws its own live path). Callers without a map (e.g.
    // the navigation-manager list) pass none and the picker just works Go-only.
    // startMode sets the walk out with Auto-Combat off or in Sprint Mode — applied only
    // once a walk actually commits (a cancelled picker changes nothing); the picker's
    // own Run / Sprint buttons override it.
    // askOnlyOverAvoids: a walk the client starts on the player's behalf (a Sell Tour
    // stop) takes the default route without asking, as a no-fork walk does; the picker
    // shows its cards whenever a room the player marked Avoid is on the shortest route
    // (or there's no default route to take). Such a walk stays out of the goto history and never flashes the
    // "Calculating…" window.
    // remember: false keeps the destination out of Recent Destinations — the walk to
    // a loop room is on the way to the loop, not somewhere the user asked to go.
    // The result is false only when the user closed the cards without picking; a walk
    // that went out (and may yet fail) is true.
    public static async Task<bool> WalkAsync(
        AppServices services,
        RoomKey destination,
        Action<IReadOnlyList<RoomKey>?>? previewSink = null,
        RunStartMode startMode = RunStartMode.Normal,
        bool askOnlyOverAvoids = false,
        bool remember = true)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Ahead of the cards and the errand question: nothing is asked about a
        // walk that isn't going to start.
        if (services.RefuseStartForMasterSwitch("Walk")) return false;

        // Stop is holding an errand: ask whether it goes first. The walk comes back
        // through here once that is settled — after the errand, or straight away.
        if (services.MovementControl.SuspendedErrand is not null)
        {
            services.MovementControl.StartUserRun(
                () => _ = WalkAsync(services, destination, previewSink, startMode, askOnlyOverAvoids, remember));
            return true;
        }

        bool went = await PlanAndWalkAsync(services, destination, previewSink, startMode, askOnlyOverAvoids, remember);
        // A walk the user starts takes over from an event paused behind a detour: the
        // detour is ended for good by it, and nothing of that event, or of the ones
        // waiting behind it, may set off by itself once this walk is done. A walk the
        // client starts for them (askOnlyOverAvoids) is no takeover.
        if (went && !askOnlyOverAvoids) services.Events.NoteUserStop();
        return went;
    }

    private static async Task<bool> PlanAndWalkAsync(
        AppServices services, RoomKey destination, Action<IReadOnlyList<RoomKey>?>? previewSink,
        RunStartMode startMode, bool askOnlyOverAvoids, bool remember)
    {
        // Remember it for the bug report even if the walk is declined at the picker
        // or fails — so a capture can re-plan and explain what the picker decided.
        services.LastRequestedWalkTo = destination;

        // AND REMEMBER IT FOR THE USER, here rather than at the call sites. Two of
        // the five user-initiated walk paths called GotoHistory.Record themselves
        // (the map right-click and the Run button) and three did not — the
        // terminal flyout's favourites, the navigation manager's Walk buttons and
        // its footer search -- so whether a destination turned up under "Recent
        // Destinations" depended on which button started the walk. Recording in
        // the one funnel every user-initiated walk already passes through makes it
        // uniform and keeps the next entry point from having to remember.
        //
        // ON REQUEST, NOT ON ARRIVAL, which is what the old call sites did: the
        // Record ran before this method and stood whether or not the picker was
        // declined or the route failed. Same reasoning as LastRequestedWalkTo
        // above — you asked to go there, so it is where you were last headed.
        //
        // The automation engines (LoopRunner, AutoLair, DeathRecovery,
        // TrainerWalk, the remote handlers) call Walker.WalkTo directly and never
        // reach here, so they stay out of the history exactly as before.
        if (!askOnlyOverAvoids && remember) services.GotoHistory.Record(destination);

        // The game data this walk is asked for and planned on, and the stops made so
        // far: asked again after every wait below, before the plan becomes a walk or
        // a card (PlanOvertaken).
        RoutePlanInFlight inFlight = new(
            destination, () => services.GameData.ActiveLoad, services.Log, LogCat, services.MovementControl);
        // Stop has this to end while nothing moves yet: the toolbar's is enabled for it.
        using IDisposable planning = services.MovementControl.RouteBeingPlanned();

        // Let the nav-map right-click menu that launched this walk close before we do
        // anything heavy.
        await Task.Yield();
        if (PlanOvertaken(services, inFlight, calcVm: null)) return false;

        Room? source = services.RoomTracker.State.CurrentRoom;
        if (source is null)
        {
            // No confident source room — let the walker plan and report the
            // "no known source" failure itself rather than second-guessing here.
            services.Log.Debug(LogCat, $"route pick to {destination}: no confident source room — plain walk");
            ApplyStartMode(services, startMode);
            CommitWalk(services, destination, gated: false, sysGotoShortcuts: true);
            return true;
        }

        RoomKey src = source.Key;

        // Boss rooms marked "stop before entering" matter to a walk the user starts.
        // One the client starts for them (a Sell Tour stop) walks as it always has.
        // Read here, on the UI thread, for the plan that may run off it.
        bool stopsForBossRooms = !askOnlyOverAvoids;
        IReadOnlySet<RoomKey> bossStopRooms = stopsForBossRooms
            ? services.BossStopRooms() : new HashSet<RoomKey>();

        // Plan the route (which fork, if any, to surface). The BFS passes are
        // synchronous and, on a large graph (Paradigm), can take up to ~1s. When the
        // walker is IDLE, run them on a background thread so the UI thread stays live —
        // and if planning drags past a short threshold, surface the picker window in a
        // "Calculating…" state that paints while planning finishes. When a walk is
        // already IN PROGRESS, plan on the UI thread instead (no calc window that
        // time): the live walker re-plans, takes holds and warms the filter's party
        // reads as it steps, and a plan alongside it isn't made to race that. The
        // gates a plan stands down are its own thread's either way (MovementFilter).
        RouteChoiceDialogViewModel? calcVm = null;
        Task<RouteChoiceResult?>? calcDialogTask = null;
        RoutePlan plan;
        if (services.Walker.State == WalkState.Idle)
        {
            Task<RoutePlan> planTask = CountThenPlanAsync(services, src, destination, bossStopRooms);
            // Only pop the "Calculating…" window if planning takes long enough to
            // notice — a fast plan (most walk-tos) wins the race and never flashes a
            // window; the picker, if any, is then built fully-populated below.
            if (!askOnlyOverAvoids
                && await Task.WhenAny(planTask, Task.Delay(RouteCalcRevealDelayMs)) != planTask)
            {
                calcVm = new RouteChoiceDialogViewModel(
                    DestinationLabel(services, destination), DestinationLabel(services, src));
                calcDialogTask = OpenCard(services, calcVm);
            }
            try
            {
                plan = await planTask;
            }
            catch (Exception ex)
            {
                // The off-thread plan reads the room graph + movement filter, which a
                // RARE concurrent UI-thread mutation (a game-data reload / reconnect, an
                // avoid-list edit) can disturb mid-read. The idle gate excludes the live
                // walker, not those. Rather than let such a race surface as a crash, log
                // it and re-plan inline on the UI thread (safe; just a one-off brief
                // freeze). Not silent — the walk still proceeds from the re-plan.
                // Game data read in afresh is the one such change with nothing to
                // re-plan: the walk is dropped.
                if (PlanOvertaken(services, inFlight, calcVm)) return false;
                services.Log.Warn(LogCat,
                    $"route pick {src} -> {destination}: off-thread plan faulted ({ex.GetType().Name}: {ex.Message}); re-planning on the UI thread");
                plan = PlanRouteChoice(services, src, destination, bossStopRooms);
            }
        }
        else
        {
            await services.CountPartyGateItemsAsync(src, destination);
            if (PlanOvertaken(services, inFlight, calcVm)) return false;
            plan = PlanRouteChoice(services, src, destination, bossStopRooms);
        }
        if (PlanOvertaken(services, inFlight, calcVm)) return false;

        // Nav lifecycle stays Info for a fork that surfaces; a plain no-fork walk is
        // Debug so an ordinary GOTO doesn't spam the log.
        if (plan.Kind == RoutePlanKind.PlainWalk)
            services.Log.Debug(LogCat, plan.LogMessage);
        else
            services.Log.Info(LogCat, plan.LogMessage);

        if (askOnlyOverAvoids && plan.Kind != RoutePlanKind.AvoidOverride)
        {
            // An avoid on the shortest route is always the player's call, even a one-step
            // saving, and even when another fork (teleport, trap, item gate) was found
            // first and would have hidden it: show the avoid cards.
            if (RouteChoicePlanner.EvaluateAvoidOverride(
                    services.Bfs, services.Movement, services.RoomGraph, src, destination, minSavings: 1)
                is { } avoid)
            {
                services.Log.Info(LogCat,
                    $"route pick {src} -> {destination}: the shortest route crosses {avoid.AvoidedRoomCount} room(s) you marked Avoid "
                    + $"({RouteChoicePlanner.ListAvoided(avoid.AvoidedRoomNames)}); showing picker");
                return await RunPickerAsync(services, destination, src, WithSysGotoCard(services, src, destination, avoid),
                    inFlight, previewSink, calcVm, calcDialogTask, startMode);
            }
            if (TakesDefaultRouteUnasked(plan))
            {
                services.Log.Info(LogCat,
                    $"route pick {src} -> {destination}: {plan.Kind} fork, no avoid on the route — taking the default route");
                calcVm?.Close();
                ApplyStartMode(services, startMode);
                CommitWalk(services, destination, gated: false, stopForBossRooms: stopsForBossRooms, sysGotoShortcuts: true);
                return true;
            }
        }

        switch (plan.Kind)
        {
            case RoutePlanKind.PlainWalk:
                calcVm?.Close();   // no picker to show — dismiss any "Calculating…" window
                ApplyStartMode(services, startMode);
                CommitWalk(services, destination, gated: false, stopForBossRooms: stopsForBossRooms, sysGotoShortcuts: true);
                return true;
            case RoutePlanKind.BossRoom when plan.Choice is { BossRoom: { } bossRoom } bossChoice:
                // Named here, on the UI thread: the plan may have run off it.
                string bossLabel = (services.BossInRoom(bossRoom) is { } bossName ? $"{bossName}'s room" : "a boss room")
                    + $" ({bossRoom.Map}/{bossRoom.Room})";
                return await RunPickerAsync(services, destination, src,
                    bossChoice with { BossRoomLabel = bossLabel }, inFlight, previewSink, calcVm, calcDialogTask, startMode);
            case RoutePlanKind.ItemGate when plan.Choice is { UnprotectedRoomNames.Count: > 0 } crossing
                && services.Movement.TeleportsOnArrival(destination):
                // The card that crosses the lake's teleporting rooms to a room that
                // teleports on is for a walk the user starts. One the client starts on
                // their behalf takes the plain walk, which finds no route and says why.
                if (askOnlyOverAvoids)
                {
                    calcVm?.Close();
                    CommitWalk(services, destination, gated: false, stopForBossRooms: stopsForBossRooms, sysGotoShortcuts: true);
                    return true;
                }
                // Named here, on the UI thread: the plan may have run off it.
                string? goalSpell = services.RoomGraph.GetRoom(destination)?.Spell is > 0 and int goal
                    ? services.GameData.FindNameByNumber("Spells", goal) ?? $"spell {goal}" : null;
                return await RunPickerAsync(services, destination, src,
                    crossing with { CrossingGoalSpell = goalSpell }, inFlight, previewSink, calcVm, calcDialogTask, startMode);
            case RoutePlanKind.AutoObtainSole:
                calcVm?.Close();
                ApplyStartMode(services, startMode);
                // Every gate here is already flagged AutoObtainForPath, but need-posting
                // keys on a per-walk forced obtain, so the flags alone arm nothing.
                // Forcing the ids opens it for this walk, which is what the flags
                // asked for.
                // Items only: no card was shown, so no trade rides on this walk.
                JourneyFetch? soleFetch = plan.Choice is { } sole
                    && services.SourceableGateItems(sole.Requirements) is { Count: > 0 } soleItems
                    ? services.NewJourneyFetch(soleItems)
                    : null;
                // The walk has to be the route the planner settled on: the items
                // just forced were worked out for that route, which was planned for
                // the fewest traps and may teleport. So it is given the route and
                // the same trap rule. No card was shown, though, so nothing on it
                // was agreed to: it walks into no hazard room uncountered.
                CommitWalk(services, destination, gated: true, avoidTraps: true, stopForBossRooms: stopsForBossRooms,
                    pickedRoute: plan.Choice?.GatedPath, closedGates: plan.Choice?.ClosedGateItems,
                    shownOnCard: false, fetch: soleFetch, sysGotoShortcuts: true);
                return true;
            default:
                return await RunPickerAsync(services, destination, src, plan.Choice!, inFlight, previewSink, calcVm, calcDialogTask, startMode);
        }
    }

    // The forks a walk that asks only over avoids skips: each has a default route the
    // walker takes on its own (overland past a token or teleport, disarming a trap,
    // around an item gate). An avoid-override always asks, and so do a sole gated route
    // and a blocked one — there's no default route there to fall back to.
    // Leading a party, count its copies of the per-member gate items on the way
    // before planning, so the plan can tell a gate the whole party clears from one
    // only the leader does. The count asks the party and can take a few seconds;
    // it's part of the awaited plan so the "Calculating…" window covers it. It
    // returns at once for a solo walk or a route with no such gate.
    private static async Task<RoutePlan> CountThenPlanAsync(
        AppServices services, RoomKey src, RoomKey destination, IReadOnlySet<RoomKey> bossStopRooms)
    {
        await services.CountPartyGateItemsAsync(src, destination);
        return await Task.Run(() => PlanRouteChoice(services, src, destination, bossStopRooms));
    }

    // The game data was read in afresh while this walk was being planned or asked
    // about: its destination and its route are the old data's numbers, so no walk
    // starts and no card goes up (RoutePlanInFlight, which writes the log line). A
    // Stop made meanwhile drops it the same way. A "Calculating…" card still up is
    // closed, and the user is told, in the words of the stop that change makes.
    // Posted: the party's answers the plan waits on are read inside the line pump,
    // where writing to the terminal re-enters it. The party's count for the plan
    // goes with it: it was taken for this walk, and one that came back after the
    // change is by the old data's item numbers.
    private static bool PlanOvertaken(AppServices services, RoutePlanInFlight inFlight, RouteChoiceDialogViewModel? calcVm)
    {
        if (inFlight.Dropped() is not { } notice) return false;
        calcVm?.Close();
        services.ClearPartyShortGateItems($"{inFlight.Why} while the route was being planned");
        Avalonia.Threading.Dispatcher.UIThread.Post(() => services.WriteTerminalNotice(notice));
        return true;
    }

    private static bool TakesDefaultRouteUnasked(RoutePlan plan) => plan.Kind switch
    {
        RoutePlanKind.Token or RoutePlanKind.Teleport or RoutePlanKind.TrapAvoid => true,
        RoutePlanKind.ItemGate => plan.Choice?.HasFreeRoute == true,
        _ => false,
    };

    private static void ApplyStartMode(AppServices services, RunStartMode mode)
    {
        if (mode != RunStartMode.Normal) services.ApplyRunStartMode?.Invoke(mode);
    }

    // The delay before a slow plan surfaces the "Calculating…" window. A fast plan
    // (well under this) finishes first and never shows it, so an ordinary quick
    // walk-to doesn't flash a window; a plan that drags gets the feedback.
    private const int RouteCalcRevealDelayMs = 150;

    private enum RoutePlanKind { Token, Teleport, TrapAvoid, AvoidOverride, ItemGate, Blocked, AutoObtainSole, BossRoom, PlainWalk }

    // The outcome of route planning: which fork (if any) to surface, the resolved
    // choice for the picker, and the ready-to-log decision line. Pure computation —
    // no UI, no logging, no network — so WalkAsync can run it off the UI thread.
    private readonly record struct RoutePlan(RoutePlanKind Kind, RouteChoice? Choice, string LogMessage);

    // Run the fork evaluations in priority order and decide the outcome. The forks
    // each need the same two full-graph pathfinds — the plain default route (gates +
    // avoids on, teleports allowed) and the avoids-lifted route — so compute each ONCE
    // behind a memoized closure (caches the result, null included) rather than
    // re-running per fork. Reads the graph, the movement filter, settings, the
    // inventory snapshot (taken under the inventory's lock) and the per-set indexes
    // of where items come from (givers, shop stock, monster drops), which are
    // rebuilt only when the game-data set is swapped. It does no UI or logging, so
    // it's safe to call from a background thread.
    private static RoutePlan PlanRouteChoice(
        AppServices services, RoomKey src, RoomKey destination, IReadOnlySet<RoomKey> bossStopRooms)
    {
        RoutePlan plan = PlanForks(services, src, destination, bossStopRooms);
        if (plan.Kind is RoutePlanKind.PlainWalk or RoutePlanKind.AutoObtainSole || plan.Choice is not { } choice)
            return plan;
        RouteChoice withCard = WithSysGotoCard(services, src, destination, choice);
        return withCard.SysGoto is not { } card ? plan
            : plan with
            {
                Choice = withCard,
                LogMessage = plan.LogMessage + $"; sys goto card: '{card.Location.Name}' to {card.Landing}, then {card.Path.Count - 1} step(s)",
            };
    }

    // The sys goto card offered beside a fork's cards, when a jump beats the route
    // the fork walks by default: the free route, else the sole one, and for a
    // blocked route any jump that gets there. The cards don't take one on their own
    // (CommitWalk), so a shortcut the picker leaves out is one nobody walks (report
    // stock-20261128-014442: a picked route set off with a jump no card showed).
    private static RouteChoice WithSysGotoCard(AppServices services, RoomKey src, RoomKey destination, RouteChoice choice)
    {
        int? toBeat = choice.Kind == RouteChoiceKind.Blocked ? null
            : choice.HasFreeRoute ? choice.FreeStepCount : choice.GatedStepCount;
        if (services.Walker.SysGotoCardPlan(src, destination, toBeat) is not { } jump) return choice;
        IReadOnlyList<RoomKey> path = RouteChoicePlanner.BuildKeyPath(services.RoomGraph, jump.LandingRoom, jump.FromArrival);
        return choice with { SysGoto = new SysGotoRouteCard(jump.Location, jump.LandingRoom, path) };
    }

    private static RoutePlan PlanForks(
        AppServices services, RoomKey src, RoomKey destination, IReadOnlySet<RoomKey> bossStopRooms)
    {
        IReadOnlyList<Direction>? baseCache = null; bool baseDone = false;
        IReadOnlyList<Direction>? BaseRoute()
        {
            if (!baseDone) { baseCache = services.Bfs.FindPath(src, destination, services.Movement); baseDone = true; }
            return baseCache;
        }
        IReadOnlyList<Direction>? avoidLiftedCache = null; bool avoidLiftedDone = false;
        IReadOnlyList<Direction>? AvoidLiftedRoute()
        {
            if (!avoidLiftedDone)
            {
                avoidLiftedCache = services.Bfs.FindPath(src, destination, services.Movement, ignoreAvoids: true);
                avoidLiftedDone = true;
            }
            return avoidLiftedCache;
        }

        // Token fork first (Paradigm only): a held transport token that reaches the
        // destination enough rooms sooner than walking. It's an explicit opt-in feature
        // and the biggest shortcut on offer, so it leads — the picker shows the plain
        // overland walk beside the blue token card. TryPlanTokenRoute short-circuits off
        // Paradigm / when disabled, so this costs nothing on other realms.
        RouteChoice? token = services.TryPlanTokenRoute(src, destination);
        if (token is not null)
            return new(RoutePlanKind.Token, token,
                $"route pick {src} -> {destination}: token fork — token to {token.TokenPlace} lands {token.TokenLanding}, "
                + $"saves {token.FreeStepCount - token.GatedStepCount} room(s) vs overland {token.FreeStepCount}; showing picker");

        // Walk-vs-teleport fork takes precedence over the item-gate fork: if the
        // shortest route teleports and a pure-walking route also exists, let the user
        // weigh the teleport's shortcut against its danger — a teleport can drop the
        // crosser somewhere lethal, a call the client can't make.
        RouteChoice? teleport = RouteChoicePlanner.EvaluateTeleport(
            services.Bfs, services.Movement, services.RoomGraph, src, destination, BaseRoute);
        if (teleport is not null)
        {
            // The trap-avoid fork below would have offered its own route: it rides
            // along as a third card.
            teleport = RouteChoicePlanner.WithTrapAvoidCard(
                teleport, services.Bfs, services.Movement, services.RoomGraph, src, destination, BaseRoute);
            return new(RoutePlanKind.Teleport, teleport,
                $"route pick {src} -> {destination}: teleport fork — walk {teleport.FreeStepCount} "
                + $"vs teleport {teleport.GatedStepCount} hop(s) via {teleport.TeleportLanding}"
                + (teleport.TrapAvoidPath is { } trapRoute
                    ? $" (crosses {teleport.GatedTrapCount} trap(s); fewest-traps card: {trapRoute.Count - 1} hop(s), "
                      + $"{teleport.TrapAvoidTrapCount} trap(s))"
                    : "")
                + "; showing picker");
        }

        // Trap-avoid fork: the shortest route crosses a trap and a trap-free route to
        // the same room exists — surface the clean detour vs. trusting the step-time
        // disarm (which can fail).
        RouteChoice? trapAvoid = RouteChoicePlanner.EvaluateTrapAvoid(
            services.Bfs, services.Movement, services.RoomGraph, src, destination, BaseRoute);
        if (trapAvoid is not null)
            return new(RoutePlanKind.TrapAvoid, trapAvoid,
                $"route pick {src} -> {destination}: trap-avoid fork — fewest-traps route crosses "
                + $"{trapAvoid.FreeTrapCount} trap(s) vs {trapAvoid.GatedTrapCount} on the shortest; showing picker");

        // Avoid-override fork: the destination is reachable only through a room the user
        // marked "avoid" (sole), or a much shorter route runs through one (two-route).
        // EvaluateAvoidOverride defers (null) when suspending the acquirable gates opens
        // an avoid-respecting route — the item-gate fork below surfaces obtain / cross
        // options for that instead of asking the user to override their own avoid.
        RouteChoice? avoidOverride = RouteChoicePlanner.EvaluateAvoidOverride(
            services.Bfs, services.Movement, services.RoomGraph, src, destination, BaseRoute, AvoidLiftedRoute);
        if (avoidOverride is not null)
            return new(RoutePlanKind.AvoidOverride, avoidOverride,
                $"route pick {src} -> {destination}: avoid-override fork — "
                + $"{(avoidOverride.HasFreeRoute ? $"a shorter route saves {avoidOverride.FreeStepCount - avoidOverride.GatedStepCount} step(s)" : "the only route")} "
                + $"crosses {avoidOverride.AvoidedRoomCount} room(s) you marked Avoid "
                + $"({RouteChoicePlanner.ListAvoided(avoidOverride.AvoidedRoomNames)}); showing picker");

        RouteChoice? choice = RouteChoicePlanner.Evaluate(
            services.Bfs, services.Movement, services.RoomGraph, src, destination, BaseRoute);
        // The route itself is clear, but an exit on it opens from rooms the crosser
        // can't reach without crossing a gate: offer that walk as the sole route.
        choice ??= RouteChoicePlanner.EvaluateLeverDetour(
            services.Bfs, services.Movement, services.RoomGraph, src, destination, BaseRoute);
        if (choice is null)
        {
            // No shorter gated route. If the route is fully blocked but the destination
            // is physically reachable up to an obstacle, offer to run to the block;
            // otherwise walk the free route (which reports its own failure if it can't).
            if (RouteChoicePlanner.PlanBlocked(
                    services.Bfs, services.Movement, services.RoomGraph, src, destination)
                is { } blocked)
                return new(RoutePlanKind.Blocked,
                    BuildBlockedChoice(services, src, destination, blocked),
                    $"route pick {src} -> {destination}: blocked — no full route; can run as far as "
                    + $"{blocked.StopRoom} ({blocked.BlockDir} is {blocked.BlockExit.Hint}); showing picker");
            // An otherwise plain walk whose route passes through a boss room marked
            // "stop before entering", with a way around it: around, up to it, or through.
            if (RouteChoicePlanner.EvaluateBossRoom(
                    services.Bfs, services.Movement, services.RoomGraph, src, destination, bossStopRooms, BaseRoute)
                is { } boss)
                return new(RoutePlanKind.BossRoom, boss,
                    $"route pick {src} -> {destination}: boss-room fork — the route passes through stop-before boss room "
                    + $"{boss.BossRoom} ({boss.GatedStepCount} step(s)); a way around is {boss.FreeStepCount}; showing picker");
            return new(RoutePlanKind.PlainWalk, null,
                $"route pick {src} -> {destination}: no fork (free route needs nothing acquirable); plain walk");
        }

        // This choice's routes all respect the avoids (Evaluate never lifts them). If a
        // route that DOES cross an avoided room also exists — one that needs no counter
        // to obtain — attach it as an extra card so the user can pick "plow through my
        // avoided rooms" instead of fetching a raft (report paradigm-20260907-212758).
        string avoidAltNote = "";
        if (RouteChoicePlanner.AvoidAlternative(
                services.Bfs, services.Movement, services.RoomGraph, src, destination, AvoidLiftedRoute) is { } alt)
        {
            choice = choice with
            {
                AvoidAlternativePath = alt.Path, AvoidAlternativeCount = alt.AvoidedCount,
                AvoidAlternativeNames = alt.AvoidedNames,
            };
            avoidAltNote = $" (+avoid-crossing alt: {alt.AvoidedCount} room(s): "
                + $"{RouteChoicePlanner.ListAvoided(alt.AvoidedNames)}, no counter)";
        }

        static string Summarize(IReadOnlyList<RouteRequirement> reqs) => string.Join(", ", reqs.Select(r =>
            $"{r.Kind}[{string.Join("/", r.ItemIds)}]{(r.Carried ? " (carried)" : "")}"
            + (r.NoProtection ? " (no protection: its holders are teleported too)" : "")));
        string reqSummary = Summarize(choice.Requirements);
        if (choice.GatedWalk is not null)
            avoidAltNote += " (its walk takes lever trips off the route; the rooms and gates count them)";
        // Each route with its own length and needs, so the log shows the two the
        // cards offered rather than one route and a saving.
        if (!choice.HasFreeRoute)
            reqSummary += $" over {choice.GatedStepCount} step(s)";
        // How many hazard rooms the route walks into: the first thing asked of a
        // route that goes near a lake.
        if (RouteChoicePlanner.UncounteredHazardRooms(services.Movement, choice.GatedPath) is { Count: > 0 } crossed)
            reqSummary += $", crossing {crossed.Count} hazard room(s)"
                + (choice.UnprotectedRoomNames is { Count: > 0 } unprotected
                    ? $", {unprotected.Count} of them teleporting room(s) "
                      + $"({RouteChoicePlanner.ListAvoided(unprotected)}): "
                      + (services.Movement.TeleportsOnArrival(destination)
                          ? "the crossing to a room that teleports on, "
                          : "the only way there, ")
                      + "offered because the level and the boat it asks are met"
                    : "");
        if (choice.ClosedGateItems is { Count: > 0 } closed)
            reqSummary += $", going round the gates that need item(s) {string.Join("/", closed)}";
        string shortcutNote = choice.ShortcutItems is { Count: > 0 } sc
            ? $" (+optional shortcut via item(s) {string.Join("/", sc)}: {choice.ShortcutStepCount} step(s), "
              + $"saving {choice.GatedStepCount - choice.ShortcutStepCount} room(s), needs "
              + $"{Summarize(choice.ShortcutRequirements ?? Array.Empty<RouteRequirement>())})"
            : "";

        // Sole route (no gate-free alternative) whose gates are item/ticket/key, not a
        // hazard. When every gate is a single item/ticket the user flagged
        // AutoObtainForPath, the acquisition pipeline sources it all: arm and cross, no
        // prompt. Otherwise a gate the client can't auto-source is on the route (a door
        // key, or an unflagged item) — surface the picker so the user sees it and
        // chooses. Hazard-only sole routes fall through to the item-gate picker below.
        if (!choice.HasFreeRoute
            && choice.Requirements.Any(r => r.Kind != RouteRequirementKind.HazardProtection))
        {
            if (services.ShouldAutoObtainSoleRoute(choice.Requirements))
                return new(RoutePlanKind.AutoObtainSole, choice,
                    $"route pick {src} -> {destination}: sole route needs {reqSummary}, all auto-obtainable "
                    + $"— arming acquisition and walking, no prompt{shortcutNote}");
            return new(RoutePlanKind.ItemGate, choice,
                $"route pick {src} -> {destination}: sole route needs {reqSummary} (not fetched unasked: {services.GatePickLogNote(choice.Requirements)}); showing picker{shortcutNote}{avoidAltNote}");
        }

        return new(RoutePlanKind.ItemGate, choice,
            $"route pick {src} -> {destination}: item-gate fork — "
            + $"{(choice.HasFreeRoute ? $"direct route saves {choice.FreeStepCount - choice.GatedStepCount} step(s)" : "sole route")} "
            + $"needing {reqSummary}; showing picker{avoidAltNote}");
    }

    // Build the picker, draw the previewed route while it's open, and commit the
    // chosen route. Shared by every fork that surfaces a choice (teleport,
    // trap-avoid, avoid-override, item-gate, and the fully-blocked "run to the
    // block" offer); the commit switch at the end branches on the choice kind, and
    // an item-gate choice further splits into free / acquire / send-it / search /
    // route-through-avoided.
    private static async Task<bool> RunPickerAsync(
        AppServices services,
        RoomKey destination,
        RoomKey source,
        RouteChoice choice,
        RoutePlanInFlight inFlight,
        Action<IReadOnlyList<RoomKey>?>? previewSink,
        // When the idle path pre-opened a "Calculating…" window (planning ran long),
        // it's passed here to populate in place; calcDialogTask is that window's
        // close-result task. Both null when planning was fast (or a walk was in
        // progress) — then this builds the fully-populated window and shows it.
        RouteChoiceDialogViewModel? calcVm = null,
        Task<RouteChoiceResult?>? calcDialogTask = null,
        RunStartMode startMode = RunStartMode.Normal)
    {
        if (choice.SysGoto is { } jumpCard)
            choice = choice with
            {
                SysGoto = jumpCard with
                {
                    LandingLabel = DestinationLabel(services, jumpCard.Landing),
                    Eta = RouteEtaEstimator.Estimate(
                        jumpCard.Path, services.AutoLair.TravelCostModel,
                        services.RoomGraph.GetRoom, includeLairDwell: services.IsAutoCombatEnabled,
                        lairWillBeFought: services.LairWillBeFought),
                },
            };

        // Approximate arrival ETA for each route — realm-aware per-hop travel plus,
        // when auto-combat is on, a dwell for each lair the walker will actually FIGHT
        // through (friendly / passive "lairs" walk straight past — services.LairWillBeFought).
        // The same estimate the live walk-status label surfaces, so they agree. Empty
        // free path (sole route) estimates to zero, so the picker shows a bare step
        // count there.
        TimeSpan freeEta = RouteEtaEstimator.Estimate(
            choice.FreePath, services.AutoLair.TravelCostModel,
            services.RoomGraph.GetRoom, includeLairDwell: services.IsAutoCombatEnabled,
            lairWillBeFought: services.LairWillBeFought);
        TimeSpan gatedEta = RouteEtaEstimator.Estimate(
            choice.GatedPath, services.AutoLair.TravelCostModel,
            services.RoomGraph.GetRoom, includeLairDwell: services.IsAutoCombatEnabled,
            lairWillBeFought: services.LairWillBeFought);
        TimeSpan trapAvoidEta = choice.TrapAvoidPath is { } trapAvoidRoute
            ? RouteEtaEstimator.Estimate(
                trapAvoidRoute, services.AutoLair.TravelCostModel,
                services.RoomGraph.GetRoom, includeLairDwell: services.IsAutoCombatEnabled,
                lairWillBeFought: services.LairWillBeFought)
            : default;

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
        // Counters the run would BUY (item + shop room) and the items to ask the
        // party about — feed the pick-time economy probe (own bank + @wealth/@have).
        List<(int ItemId, RoomKey ShopRoom)> buys = new();
        List<(int ItemId, string Name)> neededItems = new();
        // Every any-of counter id for the hazards on this route — the "search en
        // route" card force-obtains all of them so a `sea`-revealed one (whichever
        // turns up) is grabbed by the floor collector and the crossing goes safe.
        List<int> hazardCounterIds = new();
        // The specific counter resolved per hazard requirement (which item, and how) —
        // so the picker's requirement line names the exact one it'll obtain ("log raft
        // (buy at Pier)") instead of the whole any-of list.
        Dictionary<RouteRequirement, (int ItemId, string Source)> resolvedCounters = new();
        // Where each counter comes from takes a few searches of the map per hazard,
        // which held the UI thread for twenty seconds with the card (report
        // paradigm-20261021-135647). With the walker idle it is worked out off the UI
        // thread, as the plan was, the floor here read first on it; the gates it
        // stands down are its own thread's (MovementFilter). With a walk under way it
        // stays on the UI thread, as the plan does, under a scope a freeze report
        // names.
        void ResolveCounters(IReadOnlySet<int>? onFloor)
        {
            foreach (RouteRequirement req in choice.Requirements)
            {
                // An item that doesn't stop the room teleporting is not fetched or searched for.
                if (req.Kind != RouteRequirementKind.HazardProtection || req.NoProtection) continue;
                foreach (int cid in req.ItemIds)
                    if (!hazardCounterIds.Contains(cid)) hazardCounterIds.Add(cid);
                // A counter already chosen for an earlier hazard that also appears in
                // THIS hazard's any-of set covers it too — both FCCO slide rooms accept
                // rope-and-grapple OR climbing harness, so one rope answers both. Skip
                // the redundant second source rather than buying a rope AND a harness.
                if (req.ItemIds.Any(id => floorCounters.Contains(id) || detourCounters.Contains(id)))
                    continue;
                if (services.ResolveHazardCounter(req.ItemIds, source, destination, onFloor) is { } r)
                {
                    List<int> bucket = r.OnFloor ? floorCounters : detourCounters;
                    if (!bucket.Contains(r.ItemId)) bucket.Add(r.ItemId);
                    if (!hazardSources.Contains(r.Source)) hazardSources.Add(r.Source);
                    resolvedCounters[req] = (r.ItemId, r.Source);
                    // A shop-sourced counter is a money question — collect it (and the
                    // item name) for the pick-time economy probe below.
                    if (r.ShopRoom is { } shopRoom)
                    {
                        buys.Add((r.ItemId, shopRoom));
                        // Ask the party about every item that would do on this route,
                        // not just the one the run would buy: a member with a spare
                        // canoe can hand it over for the raft the picker chose.
                        foreach (int alt in RouteSubstitutes(choice.Requirements, r.ItemId))
                            if (!neededItems.Any(n => n.ItemId == alt)
                                && services.ItemNames.GetName(alt) is { Length: > 0 } bn)
                                neededItems.Add((alt, bn));
                    }
                }
            }
        }
        if (crossesHazard)
        {
            if (services.Walker.State == WalkState.Idle)
            {
                IReadOnlySet<int> onFloor = services.CountersOnFloor(
                    choice.Requirements.Where(r => r.Kind == RouteRequirementKind.HazardProtection).SelectMany(r => r.ItemIds));
                try
                {
                    await Task.Run(() => ResolveCounters(onFloor));
                }
                catch (Exception ex)
                {
                    // As the plan's own fault above: the off-thread read of the graph,
                    // the shop and give indexes can be disturbed by a rare UI-thread
                    // change. Dropped for a change of game data; otherwise said and
                    // worked out again on the UI thread, from nothing.
                    if (PlanOvertaken(services, inFlight, calcVm)) return false;
                    services.Log.Warn(LogCat,
                        $"route card {source} -> {destination}: off-thread counter sources faulted ({ex.GetType().Name}: {ex.Message}); "
                        + "working them out on the UI thread");
                    floorCounters.Clear();
                    detourCounters.Clear();
                    hazardSources.Clear();
                    buys.Clear();
                    neededItems.Clear();
                    hazardCounterIds.Clear();
                    resolvedCounters.Clear();
                    using (services.Performance.Measure("route card: hazard counter sources"))
                        ResolveCounters(onFloor: null);
                }
                if (PlanOvertaken(services, inFlight, calcVm)) return false;
            }
            else
                using (services.Performance.Measure("route card: hazard counter sources"))
                    ResolveCounters(onFloor: null);
        }
        string? hazardCounterSource = hazardSources.Count > 0
            ? string.Join("; ", hazardSources) : null;
        bool hazardObtain = hazardCounterSource is not null;

        // Requirement-clause name helpers shared by both build paths:
        //   • give: the NPC / room a free deterministic give would ask (preempts the
        //     shop and drop tails — the give router stands both down).
        //   • shop: the shop the run would detour to buy at when it's give-less and
        //     flagged buy-if-needed (resolved from this walk's source/destination).
        //   • drop: the lair a flagged dropper sits in, when there's no give or shop.
        //   • resolvedCounter: the specific counter the run resolved per hazard
        //     requirement (item + "buy at Pier" / "ask X" / …), so the clause names
        //     that one, not the whole any-of set.
        // Each of the three says what this card's main pick itself does about the
        // item (AppServices.PlanGatePick), so a pick that fetches an unflagged item
        // names its source and one that only walks somewhere and stops names none.
        // Worked out below, once it is known which of those this pick is.
        GatePickSources? gatePick = null;
        Func<int, string?> giveName = itemId => gatePick?.GiverName(itemId);
        Func<int, string?> shopName = itemId => gatePick?.BuyOrTradeNote(itemId);
        Func<int, string?> dropName = itemId => gatePick?.DropperName(itemId);
        Func<RouteRequirement, (int ItemId, string Source)?> resolvedCounter =
            req => resolvedCounters.TryGetValue(req, out (int ItemId, string Source) v) ? v : ((int, string)?)null;

        string destLabel = DestinationLabel(services, destination);

        // For a mixed route with no sourceable counter, the base card walks to the
        // hazard's edge and stops (the user then fetches a counter / clears the hard
        // gate themselves), rather than crossing the hazard blindly.
        RoomKey? hazardEdge = mixedHazard && !hazardObtain
            ? services.HazardApproachRoom(choice.GatedPath)
            : null;

        // The run would BUY a counter → check the money BEFORE surfacing (the user's
        // "check own bank, send @wealth to the party, then surface by availability"):
        // refresh own bank and, in a party, members' on-hand cash + whether a member
        // already holds a needed item. Drives the card note and — when the leader can't
        // pay from cash / the configured bank — redirects Go to walk to the shop and
        // pause for manual provisioning.
        Game.Map.RouteBuyEconomy? economy = buys.Count > 0
            ? await services.AssessRouteBuyAsync(buys, neededItems, source)
            : null;
        // The bank and the party were asked meanwhile: no card for a plan of rooms
        // the game data no longer holds.
        if (PlanOvertaken(services, inFlight, calcVm)) return false;
        string? economyNote = economy is { } eco
            ? (eco.PartyItemHolder is { } holder
                ? $"{holder} in your party has it — will hand it over on the way"
                : eco.Affordability.Note)
            : null;
        RoomKey? buyPauseRoom = economy is { PartyItemHolder: null } e2 && !e2.AutoPayable
            ? e2.ShopRoom
            : null;

        // The teleports each card's walk takes, if any, for the card to say so. The
        // base card of a route that stops short (at the hazard's edge, at the shop to
        // provision by hand) walks to that room on foot, not the route drawn, so it
        // gets no note.
        bool gatedStopsShort = hazardEdge is not null || buyPauseRoom is not null;
        Func<RouteChoiceResult, string?> teleportsOn = r =>
            RouteChoicePlanner.DescribeTeleports(TeleportsOn(
                services, RouteACardWalks(choice, r, gatedStopsShort),
                throughGates: choice.Kind == RouteChoiceKind.ItemGate
                    && r is RouteChoiceResult.Gated or RouteChoiceResult.GatedNoAcquire
                        or RouteChoiceResult.SearchEnRoute or RouteChoiceResult.Shortcut,
                closedGates: r == RouteChoiceResult.Shortcut ? null : choice.ClosedGateItems));

        RouteChoiceDialogViewModel vm;
        Task<RouteChoiceResult?> dialogTask;
        using (services.Performance.Measure("route card: gate item sources"))
            gatePick = services.PlanGatePick(
            choice.Requirements, source, destination,
            pickFetches: choice.Kind == RouteChoiceKind.ItemGate && hazardEdge is null && buyPauseRoom is null,
            closedGates: choice.ClosedGateItems);
        if (calcVm is not null && calcDialogTask is not null)
        {
            // Idle path: the "Calculating…" window is already open and painted — fill
            // its cards in place (Populate flips it out of the calculating state).
            vm = calcVm;
            dialogTask = calcDialogTask;
            vm.Populate(
                choice, services.RouteItemLabel, giveName, shopName, dropName,
                freeEta, gatedEta, hazardCounterSource, crossesSurvivableHazard, resolvedCounter, economyNote,
                teleportsOn, trapAvoidEta);
        }
        else
        {
            // Fast plan / walk-in-progress: no calc window was opened — build the
            // fully-populated VM and show it (nothing to morph, no flicker).
            vm = new RouteChoiceDialogViewModel(
                choice, destLabel, services.RouteItemLabel, giveName, shopName, dropName,
                freeEta, gatedEta, hazardCounterSource, crossesSurvivableHazard, resolvedCounter,
                economyNote, sourceLabel: DestinationLabel(services, source),
                teleportsOn: teleportsOn, trapAvoidEta: trapAvoidEta);
            dialogTask = OpenCard(services, vm);
        }

        // Draw the selected route's line while the picker is open; clear it when
        // the picker closes so a committed walk's live path isn't double-drawn and
        // a cancel leaves no stale preview behind.
        if (previewSink is not null)
        {
            vm.PreviewRequested += r => PreviewOnMap(services, previewSink, r switch
            {
                RouteChoiceResult.Free => choice.FreePath,
                // The direct / send-it / search choices all trace the same physical
                // gated line toward the hazard.
                RouteChoiceResult.Gated => choice.GatedPath,
                RouteChoiceResult.GatedNoAcquire => choice.GatedPath,
                RouteChoiceResult.SearchEnRoute => choice.GatedPath,
                RouteChoiceResult.AvoidOverrideAlt => choice.AvoidAlternativePath,
                RouteChoiceResult.Shortcut => choice.ShortcutPath,
                // The token route's only drawable segment is the post-landing walk (the
                // token hop isn't a graph edge).
                RouteChoiceResult.Token => choice.GatedPath,
                RouteChoiceResult.TrapAvoid => choice.TrapAvoidPath,
                // The jump isn't a graph edge: the walk on from its landing is drawn.
                RouteChoiceResult.SysGoto => choice.SysGoto?.Path,
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
        string detailsTitle = $"Route → {destLabel}";
        vm.ShowDetailsRequested += r => RouteDetailsLauncher.Open(
            services, detailsTitle,
            r switch
            {
                RouteChoiceResult.Free => choice.FreePath,
                RouteChoiceResult.AvoidOverrideAlt when choice.AvoidAlternativePath is { } ap => ap,
                RouteChoiceResult.Shortcut when choice.ShortcutPath is { } sp => sp,
                RouteChoiceResult.TrapAvoid when choice.TrapAvoidPath is { } tp => tp,
                RouteChoiceResult.SysGoto when choice.SysGoto is { } sg => sg.Path,
                _ => choice.GatedPath,
            },
            r is RouteChoiceResult.Free or RouteChoiceResult.AvoidOverrideAlt or RouteChoiceResult.Shortcut
                or RouteChoiceResult.TrapAvoid or RouteChoiceResult.SysGoto
                ? null
                : choice.GatedWalk);

        RouteChoiceResult? result;
        try
        {
            result = await dialogTask;
        }
        finally
        {
            if (previewSink is not null) PreviewOnMap(services, previewSink, null);
        }
        // A change of game data closes the cards without a pick, and says so here: the
        // stop's own notice names only what was running. A pick that got in ahead of
        // the change is of the old data's rooms all the same.
        if (PlanOvertaken(services, inFlight, calcVm: null)) return false;
        if (result is not null)
            ApplyStartMode(services, vm.StartMode != RunStartMode.Normal ? vm.StartMode : startMode);
        else
            services.DropCardCounts("the card was closed without a pick");

        if (result == RouteChoiceResult.SysGoto && choice.SysGoto is { } picked)
        {
            // The jump, then the walk on from its landing: this card's route, on any fork.
            services.Log.Info(LogCat,
                $"route pick -> {destination}: sys goto '{picked.Location.Name}' to {picked.Landing}, then {picked.Path.Count - 1} step(s)");
            CommitWalk(services, destination, gated: false, sysGotoVia: picked.Location);
            return true;
        }

        if (choice.Kind == RouteChoiceKind.Blocked)
        {
            // Go = "run to the blocked room anyway": a plain walk to the furthest
            // reachable room, landing the walker adjacent to the obstacle. Cancel /
            // any other result walks nothing.
            if (result == RouteChoiceResult.Gated && choice.StopRoom is { } stop)
                CommitWalk(services, stop, gated: false);
            return result is not null;
        }

        if (choice.Kind == RouteChoiceKind.Token)
        {
            switch (result)
            {
                case RouteChoiceResult.Free:
                    // "Walk it" — the plain overland route, no token.
                    CommitWalk(services, destination, gated: false, pickedRoute: choice.FreePath);
                    break;
                case RouteChoiceResult.Token when choice.TokenPlace is { } place && choice.TokenLanding is { } landing:
                    // Use the token, then resume from its landing. A walk still in
                    // progress is taken over first, as CommitWalk does, so it can't keep
                    // stepping while the party tokens across. The coordinator declines
                    // (returns false) for a party follower, who walks overland instead.
                    // Idle too: the token route's own walks go out silently, and must
                    // not be taken for legs of a journey left standing between legs.
                    services.Walker.Stop("superseded by token route");
                    services.MovementCoordinator.ClearGate(
                        MovementCoordinator.UserGate, nameof(RouteChoicePrompt));
                    if (!services.TokenRoute.TryBegin(place, landing, destination))
                        CommitWalk(services, destination, gated: false);
                    break;
                // null → cancelled: walk nothing.
            }
            return result is not null;
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
                    // "Teleport" — the user explicitly chose the shortcut, so drop the
                    // prefer-walk bias and let the walker plan the teleport hop.
                    CommitWalk(services, destination, gated: false, preferTeleportFree: false);
                    break;
                case RouteChoiceResult.TrapAvoid when choice.TrapAvoidPath is { } trapRoute:
                    // "Fewest traps" — the trap-avoid fork's own pick: plan the
                    // fewest-traps route, with the teleports it takes.
                    CommitWalk(services, destination, gated: false, avoidTraps: true, pickedRoute: trapRoute);
                    break;
                // null → cancelled: walk nothing.
            }
            return result is not null;
        }

        if (choice.Kind == RouteChoiceKind.TrapAvoid)
        {
            switch (result)
            {
                case RouteChoiceResult.Free:
                    // "Avoid traps" — plan the trap-free route (refuse trapped exits).
                    CommitWalk(services, destination, gated: false, avoidTraps: true, pickedRoute: choice.FreePath);
                    break;
                case RouteChoiceResult.Gated:
                    // "Cross traps" — the walker's default plan, disarming at step time.
                    CommitWalk(services, destination, gated: false, pickedRoute: choice.GatedPath);
                    break;
                // null → cancelled: walk nothing.
            }
            return result is not null;
        }

        if (choice.Kind == RouteChoiceKind.BossRoom)
        {
            switch (result)
            {
                case RouteChoiceResult.Free:
                    // "Walk around": keep every stop-before boss room out of the route.
                    HashSet<RoomKey> around = new(services.BossStopRooms());
                    around.Remove(destination);
                    around.Remove(source);
                    CommitWalk(services, destination, gated: false, walkAround: around, pickedRoute: choice.FreePath);
                    break;
                case RouteChoiceResult.Gated:
                    // "Walk up to it and wait": the route through, paused one room short.
                    CommitWalk(services, destination, gated: false, pickedRoute: choice.GatedPath);
                    break;
                case RouteChoiceResult.GatedNoAcquire:
                    // "Walk through": the same route, with the stop-before mark set aside.
                    CommitWalk(services, destination, gated: false, stopForBossRooms: false, pickedRoute: choice.GatedPath);
                    break;
                // null → cancelled: walk nothing.
            }
            return result is not null;
        }

        if (choice.Kind == RouteChoiceKind.AvoidOverride)
        {
            switch (result)
            {
                case RouteChoiceResult.Free:
                    // "Respect my avoids" (two-route case) — the longer route that
                    // honours the avoid list, planned normally.
                    CommitWalk(services, destination, gated: false, pickedRoute: choice.FreePath);
                    break;
                case RouteChoiceResult.Gated:
                    // "Route through avoided rooms" — override the avoid list for this
                    // one walk. Avoids stay set; only this walk crosses them.
                    CommitWalk(services, destination, gated: false, ignoreAvoids: true, pickedRoute: choice.GatedPath);
                    break;
                // null → cancelled: walk nothing.
            }
            return result is not null;
        }

        switch (result)
        {
            case RouteChoiceResult.Free:
                CommitWalk(services, destination, gated: false, pickedRoute: choice.FreePath);
                break;
            case RouteChoiceResult.Gated when hazardEdge is { } edge:
                // Mixed route, no sourceable counter: the base card walks to the
                // hazard's edge and stops (a plain gate-free walk to the room just
                // short of the river), so the user can fetch a counter / clear the
                // hard gate by hand from there — rather than crossing blindly.
                CommitWalk(services, edge, gated: false);
                break;
            case RouteChoiceResult.Gated when buyPauseRoom is { } shopStop:
                // The counter must be bought but the leader can't pay from cash or the
                // configured bank (the money's at another bank or spread across the
                // party). Don't arm an auto-buy that would stall — walk to the shop
                // and stop, so the user withdraws / pools cash / provisions the party
                // by hand from there (the card names where the money is).
                CommitWalk(services, shopStop, gated: false);
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
                // The route's ITEM gates need the same force as its hazard counters,
                // for the same reason: the pick is the consent, and without a forced
                // obtain nothing would be armed for them — the walk would cross a gate
                // it had made no arrangements for.
                //
                // Both go in one order, which the walk carries on its journey. A
                // trade hands an item of the user's over: picking this card agrees to
                // the trades it named and to no others, for this journey alone.
                CommitWalk(services, destination, gated: true, avoidTraps: !choice.HasFreeRoute,
                    pickedRoute: choice.GatedPath, closedGates: choice.ClosedGateItems,
                    fetch: services.NewJourneyFetch(
                        detourCounters.Concat(services.SourceableGateItems(choice.Requirements)), gatePick.Trades));
                break;
            case RouteChoiceResult.GatedNoAcquire:
                // "Send it": walk the gated route but don't arm acquisition — the
                // user asserts they'll clear the gates without provisioning. A sole
                // route still avoids traps, matching the planner's chosen approach.
                CommitWalk(services, destination, gated: true,
                    armAcquisition: false, avoidTraps: !choice.HasFreeRoute,
                    pickedRoute: choice.GatedPath, closedGates: choice.ClosedGateItems);
                break;
            case RouteChoiceResult.SearchEnRoute:
                // "Search en route": force-obtain every any-of counter, then walk the
                // gated route. The forced obtain both arms the shop-buy fallback (the
                // reliable acquire path — see PathItemShopRouter) and, when the master
                // auto-search toggle is on, the per-room `sea`. The floor collector
                // grabs whichever counter a search reveals first; found-first aborts the
                // buy and the walk crosses. With auto-search off (or if nothing turns up
                // en route) the shop-buy is the last resort. Auto-search is the driver:
                // toggling it off mid-route stops the `sea` and leaves the buy running.
                // Picking Search asserts intent to search, so turn auto-search on for
                // this leg if it's off — the card always actually searches. It flips
                // back off once the counter lands (found or bought) or the walk ends.
                services.BeginRouteSearchAutoSearch();
                CommitWalk(services, destination, gated: true, avoidTraps: !choice.HasFreeRoute,
                    pickedRoute: choice.GatedPath, closedGates: choice.ClosedGateItems,
                    fetch: services.NewJourneyFetch(hazardCounterIds));
                break;
            case RouteChoiceResult.AvoidOverrideAlt:
                // "Route through my avoided rooms" — the extra card: override the avoid
                // list for this one walk (needs no counter). Avoids stay set; only this
                // walk crosses them, same as the avoid-override fork's commit.
                CommitWalk(services, destination, gated: false, ignoreAvoids: true,
                    pickedRoute: choice.AvoidAlternativePath);
                break;
            case RouteChoiceResult.Shortcut when choice.ShortcutItems is { Count: > 0 } sci:
                // The optional shortcut route. Already holding the item → walk it (a
                // live-filter walk takes the shortcut since its gate is open). Not
                // holding it → hand to the shortcut-source coordinator: walk to the
                // item's source, try to obtain it, then take the shortcut if it turned
                // up or the long route if it didn't. The coordinator declines when the
                // item has no reachable source, so we just walk the long route.
                int shortcutItem = sci[0];
                if (services.IsItemCarried(shortcutItem))
                {
                    // Fewest traps, as the card's route was planned (a sole route's is).
                    CommitWalk(services, destination, gated: false, avoidTraps: true, pickedRoute: choice.ShortcutPath);
                    break;
                }
                // A walk this pick replaces is stopped out loud: taken over silently,
                // its detour routers never hear of it and go on to issue its next leg.
                services.Walker.Stop("superseded by a shortcut pick");
                // The trip to the item's source and on from it goes through the detour
                // walk, which starts no journey of its own. Declared here it is the
                // user's walk for both legs (never held to the automatic-walk teleport
                // list, the shortcut's own teleports kept for the leg that takes it).
                services.Walker.BeginJourney(CardJourney(
                    services, destination, choice.ShortcutPath, closedGates: null));
                // If the item doesn't turn up the walk goes the long way, which is the
                // main card's route and not this one's: it takes teleports as that
                // card showed and goes round the gates that card went round. Left on
                // the shortcut's rules, a shortcut that walks sent the long way round
                // on foot too, past the teleport its card names.
                WalkJourney theLongWay = CardJourney(
                    services, destination, choice.GatedPath, choice.ClosedGateItems);
                if (!services.ShortcutSource.TryBegin(shortcutItem, destination,
                        beforeLongRoute: () => services.Walker.BeginJourney(theLongWay)))
                    CommitWalk(services, destination, gated: false);
                break;
            // null → cancelled: walk nothing (and leave any manual pause intact —
            // the user backed out, so nothing changed).
        }
        return result is not null;
    }

    // The user started a loop. Getting to a loop is the same as getting anywhere
    // else, so from off the loop this is a walk-to to the nearest loop room, with
    // every route card a walk-to is offered, and the loop begins when that walk
    // arrives (LoopWalkHandoff). Standing on the loop already, or with no room of it
    // to plan a walk to, the loop runner starts it directly as it always has.
    // declined runs when that walk ends before it began: its route cards were closed
    // without a pick, or a stop dropped it while it was being planned.
    public static async Task StartLoopAsync(
        AppServices services, Loop loop,
        Action<IReadOnlyList<RoomKey>?>? previewSink = null,
        RunStartMode startMode = RunStartMode.Normal,
        Action? declined = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(loop);
        // Here, not left to the approach walk's own check, so the notice names
        // the loop the user asked for.
        if (services.RefuseStartForMasterSwitch("Loop")) return;
        // A loop the runner will refuse is handed straight to it, to be refused there
        // with its reason, instead of walking to the loop first.
        RoomKey? entry = services.RoomTracker.State.CurrentRoom is { } here
            && services.LoopRunner.RefusalFor(loop) is null
            ? services.LoopRunner.NearestRoomOf(loop, here.Key)
            : null;
        if (entry is not { } loopRoom)
        {
            ApplyStartMode(services, startMode);
            // The user's loop takes over from an event paused behind a detour, as a
            // user's walk does (WalkAsync).
            if (services.LoopRunner.Start(loop, userStarted: true)) services.Events.NoteUserStop();
            return;
        }

        services.Log.Info(LogCat, $"loop '{loop.Name}' started from off the loop: walking to {loopRoom} first, as a walk-to");
        services.LoopHandoff.Begin(loop, loopRoom);
        bool went = await WalkAsync(services, loopRoom, previewSink, startMode, remember: false);
        if (went) return;
        services.LoopHandoff.Cancel("cancelled at the route cards");
        declined?.Invoke();
    }

    // Start the walk, first lifting any lingering manual pause. A user picking a
    // fresh destination is an explicit "go here now" that outranks a mid-walk
    // Pause: without clearing the UserGate the new walk would immediately re-pause
    // (AutoWalkManager.WalkToImmediate honours the coordinator's paused state), so
    // the destination changed but the walker stayed frozen. Engine waits (Combat /
    // rest / party) are left asserted and re-pause on their own if still relevant.
    // preferTeleportFree defaults TRUE for every user-picker commit: a walk the user
    // launched from the picker (or a plain walk-to) should take the pure-walking route
    // and only fall back to a teleport hop when walking is genuinely impossible — so a
    // mid-walk re-plan (e.g. after a search-en-route counter turns up) never silently
    // pivots onto a vortex the user didn't ask for. The one exception is the teleport
    // fork's explicit "Teleport" pick, which passes false to allow the shortcut.
    private static void CommitWalk(
        AppServices services, RoomKey destination, bool gated,
        bool armAcquisition = true, bool avoidTeleports = false, bool avoidTraps = false,
        bool ignoreAvoids = false, bool preferTeleportFree = true,
        // A walk the user starts pauses one room short of each boss room marked "stop
        // before entering" that it passes through, unless they picked the walk around
        // them (walkAround) or the walk through regardless (stopForBossRooms: false).
        IReadOnlySet<RoomKey>? walkAround = null, bool stopForBossRooms = true,
        // The route of the card the user picked, and the gate items it was planned
        // round. The walker plans for itself from flags, so the flags have to add up
        // to this route: a card that showed a 45-step way through a hole in the
        // ground was walked as 263 steps on foot, and a card that went round the
        // amber talisman's exit was walked straight at it (reports
        // paradigm-20261008-174236, paradigm-20261008-173911).
        IReadOnlyList<RoomKey>? pickedRoute = null, IReadOnlyCollection<int>? closedGates = null,
        // False for a route the walk must match but no card showed (the sole route
        // whose items are all fetched for it): planned like a picked one, with no
        // hazard room agreed to.
        bool shownOnCard = true,
        // What the walk is to fetch on its way, and the trades its card named.
        JourneyFetch? fetch = null,
        // A card the user picked is walked as shown, with no sys goto shortcut its
        // card didn't show (the default). A walk no card showed (a plain walk-to, the
        // sole route) passes true and may jump as any walk does. sysGotoVia: the sys
        // goto card's own jump.
        bool sysGotoShortcuts = false,
        SysopGotoLocation? sysGotoVia = null)
    {
        // On the UI thread, from the pick to the walker's first step: a freeze report
        // names this scope (and the walker's own inside it) when that holds it.
        using PerformanceMonitor.Scope commit = services.Performance.Measure("route pick: walk commit");
        IReadOnlyList<string> landings = TeleportsOn(services, pickedRoute, throughGates: gated, closedGates);
        string? teleports = RouteChoicePlanner.DescribeTeleports(landings);
        // Walking into a hazard room uncountered is agreed to on a card, for the
        // rooms on that card's route. A gated walk nobody was shown a card for (the
        // sole route whose items are all fetched for it) agrees to none.
        bool shown = gated && pickedRoute is not null && shownOnCard;
        IReadOnlyList<RoomKey>? agreedHazards = shown
            ? RouteChoicePlanner.UncounteredHazardRooms(services.Movement, pickedRoute) : null;
        preferTeleportFree = RouteChoicePlanner.PickedWalkPrefersTeleportFree(preferTeleportFree, avoidTeleports, landings);
        if (pickedRoute is { Count: > 1 })
            services.Log.Info(LogCat,
                $"route pick -> {destination}: walking the picked route, {pickedRoute.Count - 1} step(s), "
                + (teleports is null ? "on foot (a re-plan keeps to walking)" : $"takes {teleports}")
                + (gated ? ", planned through its gates" : "")
                + (closedGates is { Count: > 0 }
                    ? $", going round the gates that need item(s) {string.Join("/", closedGates)}" : "")
                + (agreedHazards is { Count: > 0 }
                    ? $", walking into hazard room(s) {string.Join(", ", agreedHazards)} as picked" : ""));

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
        services.LoopHandoff.NoteWalkCommitted(destination);
        services.Walker.SetBossRoomRule(destination, walkAround,
            haltBefore: stopForBossRooms && walkAround is null ? services.BossStopRooms() : null);
        services.Walker.WalkTo(
            destination,
            planThroughAcquirableGates: gated,
            armItemAcquisition: armAcquisition,
            avoidTeleports: avoidTeleports,
            avoidTraps: avoidTraps,
            ignoreAvoids: ignoreAvoids,
            preferTeleportFree: preferTeleportFree,
            pickedRoute: shown,
            keepGatesClosedFor: gated ? closedGates : null,
            agreedHazardRooms: agreedHazards,
            fetch: fetch,
            sysGotoShortcuts: sysGotoShortcuts,
            sysGotoVia: sysGotoVia);
    }

    // Where a card's route teleports, read with the gates stood down that its walk
    // plans through: whether a hop can be walked instead of teleported is judged by
    // the exits open to that plan, which for a gated card are not the ones open now.
    private static IReadOnlyList<string> TeleportsOn(
        AppServices services, IReadOnlyList<RoomKey>? route, bool throughGates, IReadOnlyCollection<int>? closedGates)
    {
        using (throughGates ? services.Movement.SuspendAcquirableGatesExcept(closedGates ?? Array.Empty<int>()) : null)
            return RouteChoicePlanner.TeleportLandings(services.RoomGraph, route, services.Movement);
    }

    // The journey of a card whose walk goes out through the detour walk, leg by leg,
    // and so never passes CommitWalk: the card's destination, its teleports and the
    // gates it goes round, planned for the fewest traps as a sole route's card is.
    private static WalkJourney CardJourney(
        AppServices services, RoomKey destination, IReadOnlyList<RoomKey>? route,
        IReadOnlyCollection<int>? closedGates)
        => new(destination,
            AvoidTraps: true,
            PreferTeleportFree: RouteChoicePlanner.PickedWalkPrefersTeleportFree(
                requested: true, avoidTeleports: false, TeleportsOn(services, route, throughGates: true, closedGates)),
            ClosedGates: closedGates is { Count: > 0 } ? closedGates : null,
            SysGotoShortcuts: false);

    // The route a card's walk follows, or null when its walk follows none of the
    // routes drawn: the base card of a route that stops short (gatedStopsShort)
    // walks to the hazard's edge or the shop, on foot, and a blocked route's card
    // walks to the block.
    internal static IReadOnlyList<RoomKey>? RouteACardWalks(
        RouteChoice choice, RouteChoiceResult card, bool gatedStopsShort)
    {
        if (card == RouteChoiceResult.SysGoto) return choice.SysGoto?.Path;
        if (choice.Kind == RouteChoiceKind.Blocked) return null;
        return card switch
        {
            RouteChoiceResult.Free => choice.FreePath,
            RouteChoiceResult.AvoidOverrideAlt => choice.AvoidAlternativePath,
            RouteChoiceResult.Shortcut => choice.ShortcutPath,
            RouteChoiceResult.TrapAvoid => choice.TrapAvoidPath,
            RouteChoiceResult.Gated when gatedStopsShort => null,
            _ => choice.GatedPath,
        };
    }

    // The items that protect as well as itemId on this route: the intersection of
    // every hazard requirement's any-of set that contains it (a canoe crosses the
    // river but not Crystal Lake, so a route through both only accepts a raft or
    // skiff). itemId first; just itemId when no hazard requirement names it.
    internal static IReadOnlyList<int> RouteSubstitutes(IReadOnlyList<RouteRequirement> reqs, int itemId)
    {
        HashSet<int>? subs = null;
        foreach (RouteRequirement req in reqs)
        {
            if (req.Kind != RouteRequirementKind.HazardProtection || !req.ItemIds.Contains(itemId)) continue;
            if (subs is null) subs = new HashSet<int>(req.ItemIds);
            else subs.IntersectWith(req.ItemIds);
        }
        if (subs is null) return new[] { itemId };
        var ordered = new List<int> { itemId };
        ordered.AddRange(subs.Where(id => id != itemId).OrderBy(id => id));
        return ordered;
    }

    // The selected card's route drawn on the map (null clears it). Measured: the
    // map redraws on the UI thread, and a freeze report should say so if that holds it.
    private static void PreviewOnMap(
        AppServices services, Action<IReadOnlyList<RoomKey>?> previewSink, IReadOnlyList<RoomKey>? route)
    {
        using PerformanceMonitor.Scope preview = services.Performance.Measure("route pick: map preview");
        previewSink(route);
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
        // A toll or fare that stops the walk: say what the purse was taken to be,
        // since a count the client has wrong is the usual reason it surprises.
        RoomExit blockExit = plan.BlockExit;
        if ((services.Movement.DescribeExitBlock(in blockExit) & (ExitBlockReason.Toll | ExitBlockReason.Fare)) != 0
            && services.Movement.DescribePurseFor(in blockExit) is { } purse)
            reason += $": {purse}";
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
