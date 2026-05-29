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
}
