using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;

namespace MudPlay.Services;

// The kind of acquirable gate a direct route crosses. Drives the requirement
// phrasing in the route picker (a raft to carry, a ticket, a door key, a hazard
// counter item).
public enum RouteRequirementKind
{
    CarryItem,          // (Item: N) — a raft / held item that must be in hand.
    Ticket,             // (Ticket: N) — a consumed passage ticket.
    DoorKey,            // (Key: N) — the key that opens a locked door.
    HazardProtection,   // a cast-on-enter room hazard; carry any listed counter.
}

// One requirement the direct route imposes: the item id(s) that satisfy it. For
// CarryItem / Ticket / DoorKey the list is the single gate item; for
// HazardProtection it's the any-of counter set (hold at least one).
public sealed record RouteRequirement(RouteRequirementKind Kind, IReadOnlyList<int> ItemIds)
{
    // True when the crosser ALREADY carries this gate's item. Surfaced (rather than
    // silently dropped as "not blocked") so the requirement list is complete — the
    // route genuinely crosses this gate, the crosser just already satisfies it.
    public bool Carried { get; init; }

    // True for a hazard no item makes safe to route through (Crystal Lake's sea
    // rooms, which teleport a boat's holder too). It is on a route only for a
    // crossing the client makes (CrossingCard, or a place only the lake reaches),
    // and the card says so; nothing is ever fetched for it.
    public bool NoProtection { get; init; }
}

// Which kind of route fork the picker is presenting. ItemGate: a shorter direct
// route crosses an acquirable item / ticket / key / hazard gate the free route
// detours around. Teleport: a shorter route takes a teleport hop the walking
// route avoids — offered because a teleport can drop the crosser somewhere
// deadly (a damaging plane, water with no boat) that only the user's character
// knowledge can judge, so the client can't silently take the shortcut for them.
public enum RouteChoiceKind
{
    ItemGate,
    Teleport,
    TrapAvoid, // the shortest route crosses a trap; a trap-free detour exists — offered
               // because the walker would otherwise disarm at step time, and the user
               // may prefer the longer clean route to risking the disarm.
    Blocked,   // no route at all — offer to walk as far as possible, up to the block.
    AvoidOverride, // the destination is reachable ONLY by routing through a room the user
               // marked "avoid" (sole case), or a much shorter route exists through one
               // (two-route case). Offered so the user can override their own avoid list
               // for this one walk, warned about which/how many avoided rooms it crosses.
    Token,     // (Paradigm only) a held transport token teleports to a fixed town that
               // reaches the destination meaningfully faster than walking. Unlike an
               // in-game teleport this is NOT a graph edge — the token is USED (costs
               // gold + a daily charge + wipes buffs), lands the player at the token's
               // town, then the walk resumes from there. Never auto-taken — the user
               // picks it against the plain overland route shown alongside.
    BossRoom,  // the shortest route passes through a boss room flagged "stop before
               // entering" and a way around exists: walk around it, walk up to it
               // and wait, or walk through regardless.
}

// A "run to the blocked room anyway" plan: the furthest room the walker can
// actually reach toward a destination it can't fully reach, plus the exit that
// stops it there (so the picker can name the obstacle) and the reachable path for
// the map preview. StopRoom is always reachable under the live filter, so a plain
// WalkTo(StopRoom) lands the walker adjacent to the block.
public sealed record BlockedRoutePlan(
    RoomKey StopRoom, Direction BlockDir, RoomExit BlockExit, IReadOnlyList<RoomKey> Preview);

// A free-vs-direct route comparison for one destination: the free route's step
// count, the shorter direct route's step count, the requirements the direct
// route demands, and each route as a RoomKey sequence (source first, then every
// hop's target) so the picker can draw a map preview of whichever the user
// selects before committing. Produced when the direct route is a genuine
// shortcut that needs an acquirable item, OR when NO gate-free route exists and
// the only way there crosses something acquirable (a survivable room hazard, or
// an item / ticket / key gate). The caller decides what to do with a sole route
// by its requirement shape; see RouteChoicePrompt.
// For a Teleport choice the "free" route is the pure-walking one (longer, safe)
// and the "gated" route is the teleport shortcut (shorter, potentially deadly);
// Requirements is empty and TeleportLanding names the room the teleport drops you
// in so the picker can caveat the danger.
public sealed record RouteChoice(
    int FreeStepCount,
    int GatedStepCount,
    IReadOnlyList<RouteRequirement> Requirements,
    IReadOnlyList<RoomKey> FreePath,
    IReadOnlyList<RoomKey> GatedPath,
    RouteChoiceKind Kind = RouteChoiceKind.ItemGate,
    string? TeleportLanding = null,
    RoomKey? StopRoom = null,
    string? BlockedReason = null,
    // For a TrapAvoid choice: how many traps each route crosses. The free
    // (fewest-traps) route may still cross an unavoidable trap, so the picker states
    // the real counts rather than claiming "trap-free" when it isn't. Zero for every
    // other kind.
    int FreeTrapCount = 0,
    int GatedTrapCount = 0,
    // For an AvoidOverride choice: how many rooms the user marked "avoid" the gated
    // (override) route passes through, so the card can warn ("routes through N rooms
    // you marked Avoid"). Zero for every other kind.
    int AvoidedRoomCount = 0,
    // An OPTIONAL avoid-crossing alternative offered alongside a hazard/gate route
    // that already respects the avoids: the ignore-avoids route (RoomKey path) and
    // how many marked rooms it crosses. Non-null only when the picker attaches it to
    // an item-gate/hazard choice — a raft route respects your avoids, but plowing
    // through the avoided rooms needs no raft, so it's a real second way there. Null
    // on every fork that doesn't offer it.
    IReadOnlyList<RoomKey>? AvoidAlternativePath = null,
    int AvoidAlternativeCount = 0,
    // An OPTIONAL shortcut alternative to the committed (GatedPath) route: a shorter
    // route unlocked by carrying extra item(s) the committed route avoids. Non-null
    // only when the committed route deliberately took the longer, gate-item-fewer way
    // and a genuine shortcut exists — the picker surfaces it as "shortcut via X, saves
    // N rooms" so the user can weigh fetching the (possibly unsourceable) shortcut item
    // against the reliable long route. ShortcutItems is never auto-obtained.
    IReadOnlyList<RoomKey>? ShortcutPath = null,
    int ShortcutStepCount = 0,
    IReadOnlyList<int>? ShortcutItems = null,
    // Everything the shortcut route needs, not only the item that makes it a
    // shortcut: it usually shares the committed route's required gates (the way
    // through the talisman exit still crosses the orb gate), and its card lists them.
    IReadOnlyList<RouteRequirement>? ShortcutRequirements = null,
    // The gate items the committed (GatedPath) route was planned round. The walk has
    // to plan the same way, or its own search (every gate assumed open) takes the
    // shortcut the card set aside. Set whenever such gates exist, shortcut saving or not.
    IReadOnlyList<int>? ClosedGateItems = null,
    // For a Token choice (Paradigm transport token): the token's place ("Silvermere"),
    // the room it lands in, the gold cost in copper, the level needed to use it, and
    // the daily charges left (null = not yet looked). FreeStepCount/FreePath are the
    // overland walk; GatedStepCount/GatedPath are the landing→destination walk the
    // token shortcut leaves. TeleportLanding carries the landing's display label,
    // reused from the teleport fork. Zero/null for every other kind.
    string? TokenPlace = null,
    RoomKey? TokenLanding = null,
    long TokenCostCopper = 0,
    int TokenMinLevel = 0,
    int? TokenCharges = null,
    // For a Teleport choice: the per-person copper fare the teleport route's paid
    // transports charge (an NPC ask-transport), so the card can state the cost
    // before the user commits. Zero when the teleports are free.
    long TeleportFareCopper = 0,
    // For a route whose lever detour crosses the gates (EvaluateLeverDetour): the
    // walk already expanded, lever trips included. GatedPath is then the rooms that
    // walk passes, in order, with revisits, so re-expanding it would detour twice;
    // the step list is built from this instead. Null on every other choice.
    IReadOnlyList<WalkStep>? GatedWalk = null,
    // The avoided rooms behind AvoidedRoomCount / AvoidAlternativeCount, named in
    // the order the route meets them ("Black Wasteland (3/740)"). The card lists
    // them: a count alone left the player hunting the map for a room that may sit
    // on another map altogether (report paradigm-20261007-215302).
    IReadOnlyList<string>? AvoidedRoomNames = null,
    IReadOnlyList<string>? AvoidAlternativeNames = null,
    // For a BossRoom choice: the stop-before boss room the shortest route passes
    // through, and that route cut off at the room before it (where "walk up and
    // wait" stops). FreePath is the way around every stop-before boss room;
    // GatedPath is the shortest route, through the boss room.
    RoomKey? BossRoom = null,
    IReadOnlyList<RoomKey>? BossWaitPath = null,
    // "pharaoh rastep's room (12/2250)", filled in by the caller for the cards.
    string? BossRoomLabel = null,
    // On a card whose route enters rooms closed to routes (CrossingCard, the walk to
    // the room that teleports on to the Bloodwood Weald; or a crossing the plain
    // walk makes, with another gate on its way): those rooms, named in the order
    // the route meets them ("Crystal Lake (17/1448)"). Null on a route that enters
    // none.
    IReadOnlyList<string>? UnprotectedRoomNames = null,
    // On the Weald card alone: what the destination's room spell is called in the
    // game data ("bloodwood weald temp"), filled in by the caller so the card can
    // say where the room sends you. Null on every other card, which is how the card
    // knows the crossing is not to such a room.
    string? CrossingGoalSpell = null)
{
    // No gate-free alternative — every path to the destination crosses a hazard,
    // so the direct route is the ONLY way there (empty FreePath is the sentinel).
    // The picker presents it as the sole option instead of a shortcut choice.
    public bool HasFreeRoute => FreePath.Count > 0;
}

