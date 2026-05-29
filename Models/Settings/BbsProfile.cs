using System.Text.Json;

namespace FujinTerm.Models.Settings;

/// <summary>
/// Root DTO for <c>Data/BBS/{bbs-name}.json</c> — the BBS tier of the
/// settings hierarchy. Connection info plus deltas the user pinned to "only
/// for this BBS." Per-character credentials are stored separately under each
/// <c>CharacterProfile</c>; this file describes the BBS itself.
/// </summary>
public sealed class BbsProfile
{
    /// <summary>JSON schema version (see <c>GlobalSettings.SchemaVersion</c> for the contract).</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Display name + filename key for this BBS.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Hostname or IP address the Telnet client connects to.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>TCP port; defaults to the Telnet well-known port.</summary>
    public int Port { get; set; } = 23;

    /// <summary>
    /// Optional URL the user wants the Help → {BBS site} ↗ menu entry to open
    /// (the BBS's web site, wiki, Discord — whatever the operator publishes).
    /// <c>null</c> hides the link; the menu entry stays present but disabled.
    /// </summary>
    public string? WebsiteUrl { get; set; }

    // ----- Connection / retry behaviour (Phase 4 PR 4.5) -----

    /// <summary>How many connect attempts (initial + retries) before giving up.</summary>
    public int MaxRedials { get; set; } = 3;

    /// <summary>Seconds to wait between connect attempts.</summary>
    public int RedialPauseSeconds { get; set; } = 5;

    /// <summary>
    /// Minutes the BBS allows a session to idle before kicking. Drives the
    /// optional <see cref="ReconnectAfterCleanup"/> auto-reconnect; <c>0</c>
    /// disables the timer.
    /// </summary>
    public int CleanupPeriodMinutes { get; set; }

    /// <summary>Reconnect automatically when the previous connect attempt failed.</summary>
    public bool ReconnectOnFailedConnect { get; set; }

    /// <summary>Reconnect automatically after the carrier signal drops mid-session.</summary>
    public bool ReconnectOnCarrierLost { get; set; }

    /// <summary>Reconnect automatically when the server stops responding to keep-alives.</summary>
    public bool ReconnectOnNoResponse { get; set; }

    /// <summary>
    /// Reconnect automatically after the BBS kicks the session for cleanup
    /// (see <see cref="CleanupPeriodMinutes"/>).
    /// </summary>
    public bool ReconnectAfterCleanup { get; set; }

    /// <summary>
    /// User has sysop / goto powers on this BBS — flips a few UI affordances
    /// (e.g., the Phase 13 RemoteCommandManager assumes commands like
    /// <c>@goto &lt;player&gt;</c> are allowed without further gating).
    /// </summary>
    public bool HasSysopPowers { get; set; }

    // ----- Terminal dimensions (NAWS, RFC 1073) -----

    /// <summary>
    /// Terminal columns to advertise via Telnet NAWS at connect-time. Defaults
    /// to 80 — MajorMUD's hard-coded rendering grid; non-game BBS doors that
    /// reflow can push higher.
    /// </summary>
    public int TerminalCols { get; set; } = 80;

    /// <summary>Terminal rows to advertise via Telnet NAWS. Defaults to 25.</summary>
    public int TerminalRows { get; set; } = 25;

    // ----- Login automation (Phase 4 PR 4.5c) -----

    /// <summary>
    /// Substring (case-insensitive) the BBS prints when it wants the username.
    /// <see cref="Services.LoginAutomator"/> watches the incoming byte stream
    /// for this pattern and replies with the per-character username.
    /// </summary>
    public string LoginPromptPattern { get; set; } = "Login:";

    /// <summary>
    /// Substring (case-insensitive) the BBS prints when it wants the password.
    /// <see cref="Services.LoginAutomator"/> watches for this pattern and
    /// replies with the password decrypted from <see cref="Services.ICredentialStore"/>.
    /// </summary>
    public string PasswordPromptPattern { get; set; } = "Password:";

    /// <summary>
    /// Per-tab settings deltas at the BBS tier — same shape as
    /// <see cref="GlobalSettings.Settings"/>. Holds anything the user pinned
    /// to "only for this BBS."
    /// </summary>
    public Dictionary<string, JsonElement>? Settings { get; set; }

    /// <summary>
    /// Per-record game-data overrides at the BBS tier. Same shape as
    /// <see cref="GlobalSettings.GameDataOverrides"/>.
    /// </summary>
    public Dictionary<string, Dictionary<string, JsonElement>>? GameDataOverrides { get; set; }
}
