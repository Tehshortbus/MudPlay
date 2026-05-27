using FujinTerm.Models.Settings;

namespace FujinTerm.Services;

/// <summary>
/// Owns <c>Data/Global/global.json</c> — the Global tier of the settings
/// hierarchy. Singleton owned by <see cref="AppServices"/>.
/// </summary>
public sealed class SettingsService
{
    private GlobalSettings _current;

    /// <summary>The currently loaded global-settings DTO. Never <c>null</c>.</summary>
    public GlobalSettings Current => _current;

    /// <summary>
    /// Fires after <see cref="Save"/> writes a new snapshot to disk. Consumers
    /// that mirror state from the global file re-read here.
    /// </summary>
    public event Action<GlobalSettings>? GlobalSettingsChanged;

    /// <summary>
    /// Construct and load. If the file is missing (first run) a default
    /// <see cref="GlobalSettings"/> is created in memory but not yet written —
    /// the first <see cref="Save"/> call persists it.
    /// </summary>
    public SettingsService()
    {
        _current = JsonStore.Load<GlobalSettings>(AppPaths.GlobalSettingsFile)
            ?? new GlobalSettings();
    }

    /// <summary>Replace the in-memory snapshot wholesale (used by OK-commit flows).</summary>
    public void Replace(GlobalSettings next)
    {
        _current = next ?? throw new ArgumentNullException(nameof(next));
    }

    /// <summary>
    /// Persist the current in-memory snapshot to disk and fire
    /// <see cref="GlobalSettingsChanged"/>.
    /// </summary>
    public void Save()
    {
        JsonStore.Save(AppPaths.GlobalSettingsFile, _current);
        GlobalSettingsChanged?.Invoke(_current);
    }
}
