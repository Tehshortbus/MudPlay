namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character toolbar visibility settings. One bool per icon on the
/// main window's toolbar — true keeps the icon visible, false collapses
/// it. Persisted as the <c>"Toolbar"</c> entry in
/// <see cref="CharacterProfile.Settings"/>.
/// </summary>
/// <remarks>
/// Char-tier (not Global) because the user wants different characters
/// to land on different toolbar layouts — a healer profile may surface
/// Spell Book / Party while a tank profile pulls those off in favour
/// of Combat / Workshop. Loading a profile reapplies that profile's
/// layout; the unsaved-draft profile keeps its own running layout in
/// memory until saved.
///
/// All defaults are <c>true</c> so a fresh profile (or any setting not
/// migrated forward) gets the full toolbar. The user opts <em>out</em>
/// of icons they don't want, rather than opting in.
/// </remarks>
public sealed class ToolbarSettings
{
    public bool ShowConnect { get; set; } = true;
    public bool ShowSettings { get; set; } = true;
    public bool ShowNavigation { get; set; } = true;
    public bool ShowBackscroll { get; set; } = true;
    public bool ShowCapture { get; set; } = true;
    public bool ShowWireInspector { get; set; } = true;
    public bool ShowConversation { get; set; } = true;
    public bool ShowParty { get; set; } = true;
    public bool ShowWorkshop { get; set; } = true;
    public bool ShowSpellBook { get; set; } = true;
    public bool ShowSessionStats { get; set; } = true;
    public bool ShowGameDataBrowser { get; set; } = true;
    public bool ShowLog { get; set; } = true;
}
