using System.Collections.Generic;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.ViewModels.Navigation;

namespace FujinTerm.Game.Remote;

/// <summary>
/// Phase 7 PR 7.23 — thin glue between the remote-command engine and
/// the existing Navigation stack. Registers the five MovePlayer
/// commands and routes each to the in-place services
/// (<see cref="AutoWalkManager"/>, <see cref="LoopRunner"/>,
/// <see cref="AutoLairManager"/>, <see cref="MovementCoordinator"/>).
/// Resolution of free-form room references runs through the shared
/// <see cref="RoomSearchService"/> so this handler matches the
/// Navigation search box's behaviour 1:1 (coords / acronym / room name
/// substring / monster name with regen timer).
/// </summary>
public sealed class MovePlayerHandler : IDisposable
{
    private static readonly string[] RegisteredCommands =
    {
        "@goto", "@loop", "@lair", "@stop", "@rego",
    };

    private readonly RemoteCommandManager _engine;
    private readonly RoomSearchService _search;
    private readonly RoomGraphManager _graph;
    private readonly AutoWalkManager _walker;
    private readonly LoopManager _loops;
    private readonly LoopRunner _loopRunner;
    private readonly LairManager _lairs;
    private readonly AutoLairManager _autoLair;
    private readonly MovementCoordinator _coordinator;
    private bool _disposed;

    public MovePlayerHandler(
        RemoteCommandManager engine,
        RoomSearchService search,
        RoomGraphManager graph,
        AutoWalkManager walker,
        LoopManager loops,
        LoopRunner loopRunner,
        LairManager lairs,
        AutoLairManager autoLair,
        MovementCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(walker);
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(loopRunner);
        ArgumentNullException.ThrowIfNull(lairs);
        ArgumentNullException.ThrowIfNull(autoLair);
        ArgumentNullException.ThrowIfNull(coordinator);
        _engine = engine;
        _search = search;
        _graph = graph;
        _walker = walker;
        _loops = loops;
        _loopRunner = loopRunner;
        _lairs = lairs;
        _autoLair = autoLair;
        _coordinator = coordinator;

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
        switch (walkable.Count)
        {
            case 0:
                ctx.Reply($"no match for '{query}'");
                return;
            case 1:
                DispatchGoto(ctx, walkable[0]);
                return;
            case <= 3:
                ctx.Reply("did you mean: " + string.Join(", ",
                    walkable.Select(m => $"{m.Name} ({m.Key.Map}/{m.Key.Room})")) + "?");
                return;
            default:
                ctx.Reply($"too many matches ({walkable.Count}) for '{query}'");
                return;
        }
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
            if (_walker.WalkTo(wait.Value))
                ctx.Reply($"walking to wait outside {match.Name} ({match.Key.Map}/{match.Key.Room})");
            else
                ctx.Reply($"no path to {match.Name}");
            return;
        }

        if (_walker.WalkTo(match.Key))
            ctx.Reply($"walking to {match.Name} ({match.Key.Map}/{match.Key.Room})");
        else
            ctx.Reply($"no path to {match.Name}");
    }

    /// <summary>
    /// Pick any walkable neighbour of <paramref name="lair"/> so the
    /// monster-search @goto can stop one room outside. First-found
    /// wins; the walker handles BFS from current to that neighbour.
    /// </summary>
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
            List<LoopWaypoint> waypoints = coords.Select(k => new LoopWaypoint(k)).ToList();
            _loopRunner.Start(new Loop($"@loop from {ctx.Sender}", waypoints));
            ctx.Reply($"starting loop with {coords.Count} waypoints");
            return;
        }

        Loop? saved = _loops.Loops.FirstOrDefault(l =>
            string.Equals(l.Name, raw, StringComparison.OrdinalIgnoreCase));
        if (saved is null) { ctx.Reply($"no saved loop named '{raw}'"); return; }
        _loopRunner.Start(saved);
        ctx.Reply($"starting loop '{saved.Name}'");
    }

    private void OnLair(RemoteCommandContext ctx)
    {
        string raw = string.Join(' ', ctx.Args).Trim();
        if (raw.Length == 0) { ctx.Reply("@lair requires a name or coordinate list"); return; }

        if (RoomSearchService.TryParseCoordList(raw) is { Count: >= 2 } coords)
        {
            _autoLair.Clear();
            foreach (RoomKey k in coords) _autoLair.Mark(k);
            ctx.Reply(_autoLair.Start()
                ? $"cycling {coords.Count} lairs"
                : "auto-lair failed to start");
            return;
        }

        Models.Profile.LairSetup? setup = _lairs.Setups.FirstOrDefault(s =>
            string.Equals(s.Name, raw, StringComparison.OrdinalIgnoreCase));
        if (setup is null) { ctx.Reply($"no saved lair setup named '{raw}'"); return; }
        _autoLair.Clear();
        foreach (Models.Profile.LairMarker m in setup.Markers)
            _autoLair.Mark(new RoomKey(m.Map, m.Room), m.OverrideRespawnSeconds);
        ctx.Reply(_autoLair.Start()
            ? $"cycling setup '{setup.Name}' ({setup.MarkerCount} lairs)"
            : "auto-lair failed to start");
    }

    private void OnStop(RemoteCommandContext ctx)
    {
        _coordinator.AssertGate(MovementCoordinator.UserGate);
        ctx.Reply("movement paused");
    }

    private void OnRego(RemoteCommandContext ctx)
    {
        _coordinator.ClearGate(MovementCoordinator.UserGate);
        ctx.Reply("movement resumed");
    }
}
