using System.Text.Json;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

public sealed class GeneralSettingsTests
{
    [Fact]
    public void Defaults_AllAutoTogglesOn()
    {
        GeneralSettings dto = new();

        Assert.Equal(InitialTask.DoNothing, dto.DefaultTask);
        Assert.Null(dto.DefaultLoopName);
        Assert.False(dto.AutoConnect);
        AssertAllOn(dto.ManualMode);
        AssertAllOn(dto.AutoMode);
    }

    [Fact]
    public void RoundTripJson_PreservesEveryField()
    {
        GeneralSettings original = new()
        {
            DefaultTask = InitialTask.BeginLoop,
            DefaultLoopName = "Sewer farm",
            AutoConnect = true,
            ManualMode = new() { AutoCombat = true,  AutoNuke = false, AutoHealRest = true,  AutoBless = false, AutoLight = true  },
            AutoMode   = new() { AutoCombat = false, AutoNuke = true,  AutoHealRest = false, AutoBless = true,  AutoLight = false },
        };

        string json = JsonSerializer.Serialize(original);
        GeneralSettings? round = JsonSerializer.Deserialize<GeneralSettings>(json);

        Assert.NotNull(round);
        Assert.Equal(original.DefaultTask, round!.DefaultTask);
        Assert.Equal(original.DefaultLoopName, round.DefaultLoopName);
        Assert.Equal(original.AutoConnect, round.AutoConnect);
        Assert.Equal(original.ManualMode.AutoCombat,   round.ManualMode.AutoCombat);
        Assert.Equal(original.ManualMode.AutoNuke,     round.ManualMode.AutoNuke);
        Assert.Equal(original.ManualMode.AutoHealRest, round.ManualMode.AutoHealRest);
        Assert.Equal(original.ManualMode.AutoBless,    round.ManualMode.AutoBless);
        Assert.Equal(original.ManualMode.AutoLight,    round.ManualMode.AutoLight);
        Assert.Equal(original.AutoMode.AutoCombat,     round.AutoMode.AutoCombat);
        Assert.Equal(original.AutoMode.AutoNuke,       round.AutoMode.AutoNuke);
        Assert.Equal(original.AutoMode.AutoHealRest,   round.AutoMode.AutoHealRest);
        Assert.Equal(original.AutoMode.AutoBless,      round.AutoMode.AutoBless);
        Assert.Equal(original.AutoMode.AutoLight,      round.AutoMode.AutoLight);
    }

    [Fact]
    public void PartialJson_FillsMissingFieldsWithDefaults()
    {
        // User hand-edits the profile and removes some fields — we must not
        // throw, and missing fields should fall back to type defaults.
        const string partial = """
            {
              "DefaultTask": 2,
              "AutoConnect": true
            }
            """;

        GeneralSettings? dto = JsonSerializer.Deserialize<GeneralSettings>(partial);

        Assert.NotNull(dto);
        Assert.Equal(InitialTask.BeginAutoLair, dto!.DefaultTask);
        Assert.True(dto.AutoConnect);
        AssertAllOn(dto.ManualMode);     // sub-DTO defaulted from its own type defaults.
        AssertAllOn(dto.AutoMode);
    }

    private static void AssertAllOn(AutoActionDefaults d)
    {
        Assert.True(d.AutoCombat);
        Assert.True(d.AutoNuke);
        Assert.True(d.AutoHealRest);
        Assert.True(d.AutoBless);
        Assert.True(d.AutoLight);
    }
}
