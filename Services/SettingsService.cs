using FujinTerm.Models.Settings;

namespace FujinTerm.Services;

// Owns Data/Global/global.json — the Global tier of the settings hierarchy.
// Singleton owned by AppServices.
public sealed class SettingsService
{
    private GlobalSettings _current;

    // The currently loaded global-settings DTO. Never null.
    public GlobalSettings Current => _current;

    // Fires after Save writes a new snapshot to disk. Consumers that mirror
    // state from the global file re-read here.
    public event Action<GlobalSettings>? GlobalSettingsChanged;

    // Construct and load. If the file is missing (first run) a default
    // GlobalSettings is created in memory but not yet written — the first Save
    // call persists it.
    public SettingsService()
    {
        _current = JsonStore.Load<GlobalSettings>(AppPaths.GlobalSettingsFile)
            ?? new GlobalSettings();
    }

    // Replace the in-memory snapshot wholesale (used by OK-commit flows).
    public void Replace(GlobalSettings next)
    {
        _current = next ?? throw new ArgumentNullException(nameof(next));
    }

    // Persist the current in-memory snapshot to disk and fire
    // GlobalSettingsChanged.
    public void Save()
    {
        JsonStore.Save(AppPaths.GlobalSettingsFile, _current);
        GlobalSettingsChanged?.Invoke(_current);
    }
}
