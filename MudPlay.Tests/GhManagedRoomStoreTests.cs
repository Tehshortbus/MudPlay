using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// GhManagedRoomStore — the PER-CHARACTER set of gang-house rooms this character
// actively sweeps. Room labels are shared per-BBS, but which subset a character
// manages is per-character, so alts in different houses on one BBS pick their own.
// Presence in the set = managed; hydrates from CharacterProfile.GhManagedRooms.
public sealed class GhManagedRoomStoreTests
{
    private static ProfileService BlankProfile()
    {
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();   // Current set; Save() no-ops (no name/BBS), so purely in-memory
        return profile;
    }

    [Fact]
    public void SetManaged_On_MarksRoomAndWritesProfileAsMapRoom()
    {
        ProfileService profile = BlankProfile();
        GhManagedRoomStore store = new(profile);

        store.SetManaged(new RoomKey(1, 100), true);

        Assert.True(store.IsManaged(new RoomKey(1, 100)));
        Assert.Equal(1, store.Count);
        Assert.True(store.Any);
        Assert.Contains("1/100", profile.Current!.GhManagedRooms!);
    }

    [Fact]
    public void SetManaged_Off_RemovesRoom()
    {
        GhManagedRoomStore store = new(BlankProfile());
        store.SetManaged(new RoomKey(1, 100), true);

        store.SetManaged(new RoomKey(1, 100), false);

        Assert.False(store.IsManaged(new RoomKey(1, 100)));
        Assert.Equal(0, store.Count);
        Assert.False(store.Any);
    }

    [Fact]
    public void UnknownRoom_IsNotManaged()
    {
        GhManagedRoomStore store = new(BlankProfile());
        Assert.False(store.IsManaged(new RoomKey(9, 9)));
    }

    [Fact]
    public void Load_HydratesFromProfileManagedRooms()
    {
        ProfileService profile = BlankProfile();
        profile.Current!.GhManagedRooms = new List<string> { "1/2", "3/4" };

        GhManagedRoomStore store = new(profile);

        Assert.True(store.IsManaged(new RoomKey(1, 2)));
        Assert.True(store.IsManaged(new RoomKey(3, 4)));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void ProfileSwap_FiresChanged_AndDropsPreviousCharactersPicks()
    {
        ProfileService profile = BlankProfile();
        profile.Current!.GhManagedRooms = new List<string> { "1/2" };
        GhManagedRoomStore store = new(profile);
        Assert.True(store.IsManaged(new RoomKey(1, 2)));

        int fires = 0;
        store.Changed += () => fires++;

        // Swap characters (LoadDefaultProfile fires ProfileClosed + ProfileLoaded) — the new
        // character has its own (empty) managed set.
        profile.LoadDefaultProfile();

        Assert.True(fires >= 1);
        Assert.False(store.IsManaged(new RoomKey(1, 2)));   // previous char's pick gone
        Assert.Equal(0, store.Count);
    }
}
