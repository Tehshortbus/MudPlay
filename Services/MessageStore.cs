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
    //   3. Bundled seed AppPaths.BundledMessagesSeedFile(realm) shipped beside the
    //      app — the read-only floor. Reached only when the Global copy is missing
    //      (never bootstrapped, or deleted), so the catalogue is never empty for a
    //      realm we ship. Consulted last, it never overrides a user's per-set edits.
    // Neither seed is ever written.
    public void Load(string? setName)
    {
        ActiveSet = setName;
        if (string.IsNullOrWhiteSpace(setName))
        {
            Messages.ReplaceAll([]);
            _log?.Log(LogSeverity.Info, "Messages", "no active game-data set — message catalogue cleared.");
            return;
        }

        string realm = GameDataRealm.Resolve(setName);
        (List<MessageRecord> loaded, string source) = LoadFrom(setName, realm);
        Messages.ReplaceAll(loaded);

        if (loaded.Count == 0)
            _log?.Log(LogSeverity.Warn, "Messages",
                $"set '{setName}' (realm '{realm}'): 0 message records — no per-set file, Global seed, or bundled " +
                "seed was found or parsed, so the Messages tab will be empty and no lines are recognized.");
        else
            _log?.Log(LogSeverity.Info, "Messages",
                $"set '{setName}' (realm '{realm}'): loaded {loaded.Count} message records from {source}.");
    }

    // First readable source wins: the per-set file (persisted user edits) → the Global
    // realm seed (bootstrapped on first launch) → the bundled realm seed shipped beside the
    // app. The bundled copy is the floor — a genuinely-missing Global seed (never
    // bootstrapped, or deleted) still yields the shipped catalogue instead of an empty one,
    // and because it is consulted last it can never override a user's per-set edits.
    private (List<MessageRecord> Records, string Source) LoadFrom(string setName, string realm)
    {
        if (TryLoad(AppPaths.MessagesFile(setName)) is { } perSet)
            return (perSet, "per-set file");
        if (TryLoad(AppPaths.MessagesSeedFile(realm)) is { } globalSeed)
            return (globalSeed, "Global seed");
        if (TryLoad(AppPaths.BundledMessagesSeedFile(realm)) is { } bundled)
            return (bundled, "bundled seed");
        return ([], "none");
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
