namespace FujinTerm.Services;

// Lightweight singleton service holder. POCO — no DI container.
// Every cross-cutting service the app owns is exposed as an instance property
// here (profile/settings I/O, message bus, dialog spawner, log service,
// importers, game-data cache, etc.).
// Per-character / per-game-data lifetime is event-driven: services subscribe
// to ProfileService.ProfileLoaded and GameDataCache.ActiveSetChanged and reload
// their per-scope state in those handlers. There is intentionally no IoC
// container — explicit subscription and explicit teardown beats magic
// resolution at this scale (see CLAUDE.md "Architecture rules").
public sealed class AppServices
{
    private static AppServices? _current;

    // The active service holder. Initialize must be called first.
    public static AppServices Current => _current
        ?? throw new InvalidOperationException(
            "AppServices not initialized — call AppServices.Initialize() during app startup.");

    // Owns Data/Global/global.json — the Global settings tier.
    public SettingsService Settings { get; }

    // Owns the currently loaded character profile (Character tier).
    public ProfileService Profile { get; }

    // Owns Data/BBS/*.json — the BBS tier.
    public BbsProfileStore Bbs { get; }

    // Single read / write API for the 4-tier settings + game-data override
    // hierarchy (Defaults → Global → BBS → Character).
    public SettingsResolver Resolver { get; }

    // Modeless-only window spawner (no ShowDialog wrapper).
    public DialogService Dialogs { get; }

    // Opens the single-instance Game Data Browser at the Items section,
    // pre-selected to a given item's record. Only MainWindowViewModel can
    // spawn / toggle that window, so it registers the opener here and deep
    // VMs (the Item Finder's row double-click) reach it without a back-
    // reference to the main VM. No-op until the main VM binds it.
    private Action<int>? _itemGameDataOpener;
    public void SetItemGameDataOpener(Action<int> opener) => _itemGameDataOpener = opener;
    public void OpenItemGameData(int itemNumber) => _itemGameDataOpener?.Invoke(itemNumber);

    // Single source of truth for "are you sure?" prompts (exit /
    // hangup / save / delete). Lives at Global tier; mirrored from
    // SettingsService on startup and every save.
    public ConfirmService Confirm { get; }

    // App-wide severity-tagged ring-buffer log. Status bar + log pane subscribe.
    public LogService Log { get; }

    // Session-only diagnostic switches surfaced in the Log pane menu
    // (combat-verbose / round-trace umbrella). Consumers
    // (e.g. Game.Combat.RoundDamageTracker) read this
    // instead of per-character settings because verbose tracing isn't
    // a per-character affordance — it's a "while I'm debugging right
    // now" knob that resets on app launch.
    public LogDiagnosticState LogDiagnostics { get; } = new();

    // Docking / floating panel framework (single-UserControl reparented).
    public FloatingPanelHost Panels { get; }

    // Per-character top-level window position + size memory. Each
    // window calls WindowLayoutStore.AttachWindow once
    // during construction with a stable id; the store handles
    // restore-on-open and capture-on-close, hydrating from
    // CharacterProfile.WindowBounds on profile load and
    // snapshotting back on save.
    public WindowLayoutStore WindowLayouts { get; }

    // Per-character splitter-position memory for two-pane resizable
    // dialogs. Each dialog calls SplitterLayoutStore.AttachGrid
    // once during construction with a stable id + the Grid to manage;
    // the store handles restore-on-open and capture-on-close,
    // hydrating from CharacterProfile.SplitterRatios on
    // profile load and snapshotting back on save.
    public SplitterLayoutStore SplitterLayouts { get; }

    // Per-character memory of the Session Stats window's panel order +
    // hidden set. The window's VM reads it on open and pushes drag-reorders /
    // visibility toggles back through it; it hydrates from
    // CharacterProfile.SessionStatsLayout on profile load and
    // snapshots back on save.
    public SessionStatsLayoutStore SessionStatsLayout { get; }

    // Ring buffer of recent cleaned (post-IAC) bytes from the live Telnet
    // connection. Feeds the Wire Inspector window and any future
    // "what did the server just say" diagnostic.
    public WireBuffer Wire { get; }

    // Central pattern bus. Every line-aware subsystem (ChatRouter,
    // Triggers, automation engines) registers patterns + handlers here;
    // LineExtractor.LineEmitted is forwarded into
    // MessageRouter.Dispatch.
    public MessageRouter Router { get; }

    // Classifies chat / realm-event lines into Game.ChatLogEntry
    // events. ChatHistoryStore and the Conversation window
    // subscribe to EntryClassified.
    public Game.ChatRouter Chat { get; }

    // App-singleton chat history. Survives profile swap / connect /
    // disconnect; cleared only on app exit or explicit
    // Game.ChatHistoryStore.Clear.
    public Game.ChatHistoryStore ChatHistory { get; }

    // Live player state — HP / mana / position / mana type. Updated by
    // Player from every prompt line; bound by the status
    // bar, the Workshop STATS section, and automation
    // engines that gate on HP / MP thresholds.
    public Game.PlayerState PlayerState { get; }

    // Parses MajorMUD status-line prompts into PlayerState.
    // Sole writer of the state's HP / MA / position / mana-type fields
    // (the single-writer IL scan enforces this).
    public Game.PromptParser Player { get; }

    // Live party-membership state — roster, leader, per-member HP%/MA%/
    // position/status-flags. Updated by Party from
    // follows-you / stops-following messages and the multi-line
    // par table. Bound by the PartyWindow and read by the
    // remote-command engine to gate the @party <sub> whitelist.
    // Client-side terminal line buffer. Routes user keystrokes through
    // a local 254-char accumulator that only flushes to the wire on
    // Enter. Without this, engine auto-sends (par poll, AutoParty
    // invite, @health round-trip, etc.) interleave into half-typed
    // user input on the server's line buffer and submit as garbage
    // commands. See Terminal.LocalInputBuffer.
    public Terminal.LocalInputBuffer InputBuffer { get; } = new();

    // Shared recall ring of the user's most-recent typed commands. The
    // terminal line buffer and the Conversation window both record into
    // it and read from it for Up / Down recall. App-session lifetime —
    // see CommandHistory.
    public CommandHistory CommandHistory { get; } = new();

    public Game.PartyState PartyState { get; }

    // Sole writer of PartyState — every observable field
    // on Game.PartyState and Game.PartyMember
    // declares this type via OwnerAttribute, enforced by
    // the single-writer IL scan.
    public Game.PartyManager Party { get; }

    // Remote-command engine. Subscribes to Chat's
    // Game.ChatRouter.EntryClassified, identifies
    // @-prefixed messages from other players, enforces hard-blocks
    // and per-player Models.GameData.PlayerRemoteControls
    // permissions, and dispatches to registered handlers.
    public Game.Remote.RemoteCommandManager RemoteCommands { get; }

    // Registers the party-essential @-command handlers
    // against RemoteCommands: @health, @where,
    // @version, @status, @lives,
    // @party (status query + sub-command dispatch),
    // @invite, @join, @wait, @ok. Later
    // phases register additional handlers without going through this
    // class.
    public Game.Remote.PartyEssentialHandlers PartyEssentials { get; }

    // Tracks who's dragging our mortally-wounded body (the
    // "<leader> is dragging you around." line). Read by the @join / @invite
    // refusal reply so a downed member can tell a partymate whether help is
    // already underway.
    public Game.DraggedTracker Dragged { get; }

    // Drives the on-join @health exchange that
    // captures each new Game.PartyMember's absolute HP/MA
    // baseline, plus the periodic par poll (5 s default cadence;
    // Settings.Party carries the user-configurable frequency).
    public Game.PartyPoller PartyPoller { get; }

    // Emit side of @wait / @ok. Observes
    // PlayerState.Position transitions and telepaths the
    // leader when the local character enters / leaves a rest state.
    // Receive side lives in Game.Remote.PartyEssentialHandlers.
    public Game.PartyRestSync PartyRest { get; }

    // One-to-many @-command sender. Used for Auto-Exp-Reset
    // (@Reset broadcast on loop / Auto-Lair start) and the
    // panic / kill broadcasts.
    public Game.Remote.PartyBroadcaster PartyBroadcaster { get; }

    // Live mirror of the per-character game-menu commands
    // (GameCommands.EntryCommand /
    // GameCommands.ExitCommand). Hydrated from the
    // Other-tab settings on every profile load + Apply; engines
    // (Game.Remote.HangupHandler, future cleanup-flow
    // automation) read from here instead of going through
    // Profile directly.
    public GameCommands GameCommands { get; } = new();

    // Consumer of RemoteCommands for the
    // Models.GameData.PlayerRemoteControls.HangupDisconnect
    // permission category — currently just @hangup. Sends the
    // configured Services.GameCommands.ExitCommand to
    // the wire when a permitted sender requests it.
    public Game.Remote.HangupHandler Hangup { get; }

    // Consumer of RemoteCommands for the
    // Models.GameData.PlayerRemoteControls.HangupDisconnect
    // permission category — @relog. Sends the configured
    // Services.GameCommands.ExitCommand to gracefully log
    // out, then arms RelogSignal so MainWindowVM forces a
    // reconnect-and-login cycle.
    public Game.Remote.RelogHandler Relog { get; }

    // Consumer of RemoteCommands for the
    // Models.GameData.PlayerRemoteControls.DivertConversations
    // category — @divert <player>. While diverting, repeats
    // every incoming telepath to the chosen target as
    // <sender> telepathed: <message>; bare @divert
    // stops.
    public Game.Remote.DivertHandler Divert { get; }

    // Consumer of RemoteCommands for the
    // Models.GameData.PlayerRemoteControls.QueryVersion
    // category — @help. Replies with the flat list of remote
    // commands the sender's per-player permission grant allows, split
    // across telepaths when long.
    public Game.Remote.HelpHandler Help { get; }

    // Consumer of RemoteCommands for the
    // Models.GameData.PlayerRemoteControls.QueryExperience
    // category — @exp (session exp, rate, ETA) and @level
    // (level, total exp, exp-to-next). Read-only; replies only.
    public Game.Remote.ExperienceQueryHandler ExperienceQuery { get; private set; } = null!;

    // Tracks the items on the current room floor from the "You notice
    // <list> here." survey (cash excluded). Feeds the read-side
    // @what and the write-side @get-all; cleared on room change.
    public Game.Inventory.GroundItemTracker GroundItems { get; private set; } = null!;

    // Consumer of RemoteCommands for the
    // Models.GameData.PlayerRemoteControls.QueryInventory
    // category — @wealth / @enc / @have / @what.
    // Reads the Game.Inventory.InventoryManager snapshot and the
    // GroundItems survey; replies only.
    public Game.Remote.InventoryQueryHandler InventoryQuery { get; private set; } = null!;

    // Write-side consumer of RemoteCommands for the inventory /
    // cash action commands — @get-all / @drop-all /
    // @deposit-all (ExecuteCommands) and @share (party-whitelist).
    // Emits get / drop / dep / with / give on
    // the wire, so its sender is bound in MainWindowViewModel.
    public Game.Remote.InventoryActionHandler InventoryAction { get; private set; } = null!;

    // Receive side of @heal: a configured party-healer polls par
    // on request so CastDirector re-evaluates its party-heal
    // thresholds against fresh member HP. The emit side is the follower
    // flee-substitute in Health / PartyRest.
    // Sends par, so its sender is bound in MainWindowViewModel.
    public Game.Remote.HealCommandHandler Heal { get; private set; } = null!;

    // Consumer of RemoteCommands for the MovePlayer
    // category: @goto / @loop / @lair / @stop / @rego. Wires the
    // remote walk-to / loop-start / lair-cycle / pause / resume
    // dispatch into the Navigation stack.
    public Game.Remote.MovePlayerHandler MoveRemote { get; private set; } = null!;

    // Centralised room-search resolver. Backs the Navigation rail
    // search box, the Loop / Lair editor "Add room" rows, the
    // Center-on dialog, and the @goto remote handler.
    public RoomSearchService RoomSearch { get; private set; } = null!;

    // Consumer of RemoteCommands for the
    // Models.GameData.PlayerRemoteControls.ExecuteCommands
    // permission category's @do <command> passthrough.
    // Joins the sender's args back into a single command string and
    // ships it on the wire. Engine-level hard-blocks (reroll,
    // suicide-lives-threshold) already gate the catalogue's
    // destructive verbs before this handler runs.
    public Game.Remote.DoHandler Do { get; }

    // @auto-* remote command family
    // (party member toggles our AutoMode flags). Backed by the
    // loaded character profile's General section.
    public Game.Remote.AutoModeRemoteHandler AutoMode { get; private set; } = null!;

    // @atkprio / @atkorder remote commands — a party member
    // changes our Target Priority (who) / Attack Order (when) via the same
    // numbered options as the Combat tab's dropdowns. Backed by the loaded
    // character profile's Combat section.
    public Game.Remote.AttackTargetingRemoteHandler AttackTargeting { get; }

    // @kill <target> remote command — a party member asks us to
    // engage a named monster. Retargets Combat (forcing an
    // engage even with master auto-attack off) and stays silent on success.
    public Game.Remote.KillHandler Kill { get; }

    // Master "Auto-All" kill-switch shared by the toolbar / Action-menu
    // button and the @auto-all remote command. One press snapshots
    // + clears every wired auto-engine; the next restores the snapshot.
    public Game.AutoModeController AutoModeController { get; }

    // Leader-side @comeback party-pickup flow — pauses the
    // running movement engine, walks to recover a stranded follower
    // (explicit room or backtrack along the just-walked path), re-
    // invites + awaits follow, then resumes the captured engine. The
    // Game.Remote.PartyComebackManager.MaxBacktrackRooms
    // budget is pushed from Settings → Other.
    public Game.Remote.PartyComebackManager PartyComeback { get; private set; } = null!;

    // Follower-side @comeback sender. Detects being left
    // behind (a movement-failure line just before "You are no longer
    // following X.") and telepaths @comeback to the leader.
    // Game.Remote.ComebackRequester.Enabled is pushed from
    // Settings → Other.
    public Game.Remote.ComebackRequester ComebackRequest { get; private set; } = null!;

    // Follower-side reconnect auto-rejoin. Remembers the leader we follow
    // (crash-survivable in the profile) and, on the first in-game room display
    // after a reconnect, telepaths @comeback then @invite to walk us back into
    // the party. Cleared on a deliberate leave or clean shutdown.
    public Game.Remote.PartyRejoinCoordinator PartyRejoin { get; private set; } = null!;

    // Drives the @trap <direction> auto-disarm flow:
    // search → disarm state machine + FIFO request queue + Stats-
    // skill gate. Bound by TrapRemote's handler at
    // dispatch time, configured via the
    // Models.Profile.OtherSettings.MaxTrapSearchAttempts
    // / MaxTrapDisarmAttempts knobs in Settings → Other.
    public Game.TrapDisarmManager TrapDisarm { get; }

    // Party-member trap delegation — when the local character can't
    // disarm a trapped exit but a capable party member can, broadcasts
    // @trap <dir> on say and resumes the walk on the
    // member's say reply. Capability via class (main gate) + race
    // (secondary). Distinct from TrapDisarm, which owns the
    // LOCAL self-disarm path keyed on the game's first-person signals.
    public Game.TrapDelegationManager TrapDelegation { get; }

    // Walker's door-handling FSM — bash / pick / open with
    // configurable attempt caps. Subscribes to Router
    // for the door-message patterns; the walker calls
    // Game.Map.DoorOpenManager.Enqueue at door-exit
    // step time and resumes on the callback's terminal
    // Game.Map.DoorOpenResult. Attempt caps + verb
    // preference (bash vs pick) read live from Settings.Other on
    // each request.
    public Game.Map.DoorOpenManager Door { get; }

    // Helps the party leader force a door — when we observe the leader
    // fail to bash a door we can see, send the same bash / pick
    // verb at the same direction. Gated on
    // Models.Profile.PartySettings.HelpLeaderOpenDoors.
    public Game.Map.LeaderDoorAssistManager LeaderDoorAssist { get; }

    // Walker's hidden-exit reveal FSM — fires sea <dir>
    // in a retry loop until the exit appears on the room display.
    // Subscribes to RoomTracker.StateChanged for the
    // "exit now visible" signal; max retries pulled live from
    // Models.Profile.OtherSettings.MaxHiddenSearchAttempts.
    public Game.Map.HiddenExitRevealManager HiddenSearch { get; }

    // Auth boundary + queue gate for @trap: parses the
    // direction, runs the channel-aware Traps-skill gate, and hands
    // off to TrapDisarm. @trap stop drains the
    // queue + aborts the in-flight request.
    public Game.Remote.TrapHandler TrapRemote { get; }

    // @train handler — trains in place (no walk) on a permitted party
    // member's request, applying the CP plan when Auto-train-stats is on.
    public Game.Remote.TrainHandler TrainRemote { get; }

    // @equip-<set> handler — a permitted party member asks us to
    // wear one of our saved gear sets. The set keyword is the suffix after
    // @equip-; routed via RemoteCommands's prefix handler
    // into Equipment.
    public Game.Remote.EquipHandler EquipRemote { get; private set; } = null!;

    // Consumer of RemoteCommands for @suicide.
    // Authorised callers (Elevated-Commands permission, lives above
    // the suicide threshold) trigger the suicide round-trip; on
    // "Invalid password specified." the handler telepaths the
    // caller back so they know our stored password is stale.
    public Game.Remote.SuicideHandler Suicide { get; private set; } = null!;

    // Consumer of RemoteCommands for @reset — an
    // authorised party member zeroes our session-stats trackers,
    // the same wipe the Session Stats window's "Reset session" button does.
    public Game.Remote.SessionResetHandler SessionReset { get; private set; } = null!;

    // Snapshot of the most recent stat-screen parse. Written exclusively by Stats.
    public Game.PlayerStats PlayerStats { get; } = new();

    // Parses the in-game stat screen and writes every field
    // onto PlayerStats. Feeds
    // RemoteCommands's LivesProvider so the
    // @suicide hard-block has a real value to gate against.
    public Game.StatParser Stats { get; private set; } = null!;

    // Per-class learnable-spell catalogue built from the active game-data
    // set — computes each spell's usability from the class + level gates.
    // Backs both the Spell Book window and the Settings spell pickers.
    public Game.Spells.KnownSpellCatalog SpellCatalog { get; }

    // The local character's spell book — the class's full learnable list
    // paired with the obtained set. Refreshed from Stats'
    // class+level on every stat poll; obtained set fed by
    // SpellList.
    public Game.Spells.SpellbookState Spellbook { get; }

    // Parses spells / pow output into
    // Spellbook's obtained set. App-level; bound to the
    // per-session Terminal.LineExtractor by
    // ViewModels.MainWindowViewModel.
    public Game.Spells.SpellListParser SpellList { get; }

    // Marks powers obtained the moment they're learned at training (the
    // "You learn the following Kai abilities:" block). Incremental, like the
    // learn-scroll line — feeds Spellbook's obtained set
    // without snapshotting it. Bound to the per-session
    // Terminal.LineExtractor by
    // ViewModels.MainWindowViewModel.
    public Game.Spells.TrainLearnParser TrainLearn { get; }

    // Sends the configured GameCommands.EntryCommand
    // when the MajorMUD main-menu screen is recognised at the tail
    // end of the automated BBS-login sequence. Latched closed by
    // default — only briefly armed when Services.LoginAutomator.LoggedIntoGame
    // fires, so an in-game chat line that happens to look like the
    // menu (gossip / telepath / room description) can't trick the
    // engine into auto-entering when the player wanted to stay
    // out-of-realm.
    public Game.MainMenuEntryAutomation MainMenuEntry { get; }

    // Consumer of the per-player
    // Models.GameData.PlayerCustomization.InviteToPartyIfSeen
    // and
    // Models.GameData.PlayerCustomization.JoinPartyIfInvited
    // flags. Watches "Also here:" room-occupant lines + incoming
    // "X invites you to join their party" messages and drives the
    // matching invite / follow commands. Wire-sender
    // bound from ViewModels.MainWindowViewModel.
    public Game.AutoPartyManager AutoParty { get; }

    // Detects the in-game train stats menu round-trip so we can
    // refresh party state after the user returns to the realm. Armed
    // by observing outbound train stats on the wire-send path
    // (ViewModels.MainWindowViewModel.SendUserInput calls
    // Game.TrainerMenuTracker.ObserveOutbound) and
    // confirmed by the anchored "Point Cost Chart" marker.
    public Game.TrainerMenuTracker TrainerMenu { get; }

    // Scans the post-IAC wire stream for status-line prompts. Feeds
    // Player directly so prompts overwritten in place on
    // a single row (server CR + erase-line + rewrite) don't get lost
    // the way they would going through Terminal.LineExtractor.
    public WirePromptScanner PromptScanner { get; }

    // Reasserts the editor's statline on every connect. Verifies the live
    // prompt against the editor-built pattern and resends set statline
    // when the game has drifted (e.g. a fresh character on the class default).
    public Game.StatlineReconciler StatlineReconcile { get; }

    // Sniffs the post-IAC wire stream for "BBS shutting down in N minutes"
    // announcements. The connect lifecycle in MainWindowViewModel reads
    // CleanupWarningWatcher.Latest on disconnect to decide
    // whether to arm an auto-reconnect.
    public CleanupWarningWatcher Cleanup { get; } = new();

    // Proactive log-off engine for the nightly-cleanup cycle: on the
    // BBS's shutdown warning it waits for a safe room, exits to the main
    // menu, and drops the carrier — handing off to the predictive
    // reconnect scheduler in MainWindowViewModel. Opt-in behind the
    // active BBS's Models.Settings.BbsProfile.ReconnectAfterCleanup.
    public Game.CleanupLogoutOrchestrator CleanupLogout { get; }

    // Combat / HP / MA tick heartbeat. Status bar countdown binds here;
    // automation engines subscribe to CombatTickElapsed +
    // the regen ticks.
    public Game.TickEngine Tick { get; }

    // Observation-based regen tracker. Folds upward HP / MA deltas into
    // per-position running averages; subscribed to by the status bar and
    // HealthManager for tick-aware automation.
    public Game.RegenTracker Regen { get; }

    // Debug-channel instrument that traces observed HP / MA regen ticks to
    // the program log (silent unless the Log pane's Debug toggle is on). Held
    // here purely to keep the Regen subscription alive for the
    // app's lifetime; nothing reads it back.
    public Game.RegenDiagnosticsRecorder RegenDiagnostics { get; }

    // Live mirror of the loaded character profile's Display settings.
    // The Settings → Display section writes through to this so changes
    // (font size in particular) apply without restarting the app.
    public DisplayConfig Display { get; } = new();

    // Global-tier toolbar visibility mirror. MainWindow toolbar buttons
    // bind their IsVisible here. Hydrated on startup from the
    // "Toolbar" entry in SettingsService.Current.Settings
    // and re-hydrated on every SettingsService.GlobalSettingsChanged
    // tick.
    public ToolbarConfig Toolbar { get; } = new();

    // AES-GCM encrypt / decrypt for short secrets (BBS passwords).
    // Ciphertext is stored inline on the owning record (e.g.
    // Models.Profile.BbsCredentials.EncryptedPassword),
    // so profile JSON stays fully self-contained for backup. The
    // per-user key lives at Data/.credkey.
    public PasswordProtector Passwords { get; } = new();

    // One-flag pause switch wrapping every engine's wire-sender.
    // Raised by Game.SuicidePasswordTracker while a
    // password-entry prompt is active so engine auto-sends don't
    // pollute the input.
    public EngineSendGate EngineGate { get; } = new();

    // Two-flag one-shot coordinator for "intentional hangup" intent.
    // Set by every engine that deliberately drops the carrier
    // (Game.Remote.HangupHandler; the hang-up-if-naked /
    // hang-up-if-low-HP automation).
    // Consumed by ViewModels.MainWindowViewModel (to
    // suppress reactive auto-reconnect) and by
    // Game.MainMenuEntryAutomation (to suppress the
    // auto-entry latch on the next connect so the user can read
    // what's on screen and decide).
    public HangupSignal HangupSignal { get; } = new();

    // One-shot coordinator for "relog" intent — a graceful exit plus a
    // forced reconnect-and-login. Set by
    // Game.Remote.RelogHandler when an authorised sender
    // requests @relog; consumed by
    // ViewModels.MainWindowViewModel to force the
    // unconditional dial-back. Inverse of HangupSignal:
    // relog does NOT suppress the entry automation, so login runs
    // normally on the reconnect.
    public RelogSignal RelogSignal { get; } = new();

    // Passive observer for the in-game set suicide /
    // suicide password flows. Locks
    // EngineGate for the duration of each prompt and
    // captures the user-typed new password (committed to the
    // profile's Models.Profile.CharacterProfile.EncryptedSuicidePassword
    // on the server-side Password Changed confirmation).
    public Game.SuicidePasswordTracker SuicidePassword { get; private set; } = null!;

