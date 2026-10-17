using System.Text.Json;
using MudPlay.Models.Settings;
using Xunit;

namespace MudPlay.Tests;

public sealed class BbsProfileFieldsTests
{
    [Fact]
    public void Defaults_MatchSpec()
    {
        BbsProfile dto = new();

        Assert.Equal(string.Empty, dto.Name);
        Assert.Equal(string.Empty, dto.Host);
        Assert.Equal(23,           dto.Port);
        Assert.Null(dto.WebsiteUrl);

        Assert.Equal(3, dto.MaxRedials);
        Assert.Equal(5, dto.RedialPauseSeconds);
        Assert.False(dto.InfiniteRetries);
        Assert.Equal(0, dto.CleanupPeriodMinutes);
        Assert.Equal(20, dto.NoResponseTimeoutSeconds);   // ~50s dead-connection detection out of the box
        Assert.False(dto.ReconnectOnFailedConnect);
        Assert.False(dto.ReconnectOnCarrierLost);
        Assert.False(dto.ReconnectOnNoResponse);
        Assert.False(dto.ReconnectAfterCleanup);

        Assert.Equal(80, dto.TerminalCols);
        Assert.Equal(25, dto.TerminalRows);
        Assert.Equal(4_000, dto.ScrollbackLines);

        Assert.Empty(dto.Realms);   // BbsProfileStore adds the first realm on load / save

        // Game-menu commands default to MajorMUD's standard picks:
        // E to enter the realm, =x to log off from the main menu.
        RealmProfile realm = new();
        Assert.Equal("E",  realm.GameEntryCommand);
        Assert.Equal("=x", realm.GameExitCommand);

        // Runic denomination defaults to the stock label until a realm renames it.
        Assert.Equal("runic", realm.RunicCurrencyName);
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
            InfiniteRetries = true,
            CleanupPeriodMinutes = 60,
            NoResponseTimeoutSeconds = 90,
            ReconnectOnFailedConnect = true,
            ReconnectOnCarrierLost = true,
            ReconnectOnNoResponse = false,
            ReconnectAfterCleanup = true,
            TerminalCols = 132,
            TerminalRows = 50,
            ScrollbackLines = 50_000,
            Realms =
            {
                new RealmProfile { Name = "PVE", GameEntryCommand = "enter", GameExitCommand = "bye", RunicCurrencyName = "quatloos" },
                new RealmProfile { Name = "PVP", ActiveGameDataSet = "custom-mdb" },
            },
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
        Assert.Equal(original.InfiniteRetries,          round.InfiniteRetries);
        Assert.Equal(original.CleanupPeriodMinutes,     round.CleanupPeriodMinutes);
        Assert.Equal(original.NoResponseTimeoutSeconds, round.NoResponseTimeoutSeconds);
        Assert.Equal(original.ReconnectOnFailedConnect, round.ReconnectOnFailedConnect);
        Assert.Equal(original.ReconnectOnCarrierLost,   round.ReconnectOnCarrierLost);
        Assert.Equal(original.ReconnectOnNoResponse,    round.ReconnectOnNoResponse);
        Assert.Equal(original.ReconnectAfterCleanup,    round.ReconnectAfterCleanup);
        Assert.Equal(original.TerminalCols,             round.TerminalCols);
        Assert.Equal(original.TerminalRows,             round.TerminalRows);
        Assert.Equal(original.ScrollbackLines,          round.ScrollbackLines);
        Assert.Equal(2, round.Realms.Count);
        Assert.Equal("enter",      round.Realms[0].GameEntryCommand);
        Assert.Equal("bye",        round.Realms[0].GameExitCommand);
        Assert.Equal("quatloos",   round.Realms[0].RunicCurrencyName);
        Assert.Equal("custom-mdb", round.Realms[1].ActiveGameDataSet);
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
        Assert.False(dto.InfiniteRetries);                       // default
        Assert.Equal(80,              dto.TerminalCols);          // default
    }
}
