using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game;
using MudPlay.Models.Profile;

namespace MudPlay.Services;

// Loads and resolves the boss catalog for the active realm. Two layers, keyed by
// boss Name (one boss per Name; usually the game-data spelling, see BossDef.GameName):
//   1. the realm's overlay {realm}/bosses.json — added / removed bosses, edited
//      rooms, StopBefore + respawn-hours overrides;
//   2. the universal read-only seed BossDefs.seed.json (curated from the boss-timer
//      sheet).
// Timer VALUES are not stored — resolved from game data (BossCatalog.ResolveRegenHours)
// so they track the loaded set's version. Mirrors QuestStore: the overlay is a delta
// (entries equal to the seed are dropped) and reloads on OnRealmChanged. The boss
// list is the realm's: every character on it shares one, and two realms on the same
// game data keep their own (it used to live with the game-data set, which they
// shared).
//
// Clients on the same realm share the file, so it is re-read when another one has
// written it, on a poll and before a save.
//
// Stop before and Grab All are the exception: each character's own (user,
// 2026-10-08), kept in its profile and laid over the list as it is resolved
// (SetCharacterFlags). The realm file keeps the values it held from when it carried
// them, untouched, so a character loading for the first time starts from those.
public sealed class BossStore
{
    private readonly LogService? _log;
    private readonly string _seedPath;
    private readonly List<BossDef> _seed = new();
    private readonly Dictionary<string, BossDef> _seedByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BossDef> _overlay = new(StringComparer.OrdinalIgnoreCase);

    // Realm folder whose overlay is loaded, or null when none.
    public string? ActiveRealmFolder { get; private set; }

    // Write time of the overlay file as this client last read or wrote it.
    private DateTime _seenWrite = DateTime.MinValue;

    // Fires when the resolved boss list changes — a Save (add / edit / remove /
    // StopBefore toggle), a realm swap, or new game data under the same list. Consumers that derive state from the
    // boss list (the nav-map boss-room markers) re-read Resolve*/on this. Mirrors
    // BossTimerStore.Changed; the def store lacked one, so map markers had no way to
    // refresh on an edit.
    public event Action? Changed;

    // seedPath defaults to the bootstrapped Global copy.
    public BossStore(LogService? log = null, string? seedPath = null)
    {
        _log = log;
        _seedPath = seedPath ?? AppPaths.DefaultBossDefsSeedFile;
        List<BossDef>? seed = JsonStore.Load<List<BossDef>>(_seedPath);
        if (seed is not null)
            foreach (BossDef b in seed) { _seed.Add(b); _seedByName[b.Name] = b; }
    }

    // ----- Stop before / Grab All: the character's own -------------------------
    private Func<bool>? _hasCharacter;
    private Func<Dictionary<string, BossFlagChoice>?>? _readFlags;
    private Action<Dictionary<string, BossFlagChoice>>? _writeFlags;

