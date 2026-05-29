namespace FujinTerm.Models.Profile;

/// <summary>
/// One step in the per-character menu-navigation sequence that runs after
/// the BBS login prompts complete. The LoginAutomator (Phase 4 PR 4.5c)
/// walks the list in order: wait for <see cref="WaitForPattern"/>, send
/// <see cref="Send"/>, move on. The final step's pattern match fires
/// <see cref="Services.LoginAutomator.LoggedIntoGame"/>.
/// </summary>
public sealed class MenuStep
{
    /// <summary>The text the BBS sends before this step is "ready" — e.g. "Main Menu:".</summary>
    public string WaitForPattern { get; set; } = string.Empty;

    /// <summary>How <see cref="WaitForPattern"/> is interpreted against the incoming byte stream.</summary>
    public MenuStepMatchType MatchType { get; set; } = MenuStepMatchType.Literal;

    /// <summary>The response to send when the pattern matches. <c>\r</c> for Enter.</summary>
    public string Send { get; set; } = string.Empty;

    /// <summary>Seconds to wait before giving up on this step and aborting the automator.</summary>
    public int TimeoutSeconds { get; set; } = 15;
}

/// <summary>How a step's <see cref="MenuStep.WaitForPattern"/> matches incoming bytes.</summary>
public enum MenuStepMatchType
{
    /// <summary>Plain substring match (case-insensitive).</summary>
    Literal = 0,

    /// <summary>.NET regex. Compiled fresh per match attempt.</summary>
    Regex = 1,

    /// <summary>Wildcard glob: <c>*</c> any-run, <c>?</c> any-char.</summary>
    Wildcard = 2,
}
