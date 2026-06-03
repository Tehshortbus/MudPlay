namespace FujinTerm.Services;

/// <summary>
/// Live mirror of the per-character game-menu commands configured on
/// the Settings → Other tab. Engines (HangupHandler, future cleanup-
/// flow automation) read from here instead of going through
/// <see cref="ProfileService"/> every time, so the values are settable
/// in one place and the engines stay decoupled from the settings UI.
/// </summary>
/// <remarks>
/// Hydrated by <see cref="AppServices.ApplyOtherFromActiveProfile"/>
/// on every profile load / mutate / close. Defaults are the standard
/// MajorMUD main-menu picks: <c>E</c> to enter the realm, <c>=x</c>
/// to log off from the main menu.
/// </remarks>
public sealed class GameCommands
{
    /// <summary>Sent at the main menu to enter the realm. Default <c>E</c>.</summary>
    public string EntryCommand { get; set; } = "E";

    /// <summary>Sent at the main menu to log off. Default <c>=x</c>.</summary>
    public string ExitCommand  { get; set; } = "=x";
}
