using System.Text.Json;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

public sealed class StatlineSettingsTests
{
    [Fact]
    public void Defaults_NullWildcard()
    {
        StatlineSettings dto = new();
        Assert.Null(dto.Wildcard);
    }

    [Fact]
    public void RoundTripJson_PreservesWildcard()
    {
        StatlineSettings original = new() { Wildcard = "[HP=%h/MA=%m]: (%p) " };
        string json = JsonSerializer.Serialize(original);
        StatlineSettings? round = JsonSerializer.Deserialize<StatlineSettings>(json);
        Assert.NotNull(round);
        Assert.Equal(original.Wildcard, round!.Wildcard);
    }

    [Fact]
    public void RoundTripJson_NullSurvives()
    {
        StatlineSettings original = new() { Wildcard = null };
        string json = JsonSerializer.Serialize(original);
        StatlineSettings? round = JsonSerializer.Deserialize<StatlineSettings>(json);
        Assert.NotNull(round);
        Assert.Null(round!.Wildcard);
    }
}