// Compares the free-preferring route (acquirable gates active, so BFS detours
// around them) against the direct route (gates suspended, so BFS crosses them as
// if every gate item were carried). Returns a RouteChoice when the direct route
// saves steps AND needs something acquirable, OR when no gate-free route exists
// and the sole route crosses something acquirable (HasFreeRoute false). Null
// means the free route is fine on its own — the caller walks it plainly.
//
// Only the four acquirable gates (item / ticket / key-door / hazard) are
// suspended for the direct pass — level / toll / class gates stay active, so the
// direct route never routes through a gate the crosser fundamentally can't pass.
public static class RouteChoicePlanner
{
    // Minimum rooms a shorter item / ticket / key shortcut must save before the
    // picker offers it. Below this, the acquisition (often purchase) detour isn't
    // worth the saving, so the free route wins outright. Hazard-only shortcuts
    // ignore this floor.
    private const int MinItemGateSavings = 2;

    // Minimum rooms a teleport shortcut must save before the picker surfaces the
    // walk-vs-teleport fork. A teleport that shaves only a room isn't worth
    // interrupting the walk to weigh against its danger; below this the walker
    // just takes it (unchanged silent behavior).
    private const int MinTeleportSavings = 2;

    // Minimum rooms an avoid-crossing route must save before the picker offers the
    // TWO-ROUTE avoid override (an avoid-honouring route ALSO exists but is longer).
    // Below this, the user's marked avoid is worth keeping — a one-room saving isn't
    // worth routing through a room they deliberately excluded. The SOLE case (no
    // avoid-honouring route at all) ignores this floor: it's the only way there.
    private const int MinAvoidOverrideSavings = 2;

    public static RouteChoice? Evaluate(
        BfsMapper bfs,
        MovementFilter filter,
        RoomGraphManager graph,
        RoomKey source,
        RoomKey destination,
        // Optional memoized provider for the plain default route (gates + avoids on,
        // teleports allowed). Several forks compute the SAME route; the caller passes
        // one memoized closure so the full-graph BFS runs once instead of per fork.
        // Null → compute it here (standalone callers).
        Func<IReadOnlyList<Direction>?>? baseRoute = null)
    {
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(graph);

        // Free route: gates active. May be null when every path to the
        // destination crosses an acquirable gate.
        IReadOnlyList<Direction>? free = baseRoute is { } bp ? bp() : bfs.FindPath(source, destination, filter);

        bool hasFree = free is { Count: > 0 };

        // Direct route: acquirable gates suspended so BFS crosses them. Beside a free
        // route it is the route obtaining something would open, so a hazard room no
        // item protects from stays closed: a boat is not offered for a lake it does
        // nothing on (report paradigm-20261009-123349). With no free route the
        // hazards are dealt with below.
        IReadOnlyList<Direction>? gated = null;
        if (hasFree)
            using (filter.SuspendAcquirableGatesButUnprotectableHazards())
                gated = bfs.FindPath(source, destination, filter);

        // A SOLE route (no gate-free way there) goes round the rooms nothing protects
        // from (the lake's sea rooms) when a way round exists. Standing every gate
        // down at once made a route that needed only a door key walk the lake's
        // teleport rooms as open ground, and ask for a boat to do it (reports
        // paradigm-20261009-135049, paradigm-20261009-135314). Other hazard rooms are
        // not gone round: a detour of any length to avoid a counter that can be had
        // was worse than asking for it.
        //
        // Either way a trap on one approach may be dodgeable by taking another (e.g.
        // a hazard room reachable from several sides, only one of which is trapped).
        // Prefer the fewest-traps approach so the forced crossing doesn't also eat a
        // trap it could have avoided — the walk commits this route with avoidTraps
        // to match, and the card's route is planned by that walk's own search so the
        // two are one route and not two of the same length.
        // A genuine shortcut (a free route exists) keeps the shortest-by-hops gated
        // route so the step-saving comparison below stays meaningful.
        if (!hasFree)
        {
            using (filter.SuspendAcquirableGatesExceptUnprotectable(Array.Empty<int>()))
                gated = PlanAsTheWalkWill(bfs, graph, filter, source, destination, avoidTraps: true);
            // Nothing gets there with every item in hand and the sea rooms closed:
            // disconnected, behind a gate no item opens, or beyond the lake. No card
            // offers a way across the lake but the one below (user, 2026-10-10: "only
            // typed moves should enter the lake unless the user points to the
            // bloodwood weald teleport room or the ancient galleon").
            if (gated is not { Count: > 0 })
                return CrossingCard(bfs, filter, graph, source, destination);
        }
        if (gated is null || gated.Count == 0) return null;

        List<RouteRequirement> reqs = CollectRequirements(graph, filter, source, gated);
        if (reqs.Count == 0) return null;   // needs nothing acquirable → not a gated choice

        if (!hasFree)
        {
            // No gate-free route at all — surface the sole route (HasFreeRoute
            // false) and let the caller decide by its shape. A survivable-hazard
            // route is offered in the picker (carry / buy / `use` a counter and
            // walk through, rather than aborting with "a room hazard you can't
            // survive"). A sole item / ticket / key route is governed there by the
            // item's AutoObtainForPath flag: flagged arms the acquisition pipeline
            // and crosses the gate; unflagged falls back to the plain walk whose
            // failure names the missing item.
            //
            // But first separate genuinely-required gates from optional shortcuts an
            // item merely unlocks: commit the route that avoids every optional shortcut
            // (so the walk takes the reliable way and its carried gates surface), and
            // offer the shortcut as an alternative rather than mislabelling its item as
            // required (report paradigm-20260913-100733).
            GateClassification gc = ClassifyGates(bfs, filter, graph, source, destination, gated);
            IReadOnlyList<RoomKey> soleKeys = BuildKeyPath(graph, source, gc.CommittedPath);
            RouteChoice sole = new(
                0, gc.CommittedPath.Count, gc.Requirements,
                Array.Empty<RoomKey>(),
                soleKeys)
            {
                ClosedGateItems = gc.ClosedGateItems.Count > 0 ? gc.ClosedGateItems : null,
                UnprotectedRoomNames = ClosedRoomsOn(filter, graph, soleKeys),
            };
            if (gc.ShortcutPath is { } scp)
                sole = sole with
                {
                    ShortcutPath = BuildKeyPath(graph, source, scp),
                    ShortcutStepCount = scp.Count,
                    ShortcutItems = gc.ClosedGateItems,
                    ShortcutRequirements = TaggedRequirements(graph, filter, source, scp),
                };
            return sole;
        }

        // A free route exists — only offer the direct one when it's actually
        // shorter. An equal-or-longer "shortcut" is no bargain (equal length is
        // what we get when the player already carries the gate items, so the two
        // routes coincide).
        if (gated.Count >= free!.Count) return null;

        // A shortcut that shaves only a single room isn't worth a detour to
        // acquire — and usually to buy from a shop — the gate item: a player
        // won't sail to a boatman and spend gold on a skiff to save one step.
        // Suppress the offer for such a marginal item / ticket / key shortcut and
        // just walk the free route. Hazard-only shortcuts are exempt (walking a
        // warded room you can counter is a fair trade even for one room, and
        // nothing needs buying).
        int saved = free.Count - gated.Count;
        if (saved < MinItemGateSavings
            && reqs.Any(r => r.Kind != RouteRequirementKind.HazardProtection))
            return null;

        IReadOnlyList<RoomKey> gatedKeys = BuildKeyPath(graph, source, gated);
        return new RouteChoice(
            free.Count, gated.Count, reqs,
            BuildKeyPath(graph, source, free),
            gatedKeys)
        {
            UnprotectedRoomNames = ClosedRoomsOn(filter, graph, gatedKeys),
        };
    }

