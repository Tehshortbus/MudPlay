using System.Collections.Generic;
using System.IO;
using System.Linq;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

/// <summary>
/// Per-BBS Auto-Lair setup catalogue. Round-trips
/// <see cref="LairSetup"/>s under the shared
/// <see cref="AppPaths.BbsLoopsFolder"/> — same folder as loops, with
/// the <c>.lair.json</c> filename suffix as the schema discriminator.
/// Mirrors <see cref="LoopManager"/>'s shape (same load-on-pin,
/// save-fires-Changed, delete-on-name lifecycle) so the UI rail can
/// render Loops + Auto-Lair Setups side by side with identical wiring.
/// </summary>
/// <remarks>
/// One-shot migration: earlier exports lived under
/// <see cref="AppPaths.LegacyBbsLairsFolder"/> as plain <c>.json</c>
/// files. The first <see cref="LoadAll"/> for a given BBS scans that
/// folder, copies each setup into the shared Loops folder with the
/// <c>.lair.json</c> suffix, and removes the legacy directory once
/// empty.
/// </remarks>
public sealed class LairManager
{
    /// <summary>Suffix that flags a file in the shared Loops folder as a lair setup.</summary>
    public const string LairFileSuffix = ".lair.json";

    private readonly LogService? _log;
    private readonly Dictionary<string, LairSetup> _setups
        = new(StringComparer.OrdinalIgnoreCase);
    private string? _bbsName;

    public LairManager(LogService? log = null)
    {
        _log = log;
    }

    /// <summary>BBS the catalogue is bound to, or null when no BBS is active.</summary>
    public string? BbsName => _bbsName;

    /// <summary>Saved setups for the active BBS, sorted by name (case-insensitive).</summary>
    public IReadOnlyList<LairSetup> Setups =>
        _setups.Values
               .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
               .ToArray();

    /// <summary>Fired after every mutation (load / save / delete).</summary>
    public event Action? SetupsChanged;

    public LairSetup? Get(string name) =>
        _setups.TryGetValue(name, out LairSetup? setup) ? setup : null;

    /// <summary>
    /// Rebuild the in-memory cache from disk for <paramref name="bbsName"/>.
    /// Pass <c>null</c> to clear.
    /// </summary>
    public void LoadAll(string? bbsName)
    {
        _setups.Clear();
        _bbsName = bbsName;

        if (string.IsNullOrWhiteSpace(bbsName))
        {
            SetupsChanged?.Invoke();
            return;
        }

        // One-shot migration: drain the legacy Lairs/ folder into the
        // shared Loops/ folder with the new .lair.json suffix. Safe to
        // re-run — the move skips files whose destination already
        // exists (later loads can't tell the difference).
        MigrateLegacyFolderIfPresent(bbsName);

        string folder = AppPaths.BbsLoopsFolder(bbsName);
        if (!Directory.Exists(folder))
        {
            _log?.Info("Lairs", $"no loops folder for '{bbsName}'; empty lair catalogue.");
            SetupsChanged?.Invoke();
            return;
        }

        int loaded = 0;
        int failed = 0;
        foreach (string path in Directory.EnumerateFiles(folder, "*" + LairFileSuffix))
        {
            try
            {
                LairSetup? setup = JsonStore.Load<LairSetup>(path);
                if (setup is null || string.IsNullOrWhiteSpace(setup.Name)) { failed++; continue; }
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
            $"loaded {loaded} setup(s) for '{bbsName}'"
          + (failed > 0 ? $" ({failed} failed)" : string.Empty));
        SetupsChanged?.Invoke();
    }

    /// <summary>
    /// Persist <paramref name="setup"/> under
    /// <see cref="AppPaths.BbsLoopsFolder"/> with the
    /// <see cref="LairFileSuffix"/> filename extension. No-op when no
    /// BBS is bound.
    /// </summary>
    public void Save(LairSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        if (string.IsNullOrWhiteSpace(setup.Name))
            throw new ArgumentException("Setup name is required.", nameof(setup));
        if (_bbsName is null) return;

        setup.SchemaVersion = 1;
        string folder = AppPaths.BbsLoopsFolder(_bbsName);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, SafeFileName(setup.Name));
        JsonStore.Save(path, setup);
        _setups[setup.Name] = setup;
        SetupsChanged?.Invoke();
    }

    /// <summary>Delete the setup named <paramref name="name"/>. No-op when not present or no BBS bound.</summary>
    public bool Delete(string name)
    {
        if (_bbsName is null) return false;
        if (!_setups.Remove(name)) return false;

        string path = Path.Combine(AppPaths.BbsLoopsFolder(_bbsName), SafeFileName(name));
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex)
        {
            _log?.Warn("Lairs", $"failed to delete setup file '{path}': {ex.Message}");
        }
        SetupsChanged?.Invoke();
        return true;
    }

    // ----- internals -----------------------------------------------

    private void MigrateLegacyFolderIfPresent(string bbsName)
    {
        string legacy = AppPaths.LegacyBbsLairsFolder(bbsName);
        if (!Directory.Exists(legacy)) return;

        string target = AppPaths.BbsLoopsFolder(bbsName);
        Directory.CreateDirectory(target);

        int moved = 0;
        foreach (string srcPath in Directory.EnumerateFiles(legacy, "*.json"))
        {
            string baseName = Path.GetFileNameWithoutExtension(srcPath);
            if (string.IsNullOrWhiteSpace(baseName)) continue;
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

    private static string SafeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString() + LairFileSuffix;
    }
}
