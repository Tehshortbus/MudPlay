using System.Text.Json;
using MudPlay.Models.Profile;

namespace MudPlay.Services;

// Owns BBS/{bbs}/profiles/{char}/profile.json — the Character tier of the
// settings hierarchy. Profiles nest under their BBS folder because each
// MajorMUD server allows only one character of a given name, so the same name
// on two BBSes is two different people. Tracks at most one loaded profile at a
// time (identified by the CurrentBbsName + CurrentProfileName pair);
// per-character services subscribe to ProfileLoaded / ProfileClosed to swap
// their per-char state.
public sealed class ProfileService
{
    // The currently loaded character profile. Set on startup so settings edits
    // always have a target — either the auto-loaded last-used profile, or the
    // default profile (see LoadDefaultProfile). Goes null only after an
    // explicit Close.
    public CharacterProfile? Current { get; private set; }

    // Decide whether a stored CharacterProfile.Name (the in-game character name — see
    // CurrentProfileName below for why that's distinct from the profile FILE label)
    // should be healed from the authoritative `stat` screen, and to what. Returns the
    // name to store — the full live "Given Family" name — when they differ, or null to
    // leave it (identical name → no write → no Save churn; blank stat name → nothing to
    // heal from). Name is otherwise only ever written on create/rename, so a profile
    // COPIED from another character keeps the old name until this heals it, mis-tagging
    // "self" in every consumer (report stock-20260828-104653). Pure;
    // AppServices' stat-name handler applies the result.
    public static string? HealedCharacterName(string? currentName, string? statName)
        => string.IsNullOrWhiteSpace(statName)
           || string.Equals(currentName, statName, StringComparison.OrdinalIgnoreCase)
            ? null
            : statName;

    // Profile-name identifier (filename without extension) of the loaded
    // profile, or null when the loaded profile is a blank in-memory draft that
    // hasn't been saved yet. Distinct from Current.Name because the in-game
    // name may differ from the profile filename when two characters share a
    // name across BBSes.
    public string? CurrentProfileName { get; private set; }

    // BBS folder the loaded profile lives under — the authoritative link
    // between a character and its server (there is no longer a BbsName field on
    // the DTO; folder location is the source of truth). For a named profile
    // this is set from disk on Load. A default-profile draft started from Profile
    // Management's New… carries the BBS that was selected there (so connecting,
    // BBS-tier settings and Save As… all follow it); otherwise a draft has none
    // until it's named + homed. Only a name AND a BBS make a real character.
    // Consumed by SettingsResolver and AppServices.ResolveActiveBbs to decide the
    // active BBS.
    public string? CurrentBbsName { get; private set; }

    // A save was asked for while the unnamed draft was the loaded profile: it holds
    // something that is kept only if it is given a name. The draft is never written
    // (see Save), so this is what closing the client, or loading another profile,
    // asks the user about. Cleared when a profile is loaded or the draft is named.
    public bool UnnamedDraftHasChanges => _draftSaveWanted && Current is not null && CurrentProfileName is null;

    private bool _draftSaveWanted;

    // The user chose not to keep what the unnamed draft holds.
    public void DiscardUnnamedDraftChanges() => _draftSaveWanted = false;

    // The loaded character's realm as this client last read or wrote it on disk.
    // Profile Management in another client assigns a realm by rewriting the file,
    // while this client keeps its in-memory copy and saves the whole profile often
    // — so a character open in two clients lost its realm to the stale copy and
    // played (and wrote its realm files) on the BBS's first realm. Save compares
    // against this to adopt such a change instead of writing over it.
    private string? _savedRealm;

    // True when the loaded profile is the default profile (no named character) —
    // it persists to the Global default-profile file rather than a per-character
    // one, and gates File → Save's label + the "not a named character" UI.
    public bool IsBlankDraft => Current is not null && CurrentProfileName is null;

    // Late-bound system-log sink. Set by AppServices after construction so the
    // character-lifecycle transitions (load / swap / close / re-home) land in
    // the always-on Info stream — the peer of the game-data set audit in
    // GameDataCache.
    public LogService? Log { get; set; }
    // Times saves for the performance log; set once by AppServices.
    public PerformanceMonitor? Performance { get; set; }

    private const string LogCategory = "Profile";

    // The default profile's template and the marker of its one-time reset.
    private readonly string _defaultProfileFile;
    private readonly string _defaultProfileResetMarker;

    public ProfileService() : this(AppPaths.DefaultProfileFile, AppPaths.DefaultProfileResetMarker) { }

    internal ProfileService(string defaultProfileFile, string defaultProfileResetMarker)
    {
        _defaultProfileFile = defaultProfileFile;
        _defaultProfileResetMarker = defaultProfileResetMarker;
    }

    // Fired after a profile becomes Current. Per-character services rebind
    // their state inside the handler.
    public event Action<CharacterProfile>? ProfileLoaded;

    // Fired after Close wipes Current, so per-character services can release
    // per-char resources.
    public event Action? ProfileClosed;

    // Fired inside Save just before serialization. Subscribers write their
    // latest state into the profile DTO so it lands in the JSON.
    public event Action<CharacterProfile>? ProfileSaving;

    // Fired whenever in-memory state on Current changes via the settings UI
    // (BBS pin, credential edit, etc.) — i.e. anywhere a disk save isn't
    // guaranteed (blank drafts no-op the save path but observers still need to
    // refresh). Bindings like the main window's title + active-BBS-derived
    // Host / Port listen here.
    public event Action<CharacterProfile>? ProfileMutated;

    // Fire ProfileMutated for the current profile, if any.
    public void NotifyMutated()
    {
        if (Current is not null) ProfileMutated?.Invoke(Current);
    }

    // Raised whenever the loaded character's BBS or realm is (re)pinned — the
    // Settings → BBS tab's Apply, a realm assignment or re-home in Profile
    // Management, or a Save that adopts a realm another client wrote — so
    // consumers that follow the active realm reload, and ones that care about an
    // explicit pin selection (the main window's Quick Connect override) can clear
    // themselves even when the same BBS was re-selected (the pinned name didn't
    // change and ProfileMutated would not otherwise convey new intent).
    public event Action<CharacterProfile>? BbsPinApplied;

