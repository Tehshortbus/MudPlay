using System.Collections.ObjectModel;
using System.IO;
using FujinTerm.Models.GameData;

namespace FujinTerm.Services;

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
    public ObservableCollection<MonsterMessageRecord> Messages { get; } = new();

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
    // The seed itself is never written.
    public void Load(string? setName)
    {
        Messages.Clear();
        ActiveSet = setName;
        if (string.IsNullOrWhiteSpace(setName)) return;

        if (TryLoadInto(AppPaths.MonsterMessagesFile(setName))) return;
        TryLoadInto(AppPaths.DefaultMonsterMessagesSeedFile);
    }

    private bool TryLoadInto(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            List<MonsterMessageRecord>? loaded = JsonStore.Load<List<MonsterMessageRecord>>(path);
            if (loaded is null) return false;
            foreach (MonsterMessageRecord m in loaded) Messages.Add(m);
            return true;
        }
        catch (Exception ex)
        {
            _log?.Log(LogSeverity.Warn, "MonsterMessages",
                $"Failed to load '{path}': {ex.Message}");
            return false;
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
        Messages.Clear();
        foreach (MonsterMessageRecord m in records) Messages.Add(m);
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
