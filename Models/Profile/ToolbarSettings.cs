namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character toolbar layout. An ordered list of items — top-to-bottom
/// in Settings ≡ left-to-right on the live toolbar. Each item is either
/// a button (resolved through <see cref="Services.ToolbarItemCatalogue"/>
/// against its <see cref="ToolbarItem.ActionId"/>) or a separator.
/// Persisted as the <c>"Toolbar"</c> entry in
/// <see cref="CharacterProfile.Settings"/>.
/// </summary>
/// <remarks>
/// Char-tier (not Global) — different characters land on different
/// toolbar layouts. <c>null</c> Layout means "use defaults": the 13
/// standard buttons in their original group order.
/// </remarks>
public sealed class ToolbarSettings
{
    /// <summary>
    /// Ordered toolbar items. <c>null</c> or empty falls back to
    /// <see cref="Services.ToolbarDefaults"/>.
    /// </summary>
    public List<ToolbarItem>? Layout { get; set; }

    /// <summary>
    /// Master visibility — when false the toolbar is hidden entirely
    /// regardless of <see cref="Position"/>. Default true.
    /// </summary>
    public bool Visible { get; set; } = true;

    /// <summary>
    /// Edge the toolbar docks to — one of four. Top / Bottom mount it
    /// horizontally; Left / Right mount it vertically. Ignored when
    /// <see cref="Visible"/> is false. Default <see cref="ToolbarPosition.Top"/>
    /// (the familiar MegaMUD spot).
    /// </summary>
    public ToolbarPosition Position { get; set; } = ToolbarPosition.Top;
}

/// <summary>Edge the toolbar docks to. Top / Bottom are horizontal mounts; Left / Right are vertical.</summary>
public enum ToolbarPosition
{
    Top,
    Bottom,
    Left,
    Right,
}

/// <summary>One entry in the persisted toolbar layout.</summary>
public sealed class ToolbarItem
{
    public ToolbarItemKind Kind { get; set; } = ToolbarItemKind.Button;

    /// <summary>
    /// Stable action id resolved against <see cref="Services.ToolbarItemCatalogue"/>.
    /// <c>null</c> when <see cref="Kind"/> is <see cref="ToolbarItemKind.Separator"/>.
    /// </summary>
    public string? ActionId { get; set; }
}

public enum ToolbarItemKind
{
    Button,
    Separator,
}
