using System.Collections.Generic;
using System.IO;
using System.Linq;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

/// <summary>
/// Per-BBS Auto-Lair setup catalogue. Round-trips
/// <see cref="LairSetup"/>s under <see cref="AppPaths.BbsLairsFolder"/>
/// one file per setup, keyed by name. Mirrors <see cref="LoopManager"/>'s
/// shape — same load-on-pin, save-fires-Changed, delete-on-name lifecycle —
/// so the UI rail can render Loops + Auto-Lair Setups side by side with
/// identical wiring.
/// </summary>
public sealed class LairManager
{
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
    /// Pass <c>null</c> to clear. Same semantics as
    /// <see cref="LoopManager.LoadAll"/>.
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

        string folder = AppPaths.BbsLairsFolder(bbsName);
        if (!Directory.Exists(folder))
        {
            _log?.Info("Lairs", $"no lairs folder for '{bbsName}'; empty catalogue.");
            SetupsChanged?.Invoke();
            return;
        }

        int loaded = 0;
        int failed = 0;
        foreach (string path in Directory.EnumerateFiles(folder, "*.json"))
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
    /// <see cref="AppPaths.BbsLairsFolder"/>. No-op when no BBS is bound.
    /// </summary>
    public void Save(LairSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        if (string.IsNullOrWhiteSpace(setup.Name))
            throw new ArgumentException("Setup name is required.", nameof(setup));
        if (_bbsName is null) return;

        setup.SchemaVersion = 1;
        string folder = AppPaths.BbsLairsFolder(_bbsName);
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

        string path = Path.Combine(AppPaths.BbsLairsFolder(_bbsName), SafeFileName(name));
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex)
        {
            _log?.Warn("Lairs", $"failed to delete setup file '{path}': {ex.Message}");
        }
        SetupsChanged?.Invoke();
        return true;
    }

    private static string SafeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString() + ".json";
    }
}
