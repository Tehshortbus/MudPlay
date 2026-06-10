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

    /// <summary>
    /// Session-only diagnostic switches surfaced in the Log pane menu
    /// (Phase 9 — combat-verbose / round-trace umbrella). Consumers
    /// (e.g. <see cref="Game.Combat.RoundDamageTracker"/>) read this
    /// instead of per-character settings because verbose tracing isn't
    /// a per-character affordance — it's a "while I'm debugging right
    /// now" knob that resets on app launch.
    /// </summary>
    public LogDiagnosticState LogDiagnostics { get; } = new();

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
    /// <c>@version</c>, <c>@status</c>, <c>@lives</c>,
    /// <c>@party</c> (status query + sub-command dispatch),
    /// <c>@invite</c>, <c>@join</c>, <c>@wait</c>, <c>@ok</c>. Later
    /// phases register additional handlers without going through this
    /// class.
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
    /// Consumer of <see cref="RemoteCommands"/> for the MovePlayer
    /// category: @goto / @loop / @lair / @stop / @rego. Wires the
    /// remote walk-to / loop-start / lair-cycle / pause / resume
    /// dispatch into the Phase 7 Navigation stack.
    /// </summary>
    public Game.Remote.MovePlayerHandler MoveRemote { get; private set; } = null!;

    /// <summary>
    /// Centralised room-search resolver. Backs the Navigation rail
    /// search box, the Loop / Lair editor "Add room" rows, the
    /// Center-on dialog, and the @goto remote handler.
    /// </summary>
    public RoomSearchService RoomSearch { get; private set; } = null!;

    /// <summary>
    /// Consumer of <see cref="RemoteCommands"/> for the
    /// <see cref="Models.GameData.PlayerRemoteControls.ExecuteCommands"/>
    /// permission category's <c>@do &lt;command&gt;</c> passthrough.
    /// Joins the sender's args back into a single command string and
    /// ships it on the wire. Engine-level hard-blocks (reroll,
    /// suicide-lives-threshold) already gate the catalogue's
    /// destructive verbs before this handler runs.
    /// </summary>
    public Game.Remote.DoHandler Do { get; }

    /// <summary>
    /// Phase 9 Cluster 5d — <c>@auto-*</c> remote command family
    /// (party member toggles our AutoMode flags). Backed by the
    /// loaded character profile's <c>General</c> section.
    /// </summary>
    public Game.Remote.AutoModeRemoteHandler AutoMode { get; private set; } = null!;

    /// <summary>
    /// Leader-side <c>@comeback</c> party-pickup flow — pauses the
    /// running movement engine, walks to recover a stranded follower
    /// (explicit room or backtrack along the just-walked path), re-
    /// invites + awaits follow, then resumes the captured engine. The
    /// <see cref="Game.Remote.PartyComebackManager.MaxBacktrackRooms"/>
    /// budget is pushed from Settings → Other.
    /// </summary>
    public Game.Remote.PartyComebackManager PartyComeback { get; private set; } = null!;

    /// <summary>
    /// PR 6.2 — follower-side <c>@comeback</c> sender. Detects being left
    /// behind (a movement-failure line just before "You are no longer
    /// following X.") and telepaths <c>@comeback</c> to the leader.
    /// <see cref="Game.Remote.ComebackRequester.Enabled"/> is pushed from
    /// Settings → Other.
    /// </summary>
    public Game.Remote.ComebackRequester ComebackRequest { get; private set; } = null!;

    /// <summary>
    /// Drives the <c>@trap &lt;direction&gt;</c> auto-disarm flow:
    /// search → disarm state machine + FIFO request queue + Stats-
    /// skill gate. Bound by <see cref="TrapRemote"/>'s handler at
    /// dispatch time, configured via the
    /// <see cref="Models.Profile.OtherSettings.MaxTrapSearchAttempts"/>
    /// / <c>MaxTrapDisarmAttempts</c> knobs in Settings → Other.
    /// </summary>
    public Game.TrapDisarmManager TrapDisarm { get; }

    /// <summary>
    /// Walker's door-handling FSM — bash / pick / open with
    /// configurable attempt caps. Subscribes to <see cref="Router"/>
    /// for the door-message patterns; the walker calls
    /// <see cref="Game.Map.DoorOpenManager.Enqueue"/> at door-exit
    /// step time and resumes on the callback's terminal
    /// <see cref="Game.Map.DoorOpenResult"/>. Attempt caps + verb
    /// preference (bash vs pick) read live from Settings.Other on
    /// each request.
    /// </summary>
    public Game.Map.DoorOpenManager Door { get; }

    /// <summary>
    /// Walker's hidden-exit reveal FSM — fires <c>sea &lt;dir&gt;</c>
    /// in a retry loop until the exit appears on the room display.
    /// Subscribes to <see cref="RoomTracker.StateChanged"/> for the
    /// "exit now visible" signal; max retries pulled live from
    /// <see cref="Models.Profile.OtherSettings.MaxHiddenSearchAttempts"/>.
    /// </summary>
    public Game.Map.HiddenExitRevealManager HiddenSearch { get; }

    /// <summary>
    /// Auth boundary + queue gate for <c>@trap</c>: parses the
    /// direction, runs the channel-aware Traps-skill gate, and hands
    /// off to <see cref="TrapDisarm"/>. <c>@trap stop</c> drains the
    /// queue + aborts the in-flight request.
    /// </summary>
    public Game.Remote.TrapHandler TrapRemote { get; }

    /// <summary>
    /// Consumer of <see cref="RemoteCommands"/> for <c>@suicide</c>.
    /// Authorised callers (Elevated-Commands permission, lives above
    /// the suicide threshold) trigger the suicide round-trip; on
    /// "Invalid password specified." the handler telepaths the
    /// caller back so they know our stored password is stale.
    /// </summary>
    public Game.Remote.SuicideHandler Suicide { get; private set; } = null!;

    /// <summary>Snapshot of the most recent <c>stat</c>-screen parse. Written exclusively by <see cref="Stats"/>.</summary>
    public Game.PlayerStats PlayerStats { get; } = new();

    /// <summary>
    /// Parses the in-game <c>stat</c> screen and writes every field
    /// onto <see cref="PlayerStats"/>. Feeds
    /// <see cref="RemoteCommands"/>'s LivesProvider so the
    /// <c>@suicide</c> hard-block has a real value to gate against.
    /// </summary>
    public Game.StatParser Stats { get; private set; } = null!;

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
    /// One-flag pause switch wrapping every engine's wire-sender.
    /// Raised by <see cref="Game.SuicidePasswordTracker"/> while a
    /// password-entry prompt is active so engine auto-sends don't
    /// pollute the input.
    /// </summary>
    public EngineSendGate EngineGate { get; } = new();

    /// <summary>
    /// Two-flag one-shot coordinator for "intentional hangup" intent.
    /// Set by every engine that deliberately drops the carrier
    /// (<see cref="Game.Remote.HangupHandler"/> today; Phase 13
    /// hang-up-if-naked / hang-up-if-low-HP automation later).
    /// Consumed by <see cref="ViewModels.MainWindowViewModel"/> (to
    /// suppress reactive auto-reconnect) and by
    /// <see cref="Game.MainMenuEntryAutomation"/> (to suppress the
    /// auto-entry latch on the next connect so the user can read
    /// what's on screen and decide).
    /// </summary>
    public HangupSignal HangupSignal { get; } = new();

    /// <summary>
    /// Passive observer for the in-game <c>set suicide</c> /
    /// <c>suicide</c> password flows. Locks
    /// <see cref="EngineGate"/> for the duration of each prompt and
    /// captures the user-typed new password (committed to the
    /// profile's <see cref="Models.Profile.CharacterProfile.EncryptedSuicidePassword"/>
    /// on the server-side <c>Password Changed</c> confirmation).
    /// </summary>
    public Game.SuicidePasswordTracker SuicidePassword { get; private set; } = null!;

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
    /// Loaded character's scheduled / lifecycle events store +
    /// dispatcher (Phase 8 PR 8.1). CRUD surface for the Settings →
    /// Events tab; <see cref="Game.Events.EventManager.Fire"/> routes
    /// to <see cref="Walker"/> / <see cref="LoopRunner"/> /
    /// <see cref="AutoLair"/> / the bound wire sender.
    /// </summary>
    public Game.Events.EventManager Events { get; private set; } = null!;

    /// <summary>
    /// Trigger sources for <see cref="Events"/> (Phase 8 PR 8.2).
    /// Owns the AtTime ticker, per-event Every-timers, and the
    /// connection-aware Logon / Re-log latch. MainWindowVM calls
    /// <see cref="Game.Events.EventScheduler.NotifyConnected"/> /
    /// <see cref="Game.Events.EventScheduler.NotifyDisconnected"/> as
    /// its <see cref="TelnetClient"/> raises those events, since the
    /// telnet client is per-connection and not a stable singleton.
    /// Logoff events fire via
    /// <see cref="Game.Events.EventManager.FireLogoffEvents"/>
    /// directly from the user-initiated disconnect path.
    /// </summary>
    public Game.Events.EventScheduler EventScheduler { get; private set; } = null!;

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
    /// Phase 9 PR 9.0b — turns the wire's <c>Also here:</c> line into
    /// a classified Player / Monster / Unknown list. Feeds
    /// <see cref="CombatTracker"/>'s gate decisions and the LogPane's
    /// unknown-entity click-to-fix dialog.
    /// </summary>
    public Game.Combat.RoomEntityClassifier RoomClassifier { get; private set; } = null!;

    /// <summary>
    /// Phase 9 PR 9.0b — owns <see cref="PlayerState.InCombat"/> and
    /// the <see cref="Game.Map.MovementCoordinator.CombatGate"/> hold
    /// state. Cleared automatically when the room is free of
    /// engageable monsters.
    /// </summary>
    public Game.Combat.CombatStateTracker CombatTracker { get; private set; } = null!;

    /// <summary>
    /// Phase 9 PR 9.0c — aggregates combat lines into per-round
    /// <see cref="Game.Combat.RoundSummary"/> records, keeping the
    /// last 50 in a ring buffer. <c>CastingDirector</c> (PR 9.D) and
    /// Phase 11 <c>CombatSessionTracker</c> consume the
    /// <c>RoundComplete</c> event.
    /// </summary>
    public Game.Combat.RoundDamageTracker RoundDamage { get; private set; } = null!;

    /// <summary>
    /// Phase 9 PR 9.0d — observes the "You have been slain by..."
    /// line and emits <see cref="Game.Combat.DeathLineWatcher.PlayerDied"/>.
    /// DeathRecoveryManager (PR 9.I) is the primary consumer; other
    /// engines subscribe for their own death-clean-up paths.
    /// </summary>
    public Game.Combat.DeathLineWatcher DeathWatcher { get; private set; } = null!;

    /// <summary>
    /// Phase 9 PR 9.A — auto-attack engine. Picks a target from
    /// <see cref="RoomClassifier"/>'s last observation and sends the
    /// configured attack command when
    /// <see cref="Models.Profile.CombatSettings.MasterAutoAttackEnabled"/>
    /// is on. Wire sender is bound by <see cref="MainWindowViewModel"/>
    /// alongside the other engines once the telnet client is up.
    /// </summary>
    public Game.Combat.CombatManager Combat { get; private set; } = null!;

    /// <summary>
    /// Lookup of monster Numbers carrying the SeeHidden ability (code
    /// 57) in the active game-data set. Drives CombatManager's
    /// backstab-skip — a seehidden room occupant ruins the opening BS.
    /// </summary>
    public Game.Combat.SeeHiddenIndex SeeHidden { get; private set; } = null!;

    /// <summary>
    /// Phase 9 PR 9.A — observes mid-room arrival lines
    /// ("&lt;name&gt; &lt;verb&gt; into the room from &lt;dir&gt;.")
    /// and appends the new entity to
    /// <see cref="RoomClassifier"/>'s observation so CombatStateTracker
    /// re-evaluates the Combat gate immediately on spawn.
    /// </summary>
    public Game.Combat.RoomEntryWatcher RoomEntry { get; private set; } = null!;

    /// <summary>
    /// Phase 9 PR 9.A — recognises monster deaths via the per-monster
    /// <see cref="Models.GameData.MonsterMessageRecord.DeathLine"/>
    /// patterns + the "experience + Combat Off" fallback. On a match,
    /// the dead monster is removed from <see cref="RoomClassifier"/>'s
    /// observation so CombatManager re-picks correctly instead of
    /// sitting on a stale entry.
    /// </summary>
    public Game.Combat.MonsterDeathWatcher MonsterDeath { get; private set; } = null!;

    /// <summary>
    /// Phase 9 PR 9.B — passive HP/MA threshold engine. Asserts /
    /// clears HealthRecovery + ManaRecovery gates and drives the
    /// rest / stand cycle with pre- and post-rest command sequencing.
    /// Does NOT cast spells — those route through CastingDirector
    /// (PR 9.D).
    /// </summary>
    public Game.Health.HealthManager Health { get; private set; } = null!;

    /// <summary>
    /// Phase 9 PR 9.C — low-level <c>c &lt;spell&gt; [target]</c>
    /// emitter. Gates on combat-round cooldown + a cast-blocked latch
    /// driven by server failure messages (fizzle / no-mana / already-
    /// cast / interrupted). Consumed by CastingDirector (PR 9.D) and
    /// any other engine that issues spell commands.
    /// </summary>
    public Game.Spells.CastCoordinator Cast { get; private set; } = null!;

    /// <summary>
    /// Phase 9 PR 9.D — unified self+party heal / cure / buff
    /// decision engine. Sits on top of <see cref="Cast"/> and decides
    /// which spell (if any) to issue based on HP / MA / ailment state
    /// + the user's Spells + Health tab thresholds.
    /// </summary>
    public Game.Spells.CastingDirector CastDirector { get; private set; } = null!;

    /// <summary>
    /// Phase 9 PR 9.D — condition tracker driven by the game-data
    /// Messages tab. Subscribes to inbound lines, matches against
    /// every <see cref="Models.GameData.MessageRecord.AppliedMessage"/>
    /// / <see cref="Models.GameData.MessageRecord.AppliedEndsWith"/>
    /// pair, surfaces the aggregated
    /// <see cref="Models.GameData.MessageFlags"/> bitfield. Consumed
    /// by CastingDirector's Tier-2 cure path.
    /// </summary>
    public Game.Conditions.ConditionTracker Conditions { get; private set; } = null!;

    /// <summary>
    /// Phase 9 PR 9.F — stealth state tracker. Owns
    /// <see cref="PlayerState.IsSneaking"/> /
    /// <see cref="PlayerState.IsHidden"/> and emits FSM-state
    /// transitions + silent-loss detection on room change. Auto-
    /// sneak / auto-hide engines (which actually issue commands)
    /// layer on top in a follow-up.
    /// </summary>
    public Game.Stealth.StealthManager Stealth { get; private set; } = null!;

    /// <summary>
    /// Phase 9 PR 9.K — auto-light need poster. On a "can't see"
    /// room-light line it posts a <see cref="NeedKind.LightSource"/>
    /// need to <see cref="Needs"/>; auto-get (PR 9.L) fulfils it.
    /// Gated by the AutoLight master toggle.
    /// </summary>
    public Game.Light.AutoLightManager AutoLight { get; private set; } = null!;

    /// <summary>
    /// Phase 9 PR 9.I — death observation aggregator. Mirrors the
    /// most recent death record from the loaded profile into live
    /// observables (LivesRemaining / LastKiller / LastDeathAt /
    /// DeathCount) so the Workshop DEATH section can bind without
    /// walking <see cref="Models.Profile.CharacterProfile.DeathHistory"/>.
    /// Also exposes the <c>@comeback</c> hook for
    /// <c>RemoteCommandManager</c>.
    /// </summary>
    public Game.Recovery.DeathRecoveryManager DeathRecovery { get; private set; } = null!;

    /// <summary>
    /// Phase 9 — runtime inventory parser. Folds the full <c>i</c>
    /// dump into a currency + numeric-encumbrance
    /// <see cref="Game.Inventory.InventorySnapshot"/> and patches it
    /// incrementally on coin pickups / drops / bank moves. Feeds
    /// <see cref="Cash"/>'s encumbrance gate the live carry weight.
    /// </summary>
    public Game.Inventory.InventoryManager Inventory { get; private set; } = null!;

    /// <summary>
    /// Phase 9 PR 9.E — per-currency cash pickup engine. Dispatches
    /// <c>get &lt;count&gt; &lt;coin&gt;</c> commands per
    /// <see cref="Models.Profile.CashSettings"/> policy when the
    /// room-cash line lands; tracks held tallies for the auto-
    /// deposit trigger. Encumbrance gates + drop-smaller-for-larger
    /// cascade run off <see cref="Inventory"/>'s snapshot; walker-
    /// driven reroute is follow-up work.
    /// </summary>
    public Game.Cash.CashManager Cash { get; private set; } = null!;

    /// <summary>
    /// Phase 9 PR 9.L — auto-get items engine. Parses the room
    /// "You notice ... here." survey, resolves each entry against the
    /// active set's items + the per-character
    /// <see cref="Models.GameData.ItemOverlay.AutoCollect"/> flag, and
    /// sends <c>get &lt;name&gt;</c> per flagged item. Gated by the
    /// AutoGetItems master toggle; defer-until-combat-finished honours
    /// the Settings → Items tab.
    /// </summary>
    public Game.Inventory.AutoGetItemsManager AutoGetItems { get; private set; } = null!;

    /// <summary>
    /// Phase 9 PR 9.J — shared Acquisition movement-gate driver. Both
    /// <see cref="Cash"/> and <see cref="AutoGetItems"/> feed it; it owns
    /// the single assert/clear of
    /// <see cref="Game.Map.MovementCoordinator.AcquisitionGate"/> so the
    /// walker resumes only once both engines finish looting.
    /// </summary>
    public Game.Inventory.AcquisitionGate Acquisition { get; private set; } = null!;

    /// <summary>
    /// Phase 9 PR 9.E follow-up — on-entry stash plan for user-
    /// marked stash rooms. Dispatches <c>hide N &lt;coin&gt;</c>
    /// commands per <see cref="Models.Profile.StashCurrencyRule"/>
    /// when <see cref="RoomTracker"/> reports we've arrived in a
    /// configured <see cref="Models.Profile.StashRoom"/>. Item-side
    /// stash rules land when the inventory subsystem ships.
    /// </summary>
    public Game.Cash.StashRoomManager Stash { get; private set; } = null!;

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
    /// In-memory graph of every room in the active game-data set, built
    /// once at set-switch time from <c>Rooms.json</c>. Phase 7's
    /// navigation stack (room tracker, BFS mapper, walker, loop
    /// manager, auto-lair scheduler) all read from this; Phase 7 PR
    /// 7.4 ships the loader + indexer. Subscribes to
    /// <see cref="GameDataCache.ActiveSetChanged"/> in
    /// <see cref="Initialize"/>; consumers subscribe to
    /// <see cref="Game.Map.RoomGraphManager.GraphReloaded"/> to drop
    /// any cached room references.
    /// </summary>
    public Game.Map.RoomGraphManager RoomGraph { get; private set; } = null!;

    /// <summary>
    /// TextBlock Info index for the active game-data set. Loaded from
    /// <c>TBInfo.json</c>; consumed by the teleport handler (room
    /// <c>CMD &gt; 0</c> + <c>(Item: N)</c> exit promotes to
    /// <see cref="Game.Map.RoomExitHint.Teleport"/>, then the walker
    /// follows the chain to extract keyword + destination).
    /// </summary>
    public TBInfoStore TBInfo { get; private set; } = null!;

    /// <summary>
    /// Reverse index of <c>RoomKey → monster ids whose Monsters.json
    /// "Summoned By" field references that room</c>. Lets the tooltip's
    /// <c>Also Here</c> line surface boss / script-spawn monsters whose
    /// presence lives only on the monster record (no room-side lair
    /// tag entry). Lazily built on first lookup per active set.
    /// </summary>
    public MonsterSpawnIndex MonsterSpawns { get; private set; } = null!;

    /// <summary>
    /// Item-id → name lookup for the active set. Consumed by the
    /// keyed-door FSM (<see cref="Game.Map.DoorOpenManager"/>) to
    /// translate an exit's <see cref="Game.Map.RoomExit.KeyItemId"/>
    /// into the verbatim name fed to <c>use &lt;name&gt; &lt;dir&gt;</c>.
    /// </summary>
    public ItemNameStore ItemNames { get; private set; } = null!;

    /// <summary>
    /// Trust-by-default room tracker. Owns
    /// <see cref="Game.Map.RoomState"/>; the Navigation status strip
    /// and any source-room-required engine (walker, loop runner,
    /// auto-lair scheduler) bind here. PR 7.1 ships the FSM; PR 7.1b
    /// wires the wire-side parser that feeds it
    /// <c>NoteRoomObserved</c> / <c>NoteMoveBlocked</c>.
    /// </summary>
    public Game.Map.RoomTracker RoomTracker { get; private set; } = null!;

    /// <summary>
    /// Shared tier-1/2/3 recovery gate for the walker / loop runner /
    /// auto-lair scheduler. Engines attach themselves on Start and
    /// detach on Stop; the gate owns the strict-1-of-1 anchor + the
    /// executed-step history + tier-3 backtrack logic.
    /// </summary>
    public Game.Map.EngineRecoveryGate Recovery { get; private set; } = null!;

    /// <summary>
    /// Writer that persists tracker-learned room names back into the
    /// active set's <c>Rooms.json</c>. Consumed by the
    /// MainWindowViewModel name-learned prompt handler after the user
    /// confirms the rename.
    /// </summary>
    public RoomNamePersistence RoomNamePersist { get; private set; } = null!;

    /// <summary>
    /// Sniffs outbound user-typed commands and tells
    /// <see cref="RoomTracker"/> about <c>look &lt;dir&gt;</c> peeks
    /// (so the next room display is dropped instead of mistaken for a
    /// move) and text-exit movement verbs (<c>go path</c>,
    /// <c>enter portal</c>, etc., so the step is captured in
    /// <see cref="Models.Profile.CharacterProfile.RecentSteps"/>).
    /// Hooked from <c>MainWindowViewModel.SendUserInput</c>.
    /// </summary>
    public Game.Map.OutboundMovementObserver OutboundMovement { get; private set; } = null!;

    /// <summary>
    /// Death-message detector — watches lines for the post-suicide /
    /// killed-in-combat <c>You now have N lives remaining.</c> shape
    /// and fires <see cref="Game.Map.RoomTracker.NoteDeath"/>. Captures
    /// a <see cref="Models.Profile.DeathRecord"/> on the loaded profile
    /// for the Phase 9 Workshop DEATH section and pivots the tracker
    /// into <see cref="Game.Map.RoomConfidence.PendingRespawn"/>.
    /// Bound to the per-session LineExtractor by
    /// <c>MainWindowViewModel</c>.
    /// </summary>
    public Game.DeathDetector Death { get; private set; } = null!;

    /// <summary>
    /// BFS pathfinding + planar layout over the active
    /// <see cref="RoomGraph"/>. Consumed by the walker, loop runner,
    /// auto-lair scheduler (pathfinding), and the Navigation
    /// <c>MapControl</c> (layout). PR 7.5.
    /// </summary>
    public Game.Map.BfsMapper Bfs { get; private set; } = null!;

    /// <summary>
    /// Per-character avoided + stash room set. Implements
    /// <see cref="Game.Map.IRoomFilter"/> so pathing layers can plug
    /// it into <see cref="Bfs"/> without further wiring. PR 7.6.
    /// </summary>
    public MovementFilter Movement { get; private set; } = null!;

    /// <summary>
    /// Per-character favourite-room bookmarks. Wires Navigation's
    /// GOTO pane + the map's "Add to favorites" context menu;
    /// persisted via <see cref="ProfileService"/>.
    /// </summary>
    public FavoritesStore Favorites { get; private set; } = null!;

    /// <summary>
    /// Shared pause-gate aggregator for every Phase 7 movement engine
    /// (walker, loop runner, auto-lair scheduler). A pause from any
    /// source halts whichever engine is active. PR 7.7.
    /// </summary>
    public Game.Map.MovementCoordinator MovementCoordinator { get; private set; } = null!;

    /// <summary>
    /// Fulfillment half of the Phase 9 auto-engine coordination model —
    /// requesters post acquisition needs (light source, etc.), fulfilling
    /// engines claim + resolve them. No engine references another by
    /// type. PR 9.J.
    /// </summary>
    public NeedsRegistry Needs { get; private set; } = null!;

    /// <summary>
    /// Walk-to engine — sends one move at a time, waits for the room
    /// tracker to confirm before advancing, and honours
    /// <see cref="MovementCoordinator"/> pause gates. PR 7.7.
    /// </summary>
    public Game.Map.AutoWalkManager Walker { get; private set; } = null!;

    /// <summary>
    /// Per-BBS saved-loop catalogue. CRUD over
    /// <c>Data/BBS/{bbs}/Loops/</c>; consumers re-bind when the active
    /// BBS changes. PR 7.8.
    /// </summary>
    public Game.Map.LoopManager Loops { get; private set; } = null!;

    /// <summary>
    /// MegaMUD <c>.mp</c> loop-file importer. Stateless w.r.t. the
    /// profile; takes the active <see cref="RoomGraph"/> at construct
    /// time and resolves anchors against whatever it currently
    /// contains. See <c>docs/08-phase-7-…</c> PR 7.9.
    /// </summary>
    public Game.Map.MpFile.MpFileImporter MpImporter { get; private set; } = null!;

    /// <summary>
    /// Per-BBS Auto-Lair setup catalogue. Loads on profile load + BBS
    /// pin via the same ResolveActiveBbs path Loops uses. The Manage
    /// dialog reads / writes through this surface; the
    /// <see cref="LairTimers"/> store derives default respawn timers
    /// from game data and tracks in-session arrivals.
    /// </summary>
    public Game.Map.LairManager Lairs { get; private set; } = null!;

    /// <summary>
    /// Game-data-derived respawn timer resolver + in-session arrival
    /// tracker for marked lair rooms. The Phase 7 PR 7.19 Auto-Lair
    /// scheduler reads <c>NextReadyAt</c> to choose the next leg.
    /// </summary>
    public Game.Map.LairTimerStore LairTimers { get; private set; } = null!;

    /// <summary>
    /// Sole writer of <see cref="Game.PlayerState.Encumbrance"/>.
    /// Subscribes the <c>enc</c> line via MessageRouter.
    /// </summary>
    public Game.EncumbranceParser Encumbrance { get; private set; } = null!;

    /// <summary>
    /// Debug instrumentation logging measured per-hop times tagged
    /// with the current <see cref="Game.EncumbranceLevel"/>. Off by
    /// default; flipped on via Settings → Other.
    /// </summary>
    public Game.HopTimingCalibrator HopCalibrator { get; private set; } = null!;

    /// <summary>
    /// Per-BBS room blacklist — hides target rooms from the
    /// Navigation map render and the search box. Consumed by
    /// <see cref="Game.Map.BfsMapper"/> (skip placement, keep edge
    /// for dangling stub) and the right-click "Add to blacklist"
    /// + "Modify Blacklist…" flows.
    /// </summary>
    public RoomBlacklistStore RoomBlacklist { get; private set; } = null!;

    /// <summary>
    /// Loop execution engine — Phase 7 PR 7.16. Shares
    /// <see cref="MovementCoordinator"/> + <see cref="RoomTracker"/>
    /// with the walker, plus <see cref="WirePromptScanner"/> for
    /// command-step confirmation.
    /// </summary>
    public Game.Map.LoopRunner LoopRunner { get; private set; } = null!;

    /// <summary>
    /// Random-walk roam scheduler. Foundation for the deterministic
    /// Auto-Lair scheduler. Session-only state.
    /// </summary>
    public Game.Map.AutoLairManager AutoLair { get; private set; } = null!;


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
        // Stat-screen parser ahead of LivesProvider hookup below so
        // both the engine's @suicide hard-block and the @lives reply
        // path share the same "unknown until first stat poll" source.
        Stats = new Game.StatParser(PlayerStats, Log);
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
        // Suicide-password observer + engine-gate consumer. Drives
        // EngineGate.IsLocked during password-entry prompts so
        // MainWindowViewModel's wrapped engine wire-senders silently
        // no-op for the duration; on commit, stores the encrypted
        // password to CharacterProfile.EncryptedSuicidePassword.
        SuicidePassword = new Game.SuicidePasswordTracker(
            Router, EngineGate, Profile, Passwords, Log);

        // LivesProvider — feeds the engine-level @suicide hard-block
        // and the @lives handler's reply. Returns null until the user
        // types `stat` for the first time this session so the
        // hard-block treats lives as unknown (= blocked) per spec.
        // Stats itself is constructed above where PartyEssentials needs
        // PlayerStats injected.
        RemoteCommands.LivesProvider = () => Stats.HasParsed ? PlayerStats.Lives : (int?)null;

        // Persist stat captures onto the loaded profile so the next
        // session starts hydrated with the last-observed values
        // (Save-on-close at MainWindow.Closing flushes the in-memory
        // profile to disk). Drafts (no name) are still snapshotted —
        // ProfileService.Save no-ops on them, so the data just lives
        // for the rest of the session.
        Stats.ScreenParsed += snapshot =>
        {
            if (Profile.Current is { } p) p.LastKnownStats = snapshot;
        };
        // Restore the snapshot back into live PlayerStats whenever a
        // profile loads. StatParser owns the PlayerStats fields, so
        // hydration MUST route through Stats.Hydrate; passing null
        // resets every field to default (covers fresh / never-stat'd
        // profiles cleanly).
        Profile.ProfileLoaded += p => Stats.Hydrate(p.LastKnownStats);
        Profile.ProfileClosed += () => Stats.Hydrate(null);
        // @hangup handler — sends the configured GameCommands.ExitCommand
        // when an authorised sender (HangupDisconnect permission on
        // the Players-tab record) telepaths @hangup. Also raises the
        // HangupSignal so MainWindowVM suppresses auto-reconnect and
        // MainMenuEntryAutomation skips the entry-latch on the next
        // connect — user manually re-enters the realm after reading
        // what's on the screen.
        Hangup = new Game.Remote.HangupHandler(RemoteCommands, GameCommands, HangupSignal);
        // @do passthrough — wire-sender bound in MainWindowVM after the
        // telnet client is up. Hard-blocks (reroll, suicide-lives) fire
        // at engine level before this handler runs.
        Do = new Game.Remote.DoHandler(RemoteCommands, Log);
        // Cluster 5d — @auto-* family. AutoMode handler mutates the
        // loaded profile's General section + persists. (@comeback is
        // wired in the Navigation block below as PartyComebackManager,
        // which needs the movement engines.)
        AutoMode = new Game.Remote.AutoModeRemoteHandler(RemoteCommands, Profile, Log);
        // @trap auto-disarm flow — manager owns the state machine,
        // handler owns the @-command auth boundary. Wire-sender +
        // OtherSettings cadence knobs bind in MainWindowVM /
        // ApplyOtherFromActiveProfile.
        TrapDisarm = new Game.TrapDisarmManager(Router, PlayerStats, Log);
        TrapRemote = new Game.Remote.TrapHandler(RemoteCommands, TrapDisarm);

        // Phase 7 PR 7.23 — @goto / @loop / @lair / @stop / @rego land
        // in the Navigation block below, after Walker / LoopRunner /
        // AutoLair are constructed.

        // DoorOpenManager — walker's bash/pick/open FSM. Attempt caps
        // + verb preference are pulled live from the resolved Other
        // settings so the user can edit thresholds mid-session without
        // restarting an engine. Wire-sender is bound by MainWindowVM
        // alongside the trap one (gate-wrapped SendUserInput).
        Door = new Game.Map.DoorOpenManager(Router, PlayerStats,
            maxBashAttemptsProvider:    () => Resolver.Resolve<Models.Profile.OtherSettings>("Other").MaxBashAttempts,
            maxPickAttemptsProvider:    () => Resolver.Resolve<Models.Profile.OtherSettings>("Other").MaxPickAttempts,
            picklocksOverBashProvider:  () => Resolver.Resolve<Models.Profile.OtherSettings>("Other").PicklocksOverBash,
            itemNameLookup:             id => ItemNames.GetName(id),
            log: Log);
        // HiddenSearch is constructed later, after RoomTracker exists
        // (it subscribes to RoomTracker.StateChanged for the reveal
        // signal). See the wiring near RoomTracker = new(...).
        // SuicideHandler — needs the raw wire-sender (NOT the gate-
        // wrapped one) because it owns the suicide flow and must keep
        // sending while the password tracker locks the gate. Bound by
        // MainWindowViewModel a few lines after the other engine
        // wire-senders, deliberately to the un-wrapped SendUserInput.
        Suicide = new Game.Remote.SuicideHandler(RemoteCommands, Router, Profile, Passwords, PromptScanner, Log);
        // Main-menu entry automation — armed by MainWindowVM when
        // LoginAutomator.LoggedIntoGame fires; observes the
        // MainMenuEnterRealm pattern and sends GameCommands.EntryCommand
        // exactly once per arm, followed by the post-entry refresh
        // sequence (CR + stat + exp + i) to seed PlayerStats. Closed
        // by default so in-game chat matching the menu pattern can
        // never trick it; ALSO skips on the first connect after a
        // hangup (HangupSignal.ConsumeSuppressEntry) so the user can
        // read the screen before they decide to act.
        MainMenuEntry = new Game.MainMenuEntryAutomation(Router, GameCommands, HangupSignal, Log);

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
        Profile.ProfileLoaded  += _ => ApplyAutoLairFromActiveProfile();
        Profile.ProfileClosed  += ResetAutoLairToDefaults;
        Profile.ProfileMutated += _ => ApplyAutoLairFromActiveProfile();

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

        // Phase 7 room graph — seeded from the active set's Rooms.json
        // every time the set switches. Built once per swap; consumers
        // hold typed Room references for the lifetime of the set.
        RoomGraph = new Game.Map.RoomGraphManager(GameData, Log);
        GameData.ActiveSetChanged += RoomGraph.OnActiveSetChanged;
        if (GameData.ActiveSet is not null)
            RoomGraph.OnActiveSetChanged(GameData.ActiveSet);

        // TBInfo store — TextBlock Info table indexed by Room.Cmd. Used
        // by the teleport / NPC-service / gambling code paths (commit 5+
        // wires the teleport resolver). Mirrors RoomGraph's load shape:
        // active-set-driven, raw JSON evicted after typed conversion.
        TBInfo = new TBInfoStore(GameData, Log);
        MonsterSpawns = new MonsterSpawnIndex(GameData, Log);
        GameData.ActiveSetChanged += TBInfo.OnActiveSetChanged;
        if (GameData.ActiveSet is not null)
            TBInfo.OnActiveSetChanged(GameData.ActiveSet);

        // ItemNameStore — int→name index for the active Items.json so
        // the keyed-door FSM can resolve KeyItemId → in-game name and
        // send `use <name> <dir>`.
        ItemNames = new ItemNameStore(GameData, Log);
        GameData.ActiveSetChanged += ItemNames.OnActiveSetChanged;
        if (GameData.ActiveSet is not null)
            ItemNames.OnActiveSetChanged(GameData.ActiveSet);

        // Phase 7 PR 7.1 — room tracker. Resets to Unknown on every
        // graph reload because per-room references are invalidated
        // when the active set rebuilds.
        RoomTracker = new Game.Map.RoomTracker(RoomGraph, Log);
        RoomGraph.GraphReloaded += () => RoomTracker.OnGraphReloaded();

        // Shared engine-level recovery gate. Walker / LoopRunner /
        // AutoLair attach themselves on Start (next commits).
        Recovery = new Game.Map.EngineRecoveryGate(RoomGraph, RoomTracker, Log);

        // Writer that persists tracker-learned names back to
        // Rooms.json. The MainWindowVM subscribes to NameLearned to
        // prompt the user, then calls this on accept.
        RoomNamePersist = new RoomNamePersistence(GameData, Log);

        // Hand the loaded profile to the tracker so it can hydrate
        // LastKnownRoom + RecentSteps (replay-from-last-Confirmed
        // recovery) and write back on every Confirmed transition /
        // step. Persistence flushes to disk on the regular profile-save
        // cycle (app close / settings Apply / explicit save).
        Profile.ProfileLoaded += p => RoomTracker.Hydrate(p);
        Profile.ProfileClosed += () => RoomTracker.OnProfileClosed();
        if (Profile.Current is { } loaded) RoomTracker.Hydrate(loaded);

        // Outbound-command observer — recognises `look <dir>` peeks and
        // text-exit movement (go path / enter portal / climb tree / …)
        // typed at the terminal or conversation window. Hooked into the
        // wire-send pipeline by MainWindowViewModel.SendUserInput.
        OutboundMovement = new Game.Map.OutboundMovementObserver(RoomTracker, Log);

        // Death-message detector — bound to the per-session
        // LineExtractor by MainWindowViewModel.AttachLineExtractor.
        Death = new Game.DeathDetector(RoomTracker, Log);

        // "There is no exit in that direction!" → demote tracker to
        // Suspect so the next observation re-resolves via candidate
        // search. Without this hook, a bonk while the tracker's
        // model is wrong silently sticks; the user's only recourse
        // is to walk back through a unique room to re-anchor.
        Router.Subscribe(Services.Patterns.KnownPatterns.DirectionFailed,
            _ => RoomTracker.NoteDirectionFailed());

        // HiddenExitRevealManager — walker's sea-retry loop for
        // SearchableHidden exits. Subscribes to RoomTracker.StateChanged
        // for the "exit now visible" signal. Constructed here (after
        // RoomTracker exists); the walker's enqueuer binding and the
        // wire-sender land in MainWindowVM.
        HiddenSearch = new Game.Map.HiddenExitRevealManager(
            RoomTracker,
            maxAttemptsProvider: () => Resolver.Resolve<Models.Profile.OtherSettings>("Other").MaxHiddenSearchAttempts,
            router: Router,
            log: Log);

        // Phase 7 PR 7.5 — BFS pathfinding + planar layout. Layout
        // cache invalidates on every graph reload.
        Bfs = new Game.Map.BfsMapper(RoomGraph, Log);
        RoomGraph.GraphReloaded += Bfs.OnGraphReloaded;
        // Pre-warm the layout on a thread-pool task so the user
        // doesn't pay the BFS cost on the UI thread when they first
        // open the Navigation window.
        RoomGraph.GraphReloaded += Bfs.PrewarmAsync;

        // Phase 7 PR 7.6 — per-character avoided + stash rooms.
        // Constructor subscribes ProfileLoaded / ProfileClosed and
        // hydrates from the currently-loaded profile if there is one.
        Movement = new MovementFilter(Profile, Log);
        Favorites = new FavoritesStore(Profile, Log);

        // Phase 7 PR 7.7 — coordinator + walker. Coordinator is the
        // single pause-gate hub for every movement engine (walker now,
        // loop / auto-lair later). Walker's wire sender is bound by
        // MainWindowViewModel once the telnet client is up (matching
        // the PartyPoller / AutoPartyManager pattern).
        MovementCoordinator = new Game.Map.MovementCoordinator(Log);

        // Phase 9 PR 9.J — needs registry. Cross-engine fulfillment hub;
        // auto-light (9.K) posts, auto-get (9.L) fulfils. Cleared on
        // character swap so pending needs don't leak across profiles.
        Needs = new NeedsRegistry(Log);
        Profile.ProfileLoaded += _ => Needs.Clear();

        // Phase 9 PR 9.J — shared Acquisition movement-gate driver. Both
        // AutoGetItems and Cash feed this one instance (bound after they're
        // constructed below) so the walker holds until BOTH finish looting.
        Acquisition = new Game.Inventory.AcquisitionGate(MovementCoordinator, Log);

        // Phase 9 PR 9.0b — RoomEntityClassifier + CombatStateTracker.
        // Classifier subscribes to RoomAlsoHere; tracker subscribes to
        // classifier output + combat-status / damage patterns to drive
        // PlayerState.InCombat + the MovementCoordinator.CombatGate.
        //
        // CombatStateTracker's master switch reads
        // GeneralSettings.AutoMode.AutoCombat from the live profile.
        // Settings → General checkbox + the toolbar Toggle button
        // write the same flag; the delegate is queried on every
        // Also-Here line so toggling takes effect immediately.
        RoomClassifier = new Game.Combat.RoomEntityClassifier(
            Router, MonsterMessages, Players, RoomTracker, Log);
        CombatTracker = new Game.Combat.CombatStateTracker(
            Router, MovementCoordinator, RoomClassifier, MonsterMessages,
            PlayerState,
            isAutoAttackEnabled: () => ReadAutoModeFlag(d => d.AutoCombat),
            // Same overlay-resolve closure CombatManager uses — keeps
            // the engageable predicate consistent so the gate and the
            // swing decision can't diverge on the same room state.
            resolveOverlay: n => Resolver.ResolveGameData<Models.GameData.MonsterOverlay>(
                "Monsters",
                n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                MonsterOverlaySeed.GetOverlay(n)),
            log: Log);

        // Phase 9 PR 9.0c — RoundDamageTracker. shouldWriteTrace
        // delegate reads the Log pane's combat-diagnostics umbrella
        // (session-only, no per-profile persistence) so the user can
        // toggle the per-round trace from the Log menu mid-session.
        RoundDamage = new Game.Combat.RoundDamageTracker(
            Router, PlayerState, Log,
            shouldWriteTrace: () => LogDiagnostics.CombatDiagnostics);
        // Reset round counter + ring on BBS connect to match
        // CombatSessionTracker's session-boundary convention (PR 9.0c
        // doesn't ship CombatSessionTracker — Phase 11 does — but the
        // reset hook lives here on the data producer).
        Profile.ProfileLoaded += _ => RoundDamage.Reset();

        // Phase 9 PR 9.0d — local-death observation. Pure subscriber;
        // DeathRecoveryManager (PR 9.I) consumes the PlayerDied event
        // for its corpse-recovery flow. Reset the in-flight round
        // accumulator on death so a partial round doesn't get
        // attributed to the next combat.
        DeathWatcher = new Game.Combat.DeathLineWatcher(Router, Log);
        DeathWatcher.PlayerDied += _ => RoundDamage.MarkCombatEnded();

        // Phase 9 PR 9.A — CombatManager. Picks a target on each
        // classifier emit and sends the configured attack command via
        // the bound wire sender. Reads CombatSettings live (same
        // pattern as CombatStateTracker) so toggling Master / changing
        // TargetOrder / etc. mid-session takes effect on the next
        // Also-Here line.
        // Phase 9 — mid-room arrival watcher. Subscribes to the
        // RoomEntryArrival pattern + appends to the classifier so the
        // Combat gate / CombatManager react to spawns immediately.
        RoomEntry = new Game.Combat.RoomEntryWatcher(Router, RoomClassifier, Log);

        // Phase 9 — monster death watcher. Specific-pattern matches
        // (per-monster DeathLine) + fallback (exp + Combat Off). On a
        // death event the classifier removes the dead entity so
        // CombatManager re-picks correctly instead of being blocked
        // by a stale "still in the list" check against the
        // just-killed mob (the "kobold thief arrived but no attack"
        // bug). Multiple candidates per pattern are normal — shared
        // wordings; we remove ONE matching entry and let the next
        // room re-display correct any cross-variant ambiguity.
        MonsterDeath = new Game.Combat.MonsterDeathWatcher(
            Router, MonsterMessages, Log);
        MonsterDeath.MonsterDied += evt =>
        {
            if (evt.IsFallback)
            {
                // Fallback path: we don't know which monster died.
                // CombatManager's next swing window will be correct
                // because the server's room re-display (or the next
                // arrival) eventually rebuilds the list. We just log.
                Log.Info(Game.Combat.MonsterDeathWatcher.LogCategory,
                    $"fallback death — no entity removed");
                return;
            }
            foreach (Game.Combat.MonsterDeathIdentity id in evt.Candidates)
            {
                // Order matters: drop CombatManager's _currentTarget
                // BEFORE removing the entity from the observation.
                // NoteMonsterDied's resolved-name lookup needs the
                // entity still present so the raw/resolved mapping
                // is intact; RemoveDeadEntity then fires
                // EntitiesObserved, which re-picks from the surviving
                // engageables and re-issues `attack`. Without this
                // ordering, a same-name kill (two "giant rat"s in a
                // room, one dies) leaves CombatManager silent — the
                // surviving rat shared RawName with our just-dead
                // target and tripped the "server still swinging"
                // short-circuit. See CombatManager.NoteMonsterDied.
                Combat.NoteMonsterDied(id.Name);
                if (RoomClassifier.RemoveDeadEntity(id.Name))
                {
                    Log.Info(Game.Combat.MonsterDeathWatcher.LogCategory,
                        $"removed dead entity name={id.Name}");
                    break;     // remove one — multiple candidates are alt-names for the same death
                }
            }
        };

        Combat = new Game.Combat.CombatManager(
            Router, RoomClassifier, MonsterMessages,
            // Resolve per-monster overlay: seed-store value forms the
            // Defaults tier, SettingsResolver overlays Global / BBS /
            // Char-tier user overrides on top.
            resolveOverlay: n => Resolver.ResolveGameData<Models.GameData.MonsterOverlay>(
                "Monsters",
                n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                MonsterOverlaySeed.GetOverlay(n)),
            party: PartyState,
            readSettings: () =>
                ReadSection<Models.Profile.CombatSettings>(Profile.Current, "Combat"),
            isEnabled: () => ReadAutoModeFlag(d => d.AutoCombat),
            readOwnGivenName: () => Profile.CurrentProfileName,
            log: Log);

        // Phase 9 PR 9.B — HealthManager. Master on/off is
        // GeneralSettings.AutoMode.AutoHealRest (shared with the
        // Settings → General checkbox + toolbar Toggle button). When
        // off, every threshold check + rest/stand emit short-circuits.
        Health = new Game.Health.HealthManager(
            PlayerState, MovementCoordinator,
            readSettings: () =>
                ReadSection<Models.Profile.HealthSettings>(Profile.Current, "Health"),
            isEnabled: () => ReadAutoModeFlag(d => d.AutoHealRest),
            readHangupCommand: () => GameCommands.ExitCommand,
            getActiveMovementEngine: ResolveActiveMovementEngine,
            getLastSentDirection: () =>
                Recovery.ExecutedSinceAnchor.Count > 0
                    ? Recovery.ExecutedSinceAnchor[^1]
                    : (Game.Map.Direction?)null,
            readOtherSettings: () =>
                ReadSection<Models.Profile.OtherSettings>(Profile.Current, "Other"),
            readCombatSettings: () =>
                ReadSection<Models.Profile.CombatSettings>(Profile.Current, "Combat"),
            // Don't try to rest while engageable hostiles are in the
            // room — every combat round would otherwise break rest.
            // CombatStateTracker owns the same boolean it uses to
            // assert the CombatGate, so we stay in sync with the
            // movement gate logic.
            hasEngageableHostiles: () => CombatTracker.HasEngageableHostiles,
            log: Log);

        // Role-aware recovery: as a party follower we top off only to the
        // rest floor (not full) and ping the leader via @wait / @ok so we
        // don't silently hold or release the party. Solo / leader keeps the
        // full rest-max topoff — PartyRestSync self-gates the telepaths.
        Health.SetPartyRoleSync(
            isPartyFollower: () => PartyState.IsInParty && !PartyState.SelfIsLeader,
            requestPartyWait: PartyRest.RequestWait,
            requestPartyOk: PartyRest.RequestOk);

        // Server-side resting state clears on move; drop our latch
        // too so the next threshold breach actually fires `rest`
        // again instead of skipping it on a stale _restInFlight.
        RoomTracker.StateChanged += t =>
        {
            if (t.PreviousRoom is null || t.NewRoom is null) return;
            if (ReferenceEquals(t.PreviousRoom, t.NewRoom)) return;
            if (t.PreviousRoom.Key.Equals(t.NewRoom.Key)) return;
            Health.NoteRoomChanged(t.NewRoom.Key);
        };

        // Phase 9 PR 9.C — CastCoordinator. Subscribes to spell-failure
        // patterns directly; tick-clears its block latch + cooldown via
        // TickEngine.CombatTickElapsed so the next round can cast.
        Cast = new Game.Spells.CastCoordinator(Router, Log);
        Tick.CombatTickElapsed += Cast.OnCombatTick;

        // Phase 9 PR 9.D — ConditionTracker reads MessageStore +
        // line-side patterns to surface ActiveFlags. CastingDirector
        // consumes it for Tier-2 cure decisions. AttachLineExtractor
        // lands in MainWindowViewModel alongside the other line
        // consumers.
        Conditions = new Game.Conditions.ConditionTracker(Messages, Log);

        // Phase 9 PR 9.D — CastingDirector. Sits on top of Cast,
        // decides which heal / cure / buff (if any) to issue based on
        // PlayerState + Spells/Health settings. AutoHealRest gates
        // the engine (shared toggle with HealthManager's passive rest).
        CastDirector = new Game.Spells.CastingDirector(
            PlayerState, Cast, Conditions, PartyState,
            readSpells: () => ReadSection<Models.Profile.SpellsSettings>(Profile.Current, "Spells"),
            readHealth: () => ReadSection<Models.Profile.HealthSettings>(Profile.Current, "Health"),
            readPartySettings: () => ReadSection<Models.Profile.PartySettings>(Profile.Current, "Party"),
            isEnabled: () => ReadAutoModeFlag(d => d.AutoHealRest),
            log: Log);
        // Cluster 3 stealth gate — buff casts suppressed while
        // sneaking or hidden so we don't break the backstab window.
        CastDirector.SetStealthGate(() => Stealth.IsStealthed);
        Tick.CombatTickElapsed += CastDirector.OnCombatTick;

        // Phase 9 PR 9.F — StealthManager state tracker + auto-sneak /
        // auto-hide engines. Owns PlayerState.IsSneaking/IsHidden,
        // detects silent loss on room change, and sends `sneak` /
        // `hide` per AutoMode toggles.
        Stealth = new Game.Stealth.StealthManager(Router, PlayerState, Log);
        Stealth.SetAutoToggles(
            isAutoSneakEnabled: () => ReadAutoModeFlag(d => d.AutoSneak),
            isAutoHideEnabled:  () => ReadAutoModeFlag(d => d.AutoHide));
        // PR 4.b decision #1 — any NPC in the room prevents sneak, so
        // suppress the doomed `sn` instead of firing it into a rejection.
        Stealth.SetSneakBlockCheck(() => CombatTracker.HasRoomNpc);

        // PR 4.c backstab window — CombatManager opens with `bs` on the
        // first swing into a room while sneaking, unless a seehidden
        // monster is present (which reveals us to the whole room).
        SeeHidden = new Game.Combat.SeeHiddenIndex(GameData);
        Combat.SetBackstabHooks(
            isSneaking:   () => Stealth.IsSneaking,
            hasSeeHidden: n => SeeHidden.Has(n));

        // PR 4.c-b combat-off "clear hostiles when seen Hidden" override —
        // a stealth runner (AutoSneak on) sprinting a route with combat
        // OFF that hits a SeeHidden room must stop and clear it rather than
        // drag/stack monsters onward. CombatStateTracker owns the latch +
        // holds the walker gate; CombatManager reads the latch to engage
        // despite combat-off.
        CombatTracker.SetSeeHiddenClearGate(
            clearWhenSeenHidden: () => ReadSection<Models.Profile.CombatSettings>(
                Profile.Current, "Combat").ClearHostilesWhenSeenHidden,
            isAutoSneakEnabled:  () => ReadAutoModeFlag(d => d.AutoSneak),
            hasSeeHidden:        n => SeeHidden.Has(n));
        Combat.SetSeeHiddenClearGate(() => CombatTracker.SeeHiddenClearActive);
        RoomTracker.StateChanged += t =>
        {
            if (t.PreviousRoom is null || t.NewRoom is null) return;
            if (ReferenceEquals(t.PreviousRoom, t.NewRoom)) return;
            if (t.PreviousRoom.Key.Equals(t.NewRoom.Key)) return;
            Stealth.NoteRoomChanged();
            // Same hook drives the idle-hide opportunity for v1.
            // Refine when a dedicated walker-idle signal lands.
            Stealth.NoteIdleOpportunity();
        };

        // Phase 9 PR 9.K — AutoLightManager. Posts a LightSource need to
        // the registry on a "can't see" room-light line; auto-get (9.L)
        // fulfils it. Gated by the AutoLight master toggle (Settings →
        // General checkbox + the toolbar Toggle button write the same
        // flag; the delegate is queried per dark-room line so toggling
        // takes effect immediately).
        AutoLight = new Game.Light.AutoLightManager(Router, Needs, Log);
        AutoLight.SetEnabledToggle(() => ReadAutoModeFlag(d => d.AutoLight));

        // Phase 9 PR 9.I — DeathRecoveryManager. Aggregates the
        // DeathLineWatcher.PlayerDied event + the profile's
        // DeathHistory list (written by DeathDetector ->
        // RoomTracker.NoteDeath) into observables the Workshop
        // DEATH section binds to. (@comeback is a separate party-pickup
        // flow owned by PartyComebackManager, wired after the engines.)
        DeathRecovery = new Game.Recovery.DeathRecoveryManager(
            DeathWatcher, Profile, Log);

        // Phase 9 — InventoryManager. Parses the full `i` dump into a
        // currency + numeric-encumbrance snapshot and patches it on
        // coin pickups / drops. CashManager reads the snapshot for its
        // encumbrance gate. MarkStale on profile swap so the new
        // character's first gate evaluation waits for a fresh `i`.
        Inventory = new Game.Inventory.InventoryManager(Log);
        Profile.ProfileLoaded += _ => Inventory.MarkStale();

        // Phase 9 PR 9.E — CashManager. Subscribes to cash-on-ground
        // / cash-picked-up / cash-dropped patterns and dispatches
        // per-currency policy. AutoGetCash gates the whole engine
        // (Settings -> General toggle + toolbar Toggle command).
        Cash = new Game.Cash.CashManager(Router,
            readSettings: () => ReadSection<Models.Profile.CashSettings>(Profile.Current, "Cash"),
            isEnabled: () => ReadAutoModeFlag(d => d.AutoGetCash),
            getSnapshot: () => Inventory.Snapshot,
            log: Log);
        // Reset held tallies on profile swap — prior character's
        // counts aren't relevant to the new one.
        Profile.ProfileLoaded += _ => Cash.ResetTallies();
        Cash.SetAcquisitionGate(Acquisition);

        // Phase 9 PR 9.E follow-up — StashRoomManager. Driven by
        // RoomTracker.StateChanged; looks up the entered room in
        // the user's stash list and dispatches per-currency hide
        // commands. Shares AutoGetCash gating with CashManager
        // (cash automation is one mental toggle).
        Stash = new Game.Cash.StashRoomManager(Cash, Profile,
            readCash: () => ReadSection<Models.Profile.CashSettings>(Profile.Current, "Cash"),
            isEnabled: () => ReadAutoModeFlag(d => d.AutoGetCash),
            log: Log);
        RoomTracker.StateChanged += t =>
        {
            if (t.NewRoom is null) return;
            // Only fire on actual room change (key differs) — same
            // pattern HealthManager / StealthManager use.
            if (t.PreviousRoom is not null
             && t.PreviousRoom.Key.Equals(t.NewRoom.Key)) return;
            Stash.NoteRoomEntered(t.NewRoom.Key);
        };

        // Phase 9 PR 9.L — AutoGetItemsManager. The resolve delegate
        // maps a loose "You notice ..." entry back to an item Number
        // (ItemNames reverse index), reads the verbatim Name to send,
        // and resolves the per-character AutoCollect override through
        // the 4-tier hierarchy seeded by ItemOverlaySeed. Constructed
        // after CombatTracker so its EntitiesObserved handler (wired
        // below) runs after the gate update and reads a current
        // HasEngageableHostiles.
        AutoGetItems = new Game.Inventory.AutoGetItemsManager(Router,
            resolve: ResolveAutoGetItem,
            isEnabled: () => ReadAutoModeFlag(d => d.AutoGetItems),
            collectAfterCombatFinished: () =>
                ReadSection<Models.Profile.CashSettings>(Profile.Current, "Cash")
                    .CollectAfterCombatFinished,
            hasEngageableHostiles: () => CombatTracker.HasEngageableHostiles,
            log: Log);
        AutoGetItems.SetAcquisitionGate(Acquisition);
        // Combat-finished flush: every room-entity observation re-checks
        // the deferred queue (CombatStateTracker's handler ran first, so
        // the hostile flag is current).
        RoomClassifier.EntitiesObserved += _ => AutoGetItems.OnRoomObserved();
        // Drop the stale queue when we actually change rooms.
        RoomTracker.StateChanged += t =>
        {
            if (t.NewRoom is null) return;
            if (t.PreviousRoom is not null
             && t.PreviousRoom.Key.Equals(t.NewRoom.Key)) return;
            AutoGetItems.OnRoomChanged();
        };

        Walker = new Game.Map.AutoWalkManager(RoomGraph, Bfs, RoomTracker,
            MovementCoordinator, filter: Movement, log: Log,
            promptScanner: PromptScanner, recovery: Recovery);
        // Phase 7 PR 7.22 — route walker over trapped exits through
        // the Phase 6 TrapDisarmManager.
        Walker.SetTrapEnqueuer(TrapDisarm.Enqueue);
        // PR 4.b — proactive pre-move sneak: `sn` goes out as the last
        // command before each walker move so the move itself is sneaked
        // (the reactive RoomTracker hook above only re-sneaks AFTER
        // arriving). Non-blocking; the settled-state guard in
        // StealthManager prevents a double-send when both paths fire.
        Walker.SetPreMoveHook(() => Stealth.RequestPreMoveStealth());

        // Phase 7 PR 7.8 — per-BBS loop catalogue. PR 7.13 wires the
        // BBS-change signals so the catalogue reloads on profile load
        // and on explicit BBS pin from Settings → BBS Apply.
        //
        // Resolve through ResolveActiveBbs (NOT raw Profile.BbsName)
        // so a blank-draft profile + global default-BBS still binds
        // the catalogue to that default. Otherwise Save on a
        // brand-new loop silently no-ops in LoopManager (the
        // _bbsName==null bail) and the user-visible Save button
        // appears to do nothing.
        Loops = new Game.Map.LoopManager(Bfs, RoomGraph, Log);
        Profile.ProfileLoaded += _  => Loops.LoadAll(ResolveActiveBbs()?.Name);
        Profile.BbsPinApplied += _  => Loops.LoadAll(ResolveActiveBbs()?.Name);
        Profile.ProfileClosed += () => Loops.LoadAll(null);

        // Phase 7 PR 7.9 — MegaMUD .mp loop importer. Pure resolution
        // service over the active graph; no per-profile state of its
        // own. The Manage dialog calls it on user "Import .mp".
        MpImporter = new Game.Map.MpFile.MpFileImporter(RoomGraph, Log);

        // Phase 7 PR 7.18 — Auto-Lair setup catalogue (per-BBS, mirrors
        // LoopManager) + game-data-driven respawn timer resolver +
        // in-session arrival tracker.
        Lairs = new Game.Map.LairManager(Log);
        Profile.ProfileLoaded += _  => Lairs.LoadAll(ResolveActiveBbs()?.Name);
        Profile.BbsPinApplied += _  => Lairs.LoadAll(ResolveActiveBbs()?.Name);
        Profile.ProfileClosed += () => Lairs.LoadAll(null);
        LairTimers = new Game.Map.LairTimerStore(GameData, RoomGraph, RoomTracker, Log);

        // Phase 7 PR 7.18 — Encumbrance parser writes
        // PlayerState.Encumbrance from the `enc` line; HopTimingCalibrator
        // logs measured per-hop times tagged with that level. Enabled via
        // Settings → Other → "Log movement-hop timing".
        Encumbrance = new Game.EncumbranceParser(Router, PlayerState, Log);
        HopCalibrator = new Game.HopTimingCalibrator(RoomTracker, PlayerState, Log);

        // Per-BBS room blacklist — hides ganghouse / dead-end rooms
        // from the map render + room search. Loaded on BBS pin so
        // BFS picks it up via the Changed event before the first
        // layout build for the new BBS.
        RoomBlacklist = new RoomBlacklistStore(Log);
        Profile.ProfileLoaded += p => RoomBlacklist.OnBbsPinApplied(p);
        Profile.BbsPinApplied += p => RoomBlacklist.OnBbsPinApplied(p);
        // BFS consults the blacklist to skip placement of hidden
        // rooms (edge still recorded → dangling stub). Cache flushes
        // on every blacklist change so the next layout build picks
        // up the new filter.
        Bfs.ConfigureBlacklist(RoomBlacklist.IsBlacklisted);
        RoomBlacklist.Changed += () => Bfs.InvalidateCache();

        // Phase 7 PR 7.16 — loop execution engine. MainWindowViewModel
        // binds the wire-sender once telnet is up (same pattern as
        // the walker). RoomGraph passed in so the runner can resolve
        // MoveLoopStep sequences into room-key polylines for the map
        // overlay.
        LoopRunner = new Game.Map.LoopRunner(RoomTracker, MovementCoordinator,
            PromptScanner, Log, RoomGraph, Recovery, Bfs, Walker, Movement);
        // PR 4.b — same proactive pre-move sneak for loop circuits.
        LoopRunner.SetPreMoveHook(() => Stealth.RequestPreMoveStealth());
        // Avoid-list mutation mid-loop → LoopRunner re-routes via a
        // Stop+Start cycle so the new filter applies on the next BFS.
        Movement.AvoidedChanged += () => LoopRunner.NotifyAvoidedChanged();

        // Deterministic Auto-Lair scheduler — picks the next marked
        // lair to enter based on respawn timers + travel cost, parks
        // at a wait-room one hop short, then steps in on the tick.
        AutoLair = new Game.Map.AutoLairManager(
            Walker, RoomTracker, RoomGraph, Bfs, LairTimers, Log, MovementCoordinator);

        // Shared room-search resolver — backs the Nav rail search
        // box AND the @goto handler. Subscribes to ActiveSetChanged
        // + GraphReloaded internally so callers don't need to wire
        // cache invalidation.
        RoomSearch = new RoomSearchService(
            RoomGraph, GameData, Bfs, RoomBlacklist, Movement, Log);

        // Phase 7 PR 7.23 — MovePlayer remote-command handler.
        // Registers @goto, @loop, @lair, @stop, @rego against the
        // RemoteCommandManager. Dispatch routes to the now-existing
        // Walker / LoopRunner / AutoLairManager. The Catalog permission
        // gate ensures only players the user has granted MovePlayer
        // can issue these.
        MoveRemote = new Game.Remote.MovePlayerHandler(
            RemoteCommands, RoomSearch, RoomGraph, RoomTracker, Walker, Loops, LoopRunner,
            Lairs, AutoLair, MovementCoordinator);

        // PR 6.1 — leader-side @comeback. Snapshots the running movement
        // engine, stops it (stop-and-restart, NOT a coordinator gate —
        // a gate would block the recovery walk itself), walks to recover
        // the stranded follower (explicit room or backtrack along the
        // just-walked RoomTracker trail), re-invites + awaits follow,
        // then resumes the captured engine. MaxBacktrackRooms is pushed
        // from Settings → Other by ApplyOtherFromActiveProfile on load.
        PartyComeback = new Game.Remote.PartyComebackManager(
            RemoteCommands, Party, RoomTracker, RoomClassifier, Walker, LoopRunner, AutoLair, Log);

        // PR 6.2 — follower-side @comeback. Watches for a movement-failure
        // line (prevents-movement flag / over-encumbered) immediately
        // before "You are no longer following X." — the signature of being
        // left behind — and telepaths @comeback to the leader. Enabled is
        // pushed from Settings → Other by ApplyOtherFromActiveProfile.
        ComebackRequest = new Game.Remote.ComebackRequester(Router, RoomTracker, Log);

        // Phase 8 PR 8.1 — EventManager. Holds the loaded character's
        // scheduled / lifecycle events, dispatches actions into the
        // existing movement / command stack, and reconciles saved Loop /
        // AutoLair target references against their managers'
        // collections.
        Events = new Game.Events.EventManager(
            Profile, Loops, Lairs, LoopRunner, AutoLair, Walker, Log);

        // Phase 8 PR 8.2 — EventScheduler. Owns the AtTime ticker +
        // per-event Every-timers + connection-aware Logon / Re-log
        // latch. Subscribes to the stable WirePromptScanner singleton
        // for in-game detection; MainWindowVM signals Connected /
        // Disconnected via NotifyConnected / NotifyDisconnected since
        // the TelnetClient itself is per-connection.
        EventScheduler = new Game.Events.EventScheduler(
            Events, PromptScanner, Cleanup, Profile, Log);

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
    /// <summary>
    /// Returns whichever of Walker / LoopRunner / AutoLair is currently
    /// not Idle. Per design they're mutually exclusive (entering one
    /// cleanly exits the other) so a simple first-non-idle scan is
    /// sufficient. Returns <c>null</c> when the player is idle —
    /// HealthManager treats that as "don't flee".
    /// </summary>
    private Game.Map.IRecoverableEngine? ResolveActiveMovementEngine()
    {
        if (Walker.State != Game.Map.WalkState.Idle) return Walker;
        if (LoopRunner.State != Game.Map.LoopState.Idle) return LoopRunner;
        // AutoLair routes through the walker when stepping; its own
        // state machine reflects scheduling. If the walker is idle
        // the AutoLair has nothing to flee from either.
        return null;
    }

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
    /// Read a single boolean off the active profile's
    /// <see cref="Models.Profile.GeneralSettings.AutoMode"/>. Used by
    /// the Phase 9 engine isEnabled delegates so toggling Settings →
    /// General → Auto-Combat (or the toolbar Toggle button) takes
    /// effect immediately — no event subscription needed since each
    /// engine queries on every tick / classifier emit.
    /// </summary>
    private bool ReadAutoModeFlag(Func<Models.Profile.AutoActionDefaults, bool> selector)
    {
        Models.Profile.GeneralSettings general =
            ReadSection<Models.Profile.GeneralSettings>(Profile.Current, "General");
        return selector(general.AutoMode);
    }

    /// <summary>
    /// Resolve a single room "You notice ..." entry for
    /// <see cref="AutoGetItems"/>: map the loose wording to an item
    /// Number, read its verbatim Name, and resolve the per-character
    /// <see cref="Models.GameData.ItemOverlay.AutoCollect"/> override
    /// (Defaults seed → Global → BBS → Char). Returns <c>null</c> when
    /// the entry isn't an item in the active set (cash, scenery), so the
    /// engine skips it. AutoCollect defaults to <c>false</c> — pickup is
    /// opt-in per item.
    /// </summary>
    private Game.Inventory.AutoGetItemsManager.ResolvedItem? ResolveAutoGetItem(string entry)
    {
        if (ItemNames.FindByName(entry) is not int number) return null;
        string? name = ItemNames.GetName(number);
        if (string.IsNullOrWhiteSpace(name)) return null;

        Models.GameData.ItemOverlay overlay = Resolver.ResolveGameData<Models.GameData.ItemOverlay>(
            "Items",
            number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ItemOverlaySeed.GetOverlay(number));

        return new Game.Inventory.AutoGetItemsManager.ResolvedItem(name, overlay.AutoCollect ?? false);
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
        // Shared nag cadence — same Settings.Party knobs feed both the
        // AutoPartyManager @join-after-invite loop and the PartyPoller
        // on-join @health retry. UI groups them under one section
        // header ("@join/@health nag settings").
        TimeSpan nagInitial = TimeSpan.FromSeconds(Math.Clamp(dto.JoinNagInitialDelaySec, 1, 60));
        TimeSpan nagFreq    = TimeSpan.FromSeconds(Math.Clamp(dto.JoinNagFrequencySec,    1, 60));
        TimeSpan nagMax     = TimeSpan.FromSeconds(Math.Clamp(dto.JoinNagMaxTotalSec,     5, 600));
        AutoParty.JoinNagInitialDelay = nagInitial;
        AutoParty.JoinNagFrequency    = nagFreq;
        AutoParty.JoinNagMaxTotal     = nagMax;
        PartyPoller.HealthNagInitialDelay = nagInitial;
        PartyPoller.HealthNagFrequency    = nagFreq;
        PartyPoller.HealthNagMaxTotal     = nagMax;
    }

    private void ResetPartyToDefaults()
    {
        Models.Profile.PartySettings defaults = new();
        PartyPoller.SetParCadence(TimeSpan.FromSeconds(defaults.ParPollFrequencySec));
        Party.AutoInviteEnabled = defaults.AutoInviteReconnecting;
        Party.DisconnectGraceWindow = TimeSpan.FromSeconds(defaults.IfLeadingWaitTotalSec);
        Party.LocalRankPreference = defaults.Rank;
        PartyBroadcaster.AutoExpResetEnabled = defaults.ResetStatisticsOnLoopStart;
        TimeSpan nagInitial = TimeSpan.FromSeconds(defaults.JoinNagInitialDelaySec);
        TimeSpan nagFreq    = TimeSpan.FromSeconds(defaults.JoinNagFrequencySec);
        TimeSpan nagMax     = TimeSpan.FromSeconds(defaults.JoinNagMaxTotalSec);
        AutoParty.JoinNagInitialDelay = nagInitial;
        AutoParty.JoinNagFrequency    = nagFreq;
        AutoParty.JoinNagMaxTotal     = nagMax;
        PartyPoller.HealthNagInitialDelay = nagInitial;
        PartyPoller.HealthNagFrequency    = nagFreq;
        PartyPoller.HealthNagMaxTotal     = nagMax;
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
        // @trap auto-disarm attempt caps.
        TrapDisarm.MaxSearchAttempts = Math.Clamp(dto.MaxTrapSearchAttempts, 1, 100);
        TrapDisarm.MaxDisarmAttempts = Math.Clamp(dto.MaxTrapDisarmAttempts, 1, 50);
        // Leader-side @comeback backtrack budget.
        PartyComeback.MaxBacktrackRooms = Math.Clamp(dto.MaxComebackBacktrackRooms, 1, 50);
        // Follower-side auto-@comeback toggle.
        ComebackRequest.Enabled = dto.AutoRequestComebackWhenLeftBehind;
        // Hop-timing calibration logger — off by default; user flips
        // on for a data-collection session.
        HopCalibrator.Enabled = dto.LogMovementHopTiming;
    }

    private void ResetOtherToDefaults()
    {
        Models.Profile.OtherSettings defaults = new();
        RemoteCommands.MaxSuicideLivesThreshold = defaults.MaxSuicideLivesThreshold;
        GameCommands.EntryCommand = defaults.GameEntryCommand;
        GameCommands.ExitCommand  = defaults.GameExitCommand;
        TrapDisarm.MaxSearchAttempts = defaults.MaxTrapSearchAttempts;
        TrapDisarm.MaxDisarmAttempts = defaults.MaxTrapDisarmAttempts;
        PartyComeback.MaxBacktrackRooms = defaults.MaxComebackBacktrackRooms;
        ComebackRequest.Enabled = defaults.AutoRequestComebackWhenLeftBehind;
    }

    /// <summary>
    /// Push the loaded character's
    /// <see cref="Models.Profile.AutoLairSettings"/> into
    /// <see cref="AutoLair"/> — heuristic, idle penalty, engage timeout,
    /// and the chosen <see cref="Game.Map.ITravelCostModel"/>
    /// implementation. Same shape as
    /// <see cref="ApplyOtherFromActiveProfile"/>.
    /// </summary>
    public void ApplyAutoLairFromActiveProfile()
    {
        Models.Profile.AutoLairSettings dto =
            ReadSection<Models.Profile.AutoLairSettings>(Profile.Current, "AutoLair");
        AutoLair.Heuristic = dto.Heuristic;
        AutoLair.IdlePenalty = Math.Max(0, dto.IdlePenalty);
        AutoLair.EngageTimeoutSeconds = Math.Clamp(dto.EngageTimeoutSeconds, 1, 3600);
        AutoLair.TravelCostModel = dto.TravelCostMode switch
        {
            Models.Profile.AutoLairTravelCostMode.EncumbranceGated =>
                new Game.Map.EncumbranceGatedTravelCostModel(PlayerState, dto.HopTimesByEncumbrance),
            _ =>
                new Game.Map.FlatTravelCostModel(Math.Max(0.1, dto.FlatSecondsPerHop)),
        };
    }

    private void ResetAutoLairToDefaults()
    {
        Models.Profile.AutoLairSettings defaults = new();
        AutoLair.Heuristic = defaults.Heuristic;
        AutoLair.IdlePenalty = defaults.IdlePenalty;
        AutoLair.EngageTimeoutSeconds = defaults.EngageTimeoutSeconds;
        AutoLair.TravelCostModel = new Game.Map.FlatTravelCostModel(defaults.FlatSecondsPerHop);
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
