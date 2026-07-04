namespace FujinTerm.Models.Profile;

// Initial state for every Action-menu auto-toggle when the character
// logs in. Lives on GeneralSettings twice — once per Manual-Mode column
// and once per Auto-Mode column — so the user can pick which engines come
// up engaged depending on the play mode. The engines read these flags as
// their boot-up state.
//
// Field set mirrors the Action menu's auto-toggle group exactly
// (Combat / Nuke / Heal-Rest / Bless / Light / Get-Items / Get-Cash /
// Sneak / Hide / Search).
public sealed class AutoActionDefaults
{
    public bool AutoCombat   { get; set; } = true;
    public bool AutoNuke     { get; set; } = true;
    public bool AutoHealRest { get; set; } = true;
    public bool AutoBless    { get; set; } = true;
    public bool AutoLight    { get; set; } = true;
    public bool AutoGetItems { get; set; } = true;
    public bool AutoGetCash  { get; set; } = true;
    public bool AutoSneak    { get; set; }
    public bool AutoHide     { get; set; }
    public bool AutoSearch   { get; set; }
}
