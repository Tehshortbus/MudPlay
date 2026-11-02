using System.Text.Json;
using System.Text.Json.Serialization;
using MudPlay.Services;

namespace MudPlay.Models.Settings;

// Root DTO for Global/global.json — the Global tier of the settings
// hierarchy. Holds app-wide deltas (the things every character shares) plus
// pointers the launcher needs before any profile is loaded. Anything in
// this file must be resolvable without a character profile loaded (Global
// tier is the highest non-Char layer in SettingsResolver).
public sealed class GlobalSettings
{
    // JSON schema version. Bump whenever the on-disk format changes in a
    // non-backward-compatible way; migration logic keys off this.
    public int SchemaVersion { get; set; } = 1;

    // (BBS, character) reference of the most recently loaded profile. Used
    // at startup to auto-load the last session; null on first run. Profiles
    // are BBS-scoped now, so the bare character name is no longer a unique
    // key.
    public Profile.ProfileRef? LastUsedProfile { get; set; }

    // When true, startup loads LastUsedProfile instead of opening a blank draft
    // (Settings → General). Off by default — the app opens blank and the user
    // picks / builds a profile, the historical behaviour.
    public bool AutoLoadLastProfile { get; set; }

    // When true (the default), the app checks GitHub Releases for a newer build — on
    // startup, and again twice a day so a long-running client doesn't sit on a stale
    // verdict. If one exists it flags it (red banner on the splash, a crawl across the
    // title bar) — it never downloads or installs on its own; the user triggers the
    // update from Help / Tools → Update the Client. App-wide (Global tier); only meaningful
    // for a published self-contained install, a no-op for a `dotnet run` dev build.
    //
    // The on-disk key keeps the original startup-only name: the setting shipped in
    // 3.78.0 and renaming the key would silently reset it for everyone who has one.
    [JsonPropertyName("CheckForUpdatesOnStartup")]
    public bool AutoCheckForUpdates { get; set; } = true;

    // The profile to auto-load at startup, or null to open the blank draft: the
    // last-used profile when AutoLoadLastProfile is on, else null. Stays null on
    // first run (no LastUsedProfile yet) even with the toggle on, so a fresh
    // install still opens blank.
    public Profile.ProfileRef? StartupProfile()
        => AutoLoadLastProfile ? LastUsedProfile : null;

    // Up to MaxRecentProfilesShown profiles the user has loaded recently,
    // ordered most-recent-first. Each entry is a (BBS, character)
    // reference. Drives the File → Recent profiles submenu.
    public List<Profile.ProfileRef>? RecentProfiles { get; set; }

    // Hard cap on RecentProfiles retention (and the number of menu slots) — the
    // ceiling the "recent profiles shown" picker can reach.
    public const int MaxRecentProfilesShown = 10;

    // How many recent profiles the File → Recent submenu actually shows
    // (Settings → General, Global tier). The full retained list keeps up to
    // MaxRecentProfilesShown so raising this reveals more without re-loading.
    [SettingRange(0, MaxRecentProfilesShown)]
    public int RecentProfilesShown { get; set; } = 5;

    // Fallback game-data set name used when no BBS is pinned (or the pinned
    // BBS has no ActiveGameDataSet of its own). Once a BBS is pinned its
    // setting takes over via the resolution chain in AppServices.
    public string? DefaultGameDataSet { get; set; }

    // Auto-cleanup window for the Players table (see
    // PlayerDatabase.PurgeStale). Records whose LastSeenUtc falls outside
    // this window get dropped at startup so the table doesn't accumulate
    // one-shot tourists from every session. Per-record DontAutoDelete opts
    // out. 0 or negative disables auto-cleanup entirely.
    [SettingRange(0, 3650)]
    public int PlayerCleanupDays { get; set; } = 90;

    // Per-line navigation-line appearance overrides (colour + thickness), edited in
    // Settings → General. Delta only — null (or an all-default NavLineStyles) means
    // the map draws its factory pens (NavLineDefaults). Install-wide (Global tier)
    // so one look applies to every character. Read live by NavigationViewModel,
    // which pushes it to MapControl.
    public NavLineStyles? NavLines { get; set; }

