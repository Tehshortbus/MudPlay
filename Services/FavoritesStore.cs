using System.Collections.Generic;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

/// <summary>
/// Per-character favourite-room bookmarks for the Navigation GOTO
/// pane. Mirrors <see cref="MovementFilter"/>'s ProfileLoaded /
/// ProfileClosed wiring — hydrates the in-memory cache from
/// <see cref="CharacterProfile.Favorites"/> on profile load, writes
/// back to the profile on every mutation. Persisted via
/// <see cref="ProfileService.Save"/>.
/// </summary>
/// <remarks>
/// <para>
/// Singleton in <see cref="AppServices"/>. Consumers (Navigation
/// view-model) subscribe to <see cref="Changed"/> for refresh; the
/// store doesn't push a sorted view itself — sort order is a UI
/// concern.
/// </para>
/// <para>
/// The label stored per entry is the user's chosen text from the
/// "Add to favorites" prompt. When the label is null/empty, callers
/// fall back to the room's graph display name (so the GOTO row still
/// reads sensibly).
/// </para>
/// </remarks>
public sealed class FavoritesStore
{
    private readonly ProfileService _profile;
    private readonly LogService? _log;
    private readonly Dictionary<RoomKey, FavoriteRoom> _favorites = new();

    public FavoritesStore(ProfileService profile, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _log = log;

        _profile.ProfileLoaded += OnProfileLoaded;
        _profile.ProfileClosed += OnProfileClosed;

        // Pick up the already-loaded profile, if any.
        if (_profile.Current is { } current) OnProfileLoaded(current);
    }

    /// <summary>Read-only snapshot of every favourite for the active character.</summary>
    public IReadOnlyCollection<FavoriteRoom> All => _favorites.Values;

    /// <summary>True when <paramref name="key"/> is currently bookmarked.</summary>
    public bool IsFavorite(RoomKey key) => _favorites.ContainsKey(key);

    /// <summary>Fires after every mutation (add / rename / remove / profile-swap).</summary>
    public event Action? Changed;

    /// <summary>
    /// Bookmark <paramref name="key"/> with an optional user-typed
    /// label. No-op when the key is already in the list (rename via
    /// <see cref="Rename"/> or remove + add) or no profile is loaded.
    /// Persists immediately.
    /// </summary>
    public void Add(RoomKey key, string? label = null)
    {
        if (_profile.Current is not { } current) return;
        if (_favorites.ContainsKey(key)) return;

        FavoriteRoom entry = new(key.Map, key.Room, label);
        _favorites[key] = entry;
        current.Favorites ??= new List<FavoriteRoom>();
        current.Favorites.Add(entry);
        _profile.Save();
        _log?.Info("Favorites", $"added {key}" + (label is null ? string.Empty : $" ('{label}')"));
        Changed?.Invoke();
    }

    /// <summary>Update an existing favourite's label. No-op when not bookmarked or no profile loaded.</summary>
    public void Rename(RoomKey key, string? newLabel)
    {
        if (_profile.Current is not { } current) return;
        if (!_favorites.TryGetValue(key, out FavoriteRoom? entry)) return;

        entry.Label = newLabel;
        _profile.Save();
        _log?.Info("Favorites", $"renamed {key} → '{newLabel}'");
        Changed?.Invoke();
    }

    /// <summary>Remove the favourite. No-op when not bookmarked or no profile loaded.</summary>
    public void Remove(RoomKey key)
    {
        if (_profile.Current is not { } current) return;
        if (!_favorites.Remove(key)) return;

        if (current.Favorites is { } list)
            list.RemoveAll(f => f.Map == key.Map && f.Room == key.Room);

        _profile.Save();
        _log?.Info("Favorites", $"removed {key}");
        Changed?.Invoke();
    }

    private void OnProfileLoaded(CharacterProfile profile)
    {
        _favorites.Clear();
        if (profile.Favorites is { } list)
        {
            foreach (FavoriteRoom f in list)
                _favorites[new RoomKey(f.Map, f.Room)] = f;
        }
        Changed?.Invoke();
    }

    private void OnProfileClosed()
    {
        bool had = _favorites.Count > 0;
        _favorites.Clear();
        if (had) Changed?.Invoke();
    }
}
