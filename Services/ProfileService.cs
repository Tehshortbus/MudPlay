using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

/// <summary>
/// Owns <c>Data/BBS/{bbs}/profiles/{char}/profile.json</c> — the Character
/// tier of the settings hierarchy. Profiles nest under their BBS folder
/// because each MajorMUD server allows only one character of a given name,
/// so the same name on two BBSes is two different people. Tracks at most
/// one loaded profile at a time (identified by the
/// <see cref="CurrentBbsName"/> + <see cref="CurrentProfileName"/> pair);
/// per-character services subscribe to <see cref="ProfileLoaded"/> /
/// <see cref="ProfileClosed"/> to swap their per-char state.
/// </summary>
public sealed class ProfileService
{
    /// <summary>
    /// The currently loaded character profile. Set on startup so settings
    /// edits always have a target — either the auto-loaded last-used
    /// profile, or a fresh in-memory blank one (see <see cref="LoadBlank"/>).
    /// Goes <c>null</c> only after an explicit <see cref="Close"/>.
    /// </summary>
    public CharacterProfile? Current { get; private set; }

    /// <summary>
    /// Profile-name identifier (filename without extension) of the loaded
    /// profile, or <c>null</c> when the loaded profile is a blank in-memory
    /// draft that hasn't been saved yet. Distinct from <c>Current.Name</c>
    /// because the in-game name may differ from the profile filename when
    /// two characters share a name across BBSes.
    /// </summary>
    public string? CurrentProfileName { get; private set; }

    /// <summary>
    /// BBS folder the loaded profile lives under — the authoritative link
    /// between a character and its server (there is no longer a
    /// <c>BbsName</c> field on the DTO; folder location is the source of
    /// truth). For a named profile this is set from disk on
    /// <see cref="Load"/>; for a blank draft it stays <c>null</c> until the
    /// user pins a BBS via <see cref="PinDraftBbs"/> (Settings → BBS Apply).
    /// Consumed by <see cref="SettingsResolver"/> and
    /// <c>AppServices.ResolveActiveBbs</c> to decide the active BBS.
    /// </summary>
    public string? CurrentBbsName { get; private set; }

    /// <summary>True when <see cref="Current"/> is an unsaved blank draft (no name on disk yet).</summary>
    public bool IsBlankDraft => Current is not null && CurrentProfileName is null;

    /// <summary>
    /// Fired after a profile becomes <see cref="Current"/>. Per-character
    /// services rebind their state inside the handler.
    /// </summary>
    public event Action<CharacterProfile>? ProfileLoaded;

    /// <summary>
    /// Fired after <see cref="Close"/> wipes <see cref="Current"/>. Used by
    /// per-character services to release per-char resources.
    /// </summary>
    public event Action? ProfileClosed;

    /// <summary>
    /// Fired inside <see cref="Save"/> just before serialization. Subscribers
    /// write their latest state into the profile DTO so it lands in the
    /// JSON. Example: <see cref="FloatingPanelHost"/> updates
    /// <see cref="CharacterProfile.PanelLayouts"/>.
    /// </summary>
    public event Action<CharacterProfile>? ProfileSaving;

    /// <summary>
    /// Fired whenever in-memory state on <see cref="Current"/> changes via
    /// the settings UI (BBS pin, credential edit, etc.) — i.e. anywhere a
    /// disk save isn't guaranteed (blank drafts no-op the save path but
    /// observers still need to refresh). Bindings like the main window's
    /// title + active-BBS-derived Host / Port listen here.
    /// </summary>
    public event Action<CharacterProfile>? ProfileMutated;

    /// <summary>Fire <see cref="ProfileMutated"/> for the current profile, if any.</summary>
    public void NotifyMutated()
    {
        if (Current is not null) ProfileMutated?.Invoke(Current);
    }

    /// <summary>
    /// Raised specifically from the Settings → BBS tab's Apply path so
    /// consumers that care about an explicit BBS-pin selection — like
    /// the main window's Quick Connect override — can clear themselves
    /// even when the user re-selected the same BBS (i.e. the pinned
    /// name didn't change and <see cref="ProfileMutated"/> would not
    /// otherwise convey new intent).
    /// </summary>
    public event Action<CharacterProfile>? BbsPinApplied;

    /// <summary>Fire <see cref="BbsPinApplied"/> for the current profile, if any.</summary>
    public void NotifyBbsPinApplied()
    {
        if (Current is not null) BbsPinApplied?.Invoke(Current);
    }

