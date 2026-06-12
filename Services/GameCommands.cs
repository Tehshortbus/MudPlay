namespace FujinTerm.Services;

/// <summary>
/// Live mirror of the per-BBS game-menu commands configured on the
/// Settings → BBS tab. Engines (HangupHandler, future cleanup-flow
/// automation) read from here instead of going through
/// <see cref="BbsProfileStore"/> every time, so the values are settable
/// in one place and the engines stay decoupled from the settings UI.
/// </summary>
/// <remarks>
/// Hydrated from the active <see cref="Models.Settings.BbsProfile"/>
/// (via <c>AppServices.ApplyDisplayFromActiveBbs</c>) on every profile
/// load / mutate / close — the menu picks live at BBS tier because the
/// key bindings belong to the realm / front-end, not the character.
/// Defaults are the standard MajorMUD main-menu picks: <c>E</c> to
/// enter the realm, <c>=x</c> to log off from the main menu.
/// </remarks>
public sealed class GameCommands
{
    /// <summary>Sent at the main menu to enter the realm. Default <c>E</c>.</summary>
    public string EntryCommand { get; set; } = "E";

    /// <summary>Sent at the main menu to log off. Default <c>=x</c>.</summary>
    public string ExitCommand  { get; set; } = "=x";
}
