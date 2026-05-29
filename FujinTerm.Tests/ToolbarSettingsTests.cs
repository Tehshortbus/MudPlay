using System.Linq;
using System.Text.Json;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class ToolbarSettingsTests
{
    [Fact]
    public void Defaults_LayoutIsNull()
    {
        ToolbarSettings dto = new();
        Assert.Null(dto.Layout);
    }

    [Fact]
    public void ToolbarDefaults_ReturnsEveryCatalogueEntry_AsButtons()
    {
        var defaults = ToolbarDefaults.Build();
        Assert.Equal(ToolbarItemCatalogue.AllEntries.Count, defaults.Count);
        Assert.All(defaults, item =>
        {
            Assert.Equal(ToolbarItemKind.Button, item.Kind);
            Assert.NotNull(item.ActionId);
        });

        // Order matches catalogue order.
        for (int i = 0; i < defaults.Count; i++)
        {
            Assert.Equal(ToolbarItemCatalogue.AllEntries[i].ActionId, defaults[i].ActionId);
        }
    }

    [Fact]
    public void RoundTripJson_PreservesLayoutOrderAndKinds()
    {
        ToolbarSettings original = new()
        {
            Layout = new()
            {
                new() { Kind = ToolbarItemKind.Button,    ActionId = "OpenSettings" },
                new() { Kind = ToolbarItemKind.Separator, ActionId = null },
                new() { Kind = ToolbarItemKind.Button,    ActionId = "OpenLogPane" },
            },
        };

        string json = JsonSerializer.Serialize(original);
        ToolbarSettings? round = JsonSerializer.Deserialize<ToolbarSettings>(json);
        Assert.NotNull(round);
        Assert.NotNull(round!.Layout);
        Assert.Equal(3, round.Layout!.Count);
        Assert.Equal(ToolbarItemKind.Button,    round.Layout[0].Kind);
        Assert.Equal("OpenSettings",            round.Layout[0].ActionId);
        Assert.Equal(ToolbarItemKind.Separator, round.Layout[1].Kind);
        Assert.Null(round.Layout[1].ActionId);
        Assert.Equal(ToolbarItemKind.Button,    round.Layout[2].Kind);
        Assert.Equal("OpenLogPane",             round.Layout[2].ActionId);
    }

    [Fact]
    public void ToolbarConfig_ApplyFrom_NullLayout_FallsBackToDefaults()
    {
        ToolbarConfig live = new();
        live.ApplyFrom(new ToolbarSettings());

        Assert.Equal(ToolbarItemCatalogue.AllEntries.Count, live.Layout.Count);
        Assert.Equal(
            ToolbarItemCatalogue.AllEntries.Select(e => e.ActionId).ToArray(),
            live.Layout.Select(i => i.ActionId).ToArray());
    }

    [Fact]
    public void ToolbarConfig_ApplyFrom_NonEmptyLayout_ReplacesContents()
    {
        ToolbarConfig live = new();
        live.ApplyFrom(new ToolbarSettings
        {
            Layout = new()
            {
                new() { Kind = ToolbarItemKind.Button,    ActionId = "OpenParty" },
                new() { Kind = ToolbarItemKind.Separator, ActionId = null },
            },
        });

        Assert.Equal(2, live.Layout.Count);
        Assert.Equal("OpenParty",                live.Layout[0].ActionId);
        Assert.Equal(ToolbarItemKind.Separator,  live.Layout[1].Kind);
    }

    [Fact]
    public void ToolbarConfig_Snapshot_RoundTripsThroughDto()
    {
        ToolbarConfig live = new();
        live.ApplyFrom(new ToolbarSettings
        {
            Layout = new()
            {
                new() { Kind = ToolbarItemKind.Button,    ActionId = "OpenSettings" },
                new() { Kind = ToolbarItemKind.Separator, ActionId = null },
            },
        });

        ToolbarSettings snap = live.Snapshot();
        Assert.NotNull(snap.Layout);
        Assert.Equal(2, snap.Layout!.Count);
        Assert.Equal("OpenSettings",             snap.Layout[0].ActionId);
        Assert.Equal(ToolbarItemKind.Separator,  snap.Layout[1].Kind);
    }

    [Fact]
    public void ToolbarItemCatalogue_Find_IsCaseInsensitive_AndReturnsNullForUnknown()
    {
        Assert.NotNull(ToolbarItemCatalogue.Find("opensettings"));
        Assert.NotNull(ToolbarItemCatalogue.Find("OpenSettings"));
        Assert.Null(ToolbarItemCatalogue.Find("DefinitelyNotAnAction"));
        Assert.Null(ToolbarItemCatalogue.Find(null));
    }
}