    // Which colours the 16 base ANSI colours are drawn in: the standard ones, a
    // scheme for a kind of colour blindness, or the user's own (Settings → General).
    // Null means the standard colours. Install-wide (Global tier), and pushed to
    // Terminal.AnsiPalette at launch and on every save.
    public TerminalColorSettings? TerminalColors { get; set; }

    // The Program Log window's switches (Debug / Combat channels, Auto-collect
    // logs, Hop timing, Capture unrecognized messages). Null until first set, which
    // lets the first character loaded after they moved here hand over its own.
    public LogDiagnosticsSettings? LogDiagnostics { get; set; }

    // The Navigation map's other floors: the floors reached through up/down exits
    // drawn dimmed around the one shown. Which side is drawn is the map's Overlays
    // chip alone. Settings → General holds how many up/down steps away they reach,
    // and the share of a floor that may land on rooms already drawn before it's
    // left out as a different place stacked under or over this one (100 keeps
    // every floor).
    public Game.Map.OtherFloorsMode MapOtherFloors { get; set; } = Game.Map.OtherFloorsMode.Both;

    // Settings saved before the chip had up-only / down-only carry an on/off flag.
    // Reading an off turns the floors off; it is never written back.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? MapShowOtherFloors
    {
        get => null;
        set { if (value == false) MapOtherFloors = Game.Map.OtherFloorsMode.Off; }
    }
    public const int MaxMapOtherFloorsLevels = 99;
    [SettingRange(1, MaxMapOtherFloorsLevels)]
    public int MapOtherFloorsLevels { get; set; } = 10;
    [SettingRange(0, 100)]
    public int MapOtherFloorsMaxOverlapPercent { get; set; } = 50;
    // Whether route lines run on through the other floors' rooms. Off keeps them
    // to the floor shown: where the game's own layout of two floors doesn't line
    // up, a route crossing between them draws as a jarring diagonal.
    public bool MapRouteLinesOnOtherFloors { get; set; } = true;

    // Map legend overlay's user-dragged position, in pixels from the top-left of
    // the map viewport. Null (the default) parks it bottom-left. Install-wide
    // (Global tier), persisted on drop and re-clamped into the visible map when
    // the legend is toggled back on (so a position left off-view by a since-shrunk
    // window snaps back). Both non-null or both null — a half-set pair is ignored.
    [SettingRange(-100_000, 100_000)]
    public double? MapLegendX { get; set; }
    [SettingRange(-100_000, 100_000)]
    public double? MapLegendY { get; set; }

    // Master enable for the Great Pyramid climb solver — when true (default) a
    // walk-to a pyramid room hands off to PyramidSolver; when false the walk
    // fails normally like any unroutable destination. Install-wide (Global tier);
    // read live via PyramidSolver.Enabled. Surfaced in Settings → Other.
    public bool PyramidSolverEnabled { get; set; } = true;

    // Master enable for the random-teleport asylum (maze) solver — when true
    // (default) a walk-to a maze-pocket room hands off to TeleportMazeSolver; when
    // false the walk fails normally. Install-wide (Global tier); read live via
    // TeleportMazeSolver.Enabled. Surfaced in Settings → Other.
    public bool AsylumSolverEnabled { get; set; } = true;

    // Master enable for Paradigm transport-token routing — when true (default) a
    // walk-to whose destination a held token reaches meaningfully faster surfaces a
    // (never-auto) token card in the route picker; when false, token cards never
    // appear. Install-wide (Global tier); read live at plan time. Surfaced in
    // Settings → Other (Paradigm realms only).
    public bool EnableTokenRoutes { get; set; } = true;

    // Only surface a token route when it saves at least this many rooms over the
    // overland walk (a token use costs gold, a daily charge, and wipes buffs, so it's
    // only worth offering for a big saving). Install-wide; default 50.
    [SettingRange(1, 300)]
    public int TokenRouteMinRoomsShorter { get; set; } = 50;

