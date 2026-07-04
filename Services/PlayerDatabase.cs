using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Models.Settings;

namespace FujinTerm.Services;

// Two-layer store of player records — the BBS-tier observation list (one row
// per player ever seen on the active BBS) merged with the loaded character's
// customization dictionary (per-player remote-command permissions, auto-party
// toggles, etc.). Both layers persist to disk: observations to
// Data/BBS/{name}/players.json; customizations to the loaded profile's
// CharacterProfile.PlayerCustomizations dictionary, pruned at save time so
// only non-default entries hit disk.
//
// Players is the merged view bound by the Game Data Browser → Players tab.
// Observation writes (RecordObservation from the who-list + look-on-player
// parsers) update the BBS layer and schedule a disk save; customization writes
// (EditCustomization from the player edit dialog) update the Character layer
// and schedule a profile save. Either path rebuilds Players.
//
// On BBS swap the observation layer reloads from disk; on profile swap the
// customization layer reloads. PurgeStale drops observations only —
// customizations stay attached to the profile, harmless when no record exists
// for them at the moment (a later observation re-binds them automatically).
public sealed class PlayerDatabase
{
    private readonly ProfileService? _profile;
    private readonly Func<BbsProfile?>? _activeBbsProvider;

    // ----- Backing layers ------------------------------------------------

    // BBS-tier observations, keyed by GIVEN name (case-insensitive). Given
    // name is the stable identity across train-stats family-name changes —
    // keying on display name would split the same player into two rows the
    // moment they rename.
    private readonly Dictionary<string, PlayerObservation> _observations =
        new(StringComparer.OrdinalIgnoreCase);

    // Char-tier customizations, keyed by GIVEN name (case-insensitive). Same
    // rationale as _observations: the user's per-player toggles must follow
    // the player across renames, not orphan when their family name changes.
    // Empty when no profile loaded.
    private readonly Dictionary<string, PlayerCustomization> _customizations =
        new(StringComparer.OrdinalIgnoreCase);

    // Active BBS the observation layer currently mirrors. Null when no BBS is pinned.
    private string? _activeBbsName;

    // Merged view — observable so views can react to updates.
    public ObservableCollection<PlayerRecord> Players { get; } = new();

    // Raised after a who / manual observation is recorded, carrying the
    // player's GIVEN name. AlignmentTracker uses it to clear the local
    // character's stale-alignment flag once our own row is re-observed by a who.
    public event Action<string>? ObservationRecorded;

    // ----- Construction --------------------------------------------------

    // Parameterless ctor for tests and in-memory-only scenarios — no disk persistence.
    public PlayerDatabase() { }

    // Production ctor. Subscribes to ProfileService's ProfileLoaded /
    // ProfileClosed / ProfileSaving / BbsPinApplied so both layers stay in sync
    // with whichever character + BBS the user is on.
    public PlayerDatabase(ProfileService profile, Func<BbsProfile?> activeBbsProvider)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(activeBbsProvider);
        _profile = profile;
        _activeBbsProvider = activeBbsProvider;