    // Live cache of imported MajorMUD game data. Loads JSON tables on
    // demand from Data/game data/{set}/; the active set follows
    // the pinned BBS's
    // Models.Settings.BbsProfile.ActiveGameDataSet field
    // (falling back to Models.Settings.GlobalSettings.DefaultGameDataSet
    // when no BBS is pinned). Per-tab consumers
    // convert raw System.Text.Json.JsonDocument rows into
    // typed model collections and call EvictTable to drop the
    // raw bytes.
    public GameDataCache GameData { get; } = new();

    // In-memory cache of the active character's
    // Models.GameData.Trigger list + the shared
    // session-scoped named-variable store used by both triggers and
    // aliases. Drives MessageRouter integration + runtime action
    // dispatch.
    public TriggerEngine Triggers { get; }

    // In-memory cache of the active character's
    // Models.GameData.Alias entries. Outgoing-text
    // mirror of Triggers; matches on the first token of typed input.
    public AliasEngine Aliases { get; }

    // Observed + edited Models.GameData.PlayerRecord
    // store. The who-output parser that calls RecordObservation
    // lives with PartyManager.
    public PlayerDatabase Players { get; }

    // Flags the local character's displayed alignment stale when the game
    // prints "A dark cloud passes over you", clearing on the next who.
    // Read by the Character Workshop's Character Info tab.
    public Game.AlignmentTracker Alignment { get; }

    // Drives the train stats screen to apply the saved CP plan. Wrapped
    // by TrainerWalk, which owns the walk-to-trainer + level-up.
    public Game.AutoTrainManager AutoTrain { get; }

    // Trainer-walk coordinator: resolves the nearest allowed, level-appropriate
    // trainer, walks there, trains, and applies the CP plan. Backs the CP
    // Allocation tab's Train Now + the armed auto-train.
    public Game.TrainerWalkManager TrainerWalk { get; }

    // Broadcasts "I can now train to level: N" on the configured channel when a
    // live experience gain makes a new level trainable. Gated by the Settings →
    // Auto-Trainer "Announce level-ups" toggle.
    public Game.LevelUpAnnouncer LevelUp { get; }

    // Loaded character's Models.GameData.Macro store.
    // Surfaced by the Game Data Browser → Macros tab; the
    // MacroManager engine intercepts keystrokes and dispatches from
    // the same store.
    public MacroStore Macros { get; }

    // Per-set quest name / visibility / edited-step overlay store. Backs the
    // Character Workshop → Quest Status tab (the mechanical step + bonus data is
    // crawled from GameData's TBInfo at runtime). Reloads its
    // overlay on GameDataCache.ActiveSetChanged.
    public QuestStore Quests { get; }

    // Runtime keystroke → macro → wire-send bridge. Constructed up-
    // front; MacroDispatcher.SetSender gets bound from
    // MainWindowViewModel after the telnet client is
    // ready. Pre-binding, key handlers fall through to the normal
    // terminal path.
    public MacroDispatcher MacroDispatcher { get; }

    // Loaded character's scheduled / lifecycle events store +
    // dispatcher. CRUD surface for the Settings →
    // Events tab; Game.Events.EventManager.Fire routes
    // to Walker / LoopRunner /
    // AutoLair / the bound wire sender.
    public Game.Events.EventManager Events { get; private set; } = null!;

    // Trigger sources for Events.
    // Owns the AtTime ticker, per-event Every-timers, and the
    // connection-aware Logon / Re-log latch. MainWindowVM calls
    // Game.Events.EventScheduler.NotifyConnected /
    // Game.Events.EventScheduler.NotifyDisconnected as
    // its TelnetClient raises those events, since the
    // telnet client is per-connection and not a stable singleton.
    // Logoff events fire via
    // Game.Events.EventManager.FireLogoffEvents
    // directly from the user-initiated disconnect path.
    public Game.Events.EventScheduler EventScheduler { get; private set; } = null!;

    // Per-character keybindings for built-in app actions (toolbar +
    // menu shortcuts). Sister service to Macros — both
    // contribute to the unified conflict-detection check so a chord
    // can never bind to both a macro and a built-in action.
    public KeybindingStore Keybindings { get; }

    // Active game-data set's Messages/Responses catalogue. Seeded
    // from the wcc-derived JSON at Data/Global/Messages.seed.json
    // (bootstrapped from the bundled Defaults/ copy on first
    // launch), persisted per set at Data/game data/{set}/messages.json.
    // Surfaced by the Game Data Browser → Messages tab; the
    // HealthManager / CastingDirector consume the same catalogue at
    // runtime to gate on observed conditions.
    public MessageStore Messages { get; private set; } = null!;

    // Active game-data set's Monster Messages catalogue — one record
    // per Monsters-table row, carrying the parser patterns for every
    // line a monster can produce in combat (HitYou / HitOther /
    // DeathLine / ArmorBlock / Dodge / Miss + flavor prefixes).
    // Generated offline from the wcc monster-messages.json
    // export joined on Monsters.Number; per-set edits land at
    // Data/game data/{set}/monster-messages.json.
    public MonsterMessageStore MonsterMessages { get; private set; } = null!;

    // Turns the wire's Also here: line into
    // a classified Player / Monster / Unknown list. Feeds
    // CombatTracker's gate decisions and the LogPane's
    // unknown-entity click-to-fix dialog.
    public Game.Combat.RoomEntityClassifier RoomClassifier { get; private set; } = null!;

    // Auto-greets newly-seen non-party players (Settings → Talk
    // "Greet players when first met"). Subscribes to
    // RoomClassifier's observations; once-per-local-day
    // dedup on the per-BBS player record. Off by default.
    public Game.GreetManager Greet { get; private set; } = null!;

    // Owns PlayerState.InCombat and
    // the Game.Map.MovementCoordinator.CombatGate hold
    // state. Cleared automatically when the room is free of
    // engageable monsters.
    public Game.Combat.CombatStateTracker CombatTracker { get; private set; } = null!;

    // Aggregates combat lines into per-round
    // Game.Combat.RoundSummary records, keeping the
    // last 50 in a ring buffer. CastingDirector and
    // CombatSessionTracker consume the RoundComplete event.
    public Game.Combat.RoundDamageTracker RoundDamage { get; private set; } = null!;

    // Aggregates combat lines + RoundDamage rounds
    // into the session combat figures (hit / miss / crit / dodge rates,
    // physical & backstab damage extents, per-round damage) the Session
    // Stats panel displays. Pure downstream subscriber; reset on the session
    // boundary alongside RoundDamage.
    public Game.Combat.CombatSessionTracker CombatSession { get; private set; } = null!;

    // Divides the session's wall-clock time across the player's
    // activities (waiting / moving / attacking / resting HP / resting MA) plus
    // the blinded / poisoned overlays, for the Time Analysis panel. Fed by
    // PlayerState, Conditions, and
    // RoomTracker; reset on the session boundary.
    public Game.Combat.TimeAnalysisTracker TimeAnalysis { get; private set; } = null!;

    // Counts the session's monster kills and experience earned and
    // keeps a rolling kill-timestamp history for the Session Stats panel's
    // kills/hour sparkline. Fed by MonsterDeath and the
    // experience-gain line; reset on the session boundary.
    public Game.Combat.SessionActivityTracker SessionActivity { get; private set; } = null!;

    // Per-session ledger of cash/item offloads (bank deposits +
    // stash-room hides) behind the Session Stats → Transaction history window.
    // Fed by AutoDeposit and Stash; reset on the
    // same session boundary as the other session-stats trackers.
    public Game.Cash.TransactionHistoryTracker TransactionHistory { get; private set; } = null!;

    // Observes the "You have been slain by..."
    // line and emits Game.Combat.DeathLineWatcher.PlayerDied.
    // DeathRecoveryManager is the primary consumer; other
    // engines subscribe for their own death-clean-up paths.
    public Game.Combat.DeathLineWatcher DeathWatcher { get; private set; } = null!;

    // Refines the active BBS's negative-HP death floor
    // (Models.Settings.BbsProfile.PlayerDiesAtHp) from observed slow deaths by
    // watching the local HP trajectory into each death.
    public Game.Health.DeathFloorTracer DeathFloorTracer { get; private set; } = null!;

    // Auto-attack engine. Picks a target from
    // RoomClassifier's last observation and sends the
    // configured attack command when
    // Models.Profile.CombatSettings.MasterAutoAttackEnabled
    // is on. Wire sender is bound by MainWindowViewModel
    // alongside the other engines once the telnet client is up.
    public Game.Combat.CombatManager Combat { get; private set; } = null!;

    // Lookup of monster Numbers carrying the SeeHidden ability (code
    // 57) in the active game-data set. Drives CombatManager's
    // backstab-skip — a seehidden room occupant ruins the opening BS.
    public Game.Combat.SeeHiddenIndex SeeHidden { get; private set; } = null!;

    // Lookup of each monster's Magical / SpellImmu
    // levels (codes 28 / 139) in the active game-data set. Drives
    // CombatManager's deterministic weapon-vs-monster hit eligibility and
    // spell-immunity gating.
    public Game.Combat.MonsterMagicIndex MonsterMagic { get; private set; } = null!;

    // Number → max-HP lookup in the active game-data set. Feeds the look-target
    // HP-range readout (MonsterLookParser turns a wound descriptor into an
    // absolute HP window).
    public Game.Combat.MonsterHpIndex MonsterHp { get; private set; } = null!;

    // Lookup of each weapon's HitMagic level (code 142) in
    // the active game-data set. Paired with MonsterMagic for
    // the HitMagic ≥ Magical hit check.
    public Game.Combat.ItemMagicIndex ItemMagic { get; private set; } = null!;

    // Lookup of each spell's ReqLevel by cast-code in the
    // active game-data set. Paired with MonsterMagic for the
    // ReqLevel ≥ SpellImmu eligibility check.
    public Game.Combat.SpellReqLevelIndex SpellReqLevel { get; private set; } = null!;

    // Lookup of each monster's elemental damage-type resistance (codes
    // 3/5/65/66/147) in the active game-data set. Paired with SpellAttackType for
    // the pre-emptive resist guard — skip an attack spell whose element the target
    // resists ≥ 100%.
    public Game.Combat.MonsterResistIndex MonsterResist { get; private set; } = null!;

    // Lookup of each spell's AttType (damage element) by cast-code in the active
    // game-data set. Paired with MonsterResist for the resist guard.
    public Game.Combat.SpellAttackTypeIndex SpellAttackType { get; private set; } = null!;

    // Catalogue of every light-source item (ItemType 6) in the
    // active set — projected illumination (IlluTarget) + burn budget —
    // for computing carried illumination and provisioning a dark route.
    public Game.Light.LightItemIndex Lights { get; private set; } = null!;

    // The highest Strength any race + class + gear build can reach on the
    // active set — the door FSM's per-set bash ceiling, replacing the old hardcoded
    // 200. Feeds Game.Map.DoorOpenManager via a provider so a
    // strength-gated door is only ruled unbashable when no build could open it.
    public Game.Map.MaxStrengthIndex MaxStrength { get; private set; } = null!;

    // The player's live carried illumination (worn +illu gear +
    // the readied light's strength) — the charIllu input to the
    // Game.Light.LightModel visibility bands.
    public Game.Light.PlayerIllumination PlayerIllumination { get; private set; } = null!;

    // Observes mid-room arrival lines
    // ("<name> <verb> into the room from <dir>.")
    // and appends the new entity to
    // RoomClassifier's observation so CombatStateTracker
    // re-evaluates the Combat gate immediately on spawn.
    public Game.Combat.RoomEntryWatcher RoomEntry { get; private set; } = null!;

    // Recognises monster deaths via the per-monster
    // Models.GameData.MonsterMessageRecord.DeathLine
    // patterns + the "experience + Combat Off" fallback. On a match,
    // the dead monster is removed from RoomClassifier's
    // observation so CombatManager re-picks correctly instead of
    // sitting on a stale entry.
    public Game.Combat.MonsterDeathWatcher MonsterDeath { get; private set; } = null!;

    // Engages a monster hidden by darkness. A dark room prints no "Also here:"
    // line, so the only tell a hostile shares it is the mob's dark-cyan attack
    // line; this watcher reads the name off that line (gated on
    // RoomTracker.IsInDarkRoom) and injects it into RoomClassifier so
    // CombatManager engages it as if it had been listed.
    public Game.Combat.DarkRoomCombatWatcher DarkRoomCombat { get; private set; } = null!;

    // Passive HP/MA threshold engine. Asserts /
    // clears HealthRecovery + ManaRecovery gates and drives the
    // rest / stand cycle with pre- and post-rest command sequencing.
    // Does NOT cast spells — those route through CastingDirector.
    public Game.Health.HealthManager Health { get; private set; } = null!;

    // Low-level c <spell> [target]
    // emitter. Gates on combat-round cooldown + a cast-blocked latch
    // driven by server failure messages (fizzle / no-mana / already-
    // cast / interrupted). Consumed by CastingDirector and
    // any other engine that issues spell commands.
    public Game.Spells.CastCoordinator Cast { get; private set; } = null!;

    // Unified self+party heal / cure / buff
    // decision engine. Sits on top of Cast and decides
    // which spell (if any) to issue based on HP / MA / ailment state
    // + the user's Spells + Health tab thresholds.
    public Game.Spells.CastingDirector CastDirector { get; private set; } = null!;

    // Parser for abil <code> breakdown output. Attached to the
    // live line stream in the main VM; feeds ManaRegen the
    // rolled spells: slice of an abil 145 mana-regen read.
    public Game.AbilBreakdownParser AbilBreakdown { get; private set; } = null!;

    // Paradigm-only mana-regen roll-spell reroll engine (nature tap / mana
    // flux, ability 145). Driven by CastDirector's self-buff
    // landing sink + AbilBreakdown; recasts a below-threshold
    // roll up to the configured cap.
    public Game.Spells.ManaRegenReroller ManaRegen { get; private set; } = null!;

    // Runs the equip → use → re-equip wire sequence for an
    // item-cast Bless slot (a Game.Spells.ItemCastToken). Driven
    // by CastDirector; wire-sender bound in the main VM.
    public Game.Spells.ItemCastSequencer ItemCast { get; private set; } = null!;

    // Condition tracker driven by the game-data
    // Messages tab. Subscribes to inbound lines, matches against
    // every Models.GameData.MessageRecord.AppliedMessage
    // / Models.GameData.MessageRecord.AppliedEndsWith
    // pair, surfaces the aggregated
    // Models.GameData.MessageFlags bitfield. Consumed
    // by CastingDirector's Tier-2 cure path.
    public Game.Conditions.ConditionTracker Conditions { get; private set; } = null!;

    // Outbound ailment-sync engine — on a local curable ailment it
    // announces on say (.@poisoned etc.) so other FujinTerm
    // clients mirror our state, and @waits the leader; on clear it @oks.
    public Game.Conditions.AilmentSyncEngine AilmentSync { get; private set; } = null!;

    // Inbound ailment-sync engine — mirrors a party member's
    // .@poisoned / .@blind / .@diseased / .@confused
    // say announce onto their party chip, and clears the chip when OUR cure
    // spell is observed landing on them. Counterpart to
    // AilmentSync.
    public Game.Conditions.PartyAilmentTracker PartyAilment { get; private set; } = null!;

    // Stealth state tracker. Owns
    // PlayerState.IsSneaking /
    // PlayerState.IsHidden and emits FSM-state
    // transitions + silent-loss detection on room change. Auto-
    // sneak / auto-hide engines (which actually issue commands)
    // layer on top in a follow-up.
    public Game.Stealth.StealthManager Stealth { get; private set; } = null!;

    // Auto-light need poster. On a "can't see"
    // room-light line it posts a NeedKind.LightSource
    // need to Needs; auto-get fulfils it.
    // Gated by the AutoLight master toggle.
    public Game.Light.AutoLightManager AutoLight { get; private set; } = null!;

    // Active auto-light engine. Bound to the walker's route announcer: on each
    // planned route it scans for the darkest room and readies a covering carried
    // light (use <light>), or hands off to
    // AutoLightShopRouter to provision one it lacks. Every action
    // is gated by the AutoLight master toggle.
    public Game.Light.AutoLightProvisioner AutoLightProvisioner { get; private set; } = null!;

    // Auto-light provisioning detour. On the provisioner's Buy verdict (route
    // dark, nothing carried covers) it walks to the fewest-added-steps shop that
    // stocks the light, buys the carry batch, and resumes — the provisioner
    // lights it on the resumed route. Gated entirely by the AutoLight master
    // toggle; wire-sender bound by MainWindowViewModel after connect.
    public Game.Light.AutoLightShopRouter AutoLightShopRouter { get; private set; } = null!;

    // Death observation aggregator. Surfaces the loaded
    // profile's Models.Profile.CharacterProfile.DeathHistory
    // as the Workshop DEATH section's deathpile grid, owns the per-character
    // Auto-Recover / Auto-Equip toggles, and drives the corpse-recovery
    // state machine off room re-entry and pickup confirmations.
    public Game.Recovery.DeathRecoveryManager DeathRecovery { get; private set; } = null!;

    // Runtime inventory parser. Folds the full i
    // dump into a currency + numeric-encumbrance
    // Game.Inventory.InventorySnapshot and patches it
    // incrementally on coin pickups / drops / bank moves. Feeds
    // Cash's encumbrance gate the live carry weight.
    public Game.Inventory.InventoryManager Inventory { get; private set; } = null!;

    // Gear-set apply engine (Workshop Equipment tab). Diffs a saved
    // Models.Profile.EquipmentSet against the live worn loadout
    // (Inventory's snapshot) and paces wear commands;
    // virtual slots write Models.Profile.CombatSettings instead.
    // Driven by the @equip-<set> remote command
    // (EquipRemote) and the auto-equip triggers
    // (AutoEquip).
    public Game.Inventory.EquipmentManager Equipment { get; private set; } = null!;

    // Auto-equip trigger coordinator. Subscribes to
    // Game.PlayerState's position / combat signals and, when the
    // matching trigger-purposed Models.Profile.EquipmentSet is
    // enabled, hands its id to Equipment for the moment.
    public Game.Inventory.AutoEquipCoordinator AutoEquip { get; private set; } = null!;

    // Per-currency cash pickup engine. Dispatches
    // get <count> <coin> commands per
    // Models.Profile.CashSettings policy when the
    // room-cash line lands; tracks held tallies for the auto-
    // deposit trigger. Encumbrance gates + drop-smaller-for-larger
    // cascade run off Inventory's snapshot; walker-
    // driven reroute is follow-up work.
    public Game.Cash.CashManager Cash { get; private set; } = null!;

    // Auto-get items engine. Parses the room
    // "You notice ... here." survey, resolves each entry against the
    // active set's items + the per-character
    // Models.GameData.ItemOverlay.AutoCollect flag, and
    // sends get <name> per flagged item. Gated by the
    // AutoGetItems master toggle; defer-until-combat-finished honours
    // the Settings → Items tab.
    public Game.Inventory.AutoGetItemsManager AutoGetItems { get; private set; } = null!;

    // Base auto-search engine — sends a bare sea on each room
    // entry while the AutoSearch master toggle is on, revealing hidden
    // items so AutoGetItems / Cash can
    // collect them. Fired from the RoomTracker.StateChanged
    // seam; off by default and armed manually.
    public Game.Map.AutoSearchManager AutoSearch { get; private set; } = null!;

    // Demand-driven auto-search coordinator — posts a
    // NeedKind.PathItem need when the walker plans a route
    // through an Item/Ticket exit whose item we don't carry, and resolves it
    // when the item enters inventory. While such a need is outstanding (and
    // Settings → Other "search rooms if item needed" is on),
    // AutoSearch arms itself via
    // Game.Map.PathItemDemandTracker.SearchDemandActive.
    public Game.Map.PathItemDemandTracker PathItemDemand { get; private set; } = null!;

    // Reverse index of the active set's Shops.json — item id → the
    // shops that stock it. Feeds PathItemShopRouter's shop
    // lookup; rebuilt on GameDataCache.ActiveSetChanged.
    public ShopStockIndex ShopStock { get; private set; } = null!;

    // Active fulfiller for NeedKind.PathItem needs backed by a
    // shop: on a one-shot walk-to that needs an uncarried item a shop sells,
    // detours to the fewest-added-steps shop, buys it, and resumes. Gated by
    // Settings → Other "buy item if needed".
    public Game.Map.PathItemShopRouter PathItemShopRouter { get; private set; } = null!;

    // Index of the active set's Monsters.json — which monsters drop
    // an item and where each spawns. Feeds
    // MonsterDropRouter's hunt lookup; rebuilt on
    // GameDataCache.ActiveSetChanged.
    public MonsterDropIndex MonsterDrops { get; private set; } = null!;

    // Active fulfiller for NeedKind.PathItem needs no shop can
    // satisfy: on a one-shot walk-to that needs an uncarried item no shop
    // sells, prompts to reroute to the nearest room a monster that drops it
    // spawns in, then resumes once it lands. Gated by Settings → Other
    // "hunt item if needed".
    public Game.Map.MonsterDropRouter MonsterDropRouter { get; private set; } = null!;

    // On-demand party-inventory probe — broadcasts @have and aggregates
    // the party's replies into per-member counts. Feeds
    // PartyPathItemGate's give-from-surplus decision.
    public Game.Remote.PartyInventoryProbe PartyInventory { get; private set; } = null!;

    // Party-first stage of the path-item pipeline: on a walk-to that needs an
    // uncarried per-member Item/Ticket item, probes the party
    // (PartyInventory) and, if a member has a spare, arranges a
    // give instead of posting a need. Only a genuine shortfall falls
    // through to PathItemDemand. Gated by Settings → Other
    // "defer to party inventory".
    public Game.Map.PartyPathItemGate PartyPathItemGate { get; private set; } = null!;

    // On-demand party-level probe — broadcasts @level and records
    // each member's exact level into Players. Fired by
    // PartyLevel on roster change so the players table stays
    // the authoritative level source (superseding the title-derived band).
    public Game.Remote.PartyLevelProbe PartyLevelProbe { get; private set; } = null!;

    // Keeps the party's level bounds warm for path planning and feeds
    // MovementFilter.PartyLevelBoundsProvider so BFS routes a
    // following party around (Level: MIN to MAX) gates a member
    // can't clear. Gated by Settings → Other "avoid party-impassable level
    // gates".
    public Game.Remote.PartyLevelTracker PartyLevel { get; private set; } = null!;

    // On-demand party-wealth probe — broadcasts @wealth and forwards each
    // reply to PartyWealth. Unlike the level probe it doesn't persist to
    // the players table (wealth drifts); it's fired only when a route
    // crosses a toll.
    public Game.Remote.PartyWealthProbe PartyWealthProbe { get; private set; } = null!;

    // Demand-driven party-wealth gate — feeds
    // MovementFilter.PartyWealthProvider so BFS routes a following party
    // around (Toll: N) exits a member can't afford. Polls @wealth only when
    // a toll is on a candidate path. Always on: a toll is per-crosser, so
    // stranding a member at a gate is never the wanted behaviour.
    public Game.Remote.PartyWealthTracker PartyWealth { get; private set; } = null!;

    // Shared Acquisition movement-gate driver. Both
    // Cash and AutoGetItems feed it; it owns
    // the single assert/clear of
    // Game.Map.MovementCoordinator.AcquisitionGate so the
    // walker resumes only once both engines finish looting.
    public Game.Inventory.AcquisitionGate Acquisition { get; private set; } = null!;

    // On-entry stash plan for user-
    // marked stash rooms. Dispatches hide N <coin>
    // commands per Models.Profile.StashCurrencyRule
    // when RoomTracker reports we've arrived in a
    // configured Models.Profile.StashRoom. Item-side
    // stash rules land when the inventory subsystem ships.
    public Game.Cash.StashRoomManager Stash { get; private set; } = null!;

    // Auto-deposit reroute. Subscribes to
    // Game.Cash.CashManager.AutoDepositRequested; when a
    // wealth / coin gate crosses while a loop or auto-lair is running,
    // detours to the configured bank / stash room, offloads the excess
    // coin (dep for a bank, Stash's hide for
    // a stash room), walks back, and restarts the captured engine.
    public Game.Cash.AutoDepositManager AutoDeposit { get; private set; } = null!;

    // Active set's MonsterOverlay seed — Defaults-tier baseline for
    // per-monster automation behavior (relationship / priority /
    // NotHostile / DontBackstab). Realm flavor is auto-picked from
    // the active set's Info.json[0].Legit; bundled seeds for
    // each realm ship at Defaults/MonsterOverlay.{realm}.seed.json
    // and bootstrap to the per-install Data/Global/ copy at
    // startup. Consulted by Monsters-tab editing + (future) combat
    // engines via MonsterOverlaySeedStore.GetOverlay(int).
    public MonsterOverlaySeedStore MonsterOverlaySeed { get; private set; } = null!;

