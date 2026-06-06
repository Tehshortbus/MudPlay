using System.Collections.Generic;
using System.IO;
using System.Linq;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

/// <summary>
/// Per-BBS catalogue of saved navigation loops. CRUD over the JSON
/// files under <c>Data/BBS/{bbs}/Loops/</c>, plus the builder helpers
/// the Navigation window needs to turn a sequence of clicked rooms
/// into a runnable loop (gap-fill BFS for non-adjacent clicks,
/// three-tier fallback for ambiguous click orders).
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle: the catalogue is keyed by the active BBS name. When the
/// connected BBS changes (or on profile load), <see cref="LoadAll"/>
/// reloads from disk. Mutating methods (<see cref="Save"/>,
/// <see cref="Delete"/>) update the in-memory cache and fire
/// <see cref="LoopsChanged"/> so the UI can refresh.
/// </para>
/// <para>
/// Gap-fill: when the user clicks rooms that aren't directly
/// connected, <see cref="ExpandClickedRooms"/> runs
/// <see cref="BfsMapper.FindPath"/> between consecutive clicks and
/// inlines the intermediate hops. Surfaces a per-segment error when a
/// segment can't be pathed — the editor disables Save until the user
/// removes the bad click.
/// </para>
/// </remarks>
public sealed class LoopManager
{
    private readonly BfsMapper _bfs;
    private readonly RoomGraphManager _graph;
    private readonly LogService? _log;

    private string? _bbsName;
    private readonly Dictionary<string, Loop> _loops = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>BBS that <see cref="Loops"/> is currently sourced from. <c>null</c> when no BBS is bound.</summary>
    public string? BbsName => _bbsName;

    /// <summary>Loaded loops, ordered alphabetically by name.</summary>
    public IReadOnlyList<Loop> Loops =>
        _loops.Values.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Fires after any mutation to <see cref="Loops"/> (load, save, delete).</summary>
    public event Action? LoopsChanged;

    public LoopManager(BfsMapper bfs, RoomGraphManager graph, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(graph);
        _bfs = bfs;
        _graph = graph;
        _log = log;
    }

    // ----- BBS lifecycle ---------------------------------------------

    /// <summary>
    /// Rebuild the in-memory cache from disk for <paramref name="bbsName"/>.
    /// Pass <c>null</c> to clear (no BBS bound). Idempotent on no-op
    /// transitions — calling with the same name twice still rereads
    /// because the user may have hand-edited a loop file between
    /// calls.
    /// </summary>
    public void LoadAll(string? bbsName)
    {
        _loops.Clear();
        _bbsName = bbsName;

        if (string.IsNullOrWhiteSpace(bbsName))
        {
            LoopsChanged?.Invoke();
            return;
        }

        string folder = AppPaths.BbsLoopsFolder(bbsName);
        if (!Directory.Exists(folder))
        {
            _log?.Info("Loops", $"no loops folder for '{bbsName}'; empty catalogue.");
            LoopsChanged?.Invoke();
            return;
        }

        int loaded = 0;
        int failed = 0;
        int upgraded = 0;
        foreach (string path in Directory.EnumerateFiles(folder, "*.json"))
        {
            // Lair setups share this folder under the .lair.json
            // suffix; skip them here so LoopManager only sees real
            // loop files. (LairManager owns the inverse filter.)
            if (path.EndsWith(LairManager.LairFileSuffix, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                Loop? loop = JsonStore.Load<Loop>(path);
                if (loop is null || string.IsNullOrWhiteSpace(loop.Name)) { failed++; continue; }
                if (UpgradeIfNeeded(loop)) upgraded++;
                _loops[loop.Name] = loop;
                loaded++;
            }
            catch
            {
                failed++;
            }
        }

        string detail = failed == 0
            ? $"Loaded {loaded} loop(s) for '{bbsName}'"
            : $"Loaded {loaded} loop(s) for '{bbsName}' ({failed} malformed file(s) skipped)";
        if (upgraded > 0) detail += $"; {upgraded} legacy loop(s) upgraded to v{Loop3Schema} in memory";
        _log?.Info("Loops", detail + ".");
        LoopsChanged?.Invoke();
    }

    /// <summary>
    /// Lookup by name. Returns <c>null</c> when the loop isn't in the
    /// catalogue.
    /// </summary>
    public Loop? Get(string name) =>
        _loops.TryGetValue(name, out Loop? loop) ? loop : null;

    /// <summary>
    /// Persist <paramref name="loop"/> under
    /// <see cref="AppPaths.BbsLoopsFolder"/>. No-op when no BBS is
    /// bound.
    /// </summary>
    public void Save(Loop loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        if (string.IsNullOrWhiteSpace(loop.Name))
            throw new ArgumentException("Loop name is required.", nameof(loop));
        if (_bbsName is null) return;

        // Saving normalises to the current schema version so a v2
        // loop edited in the new editor lands on disk as v3 cleanly.
        loop.SchemaVersion = Loop3Schema;
        string folder = AppPaths.BbsLoopsFolder(_bbsName);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, SafeFileName(loop.Name));
        JsonStore.Save(path, loop);
        _loops[loop.Name] = loop;
        LoopsChanged?.Invoke();
    }

