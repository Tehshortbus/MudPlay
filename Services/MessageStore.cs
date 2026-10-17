using System.IO;
using MudPlay.Models.GameData;

namespace MudPlay.Services;

// In-memory cache of the Messages/Responses catalogue for the active set.
// The catalogue is the realm-flavored seed at Global/Messages.{stock|paradigm}.seed.json
// (the realm is picked from the set's Info.json Legit; re-synced from the seed embedded
// in the exe on every launch) with the user's edits layered on top from
// game data/{set}/messages.json. That per-set file holds only the DELTA against the
// seed (see SeedDelta), so a shipped seed fix still reaches every record the user
// hasn't changed.
//
// Wiring: AppServices subscribes the store to
// GameDataCache.ActiveSetChanged — on every set switch the catalogue is rebuilt
// from the seed plus AppPaths.MessagesFile. The Game Data Browser → Messages tab
// binds the live Messages collection.
public sealed class MessageStore
{
    private readonly LogService? _log;

    // Text (Name + the five lines) is the record's identity and always comes from the
    // seed; Flags, Links, the confuse-fumble line and the cast response are what a user
    // can change on a seed record without re-Id-ing it.
    internal static readonly SeedDelta<MessageRecord> Delta = new(
        id:            r => r.Id,
        name:          r => r.Name,
        links:         r => r.Links,
        withoutLinks:  r => r with { Links = null },
        applyOverride: (seed, user) => seed with
        {
            Flags             = user.Flags,
            RawFlagsHex       = user.RawFlagsHex,
            Links             = user.Links,
            ConfuseFumbleLine = user.ConfuseFumbleLine,
            CastResponse      = user.CastResponse,
        });

    // The seed the live catalogue was built over — Save diffs against it.
    private List<MessageRecord> _seed = [];

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

    // Switch the catalogue to setName's data. Pass null to clear (no set active).
    // The seed comes first-readable-wins from:
    //   1. the realm seed AppPaths.MessagesSeedFile(realm) in Global/ — the realm is
    //      resolved from the set's Info.json Legit (GameDataRealm.Resolve), since a
    //      paradigm realm carries message records a stock realm doesn't, and vice-versa;
    //   2. the bundled seed AppPaths.BundledMessagesSeedFile(realm) — the read-only floor,
    //      reached only when the Global copy is missing, so the catalogue is never empty
    //      for a realm we ship.
    // The user's delta file AppPaths.MessagesFile is then layered over it. Neither seed
    // is ever written.
    public void Load(string? setName)
    {
        ActiveSet = setName;
        if (string.IsNullOrWhiteSpace(setName))
        {
            _seed = [];
            Messages.ReplaceAll([]);
            _log?.Log(LogSeverity.Info, "Messages", "no active game-data set — message catalogue cleared.");
            return;
        }

        string realm = GameDataRealm.Resolve(setName);
        (List<MessageRecord> seed, string seedSource) = LoadSeed(realm);
        _seed = seed;
        List<MessageRecord>? withEdits = Delta.Load(AppPaths.MessagesFile(setName), seed, _log, "Messages");
        List<MessageRecord> loaded = withEdits ?? seed;
        string source = withEdits is null ? seedSource : $"the {seedSource} plus the per-set edits file";
        Messages.ReplaceAll(loaded);

        if (loaded.Count == 0)
            _log?.Log(LogSeverity.Warn, "Messages",
                $"set '{setName}' (realm '{realm}'): 0 message records — no Global seed, bundled seed, or per-set " +
                "file was found or parsed, so the Messages tab will be empty and no lines are recognized.");
        else
            _log?.Log(LogSeverity.Info, "Messages",
                $"set '{setName}' (realm '{realm}'): loaded {loaded.Count} message records from {source}.");
    }

    // Global realm seed (re-synced from the embedded copy every launch) → bundled floor.
    private (List<MessageRecord> Records, string Source) LoadSeed(string realm)
    {
        if (TryLoad(AppPaths.MessagesSeedFile(realm)) is { } globalSeed)
            return (globalSeed, "Global seed");
        if (TryLoad(AppPaths.BundledMessagesSeedFile(realm)) is { } bundled)
            return (bundled, "bundled seed");
        return ([], "no seed");
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

    // Persist the user's edits (the catalogue's delta against the seed it was loaded
    // over) to ActiveSet's file.
    public void Save()
    {
        if (string.IsNullOrWhiteSpace(ActiveSet)) return;
        Delta.Save(AppPaths.MessagesFile(ActiveSet), Messages, _seed, _log, "Messages");
    }

    // How the live catalogue departs from the seed it was built over.
    public List<SeedDelta<MessageRecord>.Difference> SeedDifferences() => Delta.Compare(Messages, _seed);

    // Put each of useSeed back to the seed (see SeedDelta.Revert) and persist — the saved
    // delta shrinks by those messages, so they follow the seed again. Returns how many
    // applied; an entry the catalogue has moved on from since it was listed is skipped.
    public int RevertToSeed(IEnumerable<SeedDelta<MessageRecord>.Difference> useSeed)
    {
        SeedDelta<MessageRecord>.RevertResult result = Delta.Revert(Messages, _seed, useSeed);
        if (result.Reverted == 0) return 0;
        Messages.ReplaceAll(result.Records);
        Save();
        return result.Reverted;
    }

}
