namespace FujinTerm.Services;

/// <summary>
/// Lightweight singleton service holder. POCO — no DI container.
/// Every cross-cutting service the app owns is exposed as an instance property
/// here as it's introduced by later phase PRs (profile/settings I/O, message
/// bus, dialog spawner, log service, importers, game-data cache, etc.).
/// </summary>
/// <remarks>
/// Per-character / per-game-data lifetime is event-driven: services subscribe
/// to <c>ProfileService.ProfileLoaded</c> (added in PR 0.3) and
/// <c>GameDataCache.ActiveSetChanged</c> (Phase 5) and reload their per-scope
/// state in those handlers. There is intentionally no IoC container —
/// explicit subscription and explicit teardown beats magic resolution at this
/// scale (see CLAUDE.md "Architecture rules").
/// </remarks>
public sealed class AppServices
{
    private static AppServices? _current;

    /// <summary>The active service holder. <see cref="Initialize"/> must be called first.</summary>
    public static AppServices Current => _current
        ?? throw new InvalidOperationException(
            "AppServices not initialized — call AppServices.Initialize() during app startup.");

    /// <summary>Owns <c>Data/Global/global.json</c> — the Global settings tier.</summary>
    public SettingsService Settings { get; }

    /// <summary>Owns the currently loaded character profile (Character tier).</summary>
    public ProfileService Profile { get; }

    /// <summary>Owns <c>Data/BBS/*.json</c> — the BBS tier.</summary>
    public BbsProfileStore Bbs { get; }

    /// <summary>
    /// Single read / write API for the 4-tier settings + game-data override
    /// hierarchy (Defaults → Global → BBS → Character).
    /// </summary>
    public SettingsResolver Resolver { get; }

    /// <summary>Modeless-only window spawner (no <c>ShowDialog</c> wrapper).</summary>
    public DialogService Dialogs { get; }

    /// <summary>App-wide severity-tagged ring-buffer log. Status bar + Phase 1 log pane subscribe.</summary>
    public LogService Log { get; }

    /// <summary>Docking / floating panel framework (single-UserControl reparented).</summary>
    public FloatingPanelHost Panels { get; }

    /// <summary>
    /// Per-character top-level window position + size memory. Each
    /// window calls <see cref="WindowLayoutStore.AttachWindow"/> once
    /// during construction with a stable id; the store handles
    /// restore-on-open and capture-on-close, hydrating from
    /// <see cref="CharacterProfile.WindowBounds"/> on profile load and
    /// snapshotting back on save.
    /// </summary>
    public WindowLayoutStore WindowLayouts { get; }

    /// <summary>
    /// Ring buffer of recent cleaned (post-IAC) bytes from the live Telnet
    /// connection. Feeds the Wire Inspector window and any future
    /// "what did the server just say" diagnostic.
    /// </summary>
    public WireBuffer Wire { get; }

    /// <summary>
    /// Central pattern bus. Every line-aware subsystem (ChatRouter,
    /// Triggers, automation engines) registers patterns + handlers here;
    /// <see cref="LineExtractor.LineEmitted"/> is forwarded into
    /// <see cref="MessageRouter.Dispatch"/>.
    /// </summary>
    public MessageRouter Router { get; }

    /// <summary>
    /// Classifies chat / realm-event lines into <see cref="Game.ChatLogEntry"/>
    /// events. ChatHistoryStore and the Conversation window (PR 2.5)
    /// subscribe to <c>EntryClassified</c>.
    /// </summary>
    public Game.ChatRouter Chat { get; }

    /// <summary>
    /// App-singleton chat history. Survives profile swap / connect /
    /// disconnect; cleared only on app exit or explicit
    /// <see cref="Game.ChatHistoryStore.Clear"/>.
    /// </summary>
    public Game.ChatHistoryStore ChatHistory { get; }

    /// <summary>
    /// Live player state — HP / mana / position / mana type. Updated by
    /// <see cref="Player"/> from every prompt line; bound by the status
    /// bar, the Phase 9 Workshop STATS section, and Phase 13 automation
    /// engines that gate on HP / MP thresholds.
    /// </summary>
    public Game.PlayerState PlayerState { get; }

    /// <summary>
    /// Parses MajorMUD status-line prompts into <see cref="PlayerState"/>.
    /// Sole writer of the state's HP / MA / position / mana-type fields
    /// (Phase 3 PR 3.5 enforces this via the single-writer IL scan).
    /// </summary>
    public Game.PromptParser Player { get; }

    /// <summary>
    /// Scans the post-IAC wire stream for status-line prompts. Feeds
    /// <see cref="Player"/> directly so prompts overwritten in place on
    /// a single row (server CR + erase-line + rewrite) don't get lost
    /// the way they would going through <see cref="Terminal.LineExtractor"/>.
    /// </summary>
    public WirePromptScanner PromptScanner { get; }

    /// <summary>
    /// Sniffs the post-IAC wire stream for "BBS shutting down in N minutes"
    /// announcements. The connect lifecycle in MainWindowViewModel reads
    /// <see cref="CleanupWarningWatcher.Latest"/> on disconnect to decide
    /// whether to arm an auto-reconnect.
    /// </summary>
    public CleanupWarningWatcher Cleanup { get; } = new();

    /// <summary>
    /// Combat / HP / MA tick heartbeat. Status bar countdown binds here;
    /// Phase 13 automation engines subscribe to <c>CombatTickElapsed</c> +
    /// the regen ticks.
    /// </summary>
    public Game.TickEngine Tick { get; }

