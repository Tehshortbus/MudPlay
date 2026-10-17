using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// HealthSettings default values: what a fresh character sees in the Health tab
// on first open.
public sealed class HealthSettingsTests
{
    [Fact]
    public void Defaults_AreSane()
    {
        HealthSettings dto = new();

        Assert.Equal(ThresholdMode.Percentage, dto.HpThresholdMode);
        Assert.Equal(95, dto.RestMaxHp);
        Assert.Equal(60, dto.RestIfBelowHp);
        Assert.Equal(20, dto.RunIfBelowHp);
        Assert.Equal(5,  dto.HangIfBelowHp);

        Assert.Equal(80, dto.HealRestTrigger);
        Assert.Equal(70, dto.MinorHealCombatTrigger);
        Assert.Equal(40, dto.MajorHealCombatTrigger);

        Assert.Equal(ThresholdMode.Percentage, dto.MaThresholdMode);
        Assert.Equal(95, dto.RestMaxMa);
        Assert.Equal(30, dto.RestIfBelowMa);
        Assert.Equal(10, dto.RunIfBelowMa);
        Assert.Equal(70, dto.BlessIfAboveMa);

        Assert.False(dto.UseMeditateAbility);
        Assert.False(dto.MeditateBeforeResting);

        Assert.Equal(string.Empty, dto.PreRestCommand);
        Assert.Equal(string.Empty, dto.PostRestCommand);
    }
}
