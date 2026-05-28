using System.Text.Json;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

public sealed class DisplaySettingsTests
{
    [Fact]
    public void Defaults_MatchTerminalConstants()
    {
        DisplaySettings dto = new();
        Assert.Equal(16.0,    dto.FontSize);
        Assert.Equal(10_000,  dto.ScrollbackLines);
    }

    [Fact]
    public void RoundTripJson_PreservesFields()
    {
        DisplaySettings original = new() { FontSize = 13.5, ScrollbackLines = 25_000 };
        string json = JsonSerializer.Serialize(original);
        DisplaySettings? round = JsonSerializer.Deserialize<DisplaySettings>(json);

        Assert.NotNull(round);
        Assert.Equal(original.FontSize,        round!.FontSize);
        Assert.Equal(original.ScrollbackLines, round.ScrollbackLines);
    }

    [Fact]
    public void PartialJson_FillsMissingFieldsWithDefaults()
    {
        // User trims their profile by hand — missing fields fall back to
        // type defaults without throwing.
        const string partial = """ { "FontSize": 20 } """;
        DisplaySettings? dto = JsonSerializer.Deserialize<DisplaySettings>(partial);

        Assert.NotNull(dto);
        Assert.Equal(20.0,   dto!.FontSize);
        Assert.Equal(10_000, dto.ScrollbackLines);
    }
}
