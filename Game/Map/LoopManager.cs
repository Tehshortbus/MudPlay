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
        foreach (string path in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                Loop? loop = JsonStore.Load<Loop>(path);
                if (loop is null || string.IsNullOrWhiteSpace(loop.Name)) { failed++; continue; }
                _loops[loop.Name] = loop;
                loaded++;
            }
            catch
            {
                failed++;
            }
        }

        _log?.Info("Loops", failed == 0
            ? $"Loaded {loaded} loop(s) for '{bbsName}'."
            : $"Loaded {loaded} loop(s) for '{bbsName}' ({failed} malformed file(s) skipped).");
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
    /// <see cref="AppPaths.BbsLoopsFolder"/>. Stamp
    /// <see cref="Loop.LastModifiedAt"/> automatically. No-op when no
    /// BBS is bound.
    /// </summary>
    public void Save(Loop loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        if (string.IsNullOrWhiteSpace(loop.Name))
            throw new ArgumentException("Loop name is required.", nameof(loop));
        if (_bbsName is null) return;

        loop.LastModifiedAt = DateTimeOffset.UtcNow;
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

    /// <summary>
    /// Record that the loop was just run — stamps
    /// <see cref="Loop.LastRunAt"/>, persists, and fires
    /// <see cref="LoopsChanged"/>. Called by the loop runner (PR 7.16).
    /// </summary>
    public void NoteRun(string name)
    {
        if (_bbsName is null) return;
        if (!_loops.TryGetValue(name, out Loop? loop)) return;

        loop.LastRunAt = DateTimeOffset.UtcNow;
        Save(loop);   // re-stamp LastModifiedAt is OK — run records the touch
    }

    // ----- builder helpers -------------------------------------------

    /// <summary>
    /// Expand a sequence of clicked room keys into a flat
    /// <see cref="LoopStep"/> sequence — BFS the gap between each
    /// consecutive pair and inline the directional steps. Returns the
    /// step list (possibly empty) and a list of unreachable segments
    /// the user must fix before saving.
    /// </summary>
    /// <param name="clicks">Rooms in the order the user clicked them.</param>
    /// <param name="closeLoop">When true, append a path from the last click back to the first (makes the loop circular).</param>
    /// <param name="filter">Optional avoided-rooms filter — same one the walker uses.</param>
    public (IReadOnlyList<LoopStep> Steps, IReadOnlyList<(RoomKey From, RoomKey To)> UnreachableSegments)
        ExpandClickedRooms(IReadOnlyList<RoomKey> clicks, bool closeLoop = false, IRoomFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(clicks);
        if (clicks.Count < 2)
            return (Array.Empty<LoopStep>(), Array.Empty<(RoomKey, RoomKey)>());

        var steps = new List<LoopStep>();
        var unreachable = new List<(RoomKey From, RoomKey To)>();

        for (int i = 0; i < clicks.Count - 1; i++)
        {
            RoomKey from = clicks[i];
            RoomKey to = clicks[i + 1];
            IReadOnlyList<Direction>? path = _bfs.FindPath(from, to, filter);
            if (path is null || path.Count == 0)
            {
                unreachable.Add((from, to));
                continue;
            }
            foreach (Direction d in path) steps.Add(new MoveLoopStep(d));
        }

        if (closeLoop && clicks.Count >= 2)
        {
            RoomKey from = clicks[^1];
            RoomKey to = clicks[0];
            IReadOnlyList<Direction>? path = _bfs.FindPath(from, to, filter);
            if (path is null || path.Count == 0) unreachable.Add((from, to));
            else foreach (Direction d in path) steps.Add(new MoveLoopStep(d));
        }

        return (steps, unreachable);
    }

    // ----- internals -------------------------------------------------

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
