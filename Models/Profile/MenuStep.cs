namespace FujinTerm.Models.Profile;

// One step in the per-character menu-navigation sequence that runs after the BBS
// connect completes. The LoginAutomator walks the list in order: wait for
// WaitForPattern (case-insensitive substring), send Send, move on. The final
// step's match fires Services.LoginAutomator.LoggedIntoGame.
public sealed class MenuStep
{
    // Substring the BBS prints before this step is "ready" — e.g. "Main Menu:".
    // Match is case-insensitive plain substring; wildcards / regex aren't
    // supported because real BBS menu prompts are static strings.
    public string WaitForPattern { get; set; } = string.Empty;

    // The response to send when the pattern matches. The Enter key is appended
    // automatically.
    public string Send { get; set; } = string.Empty;
}