    // Active set's ItemOverlay seed — Defaults-tier baseline for
    // per-item automation behavior (9 Options flags + MinToKeep /
    // MaxToGet). Realm flavor is auto-picked from the active set's
    // Info.json[0].Legit; bundled seeds for each realm ship at
    // Defaults/ItemOverlay.{realm}.seed.json and bootstrap to
    // the per-install Data/Global/ copy at startup. Consulted
    // by the Items tab editing + (future) loot / equipment engines
    // via ItemOverlaySeedStore.GetOverlay(int).
    public ItemOverlaySeedStore ItemOverlaySeed { get; private set; } = null!;

    // Background audit comparing player-facing spells in the active
    // set against the Messages catalogue's Links field — surfaces a
    // summary LogEntry per audit run so users know which spells
    // don't have a parser entry. Bound in Initialize
    // once GameData + Messages + the
    // Log sink are all live.
    public SpellCoverageAuditor SpellCoverage { get; private set; } = null!;

    // In-memory graph of every room in the active game-data set, built
    // once at set-switch time from Rooms.json. The navigation stack
    // (room tracker, BFS mapper, walker, loop manager, auto-lair
    // scheduler) all read from this. Subscribes to
    // GameDataCache.ActiveSetChanged in
    // Initialize; consumers subscribe to
    // Game.Map.RoomGraphManager.GraphReloaded to drop
    // any cached room references.
    public Game.Map.RoomGraphManager RoomGraph { get; private set; } = null!;

    // TextBlock Info index for the active game-data set. Loaded from
    // TBInfo.json; consumed by the teleport handler (room
    // CMD > 0 + (Item: N) exit promotes to
    // Game.Map.RoomExitHint.Teleport, then the walker
    // follows the chain to extract keyword + destination).
    public TBInfoStore TBInfo { get; private set; } = null!;

    // Reverse index of RoomKey → monster ids whose Monsters.json
    // "Summoned By" field references that room. Lets the tooltip's
    // Also Here line surface boss / script-spawn monsters whose
    // presence lives only on the monster record (no room-side lair
    // tag entry). Lazily built on first lookup per active set.
    public MonsterSpawnIndex MonsterSpawns { get; private set; } = null!;

    // Item-id → name lookup for the active set. Consumed by the
    // keyed-door FSM (Game.Map.DoorOpenManager) to
    // translate an exit's Game.Map.RoomExit.KeyItemId
    // into the verbatim name fed to use <name> <dir>.
    public ItemNameStore ItemNames { get; private set; } = null!;

    // Trust-by-default room tracker. Owns
    // Game.Map.RoomState; the Navigation status strip
    // and any source-room-required engine (walker, loop runner,
    // auto-lair scheduler) bind here. The wire-side parser feeds it
    // NoteRoomObserved / NoteMoveBlocked.
    public Game.Map.RoomTracker RoomTracker { get; private set; } = null!;

    // Shared tier-1/2/3 recovery gate for the walker / loop runner /
    // auto-lair scheduler. Engines attach themselves on Start and
    // detach on Stop; the gate owns the strict-1-of-1 anchor + the
    // executed-step history + tier-3 backtrack logic.
    public Game.Map.EngineRecoveryGate Recovery { get; private set; } = null!;

    // Writer that persists tracker-learned room names back into the
    // active set's Rooms.json. Consumed by the
    // MainWindowViewModel name-learned prompt handler after the user
    // confirms the rename.
    public RoomNamePersistence RoomNamePersist { get; private set; } = null!;

    // Sniffs outbound user-typed commands and tells
    // RoomTracker about look <dir> peeks
    // (so the next room display is dropped instead of mistaken for a
    // move) and text-exit movement verbs (go path,
    // enter portal, etc., so the step is captured in
    // Models.Profile.CharacterProfile.RecentSteps).
    // Hooked from MainWindowViewModel.SendUserInput.
    public Game.Map.OutboundMovementObserver OutboundMovement { get; private set; } = null!;

    // Feeds leader-driven follower drags into RoomTracker. A dragged follower
    // sends no movement bytes of its own, so the " -- Following your Party leader
    // <dir> --" line is the only move signal that keeps the map located instead
    // of drifting to Lost. Subscribes to the router for app lifetime.
    public Game.Map.FollowMoveObserver FollowMove { get; private set; } = null!;

    // Recognises a manually-typed spell cast-code on the wire and arms the
    // combat engine's between-round-cast resume, so a hand-cast that breaks
    // combat mid-fight re-attacks a still-alive target at once instead of
    // idling until the next round. Hooked from MainWindowViewModel.SendUserInput.
    public Game.Combat.OutboundCastObserver OutboundCast { get; private set; } = null!;

    // Death-message detector — watches lines for either post-death lives
    // readout (You now have N lives remaining. / You have N lives left.,
    // the latter the miracle-save death) and fires
    // Game.Map.RoomTracker.NoteDeath. Captures
    // a Models.Profile.DeathRecord on the loaded profile
    // for the Workshop DEATH section and pivots the tracker
    // into Game.Map.RoomConfidence.PendingRespawn.
    // Bound to the per-session LineExtractor by
    // MainWindowViewModel.
    public Game.DeathDetector Death { get; private set; } = null!;

    // BFS pathfinding + planar layout over the active
    // RoomGraph. Consumed by the walker, loop runner,
    // auto-lair scheduler (pathfinding), and the Navigation
    // MapControl (layout).
    public Game.Map.BfsMapper Bfs { get; private set; } = null!;

    // Per-character avoided + stash room set. Implements
    // Game.Map.IRoomFilter so pathing layers can plug
    // it into Bfs without further wiring.
    public MovementFilter Movement { get; private set; } = null!;

    // Per-character favourite-room bookmarks. Wires Navigation's
    // GOTO pane + the map's "Add to favorites" context menu;
    // persisted via ProfileService.
    public FavoritesStore Favorites { get; private set; } = null!;

    // Shared pause-gate aggregator for every movement engine
    // (walker, loop runner, auto-lair scheduler). A pause from any
    // source halts whichever engine is active.
    public Game.Map.MovementCoordinator MovementCoordinator { get; private set; } = null!;

    // Party-vitals pause bridge — holds the active movement engine while
    // a party member is below the Party-tab HP% threshold.
    public Game.PartyVitalsWatcher PartyVitals { get; private set; } = null!;

    // Follower-movement pause bridge — holds every movement engine while
    // we're a party follower, so the leader's drag isn't fought by our own
    // walk / loop / auto-lair.
    public Game.PartyFollowerMovementGate PartyFollowerMovement { get; private set; } = null!;

    // Inbound-@wait pause bridge — holds the active movement engine while a
    // party member has asked us to @wait (or announced .@held) and hasn't yet
    // sent @ok, so a loop doesn't walk away from a resting member.
    public Game.PartyWaitMovementGate PartyWaitMovement { get; private set; } = null!;

    // Follower-disconnect pause bridge (leader side) — holds movement while a
    // dropped party follower is inside the reconnect grace window, so we don't
    // sprint off without a member who's trying to reconnect and re-party.
    public Game.PartyDisconnectMovementGate PartyDisconnectMovement { get; private set; } = null!;

    // Death-halt bridge — when the local player dies, asserts UserGate so every
    // movement engine stops and we sit in the graveyard until the player
    // manually resumes. Exposes HaltedForDeath so the Navigation chip can read
    // "Paused — recovering" while the death pause holds.
    public Game.PlayerDeathMovementHalt PlayerDeathHalt { get; private set; } = null!;

    // Dropped / mortally-wounded bridge — while the local character is at or
    // below 0 HP, holds the EngineSendGate (a dropped character can't act, so
    // every engine send is rejected), asserts MovementCoordinator's
    // MortallyWoundedGate, and clears the stale party roster (a drop removes us
    // from the party game-side). Auto-clears on recovery.
    public Game.PlayerDroppedGate PlayerDropped { get; private set; } = null!;

    // Ally-drop rescue bridge — reacts to another party / recently-partied member
    // dropping to the ground (0 HP): holds movement (AllyDownGate) to stay with
    // them, sends `aid <name>`, feeds the aided ally into CastDirector for a
    // heal-by-name until they recover, polls their off-roster vitals via `@health`,
    // and (if leading) re-invites them once aided. Auto-releases on recovery,
    // rejoin, death, logoff, or timeout.
    public Game.AllyDroppedHandler AllyDropped { get; private set; } = null!;

    // Party-death roster-cleanup bridge — when we're leading an automated route
    // and an active member dies (turning into a phantom [Invited] par slot),
    // uninvites that slot once the room clears so the loop doesn't stall on the
    // PartyInviteGate waiting for a corpse to "join". Needs MovementControl for
    // the movement-active gate, so it's constructed later than the other party
    // bridges.
    public Game.PartyDeathRosterCleanup PartyDeathCleanup { get; private set; } = null!;

    // Leader-rest bridge — nudges Health to re-evaluate when
    // the party leader's rest / meditate posture flips, so a standing-idle
    // follower opportunistically tops off during the leader's downtime
    // without waiting on its own next prompt tick.
    public Game.PartyLeaderRestWatcher PartyLeaderRest { get; private set; } = null!;

    // Fulfillment half of the auto-engine coordination model —
    // requesters post acquisition needs (light source, etc.), fulfilling
    // engines claim + resolve them. No engine references another by
    // type.
    public NeedsRegistry Needs { get; private set; } = null!;

    // Walk-to engine — sends one move at a time, waits for the room
    // tracker to confirm before advancing, and honours
    // MovementCoordinator pause gates.
    public Game.Map.AutoWalkManager Walker { get; private set; } = null!;

    // Per-BBS saved-loop catalogue. CRUD over
    // Data/BBS/{bbs}/Loops/; consumers re-bind when the active
    // BBS changes.
    public Game.Map.LoopManager Loops { get; private set; } = null!;

    // MegaMUD .mp loop-file importer. Stateless w.r.t. the
    // profile; takes the active RoomGraph at construct
    // time and resolves anchors against whatever it currently
    // contains.
    public Game.Map.MpFile.MpFileImporter MpImporter { get; private set; } = null!;

    // Per-BBS Auto-Lair setup catalogue. Loads on profile load + BBS
    // pin via the same ResolveActiveBbs path Loops uses. The Manage
    // dialog reads / writes through this surface; the
    // LairTimers store derives default respawn timers
    // from game data and tracks in-session arrivals.
    public Game.Map.LairManager Lairs { get; private set; } = null!;

    // Game-data-derived respawn timer resolver + in-session arrival
    // tracker for marked lair rooms. The Auto-Lair
    // scheduler reads NextReadyAt to choose the next leg.
    public Game.Map.LairTimerStore LairTimers { get; private set; } = null!;

    // Folder CRUD over the shared per-BBS Loops directory that holds
    // both Loops and Lairs. Create / rename
    // / delete folders; reloads both catalogues after a filesystem
    // move so their in-memory Folder values stay in sync.
    public Game.Map.NavFolderManager NavFolders { get; private set; } = null!;

    // Game Data → "Manage Sets…" backend: copy / move a set's loop
    // library to another set, delete a set (tables + loops).
    public GameDataSetManager GameDataSetManager { get; private set; } = null!;

    // Sole writer of Game.PlayerState.Encumbrance.
    // Subscribes the enc line via MessageRouter.
    public Game.EncumbranceParser Encumbrance { get; private set; } = null!;

    // Debug instrumentation logging measured per-hop times tagged
    // with the current Game.EncumbranceLevel. Off by
    // default; flipped on via Settings → Other.
    public Game.HopTimingCalibrator HopCalibrator { get; private set; } = null!;

    // Per-BBS room blacklist — hides target rooms from the
    // Navigation map render and the search box. Consumed by
    // Game.Map.BfsMapper (skip placement, keep edge
    // for dangling stub) and the right-click "Add to blacklist"
    // + "Modify Blacklist…" flows.
    public RoomBlacklistStore RoomBlacklist { get; private set; } = null!;

    // Loop execution engine. Shares
    // MovementCoordinator + RoomTracker
    // with the walker, plus WirePromptScanner for
    // command-step confirmation.
    public Game.Map.LoopRunner LoopRunner { get; private set; } = null!;

    // Random-walk roam scheduler. Foundation for the deterministic
    // Auto-Lair scheduler. Session-only state.
    public Game.Map.AutoLairManager AutoLair { get; private set; } = null!;

    // Always-alive control surface over the three movement engines —
    // coalesces their run-state and routes Pause / Resume / Stop to the
    // right engine. Backs the toolbar movement-flow buttons.
    public Game.Map.MovementController MovementControl { get; private set; } = null!;


    // Construct and register the singleton. Idempotent — repeated calls return
    // the existing instance. Touches AppPaths to force
    // directory creation before any service tries to read or write a file.
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
        // exposes the knob.
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
        // Gate the generation-gated Debug / Combat channels on the live
        // per-character diagnostic toggles (applied from the profile below,
        // flipped from the Log pane).
        Log.Diagnostics = LogDiagnostics;
        // Late-bind the cache's log sink so SwitchSet emits the swap
        // audit entries (load / unload / swap) without coupling the
        // cache to AppServices construction order.
        GameData.Log = bootstrapLog;
        Settings = new SettingsService();
        Profile = new ProfileService();
        // Same late-bind pattern as GameData.Log above: the profile-lifecycle
        // audit (load / swap / close / re-home) rides the always-on Info stream.
        Profile.Log = bootstrapLog;
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
        SessionStatsLayout = new SessionStatsLayoutStore(Profile);
        Wire = new WireBuffer();
        Router = new MessageRouter();

        // Populate the default pattern registry now so later subsystems
        // (ChatRouter, automation engines, the Trigger
        // UI's "pick a built-in pattern" picker) can subscribe by
        // KnownPatterns.Whatever id.
        Patterns.DefaultPatterns.Seed(Router);

        // First MessageRouter consumer — subscribes to the conversation +
        // realm-event patterns. ChatHistoryStore + ConversationWindow
        // subscribe to its EntryClassified event.
        Chat = new Game.ChatRouter(Router);
        ChatHistory = new Game.ChatHistoryStore(Chat);
        PlayerState = new Game.PlayerState();
        PromptScanner = new WirePromptScanner();
        Player = new Game.PromptParser(PromptScanner, PlayerState);
        // Reconcile the live statline to the editor on every connect. Reads the
        // desired command from the active profile at send time so the latest
        // saved value is what gets reasserted. Armed / disarmed by the connect
        // lifecycle in MainWindowViewModel.
        StatlineReconcile = new Game.StatlineReconciler(PromptScanner, Log);
        StatlineReconcile.SetDesiredCommandProvider(
            () => ReadSection<Models.Profile.StatlineSettings>(Profile.Current, "Statline").Command);
        PartyState = new Game.PartyState();
        Party = new Game.PartyManager(Router, PartyState);
        // Mirror the local character's live HP/MA into the self party
        // row on every prompt — without this the self row only updates
        // on a par poll, so per-prompt damage between polls doesn't
        // surface in the PartyWindow.
        Party.AttachPlayerState(PlayerState);
        Tick = new Game.TickEngine(Router);
        Regen = new Game.RegenTracker(PlayerState);
        // Seed the regen cadence from the active realm (Stock 30/20/10 vs
        // ParaMud's thirds-on-a-10s-grid) and re-seed on every set switch.
        // ActiveRealm reads Stock until a set with an Info table loads; the
        // subscription corrects it when SwitchSet first fires.
        Regen.SetRealm(GameData.ActiveRealm);
        GameData.ActiveSetChanged += _ => Regen.SetRealm(GameData.ActiveRealm);
        RegenDiagnostics = new Game.RegenDiagnosticsRecorder(Regen, PlayerState, Log);
        // RemoteCommands is constructed AFTER Chat / Party / Players are
        // ready (they're all dependencies). Handlers register later — the
        // engine is empty here; we just wire the plumbing.
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
        // Board-specific disconnect line: PartyManager reads the active BBS's
        // custom DisconnectPattern live (empty on boards that use the standard
        // lines) and resolves a captured presence name — which on some boards is
        // the account name, not the character name — back to a given name via the
        // player account-name overrides.
        Party.DisconnectPatternProvider = () => ResolveActiveBbs()?.DisconnectPattern;
        Party.PresenceNameResolver = Players.ResolveGivenNameFromPresenceName;
        // Engine only — other subsystems register additional
        // handlers without touching the engine.
        RemoteCommands = new Game.Remote.RemoteCommandManager(Chat, PartyState, Players, Log);
        // Reserve the party ailment-sync announces (@poisoned / @blind / @held …)
        // so the engine swallows them instead of bouncing a "{command invalid}"
        // reply at the member who announced — PartyAilmentTracker consumes them on
        // its own ChatRouter subscription.
        foreach (string token in Game.Conditions.PartyAilmentTracker.AnnounceTokens)
            RemoteCommands.RegisterIgnored(token);
        // Stat-screen parser ahead of LivesProvider hookup below so
        // both the engine's @suicide hard-block and the @lives reply
        // path share the same "unknown until first stat poll" source.
        Stats = new Game.StatParser(PlayerStats, Log);
        // Spell Book — the class's full learnable list (SpellCatalog) paired
        // with the obtained set (Spellbook), fed by the spells/pow parser
        // (SpellList). SpellList binds to the per-session LineExtractor in
        // MainWindowViewModel; the Refresh coordinator lives in the
        // Stats.ScreenParsed handler below.
        SpellCatalog = new Game.Spells.KnownSpellCatalog(GameData);
        Spellbook = new Game.Spells.SpellbookState(SpellCatalog);
        SpellList = new Game.Spells.SpellListParser(Spellbook, Log);
        // Train-time learning — mark a power obtained the moment the
        // "You learn the following Kai abilities:" block lists it, without
        // waiting for the next `pow` poll. Incremental, like the learn-scroll
        // line. Also binds to the per-session LineExtractor in MainWindowVM.
        TrainLearn = new Game.Spells.TrainLearnParser(Spellbook, Log);
        // Reroll → drop the obtained set. The fresh character has learned
        // nothing; the next `stat` rebuilds the available list. Done here
        // rather than waiting for the stat poll so a same-class reroll
        // doesn't keep spells the new character can't have yet.
        Router.Subscribe(Services.Patterns.KnownPatterns.Reroll, _ => Spellbook.ClearObtained());
        // Learn-scroll signal — mark the spell obtained the moment the
        // "…and learn the spell <name>." line fires, without waiting for
        // the next `spells` poll. Group 1 carries the full spell Name.
        Router.Subscribe(Services.Patterns.KnownPatterns.LearnSpell, m =>
        {
            if (m.Groups.Count > 0) Spellbook.MarkObtainedByName(m.Groups[0]);
        });
        // Alignment staleness — "A dark cloud passes over you" flags the
        // Character Workshop's displayed alignment stale until the next `who`
        // re-observes our own row. Long-lived so the line is caught even when
        // the Workshop is closed.
        Alignment = new Game.AlignmentTracker(Router, PlayerStats, Players);
        // First consumer; registers the party-essential
        // handler set against the engine.
        // readCurrentRoom / readRoomEntities defer to the live RoomTracker
        // and RoomEntityClassifier (both constructed later in
        // OnGameDataLoaded) via the property on each call, so they always
        // read the current snapshot even across set-switch rebuilds.
        // Watches "<leader> is dragging you around." so a downed member's @join /
        // @invite reply can name who's already hauling it out.
        Dragged = new Game.DraggedTracker(Router, PlayerState);
        PartyEssentials = new Game.Remote.PartyEssentialHandlers(
            RemoteCommands, PlayerState, PartyState,
            readPartySettings: () => ReadSection<Models.Profile.PartySettings>(Profile.Current, "Party"),
            readCurrentRoom: () => RoomTracker?.State.CurrentRoom,
            readRoomEntities: () => RoomClassifier?.Current?.Entities,
            readMovement: () => Game.Remote.MovementStatus.Capture(Walker, LoopRunner, AutoLair),
            readDraggedBy: () => Dragged.DraggedBy);
        // Drives the on-join @health exchange + the
        // periodic par poll. Wire-sender + cadence-from-settings hookup
        // happens in MainWindowViewModel.
        PartyPoller = new Game.PartyPoller(Chat, PartyState, Party)
        {
            // par reads party health, so it lives under the auto-heal/rest
            // toggle like every other automatic action. AutoModeController's
            // kill-all zeroes that flag, so auto-all off silences par too.
            IsParPollEnabled = () => ReadAutoModeFlag(d => d.AutoHealRest),
        };
        // Emit side of @wait/@ok. Observes our own
        // position transitions and telepaths the leader when we enter
        // / leave a rest state. Wire-sender hookup in MainWindowVM.
        PartyRest = new Game.PartyRestSync(PartyState);
        // One-to-many @-command sender. Auto-Exp-Reset
        // is the first consumer (LoopManager calls BroadcastExpReset on
        // loop start); the broadcaster's also the canonical spot for the
        // panic / kill broadcasts.
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
        // Full-screen forms (trainer stats / char creation) want
        // character-at-a-time input with server echo, not client-side
        // line buffering. Flip LocalInputBuffer into character-mode on
        // menu entry and back to line-mode on exit.
        TrainerMenu.MenuEntered += () => InputBuffer.CharacterMode = true;
        TrainerMenu.MenuExited  += () => InputBuffer.CharacterMode = false;
        // Silence the poller's wall-clock cadences (par poll + @health nag)
        // while parked in the trainer stats menu; the auto-trainer drives its
        // own wire, so its CP replay is unaffected.
        PartyPoller.IsInTrainerMenu = () => TrainerMenu.IsInTrainerMenu;
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
        // Rebuild the Spell Book's available list from a class+level
        // snapshot. Unknown / null class resolves to 0 (no class), which
        // yields an empty book — correct for non-magery classes and the
        // no-profile case alike. The obtained set is restored separately in
        // the ProfileLoaded handler below (after this seeds the class list),
        // so the learned checkmarks survive across sessions.
        void SeedSpellbook(Models.Profile.LastKnownStats? snap) =>
            Spellbook.Refresh(snap is null ? 0 : SpellCatalog.ResolveClassNumber(snap.Class) ?? 0, snap?.Level ?? 0);

        // Persist the learned-spell set with the rest of the profile. Snapshot
        // only when the book has a resolved class — with no class the obtained
        // set is empty for lack of a spell list, and blindly writing that would
        // wipe a previously-persisted set we simply can't re-resolve right now.
        Profile.ProfileSaving += p =>
        {
            if (Spellbook.ClassNumber < 1) return;
            IReadOnlyList<string> learned = Spellbook.ObtainedNames;
            p.LearnedSpells = learned.Count > 0 ? new List<string>(learned) : null;
        };

