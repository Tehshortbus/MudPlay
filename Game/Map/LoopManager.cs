using System.Collections.Generic;
using System.IO;
using System.Linq;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// Per-game-data-set catalogue of saved navigation loops. CRUD over the
// JSON files under Data/game data/{set}/Loops/, plus the builder helpers
// the Navigation window needs to turn a sequence of clicked rooms into a
// runnable loop (gap-fill BFS for non-adjacent clicks, three-tier fallback
// for ambiguous click orders).
//
// Lifecycle: the catalogue is keyed by the active game-data set (the
// realm's MDB), so the same loop library follows the realm across every
// BBS / character that points at that set. When the active set changes,
// LoadAll reloads from disk. Mutating methods (Save, Delete) update the
// in-memory cache and fire LoopsChanged so the UI can refresh.
//
// Gap-fill: when the user clicks rooms that aren't directly connected,
// waypoint expansion runs BfsMapper.FindPath between consecutive clicks
// and inlines the intermediate hops. Surfaces a per-segment error when a
// segment can't be pathed — the editor disables Save until the user
// removes the bad click.
public sealed class LoopManager
{
    private readonly BfsMapper _bfs;
    private readonly RoomGraphManager _graph;
    private readonly LogService? _log;

    private string? _setName;
    private readonly Dictionary<string, Loop> _loops = new(StringComparer.OrdinalIgnoreCase);

    // Game-data set that Loops is currently sourced from. null when no set is active.
    public string? SetName => _setName;

    // Loaded loops, ordered alphabetically by name.
    public IReadOnlyList<Loop> Loops =>
        _loops.Values.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    // Fires after any mutation to Loops (load, save, delete).
    public event Action? LoopsChanged;

    public LoopManager(BfsMapper bfs, RoomGraphManager graph, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(graph);
        _bfs = bfs;
        _graph = graph;
        _log = log;
    }

    // ----- Game-data-set lifecycle -----------------------------------

    // Rebuild the in-memory cache from disk for setName. Pass null to clear
    // (no set active). Idempotent on no-op transitions — calling with the
    // same name twice still rereads because the user may have hand-edited a
    // loop file between calls.
    public void LoadAll(string? setName)
    {
        _loops.Clear();
        _setName = setName;

        if (string.IsNullOrWhiteSpace(setName))
        {
            LoopsChanged?.Invoke();
            return;
        }

        string folder = AppPaths.GameDataSetLoopsFolder(setName);
        if (!Directory.Exists(folder))
        {
            _log?.Info("Loops", $"no loops folder for set '{setName}'; empty catalogue.");
            LoopsChanged?.Invoke();
            return;
        }

        // One-shot migration: rename pre-suffix .json loop files to
        // .loop so subsequent loads find them. Skips lair files.
        MigrateLegacyJsonFilesIfPresent(folder);

        int loaded = 0;
        int failed = 0;
        int upgraded = 0;
        // Recurse: loops may live in user-created sub-folders under the
        // Loops root. The relative directory is the loop's Folder.
        foreach (string path in Directory.EnumerateFiles(folder, "*" + LoopFileSuffix, SearchOption.AllDirectories))
        {
            try
            {
                Loop? loop = JsonStore.Load<Loop>(path);
                // Schema-validate: a JSON file dropped in this folder
                // that isn't a Loop (or one we can't deserialise) is
                // silently skipped — the user can hand-drop .loop
                // files without us blowing up.
                if (loop is null || string.IsNullOrWhiteSpace(loop.Name)) { failed++; continue; }
                if (UpgradeIfNeeded(loop)) upgraded++;
                loop.Folder = NavFolders.RelativeFolder(folder, path);
                _loops[loop.Name] = loop;
                loaded++;
            }
            catch
            {
                failed++;
            }
        }

        string detail = failed == 0
            ? $"Loaded {loaded} loop(s) for set '{setName}'"
            : $"Loaded {loaded} loop(s) for set '{setName}' ({failed} malformed file(s) skipped)";
        if (upgraded > 0) detail += $"; {upgraded} legacy loop(s) upgraded to v{Loop3Schema} in memory";
        _log?.Info("Loops", detail + ".");
        LoopsChanged?.Invoke();
    }

    // Lookup by name. Returns null when the loop isn't in the catalogue.
    public Loop? Get(string name) =>
        _loops.TryGetValue(name, out Loop? loop) ? loop : null;

