using System.Collections.Generic;
using System.Collections.ObjectModel;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

/// <summary>
/// In-memory cache of the active character's
/// <see cref="Models.GameData.Favorite"/> entries plus the
/// per-active-set pre-seeded defaults (master plan: "folder
/// hierarchy + pre-seeded defaults from active set").
/// </summary>
/// <remarks>
/// PR 5.21 ships the storage spine + the Favorites tab. Loading the
/// pre-seeded defaults from <see cref="GameDataCache"/> happens when
/// the v1.11p starter bundle (PR 5.25) lands; until then the user
/// authors favourites by hand.
/// </remarks>
public sealed class FavoritesManager
{
    /// <summary>The loaded character's favourites — empty when no profile is active.</summary>
    public ObservableCollection<Favorite> Favorites { get; } = new();

    public FavoritesManager(ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.ProfileLoaded += LoadFrom;
        profile.ProfileClosed += Clear;
        if (profile.Current is { } current) LoadFrom(current);
    }

    private void LoadFrom(CharacterProfile profile)
    {
        Favorites.Clear();
        if (profile.Favorites is null) return;
        foreach (Favorite f in profile.Favorites) Favorites.Add(f);
    }

    private void Clear() => Favorites.Clear();
}
