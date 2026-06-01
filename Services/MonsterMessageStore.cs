using System.Collections.ObjectModel;
using System.IO;
using FujinTerm.Models.GameData;

namespace FujinTerm.Services;

/// <summary>
/// In-memory cache of the Monster Messages catalogue for the active
/// game-data set. Parallels <see cref="MessageStore"/> for monsters:
/// one <see cref="MonsterMessageRecord"/> per Monsters-table row,
/// carrying the parser patterns for every line the monster can
/// produce in combat (hit / death / armor-block / dodge / miss +
/// flavor prefixes).
/// </summary>
/// <remarks>
/// Wiring: <see cref="AppServices"/> subscribes the store to
/// <see cref="GameDataCache.ActiveSetChanged"/> — on every set switch
/// the per-set file is reloaded (missing file ⇒ falls back to the
/// universal seed; missing seed ⇒ empty catalogue). The Monsters tab
/// edit dialog binds individual records via the standard load-edit-save
/// flow shared with the spell-message editor.
/// </remarks>
public sealed class MonsterMessageStore
{
    private readonly LogService? _log;

    /// <summary>Live mirror of the active set's monster-message records.</summary>
    public ObservableCollection<MonsterMessageRecord> Messages { get; } = new();

    /// <summary>Set name currently sourcing <see cref="Messages"/>, or <c>null</c> when none is active.</summary>
    public string? ActiveSet { get; private set; }

    public MonsterMessageStore() { }

    public MonsterMessageStore(LogService log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>
    /// Switch the catalogue to <paramref name="setName"/>'s on-disk
    /// file. Pass <c>null</c> to clear (no set active). Load priority:
    /// <list type="number">
    ///   <item>Per-set file <see cref="AppPaths.MonsterMessagesFile"/>
    ///     — the canonical persisted state once a user has saved.</item>
    ///   <item>Universal seed <see cref="AppPaths.DefaultMonsterMessagesSeedFile"/>
    ///     — applies on first launch; the monster Number ↔ message
    ///     mapping is universal for 1.11p, usable as a starting point
    ///     for other realms (the editor lets the user fix mismatches).</item>
    /// </list>
    /// The seed itself is never written.
    /// </summary>
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

    /// <summary>Persist <see cref="Messages"/> to <see cref="ActiveSet"/>'s file.</summary>
    public void Save()
    {
        if (string.IsNullOrWhiteSpace(ActiveSet)) return;
        JsonStore.Save(AppPaths.MonsterMessagesFile(ActiveSet), Messages);
    }

    /// <summary>Replace the catalogue with <paramref name="records"/> and persist.</summary>
    public void Replace(IEnumerable<MonsterMessageRecord> records)
    {
        Messages.Clear();
        foreach (MonsterMessageRecord m in records) Messages.Add(m);
        Save();
    }

    /// <summary>Find the record anchored to <paramref name="monsterNumber"/>, or <c>null</c>.</summary>
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

    /// <summary>
    /// Upsert <paramref name="record"/>: replace the existing record
    /// with the same Id if present, else append. Persists after.
    /// </summary>
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

    /// <summary>
    /// Replace the record at the original Id with <paramref name="updated"/>.
    /// Used by the editor when content edits flip the projected Id —
    /// the originalId reference still points at the slot to swap.
    /// Falls back to upsert when no slot matches.
    /// </summary>
    public void Replace(string originalId, MonsterMessageRecord updated)
    {
        for (int i = 0; i < Messages.Count; i++)
        {
            if (Messages[i].Id == originalId) { Messages[i] = updated; Save(); return; }
        }
        Upsert(updated);
    }
}
