using System;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The per-character favourites store: the quick-access star (toggle, the
// 10-favourite write-side cap, surviving a reload) and the list a profile takes the
// first time it loads. Scratch set and BBS names under the test data root, removed
// in Dispose.
public sealed class FavoritesStoreTests : IDisposable
{
    private readonly string _setName;
    private readonly string _bbs;

    public FavoritesStoreTests()
    {
        string id = Guid.NewGuid().ToString("N").Substring(0, 12);
        _setName = "test-favstore-" + id;
        _bbs = "test-favbbs-" + id;
        Directory.CreateDirectory(Path.Combine(AppPaths.GameDataRoot, _setName));
    }

    public void Dispose()
    {
        foreach (string folder in new[] { Path.Combine(AppPaths.GameDataRoot, _setName), AppPaths.BbsFolder(_bbs) })
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    // A store over a blank in-memory profile, with no game data to take a list from.
    private readonly ProfileService _blank = NewBlankProfile();

    private static ProfileService NewBlankProfile()
    {
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        return profile;
    }

    private FavoritesStore NewStore() => new(_blank, new GameDataCache(), () => null);

    // The list every character on the set used to share.
    private void WriteSharedList(params (int Map, int Room, string Label)[] rooms) =>
        JsonStore.Save(AppPaths.GameDataSetFavoritesFile(_setName), new
        {
            Favorites = rooms.Select(r => new FavoriteRoom { Map = r.Map, Room = r.Room, Label = r.Label }).ToList(),
        });

    // A character saved by a version from before favourites were per character.
    private void WriteOlderCharacter(string name)
    {
        Directory.CreateDirectory(AppPaths.ProfileFolder(_bbs, name));
        File.WriteAllText(AppPaths.CharacterProfileFile(_bbs, name),
            $$"""{ "Name": "{{name}}", "SchemaVersion": 7 }""");
    }

    // The starters the program ships for the set's realm, stood in for by a file in
    // the scratch set.
    private string StarterFile => Path.Combine(AppPaths.GameDataRoot, _setName, "bundled-starters.json");

    private void WriteStarters(params (int Map, int Room, string Label, string? Folder)[] rooms) =>
        JsonStore.Save(StarterFile, new
        {
            Favorites = rooms.Select(r => new FavoriteRoom { Map = r.Map, Room = r.Room, Label = r.Label, Folder = r.Folder }).ToList(),
        });

    private (ProfileService Profile, FavoritesStore Store) LoadCharacter(string name)
    {
        ProfileService profile = TestProfiles.Service();
        FavoritesStore store = new(profile, new GameDataCache(), () => _setName, starterFile: _ => StarterFile);
        profile.Load(_bbs, name);
        return (profile, store);
    }

    // Report paradigm-20261006-220135: favourites were one list per game-data set, so
    // a character's list was whatever every other character on that data had put in
    // it. Each character from then takes a copy and keeps its own from there.
    [Fact]
    public void OlderCharacter_TakesACopyOfTheSharedList_ThenKeepsItsOwn()
    {
        WriteSharedList((1, 45, "Bank"));
        WriteOlderCharacter("Alpha");
        WriteOlderCharacter("Beta");

        (_, FavoritesStore alpha) = LoadCharacter("Alpha");
        (_, FavoritesStore beta) = LoadCharacter("Beta");
        Assert.True(alpha.IsFavorite(new RoomKey(1, 45)));
        Assert.True(beta.IsFavorite(new RoomKey(1, 45)));

        alpha.Add(new RoomKey(2, 10), "Trainer");
        beta.Remove(new RoomKey(1, 45));

        // Reloaded from their own files: neither sees the other's change.
        (_, FavoritesStore alphaAgain) = LoadCharacter("Alpha");
        (_, FavoritesStore betaAgain) = LoadCharacter("Beta");
        Assert.True(alphaAgain.IsFavorite(new RoomKey(1, 45)));
        Assert.True(alphaAgain.IsFavorite(new RoomKey(2, 10)));
        Assert.False(betaAgain.IsFavorite(new RoomKey(1, 45)));
        Assert.False(betaAgain.IsFavorite(new RoomKey(2, 10)));
    }

    // A character made now starts with the starters the program ships for its realm
    // (user, 2026-10-12), not with whatever the characters before it had collected
    // in the shared list. From 2026-10-06 it started with none, and a new player's
    // GOTO pane came up empty.
    [Fact]
    public void NewCharacter_StartsWithTheBundledStarters_NotTheSharedList()
    {
        WriteSharedList((99, 9999, "someone else's"));
        WriteStarters((1, 45, "Bank", "Silvermere"), (2, 10, "Trainer", null));
        TestProfiles.Service().CreateProfile(_bbs, "Fresh");

        (ProfileService profile, FavoritesStore store) = LoadCharacter("Fresh");

        Assert.Equal(2, store.All.Count);
        Assert.True(store.IsFavorite(new RoomKey(1, 45)));
        Assert.Equal("Silvermere", store.FolderOf(new RoomKey(1, 45)));
        Assert.False(store.IsFavorite(new RoomKey(99, 9999)));
        Assert.NotNull(profile.Current!.Favorites);   // it has its own list now
    }

    // The default profile takes them too, so a character saved from it has them.
    [Fact]
    public void DefaultProfile_StartsWithTheBundledStarters()
    {
        WriteStarters((1, 45, "Bank", null));
        ProfileService profile = TestProfiles.Service();
        FavoritesStore store = new(profile, new GameDataCache(), () => _setName, starterFile: _ => StarterFile);

        profile.LoadDefaultProfile();

        Assert.True(store.IsFavorite(new RoomKey(1, 45)));
    }

    // "Restore defaults": every starter the character has no favourite for comes
    // back, one it deleted included. A room it bookmarked under its own label stays
    // as it made it, and nothing of its own is removed.
    [Fact]
    public void AddStarters_AddsOnlyTheRoomsTheCharacterLacks_AndLeavesItsOwnAlone()
    {
        WriteStarters((1, 45, "Bank", "Silvermere"), (2, 10, "Trainer", null), (3, 7, "Healer", null));
        TestProfiles.Service().CreateProfile(_bbs, "Pruner");
        (_, FavoritesStore store) = LoadCharacter("Pruner");
        store.Remove(new RoomKey(2, 10));                        // a starter it deleted
        store.Rename(new RoomKey(3, 7), "my healer");            // a starter it relabelled
        store.Add(new RoomKey(50, 500), "my own");
        Assert.Equal(1, store.MissingStarterCount);

        Assert.Equal(1, store.AddStarters());

        Assert.True(store.IsFavorite(new RoomKey(2, 10)));
        Assert.Equal("my healer", store.All.Single(f => f.Map == 3 && f.Room == 7).Label);
        Assert.True(store.IsFavorite(new RoomKey(50, 500)));
        Assert.Equal(4, store.All.Count);
        Assert.Equal(0, store.MissingStarterCount);
        Assert.Equal(0, store.AddStarters());                    // nothing left to add

        // And it is saved: the character reloads with them.
        (_, FavoritesStore again) = LoadCharacter("Pruner");
        Assert.Equal(4, again.All.Count);
    }

    // A character that was saved with an empty list while new characters started
    // with none: the button is how it gets the starters.
    [Fact]
    public void AddStarters_FillsACharacterSavedWithAnEmptyList()
    {
        WriteStarters((1, 45, "Bank", null), (2, 10, "Trainer", null));
        Directory.CreateDirectory(AppPaths.ProfileFolder(_bbs, "Empty"));
        TestProfiles.Service().CreateProfile(_bbs, "Empty", p => p.Favorites = new());
        (_, FavoritesStore store) = LoadCharacter("Empty");
        Assert.Empty(store.All);                                 // an empty list is a list: not seeded again

        Assert.Equal(2, store.AddStarters());
        Assert.Equal(2, store.All.Count);
    }

    // With no game data there is nothing to copy from yet: the list is left untaken
    // and picked up when a set arrives.
    [Fact]
    public void ProfileLoadedBeforeAnyGameData_TakesItsListWhenASetArrives()
    {
        WriteSharedList((1, 45, "Bank"));
        WriteOlderCharacter("Early");
        ProfileService profile = TestProfiles.Service();
        GameDataCache cache = new();
        string? set = null;
        FavoritesStore store = new(profile, cache, () => set);
        profile.Load(_bbs, "Early");
        Assert.Null(profile.Current!.Favorites);

        set = _setName;
        cache.SwitchSet(_setName);

        Assert.True(store.IsFavorite(new RoomKey(1, 45)));
    }

    [Fact]
    public void SetStarred_TogglesFlagAndCount()
    {
        FavoritesStore store = NewStore();
        RoomKey key = new(1, 45);
        store.Add(key, "Bank");

        Assert.False(store.IsStarred(key));
        Assert.Equal(0, store.StarredCount);

        Assert.True(store.SetStarred(key, true));
        Assert.True(store.IsStarred(key));
        Assert.Equal(1, store.StarredCount);
        Assert.Contains(store.StarredFavorites(), f => f.Map == 1 && f.Room == 45);

        Assert.True(store.SetStarred(key, false));
        Assert.False(store.IsStarred(key));
        Assert.Equal(0, store.StarredCount);
    }

    [Fact]
    public void SetStarred_UnknownKey_ReturnsFalse()
    {
        FavoritesStore store = NewStore();
        Assert.False(store.SetStarred(new RoomKey(9, 9), true));
    }

    [Fact]
    public void SetStarred_EleventhStar_BlockedAtCap()
    {
        FavoritesStore store = NewStore();
        for (int i = 1; i <= FavoritesStore.MaxStarred + 1; i++)
            store.Add(new RoomKey(1, i));

        for (int i = 1; i <= FavoritesStore.MaxStarred; i++)
            Assert.True(store.SetStarred(new RoomKey(1, i), true));

        Assert.Equal(FavoritesStore.MaxStarred, store.StarredCount);

        RoomKey overflow = new(1, FavoritesStore.MaxStarred + 1);
        Assert.False(store.SetStarred(overflow, true));   // blocked
        Assert.False(store.IsStarred(overflow));
        Assert.Equal(FavoritesStore.MaxStarred, store.StarredCount);
    }

    [Fact]
    public void Starred_SurvivesReload()
    {
        RoomKey key = new(2, 297);
        FavoritesStore store = NewStore();
        store.Add(key, "Bank of Godfrey");
        Assert.True(store.SetStarred(key, true));

        // A fresh store over the same profile reads the list back off it.
        FavoritesStore reloaded = NewStore();
        Assert.True(reloaded.IsStarred(key));
    }
}
