namespace FujinTerm.Models.Profile;

/// <summary>
/// One step in the per-character menu-navigation sequence that runs after
/// the BBS connect completes. The LoginAutomator walks the list in order:
/// wait for <see cref="WaitForPattern"/> (case-insensitive substring),
/// send <see cref="Send"/>, move on. The final step's match fires
/// <see cref="Services.LoginAutomator.LoggedIntoGame"/>.
/// </summary>
public sealed class MenuStep
{
    /// <summary>
    /// Substring the BBS prints before this step is "ready" — e.g.
    /// <c>"Main Menu:"</c>. Match is case-insensitive plain substring;
    /// wildcards / regex aren't supported because real BBS menu prompts
    /// are static strings.
    /// </summary>
    public string WaitForPattern { get; set; } = string.Empty;

    /// <summary>The response to send when the pattern matches. <c>\r</c> for Enter.</summary>
    public string Send { get; set; } = string.Empty;

    /// <summary>Seconds to wait before giving up on this step and aborting the automator.</summary>
    public int TimeoutSeconds { get; set; } = 15;
}
