using System.Collections.Generic;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.ViewModels.Navigation;

namespace FujinTerm.Game.Remote;

// Thin glue between the remote-command engine and the existing Navigation stack.
// Registers the five MovePlayer commands and routes each to the in-place services
// (AutoWalkManager, LoopRunner, AutoLairManager, MovementCoordinator). Resolution
// of free-form room references runs through the shared RoomSearchService so this
// handler matches the Navigation search box's behaviour 1:1 (coords / acronym /
// room name substring / monster name with regen timer).
public sealed class MovePlayerHandler : IDisposable
{
    private static readonly string[] RegisteredCommands =
    {
        "@goto", "@loop", "@lair", "@stop", "@rego",
    };

    private readonly RemoteCommandManager _engine;
    private readonly RoomSearchService _search;
    private readonly RoomGraphManager _graph;
    private readonly RoomTracker _tracker;
    private readonly AutoWalkManager _walker;
    private readonly LoopManager _loops;
    private readonly LoopRunner _loopRunner;
    private readonly LairManager _lairs;
    private readonly AutoLairManager _autoLair;
    private readonly MovementCoordinator _coordinator;
    private readonly MovementController _controller;
    private bool _disposed;

    public MovePlayerHandler(
        RemoteCommandManager engine,
        RoomSearchService search,
        RoomGraphManager graph,
        RoomTracker tracker,
        AutoWalkManager walker,
        LoopManager loops,
        LoopRunner loopRunner,
        LairManager lairs,
        AutoLairManager autoLair,
        MovementCoordinator coordinator,
        MovementController controller)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(walker);
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(loopRunner);
        ArgumentNullException.ThrowIfNull(lairs);
        ArgumentNullException.ThrowIfNull(autoLair);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(controller);
        _engine = engine;
        _search = search;
        _graph = graph;
        _tracker = tracker;
        _walker = walker;
        _loops = loops;
        _loopRunner = loopRunner;
        _lairs = lairs;
        _autoLair = autoLair;
        _coordinator = coordinator;
        _controller = controller;

