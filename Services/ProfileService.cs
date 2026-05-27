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
    /// The currently loaded character profile, or <c>null</c> when no profile
    /// is active (first launch, or user explicitly closed it).
    /// </summary>
    public CharacterProfile? Current { get; private set; }

    /// <summary>
    /// Profile-name identifier (filename without extension) of the loaded
    /// profile, or <c>null</c> when none is loaded. Distinct from
    /// <c>Current.Name</c> because the in-game name may differ from the
    /// profile filename when two characters share a name across BBSes.
    /// </summary>
    public string? CurrentProfileName { get; private set; }

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
    /// Persist the currently loaded profile back to disk. No-op when no
    /// profile is loaded.
    /// </summary>
    public void Save()
    {
        if (Current is null || CurrentProfileName is null) return;
        JsonStore.Save(AppPaths.CharacterProfileFile(CurrentProfileName), Current);
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
