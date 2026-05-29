using System.Text.Json;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

public sealed class StatlineSettingsTests
{
    [Fact]
    public void Defaults_NullCommand()
    {
        StatlineSettings dto = new();
        Assert.Null(dto.Command);
    }

    [Fact]
    public void RoundTripJson_PreservesFullCustomString()
    {
        StatlineSettings original = new() { Command = "full custom [HP=%h,MA=%m]: %r" };
        string json = JsonSerializer.Serialize(original);
        StatlineSettings? round = JsonSerializer.Deserialize<StatlineSettings>(json);
        Assert.NotNull(round);
        Assert.Equal(original.Command, round!.Command);
    }

    [Fact]
    public void RoundTripJson_PreservesRawWildcard()
    {
        StatlineSettings original = new() { Command = "[HP=%h]: %r" };
        string json = JsonSerializer.Serialize(original);
        StatlineSettings? round = JsonSerializer.Deserialize<StatlineSettings>(json);
        Assert.NotNull(round);
        Assert.Equal(original.Command, round!.Command);
    }

    [Fact]
    public void RoundTripJson_NullSurvives()
    {
        StatlineSettings original = new() { Command = null };
        string json = JsonSerializer.Serialize(original);
        StatlineSettings? round = JsonSerializer.Deserialize<StatlineSettings>(json);
        Assert.NotNull(round);
        Assert.Null(round!.Command);
    }
}
