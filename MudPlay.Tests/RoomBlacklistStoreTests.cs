using System.IO;
using System.Text.Json;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// RoomBlacklistStore: in-memory Add/Remove/ReplaceAll semantics,
// Changed-event firing rules, and a single round-trip through disk via
// OnRealmChanged to exercise the load/persist path.
public sealed class RoomBlacklistStoreTests : IDisposable
{
    // The realm folder the store is pointed at: a per-test scratch folder.
    private readonly string _realm = Path.Combine(Path.GetTempPath(), "rbl-test-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_realm)) Directory.Delete(_realm, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // ----- in-memory behaviour (no realm, no disk I/O) ---------------

    [Fact]
    public void IsBlacklisted_FalseForUnknownKey()
    {
        RoomBlacklistStore store = new();
        Assert.False(store.IsBlacklisted(new RoomKey(1, 1)));
    }

    [Fact]
    public void Add_FiresChanged_AndMarksRoomBlacklisted()
    {
        RoomBlacklistStore store = new();
        int fires = 0;
        store.Changed += () => fires++;

        store.Add(new RoomKey(15, 200), "Ganghouse");

        Assert.Equal(1, fires);
        Assert.True(store.IsBlacklisted(new RoomKey(15, 200)));
        Assert.Contains(new RoomKey(15, 200), store.Blacklisted);
        Assert.Single(store.Entries);
        Assert.Equal("Ganghouse", store.Entries[0].Name);
    }

    [Fact]
    public void Add_Idempotent_SameName_DoesNotRefireChanged()
    {
        RoomBlacklistStore store = new();
        store.Add(new RoomKey(15, 200), "Ganghouse");
        int fires = 0;
        store.Changed += () => fires++;

        store.Add(new RoomKey(15, 200), "Ganghouse");

        Assert.Equal(0, fires);
        Assert.Single(store.Entries);
    }

    [Fact]
    public void Add_SameKey_NewName_UpdatesNameAndFires()
    {
        RoomBlacklistStore store = new();
        store.Add(new RoomKey(15, 200), "old");
        int fires = 0;
        store.Changed += () => fires++;

        store.Add(new RoomKey(15, 200), "new");

        Assert.Equal(1, fires);
        Assert.Single(store.Entries);
        Assert.Equal("new", store.Entries[0].Name);
    }

    [Fact]
    public void ReplaceAll_SwapsContents_AndFiresChangedOnce()
    {
        RoomBlacklistStore store = new();
        store.Add(new RoomKey(15, 1), "a");
        store.Add(new RoomKey(15, 2), "b");
        int fires = 0;
        store.Changed += () => fires++;

        store.ReplaceAll(new[]
        {
            new BlacklistedRoom(15, 3, "c"),
            new BlacklistedRoom(15, 4, "d"),
        });

        Assert.Equal(1, fires);
        Assert.False(store.IsBlacklisted(new RoomKey(15, 1)));
        Assert.True(store.IsBlacklisted(new RoomKey(15, 3)));
        Assert.True(store.IsBlacklisted(new RoomKey(15, 4)));
        Assert.Equal(2, store.Entries.Count);
    }

    [Fact]
    public void Persist_NoOps_WhenNoRealm()
    {
        // Without OnRealmChanged there is no realm folder, so an Add changes
        // memory only: a store later pointed at a realm reads nothing of it.
        RoomBlacklistStore store = new();
        store.Add(new RoomKey(15, 200), "Ganghouse");

        RoomBlacklistStore later = new();
        later.OnRealmChanged(_realm);

        Assert.False(later.IsBlacklisted(new RoomKey(15, 200)));
        Assert.False(File.Exists(AppPaths.RealmRoomBlacklistFile(_realm)));
    }

    // ----- round trip through disk -----------------------------------

    [Fact]
    public void OnRealmChanged_LoadsExistingFile()
    {
        // Pre-seed the realm folder so the load path has something to read.
        string folder = _realm;
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "room_blacklist.json"),
            JsonSerializer.Serialize(new[]
            {
                new BlacklistedRoom(15, 200, "Ganghouse"),
                new BlacklistedRoom(15, 201, "Vault"),
            }));

