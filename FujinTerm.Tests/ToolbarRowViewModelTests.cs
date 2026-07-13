using System;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.ViewModels.Settings;
using Xunit;

namespace FujinTerm.Tests;

// Row-VM resolution for the Settings → Toolbar + Shortcuts editor. The row VM
// is the unit that decides whether an action carries a toolbar button (catalogue
// entry) or is keybind-only (File-menu actions with no catalogue entry), so
// these pin the Option-B behavior: keybind-only rows resolve a friendly label,
// have no icon, and can't be promoted to the toolbar.
public sealed class ToolbarRowViewModelTests
{
    [Fact]
    public void CatalogueAction_IsToolbarEligible_WithIconLabelAndBoundAction()
    {
        ToolbarRowViewModel row = new(ToolbarItemKind.Button, "OpenSettings");

        Assert.True(row.IsToolbarEligible);
        Assert.Equal("IconGear", row.IconResourceKey);
        Assert.Equal("Settings", row.DisplayLabel);
        Assert.Equal(BuiltInAction.OpenSettings, row.BoundAction);
    }

    [Theory]
    [InlineData("NewProfile",    BuiltInAction.NewProfile)]
    [InlineData("OpenProfile",   BuiltInAction.OpenProfile)]
    [InlineData("SaveProfile",   BuiltInAction.SaveProfile)]
    [InlineData("SaveProfileAs", BuiltInAction.SaveProfileAs)]
    [InlineData("Quit",          BuiltInAction.Quit)]
    public void FileMenuAction_IsKeybindOnly_NoIcon_FriendlyLabel(string actionId, BuiltInAction expected)
    {
        // Guard the premise: these must have no catalogue entry, which is what
        // makes them keybind-only in the first place.
        Assert.Null(ToolbarItemCatalogue.Find(actionId));

        ToolbarRowViewModel row = new(ToolbarItemKind.Button, actionId);

        Assert.False(row.IsToolbarEligible);
        Assert.Null(row.IconResourceKey);
        Assert.Equal(expected, row.BoundAction);
        Assert.Equal(KeybindingStore.ActionLabel(expected), row.DisplayLabel);
    }

    [Fact]
    public void Separator_HasNoActionIconOrEligibility()
    {
        ToolbarRowViewModel row = new(ToolbarItemKind.Separator, null);

        Assert.True(row.IsSeparator);
        Assert.False(row.IsToolbarEligible);
        Assert.Null(row.BoundAction);
        Assert.Null(row.IconResourceKey);
    }

    [Fact]
    public void UnknownActionId_NotEligible_NoBoundAction()
    {
        ToolbarRowViewModel row = new(ToolbarItemKind.Button, "DefinitelyNotAnAction");

        Assert.False(row.IsToolbarEligible);
        Assert.Null(row.BoundAction);
        Assert.Null(row.IconResourceKey);
        Assert.Contains("unknown", row.DisplayLabel, StringComparison.OrdinalIgnoreCase);
    }

    // Structural guard for RefreshShortcutRows: every catalogue-less BuiltInAction
    // must resolve to a keybind-only row (parses, friendly label, no icon) so the
    // Shortcuts list never surfaces an "(unknown action: …)" row.
    [Fact]
    public void EveryCatalogueLessAction_ResolvesAsKeybindOnly()
    {
        foreach (BuiltInAction action in Enum.GetValues<BuiltInAction>())
        {
            string id = action.ToString();
            if (ToolbarItemCatalogue.Find(id) is not null) continue;   // has a toolbar button

            ToolbarRowViewModel row = new(ToolbarItemKind.Button, id);
            Assert.False(row.IsToolbarEligible);
            Assert.Null(row.IconResourceKey);
            Assert.Equal(action, row.BoundAction);
            Assert.DoesNotContain("unknown", row.DisplayLabel, StringComparison.OrdinalIgnoreCase);
        }
    }
}
