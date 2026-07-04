namespace FujinTerm.Models.Profile;

// Per-character credentials for a single BBS. Stored under
// CharacterProfile.BbsCredentials keyed by BBS name — one entry per BBS the
// character has ever logged into.
//
// The password is stored inline as EncryptedPassword — an AES-GCM blob
// produced by PasswordProtector with the per-user Data/.credkey file.
// Plaintext passwords never land in any user-readable file written by the app.
public sealed class BbsCredentials
{
    // AES-GCM-encrypted username, produced by PasswordProtector.Protect. Same
    // scheme as EncryptedPassword — usernames are less sensitive than passwords
    // but still don't need to sit in cleartext on disk. Decrypted to plaintext
    // when the profile's BBS tab loads (the UI shows the username openly) and
    // when the login automator pulls it for the wire send. null when no
    // username has been set.
    public string? EncryptedUsername { get; set; }

    // AES-GCM-encrypted password, produced by PasswordProtector.Protect. Stored
    // inline on the character profile JSON so the profile is fully
    // self-contained for backup / copy; decryption needs the per-user key file
    // at Data/.credkey. null when no password has been set.
    public string? EncryptedPassword { get; set; }

    // Menu-navigation steps the LoginAutomator walks after the BBS
    // login/password handshake completes. Each step waits for a server pattern
    // and sends a reply — used to flow through "Press any key", main-menu picks,
    // and the door-game entry prompt before the MajorMUD session is live.
    public List<MenuStep> MenuNavSteps { get; set; } = new();

    // This character has sysop / goto powers on the BBS — flips a few UI
    // affordances (e.g., RemoteCommandManager assumes commands like
    // @goto <player> are allowed without further gating). Per-character per-BBS
    // because different characters on the same BBS can have different powers.
    public bool HasSysopPowers { get; set; }
}