    /// <summary>
    /// Load the profile stored at
    /// <c>Data/BBS/{bbsName}/profiles/{profileName}/profile.json</c> and fire
    /// <see cref="ProfileLoaded"/>. If a different profile is already loaded
    /// it is closed first (<see cref="ProfileClosed"/> fires before the new
    /// load).
    /// </summary>
    /// <param name="bbsName">BBS folder the profile lives under.</param>
    /// <param name="profileName">Character folder name (the profile name).</param>
    /// <returns>The loaded profile.</returns>
    /// <exception cref="FileNotFoundException">No file at the expected path.</exception>
    public CharacterProfile Load(string bbsName, string profileName)
    {
        if (string.IsNullOrWhiteSpace(bbsName))
            throw new ArgumentException("BBS name is required.", nameof(bbsName));
        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException("Profile name is required.", nameof(profileName));

        string path = AppPaths.CharacterProfileFile(bbsName, profileName);
        CharacterProfile loaded = JsonStore.Load<CharacterProfile>(path)
            ?? throw new FileNotFoundException(
                $"Character profile '{profileName}' on '{bbsName}' not found.", path);
        NormalizeForLoad(loaded);

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
        ProfileLoaded?.Invoke(loaded);
        return loaded;
    }

    /// <summary>
    /// Normalize a freshly-deserialized profile's BBS-keyed lookups to be
    /// case-insensitive. BBS names are case-insensitive (they're folder names
    /// on a case-insensitive filesystem), but <see cref="CharacterProfile.BbsCredentials"/>
    /// may have been keyed with different casing than the BBS profile's
    /// <c>Name</c> (e.g. a <c>"Playpen"</c> credential key for a <c>"playpen"</c>
    /// BBS), which a default case-sensitive dictionary fails to resolve.
    /// Rebuilds the dictionary with <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// (last-wins on any case-duplicate key). Internal for test access.
    /// </summary>
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

    /// <summary>
    /// Replace <see cref="Current"/> with a fresh in-memory draft profile and
    /// fire <see cref="ProfileLoaded"/>. Used on app start when no last-used
    /// profile exists — every settings tab still has a target to read / write,
    /// and the user can keep editing freely until they explicitly save the
    /// draft under a name (File → Save profile, Phase 4 PR 4.5).
    /// </summary>
    /// <remarks>
    /// <see cref="CurrentProfileName"/> stays <c>null</c> for a draft, so
    /// <see cref="Save"/> is a no-op until the user names it. In-memory edits
    /// are lost if the user closes the app without naming + saving.
    /// </remarks>
    public CharacterProfile LoadBlank()
    {
        if (Current is not null)
        {
            // Auto-save the outgoing profile (no-op on drafts) so per-session
            // edits aren't dropped by File → New.
            try { Save(); }
            catch { /* swallow — LoadBlank shouldn't fail because the outgoing save did */ }
            Current = null;
            CurrentProfileName = null;
            ProfileClosed?.Invoke();
        }

        CharacterProfile draft = new();
        Current = draft;
        CurrentProfileName = null;
        CurrentBbsName = null;
        ProfileLoaded?.Invoke(draft);
        return draft;
    }

    /// <summary>
    /// Pin a BBS onto the loaded blank draft so its credentials / overrides
    /// have a home before the draft is named. No-op (and ignored) for a
    /// named profile — those re-home via <see cref="ReHome"/> instead, since
    /// the folder already exists on disk. Fires nothing; the Settings → BBS
    /// Apply path that calls this also raises
    /// <see cref="NotifyMutated"/> / <see cref="NotifyBbsPinApplied"/>.
    /// </summary>
    public void PinDraftBbs(string? bbsName)
    {
        if (CurrentProfileName is not null) return; // named profile → ReHome path owns this.
        CurrentBbsName = string.IsNullOrWhiteSpace(bbsName) ? null : bbsName;
    }

    /// <summary>
    /// Move the loaded named profile's folder from its current BBS (and
    /// name) to <paramref name="newBbs"/> (and optionally
    /// <paramref name="newName"/>), updating <see cref="CurrentBbsName"/> /
    /// <see cref="CurrentProfileName"/> / <see cref="CharacterProfile.Name"/>
    /// to match. Silent — mirrors the prompt-less rename the Settings → BBS
    /// tab does for the no-clash case. Throws if no named profile is loaded
    /// or the destination folder already exists (the caller resolves a name
    /// clash first, then re-invokes with a clash-free <paramref name="newName"/>).
    /// </summary>
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

        string sourceFolder = AppPaths.ProfileFolder(CurrentBbsName, CurrentProfileName);
        string destFolder = AppPaths.ProfileFolder(newBbs, targetName);
        if (Directory.Exists(destFolder))
            throw new IOException($"A profile already exists at '{destFolder}'.");