    // Persist loop under GameDataSetLoopsFolder. No-op when no set is active.
    public void Save(Loop loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        if (string.IsNullOrWhiteSpace(loop.Name))
            throw new ArgumentException("Loop name is required.", nameof(loop));
        if (_setName is null) return;

        // Saving normalises to the current schema version so a v2
        // loop edited in the new editor lands on disk as v3 cleanly.
        loop.SchemaVersion = Loop3Schema;
        loop.Folder = NavFolders.Normalize(loop.Folder);
        string root = AppPaths.GameDataSetLoopsFolder(_setName);
        // A loop name is unique set-wide, so a folder change is a move:
        // delete any stale file for this name (in any folder) first so
        // we don't leave a duplicate behind.
        DeleteFileForName(root, loop.Name);
        string dir = NavFolders.ToDirectory(root, loop.Folder);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, SafeFileName(loop.Name));
        JsonStore.Save(path, loop);
        _loops[loop.Name] = loop;
        LoopsChanged?.Invoke();
    }

    // Move the loop named name into folder (empty = Loops root). No-op when
    // the loop isn't in the catalogue or no set is active.
    public bool Move(string name, string? folder)
    {
        if (Get(name) is not { } loop) return false;
        string target = NavFolders.Normalize(folder);
        if (string.Equals(NavFolders.Normalize(loop.Folder), target, StringComparison.OrdinalIgnoreCase))
            return false;
        loop.Folder = target;
        Save(loop);
        return true;
    }

    // Delete the loop named name. No-op when not in the catalogue or no set
    // is active.
    public bool Delete(string name)
    {
        if (_setName is null) return false;
        if (!_loops.Remove(name)) return false;

        DeleteFileForName(AppPaths.GameDataSetLoopsFolder(_setName), name);
        LoopsChanged?.Invoke();
        return true;
    }

    // Delete the on-disk .loop file for name wherever it lives under root
    // (the name is unique set-wide). Best-effort — failures are logged.
    private void DeleteFileForName(string root, string name)
    {
        if (!Directory.Exists(root)) return;
        string fileName = SafeFileName(name);
        foreach (string path in Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories))
        {
            try { File.Delete(path); }
            catch (Exception ex)
            {
                _log?.Warn("Loops", $"failed to delete loop file '{path}': {ex.Message}");
            }
        }
    }

    // ----- builder helpers -------------------------------------------

    // Convenience wrapper around LoopExpander.Expand pre-bound to this
    // manager's BfsMapper. Used by the builder for the live-preview step
    // count + unreachable summary; the runner expands directly via
    // LoopExpander at start time.
    public (IReadOnlyList<LoopStep> Steps, IReadOnlyList<(RoomKey From, RoomKey To)> UnreachableSegments)
        ExpandWaypoints(IReadOnlyList<LoopWaypoint> waypoints, IRoomFilter? filter = null)
        => LoopExpander.Expand(waypoints, _bfs, filter);

    // ----- internals -------------------------------------------------

    // Current schema version persisted on save.
    public const int Loop3Schema = 3;

    // Upgrade an in-memory Loop in place if its SchemaVersion is below the
    // current version. Returns true when an upgrade was applied so the
    // caller can surface a count in the load log.
    //
    // v1 → v3: file had IsCircular + flat Steps. Waypoints stays empty —
    // the v1 schema had no waypoint anchors to recover, so legacy loops
    // can't be re-expanded by avoid-list change until the user re-saves
    // them in the builder.
    //
    // v2 → v3: file had UserWaypoints + Steps. Copy UserWaypoints into
    // Waypoints with null commands; any mid-leg CommandLoopSteps in the
    // flat list are dropped (logged once) because v3 attaches commands to
    // waypoints, not arbitrary positions.
    //
    // The on-disk file isn't rewritten — upgrades are in-memory only so a
    // user who downgrades doesn't lose their old files. The next
    // user-driven Save persists the v3 form.
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

    // Make name filesystem-safe. The user can call a loop anything; we
    // strip path separators + reserved chars and append the loop suffix.
    private static string SafeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString() + LoopFileSuffix;
    }

    // Filename suffix for loop files in the shared Loops folder.
    public const string LoopFileSuffix = ".loop";

    // Rename pre-suffix-migration {name}.json files to {name}.loop so the new
    // LoadAll scan picks them up. Skips files whose target already exists.
    // Best-effort — failures are logged but don't break the load path.
    private void MigrateLegacyJsonFilesIfPresent(string folder)
    {
        if (!Directory.Exists(folder)) return;
        int moved = 0;
        foreach (string srcPath in Directory.EnumerateFiles(folder, "*.json"))
        {
            // Skip lair setups (those still carry the .lair.json
            // intermediate suffix — LairManager owns their migration).
            if (srcPath.EndsWith(LairManager.LairFileSuffix, StringComparison.OrdinalIgnoreCase)
                || srcPath.EndsWith(LairManager.LegacyLairFileSuffix, StringComparison.OrdinalIgnoreCase))
                continue;
            string baseName = Path.GetFileNameWithoutExtension(srcPath);
            if (string.IsNullOrWhiteSpace(baseName)) continue;
            string destPath = Path.Combine(folder, baseName + LoopFileSuffix);
            if (File.Exists(destPath)) continue;
            try
            {
                File.Move(srcPath, destPath);
                moved++;
            }
            catch (Exception ex)
            {
                _log?.Warn("Loops",
                    $"migration: failed to rename '{srcPath}' → '{destPath}': {ex.Message}");
            }
        }
        if (moved > 0)
            _log?.Info("Loops",
                $"migrated {moved} loop file(s) from .json → {LoopFileSuffix}.");
    }
}
