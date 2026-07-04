namespace FujinTerm.Services;

// Live mirror of the per-BBS game-menu commands configured on the Settings → BBS
// tab. Engines (HangupHandler, future cleanup-flow automation) read from here
// instead of going through BbsProfileStore every time, so the values are settable
// in one place and the engines stay decoupled from the settings UI.
//
// Hydrated from the active BbsProfile (via
// AppServices.ApplyDisplayFromActiveBbs) on every profile load / mutate / close
// — the menu picks live at BBS tier because the key bindings belong to the realm
// / front-end, not the character. Defaults are the standard MajorMUD main-menu
// picks: E to enter the realm, =x to log off from the main menu.
public sealed class GameCommands
{
    // Sent at the main menu to enter the realm. Default E.
    public string EntryCommand { get; set; } = "E";

    // Sent at the main menu to log off. Default =x.
    public string ExitCommand  { get; set; } = "=x";
}