        Register("@goto", OnGoto);
        Register("@loop", OnLoop);
        Register("@lair", OnLair);
        Register("@stop", OnStop);
        Register("@rego", OnRego);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (string cmd in RegisteredCommands) _engine.UnregisterHandler(cmd);
    }

    private void Register(string command, Action<RemoteCommandContext> handler)
    {
        if (!RemoteCommandCatalog.TryGetCategory(command, out Models.GameData.PlayerRemoteControls category))
            throw new InvalidOperationException(
                $"RemoteCommandCatalog missing entry for '{command}'. Add it before registering.");
        _engine.RegisterHandler(command, category, handler);
    }

    private void OnGoto(RemoteCommandContext ctx)
    {
        string query = string.Join(' ', ctx.Args).Trim();
        if (query.Length == 0) { ctx.Reply("@goto requires a destination"); return; }

        // Acronyms (FCCO-style) are the only @goto-specific tier; the
        // rest mirrors the Navigation search box behaviour.
        IReadOnlyList<RoomSearchResult> matches = _search.Search(
            query, source: null, cap: 50, includeAcronyms: true);

        // Drop informational rows (monsters with no known lair room)
        // — they can't be walked to.
        List<RoomSearchResult> walkable = matches.Where(m => !m.IsInformational).ToList();

        // Rooms beat monsters. Rationale: when the user types a place
        // name that ALSO happens to be a monster substring, the room is
        // almost always what they meant — they'd specify a coordinate
        // if they meant a particular spawn. Falling through to the
        // monster tier only when there's no room hit keeps "@goto
        // godfrey" routing to the Bank of Godfrey instead of drowning
        // in the Mayor's three spawn rooms.
        List<RoomSearchResult> roomMatches = walkable.Where(m => m.MonsterTag is null).ToList();
        if (roomMatches.Count > 0)
        {
            DispatchRoomMatches(ctx, query, roomMatches);
            return;
        }

        List<RoomSearchResult> monsterMatches = walkable.Where(m => m.MonsterTag is not null).ToList();
        DispatchMonsterMatches(ctx, query, monsterMatches);
    }

    private void DispatchRoomMatches(RemoteCommandContext ctx, string query, List<RoomSearchResult> rooms)
    {
        switch (rooms.Count)
        {
            case 1:
                DispatchGoto(ctx, rooms[0]);
                return;
            case <= 3:
                ctx.Reply("did you mean: " + string.Join(", ",
                    rooms.Select(m => $"{m.Name} ({m.Key.Map}/{m.Key.Room})")) + "?");
                return;
            default:
                ctx.Reply($"too many room matches ({rooms.Count}) for '{query}'");
                return;
        }
    }

    private void DispatchMonsterMatches(RemoteCommandContext ctx, string query, List<RoomSearchResult> monsters)
    {
        if (monsters.Count == 0)
        {
            ctx.Reply($"no match for '{query}'");
            return;
        }

        // Collapse multiple spawns of the same monster into one group —
        // the user wants "this monster has N lairs", not N separate
        // "did you mean" entries for the same name.
        List<IGrouping<string, RoomSearchResult>> groups = monsters
            .GroupBy(m => m.MonsterTag!)
            .ToList();

        switch (groups.Count)
        {
            case 1:
                IGrouping<string, RoomSearchResult> only = groups[0];
                List<RoomSearchResult> spawns = only.ToList();
                string name = ExtractMonsterName(only.Key);
                if (spawns.Count == 1)
                {
                    DispatchGoto(ctx, spawns[0]);
                    return;
                }
                string coords = string.Join(", ", spawns.Select(s => $"{s.Key.Map}/{s.Key.Room}"));
                ctx.Reply($"{name} has {spawns.Count} lairs: {coords} — specify a coordinate");
                return;

            case <= 3:
                ctx.Reply("did you mean: " + string.Join(", ", groups.Select(FormatMonsterGroup)) + "?");
                return;

            default:
                ctx.Reply($"too many monster matches ({groups.Count}) for '{query}'");
                return;
        }
    }

    private static string FormatMonsterGroup(IGrouping<string, RoomSearchResult> group)
    {
        List<RoomSearchResult> spawns = group.ToList();
        string name = ExtractMonsterName(group.Key);
        return spawns.Count == 1
            ? $"{name} ({spawns[0].Key.Map}/{spawns[0].Key.Room})"
            : $"{name} ({spawns.Count} lairs)";
    }

    // Strip the regen suffix from the search result's MonsterTag. Tag format from
    // RoomSearchService: "Mayor of Godfrey · regen 4h" — we want just the monster
    // name for chat replies.
    private static string ExtractMonsterName(string monsterTag)
    {
        int sep = monsterTag.IndexOf(" · ", StringComparison.Ordinal);
        return sep > 0 ? monsterTag.Substring(0, sep) : monsterTag;
    }

    private void DispatchGoto(RemoteCommandContext ctx, RoomSearchResult match)
    {
        // Monster-tagged matches → walk to a neighbour, stop OUTSIDE
        // the lair so the user doesn't trigger the spawn on arrival.
        // Plain room matches → walk straight there.
        if (match.MonsterTag is not null)
        {
            RoomKey? wait = PickNeighbour(match.Key);
            if (wait is null)
            {
                ctx.Reply($"no neighbour to wait at for {match.Name}");
                return;
            }
            StopConflictingEngines(ctx.Sender, keep: SupersedeKeep.Walker);
            if (_walker.WalkTo(wait.Value))
                ctx.Reply($"walking outside {match.Name} ({match.Key.Map}/{match.Key.Room})");
            else
                ctx.Reply($"no path to {match.Name}");
            return;
        }

        StopConflictingEngines(ctx.Sender, keep: SupersedeKeep.Walker);
        if (_walker.WalkTo(match.Key))
            ctx.Reply($"walking to {match.Name} ({match.Key.Map}/{match.Key.Room})");
        else
            ctx.Reply($"no path to {match.Name}");
    }

    // Pick any walkable neighbour of lair so the monster-search @goto can stop one
    // room outside. First-found wins; the walker handles BFS from current to that
    // neighbour.
    private RoomKey? PickNeighbour(RoomKey lair)
    {
        if (_graph.GetRoom(lair) is not { } room) return null;
        foreach (RoomExit exit in room.Exits.Values)
        {
            if (exit.Target.Equals(lair)) continue;
            if (_graph.GetRoom(exit.Target) is not null) return exit.Target;
        }
        return null;
    }

    private void OnLoop(RemoteCommandContext ctx)
    {
        string raw = string.Join(' ', ctx.Args).Trim();
        if (raw.Length == 0) { ctx.Reply("@loop requires a name or coordinate list"); return; }

        if (RoomSearchService.TryParseCoordList(raw) is { Count: >= 2 } coords)
        {
            StopConflictingEngines(ctx.Sender, keep: SupersedeKeep.Loop);
            List<LoopWaypoint> waypoints = coords.Select(k => new LoopWaypoint(k)).ToList();
            _loopRunner.Start(new Loop($"@loop from {ctx.Sender}", waypoints));
            ctx.Reply($"looping {coords.Count} rooms");
            return;
        }

        Loop? saved = _loops.Loops.FirstOrDefault(l =>
            string.Equals(l.Name, raw, StringComparison.OrdinalIgnoreCase));
        if (saved is null) { ctx.Reply($"no saved loop named '{raw}'"); return; }
        StopConflictingEngines(ctx.Sender, keep: SupersedeKeep.Loop);
        _loopRunner.Start(saved);
        ctx.Reply($"looping '{saved.Name}' ({saved.Waypoints.Count} rooms)");
    }

    private void OnLair(RemoteCommandContext ctx)
    {
        string raw = string.Join(' ', ctx.Args).Trim();
        if (raw.Length == 0) { ctx.Reply("@lair requires a name or coordinate list"); return; }

        if (RoomSearchService.TryParseCoordList(raw) is { Count: >= 2 } coords)
        {
            StopConflictingEngines(ctx.Sender, keep: SupersedeKeep.Lair);
            _autoLair.Clear();
            foreach (RoomKey k in coords) _autoLair.Mark(k);
            ctx.Reply(_autoLair.Start()
                ? $"auto-lair: {coords.Count} lairs"
                : "auto-lair failed to start");
            return;
        }

        Models.Profile.LairSetup? setup = _lairs.Setups.FirstOrDefault(s =>
            string.Equals(s.Name, raw, StringComparison.OrdinalIgnoreCase));
        if (setup is null) { ctx.Reply($"no saved lair setup named '{raw}'"); return; }
        StopConflictingEngines(ctx.Sender, keep: SupersedeKeep.Lair);
        _autoLair.Clear();
        foreach (Models.Profile.LairMarker m in setup.Markers)
            _autoLair.Mark(new RoomKey(m.Map, m.Room), m.OverrideRespawnSeconds);
        ctx.Reply(_autoLair.Start()
            ? $"auto-lair '{setup.Name}': {setup.MarkerCount} lairs"
            : "auto-lair failed to start");
    }

    // @stop mirrors the toolbar Pause button exactly (routes through the same
    // MovementController). The user-pause STACKS on top of any engine wait, so a
    // route paused mid-combat (CombatGate held) stays paused after the fight
    // clears — instead of the old bug where @stop saw the coordinator already
    // "paused" (by Combat), replied "already stopped", and let the loop walk on
    // the moment combat ended. IsUserPaused is the user-tier check (Auto-Lair's
    // own pause flag, or the UserGate); combat / rest / party waits don't count
    // as a user stop. MovementController.Pause routes to the right target
    // (Auto-Lair's own pause vs the shared UserGate) but no-ops when idle, so an
    // idle @stop arms the gate directly to still register an explicit hold for
    // whatever starts next. Idempotent: a second @stop while already user-paused
    // just re-confirms.
    private void OnStop(RemoteCommandContext ctx)
    {
        if (_controller.IsUserPaused)
        {
            ctx.Reply("already @stopped");
            return;
        }
        if (_controller.IsActive) _controller.Pause();
        else _coordinator.AssertGate(MovementCoordinator.UserGate, nameof(MovePlayerHandler));

        string here = DescribeCurrentRoom();
        ctx.Reply(here.Length > 0
            ? $"movement paused, I'm in {here}"
            : "movement paused");
    }

    // Mirror of OnStop: lift only the USER pause, exactly like the toolbar
    // Resume (routes through the same MovementController). An engine wait still
    // holding the stack (an active fight, a rest) keeps the engine paused on its
    // own gate and resumes itself later — @rego doesn't force-clear engine waits.
    // Auto-Lair's own Resume runs its respawn-aware re-evaluation (in-game timers
    // kept ticking through the stop window — the original target may no longer be
    // the best pick). If the user pause isn't held, reply with what we're already
    // doing (or "nothing to resume") so the sender doesn't assume it worked.
    private void OnRego(RemoteCommandContext ctx)
    {
        if (!_controller.IsUserPaused)
        {
            ctx.Reply(DescribeActivity() ?? "nothing to resume");
            return;
        }
        // Capture the "what's running" description BEFORE the resume
        // call — clearing the gate may transition the walker / LoopRunner from
        // Paused back to Walking / Running immediately, after which the activity
        // description (which gates on the Paused state) becomes a no-op.
        string what = _autoLair.IsActive
            ? DescribePausedAutoLair()
            : DescribePausedLoopOrWalker();
        _controller.Resume();
        ctx.Reply($"resuming {what}");
    }

    // Render the tracker's current room as "Room Name (m/r)" for inclusion in chat
    // replies. Returns empty string when the tracker has no settled room (Lost /
    // Pending pre-arrival) so the caller can fall back to a bare reply.
    private string DescribeCurrentRoom()
    {
        if (_tracker.State.CurrentRoom is not { } here) return string.Empty;
        return $"{here.DisplayName} ({here.Key.Map}/{here.Key.Room})";
    }

    // Reply phrase for an @rego that resumes a paused Auto-Lair. Auto-Lair doesn't
    // carry the originating setup name forward, so fall back to the marker count.
    private string DescribePausedAutoLair() =>
        $"auto-lair ({_autoLair.Marked.Count} lairs)";

    // Reply phrase for an @rego that clears the coordinator gate. The gate covers
    // two engines — pick LoopRunner over Walker because a loop owns the wire while
    // the walker only handles its approach leg, so "loop X" is the user-visible
    // activity.
    private string DescribePausedLoopOrWalker()
    {
        if (_loopRunner.State is LoopState.Paused
            && _loopRunner.CurrentLoop is { } loop)
        {
            return $"loop '{loop.Name}'";
        }
        if (_walker.State is WalkState.Paused
            && _walker.Destination is { } dest)
        {
            string name = _graph.GetRoom(dest)?.DisplayName ?? "?";
            return $"walking to {name} ({dest.Map}/{dest.Room})";
        }
        return "movement";
    }

    // One-line description of the currently-running engine, in precedence order
    // AutoLair → LoopRunner → Walker (because the upper engines drive the lower
    // ones — describing "walking to X" while a loop owns the wire is misleading).
    // Returns null when nothing is running.
    private string? DescribeActivity()
    {
        if (_autoLair.IsActive)
            return $"auto-lair already running ({_autoLair.Marked.Count} lairs)";

        if (_loopRunner.State is not LoopState.Idle
            && _loopRunner.CurrentLoop is { } loop)
        {
            return $"loop '{loop.Name}' already running";
        }

        if (_walker.State is WalkState.Walking
            && _walker.Destination is { } dest)
        {
            string name = _graph.GetRoom(dest)?.DisplayName ?? "?";
            return $"already walking to {name} ({dest.Map}/{dest.Room})";
        }

        return null;
    }

    // Stop the engines that would collide with the new command. Without this, a
    // remote @goto issued during an active loop would see the walker supersede its
    // prior plan while LoopRunner kept writing circle moves directly to the wire —
    // both engines fight for the command stream. AutoLair is the same: its
    // scheduler re-issues WalkTo on every tick, immediately overriding the new
    // goto. Mirrors the Navigation UI's pattern via the shared EngineSupersede
    // helper.
    private void StopConflictingEngines(string sender, SupersedeKeep keep)
        => EngineSupersede.StopOthers(
            _walker, _loopRunner, _autoLair,
            keep, $"superseded by remote @ from {sender}");
}
