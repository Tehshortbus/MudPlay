using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;

namespace MudPlay.Services;

// Two-layer store of player records — the realm's observation list (one row per
// player ever seen on the active realm, shared by every character on it) merged
// with the loaded character's customization dictionary (per-player remote-command
// permissions, auto-party toggles, etc.). Both layers persist to disk:
// observations to the realm's players.json; customizations to the loaded profile's
// CharacterProfile.PlayerCustomizations dictionary, pruned at save time so
// only non-default entries hit disk.
//
// Players is the merged view bound by the Game Data Browser → Players tab.
// Observation writes (RecordObservation from the who-list + look-on-player
// parsers) update the realm layer and schedule a disk save; customization writes
// (EditCustomization from the player edit dialog) update the Character layer
// and schedule a profile save. Either path rebuilds Players.
//
// On a realm swap the observation layer reloads from disk; on profile swap the
// customization layer reloads. PurgeStale drops observations only —
// customizations stay attached to the profile, harmless when no record exists
// for them at the moment (a later observation re-binds them automatically).
public sealed class PlayerDatabase
{
    private readonly ProfileService _profile;
    private readonly Func<string?> _activeRealmFolder;

    // ----- Backing layers ------------------------------------------------

    // realm observations, keyed by GIVEN name (case-insensitive). Given
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

    // Folder of the realm the observation layer currently mirrors. Null when no
    // BBS resolves (in-memory only).
    private string? _realmFolder;

    // Merged view — observable so views can react to updates.
    public ObservableCollection<PlayerRecord> Players { get; } = new();

    // Raised after a who / manual observation is recorded, carrying the
    // player's GIVEN name. AlignmentTracker uses it to clear the local
    // character's stale-alignment flag once our own row is re-observed by a who.
    public event Action<string>? ObservationRecorded;

    // ----- Construction --------------------------------------------------

    // Production ctor. Subscribes to ProfileService's ProfileLoaded /
    // ProfileClosed / ProfileSaving / BbsPinApplied so both layers stay in sync
    // with whichever character + realm the user is on.
    public PlayerDatabase(ProfileService profile, Func<string?> activeRealmFolder)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(activeRealmFolder);
        _profile = profile;
        _activeRealmFolder = activeRealmFolder;