        Stats.ScreenParsed += snapshot =>
        {
            if (Profile.Current is { } p)
            {
                p.LastKnownStats = snapshot;
                // Persist immediately so the next profile load hydrates these
                // stats into PlayerStats (and the Character Workshop reads them)
                // — without this the snapshot lived only in memory and was lost
                // on reload, leaving the Workshop blank. No-op on unnamed drafts.
                Profile.Save();
            }
            // The status line carries only current HP / MA, so PromptParser
            // learns the maxima as a high-water mark that reads low until the
            // character is seen at full. The stat screen reports the true
            // ceilings — snap PlayerState.MaxHp/MaxMa to them (routed through
            // PromptParser to keep it the sole writer of the max fields).
            Player.ApplyStatScreenMax(snapshot.MaxHits, snapshot.MaxMana);
            SeedSpellbook(snapshot);
        };
        // Restore the snapshot back into live PlayerStats whenever a
        // profile loads. StatParser owns the PlayerStats fields, so
        // hydration MUST route through Stats.Hydrate; passing null
        // resets every field to default (covers fresh / never-stat'd
        // profiles cleanly). Hydrate doesn't fire ScreenParsed, so seed
        // the Spell Book here too — the persisted class+level gives the
        // Settings spell pickers their suggestions immediately, before
        // the first live `stat` reconfirms.
        Profile.ProfileLoaded += p =>
        {
            // Capture the persisted learned set before seeding fires Changed —
            // the restore below re-applies it once the class list exists.
            List<string>? learned = p.LearnedSpells is { Count: > 0 } ls
                ? new List<string>(ls) : null;
            Stats.Hydrate(p.LastKnownStats);
            // Seed the live max ceilings from the persisted snapshot so a
            // returning session starts correct instead of re-learning the
            // high-water mark from prompts. Null / never-stat'd passes 0,
            // which ApplyStatScreenMax ignores.
            Player.ApplyStatScreenMax(p.LastKnownStats?.MaxHits ?? 0, p.LastKnownStats?.MaxMana ?? 0);
            SeedSpellbook(p.LastKnownStats);
            // Restore the learned checkmarks now the class's available list is
            // built. Resolves by name against Available, so entries the current
            // class can't learn (a cross-set carryover) are harmlessly dropped.
            if (learned is not null) Spellbook.SetObtainedByNames(learned);
        };
        Profile.ProfileClosed += () =>
        {
            Stats.Hydrate(null);
            SeedSpellbook(null);
        };
        // @hangup handler — sends the configured GameCommands.ExitCommand
        // when an authorised sender (HangupDisconnect permission on
        // the Players-tab record) telepaths @hangup. Also raises the
        // HangupSignal so MainWindowVM suppresses auto-reconnect and
        // MainMenuEntryAutomation skips the entry-latch on the next
        // connect — user manually re-enters the realm after reading
        // what's on the screen.
        Hangup = new Game.Remote.HangupHandler(RemoteCommands, GameCommands, HangupSignal);
        Hangup.SetHangupsDisabledCheck(ReadDisableHangups);
        // @relog handler — graceful exit (GameCommands.ExitCommand) +
        // RelogSignal so MainWindowVM forces an unconditional reconnect
        // and the normal login automation logs the character back in.
        Relog = new Game.Remote.RelogHandler(RemoteCommands, GameCommands, RelogSignal);
        Relog.SetHangupsDisabledCheck(ReadDisableHangups);
        // @divert handler — subscribes to ChatRouter telepaths and repeats
        // them to a target while diverting. Wire-sender bound in
        // MainWindowVM after the telnet client is up.
        Divert = new Game.Remote.DivertHandler(RemoteCommands, Chat);
        // @help — replies to the sender with the catalog commands their
        // per-player permission grant allows. Reply routes through the
        // engine (ctx.Reply), so no separate wire-sender to bind.
        Help = new Game.Remote.HelpHandler(RemoteCommands);
        // @do passthrough — wire-sender bound in MainWindowVM after the
        // telnet client is up. Hard-blocks (reroll, suicide-lives) fire
        // at engine level before this handler runs.
        Do = new Game.Remote.DoHandler(RemoteCommands, Log);
        // @auto-* family. AutoMode handler mutates the
        // loaded profile's General section + persists. (@comeback is
        // wired in the Navigation block below as PartyComebackManager,
        // which needs the movement engines.)
        // AutoModeController owns the master "Auto-All" snapshot; the
        // remote handler reuses it for @auto-all so button + telepath
        // share one session snapshot. ResetSnapshot on load so a freshly
        // loaded character doesn't restore the previous one's state.
        AutoModeController = new Game.AutoModeController(Profile, Log);
        Profile.ProfileLoaded += _ => AutoModeController.ResetSnapshot();
        AutoMode = new Game.Remote.AutoModeRemoteHandler(
            RemoteCommands, Profile, AutoModeController, Log);
        // @atkprio / @atkorder — party member retunes our Target Priority /
        // Attack Order through the same numbered options as the Combat tab.
        AttackTargeting = new Game.Remote.AttackTargetingRemoteHandler(
            RemoteCommands, Profile, Log);
        // @kill <target> — party member asks us to engage a named monster.
        // Lazily resolves Combat (constructed later in this ctor) so the
        // retarget runs against the live engine at @kill time.
        Kill = new Game.Remote.KillHandler(
            RemoteCommands, name => Combat.RetargetTo(name), Log);
        // @trap auto-disarm flow — manager owns the state machine,
        // handler owns the @-command auth boundary. Wire-sender +
        // OtherSettings cadence knobs bind in MainWindowVM /
        // ApplyOtherFromActiveProfile.
        TrapDisarm = new Game.TrapDisarmManager(Router, PlayerStats, Log);
        TrapDelegation = new Game.TrapDelegationManager(Party, Players, GameData, Router, Log);
        TrapRemote = new Game.Remote.TrapHandler(RemoteCommands, TrapDisarm);

        // @goto / @loop / @lair / @stop / @rego land
        // in the Navigation block below, after Walker / LoopRunner /
        // AutoLair are constructed.

        // DoorOpenManager — walker's bash/pick/open FSM. Attempt caps
        // + verb preference are pulled live from the resolved Other
        // settings so the user can edit thresholds mid-session without
        // restarting an engine. Wire-sender is bound by MainWindowVM
        // alongside the trap one (gate-wrapped SendUserInput).
        Door = new Game.Map.DoorOpenManager(Router, PlayerStats,
            maxBashAttemptsProvider:       () => Resolver.Resolve<Models.Profile.OtherSettings>("Other").MaxBashAttempts,
            maxPickAttemptsProvider:       () => Resolver.Resolve<Models.Profile.OtherSettings>("Other").MaxPickAttempts,
            picklocksOverBashProvider:     () => Resolver.Resolve<Models.Profile.OtherSettings>("Other").PicklocksOverBash,
            itemNameLookup:                id => ItemNames.GetName(id),
            maxBashableStrengthProvider:   () => MaxStrength.MaxAchievableStrength,
            log: Log);
        // LeaderDoorAssistManager — observes the leader failing to bash a
        // door and pitches in. Reads the Party-tab toggle + the Other-tab
        // pick/bash preference live. Wire-sender bound by MainWindowVM
        // alongside the door/trap engines (gate-wrapped SendUserInput).
        LeaderDoorAssist = new Game.Map.LeaderDoorAssistManager(Router, PartyState,
            readPartySettings: () => ReadSection<Models.Profile.PartySettings>(Profile.Current, "Party"),
            readOtherSettings: () => Resolver.Resolve<Models.Profile.OtherSettings>("Other"),
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
        // Auto-entry obeys the Auto-All kill switch: when the user (or an
        // @auto-all off) actively silences automation, the menu-match send
        // is suppressed too. We gate on KillSwitchEngaged, NOT AllWiredOff —
        // a manual-play character runs with every auto-engine off but never
        // pressed the kill switch, and must still auto-enter the realm.
        MainMenuEntry = new Game.MainMenuEntryAutomation(
            Router, GameCommands, HangupSignal,
            isAutoEnabled: () => !AutoModeController.KillSwitchEngaged,
            log: Log);
        // Cleanup-driven proactive log-off. Subscribes to the same
        // CleanupWarningWatcher the reconnect scheduler reads; its safe
        // predicate + connection check + disconnect callback are wired by
        // MainWindowViewModel (they depend on VM-level connection state).
        CleanupLogout = new Game.CleanupLogoutOrchestrator(Cleanup, Router, Log);

        // Bridge: load persisted panel layouts on profile load; snapshot back
        // into the profile DTO just before serialization on save.
        Profile.ProfileLoaded += p => Panels.ApplyLayouts(p.PanelLayouts);

        // PartyManager needs the local character's name so its
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

        // Bridge: compile the prompt scanner's regex from the active
        // character's statline command (Char-tier). The same string is sent
        // to the BBS via `set statline`, so building the parser from it keeps
        // them in lockstep. Re-hydrates on load AND on every ProfileMutated
        // tick (the Statline section's Apply path fires one after a save);
        // profile close drops back to the permissive class-default pattern.
        Profile.ProfileLoaded += _ => ApplyStatlineRegex();
        Profile.ProfileClosed += PromptScanner.ResetRegexToDefault;
        Profile.ProfileMutated += _ => ApplyStatlineRegex();

        // Bridge: keep the live ToolbarConfig in sync with the loaded
        // character profile (Char-tier — each character can have its own
        // toolbar layout). Re-hydrates on every profile load AND on every
        // ProfileMutated tick (which fires from the Settings → Toolbar
        // Apply path).
        Profile.ProfileLoaded += _ => ApplyToolbarFromActiveProfile();
        Profile.ProfileClosed += ResetToolbarToDefaults;
        Profile.ProfileMutated += _ => ApplyToolbarFromActiveProfile();

        // Bridge: per-character log-diagnostic toggles (Char-tier). Apply the
        // persisted state on load, reset to off on close, and persist back
        // whenever a Log-pane toggle flips (the LogPane is the only editor —
        // no Settings-tab Apply path, so we persist on Changed directly).
        Profile.ProfileLoaded += _ => ApplyLogDiagnosticsFromActiveProfile();
        Profile.ProfileClosed += ResetLogDiagnosticsToDefaults;
        LogDiagnostics.Changed += PersistLogDiagnostics;

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

        // TBInfo store — TextBlock Info table indexed by Room.Cmd. Used
        // by the teleport / NPC-service / gambling code paths (the teleport
        // resolver reads it at walk time). Loaded BEFORE the room graph and
        // subscribed first so a set swap reloads it ahead of the graph: the
        // graph consults it during build to re-hint the door exits a CMD
        // teleport shadows (ring chime bypassing the Slum Street door). The
        // graph reads the typed store, so the raw JSON eviction here is fine.
        TBInfo = new TBInfoStore(GameData, Log);
        MonsterSpawns = new MonsterSpawnIndex(GameData, Log);
        GameData.ActiveSetChanged += TBInfo.OnActiveSetChanged;
        if (GameData.ActiveSet is not null)
            TBInfo.OnActiveSetChanged(GameData.ActiveSet);

        // Room graph — seeded from the active set's Rooms.json every time the
        // set switches. Built once per swap; consumers hold typed Room
        // references for the lifetime of the set. Takes TBInfo (loaded above)
        // so the build can promote CMD-teleport-shadowed door exits to Teleport.
        RoomGraph = new Game.Map.RoomGraphManager(GameData, Log, TBInfo);
        GameData.ActiveSetChanged += RoomGraph.OnActiveSetChanged;
        if (GameData.ActiveSet is not null)
            RoomGraph.OnActiveSetChanged(GameData.ActiveSet);

        // Quest name / visibility overlay — sibling to the per-set triggers file,
        // reloaded on every set switch. The mechanical step + bonus data the Quest
        // Status tab shows is crawled from TBInfo at runtime, not stored here.
        Quests = new QuestStore(Log);
        GameData.ActiveSetChanged += Quests.OnActiveSetChanged;
        if (GameData.ActiveSet is not null)
            Quests.OnActiveSetChanged(GameData.ActiveSet);

        // ItemNameStore — int→name index for the active Items.json so
        // the keyed-door FSM can resolve KeyItemId → in-game name and
        // send `use <name> <dir>`.
        ItemNames = new ItemNameStore(GameData, Log);
        GameData.ActiveSetChanged += ItemNames.OnActiveSetChanged;
        if (GameData.ActiveSet is not null)
            ItemNames.OnActiveSetChanged(GameData.ActiveSet);

        // ShopStockIndex — item id → shops stocking it, from Shops.json.
        // Feeds PathItemShopRouter's "who sells this?" lookup.
        ShopStock = new ShopStockIndex(GameData, Log);
        GameData.ActiveSetChanged += ShopStock.OnActiveSetChanged;
        if (GameData.ActiveSet is not null)
            ShopStock.OnActiveSetChanged(GameData.ActiveSet);

        // MonsterDropIndex — item id → dropping monsters + their spawn rooms,
        // from Monsters.json. Feeds MonsterDropRouter's "who drops this, and
        // where?" lookup for items no shop sells.
        MonsterDrops = new MonsterDropIndex(GameData, Log);
        GameData.ActiveSetChanged += MonsterDrops.OnActiveSetChanged;
        if (GameData.ActiveSet is not null)
            MonsterDrops.OnActiveSetChanged(GameData.ActiveSet);

        // Room tracker. Resets to Unknown on every
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

        // Realm-entry keystroke isn't a move. The entry command (default "E")
        // collides with cardinal East and is pumped through the same
        // wire-observe pipeline as manual movement; without this coupling a
        // fresh-login "E" fabricates an East step that walks RoomTracker off
        // the just-hydrated login room.
        MainMenuEntry.SetMoveSuppressor(OutboundMovement.SuppressNextMove);

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

        // Dark-room position tracking. A room too dark to see starves the normal
        // name + exits display (see GAME_MECHANICS.md), so the usual
        // move-confirming observation never fires. Both darkness forms feed
        // NoteDarkRoomEntered, which advances position along the pending move's
        // mapped edge (no bonk means we traversed) and flags IsInDarkRoom so
        // DarkRoomCombatWatcher can engage a mob revealed only by its attack
        // line. Independent of AutoLight's master switch — position tracking
        // always runs.
        Router.Subscribe(Services.Patterns.KnownPatterns.RoomPitchBlack,
            _ => RoomTracker.NoteDarkRoomEntered());
        Router.Subscribe(Services.Patterns.KnownPatterns.RoomVeryDark,
            _ => RoomTracker.NoteDarkRoomEntered());

        // Follower-drag → tracker bridge. When the party leader walks, the game
        // drags us one room and prints " -- Following your Party leader <dir> --";
        // a follower types no move, so without turning that line into a
        // NoteMoveSent the tracker keeps its old anchor, mismatches every new room
        // and falls to Lost within a few rooms.
        FollowMove = new Game.Map.FollowMoveObserver(Router, RoomTracker, Log);

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

        // BFS pathfinding + planar layout. Layout
        // cache invalidates on every graph reload.
        Bfs = new Game.Map.BfsMapper(RoomGraph, Log);
        RoomGraph.GraphReloaded += Bfs.OnGraphReloaded;
        // Pre-warm the layout on a thread-pool task so the user
        // doesn't pay the BFS cost on the UI thread when they first
        // open the Navigation window.
        RoomGraph.GraphReloaded += Bfs.PrewarmAsync;

        // Per-character avoided + stash rooms.
        // Constructor subscribes ProfileLoaded / ProfileClosed and
        // hydrates from the currently-loaded profile if there is one.
        Movement = new MovementFilter(Profile, Log);
        // Feed the player's level into Form-A exit level-gate evaluation.
        // null until a stat screen parses — IsExitBlocked never gates on
        // an unknown level, so an unparsed character walks unrestricted.
        Movement.LevelProvider = () => Stats.HasParsed ? PlayerStats.Level : (int?)null;
        // Feed on-hand wealth into (Toll: N) exit affordability. null until an
        // 'i' dump parses (IsLoaded false), so an unknown wallet never gates —
        // same rule as an unknown level. IsLoaded distinguishes "empty purse"
        // (a real 0 that WOULD gate a toll) from "haven't parsed inventory yet".
        Movement.WealthProvider = () =>
            Inventory.IsLoaded ? Inventory.Snapshot.Currency.TotalCopperValue : (long?)null;
        // Feed the player's own class Number into "(Class: N OK)" gate
        // evaluation, resolving the class name through the Classes table (reuses
        // the equip-filter resolver so the name→Number mapping lives in one
        // place). null until stats parse or when the class is unknown, so an
        // unparsed character walks unrestricted — same rule as level / wealth.
        Movement.ClassNumberProvider = () =>
        {
            if (!Stats.HasParsed) return null;
            int n = Game.Inventory.ItemEquipFilter
                .ResolveClassProfile(GameData, PlayerStats.Class).ClassNumber;
            return n > 0 ? n : (int?)null;
        };
        Favorites = new FavoritesStore(Profile, Log);

        // Coordinator + walker. Coordinator is the
        // single pause-gate hub for every movement engine (walker now,
        // loop / auto-lair later). Walker's wire sender is bound by
        // MainWindowViewModel once the telnet client is up (matching
        // the PartyPoller / AutoPartyManager pattern).
        MovementCoordinator = new Game.Map.MovementCoordinator(Log);

        // Party-vitals pause bridge — asserts MovementCoordinator's
        // PartyVitalsGate while any other party member's HP% is below the
        // Party-tab "wait if members are below" threshold.
        PartyVitals = new Game.PartyVitalsWatcher(
            PartyState, MovementCoordinator,
            readSettings: () => ReadSection<Models.Profile.PartySettings>(Profile.Current, "Party"),
            log: Log);

        // Follower-movement pause bridge — asserts MovementCoordinator's
        // FollowerGate while we're a party follower (in a party, not leading)
        // so the leader's drag isn't fought by our own walk / loop / auto-lair.
        // Unconditional: leader-driven movement is a hard game constraint, not
        // a user toggle.
        PartyFollowerMovement = new Game.PartyFollowerMovementGate(
            PartyState, MovementCoordinator, Log);

        // Inbound-@wait pause bridge — asserts MovementCoordinator's
        // PartyWaitGate while a party member has telepathed @wait (or announced
        // .@held) and hasn't sent @ok, so our own loop / Auto-Lair / walk-to
        // holds instead of splitting from a resting member. PartyEssentials was
        // constructed earlier and already applies the leader-side opt-out.
        PartyWaitMovement = new Game.PartyWaitMovementGate(
            PartyEssentials, MovementCoordinator, Log);

        // Follower-disconnect pause bridge (leader side) — asserts
        // MovementCoordinator's MemberDisconnectGate when PartyManager reports a
        // follower drop, so we hold in place while they try to reconnect and
        // re-party instead of sprinting off. Clears on their re-follow or when
        // the grace window (IfLeadingWaitTotalSec) elapses.
        PartyDisconnectMovement = new Game.PartyDisconnectMovementGate(
            Party, MovementCoordinator, Log);

        // Needs registry. Cross-engine fulfillment hub;
        // auto-light (9.K) posts, auto-get (9.L) fulfils. Cleared on
        // character swap so pending needs don't leak across profiles.
        Needs = new NeedsRegistry(Log);
        Profile.ProfileLoaded += _ => Needs.Clear();

        // Shared Acquisition movement-gate driver. Both
        // AutoGetItems and Cash feed this one instance (bound after they're
        // constructed below) so the walker holds until BOTH finish looting.
        Acquisition = new Game.Inventory.AcquisitionGate(MovementCoordinator, Log);

        // RoomEntityClassifier + CombatStateTracker.
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

        // RoundDamageTracker. shouldWriteTrace
        // delegate reads the Log pane's combat-diagnostics umbrella
        // (session-only, no per-profile persistence) so the user can
        // toggle the per-round trace from the Log menu mid-session.
        RoundDamage = new Game.Combat.RoundDamageTracker(
            Router, PlayerState, Log,
            shouldWriteTrace: () => LogDiagnostics.CombatDiagnostics);
        // Drive round boundaries off the 5-second combat heartbeat so each round
        // closes (and is counted) in real time rather than lagging until the next
        // damage line or *Combat Off*. Both are app-lifetime singletons, so no
        // unsubscribe is needed.
        Tick.CombatTickElapsed += RoundDamage.OnCombatTick;
        // Reset round counter + ring on BBS connect to match
        // CombatSessionTracker's session-boundary convention — the
        // reset hook lives here on the data producer.
        Profile.ProfileLoaded += _ => RoundDamage.Reset();
        // CombatSessionTracker is constructed after Inventory (its
        // proc recogniser reads the worn-weapon snapshot) — see below.

        // Local-death observation. Pure subscriber;
        // DeathRecoveryManager consumes the PlayerDied event
        // for its corpse-recovery flow. Reset the in-flight round
        // accumulator on death so a partial round doesn't get
        // attributed to the next combat.
        DeathWatcher = new Game.Combat.DeathLineWatcher(Router, Log);
        DeathWatcher.PlayerDied += _ => RoundDamage.MarkCombatEnded();

        // Death-floor tracer. Watches the HP descent into each death and, on a
        // clean slow death (bled gradually to the floor, not overkilled), refines
        // the active BBS's PlayerDiesAtHp to the measured value — the seed is only
        // a guess. Reads / persists the realm profile through the same
        // ResolveActiveBbs / Bbs.Save path the settings UI uses.
        DeathFloorTracer = new Game.Health.DeathFloorTracer(
            PlayerState, ResolveActiveBbs, Bbs.Save, Log);
        DeathWatcher.PlayerDied += _ => DeathFloorTracer.RecordDeath();

        // Death-halt bridge. On our death, stops every movement engine (via
        // UserGate) so we stay in the graveyard we respawn into until the player
        // manually resumes — no loop / walk-to / auto-lair marches us back out
        // before we've recovered. Rides RoomTracker.PlayerDeathObserved (fires on
        // BOTH death phrasings) rather than DeathLineWatcher's "slain by"-only line
        // so a miracle-save death halts too.
        PlayerDeathHalt = new Game.PlayerDeathMovementHalt(RoomTracker, MovementCoordinator, Log);

        // Dropped / mortally-wounded bridge. While HP is at or below 0 the
        // character can't act — the game rejects every command — so this holds
        // the EngineSendGate (silences all wrapped engines), asserts the
        // MortallyWoundedGate (visible movement pause), and clears the stale
        // party roster (a drop removes us from the party game-side; recovery
        // needs a re-invite from the leader to rejoin). All three release the
        // moment HP climbs back positive.
        PlayerDropped = new Game.PlayerDroppedGate(
            PlayerState, EngineGate, MovementCoordinator, Party, Log);

        // Ally-drop rescue. Distinct from PlayerDropped (which owns OUR drop):
        // reacts to another party / recently-partied member hitting 0 HP — aids
        // them, holds movement via AllyDownGate to stay in the room, polls their
        // off-roster vitals via @health, and re-invites once aided when we lead.
        // The heal-by-name is delegated to CastDirector via the downed-ally
        // provider wired below. Gated on AutoHealRest (shared party-heal master).
        AllyDropped = new Game.AllyDroppedHandler(
            Router, PartyState, Party, Chat, MovementCoordinator,
            readParty: () => ReadSection<Models.Profile.PartySettings>(Profile.Current, "Party"),
            isEnabled: () => ReadAutoModeFlag(d => d.AutoHealRest),
            log: Log);

        // CombatManager. Picks a target on each
        // classifier emit and sends the configured attack command via
        // the bound wire sender. Reads CombatSettings live (same
        // pattern as CombatStateTracker) so toggling Master / changing
        // TargetOrder / etc. mid-session takes effect on the next
        // Also-Here line.
        // Mid-room arrival watcher. Subscribes to the
        // RoomEntryArrival pattern + appends to the classifier so the
        // Combat gate / CombatManager react to spawns immediately.
        RoomEntry = new Game.Combat.RoomEntryWatcher(Router, RoomClassifier, Log);

        // Monster death watcher. Specific-pattern matches
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
            // The six weapon fields are derived from the Equipment Manager's gear
            // sets (the Combat tab no longer edits weapons): normal + alternate
            // from the Default set, backstab from the Backstab set when enabled
            // else the Default set. Overlaid on each read so combat tracks the
            // current gear sets + the live backstab-set Enabled state.
            readSettings: () =>
            {
                Models.Profile.CombatSettings combat =
                    ReadSection<Models.Profile.CombatSettings>(Profile.Current, "Combat");
                Game.Inventory.EquipmentWeaponSync.ApplyWeapons(
                    combat, Profile.Current?.Equipment ?? new Models.Profile.EquipmentSettings());
                return combat;
            },
            isEnabled: () => ReadAutoModeFlag(d => d.AutoCombat),
            readOwnGivenName: () => Profile.CurrentProfileName,
            post: action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            log: Log,
            readPartySettings: () =>
                ReadSection<Models.Profile.PartySettings>(Profile.Current, "Party"));

        // Dark-room combat. A room too dark to show "Also here:" hides any
        // hostile sharing it — the only evidence is the mob's dark-cyan attack
        // line. This watcher reads the monster name off that line and injects it
        // into the classifier so CombatManager engages it exactly as if it had
        // been listed (see GAME_MECHANICS.md). Gated on RoomTracker.IsInDarkRoom
        // so it never fabricates a target in a lit room. Retracts on "Your
        // command had no effect." — the game's tell that the target has left.
        DarkRoomCombat = new Game.Combat.DarkRoomCombatWatcher(
            Router, RoomTracker, RoomClassifier,
            currentTarget: () => Combat.CurrentTarget,
            log: Log);

