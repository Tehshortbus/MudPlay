namespace FujinTerm.Models.Settings;

/// <summary>
/// Global-tier toolbar visibility settings. One bool per icon on the main
/// window's toolbar — true keeps the icon visible, false collapses it.
/// Persisted as the <c>"Toolbar"</c> entry in <see cref="GlobalSettings.Settings"/>.
/// </summary>
/// <remarks>
/// All defaults are <c>true</c> so a fresh install (or any setting not
/// migrated forward) gets the full toolbar. The user opts <em>out</em> of
/// icons they don't want, rather than opting in.
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
