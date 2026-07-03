using System.Text.Json;
using FujinTerm.Models.Settings;
using Xunit;

namespace FujinTerm.Tests;

public sealed class BbsProfileFieldsTests
{
    [Fact]
    public void Defaults_MatchPhase4_5Spec()
    {
        BbsProfile dto = new();

        Assert.Equal(string.Empty, dto.Name);
        Assert.Equal(string.Empty, dto.Host);
        Assert.Equal(23,           dto.Port);
        Assert.Null(dto.WebsiteUrl);

        Assert.Equal(3, dto.MaxRedials);
        Assert.Equal(5, dto.RedialPauseSeconds);
        Assert.Equal(0, dto.CleanupPeriodMinutes);
        Assert.Equal(0, dto.NoResponseTimeoutSeconds);
        Assert.False(dto.ReconnectOnFailedConnect);
        Assert.False(dto.ReconnectOnCarrierLost);
        Assert.False(dto.ReconnectOnNoResponse);
        Assert.False(dto.ReconnectAfterCleanup);

        Assert.Equal(80, dto.TerminalCols);
        Assert.Equal(25, dto.TerminalRows);
        Assert.Equal(16.0, dto.FontSize);
        Assert.Equal(4_000, dto.ScrollbackLines);

        // Game-menu commands default to MajorMUD's standard picks:
        // E to enter the realm, =x to log off from the main menu.
        Assert.Equal("E",  dto.GameEntryCommand);
        Assert.Equal("=x", dto.GameExitCommand);
    }

    [Fact]
    public void RoundTripJson_PreservesEveryField()
    {
        BbsProfile original = new()
        {
            Name = "PlayPen BBS",
            Host = "playpenbbs.com",
            Port = 2323,
            WebsiteUrl = "https://playpenbbs.com",
            MaxRedials = 5,
            RedialPauseSeconds = 10,
            CleanupPeriodMinutes = 60,
            NoResponseTimeoutSeconds = 90,
            ReconnectOnFailedConnect = true,
            ReconnectOnCarrierLost = true,
            ReconnectOnNoResponse = false,
            ReconnectAfterCleanup = true,
            TerminalCols = 132,
            TerminalRows = 50,
            FontSize = 20.0,
            ScrollbackLines = 50_000,
            GameEntryCommand = "enter",
            GameExitCommand = "bye",
        };

        string json = JsonSerializer.Serialize(original);
        BbsProfile? round = JsonSerializer.Deserialize<BbsProfile>(json);

        Assert.NotNull(round);
        Assert.Equal(original.Name,                     round!.Name);
        Assert.Equal(original.Host,                     round.Host);
        Assert.Equal(original.Port,                     round.Port);
        Assert.Equal(original.WebsiteUrl,               round.WebsiteUrl);
        Assert.Equal(original.MaxRedials,               round.MaxRedials);
        Assert.Equal(original.RedialPauseSeconds,       round.RedialPauseSeconds);
        Assert.Equal(original.CleanupPeriodMinutes,     round.CleanupPeriodMinutes);
        Assert.Equal(original.NoResponseTimeoutSeconds, round.NoResponseTimeoutSeconds);
        Assert.Equal(original.ReconnectOnFailedConnect, round.ReconnectOnFailedConnect);
        Assert.Equal(original.ReconnectOnCarrierLost,   round.ReconnectOnCarrierLost);
        Assert.Equal(original.ReconnectOnNoResponse,    round.ReconnectOnNoResponse);
        Assert.Equal(original.ReconnectAfterCleanup,    round.ReconnectAfterCleanup);
        Assert.Equal(original.TerminalCols,             round.TerminalCols);
        Assert.Equal(original.TerminalRows,             round.TerminalRows);
        Assert.Equal(original.FontSize,                 round.FontSize);
        Assert.Equal(original.ScrollbackLines,          round.ScrollbackLines);
        Assert.Equal(original.GameEntryCommand,         round.GameEntryCommand);
        Assert.Equal(original.GameExitCommand,          round.GameExitCommand);
    }

    [Fact]
    public void PartialJson_FallsBackToDefaults()
    {
        const string partial = """ { "Name": "Old File", "Host": "old.example", "Port": 23 } """;
        BbsProfile? dto = JsonSerializer.Deserialize<BbsProfile>(partial);

        Assert.NotNull(dto);
        Assert.Equal("Old File",      dto!.Name);
        Assert.Equal("old.example",   dto.Host);
        Assert.Equal(23,              dto.Port);
        Assert.Equal(3,               dto.MaxRedials);            // default
        Assert.Equal(80,              dto.TerminalCols);          // default
    }
}
