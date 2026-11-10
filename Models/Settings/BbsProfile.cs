using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Models.Settings;

// Root DTO for BBS/{bbs}/bbs.json — the board itself: connection, retry and
// display settings, plus the realms it hosts (RealmProfile), which carry
// everything specific to one version of the game on the board. Per-character
// credentials are stored separately under each CharacterProfile.
public sealed class BbsProfile
{
    // JSON schema version (see GlobalSettings.SchemaVersion for the contract).
    public int SchemaVersion { get; set; } = 1;

    // Display name + filename key for this BBS.
    public string Name { get; set; } = string.Empty;


    // Hostname or IP address the Telnet client connects to.
    public string Host { get; set; } = string.Empty;

    // TCP port; defaults to the Telnet well-known port.
    [SettingRange(1, 65535)]
    public int Port { get; set; } = 23;

    public Dictionary<string, Game.Gmcp.GmcpMode> GmcpModes { get; set; } = new();

    // The board's server has completed a GMCP handshake with a contract the client
    // takes: the Settings BBS tab shows the server-data modes only for such a board.
    public bool GmcpOffered { get; set; }

    // Optional URL the user wants the Help → {BBS site} ↗ menu entry to
    // open (the BBS's web site, wiki, Discord — whatever the operator
    // publishes). null leaves the entry disabled (when shown). Edited under
    // Settings → Toolbar + Shortcuts alongside the global Help-link list.
    public string? WebsiteUrl { get; set; }

    // Whether the "BBS site ↗" entry appears in the Help menu for this BBS.
    // Independent of WebsiteUrl — unchecking hides the entry even with a URL
    // saved. Default on so a freshly-set URL surfaces without extra clicks.
    public bool ShowWebsiteInHelp { get; set; } = true;

    // ----- Connection / retry behaviour -----

    // How many connect attempts (initial + retries) before giving up.
    [SettingRange(1, 9999)]
    public int MaxRedials { get; set; } = 3;

    // Seconds to wait between connect attempts.
    [SettingRange(1, 300)]
    public int RedialPauseSeconds { get; set; } = 5;

    // Never give up: retry forever at a fixed 3s pause. Overrides both
    // MaxRedials (→ unlimited) and RedialPauseSeconds (→ 3s) everywhere a
    // retry fires. Does NOT change WHICH events trigger a reconnect — the
    // ReconnectOn* toggles still gate that; this only changes the count and
    // pause once a retry is already in play.
    public bool InfiniteRetries { get; set; }

    // Minutes the BBS is offline for its nightly auto-cleanup. With
    // ReconnectAfterCleanup on, when the CleanupWarningWatcher catches
    // a "shutting down in N minutes" announcement, the client arms a
    // reconnect at warning_observed_at + N + CleanupPeriodMinutes. 0 means
    // "dial back the moment shutdown_at is past."
    [SettingRange(0, 600)]
    public int CleanupPeriodMinutes { get; set; }

    // Reconnect automatically when the previous connect attempt failed.
    public bool ReconnectOnFailedConnect { get; set; }

    // Reconnect automatically after the carrier signal drops mid-session.
    public bool ReconnectOnCarrierLost { get; set; }

    // Reconnect automatically when the server stops responding to keep-alives.
    public bool ReconnectOnNoResponse { get; set; }

    // Seconds of TCP-level idle before the OS starts probing the connection
    // with TCP keepalive packets. Defaults to 20 — a lost carrier is caught
    // within ~50s (idle + the ~30s probe tail; on Linux TelnetClient also caps
    // the actively-sending case at idle + 30s via TCP_USER_TIMEOUT). 0 disables
    // the idle keepalive probing (on Linux TelnetClient still caps dead-connection
    // detection at ~60s); the OS default of ~2h idle would be way too long for a BBS.
    //
    // We pair this with hardcoded probe interval = 10s and retry count = 3,
    // so once the idle elapses the OS declares the socket dead within ~30s.
    // The ReconnectOnNoResponse toggle then decides whether to auto-dial
    // back.
    [SettingRange(0, 3600)]
    public int NoResponseTimeoutSeconds { get; set; } = 20;

    // Manage the whole nightly-cleanup cycle for this BBS. Two halves:
    // (1) proactive log-off — when a shutdown warning is observed, the
    // CleanupLogoutOrchestrator waits for a safe room, exits the realm to
    // MajorMUD's main menu, and drops the carrier before the BBS yanks us;
    // (2) auto-redial — reconnect after the cleanup window (see
    // CleanupPeriodMinutes). One toggle governs both.
    public bool ReconnectAfterCleanup { get; set; }

    // Board-specific player-disconnect line, matched IN ADDITION to the
    // built-in "X just disconnected!!!" / "X just hung up!!!" forms. Some
    // boards (Playpen) don't emit those and instead print a custom BBS-level
    // logoff line — and worse, key it on the player's ACCOUNT name, not their
    // character name. This pattern lets the user teach the client that line so
    // a party member's disconnect is caught (and the party waits for them
    // instead of sprinting off). Uses the same literal syntax as triggers:
    // {name} captures the disconnecting player's name (matched against a
    // member's account-name override, else their character name), * swallows a
    // varying run (e.g. Playpen's trailing "Lines in Use: N" count). Empty =
    // only the built-in forms are watched (default). Example for Playpen:
    //   ►►► [{name}] logs OFF*
    public string? DisconnectPattern { get; set; }


    // ----- Realms -----

    // The realms this board hosts (a BBS can offer several versions of the game
    // from its menu). Always at least one once loaded — BbsProfileStore adds a
    // realm named after the BBS when there's none. Each character profile names
    // its realm (CharacterProfile.Realm); a missing / unknown name means the first.
    public List<RealmProfile> Realms { get; set; } = new();

    // The realm a character assigned to realmName plays on: that realm, or the
    // first when the name is blank / unknown. Null only when Realms is empty.
    public RealmProfile? RealmFor(string? realmName) =>
        (string.IsNullOrWhiteSpace(realmName)
            ? null
            : Realms.FirstOrDefault(r => string.Equals(r.Name, realmName, StringComparison.OrdinalIgnoreCase)))
        ?? Realms.FirstOrDefault();

    // ----- Terminal dimensions (NAWS, RFC 1073) -----

    // Terminal columns to advertise via Telnet NAWS at connect-time.
    // Defaults to 80 — MajorMUD's hard-coded rendering grid; non-game BBS
    // doors that reflow can push higher.
    [SettingRange(40, 200)]
    public int TerminalCols { get; set; } = 80;

    // Terminal rows to advertise via Telnet NAWS. Defaults to 25.
    [SettingRange(20, 100)]
    public int TerminalRows { get; set; } = 25;

    // How many scrolled-off rows the backscroll ring retains. A change applies
    // at once: the ring is resized in place, dropping the oldest rows when it
    // shrinks.
    [SettingRange(100, 1_000_000)]
    public int ScrollbackLines { get; set; } = 4_000;

    // How many rows one mouse-wheel notch scrolls in the Backscroll window.
    // Line-by-line (1) is exhaustingly slow through a big buffer; default 5.
    [SettingRange(1, 50)]
    public int BackscrollWheelLines { get; set; } = 5;

    // Per-tab settings deltas at the BBS tier — same shape as
    // GlobalSettings.Settings. Holds anything the user pinned to "only for
    // this BBS."
    public Dictionary<string, JsonElement>? Settings { get; set; }
}