        // HealthManager. Master on/off is
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
            readCombatSettings: () =>
                ReadSection<Models.Profile.CombatSettings>(Profile.Current, "Combat"),
            readGeneralSettings: () =>
                ReadSection<Models.Profile.GeneralSettings>(Profile.Current, "General"),
            // Don't try to rest while engageable hostiles are in the
            // room — every combat round would otherwise break rest.
            // CombatStateTracker owns the same boolean it uses to
            // assert the CombatGate, so we stay in sync with the
            // movement gate logic.
            hasEngageableHostiles: () => CombatTracker.HasEngageableHostiles,
            // Per-realm negative-HP death floor: keeps the emergency
            // hangup firing through the bleeding-out window down to the
            // point the character actually dies.
            readDeathFloor: () => ResolveActiveBbs()?.PlayerDiesAtHp ?? -25,
            log: Log,
            // Emergency hangup drops the carrier on purpose — flag it so the
            // reactive-reconnect path doesn't immediately dial back in.
            hangupSignal: HangupSignal,
            // Hostile-aware gate for the emergency hangup: only bail while a
            // hostile is actually here. HasHostileMonster (unlike
            // HasEngageableHostiles) ignores the auto-attack master switch, so a
            // manual player still hangs up when a mob shows up.
            hasHostileInRoom: () => CombatTracker.HasHostileMonster,
            // Reverse-flee routing: BFS from the current room back to the active
            // engine's start. No filter so gates / avoided rooms never block an
            // escape — a flee just needs to physically retreat along the graph.
            findReversePath: (from, to) => Bfs.FindPath(from, to));

        // Late-wire the classifier's flee probe now that Health exists (it's
        // built after RoomClassifier). While fleeing, a monster that pursues us
        // into the next room must not re-arm the Combat gate — the classifier
        // reads this to keep running instead of halting to fight the pursuer.
        RoomClassifier.FleeProbe = () => Health.IsFleeing;

        // Re-check the emergency hangup whenever the room's occupants change: a
        // hostile that wanders in or spawns while we're already below the trigger
        // won't touch our own PlayerState, so nothing else would drive the check.
        // Subscribed after CombatTracker (which updates HasHostileMonster in its
        // own EntitiesObserved handler) so this reads the current hostile flag.
        RoomClassifier.EntitiesObserved += _ => Health.ReevaluateEmergencyHangup();

        // Leader-rest nudge: a standing-idle follower's own PlayerState may
        // not change between the 5s par polls that flip the leader's
        // Resting / Meditating flags, so without this poke Health wouldn't
        // re-evaluate (and start opportunistically resting) until its next
        // prompt tick. Edge-triggered — fires only when the leader's posture
        // actually flips. Process-lifetime singleton (not disposed here).
        PartyLeaderRest = new Game.PartyLeaderRestWatcher(
            PartyState, onLeaderRestChanged: () => Health.Evaluate());

        // Role-aware recovery: as a party follower we top off only to the
        // rest floor (not full) and ping the leader via @wait / @ok so we
        // don't silently hold or release the party. Solo / leader keeps the
        // full rest-max topoff — PartyRestSync self-gates the telepaths.
        // isLeaderResting drives the inherent "rest while the leader rests"
        // opportunistic topoff (gated only by the auto-heal master switch).
        // requestPartyHeal is the follower's flee-substitute: at the run-if-below
        // trigger a follower broadcasts @heal (via PartyRest) instead of running
        // off alone. Leader / solo still flee. The HealCommandHandler below is
        // the receive side that turns that broadcast into a party heal.
        Health.SetPartyRoleSync(
            isPartyFollower: () => PartyState.IsInParty && !PartyState.SelfIsLeader,
            requestPartyWait: () => PartyRest.RequestWait(Game.WaitReason.Health),
            requestPartyOk: () => PartyRest.RequestOk(Game.WaitReason.Health),
            isLeaderResting: () => PartyLeaderRest.LeaderIsResting,
            requestPartyHeal: () => PartyRest.RequestHeal());

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

        // CastCoordinator. Subscribes to spell-failure
        // patterns directly; tick-clears its block latch + cooldown via
        // TickEngine.CombatTickElapsed so the next round can cast.
        Cast = new Game.Spells.CastCoordinator(Router, Log);
        Tick.CombatTickElapsed += Cast.OnCombatTick;

        // ConditionTracker reads MessageStore +
        // line-side patterns to surface ActiveFlags. CastingDirector
        // consumes it for Tier-2 cure decisions. AttachLineExtractor
        // lands in MainWindowViewModel alongside the other line
        // consumers.
        Conditions = new Game.Conditions.ConditionTracker(Messages, Log);

        // AilmentSyncEngine — outbound ailment broadcast. On catching a
        // curable ailment (or being held) it announces ".@poisoned" /
        // ".@held" etc. on say (so other FujinTerm clients mirror our state
        // and a cure-holds caster can free us) and, for the curable four,
        // @waits the leader; on clear it @oks. The say only fires when we're
        // in a party AND have no cure spell configured for that ailment (we
        // self-cure silently otherwise); held rides its say-pause with no
        // @wait. Per-ailment OtherSettings DoNotAnnounce* (say) and Ignore*
        // (@wait) gate the curable four on top. Wire-sender for the say bound
        // in MainWindowViewModel; the @wait routes via PartyRest's own sender.
        AilmentSync = new Game.Conditions.AilmentSyncEngine(
            Conditions, PartyRest,
            readSpells: () => ReadSection<Models.Profile.SpellsSettings>(Profile.Current, "Spells"),
            isInParty: () => PartyState.IsInParty,
            hasCureConfigured: HasCureConfigured,
            log: Log);

        // PartyAilmentTracker — inbound counterpart. Mirrors a member's
        // ".@poisoned" / ".@held" etc. say announce onto their party chip (via
        // PartyManager, the chip-field owner), pauses the leader on ".@held"
        // (via PartyEssentials.NotePause), and clears the chip when OUR cure
        // spell is observed landing on them. The cure matchers are read live
        // each line so re-configuring a cure spell takes effect without
        // rebuilding the tracker. AttachLineExtractor lands in
        // MainWindowViewModel alongside the other line consumers.
        PartyAilment = new Game.Conditions.PartyAilmentTracker(
            Chat, Party, PartyEssentials, CureCastMatchers, Log);

        // CastingDirector. Sits on top of Cast,
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
        // Stealth gate — buff casts suppressed while
        // sneaking or hidden so we don't break the backstab window.
        CastDirector.SetStealthGate(() => Stealth.IsStealthed);
        // Survival casts (heal / cure / buff / party heal) skip any spell the
        // player can't afford — the cost comes from the game-data Spells table
        // via the live spellbook. Combat-tab spells keep their own
        // MinManaPerCast threshold and aren't gated here.
        CastDirector.SetManaCostLookup(Spellbook.ManaCostOf);
        // Auto-Bless auto-engine gate — when off, the Buffing category is
        // suppressed (no Bless / regen / when-full buff fires).
        CastDirector.SetAutoBlessGate(() => ReadAutoModeFlag(d => d.AutoBless));
        // Buff-duration recast model. A buff cast (self or
        // party) is confirmed, then suppressed until it's within the
        // pre-expiry recast window. BuffInfoByShort maps a 4-letter cast
        // code to its CasterMessage confirmation template + computed
        // duration (SpellCalculator.Duration at the live level);
        // ShortFromAppliedRecord maps a fired AppliedMessage record back
        // to the cast code so a confirmed self-buff starts its timer.
        CastDirector.SetBuffDurationSources(BuffInfoByShort, ShortFromAppliedRecord);
        // Party-bless slots store class numbers; PartyMember.Class is a
        // class name — resolve via the active set's Classes table.
        CastDirector.SetClassResolver(SpellCatalog.ResolveClassName);
        // A party-wide buff (Spells.Targets = Full / Divided Party Area) is
        // cast once for the whole party; the picker checks this to skip the
        // per-member loop.
        CastDirector.SetPartyWideBuffCheck(IsPartyWideBuff);
        // Downed-ally rescue heal. A dropped ally leaves `par`, so PickPartyHeal's
        // roster walk can't see them — the AllyDroppedHandler feeds each aided
        // downed ally back in here as the top-priority name-targeted heal until
        // they recover / rejoin.
        CastDirector.SetDownedAllyProvider(() => AllyDropped.AidedDownedGivenNames());
        Tick.CombatTickElapsed += CastDirector.OnCombatTick;

        // Mana-regen roll-spell reroll (Paradigm only). AbilBreakdown parses
        // `abil 145`; ManaRegen reads its rolled `spells:` slice after each
        // nature-tap / mana-flux landing and recasts a below-threshold roll up
        // to the cap, hard-stopping at the buff mana floor. The abil query + the
        // deliberate cooldown-bypassing recast go out on the raw engine sender
        // (bound in the main VM); the recast still notifies Cast so the
        // one-cast-per-round cooldown bookkeeping stays honest.
        AbilBreakdown = new Game.AbilBreakdownParser(Log);
        ManaRegen = new Game.Spells.ManaRegenReroller(
            AbilBreakdown,
            readConfig: () =>
            {
                Models.Profile.SpellsSettings s =
                    ReadSection<Models.Profile.SpellsSettings>(Profile.Current, "Spells");
                return new Game.Spells.ManaRegenRerollConfig(
                    s.ManaRegenRerollThreshold, s.ManaRegenRerollCap);
            },
            sendAbilQuery: () =>
                _engineWireSend?.Invoke(System.Text.Encoding.Latin1.GetBytes("abil 145\r")),
            recast: shortCode =>
            {
                _engineWireSend?.Invoke(
                    System.Text.Encoding.Latin1.GetBytes(shortCode.Trim() + "\r"));
                Cast.NotifyExternalCastSent();
            },
            canAffordReroll: CanAffordManaRegenReroll,
            log: Log);
        CastDirector.SetSelfBuffLandedSink(OnSelfBuffLandedForReroll);

        // Opt the combat engine into the
        // per-round combat-spell economy (pre-attack debuff + multi/normal/
        // alternate attack spells) atop the shared CastCoordinator so the
        // one-cast-per-round cooldown is honoured. The heartbeat subscribes
        // AFTER Cast.OnCombatTick (clears the cooldown) and
        // CastDirector.OnCombatTick (survival heal/cure/buff) so offensive
        // combat casts yield this round when survival already spent it.
        Combat.SetCombatSpellCaster(Cast, () => (PlayerState.Ma, PlayerState.MaxMa));
        // Auto-Nuke auto-engine gate — when off, the chooser never offers the
        // multi-target attack spell or either debuff (single-target attack
        // spells are not nukes and stay available).
        Combat.SetAutoNukeGate(() => ReadAutoModeFlag(d => d.AutoNuke));
        // Debuffs are in-between actions, not combat actions — the combat
        // engine owns the decision but CastDirector casts them through the
        // shared in-between window (at PriorityDebuffing, so survival heals
        // win). CastDirector.OnCombatTick (subscribed above) runs before
        // Combat.OnCombatTick, so the debuff is offered before the combat
        // heartbeat re-issues the round's combat action.
        CastDirector.SetCombatDebuffSource(Combat.PickInBetweenDebuff, Combat.CommitInBetweenDebuff);
        // A between-round survival cast stops our auto-attack; let the combat
        // engine resume the weapon attack on the resulting *Combat Off*
        // instead of idling until the next round.
        CastDirector.CastFired += Combat.NoteBetweenRoundCast;
        // Same resume, but for a HAND-typed cast: a manual cast-code never
        // routes through CastDirector, so sniff the wire for one and arm the
        // identical signal. A cast-code is any Spells.Short in the active
        // class's available list.
        OutboundCast = new Game.Combat.OutboundCastObserver(
            isCastCode: c => Spellbook.FindByCastCode(c) is not null,
            onManualCast: Combat.NoteBetweenRoundCast);
        Tick.CombatTickElapsed += Combat.OnCombatTick;

        // StealthManager state tracker + auto-sneak /
        // auto-hide engines. Owns PlayerState.IsSneaking/IsHidden,
        // detects silent loss on room change, and sends `sneak` /
        // `hide` per AutoMode toggles.
        Stealth = new Game.Stealth.StealthManager(Router, PlayerState, Log);
        Stealth.SetAutoToggles(
            isAutoSneakEnabled: () => ReadAutoModeFlag(d => d.AutoSneak),
            isAutoHideEnabled:  () => ReadAutoModeFlag(d => d.AutoHide));
        // Any NPC in the room prevents sneak, so
        // suppress the doomed `sn` instead of firing it into a rejection.
        Stealth.SetSneakBlockCheck(() => CombatTracker.HasRoomNpc);
        // Auto-hide is suppressed in a party — a hidden member falls off the
        // Also-here line and can't be single-target-healed/buffed until revealed.
        Stealth.SetPartyCheck(() => PartyState.IsInParty);

        // Backstab window — CombatManager opens with `bs` on the first swing while
        // stealthed: either a sneak-approach into the monster's room, or a monster
        // walking into a room the character is (optimistically) hidden in. Skipped
        // when a seehidden monster is present (which reveals us to the whole room).
        SeeHidden = new Game.Combat.SeeHiddenIndex(GameData);
        Combat.SetBackstabHooks(
            isStealthed:  () => Stealth.IsStealthed,
            hasSeeHidden: n => SeeHidden.Has(n));
        // A fresh hide re-arms the surprise round for the stationary hidden opener:
        // when the FSM latches Hidden, re-open so a monster that wanders in is a
        // genuine backstab target again (no gear swap — equipping would break hide).
        Stealth.StateChanged += (prev, next) =>
        {
            if (next == Game.Stealth.StealthState.Hidden
             && prev != Game.Stealth.StealthState.Hidden)
                Combat.RearmBackstabForHide();
        };
        // Backstab-failure flee (CombatSettings.RunIfBackstabFails). Combat detects
        // the failed surprise round; HealthManager owns the flee route + engine.
        Combat.SetBackstabFailureFlee(() => Health.RunFromBackstabFailure());

        // ShadowRest (Paradigm): classes carrying ability code 1103 can rest while
        // hidden/sneaking in a room with monsters without being attacked. The rest
        // engine relaxes its hostiles guard when solo + stealthed + class-capable +
        // opted in; combat stands down (reads ShadowRestHolding) so the rest isn't
        // broken, and HealthManager fires ResumeAfterShadowRest at rest-max to
        // re-open with the held-back backstab. Inert on classes without 1103.
        bool ClassHasShadowRest() =>
            Stats.HasParsed
            && GameData.FindRowByName("Classes", PlayerStats.Class) is { } classRow
            && Game.GameData.AbilityNames.HasShadowRest(classRow);
        Health.SetShadowRest(
            shadowRestClass: ClassHasShadowRest,
            isStealthed:     () => Stealth.IsStealthed,
            isSolo:          () => !PartyState.IsInParty,
            onRecovered:     Combat.ResumeAfterShadowRest);
        Combat.SetShadowRestSuppression(() => Health.ShadowRestHolding);

        // Deterministic magic eligibility — weapon HitMagic ≥ monster Magical
        // picks normal-vs-alternate, spell ReqLevel ≥ monster SpellImmu gates
        // single-target debuff / attack spells, and the resist pair skips an attack
        // spell whose element the target resists ≥ 100%. All fail open when game
        // data is silent.
        MonsterMagic = new Game.Combat.MonsterMagicIndex(GameData);
        MonsterHp = new Game.Combat.MonsterHpIndex(GameData);
        ItemMagic = new Game.Combat.ItemMagicIndex(GameData);
        SpellReqLevel = new Game.Combat.SpellReqLevelIndex(GameData);
        MonsterResist = new Game.Combat.MonsterResistIndex(GameData);
        SpellAttackType = new Game.Combat.SpellAttackTypeIndex(GameData);
        Combat.SetMagicEligibility(
            MonsterMagic, ItemMagic, SpellReqLevel, MonsterResist, SpellAttackType);

        // Light catalogue + live carried illumination. The snapshot provider is
        // deferred (Inventory is assigned later in this method), so reading
        // PlayerIllumination.Current at tooltip / route time sees the live dump.
        Lights = new Game.Light.LightItemIndex(GameData);
        PlayerIllumination = new Game.Light.PlayerIllumination(
            () => Inventory.Snapshot, Lights, GameData);

        // Per-set bash ceiling — strongest race's Strength cap plus the best
        // +Strength gear any class can wear. The door FSM (constructed earlier)
        // reads this via its maxBashableStrengthProvider so a strength-gated door
        // is only ruled unbashable when no reachable build could open it.
        MaxStrength = new Game.Map.MaxStrengthIndex(GameData);

        // Actionability gate — the walker-gate owner releases when a room's
        // remaining hostiles are all un-actionable (no weapon hits, every
        // attack spell level-blocked) so the walker moves past instead of
        // standing in an unwinnable fight. Reuses CombatManager's deterministic
        // CanEngageMonster so the gate and the swing decision can't diverge.
        CombatTracker.SetActionabilityGate(n => Combat.CanEngageMonster(n));

        // Combat-off "clear hostiles when seen Hidden" override —
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

        // AutoLightManager. Posts a LightSource need to
        // the registry on a "can't see" room-light line; auto-get (9.L)
        // fulfils it. Gated by the AutoLight master toggle (Settings →
        // General checkbox + the toolbar Toggle button write the same
        // flag; the delegate is queried per dark-room line so toggling
        // takes effect immediately).
        AutoLight = new Game.Light.AutoLightManager(Router, Needs, Log);
        AutoLight.SetEnabledToggle(() => ReadAutoModeFlag(d => d.AutoLight));

        // DeathRecoveryManager. Aggregates the
        // DeathLineWatcher.PlayerDied event + the profile's
        // DeathHistory list (written by DeathDetector ->
        // RoomTracker.NoteDeath) into observables the Workshop
        // DEATH section binds to. (@comeback is a separate party-pickup
        // flow owned by PartyComebackManager, wired after the engines.)
        DeathRecovery = new Game.Recovery.DeathRecoveryManager(
            DeathWatcher, Profile, RoomTracker, Log);

        // InventoryManager. Parses the full `i` dump into a
        // currency + numeric-encumbrance snapshot and patches it on
        // coin pickups / drops and item get / drop / buy / sell. CashManager
        // reads the snapshot for its encumbrance gate. The item-weight resolver
        // lets item transactions move the encumbrance estimate between dumps;
        // the slot resolver labels a freshly-worn piece with its real slot (the
        // wear line names none) so "Snapshot Current" files it correctly (both
        // read ItemNames, already loaded above). MarkStale on profile swap so the
        // new character's first gate evaluation waits for a fresh `i`.
        Inventory = new Game.Inventory.InventoryManager(
            Log,
            ItemNames.WeightOf,
            name => ItemNames.WornCodeOf(name) is int worn
                ? Game.Inventory.EquipmentSlotMap.InventorySlotForWornCode(worn)
                : null);
        Profile.ProfileLoaded += _ => Inventory.MarkStale();
        // Death-recovery deathpile capture. RoomTracker.NoteDeath
        // records the worn + carried items from the last-known `i` snapshot
        // onto the death record; DeathRecoveryManager.SimulateDeath captures
        // the same way for the test button.
        RoomTracker.AttachInventorySnapshot(() => Inventory.Snapshot);
        DeathRecovery.AttachInventorySnapshot(() => Inventory.Snapshot);

        // CombatSessionTracker. Aggregates the same combat lines
        // plus RoundDamage's closed rounds into the Session Stats figures, and
        // recognises two game-data-driven damage rows the fixed regex patterns
        // can't: a configured attack SPELL's cast (Combat tab → KnownSpell →
        // CasterMessage) and the equipped weapon's PROC (worn weapon → Items#N
        // message). Both fold into their own rows — out of the swing accuracy +
        // physical extent — while their damage still rolls into the per-round
        // total via RoundDamage's UserHits subscription. Constructed here (not
        // beside RoundDamage) because the proc resolver reads Inventory's
        // worn-weapon snapshot. Matchers refresh on the boundaries that move
        // them: connect / char switch (ProfileLoaded, which also zeroes the
        // session in lockstep with RoundDamage), a Combat-tab edit
        // (ProfileMutated), a game-data set swap (ActiveSetChanged), and a
        // weapon swap (Inventory.Changed).
        CombatSession = new Game.Combat.CombatSessionTracker(
            Router, RoundDamage, AttackSpellMatchers, EquippedWeaponProcMatcher);
        Profile.ProfileLoaded  += _ => { CombatSession.Reset(); CombatSession.RefreshMatchers(); };
        Profile.ProfileMutated += _ => CombatSession.RefreshMatchers();
        GameData.ActiveSetChanged += _ => { _procWeaponName = null; CombatSession.RefreshMatchers(); };
        Inventory.Changed += () => CombatSession.RefreshMatchers();

        // TimeAnalysisTracker. Divides the session's wall-clock time
        // across the player's activities + the affliction overlays (blinded /
        // poisoned / diseased / confused / held). It
        // owns no subscriptions (its inputs span three sources), so forward each
        // here: PlayerState carries combat / position / vitals, Conditions the
        // affliction flags, and a confirmed room change (NewRoom differs from
        // the previous) opens its movement window. Reset on the same
        // ProfileLoaded boundary as the other session-stats trackers.
        TimeAnalysis = new Game.Combat.TimeAnalysisTracker();
        PlayerState.PropertyChanged += (_, _) => TimeAnalysis.NotePlayerState(
            PlayerState.InCombat, PlayerState.Position,
            PlayerState.Hp, PlayerState.MaxHp, PlayerState.Ma, PlayerState.MaxMa);
        Conditions.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Game.Conditions.ConditionTracker.ActiveFlags))
                TimeAnalysis.NoteAfflictions(
                    Conditions.IsBlinded, Conditions.IsPoisoned, Conditions.IsDiseased,
                    Conditions.IsConfused, Conditions.IsMovementPrevented);
        };
        RoomTracker.StateChanged += t =>
        {
            if (t.NewRoom is not null && !ReferenceEquals(t.NewRoom, t.PreviousRoom))
                TimeAnalysis.NoteRoomChanged();
        };
        // In-game gate: the first prompt of the session arms accrual (idempotent
        // — subsequent prompts no-op), so BBS-menu / login time never counts.
        // Same WirePromptScanner the EventScheduler uses for its in-game latch.
        PromptScanner.PromptObserved += _ => TimeAnalysis.NoteInGame();
        // A fresh character starts disarmed: zero the counters, then Suspend so
        // accrual waits for that character's first in-game prompt. (Disconnect
        // disarms via MainWindowVM; @reset / the window button keep counting.)
        Profile.ProfileLoaded += _ => { TimeAnalysis.Reset(); TimeAnalysis.Suspend(); };

        // SessionActivityTracker. Counts kills + experience and keeps
        // the rolling kill history for the kills/hour sparkline. Like the other
        // session-stats trackers it owns no subscriptions: a kill arrives from
        // MonsterDeath (specific or fallback alike — both mean one mob down) and
        // experience from the gain line. Reset on the same session boundary.
        SessionActivity = new Game.Combat.SessionActivityTracker();
        MonsterDeath.MonsterDied += _ => SessionActivity.NoteKill();
        Router.Subscribe(Services.Patterns.KnownPatterns.UserGainExperience, m =>
        {
            if (m.Groups.Count > 0 && int.TryParse(m.Groups[0], out int exp))
                SessionActivity.NoteExperience(exp);
        });
        Profile.ProfileLoaded += _ => SessionActivity.Reset();

        // TransactionHistory. A per-session ledger of cash/item
        // offloads: bank `dep`osits (AutoDeposit.Deposited) and stash-room
        // `hide`s (Stash.StashExecuted), wired to their events below. Feeds the
        // Session Stats → Transaction history window; reset on the same session
        // boundary as the other session-stats trackers.
        TransactionHistory = new Game.Cash.TransactionHistoryTracker();
        Profile.ProfileLoaded += _ => TransactionHistory.Reset();

        // @reset — a party member zeroes our session-stats trackers (the same
        // wipe as the window button / connect boundary). Constructed here, after
        // the session-stats trackers exist; RemoteCommands was built upstream.
        SessionReset = new Game.Remote.SessionResetHandler(
            RemoteCommands, CombatSession, TimeAnalysis, SessionActivity, TransactionHistory, Log);

        // Read-only progression queries — @exp / @level report against the
        // PlayerStats snapshot (from `stat` / `exp`) and the session
        // exp-rate tracker. No wire output, so no sender to bind.
        ExperienceQuery = new Game.Remote.ExperienceQueryHandler(
            RemoteCommands, PlayerStats, SessionActivity);

        // Room-floor loot snapshot from the "You notice <list> here." survey,
        // cash filtered out. Feeds @what (read) and @get-all (get each).
        // LineExtractor attached + OnRoomChanged wired below (and in MainWindowVM).
        GroundItems = new Game.Inventory.GroundItemTracker(Router);

        // Read-only inventory queries — @wealth / @enc / @have report off the
        // InventoryManager snapshot; @what reports the GroundItems survey. No
        // wire output either.
        InventoryQuery = new Game.Remote.InventoryQueryHandler(RemoteCommands, Inventory, GroundItems);

        // Write-side inventory / cash actions — @get-all / @drop-all /
        // @deposit-all / @share emit get / drop / dep / with / give on the wire.
        // Keep-on-hand floors come from the per-character Cash settings;
        // wire-sender bound in MainWindowVM.
        InventoryAction = new Game.Remote.InventoryActionHandler(
            RemoteCommands,
            Inventory,
            GroundItems,
            PartyState,
            readCash: () => ReadSection<Models.Profile.CashSettings>(Profile.Current, "Cash"));

        // Receive side of @heal — a configured party-healer polls `par` on
        // request so CastingDirector re-evaluates its party-heal thresholds
        // against fresh member HP. Emit side is the follower flee-substitute
        // wired into Health.SetPartyRoleSync above. Wire-sender bound in
        // MainWindowVM.
        Heal = new Game.Remote.HealCommandHandler(
            RemoteCommands,
            readParty: () => ReadSection<Models.Profile.PartySettings>(Profile.Current, "Party"));

        // Item-cast buffs. A Bless slot may hold a #-token naming an
        // unlimited-use cast item (surfaced in the Spell Book); the director
        // fires it by wielding + using the item, then re-wielding the displaced
        // weapon (read from Inventory's last `i` dump). Duration drives the
        // recast clock. Wire-sender bound in MainWindowViewModel.
        ItemCast = new Game.Spells.ItemCastSequencer(
            () => Spellbook.GetCastItems(), () => Inventory.Snapshot, Log);
        CastDirector.SetItemCastSource(ItemCastDurationOf, ItemCast.Execute);
        CastDirector.SetItemCastManaCost(ItemCastManaCostOf);

        // Auto-train. Drives the `train stats` screen to apply the CP
        // plan (Workshop CP Allocation tab) when armed + a level-up enables it.
        // Needs Inventory (raw-base = live - gear) + TrainerMenu (screen enter/
        // exit gating, already wired to char-mode). Wire-sender bound in
        // MainWindowViewModel.
        AutoTrain = new Game.AutoTrainManager(PlayerStats, GameData, Inventory, Profile, TrainerMenu, Log);

        // EquipmentManager + the @equip-<set> handler. The engine
        // reads saved gear sets off the char profile, diffs against Inventory's
        // worn loadout, and paces `wear` commands; virtual slots (Alternate
        // Weapon / Off-Hand) persist into the char-tier Combat section so the
        // combat weapon-swap matrix re-reads them. Wire-sender bound in
        // MainWindowViewModel.
        Equipment = new Game.Inventory.EquipmentManager(
            readEquipment: () => Profile.Current?.Equipment ?? new Models.Profile.EquipmentSettings(),
            getSnapshot: () => Inventory.Snapshot,
            readCombat: () => ReadSection<Models.Profile.CombatSettings>(Profile.Current, "Combat"),
            writeCombat: combat =>
            {
                if (Profile.Current is not { } p) return;
                p.Settings ??= new();
                p.Settings["Combat"] = System.Text.Json.JsonSerializer.SerializeToElement(combat);
                Profile.Save();
            },
            isTwoHanded: IsConfiguredWeaponTwoHanded,
            resolveItemSlot: ResolveEquipItemSlot,
            canEquipItem: CanCharacterEquipItem,
            log: Log);
        EquipRemote = new Game.Remote.EquipHandler(RemoteCommands, Equipment);

        // EquipmentManager is the sole gear actuator: the combat engine decides
        // which weapon it wants and hands the act off here. The backstab-set
        // armor (deltas only, synchronous) and the weapon swap both fire from the
        // pre-move sequence, before the sn — equipping breaks sneak.
        Combat.SetWeaponActuator(Equipment.SwapWeapon, () => Equipment.ApplyBackstabArmor());

        // CashManager. Subscribes to cash-on-ground
        // / cash-picked-up / cash-dropped patterns and dispatches
        // per-currency policy. AutoGetCash gates the whole engine
        // (Settings -> General toggle + toolbar Toggle command).
        Cash = new Game.Cash.CashManager(Router,
            readSettings: () => ReadSection<Models.Profile.CashSettings>(Profile.Current, "Cash"),
            isEnabled: () => ReadAutoModeFlag(d => d.AutoGetCash),
            getSnapshot: () => Inventory.Snapshot,
            isPeekSuppressed: () => RoomTracker.IsPeekSuppressed(),
            log: Log);
        // Reset held tallies on profile swap — prior character's
        // counts aren't relevant to the new one.
        Profile.ProfileLoaded += _ => Cash.ResetTallies();
        Cash.SetAcquisitionGate(Acquisition);
        // Feed confirmed coin pickups into the Session Stats
        // currency-collected tally, converting each denomination to its copper
        // value so mixed currency streams fold into one figure.
        Cash.CoinCollected += (currency, count) =>
            SessionActivity.NoteCurrencyCollected(
                Game.Inventory.CurrencyHoldings.ToCopper(currency, count));
        // The auto-deposit gates read the authoritative inventory snapshot
        // (wealth value + coin count), so re-evaluate whenever the parser
        // updates holdings — this is the only path that catches buy / sell
        // wealth swings (CashManager's own patterns see get / drop only).
        Inventory.Changed += Cash.OnInventoryChanged;

        // StashRoomManager. NOT autonomous:
        // AutoDepositManager (built below) drives ExecuteStash on arrival
        // at a stash destination during an auto-deposit reroute, so a
        // manual walk through a stash room never triggers a hide. Shares
        // AutoGetCash gating with CashManager (cash automation is one
        // mental toggle).
        Stash = new Game.Cash.StashRoomManager(Profile,
            readCash: () => ReadSection<Models.Profile.CashSettings>(Profile.Current, "Cash"),
            getSnapshot: () => Inventory.Snapshot,
            resolveAutoStashItem: ResolveAutoStashItem,
            isEnabled: () => ReadAutoModeFlag(d => d.AutoGetCash),
            log: Log);
        // Count stash-room hides toward the Session Stats
        // stashed/deposited figure (copper value across the dispatched coins).
        // Also record the hide (coins + items) in the transaction
        // ledger.
        Stash.StashExecuted += dispatch =>
        {
            long copper = 0;
            foreach ((string currency, long amount) in dispatch.Currencies)
                copper += Game.Inventory.CurrencyHoldings.ToCopper(currency, amount);
            SessionActivity.NoteCurrencyStashed(copper);
            TransactionHistory.NoteStash(dispatch.Currencies, dispatch.Items);
        };

        // AutoGetItemsManager. The resolve delegate
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
            isPeekSuppressed: () => RoomTracker.IsPeekSuppressed(),
            log: Log);
        AutoGetItems.SetAcquisitionGate(Acquisition);
        // Combat-finished flush: every room-entity observation re-checks
        // the deferred queue (CombatStateTracker's handler ran first, so
        // the hostile flag is current).
        RoomClassifier.EntitiesObserved += _ => AutoGetItems.OnRoomObserved();
        // Settings → Talk auto-greet. Self name resolves through the
        // PartyManager's LocalCharacterName first (set on connect), then
        // the loaded profile name as a fallback. Wire-sender bound by
        // MainWindowViewModel after telnet connects.
        Greet = new Game.GreetManager(RoomClassifier, Players, Party.State,
            selfNameProvider: () => Party.LocalCharacterName ?? Profile.Current?.Name);
        // Demand-driven auto-search (PR B). Posts a PathItem need when the
        // walker plans a route through an Item/Ticket exit whose item we
        // don't carry; resolves it when the item enters inventory. The
        // enabled gate reads Settings → Other live through the resolver so a
        // toggle takes effect without a profile reload. Walker's announce
        // seam is bound after the walker is built (below).
        PathItemDemand = new Game.Map.PathItemDemandTracker(
            Needs,
            carriedCount: CountItemCarried,
            inventoryLoaded: () => Inventory.IsLoaded,
            isEnabled: () =>
                Resolver.Resolve<Models.Profile.OtherSettings>("Other").SearchRoomsIfItemNeeded,
            log: Log);
        Inventory.Changed += PathItemDemand.OnInventoryChanged;

        // Party-inventory awareness (PR E). The probe broadcasts @have and
        // aggregates the party's replies; the gate sits ahead of the demand
        // tracker on the walker's announce seam. When "defer to party
        // inventory" is on and we're grouped, a needed per-member item we lack
        // is probed first — if a member has a spare it's handed over (give)
        // and no need is posted; a shortfall forwards to PathItemDemand so
        // search / shop / hunt still cover it. Solo / feature-off passes the
        // announced list straight through. The probe self-subscribes to
        // ChatRouter for replies; the give hand-off's wire-sender is bound by
        // MainWindowViewModel after connect.
        PartyInventory = new Game.Remote.PartyInventoryProbe(PartyBroadcaster, Chat, PartyState, Log);
        PartyPathItemGate = new Game.Map.PartyPathItemGate(
            isCarried: IsItemCarried,
            selfCount: CountItemCarried,
            query: (id, name) => PartyInventory.QueryAsync(id, name),
            itemName: ItemNames.GetName,
            isEnabled: () =>
                Resolver.Resolve<Models.Profile.OtherSettings>("Other").DeferToPartyInventory,
            searchEnabled: () =>
                Resolver.Resolve<Models.Profile.OtherSettings>("Other").SearchRoomsIfItemNeeded,
            inParty: () => PartyState.IsInParty,
            selfIsLeader: () => PartyState.SelfIsLeader,
            selfGivenName: () => GivenNameOf(Party.LocalCharacterName ?? Profile.Current?.Name),
            forward: PathItemDemand.OnPathItemsRequired,
            post: action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            log: Log);
        // The leader coordinates redistribution once acquisition makes the
        // party whole — re-check on every inventory change.
        Inventory.Changed += PartyPathItemGate.OnInventoryChanged;

        // Party-level probe + tracker. The probe broadcasts @level and
        // persists each reply into the players table (RecordLevel), so the
        // exact level supersedes the title band. The tracker fires it on
        // roster change and exposes the party's most-constraining level
        // window; MovementFilter reads that window to route a following
        // party around gates a member can't clear. Both gated by the
        // "avoid party-impassable level gates" toggle.
        PartyLevelProbe = new Game.Remote.PartyLevelProbe(
            PartyBroadcaster, Chat, PartyState,
            recordLevel: (given, level) => Players.RecordLevel(given, level, DateTime.UtcNow),
            log: Log);
        PartyLevel = new Game.Remote.PartyLevelTracker(
            PartyState, PartyLevelProbe, Players,
            selfLevel: () => Stats.HasParsed ? PlayerStats.Level : (int?)null,
            isEnabled: () =>
                Resolver.Resolve<Models.Profile.OtherSettings>("Other").AvoidPartyImpassableLevelGates,
            log: Log);
        Movement.PartyLevelBoundsProvider = PartyLevel.Bounds;

        // Party-wealth probe + tracker. Unlike level, wealth isn't kept warm —
        // it drifts with loot / spend — so the tracker probes @wealth only when
        // BFS actually evaluates a toll exit (MinWealth is the demand trigger),
        // records each reply, and exposes the party's minimum wallet;
        // MovementFilter reads that to route a following party around a toll a
        // member can't afford. The probe forwards replies straight to the
        // tracker (not the players table). Always on — a toll is per-crosser, so
        // stranding a member at a gate is never wanted. The recordWealth closure
        // reads the PartyWealth property lazily, so the construction order is fine.
        PartyWealthProbe = new Game.Remote.PartyWealthProbe(
            PartyBroadcaster, Chat, PartyState,
            recordWealth: (given, copper) => PartyWealth.Record(given, copper),
            log: Log);
        PartyWealth = new Game.Remote.PartyWealthTracker(
            PartyState, PartyWealthProbe,
            selfWealth: () =>
                Inventory.IsLoaded ? Inventory.Snapshot.Currency.TotalCopperValue : (long?)null,
            post: action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            log: Log);
        Movement.PartyWealthProvider = PartyWealth.MinWealth;
        Movement.TollWealthProbe = PartyWealth.Probe;

        // Base auto-search — a bare `sea` on each genuine room entry reveals
        // hidden items for the auto-get engines. Armed by the persisted
        // master toggle OR the transient path-item demand gate above.
        // Wire-sender bound by MainWindowViewModel after connect.
        AutoSearch = new Game.Map.AutoSearchManager(
            isEnabled: () => ReadAutoModeFlag(d => d.AutoSearch),
            isDemandActive: () =>
                PathItemDemand.SearchDemandActive || PartyPathItemGate.SearchDemandActive,
            log: Log);

        // Drop the stale queue / ground snapshot when we actually change rooms.
        RoomTracker.StateChanged += t =>
        {
            if (t.NewRoom is null) return;
            if (t.PreviousRoom is not null
             && t.PreviousRoom.Key.Equals(t.NewRoom.Key)) return;
            AutoSearch.OnRoomChanged();
            AutoGetItems.OnRoomChanged();
            GroundItems.OnRoomChanged();
        };

        Walker = new Game.Map.AutoWalkManager(RoomGraph, Bfs, RoomTracker,
            MovementCoordinator, filter: Movement, log: Log,
            promptScanner: PromptScanner, recovery: Recovery);
        // DeathRecoveryManager's Walk-to-Room / Recover-Now actions route
        // through the walker — attached here since the walker is built
        // after the manager.
        DeathRecovery.AttachWalker(Walker);
        // Route walker over trapped exits through
        // the TrapDisarmManager.
        Walker.SetTrapEnqueuer(TrapDisarm.Enqueue);
        // Settings → Other "Utilize disarm traps if able": gate the
        // walker's trap-disarm on the toggle AND a real local capability
        // (Traps skill). When the gate is false the walker steps through
        // trapped exits without a disarm. The party-delegation half of
        // "if able" lands in a follow-up.
        Walker.SetTrapDisarmGate(() =>
            Resolver.Resolve<Models.Profile.OtherSettings>("Other").UtilizeDisarmTrapsIfAble
            && TrapDisarm.CanDisarm);
        // Party-delegation half of "if able": same toggle, but the LOCAL
        // character can't disarm AND a capable party member can. The
        // walker tries the local gate first, then this; the delegation
        // manager broadcasts @trap on say and resumes on the member's
        // say reply (a signal source kept distinct from the self path).
        Walker.SetTrapDelegator(TrapDelegation.Delegate);
        Walker.SetTrapDelegateGate(() =>
            Resolver.Resolve<Models.Profile.OtherSettings>("Other").UtilizeDisarmTrapsIfAble
            && !TrapDisarm.CanDisarm
            && TrapDelegation.AnyPartyMemberCanDisarm());
        Walker.SetTrapDelegateStopper(TrapDelegation.Cancel);
        // Proactive pre-move approach sequence: gear then `sn`, both as the last
        // commands before each walker move so the move itself is sneaked (the
        // reactive RoomTracker hook above only re-sneaks AFTER arriving).
        // Backstab gear goes out FIRST — equipping breaks sneak, so the loadout
        // must land before the sn (weapon → armor → sn → move). PrepBackstabForMove
        // no-ops unless backstab is enabled. Non-blocking; the settled-state
        // guard in StealthManager prevents a double sn when both paths fire.
        Walker.SetPreMoveHook(() =>
        {
            Combat.PrepBackstabForMove();
            Stealth.RequestPreMoveStealth();
        });
        // PR B — announce the route's possession-gated item ids at walk-start
        // so the demand tracker arms auto-search for anything we lack. PR E
        // interposes the party-inventory gate ahead of the tracker: it forwards
        // anything the party can't cover to PathItemDemand.OnPathItemsRequired,
        // so with "defer to party inventory" off (or solo) the behaviour is
        // unchanged.
        Walker.SetPathItemAnnouncer(PartyPathItemGate.OnPathItemsRequired);

        // Active auto-light engine — announced the same planned route as the
        // item gate above. It scans for the darkest room and readies a covering
        // carried light before we walk into the dark. `wornIllu` is the worn-only
        // baseline (the readied light it may swap out is excluded) so a light it
        // picks is measured on its own strength. Gated by the AutoLight toggle;
        // its wire-sender is bound by MainWindowViewModel after connect.
        AutoLightProvisioner = new Game.Light.AutoLightProvisioner(
            isEnabled:   () => ReadAutoModeFlag(d => d.AutoLight),
            snapshot:    () => Inventory.Snapshot,
            catalogue:   () => Lights.All,
            resolveRoom: RoomGraph.GetRoom,
            wornIllu:    () => PlayerIllumination.WornOnly,
            settings:    () => ReadSection<Models.Profile.AutoLightSettings>(Profile.Current, "AutoLight"),
            log:         Log);
        Walker.SetRouteAnnouncer(AutoLightProvisioner.OnRoutePlanned);

        // Auto-light provisioning detour. When the provisioner's planner returns
        // Buy (route dark, nothing carried covers), detour to the fewest-added-
        // steps shop that stocks the light, buy the carry batch, and resume — the
        // provisioner's ready path lights it on the resumed announcement. Reuses
        // the same shop-lookup / distance / carried-count seams as
        // PathItemShopRouter, but gated ENTIRELY by the AutoLight master toggle
        // (no separate opt-in — a player who doesn't want light bought leaves
        // AutoLight off). engineWalkActive suppresses the detour during a loop /
        // lair run. Wire-sender bound by MainWindowViewModel after connect.
        AutoLightShopRouter = new Game.Light.AutoLightShopRouter(
            shopRoomsSellingItem: ShopRoomsSellingItem,
            currentRoom: () => RoomTracker.State.CurrentRoom?.Key,
            walkDestination: () => Walker.Destination,
            distanceBetween: (a, b) => Bfs.DistanceBetween(a, b, Movement),
            carriedCount: CountItemCarried,
            isEnabled: () => ReadAutoModeFlag(d => d.AutoLight),
            engineWalkActive: () =>
                AutoLair.IsActive || LoopRunner.State != Game.Map.LoopState.Idle,
            walkTo: key => Walker.WalkTo(key),
            post: action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            log: Log);
        AutoLightProvisioner.SetProvisioner(AutoLightShopRouter.OnBuyRequested);
        Walker.Event += AutoLightShopRouter.OnWalkEvent;
        Inventory.Changed += AutoLightShopRouter.OnInventoryChanged;
        // Reorder poll: an `i` dump is the only moment the readied light's charge
        // refreshes, so the provisioner catches a dwindling supply here and hands
        // a restock to the same shop-detour router (once per readied instance).
        Inventory.Changed += AutoLightProvisioner.OnInventoryChanged;

        // Auto-equip trigger coordinator. Reads the same live
        // Equipment blob as the apply engine and the HealthManager's recovery gates
        // (to tell an HP rest from a mana rest), and subscribes to PlayerState
        // (position / combat) for the pre-rest and default trigger moments.
        // App-lifetime subscriber to app-lifetime singletons, so it isn't
        // disposed/re-created on profile swap.
        AutoEquip = new Game.Inventory.AutoEquipCoordinator(
            PlayerState,
            readEquipment: () => Profile.Current?.Equipment ?? new Models.Profile.EquipmentSettings(),
            hpGateAsserted: () => Health.HpGateAsserted,
            maGateAsserted: () => Health.MaGateAsserted,
            applyBySetId: Equipment.ApplyBySetId,
            // Gate auto-fire on a known worn loadout — the engine can't diff a set
            // against an inventory it hasn't parsed yet without emitting redundant
            // wears for gear already worn.
            wornLoadoutKnown: () => Inventory.IsLoaded,
            log: Log);

        // Per-game-data-set loop catalogue. Loops live
        // under the active set's Loops/ folder, so the catalogue reloads
        // whenever the active set changes (wired below, alongside lairs,
        // since the two share one on-disk tree).
        Loops = new Game.Map.LoopManager(Bfs, RoomGraph, Log);

        // MegaMUD .mp loop importer. Pure resolution
        // service over the active graph; no per-profile state of its
        // own. The Manage dialog calls it on user "Import .mp".
        MpImporter = new Game.Map.MpFile.MpFileImporter(RoomGraph, Log);

        // Auto-Lair setup catalogue (per-set, mirrors
        // LoopManager) + game-data-driven respawn timer resolver +
        // in-session arrival tracker.
        Lairs = new Game.Map.LairManager(Log);
        LairTimers = new Game.Map.LairTimerStore(GameData, RoomGraph, RoomTracker, Log);

        // Loops + lairs are per-game-data-set and share one on-disk tree,
        // so they reload together on every active-set change. Mirrors the
        // other per-set subsystems above: hook ActiveSetChanged, then
        // prime from the current set. ApplyActiveGameDataSet re-derives the
        // active set on every profile load / BBS pin / mutate / close, so
        // this one hook covers every reload case the old per-BBS wiring did.
        GameData.ActiveSetChanged += setName =>
        {
            Loops.LoadAll(setName);
            Lairs.LoadAll(setName);
        };
        if (GameData.ActiveSet is not null)
        {
            Loops.LoadAll(GameData.ActiveSet);
            Lairs.LoadAll(GameData.ActiveSet);
        }

        // Shared folder CRUD over the Loops directory (loops + lairs
        // live in the same on-disk tree). Owns the filesystem move once
        // and reloads both managers, instead of either racing the dir.
        NavFolders = new Game.Map.NavFolderManager(Loops, Lairs, Log);

        // Game Data → "Manage Sets…" backend. The reload callback re-pulls
        // the active set's loop/lair caches after a copy/move touches it;
        // the delete callback clears any profile / global reference that
        // still names a just-deleted set.
        GameDataSetManager = new GameDataSetManager(
            GameData,
            reloadActiveLibrary: () =>
            {
                Loops.LoadAll(GameData.ActiveSet);
                Lairs.LoadAll(GameData.ActiveSet);
            },
            onSetDeleted: ClearGameDataSetReferences,
            Log);

        // Encumbrance parser writes
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
        Profile.ProfileLoaded += _ => RoomBlacklist.OnBbsPinApplied(ResolveActiveBbs()?.Name);
        Profile.BbsPinApplied += _ => RoomBlacklist.OnBbsPinApplied(ResolveActiveBbs()?.Name);
        // BFS consults the blacklist to skip placement of hidden
        // rooms (edge still recorded → dangling stub). Cache flushes
        // on every blacklist change so the next layout build picks
        // up the new filter.
        Bfs.ConfigureBlacklist(RoomBlacklist.IsBlacklisted);
        // Rooms flagged CannotBeReached are dropped from the tracker's
        // position-candidate resolution so a login / silent-desync
        // observation can never land the player in a dev / orphan room.
        // The predicate reads the store live, so no reindex is needed
        // when the flag set changes — but re-invoke on Changed anyway to
        // keep the wiring symmetric and future-proof against a cached
        // predicate.
        RoomGraph.ConfigureUnreachable(RoomBlacklist.IsUnreachable);
        RoomBlacklist.Changed += () => Bfs.InvalidateCache();

        // Loop execution engine. MainWindowViewModel
        // binds the wire-sender once telnet is up (same pattern as
        // the walker). RoomGraph passed in so the runner can resolve
        // MoveLoopStep sequences into room-key polylines for the map
        // overlay.
        LoopRunner = new Game.Map.LoopRunner(RoomTracker, MovementCoordinator,
            PromptScanner, Log, RoomGraph, Recovery, Bfs, Walker, Movement);
        // Same proactive pre-move approach sequence for loop circuits — backstab
        // gear before the sneak (equipping breaks sneak), then the move.
        LoopRunner.SetPreMoveHook(() =>
        {
            Combat.PrepBackstabForMove();
            Stealth.RequestPreMoveStealth();
        });
        // Avoid-list mutation mid-loop → LoopRunner re-routes via a
        // Stop+Start cycle so the new filter applies on the next BFS.
        Movement.AvoidedChanged += () => LoopRunner.NotifyAvoidedChanged();

        // Invite-as-wait-signal — AutoPartyManager holds the loop (via the
        // PartyInvite gate) while waiting for an auto-invited player to join,
        // and uninvites + resumes if they miss the wait window. Wired here
        // because both the coordinator and loop engine now exist (AutoParty
        // is constructed earlier, before the movement layer).
        AutoParty.SetMovementGate(MovementCoordinator,
            () => LoopRunner.State != Game.Map.LoopState.Idle);

        // Deterministic Auto-Lair scheduler — picks the next marked
        // lair to enter based on respawn timers + travel cost, parks
        // at a wait-room one hop short, then steps in on the tick.
        AutoLair = new Game.Map.AutoLairManager(
            Walker, RoomTracker, RoomGraph, Bfs, LairTimers, Log, MovementCoordinator);

        // Always-alive control surface over the three movement engines.
        // Backs the toolbar Start / Pause / Stop buttons (which outlive
        // the window-scoped NavigationViewModel) and stays in sync with
        // the Nav window because both act on the same engine primitives.
        MovementControl = new Game.Map.MovementController(
            Walker, LoopRunner, AutoLair, MovementCoordinator);

        // Death engine-quiescence. On our death RoomTracker fires
        // PlayerDeathObserved (both death phrasings). PlayerDeathHalt applies the
        // UserGate pause, but a loop caught mid-recovery — a miracle-save restores
        // HP, which clears the HealthRecovery gate and fires the loop's
        // ResumeAfterRecovery just before the death registers — sits in the
        // Recovering state that the pause doesn't cover, so the graveyard's
        // respawn-room confirm would drive a recovery-reroute straight back out.
        // Stop the engines outright: the reset clears that recovery state so no
        // reroute can fire. Also wipe the classifier's room view so a hostile from
        // the room we died in doesn't linger as a stale target the combat engine
        // re-attacks when a party member later walks into the graveyard.
        RoomTracker.PlayerDeathObserved += () =>
        {
            LoopRunner.Stop("player died — halting in graveyard");
            Walker.Stop("player died — halting in graveyard");
            AutoLair.Stop("player died — halting in graveyard");
            RoomClassifier.NoteRoomChanged();
        };

        // Party-death roster-cleanup bridge. Leader-side: when an active party
        // member dies mid-route it lingers as an [Invited] par slot; we uninvite
        // that phantom once combat clears so the loop / walk-to doesn't stall on
        // the PartyInviteGate. Gated on a movement engine actually running so
        // hands-on party management is left to the user.
        PartyDeathCleanup = new Game.PartyDeathRosterCleanup(
            Router, PartyState, Party, MovementCoordinator,
            isMovementActive: () => MovementControl.IsActive, log: Log);

        // Shared room-search resolver — backs the Nav rail search
        // box AND the @goto handler. Subscribes to ActiveSetChanged
        // + GraphReloaded internally so callers don't need to wire
        // cache invalidation.
        RoomSearch = new RoomSearchService(
            RoomGraph, GameData, Bfs, RoomBlacklist, Movement, Log);

        // MovePlayer remote-command handler.
        // Registers @goto, @loop, @lair, @stop, @rego against the
        // RemoteCommandManager. Dispatch routes to the now-existing
        // Walker / LoopRunner / AutoLairManager. The Catalog permission
        // gate ensures only players the user has granted MovePlayer
        // can issue these.
        MoveRemote = new Game.Remote.MovePlayerHandler(
            RemoteCommands, RoomSearch, RoomGraph, RoomTracker, Walker, Loops, LoopRunner,
            Lairs, AutoLair, MovementCoordinator);

        // Leader-side @comeback. Snapshots the running movement
        // engine, stops it (stop-and-restart, NOT a coordinator gate —
        // a gate would block the recovery walk itself), walks to recover
        // the stranded follower (explicit room or backtrack along the
        // just-walked RoomTracker trail), re-invites + awaits follow,
        // then resumes the captured engine. MaxBacktrackRooms is pushed
        // from Settings → Other by ApplyOtherFromActiveProfile on load.
        PartyComeback = new Game.Remote.PartyComebackManager(
            RemoteCommands, Party, RoomTracker, RoomClassifier, Walker, LoopRunner, AutoLair, Router, Bfs, Log);

        // Auto-deposit reroute. Built here
        // (after the movement engines) so it can snapshot / stop / restart
        // the running Loop or Auto-Lair when CashManager's gate crosses.
        // Stop-and-restart, NOT a coordinator gate — a gate would block the
        // detour walk itself (same reasoning as PartyComebackManager). The
        // wire sender for the bank `dep` is bound by MainWindowViewModel
        // after telnet connects, alongside the Cash / Stash senders.
        // Trainer-walk coordinator. Built here (after the movement
        // engines) so it can snapshot / stop / restart the running Loop or
        // Auto-Lair for a train detour, same as AutoDeposit. Manual Train Now
        // (CP tab) + the armed auto-train (live-exp threshold during a loop)
        // both route through it. Wire-sender bound in MainWindowViewModel.
        TrainerWalk = new Game.TrainerWalkManager(PlayerStats, Stats, GameData, Profile,
            RoomTracker, Bfs, Walker, LoopRunner, AutoLair, AutoTrain, Router, Log);
        // @train remote: trains in place (no walk) via the coordinator.
        TrainRemote = new Game.Remote.TrainHandler(RemoteCommands, TrainerWalk);
        // Level-up announcer. Built after StatParser + the ProfileLoaded
        // Hydrate wiring so its baseline seed sees freshly-hydrated stats; watches
        // StatParser.ExperienceGained to broadcast newly-trainable levels.
        LevelUp = new Game.LevelUpAnnouncer(PlayerStats, Stats, GameData, Profile, Log);

        AutoDeposit = new Game.Cash.AutoDepositManager(
            Cash,
            readCash: () => ReadSection<Models.Profile.CashSettings>(Profile.Current, "Cash"),
            getSnapshot: () => Inventory.Snapshot,
            isBankRoom: key => Game.GameData.BankCatalog.IsBankRoom(GameData, key),
            profile: Profile,
            tracker: RoomTracker,
            walker: Walker,
            loopRunner: LoopRunner,
            autoLair: AutoLair,
            stash: Stash,
            log: Log);
        // Bank deposits (already a copper value) join stash hides in
        // the Session Stats stashed/deposited figure, and record the
        // deposit in the transaction ledger.
        AutoDeposit.Deposited += copper =>
        {
            SessionActivity.NoteCurrencyStashed(copper);
            TransactionHistory.NoteBankDeposit(copper);
        };

        // Shop-source routing (PR C). On a one-shot walk-to that needs an
        // uncarried Item/Ticket-gate item a shop sells, detour to the
        // fewest-added-steps shop, buy it, and resume — gated by Settings →
        // Other "buy item if needed". Distances use the same movement filter
        // the walker routes with so the estimate matches the real walk; the
        // shop lookup joins ShopStock (who sells it) against the live graph
        // (which rooms host those shops). engineWalkActive suppresses the
        // detour while a loop / auto-lair run drives movement. WalkTo is
        // deferred through the dispatcher because the triggering NeedPosted
        // fires synchronously inside the walker's WalkTo. Wire-sender bound
        // by MainWindowViewModel after connect.
        PathItemShopRouter = new Game.Map.PathItemShopRouter(
            shopRoomsSellingItem: ShopRoomsSellingItem,
            currentRoom: () => RoomTracker.State.CurrentRoom?.Key,
            walkDestination: () => Walker.Destination,
            distanceBetween: (a, b) => Bfs.DistanceBetween(a, b, Movement),
            carriedCount: CountItemCarried,
            itemName: ItemNames.GetName,
            isEnabled: () =>
                Resolver.Resolve<Models.Profile.OtherSettings>("Other").BuyNeededPathItems,
            engineWalkActive: () =>
                AutoLair.IsActive || LoopRunner.State != Game.Map.LoopState.Idle,
            walkTo: key => Walker.WalkTo(key),
            post: action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            log: Log);
        Needs.NeedPosted += PathItemShopRouter.OnNeedPosted;
        Walker.Event += PathItemShopRouter.OnWalkEvent;
        Inventory.Changed += PathItemShopRouter.OnInventoryChanged;

        // Monster-drop reroute (PR D). The no-shop counterpart to the shop
        // router: when a walk-to needs an uncarried Item/Ticket-gate item no
        // shop sells but a monster drops, prompt (ConfirmService) to reroute
        // to the nearest room that monster spawns in, then resume once the
        // drop lands — gated by Settings → Other "hunt item if needed". The
        // two routers are mutually exclusive via anyShopSells: this one acts
        // only when no shop stocks the item. Nearest spawn is chosen with a
        // single forward BFS (ComputeDistancesFrom) since a common monster
        // spawns in hundreds of rooms; dropSpawnsForItem flattens the index's
        // droppers × their spawn rooms lazily, only for the needed item.
        MonsterDropRouter = new Game.Map.MonsterDropRouter(
            dropSpawnsForItem: DropSpawnsForItem,
            anyShopSells: ShopStock.AnyShopSells,
            currentRoom: () => RoomTracker.State.CurrentRoom?.Key,
            walkDestination: () => Walker.Destination,
            distancesFrom: src => Bfs.ComputeDistancesFrom(src, Movement),
            isCarried: IsItemCarried,
            itemName: ItemNames.GetName,
            isEnabled: () =>
                Resolver.Resolve<Models.Profile.OtherSettings>("Other").HuntNeededPathItems,
            engineWalkActive: () =>
                AutoLair.IsActive || LoopRunner.State != Game.Map.LoopState.Idle,
            confirm: (title, body) => Confirm.ConfirmAsync(title, body, "Reroute"),
            walkTo: key => Walker.WalkTo(key),
            post: action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            log: Log);
        Needs.NeedPosted += MonsterDropRouter.OnNeedPosted;
        Walker.Event += MonsterDropRouter.OnWalkEvent;
        Inventory.Changed += MonsterDropRouter.OnInventoryChanged;

        // Follower-side @comeback. Watches for a movement-failure
        // line (prevents-movement flag / over-encumbered) immediately
        // before "You are no longer following X." — the signature of being
        // left behind — and telepaths @comeback to the leader. Enabled is
        // pushed from Settings → Other by ApplyOtherFromActiveProfile.
        ComebackRequest = new Game.Remote.ComebackRequester(Router, RoomTracker, Log);

        // Follower-side reconnect auto-rejoin. Mirrors live follower membership
        // into the profile (crash-survivable) and, on the first in-game room
        // after a reconnect, telepaths @comeback + @invite to re-form the party.
        // Gated by the Auto-All kill switch like MainMenuEntry — a manual-play
        // character that silenced automation won't auto-rejoin.
        PartyRejoin = new Game.Remote.PartyRejoinCoordinator(
            Router, PartyState, RoomTracker,
            isAutoEnabled: () => !AutoModeController.KillSwitchEngaged,
            log: Log);
        // Write-through: whenever follower membership changes, stamp the loaded
        // profile and persist immediately so a crash at any moment retains the
        // right leader. Save() no-ops on a blank draft (nothing to write).
        PartyRejoin.PersistLeader = leader =>
        {
            if (Profile.Current is not { } current) return;
            if (string.Equals(current.PendingReconnectLeader, leader, StringComparison.Ordinal)) return;
            current.PendingReconnectLeader = leader;
            Profile.Save();
        };
        // Hydrate the crash-survivable memory on every profile load / swap.
        Profile.ProfileLoaded += p => PartyRejoin.HydrateRememberedLeader(p.PendingReconnectLeader);

        // Reconnect-recovery cross-wiring — done here (after PartyRejoin exists)
        // because these hooks bridge the leader-side comeback manager and the
        // follower-side rejoin memory:
        //   - A remembered leader's re-invite auto-follows even without a
        //     per-player "join if invited" grant (remembering we were in their
        //     party is the standing consent).
        //   - A @forget from a recently-partied member OR a remembered leader is
        //     authorised even though neither is a live party member any more.
        //   - When we receive @forget from a leader we remembered, clear the
        //     crash-rejoin memory so a later reconnect stops telepathing them.
        AutoParty.ForceAcceptFrom = PartyRejoin.IsRememberedLeader;
        RemoteCommands.ForgetEligibility = s =>
            Party.WasRecentlyPartied(s) || PartyRejoin.IsRememberedLeader(s);
        PartyComeback.ForgetLeaderCallback = PartyRejoin.ForgetRememberedLeader;

        // EventManager. Holds the loaded character's
        // scheduled / lifecycle events, dispatches actions into the
        // existing movement / command stack, and reconciles saved Loop /
        // AutoLair target references against their managers'
        // collections.
        Events = new Game.Events.EventManager(
            Profile, Loops, Lairs, LoopRunner, AutoLair, Walker, Log);

        // EventScheduler. Owns the AtTime ticker +
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

    // Guards the persist-on-Changed handler while we're pushing values INTO
    // LogDiagnostics from disk — otherwise applying the loaded state would
    // immediately write it straight back.
    private bool _suppressLogDiagnosticsPersist;

    private void ApplyLogDiagnosticsFromActiveProfile()
    {
        Models.Profile.LogDiagnosticsSettings dto =
            ReadSection<Models.Profile.LogDiagnosticsSettings>(Profile.Current, "LogDiagnostics");
        _suppressLogDiagnosticsPersist = true;
        LogDiagnostics.DebugDiagnostics  = dto.Debug;
        LogDiagnostics.CombatDiagnostics = dto.Combat;
        _suppressLogDiagnosticsPersist = false;
    }

    private void ResetLogDiagnosticsToDefaults()
    {
        _suppressLogDiagnosticsPersist = true;
        LogDiagnostics.DebugDiagnostics  = false;
        LogDiagnostics.CombatDiagnostics = false;
        _suppressLogDiagnosticsPersist = false;
    }

    private void PersistLogDiagnostics()
    {
        if (_suppressLogDiagnosticsPersist) return;
        // No loaded character → session-only value; nothing to persist to.
        if (Profile.Current is not { } profile) return;

        Models.Profile.LogDiagnosticsSettings dto = new()
        {
            Debug  = LogDiagnostics.DebugDiagnostics,
            Combat = LogDiagnostics.CombatDiagnostics,
        };
        profile.Settings ??= new();
        profile.Settings["LogDiagnostics"] = System.Text.Json.JsonSerializer.SerializeToElement(dto);
        Profile.Save();
    }

    // Generic per-section settings reader. Returns a fresh default-
    // constructed DTO when the profile is null, has no Settings dict,
    // is missing the named entry, or the JSON is malformed — the
    // callers all want a non-null DTO they can apply unconditionally.
    // Returns whichever of Walker / LoopRunner / AutoLair is currently
    // not Idle. Per design they're mutually exclusive (entering one
    // cleanly exits the other) so a simple first-non-idle scan is
    // sufficient. Returns null when the player is idle —
    // HealthManager treats that as "don't flee".
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

    // True when the named weapon resolves to a two-handed item in the active
    // game-data set (Items.WeaponType 2H). Fed to
    // Game.Combat.CombatManager so its weapon-swap can free the
    // off-hand before wielding a two-hander. An unknown / unmatched name
    // resolves to false — the swap then behaves as it always did.
    private bool IsConfiguredWeaponTwoHanded(string? weaponName)
    {
        if (string.IsNullOrWhiteSpace(weaponName)) return false;
        if (GameData.FindRowByName("Items", weaponName) is not { } row) return false;
        if (!row.TryGetProperty("WeaponType", out System.Text.Json.JsonElement wt)) return false;
        int code = wt.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number when wt.TryGetInt32(out int n) => n,
            System.Text.Json.JsonValueKind.String when int.TryParse(wt.GetString(), out int n) => n,
            _ => 0,
        };
        return Game.GameData.LookupEnums.IsTwoHandedWeaponType(code);
    }

    // Physical EquipmentSlot a carried item name fills, or null if the active
    // game-data set has no matching Items row / the item isn't wearable gear.
    // Fed to EquipmentManager's inventory-fallback planner so it can slot loose
    // carried gear into empty slots.
    private Models.Profile.EquipmentSlot? ResolveEquipItemSlot(string itemName)
    {
        if (GameData.FindRowByName("Items", itemName) is not System.Text.Json.JsonElement row)
            return null;
        return Game.Inventory.EquipmentSlotMap.SlotForItem(row);
    }

    // True when the live character can actually wear the named carried item —
    // gated by level / class / alignment against the active game-data set. Feeds
    // the inventory-fallback planner so it never queues gear the game would reject.
    // An unknown item (no Items row) resolves false: don't queue what we can't verify.
    private bool CanCharacterEquipItem(string itemName)
    {
        if (GameData.FindRowByName("Items", itemName) is not System.Text.Json.JsonElement row)
            return false;
        Game.Inventory.ClassEquipProfile cls =
            Game.Inventory.ItemEquipFilter.ResolveClassProfile(GameData, PlayerStats.Class);
        Game.Calculators.AlignmentBucket? bucket =
            Game.Inventory.ItemEquipFilter.BucketForWord(Players.Find(PlayerStats.Name)?.Alignment);
        return Game.Inventory.ItemEquipFilter.CanEquip(row, PlayerStats.Level, cls, bucket);
    }

    // Read a single boolean off the active profile's
    // Models.Profile.GeneralSettings.AutoMode. Used by
    // the engine isEnabled delegates so toggling Settings →
    // General → Auto-Combat (or the toolbar Toggle button) takes
    // effect immediately — no event subscription needed since each
    // engine queries on every tick / classifier emit.
    private bool ReadAutoModeFlag(Func<Models.Profile.AutoActionDefaults, bool> selector)
    {
        Models.Profile.GeneralSettings general =
            ReadSection<Models.Profile.GeneralSettings>(Profile.Current, "General");
        return selector(general.AutoMode);
    }

    // Live read of the master "Disable hangups" kill-switch from the
    // char-tier General section — the same store the toolbar toggle
    // writes. Wired into every automatic-hangup site (HangupHandler,
    // RelogHandler, CleanupLogout; HealthManager reads it through its own
    // General-settings provider) so flipping the toggle takes effect
    // without restarting an engine.
    private bool ReadDisableHangups() =>
        ReadSection<Models.Profile.GeneralSettings>(Profile.Current, "General").DisableHangups;

    // Buff-duration source: map a 4-letter cast code to the
    // buff's Models.GameData.MessageRecord.CasterMessage
    // confirmation template plus its computed effect duration in
    // seconds (Game.Spells.SpellCalculator.Duration rounds ×
    // Game.Spells.SpellCalculator.SpellRoundSeconds at the
    // live Game.Spells.SpellbookState.Level). Returns
    // null for an unknown code, a code with no game-data message
    // record, or a record with no caster line.
    // Item-cast recast clock: resolve a Bless-slot
    // Game.Spells.ItemCastToken to the cast item's spell effect
    // duration in seconds (Game.Spells.SpellCalculator.Duration
    // rounds × Game.Spells.SpellCalculator.SpellRoundSeconds
    // at the live Game.Spells.SpellbookState.Level). Returns
    // null when the token doesn't resolve to a class cast item or the
    // cast spell has no duration (i.e. it isn't a buff) — the director then
    // won't fire it.
    private long? ItemCastDurationOf(string token)
    {
        if (!Game.Spells.ItemCastToken.TryResolve(token, Spellbook.GetCastItems(),
                out Game.Spells.ClassCastItem item))
            return null;
        if (SpellCatalog.GetFormulaByNumber(item.SpellNumber) is not { } formula)
            return null;
        // Duration is in spell rounds — convert to wall-clock seconds for the
        // recast clock (CastingDirector treats the returned value as seconds).
        long rounds = Game.Spells.SpellCalculator.Duration(formula, Spellbook.Level);
        return rounds > 0 ? rounds * Game.Spells.SpellCalculator.SpellRoundSeconds : null;
    }

    // Mana the item-cast buff named by token draws on use —
    // the cast spell's Spells.ManaCost, surfaced on the resolved
    // Game.Spells.ClassCastItem. Drives the director's per-slot
    // buff affordability: a free item-cast (cost 0) recasts regardless of mana;
    // a paid one waits until the pool can cover it. Returns null when the
    // token doesn't resolve to a class cast item (treated as free / never gated).
    private int? ItemCastManaCostOf(string token)
        => Game.Spells.ItemCastToken.TryResolve(token, Spellbook.GetCastItems(),
                out Game.Spells.ClassCastItem item)
            ? item.ManaCost
            : null;

    private (string Caster, long DurationSec)? BuffInfoByShort(string castCode)
    {
        if (string.IsNullOrWhiteSpace(castCode)) return null;
        string target = castCode.Trim();
        foreach (Game.Spells.KnownSpell s in Spellbook.Available)
        {
            if (!string.Equals(s.Short.Trim(), target, StringComparison.OrdinalIgnoreCase)) continue;
            Models.GameData.MessageRecord? rec = FindSpellMessage(s.Number, s.Name);
            if (rec is null || string.IsNullOrWhiteSpace(rec.CasterMessage)) return null;
            // Duration is in spell rounds; the recast clock wants wall-clock seconds.
            long durSec = Game.Spells.SpellCalculator.Duration(s.Formula, Spellbook.Level)
                          * Game.Spells.SpellCalculator.SpellRoundSeconds;
            return (rec.CasterMessage, durSec);
        }
        return null;
    }

    // True when the buff with cast code castCode targets
    // the whole party at once. Resolved from the active set's
    // Spells.Targets scope code: 13 = Full Party Area, 10 = Divided
    // Party Area — both blanket the party in a single cast (verified against
    // 1.11p, where every party-wide buff / heal uses 13; 10 is the divided
    // variant). See Game.GameData.LookupEnums.FormatSpellTargets
    // for the full label table. Unknown / non-party scopes ⇒ single-target.
    private bool IsPartyWideBuff(string castCode)
    {
        if (string.IsNullOrWhiteSpace(castCode)) return false;
        string target = castCode.Trim();
        foreach (Game.Spells.KnownSpell s in Spellbook.Available)
            if (string.Equals(s.Short.Trim(), target, StringComparison.OrdinalIgnoreCase))
                return s.Targets is 10 or 13;
        return false;
    }

    // Build the cure-confirmation matchers
    // Game.Conditions.PartyAilmentTracker uses to clear a
    // member's ailment chip when OUR cure spell lands on them. Each
    // configured cure spell (poison / disease / blindness / holds) is resolved
    // via the live spellbook → its game-data
    // Models.GameData.MessageRecord.CasterMessage →
    // a Game.Spells.CasterMessageMatcher. Confusion has no
    // cure spell, so it's never listed. Re-read on every call so
    // re-configuring a cure spell takes effect immediately.
    private IReadOnlyList<Game.Conditions.CureCastMatcher> CureCastMatchers()
    {
        Models.Profile.SpellsSettings spells =
            ReadSection<Models.Profile.SpellsSettings>(Profile.Current, "Spells");
        List<Game.Conditions.CureCastMatcher> list = new(4);
        Add(spells.CurePoisonSpell,    Models.GameData.MessageFlags.Poisoned);
        Add(spells.CureDiseaseSpell,   Models.GameData.MessageFlags.Diseased);
        Add(spells.CureBlindnessSpell, Models.GameData.MessageFlags.Blinded);
        Add(spells.CureHoldsSpell,     Models.GameData.MessageFlags.MovementPrevented);
        return list;

        void Add(string? castCode, Models.GameData.MessageFlags ailment)
        {
            if (CureMatcherFor(castCode) is { } resolved)
                list.Add(new Game.Conditions.CureCastMatcher(
                    ailment, resolved.SpellName, resolved.Caster, resolved.Witness));
        }
    }

    // Whether the player has a cure spell configured (a non-blank cast code
    // in Models.Profile.SpellsSettings) for
    // ailment. The Game.Conditions.AilmentSyncEngine
    // say-announce gate consults this — if we can self-cure an ailment we
    // clear it silently rather than broadcasting .@poisoned /
    // .@held to the party. Confusion has no cure field, so it always
    // reports unconfigured.
    private bool HasCureConfigured(Models.GameData.MessageFlags ailment)
    {
        Models.Profile.SpellsSettings spells =
            ReadSection<Models.Profile.SpellsSettings>(Profile.Current, "Spells");
        string? code = ailment switch
        {
            Models.GameData.MessageFlags.Poisoned          => spells.CurePoisonSpell,
            Models.GameData.MessageFlags.Diseased          => spells.CureDiseaseSpell,
            Models.GameData.MessageFlags.Blinded           => spells.CureBlindnessSpell,
            Models.GameData.MessageFlags.MovementPrevented  => spells.CureHoldsSpell,
            _ => null,
        };
        return !string.IsNullOrWhiteSpace(code);
    }

    // Resolve a cure spell's cast code to its game-data name plus the
    // Game.Spells.CasterMessageMatchers built from the spell's
    // Models.GameData.MessageRecord.CasterMessage (OUR cast) and
    // Models.GameData.MessageRecord.WitnessMessage (another
    // member's cast we see in the room). The name is carried so the tracker
    // confirms the spell slot, not just the target. The witness matcher is
    // null when the record has no witness template. Returns null
    // when the code is blank, unknown to the spellbook, has no message record,
    // or the caster message has no string capture (nothing to confirm against).
    private (string SpellName, Game.Spells.CasterMessageMatcher Caster, Game.Spells.CasterMessageMatcher? Witness)?
        CureMatcherFor(string? castCode)
    {
        if (string.IsNullOrWhiteSpace(castCode)) return null;
        string target = castCode.Trim();
        foreach (Game.Spells.KnownSpell s in Spellbook.Available)
        {
            if (!string.Equals(s.Short.Trim(), target, StringComparison.OrdinalIgnoreCase)) continue;
            Models.GameData.MessageRecord? rec = FindSpellMessage(s.Number, s.Name);
            if (rec is null) return null;
            return Game.Spells.CasterMessageMatcher.TryCreate(rec.CasterMessage) is { } caster
                ? (s.Name, caster, Game.Spells.CasterMessageMatcher.TryCreate(rec.WitnessMessage))
                : null;
        }
        return null;
    }

    // Buff-duration source: map a fired AppliedMessage
    // Models.GameData.MessageRecord back to the buff's
    // 4-letter cast code so a confirmed self-buff starts / clears its
    // duration timer. Resolves via the record's Spells#N link
    // first, then falls back to a name match against the live spellbook.
    private string? ShortFromAppliedRecord(Models.GameData.MessageRecord record)
    {
        if (record.Links is not null)
            foreach (Models.GameData.GameDataLink link in record.Links)
            {
                if (!string.Equals(link.Table, "Spells", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (Game.Spells.KnownSpell s in Spellbook.Available)
                    if (s.Number == link.Number) return s.Short;
            }

        foreach (Game.Spells.KnownSpell s in Spellbook.Available)
            if (string.Equals(s.Name.Trim(), record.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                return s.Short;
        return null;
    }

    // ----- Mana-regen reroll glue ---------------------------------------
    // Raw engine wire-send used by the reroll engine for its abil query + the
    // deliberate cooldown-bypassing recast. Bound in the main VM alongside the
    // per-service SetWireSender calls; null until the first connect.
    private Action<byte[]>? _engineWireSend;

    // Bind the raw engine wire-sender the mana-regen reroll engine
    // uses to send abil 145 and its recast. Same
    // engineSend the per-service SetWireSender calls receive.
    public void SetEngineWireSender(Action<byte[]> send)
    {
        ArgumentNullException.ThrowIfNull(send);
        _engineWireSend = send;
    }

    // A self-buff of ours landed (confirmed via its AppliedMessage). On
    // Paradigm, if it's the configured mana-regen spell AND that spell is a
    // code-145 rolled affect (nature tap / mana flux, not a HoT like chaos
    // surge), hand it to the reroll engine to read abil 145 and reroll a
    // bad value. Stock has no abil breakdown, so it's a no-op there.
    private void OnSelfBuffLandedForReroll(string shortCode)
    {
        if (string.IsNullOrWhiteSpace(shortCode)) return;
        if (GameData.ActiveRealm != Game.RealmType.ParaMud) return;

        Models.Profile.SpellsSettings spells =
            ReadSection<Models.Profile.SpellsSettings>(Profile.Current, "Spells");
        string? maRegen = spells.MaRegenSpell?.Trim();
        if (string.IsNullOrEmpty(maRegen)) return;
        if (!string.Equals(maRegen, shortCode.Trim(), StringComparison.OrdinalIgnoreCase)) return;
        if (!IsManaRegenRollSpell(maRegen)) return;

        ManaRegen.OnRollSpellLanded(maRegen);
    }

    // True when the spell with cast code shortCode carries a
    // code-145 (mana-regen) ability whose AbilVal is 0 — the signature
    // of a rolled regen-rate modifier (nature tap / mana flux) whose
    // magnitude comes from the level-scaled Min/Max range. A fixed +N regen
    // buff (AbilVal = N) or a mana HoT (code 150 / 123, e.g. chaos surge) is
    // excluded — rerolling those is pointless / wrong.
    private bool IsManaRegenRollSpell(string shortCode)
        => Spellbook.FindByCastCode(shortCode) is { } s
           && Game.Spells.ManaRegenReroller.IsRollSpell(s.Formula);

    // Reroll affordability gate: would paying for one more recast of the
    // configured mana-regen spell drop mana below the buff floor
    // (Models.Profile.HealthSettings.BlessIfAboveMa percent of
    // max)? An unknown cost is treated as free. Returns false when the
    // pool is unknown or the recast would breach the floor.
    private bool CanAffordManaRegenReroll()
    {
        int maxMa = PlayerState.MaxMa;
        if (maxMa <= 0) return false;

        Models.Profile.SpellsSettings spells =
            ReadSection<Models.Profile.SpellsSettings>(Profile.Current, "Spells");
        string? shortCode = spells.MaRegenSpell?.Trim();
        if (string.IsNullOrEmpty(shortCode)) return false;

        int cost = Spellbook.ManaCostOf(shortCode) ?? 0;
        Models.Profile.HealthSettings health =
            ReadSection<Models.Profile.HealthSettings>(Profile.Current, "Health");
        int floor = (int)Math.Round(maxMa * (health.BlessIfAboveMa / 100.0));
        return PlayerState.Ma - cost >= floor;
    }

    // Find the active set's Models.GameData.MessageRecord
    // for a spell — by Spells#N link first, then by name. Returns
    // null when the catalogue has no record for the spell.
    private Models.GameData.MessageRecord? FindSpellMessage(int spellNumber, string spellName)
    {
        foreach (Models.GameData.MessageRecord m in Messages.Messages)
        {
            if (m.Links is null) continue;
            foreach (Models.GameData.GameDataLink link in m.Links)
                if (string.Equals(link.Table, "Spells", StringComparison.OrdinalIgnoreCase)
                    && link.Number == spellNumber)
                    return m;
        }

        string target = spellName.Trim();
        foreach (Models.GameData.MessageRecord m in Messages.Messages)
            if (string.Equals(m.Name.Trim(), target, StringComparison.OrdinalIgnoreCase))
                return m;
        return null;
    }

    // Find the active set's Models.GameData.MessageRecord for an
    // item — by Items#N link first, then by the item's resolved name.
    // An item-proc record's Models.GameData.MessageRecord.CasterMessage
    // is the line YOU see when the weapon procs. Returns null when no
    // record anchors to the item. Mirrors FindSpellMessage.
    private Models.GameData.MessageRecord? FindItemMessage(int itemNumber)
    {
        foreach (Models.GameData.MessageRecord m in Messages.Messages)
        {
            if (m.Links is null) continue;
            foreach (Models.GameData.GameDataLink link in m.Links)
                if (string.Equals(link.Table, "Items", StringComparison.OrdinalIgnoreCase)
                    && link.Number == itemNumber)
                    return m;
        }

        string? itemName = ItemNames.GetName(itemNumber);
        if (string.IsNullOrWhiteSpace(itemName)) return null;
        string target = itemName.Trim();
        foreach (Models.GameData.MessageRecord m in Messages.Messages)
            if (string.Equals(m.Name.Trim(), target, StringComparison.OrdinalIgnoreCase))
                return m;
        return null;
    }

    // Compile the Game.Spells.CasterMessageMatchers for the
    // player's configured attack spells (the Combat tab's Normal + Alternate
    // single-target damage slots) from each spell's game-data
    // Models.GameData.MessageRecord.CasterMessage. Feeds
    // CombatSession so a recognised cast tallies its own
    // damage row instead of being miscounted as a melee swing. Re-read on each
    // refresh so a slot change takes effect without a reconnect; a blank /
    // unknown / message-less slot contributes nothing.
    private IReadOnlyList<Game.Spells.CasterMessageMatcher> AttackSpellMatchers()
    {
        Models.Profile.CombatSettings combat =
            ReadSection<Models.Profile.CombatSettings>(Profile.Current, "Combat");
        List<Game.Spells.CasterMessageMatcher> list = new(2);
        Add(combat.NormalAttackSpell?.SpellName);
        Add(combat.AlternateAttackSpell?.SpellName);
        return list;

        void Add(string? spellName)
        {
            if (AttackSpellMatcherFor(spellName) is { } matcher) list.Add(matcher);
        }
    }

    // Resolve one attack-spell slot name to its caster-message matcher: match
    // the live spellbook by full name (the form a slot stores) or 4-letter
    // cast code, take its game-data record's
    // Models.GameData.MessageRecord.CasterMessage, and compile.
    // Returns null when the name is blank, unknown to the spellbook, has
    // no record, or the record has no usable caster template.
    private Game.Spells.CasterMessageMatcher? AttackSpellMatcherFor(string? spellName)
    {
        if (string.IsNullOrWhiteSpace(spellName)) return null;
        string target = spellName.Trim();
        foreach (Game.Spells.KnownSpell s in Spellbook.Available)
        {
            if (!string.Equals(s.Name.Trim(), target, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(s.Short.Trim(), target, StringComparison.OrdinalIgnoreCase))
                continue;
            Models.GameData.MessageRecord? rec = FindSpellMessage(s.Number, s.Name);
            return rec is null ? null : Game.Spells.CasterMessageMatcher.TryCreate(rec.CasterMessage);
        }
        return null;
    }

    // Equipped-weapon proc matcher, cached by weapon name so a hot
    // Inventory.Changed (coin pickups republish the snapshot too) doesn't
    // recompile the regex every time — only an actual weapon swap rebuilds.
    // Invalidated by nulling _procWeaponName on a game-data set swap, where the
    // same name may resolve to a different message.
    private string? _procWeaponName;
    private Game.Spells.CasterMessageMatcher? _procMatcherCache;

    // Compile the Game.Spells.CasterMessageMatcher for the
    // currently-wielded weapon's proc, from the item's game-data
    // Models.GameData.MessageRecord.CasterMessage. Resolves the
    // worn "Weapon Hand" item → ItemNames Number →
    // FindItemMessage. Returns null when nothing's wielded
    // or the weapon has no proc message. Cached on the weapon name.
    private Game.Spells.CasterMessageMatcher? EquippedWeaponProcMatcher()
    {
        string? weapon = EquippedWeaponName();
        if (string.Equals(weapon, _procWeaponName, StringComparison.OrdinalIgnoreCase))
            return _procMatcherCache;
        _procWeaponName = weapon;
        _procMatcherCache = BuildWeaponProcMatcher(weapon);
        return _procMatcherCache;
    }

    private string? EquippedWeaponName()
    {
        foreach (Game.Inventory.EquippedItem item in Inventory.Snapshot.EquippedItems)
            if (string.Equals(item.Slot, "Weapon Hand", StringComparison.OrdinalIgnoreCase))
                return item.Name;
        return null;
    }

    private Game.Spells.CasterMessageMatcher? BuildWeaponProcMatcher(string? weaponName)
    {
        if (string.IsNullOrWhiteSpace(weaponName)) return null;
        if (ItemNames.FindByName(weaponName) is not int number) return null;
        Models.GameData.MessageRecord? rec = FindItemMessage(number);
        return rec is null ? null : Game.Spells.CasterMessageMatcher.TryCreate(rec.CasterMessage);
    }

    // The given (first) name of fullName, or null
    // when unset. MajorMUD telepath / party-give syntax addresses by given
    // name only, so Game.Map.PartyPathItemGate's self-recipient
    // is reduced the same way Game.Remote.PartyBroadcaster
    // reduces its recipients.
    private static string? GivenNameOf(string? fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return null;
        int space = fullName.IndexOf(' ');
        return space >= 0 ? fullName[..space] : fullName;
    }

    // True when the given item id is in the current inventory snapshot —
    // carried or worn. The snapshot tracks names, so each carried / worn
    // display-name is mapped back to its item Number via
    // ItemNames (sharing the article/count normalization) and
    // compared. Backs PathItemDemand's possession check.
    private bool IsItemCarried(int itemId)
    {
        Game.Inventory.InventorySnapshot snap = Inventory.Snapshot;
        foreach (string name in snap.CarriedItems)
            if (ItemNames.FindByName(name) == itemId) return true;
        foreach (Game.Inventory.EquippedItem worn in snap.EquippedItems)
            if (ItemNames.FindByName(worn.Name) == itemId) return true;
        return false;
    }

    // How many copies of itemId the current snapshot holds
    // (carried + worn). The carried list stores one entry per copy, so gives /
    // receives accumulate as distinct entries; matching each display-name back
    // to its Number and counting yields the live copy count the leader's
    // party-provisioning redistribution needs. Backs
    // Game.Map.PartyPathItemGate's self-count seam.
    private int CountItemCarried(int itemId)
    {
        int count = 0;
        Game.Inventory.InventorySnapshot snap = Inventory.Snapshot;
        foreach (string name in snap.CarriedItems)
            if (ItemNames.FindByName(name) == itemId) count++;
        foreach (Game.Inventory.EquippedItem worn in snap.EquippedItems)
            if (ItemNames.FindByName(worn.Name) == itemId) count++;
        return count;
    }

    // Room keys of every shop in the live graph that stocks
    // itemId — the join of ShopStock (which
    // shops sell it) against RoomGraph (which rooms host those
    // shops). Backs PathItemShopRouter's detour-target search.
    // Only rooms present in the active graph can be walk targets, so shops
    // whose room isn't loaded are naturally excluded.
    private System.Collections.Generic.IReadOnlyList<Game.Map.RoomKey> ShopRoomsSellingItem(int itemId)
    {
        System.Collections.Generic.IReadOnlyCollection<int> shops = ShopStock.ShopsSelling(itemId);
        if (shops.Count == 0) return System.Array.Empty<Game.Map.RoomKey>();
        var rooms = new System.Collections.Generic.List<Game.Map.RoomKey>();
        foreach (Game.Map.Room room in RoomGraph.Rooms)
            if (room.Shop != 0 && shops.Contains(room.Shop))
                rooms.Add(room.Key);
        return rooms;
    }

    // Every spawn site of a monster that drops itemId —
    // the flatten of MonsterDrops's droppers × each dropper's
    // spawn rooms, tagged with the monster and drop chance for the reroute
    // prompt. Backs MonsterDropRouter's nearest-spawn search.
    // Computed lazily (only when a no-shop need fires), so the per-item
    // cross-product is never materialised at load time.
    private System.Collections.Generic.IReadOnlyList<Game.Map.MonsterDropSpawn> DropSpawnsForItem(int itemId)
    {
        System.Collections.Generic.IReadOnlyList<MonsterDropIndex.MonsterDrop> droppers
            = MonsterDrops.DroppersOf(itemId);
        if (droppers.Count == 0)
            return System.Array.Empty<Game.Map.MonsterDropSpawn>();
        var result = new System.Collections.Generic.List<Game.Map.MonsterDropSpawn>();
        foreach (MonsterDropIndex.MonsterDrop d in droppers)
            foreach (Game.Map.RoomKey room in MonsterDrops.SpawnRoomsOf(d.MonsterId))
                result.Add(new Game.Map.MonsterDropSpawn(room, d.MonsterId, d.MonsterName, d.DropPercent));
        return result;
    }

    // Resolve a single room "You notice ..." entry for
    // AutoGetItems: map the loose wording to an item
    // Number, read its verbatim Name, and resolve the per-character
    // Models.GameData.ItemOverlay.AutoCollect override
    // (Defaults seed → Global → BBS → Char). Returns null when
    // the entry isn't an item in the active set (cash, scenery), so the
    // engine skips it. AutoCollect defaults to false — pickup is
    // opt-in per item.
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

    // Resolve a single carried-inventory entry for Stash:
    // map the loose carry wording to an item Number, read its verbatim
    // Name, and resolve the per-character
    // Models.GameData.ItemOverlay.AutoStash override
    // (Defaults seed → Global → BBS → Char). Returns the canonical name
    // to hide when the item is flagged for auto-stash, else
    // null so the stash engine leaves it in the pack. AutoStash
    // defaults to false — stashing is opt-in per item.
    private string? ResolveAutoStashItem(string entry)
    {
        if (ItemNames.FindByName(entry) is not int number) return null;
        string? name = ItemNames.GetName(number);
        if (string.IsNullOrWhiteSpace(name)) return null;

        Models.GameData.ItemOverlay overlay = Resolver.ResolveGameData<Models.GameData.ItemOverlay>(
            "Items",
            number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ItemOverlaySeed.GetOverlay(number));

        return overlay.AutoStash ?? false ? name : null;
    }

    // Push the loaded character's Models.Profile.PartySettings
    // into the live PartyPoller / Party /
    // PartyBroadcaster. Subscribed to
    // ProfileService.ProfileLoaded +
    // ProfileService.ProfileMutated so a per-character
    // cadence (e.g. par-poll-frequency=15s) is honoured the moment the
    // profile auto-loads at startup — not just when the user opens the
    // Settings window. Pre-fix the cadence stayed at the 5 s default
    // for every character because the section-VM-only ApplyToServices
    // never fired until Settings was opened.
    public void ApplyPartyFromActiveProfile()
    {
        Models.Profile.PartySettings dto = ReadSection<Models.Profile.PartySettings>(Profile.Current, "Party");
        PartyPoller.SetParCadence(TimeSpan.FromSeconds(Math.Clamp(dto.ParPollFrequencySec, 1, 60)));
        Party.AutoInviteEnabled = dto.AutoInviteReconnecting;
        Party.DisconnectGraceWindow = TimeSpan.FromSeconds(Math.Clamp(dto.IfLeadingWaitTotalSec, 0, 3600));
        // Same "If leading, wait only" window also caps the invite-as-wait-signal
        // loop hold before we uninvite a no-show, and the inbound-@wait pause
        // before we give up on a member who never sent @ok.
        AutoParty.InviteWaitWindow = TimeSpan.FromSeconds(Math.Clamp(dto.IfLeadingWaitTotalSec, 0, 3600));
        PartyWaitMovement.WaitWindow = TimeSpan.FromSeconds(Math.Clamp(dto.IfLeadingWaitTotalSec, 0, 3600));
        // Same window holds movement for a dropped follower to reconnect and
        // re-party before we resume.
        PartyDisconnectMovement.GraceWindow = TimeSpan.FromSeconds(Math.Clamp(dto.IfLeadingWaitTotalSec, 0, 3600));
        // Leader-side recovery reach — the farthest we'll BFS-walk to re-collect a
        // returning member before declining via @forget.
        PartyComeback.ReturnDistanceRooms = Math.Clamp(dto.ReturnDistanceRooms, 1, 500);
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
        AutoParty.JoinNagEnabled      = dto.SendJoinToInvited;
        PartyPoller.HealthNagInitialDelay = nagInitial;
        PartyPoller.HealthNagFrequency    = nagFreq;
        PartyPoller.HealthNagMaxTotal     = nagMax;
        PartyPoller.HealthNagEnabled      = dto.SendHealthToMembers;
    }

    private void ResetPartyToDefaults()
    {
        Models.Profile.PartySettings defaults = new();
        PartyPoller.SetParCadence(TimeSpan.FromSeconds(defaults.ParPollFrequencySec));
        Party.AutoInviteEnabled = defaults.AutoInviteReconnecting;
        Party.DisconnectGraceWindow = TimeSpan.FromSeconds(defaults.IfLeadingWaitTotalSec);
        AutoParty.InviteWaitWindow = TimeSpan.FromSeconds(defaults.IfLeadingWaitTotalSec);
        PartyWaitMovement.WaitWindow = TimeSpan.FromSeconds(defaults.IfLeadingWaitTotalSec);
        PartyDisconnectMovement.GraceWindow = TimeSpan.FromSeconds(defaults.IfLeadingWaitTotalSec);
        PartyComeback.ReturnDistanceRooms = defaults.ReturnDistanceRooms;
        Party.LocalRankPreference = defaults.Rank;
        PartyBroadcaster.AutoExpResetEnabled = defaults.ResetStatisticsOnLoopStart;
        TimeSpan nagInitial = TimeSpan.FromSeconds(defaults.JoinNagInitialDelaySec);
        TimeSpan nagFreq    = TimeSpan.FromSeconds(defaults.JoinNagFrequencySec);
        TimeSpan nagMax     = TimeSpan.FromSeconds(defaults.JoinNagMaxTotalSec);
        AutoParty.JoinNagInitialDelay = nagInitial;
        AutoParty.JoinNagFrequency    = nagFreq;
        AutoParty.JoinNagMaxTotal     = nagMax;
        AutoParty.JoinNagEnabled      = defaults.SendJoinToInvited;
        PartyPoller.HealthNagInitialDelay = nagInitial;
        PartyPoller.HealthNagFrequency    = nagFreq;
        PartyPoller.HealthNagMaxTotal     = nagMax;
        PartyPoller.HealthNagEnabled      = defaults.SendHealthToMembers;
    }

    // Push the loaded character's Models.Profile.TalkSettings
    // into the live RemoteCommands engine. Same shape +
    // rationale as ApplyPartyFromActiveProfile.
    public void ApplyTalkFromActiveProfile()
    {
        Models.Profile.TalkSettings dto = ReadSection<Models.Profile.TalkSettings>(Profile.Current, "Talk");
        RemoteCommands.MasterDisable          = dto.DisallowAllRemoteCommands;
        RemoteCommands.DisallowPartyDirectives = dto.DisallowPartyCommands;
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
        RemoteCommands.DisallowPartyDirectives = defaults.DisallowPartyCommands;
        RemoteCommands.DisableTelepathChannel = defaults.DisallowRemoteFromTelepaths;
        RemoteCommands.DisableGangpathChannel = defaults.DisallowRemoteFromGangpaths;
        RemoteCommands.DisableLocalChannel    = defaults.DisallowRemoteFromLocal;
        RemoteCommands.WarnOnDenial           = defaults.WarnOnInvalidRemoteCommand;
        RemoteCommands.FailureMessage         = defaults.RemoteCommandFailureMessage ?? string.Empty;
    }

    // Push the loaded character's Models.Profile.OtherSettings
    // into the live engine knobs (currently
    // Game.Remote.RemoteCommandManager.MaxSuicideLivesThreshold).
    // Same shape + rationale as ApplyPartyFromActiveProfile.
    public void ApplyOtherFromActiveProfile()
    {
        Models.Profile.OtherSettings dto = ReadSection<Models.Profile.OtherSettings>(Profile.Current, "Other");
        RemoteCommands.MaxSuicideLivesThreshold = Math.Clamp(dto.MaxSuicideLivesThreshold, 0, 9);
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
        TrapDisarm.MaxSearchAttempts = defaults.MaxTrapSearchAttempts;
        TrapDisarm.MaxDisarmAttempts = defaults.MaxTrapDisarmAttempts;
        PartyComeback.MaxBacktrackRooms = defaults.MaxComebackBacktrackRooms;
        ComebackRequest.Enabled = defaults.AutoRequestComebackWhenLeftBehind;
    }

    // Push the loaded character's
    // Models.Profile.AutoLairSettings into
    // AutoLair — heuristic, idle penalty, engage timeout,
    // and the chosen Game.Map.ITravelCostModel
    // implementation. Same shape as
    // ApplyOtherFromActiveProfile.
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

    // Pull Models.Settings.ConfirmSettings out of the
    // Global-tier "Confirm" bucket and push it into
    // Confirm. Confirm prefs are Global tier (one
    // install-wide preference, not per-character) so this fires off
    // SettingsService.GlobalSettingsChanged, not the
    // per-profile events.
    private void ApplyConfirmFromGlobalSettings()
    {
        Models.Settings.ConfirmSettings dto =
            ReadGlobalSection<Models.Settings.ConfirmSettings>("Confirm");
        Confirm.ApplyFrom(dto);
    }

    // Read a typed DTO out of the Global-tier Settings
    // dictionary, returning a default-constructed instance when the
    // bucket is missing or unparseable.
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

    // Resolve which BBS the runtime should treat as active. Pin on
    // the loaded character profile wins; otherwise fall back to the
    // first BBS alphabetically (a user on a blank draft with one
    // saved BBS should still get its connection info, display
    // settings, and ActiveGameDataSet applied without manual
    // intervention). Returns null only when there's no pin
    // AND zero BBSes saved on disk. Mirrors the resolution logic
    // the main window's title-bar / Connect button use, so the
    // game-data + display + cache layers see the same active BBS
    // the user sees in the chrome.
    public Models.Settings.BbsProfile? ResolveActiveBbs()
    {
        string? name = Profile.CurrentBbsName;
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

    // Recompute the active game-data set from the BBS-pin chain and
    // flip GameData if it differs. Idempotent — the
    // cache short-circuits no-op switches so calling this on every
    // profile / BBS / mutate signal is cheap.
    private void ApplyActiveGameDataSet()
    {
        Models.Settings.BbsProfile? bbs = ResolveActiveBbs();
        string? resolved = bbs?.ActiveGameDataSet ?? Settings.Current.DefaultGameDataSet;
        GameData.SwitchSet(resolved);
    }

    // Drop any persisted reference to a just-deleted game-data set so a
    // later resolve doesn't point GameData at a folder
    // that's gone. Clears the global
    // Models.Settings.GlobalSettings.DefaultGameDataSet and
    // every BBS profile's
    // Models.Settings.BbsProfile.ActiveGameDataSet that
    // named it. Wired into GameDataSetManager as its
    // delete callback.
    private void ClearGameDataSetReferences(string deletedSet)
    {
        bool Matches(string? s) => string.Equals(s, deletedSet, StringComparison.OrdinalIgnoreCase);

        if (Matches(Settings.Current.DefaultGameDataSet))
        {
            Settings.Current.DefaultGameDataSet = null;
            Settings.Save();
        }

        foreach (string name in Bbs.ListNames().ToArray())
        {
            Models.Settings.BbsProfile? p = Bbs.Get(name);
            if (p is not null && Matches(p.ActiveGameDataSet))
            {
                p.ActiveGameDataSet = null;
                Bbs.Save(p);
            }
        }
    }

    private void ApplyDisplayFromActiveBbs()
    {
        Models.Settings.BbsProfile values = ResolveActiveBbs() ?? new Models.Settings.BbsProfile();
        Display.FontSize = values.FontSize;
        Display.ScrollbackLines = values.ScrollbackLines;
        Display.TerminalCols = values.TerminalCols;
        Display.TerminalRows = values.TerminalRows;

        // Game-menu commands are BBS-tier too — HangupHandler consumes
        // ExitCommand synchronously on @hangup; MainMenuEntryAutomation +
        // the cleanup-logout flow consume both. Blank entries fall back to
        // the DTO defaults (E / =x) so a misconfiguration can't leave the
        // engine with empty wire-sends.
        Models.Settings.BbsProfile defaults = new();
        GameCommands.EntryCommand = string.IsNullOrWhiteSpace(values.GameEntryCommand)
            ? defaults.GameEntryCommand
            : values.GameEntryCommand;
        GameCommands.ExitCommand = string.IsNullOrWhiteSpace(values.GameExitCommand)
            ? defaults.GameExitCommand
            : values.GameExitCommand;
    }

    private void ResetDisplayToDefaults()
    {
        Models.Settings.BbsProfile defaults = new();
        Display.FontSize = defaults.FontSize;
        Display.ScrollbackLines = defaults.ScrollbackLines;
        Display.TerminalCols = defaults.TerminalCols;
        Display.TerminalRows = defaults.TerminalRows;
        GameCommands.EntryCommand = defaults.GameEntryCommand;
        GameCommands.ExitCommand = defaults.GameExitCommand;
    }

    private void ApplyStatlineRegex()
    {
        Models.Profile.StatlineSettings statline =
            ReadSection<Models.Profile.StatlineSettings>(Profile.Current, "Statline");
        PromptScanner.InstallRegex(Game.StatlinePromptRegexBuilder.Build(statline.Command));
    }

    private void OnProfileLoaded(Models.Profile.CharacterProfile profile)
    {
        if (Profile.CurrentProfileName is null || Profile.CurrentBbsName is null) return;

        Models.Profile.ProfileRef loaded = new(Profile.CurrentBbsName, Profile.CurrentProfileName);
        if (Settings.Current.LastUsedProfile == loaded) return;

        Settings.Current.LastUsedProfile = loaded;
        Settings.Save();
    }
}