    // hasCharacter: a profile is loaded. read: its stored choices, null when it has
    // never had any. write: replace them and save the profile. Until this is called
    // the two flags are read from the list itself, as they were.
    public void SetCharacterFlags(Func<bool> hasCharacter,
        Func<Dictionary<string, BossFlagChoice>?> read, Action<Dictionary<string, BossFlagChoice>> write)
    {
        ArgumentNullException.ThrowIfNull(hasCharacter);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);
        _hasCharacter = hasCharacter;
        _readFlags = read;
        _writeFlags = write;
        AdoptRealmFlags();
        _current = null;
    }

    // A character with no choices of its own yet takes the ones the realm's list
    // holds, once. An empty set is still "has its own", so this never runs twice.
    private void AdoptRealmFlags()
    {
        if (_writeFlags is null || _hasCharacter?.Invoke() != true || _readFlags?.Invoke() is not null) return;
        if (ActiveRealmFolder is null) return;
        Dictionary<string, BossFlagChoice> adopted = FlagChoices(ResolveStored());
        _writeFlags(adopted);
        _log?.Info("Bosses",
            $"Stop before / Grab All are now this character's own: took {adopted.Count} choice(s) from the realm's list");
    }

    // The choices in a list that differ from each boss's own default.
    private static Dictionary<string, BossFlagChoice> FlagChoices(IEnumerable<BossDef> list)
    {
        var choices = new Dictionary<string, BossFlagChoice>(StringComparer.OrdinalIgnoreCase);
        foreach (BossDef b in list)
        {
            bool? stop = b.StopBefore != b.ResetStopBefore ? b.StopBefore : null;
            bool? grab = b.GrabAll != b.ResetGrabAll ? b.GrabAll : null;
            if (stop is not null || grab is not null)
                choices[b.Name] = new BossFlagChoice { StopBefore = stop, GrabAll = grab };
        }
        return choices;
    }

    // Load realmFolder's boss list. legacySet names the game-data set the realm
    // runs on: a realm with no list of its own yet takes a copy of the one that set
    // carried, from when the list was kept per set.
    public void OnRealmChanged(string? realmFolder, string? legacySet = null)
    {
        ActiveRealmFolder = string.IsNullOrWhiteSpace(realmFolder) ? null : realmFolder;
        if (ActiveRealmFolder is not null) AdoptLegacyList(ActiveRealmFolder, legacySet);
        Load();
        AdoptRealmFlags();
        Changed?.Invoke();   // fire even when cleared to null, so derived markers clear
    }

    // New game data under the same boss list: what consumers derive from both (the
    // realm's bosses, their timers) has to be worked out again.
    public void NoteGameDataChanged()
    {
        _current = null;
        Changed?.Invoke();
    }

    // Re-read the list when another client on the realm has saved it. Called on the
    // heartbeat and before a save. True when anything was re-read.
    public bool TakeInOutsideChanges()
    {
        if (ActiveRealmFolder is null) return false;
        if (WriteTime(AppPaths.RealmBossesFile(ActiveRealmFolder)) == _seenWrite) return false;
        try { Load(); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                       or System.Text.Json.JsonException)
        {
            // Caught mid-write by the other client; the next poll reads it whole.
            _log?.Debug("Bosses", $"boss list re-read failed, will retry: {ex.Message}");
            return false;
        }
        _log?.Debug("Bosses", "boss list re-read: another client on this realm changed it");
        Changed?.Invoke();
        return true;
    }

    private void Load()
    {
        _overlay.Clear();
        _current = null;
        _seenWrite = DateTime.MinValue;
        if (ActiveRealmFolder is null) return;
        string path = AppPaths.RealmBossesFile(ActiveRealmFolder);
        _seenWrite = WriteTime(path);
        if (JsonStore.Load<List<BossDef>>(path) is { } ov)
            foreach (BossDef b in ov) _overlay[b.Name] = b;
    }

    private void AdoptLegacyList(string realmFolder, string? legacySet)
    {
        if (string.IsNullOrWhiteSpace(legacySet)) return;
        string realmFile = AppPaths.RealmBossesFile(realmFolder);
        string legacy = AppPaths.LegacySetBossesFile(legacySet);
        if (System.IO.File.Exists(realmFile) || !System.IO.File.Exists(legacy)) return;
        try
        {
            System.IO.Directory.CreateDirectory(realmFolder);
            System.IO.File.Copy(legacy, realmFile);
            _log?.Info("Bosses", $"boss list copied to the realm from game-data set '{legacySet}'");
        }
        catch (System.IO.IOException)
        {
            // Another client on the realm copied it first — the list is where it belongs.
        }
    }

    private static DateTime WriteTime(string path) =>
        System.IO.File.Exists(path) ? System.IO.File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

    // The merged boss list. Overlay wins per Name; a Removed overlay entry hides the
    // seed boss; an overlay entry with no seed match is user-added. Returns clones so
    // the UI can edit rows without mutating stored instances.
    // The resolved list for callers that only read it (the boss timers, on every
    // room observation): built once per change of the store
    // instead of cloning every boss on each call. Never modify what it returns —
    // Resolve hands out copies for that.
    public IReadOnlyList<BossDef> Current => _current ??= Resolve();
    private IReadOnlyList<BossDef>? _current;

    public IReadOnlyList<BossDef> Resolve()
    {
        List<BossDef> list = ResolveStored();
        if (_readFlags is null) return list;

        // Stop before and Grab All come from the loaded character: its choice where
        // it made one, the boss's own default where it didn't.
        Dictionary<string, BossFlagChoice>? choices = _readFlags();
        foreach (BossDef b in list)
        {
            BossFlagChoice? choice = null;
            choices?.TryGetValue(b.Name, out choice);
            b.StopBefore = choice?.StopBefore ?? b.ResetStopBefore;
            b.GrabAll = choice?.GrabAll ?? b.ResetGrabAll;
        }
        return list;
    }

    // The list as the seed and the realm's file hold it, the two flags included.
    private List<BossDef> ResolveStored()
    {
        var byName = new Dictionary<string, BossDef>(StringComparer.OrdinalIgnoreCase);
        foreach (BossDef s in _seed) byName[s.Name] = s;
        foreach ((string name, BossDef ov) in _overlay)
        {
            if (ov.Removed) byName.Remove(name);
            else byName[name] = ov;
        }
        return byName.Values.Select(WithSeedResetDefaults).ToList();
    }

    // An overlay entry is a whole copy of the boss as it stood when the user edited
    // it, and some of that copy was never theirs to set:
    //   - which monster record the boss is and what the game calls it. Nothing in the
    //     client edits these, so the seed's are the current ones; a copy made before
    //     the seed was corrected would keep reading the wrong record's timer.
    //   - the reset defaults, when the copy predates them: a boss the user only
    //     re-roomed still resets the way it ships.
    private BossDef WithSeedResetDefaults(BossDef def)
    {
        BossDef clone = def.Clone();
        if (_seedByName.TryGetValue(def.Name, out BossDef? seed))
        {
            clone.MonsterNumber = seed.MonsterNumber ?? clone.MonsterNumber;
            if (!string.IsNullOrWhiteSpace(seed.GameName)) clone.GameName = seed.GameName;
            clone.DefaultStopBefore ??= seed.DefaultStopBefore;
            clone.DefaultGrabAll ??= seed.DefaultGrabAll;
        }
        return clone;
    }

    // Bosses visible on the given realm (Stock hides Paradigm-only entries).
    public IReadOnlyList<BossDef> ResolveForRealm(RealmType realm)
        => Resolve().Where(b => realm == RealmType.ParaMud ? b.InParadigm : b.InStock).ToList();

    // Find a boss ANYWHERE in the catalog — the user's overlay additions AND the seed,
    // INCLUDING a seed boss the user removed from their list (which Resolve() hides) —
    // by MDB number first, else by name. Used to adopt a synced timer for a boss the
    // user isn't currently tracking: the real def (name + respawn config) is recovered
    // so the boss can be un-hidden rather than the timer orphaned. Returns a clone, or
    // null when no catalog entry matches.
    public BossDef? FindInCatalog(int? monsterNumber, string? name)
    {
        if (monsterNumber is { } num)
        {
            BossDef? byNum = _overlay.Values.FirstOrDefault(b => !b.Removed && b.MonsterNumber == num)
                             ?? _seed.FirstOrDefault(b => b.MonsterNumber == num);
            if (byNum is not null) return byNum.Clone();
        }
        if (!string.IsNullOrEmpty(name))
        {
            if (_overlay.TryGetValue(name, out BossDef? ov) && !ov.Removed) return ov.Clone();
            if (_seedByName.TryGetValue(name, out BossDef? seed)) return seed.Clone();
        }
        return null;
    }

    // Persist the user's boss list to the realm's overlay as a DELTA: a boss
    // matching the seed is dropped (so a later seed update still flows through); an
    // edited / added boss is written; a seed boss the user deleted is written as a
    // Removed tombstone. No-op when no realm is active. The caller hands over the
    // whole list as its window showed it, so this replaces whatever is on file.
    public void Save(IEnumerable<BossDef> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (ActiveRealmFolder is null)
        {
            _log?.Warn("Bosses", "boss list not saved: no realm is active (load a character on a BBS first)");
            return;
        }

        // The two flags go to the character. The realm's file keeps whatever it
        // held for them before, so a boss ticked here doesn't change for the other
        // characters on the realm, and one that hasn't loaded yet still finds the
        // realm's old choices to start from.
        List<BossDef> rows = current.ToList();
        bool flagsAreTheCharacters = _writeFlags is not null && _hasCharacter?.Invoke() == true;
        Dictionary<string, BossDef>? stored = null;
        if (flagsAreTheCharacters)
        {
            stored = ResolveStored().ToDictionary(b => b.Name, StringComparer.OrdinalIgnoreCase);
            _writeFlags!(FlagChoices(rows));
        }

        var overlay = new List<BossDef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BossDef row in rows)
        {
            if (!seen.Add(row.Name)) continue;   // dedupe by name
            BossDef b = row;
            if (stored is not null)
            {
                b = row.Clone();
                stored.TryGetValue(row.Name, out BossDef? was);
                b.StopBefore = was?.StopBefore ?? true;
                b.GrabAll = was?.GrabAll ?? false;
            }
            if (_seedByName.TryGetValue(b.Name, out BossDef? seed) && b.MatchesSeed(seed)) continue;
            overlay.Add(b.Clone());
        }
        foreach (BossDef s in _seed)
            if (!seen.Contains(s.Name))
                overlay.Add(new BossDef { Name = s.Name, Removed = true });

        _overlay.Clear();
        foreach (BossDef b in overlay) _overlay[b.Name] = b;
        string path = AppPaths.RealmBossesFile(ActiveRealmFolder);
        JsonStore.Save(path, overlay);
        _seenWrite = WriteTime(path);
        _log?.Debug("Bosses", $"saved {overlay.Count} overlay delta(s) for realm {ActiveRealmFolder}");
        _current = null;
        Changed?.Invoke();
    }
}
