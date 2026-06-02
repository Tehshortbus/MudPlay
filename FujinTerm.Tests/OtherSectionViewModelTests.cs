using System.Text.Json;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Coverage for the persistable shape of <see cref="OtherSettings"/>.
/// The engine-side effect of the suicide-threshold knob is covered in
/// <see cref="RemoteCommandManagerTests"/>; this file pins the DTO
/// schema + defaults so a JSON-format regression fails loudly.
/// </summary>
public sealed class OtherSectionViewModelTests
{
    [Fact]
    public void OtherSettings_RoundTripsThroughJson()
    {
        OtherSettings src = new()
        {
            MaxSuicideLivesThreshold = 7,
        };

        string json = JsonSerializer.Serialize(src);
        OtherSettings? back = JsonSerializer.Deserialize<OtherSettings>(json);

        Assert.NotNull(back);
        Assert.Equal(7, back!.MaxSuicideLivesThreshold);
    }

    [Fact]
    public void OtherSettings_Default_MatchesPhase6Spec()
    {
        // Phase 6 spec: block @do suicide / @party suicide at lives ≤ 3.
        // A freshly-loaded profile with no Other entry falls through to
        // this value for the engine, so it has to be stable.
        OtherSettings dto = new();
        Assert.Equal(3, dto.MaxSuicideLivesThreshold);
    }
}