    // A route that walks plainly but whose lever detour doesn't: an exit on it opens
    // from other rooms, and the trip to one of them crosses an acquirable gate (a
    // hazard room with no counter carried, an item or key gate). BFS sees the route
    // as free, so Evaluate offers nothing, and the walk then fails at plan time.
    // Returns that walk as a sole gated choice, with the gates the lever trips
    // cross as its requirements, so the picker offers the same ways across it does
    // for a gate on the route itself: obtain the counter, or cross unprotected. Null
    // when the plain route expands fine, or when the detour can't be walked even
    // with every gate item in hand.
    public static RouteChoice? EvaluateLeverDetour(
        BfsMapper bfs,
        MovementFilter filter,
        RoomGraphManager graph,
        RoomKey source,
        RoomKey destination,
        Func<IReadOnlyList<Direction>?>? baseRoute = null)
    {
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(graph);

        IReadOnlyList<Direction>? free = baseRoute is { } bp ? bp() : bfs.FindPath(source, destination, filter);
        if (free is null || free.Count == 0) return null;
        if (RemoteActionPathExpander.ReachesDestination(
                RemoteActionPathExpander.Expand(graph, source, free, bfs, filter), destination))
            return null;

        // The rooms no item makes safe stay closed: a lever trip is never offered
        // across the lake's sea rooms either.
        IReadOnlyList<WalkStep> walk;
        using (filter.SuspendAcquirableGatesButUnprotectableHazards())
        {
            IReadOnlyList<Direction>? gated = bfs.FindPath(source, destination, filter);
            if (gated is null || gated.Count == 0) return null;
            walk = RemoteActionPathExpander.Expand(graph, source, gated, bfs, filter);
        }
        if (!RemoteActionPathExpander.ReachesDestination(walk, destination)) return null;

        // Gating is live again: every hop of the walk the filter blocks is a gate
        // the lever trips cross.
        var keys = new List<RoomKey> { source };
        var reqs = new List<RouteRequirement>();
        RoomKey cur = source;
        foreach (WalkStep step in walk)
        {
            if (step is not MoveStep move) continue;
            if (graph.GetRoom(cur) is { } room
                && room.Exits.TryGetValue(move.Direction, out RoomExit exit)
                && filter.IsExitBlocked(in exit)
                && Classify(filter, in exit) is { } req
                && !AlreadyHave(reqs, req))
                reqs.Add(req);
            cur = move.ExpectedTarget;
            keys.Add(cur);
        }
        if (reqs.Count == 0) return null;

        return new RouteChoice(0, keys.Count - 1, reqs, Array.Empty<RoomKey>(), keys)
        {
            GatedWalk = walk,
            UnprotectedRoomNames = ClosedRoomsOn(filter, graph, keys),
        };
    }

    // Compares the shortest route (teleport hops allowed — BFS treats an item /
    // CMD-cast teleport exit as a normal short edge) against the pure-walking
    // route (teleports refused). Returns a Teleport RouteChoice when the shortest
    // route takes a teleport the walking route avoids AND the walk is meaningfully
    // longer, so the user can weigh the teleport's shortcut against its danger — a
    // teleport can drop the crosser on a damaging plane or across water with no
    // boat, survivable or lethal depending on the character, a call only the user
    // can make. Null when the shortest route already walks the whole way (no
    // teleport to weigh), no walking route exists (teleport is the only way there,
    // so there's no fork), or the teleport saves too little to be worth the risk.
    public static RouteChoice? EvaluateTeleport(
        BfsMapper bfs,
        MovementFilter filter,
        RoomGraphManager graph,
        RoomKey source,
        RoomKey destination,
        // Memoized plain default route (see Evaluate). The teleports-allowed shortest
        // route IS that base route; reuse it rather than re-running the BFS.
        Func<IReadOnlyList<Direction>?>? baseRoute = null)
    {
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(graph);

        // Shortest route: teleports allowed (the walker's default plan).
        IReadOnlyList<Direction>? tele = baseRoute is { } bp ? bp() : bfs.FindPath(source, destination, filter);
        if (tele is null || tele.Count == 0) return null;

        // Nothing to weigh unless the shortest route actually teleports.
        if (FirstTeleportLanding(graph, source, tele) is not { } landing) return null;

        // Pure-walking route: teleports refused. No walking route → the teleport
        // is the only way there, so there's no walk-vs-teleport fork to offer.
        IReadOnlyList<Direction>? walk =
            bfs.FindPath(source, destination, filter, refuseTeleports: true);
        if (walk is null || walk.Count == 0) return null;

        // The teleport must shave enough walking to be worth weighing against its
        // danger — a one-room shortcut isn't worth a lethal-plane gamble.
        if (walk.Count - tele.Count < MinTeleportSavings) return null;

        // ...but if OBTAINING an acquirable gate item opens a WALKING route shorter than
        // the teleport itself, that route is the better answer — defer to Evaluate, which
        // surfaces the obtain/cross options, rather than framing the choice as a
        // lethal-teleport gamble. Same deferral the avoid-override fork uses, so a
        // buyable boat isn't hidden behind a teleport warning. (obtainable < tele can
        // only happen by crossing a suspended gate, so Evaluate is guaranteed to fire.)
        //
        // Teleports are refused for the comparison: an obtainable route that is shorter
        // only because it takes this same teleport is no alternative to it. Deferring on
        // one drops the teleport question altogether, and the item fork's picks commit
        // teleport-free — the walk then goes the long way round a portal nobody was
        // asked about.
        using (filter.SuspendAcquirableGatesButUnprotectableHazards())
            if (bfs.FindPath(source, destination, filter, refuseTeleports: true) is { Count: > 0 } obtainable
                && obtainable.Count < tele.Count)
                return null;

        return new RouteChoice(
            walk.Count, tele.Count,
            Array.Empty<RouteRequirement>(),
            BuildKeyPath(graph, source, walk),
            BuildKeyPath(graph, source, tele),
            RouteChoiceKind.Teleport,
            landing,
            TeleportFareCopper: RouteFareCopper(graph, source, tele));
    }