        profile.ProfileLoaded  += _ => OnSwap();
        profile.ProfileClosed  +=      OnSwap;
        profile.BbsPinApplied  += _ => OnSwap();
        profile.ProfileSaving  += SnapshotCustomizationsForSave;
    }

    // ----- Observation writes (BBS tier) --------------------------------

    // Apply one observed row (typically from who output). Wire names are split
    // on the first whitespace; the record is keyed on given name so a player
    // who renames at the train-stats screen (family-name change) updates her
    // existing row instead of creating a duplicate. Existing records merge —
    // nulls don't overwrite — so a sparse observation only updates the fields
    // it has; the observed family name DOES overwrite, because seeing a new
    // family name is itself an observation. Saves the BBS observation file
    // after the merge.
    public void RecordObservation(
        string name,
        string? @class,
        string? race,
        string? alignment,
        string? title,
        string? gang,
        string? role,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(name);
        (string given, string family) = PlayerObservation.SplitName(name);
        if (string.IsNullOrEmpty(given)) return;

        if (_observations.TryGetValue(given, out PlayerObservation? existing))
        {
            _observations[given] = existing with
            {
                // GivenName is the key — never changes for an existing row.
                // FamilyName is always overwritten by the latest observation:
                // a rename at train-stats produces a new family name, and
                // that's a real observation we need to record (not a "null /
                // unseen" event we should preserve the prior value through).
                FamilyName  = family,
                Class       = @class    ?? existing.Class,
                Race        = race      ?? existing.Race,
                Alignment   = alignment ?? existing.Alignment,
                Title       = title     ?? existing.Title,
                Gang        = gang      ?? existing.Gang,
                Role        = role      ?? existing.Role,
                LastSeenUtc = nowUtc,
            };
        }
        else
        {
            _observations[given] = new PlayerObservation(
                GivenName:    given,
                FamilyName:   family,
                Class:        @class,
                Race:         race,
                Alignment:    alignment,
                Title:        title,
                Gang:         gang,
                Role:         role,
                FirstSeenUtc: nowUtc,
                LastSeenUtc:  nowUtc);
        }

        Rebuild();
        SaveObservations();
        ObservationRecorded?.Invoke(given);
    }

    // Apply one "look <player>" observation — race + class extracted from the
    // description sentence, plus the equipment loadout block. Creates a new
    // record if the player is unknown, merges into the existing observation
    // otherwise. Nulls for race / class don't overwrite (caller may have failed
    // to infer either); equipment, when supplied, REPLACES the previous loadout
    // (it's a fresh snapshot, not a delta — empty list means "they were
    // equipped with Nothing"). Saves the BBS observation file after the merge.
    public void RecordLook(
        string name,
        string? race,
        string? @class,
        IReadOnlyList<EquipmentItem>? equipment,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(name);
        (string given, string family) = PlayerObservation.SplitName(name);
        if (string.IsNullOrEmpty(given)) return;

        if (_observations.TryGetValue(given, out PlayerObservation? existing))
        {
            _observations[given] = existing with
            {
                // Family name on a look-observation overwrites for the same
                // reason as in RecordObservation — a new family name is
                // itself a real observation, not "I didn't see it".
                FamilyName  = string.IsNullOrEmpty(family) ? existing.FamilyName : family,
                Race        = race      ?? existing.Race,
                Class       = @class    ?? existing.Class,
                Equipment   = equipment ?? existing.Equipment,
                LastSeenUtc = nowUtc,
            };
        }
        else
        {
            _observations[given] = new PlayerObservation(
                GivenName:    given,
                FamilyName:   family,
                Class:        @class,
                Race:         race,
                Alignment:    null,
                Title:        null,
                Gang:         null,
                Role:         null,
                FirstSeenUtc: nowUtc,
                LastSeenUtc:  nowUtc,
                Equipment:    equipment);
        }

        Rebuild();
        SaveObservations();
    }

    // ----- Reads ---------------------------------------------------------

    // Look up one player's merged record by name (given or full display name —
    // reduced to the given name, the stable key). Returns null when no
    // observation exists for that player. Read-only; never creates a row. Used
    // by PartyLevelTracker to read a party member's known level (exact or
    // title-derived) at path-planning time.
    public PlayerRecord? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        (string given, _) = PlayerObservation.SplitName(name);
        if (string.IsNullOrEmpty(given)) return null;
        if (!_observations.TryGetValue(given, out PlayerObservation? obs)) return null;
        _customizations.TryGetValue(given, out PlayerCustomization c);
        return PlayerRecord.Merge(obs, c);
    }

    // ----- Greet tracking (BBS tier) ------------------------------------

    // When GreetManager last auto-greeted this player (UTC), or null if never.
    // Keyed on given name like every other observation lookup. The manager
    // compares this against the local-calendar day to enforce "greet at most
    // once per day".
    public DateTime? GetLastGreetedUtc(string givenName)
    {
        if (string.IsNullOrWhiteSpace(givenName)) return null;
        (string given, _) = PlayerObservation.SplitName(givenName);
        if (string.IsNullOrEmpty(given)) return null;
        return _observations.TryGetValue(given, out PlayerObservation? o) ? o.LastGreetedUtc : null;
    }

    // Stamp the auto-greet time for one player. Creates a minimal observation
    // row when the player is unknown (we genuinely just saw them in the room),
    // otherwise updates the existing row's LastGreetedUtc in place — every
    // other field is left untouched (greeting isn't a who/look observation, so
    // it must not overwrite class / race / LastSeen). Saves the BBS observation
    // file. Called by GreetManager right after emitting greet / look.
    public void RecordGreeted(string name, DateTime whenUtc)
    {
        ArgumentNullException.ThrowIfNull(name);
        (string given, string family) = PlayerObservation.SplitName(name);
        if (string.IsNullOrEmpty(given)) return;

        if (_observations.TryGetValue(given, out PlayerObservation? existing))
        {
            _observations[given] = existing with { LastGreetedUtc = whenUtc };
        }
        else
        {
            _observations[given] = new PlayerObservation(
                GivenName:      given,
                FamilyName:     family,
                Class:          null,
                Race:           null,
                Alignment:      null,
                Title:          null,
                Gang:           null,
                Role:           null,
                FirstSeenUtc:   whenUtc,
                LastSeenUtc:    whenUtc,
                LastGreetedUtc: whenUtc);
        }

        Rebuild();
        SaveObservations();
    }

    // Record one player's exact character level, as learned from an @level
    // probe reply ("Level N, X exp, …"). This is the authoritative source for
    // a player's level — it supersedes the 5-level band the game's title
    // otherwise implies (see ClassTitleTable). Keyed on given name like every
    // other observation lookup; creates a minimal row when the player is
    // unknown (we only get a level reply from someone we asked, so they're
    // real), otherwise updates the existing row's Level and bumps LastSeenUtc —
    // answering a telepath proves presence. Every other field is left
    // untouched. Saves the BBS observation file.
    public void RecordLevel(string name, int level, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (level <= 0) return;
        (string given, string family) = PlayerObservation.SplitName(name);
        if (string.IsNullOrEmpty(given)) return;

        if (_observations.TryGetValue(given, out PlayerObservation? existing))
        {
            _observations[given] = existing with { Level = level, LastSeenUtc = nowUtc };
        }
        else
        {
            _observations[given] = new PlayerObservation(
                GivenName:    given,
                FamilyName:   family,
                Class:        null,
                Race:         null,
                Alignment:    null,
                Title:        null,
                Gang:         null,
                Role:         null,
                FirstSeenUtc: nowUtc,
                LastSeenUtc:  nowUtc,
                Level:        level);
        }

        Rebuild();
        SaveObservations();
        ObservationRecorded?.Invoke(given);
    }

    // Replace the customization slice for one player. Triggered by the player
    // edit dialog Save path; persists via ProfileService.Save. Defaults aren't
    // stored: a pristine "everything off / no notes" customization removes any
    // existing entry so the profile JSON doesn't bloat with one row per
    // observed stranger.
    public bool EditCustomization(string nameOrGiven, PlayerCustomization customization)
    {
        if (string.IsNullOrWhiteSpace(nameOrGiven)) return false;
        // Accept either the bare given name ("Debbie") or a full display
        // name ("Debbie Par") for backwards-compatible callers. The dict
        // is keyed on given so the customization follows the player
        // across family-name changes.
        (string given, _) = PlayerObservation.SplitName(nameOrGiven);
        if (string.IsNullOrEmpty(given)) return false;

        if (customization.IsDefault)
            _customizations.Remove(given);
        else
            _customizations[given] = customization;

        Rebuild();
        _profile?.Save();
        return true;
    }

    // Manually add a player record from the Game Data Browser → Players tab's
    // Add button. Differs from RecordObservation only in intent — same merge
    // semantics if the given-name already exists (overwrites Family, bumps
    // LastSeen, keeps prior fields the caller didn't supply). Returns true when
    // a brand-new row was created, false when the call merged into an existing
    // row.
    public bool AddManual(string givenName, string familyName, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(givenName);
        string given  = givenName.Trim();
        string family = (familyName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(given)) return false;

        bool isNew = !_observations.ContainsKey(given);
        // Re-use the observation pipeline so the dedup + sparse-merge
        // rules apply consistently. A manual add with no class / race /
        // alignment etc. is treated as a sparse observation — exactly
        // the right semantics: we don't claim to know more than what
        // the user typed.
        string composedName = string.IsNullOrEmpty(family) ? given : $"{given} {family}";
        RecordObservation(composedName, null, null, null, null, null, null, nowUtc);
        return isNew;
    }

    // Remove the observation for one player by given-name. Returns true when a
    // row was removed. The customization layer stays attached to the profile —
    // if the user re-observes this player later (via who or look), the
    // customization automatically re-binds. To reset customizations too, the
    // user uses the edit dialog to clear every flag (which removes the
    // customization entry once PlayerCustomization.IsDefault is true).
    public bool RemoveByGivenName(string givenName)
    {
        if (string.IsNullOrWhiteSpace(givenName)) return false;
        (string given, _) = PlayerObservation.SplitName(givenName);
        if (string.IsNullOrEmpty(given)) return false;
        if (!_observations.Remove(given)) return false;
        Rebuild();
        SaveObservations();
        return true;
    }

    // Drop every observation last seen more than days days ago, EXCEPT those
    // whose customization is flagged DontAutoDelete. Returns the number
    // removed. Customizations for purged players stay attached to the profile —
    // a later observation re-binds them automatically.
    public int PurgeStale(int days, DateTime nowUtc)
    {
        if (days <= 0) return 0;
        DateTime cutoff = nowUtc.AddDays(-days);
        int removed = 0;
        // Both dicts share the same key (GivenName) so the customization
        // lookup for the DontAutoDelete flag is a direct hit.
        foreach (string given in _observations.Keys.ToArray())
        {
            PlayerObservation o = _observations[given];
            if (o.LastSeenUtc >= cutoff) continue;
            if (_customizations.TryGetValue(given, out PlayerCustomization c) && c.DontAutoDelete) continue;
            _observations.Remove(given);
            removed++;
        }
        if (removed > 0)
        {
            Rebuild();
            SaveObservations();
        }
        return removed;
    }

    // Replace the BBS-tier observation layer wholesale. Used by load-from-disk
    // paths and by tests that want a deterministic baseline.
    public void ReplaceObservations(IEnumerable<PlayerObservation> rows)
    {
        _observations.Clear();
        foreach (PlayerObservation o in rows)
        {
            if (string.IsNullOrEmpty(o.GivenName)) continue;
            MergeOnLoad(o);
        }
        Rebuild();
    }

    // ----- Swap + persistence -------------------------------------------

    private void OnSwap()
    {
        BbsProfile? bbs = _activeBbsProvider?.Invoke();
        _activeBbsName = bbs?.Name;

        // BBS layer: load from disk if a BBS resolves, else clear.
        // Files may have been written under the pre-bugfix layout — one
        // dict entry per (Given, Family) pair, so the same player at
        // different family names appears twice. Collapse those on load
        // by given-name, picking the newer LastSeen as the canonical
        // observation. The next save rewrites the file in the merged
        // form, so this migration is one-shot.
        _observations.Clear();
        if (!string.IsNullOrEmpty(_activeBbsName))
        {
            string path = AppPaths.BbsPlayersFile(_activeBbsName);
            List<PlayerObservation>? loaded = JsonStore.Load<List<PlayerObservation>>(path);
            if (loaded is not null)
            {
                foreach (PlayerObservation o in loaded)
                {
                    if (string.IsNullOrEmpty(o.GivenName)) continue;
                    MergeOnLoad(o);
                }
            }
        }

        // Char layer: pull off the loaded profile (null when no profile).
        // Same migration story: customization dicts written before the
        // re-key may carry "Given Family" string keys. Extract given and
        // re-bucket. If a player had been customized under both an old
        // and a new family name, the non-default entry wins; ties pick
        // the last-iterated one (rare in practice — most users only
        // configure flags on characters they actually party with).
        _customizations.Clear();
        CharacterProfile? profile = _profile?.Current;
        if (profile?.PlayerCustomizations is { } pcs)
        {
            foreach ((string oldKey, PlayerCustomization c) in pcs)
            {
                if (string.IsNullOrEmpty(oldKey)) continue;
                (string given, _) = PlayerObservation.SplitName(oldKey);
                if (string.IsNullOrEmpty(given)) continue;
                if (!_customizations.TryGetValue(given, out PlayerCustomization existing)
                    || existing.IsDefault)
                {
                    _customizations[given] = c;
                }
            }
        }

        Rebuild();
    }

    // Insert one observation, collapsing collisions on GivenName. The newer
    // LastSeenUtc wins for the volatile fields (FamilyName / Class / Race /
    // etc.); the older FirstSeenUtc is retained so the "we've known this player
    // since X" stat survives the merge. Equipment is treated the same as in
    // RecordLook — the newer snapshot replaces the older when present.
    private void MergeOnLoad(PlayerObservation o)
    {
        if (!_observations.TryGetValue(o.GivenName, out PlayerObservation? existing))
        {
            _observations[o.GivenName] = o;
            return;
        }
        bool incomingIsNewer = o.LastSeenUtc >= existing.LastSeenUtc;
        PlayerObservation newer = incomingIsNewer ? o : existing;
        PlayerObservation older = incomingIsNewer ? existing : o;
        _observations[o.GivenName] = newer with
        {
            FirstSeenUtc = older.FirstSeenUtc < newer.FirstSeenUtc ? older.FirstSeenUtc : newer.FirstSeenUtc,
            // Equipment: keep newer snapshot when it has one, else fall
            // back to older (an equipment-bearing look-observation
            // shouldn't be erased by a later who-observation that didn't
            // carry equipment).
            Equipment    = newer.Equipment ?? older.Equipment,
            // Level: same treatment — a probed level survives a later
            // who-observation that carried no level.
            Level        = newer.Level ?? older.Level,
            // Greet time isn't ordered by LastSeen — keep the later of
            // the two so a duplicate-row collapse never re-opens a greet
            // we already sent today.
            LastGreetedUtc = LaterGreet(newer.LastGreetedUtc, older.LastGreetedUtc),
        };
    }

    private static DateTime? LaterGreet(DateTime? a, DateTime? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a.Value >= b.Value ? a : b;
    }

    // Pushed onto ProfileService.ProfileSaving so the in-memory customization
    // dict gets serialised onto the profile JUST before the file is written.
    // Pristine entries are pruned — only customizations that hold a non-default
    // value land on disk.
    private void SnapshotCustomizationsForSave(CharacterProfile profile)
    {
        if (_customizations.Count == 0)
        {
            profile.PlayerCustomizations = null;
            return;
        }
        Dictionary<string, PlayerCustomization> snapshot = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, PlayerCustomization c) in _customizations)
        {
            if (c.IsDefault) continue;
            snapshot[key] = c;
        }
        profile.PlayerCustomizations = snapshot.Count == 0 ? null : snapshot;
    }

    private void SaveObservations()
    {
        if (string.IsNullOrEmpty(_activeBbsName)) return; // in-memory mode
        string path = AppPaths.BbsPlayersFile(_activeBbsName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonStore.Save(path, _observations.Values
            .OrderBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    // ----- Merged view rebuild ------------------------------------------

    private void Rebuild()
    {
        Players.Clear();
        foreach (PlayerObservation o in _observations.Values
                     .OrderBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            // Customization key follows the given-name layer — the user's
            // per-player toggles must survive a family-name change.
            _customizations.TryGetValue(o.GivenName, out PlayerCustomization c);
            Players.Add(PlayerRecord.Merge(o, c));
        }
    }
}
