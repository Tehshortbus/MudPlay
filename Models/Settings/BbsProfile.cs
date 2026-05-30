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

    /// <summary>
    /// Imported game-data set folder this BBS uses by default. Tracked
    /// at BBS scope (not per character) because every character on the
    /// same realm shares the same MajorMUD MDB, and switching realms
    /// almost always means switching BBSes. The folder name is one of
    /// the subdirectories under <see cref="AppPaths.GameDataRoot"/>.
    /// <c>null</c> falls back to <c>GlobalSettings.DefaultGameDataSet</c>.
    /// Surfaced + writable via the File → Game Data → Active set menu.
    /// </summary>
    public string? ActiveGameDataSet { get; set; }

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
    /// Minutes the BBS is offline for its nightly auto-cleanup. Used by
    /// the <see cref="ReconnectAfterCleanup"/> schedule: when the
    /// CleanupWarningWatcher catches a "shutting down in N minutes"
    /// announcement, the client arms a reconnect at
    /// <c>warning_observed_at + N + CleanupPeriodMinutes</c>.
    /// <c>0</c> means "dial back the moment shutdown_at is past."
    /// </summary>
    public int CleanupPeriodMinutes { get; set; }

    /// <summary>Reconnect automatically when the previous connect attempt failed.</summary>
    public bool ReconnectOnFailedConnect { get; set; }

    /// <summary>Reconnect automatically after the carrier signal drops mid-session.</summary>
    public bool ReconnectOnCarrierLost { get; set; }

    /// <summary>Reconnect automatically when the server stops responding to keep-alives.</summary>
    public bool ReconnectOnNoResponse { get; set; }

    /// <summary>
    /// Seconds of TCP-level idle before the OS starts probing the
    /// connection with TCP keepalive packets. <c>0</c> disables
    /// keepalive entirely (the OS default — typically ~2 hours idle —
    /// is way too long for a BBS).
    /// </summary>
    /// <remarks>
    /// We pair this with hardcoded probe interval = 10s and retry
    /// count = 3, so once the idle elapses the OS declares the socket
    /// dead within ~30s. The <see cref="ReconnectOnNoResponse"/>
    /// toggle then decides whether to auto-dial back.
    /// </remarks>
    public int NoResponseTimeoutSeconds { get; set; }

    /// <summary>
    /// Reconnect automatically after the BBS kicks the session for cleanup
    /// (see <see cref="CleanupPeriodMinutes"/>).
    /// </summary>
    public bool ReconnectAfterCleanup { get; set; }

    // ----- Terminal dimensions (NAWS, RFC 1073) -----

    /// <summary>
    /// Terminal columns to advertise via Telnet NAWS at connect-time. Defaults
    /// to 80 — MajorMUD's hard-coded rendering grid; non-game BBS doors that
    /// reflow can push higher.
    /// </summary>
    public int TerminalCols { get; set; } = 80;

    /// <summary>Terminal rows to advertise via Telnet NAWS. Defaults to 25.</summary>
    public int TerminalRows { get; set; } = 25;

    /// <summary>
    /// Terminal canvas font size in points. Per-BBS so a high-density door
    /// game and a chatty BBS can each get their own legibility tuning.
    /// </summary>
    public double FontSize { get; set; } = 16.0;

    /// <summary>
    /// How many scrolled-off rows the backscroll ring retains.
    /// Applies on next launch — in-place ring resize would need to copy /
    /// drop rows and is intentionally deferred.
    /// </summary>
    public int ScrollbackLines { get; set; } = 10_000;

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
