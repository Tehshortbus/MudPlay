using System.Collections.Generic;
using System.IO;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// Folder CRUD over the shared per-set Loops directory
// (AppPaths.GameDataSetLoopsFolder), which holds BOTH navigation loops
// (LoopManager) and Auto-Lair setups (LairManager) as real on-disk
// subdirectories. Because the two catalogues share one directory tree, a
// folder create / rename / delete affects both — so the coordinator owns the
// filesystem operation once and reloads both managers afterwards, rather than
// each manager racing the same directory.
//
// The stored folder vocabulary is the same /-separated form used everywhere in
// navigation (see NavFolders); NavFolders.ToDirectory bridges it to real
// subdirectories. Empty folders are first-class — they exist as real
// (file-less) directories, so AllFolders finds them without any side record.
//
// Per-favourite (GOTO) folders live in the character profile, not the
// filesystem, so they are NOT managed here — see FavoritesStore. This
// coordinator is loops + lairs only.
public sealed class NavFolderManager
{
    private readonly LoopManager _loops;
    private readonly LairManager _lairs;
    private readonly LogService? _log;

    public NavFolderManager(LoopManager loops, LairManager lairs, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(lairs);
        _loops = loops;
        _lairs = lairs;
        _log = log;
    }

    // Fires after any folder mutation (create / rename / delete). The rail /
    // Manage dialog refresh their folder tree on this — empty-folder creation
    // produces no loop/lair change, so the UI can't rely on the managers' own
    // events alone.
    public event Action? FoldersChanged;

    // The game-data set whose Loops directory we operate on. Sourced from
    // LoopManager.
    private string? SetName => _loops.SetName;

    // Every folder (in stored / form) that physically exists under the Loops
    // root, including empty ones. Excludes the root. De-duplicated; order
    // unspecified — the UI sorts.
    public IReadOnlyCollection<string> AllFolders
    {
        get
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (SetName is not { } setName) return set;
            string root = AppPaths.GameDataSetLoopsFolder(setName);
            if (!Directory.Exists(root)) return set;
            foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                string rel = NavFolders.Normalize(Path.GetRelativePath(root, dir));
                if (rel.Length != 0) set.Add(rel);
            }
            return set;
        }
    }

    // Create an empty folder (and any missing ancestors) under the Loops root
    // so it shows in the tree before any loop / lair is filed under it. No-op
    // when no set is active, the path is the root, or the directory already
    // exists.
    public bool CreateFolder(string path)
    {
        if (SetName is not { } setName) return false;
        string norm = NavFolders.Normalize(path);
        if (norm.Length == 0) return false;
        string dir = NavFolders.ToDirectory(AppPaths.GameDataSetLoopsFolder(setName), norm);
        if (Directory.Exists(dir)) return false;
        Directory.CreateDirectory(dir);
        _log?.Info("NavFolders", $"created folder '{norm}'");
        FoldersChanged?.Invoke();
        return true;
    }

    // Rename folder oldPath (and everything beneath it) to newPath. Reloads
    // both managers so their in-memory Folder values pick up the move. No-op at
    // the root, when the source is missing, or the target already exists (we
    // don't merge — surfaced to the user as a no-op).
    public bool RenameFolder(string oldPath, string newPath)
    {
        if (SetName is not { } setName) return false;
        string from = NavFolders.Normalize(oldPath);
        string to = NavFolders.Normalize(newPath);
        if (from.Length == 0 || to.Length == 0) return false;
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return false;

        string root = AppPaths.GameDataSetLoopsFolder(setName);
        string fromDir = NavFolders.ToDirectory(root, from);
        string toDir = NavFolders.ToDirectory(root, to);
        if (!Directory.Exists(fromDir)) return false;
        if (Directory.Exists(toDir))
        {
            _log?.Warn("NavFolders", $"rename '{from}' → '{to}' skipped: target exists.");
            return false;
        }

        try
        {
            string? parent = Path.GetDirectoryName(toDir);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            Directory.Move(fromDir, toDir);
        }
        catch (Exception ex)
        {
            _log?.Warn("NavFolders", $"failed to rename '{from}' → '{to}': {ex.Message}");
            return false;
        }

        _log?.Info("NavFolders", $"renamed folder '{from}' → '{to}'");
        ReloadStores(setName);
        FoldersChanged?.Invoke();
        return true;
    }

    // Remove folder path. When moveContentsToParent is true, its immediate
    // children (loops, lairs, and sub-folders, each kept intact) are
    // re-parented one level up first; otherwise the directory is deleted with
    // everything inside. Reloads both managers. No-op at the root or when the
    // folder doesn't exist.
    public bool DeleteFolder(string path, bool moveContentsToParent = true)
    {
        if (SetName is not { } setName) return false;
        string from = NavFolders.Normalize(path);
        if (from.Length == 0) return false;

        string root = AppPaths.GameDataSetLoopsFolder(setName);
        string dir = NavFolders.ToDirectory(root, from);
        if (!Directory.Exists(dir)) return false;

        try
        {
            if (moveContentsToParent)
            {
                string parentDir = NavFolders.ToDirectory(root, NavFolders.Parent(from));
                Directory.CreateDirectory(parentDir);
                foreach (string entry in Directory.EnumerateFileSystemEntries(dir))
                {
                    string dest = Path.Combine(parentDir, Path.GetFileName(entry));
                    // Collision = a same-named item already sits in the
                    // parent. Keep the existing one; leave ours behind to
                    // be removed with the directory (safer than clobber).
                    if (File.Exists(dest) || Directory.Exists(dest)) continue;
                    if (Directory.Exists(entry)) Directory.Move(entry, dest);
                    else File.Move(entry, dest);
                }
            }
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            _log?.Warn("NavFolders", $"failed to delete folder '{from}': {ex.Message}");
            return false;
        }

        _log?.Info("NavFolders", $"removed folder '{from}'");
        ReloadStores(setName);
        FoldersChanged?.Invoke();
        return true;
    }

    private void ReloadStores(string setName)
    {
        _loops.LoadAll(setName);
        _lairs.LoadAll(setName);
    }
}
