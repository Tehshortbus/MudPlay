namespace MudPlay.Models.Profile;

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

    // The individual SYSOP powers this character actually holds on this BBS. Each
    // is an independent capability the client only exercises when its box is
    // checked (the underlying `sys …` command is refused / meaningless without
    // real sysop access on the board). Per-character per-BBS, since different
    // characters on the same BBS can have different powers. NONE of these relate
    // to `@goto` — that remote command is gated solely by the per-player
    // PlayerRemoteControls.MovePlayer permission.

    // `sys map` — the client may request the game's own text area map to help
    // locate itself. (Reading the map to recover position is a later addition;
    // for now this only records that the power is available.)
    public bool SysopMap { get; set; }

    // `sys st` (SYSOP STATUS) — the client may read the room dump to recover its
    // position when the walker gets lost. This is the master gate on SysStatusProbe.
    public bool SysopStatus { get; set; }

    // `sys god <name> add life` — on the character's own death, auto-recover the
    // life just spent.
    public bool SysopGodLives { get; set; }

    // Legacy one-way migration. Releases through 3.50.x persisted a single
    // combined "HasSysopPowers" flag (dead until it was wired to sysop-status
    // recovery). An old checked value folds into SysopStatus on load. Set-only +
    // JsonInclude so it deserializes from old profiles but is never written back —
    // new saves use the three fields above.
    [System.Text.Json.Serialization.JsonInclude]
    public bool HasSysopPowers { set { if (value) SysopStatus = true; } }
}
