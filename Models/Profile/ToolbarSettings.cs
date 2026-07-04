namespace FujinTerm.Models.Profile;

// Per-character toolbar layout. An ordered list of items — top-to-bottom in
// Settings ≡ left-to-right on the live toolbar. Each item is either a button
// (resolved through Services.ToolbarItemCatalogue against its
// ToolbarItem.ActionId) or a separator. Persisted as the "Toolbar" entry in
// CharacterProfile.Settings.
//
// Char-tier (not Global) — different characters land on different toolbar
// layouts. null Layout means "use defaults": the 13 standard buttons in their
// original group order.
public sealed class ToolbarSettings
{
    // Ordered toolbar items. null or empty falls back to
    // Services.ToolbarDefaults.
    public List<ToolbarItem>? Layout { get; set; }

    // Master visibility — when false the toolbar is hidden entirely regardless
    // of Position. Default true.
    public bool Visible { get; set; } = true;

    // Edge the toolbar docks to — one of four. Top / Bottom mount it
    // horizontally; Left / Right mount it vertically. Ignored when Visible is
    // false. Default ToolbarPosition.Top (the familiar MegaMUD spot).
    public ToolbarPosition Position { get; set; } = ToolbarPosition.Top;
}

// Edge the toolbar docks to. Top / Bottom are horizontal mounts; Left / Right
// are vertical.
public enum ToolbarPosition
{
    Top,
    Bottom,
    Left,
    Right,
}

// One entry in the persisted toolbar layout.
public sealed class ToolbarItem
{
    public ToolbarItemKind Kind { get; set; } = ToolbarItemKind.Button;

    // Stable action id resolved against Services.ToolbarItemCatalogue. null when
    // Kind is ToolbarItemKind.Separator.
    public string? ActionId { get; set; }
}

public enum ToolbarItemKind
{
    Button,
    Separator,
}
