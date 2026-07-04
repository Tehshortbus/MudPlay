using System.IO;
using System.Linq;

namespace FujinTerm.Services;

// Game-data set lifecycle operations exposed by the Game Data → "Manage Sets…"
// dialog: copy or move a set's nav-library between sets, and delete a set
// outright. "Loops" here means the whole shared
// AppPaths.GameDataSetLoopsFolder tree — loop circuits, Auto-Lair setups, and
// the nav-folder subdirectories all live in it — so a copy/move drags the entire
// library, not just .loop files.
//
// Reload + reference hygiene are folded into each op so the call site only has to
// render a result:
//   - A copy/move into or out of the active set fires reloadActiveLibrary so the
//     live Game.Map.LoopManager + Game.Map.LairManager caches pick up the change.
//   - Deleting the active set switches the cache to no-set first (so no
//     JsonDocument handle pins the directory), then fires onSetDeleted so the
//     caller can clear any profile / global reference that named the removed set.
// The manager itself only touches the filesystem + GameDataCache;
// settings/profile writes are the caller's job via the injected callbacks, which
// keeps it unit-testable against an isolated set folder.
public sealed class GameDataSetManager
{
    private readonly GameDataCache _cache;
    private readonly Action _reloadActiveLibrary;
    private readonly Action<string> _onSetDeleted;
    private readonly LogService? _log;

    public GameDataSetManager(
        GameDataCache cache,
        Action reloadActiveLibrary,
        Action<string> onSetDeleted,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(reloadActiveLibrary);
        ArgumentNullException.ThrowIfNull(onSetDeleted);
        _cache = cache;
        _reloadActiveLibrary = reloadActiveLibrary;
        _onSetDeleted = onSetDeleted;
        _log = log;
    }

    // Outcome of an op, ready to surface verbatim as dialog status text.
    public readonly record struct OpResult(bool Ok, string Message);

    // Copy sourceSet's loop library into destSet.
    public OpResult CopyLoops(string sourceSet, string destSet) =>
        CopyOrMove(sourceSet, destSet, move: false);

    // Move sourceSet's loop library into destSet (source loops removed).
    public OpResult MoveLoops(string sourceSet, string destSet) =>
        CopyOrMove(sourceSet, destSet, move: true);

    private OpResult CopyOrMove(string sourceSet, string destSet, bool move)
    {
        string verb = move ? "move" : "copy";
        if (string.IsNullOrWhiteSpace(sourceSet) || string.IsNullOrWhiteSpace(destSet))
            return new OpResult(false, $"Pick a source and a destination set to {verb} loops.");
        if (string.Equals(sourceSet, destSet, StringComparison.OrdinalIgnoreCase))
            return new OpResult(false, "Source and destination are the same set.");
        if (!Directory.Exists(AppPaths.GameDataSetDir(destSet)))
            return new OpResult(false, $"Destination set '{destSet}' does not exist.");

        string srcLoops = AppPaths.GameDataSetLoopsFolder(sourceSet);
        if (!Directory.Exists(srcLoops) || !ContainsAnyFile(srcLoops))
            return new OpResult(false, $"'{sourceSet}' has no loops to {verb}.");

        int files;
        try
        {
            files = CopyTree(srcLoops, AppPaths.GameDataSetLoopsFolder(destSet));
            if (move) Directory.Delete(srcLoops, recursive: true);
        }
        catch (Exception ex)
        {
            _log?.Warn("GameData", $"loop {verb} '{sourceSet}' → '{destSet}' failed: {ex.Message}");
            return new OpResult(false, $"Failed to {verb} loops: {ex.Message}");
        }

        ReloadIfActive(sourceSet, destSet);
        string done = move ? "Moved" : "Copied";
        _log?.Info("GameData",
            $"{done.ToLowerInvariant()} {files} loop file(s) from '{sourceSet}' → '{destSet}'.");
        return new OpResult(true, $"{done} {files} file(s) from '{sourceSet}' to '{destSet}'.");
    }

    // Delete setName from disk — its game-data tables AND its loop library.
    // Switches the cache off the set first when it was active, then fires
    // onSetDeleted so dangling profile / global references can be cleared.
    public OpResult DeleteSet(string setName)
    {
        if (string.IsNullOrWhiteSpace(setName))
            return new OpResult(false, "Pick a set to delete.");
        string dir = AppPaths.GameDataSetDir(setName);
        if (!Directory.Exists(dir))
            return new OpResult(false, $"Set '{setName}' does not exist.");

        // Release any live JsonDocument handles into the directory before
        // we remove it; this also clears the live loop/lair caches via the
        // ActiveSetChanged wiring.
        if (string.Equals(_cache.ActiveSet, setName, StringComparison.OrdinalIgnoreCase))
            _cache.SwitchSet(null);

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            _log?.Warn("GameData", $"delete set '{setName}' failed: {ex.Message}");
            return new OpResult(false, $"Failed to delete '{setName}': {ex.Message}");
        }

        _onSetDeleted(setName);
        _log?.Info("GameData", $"deleted game data set '{setName}' (tables + loops).");
        return new OpResult(true, $"Deleted set '{setName}'.");
    }

    private void ReloadIfActive(string sourceSet, string destSet)
    {
        if (_cache.ActiveSet is not { } active) return;
        if (string.Equals(active, sourceSet, StringComparison.OrdinalIgnoreCase)
         || string.Equals(active, destSet, StringComparison.OrdinalIgnoreCase))
            _reloadActiveLibrary();
    }

    private static bool ContainsAnyFile(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any();

    // Recursively copy src into dst, creating dst and any sub-directories.
    // Existing destination files are overwritten — the user explicitly chose to
    // push these loops over. Returns the number of files copied.
    private static int CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        int count = 0;
        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(dst, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            count++;
        }
        return count;
    }
}
