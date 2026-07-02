using System.Text.Json;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="AutoLightSettings"/> default values + JSON round-trip. Defaults
/// shape what a fresh character sees in the Auto-Light tab on first open;
/// round-trip protects against schema drift (JsonSerializer is name-keyed). The
/// section VM itself does I/O through the <see cref="Services.AppServices"/>
/// singleton, so — as with the other section tests — this pins the persistable
/// shape rather than driving the VM.
/// </summary>
public sealed class AutoLightSettingsTests
{
    [Fact]
    public void Defaults_AreSane()
    {
        AutoLightSettings dto = new();

        Assert.Equal(6, dto.CarryHours);
        Assert.Equal(60, dto.ReorderThresholdMinutes);
        Assert.Null(dto.PreferredLightName);   // null = engine auto-picks per route
    }

    [Fact]
    public void RoundTripsThroughJson()
    {
        AutoLightSettings src = new()
        {
            CarryHours = 12,
            ReorderThresholdMinutes = 90,
            PreferredLightName = "lantern",
        };

        string json = JsonSerializer.Serialize(src);
        AutoLightSettings? back = JsonSerializer.Deserialize<AutoLightSettings>(json);

        Assert.NotNull(back);
        Assert.Equal(12, back!.CarryHours);
        Assert.Equal(90, back.ReorderThresholdMinutes);
        Assert.Equal("lantern", back.PreferredLightName);
    }

    [Fact]
    public void NullPreferredLight_SurvivesRoundTrip()
    {
        // Auto-pick is the default and must persist as an absent/null name, not
        // an empty string the engine would try to resolve as a light.
        AutoLightSettings src = new() { PreferredLightName = null };

        string json = JsonSerializer.Serialize(src);
        AutoLightSettings? back = JsonSerializer.Deserialize<AutoLightSettings>(json);

        Assert.Null(back!.PreferredLightName);
    }
}
