using System.Collections.Generic;
using System.Linq;
using System.Text;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

/// <summary>
/// Phase 7 PR 7.23 — wires the five MovePlayer remote commands into
/// the Navigation stack. Permission gating routes through
/// <see cref="RemoteCommandCatalog"/> + the per-player
/// <see cref="Models.GameData.PlayerRemoteControls.MovePlayer"/> flag.
/// </summary>
/// <remarks>
/// Commands registered (per user direction; replaces the upstream
/// MegaMUD @looponce / @roam, neither of which we ship):
/// <list type="bullet">
///   <item><b>@goto &lt;args&gt;</b> — resolve args to a room and walk
///     there. Args can be a coordinate (<c>1/297</c>, <c>1,297</c>,
///     bare <c>297</c>), an exact (case-insensitive) room name, or
///     a first-letter acronym ("Frozen Cavern, Cave Opening" →
///     <c>FCCO</c>). 1-of-1 dispatches the walker; 2-3 surfaces a
///     "did you mean" reply listing the candidates; 4+ falls back to
///     "too many matches". Zero matches replies with the bare
///     "no match" line.</item>
///   <item><b>@loop &lt;args&gt;</b> — start a loop. Args can be a
///     saved-loop name OR a comma-separated list of map/room
///     coordinates which the handler builds into a transient
///     loop via <see cref="LoopManager.ExpandWaypoints"/>.</item>
///   <item><b>@lair &lt;args&gt;</b> — same shape as @loop, but
///     routes through the Auto-Lair stack (saved
///     <see cref="LairManager"/> setup OR a list of marker
///     coordinates).</item>
///   <item><b>@stop</b> — asserts the user pause-gate on
///     <see cref="MovementCoordinator"/>. The user gate is the same
///     one the Run-chip Pause uses, so every existing engine
///     (walker / LoopRunner / AutoLairManager) freezes uniformly.
///     Stronger than <c>@wait</c>: there's no auto-expire — the
///     user clears it explicitly via @rego or the Run chip.</item>
///   <item><b>@rego</b> — releases the user pause-gate so whatever
///     movement engine was running continues.</item>
/// </list>
/// </remarks>
public sealed class MovePlayerHandler : IDisposable
{
    private static readonly string[] RegisteredCommands =
    {
        "@goto", "@loop", "@lair", "@stop", "@rego",
    };

    private readonly RemoteCommandManager _engine;
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
        RoomGraphManager graph,
        AutoWalkManager walker,
        LoopManager loops,
        LoopRunner loopRunner,
        LairManager lairs,
        AutoLairManager autoLair,
        MovementCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(walker);
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(loopRunner);
        ArgumentNullException.ThrowIfNull(lairs);
        ArgumentNullException.ThrowIfNull(autoLair);
        ArgumentNullException.ThrowIfNull(coordinator);
        _engine = engine;
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

    // ----- @goto -------------------------------------------------------

    private void OnGoto(RemoteCommandContext ctx)
    {
        string query = string.Join(' ', ctx.Args).Trim();
        if (query.Length == 0) { ctx.Reply("@goto requires a destination"); return; }

        List<RoomMatch> matches = ResolveQuery(query);
        switch (matches.Count)
        {
            case 0:
                ctx.Reply($"no match for '{query}'");
                return;
            case 1:
                {
                    RoomMatch m = matches[0];
                    if (_walker.WalkTo(m.Key))
                        ctx.Reply($"walking to {m.Label} ({m.Key.Map}/{m.Key.Room})");
                    else
                        ctx.Reply($"no path to {m.Label}");
                    return;
                }
            case <= 3:
                ctx.Reply("did you mean: " + string.Join(", ",
                    matches.Select(m => $"{m.Label} ({m.Key.Map}/{m.Key.Room})")) + "?");
                return;
            default:
                ctx.Reply($"too many matches ({matches.Count}) for '{query}'");
                return;
        }
    }

    // ----- @loop -------------------------------------------------------

    private void OnLoop(RemoteCommandContext ctx)
    {
        string raw = string.Join(' ', ctx.Args).Trim();
        if (raw.Length == 0) { ctx.Reply("@loop requires a name or coordinate list"); return; }

        // Coordinate-list path: "1/224, 1/218, 1/245" → transient loop.
        // Mixed args (some coords, some not) fall back to name match.
        List<RoomKey>? coords = TryParseCoordList(raw);
        if (coords is not null && coords.Count >= 2)
        {
            List<LoopWaypoint> waypoints = coords.Select(k => new LoopWaypoint(k)).ToList();
            Loop transient = new($"@loop from {ctx.Sender}", waypoints);
            _loopRunner.Start(transient);
            ctx.Reply($"starting loop with {coords.Count} waypoints");
            return;
        }

        // Otherwise: saved-loop name.
        Loop? saved = _loops.Loops.FirstOrDefault(l =>
            string.Equals(l.Name, raw, StringComparison.OrdinalIgnoreCase));
        if (saved is null)
        {
            ctx.Reply($"no saved loop named '{raw}'");
            return;
        }
        _loopRunner.Start(saved);
        ctx.Reply($"starting loop '{saved.Name}'");
    }

    // ----- @lair -------------------------------------------------------

