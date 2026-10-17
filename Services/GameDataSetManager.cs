using System.IO;
using System.Linq;

namespace MudPlay.Services;

// Game-data set lifecycle operations exposed by the Game Data → "Manage Game
// Data…" dialog: copy or move the data the user made for one set into another (the parts
// they tick: GameDataSetPart), and delete a set outright. "Loops" means the whole
// shared AppPaths.GameDataSetLoopsFolder tree — loop circuits, Auto-Lair setups,
// and the nav-folder subdirectories all live in it — so it goes as one library,
// merged into the destination's. Every other part is a file (or a few) and
// replaces the destination's.
//
// Reload + reference hygiene are folded into each op so the call site only has to
// render a result:
//   - A copy/move into or out of the active set fires reloadActive with the parts
//     that changed, so the live stores pick them up.
//   - Deleting the active set switches the cache to no-set first (so nothing
//     is still loaded from the directory), then fires onSetDeleted so the
//     caller can clear any profile / global reference that named the removed set.
//   - onSetDataReplaced names each set whose data an op replaced, active or not: the
//     destination of a copy or move, the source of a move, a deleted set. For what
//     is held per set outside the live stores, which reloadActive never reaches for
//     a set that isn't the active one. A copy or move of the loop library alone
//     replaces none of a set's data and names nothing.
// The manager itself only touches the filesystem + GameDataCache;
// settings/profile writes are the caller's job via the injected callbacks.
public sealed class GameDataSetManager
{
    private readonly GameDataCache _cache;
    private readonly Action<GameDataSetPart> _reloadActive;
    private readonly Action<string> _onSetDeleted;
    private readonly Action<string>? _onSetDataReplaced;
    private readonly LogService? _log;

    public GameDataSetManager(
        GameDataCache cache,
        Action<GameDataSetPart> reloadActive,
        Action<string> onSetDeleted,
        LogService? log = null,
        Action<string>? onSetDataReplaced = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(reloadActive);
        ArgumentNullException.ThrowIfNull(onSetDeleted);
        _cache = cache;
        _reloadActive = reloadActive;
        _onSetDeleted = onSetDeleted;
        _onSetDataReplaced = onSetDataReplaced;
        _log = log;
    }

    // Outcome of an op, ready to surface verbatim as dialog status text.
    public readonly record struct OpResult(bool Ok, string Message);

    // Every part, in the order the dialog lists them, with the name a result uses.
    public static readonly IReadOnlyList<(GameDataSetPart Part, string Name)> Parts = new[]
    {
        (GameDataSetPart.Loops,             "loops and lair setups"),
        (GameDataSetPart.Messages,          "message edits"),
        (GameDataSetPart.RecordOverrides,   "Game Data Browser edits"),
    };

    // Copy the ticked parts of sourceSet into destSet.
    public OpResult Copy(string sourceSet, string destSet, GameDataSetPart parts) =>
        CopyOrMove(sourceSet, destSet, parts, move: false);

    // Move the ticked parts of sourceSet into destSet (removed from the source).
    public OpResult Move(string sourceSet, string destSet, GameDataSetPart parts) =>
        CopyOrMove(sourceSet, destSet, parts, move: true);

