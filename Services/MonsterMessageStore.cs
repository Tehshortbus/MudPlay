using System.IO;
using MudPlay.Models.GameData;

namespace MudPlay.Services;

// In-memory cache of the Monster Messages catalogue for the active game-data
// set: one MonsterMessageRecord per Monsters-table row, the canonical name the
// room classifier resolves a display name back to.
//
// The catalogue is the universal seed with the user's additions (placeholder names
// for unknown monsters) layered on top from the per-set file, which holds only the
// DELTA against the seed (see SeedDelta) so a shipped seed update still reaches
// every record the user hasn't changed.
//
// Wiring: AppServices subscribes the store to
// GameDataCache.ActiveSetChanged — on every set switch the catalogue is rebuilt
// from the seed plus the per-set file (missing seed ⇒ just the per-set records).
public sealed class MonsterMessageStore
{
    private readonly LogService? _log;

    // The Name is the record's identity and always comes from the seed; Links are what
    // a user can change on a seed record without re-Id-ing it.
    internal static readonly SeedDelta<MonsterMessageRecord> Delta = new(
        id:            r => r.Id,
        name:          r => r.Name,
        links:         r => r.Links,
        withoutLinks:  r => r with { Links = null },
        applyOverride: (seed, user) => seed with { Links = user.Links });

    // The seed the live catalogue was built over — Save diffs against it.
    private List<MonsterMessageRecord> _seed = [];

    // Live mirror of the active set's monster-message records.
    // BulkObservableCollection so a full (re)load raises one Reset instead of
    // Clear + N Add, matching MessageStore. Per-record editor upserts keep their
    // normal per-op notification.
    public BulkObservableCollection<MonsterMessageRecord> Messages { get; } = new();

    // Set name currently sourcing Messages, or null when none is active.
    public string? ActiveSet { get; private set; }

    public MonsterMessageStore() { }

    public MonsterMessageStore(LogService log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    // Switch the catalogue to setName's data. Pass null to clear (no set active).
    // The seed comes first-readable-wins from:
    //   1. the universal seed AppPaths.DefaultMonsterMessagesSeedFile in Global/ — the
    //      monster Number ↔ name mapping is universal for 1.11p, usable as a starting
    //      point for other realms;
    //   2. the bundled seed AppPaths.BundledMonsterMessagesSeedFile — the read-only
    //      floor, reached only when the Global copy is missing, so the catalogue is
    //      never empty.
    // The user's delta file AppPaths.MonsterMessagesFile is then layered over it.
    // Neither seed is ever written.
    public void Load(string? setName)
    {
        ActiveSet = setName;
        if (string.IsNullOrWhiteSpace(setName))
        {
            _seed = [];
            Messages.ReplaceAll([]);
            _log?.Log(LogSeverity.Info, "MonsterMessages", "no active game-data set — monster-message catalogue cleared.");
            return;
        }

        (List<MonsterMessageRecord> seed, string seedSource) = LoadSeed();
        _seed = seed;
        List<MonsterMessageRecord>? withEdits =
            Delta.Load(AppPaths.MonsterMessagesFile(setName), seed, _log, "MonsterMessages");
        List<MonsterMessageRecord> loaded = withEdits ?? seed;
        string source = withEdits is null ? seedSource : $"the {seedSource} plus the per-set edits file";
        Messages.ReplaceAll(loaded);

        if (loaded.Count == 0)
            _log?.Log(LogSeverity.Warn, "MonsterMessages",
                $"set '{setName}': 0 monster-message records — no Global seed, bundled seed, or per-set file was " +
                "found or parsed, so monster combat lines are not recognized.");
        else
            _log?.Log(LogSeverity.Info, "MonsterMessages",
                $"set '{setName}': loaded {loaded.Count} monster-message records from {source}.");
    }

    // Global seed (re-synced from the embedded copy every launch) → bundled floor.
    private (List<MonsterMessageRecord> Records, string Source) LoadSeed()
    {
        if (TryLoad(AppPaths.DefaultMonsterMessagesSeedFile) is { } globalSeed)
            return (globalSeed, "Global seed");
        if (TryLoad(AppPaths.BundledMonsterMessagesSeedFile) is { } bundled)
            return (bundled, "bundled seed");
        return ([], "no seed");
    }

    // Parsed list (possibly empty) iff the file existed AND parsed cleanly;
    // null for missing/corrupt so Load falls through to the next source.
    private List<MonsterMessageRecord>? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonStore.Load<List<MonsterMessageRecord>>(path);
        }
        catch (Exception ex)
        {
            _log?.Log(LogSeverity.Warn, "MonsterMessages",
                $"Failed to load '{path}': {ex.Message}");
            return null;
        }
    }

    // Persist the user's edits (the catalogue's delta against the seed it was loaded
    // over) to ActiveSet's file.
    public void Save()
    {
        if (string.IsNullOrWhiteSpace(ActiveSet)) return;
        Delta.Save(AppPaths.MonsterMessagesFile(ActiveSet), Messages, _seed, _log, "MonsterMessages");
    }

    // Upsert record: replace the existing record with the same Id if present,
    // else append. Persists after.
    public void Upsert(MonsterMessageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        for (int i = 0; i < Messages.Count; i++)
        {
            if (Messages[i].Id == record.Id) { Messages[i] = record; Save(); return; }
        }
        Messages.Add(record);
        Save();
    }
}