    private void OnLair(RemoteCommandContext ctx)
    {
        string raw = string.Join(' ', ctx.Args).Trim();
        if (raw.Length == 0) { ctx.Reply("@lair requires a name or coordinate list"); return; }

        List<RoomKey>? coords = TryParseCoordList(raw);
        if (coords is not null && coords.Count >= 2)
        {
            _autoLair.Clear();
            foreach (RoomKey k in coords) _autoLair.Mark(k);
            if (_autoLair.Start())
                ctx.Reply($"cycling {coords.Count} lairs");
            else
                ctx.Reply("auto-lair failed to start");
            return;
        }

        Models.Profile.LairSetup? setup = _lairs.Setups.FirstOrDefault(s =>
            string.Equals(s.Name, raw, StringComparison.OrdinalIgnoreCase));
        if (setup is null)
        {
            ctx.Reply($"no saved lair setup named '{raw}'");
            return;
        }
        _autoLair.Clear();
        foreach (Models.Profile.LairMarker m in setup.Markers)
            _autoLair.Mark(new RoomKey(m.Map, m.Room), m.OverrideRespawnSeconds);
        if (_autoLair.Start())
            ctx.Reply($"cycling setup '{setup.Name}' ({setup.MarkerCount} lairs)");
        else
            ctx.Reply("auto-lair failed to start");
    }

    // ----- @stop / @rego ----------------------------------------------

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

    // ----- Resolution helpers ------------------------------------------

    /// <summary>
    /// Single-query resolver shared by @goto. Tries each dialect in
    /// order; returns the FIRST tier that yields at least one match
    /// so a unique coordinate beats a name collision. Capped at 50
    /// matches so a stray "1" (matching every room with Room == 1)
    /// doesn't return thousands.
    /// </summary>
    private List<RoomMatch> ResolveQuery(string query)
    {
        const int Cap = 50;
        string trimmed = query.Trim();
        if (trimmed.Length == 0) return new();

        // Tier 1: explicit coordinate.
        (int? mapPart, int? roomPart) = TryParseCoordinate(trimmed);
        if (mapPart is int m && roomPart is int r
            && _graph.GetRoom(new RoomKey(m, r)) is { } exact)
            return new() { new RoomMatch(exact.Key, exact.DisplayName) };

        // Tier 1b: bare room number — list rooms with that Room across all maps.
        if (mapPart is null && roomPart is int onlyRoom)
        {
            List<RoomMatch> hits = new();
            foreach (Room room in _graph.Rooms)
            {
                if (room.Key.Room != onlyRoom) continue;
                hits.Add(new RoomMatch(room.Key, room.DisplayName));
                if (hits.Count >= Cap) break;
            }
            if (hits.Count > 0) return hits;
        }

        // Tier 2: exact (case-insensitive) name. DisplayName covers
        // graph rooms with learned names; Name is the raw MDB string.
        List<RoomMatch> exactMatches = new();
        foreach (Room room in _graph.Rooms)
        {
            if (string.Equals(room.DisplayName, trimmed, StringComparison.OrdinalIgnoreCase)
                || string.Equals(room.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                exactMatches.Add(new RoomMatch(room.Key, room.DisplayName));
                if (exactMatches.Count >= Cap) break;
            }
        }
        if (exactMatches.Count > 0) return exactMatches;

        // Tier 3: acronym (first letter of each word). "Frozen
        // Cavern, Cave Opening" → "FCCO". Punctuation + non-letter
        // separators are tokeniser delimiters.
        string normalized = trimmed.ToUpperInvariant();
        List<RoomMatch> acronymMatches = new();
        foreach (Room room in _graph.Rooms)
        {
            string acro = ExtractAcronym(room.DisplayName);
            if (acro.Length == 0) continue;
            if (string.Equals(acro, normalized, StringComparison.Ordinal))
            {
                acronymMatches.Add(new RoomMatch(room.Key, room.DisplayName));
                if (acronymMatches.Count >= Cap) break;
            }
        }
        return acronymMatches;
    }

    /// <summary>
    /// First letter of each whitespace-or-punctuation-delimited word,
    /// uppercased. Empty input → empty result. Used by the @goto
    /// acronym tier ("Frozen Cavern, Cave Opening" → "FCCO").
    /// </summary>
    internal static string ExtractAcronym(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        StringBuilder sb = new();
        bool startOfWord = true;
        foreach (char c in text)
        {
            if (char.IsLetter(c))
            {
                if (startOfWord) sb.Append(char.ToUpperInvariant(c));
                startOfWord = false;
            }
            else
            {
                startOfWord = true;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Coordinate parser mirroring the Navigation rail's search box:
    /// "1/297", "1,297", "1 297" → (1, 297); bare "297" → (null, 297);
    /// non-numeric → (null, null).
    /// </summary>
    internal static (int? Map, int? Room) TryParseCoordinate(string text)
    {
        string[] parts = text.Split(new[] { '/', ',', ' ', '\t' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1 && int.TryParse(parts[0], out int onlyRoom))
            return (null, onlyRoom);
        if (parts.Length == 2
            && int.TryParse(parts[0], out int map)
            && int.TryParse(parts[1], out int room))
            return (map, room);
        return (null, null);
    }

    /// <summary>
    /// Parse a comma-separated coordinate list like
    /// <c>"1/224, 1/218, 1/245"</c> into <see cref="RoomKey"/>s.
    /// Returns null when any token fails to parse OR resolve in the
    /// graph — the caller falls back to name matching.
    /// </summary>
    internal static List<RoomKey>? TryParseCoordList(string text)
    {
        string[] tokens = text.Split(new[] { ',', ';' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 1) return null;

        List<RoomKey> keys = new(tokens.Length);
        foreach (string tok in tokens)
        {
            (int? mapPart, int? roomPart) = TryParseCoordinate(tok);
            if (mapPart is not int m || roomPart is not int r) return null;
            keys.Add(new RoomKey(m, r));
        }
        return keys;
    }

    private readonly record struct RoomMatch(RoomKey Key, string Label);
}