    // Fire BbsPinApplied for the current profile, if any.
    public void NotifyBbsPinApplied()
    {
        if (Current is not null) BbsPinApplied?.Invoke(Current);
    }

    // Fired after the loaded profile comes to be known by another BBS / profile name
    // without being closed and loaded: a rename or re-home, its BBS renamed, the
    // default profile named by Save As. Carries the BBS and profile name it had
    // (name null for the default profile); CurrentBbsName / CurrentProfileName are
    // the new ones. For whatever is kept outside the profile under that pair: it is
    // the same character, and the old pair may be given to another.
    public event Action<string?, string?>? ProfileRenamed;

    private void RaiseRenamedFrom(string? oldBbs, string? oldName)
    {
        bool sameBbs = string.Equals(oldBbs, CurrentBbsName, StringComparison.OrdinalIgnoreCase);
        // By the letter: a profile renamed to the same name in other capitals has
        // moved to another folder wherever folders are told apart by them, and what
        // is kept under its name elsewhere has to follow.
        bool sameName = string.Equals(oldName, CurrentProfileName, StringComparison.Ordinal);
        // The default profile has no name on any BBS: one it was started on is not
        // part of who it is.
        if (sameName && (sameBbs || oldName is null)) return;
        ProfileRenamed?.Invoke(oldBbs, oldName);
    }

    // After ProfileRenamed: whether the loaded profile is a copy of the one it was
    // and not that profile renamed or moved. Save As from a named profile leaves
    // the original on disk and carries on as the copy, which is another character.
    //
    // Told by the folders, to the letter: the old name and the new must each be
    // there as a folder of its own. Where capitals don't tell folders apart, a Save
    // As under the same name in other capitals writes into the one folder there is,
    // and is the same profile.
    public bool LoadedIsACopyOf(string? oldBbs, string? oldName)
    {
        if (oldBbs is null || oldName is null || CurrentBbsName is not { } bbs || CurrentProfileName is not { } name)
            return false;
        if (string.Equals(oldBbs, bbs, StringComparison.OrdinalIgnoreCase) && string.Equals(oldName, name, StringComparison.Ordinal))
            return false;
        return HasProfileFolder(oldBbs, oldName) && HasProfileFolder(bbs, name);
    }

