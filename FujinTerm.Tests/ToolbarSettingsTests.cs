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
    public void ToolbarDefaults_OnlyIncludesInDefaultLayoutEntries_AsButtons()
    {
        var defaults = ToolbarDefaults.Build();
        var expected = ToolbarItemCatalogue.AllEntries.Where(e => e.InDefaultLayout).ToArray();

        Assert.Equal(expected.Length, defaults.Count);
        Assert.All(defaults, item =>
        {
            Assert.Equal(ToolbarItemKind.Button, item.Kind);
            Assert.NotNull(item.ActionId);
        });

        // Order matches the catalogue's filtered order.
        for (int i = 0; i < defaults.Count; i++)
        {
            Assert.Equal(expected[i].ActionId, defaults[i].ActionId);
        }
    }

    [Fact]
    public void ToolbarDefaults_DoesNotIncludePhase4_6bStubs()
    {
        var defaults = ToolbarDefaults.Build();
        Assert.DoesNotContain(defaults, i => i.ActionId == "ActionGetAll");
        Assert.DoesNotContain(defaults, i => i.ActionId == "ToggleAutoCombat");
        Assert.DoesNotContain(defaults, i => i.ActionId == "ToggleAllAutoOff");
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

        var expected = ToolbarItemCatalogue.AllEntries.Where(e => e.InDefaultLayout).ToArray();
        Assert.Equal(expected.Length, live.Layout.Count);
        Assert.Equal(
            expected.Select(e => e.ActionId).ToArray(),
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

    // ===== Visibility + position (Show / Position) =====

    [Fact]
    public void Defaults_VisibilityAndPosition_AreTopShown()
    {
        ToolbarSettings dto = new();
        Assert.True(dto.Visible);
        Assert.Equal(ToolbarPosition.Top, dto.Position);
    }

    [Fact]
    public void RoundTripJson_PreservesVisibilityAndPosition()
    {
        ToolbarSettings original = new()
        {
            Visible  = false,
            Position = ToolbarPosition.Right,
        };
        string json = JsonSerializer.Serialize(original);
        ToolbarSettings? round = JsonSerializer.Deserialize<ToolbarSettings>(json);
        Assert.NotNull(round);
        Assert.False(round!.Visible);
        Assert.Equal(ToolbarPosition.Right, round.Position);
    }

    [Fact]
    public void ToolbarConfig_ShowFlags_DriveByVisibleAndPosition()
    {
        ToolbarConfig live = new();
        // Defaults: visible + Top.
        Assert.True(live.ShowTop);
        Assert.False(live.ShowBottom);
        Assert.False(live.ShowLeft);
        Assert.False(live.ShowRight);

        // Bottom.
        live.Position = ToolbarPosition.Bottom;
        Assert.False(live.ShowTop);
        Assert.True(live.ShowBottom);
        Assert.False(live.ShowLeft);
        Assert.False(live.ShowRight);

        // Left.
        live.Position = ToolbarPosition.Left;
        Assert.False(live.ShowTop);
        Assert.False(live.ShowBottom);
        Assert.True(live.ShowLeft);
        Assert.False(live.ShowRight);

        // Right.
        live.Position = ToolbarPosition.Right;
        Assert.False(live.ShowLeft);
        Assert.True(live.ShowRight);

        // Master hide collapses every flag regardless of position.
        live.Visible = false;
        Assert.False(live.ShowTop);
        Assert.False(live.ShowBottom);
        Assert.False(live.ShowLeft);
        Assert.False(live.ShowRight);
    }

    [Fact]
    public void ToolbarConfig_ApplyFrom_PropagatesVisibilityAndPosition()
    {
        ToolbarConfig live = new();
        live.ApplyFrom(new ToolbarSettings
        {
            Visible  = false,
            Position = ToolbarPosition.Right,
        });
        Assert.False(live.Visible);
        Assert.Equal(ToolbarPosition.Right, live.Position);
    }

    [Fact]
    public void ToolbarConfig_Snapshot_RoundTripsVisibilityAndPosition()
    {
        ToolbarConfig live = new();
        live.ApplyFrom(new ToolbarSettings
        {
            Visible  = false,
            Position = ToolbarPosition.Right,
        });
        ToolbarSettings snap = live.Snapshot();
        Assert.False(snap.Visible);
        Assert.Equal(ToolbarPosition.Right, snap.Position);
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
