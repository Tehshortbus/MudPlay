using MudPlay.Models.Profile;

namespace MudPlay.Services;

// Canonical default toolbar layout — a curated, ordered set of catalogue actions
// grouped by separators. Used when a profile has no stored
// ToolbarSettings.Layout and when the user clicks "Reset to defaults" in the
// Settings → Toolbar editor. This is the single source of truth for the default
// layout; the catalogue (ToolbarItemCatalogue) is just the pool of available
// actions.
public static class ToolbarDefaults
{
    // Action ids in default order. null marks a separator between groups:
    //   connection      → connect/disconnect, disable hangups
    //   navigation/move → navigation, start / pause / stop movement, sprint mode
    //   panels          → party, backscroll
    //   auto-responses  → master switch + the on-by-default auto engines
    private static readonly string?[] _order =
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

    // Returns a fresh list each call so the caller can mutate freely
    // (drag-reorder, add, delete) without affecting the static template.
    public static List<ToolbarItem> Build()
    {
        List<ToolbarItem> list = new(_order.Length);
        foreach (string? actionId in _order)
        {
            list.Add(actionId is null
                ? new ToolbarItem { Kind = ToolbarItemKind.Separator }
                : new ToolbarItem { Kind = ToolbarItemKind.Button, ActionId = actionId });
        }
        return list;
    }
}
