using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

// Canonical default toolbar layout — the catalogue entries flagged for the
// default layout, in their canonical order. Used when a profile has no stored
// ToolbarSettings.Layout and when the user clicks "Reset to defaults" in the
// Settings → Toolbar editor.
public static class ToolbarDefaults
{
    // Returns a fresh list each call so the caller can mutate freely
    // (drag-reorder, add, delete) without affecting the static template.
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
