using System.Collections.ObjectModel;
using System.IO;
using FujinTerm.Models.GameData;

namespace FujinTerm.Services;

// In-memory cache of the Messages/Responses catalogue for the active set.
// Records are paired with the active game-data set on disk at
// Data/game data/{set}/messages.json, falling back to the universal
// wcc-derived seed at Data/Global/Messages.seed.json (bootstrapped from the
// bundled Defaults/ copy on first launch).
//
// Wiring: AppServices subscribes the store to
// GameDataCache.ActiveSetChanged — on every set switch the file at
// AppPaths.MessagesFile is reloaded (missing file ⇒ falls through to the
// seed). The Game Data Browser → Messages tab binds the live Messages
// collection.
public sealed class MessageStore
{
    private readonly LogService? _log;

    // Live mirror of the active set's message records. Bound by the Messages tab.
    public ObservableCollection<MessageRecord> Messages { get; } = new();

    // Set name currently sourcing Messages, or null when none is active.
    public string? ActiveSet { get; private set; }

    public MessageStore() { }

    // Production ctor — wire the log sink so parse failures surface in the
    // LogPane instead of silently leaving the catalogue empty.
    public MessageStore(LogService log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    // Switch the catalogue to setName's on-disk file. Pass null to clear (no
    // set active). Load priority:
    //   1. Per-set file AppPaths.MessagesFile
    //      (Data/game data/{set}/messages.json) — the canonical persisted
    //      state once a user has edited.
    //   2. Universal seed AppPaths.DefaultMessagesSeedFile
    //      (Data/Global/Messages.seed.json) — applies to every set; the
    //      message text is universal across MajorMUD realms. Bootstrapped
    //      from the bundled Defaults/ copy on first launch via
    //      AppPaths.EnsureGlobalSeedsBootstrapped.
    // The seed itself is never written.
    public void Load(string? setName)
    {
        Messages.Clear();
        ActiveSet = setName;
        if (string.IsNullOrWhiteSpace(setName)) return;

        if (TryLoadInto(AppPaths.MessagesFile(setName))) return;
        TryLoadInto(AppPaths.DefaultMessagesSeedFile);
    }

    // Read a JSON list from path and append every record to Messages.
    // Returns true iff the file existed AND parsed cleanly. A corrupt file
    // returns false + leaves Messages in whatever state the partial parse
    // left it (callers reset via the upstream Clear() before invoking).
    private bool TryLoadInto(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            List<MessageRecord>? loaded = JsonStore.Load<List<MessageRecord>>(path);
            if (loaded is null) return false;
            foreach (MessageRecord m in loaded) Messages.Add(m);
            return true;
        }
        catch (Exception ex)
        {
            // Corrupt file ⇒ leave empty, but DO surface — a silent
            // swallow is what hid the missing JsonStringEnumConverter
            // for the entire seed-loading work. Log loud so future
            // schema drift fails visibly.
            _log?.Log(LogSeverity.Warn, "Messages",
                $"Failed to load '{path}': {ex.Message}");
            return false;
        }
    }

    // Persist Messages to ActiveSet's file.
    public void Save()
    {
        if (string.IsNullOrWhiteSpace(ActiveSet)) return;
        JsonStore.Save(AppPaths.MessagesFile(ActiveSet), Messages);
    }

    // Replace the catalogue with records and persist.
    public void Replace(IEnumerable<MessageRecord> records)
    {
        Messages.Clear();
        foreach (MessageRecord m in records) Messages.Add(m);
        Save();
    }
}