    // How many files setName holds for one part: what the dialog shows beside each
    // tick box, and zero means there is nothing of it to take.
    public int FileCount(string setName, GameDataSetPart part)
    {
        if (string.IsNullOrWhiteSpace(setName) || !IsASet("Counting a set's files", setName)) return 0;
        try { return FilesOf(setName, part).Count(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Debug("GameData", $"counting {part} of '{setName}' failed: {ex.Message}");
            return 0;
        }
    }

    private OpResult CopyOrMove(string sourceSet, string destSet, GameDataSetPart parts, bool move)
    {
        string verb = move ? "move" : "copy";
        if (string.IsNullOrWhiteSpace(sourceSet) || string.IsNullOrWhiteSpace(destSet))
            return new OpResult(false, $"Pick a source and a destination set to {verb} between.");
        if (string.Equals(sourceSet, destSet, StringComparison.OrdinalIgnoreCase))
            return new OpResult(false, "Source and destination are the same set.");
        string operation = move ? "Moving a set's data" : "Copying a set's data";
        if (!IsASet(operation, sourceSet)) return new OpResult(false, $"'{sourceSet}' is not a game-data set.");
        if (!IsASet(operation, destSet)) return new OpResult(false, $"'{destSet}' is not a game-data set.");
        if (!Directory.Exists(AppPaths.GameDataSetDir(destSet)))
            return new OpResult(false, $"Destination set '{destSet}' does not exist.");
        if (parts == GameDataSetPart.None)
            return new OpResult(false, $"Tick what to {verb}.");

        List<string> done = new();
        List<string> empty = new();
        GameDataSetPart changed = GameDataSetPart.None;
        try
        {
            foreach ((GameDataSetPart part, string name) in Parts)
            {
                if (!parts.HasFlag(part)) continue;
                int files = Transfer(sourceSet, destSet, part, move);
                if (files == 0) { empty.Add(name); continue; }
                changed |= part;
                done.Add($"{name} ({files} {(files == 1 ? "file" : "files")})");
            }
        }
        catch (Exception ex)
        {
            _log?.Warn("GameData", $"{verb} '{sourceSet}' → '{destSet}' failed: {ex.Message}");
            // Whatever went across before the failure is live on disk: reload it.
            if (changed != GameDataSetPart.None) Changed(sourceSet, destSet, changed, move);
            return new OpResult(false, $"Failed to {verb}: {ex.Message}");
        }

        if (changed == GameDataSetPart.None)
            return new OpResult(false, $"'{sourceSet}' has none of that to {verb}.");

        Changed(sourceSet, destSet, changed, move);
        string past = move ? "Moved" : "Copied";
        string summary = $"{past} {string.Join(", ", done)} from '{sourceSet}' to '{destSet}'.";
        _log?.Info("GameData", summary);
        return new OpResult(true,
            empty.Count == 0 ? summary : $"{summary} Nothing there for: {string.Join(", ", empty)}.");
    }

    // A set's name is its folder's under the game-data folder, and the operations
    // here delete that folder or write into it. True when setName names a folder
    // directly inside it (the sets are picked from a listing, so any name that does
    // not is not one of them); otherwise the refusal is logged.
    private static bool IsASet(string operation, string? setName)
    {
        if (PathGuard.Child(AppPaths.GameDataRoot, setName) is not null) return true;
        PathGuard.Refuse(operation, "game-data set name", setName);
        return false;
    }

    // Take one part across; returns the files written.
    private static int Transfer(string sourceSet, string destSet, GameDataSetPart part, bool move)
    {
        if (part == GameDataSetPart.Loops)
        {
            string srcLoops = AppPaths.GameDataSetLoopsFolder(sourceSet);
            if (!Directory.Exists(srcLoops) || !ContainsAnyFile(srcLoops)) return 0;
            int copied = CopyTree(srcLoops, AppPaths.GameDataSetLoopsFolder(destSet));
            if (move) Directory.Delete(srcLoops, recursive: true);
            return copied;
        }

        int count = 0;
        foreach (string file in FilesOf(sourceSet, part).ToList())
        {
            string target = part == GameDataSetPart.RecordOverrides
                ? OverrideFileFor(file, sourceSet, destSet)
                : Path.Combine(AppPaths.GameDataSetDir(destSet), Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);
            if (move) File.Delete(file);
            count++;
        }
        return count;
    }

    // The files setName holds for one part.
    private static IEnumerable<string> FilesOf(string setName, GameDataSetPart part)
    {
        switch (part)
        {
            case GameDataSetPart.Loops:
                string loops = AppPaths.GameDataSetLoopsFolder(setName);
                return Directory.Exists(loops)
                    ? Directory.EnumerateFiles(loops, "*", SearchOption.AllDirectories)
                    : Enumerable.Empty<string>();
            case GameDataSetPart.RecordOverrides:
                return OverrideFilesOf(setName);
            default:
                return SetFilesOf(setName, part).Where(File.Exists);
        }
    }

    private static string[] SetFilesOf(string setName, GameDataSetPart part) => part switch
    {
        GameDataSetPart.Messages          => new[]
        {
            AppPaths.MessagesFile(setName),
            AppPaths.MonsterMessagesFile(setName),
            AppPaths.FlavorPrefixesFile(setName),
        },
        _ => Array.Empty<string>(),
    };

    // Record overrides sit beside the tier they belong to, not in the set's folder:
    // Global/, each realm's folder and each character's, all named
    // `{table}_overrides.{set}.json` (AppPaths.OverrideFile). Found by that name,
    // whole suffix, so a set whose name another set's begins with isn't picked up.
    private static IEnumerable<string> OverrideFilesOf(string setName)
    {
        string suffix = OverrideSuffix(setName);
        foreach (string root in new[] { Path.Combine(AppPaths.DataRoot, "Global"), AppPaths.BbsDir })
        {
            if (!Directory.Exists(root)) continue;
            foreach (string file in Directory.EnumerateFiles(root, "*_overrides.*.json", SearchOption.AllDirectories))
                if (Path.GetFileName(file).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    yield return file;
        }
    }

    private static string OverrideSuffix(string setName) => $"_overrides.{setName}.json";

    private static string OverrideFileFor(string sourceFile, string sourceSet, string destSet)
    {
        string name = Path.GetFileName(sourceFile);
        string table = name[..^OverrideSuffix(sourceSet).Length];
        return Path.Combine(Path.GetDirectoryName(sourceFile)!, table + OverrideSuffix(destSet));
    }

    // Delete setName from disk — its game-data tables AND its loop library.
    // Switches the cache off the set first when it was active, then fires
    // onSetDeleted so dangling profile / global references can be cleared.
    public OpResult DeleteSet(string setName)
    {
        if (string.IsNullOrWhiteSpace(setName))
            return new OpResult(false, "Pick a set to delete.");
        const string operation = "Deleting a game-data set";
        if (!IsASet(operation, setName)) return new OpResult(false, $"'{setName}' is not a game-data set.");
        string dir = AppPaths.GameDataSetDir(setName);
        if (!Directory.Exists(dir))
            return new OpResult(false, $"Set '{setName}' does not exist.");

        // Drop the tables loaded from the directory before
        // we remove it; this also clears the live loop/lair caches via the
        // ActiveSetChanged wiring.
        if (string.Equals(_cache.ActiveSet, setName, StringComparison.OrdinalIgnoreCase))
            _cache.SwitchSet(null);

        try
        {
            // Only ever a folder inside the game-data folder, never that folder
            // itself or one above it.
            if (!PathGuard.DeleteTree(dir, AppPaths.GameDataRoot, operation))
                return new OpResult(false, $"'{setName}' is not a game-data set.");
        }
        catch (Exception ex)
        {
            _log?.Warn("GameData", $"delete set '{setName}' failed: {ex.Message}");
            return new OpResult(false, $"Failed to delete '{setName}': {ex.Message}");
        }

        _onSetDeleted(setName);
        // A set imported later under this name is another set.
        _onSetDataReplaced?.Invoke(setName);
        _log?.Info("GameData", $"deleted game data set '{setName}' (tables + loops).");
        return new OpResult(true, $"Deleted set '{setName}'.");
    }

    // The parts in changed went from sourceSet into destSet. The destination's data
    // is replaced, and so is the source's when they were moved out of it; the loop
    // library alone is no part of a set's data. Told before the reload, so what a
    // reload of the active set shows is already without what was held for it.
    private void Changed(string sourceSet, string destSet, GameDataSetPart changed, bool move)
    {
        if (changed != GameDataSetPart.Loops)
        {
            _onSetDataReplaced?.Invoke(destSet);
            if (move) _onSetDataReplaced?.Invoke(sourceSet);
        }
        ReloadIfActive(sourceSet, destSet, changed);
    }

    private void ReloadIfActive(string sourceSet, string destSet, GameDataSetPart changed)
    {
        if (_cache.ActiveSet is not { } active) return;
        if (string.Equals(active, sourceSet, StringComparison.OrdinalIgnoreCase)
         || string.Equals(active, destSet, StringComparison.OrdinalIgnoreCase))
            _reloadActive(changed);
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
