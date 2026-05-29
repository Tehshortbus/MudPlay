namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character credentials for a single BBS. Stored under
/// <see cref="CharacterProfile.BbsCredentials"/> keyed by BBS name —
/// one entry per BBS the character has ever logged into.
/// </summary>
/// <remarks>
/// Only the credential <em>id</em> is persisted in the profile JSON.
/// The password itself lives in the <see cref="Services.ICredentialStore"/>
/// (Phase 4 PR 4.5b ships an encrypted-file store; later PRs may swap to
/// OS keychains). Plaintext passwords never appear in any user-readable
/// file written by the app.
/// </remarks>
public sealed class BbsCredentials
{
    /// <summary>Username sent to the BBS at login. Plaintext is acceptable here — usernames are not sensitive.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Opaque key the <see cref="Services.ICredentialStore"/> uses to look
    /// up the password. Format: <c>bbs:{bbs-name}:{char-name}:password</c>.
    /// <c>null</c> when no password has been set yet.
    /// </summary>
    public string? PasswordCredentialId { get; set; }

    /// <summary>
    /// Menu-navigation steps the <see cref="Services.LoginAutomator"/> walks
    /// after the BBS login/password handshake completes. Each step waits for
    /// a server pattern and sends a reply — used to flow through "Press any
    /// key", main-menu picks, and the door-game entry prompt before the
    /// MajorMUD session is live.
    /// </summary>
    public List<MenuStep> MenuNavSteps { get; set; } = new();

    /// <summary>
    /// This character has sysop / goto powers on the BBS — flips a few UI
    /// affordances (e.g., the Phase 13 RemoteCommandManager assumes commands
    /// like <c>@goto &lt;player&gt;</c> are allowed without further gating).
    /// Per-character per-BBS because different characters on the same BBS
    /// can have different powers.
    /// </summary>
    public bool HasSysopPowers { get; set; }
}