        RoomBlacklistStore store = new();
        store.OnRealmChanged(_realm);

        Assert.True(store.IsBlacklisted(new RoomKey(15, 200)));
        Assert.True(store.IsBlacklisted(new RoomKey(15, 201)));
    }

    [Fact]
    public void Add_AfterRealmChange_PersistsToTheRealmFolder()
    {
        RoomBlacklistStore store = new();
        store.OnRealmChanged(_realm);
        store.Add(new RoomKey(15, 200), "Ganghouse");

        string path = AppPaths.RealmRoomBlacklistFile(_realm);
        Assert.True(File.Exists(path));
        string raw = File.ReadAllText(path);
        var list = JsonSerializer.Deserialize<List<BlacklistedRoom>>(raw);
        Assert.NotNull(list);
        Assert.Single(list!);
        Assert.Equal(200, list![0].Room);
        Assert.Equal("Ganghouse", list[0].Name);
    }

    [Fact]
    public void OnRealmChanged_ClearsStore_WhenRealmFolderBlank()
    {
        RoomBlacklistStore store = new();
        store.OnRealmChanged(_realm);
        store.Add(new RoomKey(15, 1), "x");
        Assert.True(store.IsBlacklisted(new RoomKey(15, 1)));

        // Leaving the realm clears in-memory state.
        int fires = 0;
        store.Changed += () => fires++;
        store.OnRealmChanged(null);

        Assert.False(store.IsBlacklisted(new RoomKey(15, 1)));
        Assert.Equal(1, fires);
    }

    [Fact]
    public void OnRealmChanged_MalformedJson_LeavesStoreEmpty()
    {
        string folder = _realm;
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "room_blacklist.json"),
            "{ not valid json ]");

        RoomBlacklistStore store = new();
        store.OnRealmChanged(_realm);

        Assert.Empty(store.Blacklisted);
    }

    // ----- CannotBeReached flag --------------------------------------

    [Fact]
    public void IsUnreachable_OnlyTrueForFlaggedEntries()
    {
        RoomBlacklistStore store = new();
        store.ReplaceAll(new[]
        {
            new BlacklistedRoom(7, 1, "orphan dev room", cannotBeReached: true),
            new BlacklistedRoom(7, 2, "just decluttered"),   // plain blacklist, reachable
        });

        // Flagged room is both blacklisted and unreachable.
        Assert.True(store.IsBlacklisted(new RoomKey(7, 1)));
        Assert.True(store.IsUnreachable(new RoomKey(7, 1)));

        // Plain-blacklisted room is NOT unreachable — the flag is opt-in.
        Assert.True(store.IsBlacklisted(new RoomKey(7, 2)));
        Assert.False(store.IsUnreachable(new RoomKey(7, 2)));

        // Unknown key is neither.
        Assert.False(store.IsUnreachable(new RoomKey(7, 9)));
    }

    [Fact]
    public void CannotBeReached_SurvivesDiskRoundTrip()
    {
        RoomBlacklistStore store = new();
        store.OnRealmChanged(_realm);
        store.ReplaceAll(new[]
        {
            new BlacklistedRoom(7, 1, "orphan dev room", cannotBeReached: true),
            new BlacklistedRoom(7, 2, "just decluttered"),
        });

        // Reload from disk into a fresh store — the flag must persist.
        RoomBlacklistStore reloaded = new();
        reloaded.OnRealmChanged(_realm);

        Assert.True(reloaded.IsUnreachable(new RoomKey(7, 1)));
        Assert.False(reloaded.IsUnreachable(new RoomKey(7, 2)));
        Assert.True(reloaded.IsBlacklisted(new RoomKey(7, 2)));
    }
}