    // Compares the shortest route (by hops — the walker's default plan, which crosses
    // traps and disarms them at step time) against the fewest-traps route (avoid
    // every avoidable trap, cross only unavoidable ones). Returns a TrapAvoid
    // RouteChoice when the fewest-traps route crosses STRICTLY FEWER traps than the
    // shortest — so the user can take the safer route even when it's longer, and even
    // when some traps on the way are unavoidable (it still avoids the ones it can).
    // The fewest-traps route is the "free" (pre-selected) side; the shortest is the
    // "gated" side. No length floor — worth surfacing whenever it dodges a trap. Null
    // when the shortest route crosses no trap, or the fewest-traps route can't do any
    // better (every route crosses the same traps → nothing to avoid, no fork).
    public static RouteChoice? EvaluateTrapAvoid(
        BfsMapper bfs,
        MovementFilter filter,
        RoomGraphManager graph,
        RoomKey source,
        RoomKey destination,
        // Memoized plain default route (see Evaluate) — the "shortest by hops" route
        // is that same base route.
        Func<IReadOnlyList<Direction>?>? baseRoute = null)
    {
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(graph);

        // Shortest route: by hops (what the walker would otherwise take).
        IReadOnlyList<Direction>? shortest = baseRoute is { } bp ? bp() : bfs.FindPath(source, destination, filter);
        if (shortest is null || shortest.Count == 0) return null;

        int shortestTraps = bfs.CountTrapsOnPath(source, shortest);
        if (shortestTraps == 0) return null;   // nothing to avoid

        // Fewest-traps route: avoids every avoidable trap, keeps only unavoidable
        // ones. Same destination reachable (it's a cost search, not refuse-all).
        IReadOnlyList<Direction>? fewest =
            bfs.FindPath(source, destination, filter, avoidTraps: true);
        if (fewest is null || fewest.Count == 0) return null;

        int fewestTraps = bfs.CountTrapsOnPath(source, fewest);
        if (fewestTraps >= shortestTraps) return null;   // can't dodge any → no fork

        // ...but if OBTAINING a gate item opens a route that is both shorter AND
        // trap-free, that beats trusting a step-time disarm — defer to Evaluate so it
        // surfaces the obtain/cross options. Require trap-free: an obtainable route that
        // still crosses a trap must keep this warning, so only a clean route defers.
        using (filter.SuspendAcquirableGatesButUnprotectableHazards())
            if (bfs.FindPath(source, destination, filter) is { Count: > 0 } obtainable
                && obtainable.Count < shortest.Count
                && bfs.CountTrapsOnPath(source, obtainable) == 0)
                return null;

        return new RouteChoice(
            fewest.Count, shortest.Count,
            Array.Empty<RouteRequirement>(),
            BuildKeyPath(graph, source, fewest),
            BuildKeyPath(graph, source, shortest),
            RouteChoiceKind.TrapAvoid,
            FreeTrapCount: fewestTraps,
            GatedTrapCount: shortestTraps);
    }

    // Compares the route honouring the user's "avoid this room" list against the
    // route that lifts ONLY those avoids (every real gate — level / toll / class /
    // item / ticket / key / hazard — still honoured). Returns an AvoidOverride
    // RouteChoice in two shapes:
    //   • SOLE — no avoid-honouring route exists at all, but lifting the avoids opens
    //     one: the destination is reachable ONLY through a marked-avoid room. The free
    //     side renders as a disabled "no route that respects your avoids" note; the
    //     override route is the sole option, warned by its avoided-room count. NOT
    //     offered when suspending the acquirable gates opens an avoid-respecting route
    //     (a raft-crossable river, a keyable door): that's Evaluate's obtain/cross
    //     story, which honours the avoids, so overriding them would be the wrong ask.
    //   • TWO-ROUTE — an avoid-honouring route exists but a route through avoided
    //     rooms is meaningfully shorter (>= MinAvoidOverrideSavings). The avoid-
    //     honouring route is the pre-selected "free" side; the shorter avoid-crossing
    //     route is the "gated" side. The user can keep their avoid or override it.
    // Null when lifting avoids opens nothing new (the block is a real gate / genuine
    // disconnect — Evaluate / PlanBlocked handle those), or the shorter avoid route
    // doesn't save enough to be worth crossing a deliberately-excluded room. Never
    // fires when the avoid-honouring route is already the shortest (no avoided room
    // on it → nothing to override).
    public static RouteChoice? EvaluateAvoidOverride(
        BfsMapper bfs,
        MovementFilter filter,
        RoomGraphManager graph,
        RoomKey source,
        RoomKey destination,
        // Memoized plain default route (see Evaluate) — the avoid-honouring "free"
        // route is that base route.
        Func<IReadOnlyList<Direction>?>? baseRoute = null,
        // Memoized avoids-lifted route (ignoreAvoids), shared with AvoidAlternative so
        // the ignore-avoids BFS runs once across both.
        Func<IReadOnlyList<Direction>?>? avoidLiftedRoute = null,
        // How many steps the avoid-crossing route must save before the override is
        // offered beside an avoid-honouring one. A walk that asks only over avoids
        // passes 1: any avoid on the shortest route is the player's call.
        int minSavings = MinAvoidOverrideSavings)
    {
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(graph);

        // Route lifting ONLY the avoids (every real gate stays honoured). If even
        // that finds nothing, an avoid isn't the wall — bail so Evaluate / PlanBlocked
        // can name the real gate or disconnect.
        IReadOnlyList<Direction>? overrideRoute = avoidLiftedRoute is { } ap
            ? ap() : bfs.FindPath(source, destination, filter, ignoreAvoids: true);
        if (overrideRoute is null || overrideRoute.Count == 0) return null;

        IReadOnlyList<RoomKey> overrideKeys = BuildKeyPath(graph, source, overrideRoute);
        IReadOnlyList<string> avoidedNames = AvoidedOnPath(filter, graph, overrideKeys);
        int avoidedCrossed = avoidedNames.Count;
        if (avoidedCrossed == 0) return null;   // route doesn't touch an avoided room — no override story

        // Route honouring the avoids, gates active. Null → no gate-free avoid-
        // respecting route (the potential SOLE case).
        IReadOnlyList<Direction>? free = baseRoute is { } bp ? bp() : bfs.FindPath(source, destination, filter);
        bool hasFree = free is { Count: > 0 };

        if (!hasFree)
        {
            // Before declaring the avoided room the only way there, check whether the
            // real wall is an ACQUIRABLE gate rather than the avoid: BFS with the
            // item / ticket / key / hazard gates suspended but the avoids STILL
            // honoured. If THAT reaches the destination, an avoid-respecting route
            // exists once the gate item is obtained (buy a raft two rooms away, cross
            // the river) — so overriding a deliberate avoid is the wrong ask. Defer to
            // Evaluate, which surfaces the obtain / cross-unprotected / search-en-route
            // options, all of which respect the avoids. (Report
            // paradigm-20260907-212758: a raft-crossable river route existed, but the
            // picker offered only "no route respects your avoids" because this case
            // jumped straight to the override without weighing an obtainable counter.)
            using (filter.SuspendAcquirableGatesButUnprotectableHazards())
                if (bfs.FindPath(source, destination, filter) is { Count: > 0 })
                    return null;

            return new RouteChoice(
                0, overrideRoute.Count,
                Array.Empty<RouteRequirement>(),
                Array.Empty<RoomKey>(),
                overrideKeys,
                RouteChoiceKind.AvoidOverride,
                AvoidedRoomCount: avoidedCrossed,
                AvoidedRoomNames: avoidedNames);
        }

        // TWO-ROUTE: an avoid-honouring route exists — only offer the override when
        // it's meaningfully shorter, or the user's deliberate avoid stands.
        if (free!.Count - overrideRoute.Count < minSavings) return null;

        // ...but if OBTAINING an acquirable gate item opens an avoid-respecting route
        // shorter than even the avoid-crossing override, that's the better answer —
        // defer to Evaluate, which surfaces the obtain / cross options (all of which
        // honour the avoids), rather than asking the user to override a deliberate
        // avoid for a marginal saving. This is the two-route twin of the sole-case
        // deferral above. (Report paradigm-20260915-182554: standing at the boatman —
        // a shop that sells boats — a walk to the river-turtle boss offered only a
        // 423-step avoid-honouring detour vs a 421-step through-avoids route, and never
        // the far shorter "buy a boat and sail the river" route, because this fork
        // returned before Evaluate could weigh the obtainable water counter.)
        using (filter.SuspendAcquirableGatesButUnprotectableHazards())
            if (bfs.FindPath(source, destination, filter) is { Count: > 0 } obtainable
                && obtainable.Count < overrideRoute.Count)
                return null;

        return new RouteChoice(
            free.Count, overrideRoute.Count,
            Array.Empty<RouteRequirement>(),
            BuildKeyPath(graph, source, free),
            overrideKeys,
            RouteChoiceKind.AvoidOverride,
            AvoidedRoomCount: avoidedCrossed,
            AvoidedRoomNames: avoidedNames);
    }

