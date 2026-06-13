using System.Text.Json;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 9.0a sub-A — <see cref="HealthSettings"/> default values + JSON
/// round-trip. Defaults shape what a fresh character sees in the Health tab
/// on first open; round-trip protects against schema drift when fields are
/// reordered or renamed (`JsonSerializer` is property-name-keyed).
/// </summary>
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

    [Fact]
    public void Defaults_AreOrderedFromCriticalToSafe()
    {
        // The four HP thresholds form a flee-cascade. A fresh character
        // shouldn't accidentally hang up before fleeing or flee before
        // resting — defaults must keep the strict ordering, even though
        // the engine doesn't enforce it (user can intentionally invert
        // for testing). This test fails if a future field reorder mangles
        // a default constant.
        HealthSettings dto = new();
        Assert.True(dto.RestMaxHp        > dto.RestIfBelowHp);
        Assert.True(dto.RestIfBelowHp    > dto.RunIfBelowHp);
        Assert.True(dto.RunIfBelowHp     > dto.HangIfBelowHp);
        Assert.True(dto.RestMaxMa        > dto.RestIfBelowMa);
        Assert.True(dto.RestIfBelowMa    > dto.RunIfBelowMa);
    }

    [Fact]
    public void JsonRoundTrip_PreservesEveryField()
    {
        HealthSettings dto = new()
        {
            HpThresholdMode        = ThresholdMode.Absolute,
            RestMaxHp              = 200,
            RestIfBelowHp          = 150,
            RunIfBelowHp           = 40,
            HangIfBelowHp          = 10,
            HealRestTrigger        = 175,
            MinorHealCombatTrigger = 120,
            MajorHealCombatTrigger = 75,
            MaThresholdMode        = ThresholdMode.Absolute,
            RestMaxMa              = 300,
            RestIfBelowMa          = 80,
            RunIfBelowMa           = 25,
            BlessIfAboveMa         = 220,
            UseMeditateAbility     = false,
            MeditateBeforeResting  = true,
            PreRestCommand         = "peer^M;look",
            PostRestCommand        = "stand^M;look",
        };

        string json = JsonSerializer.Serialize(dto);
        HealthSettings? round = JsonSerializer.Deserialize<HealthSettings>(json);

        Assert.NotNull(round);
        Assert.Equal(dto.HpThresholdMode,        round!.HpThresholdMode);
        Assert.Equal(dto.RestMaxHp,              round.RestMaxHp);
        Assert.Equal(dto.RestIfBelowHp,          round.RestIfBelowHp);
        Assert.Equal(dto.RunIfBelowHp,           round.RunIfBelowHp);
        Assert.Equal(dto.HangIfBelowHp,          round.HangIfBelowHp);
        Assert.Equal(dto.HealRestTrigger,        round.HealRestTrigger);
        Assert.Equal(dto.MinorHealCombatTrigger, round.MinorHealCombatTrigger);
        Assert.Equal(dto.MajorHealCombatTrigger, round.MajorHealCombatTrigger);
        Assert.Equal(dto.MaThresholdMode,        round.MaThresholdMode);
        Assert.Equal(dto.RestMaxMa,              round.RestMaxMa);
        Assert.Equal(dto.RestIfBelowMa,          round.RestIfBelowMa);
        Assert.Equal(dto.RunIfBelowMa,           round.RunIfBelowMa);
        Assert.Equal(dto.BlessIfAboveMa,         round.BlessIfAboveMa);
        Assert.Equal(dto.UseMeditateAbility,     round.UseMeditateAbility);
        Assert.Equal(dto.MeditateBeforeResting,  round.MeditateBeforeResting);
        Assert.Equal(dto.PreRestCommand,         round.PreRestCommand);
        Assert.Equal(dto.PostRestCommand,        round.PostRestCommand);
    }
}