        profile.ProfileLoaded  += _ => OnSwap();
        profile.ProfileClosed  +=      OnSwap;
        profile.BbsPinApplied  += _ => OnSwap();
        profile.ProfileSaving  += SnapshotCustomizationsForSave;
    }

    // ----- Observation writes (realm tier) --------------------------------

    // Apply one observed row (typically from who output). Wire names are split
    // on the first whitespace; the record is keyed on given name so a player
    // who renames at the train-stats screen (family-name change) updates her
    // existing row instead of creating a duplicate. Existing records merge —
    // nulls don't overwrite — so a sparse observation only updates the fields
    // it has; the observed family name DOES overwrite, because seeing a new
    // family name is itself an observation. Saves the realm observation file
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
        TakeInOutsideChanges();
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
    // equipped with Nothing"). Saves the realm observation file after the merge.
    // `gang` comes from the LOOK header's " (<gang>)" suffix and is null when
    // the header carried none — so a look never erases a gang a WHO row taught
    // us, it only fills one in.
    public void RecordLook(
        string name,
        string? race,
        string? @class,
        string? gang,
        IReadOnlyList<EquipmentItem>? equipment,
        DateTime nowUtc)
    {
        TakeInOutsideChanges();
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
                Gang        = gang      ?? existing.Gang,
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
                Gang:         gang,
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

    // ----- Greet tracking (realm tier) ------------------------------------

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
    // it must not overwrite class / race / LastSeen). Saves the realm observation
    // file. Called by GreetManager right after emitting greet / look.
    public void RecordGreeted(string name, DateTime whenUtc)
    {
        TakeInOutsideChanges();
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

    // When PlayerLookManager last auto-looked at this player (UTC), or null if
    // never. Same shape as GetLastGreetedUtc, and read the same way: the
    // manager compares it against the local-calendar day.
    public DateTime? GetLastLookedUtc(string givenName)
    {
        if (string.IsNullOrWhiteSpace(givenName)) return null;
        (string given, _) = PlayerObservation.SplitName(givenName);
        if (string.IsNullOrEmpty(given)) return null;
        return _observations.TryGetValue(given, out PlayerObservation? o) ? o.LastLookedUtc : null;
    }

    // Stamp the auto-look time for one player. Creates a minimal row when the
    // player is unknown, otherwise updates LastLookedUtc in place and leaves
    // every other field alone -- sending a look is not itself an observation,
    // so it must not overwrite class / race / equipment / LastSeen. Called by
    // PlayerLookManager right after it writes the look to the wire.
    public void RecordLooked(string name, DateTime whenUtc)
    {
        TakeInOutsideChanges();
        ArgumentNullException.ThrowIfNull(name);
        (string given, string family) = PlayerObservation.SplitName(name);
        if (string.IsNullOrEmpty(given)) return;

        if (_observations.TryGetValue(given, out PlayerObservation? existing))
        {
            _observations[given] = existing with { LastLookedUtc = whenUtc };
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
                LastLookedUtc:  whenUtc);
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
    // untouched, except a title the reported level contradicts (see below).
    // Saves the realm observation file.
    public void RecordLevel(string name, int level, DateTime nowUtc)
    {
        TakeInOutsideChanges();
        ArgumentNullException.ThrowIfNull(name);
        if (level <= 0) return;
        (string given, string family) = PlayerObservation.SplitName(name);
        if (string.IsNullOrEmpty(given)) return;

        if (_observations.TryGetValue(given, out PlayerObservation? existing))
        {
            // A stored who title whose level band excludes the level they just
            // reported can't be their current title — it belonged to an earlier
            // character under this name (a reroll). Left in place, its band would
            // outrank this reading (PartyLevelEstimate treats a band above the exact
            // level as "trained since"), so an @level answer would change nothing.
            // The next who records their real title.
            string? title = existing.Title;
            if (Game.GameData.ClassTitleTable.LookupLevelRange(title) is { } band
                && (level < band.MinLevel || level > band.MaxLevel))
                title = null;
            _observations[given] = existing with { Level = level, LevelAt = nowUtc, LastSeenUtc = nowUtc, Title = title };
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
                Level:        level,
                LevelAt:      nowUtc);
        }

        Rebuild();
        SaveObservations();
        ObservationRecorded?.Invoke(given);
    }

    // Record one player's client version, as learned from an @version probe reply
    // (e.g. "MudPlay 2.37.0", "MegaMud 1.03u"). Mirrors RecordLevel: keyed on
    // given name, creates a minimal row when unknown (we only get a version reply
    // from someone we asked), otherwise updates Version/VersionAt and bumps
    // LastSeenUtc (answering a telepath proves presence). Saves the realm file.
    public void RecordVersion(string name, string version, DateTime nowUtc)
    {
        TakeInOutsideChanges();
        ArgumentNullException.ThrowIfNull(name);
        if (string.IsNullOrWhiteSpace(version)) return;
        string trimmed = version.Trim();
        (string given, string family) = PlayerObservation.SplitName(name);
        if (string.IsNullOrEmpty(given)) return;

        if (_observations.TryGetValue(given, out PlayerObservation? existing))
        {
            _observations[given] = existing with { Version = trimmed, VersionAt = nowUtc, LastSeenUtc = nowUtc };
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
                Version:      trimmed,
                VersionAt:    nowUtc);
        }

        Rebuild();
        SaveObservations();
        ObservationRecorded?.Invoke(given);
    }

    // Reconcile a class the game stated outright — a party member's on the `par`
    // roster, a player's on the top list — with their record. A name showing a different class than the one on file is a different
    // character now holding that name (a new character after a deletion, a reroll),
    // so everything learned about the old one — its who title and the level band it
    // implies, an exact @level reading, race, alignment, gear — would describe the
    // wrong character. The title band in particular outranks a lower exact level
    // (PartyLevelEstimate), so an old high title kept routing the party around
    // gates the new character clears. Those fields are dropped and LastPartiedUtc
    // cleared so the party probe re-asks today. Customization, gang and client
    // version are left alone. Returns the replaced class when the record changed
    // identity, else null (unknown player, first class seen, or same class).
    public string? RecordStatedClass(string name, string klass)
    {
        TakeInOutsideChanges();
        ArgumentNullException.ThrowIfNull(name);
        if (string.IsNullOrWhiteSpace(klass)) return null;
        (string given, _) = PlayerObservation.SplitName(name);
        if (string.IsNullOrEmpty(given)) return null;
        if (!_observations.TryGetValue(given, out PlayerObservation? existing)) return null;

        string live = klass.Trim();
        if (string.Equals(existing.Class, live, StringComparison.OrdinalIgnoreCase)) return null;

        string? replaced = string.IsNullOrWhiteSpace(existing.Class) ? null : existing.Class;
        _observations[given] = replaced is null
            ? existing with { Class = live }
            : existing with
            {
                Class = live, Race = null, Alignment = null, Title = null,
                Level = null, LevelAt = null, Equipment = null, LastPartiedUtc = null,
            };

        Rebuild();
        SaveObservations();
        ObservationRecorded?.Invoke(given);
        return replaced;
    }

    // ----- Party-day tracking (realm tier) --------------------------------

    // When we last started partying with this player (UTC), or null if never.
    // The party stats probe compares this against the local-calendar day to
    // enforce "send @level / @version at most once per day per player".
    public DateTime? GetLastPartiedUtc(string givenName)
    {
        if (string.IsNullOrWhiteSpace(givenName)) return null;
        (string given, _) = PlayerObservation.SplitName(givenName);
        if (string.IsNullOrEmpty(given)) return null;
        return _observations.TryGetValue(given, out PlayerObservation? o) ? o.LastPartiedUtc : null;
    }

    // Stamp the "last partied with" time for one player. Mirrors RecordGreeted:
    // creates a minimal row when unknown (we're partying with them, so they're
    // real), otherwise updates LastPartiedUtc in place — partying isn't a
    // who/look observation, so it must not overwrite class / race / LastSeen.
    // Saves the realm observation file.
    public void RecordPartied(string name, DateTime whenUtc)
    {
        TakeInOutsideChanges();
        ArgumentNullException.ThrowIfNull(name);
        (string given, string family) = PlayerObservation.SplitName(name);
        if (string.IsNullOrEmpty(given)) return;

        if (_observations.TryGetValue(given, out PlayerObservation? existing))
        {
            _observations[given] = existing with { LastPartiedUtc = whenUtc };
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
                LastPartiedUtc: whenUtc);
        }

        Rebuild();
        SaveObservations();
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
        _profile.Save();
        return true;
    }

    // Set (or clear) the realm-tier account-name override for one player.
    // Authored via the player edit dialog alongside the customization save.
    // Stored on the observation (realm tier) rather than the customization
    // (Character tier) because an account belongs to the realm, not to which
    // of our alts is looking. A blank value — or one equal to the in-game
    // given name — clears the override to null, since no override is needed
    // when the account name already matches the character name. No-op (returns
    // false) when no observation exists for the player or the value is
    // unchanged.
    public bool SetAccountName(string nameOrGiven, string? accountName)
    {
        TakeInOutsideChanges();
        if (string.IsNullOrWhiteSpace(nameOrGiven)) return false;
        (string given, _) = PlayerObservation.SplitName(nameOrGiven);
        if (string.IsNullOrEmpty(given)) return false;
        if (!_observations.TryGetValue(given, out PlayerObservation? existing)) return false;

        string? normalized = string.IsNullOrWhiteSpace(accountName) ? null : accountName.Trim();
        if (normalized is not null && normalized.Equals(given, StringComparison.OrdinalIgnoreCase))
            normalized = null;

        if (string.Equals(existing.AccountName, normalized, StringComparison.OrdinalIgnoreCase))
            return false;

        _observations[given] = existing with { AccountName = normalized };
        Rebuild();
        SaveObservations();
        return true;
    }

    // Set how we stand with a player and, optionally, the response to them that
    // replaces the PvP settings' general one (null = use the general one). Realm
    // tier, like the account name: every character we play here sees the same.
    // Returns false when the player has no record or nothing changed.
    public bool SetRelationship(string nameOrGiven, PlayerRelationship relationship, PvpAction? response)
    {
        TakeInOutsideChanges();
        if (string.IsNullOrWhiteSpace(nameOrGiven)) return false;
        (string given, _) = PlayerObservation.SplitName(nameOrGiven);
        if (string.IsNullOrEmpty(given)) return false;
        if (!_observations.TryGetValue(given, out PlayerObservation? existing)) return false;
        if (existing.Relationship == relationship && existing.PvpResponse == response) return false;

        _observations[given] = existing with { Relationship = relationship, PvpResponse = response };
        Rebuild();
        SaveObservations();
        return true;
    }

    // Resolve a captured presence-line name (which may be an account name on
    // boards that key logon/logoff lines on the account rather than the
    // character) back to the player's in-game GIVEN name. Checks the authored
    // AccountName override first — both account and in-game names are unique,
    // so at most one row matches — then falls back to a direct given-name hit.
    // Returns null when nothing matches — so a board-custom logoff line keyed on
    // the account can still be attributed to a party member.
    public string? ResolveGivenNameFromPresenceName(string presenceName)
    {
        if (string.IsNullOrWhiteSpace(presenceName)) return null;
        string probe = presenceName.Trim();
        foreach (PlayerObservation o in _observations.Values)
        {
            if (!string.IsNullOrEmpty(o.AccountName)
                && o.AccountName.Equals(probe, StringComparison.OrdinalIgnoreCase))
                return o.GivenName;
        }
        // No account-name override matched — treat the captured name as an
        // in-game name directly (the common case where account == character).
        // Return it even without an observation row: a party member tracked
        // only via par may have no who/look observation yet, but we still want
        // to attribute their logoff by name.
        (string given, _) = PlayerObservation.SplitName(probe);
        return string.IsNullOrEmpty(given) ? null : given;
    }

    // Manually add a player record from the Game Data Browser → Players tab's
    // Add button. Differs from RecordObservation only in intent — same merge
    // semantics if the given-name already exists (overwrites Family, bumps
    // LastSeen, keeps prior fields the caller didn't supply). Returns true when
    // a brand-new row was created, false when the call merged into an existing
    // row.
    public bool AddManual(string givenName, string familyName, DateTime nowUtc)
    {
        TakeInOutsideChanges();
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
        TakeInOutsideChanges();
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
        TakeInOutsideChanges();
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
            // A Friend or an Enemy is something the user told us: it outlasts the
            // player going unseen, on every character, where the per-character
            // keep flag above only covers the one that set it.
            if (o.Relationship != PlayerRelationship.Neutral) continue;
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

    // ----- Swap + persistence -------------------------------------------

    private void OnSwap()
    {
        _realmFolder = _activeRealmFolder();

        // Realm layer: load from disk if a realm resolves, else clear.
        // Files may have been written under the pre-bugfix layout — one
        // dict entry per (Given, Family) pair, so the same player at
        // different family names appears twice. Collapse those on load
        // by given-name, picking the newer LastSeen as the canonical
        // observation. The next save rewrites the file in the merged
        // form, so this migration is one-shot.
        LoadObservations();

        // Char layer: pull off the loaded profile (null when no profile).
        // Same migration story: customization dicts written before the
        // re-key may carry "Given Family" string keys. Extract given and
        // re-bucket. If a player had been customized under both an old
        // and a new family name, the non-default entry wins; ties pick
        // the last-iterated one (rare in practice — most users only
        // configure flags on characters they actually party with).
        _customizations.Clear();
        CharacterProfile? profile = _profile.Current;
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
            // who-observation that carried no level. LevelAt rides along with
            // whichever Level won, so the staleness stamp stays paired with it.
            Level        = newer.Level ?? older.Level,
            LevelAt      = newer.Level is not null ? newer.LevelAt : older.LevelAt,
            // Version: same as Level — a probed version survives a later
            // observation that carried none, and VersionAt rides with it.
            Version      = newer.Version ?? older.Version,
            VersionAt    = newer.Version is not null ? newer.VersionAt : older.VersionAt,
            // Greet + last-partied times aren't ordered by LastSeen — keep the
            // later of the two so a duplicate-row collapse never re-opens a
            // greet / party-probe we already did today.
            LastGreetedUtc = LaterUtc(newer.LastGreetedUtc, older.LastGreetedUtc),
            LastLookedUtc  = LaterUtc(newer.LastLookedUtc,  older.LastLookedUtc),
            LastPartiedUtc = LaterUtc(newer.LastPartiedUtc, older.LastPartiedUtc),
        };
    }

    private static DateTime? LaterUtc(DateTime? a, DateTime? b)
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
        if (string.IsNullOrEmpty(_realmFolder)) return; // in-memory mode
        string path = AppPaths.RealmPlayersFile(_realmFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonStore.Save(path, _observations.Values
            .OrderBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList());
        _stamp.Mark(path);
    }

    private readonly SharedFileStamp _stamp = new();

    // Re-read the realm's observations when another client on it has written them.
    // Every character on the realm records into the one file, and each used to
    // write the whole file from its own copy, so what one learned (a class from a
    // look, a level from a party probe) was gone the next time another saw anyone.
    // Called on the heartbeat and before every change, so a change is made to what
    // the last character recorded. True when anything was re-read.
    public bool TakeInOutsideChanges()
    {
        if (string.IsNullOrEmpty(_realmFolder)) return false;
        if (!_stamp.ChangedOutside(AppPaths.RealmPlayersFile(_realmFolder))) return false;
        LoadObservations();
        Rebuild();
        return true;
    }

    private void LoadObservations()
    {
        _observations.Clear();
        if (string.IsNullOrEmpty(_realmFolder)) return;
        string path = AppPaths.RealmPlayersFile(_realmFolder);
        _stamp.Mark(path);
        List<PlayerObservation>? loaded = JsonStore.Load<List<PlayerObservation>>(path);
        if (loaded is null) return;
        foreach (PlayerObservation o in loaded)
        {
            if (string.IsNullOrEmpty(o.GivenName)) continue;
            MergeOnLoad(o);
        }
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
