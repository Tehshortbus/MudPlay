using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

/// <summary>
/// Canonical default toolbar layout — the 13 catalogue entries in the
/// order Phase 4 PR 4.6 originally shipped. Used when a profile has no
/// stored <see cref="ToolbarSettings.Layout"/> and when the user clicks
/// "Reset to defaults" in the Settings → Toolbar editor.
/// </summary>
public static class ToolbarDefaults
{
    /// <summary>
    /// Returns a fresh list each call so the caller can mutate freely
    /// (drag-reorder, add, delete) without affecting the static template.
    /// </summary>
    public static List<ToolbarItem> Build()
    {
        List<ToolbarItem> list = new();
        foreach (ToolbarItemCatalogue.Entry e in ToolbarItemCatalogue.AllEntries)
        {
            if (!e.InDefaultLayout) continue;
            list.Add(new ToolbarItem
            {
                Kind = ToolbarItemKind.Button,
                ActionId = e.ActionId,
            });
        }
        return list;
    }
}
