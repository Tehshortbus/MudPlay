using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The manifest exists so an interrupted sweep's load doesn't become the player's
// problem. Its whole value is surviving whatever ended the sweep, so these pin
// the persistence contract rather than any decision logic.
public sealed class GhSuspendedSweepStoreTests
{
    private static GhSuspendedMove Item(string name, string from = "1/10", string to = "1/20",
                                       int count = 1, bool carried = true)
        => new(from, to, name, count, carried, hidden: false);

    [Fact]
    public void SavedLoadSurvivesIntoTheProfile()
    {
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhSuspendedSweepStore store = new(profile);

        store.Save(new[] { Item("war hammer"), Item("mace", count: 3) });

        Assert.True(store.Any);
        IReadOnlyList<GhSuspendedMove> loaded = store.Load();
        Assert.Equal(2, loaded.Count);
        Assert.Equal("war hammer", loaded[0].Item);
        Assert.Equal("1/20", loaded[0].To);
        Assert.Equal(3, loaded[1].Count);
        // Written through to the profile, which is what makes it outlive the app.
        Assert.Equal(2, profile.Current!.GhUnfinishedSweep!.Count);
    }

    [Fact]
    public void SavingAnEmptyLoadClearsAPreviousOne()
    {
        // A clean sweep must clear the record, or its load would be re-delivered
        // by every later sweep forever.
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhSuspendedSweepStore store = new(profile);
        store.Save(new[] { Item("war hammer") });

        store.Save(System.Array.Empty<GhSuspendedMove>());

        Assert.False(store.Any);
        Assert.Empty(store.Load());
        Assert.Null(profile.Current!.GhUnfinishedSweep);
    }

    [Fact]
    public void NoProfileLoaded_IsANoOp()
    {
        ProfileService profile = TestProfiles.Service();
        GhSuspendedSweepStore store = new(profile);

        store.Save(new[] { Item("war hammer") });

        Assert.False(store.Any);
        Assert.Empty(store.Load());
    }

    [Fact]
    public void LoadReturnsACopy_SoCallersCannotMutateTheStoredList()
    {
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhSuspendedSweepStore store = new(profile);
        store.Save(new[] { Item("war hammer") });

        List<GhSuspendedMove> loaded = store.Load().ToList();
        loaded.Clear();

        Assert.Single(store.Load());
    }

    [Fact]
    public void LoadCarriedIsJustTheItemsActuallyInThePack()
    {
        // A fresh Start honours the carried half (they're in the player's pack
        // either way) but not the planned half, since it re-scans anyway.
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhSuspendedSweepStore store = new(profile);

        store.Save(new[]
        {
            Item("war hammer", carried: true),
            Item("mace", carried: false),
        });

        Assert.Equal(2, store.Load().Count);
        Assert.Equal("war hammer", Assert.Single(store.LoadCarried()).Item);
    }
}
