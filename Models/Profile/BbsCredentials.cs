namespace MudPlay.Models.Profile;

// Per-character credentials for a single BBS. Stored under
// CharacterProfile.BbsCredentials keyed by BBS name — one entry per BBS the
// character has ever logged into.
//
// The password is stored inline as EncryptedPassword — an AES-GCM blob
// produced by PasswordProtector with the per-user .credkey file in the app
// data folder.
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
    // at the app data folder's .credkey. null when no password has been set.
    public string? EncryptedPassword { get; set; }

    // This character's own logon steps on the board, walked instead of the board's
    // (BbsProfile.LogonSteps): one account's characters can need different answers
    // at the BBS's menus, a character slot or a realm. Null = the board's.
    public List<MenuStep>? LogonSteps { get; set; }

    // Releases through 3.206.4 kept every character's steps here, under the old
    // names; LogonStepMigration reads them once, for the board's default and the
    // characters that differ from it. Read from an old profile but never written
    // back: the serializer's view of each reads null, which isn't written.
    [System.Text.Json.Serialization.JsonIgnore]
    public List<MenuStep>? LegacyLogonSteps { get; private set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? LegacyAutoLogonSetup { get; private set; }

    [System.Text.Json.Serialization.JsonInclude]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    private List<MenuStep>? MenuNavSteps { get => null; set => LegacyLogonSteps = value; }

    [System.Text.Json.Serialization.JsonInclude]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    private string? AutoLogonSetup { get => null; set => LegacyAutoLogonSetup = value; }

    // The individual SYSOP powers this character actually holds on this BBS. Each
    // is an independent capability the client only exercises when its box is
    // checked (the underlying `sys …` command is refused / meaningless without
    // real sysop access on the board). Per-character per-BBS, since different
    // characters on the same BBS can have different powers. Note `sys goto` below
    // is the SYSOP self-teleport game command (gated by SysopGoto here); it is a
    // different thing from the party `@goto` remote walk command, which is gated
    // solely by the per-player PlayerRemoteControls.MovePlayer permission — the two
    // never share gating.

    // `sys st` (SYSOP STATUS) — the client may read the room dump to recover its
    // position when the walker gets lost. This is the master gate on SysStatusProbe.
    public bool SysopStatus { get; set; }

    // `sys god <name> add life` — on the character's own death, auto-recover the
    // life just spent.
    public bool SysopGodLives { get; set; }

    // `sys goto <name>` — the client may jump to a curated location by its board
    // keyword. Independently granted (a board can give this without the others).
    // Gates both the SysopGotos table below and every surface that fires it.
    public bool SysopGoto { get; set; }

    // The user's curated goto locations for this BBS. Each row's Name is sent
    // verbatim after `sys goto`; the Map/Room is where the jump LANDS (used to
    // re-anchor position and to route through the jump as a shortcut).
    // Per-character per-BBS, like the powers above; no SettingsResolver
    // tier (the sysop powers deliberately bypass the 4-tier hierarchy). Seeded with
    // the standard starter locations so the table isn't empty on first use — an old
    // profile whose JSON lacks the field keeps this default; a user who clears every
    // row saves an explicit empty list, which deserializes back to empty (their edit
    // is respected, not re-seeded).
    public List<SysopGotoLocation> SysopGotos { get; set; } = SysopGotoLocation.DefaultStarterSet();

    // Legacy one-way migration. Releases through 3.50.x persisted a single
    // combined "HasSysopPowers" flag (dead until it was wired to sysop-status
    // recovery). An old checked value folds into SysopStatus on load. Set-only +
    // JsonInclude so it deserializes from old profiles but is never written back —
    // new saves use the three fields above.
    [System.Text.Json.Serialization.JsonInclude]
    public bool HasSysopPowers { set { if (value) SysopStatus = true; } }
}
