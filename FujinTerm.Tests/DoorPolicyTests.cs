using FujinTerm.Game.Map;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Decision-matrix coverage for <see cref="DoorPolicy"/> — what verb
/// the walker tries first, and whether the door is viable at all
/// given live player stats.
/// </summary>
public sealed class DoorPolicyTests
{
    // ----- IsAchievable -----------------------------------------------

    [Fact]
    public void IsAchievable_PlainDoorNoReq_TrueWhenBashable()
    {
        Assert.True(DoorPolicy.IsAchievable(statRequirement: 0, canBash: true,
            playerStrength: 0, playerPicklocks: 0));
    }

    [Fact]
    public void IsAchievable_StatGated_TrueWhenStrengthMet()
    {
        Assert.True(DoorPolicy.IsAchievable(statRequirement: 50, canBash: true,
            playerStrength: 50, playerPicklocks: 0));
    }

    [Fact]
    public void IsAchievable_StatGated_TrueWhenPicklocksMet()
    {
        Assert.True(DoorPolicy.IsAchievable(statRequirement: 50, canBash: false,
            playerStrength: 0, playerPicklocks: 50));
    }

    [Fact]
    public void IsAchievable_StatGated_FalseWhenNeitherMet()
    {
        Assert.False(DoorPolicy.IsAchievable(statRequirement: 50, canBash: true,
            playerStrength: 10, playerPicklocks: 10));
    }

    [Fact]
    public void IsAchievable_AboveUnbashableThreshold_FallsBackToPickOnly()
    {
        // 250 > 200 threshold; bash is gated even if player strength
        // technically meets it. Pick still works.
        Assert.True(DoorPolicy.IsAchievable(statRequirement: 250, canBash: true,
            playerStrength: 500, playerPicklocks: 300));
    }

    [Fact]
    public void IsAchievable_ImpossiblyHighStrength_FalseWhenPickAlsoFails()
    {
        // 1000-req door — pick or bash both unviable for normal builds.
        Assert.False(DoorPolicy.IsAchievable(statRequirement: 1000, canBash: true,
            playerStrength: 100, playerPicklocks: 100));
    }

    [Fact]
    public void IsAchievable_DynamicCeiling_BashableAboveDefaultButBelowSetMax()
    {
        // A realm whose gear lifts the ceiling to 280 lets a 250-req door be bashed —
        // it was unbashable under the 200 default.
        Assert.True(DoorPolicy.IsAchievable(statRequirement: 250, canBash: true,
            playerStrength: 260, playerPicklocks: 0, maxBashableStrength: 280));
        Assert.False(DoorPolicy.IsAchievable(statRequirement: 250, canBash: true,
            playerStrength: 260, playerPicklocks: 0, maxBashableStrength: 200));
    }

    [Fact]
    public void ChooseVerb_DynamicCeiling_PicksBashWhenSetMaxAllows()
    {
        // 250-req door with a 280 ceiling: bash is on the table (player meets it).
        Assert.Equal("bash", DoorPolicy.ChooseVerb(250, canBash: true,
            playerStrength: 260, playerPicklocks: 260, preferPickOverBash: false,
            maxBashableStrength: 280));
        // Same door under the 200 default: bash barred, walker must pick.
        Assert.Equal("pick", DoorPolicy.ChooseVerb(250, canBash: true,
            playerStrength: 260, playerPicklocks: 260, preferPickOverBash: false,
            maxBashableStrength: 200));
    }

    // ----- ChooseVerb -------------------------------------------------

    [Fact]
    public void ChooseVerb_DefaultPreference_PicksBashWhenViable()
    {
        Assert.Equal("bash", DoorPolicy.ChooseVerb(0, canBash: true,
            playerStrength: 50, playerPicklocks: 50, preferPickOverBash: false));
    }

    [Fact]
    public void ChooseVerb_PicklockPreference_PicksPickWhenViable()
    {
        Assert.Equal("pick", DoorPolicy.ChooseVerb(0, canBash: true,
            playerStrength: 50, playerPicklocks: 50, preferPickOverBash: true));
    }

    [Fact]
    public void ChooseVerb_BashOnly_NoMatterPreference_PicksBash()
    {
        // canBash=true but picklocks=0 and stat req is 50.
        Assert.Equal("bash", DoorPolicy.ChooseVerb(50, canBash: true,
            playerStrength: 100, playerPicklocks: 0, preferPickOverBash: true));
    }

    [Fact]
    public void ChooseVerb_PickOnly_DoorNotBashable_PicksPick()
    {
        Assert.Equal("pick", DoorPolicy.ChooseVerb(50, canBash: false,
            playerStrength: 100, playerPicklocks: 50, preferPickOverBash: false));
    }

    [Fact]
    public void ChooseVerb_NeitherViable_ReturnsNull()
    {
        Assert.Null(DoorPolicy.ChooseVerb(100, canBash: true,
            playerStrength: 10, playerPicklocks: 10, preferPickOverBash: false));
    }

    [Fact]
    public void ChooseVerb_AboveUnbashableThreshold_NeverPicksBash()
    {
        // 250 > UnbashableStrengthThreshold; player has all the stats.
        // Walker must pick, not bash.
        Assert.Equal("pick", DoorPolicy.ChooseVerb(250, canBash: true,
            playerStrength: 999, playerPicklocks: 999, preferPickOverBash: false));
    }
}