    // A walk the user starts whose shortest route passes THROUGH a boss room flagged
    // "stop before entering" on the way to somewhere else (a walk that ends in one
    // already stops a room short). Returns the three ways to take it — around every
    // such room, up to the first one and wait, or straight through — when a way
    // around exists. Null when the route meets no such room, or there is no way
    // around: the walk then simply stops before the boss room, with nothing to pick.
    //
    // A boss room that is the very next step doesn't count: standing beside it and
    // asking to walk on past is the go-ahead to enter.
    public static RouteChoice? EvaluateBossRoom(
        BfsMapper bfs,
        IRoomFilter filter,
        RoomGraphManager graph,
        RoomKey source,
        RoomKey destination,
        IReadOnlySet<RoomKey> stopRooms,
        Func<IReadOnlyList<Direction>?>? baseRoute = null)
    {
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(stopRooms);
        if (stopRooms.Count == 0) return null;

        IReadOnlyList<Direction>? through = baseRoute is { } bp ? bp() : bfs.FindPath(source, destination, filter);
        if (through is not { Count: > 0 }) return null;
        IReadOnlyList<RoomKey> throughKeys = BuildKeyPath(graph, source, through);

        int bossAt = FirstBossRoomAhead(throughKeys, stopRooms);
        if (bossAt < 0) return null;

        // The way around avoids every stop-before boss room but the destination
        // itself, not just the first one met: a detour past one that runs into the
        // next would be no way around.
        HashSet<RoomKey> around = new(stopRooms);
        around.Remove(destination);
        around.Remove(source);
        IReadOnlyList<Direction>? detour = bfs.FindPath(source, destination, new WalkAroundRoomsFilter(filter, around));
        if (detour is not { Count: > 0 }) return null;

        return new RouteChoice(
            detour.Count, through.Count,
            Array.Empty<RouteRequirement>(),
            BuildKeyPath(graph, source, detour),
            throughKeys,
            RouteChoiceKind.BossRoom,
            BossRoom: throughKeys[bossAt],
            BossWaitPath: throughKeys.Take(bossAt).ToList());
    }

    // Index on a key path of the first stop-before boss room a walk would stop for:
    // past the next room, and short of the destination. -1 when there is none.
    private static int FirstBossRoomAhead(IReadOnlyList<RoomKey> keys, IReadOnlySet<RoomKey> stopRooms)
    {
        for (int i = 2; i < keys.Count - 1; i++)
            if (stopRooms.Contains(keys[i])) return i;
        return -1;
    }

    // The avoid-crossing route to offer as an EXTRA card alongside a hazard/gate
    // choice that already respects the avoids. It's the ignore-avoids route (every
    // real gate still honoured) when that route actually crosses ≥1 marked room —
    // "plow through your avoided rooms" needs no raft, so it's a genuine alternative
    // to the obtain/cross options. Null when lifting the avoids opens nothing new
    // (the avoids aren't the wall) or the route touches no avoided room. The caller
    // attaches it only to an item-gate/hazard choice (whose own routes respect the
    // avoids), so the two never describe the same path.
    public static (IReadOnlyList<RoomKey> Path, int AvoidedCount, IReadOnlyList<string> AvoidedNames)? AvoidAlternative(
        BfsMapper bfs,
        MovementFilter filter,
        RoomGraphManager graph,
        RoomKey source,
        RoomKey destination,
        // Memoized avoids-lifted route, shared with EvaluateAvoidOverride so the
        // ignore-avoids BFS runs once across both.
        Func<IReadOnlyList<Direction>?>? avoidLiftedRoute = null)
    {
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(graph);

        IReadOnlyList<Direction>? route = avoidLiftedRoute is { } ap
            ? ap() : bfs.FindPath(source, destination, filter, ignoreAvoids: true);
        if (route is null || route.Count == 0) return null;

        IReadOnlyList<RoomKey> keys = BuildKeyPath(graph, source, route);
        IReadOnlyList<string> crossed = AvoidedOnPath(filter, graph, keys);
        return crossed.Count > 0 ? (keys, crossed.Count, crossed) : null;
    }

    // The rooms on a key path (excluding the source the walker already stands in)
    // the user marked "avoid", named in route order: what the override card warns
    // with, one entry per crossing.
    private static IReadOnlyList<string> AvoidedOnPath(
        MovementFilter filter, RoomGraphManager graph, IReadOnlyList<RoomKey> keys)
    {
        List<string> crossed = new();
        for (int i = 1; i < keys.Count; i++)   // skip source
        {
            if (!filter.IsAvoided(keys[i])) continue;
            string where = $"{keys[i].Map}/{keys[i].Room}";
            crossed.Add(graph.GetRoom(keys[i])?.Name is { Length: > 0 } name ? $"{name} ({where})" : where);
        }
        return crossed;
    }

    // The one route card that crosses rooms closed to routes: a walk the user starts
    // to a room that itself teleports on arrival and lies beyond them. In the data
    // that is one room, on Paradigm: the Crystal Lake room whose spell sends you on
    // to the Bloodwood Weald. The user, 2026-10-10: only typed moves enter the lake
    // "unless the user points to the bloodwood weald teleport room or the ancient
    // galleon", and for that room "both lvl 50 and raft or skiff together"
    // (IRoomFilter.CrossingTerms, which FindCrossing holds every crossed room to).
    // Null for any other destination and for a crosser who doesn't meet the terms:
    // there is then no route, and the walk's failure says what is missing. Only the
    // route picker calls this, so a walk the client starts is never offered it.
    private static RouteChoice? CrossingCard(
        BfsMapper bfs, MovementFilter filter, RoomGraphManager graph, RoomKey source, RoomKey destination)
    {
        if (!filter.TeleportsOnArrival(destination)) return null;
        if (bfs.FindCrossing(source, destination, filter) is not { Count: > 0 } crossing) return null;

        IReadOnlyList<RoomKey> keys = BuildKeyPath(graph, source, crossing);
        IReadOnlyList<string> crossed = UnprotectedOnPath(filter, graph, keys);
        if (crossed.Count == 0) return null;
        return new RouteChoice(
            0, crossing.Count, CollectRequirements(graph, filter, source, crossing), Array.Empty<RoomKey>(), keys)
        {
            UnprotectedRoomNames = crossed,
        };
    }

    // The rooms on a key path that no item makes safe to route through, named in
    // route order, the room the walker stands in left out.
    private static IReadOnlyList<string> UnprotectedOnPath(
        MovementFilter filter, RoomGraphManager graph, IReadOnlyList<RoomKey> keys)
    {
        List<string> rooms = new();
        for (int i = 1; i < keys.Count; i++)
            if (filter.IsClosedToRoutes(keys[i])) rooms.Add(RoomLabel(graph, keys[i]));
        return rooms;
    }

    // The rooms closed to routes that a card's route enters, for the card to name;
    // null when it enters none. A crossing the plain walk makes (a place the map
    // reaches only across the lake) can sit on a card for another gate on its way.
    private static IReadOnlyList<string>? ClosedRoomsOn(
        MovementFilter filter, RoomGraphManager graph, IReadOnlyList<RoomKey> keys) =>
        UnprotectedOnPath(filter, graph, keys) is { Count: > 0 } rooms ? rooms : null;

    // "Black Wasteland (3/740), Black Wasteland (3/669) and 2 more" for a card or a
    // log line; empty when no names were recorded.
    public static string ListAvoided(IReadOnlyList<string>? names, int show = 6)
    {
        if (names is not { Count: > 0 }) return string.Empty;
        string listed = string.Join(", ", names.Take(show));
        return names.Count > show ? $"{listed} and {names.Count - show} more" : listed;
    }

