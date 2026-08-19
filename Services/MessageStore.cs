using System.IO;
using MudPlay.Models.GameData;

namespace MudPlay.Services;

// In-memory cache of the Messages/Responses catalogue for the active set.
// Records are paired with the active game-data set on disk at
// Data/game data/{set}/messages.json, falling back to the realm-flavored seed at
// Data/Global/Messages.{stock|paradigm}.seed.json (the realm is picked from the
// set's Info.json Legit; each seed is decoded offline from that realm's MegaMUD
// messages.md and bootstrapped from the bundled Defaults/ copy on first launch).
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
    // BulkObservableCollection so a full (re)load raises one Reset instead of
    // Clear + N Add — ConditionTracker rebuilds its index once per set switch,
    // not once per record (O(n²) over ~1100 records at startup). Per-record
    // editor upserts keep their normal per-op notification for synchronous
    // downstream freshness.
    public BulkObservableCollection<MessageRecord> Messages { get; } = new();

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
    //   2. Realm-flavored seed AppPaths.MessagesSeedFile(realm)
    //      (Data/Global/Messages.{stock|paradigm}.seed.json) — the realm is
    //      resolved from the set's Info.json Legit (GameDataRealm.Resolve), since
    //      a paradigm realm carries message records a stock realm doesn't, and
    //      vice-versa. Bootstrapped from the bundled Defaults/ copies on first
    //      launch via AppPaths.EnsureGlobalSeedsBootstrapped.
    // The seed itself is never written.
    public void Load(string? setName)
    {
        ActiveSet = setName;
        if (string.IsNullOrWhiteSpace(setName)) { Messages.ReplaceAll([]); return; }

        string realm = GameDataRealm.Resolve(setName);
        List<MessageRecord> loaded =
            TryLoad(AppPaths.MessagesFile(setName)) ??
            TryLoad(AppPaths.MessagesSeedFile(realm)) ??
            [];
        Messages.ReplaceAll(loaded);
    }

    // Read a JSON list from path. Returns the parsed list (possibly empty) iff
    // the file existed AND parsed cleanly; null for missing/corrupt so Load
    // falls through to the next source. Gathered fully before ReplaceAll so a
    // corrupt file never leaves a partial catalogue.
    private List<MessageRecord>? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonStore.Load<List<MessageRecord>>(path);
        }
        catch (Exception ex)
        {
            // Corrupt file ⇒ leave empty, but DO surface — a silent
            // swallow is what hid the missing JsonStringEnumConverter
            // for the entire seed-loading work. Log loud so future
            // schema drift fails visibly.
            _log?.Log(LogSeverity.Warn, "Messages",
                $"Failed to load '{path}': {ex.Message}");
            return null;
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
        Messages.ReplaceAll(records);
        Save();
    }
}