    // How many seconds the Navigation map holds where the user browsed to (a pan,
    // zoom, floor step or Center on…) before it follows the player again.
    // Install-wide; 0 = follow again straight away. Surfaced in Settings → Other.
    [SettingRange(0, 300)]
    public int MapRecenterHoldSeconds { get; set; } = 15;

    // Party token-route behaviour when some members fail to token across after the
    // regroup retries. false (default) — the leader does NOT token; it fails out in
    // the room and sits so you can sort out the stragglers. true — the leader uses its
    // token anyway and walks on, leaving the failed members behind. Install-wide.
    public bool TokenUseWhenPartyIncomplete { get; set; }

    // Snap MudPlay's panel windows to each other's edges as you drag them, and
    // move the whole snapped cluster when the main window is dragged. When false,
    // windows float freely. Install-wide (Global tier — a UI preference, not
    // per-character); read live by WindowSnapManager. Surfaced in Settings → General.
    public bool SnapWindows { get; set; } = true;

    // Draw the windows on the graphics card rather than with the CPU. Off by
    // default: the graphics driver costs a client about 120 MB, and a text client
    // draws with the CPU for a few percent of one core (user, 2026-11-02). The
    // renderer is picked once, before the settings load, so ClientEnvironment
    // reads this from the file at launch and a change takes effect on the next.
    // Install-wide (Global tier). Surfaced in Settings → General.
    public bool DrawWithGraphicsCard { get; set; }

    // Local control API — a loopback-only HTTP endpoint exposing live client
    // state, the program log, scrollback, and a bug-report-equivalent dump, so a
    // wedge can be inspected while it is happening instead of reconstructed from
    // a log tail afterwards. Install-wide (Global tier); read live by
    // LocalApiServer.ApplySettings. Surfaced in Settings → General.
    //
    // Default FALSE, deliberately: this opens a listening socket, so it is
    // something the user turns on, never something they discover they were
    // running. Access needs the bearer token from <app data>/.apitoken on top of
    // being on loopback — see LocalApiAuth for why loopback alone isn't enough.
    public bool LocalApiEnabled { get; set; }

    // Port for the above. 6683 spells MMUD on a phone keypad.
    [SettingRange(1024, 65535)]
    public int LocalApiPort { get; set; } = LocalApiDefaultPort;

    // Whether the API may issue commands that end the session or can't be undone:
    // @suicide, @hangup, @relog, and the raw-line passthrough (which is destructive
    // by nature — it can carry anything the game accepts). Classified from
    // RemoteCommandCatalog's permission categories, not a hand-kept list.
    //
    // Default FALSE and separate from LocalApiEnabled on purpose: the common case
    // is reading state and driving movement, and a bug in an automated caller
    // shouldn't be able to suicide or disconnect the character.
    public bool LocalApiAllowDestructive { get; set; }

    public const int LocalApiDefaultPort = 6683;

    // Once true, the first-run setup tutorial (the guided overlay that walks a
    // brand-new user through importing game data, adding a BBS + character, and
    // connecting) never auto-shows again. Set when the user completes it or picks
    // "Don't show again". Install-wide (Global tier). The tutorial still auto-shows
    // when this is false AND a prerequisite is missing; Help → Visual Guides
    // re-opens it on demand regardless of this flag. Read by MainWindow on open.
    public bool FirstRunTutorialDismissed { get; set; }

    // The built-in boards (BuiltInBbses) already added to this install's BBS list,
    // so one the user removes isn't added back on the next launch.
    public List<string> BuiltInBbsesAdded { get; set; } = new();

    // Step reached / finished per Visual Guide (Help → Visual Guides).
    public GuideProgress Guides { get; set; } = new();

    // Per-tab settings deltas — keyed by tab name (Health / Combat / Talk /
    // etc.). Each value is a partial DTO for that tab containing only the
    // fields the user pinned to the Global tier. SettingsResolver merges
    // these across all four tiers.
    public Dictionary<string, JsonElement>? Settings { get; set; }
}
