using System.Collections.Generic;
using System.IO;
using System.Linq;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// Per-set Auto-Lair setup catalogue. Round-trips LairSetups under the
// shared GameDataSetLoopsFolder — same folder as loops, with the
// .lair.json filename suffix as the schema discriminator. Mirrors
// LoopManager's shape (same load-on-pin, save-fires-Changed,
// delete-on-name lifecycle) so the UI rail can render Loops + Auto-Lair
// Setups side by side with identical wiring.
//
// One-shot migration: earlier exports lived under LegacyBbsLairsFolder as
// plain .json files. The first LoadAll for a given set scans that folder,
// copies each setup into the shared Loops folder with the .lair.json
// suffix, and removes the legacy directory once empty.
public sealed class LairManager
{
    // Suffix that flags a file in the shared Loops folder as a lair setup.
    public const string LairFileSuffix = ".lair";

    // Intermediate suffix used briefly between the original
    // Lairs/{name}.json layout and the current {name}.lair layout. LoadAll
    // picks these up and renames them to LairFileSuffix so users who
    // shipped a build during that transition window keep their setups.
    public const string LegacyLairFileSuffix = ".lair.json";

    private readonly LogService? _log;
    private readonly Dictionary<string, LairSetup> _setups
        = new(StringComparer.OrdinalIgnoreCase);
    private string? _setName;

    public LairManager(LogService? log = null)
    {
        _log = log;
    }

    // Game-data set the catalogue is bound to, or null when no set is active.
    public string? SetName => _setName;

    // Saved setups for the active set, sorted by name (case-insensitive).
    public IReadOnlyList<LairSetup> Setups =>
        _setups.Values
               .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
               .ToArray();

    // Fired after every mutation (load / save / delete).
    public event Action? SetupsChanged;

    public LairSetup? Get(string name) =>
        _setups.TryGetValue(name, out LairSetup? setup) ? setup : null;

    // Rebuild the in-memory cache from disk for setName. Pass null to clear.
    public void LoadAll(string? setName)
    {
        _setups.Clear();
        _setName = setName;

        if (string.IsNullOrWhiteSpace(setName))
        {
            SetupsChanged?.Invoke();
            return;
        }

        // One-shot migration step A: drain the legacy Lairs/ folder
        // into the shared Loops/ folder. Step B (right after) renames
        // any intermediate .lair.json files to the final .lair suffix.
        // Both steps are safe to re-run.
        MigrateLegacyFolderIfPresent(setName);

        string folder = AppPaths.GameDataSetLoopsFolder(setName);
        MigrateIntermediateSuffixIfPresent(folder);

        if (!Directory.Exists(folder))
        {
            _log?.Info("Lairs", $"no loops folder for set '{setName}'; empty lair catalogue.");
            SetupsChanged?.Invoke();
            return;
        }

        int loaded = 0;
        int failed = 0;
        // Recurse: setups may live in user-created sub-folders under the
        // shared Loops root. The relative directory is the setup's Folder.
        foreach (string path in Directory.EnumerateFiles(folder, "*" + LairFileSuffix, SearchOption.AllDirectories))
        {
            try
            {
                LairSetup? setup = JsonStore.Load<LairSetup>(path);
                // Schema-validate: a .lair file the user drops in by
                // hand that doesn't match the LairSetup shape (or
                // can't be deserialised) is silently skipped — no-op
                // per user direction.
                if (setup is null || string.IsNullOrWhiteSpace(setup.Name)) { failed++; continue; }
                setup.Folder = NavFolders.RelativeFolder(folder, path);
                _setups[setup.Name] = setup;
                loaded++;
            }
            catch (Exception ex)
            {
                _log?.Warn("Lairs", $"failed to load '{path}': {ex.Message}");
                failed++;
            }
        }
        _log?.Info("Lairs",
            $"loaded {loaded} setup(s) for set '{setName}'"
          + (failed > 0 ? $" ({failed} failed)" : string.Empty));
        SetupsChanged?.Invoke();
    }