    // When every route to the destination is blocked — no gate-free route, and
    // even suspending the acquirable gates (item/ticket/key/hazard) doesn't open
    // one — but the destination is physically reachable if the block weren't there,
    // plan a "run to the blocked room anyway": the furthest room the walker can
    // actually reach toward it, plus the exit that stops it. Null when the
    // destination is already reachable (nothing to run up to), when the acquirable
    // gated picker already covers it (that's Evaluate's job), when it's genuinely
    // disconnected, or when the block sits right at the current room (nowhere to
    // walk). The physical route is the shortest with every gate ignored; the
    // walker halts at the first exit the LIVE filter still blocks along it.
    public static BlockedRoutePlan? PlanBlocked(
        BfsMapper bfs,
        MovementFilter filter,
        RoomGraphManager graph,
        RoomKey source,
        RoomKey destination)
    {
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(graph);

        // Reachable outright, or reachable by clearing an acquirable gate → not a
        // "blocked, run anyway" case (the plain walk / gated picker handle those).
        if (bfs.FindPath(source, destination, filter) is { Count: > 0 }) return null;
        using (filter.SuspendAcquirableGates())
            if (bfs.FindPath(source, destination, filter) is { Count: > 0 }) return null;

        // Level-blocked first: would the destination be reachable if the character
        // were high enough level, with every OTHER gate still honoured? Then the
        // real barrier is a level gate — name that, not whatever incidental gate an
        // all-gates-lifted physical route below happens to cross (e.g. the Ancient
        // Fortress, whose only real way in is a level-75 exit but whose graph is
        // also "reachable" through an impassable pyramid door and one-way pit drops).
        //
        // Refuse teleports: a level-gated teleport is usually a SHORTCUT a blocked
        // walker would route around (report paradigm-20260914-201123 named a
        // level-40 portal shortcut instead of the destination's own level-75 gate),
        // so explain the block via the walked route the character actually commits
        // to. Fall back to allowing teleports only when one is the sole way in.
        LevelIgnoringFilter levelIgnoring = new(filter);
        IReadOnlyList<Direction>? levelLifted =
            bfs.FindPath(source, destination, levelIgnoring, refuseTeleports: true)
            ?? bfs.FindPath(source, destination, levelIgnoring);
        if (levelLifted is { Count: > 0 }
            && FirstBlockOnRoute(graph, filter, source, levelLifted, ExitBlockReason.Level) is { } lg)
            return lg;

        // The route the walk would take with nothing in the way — teleport
        // shortcuts refused first, for the same reason as above.
        IReadOnlyList<Direction>? physical =
            bfs.FindPath(source, destination, filter, ignoreExitGates: true, refuseTeleports: true)
            ?? bfs.FindPath(source, destination, filter, ignoreExitGates: true);
        if (physical is null || physical.Count == 0) return null;   // truly disconnected

        // Follow it under the live filter; stop at the first exit that still blocks.
        RoomKey cur = source;
        var reached = new List<RoomKey> { source };
        foreach (Direction dir in physical)
        {
            Room? room = graph.GetRoom(cur);
            if (room is null || !room.Exits.TryGetValue(dir, out RoomExit exit)) break;
            if (filter.IsExitBlocked(in exit))
            {
                if (cur.Equals(source)) return null;   // blocked at the doorstep — nowhere to run
                return new BlockedRoutePlan(cur, dir, exit, reached);
            }
            cur = exit.Target;
            reached.Add(cur);
        }
        return null;   // nothing blocked along the physical route (unexpected when free==null)
    }

    // Walk `route` from `source` under the live filter and return a run-to-block
    // plan for the first exit whose block reasons intersect `mask`. Null when that
    // block sits at the doorstep (nowhere to run to) or the route can't be
    // followed / carries no matching block.
    private static BlockedRoutePlan? FirstBlockOnRoute(
        RoomGraphManager graph, MovementFilter filter, RoomKey source,
        IReadOnlyList<Direction> route, ExitBlockReason mask)
    {
        RoomKey cur = source;
        var reached = new List<RoomKey> { source };
        foreach (Direction dir in route)
        {
            Room? room = graph.GetRoom(cur);
            if (room is null || !room.Exits.TryGetValue(dir, out RoomExit exit)) break;
            if ((filter.DescribeExitBlock(in exit) & mask) != ExitBlockReason.None)
            {
                if (cur.Equals(source)) return null;   // at the doorstep — nowhere to run
                return new BlockedRoutePlan(cur, dir, exit, reached);
            }
            cur = exit.Target;
            reached.Add(cur);
        }
        return null;
    }

    // The first teleport hop's landing-room label ("Silver River (12/34)"), or
    // null when the route takes no teleport. Names both item / CMD-cast teleport
    // exits and gateway portals — either can shortcut a walk.
    private static string? FirstTeleportLanding(
        RoomGraphManager graph, RoomKey source, IReadOnlyList<Direction> path)
    {
        RoomKey cur = source;
        foreach (Direction dir in path)
        {
            Room? room = graph.GetRoom(cur);
            if (room is null || !room.Exits.TryGetValue(dir, out RoomExit exit)) break;
            if (AutomaticWalkTeleportFilter.IsTeleport(in exit)) return RoomLabel(graph, exit.Target);
            cur = exit.Target;
        }
        return null;
    }

    private static string RoomLabel(RoomGraphManager graph, RoomKey key) =>
        graph.GetRoom(key)?.Name is { Length: > 0 } name ? $"{name} ({key})" : key.ToString();

    // Where a card's route teleports: the landing of each hop only a teleport makes,
    // in order; empty when it walks all the way. Decides how a picked card's walk is
    // planned (a walk that prefers going on foot would leave a route that teleports
    // for a far longer one) and what the card says.
    //
    // A card holds its route as the rooms it passes, not the exits taken, so unlike
    // FirstTeleportLanding this reads each hop back from the room: it teleports when
    // a teleport leads to the next room and no exit the crosser can walk does. (With
    // a passable exit beside the teleport, a walk that prefers going on foot takes
    // it and passes the same rooms, so the hop is no teleport.)
    public static IReadOnlyList<string> TeleportLandings(
        RoomGraphManager graph, IReadOnlyList<RoomKey>? keys, IRoomFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var landings = new List<string>();
        if (keys is null) return landings;
        for (int i = 0; i + 1 < keys.Count; i++)
        {
            if (graph.GetRoom(keys[i]) is not { } room) break;
            RoomKey next = keys[i + 1];
            bool walks = false, teleports = false;
            foreach (RoomExit exit in room.Exits.Values)
            {
                if (!exit.Target.Equals(next)) continue;
                if (AutomaticWalkTeleportFilter.IsTeleport(in exit)) teleports = true;
                else if (filter is null || !filter.IsExitBlocked(in exit)) walks = true;
            }
            if (teleports && !walks) landings.Add(RoomLabel(graph, next));
        }
        return landings;
    }

