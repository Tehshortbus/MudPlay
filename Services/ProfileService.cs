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
    /// Load the profile stored at <c>Data/profiles/{name}.json</c> and fire
    /// <see cref="ProfileLoaded"/>. If a different profile is already loaded
    /// it is closed first (<see cref="ProfileClosed"/> fires before the new
    /// load).
    /// </summary>
    /// <param name="profileName">Filename of the profile to load, without the
    /// <c>.json</c> extension.</param>
    /// <returns>The loaded profile.</returns>
    /// <exception cref="FileNotFoundException">No file at the expected path.</exception>
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
}
