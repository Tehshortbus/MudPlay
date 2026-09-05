using System.IO;
using MudPlay.Models.GameData;

namespace MudPlay.Services;

// In-memory cache of the Monster Messages catalogue for the active game-data
// set. Parallels MessageStore for monsters: one MonsterMessageRecord per
// Monsters-table row, carrying the parser patterns for every line the monster
// can produce in combat (hit / death / armor-block / dodge / miss + flavor
// prefixes).
//
// Wiring: AppServices subscribes the store to
// GameDataCache.ActiveSetChanged — on every set switch the per-set file is
// reloaded (missing file ⇒ falls back to the universal seed; missing seed ⇒
// empty catalogue). The Monsters tab edit dialog binds individual records via
// the standard load-edit-save flow shared with the spell-message editor.
public sealed class MonsterMessageStore
{
    private readonly LogService? _log;

    // Live mirror of the active set's monster-message records.
    // BulkObservableCollection so a full (re)load raises one Reset instead of
    // Clear + N Add — MonsterDeathWatcher rebuilds its death-index once per set
    // switch, not once per record (O(n²) over ~1100 records at startup).
    // Per-record editor upserts keep their normal per-op notification.
    public BulkObservableCollection<MonsterMessageRecord> Messages { get; } = new();

    // Set name currently sourcing Messages, or null when none is active.
    public string? ActiveSet { get; private set; }

    public MonsterMessageStore() { }

    public MonsterMessageStore(LogService log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    // Switch the catalogue to setName's on-disk file. Pass null to clear (no
    // set active). Load priority:
    //   1. Per-set file AppPaths.MonsterMessagesFile — the canonical
    //      persisted state once a user has saved.
    //   2. Universal seed AppPaths.DefaultMonsterMessagesSeedFile — applies on
    //      first launch; the monster Number ↔ message mapping is universal for
    //      1.11p, usable as a starting point for other realms (the editor lets
    //      the user fix mismatches).
    //   3. Bundled seed AppPaths.BundledMonsterMessagesSeedFile shipped beside the
    //      app — the read-only floor. Reached only when the Global copy is missing
    //      (never bootstrapped, or deleted), so the catalogue is never empty; last,
    //      so it never overrides a user's per-set edits.
    // Neither seed is ever written.
    public void Load(string? setName)
    {
        ActiveSet = setName;
        if (string.IsNullOrWhiteSpace(setName))
        {
            Messages.ReplaceAll([]);
            _log?.Log(LogSeverity.Info, "MonsterMessages", "no active game-data set — monster-message catalogue cleared.");
            return;
        }

        (List<MonsterMessageRecord> loaded, string source) = LoadFrom(setName);
        Messages.ReplaceAll(loaded);

        if (loaded.Count == 0)
            _log?.Log(LogSeverity.Warn, "MonsterMessages",
                $"set '{setName}': 0 monster-message records — no per-set file, Global seed, or bundled seed was " +
                "found or parsed, so monster combat lines are not recognized.");
        else
            _log?.Log(LogSeverity.Info, "MonsterMessages",
                $"set '{setName}': loaded {loaded.Count} monster-message records from {source}.");
    }

    // First readable source wins (per-set edits → Global seed → bundled floor); the
    // bundled copy is last so it never overrides a user's edits, and keeps the catalogue
    // non-empty even when the Global seed was never bootstrapped or was deleted.
    private (List<MonsterMessageRecord> Records, string Source) LoadFrom(string setName)
    {
        if (TryLoad(AppPaths.MonsterMessagesFile(setName)) is { } perSet)
            return (perSet, "per-set file");
        if (TryLoad(AppPaths.DefaultMonsterMessagesSeedFile) is { } globalSeed)
            return (globalSeed, "Global seed");
        if (TryLoad(AppPaths.BundledMonsterMessagesSeedFile) is { } bundled)
            return (bundled, "bundled seed");
        return ([], "none");
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

    // Persist Messages to ActiveSet's file.
    public void Save()
    {
        if (string.IsNullOrWhiteSpace(ActiveSet)) return;
        JsonStore.Save(AppPaths.MonsterMessagesFile(ActiveSet), Messages);
    }

    // Replace the catalogue with records and persist.
    public void Replace(IEnumerable<MonsterMessageRecord> records)
    {
        Messages.ReplaceAll(records);
        Save();
    }

    // Find the record anchored to monsterNumber, or null.
    public MonsterMessageRecord? FindByMonsterNumber(int monsterNumber)
    {
        foreach (MonsterMessageRecord m in Messages)
        {
            if (m.Links is null) continue;
            foreach (GameDataLink l in m.Links)
            {
                if (l.Number == monsterNumber &&
                    string.Equals(l.Table, "Monsters", StringComparison.OrdinalIgnoreCase))
                    return m;
            }
        }
        return null;
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

    // Replace the record at the original Id with updated. Used by the editor
    // when content edits flip the projected Id — the originalId reference
    // still points at the slot to swap. Falls back to upsert when no slot
    // matches.
    public void Replace(string originalId, MonsterMessageRecord updated)
    {
        for (int i = 0; i < Messages.Count; i++)
        {
            if (Messages[i].Id == originalId) { Messages[i] = updated; Save(); return; }
        }
        Upsert(updated);
    }
}
