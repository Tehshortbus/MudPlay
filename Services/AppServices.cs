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

    /// <summary>
    /// Single source of truth for "are you sure?" prompts (exit /
    /// hangup / save / delete). Lives at Global tier; mirrored from
    /// <see cref="SettingsService"/> on startup and every save.
    /// </summary>
    public ConfirmService Confirm { get; }

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
    /// Per-character splitter-position memory for two-pane resizable
    /// dialogs. Each dialog calls <see cref="SplitterLayoutStore.AttachGrid"/>
    /// once during construction with a stable id + the Grid to manage;
    /// the store handles restore-on-open and capture-on-close,
    /// hydrating from <see cref="CharacterProfile.SplitterRatios"/> on
    /// profile load and snapshotting back on save.
    /// </summary>
    public SplitterLayoutStore SplitterLayouts { get; }

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
    /// Live party-membership state — roster, leader, per-member HP%/MA%/
    /// position/status-flags. Updated by <see cref="Party"/> from
    /// follows-you / stops-following messages and the multi-line
    /// <c>par</c> table. Bound by the Phase 6 PR 6.6 PartyWindow and
    /// read by the Phase 6 PR 6.2 remote-command engine to gate the
    /// <c>@party &lt;sub&gt;</c> whitelist.
    /// </summary>
    /// <summary>
    /// Client-side terminal line buffer. Routes user keystrokes through
    /// a local 254-char accumulator that only flushes to the wire on
    /// Enter. Without this, engine auto-sends (par poll, AutoParty
    /// invite, @health round-trip, etc.) interleave into half-typed
    /// user input on the server's line buffer and submit as garbage
    /// commands. See <see cref="Terminal.LocalInputBuffer"/>.
    /// </summary>
    public Terminal.LocalInputBuffer InputBuffer { get; } = new();

    public Game.PartyState PartyState { get; }

    /// <summary>
    /// Sole writer of <see cref="PartyState"/> — every observable field
    /// on <see cref="Game.PartyState"/> and <see cref="Game.PartyMember"/>
    /// declares this type via <see cref="OwnerAttribute"/>, enforced by
    /// the Phase 3 PR 3.5 single-writer IL scan.
    /// </summary>
    public Game.PartyManager Party { get; }

    /// <summary>
    /// Phase 6 remote-command engine. Subscribes to <see cref="Chat"/>'s
    /// <see cref="Game.ChatRouter.EntryClassified"/>, identifies
    /// <c>@-prefixed</c> messages from other players, enforces hard-blocks
    /// and per-player <see cref="Models.GameData.PlayerRemoteControls"/>
    /// permissions, and dispatches to registered handlers. PR 6.2 ships
    /// the engine; PR 6.3 onward registers the actual command handlers.
    /// </summary>
    public Game.Remote.RemoteCommandManager RemoteCommands { get; }

    /// <summary>
    /// Phase 6 PR 6.3 — registers the party-essential @-command handlers
    /// against <see cref="RemoteCommands"/>: <c>@health</c>, <c>@where</c>,
    /// <c>@version</c>, <c>@status</c>, <c>@par</c>, <c>@party &lt;sub&gt;</c>,
    /// <c>@wait</c>, <c>@ok</c>. Later phases register additional handlers
    /// without going through this class.
    /// </summary>
    public Game.Remote.PartyEssentialHandlers PartyEssentials { get; }

    /// <summary>
    /// Phase 6 PR 6.4 — drives the on-join <c>@health</c> exchange that
    /// captures each new <see cref="Game.PartyMember"/>'s absolute HP/MA
    /// baseline, plus the periodic <c>par</c> poll (5 s default cadence;
    /// PR 6.9 wires Settings.Party for user-configurable frequency).
    /// </summary>
    public Game.PartyPoller PartyPoller { get; }

    /// <summary>
    /// Phase 6 PR 6.7 — emit side of <c>@wait</c> / <c>@ok</c>. Observes
    /// <see cref="PlayerState.Position"/> transitions and telepaths the
    /// leader when the local character enters / leaves a rest state.
    /// Receive side ships in PR 6.3 via
    /// <see cref="Game.Remote.PartyEssentialHandlers"/>.
    /// </summary>
    public Game.PartyRestSync PartyRest { get; }

    /// <summary>
    /// Phase 6 PR 6.8 — one-to-many @-command sender. Used now for
    /// Auto-Exp-Reset (<c>@Reset</c> broadcast on loop / Auto-Lair start
    /// once Phase 7 wires those triggers); Phase 12's panic / kill
    /// broadcasts will share this service.
    /// </summary>
    public Game.Remote.PartyBroadcaster PartyBroadcaster { get; }

    /// <summary>
    /// Live mirror of the per-character game-menu commands
    /// (<see cref="GameCommands.EntryCommand"/> /
    /// <see cref="GameCommands.ExitCommand"/>). Hydrated from the
    /// Other-tab settings on every profile load + Apply; engines
    /// (<see cref="Game.Remote.HangupHandler"/>, future cleanup-flow
    /// automation) read from here instead of going through
    /// <see cref="Profile"/> directly.
    /// </summary>
    public GameCommands GameCommands { get; } = new();

    /// <summary>
    /// Consumer of <see cref="RemoteCommands"/> for the
    /// <see cref="Models.GameData.PlayerRemoteControls.HangupDisconnect"/>
    /// permission category — currently just <c>@hangup</c>. Sends the
    /// configured <see cref="Services.GameCommands.ExitCommand"/> to
    /// the wire when a permitted sender requests it.
    /// </summary>
    public Game.Remote.HangupHandler Hangup { get; }

    /// <summary>
    /// Sends the configured <see cref="GameCommands.EntryCommand"/>
    /// when the MajorMUD main-menu screen is recognised at the tail
    /// end of the automated BBS-login sequence. Latched closed by
    /// default — only briefly armed when <see cref="Services.LoginAutomator.LoggedIntoGame"/>
    /// fires, so an in-game chat line that happens to look like the
    /// menu (gossip / telepath / room description) can't trick the
    /// engine into auto-entering when the player wanted to stay
    /// out-of-realm.
    /// </summary>
    public Game.MainMenuEntryAutomation MainMenuEntry { get; }

    /// <summary>
    /// Consumer of the per-player
    /// <see cref="Models.GameData.PlayerCustomization.InviteToPartyIfSeen"/>
    /// and
    /// <see cref="Models.GameData.PlayerCustomization.JoinPartyIfInvited"/>
    /// flags. Watches "Also here:" room-occupant lines + incoming
    /// "X invites you to join their party" messages and drives the
    /// matching <c>invite</c> / <c>follow</c> commands. Wire-sender
    /// bound from <see cref="ViewModels.MainWindowViewModel"/>.
    /// </summary>
    public Game.AutoPartyManager AutoParty { get; }

    /// <summary>
    /// Detects the in-game <c>train stats</c> menu round-trip so we can
    /// refresh party state after the user returns to the realm. Armed
    /// by observing outbound <c>train stats</c> on the wire-send path
    /// (<see cref="ViewModels.MainWindowViewModel.SendUserInput"/> calls
    /// <see cref="Game.TrainerMenuTracker.ObserveOutbound"/>) and
    /// confirmed by the anchored <c>"Point Cost Chart"</c> marker.
    /// </summary>
    public Game.TrainerMenuTracker TrainerMenu { get; }

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
    /// Global-tier toolbar visibility mirror. MainWindow toolbar buttons
    /// bind their IsVisible here. Hydrated on startup from the
    /// "Toolbar" entry in <see cref="SettingsService.Current"/>.Settings
    /// and re-hydrated on every <see cref="SettingsService.GlobalSettingsChanged"/>
    /// tick.
    /// </summary>
    public ToolbarConfig Toolbar { get; } = new();

    /// <summary>
    /// AES-GCM encrypt / decrypt for short secrets (BBS passwords).
    /// Ciphertext is stored inline on the owning record (e.g.
    /// <see cref="Models.Profile.BbsCredentials.EncryptedPassword"/>),
    /// so profile JSON stays fully self-contained for backup. The
    /// per-user key lives at <c>Data/.credkey</c>.
    /// </summary>
    public PasswordProtector Passwords { get; } = new();

    /// <summary>
    /// Live cache of imported MajorMUD game data. Loads JSON tables on
    /// demand from <c>Data/game data/{set}/</c>; the active set follows
    /// the pinned BBS's
    /// <see cref="Models.Settings.BbsProfile.ActiveGameDataSet"/> field
    /// (falling back to <see cref="Models.Settings.GlobalSettings.DefaultGameDataSet"/>
    /// when no BBS is pinned). Per-tab consumers (Phase 5 PRs 5.5+)
    /// convert raw <see cref="System.Text.Json.JsonDocument"/> rows into
    /// typed model collections and call <c>EvictTable</c> to drop the
    /// raw bytes.
    /// </summary>
    public GameDataCache GameData { get; } = new();

    /// <summary>
    /// In-memory cache of the active character's
    /// <see cref="Models.GameData.Trigger"/> list + the shared
    /// session-scoped named-variable store used by both triggers and
    /// aliases. Phase 5 PR 5.10 ships the data spine;
    /// MessageRouter integration + runtime action dispatch land in
    /// Phase 13.
    /// </summary>
    public TriggerEngine Triggers { get; }

    /// <summary>
    /// In-memory cache of the active character's
    /// <see cref="Models.GameData.Alias"/> entries. Outgoing-text
    /// mirror of <see cref="Triggers"/>; matches on the first token
    /// of typed input land alongside the editor in a follow-up.
    /// </summary>
    public AliasEngine Aliases { get; }

    /// <summary>
    /// Observed + edited <see cref="Models.GameData.PlayerRecord"/>
    /// store. Phase 5 PR 5.20 ships the spine; the <c>who</c>-output
    /// parser that calls <c>RecordObservation</c> lives with Phase 6
    /// PartyManager.
    /// </summary>
    public PlayerDatabase Players { get; }

    /// <summary>
    /// Loaded character's <see cref="Models.GameData.Macro"/> store.
    /// Surfaced by the Game Data Browser → Macros tab; the Phase 10
    /// MacroManager engine intercepts keystrokes and dispatches from
    /// the same store.
    /// </summary>
    public MacroStore Macros { get; }

    /// <summary>
    /// Runtime keystroke → macro → wire-send bridge. Constructed up-
    /// front; <see cref="MacroDispatcher.SetSender"/> gets bound from
    /// <see cref="MainWindowViewModel"/> after the telnet client is
    /// ready. Pre-binding, key handlers fall through to the normal
    /// terminal path.
    /// </summary>
    public MacroDispatcher MacroDispatcher { get; }

    /// <summary>
    /// Per-character keybindings for built-in app actions (toolbar +
    /// menu shortcuts). Sister service to <see cref="Macros"/> — both
    /// contribute to the unified conflict-detection check so a chord
    /// can never bind to both a macro and a built-in action.
    /// </summary>
    public KeybindingStore Keybindings { get; }

    /// <summary>
    /// Active game-data set's Messages/Responses catalogue. Seeded
    /// from the wcc-derived JSON at <c>Data/Global/Messages.seed.json</c>
    /// (bootstrapped from the bundled <c>Defaults/</c> copy on first
    /// launch), persisted per set at <c>Data/game data/{set}/messages.json</c>.
    /// Surfaced by the Game Data Browser → Messages tab; the Phase 13
    /// HealthManager / CastingDirector consume the same catalogue at
    /// runtime to gate on observed conditions.
    /// </summary>
    public MessageStore Messages { get; private set; } = null!;

    /// <summary>
    /// Active game-data set's Monster Messages catalogue — one record
    /// per Monsters-table row, carrying the parser patterns for every
    /// line a monster can produce in combat (HitYou / HitOther /
    /// DeathLine / ArmorBlock / Dodge / Miss + flavor prefixes).
    /// Generated offline from the wcc <c>monster-messages.json</c>
    /// export joined on <c>Monsters.Number</c>; per-set edits land at
    /// <c>Data/game data/{set}/monster-messages.json</c>.
    /// </summary>
    public MonsterMessageStore MonsterMessages { get; private set; } = null!;

    /// <summary>
    /// Active set's MonsterOverlay seed — Defaults-tier baseline for
    /// per-monster automation behavior (relationship / priority /
    /// NotHostile / DontBackstab). Realm flavor is auto-picked from
    /// the active set's <c>Info.json[0].Legit</c>; bundled seeds for
    /// each realm ship at <c>Defaults/MonsterOverlay.{realm}.seed.json</c>
    /// and bootstrap to the per-install <c>Data/Global/</c> copy at
    /// startup. Consulted by Monsters-tab editing + (future) combat
    /// engines via <see cref="MonsterOverlaySeedStore.GetOverlay(int)"/>.
    /// </summary>
    public MonsterOverlaySeedStore MonsterOverlaySeed { get; private set; } = null!;

    /// <summary>
    /// Active set's ItemOverlay seed — Defaults-tier baseline for
    /// per-item automation behavior (9 Options flags + MinToKeep /
    /// MaxToGet). Realm flavor is auto-picked from the active set's
    /// <c>Info.json[0].Legit</c>; bundled seeds for each realm ship at
    /// <c>Defaults/ItemOverlay.{realm}.seed.json</c> and bootstrap to
    /// the per-install <c>Data/Global/</c> copy at startup. Consulted
    /// by the Items tab editing + (future) loot / equipment engines
    /// via <see cref="ItemOverlaySeedStore.GetOverlay(int)"/>.
    /// </summary>
    public ItemOverlaySeedStore ItemOverlaySeed { get; private set; } = null!;

    /// <summary>
    /// Background audit comparing player-facing spells in the active
    /// set against the Messages catalogue's Links field — surfaces a
    /// summary LogEntry per audit run so users know which spells
    /// don't have a parser entry. Bound in <see cref="Initialize"/>
    /// once <see cref="GameData"/> + <see cref="Messages"/> + the
    /// <see cref="Log"/> sink are all live.
    /// </summary>
    public SpellCoverageAuditor SpellCoverage { get; private set; } = null!;


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

        // Copy any missing seed files from the bundled Defaults/ next to
        // the exe into the user-writable Data/Global/ location. Runs
        // once per launch; pre-existing Global seeds (user-edited or
        // user-curated) are never overwritten.
        AppPaths.EnsureGlobalSeedsBootstrapped();

        // Best-effort log rotation. Default retention window; Settings.Other
        // will surface a knob in Phase 4.
        DebugLogWriter.PruneOldLogs();

        // One-shot migration: relocate legacy flat-file layouts
        // (Data/BBS/{name}.json, Data/profiles/{name}.json) into the
        // per-name folders the rest of the bootstrap now expects.
        // Runs BEFORE any store touches disk; idempotent on
        // already-migrated trees.
        LogService bootstrapLog = new();
        DataMigration.RunIfNeeded(bootstrapLog);

        _current = new AppServices(bootstrapLog);
        return _current;
    }

    private AppServices(LogService bootstrapLog)
    {
        Log = bootstrapLog;
        // Late-bind the cache's log sink so SwitchSet emits the swap
        // audit entries (load / unload / swap) without coupling the
        // cache to AppServices construction order.
        GameData.Log = bootstrapLog;
        Settings = new SettingsService();
        Profile = new ProfileService();
        Bbs = new BbsProfileStore();

        // Resolver subscribes to Profile events for active-BBS tracking; build
        // it before Load() below so it catches the auto-load's ProfileLoaded
        // (it also self-syncs from Profile.Current as a defensive fallback).
        // The active-set provider lets game-data override I/O target the
        // currently active MDB set's per-set side-files.
        Resolver = new SettingsResolver(Settings, Bbs, Profile, () => GameData.ActiveSet);

        Dialogs = new DialogService();
        Confirm = new ConfirmService(Dialogs);
        // Hydrate the live confirm mirror from Global tier now and on
        // every subsequent global-settings save (Settings → BBS's
        // confirm checkboxes write to Global through this path).
        ApplyConfirmFromGlobalSettings();
        Settings.GlobalSettingsChanged += _ => ApplyConfirmFromGlobalSettings();
        // Log already set by ctor parameter — bootstrap log carries the
        // DataMigration entries from before AppServices was constructed.
        Panels = new FloatingPanelHost();
        WindowLayouts = new WindowLayoutStore(Profile);
        SplitterLayouts = new SplitterLayoutStore(Profile);
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
        PartyState = new Game.PartyState();
        Party = new Game.PartyManager(Router, PartyState);
        // Mirror the local character's live HP/MA into the self party
        // row on every prompt — without this the self row only updates
        // on a par poll, so per-prompt damage between polls doesn't
        // surface in the PartyWindow.
        Party.AttachPlayerState(PlayerState);
        Tick = new Game.TickEngine(Router);
        Regen = new Game.RegenTracker(PlayerState);
        // RemoteCommands is constructed AFTER Chat / Party / Players are
        // ready (they're all dependencies). Handler registration ships
        // in PR 6.3 — the engine is empty here; we just wire the plumbing.
        Triggers = new TriggerEngine(Profile, Chat, Log);
        Aliases = new AliasEngine(Profile);
        Macros = new MacroStore(Profile);
        MacroDispatcher = new MacroDispatcher(Macros);
        Keybindings = new KeybindingStore(Profile);
        // PlayerDatabase: BBS-tier observations + Char-tier customisations.
        // Wires its own subscriptions (ProfileLoaded / ProfileClosed /
        // BbsPinApplied / ProfileSaving) so both layers track the
        // active BBS + loaded character. Active-BBS delegate routes
        // through ResolveActiveBbs so Quick Connect and the BBS pin
        // resolution chain stay the single source of truth.
        Players = new PlayerDatabase(Profile, ResolveActiveBbs);
        // Phase 6 PR 6.2 — engine. Phase 7 / Phase 12 register additional
        // handlers without touching the engine.
        RemoteCommands = new Game.Remote.RemoteCommandManager(Chat, PartyState, Players, Log);
        // Phase 6 PR 6.3 — first consumer; registers the party-essential
        // handler set against the engine.
        PartyEssentials = new Game.Remote.PartyEssentialHandlers(RemoteCommands, PlayerState, PartyState);
        // Phase 6 PR 6.4 — drives the on-join @health exchange + the
        // periodic par poll. Wire-sender + cadence-from-settings hookup
        // happens in MainWindowViewModel / PR 6.9.
        PartyPoller = new Game.PartyPoller(Chat, PartyState, Party);
        // Phase 6 PR 6.7 — emit side of @wait/@ok. Observes our own
        // position transitions and telepaths the leader when we enter
        // / leave a rest state. Wire-sender hookup in MainWindowVM.
        PartyRest = new Game.PartyRestSync(PartyState);
        // Phase 6 PR 6.8 — one-to-many @-command sender. Auto-Exp-Reset
        // is the first consumer (Phase 7 LoopManager will call
        // BroadcastExpReset on loop start); the broadcaster's also the
        // canonical spot for Phase 12 panic / kill broadcasts.
        PartyBroadcaster = new Game.Remote.PartyBroadcaster(PartyState);
        // Auto-party flag consumer — invites flagged players when they
        // appear in our room, accepts invites from flagged players.
        // Wire-sender is bound by MainWindowViewModel once the telnet
        // client is up; pre-binding, the engine still observes events
        // but produces no wire output.
        // TrainerMenuTracker before AutoPartyManager so we can pass it
        // in as a constructor dep — AutoParty subscribes to MenuExited
        // to re-fire `invite` for any party member that the trainer-
        // menu round-trip dropped from the follower's view.
        TrainerMenu = new Game.TrainerMenuTracker(Router, PartyState, Log);
        AutoParty = new Game.AutoPartyManager(Router, Players, PartyState, TrainerMenu, Log);
        // @hangup handler — sends the configured GameCommands.ExitCommand
        // when an authorised sender (HangupDisconnect permission on
        // the Players-tab record) telepaths @hangup.
        Hangup = new Game.Remote.HangupHandler(RemoteCommands, GameCommands);
        // Main-menu entry automation — armed by MainWindowVM when
        // LoginAutomator.LoggedIntoGame fires; observes the
        // MainMenuEnterRealm pattern and sends GameCommands.EntryCommand
        // exactly once per arm. Closed by default so in-game chat
        // matching the menu pattern can never trick it.
        MainMenuEntry = new Game.MainMenuEntryAutomation(Router, GameCommands, Log);

        // Bridge: load persisted panel layouts on profile load; snapshot back
        // into the profile DTO just before serialization on save.
        Profile.ProfileLoaded += p => Panels.ApplyLayouts(p.PanelLayouts);

        // Phase 6: PartyManager needs the local character's name so its
        // par-row parser can tag the right row IsSelf=true (par's
        // "Given Family" name is compared against the loaded profile
        // name). Cleared on profile close so IsSelf goes back to false
        // for every row across the swap.
        Profile.ProfileLoaded += p => Party.LocalCharacterName = p.Name;
        Profile.ProfileClosed += ()  => Party.LocalCharacterName = null;
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

        // Bridge: keep the live ToolbarConfig in sync with the loaded
        // character profile (Char-tier — each character can have its own
        // toolbar layout). Re-hydrates on every profile load AND on every
        // ProfileMutated tick (which fires from the Settings → Toolbar
        // Apply path).
        Profile.ProfileLoaded += _ => ApplyToolbarFromActiveProfile();
        Profile.ProfileClosed += ResetToolbarToDefaults;
        Profile.ProfileMutated += _ => ApplyToolbarFromActiveProfile();

        // Bridge: per-character Party / Talk / Other settings into
        // their live engine knobs. Pre-fix the section VMs handled
        // their own ApplyToServices on Apply, but the load-from-disk
        // path required the user to OPEN the Settings window before
        // the cadence / engine flags actually took effect — so
        // running two characters with different par-poll cadences
        // both ran at the 5 s default until the user visited Settings
        // on each. These subscriptions push the per-character DTOs
        // automatically on every profile load + mutate.
        Profile.ProfileLoaded  += _ => ApplyPartyFromActiveProfile();
        Profile.ProfileClosed  += ResetPartyToDefaults;
        Profile.ProfileMutated += _ => ApplyPartyFromActiveProfile();
        Profile.ProfileLoaded  += _ => ApplyTalkFromActiveProfile();
        Profile.ProfileClosed  += ResetTalkToDefaults;
        Profile.ProfileMutated += _ => ApplyTalkFromActiveProfile();
        Profile.ProfileLoaded  += _ => ApplyOtherFromActiveProfile();
        Profile.ProfileClosed  += ResetOtherToDefaults;
        Profile.ProfileMutated += _ => ApplyOtherFromActiveProfile();

        // Bridge: follow the pinned BBS's preferred game-data set.
        // Active set lives at BBS scope (every character on the same
        // realm shares the same MDB). Resolution chain:
        //   pinned BBS's ActiveGameDataSet
        //     → GlobalSettings.DefaultGameDataSet
        //       → null (no set active).
        // Re-resolve on every signal that could change the answer:
        // a fresh profile load, an explicit BBS pin from Settings →
        // BBS Apply, a re-pin via ProfileMutated, and profile close.
        Profile.ProfileLoaded  += _ => ApplyActiveGameDataSet();
        Profile.BbsPinApplied  += _ => ApplyActiveGameDataSet();
        Profile.ProfileMutated += _ => ApplyActiveGameDataSet();
        Profile.ProfileClosed  += ApplyActiveGameDataSet;

        // Messages catalogue is paired per game-data set on disk
        // (Data/Global/Messages/{set-name}.json) — reload whenever the
        // active set changes so the Browser tab and runtime engines
        // see the right realm's catalogue.
        Messages = new MessageStore(Log);
        GameData.ActiveSetChanged += Messages.Load;
        // Monster-message catalogue parallels the spell-message one —
        // same per-set storage + universal seed fallback pattern.
        MonsterMessages = new MonsterMessageStore(Log);
        GameData.ActiveSetChanged += MonsterMessages.Load;
        // Realm-flavored seed for the per-monster overlay (Defaults
        // tier). Switching sets reads the new Info.Legit and reloads
        // the matching realm's seed; runtime consumers retrieve
        // baselines via MonsterOverlaySeed.GetOverlay(number).
        MonsterOverlaySeed = new MonsterOverlaySeedStore(Log);
        GameData.ActiveSetChanged += MonsterOverlaySeed.Load;
        // Realm-flavored seed for the per-item overlay (Defaults tier).
        // Parallel of MonsterOverlaySeed — same Info.Legit-driven realm
        // pick + per-set reload; consumers retrieve baselines via
        // ItemOverlaySeed.GetOverlay(number).
        ItemOverlaySeed = new ItemOverlaySeedStore(Log);
        GameData.ActiveSetChanged += ItemOverlaySeed.Load;
        // Triggers split storage: GameData-scoped triggers live in the
        // active set's per-set triggers.json; Profile-scoped triggers
        // stay on CharacterProfile.Triggers. The engine reloads its
        // GameData slice on every set switch — the Profile slice is
        // owned by ProfileLoaded, wired inside TriggerEngine's ctor.
        GameData.ActiveSetChanged += Triggers.OnActiveSetChanged;
        if (GameData.ActiveSet is not null)
            Triggers.OnActiveSetChanged(GameData.ActiveSet);

        // Coverage audit — fires on every set switch + every Messages
        // CollectionChanged; emits a summary LogEntry tagged
        // SpellCoverageAuditor.LogSource that the LogPane's
        // double-click handler routes back into a detail window. The
        // detail-handler registration itself lives in App startup
        // (it needs DialogService to spawn the modeless window).
        SpellCoverage = new SpellCoverageAuditor(GameData, Messages, Log);

        // Always start with a blank draft. Auto-loading the most recently used
        // profile is a deliberate opt-in feature that ships in a later PR
        // (Settings → General toggle); until then the user picks the profile
        // they want via File → Open profile / Recent profiles.
        Profile.LoadBlank();

        // Track which profile was last loaded so the future "auto-load last"
        // setting has a value to read.
        Profile.ProfileLoaded += OnProfileLoaded;

        // Best-effort startup prune of the Players table — drops records the
        // user hasn't seen in GlobalSettings.PlayerCleanupDays days
        // (per-record DontAutoDelete opts out). The cleanup window is global
        // and editable from Settings → General → Player database.
        int cleanupDays = Settings.Current.PlayerCleanupDays;
        if (cleanupDays > 0)
        {
            int removed = Players.PurgeStale(cleanupDays, DateTime.UtcNow);
            if (removed > 0)
                Log.Info("PlayerDatabase",
                    $"Pruned {removed} stale player record(s) older than {cleanupDays} day(s).");
        }
    }

    private void ApplyToolbarFromActiveProfile()
    {
        Models.Profile.ToolbarSettings dto = ReadSection<Models.Profile.ToolbarSettings>(Profile.Current, "Toolbar");
        Toolbar.ApplyFrom(dto);
    }

    private void ResetToolbarToDefaults()
    {
        Toolbar.ApplyFrom(new Models.Profile.ToolbarSettings());
    }

    /// <summary>
    /// Generic per-section settings reader. Returns a fresh default-
    /// constructed DTO when the profile is null, has no Settings dict,
    /// is missing the named entry, or the JSON is malformed — the
    /// callers all want a non-null DTO they can apply unconditionally.
    /// </summary>
    private static T ReadSection<T>(Models.Profile.CharacterProfile? profile, string key)
        where T : new()
    {
        if (profile?.Settings is null) return new T();
        if (!profile.Settings.TryGetValue(key, out System.Text.Json.JsonElement json)) return new T();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(json.GetRawText()) ?? new T();
        }
        catch
        {
            return new T();
        }
    }

    /// <summary>
    /// Push the loaded character's <see cref="Models.Profile.PartySettings"/>
    /// into the live <see cref="PartyPoller"/> / <see cref="Party"/> /
    /// <see cref="PartyBroadcaster"/>. Subscribed to
    /// <see cref="ProfileService.ProfileLoaded"/> +
    /// <see cref="ProfileService.ProfileMutated"/> so a per-character
    /// cadence (e.g. par-poll-frequency=15s) is honoured the moment the
    /// profile auto-loads at startup — not just when the user opens the
    /// Settings window. Pre-fix the cadence stayed at the 5 s default
    /// for every character because the section-VM-only ApplyToServices
    /// never fired until Settings was opened.
    /// </summary>
    public void ApplyPartyFromActiveProfile()
    {
        Models.Profile.PartySettings dto = ReadSection<Models.Profile.PartySettings>(Profile.Current, "Party");
        PartyPoller.SetParCadence(TimeSpan.FromSeconds(Math.Clamp(dto.ParPollFrequencySec, 1, 60)));
        Party.AutoInviteEnabled = dto.AutoInviteReconnecting;
        Party.DisconnectGraceWindow = TimeSpan.FromSeconds(Math.Clamp(dto.IfLeadingWaitTotalSec, 0, 3600));
        Party.LocalRankPreference = dto.Rank;
        PartyBroadcaster.AutoExpResetEnabled = dto.ResetStatisticsOnLoopStart;
        AutoParty.JoinNagInitialDelay = TimeSpan.FromSeconds(Math.Clamp(dto.JoinNagInitialDelaySec, 1, 60));
        AutoParty.JoinNagFrequency    = TimeSpan.FromSeconds(Math.Clamp(dto.JoinNagFrequencySec,    1, 60));
        AutoParty.JoinNagMaxTotal     = TimeSpan.FromSeconds(Math.Clamp(dto.JoinNagMaxTotalSec,     5, 600));
    }

    private void ResetPartyToDefaults()
    {
        Models.Profile.PartySettings defaults = new();
        PartyPoller.SetParCadence(TimeSpan.FromSeconds(defaults.ParPollFrequencySec));
        Party.AutoInviteEnabled = defaults.AutoInviteReconnecting;
        Party.DisconnectGraceWindow = TimeSpan.FromSeconds(defaults.IfLeadingWaitTotalSec);
        Party.LocalRankPreference = defaults.Rank;
        PartyBroadcaster.AutoExpResetEnabled = defaults.ResetStatisticsOnLoopStart;
        AutoParty.JoinNagInitialDelay = TimeSpan.FromSeconds(defaults.JoinNagInitialDelaySec);
        AutoParty.JoinNagFrequency    = TimeSpan.FromSeconds(defaults.JoinNagFrequencySec);
        AutoParty.JoinNagMaxTotal     = TimeSpan.FromSeconds(defaults.JoinNagMaxTotalSec);
    }

    /// <summary>
    /// Push the loaded character's <see cref="Models.Profile.TalkSettings"/>
    /// into the live <see cref="RemoteCommands"/> engine. Same shape +
    /// rationale as <see cref="ApplyPartyFromActiveProfile"/>.
    /// </summary>
    public void ApplyTalkFromActiveProfile()
    {
        Models.Profile.TalkSettings dto = ReadSection<Models.Profile.TalkSettings>(Profile.Current, "Talk");
        RemoteCommands.MasterDisable          = dto.DisallowAllRemoteCommands;
        RemoteCommands.DisablePartyWhitelist  = dto.DisallowPartyCommandsFromLeader;
        RemoteCommands.DisableTelepathChannel = dto.DisallowRemoteFromTelepaths;
        RemoteCommands.DisableGangpathChannel = dto.DisallowRemoteFromGangpaths;
        RemoteCommands.DisableLocalChannel    = dto.DisallowRemoteFromLocal;
        RemoteCommands.WarnOnDenial           = dto.WarnOnInvalidRemoteCommand;
        RemoteCommands.FailureMessage         = dto.RemoteCommandFailureMessage ?? string.Empty;
    }

    private void ResetTalkToDefaults()
    {
        Models.Profile.TalkSettings defaults = new();
        RemoteCommands.MasterDisable          = defaults.DisallowAllRemoteCommands;
        RemoteCommands.DisablePartyWhitelist  = defaults.DisallowPartyCommandsFromLeader;
        RemoteCommands.DisableTelepathChannel = defaults.DisallowRemoteFromTelepaths;
        RemoteCommands.DisableGangpathChannel = defaults.DisallowRemoteFromGangpaths;
        RemoteCommands.DisableLocalChannel    = defaults.DisallowRemoteFromLocal;
        RemoteCommands.WarnOnDenial           = defaults.WarnOnInvalidRemoteCommand;
        RemoteCommands.FailureMessage         = defaults.RemoteCommandFailureMessage ?? string.Empty;
    }

    /// <summary>
    /// Push the loaded character's <see cref="Models.Profile.OtherSettings"/>
    /// into the live engine knobs (currently
    /// <see cref="Game.Remote.RemoteCommandManager.MaxSuicideLivesThreshold"/>).
    /// Same shape + rationale as <see cref="ApplyPartyFromActiveProfile"/>.
    /// </summary>
    public void ApplyOtherFromActiveProfile()
    {
        Models.Profile.OtherSettings dto = ReadSection<Models.Profile.OtherSettings>(Profile.Current, "Other");
        RemoteCommands.MaxSuicideLivesThreshold = Math.Clamp(dto.MaxSuicideLivesThreshold, 0, 9);
        // Game-menu commands — HangupHandler consumes ExitCommand
        // synchronously on @hangup; the future cleanup-flow + first-
        // login automation will consume both. Blank entries fall back
        // to the DTO defaults (E / =x) so a misconfiguration can't
        // leave the engine with empty wire-sends.
        GameCommands.EntryCommand = string.IsNullOrWhiteSpace(dto.GameEntryCommand)
            ? new Models.Profile.OtherSettings().GameEntryCommand
            : dto.GameEntryCommand;
        GameCommands.ExitCommand  = string.IsNullOrWhiteSpace(dto.GameExitCommand)
            ? new Models.Profile.OtherSettings().GameExitCommand
            : dto.GameExitCommand;
    }

    private void ResetOtherToDefaults()
    {
        Models.Profile.OtherSettings defaults = new();
        RemoteCommands.MaxSuicideLivesThreshold = defaults.MaxSuicideLivesThreshold;
        GameCommands.EntryCommand = defaults.GameEntryCommand;
        GameCommands.ExitCommand  = defaults.GameExitCommand;
    }

    /// <summary>
    /// Pull <see cref="Models.Settings.ConfirmSettings"/> out of the
    /// Global-tier <c>"Confirm"</c> bucket and push it into
    /// <see cref="Confirm"/>. Confirm prefs are Global tier (one
    /// install-wide preference, not per-character) so this fires off
    /// <see cref="SettingsService.GlobalSettingsChanged"/>, not the
    /// per-profile events.
    /// </summary>
    private void ApplyConfirmFromGlobalSettings()
    {
        Models.Settings.ConfirmSettings dto =
            ReadGlobalSection<Models.Settings.ConfirmSettings>("Confirm");
        Confirm.ApplyFrom(dto);
    }

    /// <summary>
    /// Read a typed DTO out of the Global-tier <c>Settings</c>
    /// dictionary, returning a default-constructed instance when the
    /// bucket is missing or unparseable.
    /// </summary>
    private T ReadGlobalSection<T>(string key) where T : new()
    {
        Dictionary<string, System.Text.Json.JsonElement>? bucket = Settings.Current.Settings;
        if (bucket is null) return new T();
        if (!bucket.TryGetValue(key, out System.Text.Json.JsonElement json)) return new T();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(json) ?? new T();
        }
        catch
        {
            return new T();
        }
    }

    /// <summary>
    /// Resolve which BBS the runtime should treat as active. Pin on
    /// the loaded character profile wins; otherwise fall back to the
    /// first BBS alphabetically (a user on a blank draft with one
    /// saved BBS should still get its connection info, display
    /// settings, and ActiveGameDataSet applied without manual
    /// intervention). Returns <c>null</c> only when there's no pin
    /// AND zero BBSes saved on disk. Mirrors the resolution logic
    /// the main window's title-bar / Connect button use, so the
    /// game-data + display + cache layers see the same active BBS
    /// the user sees in the chrome.
    /// </summary>
    public Models.Settings.BbsProfile? ResolveActiveBbs()
    {
        string? name = Profile.Current?.BbsName;
        if (!string.IsNullOrEmpty(name))
        {
            Models.Settings.BbsProfile? pinned = Bbs.Get(name);
            if (pinned is not null) return pinned;
        }

        string? first = Bbs.ListNames()
            .OrderBy(static n => n, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return first is null ? null : Bbs.Get(first);
    }

    /// <summary>
    /// Recompute the active game-data set from the BBS-pin chain and
    /// flip <see cref="GameData"/> if it differs. Idempotent — the
    /// cache short-circuits no-op switches so calling this on every
    /// profile / BBS / mutate signal is cheap.
    /// </summary>
    private void ApplyActiveGameDataSet()
    {
        Models.Settings.BbsProfile? bbs = ResolveActiveBbs();
        string? resolved = bbs?.ActiveGameDataSet ?? Settings.Current.DefaultGameDataSet;
        GameData.SwitchSet(resolved);
    }

    private void ApplyDisplayFromActiveBbs()
    {
        Models.Settings.BbsProfile values = ResolveActiveBbs() ?? new Models.Settings.BbsProfile();
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