    private static bool HasProfileFolder(string bbs, string name)
    {
        try
        {
            string profiles = AppPaths.BbsProfilesDir(bbs);
            return Directory.Exists(profiles)
                && Directory.EnumerateDirectories(profiles).Any(d => string.Equals(Path.GetFileName(d), name, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The same news for profiles that are not the loaded one, and for the ones that
    // are gone: whatever is kept outside a profile under its BBS and name (files in a
    // game-data set's folder, say) has to follow it or go with it, since the pair
    // may be given to another character later.
    //   StoredProfileMoved  a stored profile was renamed or moved to another BBS
    //                       (the loaded one tells of itself through ProfileRenamed)
    //   ProfileDeleted      a profile was deleted, loaded or not
    //   BbsRenamed          a BBS was renamed: every profile under it, loaded or
    //                       not, is now under the new name (raised after
    //                       ProfileRenamed for the loaded one)
    //   BbsDeleted          a BBS was removed, and every profile under it
    //   RealmRenamed        a realm of a BBS was renamed (BBS, old name, new name):
    //                       its data folder has moved to the new name. Raised
    //                       before its characters are re-pointed and saved, and
    //                       before the realm's new name may be saved with its BBS
    // A copy of a profile raises nothing: the copy is another character.
    public event Action<ProfileRef, ProfileRef>? StoredProfileMoved;
    public event Action<ProfileRef>? ProfileDeleted;
    public event Action<string, string>? BbsRenamed;
    public event Action<string>? BbsDeleted;
    public event Action<string, string, string>? RealmRenamed;

    // Raise BbsDeleted. The BBS folder is BbsProfileStore's to remove; whoever
    // removes one says so here, as a rename is told through RenameBbs.
    public void NotifyBbsDeleted(string bbsName)
    {
        if (!string.IsNullOrWhiteSpace(bbsName)) BbsDeleted?.Invoke(bbsName);
    }

    // ----- Names ---------
    //
    // A BBS name and a profile name become two folders, one inside the other, and
    // some of what follows deletes or moves them whole. Neither is trusted: each
    // operation asks here first, and one given a name that can't be used
    // (PathGuard.Usable: a plain name, or a folder already there under an older,
    // odder name) changes nothing, has the refusal logged, and fails the way it
    // already failed for a blank name: the ones that threw throw, the ones that
    // returned return.

    private static bool BbsUsable(string operation, string? bbsName)
    {
        if (PathGuard.Usable(AppPaths.BbsDir, bbsName)) return true;
        PathGuard.Refuse(operation, "BBS name", bbsName);
        return false;
    }

    private static bool PairUsable(string operation, string? bbsName, string? profileName)
    {
        if (!BbsUsable(operation, bbsName)) return false;
        if (PathGuard.Usable(AppPaths.BbsProfilesDir(bbsName!), profileName)) return true;
        PathGuard.Refuse(operation, "profile name", profileName);
        return false;
    }

    private static void RequireBbs(string operation, string? bbsName, string parameter)
    {
        if (!BbsUsable(operation, bbsName))
            throw new ArgumentException($"{PathGuard.Show(bbsName)} can't be used as a BBS name.", parameter);
    }

    private static void RequirePair(string operation, string? bbsName, string? profileName, string parameter)
    {
        if (!PairUsable(operation, bbsName, profileName))
            throw new ArgumentException(
                $"{PathGuard.Show(profileName)} on {PathGuard.Show(bbsName)} can't be used as a profile.", parameter);
    }

    // Where a profile is being moved to. Its name there must be usable, or be the
    // very name it has now: a profile under an older, odder name can change BBS
    // without first being renamed.
    private static void RequireMoveTarget(string operation, string toBbs, string toName, string fromName, string parameter)
    {
        RequireBbs(operation, toBbs, parameter);
        string profiles = AppPaths.BbsProfilesDir(toBbs);
        bool keepsItsName = string.Equals(toName, fromName, StringComparison.Ordinal);
        if (keepsItsName ? PathGuard.Child(profiles, toName) is not null : PathGuard.Usable(profiles, toName)) return;
        PathGuard.Refuse(operation, "profile name", toName);
        throw new ArgumentException($"{PathGuard.Show(toName)} can't be used as a profile name.", parameter);
    }

    // Load the profile stored at
    // BBS/{bbsName}/profiles/{profileName}/profile.json and fire
    // ProfileLoaded. If a different profile is already loaded it is closed
    // first (ProfileClosed fires before the new load). Throws
    // FileNotFoundException when no file exists at the expected path.
    public CharacterProfile Load(string bbsName, string profileName)
    {
        if (string.IsNullOrWhiteSpace(bbsName))
            throw new ArgumentException("BBS name is required.", nameof(bbsName));
        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException("Profile name is required.", nameof(profileName));
        RequirePair("Loading a profile", bbsName, profileName, nameof(profileName));

        string path = AppPaths.CharacterProfileFile(bbsName, profileName);
        CharacterProfile loaded = JsonStore.Load<CharacterProfile>(path)
            ?? throw new FileNotFoundException(
                $"Character profile '{profileName}' on '{bbsName}' not found.", path);
        NormalizeForLoad(loaded);
        // Upgrade older profiles before ProfileLoaded fires so per-character
        // services (KeybindingStore, toolbar) rebind from the migrated shape.
        bool migrated = ProfileMigrations.Apply(loaded, Log);

        string? outgoing = CurrentProfileName;
        if (Current is not null)
        {
            // Auto-save the outgoing profile so per-session edits don't
            // bleed into / get lost behind the incoming profile. Save()
            // already no-ops on blank drafts (no name to write to), so
            // this is only consequential for the common "swap from one
            // named profile to another" path.
            try { Save(); }
            catch { /* swallow — Load shouldn't fail because the outgoing save did */ }
            Current = null;
            CurrentProfileName = null;
            CurrentBbsName = null;
            ProfileClosed?.Invoke();
        }

        Current = loaded;
        CurrentProfileName = profileName;
        CurrentBbsName = bbsName;
        _savedRealm = loaded.Realm;
        Log?.Info(LogCategory, outgoing is null
            ? $"Loaded profile '{profileName}' on '{bbsName}'."
            : $"Swapped profile '{outgoing}' → '{profileName}' on '{bbsName}'.");
        ProfileLoaded?.Invoke(loaded);
        if (migrated)
        {
            // Persist the upgrade now that Current + paths are set — Save runs
            // after ProfileLoaded so the keybind snapshot reflects the reset
            // defaults, not the outgoing profile's bindings.
            Save();
            // Says only that it was upgraded: which steps ran depends on the version
            // it came from, and naming one (it used to say the keybindings were
            // reset, on every upgrade) misleads whoever reads a report.
            Log?.Info(LogCategory,
                $"Upgraded profile '{profileName}' to schema v{CharacterProfile.CurrentSchemaVersion}.");
        }
        return loaded;
    }

    // Normalize a freshly-deserialized profile's BBS-keyed lookups to be
    // case-insensitive. BBS names are case-insensitive (they're folder names on
    // a case-insensitive filesystem), but CharacterProfile.BbsCredentials may
    // have been keyed with different casing than the BBS profile's Name (e.g. a
    // "Playpen" credential key for a "playpen" BBS), which a default
    // case-sensitive dictionary fails to resolve. Rebuilds the dictionary with
    // StringComparer.OrdinalIgnoreCase (last-wins on any case-duplicate key).
    internal static void NormalizeForLoad(CharacterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.BbsCredentials is { Count: > 0 } creds
            && !ReferenceEquals(creds.Comparer, StringComparer.OrdinalIgnoreCase))
        {
            var normalized = new Dictionary<string, BbsCredentials>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, BbsCredentials> kv in creds)
                normalized[kv.Key] = kv.Value;
            profile.BbsCredentials = normalized;
        }
    }

    // Load the "default profile" — the CharacterProfile persisted at
    // AppPaths.DefaultProfileFile in the Global folder — and fire ProfileLoaded.
    // Used on app start when no last-used named profile exists, and by New.
    // When the file doesn't exist (a fresh install) a new CharacterProfile
    // (installed defaults) stands in.
    //
    // CurrentProfileName stays null: the default profile isn't a named character.
    // It is the template a new character starts from, and a session on it is an
    // unnamed draft that is never written back (see Save): Save As makes it a
    // character. Any outgoing named profile is auto-saved first so per-session
    // edits aren't dropped.
    public CharacterProfile LoadDefaultProfile(string? onBbs = null) =>
        LoadUnnamedDraft(ReadDefaultProfileFile(), onBbs, somethingToKeep: false);

    // The default profile was, until 3.180.23, whatever the last session on it had
    // saved: for someone who never named a character, their whole setup. The update
    // that stops it being written also puts it back to the installed defaults, once
    // (user, 2026-10-12: "returns existing defaults back to the actual defaults when
    // they update to this"). What it held is handed back, so the launch that did
    // the reset can load it as an unnamed draft with something to keep
    // (LoadDraftFromBeforeTheReset): whoever was playing on it finds their setup
    // where it was, and it only has to be named. No copy is kept on disk (user,
    // 2026-10-12), so a launch that comes up on a named profile lets it go. The
    // startup-animation setting, which lives in this file, is carried over. Null
    // when there was nothing to reset, or it was done before.
    public CharacterProfile? ResetDefaultProfileOnce()
    {
        string marker = _defaultProfileResetMarker;
        if (File.Exists(marker)) return null;

        CharacterProfile? held = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            if (File.Exists(_defaultProfileFile))
            {
                held = ReadDefaultProfileFile();
                bool animate = ReadGeneralSection(held).ShowStartupMudAnimation;
                File.Delete(_defaultProfileFile);
                if (animate != ReadGeneralSection(new CharacterProfile()).ShowStartupMudAnimation)
                    WriteDefaultProfileStartupAnimation(animate);
                Log?.Info(LogCategory,
                    "The default profile was put back to the installed defaults (once, with this version).");
            }
            File.WriteAllText(marker, $"default profile reset by {AppInfo.Version} on {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next launch: with no marker written it is tried again.
            Log?.Warn(LogCategory, $"The default profile could not be put back to the installed defaults: {ex.Message}");
            return null;
        }
        return held;
    }

    // Load what the default profile held before the reset as the unnamed draft, with
    // something to keep: Save names it, and leaving it asks.
    public CharacterProfile LoadDraftFromBeforeTheReset(CharacterProfile held) =>
        LoadUnnamedDraft(held, onBbs: null, somethingToKeep: true);

    private CharacterProfile LoadUnnamedDraft(CharacterProfile profile, string? onBbs, bool somethingToKeep)
    {
        string? outgoing = CurrentProfileName;
        if (Current is not null)
        {
            try { Save(); }
            catch { /* swallow — a load shouldn't fail because the outgoing save did */ }
            Current = null;
            CurrentProfileName = null;
            ProfileClosed?.Invoke();
        }

        NormalizeForLoad(profile);
        ProfileMigrations.Apply(profile, Log);

        Current = profile;
        CurrentProfileName = null;
        // The BBS a draft is started on is where its BBS-tier settings are read from
        // and where Save As puts it: one that can't be a BBS's name is no BBS.
        CurrentBbsName = string.IsNullOrWhiteSpace(onBbs) || !BbsUsable("Starting the default profile on a BBS", onBbs)
            ? null
            : onBbs;
        Log?.Info(LogCategory, somethingToKeep
            ? "Loaded what the default profile held before it was reset, as an unnamed profile to be named."
            : outgoing is null
                ? "Loaded the default profile (no character loaded)."
                : $"Closed profile '{outgoing}'; loaded the default profile.");
        ProfileLoaded?.Invoke(profile);
        // After the load's own handlers: a store that saves as it takes the new
        // profile in is not the user changing anything.
        _draftSaveWanted = somethingToKeep;
        return profile;
    }

    // Deserialize the Global default-profile file, or a fresh CharacterProfile
    // (installed defaults) when it's missing or unreadable — a corrupt default
    // must never block startup.
    private CharacterProfile ReadDefaultProfileFile()
    {
        if (!File.Exists(_defaultProfileFile)) return new CharacterProfile();
        try
        {
            CharacterProfile template = JsonStore.Load<CharacterProfile>(_defaultProfileFile)
                                        ?? new CharacterProfile();
            // An empty favourites list on the template is what a session left
            // there while new profiles started with none (3.147 to 3.180.20), and
            // the template can't be changed any more. Read as "not taken yet", so
            // a draft or a new character from it takes the starters (FavoritesStore).
            if (template.Favorites is { Count: 0 })
            {
                template.Favorites = null;
                template.FavoriteFolders = null;
            }
            return template;
        }
        catch (Exception ex)
        {
            Log?.Warn(LogCategory,
                $"Default profile at '{_defaultProfileFile}' was unreadable ({ex.Message}); using installed defaults.");
            return new CharacterProfile();
        }
    }

    // The startup-animation preference is install-global, sourced from the Global
    // default profile — an attract screen shown before any character is active, so it
    // must not depend on which profile "auto-load last" happened to open. Reads
    // Current when the default profile is the loaded one, else the persisted file.
    public bool ReadDefaultProfileStartupAnimation()
        => ReadGeneralSection(CurrentProfileName is null && Current is not null
            ? Current
            : ReadDefaultProfileFile()).ShowStartupMudAnimation;

    // Persist the install-global startup-animation flag onto the Global default
    // profile. When the default profile is the loaded one, edit it in place and Save
    // (that path writes the Global file); otherwise load-modify-save the Global file
    // directly, leaving any loaded named profile untouched.
    public void WriteDefaultProfileStartupAnimation(bool value)
    {
        // The loaded unnamed draft is kept in step, and the file is written by itself:
        // a save of the draft writes nothing, and this is the one thing in the
        // default profile's file the program still changes.
        if (CurrentProfileName is null && Current is not null)
        {
            GeneralSettings live = ReadGeneralSection(Current);
            live.ShowStartupMudAnimation = value;
            WriteGeneralSection(Current, live);
        }
        CharacterProfile profile = ReadDefaultProfileFile();
        GeneralSettings general = ReadGeneralSection(profile);
        general.ShowStartupMudAnimation = value;
        WriteGeneralSection(profile, general);
        Directory.CreateDirectory(Path.GetDirectoryName(_defaultProfileFile)!);
        JsonStore.Save(_defaultProfileFile, profile);
    }

    private static GeneralSettings ReadGeneralSection(CharacterProfile profile)
    {
        if (profile.Settings is { } settings
            && settings.TryGetValue("General", out JsonElement json))
        {
            try { return JsonSerializer.Deserialize<GeneralSettings>(json) ?? new(); }
            catch { return new(); }
        }
        return new();
    }

    private static void WriteGeneralSection(CharacterProfile profile, GeneralSettings general)
    {
        profile.Settings ??= new();
        profile.Settings["General"] = JsonSerializer.SerializeToElement(general);
    }

    // Move the loaded named profile's folder from its current BBS (and name) to
    // newBbs (and optionally newName), updating CurrentBbsName /
    // CurrentProfileName / CharacterProfile.Name to match. Silent — mirrors the
    // prompt-less rename the Settings → BBS tab does for the no-clash case.
    // Throws if no named profile is loaded or the destination folder already
    // exists (the caller resolves a name clash first, then re-invokes with a
    // clash-free newName).
    public void ReHome(string newBbs, string? newName = null)
    {
        if (string.IsNullOrWhiteSpace(newBbs))
            throw new ArgumentException("Destination BBS is required.", nameof(newBbs));
        if (Current is null || CurrentProfileName is null || CurrentBbsName is null)
            throw new InvalidOperationException("ReHome requires a loaded named profile.");

        string targetName = string.IsNullOrWhiteSpace(newName) ? CurrentProfileName : newName;
        if (string.Equals(newBbs, CurrentBbsName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(targetName, CurrentProfileName, StringComparison.Ordinal))
            return; // no-op move.
        RequireMoveTarget("Moving the loaded profile", newBbs, targetName, CurrentProfileName, nameof(newName));

        string sourceFolder = AppPaths.ProfileFolder(CurrentBbsName, CurrentProfileName);
        string destFolder = AppPaths.ProfileFolder(newBbs, targetName);
        if (Directory.Exists(destFolder))
            throw new IOException($"A profile already exists at '{destFolder}'.");

        Directory.CreateDirectory(AppPaths.BbsProfilesDir(newBbs));
        if (Directory.Exists(sourceFolder))
            Directory.Move(sourceFolder, destFolder);
        else
            Directory.CreateDirectory(destFolder); // never-saved-yet edge: just stake the destination.

        string fromBbs = CurrentBbsName;
        string fromName = CurrentProfileName;
        Current.Name = targetName;
        CurrentProfileName = targetName;
        CurrentBbsName = newBbs;
        Log?.Info(LogCategory,
            $"Re-homed profile '{fromName}' ({fromBbs}) → '{targetName}' ({newBbs}).");
        RaiseRenamedFrom(fromBbs, fromName);
    }

    // Cascade a BBS rename across the character profiles stored under it. The
    // BBS folder has already been moved to newBbs (BbsProfileStore.Rename), so
    // the profiles now sit under the new name; this re-keys each profile's
    // BbsCredentials entry from oldBbs to newBbs — credentials are keyed by BBS
    // name, and a stale key breaks logon-nav / password lookup and shows the
    // dead name in the "import logon steps" picker. When the loaded profile
    // lives under the renamed BBS, its in-memory copy and CurrentBbsName are
    // updated too so the live session keeps resolving credentials and the
    // active-BBS link.
    public void RenameBbs(string oldBbs, string newBbs)
    {
        if (string.IsNullOrWhiteSpace(oldBbs) || string.IsNullOrWhiteSpace(newBbs)) return;
        if (string.Equals(oldBbs, newBbs, StringComparison.OrdinalIgnoreCase)) return;
        // The folder is under newBbs by now, so a name that is not one's is no rename.
        if (!BbsUsable("Following a BBS's rename", newBbs)) return;

        bool currentAffected = string.Equals(CurrentBbsName, oldBbs, StringComparison.OrdinalIgnoreCase);
        if (currentAffected) CurrentBbsName = newBbs;

        foreach (ProfileRef reference in ListAll())
        {
            if (!string.Equals(reference.Bbs, newBbs, StringComparison.OrdinalIgnoreCase)) continue;

            // The loaded profile's on-disk file is authored from its in-memory
            // copy below — skip it here to avoid a lost update.
            bool isCurrent = currentAffected
                && CurrentProfileName is not null
                && string.Equals(reference.Name, CurrentProfileName, StringComparison.Ordinal);
            if (isCurrent) continue;

            string path = AppPaths.CharacterProfileFile(newBbs, reference.Name);
            CharacterProfile? profile = JsonStore.Load<CharacterProfile>(path);
            if (profile is null) continue;
            NormalizeForLoad(profile);
            if (RekeyBbsCredentials(profile, oldBbs, newBbs))
                JsonStore.Save(path, profile);
        }

        if (currentAffected && Current is not null)
        {
            RekeyBbsCredentials(Current, oldBbs, newBbs);
            Save();
            RaiseRenamedFrom(oldBbs, CurrentProfileName);
        }
        BbsRenamed?.Invoke(oldBbs, newBbs);
    }

    // The realm a saved character is assigned to (CharacterProfile.Realm; null =
    // the BBS's first). The loaded character answers from memory.
    public string? RealmOf(ProfileRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (IsCurrent(reference)) return Current?.Realm;
        if (!PairUsable("Reading a profile's realm", reference.Bbs, reference.Name)) return null;
        return JsonStore.Load<CharacterProfile>(AppPaths.CharacterProfileFile(reference.Bbs, reference.Name))?.Realm;
    }

    // Assign a saved character to a realm of its BBS. The loaded character is
    // updated in memory and saved (callers re-pin so the realm's data follows).
    public void AssignRealm(ProfileRef reference, string? realm)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (IsCurrent(reference))
        {
            if (Current is null) return;
            Current.Realm = realm;
            Save();
            return;
        }
        if (!PairUsable("Assigning a profile to a realm", reference.Bbs, reference.Name)) return;
        string path = AppPaths.CharacterProfileFile(reference.Bbs, reference.Name);
        if (JsonStore.Load<CharacterProfile>(path) is not { } profile) return;
        profile.Realm = realm;
        JsonStore.Save(path, profile);
    }

    private bool IsCurrent(ProfileRef reference) =>
        CurrentProfileName is not null
        && string.Equals(reference.Bbs, CurrentBbsName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(reference.Name, CurrentProfileName, StringComparison.Ordinal);

    // Re-point every character on a BBS assigned to realm oldRealm at newRealm —
    // a realm renamed, or removed (newRealm null: back to the BBS's first realm).
    // The loaded profile is updated in memory and saved; the rest on disk.
    public void RenameRealm(string bbs, string oldRealm, string? newRealm)
    {
        if (string.IsNullOrWhiteSpace(bbs) || string.IsNullOrWhiteSpace(oldRealm)) return;
        if (!BbsUsable("Re-pointing a realm's characters", bbs)) return;
        // First, so what is kept in the realm's folder follows it before a save
        // below can re-pin the loaded character on the new name.
        if (!string.IsNullOrWhiteSpace(newRealm)) RealmRenamed?.Invoke(bbs, oldRealm, newRealm);
        bool Assigned(CharacterProfile p) =>
            string.Equals(p.Realm, oldRealm, StringComparison.OrdinalIgnoreCase);
        bool currentOnBbs = string.Equals(CurrentBbsName, bbs, StringComparison.OrdinalIgnoreCase);

        foreach (ProfileRef reference in ListAll())
        {
            if (!string.Equals(reference.Bbs, bbs, StringComparison.OrdinalIgnoreCase)) continue;
            if (currentOnBbs && CurrentProfileName is not null
                && string.Equals(reference.Name, CurrentProfileName, StringComparison.Ordinal))
                continue;   // authored from the in-memory copy below
            string path = AppPaths.CharacterProfileFile(reference.Bbs, reference.Name);
            if (JsonStore.Load<CharacterProfile>(path) is not { } profile || !Assigned(profile)) continue;
            profile.Realm = newRealm;
            JsonStore.Save(path, profile);
        }

        if (currentOnBbs && Current is { } current && Assigned(current))
        {
            current.Realm = newRealm;
            Save();
        }
    }

    // Move a BbsCredentials entry from oldBbs to newBbs on a profile's
    // case-insensitive credential map. Returns true if anything changed.
    private static bool RekeyBbsCredentials(CharacterProfile profile, string oldBbs, string newBbs)
    {
        if (profile.BbsCredentials is not { Count: > 0 } creds) return false;
        if (!creds.TryGetValue(oldBbs, out BbsCredentials? cred)) return false;
        creds.Remove(oldBbs);
        // TryAdd, not indexer: never clobber a pre-existing new-key entry. The
        // destination BBS was clash-checked before the rename, so this only
        // guards a corrupt double-keyed profile.
        creds.TryAdd(newBbs, cred);
        return true;
    }

    // Persist the currently loaded profile back to disk. No-op when no profile
    // is loaded, and for the unnamed draft, which must be named (Save As) before
    // anything is written.
    // When backup is true and a previously-saved file exists, copy it to
    // profile.json.bak beside it (overwriting any prior backup) before the new
    // content is written. The General-settings Apply path passes true when the user opts
    // in via the "Backup profile when making changes" toggle.
    public void Save(bool backup = false)
    {
        if (Current is null) return;
        using PerformanceMonitor.Scope? timing = Performance?.Measure("profile save");

        // The unnamed draft is never written. It used to be saved over the Global
        // default-profile file, the template every new character starts from, so
        // whoever played without naming a character had their whole setup become
        // that template, and never had a profile of their own (user, 2026-10-12: "i
        // just want to abandon the user being able to change the default profile").
        // What it holds is kept by naming it; that a save was wanted is remembered,
        // so the user is asked before it would be lost.
        if (CurrentProfileName is null)
        {
            if (!_draftSaveWanted)
            {
                _draftSaveWanted = true;
                Log?.Debug(LogCategory, "the unnamed draft has something to keep; nothing is written until it is named");
            }
            return;
        }

        // A named profile with no pinned BBS has nowhere to write — it's named +
        // BBS-pinned via Profile Management's Save As / the Settings → BBS Apply
        // flow first.
        if (CurrentBbsName is null) return;
        string path = AppPaths.CharacterProfileFile(CurrentBbsName, CurrentProfileName);
        bool realmAdopted = AdoptRealmChangedOnDisk(path);
        ProfileSaving?.Invoke(Current);

        Directory.CreateDirectory(AppPaths.ProfileFolder(CurrentBbsName, CurrentProfileName));
        if (backup && File.Exists(path))
        {
            File.Copy(path, path + ".bak", overwrite: true);
        }
        JsonStore.Save(path, Current);
        _savedRealm = Current.Realm;
        // Re-pin so the realm stores, game data and menu commands follow the realm.
        if (realmAdopted) NotifyBbsPinApplied();
    }

    // Take the realm another client wrote to this character's file since we last
    // read or wrote it — unless we changed it ourselves meanwhile, when ours stands.
    private bool AdoptRealmChangedOnDisk(string path)
    {
        if (Current is not { } current) return false;
        if (!string.Equals(current.Realm, _savedRealm, StringComparison.OrdinalIgnoreCase)) return false;
        if (!TryReadRealm(path, out string? onDisk)) return false;
        if (string.Equals(onDisk, _savedRealm, StringComparison.OrdinalIgnoreCase)) return false;
        Log?.Info(LogCategory,
            $"'{CurrentProfileName}' was moved to realm '{onDisk ?? "(the BBS's first)"}' by another client — following it.");
        current.Realm = onDisk;
        return true;
    }

    // Just the Realm field of a saved profile. False when the file can't be read
    // right now (missing, briefly locked, mid-write) — the next save checks again.
    private static bool TryReadRealm(string path, out string? realm)
    {
        realm = null;
        try
        {
            if (!File.Exists(path)) return false;
            using FileStream fs = File.OpenRead(path);
            using JsonDocument doc = JsonDocument.Parse(fs, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            realm = doc.RootElement.TryGetProperty(nameof(CharacterProfile.Realm), out JsonElement r)
                    && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    // Read-modify-write one section of the loaded profile's Settings: deserialize
    // Settings[key] as T (a fresh default T when absent or unparseable), hand it to
    // the mutator, then serialize it back and persist. The single safe path when a
    // section is written from more than one view (e.g. Settings → Talk and the
    // Conversation window both touch "Talk") — each mutates only its own fields, so
    // neither clobbers the other. No-op when no profile is loaded; for an unnamed
    // draft the in-memory change stays and lands on the next named Save.
    public void UpdateSection<T>(string key, Action<T> mutate) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(mutate);
        if (Current is not { } profile) return;

        T dto = new();
        if (profile.Settings is { } settings && settings.TryGetValue(key, out JsonElement json))
        {
            try { dto = JsonSerializer.Deserialize<T>(json) ?? new T(); }
            catch { dto = new T(); }
        }
        mutate(dto);
        profile.Settings ??= new();
        profile.Settings[key] = JsonSerializer.SerializeToElement(dto);
        Save();
    }

    // Clear Current and fire ProfileClosed.
    public void Close()
    {
        if (Current is null) return;
        string? outgoing = CurrentProfileName;
        Current = null;
        CurrentProfileName = null;
        CurrentBbsName = null;
        Log?.Info(LogCategory, outgoing is null
            ? "Closed the blank draft profile."
            : $"Closed profile '{outgoing}'.");
        ProfileClosed?.Invoke();
    }

    // Save the in-memory profile as profileName under bbsName. The "Save as"
    // action in the Profile Management window: names the loaded profile (e.g. a
    // fresh {default} draft) and writes it. Replaces an existing file at that
    // path without asking — the caller owns the confirm-overwrite UX.
    public void SaveAs(string bbsName, string profileName)
    {
        if (string.IsNullOrWhiteSpace(bbsName))
            throw new ArgumentException("BBS name is required.", nameof(bbsName));
        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException("Profile name is required.", nameof(profileName));
        if (Current is null)
            throw new InvalidOperationException("No profile loaded to save.");
        RequirePair("Saving a profile under a name", bbsName, profileName, nameof(profileName));

        // Naming the default profile makes a new character: one that starts from
        // the starters it holds, not from the lists older characters shared.
        if (CurrentProfileName is null) Current.PredatesOwnLists = false;
        string? fromBbs = CurrentBbsName, fromName = CurrentProfileName;
        Current.Name = profileName;
        CurrentProfileName = profileName;
        CurrentBbsName = bbsName;
        ProfileSaving?.Invoke(Current);
        Directory.CreateDirectory(AppPaths.ProfileFolder(bbsName, profileName));
        JsonStore.Save(AppPaths.CharacterProfileFile(bbsName, profileName), Current);
        _draftSaveWanted = false;
        _savedRealm = Current.Realm;
        RaiseRenamedFrom(fromBbs, fromName);
    }

    // Create a new named character on a BBS from the Global default template,
    // WITHOUT loading it — the explicit "add a character" path (the Profile
    // Management window), distinct from SaveAs which renames the loaded profile.
    // Throws on a blank name or when a profile of that name already exists under
    // the BBS. Fires nothing: Current is untouched.
    // configure, when given, shapes the new character before its first save (an
    // import writing a whole set of settings onto it).
    public void CreateProfile(string bbsName, string profileName, Action<CharacterProfile>? configure = null)
    {
        if (string.IsNullOrWhiteSpace(bbsName))
            throw new ArgumentException("BBS name is required.", nameof(bbsName));
        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException("Profile name is required.", nameof(profileName));
        RequirePair("Creating a profile", bbsName, profileName, nameof(profileName));
        if (Exists(bbsName, profileName))
            throw new IOException($"A profile named '{profileName}' already exists on '{bbsName}'.");

        CharacterProfile fresh = ReadDefaultProfileFile();
        fresh.Name = profileName;
        // Upgrade the template now: a default-profile file from an older version
        // would otherwise mark the new character as older than per-character
        // favourites and triggers, and it would take the shared lists as its own.
        ProfileMigrations.Apply(fresh, Log);
        fresh.PredatesOwnLists = false;
        // A new character's loop and auto-lair favourites are its own from the
        // start: with these left unset it would take the shared files' flags.
        fresh.FavoriteLoops ??= new List<string>();
        fresh.FavoriteLairSetups ??= new List<string>();
        // What some other character held is no part of a template, nor are its buffs.
        fresh.HeldAtDisconnect = null;
        fresh.BuffTimers = null;
        configure?.Invoke(fresh);
        Directory.CreateDirectory(AppPaths.ProfileFolder(bbsName, profileName));
        JsonStore.Save(AppPaths.CharacterProfileFile(bbsName, profileName), fresh);
        Log?.Info(LogCategory, $"Created profile '{profileName}' on '{bbsName}'.");
    }

    // Copy a saved character to a new name on the same BBS — its whole folder
    // (profile + character-tier overrides), minus .bak backups, with the copy's
    // Name rewritten. The loaded character is saved first so the copy carries its
    // unsaved session edits. Throws when the new name is taken.
    //
    // A copy is usually the settings carried to a different character (another
    // realm, level, race, gear), so what the source read off the game doesn't come
    // along: the copy starts with no stats, Default-gear maxima, carry weight,
    // position or gear-set contents, and is marked StateUnverified so its first
    // entry reads them. Its learned spells and death history are the source's own
    // record and stay behind too, as does any stash tally it still carried (those
    // are the realm's now).
    public void CopyProfile(string bbsName, string fromName, string toName)
    {
        if (string.IsNullOrWhiteSpace(bbsName) || string.IsNullOrWhiteSpace(fromName) || string.IsNullOrWhiteSpace(toName))
            throw new ArgumentException("BBS, source and new name are required.");
        RequirePair("Copying a profile", bbsName, fromName, nameof(fromName));
        RequirePair("Copying a profile", bbsName, toName, nameof(toName));
        if (Exists(bbsName, toName) || Directory.Exists(AppPaths.ProfileFolder(bbsName, toName)))
            throw new IOException($"A profile named '{toName}' already exists on '{bbsName}'.");

        bool isCurrent = Current is not null
            && string.Equals(CurrentBbsName, bbsName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(CurrentProfileName, fromName, StringComparison.Ordinal);
        if (isCurrent) Save();

        string source = AppPaths.ProfileFolder(bbsName, fromName);
        string dest = AppPaths.ProfileFolder(bbsName, toName);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;
            string target = Path.Combine(dest, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        string path = AppPaths.CharacterProfileFile(bbsName, toName);
        if (JsonStore.Load<CharacterProfile>(path) is { } copy)
        {
            copy.Name = toName;
            copy.LastKnownStats = null;
            copy.DefaultPoolBaseline = null;
            copy.LastKnownEncumbrance = null;
            copy.HeldAtDisconnect = null;
            // The buffs are on the source's character, not on the copy's.
            copy.BuffTimers = null;
            copy.LastKnownRoom = null;
            copy.RecentSteps = null;
            copy.PendingReconnectLeader = null;
            // The sets name the source's gear; the set options are settings and stay.
            copy.Equipment?.Sets.Clear();
            copy.LearnedSpells = null;
            copy.DeathHistory = null;
            // The source holds those items, not the copy.
            copy.CorpseHandoffs = null;
            copy.StashedCopper = null;
            copy.StateUnverified = true;
            JsonStore.Save(path, copy);
        }
        Log?.Info(LogCategory,
            $"Copied profile '{fromName}' → '{toName}' on '{bbsName}'; the copy re-reads its stats and inventory on first entry.");
    }

    // Delete a saved profile's folder. When it's the CURRENTLY loaded profile,
    // Close() it FIRST (that clears Current and fires ProfileClosed but does NOT
    // save — a Load/LoadDefaultProfile with a live Current would auto-save the
    // outgoing profile and resurrect the folder we're deleting), then remove the
    // folder, then LoadDefaultProfile so the session always has a live Current
    // (its outgoing-save is skipped because Current is already null). A
    // non-current profile is simply removed from disk (fires nothing).
    public void DeleteProfile(string bbsName, string profileName)
    {
        if (string.IsNullOrWhiteSpace(bbsName) || string.IsNullOrWhiteSpace(profileName)) return;
        const string operation = "Deleting a profile";
        if (!PairUsable(operation, bbsName, profileName)) return;
        string folder = AppPaths.ProfileFolder(bbsName, profileName);
        // The folder goes with all it holds, so it is checked once more as a path:
        // only ever a folder inside this BBS's profiles folder.
        string profiles = AppPaths.BbsProfilesDir(bbsName);
        bool isCurrent = Current is not null
            && string.Equals(CurrentBbsName, bbsName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(CurrentProfileName, profileName, StringComparison.Ordinal);

        if (isCurrent)
        {
            Close();
            if (PathGuard.DeleteTree(folder, profiles, operation))
            {
                Log?.Info(LogCategory, $"Deleted the loaded profile '{profileName}' on '{bbsName}'.");
                ProfileDeleted?.Invoke(new ProfileRef(bbsName, profileName));
            }
            LoadDefaultProfile();
            return;
        }

        if (!PathGuard.DeleteTree(folder, profiles, operation)) return;
        Log?.Info(LogCategory, $"Deleted profile '{profileName}' on '{bbsName}'.");
        ProfileDeleted?.Invoke(new ProfileRef(bbsName, profileName));
    }

    // Move a profile's folder to a new BBS and/or name — unifies rename (same
    // BBS, new name) and assign-to-BBS (different BBS), current or not. For the
    // CURRENTLY loaded profile this reuses ReHome (updates Current + logs). For a
    // stored non-current profile it moves the folder and rewrites the moved
    // profile.json's Name. Throws if the destination already exists (the caller
    // resolves a name clash first, then re-invokes with a clash-free name).
    public void MoveProfile(string fromBbs, string fromName, string toBbs, string toName)
    {
        if (string.IsNullOrWhiteSpace(fromBbs) || string.IsNullOrWhiteSpace(fromName))
            throw new ArgumentException("Source BBS and profile are required.", nameof(fromName));
        if (string.IsNullOrWhiteSpace(toBbs) || string.IsNullOrWhiteSpace(toName))
            throw new ArgumentException("Destination BBS and profile are required.", nameof(toName));
        if (string.Equals(fromBbs, toBbs, StringComparison.OrdinalIgnoreCase)
            && string.Equals(fromName, toName, StringComparison.Ordinal))
            return; // no-op
        const string operation = "Moving a profile";
        RequirePair(operation, fromBbs, fromName, nameof(fromName));
        RequireMoveTarget(operation, toBbs, toName, fromName, nameof(toName));

        bool isCurrent = Current is not null
            && string.Equals(CurrentBbsName, fromBbs, StringComparison.OrdinalIgnoreCase)
            && string.Equals(CurrentProfileName, fromName, StringComparison.Ordinal);
        if (isCurrent)
        {
            ReHome(toBbs, toName);
            return;
        }

        string destFolder = AppPaths.ProfileFolder(toBbs, toName);
        if (Directory.Exists(destFolder) || Exists(toBbs, toName))
            throw new IOException($"A profile already exists at '{destFolder}'.");
        string sourceFolder = AppPaths.ProfileFolder(fromBbs, fromName);
        if (!Directory.Exists(sourceFolder)) return;

        Directory.CreateDirectory(AppPaths.BbsProfilesDir(toBbs));
        Directory.Move(sourceFolder, destFolder);
        // The moved profile.json still carries the old Name — bring it in line.
        string path = AppPaths.CharacterProfileFile(toBbs, toName);
        if (JsonStore.Load<CharacterProfile>(path) is { } moved)
        {
            moved.Name = toName;
            JsonStore.Save(path, moved);
        }
        Log?.Info(LogCategory, $"Moved profile '{fromName}' ({fromBbs}) → '{toName}' ({toBbs}).");
        StoredProfileMoved?.Invoke(new ProfileRef(fromBbs, fromName), new ProfileRef(toBbs, toName));
    }

    // Enumerate every profile across every BBS that has a primary profile.json
    // on disk, as (bbs, char) pairs. Folders missing a primary file are skipped
    // — they aren't fully initialised yet. Drives the Profile Management list and
    // the recent-profiles list.
    public IEnumerable<ProfileRef> ListAll()
    {
        if (!Directory.Exists(AppPaths.BbsDir)) yield break;
        foreach (string bbsFolder in Directory.EnumerateDirectories(AppPaths.BbsDir))
        {
            // A folder's own name is one path segment, plain or not, so a profile
            // under an older, odder name is listed like any other. One that is not
            // (a listing can only hand back such a name off a broken filesystem) is
            // left out: nothing listed here may be a name that leads elsewhere.
            string bbs = Path.GetFileName(bbsFolder);
            if (string.IsNullOrEmpty(bbs) || IsRefusedHolder(bbs) || !BbsUsable("Listing profiles", bbs)) continue;
            foreach (string charFolder in ProfileFoldersIn(AppPaths.BbsProfilesDir(bbs)))
            {
                string name = Path.GetFileName(charFolder);
                if (string.IsNullOrEmpty(name) || IsRefusedHolder(name) || !PairUsable("Listing profiles", bbs, name)) continue;
                if (!File.Exists(AppPaths.CharacterProfileFile(bbs, name))) continue;
                yield return new ProfileRef(bbs, name);
            }
        }
    }

    // The folder AppPaths gives a name it refused is a holding place, never a BBS or
    // a character, whatever it came to hold.
    private static bool IsRefusedHolder(string folderName)
        => folderName.Equals(AppPaths.RefusedName, StringComparison.OrdinalIgnoreCase);

    // A BBS with no profiles folder has no profiles. Read rather than checked for first:
    // another client sharing the data folder can rename or remove a BBS between a
    // check and the read, and the listing must not throw into the window asking.
    private static string[] ProfileFoldersIn(string profilesDir)
    {
        try { return Directory.GetDirectories(profilesDir); }
        catch (DirectoryNotFoundException) { return Array.Empty<string>(); }
    }

    // True if a saved profile with the given name already exists under the BBS. A
    // name that can't be one's has none, which is also how an entry of the recent
    // list or the last-used profile read from global.json is dropped when its
    // names are not names.
    public bool Exists(string bbsName, string profileName)
        => !string.IsNullOrWhiteSpace(bbsName)
           && !string.IsNullOrWhiteSpace(profileName)
           && PairUsable("Looking for a profile", bbsName, profileName)
           && File.Exists(AppPaths.CharacterProfileFile(bbsName, profileName));
}
