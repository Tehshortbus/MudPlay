using System.Text.Json;

namespace FujinTerm.Models.Settings;

// Root DTO for Data/BBS/{bbs-name}.json — the BBS tier of the settings
// hierarchy. Connection info plus deltas the user pinned to "only for this
// BBS." Per-character credentials are stored separately under each
// CharacterProfile; this file describes the BBS itself.
public sealed class BbsProfile
{
    // JSON schema version (see GlobalSettings.SchemaVersion for the contract).
    public int SchemaVersion { get; set; } = 1;

    // Display name + filename key for this BBS.
    public string Name { get; set; } = string.Empty;

    // Imported game-data set folder this BBS uses by default. Tracked at
    // BBS scope (not per character) because every character on the same
    // realm shares the same MajorMUD MDB, and switching realms almost
    // always means switching BBSes. The folder name is one of the
    // subdirectories under AppPaths.GameDataRoot. null falls back to
    // GlobalSettings.DefaultGameDataSet. Surfaced + writable via the
    // File → Game Data → Active set menu.
    public string? ActiveGameDataSet { get; set; }

    // Hostname or IP address the Telnet client connects to.
    public string Host { get; set; } = string.Empty;

    // TCP port; defaults to the Telnet well-known port.
    public int Port { get; set; } = 23;

    // Optional URL the user wants the Help → {BBS site} ↗ menu entry to
    // open (the BBS's web site, wiki, Discord — whatever the operator
    // publishes). null hides the link; the menu entry stays present but
    // disabled.
    public string? WebsiteUrl { get; set; }

    // ----- Connection / retry behaviour -----

    // How many connect attempts (initial + retries) before giving up.
    public int MaxRedials { get; set; } = 3;

    // Seconds to wait between connect attempts.
    public int RedialPauseSeconds { get; set; } = 5;

    // Minutes the BBS is offline for its nightly auto-cleanup. Used by the
    // ReconnectAfterCleanup schedule: when the CleanupWarningWatcher catches
    // a "shutting down in N minutes" announcement, the client arms a
    // reconnect at warning_observed_at + N + CleanupPeriodMinutes. 0 means
    // "dial back the moment shutdown_at is past."
    public int CleanupPeriodMinutes { get; set; }

    // Reconnect automatically when the previous connect attempt failed.
    public bool ReconnectOnFailedConnect { get; set; }

    // Reconnect automatically after the carrier signal drops mid-session.
    public bool ReconnectOnCarrierLost { get; set; }

    // Reconnect automatically when the server stops responding to keep-alives.
    public bool ReconnectOnNoResponse { get; set; }

    // Seconds of TCP-level idle before the OS starts probing the connection
    // with TCP keepalive packets. 0 disables keepalive entirely (the OS
    // default — typically ~2 hours idle — is way too long for a BBS).
    //
    // We pair this with hardcoded probe interval = 10s and retry count = 3,
    // so once the idle elapses the OS declares the socket dead within ~30s.
    // The ReconnectOnNoResponse toggle then decides whether to auto-dial
    // back.
    public int NoResponseTimeoutSeconds { get; set; }

    // Manage the whole nightly-cleanup cycle for this BBS. Two halves:
    // (1) proactive log-off — when a shutdown warning is observed, the
    // CleanupLogoutOrchestrator waits for a safe room, exits the realm to
    // MajorMUD's main menu, and drops the carrier before the BBS yanks us;
    // (2) auto-redial — reconnect after the cleanup window (see
    // CleanupPeriodMinutes). One toggle governs both.
    public bool ReconnectAfterCleanup { get; set; }

    // ----- Game-menu commands -----
    // The two commands the client sends at the MajorMUD main menu to
    // enter / leave the realm. Stored per-BBS rather than per-character
    // because the menu key bindings are a property of the realm /
    // front-end, not the character — every character on the same BBS
    // uses the same picks. Defaults are the standard MajorMUD picks
    // ("E" = enter the realm, "=x" = log off from the main menu).

    // Sent at the main menu to enter the realm. Default "E". Consumed by
    // MainMenuEntryAutomation when the client detects the main menu after a
    // (re)connect.
    public string GameEntryCommand { get; set; } = "E";

    // Sent at the main menu to log off. Default "=x". Fired by
    // HangupHandler on a permitted @hangup and by the cleanup-warning
    // logout flow.
    public string GameExitCommand { get; set; } = "=x";

    // ----- Realm mechanics -----

    // The negative-HP floor at which a MajorMUD character actually dies.
    // Hitting 0 HP only *drops* you (bleeding out — can't move/fight/cast,
    // but revivable and still able to hang up); death happens when HP falls
    // to this value. Stored per-BBS because the floor is a realm balance
    // knob, not a per-character stat. Seeded at the standard -25; clamp
    // consumers treat any positive value as 0. The emergency auto-hangup
    // reads this so it keeps firing through the whole bleeding-out window
    // (from the hang-trigger down to — but not past — this floor).
    public int PlayerDiesAtHp { get; set; } = -25;

    // Let the client trace the realm's true death floor from observed *slow*
    // deaths and refine PlayerDiesAtHp toward it. The seed (-25) is only a
    // guess; a bleed-out crosses the floor one tick at a time and lands right
    // at it, so its HP reading is an accurate measurement. When on, the death-
    // floor tracer overwrites PlayerDiesAtHp from such deaths (an overkill's
    // over-negative reading is discarded and never refines). Off pins the
    // user's manual value. Default on — the whole point of the setting is to
    // start from a guess and let real deaths correct it.
    public bool AutoRefineDeathFloor { get; set; } = true;

    // ----- Terminal dimensions (NAWS, RFC 1073) -----

    // Terminal columns to advertise via Telnet NAWS at connect-time.
    // Defaults to 80 — MajorMUD's hard-coded rendering grid; non-game BBS
    // doors that reflow can push higher.
    public int TerminalCols { get; set; } = 80;

    // Terminal rows to advertise via Telnet NAWS. Defaults to 25.
    public int TerminalRows { get; set; } = 25;

    // Terminal canvas font size in points. Per-BBS so a high-density door
    // game and a chatty BBS can each get their own legibility tuning.
    public double FontSize { get; set; } = 16.0;

    // How many scrolled-off rows the backscroll ring retains. Applies on
    // next launch — in-place ring resize would need to copy / drop rows and
    // is intentionally deferred.
    public int ScrollbackLines { get; set; } = 4_000;

    // Per-tab settings deltas at the BBS tier — same shape as
    // GlobalSettings.Settings. Holds anything the user pinned to "only for
    // this BBS."
    public Dictionary<string, JsonElement>? Settings { get; set; }
}
