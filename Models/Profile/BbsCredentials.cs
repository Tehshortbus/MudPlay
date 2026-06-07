namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character credentials for a single BBS. Stored under
/// <see cref="CharacterProfile.BbsCredentials"/> keyed by BBS name —
/// one entry per BBS the character has ever logged into.
/// </summary>
/// <remarks>
/// The password is stored inline as
/// <see cref="EncryptedPassword"/> — an AES-GCM blob produced by
/// <see cref="Services.PasswordProtector"/> with the per-user
/// <c>Data/.credkey</c> file. Plaintext passwords never land in any
/// user-readable file written by the app.
/// </remarks>
public sealed class BbsCredentials
{
    /// <summary>
    /// AES-GCM-encrypted username, produced by
    /// <see cref="Services.PasswordProtector.Protect"/>. Same
    /// scheme as <see cref="EncryptedPassword"/> — usernames are less
    /// sensitive than passwords but still don't need to sit in
    /// cleartext on disk. Decrypted to plaintext when the profile's
    /// BBS tab loads (the UI shows the username openly) and when the
    /// login automator pulls it for the wire send. <c>null</c> when
    /// no username has been set.
    /// </summary>
    public string? EncryptedUsername { get; set; }

    /// <summary>
    /// AES-GCM-encrypted password, produced by
    /// <see cref="Services.PasswordProtector.Protect"/>. Stored inline on
    /// the character profile JSON so the profile is fully self-contained
    /// for backup / copy; decryption needs the per-user key file at
    /// <c>Data/.credkey</c>. <c>null</c> when no password has been set.
    /// </summary>
    public string? EncryptedPassword { get; set; }

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