    /// <summary>
    /// Observation-based regen tracker. Folds upward HP / MA deltas into
    /// per-position running averages; subscribed to by the status bar and
    /// Phase 13 HealthManager for tick-aware automation.
    /// </summary>
    public Game.RegenTracker Regen { get; }

    /// <summary>
    /// Live mirror of the loaded character profile's Display settings.
    /// The Settings → Display section writes through to this so changes
    /// (font size in particular) apply without restarting the app.
    /// </summary>
    public DisplayConfig Display { get; } = new();

    /// <summary>
    /// AES-GCM encrypt / decrypt for short secrets (BBS passwords).
    /// Ciphertext is stored inline on the owning record (e.g.
    /// <see cref="Models.Profile.BbsCredentials.EncryptedPassword"/>),
    /// so profile JSON stays fully self-contained for backup. The
    /// per-user key lives at <c>Data/.credkey</c>.
    /// </summary>
    public PasswordProtector Passwords { get; } = new();

    /// <summary>
    /// Construct and register the singleton. Idempotent — repeated calls return
    /// the existing instance. Touches <see cref="AppPaths"/> to force
    /// directory creation before any service tries to read or write a file.
    /// </summary>
    public static AppServices Initialize()
    {
        if (_current is not null) return _current;

        // Read any AppPaths member to fire its static constructor and create
        // the Data/ tree on disk before anyone else needs it.
        _ = AppPaths.DataRoot;

        // Best-effort log rotation. Default retention window; Settings.Other
        // will surface a knob in Phase 4.
        DebugLogWriter.PruneOldLogs();

        _current = new AppServices();
        return _current;
    }

    private AppServices()
    {
        Settings = new SettingsService();
        Profile = new ProfileService();
        Bbs = new BbsProfileStore();

        // Resolver subscribes to Profile events for active-BBS tracking; build
        // it before Load() below so it catches the auto-load's ProfileLoaded
        // (it also self-syncs from Profile.Current as a defensive fallback).
        Resolver = new SettingsResolver(Settings, Bbs, Profile);

        Dialogs = new DialogService();
        Log = new LogService();
        Panels = new FloatingPanelHost();
        WindowLayouts = new WindowLayoutStore(Profile);
        Wire = new WireBuffer();
        Router = new MessageRouter();

        // Populate the default pattern registry now so later subsystems
        // (ChatRouter, automation engines in Phase 13, the Phase 5 Trigger
        // UI's "pick a built-in pattern" picker) can subscribe by
        // KnownPatterns.Whatever id.
        Patterns.DefaultPatterns.Seed(Router);

        // First MessageRouter consumer — subscribes to the conversation +
        // realm-event patterns. ChatHistoryStore + ConversationWindow
        // (Phase 2 PR 2.4 / 2.5) subscribe to its EntryClassified event.
        Chat = new Game.ChatRouter(Router);
        ChatHistory = new Game.ChatHistoryStore(Chat);
        PlayerState = new Game.PlayerState();
        PromptScanner = new WirePromptScanner();
        Player = new Game.PromptParser(PromptScanner, PlayerState);
        Tick = new Game.TickEngine(Router);
        Regen = new Game.RegenTracker(PlayerState);

        // Bridge: load persisted panel layouts on profile load; snapshot back
        // into the profile DTO just before serialization on save.
        Profile.ProfileLoaded += p => Panels.ApplyLayouts(p.PanelLayouts);
        Profile.ProfileClosed += () => Panels.ApplyLayouts(layouts: null);
        Profile.ProfileSaving += p => p.PanelLayouts = Panels.SnapshotLayouts();

        // Bridge: keep the live DisplayConfig in sync with the active BBS.
        // Font size + scrollback are BBS-tier (different BBSes warrant
        // different legibility tuning) so we re-resolve on every profile
        // load AND on every ProfileMutated tick (which fires from the BBS
        // section's Apply path after a save).
        Profile.ProfileLoaded += _ => ApplyDisplayFromActiveBbs();
        Profile.ProfileClosed += ResetDisplayToDefaults;
        Profile.ProfileMutated += _ => ApplyDisplayFromActiveBbs();

        // Always start with a blank draft. Auto-loading the most recently used
        // profile is a deliberate opt-in feature that ships in a later PR
        // (Settings → General toggle); until then the user picks the profile
        // they want via File → Open profile / Recent profiles.
        Profile.LoadBlank();

        // Track which profile was last loaded so the future "auto-load last"
        // setting has a value to read.
        Profile.ProfileLoaded += OnProfileLoaded;
    }

    private void ApplyDisplayFromActiveBbs()
    {
        string? bbsName = Profile.Current?.BbsName;
        Models.Settings.BbsProfile? bbs = string.IsNullOrEmpty(bbsName) ? null : Bbs.Get(bbsName);
        Models.Settings.BbsProfile values = bbs ?? new Models.Settings.BbsProfile();
        Display.FontSize = values.FontSize;
        Display.ScrollbackLines = values.ScrollbackLines;
        Display.TerminalCols = values.TerminalCols;
        Display.TerminalRows = values.TerminalRows;
    }

    private void ResetDisplayToDefaults()
    {
        Models.Settings.BbsProfile defaults = new();
        Display.FontSize = defaults.FontSize;
        Display.ScrollbackLines = defaults.ScrollbackLines;
        Display.TerminalCols = defaults.TerminalCols;
        Display.TerminalRows = defaults.TerminalRows;
    }

    private void OnProfileLoaded(Models.Profile.CharacterProfile profile)
    {
        if (Profile.CurrentProfileName is null) return;
        if (Settings.Current.LastUsedProfileName == Profile.CurrentProfileName) return;

        Settings.Current.LastUsedProfileName = Profile.CurrentProfileName;
        Settings.Save();
    }
}