    // The hazard rooms on a card's route the crosser holds no counter for, in route
    // order: what picking a card that crosses them agrees to, room by room. The walk
    // is given this list, not a general licence, so nothing but these rooms opens
    // to it later (AutoWalkManager.WalkTo's agreedHazardRooms).
    public static IReadOnlyList<RoomKey> UncounteredHazardRooms(MovementFilter filter, IReadOnlyList<RoomKey>? keys)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var rooms = new List<RoomKey>();
        if (keys is null) return rooms;
        for (int i = 1; i < keys.Count; i++)   // the room they stand in is not one they step into
            if (!rooms.Contains(keys[i]) && filter.IsUncounteredHazardRoom(keys[i]))
                rooms.Add(keys[i]);
        return rooms;
    }

    // Whether the walk a picked card starts prefers to go on foot (a teleport only
    // when walking is impossible, on this plan and on every re-plan). True for a
    // route that walks. A route that teleports was shown doing so and picked, and
    // preferring to walk would trade it for whatever way on foot exists, however
    // long, which no card showed: a 45-step card was walked as 263 steps (report
    // paradigm-20261008-174236). "Walk it" (avoidTeleports) is never overridden.
    public static bool PickedWalkPrefersTeleportFree(
        bool requested, bool avoidTeleports, IReadOnlyList<string> pickedRouteTeleports)
    {
        ArgumentNullException.ThrowIfNull(pickedRouteTeleports);
        return requested && (avoidTeleports || pickedRouteTeleports.Count == 0);
    }

    // The route the walk a sole-route card commits will plan, so the card shows that
    // route and not another of the same length. Mirrors the walker: shortest first
    // and, when that walks all the way, the search with teleports refused, which is
    // the one a walk preferring to go on foot runs. Call it inside the gate
    // suspension the walk will plan under.
    private static IReadOnlyList<Direction>? PlanAsTheWalkWill(
        BfsMapper bfs, RoomGraphManager graph, MovementFilter filter,
        RoomKey source, RoomKey destination, bool avoidTraps)
    {
        IReadOnlyList<Direction>? shortest = bfs.FindPath(source, destination, filter, avoidTraps: avoidTraps);
        if (shortest is not { Count: > 0 }) return null;
        if (TeleportLandings(graph, BuildKeyPath(graph, source, shortest), filter).Count > 0) return shortest;
        return bfs.FindPath(source, destination, filter, refuseTeleports: true, avoidTraps: avoidTraps) ?? shortest;
    }

    // "the teleport to Stone Tunnel (2/1306)" / "teleports to A, B and C", for a card
    // or a log line; null when there are none.
    public static string? DescribeTeleports(IReadOnlyList<string> landings) => landings.Count switch
    {
        0 => null,
        1 => $"the teleport to {landings[0]}",
        _ => $"teleports to {string.Join(", ", landings.Take(landings.Count - 1))} and {landings[^1]}",
    };

    // Total per-person fare of the paid transports a planned route crosses (0 when
    // none). The filter already refused any fare the party can't cover, so this is
    // what the route will cost each member, stated on the card.
    private static long RouteFareCopper(RoomGraphManager graph, RoomKey source, IReadOnlyList<Direction> path)
    {
        long fare = 0;
        RoomKey cur = source;
        foreach (Direction dir in path)
        {
            Room? room = graph.GetRoom(cur);
            if (room is null || !room.Exits.TryGetValue(dir, out RoomExit exit)) break;
            fare += exit.FareCopper;
            cur = exit.Target;
        }
        return fare;
    }

    // Expand a planned direction list to the RoomKey sequence it visits (source
    // first, then each hop's target) for the picker's map preview. Stops at the
    // first hop the graph can't resolve — a defensive guard; a freshly-planned
    // BFS path is always resolvable end to end.
    internal static IReadOnlyList<RoomKey> BuildKeyPath(
        RoomGraphManager graph, RoomKey source, IReadOnlyList<Direction> path)
    {
        var keys = new List<RoomKey>(path.Count + 1) { source };
        RoomKey cur = source;
        foreach (Direction dir in path)
        {
            Room? room = graph.GetRoom(cur);
            if (room is null || !room.Exits.TryGetValue(dir, out RoomExit exit)) break;
            cur = exit.Target;
            keys.Add(cur);
        }
        return keys;
    }

    // ClosedGateItems: the optional gates CommittedPath was planned round (empty when
    // the shortest route has none, or they turned out to be needed after all).
    private readonly record struct GateClassification(
        IReadOnlyList<Direction> CommittedPath,
        List<RouteRequirement> Requirements,
        IReadOnlyList<Direction>? ShortcutPath,
        IReadOnlyList<int> ClosedGateItems);

    // Separates a sole route's item gates into genuinely-required (the destination is
    // unreachable without them), optional shortcuts (a longer route avoids them), and
    // already-carried. Returns the committed route that avoids every optional shortcut
    // — so the walk takes the reliable way and its carried gates surface — its tagged
    // requirements, and (when a real saving exists) the shortcut route + the optional
    // items it needs. Optionality is a topology fact (does a route avoiding the gate
    // exist?), independent of what the crosser currently carries.
    // The route was planned with the rooms no item makes safe kept closed, so every
    // probe here keeps them closed too: a gate is optional only when the way round it
    // also stays out of them.
    private static GateClassification ClassifyGates(
        BfsMapper bfs, MovementFilter filter, RoomGraphManager graph,
        RoomKey source, RoomKey destination, IReadOnlyList<Direction> gated)
    {
        // Distinct item-gate ids on the shortest all-suspended route that actually
        // stop the crosser. A locked door they can pick or bash is no gate: its key
        // isn't held, but the shortcut card then named a gate key for a door a thief
        // opens by hand (report paradigm-20261008-174023).
        var unheld = new List<int>();
        foreach ((RouteRequirement req, bool held, bool blocked) in ItemGatesOnPath(graph, filter, source, gated))
            if (!held && blocked)
                foreach (int id in req.ItemIds)
                    if (id > 0 && !unheld.Contains(id)) unheld.Add(id);

        // An unheld gate is an OPTIONAL shortcut when the destination is still reachable
        // with it kept closed (a longer route avoids it).
        var optional = new List<int>();
        foreach (int id in unheld)
        {
            IReadOnlyList<Direction>? around;
            using (filter.SuspendAcquirableGatesExceptUnprotectable(new[] { id }))
                around = bfs.FindPath(source, destination, filter);
            if (around is not null) optional.Add(id);
        }

        if (optional.Count == 0)
            return new(gated, TaggedRequirements(graph, filter, source, gated), null, Array.Empty<int>());

        // Re-plan avoiding EVERY optional shortcut at once. If that disconnects the
        // destination the optionals were mutually substitutable ("need one of them") —
        // fall back to the original route + all-required so nothing is under-reported.
        // Planned as the walk the card starts will plan it (fewest traps: a sole
        // route's commit passes avoidTraps).
        IReadOnlyList<Direction>? committed;
        using (filter.SuspendAcquirableGatesExceptUnprotectable(optional))
            committed = PlanAsTheWalkWill(bfs, graph, filter, source, destination, avoidTraps: true);
        if (committed is null || committed.Count == 0)
            return new(gated, TaggedRequirements(graph, filter, source, gated), null, Array.Empty<int>());

        IReadOnlyList<Direction>? shortcut = committed.Count > gated.Count ? gated : null;
        return new(committed, TaggedRequirements(graph, filter, source, committed), shortcut, optional);
    }

    // The committed route's requirements: the still-blocked gates + hazards (required,
    // as CollectRequirements sees them) PLUS the item-gates the crosser already carries,
    // surfaced with Carried=true so the list is complete rather than silently dropping a
    // satisfied gate.
    private static List<RouteRequirement> TaggedRequirements(
        RoomGraphManager graph, MovementFilter filter, RoomKey source, IReadOnlyList<Direction> path)
    {
        List<RouteRequirement> reqs = CollectRequirements(graph, filter, source, path);
        foreach ((RouteRequirement req, bool held, _) in ItemGatesOnPath(graph, filter, source, path))
            if (held && !AlreadyHave(reqs, req))
                reqs.Add(req with { Carried = true });
        return reqs;
    }

    // Every item-gate exit on a path, with whether the crosser carries all its item(s)
    // and whether the exit stops them as things stand (a keyed door they can pick is
    // unheld and not blocked). Item gates only
    // (Item/Ticket/KeyLocked/MultiActionHidden/Teleport) — hazards and plain doors
    // are not carryable possession gates.
    private static IEnumerable<(RouteRequirement Req, bool Held, bool Blocked)> ItemGatesOnPath(
        RoomGraphManager graph, MovementFilter filter, RoomKey source, IReadOnlyList<Direction> path)
    {
        RoomKey cur = source;
        foreach (Direction dir in path)
        {
            Room? room = graph.GetRoom(cur);
            if (room is null || !room.Exits.TryGetValue(dir, out RoomExit exit)) break;
            if (ClassifyItemGate(in exit) is { } req)
            {
                // Same reading of "held" the filter's gate uses, so a gate the party
                // is short for lists as needed and not as "you have it".
                bool perMember = exit.Hint is RoomExitHint.Item or RoomExitHint.Ticket;
                bool held = req.ItemIds.Count > 0 && req.ItemIds.All(id => filter.HoldsGateItem(id, perMember));
                // Stopped for want of the item, and for nothing else: a door they can
                // pick that opens into a hazard room is blocked by the hazard, and its
                // key would open nothing.
                bool blocked = (filter.DescribeExitBlock(in exit)
                    & (ExitBlockReason.Item | ExitBlockReason.LockedDoor)) != ExitBlockReason.None;
                yield return (req, held, blocked);
            }
            cur = exit.Target;
        }
    }

    // The item-possession subset of Classify (no hazard fallback) — the gate items an
    // exit demands in hand, or null for a plain / hazard exit.
    private static RouteRequirement? ClassifyItemGate(in RoomExit exit) => exit.Hint switch
    {
        RoomExitHint.Item when exit.KeyItemId > 0 =>
            new RouteRequirement(RouteRequirementKind.CarryItem, new[] { exit.KeyItemId }),
        RoomExitHint.Ticket when exit.KeyItemId > 0 =>
            new RouteRequirement(RouteRequirementKind.Ticket, new[] { exit.KeyItemId }),
        RoomExitHint.KeyLocked when exit.KeyItemId > 0 =>
            new RouteRequirement(RouteRequirementKind.DoorKey, new[] { exit.KeyItemId }),
        RoomExitHint.MultiActionHidden or RoomExitHint.Teleport
            when ExitGateItems.Of(in exit) is { Count: > 0 } gateItems =>
            new RouteRequirement(RouteRequirementKind.CarryItem, gateItems),
        _ when exit.CastGateItemId > 0 =>
            new RouteRequirement(RouteRequirementKind.CarryItem, new[] { exit.CastGateItemId }),
        _ => null,
    };

    // Walk the direct path hop by hop; every hop the live filter still blocks is
    // an acquirable gate (level/toll/class gates were never suspended, so the
    // direct route can't contain one). Classify and dedupe.
    private static List<RouteRequirement> CollectRequirements(
        RoomGraphManager graph,
        MovementFilter filter,
        RoomKey source,
        IReadOnlyList<Direction> gated)
    {
        var reqs = new List<RouteRequirement>();
        foreach ((_, _, RouteRequirement req) in PositionedGates(graph, filter, source, gated))
            if (!AlreadyHave(reqs, req)) reqs.Add(req);
        return reqs;
    }

    // Every still-blocked hop on a direction path, WITH its position — the room it's
    // crossed from and the direction — so the route step list can drop an acquire
    // row at exactly the hop that needs it. Undeduped (a re-crossed gate lists twice;
    // the step list announces it once). The positional twin of CollectRequirements.
    public static IReadOnlyList<(RoomKey Room, Direction Dir, RouteRequirement Requirement)> PositionedGates(
        RoomGraphManager graph,
        MovementFilter filter,
        RoomKey source,
        IReadOnlyList<Direction> gated)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(gated);

        var gates = new List<(RoomKey, Direction, RouteRequirement)>();
        RoomKey cur = source;
        foreach (Direction dir in gated)
        {
            Room? room = graph.GetRoom(cur);
            if (room is null || !room.Exits.TryGetValue(dir, out RoomExit exit)) break;
            if (filter.IsExitBlocked(in exit) && Classify(filter, in exit) is { } req)
                gates.Add((cur, dir, req));
            cur = exit.Target;
        }
        return gates;
    }

    // The gate items on a chosen route the acquisition pipeline can actually source,
    // deduped in requirement order. Hazard counters are excluded: the picker
    // resolves those itself (it may grab one off the current floor rather than
    // detour) and forces them on its own path.
    //
    // Flag-independent by design — an explicit "obtain then cross" pick IS the
    // consent, the same rule the hazard counters already follow. keyHasSource gates
    // door keys: a key is worth arming for only when something reliably yields it
    // (a summoned guaranteed dropper, an NPC's hand-over, a shop), since any other
    // key has no source and forcing it would just switch on a per-room search that
    // can never succeed.
    //
    // Static so the rule can be pinned without standing up AppServices, which owns
    // the key-source lookup this defers to.
    public static IReadOnlyList<int> SourceableGateItems(
        IReadOnlyList<RouteRequirement> requirements, Func<int, bool> keyHasSource)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(keyHasSource);

        var ids = new List<int>();
        foreach (RouteRequirement req in requirements)
        {
            if (req.Kind is RouteRequirementKind.HazardProtection) continue;
            // Never source an optional shortcut item (may be unsourceable) or one the
            // crosser already carries.
            if (req.Carried) continue;
            foreach (int id in req.ItemIds)
            {
                if (id <= 0 || ids.Contains(id)) continue;
                if (req.Kind == RouteRequirementKind.DoorKey && !keyHasSource(id)) continue;
                ids.Add(id);
            }
        }
        return ids;
    }

    private static RouteRequirement? Classify(MovementFilter filter, in RoomExit exit) => exit.Hint switch
    {
        RoomExitHint.Item when exit.KeyItemId > 0 =>
            new RouteRequirement(RouteRequirementKind.CarryItem, new[] { exit.KeyItemId }),
        RoomExitHint.Ticket when exit.KeyItemId > 0 =>
            new RouteRequirement(RouteRequirementKind.Ticket, new[] { exit.KeyItemId }),
        // Only when it is the lock that stops them: a door they can pick or bash
        // that opens into a hazard room is closed by the hazard, and asking for its
        // key would name the wrong thing to bring.
        RoomExitHint.KeyLocked when exit.KeyItemId > 0
            && filter.DescribeExitBlock(in exit).HasFlag(ExitBlockReason.LockedDoor) =>
            new RouteRequirement(RouteRequirementKind.DoorKey, new[] { exit.KeyItemId }),
        // A hidden exit whose unlock action needs a held item, and an item-use
        // teleport, are both plain possession gates — the item just isn't in
        // KeyItemId for the multi-action case. Without these the gate fell through
        // to HazardRequirement, returned null, and vanished from the requirement
        // list: the picker offered a route through the Lower Caverns bloodstone-orb
        // exit without ever mentioning the orb (report paradigm-20260911-010954).
        RoomExitHint.MultiActionHidden or RoomExitHint.Teleport
            when ExitGateItems.Of(in exit) is { Count: > 0 } gateItems =>
            new RouteRequirement(RouteRequirementKind.CarryItem, gateItems),
        // A cast-on-walk exit that lands the crosser on the routed side only with an
        // item in hand (the golden idol's passage).
        _ when exit.CastGateItemId > 0 && !filter.HoldsGateItem(exit.CastGateItemId, perMember: false) =>
            new RouteRequirement(RouteRequirementKind.CarryItem, new[] { exit.CastGateItemId }),
        // A plain cardinal the filter still blocks is a hazard-room entry: resolve
        // the room's cast-on-enter spell to its any-of counter items.
        _ => HazardRequirement(filter, exit.Target),
    };

    private static RouteRequirement? HazardRequirement(MovementFilter filter, RoomKey target)
    {
        if (filter.Hazards is not { } hazards || filter.RoomEntrySpellProbe is not { } spellOf)
            return null;

        int spell = spellOf(target);
        if (hazards.HazardForSpell(spell) is not { } hazard) return null;

        IReadOnlyList<int> items = hazard.ProtectingItems;
        return items.Count > 0
            ? new RouteRequirement(RouteRequirementKind.HazardProtection, items)
                { NoProtection = !MovementFilter.HazardCounterProtects(hazard) }
            : null;
    }

    // What a walk taking its default route goes round: the hazard rooms the shortest
    // way with every gate open would walk into, with both lengths. Null when that
    // way meets none the crosser has no counter for, or there is no default route.
    // For the log and the bug report: a walk that quietly goes the longer way is
    // otherwise indistinguishable from one that never saw the shorter.
    public static (int HazardRooms, int ThroughSteps, int Steps)? HazardDetour(
        BfsMapper bfs, MovementFilter filter, RoomGraphManager graph,
        RoomKey source, RoomKey destination, Func<IReadOnlyList<Direction>?>? baseRoute = null)
    {
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(graph);

        IReadOnlyList<Direction>? taken = baseRoute is { } bp ? bp() : bfs.FindPath(source, destination, filter);
        if (taken is not { Count: > 0 }) return null;
        IReadOnlyList<Direction>? through;
        using (filter.SuspendAcquirableGates())
            through = bfs.FindPath(source, destination, filter);
        if (through is not { Count: > 0 }) return null;

        int rooms = UncounteredHazardRooms(filter, BuildKeyPath(graph, source, through)).Count;
        return rooms > 0 ? (rooms, through.Count, taken.Count) : null;
    }

    private static bool AlreadyHave(List<RouteRequirement> reqs, RouteRequirement candidate) =>
        reqs.Any(r => r.Kind == candidate.Kind && r.ItemIds.SequenceEqual(candidate.ItemIds));
}
