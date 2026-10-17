using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// SessionStatsLayoutStore.Resolve
// ordering + visibility resolution. The static resolver is the contract the
// Session Stats window relies on to map a saved (possibly stale) layout onto the
// live panel set, so its invariants are pinned here without spinning up a profile.
public sealed class SessionStatsLayoutTests
{
    private static readonly string[] Default =
    {
        "KillsGraph", "ExpGraph", "HpMaGraph", "PlayerStatistics", "TimeAnalysis", "SessionStatistics",
    };

    [Fact]
    public void NullLayout_YieldsDefaultOrderAllVisible()
    {
        var resolved = SessionStatsLayoutStore.Resolve(null);

        Assert.Equal(Default, resolved.Select(p => p.Id));
        Assert.All(resolved, p => Assert.True(p.Visible));
    }

    [Fact]
    public void SavedOrder_IsHonoured()
    {
        var saved = new SessionStatsLayout
        {
            Order = new List<string> { "SessionStatistics", "ExpGraph", "HpMaGraph", "KillsGraph", "TimeAnalysis", "PlayerStatistics" },
        };

        var resolved = SessionStatsLayoutStore.Resolve(saved);

        Assert.Equal(saved.Order, resolved.Select(p => p.Id));
    }

    [Fact]
    public void StaleIds_AreDropped_AndMissingKnownPanelsAppendedInDefaultOrder()
    {
        // A save that predates two panels and carries one removed id: only the
        // known ids in the saved order survive (in order), then the panels the
        // save never knew about are appended in their default position.
        var saved = new SessionStatsLayout
        {
            Order = new List<string> { "TimeAnalysis", "GhostPanel", "KillsGraph" },
        };

        var resolved = SessionStatsLayoutStore.Resolve(saved);

        Assert.Equal(
            new[] { "TimeAnalysis", "KillsGraph", "ExpGraph", "HpMaGraph", "PlayerStatistics", "SessionStatistics" },
            resolved.Select(p => p.Id));
    }

    [Fact]
    public void DuplicateSavedIds_ArePlacedOnce()
    {
        var saved = new SessionStatsLayout
        {
            Order = new List<string> { "ExpGraph", "ExpGraph", "KillsGraph" },
        };

        var resolved = SessionStatsLayoutStore.Resolve(saved);

        Assert.Equal(
            new[] { "ExpGraph", "KillsGraph", "HpMaGraph", "PlayerStatistics", "TimeAnalysis", "SessionStatistics" },
            resolved.Select(p => p.Id));
    }

    [Fact]
    public void HiddenSet_FlipsVisibility_AndStaleHiddenIdsAreIgnored()
    {
        var saved = new SessionStatsLayout
        {
            Hidden = new List<string> { "ExpGraph", "SessionStatistics", "GhostPanel" },
        };

        var resolved = SessionStatsLayoutStore.Resolve(saved);

        Assert.Equal(Default, resolved.Select(p => p.Id));
        Assert.False(resolved.Single(p => p.Id == "ExpGraph").Visible);
        Assert.False(resolved.Single(p => p.Id == "SessionStatistics").Visible);
        Assert.True(resolved.Single(p => p.Id == "KillsGraph").Visible);
        Assert.True(resolved.Single(p => p.Id == "PlayerStatistics").Visible);
        Assert.True(resolved.Single(p => p.Id == "TimeAnalysis").Visible);
    }
}
