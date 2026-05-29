using System.Text.Json;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class ToolbarSettingsTests
{
    [Fact]
    public void Defaults_AllTrue()
    {
        ToolbarSettings dto = new();
        Assert.True(dto.ShowConnect);
        Assert.True(dto.ShowSettings);
        Assert.True(dto.ShowNavigation);
        Assert.True(dto.ShowBackscroll);
        Assert.True(dto.ShowCapture);
        Assert.True(dto.ShowWireInspector);
        Assert.True(dto.ShowConversation);
        Assert.True(dto.ShowParty);
        Assert.True(dto.ShowWorkshop);
        Assert.True(dto.ShowSpellBook);
        Assert.True(dto.ShowSessionStats);
        Assert.True(dto.ShowGameDataBrowser);
        Assert.True(dto.ShowLog);
    }

    [Fact]
    public void RoundTripJson_PreservesEveryField()
    {
        ToolbarSettings original = new()
        {
            ShowConnect = false,
            ShowSettings = true,
            ShowNavigation = false,
            ShowBackscroll = true,
            ShowCapture = false,
            ShowWireInspector = true,
            ShowConversation = false,
            ShowParty = true,
            ShowWorkshop = false,
            ShowSpellBook = true,
            ShowSessionStats = false,
            ShowGameDataBrowser = true,
            ShowLog = false,
        };

        string json = JsonSerializer.Serialize(original);
        ToolbarSettings? round = JsonSerializer.Deserialize<ToolbarSettings>(json);
        Assert.NotNull(round);

        Assert.Equal(original.ShowConnect,         round!.ShowConnect);
        Assert.Equal(original.ShowSettings,        round.ShowSettings);
        Assert.Equal(original.ShowNavigation,      round.ShowNavigation);
        Assert.Equal(original.ShowBackscroll,      round.ShowBackscroll);
        Assert.Equal(original.ShowCapture,         round.ShowCapture);
        Assert.Equal(original.ShowWireInspector,   round.ShowWireInspector);
        Assert.Equal(original.ShowConversation,    round.ShowConversation);
        Assert.Equal(original.ShowParty,           round.ShowParty);
        Assert.Equal(original.ShowWorkshop,        round.ShowWorkshop);
        Assert.Equal(original.ShowSpellBook,       round.ShowSpellBook);
        Assert.Equal(original.ShowSessionStats,    round.ShowSessionStats);
        Assert.Equal(original.ShowGameDataBrowser, round.ShowGameDataBrowser);
        Assert.Equal(original.ShowLog,             round.ShowLog);
    }

    [Fact]
    public void ToolbarConfig_ApplyFrom_MirrorsDto()
    {
        ToolbarConfig live = new();
        ToolbarSettings dto = new() { ShowConnect = false, ShowLog = false };
        live.ApplyFrom(dto);

        Assert.False(live.ShowConnect);
        Assert.True(live.ShowSettings);  // unchanged from default
        Assert.False(live.ShowLog);
    }

    [Fact]
    public void ToolbarConfig_Snapshot_RoundTripsThroughDto()
    {
        ToolbarConfig live = new()
        {
            ShowConnect = false,
            ShowWireInspector = false,
        };
        ToolbarSettings snap = live.Snapshot();
        Assert.False(snap.ShowConnect);
        Assert.True(snap.ShowSettings);
        Assert.False(snap.ShowWireInspector);
    }
}
