namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character statline configuration. Wraps the wildcard string the
/// app sends to the BBS via <c>set statline &lt;wildcard&gt;</c> so the
/// game prints prompts in the format the client knows how to parse.
/// Persisted as the <c>"Statline"</c> entry in
/// <see cref="CharacterProfile.Settings"/>.
/// </summary>
/// <remarks>
/// Phase 4 PR 4.7 ships only the raw wildcard text; Phase 12 PR 12.1
/// replaces the editor with a drag-token builder + live preview, but
/// the on-disk shape (one string) stays the same.
/// </remarks>
public sealed class StatlineSettings
{
    /// <summary>
    /// MajorMUD wildcard string — e.g. <c>"[HP=%h/MA=%m]: (%p) "</c>.
    /// <c>null</c> / empty means "use whatever the server already has
    /// configured" — no command is sent on logon.
    /// </summary>
    public string? Wildcard { get; set; }
}