    // Persist setup under GameDataSetLoopsFolder with the LairFileSuffix
    // filename extension. No-op when no set is active.
    public void Save(LairSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        if (string.IsNullOrWhiteSpace(setup.Name))
            throw new ArgumentException("Setup name is required.", nameof(setup));
        if (_setName is null) return;

        setup.SchemaVersion = 1;
        setup.Folder = NavFolders.Normalize(setup.Folder);
        string root = AppPaths.GameDataSetLoopsFolder(_setName);
        // A setup name is unique set-wide, so a folder change is a move:
        // delete any stale file for this name (in any folder) first so
        // we don't leave a duplicate behind.
        DeleteFileForName(root, setup.Name);
        string dir = NavFolders.ToDirectory(root, setup.Folder);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, SafeFileName(setup.Name));
        JsonStore.Save(path, setup);
        _setups[setup.Name] = setup;
        SetupsChanged?.Invoke();
    }

    // Move the setup named name into folder (empty = Loops root). No-op
    // when the setup isn't in the catalogue or no set is active.
    public bool Move(string name, string? folder)
    {
        if (Get(name) is not { } setup) return false;
        string target = NavFolders.Normalize(folder);
        if (string.Equals(NavFolders.Normalize(setup.Folder), target, StringComparison.OrdinalIgnoreCase))
            return false;
        setup.Folder = target;
        Save(setup);
        return true;
    }

    // Delete the setup named name. No-op when not present or no set is active.
    public bool Delete(string name)
    {
        if (_setName is null) return false;
        if (!_setups.Remove(name)) return false;

        DeleteFileForName(AppPaths.GameDataSetLoopsFolder(_setName), name);
        SetupsChanged?.Invoke();
        return true;
    }

    // Delete the on-disk .lair file for name wherever it lives under root
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
                _log?.Warn("Lairs", $"failed to delete setup file '{path}': {ex.Message}");
            }
        }
    }

    // ----- internals -----------------------------------------------

    private void MigrateLegacyFolderIfPresent(string setName)
    {
        string legacy = AppPaths.LegacyBbsLairsFolder(setName);
        if (!Directory.Exists(legacy)) return;

        string target = AppPaths.GameDataSetLoopsFolder(setName);
        Directory.CreateDirectory(target);

        int moved = 0;
        foreach (string srcPath in Directory.EnumerateFiles(legacy, "*.json"))
        {
            string baseName = Path.GetFileNameWithoutExtension(srcPath);
            if (string.IsNullOrWhiteSpace(baseName)) continue;
            // Land on the FINAL suffix (.lair) — skip the intermediate
            // .lair.json stage entirely for legacy-folder migration so
            // users don't see two renames in their git diff.
            string destPath = Path.Combine(target, baseName + LairFileSuffix);
            if (File.Exists(destPath)) continue; // already migrated; user-edited copy wins

            try
            {
                File.Move(srcPath, destPath);
                moved++;
            }
            catch (Exception ex)
            {
                _log?.Warn("Lairs",
                    $"migration: failed to move '{srcPath}' → '{destPath}': {ex.Message}");
            }
        }

        // Remove the empty legacy folder so subsequent loads short-
        // circuit at the Directory.Exists check.
        try
        {
            if (!Directory.EnumerateFileSystemEntries(legacy).Any())
                Directory.Delete(legacy, recursive: false);
        }
        catch (Exception ex)
        {
            _log?.Warn("Lairs", $"migration: failed to remove legacy folder '{legacy}': {ex.Message}");
        }

        if (moved > 0)
            _log?.Info("Lairs",
                $"migrated {moved} setup(s) from legacy Lairs/ → Loops/{LairFileSuffix}.");
    }

    // Rename intermediate {name}.lair.json files to the final {name}.lair
    // suffix. Covers the very short window in which the unification build
    // shipped with the longer suffix.
    private void MigrateIntermediateSuffixIfPresent(string folder)
    {
        if (!Directory.Exists(folder)) return;
        int moved = 0;
        foreach (string srcPath in Directory.EnumerateFiles(folder, "*" + LegacyLairFileSuffix))
        {
            // Strip the FULL legacy suffix to get the user's chosen
            // name back — Path.GetFileNameWithoutExtension would only
            // peel ".json".
            string fileName = Path.GetFileName(srcPath);
            string baseName = fileName.Substring(0,
                fileName.Length - LegacyLairFileSuffix.Length);
            if (string.IsNullOrWhiteSpace(baseName)) continue;
            string destPath = Path.Combine(folder, baseName + LairFileSuffix);
            if (File.Exists(destPath)) continue;
            try
            {
                File.Move(srcPath, destPath);
                moved++;
            }
            catch (Exception ex)
            {
                _log?.Warn("Lairs",
                    $"migration: failed to rename '{srcPath}' → '{destPath}': {ex.Message}");
            }
        }
        if (moved > 0)
            _log?.Info("Lairs",
                $"migrated {moved} setup(s) from {LegacyLairFileSuffix} → {LairFileSuffix}.");
    }

    private static string SafeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString() + LairFileSuffix;
    }
}
