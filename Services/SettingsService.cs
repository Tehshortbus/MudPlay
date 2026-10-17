using MudPlay.Models.Settings;

namespace MudPlay.Services;

// Owns Global/global.json — the Global tier of the settings hierarchy.
// Singleton owned by AppServices.
public sealed class SettingsService
{
    private GlobalSettings _current;

    // The currently loaded global-settings DTO. Never null.
    public GlobalSettings Current => _current;

    // Fires after Save writes a new snapshot to disk. Consumers that mirror
    // state from the global file re-read here.
    public event Action<GlobalSettings>? GlobalSettingsChanged;

    // Every client on this machine shares global.json. The file as this client last
    // read or wrote it is kept, so a save carries only what this client changed onto
    // whatever another has saved since, and a change another saved shows up here.
    private readonly SharedFileStamp _stamp = new();
    private System.Text.Json.Nodes.JsonNode? _baseline;

    // Construct and load. If the file is missing (first run) a default
    // GlobalSettings is created in memory but not yet written — the first Save
    // call persists it.
    public SettingsService()
    {
        _stamp.Mark(AppPaths.GlobalSettingsFile);
        _current = JsonStore.Load<GlobalSettings>(AppPaths.GlobalSettingsFile)
            ?? new GlobalSettings();
        _baseline = AsNode(_current);
    }

    // Persist the current in-memory snapshot to disk and fire
    // GlobalSettingsChanged. When another client has saved the file since this one
    // read it, what this client changed is applied on top of theirs.
    public void Save()
    {
        System.Text.Json.Nodes.JsonNode? mine = AsNode(_current);
        if (_stamp.ChangedOutside(AppPaths.GlobalSettingsFile) && ReadFile() is { } theirs)
        {
            mine = JsonThreeWayMerge.Merge(_baseline, mine, theirs);
            Adopt(mine);
        }
        JsonStore.Save(AppPaths.GlobalSettingsFile, _current);
        _stamp.Mark(AppPaths.GlobalSettingsFile);
        _baseline = mine;
        GlobalSettingsChanged?.Invoke(_current);
    }

    // Take in what another client saved, keeping anything changed here and not yet
    // saved. Called on the heartbeat. True when the settings changed.
    public bool TakeInOutsideChanges()
    {
        if (!_stamp.ChangedOutside(AppPaths.GlobalSettingsFile)) return false;
        if (ReadFile() is not { } theirs) return false;
        _stamp.Mark(AppPaths.GlobalSettingsFile);
        Adopt(JsonThreeWayMerge.Merge(_baseline, AsNode(_current), theirs));
        _baseline = theirs;
        GlobalSettingsChanged?.Invoke(_current);
        return true;
    }

    private static System.Text.Json.Nodes.JsonNode? AsNode(GlobalSettings settings) =>
        System.Text.Json.JsonSerializer.SerializeToNode(settings, JsonStore.Options);

    // The file as it stands, or null when it is missing or can't be read just now
    // (the next call tries again).
    private static System.Text.Json.Nodes.JsonNode? ReadFile()
    {
        try
        {
            return JsonStore.Load<GlobalSettings>(AppPaths.GlobalSettingsFile) is { } onFile ? AsNode(onFile) : null;
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException)
        {
            return null;
        }
    }

    // Make the live settings object hold merged's values. Copied onto the same
    // object, a property at a time, because callers keep Current and change it
    // before saving; a new object would leave them changing one nobody saves.
    private void Adopt(System.Text.Json.Nodes.JsonNode? merged)
    {
        if (System.Text.Json.JsonSerializer.Deserialize<GlobalSettings>(merged, JsonStore.Options) is not { } fresh)
            return;
        foreach (System.Reflection.PropertyInfo property in typeof(GlobalSettings).GetProperties())
            if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
                property.SetValue(_current, property.GetValue(fresh));
    }
}
