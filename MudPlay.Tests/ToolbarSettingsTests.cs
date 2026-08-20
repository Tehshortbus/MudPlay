using System.Linq;
using System.Text.Json;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class ToolbarSettingsTests
{
    [Fact]
    public void Defaults_LayoutIsNull()
    {
        ToolbarSettings dto = new();
        Assert.Null(dto.Layout);
    }

    // The canonical default layout: three separators grouping connection,
    // navigation/movement, panels, and the on-by-default auto row.
    private static readonly string?[] ExpectedDefaultLayout =
    {
        "ToggleConnection",
        "ToggleDisableHangups",
        null,
        "OpenNavigation",
        "MovementStart",
        "MovementPause",
        "MovementStop",
        "ToggleSprintMode",
        null,
        "OpenParty",
        "OpenBackscroll",
        "SendExp",
        null,
        "ToggleAllAutoOff",
        "ToggleAutoCombat",
        "ToggleAutoNuke",
        "ToggleAutoHealRest",
        "ToggleAutoBless",
        "ToggleAutoGetItems",
        "ToggleAutoGetCash",
        "ToggleAutoSneak",
    };

    [Fact]
    public void ToolbarDefaults_MatchesCanonicalOrder_WithSeparators()
    {
        var defaults = ToolbarDefaults.Build();
        Assert.Equal(ExpectedDefaultLayout.Length, defaults.Count);
        for (int i = 0; i < defaults.Count; i++)
        {
            if (ExpectedDefaultLayout[i] is null)
            {
                Assert.Equal(ToolbarItemKind.Separator, defaults[i].Kind);
                Assert.Null(defaults[i].ActionId);
            }
            else
            {
                Assert.Equal(ToolbarItemKind.Button, defaults[i].Kind);
                Assert.Equal(ExpectedDefaultLayout[i], defaults[i].ActionId);
            }
        }
    }

    [Fact]
    public void ToolbarDefaults_ButtonActionIds_ResolveInCatalogue()
    {
        // Every default button must map to a real catalogue entry, or the live
        // toolbar renders a dead button.
        var defaults = ToolbarDefaults.Build();
        Assert.All(
            defaults.Where(i => i.Kind == ToolbarItemKind.Button),
            i => Assert.NotNull(ToolbarItemCatalogue.Find(i.ActionId)));
    }

    [Fact]
    public void ToolbarDefaults_ExcludesOneShotActions()
    {
        var defaults = ToolbarDefaults.Build();
        Assert.DoesNotContain(defaults, i => i.ActionId == "ActionGetAll");
        Assert.DoesNotContain(defaults, i => i.ActionId == "ActionDropAll");
        Assert.DoesNotContain(defaults, i => i.ActionId == "ToggleAutoTrain");
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

        var expected = ToolbarDefaults.Build();
        Assert.Equal(expected.Count, live.Layout.Count);
        Assert.Equal(
            expected.Select(i => i.ActionId).ToArray(),
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