    /// <summary>
    /// Delete the loop named <paramref name="name"/>. No-op when not in
    /// the catalogue or no BBS bound.
    /// </summary>
    public bool Delete(string name)
    {
        if (_bbsName is null) return false;
        if (!_loops.Remove(name)) return false;

        string path = Path.Combine(AppPaths.BbsLoopsFolder(_bbsName), SafeFileName(name));
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex)
        {
            _log?.Warn("Loops", $"failed to delete loop file '{path}': {ex.Message}");
        }
        LoopsChanged?.Invoke();
        return true;
    }

    // ----- builder helpers -------------------------------------------

    /// <summary>
    /// Convenience wrapper around <see cref="LoopExpander.Expand"/>
    /// pre-bound to this manager's <see cref="BfsMapper"/>. Used by
    /// the builder for the live-preview step count + unreachable
    /// summary; the runner expands directly via
    /// <see cref="LoopExpander"/> at start time.
    /// </summary>
    public (IReadOnlyList<LoopStep> Steps, IReadOnlyList<(RoomKey From, RoomKey To)> UnreachableSegments)
        ExpandWaypoints(IReadOnlyList<LoopWaypoint> waypoints, IRoomFilter? filter = null)
        => LoopExpander.Expand(waypoints, _bfs, filter);

    // ----- internals -------------------------------------------------

    /// <summary>Current schema version persisted on save.</summary>
    public const int Loop3Schema = 3;

    /// <summary>
    /// Upgrade an in-memory <see cref="Loop"/> in place if its
    /// <see cref="Loop.SchemaVersion"/> is below the current version.
    /// Returns <c>true</c> when an upgrade was applied so the caller
    /// can surface a count in the load log.
    /// </summary>
    /// <remarks>
    /// <para>
    /// v1 → v3: file had IsCircular + flat Steps. <c>Waypoints</c>
    /// stays empty — the v1 schema had no waypoint anchors to
    /// recover, so legacy loops can't be re-expanded by avoid-list
    /// change until the user re-saves them in the builder.
    /// </para>
    /// <para>
    /// v2 → v3: file had UserWaypoints + Steps. Copy UserWaypoints
    /// into <see cref="Loop.Waypoints"/> with null commands; any
    /// mid-leg <c>CommandLoopStep</c>s in the flat list are dropped
    /// (logged once) because v3 attaches commands to waypoints, not
    /// arbitrary positions.
    /// </para>
    /// <para>
    /// The on-disk file isn't rewritten — upgrades are in-memory
    /// only so a user who downgrades doesn't lose their old files.
    /// The next user-driven Save persists the v3 form.
    /// </para>
    /// </remarks>
    private bool UpgradeIfNeeded(Loop loop)
    {
        if (loop.SchemaVersion >= Loop3Schema) return false;

        loop.Waypoints ??= new List<LoopWaypoint>();
        loop.Notes ??= string.Empty;

        // Pull legacy UserWaypoints out of the JsonExtensionData
        // bucket and rehydrate them as waypoints with no commands.
        // v2 also carried inline CommandLoopSteps in the flat Steps
        // list at arbitrary positions; v3 attaches commands to
        // waypoints, so mid-leg commands are silently dropped (with
        // a warning log) — the user can re-add via the editor.
        if (loop.LegacyFields is { } legacy
            && legacy.TryGetValue("UserWaypoints", out System.Text.Json.JsonElement uw)
            && uw.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (System.Text.Json.JsonElement entry in uw.EnumerateArray())
            {
                if (entry.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("Map", out System.Text.Json.JsonElement mapEl)) continue;
                if (!entry.TryGetProperty("Room", out System.Text.Json.JsonElement roomEl)) continue;
                if (!mapEl.TryGetInt32(out int map) || !roomEl.TryGetInt32(out int room)) continue;
                loop.Waypoints.Add(new LoopWaypoint(new RoomKey(map, room)));
            }
            int droppedCommands = CountLegacyCommandSteps(legacy);
            if (droppedCommands > 0)
                _log?.Warn("Loops",
                    $"v2 → v3 migration of '{loop.Name}': dropped {droppedCommands} mid-leg command step(s). " +
                    "Re-add via the loop editor to attach them to waypoints.");
        }
        loop.LegacyFields = null;
        loop.SchemaVersion = Loop3Schema;
        return true;
    }

    private static int CountLegacyCommandSteps(
        Dictionary<string, System.Text.Json.JsonElement> legacy)
    {
        if (!legacy.TryGetValue("Steps", out System.Text.Json.JsonElement steps)) return 0;
        if (steps.ValueKind != System.Text.Json.JsonValueKind.Array) return 0;
        int count = 0;
        foreach (System.Text.Json.JsonElement step in steps.EnumerateArray())
        {
            if (step.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            if (step.TryGetProperty("kind", out System.Text.Json.JsonElement kind)
                && kind.ValueKind == System.Text.Json.JsonValueKind.String
                && string.Equals(kind.GetString(), "command", StringComparison.Ordinal))
                count++;
        }
        return count;
    }

    /// <summary>
    /// Make <paramref name="name"/> filesystem-safe. The user can call
    /// a loop anything; we strip path separators + reserved chars and
    /// append <c>.json</c>.
    /// </summary>
    private static string SafeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString() + ".json";
    }
}
