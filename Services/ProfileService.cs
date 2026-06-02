using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

/// <summary>
/// Owns <c>Data/profiles/{char-name}.json</c> — the Character tier of the
/// settings hierarchy. Tracks at most one loaded profile at a time;
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
    /// Load the profile stored at <c>Data/profiles/{name}.json</c> and fire
    /// <see cref="ProfileLoaded"/>. If a different profile is already loaded
    /// it is closed first (<see cref="ProfileClosed"/> fires before the new
    /// load).
    /// </summary>
    /// <param name="profileName">Filename of the profile to load, without the
    /// <c>.json</c> extension.</param>
    /// <returns>The loaded profile.</returns>
    /// <exception cref="FileNotFoundException">No file at the expected path.</exception>
    /// <summary>
    /// Read a named profile's persisted <see cref="CharacterProfile.BbsName"/>
    /// without mutating <see cref="Current"/>. Used by the
    /// File → Recent profiles menu to label each slot with the BBS
    /// it'll connect to. Returns <c>null</c> when the profile file
    /// doesn't exist, can't be parsed, or has no pinned BBS.
    /// </summary>
    public string? PeekBbs(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return null;
        string path = AppPaths.CharacterProfileFile(profileName);
        if (!File.Exists(path)) return null;
        try
        {
            CharacterProfile? p = JsonStore.Load<CharacterProfile>(path);
            return string.IsNullOrEmpty(p?.BbsName) ? null : p.BbsName;
        }
        catch
        {
            return null;
        }
    }

    public CharacterProfile Load(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException("Profile name is required.", nameof(profileName));

        string path = AppPaths.CharacterProfileFile(profileName);
        CharacterProfile loaded = JsonStore.Load<CharacterProfile>(path)
            ?? throw new FileNotFoundException(
                $"Character profile '{profileName}' not found.", path);

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
            ProfileClosed?.Invoke();
        }

        Current = loaded;
        CurrentProfileName = profileName;
        ProfileLoaded?.Invoke(loaded);
        return loaded;
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
        ProfileLoaded?.Invoke(draft);
        return draft;
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
        if (Current is null || CurrentProfileName is null) return;
        ProfileSaving?.Invoke(Current);

        Directory.CreateDirectory(AppPaths.ProfileFolder(CurrentProfileName));
        string path = AppPaths.CharacterProfileFile(CurrentProfileName);
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
        ProfileClosed?.Invoke();
    }

    /// <summary>
    /// Save the in-memory profile under <paramref name="profileName"/>. Used
    /// by File → New profile (to name a fresh blank), File → Save As, and the
    /// "name your draft" path of File → Save when the loaded profile doesn't
    /// have a name yet. Replaces an existing file at that name without
    /// asking — the caller is responsible for the confirm-overwrite UX.
    /// </summary>
    public void SaveAs(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException("Profile name is required.", nameof(profileName));
        if (Current is null)
            throw new InvalidOperationException("No profile loaded to save.");

        Current.Name = profileName;
        CurrentProfileName = profileName;
        ProfileSaving?.Invoke(Current);
        Directory.CreateDirectory(AppPaths.ProfileFolder(profileName));
        JsonStore.Save(AppPaths.CharacterProfileFile(profileName), Current);
    }

    /// <summary>
    /// Enumerate every profile that has a primary <c>profile.json</c>
    /// on disk. The folder name (= profile name) is yielded. Folders
    /// missing a primary file are skipped — they aren't fully
    /// initialised yet.
    /// </summary>
    public IEnumerable<string> ListNames()
    {
        if (!Directory.Exists(AppPaths.ProfilesDir)) yield break;
        foreach (string folder in Directory.EnumerateDirectories(AppPaths.ProfilesDir))
        {
            string name = Path.GetFileName(folder);
            if (string.IsNullOrEmpty(name)) continue;
            if (!File.Exists(AppPaths.CharacterProfileFile(name))) continue;
            yield return name;
        }
    }

    /// <summary>True if a saved profile with the given name already exists.</summary>
    public bool Exists(string profileName)
        => !string.IsNullOrWhiteSpace(profileName)
           && File.Exists(AppPaths.CharacterProfileFile(profileName));
}
