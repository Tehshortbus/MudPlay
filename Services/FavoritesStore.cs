using System.Collections.Generic;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

// Per-character favourite-room bookmarks for the Navigation GOTO pane. Mirrors
// MovementFilter's ProfileLoaded / ProfileClosed wiring — hydrates the in-memory
// cache from CharacterProfile.Favorites on profile load, writes back to the
// profile on every mutation. Persisted via ProfileService.Save.
//
// Singleton in AppServices. Consumers (Navigation view-model) subscribe to
// Changed for refresh; the store doesn't push a sorted view itself — sort order
// is a UI concern.
//
// The label stored per entry is the user's chosen text from the "Add to
// favorites" prompt. When the label is null/empty, callers fall back to the
// room's graph display name (so the GOTO row still reads sensibly).
public sealed class FavoritesStore
{
    private readonly ProfileService _profile;
    private readonly LogService? _log;
    private readonly Dictionary<RoomKey, FavoriteRoom> _favorites = new();

    // Empty folders the user created but hasn't filled yet (mirrors CharacterProfile.FavoriteFolders).
    private readonly HashSet<string> _emptyFolders = new(StringComparer.OrdinalIgnoreCase);

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

    // Read-only snapshot of every favourite for the active character.
    public IReadOnlyCollection<FavoriteRoom> All => _favorites.Values;

    // True when key is currently bookmarked.
    public bool IsFavorite(RoomKey key) => _favorites.ContainsKey(key);

    // Normalised folder path of key, or string.Empty (root / not a favourite).
    public string FolderOf(RoomKey key) =>
        _favorites.TryGetValue(key, out FavoriteRoom? f) ? NavFolders.Normalize(f.Folder) : string.Empty;

    // Every folder node the GOTO tree must render — the ancestors of each
    // favourite's folder plus any remembered empty folders. Excludes the root.
    // Order is unspecified; the UI sorts.
    public IReadOnlyCollection<string> AllFolders
    {
        get
        {
            var paths = new List<string?>(_emptyFolders);
            foreach (FavoriteRoom f in _favorites.Values) paths.Add(f.Folder);
            return NavFolders.ExpandAncestors(paths);
        }
    }

    // Fires after every mutation (add / rename / remove / move / folder op / profile-swap).
    public event Action? Changed;

    // Bookmark key with an optional user-typed label and target folder. No-op
    // when the key is already in the list (rename via Rename or remove + add) or
    // no profile is loaded. Persists immediately.
    public void Add(RoomKey key, string? label = null, string? folder = null)
    {
        if (_profile.Current is not { } current) return;
        if (_favorites.ContainsKey(key)) return;

        string norm = NavFolders.Normalize(folder);
        FavoriteRoom entry = new(key.Map, key.Room, label, norm.Length == 0 ? null : norm);
        _favorites[key] = entry;
        current.Favorites ??= new List<FavoriteRoom>();
        current.Favorites.Add(entry);
        // The folder is now non-empty — drop any empty-folder record for it.
        if (norm.Length != 0) RemoveEmptyFolderRecord(current, norm);
        _profile.Save();
        _log?.Info("Favorites", $"added {key}" + (label is null ? string.Empty : $" ('{label}')"));
        Changed?.Invoke();
    }

    // Update an existing favourite's label. No-op when not bookmarked or no profile loaded.
    public void Rename(RoomKey key, string? newLabel)
    {
        if (_profile.Current is not { } current) return;
        if (!_favorites.TryGetValue(key, out FavoriteRoom? entry)) return;

        entry.Label = newLabel;
        _profile.Save();
        _log?.Info("Favorites", $"renamed {key} → '{newLabel}'");
        Changed?.Invoke();
    }

    // Remove the favourite. No-op when not bookmarked or no profile loaded.
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

    // Move a bookmarked room into folder (empty = root). No-op when not
    // bookmarked, no profile loaded, or already there. Persists immediately.
    public void MoveFavorite(RoomKey key, string? folder)
    {
        if (_profile.Current is not { } current) return;
        if (!_favorites.TryGetValue(key, out FavoriteRoom? entry)) return;

        string norm = NavFolders.Normalize(folder);
        string from = NavFolders.Normalize(entry.Folder);
        if (string.Equals(from, norm, StringComparison.OrdinalIgnoreCase)) return;

        entry.Folder = norm.Length == 0 ? null : norm;
        if (norm.Length != 0) RemoveEmptyFolderRecord(current, norm);
        _profile.Save();
        _log?.Info("Favorites", $"moved {key} → '{(norm.Length == 0 ? "(root)" : norm)}'");
        Changed?.Invoke();
    }