        Directory.CreateDirectory(AppPaths.BbsProfilesDir(newBbs));
        if (Directory.Exists(sourceFolder))
            Directory.Move(sourceFolder, destFolder);
        else
            Directory.CreateDirectory(destFolder); // never-saved-yet edge: just stake the destination.

        Current.Name = targetName;
        CurrentProfileName = targetName;
        CurrentBbsName = newBbs;
    }

    /// <summary>
    /// Persist the currently loaded profile back to disk. No-op when no
    /// profile is loaded or the loaded profile is a blank draft
    /// (<see cref="IsBlankDraft"/>) — drafts must be named via the
    /// File → Save profile flow before they can be saved.
    /// </summary>
    /// <param name="backup">When <c>true</c> and a previously-saved file
    /// exists, copy it to <c>{name}.json.bak</c> (overwriting any prior
    /// backup) before the new content is written. Called with <c>true</c>
    /// by the General-settings Apply path when the user opts in via the
    /// "Backup profile when making changes" toggle.</param>
    public void Save(bool backup = false)
    {
        // A draft with no name (CurrentProfileName) or no pinned BBS
        // (CurrentBbsName) has nowhere to write — drafts must be named +
        // BBS-pinned via the File → Save As / Settings → BBS Apply flow first.
        if (Current is null || CurrentProfileName is null || CurrentBbsName is null) return;
        ProfileSaving?.Invoke(Current);

        Directory.CreateDirectory(AppPaths.ProfileFolder(CurrentBbsName, CurrentProfileName));
        string path = AppPaths.CharacterProfileFile(CurrentBbsName, CurrentProfileName);
        if (backup && File.Exists(path))
        {
            File.Copy(path, path + ".bak", overwrite: true);
        }
        JsonStore.Save(path, Current);
    }

    /// <summary>Clear <see cref="Current"/> and fire <see cref="ProfileClosed"/>.</summary>
    public void Close()
    {
        if (Current is null) return;
        Current = null;
        CurrentProfileName = null;
        CurrentBbsName = null;
        ProfileClosed?.Invoke();
    }

    /// <summary>
    /// Save the in-memory profile as <paramref name="profileName"/> under
    /// <paramref name="bbsName"/>. Used by File → New profile (to name a
    /// fresh blank), File → Save As, and the "name your draft" path of
    /// File → Save when the loaded profile doesn't have a name yet. Replaces
    /// an existing file at that path without asking — the caller owns the
    /// confirm-overwrite UX.
    /// </summary>
    public void SaveAs(string bbsName, string profileName)
    {
        if (string.IsNullOrWhiteSpace(bbsName))
            throw new ArgumentException("BBS name is required.", nameof(bbsName));
        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException("Profile name is required.", nameof(profileName));
        if (Current is null)
            throw new InvalidOperationException("No profile loaded to save.");

        Current.Name = profileName;
        CurrentProfileName = profileName;
        CurrentBbsName = bbsName;
        ProfileSaving?.Invoke(Current);
        Directory.CreateDirectory(AppPaths.ProfileFolder(bbsName, profileName));
        JsonStore.Save(AppPaths.CharacterProfileFile(bbsName, profileName), Current);
    }

    /// <summary>
    /// Enumerate every profile across every BBS that has a primary
    /// <c>profile.json</c> on disk, as <c>(bbs, char)</c> pairs. Folders
    /// missing a primary file are skipped — they aren't fully initialised
    /// yet. Drives the File → Open picker and the recent-profiles list.
    /// </summary>
    public IEnumerable<ProfileRef> ListAll()
    {
        if (!Directory.Exists(AppPaths.BbsDir)) yield break;
        foreach (string bbsFolder in Directory.EnumerateDirectories(AppPaths.BbsDir))
        {
            string bbs = Path.GetFileName(bbsFolder);
            if (string.IsNullOrEmpty(bbs)) continue;
            string profilesDir = AppPaths.BbsProfilesDir(bbs);
            if (!Directory.Exists(profilesDir)) continue;
            foreach (string charFolder in Directory.EnumerateDirectories(profilesDir))
            {
                string name = Path.GetFileName(charFolder);
                if (string.IsNullOrEmpty(name)) continue;
                if (!File.Exists(AppPaths.CharacterProfileFile(bbs, name))) continue;
                yield return new ProfileRef(bbs, name);
            }
        }
    }

    /// <summary>True if a saved profile with the given name already exists under the BBS.</summary>
    public bool Exists(string bbsName, string profileName)
        => !string.IsNullOrWhiteSpace(bbsName)
           && !string.IsNullOrWhiteSpace(profileName)
           && File.Exists(AppPaths.CharacterProfileFile(bbsName, profileName));
}
