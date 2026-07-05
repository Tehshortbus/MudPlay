using System.IO;
using System.Text.Json;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 7.x — RoomBlacklistStore: in-memory Add/Remove/ReplaceAll semantics,
/// Changed-event firing rules, and a single round-trip through disk via
/// OnBbsPinApplied to exercise the load/persist path.
/// </summary>
public sealed class RoomBlacklistStoreTests : IDisposable
{
    private readonly string _scratchBbs;

    public RoomBlacklistStoreTests()
    {
        // Unique per-test BBS name keeps Persist/Load isolated under
        // the shared AppPaths.DataRoot without colliding with real
        // user data.
        _scratchBbs = "rbl-test-" + Path.GetRandomFileName();
    }

    public void Dispose()
    {
        try
        {
            string folder = AppPaths.BbsFolder(_scratchBbs);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // ----- in-memory behaviour (no BBS pin, no disk I/O) -------------

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
    public void Remove_FiresChanged_OnlyForRealRemoval()
    {
        RoomBlacklistStore store = new();
        store.Add(new RoomKey(15, 200), "Ganghouse");
        int fires = 0;
        store.Changed += () => fires++;

        Assert.True(store.Remove(new RoomKey(15, 200)));
        Assert.Equal(1, fires);
        Assert.False(store.IsBlacklisted(new RoomKey(15, 200)));

        // Removing an absent key is a no-op.
        Assert.False(store.Remove(new RoomKey(15, 200)));
        Assert.Equal(1, fires);
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
    public void Persist_NoOps_WhenNoBbsPinned()
    {
        // Without OnBbsPinApplied, _activeBbs is null → Persist is a
        // no-op. Adding still mutates in-memory state and fires Changed
        // but writes nothing to disk.
        RoomBlacklistStore store = new();
        store.Add(new RoomKey(15, 200), "Ganghouse");

        // Sanity: the scratch BBS folder is untouched (the test ctor
        // hasn't created one and we never pinned it).
        string folder = AppPaths.BbsFolder(_scratchBbs);
        Assert.False(File.Exists(Path.Combine(folder, "room_blacklist.json")));
    }

    // ----- round trip through disk -----------------------------------

    [Fact]
    public void OnBbsPinApplied_LoadsExistingFile()
    {
        // Pre-seed the BBS folder so the load path has something to read.
        string folder = AppPaths.BbsFolder(_scratchBbs);
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "room_blacklist.json"),
            JsonSerializer.Serialize(new[]
            {
                new BlacklistedRoom(15, 200, "Ganghouse"),
                new BlacklistedRoom(15, 201, "Vault"),
            }));

        RoomBlacklistStore store = new();
        store.OnBbsPinApplied(_scratchBbs);

        Assert.True(store.IsBlacklisted(new RoomKey(15, 200)));
        Assert.True(store.IsBlacklisted(new RoomKey(15, 201)));
    }

    [Fact]
    public void Add_AfterPin_PersistsToBbsFolder()
    {
        RoomBlacklistStore store = new();
        store.OnBbsPinApplied(_scratchBbs);
        store.Add(new RoomKey(15, 200), "Ganghouse");

        string path = AppPaths.BbsRoomBlacklistFile(_scratchBbs);
        Assert.True(File.Exists(path));
        string raw = File.ReadAllText(path);
        var list = JsonSerializer.Deserialize<List<BlacklistedRoom>>(raw);
        Assert.NotNull(list);
        Assert.Single(list!);
        Assert.Equal(200, list![0].Room);
        Assert.Equal("Ganghouse", list[0].Name);
    }

    [Fact]
    public void OnBbsPinApplied_ClearsStore_WhenBbsNameBlank()
    {
        RoomBlacklistStore store = new();
        store.OnBbsPinApplied(_scratchBbs);
        store.Add(new RoomKey(15, 1), "x");
        Assert.True(store.IsBlacklisted(new RoomKey(15, 1)));

        // Unpinning clears in-memory state — the new profile is fresh.
        int fires = 0;
        store.Changed += () => fires++;
        store.OnBbsPinApplied(null);

        Assert.False(store.IsBlacklisted(new RoomKey(15, 1)));
        Assert.Equal(1, fires);
    }

    [Fact]
    public void OnBbsPinApplied_MalformedJson_LeavesStoreEmpty()
    {
        string folder = AppPaths.BbsFolder(_scratchBbs);
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "room_blacklist.json"),
            "{ not valid json ]");

        RoomBlacklistStore store = new();
        store.OnBbsPinApplied(_scratchBbs);

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
        store.OnBbsPinApplied(_scratchBbs);
        store.ReplaceAll(new[]
        {
            new BlacklistedRoom(7, 1, "orphan dev room", cannotBeReached: true),
            new BlacklistedRoom(7, 2, "just decluttered"),
        });

        // Reload from disk into a fresh store — the flag must persist.
        RoomBlacklistStore reloaded = new();
        reloaded.OnBbsPinApplied(_scratchBbs);

        Assert.True(reloaded.IsUnreachable(new RoomKey(7, 1)));
        Assert.False(reloaded.IsUnreachable(new RoomKey(7, 2)));
        Assert.True(reloaded.IsBlacklisted(new RoomKey(7, 2)));
    }
}