    // Create an empty folder so it shows in the tree before any favourite is
    // filed under it. No-op when no profile loaded or the folder already exists
    // (as an empty record or via a favourite).
    public void AddFolder(string path)
    {
        if (_profile.Current is not { } current) return;
        string norm = NavFolders.Normalize(path);
        if (norm.Length == 0) return;
        if (AllFolders.Any(f => string.Equals(f, norm, StringComparison.OrdinalIgnoreCase))) return;

        _emptyFolders.Add(norm);
        current.FavoriteFolders ??= new List<string>();
        current.FavoriteFolders.Add(norm);
        _profile.Save();
        Changed?.Invoke();
    }

    // Rename folder oldPath (and every sub-folder / favourite beneath it) to
    // newPath. No-op when no profile loaded or the path is the root.
    public void RenameFolder(string oldPath, string newPath)
    {
        if (_profile.Current is not { } current) return;
        string from = NavFolders.Normalize(oldPath);
        string to = NavFolders.Normalize(newPath);
        if (from.Length == 0 || to.Length == 0) return;
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;

        foreach (FavoriteRoom f in _favorites.Values)
        {
            if (!NavFolders.IsSelfOrDescendant(from, f.Folder)) continue;
            string rebased = NavFolders.Rebase(from, to, NavFolders.Normalize(f.Folder));
            f.Folder = rebased.Length == 0 ? null : rebased;
        }
        RebaseEmptyFolders(current, from, to);
        _profile.Save();
        _log?.Info("Favorites", $"renamed folder '{from}' → '{to}'");
        Changed?.Invoke();
    }

    // Remove folder path. When moveContentsToParent is true, favourites and
    // sub-folders beneath it are re-parented one level up; otherwise the caller
    // must have emptied it first (anything still inside is also re-parented to
    // keep favourites from being orphaned). No-op at the root or with no profile
    // loaded.
    public void RemoveFolder(string path, bool moveContentsToParent = true)
    {
        if (_profile.Current is not { } current) return;
        string from = NavFolders.Normalize(path);
        if (from.Length == 0) return;
        string parent = NavFolders.Parent(from);

        foreach (FavoriteRoom f in _favorites.Values)
        {
            if (!NavFolders.IsSelfOrDescendant(from, f.Folder)) continue;
            string rebased = moveContentsToParent
                ? NavFolders.Rebase(from, parent, NavFolders.Normalize(f.Folder))
                : parent;
            f.Folder = rebased.Length == 0 ? null : rebased;
        }

        if (current.FavoriteFolders is { } empties)
            empties.RemoveAll(e => NavFolders.IsSelfOrDescendant(from, e));
        _emptyFolders.RemoveWhere(e => NavFolders.IsSelfOrDescendant(from, e));

        _profile.Save();
        _log?.Info("Favorites", $"removed folder '{from}'");
        Changed?.Invoke();
    }

    private static void RemoveEmptyFolderRecord(CharacterProfile profile, string folder)
    {
        profile.FavoriteFolders?.RemoveAll(e =>
            string.Equals(NavFolders.Normalize(e), folder, StringComparison.OrdinalIgnoreCase));
    }

    private void RebaseEmptyFolders(CharacterProfile profile, string from, string to)
    {
        _emptyFolders.Clear();
        if (profile.FavoriteFolders is not { } empties) return;
        for (int i = 0; i < empties.Count; i++)
        {
            string norm = NavFolders.Normalize(empties[i]);
            if (NavFolders.IsSelfOrDescendant(from, norm))
                norm = NavFolders.Rebase(from, to, norm);
            empties[i] = norm;
            if (norm.Length != 0) _emptyFolders.Add(norm);
        }
    }

    private void OnProfileLoaded(CharacterProfile profile)
    {
        _favorites.Clear();
        _emptyFolders.Clear();
        if (profile.Favorites is { } list)
        {
            foreach (FavoriteRoom f in list)
                _favorites[new RoomKey(f.Map, f.Room)] = f;
        }
        if (profile.FavoriteFolders is { } folders)
        {
            foreach (string e in folders)
            {
                string norm = NavFolders.Normalize(e);
                if (norm.Length != 0) _emptyFolders.Add(norm);
            }
        }
        Changed?.Invoke();
    }

    private void OnProfileClosed()
    {
        bool had = _favorites.Count > 0 || _emptyFolders.Count > 0;
        _favorites.Clear();
        _emptyFolders.Clear();
        if (had) Changed?.Invoke();
    }
}
