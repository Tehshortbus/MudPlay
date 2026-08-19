namespace MudPlay.Services;

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

    // The tables worth a background head start at startup (see GameDataCache.
    // PrewarmAsync below) — the three largest MDB exports by a wide margin, and
    // the ones RoomGraphManager's set-switch rebuild (Rooms + Monsters) and the
    // item-name / shop-stock indexes (Items) read on every cold launch.
    private static readonly string[] StartupPrewarmTables = { "Rooms", "Monsters", "Items" };

    // The active service holder. Initialize must be called first.
    public static AppServices Current => _current
        ?? throw new InvalidOperationException(
            "AppServices not initialized — call AppServices.Initialize() during app startup.");

    // The active service holder, or null when not yet initialized (e.g. the XAML
    // previewer attaching a control before app startup). Use where a null result
    // should be a no-op rather than a throw.
    public static AppServices? CurrentOrNull => _current;

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

    // Same indirection for the Monsters section — lets the room-detail popup's
    // clickable monster names jump to a monster's Game Data record without a
    // back-reference to the main VM.
    private Action<int>? _monsterGameDataOpener;
    public void SetMonsterGameDataOpener(Action<int> opener) => _monsterGameDataOpener = opener;
    public void OpenMonsterGameData(int monsterNumber) => _monsterGameDataOpener?.Invoke(monsterNumber);

    // Opens the monster record DIALOG (not the browser) by Number — the Navigation Room
    // Info panel's monster links, so a click lands on the full record like the item link.
    public System.Threading.Tasks.Task OpenMonsterRecordAsync(int monsterNumber)
        => MonsterRecord.OpenAsync(monsterNumber);

    // Opens the spell record DIALOG (Message / Game-Data tabs) by Number — the Room Info
    // room-spell link, so a click lands on the full record like the item / monster links.
    public System.Threading.Tasks.Task OpenSpellRecordAsync(int spellNumber)
        => SpellRecord.OpenAsync(spellNumber);

    // Opens (or re-focuses) the Monsters section with an "Acc ≥ minAcc" filter
    // applied — the Hit Calculator's "Show me the Monsters" jumps here with the
    // accuracy that hits the player at the picked hit-%.
    private Action<int>? _monstersAccuracyOpener;
    public void SetMonstersAccuracyOpener(Action<int> opener) => _monstersAccuracyOpener = opener;
    public void OpenMonstersWithAccuracy(int minAccuracy) => _monstersAccuracyOpener?.Invoke(minAccuracy);

    // Same indirection for the Rooms section — lets an item's clickable
    // bought/sold shop line jump to the host room's Rooms-tab record (by
    // Map Number + Room Number) without a back-reference to the main VM.
    private Action<int, int>? _roomGameDataOpener;
    public void SetRoomGameDataOpener(Action<int, int> opener) => _roomGameDataOpener = opener;
    public void OpenRoomGameData(int map, int room) => _roomGameDataOpener?.Invoke(map, room);

    // Opens (or re-focuses) the Navigation window and centres the map on a
    // given room. Used by the room-detail popup's clickable room title. No-op
    // until the main VM binds it.
    private Action<Game.Map.RoomKey>? _navigateToRoomOpener;
    public void SetNavigateToRoomOpener(Action<Game.Map.RoomKey> opener) => _navigateToRoomOpener = opener;
    public void NavigateToRoom(Game.Map.RoomKey key) => _navigateToRoomOpener?.Invoke(key);

    // Opens (or re-focuses) the Navigation window and ARMS a walk to a room —
    // sets QueuedDestination exactly as picking a search result does, so the user
    // then clicks Run. Used by the item record's "Queue Walking here" shop links.
    // No-op until the main VM binds it.
    private Action<Game.Map.RoomKey>? _queueWalkOpener;
    public void SetQueueWalkOpener(Action<Game.Map.RoomKey> opener) => _queueWalkOpener = opener;
    public void QueueWalkTo(Game.Map.RoomKey key) => _queueWalkOpener?.Invoke(key);

    // Type text at the game through the SAME path the terminal / Conversation input
    // uses — macro split, alias expansion, and the outbound cast/attack/chat/movement
    // observers — so a programmatic send is indistinguishable from the user typing
    // it. Distinct from SendGameCommand, which rides the raw wire-sender with none of
    // that. Used by the quest guide's clickable `'command'` links. No-op until the
    // main VM binds it.
    private Action<string>? _typedInputSender;
    public void SetTypedInputSender(Action<string> sender) => _typedInputSender = sender;
    public void SendTypedInput(string text)
    {
        if (!string.IsNullOrWhiteSpace(text)) _typedInputSender?.Invoke(text);
    }

    // Opens (or re-focuses) the single Navigation Management dialog. Both the map
    // window's "Navigation Management" button and the toolbar Start button route
    // here so there's only ever one instance — no two identical windows. The bool
    // picks the default tab: the toolbar entry lands on Go To, the map entry on
    // Loops. No-op until the main VM binds it.
    private Action<bool>? _navManagerOpener;
    public void SetNavManagerOpener(Action<bool> opener) => _navManagerOpener = opener;
    public void OpenNavManager(bool startOnGotoTab = false) => _navManagerOpener?.Invoke(startOnGotoTab);

    // Centres the map on a room ONLY if the Navigation window is already open —
    // never force-opens it. Used by the room-detail popup's exit clicks, which
    // walk the popup itself to the neighbour and let an open map follow along
    // without hijacking the screen when it's closed.
    private Action<Game.Map.RoomKey>? _centerNavigationIfOpenOpener;
    public void SetCenterNavigationIfOpenOpener(Action<Game.Map.RoomKey> opener) => _centerNavigationIfOpenOpener = opener;
    public void CenterNavigationIfOpen(Game.Map.RoomKey key) => _centerNavigationIfOpenOpener?.Invoke(key);

    // Single source of truth for "are you sure?" prompts (exit /
    // hangup / save / delete). Lives at Global tier; mirrored from
    // SettingsService on startup and every save.
    public ConfirmService Confirm { get; }

    // App-wide severity-tagged ring-buffer log. Status bar + log pane subscribe.
    public LogService Log { get; }

    // Tees Log to a rolling on-disk file (Data/Logs/{ts}-program.log) so a
    // hard hang / kill leaves a post-mortem trail the in-memory ring can't.
    // Only writes while LogDiagnostics.AutoCollectLogs is on (default off).
    public ProgramLogFile ProgramLog { get; }

    // Samples the process memory footprint a-minute-at-a-time to its own
    // Data/Logs/{ts}-memory.log, kept out of the program log, so an all-night
    // session leaves a trail that tells a managed-heap leak from working-set creep.
    // Only writes while LogDiagnostics.AutoCollectLogs is on (default off).
    public MemoryUsageLog MemoryLog { get; }

    // Background memory hygiene: compacts the LOH once a game-data set settles
    // (reclaiming the startup JSON-parse fragmentation) and periodically returns
    // glibc's free native pages to the OS, so a loop-mode session running for days
    // doesn't hold a working set far larger than its live heap. Invisible — no
    // toggle; see the class comment for the timing that keeps it unnoticed.
    public MemoryMaintenance Memory { get; }

    // Per-character diagnostic switches surfaced in the Log pane: DebugDiagnostics
    // and CombatDiagnostics gate in-memory Debug/Combat channel generation;
    // AutoCollectLogs gates whether the on-disk diagnostic files (program /
    // memory / combat trace) are written at all. Consumers
    // (e.g. Game.Combat.RoundDamageTracker) read this instead of per-character
    // settings directly. The live state is mirrored to the Char-tier
    // LogDiagnosticsSettings section: applied on ProfileLoaded, reset off on
    // ProfileClosed, persisted on Changed (see the Apply/Reset/Persist helpers
    // below).
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

    // Which Wire Inspector panes are currently visible — read by BugReportBuilder to
    // decide whether to attach the raw / classified wire. Updated by the inspector VM.
    public WireInspectorVisibility WireInspectorVisibility { get; } = new();

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

    // Persists the Conversation window + Transaction history to per-character
    // rolling files under Data/Logs. Constructed once the chat router and the
    // transaction tracker exist.
    public SessionLogService SessionLog { get; private set; } = null!;

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

    // Routes keyboard input from modeless dialogs back to the terminal, so typing
    // continues to reach the terminal while another window is focused (unless a
    // text box owns the keystroke). The TerminalControl registers its input core;
    // DialogKeyboardFallthrough forwards through it. Enabled gated by a setting.
    public TerminalInputRouter TerminalInput { get; } = new();

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

    // Releases a stranded cash/item deferred-collect (Acquisition gate) hold on the
    // first in-game prompt after a reconnect, so a drop mid-collect doesn't leave the
    // loop paused until a manual `rm`. Armed from the Connected handler.
    public Game.Map.DeferredCollectReconnectReleaser DeferredCollectResume { get; private set; } = null!;

    // Leader-side reconnect party reform — the mirror of PartyRejoin. Snapshots the
    // followers we're leading at disconnect and, on the first in-game room after the
    // reconnect, rebases the grace window + holds the loop so a nightly-cleanup
    // reconnect waits for them to return and re-party instead of stranding them.
    public Game.Remote.PartyReformCoordinator PartyReform { get; private set; } = null!;

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

    // Announces a quest becoming available (min level trained past) + the login dump.
    // MainWindowViewModel subscribes to write the terminal line and fires the login dump.
    public Game.Quests.QuestAvailabilityAnnouncer QuestAvailability { get; }

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

    // Realm-wide boss catalog (seed + per-set overlay); timer values resolve from
    // game data. Feeds the Player Workshop Bosses tab and the boss-timer feature.
    public BossStore Bosses { get; }

    // Persisted per-set boss kill-times driving the live respawn countdowns +
    // @timer. Auto-started on a detected boss kill; manual override on the tab.
    public BossTimerStore BossTimers { get; }

    // @timer read-only query handler. App-lifetime, like the other query handlers.
    public Game.Remote.BossTimerQueryHandler BossTimerQuery { get; private set; } = null!;

    // @death read-only query handler — reports unrecovered deaths from the
    // recovery log. App-lifetime, like the other query handlers.
    public Game.Remote.DeathQueryHandler DeathQuery { get; private set; } = null!;

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

    // Runs the character's Settings → General "Default task" (Begin looping /
    // Begin Auto-Lair) once per game entry. Like EventScheduler it's app-scoped
    // and driven by MainWindowVM's NotifyConnected / NotifyDisconnected plus the
    // stable WirePromptScanner + RoomTracker singletons.
    public Game.DefaultTaskRunner DefaultTaskRunner { get; private set; } = null!;

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

    // Per-set editable vocabulary of monster flavor adjectives the room classifier
    // strips to resolve a prefixed display name. Defaults to the built-in stock list;
    // edited in the Game Data Browser's Flavor Prefixes section.
    public FlavorPrefixStore FlavorPrefixes { get; private set; } = null!;

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

    // Reactive `look <player>` automation (Settings → Talk). Two independent
    // toggles: look-back when a player looks at us, and look at non-party
    // players who walk into the room. Both off by default. Subscribes to the
    // PlayerLooksAtYou pattern + RoomEntry.ArrivalObserved.
    public Game.PlayerLookManager PlayerLook { get; private set; } = null!;

    // Per-character log of players seen in the world — one aggregated row per
    // player with last-seen time / room and a running sighting count. Feeds the
    // Session Stats → Players Seen window. Records off the same room-presence
    // hooks Greet / PlayerLook use (Also-here matches + room walk-ins); persists
    // on the loaded profile.
    public Game.PlayerSightingTracker PlayerSightings { get; private set; } = null!;

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

    // Generic color+wording combat-line recognizer (monster-agnostic, no per-monster
    // data). Classifies each in-combat-window line into a Game.Combat.CombatLineKind
    // for the Wire Inspector's classified view + bug-report capture. The per-monster
    // MonsterMessages remain the engine's authoritative fallback for now.
    public Game.Combat.CombatLineClassifier CombatClassifier { get; private set; } = null!;

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

    // Per-loop-step HP/MA min-max profile for the Session Stats "HP/MA History"
    // graph. Fed by the prompt scanner (gated on an actively-stepping loop) keyed
    // by the live loop step index; cleared at each new loop start and the session
    // boundary.
    public Game.Combat.HpMaHistoryTracker HpMaHistory { get; private set; } = null!;

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

    // Drain-life target eligibility (living + not undead) by monster number. Feeds
    // CombatManager's drain-spell gate.
    public Game.Combat.MonsterLifeIndex MonsterLife { get; private set; } = null!;

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

    // Lookup of each spell's Short cast-code by its Spells.Number in the active
    // set — bridges the per-monster override slots (which store a Number) to the
    // Short the combat engine casts.
    public Game.Combat.SpellShortIndex SpellShort { get; private set; } = null!;

    // Catalogue of every light-source item (ItemType 6) in the
    // active set — projected illumination (IlluTarget) + burn budget —
    // for computing carried illumination and provisioning a dark route.
    public Game.Light.LightItemIndex Lights { get; private set; } = null!;

    // Resolves the illumination a configured room-light spell contributes, so the
    // auto-light engine can count it toward coverage alongside worn +illu gear.
    public Game.Light.RoomLightSpellResolver RoomLightSpell { get; private set; } = null!;

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

    // Observes mid-room departure lines
    // ("<name> walks out of the room to <dir>.")
    // and removes the departing monster from RoomClassifier's
    // observation so CombatStateTracker drops the Combat gate the
    // departed mob was holding (fleeing player dragging our engaged
    // mob out with them — see the 180449 capture).
    public Game.Combat.RoomDepartureWatcher RoomDeparture { get; private set; } = null!;

    // Recognises monster deaths via the per-monster
    // Models.GameData.MonsterMessageRecord.DeathLine
    // patterns + the "experience + Combat Off" fallback. On a match,
    // the dead monster is removed from RoomClassifier's
    // observation so CombatManager re-picks correctly instead of
    // sitting on a stale entry.
    public Game.Combat.MonsterDeathWatcher MonsterDeath { get; private set; } = null!;

    // Index of monsters whose DeathSpell summons another, + the settle that CR-
    // rechecks the room on such a kill before the walker moves on.
    public Game.Combat.MonsterDeathSummonIndex MonsterDeathSummon { get; private set; } = null!;
    public Game.Combat.SummonOnDeathSettle SummonSettle { get; private set; } = null!;

    // Room-aware monster-name resolver: disambiguates a display name shared across
    // zones to the record actually placed / summoned in the current room. Backs the
    // HP-lookup and per-monster spell-override features. Its summon-targets index
    // widens the room set with a summoner's minions.
    public Game.Combat.MonsterSummonTargetsIndex MonsterSummonTargets { get; private set; } = null!;
    public Game.Combat.RoomAwareMonsterResolver RoomAwareMonster { get; private set; } = null!;

    // Engages a monster hidden by darkness. A dark room prints no "Also here:"
    // line, so the only tell a hostile shares it is the mob's dark-cyan attack
    // line; this watcher reads the name off that line (gated on
    // RoomTracker.IsInDarkRoom) and injects it into RoomClassifier so
    // CombatManager engages it as if it had been listed.
    public Game.Combat.DarkRoomCombatWatcher DarkRoomCombat { get; private set; } = null!;

    // Holds the movement stack for a short beat after each dead-reckoned dark-room
    // advance, so the game engine has time to reveal a hostile (its "strides in"
    // arrival / first attack line) before the loop fires the next move. Without it
    // the dark advance confirms synchronously and the walker marches past the
    // fight. Constructed BEFORE the movement engines so its StateChanged handler
    // asserts the settle gate ahead of their synchronous SendNextStep.
    public Game.Map.DarkRoomMovementSettle DarkRoomSettle { get; private set; } = null!;

    // Lit-room twin of DarkRoomSettle: holds the movement loop when a combat line
    // arrives in a room our view shows empty, so a hostile that leapt in a beat
    // after an empty render engages before the loop steps past it. See
    // CombatRedisplaySettle for the race writeup.
    public Game.Combat.CombatRedisplaySettle CombatRedisplaySettle { get; private set; } = null!;

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
    // announces on say (.@poisoned etc.) so other MudPlay
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

    // Keeps a checkspell hazard buff up while the walker crosses a hazard room.
    // Bound to the same approach-room hook as the light provisioner: the instant a
    // step commits toward a checkspell-hazard room whose buff source we carry
    // (the desert waterskin), it `use`s the item so the buff is raised before we
    // arrive, re-`use`ing on the buff's own duration-timer so a long traverse
    // spends the minimum charges. No master toggle — surviving a hazard room the
    // route already commits to walking isn't opt-in. Wire-sender bound by
    // MainWindowViewModel after connect.
    public Game.Map.AutoHazardCounterProvisioner AutoHazardCounterProvisioner { get; private set; } = null!;

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

    // Runtime source-of-truth for the per-BBS runic-currency word. Read live by
    // every cash parser / command builder (Cash, Stash, GroundItems) and
    // refreshed on profile / BBS swap; defaults to stock "runic".
    public Game.Cash.CurrencyNaming Currency { get; private set; } = null!;

    // Auto-get items engine. Parses the room
    // "You notice ... here." survey, resolves each entry against the
    // active set's items + the per-character
    // Models.GameData.ItemOverlay.AutoCollect flag, and
    // sends get <name> per flagged item. Gated by the
    // AutoGetItems master toggle; defer-until-combat-finished honours
    // the Settings → Items tab.
    public Game.Inventory.AutoGetItemsManager AutoGetItems { get; private set; } = null!;

    // Auto-discard engine. On every inventory change, drops each carried item
    // flagged Models.GameData.ItemOverlay.AutoDiscard down to its keep floor —
    // one drop <name> per excess copy. Cleans chest dumps and unwanted collected
    // loot. Gated by the AutoDiscard master toggle; a LoyalItem is never dropped.
    public Game.Inventory.AutoDiscardManager AutoDiscard { get; private set; } = null!;

    // Auto-buy engine. Watches the emitted line stream for a shop `list` readout,
    // parses its stock table, and buys each stocked item flagged
    // Models.GameData.ItemOverlay.AutoBuy up to its MaxToGet cap — one buy <name>
    // per unit, advancing off the live purchase / can't-afford result. Gated by
    // the AutoBuy master toggle; LIGHT items are excluded (Auto-light owns them).
    public Game.Inventory.AutoBuyManager AutoBuy { get; private set; } = null!;

    // Auto-sell engine. When a shop `list` readout surfaces, sells each carried
    // item flagged Models.GameData.ItemOverlay.AutoSell down to its keep floor at
    // the merchant standing in — one sell <name> per unit, advancing off the live
    // sold / can't-sell-here result. Gated by the Auto-Get Items auto-mode toggle
    // (master) plus the per-item ItemOverlay.AutoSell flag; a LoyalItem and LIGHT
    // items are never sold.
    public Game.Inventory.AutoSellManager AutoSell { get; private set; } = null!;

    // Auto-open engine. On every inventory change, sends open <name> once for
    // each container item (ItemType == Container) flagged
    // Models.GameData.ItemOverlay.AutoOpen that newly entered the pack. Shares
    // the AutoGetItems master toggle; the per-item AutoOpen flag is the real gate.
    public Game.Inventory.AutoOpenManager AutoOpen { get; private set; } = null!;

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

    // Reverse item-acquisition index — the containers an item is found in and
    // the monster/room textblock `giveitem` awards that hand it over. Feeds the
    // Game Data Browser's item detail pane AND PathItemGiveRouter's giver lookup
    // (deterministic, keyword-carrying awards, with each Monster giver's spawn
    // rooms). Builds lazily on first query and self-invalidates on a set swap (no
    // ActiveSetChanged subscription).
    public ItemSourceIndex ItemSources { get; private set; } = null!;

    // Room→floor-item index (TBInfo `roomitem` placements) backing the Navigation
    // Room Info panel. Lazy + self-invalidating like ItemSources.
    public RoomFloorItemIndex RoomFloorItems { get; private set; } = null!;

    // Active fulfiller for NeedKind.PathItem needs an NPC / room hands over for
    // free: on a one-shot walk-to that needs an uncarried item a deterministic
    // textblock `giveitem` supplies, detours to the fewest-added-steps giver,
    // issues the `ask <npc> <keyword>` / room-CMD command, and resumes once it
    // lands. Preempts the shop and drop routers. Gated per item by the item
    // record's AutoObtainForPath flag.
    public Game.Map.PathItemGiveRouter PathItemGiveRouter { get; private set; } = null!;

    // Index of the active set's room-entry hazards — a room's cast-on-enter
    // Spell mapped to the item(s) that make the room safe (fish-helm negator,
    // failitem rafts, checkspell buff sources). Feeds the navigation
    // hazard-gating pass; rebuilt on GameDataCache.ActiveSetChanged.
    public RoomHazardIndex RoomHazards { get; private set; } = null!;

    // Index of the active set's buff-stripping room-entry spells — rooms whose
    // cast-on-enter Spell removes/dispels magic (RemovesSpell / DispellMagic).
    // Feeds CastingDirector's buff-suppression gate; rebuilt on
    // GameDataCache.ActiveSetChanged.
    public RoomBuffStripIndex RoomBuffStrip { get; private set; } = null!;

    // Active fulfiller for NeedKind.PathItem needs no shop can
    // satisfy: on a one-shot walk-to that needs an uncarried item no shop
    // sells, prompts to reroute to the nearest room a monster that drops it
    // spawns in, then resumes once it lands. Gated per item by the item
    // record's AutoObtainForPath flag, set in the item-edit dialog.
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

    // Once-a-day party stats probe — on the first join with a player each local
    // day, telepaths them @level + @version and records the version onto their
    // player record. Gated by Settings → Party "probe stats on partying".
    public Game.Remote.PartyProbeManager PartyProbe { get; private set; } = null!;

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

    // Coalesces the post-kill room re-render Cash and AutoGetItems each request,
    // so the last kill renders the room once, not twice. Both are bound to it.
    public Game.Inventory.RoomRedisplayCoordinator RoomRedisplay { get; } = new();

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
    // DontBackstab). Realm flavor is auto-picked from
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

    // Opens the item record (edit) dialog by Number from any surface — the Item
    // Finder double-click. Constructed once; single-instance dialog across callers.
    public ItemRecordDialogService ItemRecord { get; private set; } = null!;

    // Opens the monster record (edit) dialog by Number from any surface — the
    // Navigation Room Info panel's monster links. Single-instance across callers.
    public MonsterRecordDialogService MonsterRecord { get; private set; } = null!;

    // Opens the spell record (Message / Game-Data) dialog by Number from any surface —
    // the Navigation Room Info panel's room-spell link. Single-instance across callers.
    public SpellRecordDialogService SpellRecord { get; private set; } = null!;

    // Opens an item's on-use / proc message editor from the item dialog's Message
    // section — the Items-side mirror of SpellRecord. Item-claimed message records are
    // authored here rather than the Messages tab. Single-instance across callers.
    public ItemMessageDialogService ItemMessage { get; private set; } = null!;

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

    // Paradigm-only authoritative position re-sync. Fires `rm` on the gate's
    // request and re-anchors the tracker + gate from the Location: reply. Stock
    // realms no-op it and keep the heuristic recovery ladder.
    public Game.Map.ParadigmPositionResolver ParadigmResync { get; private set; } = null!;

    // Per-active-set index of random-teleport "maze" pockets (the Warped Asylum
    // is canonical). Detects them structurally and holds the 1x2 relocalization
    // signatures + reshuffle exits. Rebuilds on graph reload; app-lifetime.
    public Game.Map.TeleportMazeIndex MazeIndex { get; private set; } = null!;

    // Stock-only random-teleport maze solver. When the walker can't source a
    // route into a maze pocket, this drives look-peeks to relocalize after each
    // teleport and reshuffles across disconnected components until a plain route
    // to the goal exists, then hands the final walk back to the walker.
    public Game.Map.TeleportMazeSolver MazeSolver { get; private set; } = null!;

    // Great Pyramid puzzle-climb solver. When the walker can't source a route to a
    // pyramid room (the floors are joined only by sphinx teleports BFS never plans
    // through), this plays the canned per-floor climb script from the firepit to
    // 12/2085. Its wire-sender, room-display feed, and line feed are bound
    // per-session by MainWindowViewModel after connect.
    public Game.Map.PyramidSolver PyramidSolver { get; private set; } = null!;

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

    // Sniffs a hand-typed PHYSICAL attack verb so Combat treats it as a user override
    // (holds the auto attack until next round). Hooked from SendUserInput.
    public Game.Combat.OutboundAttackObserver OutboundAttack { get; private set; } = null!;

    // Classifies a cast-code as a combat spell (round energy 1–1000) vs an in-between
    // spell — drives whether a hand-typed cast is a user override or keeps the resume.
    public Game.Combat.CombatSpellIndex CombatSpells { get; private set; } = null!;

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

    // Per-character recent walk-to destinations for the Navigation goto button.
    // Persisted via ProfileService.
    public GotoHistoryStore GotoHistory { get; private set; } = null!;

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

    // Self-confusion bridge — sets our own party-window Confused chip and holds
    // our navigation (ConfusionGate) while we're confused. AilmentSyncEngine's
    // @wait covers a confused follower; this covers a confused leader / solo,
    // whose @wait is eaten. Honours the Ignore Confusion setting.
    public Game.Conditions.SelfConfusionResponder SelfConfusion { get; private set; } = null!;

    // Self-held bridge — sets our own party-window Held chip and holds our
    // navigation (HeldGate) while we're knocked down / held (MovementPrevented),
    // so the loop doesn't hammer the server with moves that bonk "flat on your
    // back" and strand the tracker. Clears on "You get back on your feet.".
    public Game.Conditions.SelfHeldResponder SelfHeld { get; private set; } = null!;

    // Self-ailment chip bridge — mirrors our own poison / blindness / disease onto
    // the self party-window chip. The say-driven mirror only lights OTHER members'
    // chips; our own state is owned by ConditionTracker, so without this our self
    // row never showed poison even though `par` and "You feel ill." did.
    public Game.Conditions.SelfAilmentChipResponder SelfAilmentChip { get; private set; } = null!;

    // Follower-disconnect pause bridge (leader side) — holds movement while a
    // dropped party follower is inside the reconnect grace window, so we don't
    // sprint off without a member who's trying to reconnect and re-party.
    public Game.PartyDisconnectMovementGate PartyDisconnectMovement { get; private set; } = null!;

    // Death-stop bridge — when the local player dies, full-stops every movement
    // engine and clears the user gate (a clean stop, same as the Nav Stop button)
    // so nothing survives to re-drive us back into the room we died in, and a
    // manual or remote nav action afterward runs freely.
    public Game.PlayerDeathMovementHalt PlayerDeathHalt { get; private set; } = null!;

    // Dropped / mortally-wounded bridge — while the local character is at or
    // below 0 HP, holds the EngineSendGate (a dropped character can't act, so
    // every engine send is rejected), asserts MovementCoordinator's
    // MortallyWoundedGate, and clears the stale party roster (a drop removes us
    // from the party game-side). Auto-clears on recovery.
    public Game.PlayerDroppedGate PlayerDropped { get; private set; } = null!;

    // Trainer-screen lockout bridge — while the `train stats` / character-creation
    // form owns the keyboard (TrainerMenuTracker.MenuOwnsKeyboard), holds the
    // EngineSendGate so NO wrapped engine can leak a send into the form's Family
    // Name field. Only the user's manual input and the auto-trainer's CP
    // allocation reach the form (both ride the raw, un-wrapped SendUserInput, so
    // they pierce the hold like the low-HP hangup). Auto-clears on form exit.
    public Game.TrainerScreenGate TrainerScreen { get; private set; } = null!;

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

    // Exp/hr estimator resolver — turns a loop's waypoints into the per-room
    // route the LoopExpSimulator scores. Reads lair/monster game data; its cache
    // drops on ActiveSetChanged (same app-lifetime pattern as LairTimers).
    public Game.Map.RouteExpResolver ExpResolver { get; private set; } = null!;

    // Set by the Navigation window's view-model; the bug report calls it to snapshot
    // the live Exp/Hr Estimator session (route + tunables + result). Returns null
    // when the estimator isn't active, so a report captured any other time just
    // notes it as inactive. Bridges VM-only state into the AppServices-based report.
    public Func<Game.Map.ExpEstimatorSnapshot?>? ExpEstimatorSnapshotProvider { get; set; }

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

    // Per-BBS captured "top N" leaderboard history, read by the Calculators tab's
    // XP/HR table. Grows communally — every character on the board feeds and reads
    // the one shared list.
    public LeaderboardSnapshotStore Leaderboards { get; private set; } = null!;

    // Passive capture tracker that snapshots a "top N" listing off the live
    // terminal into Leaderboards. Bound to the per-session LineExtractor by
    // MainWindowViewModel.AttachLineExtractor.
    public Game.Leaderboard.LeaderboardCaptureTracker LeaderboardCapture { get; private set; } = null!;

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

        // One-time forced retirement of the pre-split Messages catalogue (legacy single
        // Global seed + per-set messages.json), so existing installs land on the new
        // realm-flavored seeds bootstrapped just above. Guarded by a marker; backs up to
        // .bak first. Remove-after-rollout (tracked as a GitHub issue).
        DataMigration.RetireLegacyMessagesOnce(bootstrapLog);

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
        // Tee the program log to disk, gated on AutoCollectLogs: the writer only
        // opens once the toggle turns on (applied from the profile below or
        // flipped from the Log pane), so a normal session leaves no file.
        ProgramLog = new ProgramLogFile(Log, LogDiagnostics);
        // Surface a one-time legacy data migration (pre-3.0 FujinTerm → MudPlay)
        // now that logging is up — AppPaths ran it at static-init.
        if (AppPaths.MigrationNote is { } migrationNote)
            Log.Info("Migration", migrationNote);
        // Same gating for the memory-footprint sampler: the timer runs for the
        // whole process, but samples land on disk only while AutoCollectLogs is on.
        MemoryLog = new MemoryUsageLog(LogDiagnostics);
        // Background memory hygiene. CombatTracker is bound later in construction;
        // the combat-active probe is lazy and the first periodic tick is minutes
        // out, so it's always assigned before the Func is ever invoked.
        Memory = new MemoryMaintenance(Log, GameData, () => CombatTracker.HasEngageableHostiles);
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

        // Startup head start: parse the big MDB tables for whatever profile "Auto-load
        // last profile" is about to bring in, on a background thread, before Profile.Load
        // below triggers the real (synchronous) GameData.SwitchSet — the same resolution
        // ApplyActiveGameDataSet does later, just computed early from the not-yet-loaded
        // startup profile's BBS pin. RoomGraphManager's set-switch rebuild is the biggest
        // single cost on a cold launch (a full Rooms.json parse + graph build over
        // thousands of rooms, done synchronously before the window even exists); this
        // just gets GameDataCache's raw JsonDocument cache warm ahead of time so that
        // work finds the parse already done. A wrong guess (auto-load off, or the
        // predicted BBS/set doesn't match what actually loads) just wastes the
        // background parse — GetRawTable falls back to its normal on-demand read either
        // way, so this can't make startup any slower than it already is.
        if (Settings.Current.StartupProfile() is { } startupPrediction)
        {
            string? predictedSet = Bbs.Get(startupPrediction.Bbs)?.ActiveGameDataSet
                ?? Settings.Current.DefaultGameDataSet;
            if (!string.IsNullOrWhiteSpace(predictedSet))
                _ = GameData.PrewarmAsync(predictedSet, StartupPrewarmTables);
        }

        // Resolver subscribes to Profile events for active-BBS tracking; build
        // it before Load() below so it catches the auto-load's ProfileLoaded
        // (it also self-syncs from Profile.Current as a defensive fallback).
        // The active-set provider lets game-data override I/O target the
        // currently active MDB set's per-set side-files.
        Resolver = new SettingsResolver(Settings, Bbs, Profile, () => GameData.ActiveSet);
        Resolver.Log = bootstrapLog;

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
        // The server-PvP channel is paradigm-only; the closure is evaluated
        // lazily at line-match time, so GameData being assigned later in the
        // ctor is safe.
        Chat = new Game.ChatRouter(Router, () => GameData.ActiveRealm == Game.RealmType.ParaMud);
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
        // Same custom-disconnect source feeds the conversation window's realm
        // category — otherwise a board with a non-standard logoff line evicts the
        // roster member but never logs the disconnect in the conversation.
        Chat.DisconnectPatternProvider = () => ResolveActiveBbs()?.DisconnectPattern;
        // Known-player gate for others'-POV actions/emotes: the actor of a room-local
        // social is a player in our room's entity list. Rejects room names, monsters
        // and ambient flavour that share the action-green colour.
        Chat.IsKnownPlayer = IsKnownRoomPlayer;
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
        // ParaMud teaching-item wording ("You add <name> to your spellbook!") —
        // same effect as the learn-scroll line, so the picker's unlearned guard
        // clears the moment the spell is learned mid-session.
        Router.Subscribe(Services.Patterns.KnownPatterns.LearnSpellFromItem, m =>
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
            readDraggedBy: () => Dragged.DraggedBy,
            readAilments: () => Conditions?.ActiveFlags ?? Models.GameData.MessageFlags.None,
            // HealthManager is built later in OnGameDataLoaded; the lambda reads it
            // lazily so a @status arriving after startup sees the live flee state.
            readFleeing: () => Health?.IsFleeing ?? false);
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
        // Full-screen forms want character-at-a-time input with server echo,
        // not client-side line buffering. Two arming paths, both flip the same
        // flag (idempotent): the command-armed InputMenuEntered/Exited pair
        // covers `train stats` (whose "Point Cost Chart" marker on a cursor-
        // positioned menu completes too late — or never inline — so the outbound
        // command is the realm-independent signal), while the marker-confirmed
        // MenuEntered/Exited pair covers character creation, which is reached
        // from the class/race/alignment flow with no outbound command to arm on.
        TrainerMenu.InputMenuEntered += () => InputBuffer.CharacterMode = true;
        TrainerMenu.InputMenuExited  += () => InputBuffer.CharacterMode = false;
        TrainerMenu.MenuEntered += () => InputBuffer.CharacterMode = true;
        TrainerMenu.MenuExited  += () => InputBuffer.CharacterMode = false;
        // Silence the poller's wall-clock cadences (par poll + @health nag)
        // while parked in the trainer stats menu; the auto-trainer drives its
        // own wire, so its CP replay is unaffected. Gate on MenuOwnsKeyboard, not
        // IsInTrainerMenu: on Paradigm's cursor-positioned stat box the marker
        // never confirms, so the marker-only flag stays false and a `par\r` leaks
        // into the form's Family Name field, overwriting the character's last name.
        PartyPoller.IsInTrainerMenu = () => TrainerMenu.MenuOwnsKeyboard;
        // Blanket lockout: while the train-stats / creation form owns the
        // keyboard, hold the EngineSendGate so no wrapped engine can leak a send
        // into the form (the per-poller gate above is a belt-and-braces double
        // for the wall-clock cadences; this hold catches every other engine —
        // combat, casting, auto-get, chat replies, the lot). The user's manual
        // input and the auto-trainer's CP replay both ride the raw SendUserInput,
        // so they pierce the hold and remain the only two things that can type.
        TrainerScreen = new Game.TrainerScreenGate(TrainerMenu, EngineGate, Log);
        // Entering the train-stats screen breaks up our party server-side; a
        // FOLLOWER must clear its own stale "following <leader>" state now so the
        // leader's fresh re-invite on return is auto-accepted, not rejected as
        // "already following" (report stock-20260801-002423). PartyManager gates
        // this to followers; a leader reforms its group on trainer exit instead.
        TrainerMenu.MenuEntered += Party.NoteTrainStatsExcursion;
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
        void SeedSpellbook(Models.Profile.LastKnownStats? snap, bool reseed = false)
        {
            int classNumber = snap is null ? 0 : SpellCatalog.ResolveClassNumber(snap.Class) ?? 0;
            int level = snap?.Level ?? 0;
            // reseed = the active game-data set changed under us: force a rebuild
            // even when the class number is unchanged, since the Spells table
            // itself was replaced. Refresh alone skips the rebuild on an
            // unchanged class number and would leave Available stale.
            if (reseed) Spellbook.Reseed(classNumber, level);
            else Spellbook.Refresh(classNumber, level);
        }

        // A game-data set swap replaces the Spells / Classes tables under the
        // live character. Re-resolve the class number from the persisted class
        // NAME (a set may renumber classes) and reseed the Spell Book so
        // Available and the learned checkmarks re-resolve against the new set
        // instead of blanking. The obtained set is name-backed, so it survives
        // the renumber — no need to re-apply the profile's persisted names here.
        GameData.ActiveSetChanged += _ => SeedSpellbook(Profile.Current?.LastKnownStats, reseed: true);

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
        // Persist + restore the last-known carry weight across sessions.
        // Encumbrance only changes in the realm, so the value the client last saw
        // is still accurate on the next reconnect — seeding it starts the
        // travel-cost models / hop-timing calibrator / Workshop with the real
        // bracket instead of Unknown, without waiting on the connect-`i` (which
        // never fires on a manual login or a hangup-suppressed relog).
        // InventoryManager holds the numeric reading (Paradigm cost model,
        // calibrator, Workshop); EncumbranceParser owns PlayerState.Encumbrance
        // (stock cost model) — restore both, each through its sole writer.
        Profile.ProfileSaving += p =>
        {
            if (Inventory.SnapshotEncumbrance() is { } enc) p.LastKnownEncumbrance = enc;
        };
        Profile.ProfileLoaded += p =>
        {
            Inventory.HydrateEncumbrance(p.LastKnownEncumbrance);
            Encumbrance.Hydrate(p.LastKnownEncumbrance);
        };
        Profile.ProfileClosed += () =>
        {
            Stats.Hydrate(null);
            SeedSpellbook(null);
            Inventory.HydrateEncumbrance(null);
            Encumbrance.Hydrate(null);
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
        TrapDisarm = new Game.TrapDisarmManager(Router, PlayerStats, GameData, Log);
        TrapDelegation = new Game.TrapDelegationManager(Party, Players, GameData, Router, Log);
        // Suppress the race-probe look while a party-splitting-teleport reform is
        // settling — no member looks during that evolution (AutoParty owns the
        // reform lifecycle; a stray look re-strands the resuming walk).
        TrapDelegation.IsPartyReformSettling = () => AutoParty.IsReformSettling;
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
            // Read lazily at door-open time — Inventory is constructed after Door.
            holdsKeyItem:                  HoldsKeyItem,
            log: Log,
            // UI-thread one-shot so the door FSM's response watchdog fires on the
            // same thread its router-driven handlers run on; keeps Game/Map UI-free
            // (tests drive result lines synchronously and leave this null).
            scheduleDelay: (delay, callback) =>
            {
                var timer = new Avalonia.Threading.DispatcherTimer { Interval = delay };
                timer.Tick += (_, _) => { timer.Stop(); callback(); };
                timer.Start();
                return new DispatcherTimerHandle(timer);
            });
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

        // PartyManager needs the local character's name so its par-row
        // parser can tag the right row IsSelf=true (par's "Given Family"
        // name is compared against this). The profile name is a label the
        // user picks and often differs from the in-game character name
        // (e.g. profile "MudPlayPVP" vs character "MudPlay"), which mis-tagged
        // the self row and spawned a phantom party entry. So prefer the
        // parsed character name (StatParser owns PlayerStats.Name) whenever
        // it's known, falling back to the profile name until the first
        // stat/snapshot restore fills it in. The Hydrate handler above runs
        // first (earlier subscription), so a returning session already has
        // the restored name here; the PropertyChanged sync below then keeps
        // it current as live `stat` screens re-parse. Cleared on close so
        // IsSelf goes back to false for every row across the swap.
        Profile.ProfileLoaded += p =>
            Party.LocalCharacterName = string.IsNullOrWhiteSpace(PlayerStats.Name) ? p.Name : PlayerStats.Name;
        Profile.ProfileClosed += ()  => Party.LocalCharacterName = null;
        PlayerStats.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Game.PlayerStats.Name) && !string.IsNullOrWhiteSpace(PlayerStats.Name))
                Party.LocalCharacterName = PlayerStats.Name;
        };
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
        // Auto travel-cost mode is realm-aware, so a game-data set swap that
        // changes realm must rewire the model (ReadSection is null-safe when
        // no profile is loaded — it falls back to the Auto default).
        GameData.ActiveSetChanged += _ => ApplyAutoLairFromActiveProfile();

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
        // Per-set flavor-adjective vocabulary the room classifier strips ("large
        // giant rat" → "giant rat"). Defaults to the built-in stock list; a
        // custom realm's edits persist per set. Reloads on every set switch.
        FlavorPrefixes = new FlavorPrefixStore(Log);
        GameData.ActiveSetChanged += FlavorPrefixes.Load;
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

        // ItemSourceIndex — reverse item-acquisition (containers + textblock
        // giveitem awards) for the Game Data browser. Reads TBInfo's typed
        // entries, so it's constructed after the store above. Lazy and
        // self-invalidating, so there's no ActiveSetChanged subscription to wire.
        ItemSources = new ItemSourceIndex(GameData, TBInfo, Log);

        // RoomFloorItemIndex — the room→floor-item (`roomitem`) mapping for the
        // Navigation Room Info panel. Reads TBInfo's typed entries like ItemSources;
        // lazy and self-invalidating, so no ActiveSetChanged subscription.
        RoomFloorItems = new RoomFloorItemIndex(GameData, TBInfo, Log);

        // Shared item-record opener — opens the item edit dialog by Number from any
        // surface (the Item Finder's double-click), reusing the browser's read-only
        // view assembly. Deps all constructed above; charm read live off PlayerStats.
        ItemRecord = new ItemRecordDialogService(
            GameData, Resolver, Dialogs, ItemOverlaySeed, ItemSources);

        // Room graph — seeded from the active set's Rooms.json every time the
        // set switches. Built once per swap; consumers hold typed Room
        // references for the lifetime of the set. Takes TBInfo (loaded above)
        // so the build can promote CMD-teleport-shadowed door exits to Teleport,
        // and SpellCatalog so a cast-based CMD teleport becomes a routable edge.
        RoomGraph = new Game.Map.RoomGraphManager(GameData, Log, TBInfo, SpellCatalog);
        GameData.ActiveSetChanged += RoomGraph.OnActiveSetChanged;
        if (GameData.ActiveSet is not null)
            RoomGraph.OnActiveSetChanged(GameData.ActiveSet);

        // Quest name / visibility overlay — a BBS-tier file, reloaded on BBS change
        // (the store subscribes to Profile.BbsPinApplied / ProfileClosed via the
        // ResolveActiveBbs provider). The mechanical step + bonus data the Quest
        // Status tab shows is crawled from TBInfo at runtime, not stored here.
        Quests = new QuestStore(Profile, ResolveActiveBbs, Log);

        // Boss catalog — realm-wide list (seed + per-set overlay); timer values are
        // looked up from game data at runtime. Reloads its overlay on set change.
        Bosses = new BossStore(Log);
        GameData.ActiveSetChanged += Bosses.OnActiveSetChanged;
        if (GameData.ActiveSet is not null)
            Bosses.OnActiveSetChanged(GameData.ActiveSet);

        // Persisted boss kill-times — realm-wide like the catalog. Kill detection is
        // wired later (needs MonsterDeath + RoomTracker); here we just load the
        // active set's saved timers so a restart resumes mid-countdown.
        BossTimers = new BossTimerStore(Bosses, GameData, Log);
        GameData.ActiveSetChanged += BossTimers.OnActiveSetChanged;
        if (GameData.ActiveSet is not null)
            BossTimers.OnActiveSetChanged(GameData.ActiveSet);
        // Cleanup-boss DEAD/ALIVE state reads the active BBS's nightly-cleanup time.
        BossTimers.SetCleanupConfig(ResolveBossCleanupConfig);

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

        // RoomHazardIndex — room-entry Spell → item(s) that make the room safe,
        // from Rooms/Spells/Items/TBInfo. Feeds the walker's hazard-gating pass.
        RoomHazards = new RoomHazardIndex(GameData, Log);
        GameData.ActiveSetChanged += RoomHazards.OnActiveSetChanged;
        if (GameData.ActiveSet is not null)
            RoomHazards.OnActiveSetChanged(GameData.ActiveSet);

        // RoomBuffStripIndex — room-entry Spell that removes/dispels buffs on
        // entry, from Rooms/Spells. Feeds CastingDirector's buff-suppression gate.
        RoomBuffStrip = new RoomBuffStripIndex(GameData, Log);
        GameData.ActiveSetChanged += RoomBuffStrip.OnActiveSetChanged;
        if (GameData.ActiveSet is not null)
            RoomBuffStrip.OnActiveSetChanged(GameData.ActiveSet);

        // Room tracker. Resets to Unknown on every
        // graph reload because per-room references are invalidated
        // when the active set rebuilds.
        RoomTracker = new Game.Map.RoomTracker(RoomGraph, Log);
        RoomGraph.GraphReloaded += () => RoomTracker.OnGraphReloaded();

        // Shared engine-level recovery gate. Walker / LoopRunner /
        // AutoLair attach themselves on Start (next commits).
        Recovery = new Game.Map.EngineRecoveryGate(RoomGraph, RoomTracker, Log);
        // Tier-3 look-sweep combat gate: clear the recovery room before peeking
        // (lit) / wait a combat tick for an ambush to reveal (dark). Reads the
        // predicate live so an auto-attack toggle is honoured; the tick drives
        // the "room clear yet?" re-check. CombatTracker is assigned later in
        // init but only read at recovery time, so the lambda is safe here.
        Recovery.SetCombatGate(() => CombatTracker.HasEngageableHostiles);
        Tick.CombatTickElapsed += Recovery.OnCombatTick;

        // Paradigm-only re-sync: on a suspected drift the gate asks this
        // resolver to fire `rm`; its Location: reply hard-locates the tracker
        // and re-anchors the gate, so navigation never falls to the heuristic
        // backtrack / "Lost" dialog on Paradigm. Stock realms have no `rm`, so
        // TryRequestResync returns false and the gate keeps its heuristic path.
        // Reads GameData.ActiveRealm live per-request, so a mid-session set swap
        // is honoured without re-wiring.
        ParadigmResync = new Game.Map.ParadigmPositionResolver(Router, RoomTracker, Recovery, GameData, Log);
        ParadigmResync.ResyncFailed += Recovery.OnAuthoritativeResyncFailed;
        // @where answers from the game's authoritative position on Paradigm: when
        // the heuristic tracker is lost, the handler fires `rm` and replies once
        // the resolver re-anchors, instead of a bare "Location unknown".
        PartyEssentials.SetPositionRefix(ParadigmResync.RequestResyncOnce);
        // Recovery.TryResync is wired below once MazeSolver exists: a maze solve
        // suppresses `rm` so the asylum is driven by the realm-agnostic look-sweep
        // (stock parity) rather than rm short-circuiting the solver's relocalize.

        // Random-teleport maze index. Rebuilds itself on every graph reload (it
        // subscribes to RoomGraph.GraphReloaded in its ctor), so it's built once
        // at app scope like the tracker. The solver that consumes it is built
        // after the Walker below.
        MazeIndex = new Game.Map.TeleportMazeIndex(RoomGraph, Log);

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
        // On every save (including the save-on-close), stamp the live confirmed
        // room as LastKnownRoom so the next session lands where the player actually
        // is — not at the last strict anchor, which lags behind predicted-neighbour
        // moves through same-named rooms.
        Profile.ProfileSaving += _ => RoomTracker.PersistCurrentRoomForSave();
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

        // Blind-move position tracking. A move made while blinded succeeds but
        // starves the room display, printing only "You are blind." (see
        // GAME_MECHANICS.md) — so the move-confirming observation never fires
        // and the map freezes at the source room. NoteBlindMove dead-reckons
        // along the pending move's mapped edge exactly like the dark-room path,
        // but leaves IsInDarkRoom untouched (the player is blind, not the room).
        Router.Subscribe(Services.Patterns.KnownPatterns.BlindMoveStarved,
            _ => RoomTracker.NoteBlindMove());

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
        // Acquirable-gate providers — feed inventory / stats / hazard data into
        // item, ticket, locked-door, and hazard-room routing. Inventory readiness
        // and stat parsing gate each check so an unknown build walks unrestricted
        // (same rule as level / wealth / class above).
        Movement.InventoryReadyProbe = () => Inventory.IsLoaded;
        Movement.ItemCarriedProbe = IsItemCarried;
        Movement.StrengthProvider = () => Stats.HasParsed ? PlayerStats.Strength : (int?)null;
        Movement.PicklocksProvider = () => Stats.HasParsed ? PlayerStats.Picklocks : (int?)null;
        // Same bash ceiling the door FSM uses, so the filter and DoorOpenManager
        // never disagree on whether a strength-gated door is bashable.
        Movement.MaxBashableStrengthProvider = () => MaxStrength.MaxAchievableStrength;
        Movement.RoomEntrySpellProbe = key => RoomGraph.GetRoom(key)?.Spell ?? 0;
        Movement.Hazards = RoomHazards;
        Favorites = new FavoritesStore(GameData, Log);
        GotoHistory = new GotoHistoryStore(Profile);

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
            Router, MonsterMessages, Players, RoomTracker, Log, GameData, FlavorPrefixes);
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

        // Generic color+wording combat-line recognizer — subscribes to the router's
        // per-line dispatch (color-carrying EmittedLine) and classifies each
        // in-combat-window line. Surfaced in the Wire Inspector + bug report.
        CombatClassifier = new Game.Combat.CombatLineClassifier(Router);

        // RoundDamageTracker. shouldWriteTrace reads the Log pane's
        // auto-collect-logs toggle: the on-disk per-round trace is one of the
        // three diagnostic files that switch gates, so it follows AutoCollectLogs
        // rather than the in-memory CombatDiagnostics channel. Both are
        // per-character persisted; the user can flip either from the Log pane.
        RoundDamage = new Game.Combat.RoundDamageTracker(
            Router, PlayerState, Log,
            shouldWriteTrace: () => LogDiagnostics.AutoCollectLogs);
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
        // A reform member who crossed a party-splitting teleport that lands them
        // with a plain "walks into the room from nowhere" (a "go hole"-style CMD
        // teleport, no "blinding flash" line) still needs their withheld re-invite
        // fired on arrival — feed the watcher's classified arrivals to AutoParty.
        RoomEntry.ArrivalObserved += AutoParty.OnPlayerArrival;

        // Mid-room departure watcher. Subscribes to the
        // RoomEntryDeparture pattern + removes the departing monster
        // from the classifier so the Combat gate drops when a fleeing
        // player drags our engaged mob out of the room.
        RoomDeparture = new Game.Combat.RoomDepartureWatcher(Router, RoomClassifier, Log);

        // Monster death watcher. Specific-pattern matches
        // (per-monster DeathLine) + fallback (exp + Combat Off). On a
        // death event the classifier removes the dead entity so
        // CombatManager re-picks correctly instead of being blocked
        // by a stale "still in the list" check against the
        // just-killed mob (the "kobold thief arrived but no attack"
        // bug). Multiple candidates per pattern are normal — shared
        // wordings; we remove ONE matching entry and let the next
        // room re-display correct any cross-variant ambiguity.
        MonsterDeath = new Game.Combat.MonsterDeathWatcher(Router, Log);
        // Boss-timer auto-start. MUST be the FIRST MonsterDied subscriber: it reads
        // the engaged target name (CombatManager.CurrentTarget) live, and a later
        // subscriber (the roster-resync below) clears it via NoteMonsterDied /
        // NoteUnattributedDeath. The in-game signal is "engaged a named monster, it
        // died (awarded exp)"; the room comes from the live tracker (the event
        // carries neither). Fallback deaths (no candidate identity) are attributed
        // through the engaged name, so they're covered too.
        MonsterDeath.MonsterDied += evt =>
            BossTimers.OnMonsterDied(evt, RoomTracker.State.CurrentRoom?.Key, Combat.CurrentTarget);
        // Surface recognized deaths in the Wire Inspector's Classified view (a passive
        // display side-effect) — the exp gained marks the kill.
        MonsterDeath.MonsterDied += evt =>
            CombatClassifier.NoteMonsterDeath(evt.ExperienceGained);
        // Summon-on-death recheck. MUST subscribe to MonsterDied BEFORE the roster-
        // resync handler below: on a kill whose DeathSpell summons, it asserts a
        // hold + sends a CR to re-scan the room, and that hold has to be in place
        // before the resync's RemoveDeadEntity clears the Combat gate and steps the
        // walker (both synchronous). Wire-sender bound per-session by the VM.
        MonsterDeathSummon = new Game.Combat.MonsterDeathSummonIndex(GameData);
        MonsterSummonTargets = new Game.Combat.MonsterSummonTargetsIndex(GameData);
        RoomAwareMonster = new Game.Combat.RoomAwareMonsterResolver(
            GameData,
            // Re-fetch from the graph so the lair / NPC fields are populated even
            // when the tracked room is a lighter snapshot; fall back to the tracked
            // room, and null when we don't know where we are.
            () => RoomTracker.State.CurrentRoom is { } r
                ? RoomGraph.GetRoom(r.Key) ?? r
                : null,
            // Strip the display name's flavor prefix to the base Monsters name so a
            // "short orc lieutenant" matches this room's "orc lieutenant" record.
            RoomClassifier.ResolveBaseName,
            MonsterSpawns, MonsterSummonTargets);
        // Pass 0 of the classifier resolves an observed name against the current room's monsters
        // (NPC + lair + Summoned-By spawns + what those summon) so a homonym pins to the record
        // actually here — engagement + per-monster overrides all inherit the right Number.
        RoomClassifier.SetRoomAwareResolver(RoomAwareMonster.ResolveInCurrentRoom);
        SummonSettle = new Game.Combat.SummonOnDeathSettle(
            MonsterDeath, RoomClassifier, MovementCoordinator, MonsterDeathSummon,
            currentTargetName: () => Combat.CurrentTarget,
            movementActive: () => MovementControl.IsActive,
            log: Log);
        MonsterDeath.MonsterDied += evt =>
        {
            // Every death is the exp + *Combat Off* signal (no per-monster identity,
            // since DeathLine was retired), so we can't drop a specific roster slot:
            // attribute it to whatever we were fighting (CombatManager.CurrentTarget)
            // and nudge a debounced room re-display so the server hands back the true
            // roster — an empty room clears the Combat gate immediately and a survivor
            // is re-picked a beat later, instead of sitting through the ~5s idle-stall
            // tick that would otherwise re-pick the corpse, no-op it, and only then
            // force the re-display.
            Log.Info(Game.Combat.MonsterDeathWatcher.LogCategory,
                "death — forcing roster resync");
            Combat.NoteUnattributedDeath();
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
                ReadSection<Models.Profile.PartySettings>(Profile.Current, "Party"),
            roomAwareResolve: RoomAwareMonster.ResolveInCurrentRoom,
            // Resolve a debuff slot's cast-code to its catalog row (energy cost +
            // targeting scope) so a mis-slotted spell is rejected before it casts.
            resolveSpellByCode: code => Spellbook.FindByCastCode(code));

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

        // Subscribes to RoomTracker.StateChanged HERE — before Walker / LoopRunner
        // below — so on a synchronous dark-room advance it asserts the settle gate
        // (flipping the engines to Paused) before their own StateChanged handlers
        // run SendNextStep. That ordering is what stops the loop from racing past a
        // dark-room fight; see DarkRoomMovementSettle for the full race writeup.
        DarkRoomSettle = new Game.Map.DarkRoomMovementSettle(
            RoomTracker, MovementCoordinator, Log);

        // Lit-room twin: a combat line in an apparently-empty room means a hostile
        // leapt in a beat after the empty render, so CombatManager fires its CR
        // re-display and raises RoomAppearsEmptyDuringCombat. This holds the loop
        // for that beat — the mob surfaces on the CR response and the Combat gate
        // takes over, or the room is truly empty and it clears on that observation.
        // See CombatRedisplaySettle for the full race writeup.
        CombatRedisplaySettle = new Game.Combat.CombatRedisplaySettle(
            Combat, RoomClassifier, MovementCoordinator, Log);

        // In a dark room there's nothing to see, so a CR "where am I" refresh
        // returns only "you can't see anything" — and that stale dark line is
        // dead-reckoned by RoomTracker as a false confirmation of the movement
        // loop's in-flight step, collapsing the dark-room settle window (the loop
        // then double-steps past lairs and drags late-populating monsters). Wire
        // the dark probe so combat's recovery CRs and the idle-stall resync CR are
        // suppressed while blind.
        Combat.SetDarkRoomProbe(() => RoomTracker.IsInDarkRoom);
        CombatTracker.SetDarkRoomProbe(() => RoomTracker.IsInDarkRoom);

        // The Combat → Min/Max Monsters window only makes sense while a
        // walker / loop / auto-lair is actively trying to move us past a
        // room — standing here idle with nothing else going on (freshly
        // logged in, no route queued) should fight back regardless of room
        // population rather than stand undefended. Same probe wired to both
        // so combat's engage decision and the walker's gate never disagree.
        Combat.SetMovementActiveGate(() => Recovery.AttachedEngine is not null);
        CombatTracker.SetMovementActiveGate(() => Recovery.AttachedEngine is not null);

        // Simultaneous-arrival settle: a UI-thread one-shot so a burst of "strides
        // in" arrivals + the room re-display resolve to one engage decision on the
        // full group (rooms nuke-first instead of pecking single-target). Same shape
        // as the walker's voyage scheduler — keeps the Game/Combat layer UI-free.
        Combat.SetArrivalSettleScheduler((delay, callback) =>
        {
            var timer = new Avalonia.Threading.DispatcherTimer { Interval = delay };
            timer.Tick += (_, _) => { timer.Stop(); callback(); };
            timer.Start();
        });

        // Cascade-switch dispatch delay: a UI-thread one-shot so the per-round spell
        // switch waits out the short real-time window a kill's exp / *Combat Off* packet
        // needs to land + drop the target, instead of corpse-casting the alternate at a
        // mob the capping cast just killed. Same one-shot shape as the settle scheduler.
        Combat.SetSwitchDispatchScheduler((delay, callback) =>
        {
            var timer = new Avalonia.Threading.DispatcherTimer { Interval = delay };
            timer.Tick += (_, _) => { timer.Stop(); callback(); };
            timer.Start();
        });

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
            // Don't try to rest while an ON-SIGHT ATTACKER (Enemy) is in the
            // room — it hits us every round and would break the rest. Passive
            // KillOnSight neutrals are deliberately NOT counted here: they never
            // attack until we engage them, so we can rest among the un-engaged ones
            // between kills. The neutral we're actively fighting still blocks rest
            // via InCombat (it's hitting back). HasHostileMonster is Enemy-only.
            hasEngageableHostiles: () => CombatTracker.HasHostileMonster,
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
            findReversePath: (from, to) => Bfs.FindPath(from, to),
            // Defer the flee one UI-thread hop so the round's death line (parsed
            // after the prompt in the same wire read) settles before we commit —
            // a killing blow that empties the room then rests instead of running.
            post: action => Avalonia.Threading.Dispatcher.UIThread.Post(action));

        // Late-wire the classifier's flee probe now that Health exists (it's
        // built after RoomClassifier). While fleeing, a monster that pursues us
        // into the next room must not re-arm the Combat gate — the classifier
        // reads this to keep running instead of halting to fight the pursuer.
        RoomClassifier.FleeProbe = () => Health.IsFleeing;

        // Late-wire the classifier's active-target probe (Combat is built before
        // this but the probe lives on the classifier). On a dark-room advance the
        // classifier resets its accumulated roster but keeps the mob we're
        // fighting, so a live dark fight survives the move while pursuit arrivals
        // stop piling into a phantom roster that would trip the max-monsters gate.
        RoomClassifier.ActiveCombatTargetProbe = () => Combat.CurrentTarget;

        // Re-check the emergency hangup whenever the room's occupants change: a
        // hostile that wanders in or spawns while we're already below the trigger
        // won't touch our own PlayerState, so nothing else would drive the check.
        // Subscribed after CombatTracker (which updates HasHostileMonster in its
        // own EntitiesObserved handler) so this reads the current hostile flag.
        RoomClassifier.EntitiesObserved += _ => Health.ReevaluateEmergencyHangup();

        // Release the post-force-clear rest hold once a room observation re-confirms
        // presence. Subscribed after CombatTracker's own EntitiesObserved handler so
        // HasEngageableHostiles is current when Health re-evaluates: a monster the
        // watchdog's resync CR re-displayed now blocks the rest, an empty room lets
        // it through. Pairs with CombatForceCleared → NoteCombatForceCleared below.
        RoomClassifier.EntitiesObserved += _ => Health.NoteRoomEntitiesReconfirmed();

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
        // isLeaderWaited drives the leader's own "rest while a member @wait-held
        // us" downtime rest; isSelfPoisoned gates BOTH downtime-rest paths off the
        // self-ailment tracker (Conditions is constructed below, so the closure
        // defers the read until Evaluate runs). PartyEssentials.IsPaused is the
        // inbound-@wait state (already honours the leader opt-out upstream); scope
        // it to SelfIsLeader so only the leader rests on a wait.
        Health.SetPartyRoleSync(
            isPartyFollower: () => PartyState.IsInParty && !PartyState.SelfIsLeader,
            requestPartyWait: () => PartyRest.RequestWait(Game.WaitReason.Health),
            requestPartyOk: () => PartyRest.RequestOk(Game.WaitReason.Health),
            isLeaderResting: () => PartyLeaderRest.LeaderIsResting,
            requestPartyHeal: () => PartyRest.RequestHeal(),
            isLeaderWaited: () => PartyState.SelfIsLeader && PartyEssentials.IsPaused,
            isSelfPoisoned: () => Conditions.IsPoisoned);

        // Per-waypoint "do not rest in this room": true while a loop is running
        // and the room we're standing in is one of its waypoints flagged
        // DoNotRest — HealthManager then suppresses the rest hold so the loop
        // advances out. Matched by room key (per-room), so it clears the instant
        // the loop steps into any other room. Loops only.
        Health.SetDoNotRestSelector(() =>
            LoopRunner.State != Game.Map.LoopState.Idle
            && RoomTracker.State.CurrentRoom is { } here
            && LoopRunner.CurrentLoop?.Waypoints is { } wps
            && wps.Any(w => w.DoNotRest && w.Key.Equals(here.Key)));

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
        // ".@held" etc. on say (so other MudPlay clients mirror our state
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

        // Self-confusion bridge — the local side of our own confusion. A
        // confused follower telepaths the leader @wait (AilmentSyncEngine above);
        // a confused leader / solo has that @wait eaten, so their nav kept
        // running and their own chip never lit. This sets the self Confused chip
        // and asserts ConfusionGate, honouring the same Ignore Confusion gate the
        // @wait obeys. Reevaluate() is pinged from the Spells settings apply.
        SelfConfusion = new Game.Conditions.SelfConfusionResponder(
            Conditions, Party, MovementCoordinator,
            readSpells: () => ReadSection<Models.Profile.SpellsSettings>(Profile.Current, "Spells"),
            log: Log);

        // Self-held bridge — the same local-hold pattern for a knockdown /
        // MovementPrevented state (no opt-out; a knockdown always holds).
        SelfHeld = new Game.Conditions.SelfHeldResponder(
            Conditions, Party, MovementCoordinator, log: Log);

        // Self-ailment chip bridge — the pure-chip sibling of the two responders
        // above for poison / blindness / disease (no movement gate). Lights the
        // self party-window chip off ConditionTracker so our own poison shows the
        // same way an other member's announced poison does.
        SelfAilmentChip = new Game.Conditions.SelfAilmentChipResponder(
            Conditions, Party, log: Log);

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
        CastDirector.SetTriggeredRestGate(() => Health.IsRecoveringRest);
        // Buff-strip-room gate — the current room casts a buff-removal spell on
        // entry (RemovesSpell / DispellMagic), so suppress buffs here rather than
        // burn mana on a buff the room tears straight back off.
        CastDirector.SetBuffStripRoomGate(
            () => RoomBuffStrip.StripsBuffs(RoomTracker.State.CurrentRoom?.Spell ?? 0));
        // Suppress ALL auto-casts while the `train stats` full-screen menu has
        // character-mode input armed — otherwise a cast's letters get typed raw
        // into the character-creation form (the "bles" family-name corruption).
        // IsInputMenuActive is the realm-independent (command-driven) signal.
        CastDirector.SetInputCaptureGate(() => TrainerMenu.IsInputMenuActive);
        // Buff-duration recast model. A buff cast (self or
        // party) is confirmed, then suppressed until it's within the
        // pre-expiry recast window. BuffInfoByShort maps a 4-letter cast
        // code to its CasterMessage confirmation template + computed
        // duration (SpellCalculator.Duration at the live level);
        // ShortFromAppliedRecord maps a fired AppliedMessage record back
        // to the cast code so a confirmed self-buff starts its timer.
        CastDirector.SetBuffDurationSources(BuffInfoByShort, ShortFromAppliedRecord);
        // A fresh character starts with no buffs assumed — clear any timers carried over
        // (e.g. paused from a prior character's disconnect) so a character switch doesn't
        // resurrect the old character's buffs. A same-character reconnect does NOT reload
        // the profile, so its paused timers survive to be resumed.
        Profile.ProfileLoaded += _ => CastDirector.ResetBuffTracking();
        // Party-bless slots store class numbers; PartyMember.Class is a
        // class name — resolve via the active set's Classes table.
        CastDirector.SetClassResolver(SpellCatalog.ResolveClassName);
        // A party-wide buff (Spells.Targets = Full / Divided Party Area) is
        // cast once for the whole party; the picker checks this to skip the
        // per-member loop.
        CastDirector.SetPartyWideBuffCheck(IsPartyWideBuff);
        // Self-buff supersession: in a party, a configured party-wide buff that removes a
        // self-buff (RemovesSpell) covers us, so the director stops self-casting the
        // removed one — the Buff Watchdog shows that slot "covered by" the party buff.
        CastDirector.SetSelfBuffCoverage(SelfBuffCoverage);
        // Downed-ally rescue heal. A dropped ally leaves `par`, so PickPartyHeal's
        // roster walk can't see them — the AllyDroppedHandler feeds each aided
        // downed ally back in here as the top-priority name-targeted heal until
        // they recover / rejoin.
        CastDirector.SetDownedAllyProvider(() => AllyDropped.AidedDownedGivenNames());
        // Free the once-per-round between-round cast slot on the combat ROUND TICK —
        // TickEngine's 5s heartbeat (refreshed by damage lines), NOT *Combat Off*.
        // *Combat Off* fires per kill, so in a multi-mob room it lands several times a
        // round and would re-open the slot mid-round; the combat tick is the actual
        // round cadence. Subscribed BEFORE CastDirector.OnCombatTick below so the slot
        // is freed before this round's between-round evaluation runs.
        Tick.CombatTickElapsed += CastDirector.NotifyRoundComplete;
        Tick.CombatTickElapsed += CastDirector.OnCombatTick;

        // Mana-regen roll-spell reroll (Paradigm only). AbilBreakdown parses
        // `abil 145`; ManaRegen reads its rolled `spells:` slice after each
        // nature-tap / mana-flux landing and recasts a below-threshold roll up
        // to the cap, hard-stopping at the buff mana floor. The abil query goes
        // out on the raw engine sender (bound in the main VM); the RECAST is
        // staged on CastDirector so it runs through the same between-round
        // priority pass as every other 0-energy cast — it competes by
        // PriorityBuffing against a due heal/cure and spends the one-cast-per-round
        // slot, rather than firing directly on the wire and bypassing both.
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
            recast: shortCode => CastDirector.RequestManaRegenReroll(shortCode),
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
        Combat.SetCombatSpellCaster(Cast, () => (PlayerState.Ma, PlayerState.MaxMa),
            () => (PlayerState.Hp, PlayerState.MaxHp));
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
        // On a fresh engage the combat engine runs this in-between evaluator first,
        // so a due survival cast — or, if none, the configured debuff — fires
        // BEFORE the attack rather than a round later (the "fire the debuff before
        // the attack spell" ordering). Only exercised when a debuff is actually
        // due, so a normal engage is untouched.
        Combat.SetInBetweenEvaluator(CastDirector.Evaluate);
        Combat.SetBetweenRoundSlotMarker(CastDirector.MarkBetweenRoundSlotUsed);
        // A between-round survival cast stops our auto-attack; let the combat
        // engine resume the weapon attack on the resulting *Combat Off*
        // instead of idling until the next round.
        CastDirector.CastFired += Combat.NoteBetweenRoundCast;
        // The round after a survival cast belongs to the attack spell it
        // interrupted — CastDirector must sit out until that resume lands, or it
        // just re-claims the round the instant HP dips again and the attack never
        // gets a turn back.
        CastDirector.SetAttackOwedGate(() => Combat.IsSpellAttackOwed);
        // Same resume, but for a HAND-typed cast: a manual cast-code never
        // routes through CastDirector, so sniff the wire for one and arm the
        // identical signal. A cast-code is any Spells.Short in the active
        // class's available list.
        OutboundCast = new Game.Combat.OutboundCastObserver(
            isCastCode: c => Spellbook.FindByCastCode(c) is not null,
            // A hand-typed cast feeds BOTH the combat resume signal and the buff-recast
            // clock: NoteManualBuffCast arms the timer (by cast code) for a hand-cast buff
            // so the Buff Watchdog + recast engine track it the same as an engine cast.
            onManualCast: (c, target) => { Combat.OnManualCastObserved(c, target); CastDirector.NoteManualBuffCast(c); });
        // Classify a hand-typed cast: a combat spell (round energy 1–1000) is the user
        // taking the round's attack — a user override — while an in-between spell (heal
        // / buff / cure, energy 0) keeps the resume-after-cast. See CombatSpellIndex.
        CombatSpells = new Game.Combat.CombatSpellIndex(GameData);
        Combat.SetCombatSpellPredicate(CombatSpells.IsCombatSpell);
        // A hand-typed PHYSICAL attack (a / at / att / aa / bash / smash / sm / sma / bs)
        // is likewise a user override — the observer forwards every recognised verb and
        // Combat drops its own swing's echo via a one-shot claim.
        OutboundAttack = new Game.Combat.OutboundAttackObserver(
            (verb, target) => Combat.NoteAttackCommandObserved(verb, target));
        Tick.CombatTickElapsed += Combat.OnCombatTick;
        // Idle-stall watchdog: the 1s heartbeat (not the coarse 5s combat tick)
        // drives CombatStateTracker's stuck-gate recovery so it fires within a
        // second of its threshold — a final kill that never triggered a resync
        // re-display is caught and cleared in ~6s total instead of ~10-15s.
        Tick.HeartbeatElapsed += CombatTracker.OnCombatTick;

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
        // Combat spends stealth (attacking reveals you) but emits no line the FSM
        // can key on, so a room cleared by winning leaves IsSneaking stale-true.
        // Reset it the instant combat ends — before the Combat gate releases the
        // walker — so the pre-move re-sneak re-establishes stealth for the step out.
        CombatTracker.CombatSpentStealth += () => Stealth.NoteCombatEndedStealthReset();

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

        // Passive-neutral recovery hold: engage a KillOnSight neutral only once we're
        // at/above the rest trigger, so we can rest between kills (a neutral won't
        // attack until we hit it). Never holds when an on-sight attacker is present.
        Combat.SetNeutralRecoveryHold(
            recoveryPending:     () => Health.IsRecoveringRest,
            hasAttackingHostile: () => CombatTracker.HasHostileMonster,
            clearInCombat:       CombatTracker.ClearInCombatForRecoveryHold);
        Health.SetRecoveryCompleteCallback(Combat.ResumeAfterRecovery);

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

        // Drain-life eligibility — a drain spell can only affect a living, non-undead
        // target; the index tells the chooser which mobs to skip (fall back to the
        // normal attack). Fails open when game data is silent.
        MonsterLife = new Game.Combat.MonsterLifeIndex(GameData);
        Combat.SetDrainEligibility(MonsterLife);

        // Per-monster spell overrides store a Spell.Number; the engine casts the
        // Short. Wire the resolver so the chooser can substitute a numbered
        // override in place of the global Combat-tab cast-code slot.
        SpellShort = new Game.Combat.SpellShortIndex(GameData);
        Combat.SetSpellShortResolver(SpellShort.ShortByNumber, SpellShort.NumberByShort);

        // Shared monster-record opener — opens the monster edit dialog by Number from any
        // surface (the Navigation Room Info panel), reusing the browser's read-only "Other
        // Info" assembly (MonsterMdbInfoBuilder). Constructed here so RoomGraph + SpellShort
        // (both above) are ready.
        MonsterRecord = new MonsterRecordDialogService(
            GameData, Resolver, Dialogs, MonsterOverlaySeed, RoomGraph, TBInfo, SpellShort);

        // Shared spell-record opener — opens the spell's Message / Game-Data dialog by Number
        // (the Room Info room-spell link), reusing the Spells tab's message-link flow + the
        // shared SpellInfoRowsBuilder. Messages (2366) is ready.
        SpellRecord = new SpellRecordDialogService(GameData, Messages, Dialogs);

        // Item-side mirror of SpellRecord — opens an item's on-use / proc message
        // editor from the item dialog's Message section.
        ItemMessage = new ItemMessageDialogService(GameData, Messages, Dialogs);

        // Light catalogue + live carried illumination. The snapshot provider is
        // deferred (Inventory is assigned later in this method), so reading
        // PlayerIllumination.Current at tooltip / route time sees the live dump.
        Lights = new Game.Light.LightItemIndex(GameData);
        RoomLightSpell = new Game.Light.RoomLightSpellResolver(GameData, Lights);
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

        // A passive neutral the user hand-engaged fights like a hostile until it dies —
        // so the walker gate must hold for it too, matching CombatManager's attack
        // takeover. Share CombatManager's per-instance set so the two can't disagree.
        CombatTracker.SetUserEngagedInstanceGate(raw => Combat.IsUserEngagedInstance(raw));

        // Keep the walker gate's room-population read in sync with
        // CombatManager's own Min/Max monster skip, so a too-crowded room
        // releases the walker instead of holding it while combat refuses to
        // engage — see SetMonsterCountWindow.
        CombatTracker.SetMonsterCountWindow(
            () => ReadSection<Models.Profile.CombatSettings>(Profile.Current, "Combat"));

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

        // Break-before-run: turning auto-attack OFF mid-fight releases the Combat
        // gate so the walker resumes — send `break` first when the user has
        // CombatSettings.BreakBeforeFleeing on, mirroring the flee path's disengage.
        CombatTracker.SetBreakBeforeRunGate(
            () => ReadSection<Models.Profile.CombatSettings>(Profile.Current, "Combat").BreakBeforeFleeing);
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
        // Same in-game gate resumes the party wall-clock cadences (par poll +
        // @health) once we're back in the realm after a disconnect — they were
        // suspended on the drop so nothing leaks into the login-menu nav.
        PromptScanner.PromptObserved += _ => PartyPoller.NotifyEnteredRealm();
        PromptScanner.PromptObserved += _ => PartyProbe.NotifyEnteredRealm();
        // Same in-game gate resumes frozen buff timers after an unexpected drop: the
        // disconnect handler paused them (kept the remaining), and this shifts each
        // forward by the offline gap so the recast clock picks up where it left off.
        // Idempotent — no-op unless a drop paused the timers.
        PromptScanner.PromptObserved += _ => CastDirector.ResumeBuffTimers();
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

        // HpMaHistoryTracker. Accumulates per-loop-step min/max HP + mana for the
        // Session Stats "HP/MA History" band graph. Its inputs need LoopRunner
        // (built later in the movement layer) to gate sampling and supply the step
        // index, so the prompt-scanner subscription + loop-start reset are wired
        // in the LoopRunner block below; here we just construct it and clear on the
        // connect / character-switch boundary like the other session trackers.
        HpMaHistory = new Game.Combat.HpMaHistoryTracker();
        Profile.ProfileLoaded += _ => HpMaHistory.Reset();

        // TransactionHistory. A per-session ledger of cash/item
        // offloads: bank `dep`osits (AutoDeposit.Deposited) and stash-room
        // `hide`s (Stash.StashExecuted), wired to their events below. Feeds the
        // Session Stats → Transaction history window; reset on the same session
        // boundary as the other session-stats trackers.
        TransactionHistory = new Game.Cash.TransactionHistoryTracker();
        Profile.ProfileLoaded += _ => TransactionHistory.Reset();

        // Rolling per-character disk logs for the Conversation window +
        // Transaction history. Reads its own char-tier Talk settings; switches
        // files on profile / BBS change.
        SessionLog = new SessionLogService(
            Profile, Chat, ChatHistory, TransactionHistory, Log,
            () => ReadSection<Models.Profile.TalkSettings>(Profile.Current, "Talk"));

        // @reset — a party member zeroes our session-stats trackers (the same
        // wipe as the window button / connect boundary). Constructed here, after
        // the session-stats trackers exist; RemoteCommands was built upstream.
        SessionReset = new Game.Remote.SessionResetHandler(
            RemoteCommands, CombatSession, TimeAnalysis, SessionActivity, Log);

        // Read-only progression queries — @exp / @level report against the
        // PlayerStats snapshot (from `stat` / `exp`) and the session
        // exp-rate tracker. No wire output, so no sender to bind.
        ExperienceQuery = new Game.Remote.ExperienceQueryHandler(
            RemoteCommands, PlayerStats, SessionActivity);

        // Per-BBS runic-currency naming. Reads the active BBS's RunicCurrencyName
        // live (via ResolveActiveBbs) and re-reads on profile / BBS swap. Injected
        // into every cash parser / command builder so a board-renamed runic word
        // is matched on the wire and sent back on outgoing get/drop/hide commands.
        Currency = new Game.Cash.CurrencyNaming(() => ResolveActiveBbs()?.RunicCurrencyName);
        Profile.ProfileLoaded += _ => Currency.Refresh();
        Profile.BbsPinApplied += _ => Currency.Refresh();

        // Room-floor loot snapshot from the "You notice <list> here." survey,
        // cash filtered out. Feeds @what (read) and @get-all (get each).
        // LineExtractor attached + OnRoomChanged wired below (and in MainWindowVM).
        // isKnownItem gives the cash filter an authoritative item-table
        // tiebreaker so a stacked denomination-named item ("2 gold key") isn't
        // mistaken for coin (see IsCashEntry).
        GroundItems = new Game.Inventory.GroundItemTracker(Router, Currency,
            isKnownItem: IsKnownGroundItem);
        // Auto-recover reads the floor survey to confirm our corpse is in the room
        // before sending `recover corpse` (and arms off its SurveyUpdated event).
        DeathRecovery.AttachGroundItems(GroundItems);

        // Read-only inventory queries — @wealth / @enc / @have report off the
        // InventoryManager snapshot; @what reports the GroundItems survey. No
        // wire output either.
        InventoryQuery = new Game.Remote.InventoryQueryHandler(RemoteCommands, Inventory, GroundItems, Currency);

        // @timer — read-only report of the boss respawn timers being tracked. Reads
        // the boss catalog + persisted kill-times; no wire output beyond its reply.
        BossTimerQuery = new Game.Remote.BossTimerQueryHandler(RemoteCommands, Bosses, BossTimers, GameData);
        DeathQuery = new Game.Remote.DeathQueryHandler(RemoteCommands, () => DeathRecovery.Records);

        // Write-side inventory / cash actions — @get-all / @drop-all /
        // @deposit-all / @share emit get / drop / dep / with / give on the wire.
        // Keep-on-hand floors come from the per-character Cash settings;
        // wire-sender bound in MainWindowVM.
        InventoryAction = new Game.Remote.InventoryActionHandler(
            RemoteCommands,
            Inventory,
            GroundItems,
            PartyState,
            readCash: () => ReadSection<Models.Profile.CashSettings>(Profile.Current, "Cash"),
            naming: Currency);

        // Receive side of @heal — a configured party-healer polls `par` on
        // request so CastingDirector re-evaluates its party-heal thresholds
        // against fresh member HP. Emit side is the follower flee-substitute
        // wired into Health.SetPartyRoleSync above. Wire-sender bound in
        // MainWindowVM.
        Heal = new Game.Remote.HealCommandHandler(
            RemoteCommands,
            readParty: () => ReadSection<Models.Profile.PartySettings>(Profile.Current, "Party"))
        {
            // Same trainer-screen suppression as the timed par poll — an @heal
            // that fires while parked on the stat form would corrupt the last name.
            IsInTrainerMenu = () => TrainerMenu.MenuOwnsKeyboard,
        };

        // Item-cast buffs. A Bless slot may hold a #-token naming an
        // unlimited-use cast item (surfaced in the Spell Book); the director
        // fires it by wielding + using the item, then re-wielding the displaced
        // weapon (read from Inventory's last `i` dump). Duration drives the
        // recast clock. Wire-sender bound in MainWindowViewModel.
        ItemCast = new Game.Spells.ItemCastSequencer(
            () => Spellbook.GetCastItems(), () => Inventory.Snapshot, Log, DesiredEquipSlotItem,
            // Stand auto-equip off the slot the item-cast borrows so its own restore
            // isn't doubled by the rest-break the swap triggers (AutoEquip is built
            // just below; this lambda reads it at fire time). See NoteItemCastSwap.
            onSwap: () => AutoEquip?.NoteItemCastSwap());
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

        // Let an auto-fire gear-set apply defer the weapon slot to combat while it
        // holds a per-monster alternate-weapon override, so the Default set's
        // combat-entry trigger can't clobber the swap (the weapon-flap report).
        Equipment.SetCombatWeaponOwnershipProbe(() => Combat.IsWeaponOverrideActive);

        // Confusion-fumble retry: a fumbled attack is consumed without engaging,
        // so re-send the last swing on every fumble line (ConditionTracker gates
        // the raw signal to the confusion record; Combat gates on an active fight).
        Conditions.ActionFailed += _ => Combat.OnActionFailed();

        // CashManager. Subscribes to cash-on-ground
        // / cash-picked-up / cash-dropped patterns and dispatches
        // per-currency policy. AutoGetCash gates the whole engine
        // (Settings -> General toggle + toolbar Toggle command).
        Cash = new Game.Cash.CashManager(Router,
            readSettings: () => ReadSection<Models.Profile.CashSettings>(Profile.Current, "Cash"),
            isEnabled: () => ReadAutoModeFlag(d => d.AutoGetCash),
            // Shared Cash + Items timing toggle: defer ground / corpse / notice
            // cash until the room clears so a get between kills doesn't burn the
            // pre-attack round. hasEngageableHostiles reads CombatTracker, which
            // subscribed to EntitiesObserved first, so the flush below sees a
            // current flag.
            collectAfterCombatFinished: () =>
                ReadSection<Models.Profile.CashSettings>(Profile.Current, "Cash")
                    .CollectAfterCombatFinished,
            hasEngageableHostiles: () => CombatTracker.HasEngageableHostiles,
            getSnapshot: () => Inventory.Snapshot,
            isPeekSuppressed: () => RoomTracker.IsPeekSuppressed(),
            log: Log,
            naming: Currency,
            // Same item-table tiebreaker the ground tracker uses — a stacked
            // denomination-named item ("2 gold key") isn't collected as coin.
            isKnownItem: IsKnownGroundItem,
            // Defer the room-survey collect decision one tick so the room's later
            // "Also here:" hostile line has been parsed before we choose to get vs
            // hold (report stock-20260730-193107).
            post: action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        // Reset held tallies on profile swap — prior character's
        // counts aren't relevant to the new one.
        Profile.ProfileLoaded += _ => Cash.ResetTallies();
        Cash.SetAcquisitionGate(Acquisition);
        Cash.SetRoomRedisplay(RoomRedisplay);
        // Combat-finished flush: every room-entity observation re-checks the
        // deferred collect queue. CombatStateTracker's handler subscribed in its
        // constructor (well before here), so it runs first and the hostile flag
        // is current. Mirrors AutoGetItems.OnRoomObserved.
        RoomClassifier.EntitiesObserved += _ => Cash.OnRoomObserved();
        // Feed confirmed coin pickups into the Session Stats
        // currency-collected tally, converting each denomination to its copper
        // value so mixed currency streams fold into one figure.
        Cash.CoinCollected += (currency, count) =>
            SessionActivity.NoteCurrencyCollected(
                Game.Inventory.CurrencyHoldings.ToCopper(Currency.Canonicalize(currency), count));
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
        // Paradigm (ParaMud) accepts one counted action per item — the loot engines
        // batch a pile into a single get/drop/hide/sell/buy N <item>; Stock can only
        // act on one at a time. Read live so an active-set swap flips it.
        Func<bool> onParadigm = () => GameData.ActiveRealm == Game.RealmType.ParaMud;

        Stash = new Game.Cash.StashRoomManager(Profile,
            readCash: () => ReadSection<Models.Profile.CashSettings>(Profile.Current, "Cash"),
            getSnapshot: () => Inventory.Snapshot,
            resolveAutoStashItem: ResolveAutoStashItem,
            isEnabled: () => ReadAutoModeFlag(d => d.AutoGetCash),
            log: Log,
            naming: Currency,
            isParadigm: onParadigm);
        // Count stash-room hides toward the Session Stats stashed/deposited figure
        // (copper value across the dispatched coins). The transaction-history
        // ledger is NOT fed here — it sources from the server's own `You hid …` /
        // `You deposit …` echoes below, so a hand-typed stash is recorded too.
        Stash.StashExecuted += dispatch =>
        {
            long copper = 0;
            foreach ((string currency, long amount) in dispatch.Currencies)
                copper += Game.Inventory.CurrencyHoldings.ToCopper(Currency.Canonicalize(currency), amount);
            SessionActivity.NoteCurrencyStashed(copper);
        };

        // Transaction-history ledger sources — the server-confirmation echoes,
        // which fire for a manual `dep` / `hide` and an automated reroute alike
        // (so both are recorded), and arrive one per denomination / item:
        //   coin stash   -> CashManager.CoinHidden       ("You hid N <coin>.")
        //   item stash   -> InventoryManager.ItemHidden  ("You hid <item>.")
        //   bank deposit -> InventoryManager.BankDeposited ("You deposit …", wrap-merged there)
        // Each echo captures the room it fired in — the stash room for a hide,
        // the bank room for a deposit — so the ledger records where excess went.
        Cash.CoinHidden += (currency, count) =>
            TransactionHistory.NoteStash(
                new[] { (currency, (long)count) }, Array.Empty<string>(), CurrentRoomLabel());
        Inventory.ItemHidden += item =>
        {
            // An auto-discard offload uses `hide <item>` in HideMode — that's a
            // discard, not a stash, so it claims its own confirmation here and is
            // kept out of the ledger. Manual / stash-room hides were never
            // registered, so they still record.
            if (AutoDiscard.TryConsumeSuppressedHide(item)) return;
            TransactionHistory.NoteStash(
                Array.Empty<(string, long)>(), new[] { item }, CurrentRoomLabel());
        };
        Inventory.BankDeposited += copper =>
            TransactionHistory.NoteBankDeposit(copper, CurrentRoomLabel());

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
            heldCount: CountItemHeld,
            encumbrance: () => Inventory.Snapshot.Encumbrance,
            itemEncGates: () =>
            {
                Models.Profile.CashSettings c =
                    ReadSection<Models.Profile.CashSettings>(Profile.Current, "Cash");
                return (c.SkipGetItemIfMakesLight, c.SkipGetItemIfMakesMedium, c.SkipGetItemIfMakesHeavy);
            },
            log: Log,
            isParadigm: onParadigm);
        AutoGetItems.SetAcquisitionGate(Acquisition);
        AutoGetItems.SetRoomRedisplay(RoomRedisplay);
        // Combat-finished flush: every room-entity observation re-checks
        // the deferred queue (CombatStateTracker's handler ran first, so
        // the hostile flag is current).
        RoomClassifier.EntitiesObserved += _ => AutoGetItems.OnRoomObserved();

        // Force-clear flush: the normal end-of-fight flushes deferred cash/item
        // pickups off the clean room re-look that follows a kill, but a FORCE-clear
        // (idle-stall watchdog / Reset States) produces no such observation — so a
        // pickup deferred "until combat clears" would strand the Acquisition gate and
        // wedge the walker (report paradigm-20260814-131551). Re-run both engines'
        // deferred flush now that the gate is down (HasEngageableHostiles is false).
        CombatTracker.CombatForceCleared += () =>
        {
            Cash.OnRoomObserved();
            AutoGetItems.OnRoomObserved();
        };

        // The force-clear is optimistic (a resync CR re-display re-confirms a beat
        // later). Hold resting until that re-confirm so a monster still in the room
        // doesn't get a `rest` sent at it (paradigm-20260814-225055).
        CombatTracker.CombatForceCleared += Health.NoteCombatForceCleared;

        // A disconnect can strand the Acquisition gate's deferred-collect hold
        // (cash/items queued mid-fight), pausing the loop until a manual `rm`. On the
        // first in-game prompt after a reconnect, drop those stale holds so the loop
        // resumes from the (correct, still-confirmed) room. Armed from the Connected
        // handler; a no-op when nothing was deferred. Both engines share the gate.
        DeferredCollectResume = new Game.Map.DeferredCollectReconnectReleaser(
            PromptScanner,
            releaseDeferred: () =>
            {
                Cash.CancelDeferredCollect("reconnect");
                AutoGetItems.CancelDeferredCollect("reconnect");
            },
            Log);

        // Loot-automation engines (auto-discard / auto-buy / auto-sell). All
        // three share the single AutoGetItems master toggle — the per-item
        // ItemOverlay flags (AutoDiscard / AutoBuy / AutoSell) are the real
        // per-item gate; "Auto Get Items" is the umbrella item-automation
        // switch and the group has no separate Action-menu toggles.
        AutoDiscard = new Game.Inventory.AutoDiscardManager(Router,
            carriedItems: () => Inventory.Snapshot.CarriedItems,
            resolve: ResolveAutoDiscardItem,
            isEnabled: () => ReadAutoModeFlag(d => d.AutoGetItems),
            log: Log,
            isParadigm: onParadigm);
        // Auto-discard re-evaluates the pack on every inventory change — the
        // seam that surfaces chest dumps and freshly collected loot.
        Inventory.Changed += AutoDiscard.OnInventoryChanged;

        AutoBuy = new Game.Inventory.AutoBuyManager(Router,
            resolve: ResolveAutoBuyItem,
            countCarried: CountItemCarried,
            isEnabled: () => ReadAutoModeFlag(d => d.AutoGetItems),
            log: Log,
            isParadigm: onParadigm);

        AutoSell = new Game.Inventory.AutoSellManager(Router,
            carriedItems: () => Inventory.Snapshot.CarriedItems,
            resolve: ResolveAutoSellItem,
            isEnabled: () => ReadAutoModeFlag(d => d.AutoGetItems),
            log: Log,
            isParadigm: onParadigm);

        AutoOpen = new Game.Inventory.AutoOpenManager(
            carriedItems: () => Inventory.Snapshot.CarriedItems,
            resolve: ResolveAutoOpenItem,
            isEnabled: () => ReadAutoModeFlag(d => d.AutoGetItems),
            isLoaded: () => Inventory.IsLoaded,
            log: Log);
        // Auto-open re-evaluates the pack on every inventory change — the seam
        // that surfaces a container the moment it enters inventory.
        Inventory.Changed += AutoOpen.OnInventoryChanged;
        // Settings → Talk auto-greet. Self name resolves through the
        // PartyManager's LocalCharacterName first (set on connect), then
        // the loaded profile name as a fallback. Wire-sender bound by
        // MainWindowViewModel after telnet connects.
        Greet = new Game.GreetManager(RoomClassifier, Players, Party.State,
            selfNameProvider: () => Party.LocalCharacterName ?? Profile.Current?.Name);
        // Settings → Talk reactive-look automation. Shares Greet's self-name
        // resolution; RoomEntry (built earlier) supplies the arrival hook.
        // Wire-sender bound by MainWindowViewModel after telnet connects.
        PlayerLook = new Game.PlayerLookManager(Router, RoomEntry, Party.State,
            selfNameProvider: () => Party.LocalCharacterName ?? Profile.Current?.Name);
        // Players Seen log. Records off the same room-presence hooks (Also-here
        // classification + room walk-ins) and shares the self-name resolution;
        // persists the aggregated rows on the loaded character's profile. Owns no
        // room-source subscriptions of its own — we wire the two hooks here.
        PlayerSightings = new Game.PlayerSightingTracker(
            () => RoomTracker.State.CurrentRoom, Profile,
            selfNameProvider: () => Party.LocalCharacterName ?? Profile.Current?.Name);
        RoomClassifier.EntitiesObserved += PlayerSightings.NoteAlsoHere;
        RoomEntry.ArrivalObserved += PlayerSightings.NoteArrival;
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
            // The route picker's explicit "obtain then cross" pick forces a per-walk
            // obtain regardless of the global search-if-needed preference — the pick
            // IS the consent, so open the demand gate whenever a forced obtain is live.
            isEnabled: () =>
                Resolver.Resolve<Models.Profile.OtherSettings>("Other").SearchRoomsIfItemNeeded
                || _forcedPathObtain.Count > 0,
            log: Log);
        Inventory.Changed += PathItemDemand.OnInventoryChanged;

        // Party-inventory awareness (PR E). The probe broadcasts @have and
        // aggregates the party's replies; the gate sits ahead of the demand
        // tracker on the walker's announce seam. For an item flagged
        // "auto-obtain for path → provision party" (per-item overlay), when
        // grouped a needed per-member copy we lack is probed first — if a member
        // has a spare it's handed over (give) and no need is posted; a shortfall
        // forwards to PathItemDemand so search / shop / drops still cover it.
        // Solo, or an unflagged item, passes straight through. The probe
        // self-subscribes to ChatRouter for replies; the give hand-off's
        // wire-sender is bound by MainWindowViewModel after connect.
        PartyInventory = new Game.Remote.PartyInventoryProbe(PartyBroadcaster, Chat, PartyState, Log);
        PartyPathItemGate = new Game.Map.PartyPathItemGate(
            isCarried: IsItemCarried,
            selfCount: CountItemCarried,
            query: (id, name) => PartyInventory.QueryAsync(id, name),
            itemName: ItemNames.GetName,
            isEnabled: IsAutoObtainForPath,
            perPersonQuantity: PathPerPersonQuantity,
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

        // Per-walk forced-obtain (the route picker's "obtain then cross" choice):
        // drop an item from the override once it's acquired. The abandon-clear on
        // Walker.Event is wired after the walker is constructed (see below).
        Inventory.Changed += () => _forcedPathObtain.RemoveWhere(IsItemCarried);

        // Party-level probe + tracker. The probe broadcasts @level and
        // persists each reply into the players table (RecordLevel) — the sole
        // @level recorder, so a level (from the route-gate probe, the once-a-day
        // PartyProbeManager send, or a manual /<player> @level) supersedes the
        // title band. The tracker exposes the party's most-constraining level
        // window (MovementFilter reads it to route a following party around
        // gates a member can't clear) and, via WarmStaleLevels, re-probes a
        // member whose exact level is unknown or not from today when a planned
        // route actually crosses a level gate — wired into the route-scoped
        // MovementFilter.LevelWarmProbe below. Leader-scoped; the on-partying
        // level refresh lives in PartyProbeManager, not here.
        PartyLevelProbe = new Game.Remote.PartyLevelProbe(
            PartyBroadcaster, Chat, PartyState,
            recordLevel: (given, level) => Players.RecordLevel(given, level, DateTime.UtcNow),
            log: Log);
        PartyLevel = new Game.Remote.PartyLevelTracker(
            PartyState, PartyLevelProbe, Players,
            selfLevel: () => Stats.HasParsed ? PlayerStats.Level : (int?)null,
            log: Log);
        Movement.PartyLevelBoundsProvider = PartyLevel.Bounds;
        Movement.LevelWarmProbe = PartyLevel.WarmStaleLevels;

        // Once-a-day party stats probe. On the first party of the local day with
        // a given player it telepaths @level + @version and records the version
        // onto their player record (@level rides PartyLevelProbe's recorder). Its
        // wire sender is bound at connect (MainWindowViewModel); NotifyDisconnected
        // / NotifyEnteredRealm suspend it at the login menu like PartyPoller.
        PartyProbe = new Game.Remote.PartyProbeManager(Chat, PartyState, Players, Log)
        {
            IsInTrainerMenu = () => TrainerMenu.MenuOwnsKeyboard,
        };

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
        Movement.WealthWarmProbe = PartyWealth.Probe;

        // Base auto-search — a room-wide `sea` reveals hidden items for the
        // auto-get engines. Armed by the persisted master toggle OR the transient
        // path-item demand gate above. A search won't run mid-combat, so the engine
        // defers past a fight and holds the walker via the Search gate until the
        // room clears (see AutoSearchManager). Wire-sender bound by
        // MainWindowViewModel after connect.
        AutoSearch = new Game.Map.AutoSearchManager(
            isEnabled: () => ReadAutoModeFlag(d => d.AutoSearch),
            isDemandActive: () =>
                PathItemDemand.SearchDemandActive || PartyPathItemGate.SearchDemandActive,
            hasEngageableHostiles: () => CombatTracker.HasEngageableHostiles,
            hasGetEngineArmed: () =>
                ReadAutoModeFlag(d => d.AutoGetItems) || ReadAutoModeFlag(d => d.AutoGetCash),
            coordinator: MovementCoordinator,
            log: Log);

        // Combat-clear seam: fires the deferred `sea` once the room is clear.
        // Wired after AutoGetItems.OnRoomObserved (above) so the search's revealed
        // loot is collected after the fight's own drops; CombatStateTracker's
        // handler ran first, so the hostile flag is current.
        RoomClassifier.EntitiesObserved += _ => AutoSearch.OnRoomObserved();

        // Drop the stale queue / ground snapshot when we actually change rooms.
        RoomTracker.StateChanged += t =>
        {
            if (t.NewRoom is null) return;
            if (t.PreviousRoom is not null
             && t.PreviousRoom.Key.Equals(t.NewRoom.Key)) return;
            AutoSearch.OnRoomChanged();
            AutoGetItems.OnRoomChanged();
            GroundItems.OnRoomChanged();
            Cash.OnRoomChanged();
        };

        Walker = new Game.Map.AutoWalkManager(RoomGraph, Bfs, RoomTracker,
            MovementCoordinator, filter: Movement, log: Log,
            promptScanner: PromptScanner, recovery: Recovery);
        // Random-teleport maze solver. The walker calls into it (via
        // SetMazeSolver) whenever a destination inside a maze pocket has no
        // sourceable route; the solver drives look-peeks + reshuffles until a
        // plain route exists, then hands the final walk back. Its wire-sender
        // and the RoomDisplayParser.RoomParsed feed are bound per-session by
        // MainWindowViewModel after connect.
        MazeSolver = new Game.Map.TeleportMazeSolver(
            MazeIndex, RoomGraph, RoomTracker, Bfs, Walker, Log,
            isParadigm: () => GameData.ActiveRealm == Game.RealmType.ParaMud,
            paradigmResolver: ParadigmResync,
            enabled: () => Settings.Current.AsylumSolverEnabled);
        Walker.SetMazeSolver(MazeSolver);
        // Great Pyramid climb solver — same no-route hand-off as the maze solver,
        // on its own slot. Drives the leader only, and only when leading or solo
        // (canDrive), pre-flighting the floor-1 timer against live encumbrance +
        // quickness. Wire-sender / RoomParsed / line feeds bound per-session.
        PyramidSolver = new Game.Map.PyramidSolver(
            RoomTracker, Walker,
            snapshot: () => Inventory.Snapshot,
            quickness: () => Game.Calculators.CharacterCalculator
                .AggregateEquipmentStats(Inventory.Snapshot.EquippedItems, GameData).Totals.PlusQuickness,
            log: Log,
            isParadigm: () => GameData.ActiveRealm == Game.RealmType.ParaMud,
            canDrive: () => PartyState.SelfIsLeader || PartyState.Members.Count <= 1,
            leaderName: () => PartyState.SelfIsLeader ? PartyState.LeaderName : null,
            enabled: () => Settings.Current.PyramidSolverEnabled,
            coordinator: MovementCoordinator,
            isPartyMember: IsPartyMemberName);
        Walker.SetPyramidSolver(PyramidSolver);
        // Data-driven boat routing. When a walk's goal is cheaper (or only)
        // reachable by a sea-captain sailing, the planner stitches the two land
        // legs around the boat hop and the walker inserts a BoatStep. The planner
        // pulls its candidate sailings from RoomGraph's data-driven boat index, so
        // it no-ops on realms without docks.
        Walker.SetBoatPlanner(new Game.Map.BoatRoutePlanner(RoomGraph, Bfs, Log));
        // Voyage timer: the boat step waits out the sail — from boarding in the
        // captain's room, through the buff-locked transit legs, to landing at the
        // arrival shore — on a wall-clock deadline it sizes from the passage's
        // transit-spell rounds. Wire a UI-thread one-shot so OnBoatDeadline runs on
        // the same thread the walker's tracker events do; the injected shape keeps
        // the Game/Map layer UI-free (tests drive a fake clock instead).
        Walker.SetVoyageScheduler((delay, callback) =>
        {
            var timer = new Avalonia.Threading.DispatcherTimer { Interval = delay };
            timer.Tick += (_, _) => { timer.Stop(); callback(); };
            timer.Start();
            return new DispatcherTimerHandle(timer);
        });
        // While a maze solve is Active the tracker legitimately churns Lost/Suspect
        // between same-named teleport landings — relocalizing that is the solver's
        // job. On Paradigm the solver drives its OWN `rm` after each landing (see
        // TeleportMazeSolver); keep the recovery gate's proactive `rm` suppressed
        // for the duration so it can't fire a second, uncoordinated `rm` that races
        // the solver's. On stock (no `rm`) the solver uses the look-sweep and this
        // gate no-ops anyway.
        Recovery.TryResync = reason => !MazeSolver.Active && ParadigmResync.TryRequestResync(reason);
        // DeathRecoveryManager's Walk-to-Room / Recover-Now actions route
        // through the walker — attached here since the walker is built
        // after the manager.
        DeathRecovery.AttachWalker(Walker);
        // Route walker over trapped exits through the TrapDisarmManager. The
        // walker only enqueues on a RoomExitHint.Trap — it already knows a trap
        // sits on the exit, so it disarms directly (trapKnown: true) instead of
        // searching first.
        Walker.SetTrapEnqueuer((dir, sender, reply) =>
            TrapDisarm.Enqueue(dir, sender, reply, trapKnown: true));
        // Settings → Other "Utilize disarm traps if able": gate the
        // walker's trap-disarm on the toggle AND a real local capability
        // (a positive Traps stat, or a class/race game-data trap-skill
        // grant when the value hasn't been captured yet). When the gate is
        // false the walker tries party delegation, else steps through.
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

        // Fold each entered hazard room's counter into the same walk-start item
        // announce, so a route the user chose to run through a hazard room
        // provisions its counter like an Item/Ticket gate. Single-counter
        // (no-substitute) items always announce; an any-of group's counter
        // announces only when the user forced one via the route picker's "obtain
        // then cross" choice (otherwise the group stays a manual counter choice).
        Walker.SetHazardItemResolver(HazardAnnounceItems);

        // Clear the per-walk forced-obtain override when a walk is abandoned, so a
        // forced flag never leaks into a later unrelated walk. (The per-item drop
        // on acquisition is wired to Inventory.Changed above.)
        Walker.Event += e =>
        {
            if (e.Kind is Game.Map.WalkEventKind.Stopped or Game.Map.WalkEventKind.Failed)
                _forcedPathObtain.Clear();
        };

        // Boss "stop before" rooms — the walker halts one room short of any boss
        // room flagged StopBefore on the active realm. Resolved live so realm swaps
        // + tab edits take effect without re-wiring; only the point-to-point walker
        // consults it (loops / auto-lair route through boss rooms untouched).
        Walker.SetBossStopRooms(() =>
        {
            var set = new HashSet<Game.Map.RoomKey>();
            foreach (Models.Profile.BossDef b in Bosses.ResolveForRealm(GameData.ActiveRealm))
            {
                if (!b.StopBefore) continue;
                foreach (string wire in b.Rooms)
                    if (Game.Map.RoomKey.TryParseWire(wire, out Game.Map.RoomKey k)) set.Add(k);
            }
            return set;
        });

        // If an in-flight move carried us out of a room where combat had just
        // engaged an actionable hostile (the move confirms + wipes the room
        // before the kill lands), halt the walk so it doesn't keep going deeper
        // past the abandoned fight. Both engines are rebuilt together in this
        // method, so the subscription dies with them — no explicit unsubscribe.
        CombatTracker.EngagedTargetAbandoned += reason => Walker.HaltForAbandonedCombat(reason);
        // So the abandoned-combat halt fires for a running loop / auto-lair too, not
        // just a point-to-point walk (report stock-20260731-010401). Lazy — reads
        // MovementControl at halt time, after it's constructed below.
        Walker.SetAnyEngineActiveCheck(() => MovementControl.IsActive);

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
            roomLightSpellIllu: () => RoomLightSpell.IlluForSpell(
                ReadSection<Models.Profile.SpellsSettings>(Profile.Current, "Spells").RoomLightSpell),
            roomLightSpellName: () =>
                ReadSection<Models.Profile.SpellsSettings>(Profile.Current, "Spells").RoomLightSpell,
            castRoomLightSpell: name => Cast.TryCast(name),
            settings:    () => ReadSection<Models.Profile.AutoLightSettings>(Profile.Current, "AutoLight"),
            log:         Log);
        Walker.SetRouteAnnouncer(AutoLightProvisioner.OnRoutePlanned);

        // Keeps a checkspell hazard buff up as the walker crosses a hazard room.
        // Shares the approach-room hook below with the light provisioner: on each
        // committed step it resolves the room's hazard and, for a carried buff
        // source (the desert waterskin), `use`s it so the buff is up on arrival —
        // re-`use`ing only when the buff's own duration would have lapsed so a fast
        // traverse spends one charge. No opt-in gate: a route the user chose to run
        // through a hazard room must survive it.
        AutoHazardCounterProvisioner = new Game.Map.AutoHazardCounterProvisioner(
            resolveRoom:    RoomGraph.GetRoom,
            hazardForSpell: spell => RoomHazards.HazardForSpell(spell),
            carriedCount:   CountItemCarried,
            itemName:       ItemNames.GetName,
            // The lapse-prompt / swig-confirmation line recognisers for the
            // reactive re-raise. walkActive / haltWalk defer to MovementControl
            // (assigned below, only ever invoked mid-walk) so a lapse prompt with
            // no swig — out of charges — backs the route out instead of marching
            // deeper into a hazard it can no longer counter.
            messageMatcherForSpell: BuildSpellLinePredicate,
            walkActive:     () => MovementControl.IsActive,
            haltWalk:       _ => MovementControl.Stop(),
            log:            Log);

        // Predictive one-room-lookahead: the walker hands the room it's about to
        // enter to both provisioners BEFORE the move bytes — the light one `use`s a
        // carried light when the room reads dark, the hazard one raises a carried
        // buff when the room is a checkspell hazard (LoopRunner gets the same hook
        // below).
        Walker.SetApproachRoomHook(key =>
        {
            AutoLightProvisioner.OnApproachingRoom(key);
            AutoHazardCounterProvisioner.OnApproachingRoom(key);
        });

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
                AutoLair.IsActive || LoopRunner.State != Game.Map.LoopState.Idle
                || AutoDeposit.IsRerouting,
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
        // Reactive readying — the authoritative "light this room" signal. The
        // server's two "can't see" lines (the same ones that drive
        // NoteDarkRoomEntered) are the ONLY trigger that lights a carried light:
        // the provisioner never readies predictively off a route scan, so a room
        // that renders fine can't be over-lit by a bad darkness guess.
        Router.Subscribe(Services.Patterns.KnownPatterns.RoomPitchBlack,
            _ => AutoLightProvisioner.OnDarkRoomObserved());
        Router.Subscribe(Services.Patterns.KnownPatterns.RoomVeryDark,
            _ => AutoLightProvisioner.OnDarkRoomObserved());

        // A readied light burning out ("Your <light> flickers and goes out.")
        // clears in the snapshot only on the next `i` dump; this live line lets the
        // provisioner treat the readied light as gone now, so the dark-room line
        // that follows re-readies a carried spare instead of seeing a stale light.
        Router.Subscribe(Services.Patterns.KnownPatterns.LightBurnedOut,
            _ => AutoLightProvisioner.OnReadiedLightExpired());

        // The other half of the reactive light policy: putting the light away once
        // we reach a room that renders without it. On each confirmed room entry —
        // but never while the room is still dark (IsInDarkRoom guards against
        // rem'ing the light we just lit for it) — hand the new room to the
        // provisioner, which `rem`s an auto-readied light when the room is seeable
        // on worn gear alone.
        RoomTracker.StateChanged += t =>
        {
            if (RoomTracker.IsInDarkRoom) return;
            if (t.NewRoom is { } room)
                AutoLightProvisioner.OnRoomEntered(room);
        };

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
            // Master gate: no per-set AutoMode flag exists, so auto-equip follows
            // the Auto-All kill-switch — silenced automation means no gear swaps.
            isAutoEnabled: () => !AutoModeController.KillSwitchEngaged,
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
        ExpResolver = new Game.Map.RouteExpResolver(RoomGraph, Bfs, LairTimers, GameData);

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
        // logs measured per-hop times tagged with the carry-weight reading the
        // workshop records (Inventory snapshot). Enabled via the Program Log
        // window's "Hop timing" toggle (LogDiagnostics.HopTiming).
        Encumbrance = new Game.EncumbranceParser(Router, PlayerState, Log);
        HopCalibrator = new Game.HopTimingCalibrator(RoomTracker, PlayerState, Inventory, Log);
        // The calibrator's gate follows the live diagnostic flag: apply the
        // current value now, then track every change. Wired here (after
        // construction, before any ProfileLoaded fires) so it's never null.
        HopCalibrator.Enabled = LogDiagnostics.HopTiming;
        LogDiagnostics.Changed += () => HopCalibrator.Enabled = LogDiagnostics.HopTiming;

        // Per-BBS room blacklist — hides ganghouse / dead-end rooms
        // from the map render + room search. Loaded on BBS pin so
        // BFS picks it up via the Changed event before the first
        // layout build for the new BBS.
        RoomBlacklist = new RoomBlacklistStore(Log);
        Profile.ProfileLoaded += _ => RoomBlacklist.OnBbsPinApplied(ResolveActiveBbs()?.Name);
        Profile.BbsPinApplied += _ => RoomBlacklist.OnBbsPinApplied(ResolveActiveBbs()?.Name);

        // Per-BBS "top N" leaderboard history + its live capture tracker. The
        // store loads on BBS pin (same shape as the blacklist); the tracker binds
        // to the per-session LineExtractor in MainWindowViewModel.AttachLineExtractor
        // and passively snapshots the block whenever the player runs `top <N>`.
        Leaderboards = new LeaderboardSnapshotStore(Log);
        Profile.ProfileLoaded += _ => Leaderboards.OnBbsPinApplied(ResolveActiveBbs()?.Name);
        Profile.BbsPinApplied += _ => Leaderboards.OnBbsPinApplied(ResolveActiveBbs()?.Name);
        LeaderboardCapture = new Game.Leaderboard.LeaderboardCaptureTracker(Leaderboards, PromptScanner, Log);
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
        // Predictive equip on loop laps — same hook the walker uses, so a circuit
        // step lights a dark room and raises a carried hazard buff ahead of the
        // move too.
        LoopRunner.SetApproachRoomHook(key =>
        {
            AutoLightProvisioner.OnApproachingRoom(key);
            AutoHazardCounterProvisioner.OnApproachingRoom(key);
        });
        // Avoid-list mutation mid-loop → LoopRunner re-routes via a
        // Stop+Start cycle so the new filter applies on the next BFS.
        Movement.AvoidedChanged += () => LoopRunner.NotifyAvoidedChanged();

        // Loop-start session reset. ReachedFirstWaypoint fires once per loop
        // session at the moment the circle actually begins (after any walker
        // approach), which is the point a lap's stats should re-anchor. Gated by
        // Settings.Party.ResetStatisticsOnLoopStart (mirrored onto
        // PartyBroadcaster.AutoExpResetEnabled): when on, zero our own
        // session-stats trackers — the same wipe the Session Stats window button
        // and the inbound @reset handler perform — and telepath @reset to the
        // party so every follower re-anchors to the new circuit. This consumer
        // was described in the PartyBroadcaster wiring comment but never built,
        // so loop starts silently skipped the reset.
        LoopRunner.Event += e =>
        {
            if (e.Kind != Game.Map.LoopEventKind.ReachedFirstWaypoint) return;
            // The HP/MA-history profile is per-loop by definition — a new circuit
            // makes the old step-indexed bands meaningless — so it re-anchors on
            // every loop start, independent of the ResetStatisticsOnLoopStart
            // opt-out that gates the counter trackers below.
            HpMaHistory.Reset();
            if (!PartyBroadcaster.AutoExpResetEnabled) return;
            CombatSession.Reset();
            TimeAnalysis.Reset();
            SessionActivity.Reset();
            // Transaction history is deliberately NOT reset here: the ledger of
            // bank/stash offloads is user-owned, cleared only by the user (its own
            // Clear button) or the connect / character-switch boundary — never by a
            // loop start.
            Log.Info("LoopRunner",
                "loop start: session stats reset; broadcasting @reset to party.");
            PartyBroadcaster.BroadcastExpReset();
        };

        // HP/MA-history sampling. Every statline (finest-grained vitals feed —
        // catches mid-combat dips PlayerState.PropertyChanged would coalesce away)
        // folds the current HP/mana percent into the loop step being traversed,
        // but only while a loop is actively stepping. CurrentIndex is the live step
        // position (stable during a step, wraps 0 each lap), so the same circuit
        // step accumulates across laps. Max comes from PlayerState, which the
        // earlier-subscribed PromptParser has already ratcheted for this prompt.
        PromptScanner.PromptObserved += obs =>
        {
            if (LoopRunner.State != Game.Map.LoopState.Running) return;
            int maxHp = PlayerState.MaxHp, maxMa = PlayerState.MaxMa;
            double hpPct = maxHp > 0 ? 100.0 * obs.Hp / maxHp : 0.0;
            double? maPct = obs.ManaType != Game.ManaType.None && maxMa > 0
                ? 100.0 * obs.Mana / maxMa
                : null;
            HpMaHistory.NoteVitals(LoopRunner.CurrentIndex, hpPct, maPct);
        };

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
            Walker, LoopRunner, AutoLair, MovementCoordinator, Log);

        // A manually-typed movement step (one the walker / loop / auto-lair didn't
        // send — RoomTracker's echo-claim tells them apart) pauses the active nav
        // engine as a user override: the automation must never fight a hand-driven
        // move. Manual resume, exactly like the Pause button — the user hits Start
        // when they're ready to hand control back. No-op when nav is idle or already
        // user-paused (MovementControl.Pause guards both).
        //
        // Marshalled to the next dispatcher turn, NOT called inline: this fires
        // synchronously deep inside the manual move's own send → observe → track
        // stack, and pausing re-entrantly there raced the move's own state update —
        // the gate asserted but the toolbar / coalesced state didn't cleanly reflect
        // the pause (report paradigm-20260814-131551). Deferring lets the move fully
        // settle first, then the pause applies exactly like a Pause-button click.
        RoomTracker.ManualMoveObserved += () =>
        {
            if (!MovementControl.IsActive || MovementControl.IsUserPaused) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!MovementControl.IsActive || MovementControl.IsUserPaused) return;
                Log.Info("Navigation",
                    "manual movement command — pausing navigation (user override; press Start to resume)");
                MovementControl.Pause();
            });
        };

        // Auto-All kill switch also parks navigation: engaging it suspends any
        // in-flight walk / loop / auto-lair (retaining where it is), and restoring
        // it resumes exactly that. Both the toolbar button and the @auto-all remote
        // funnel through AutoModeController.ToggleAll, so this one bridge covers
        // both. (MovementControl is built here, after AutoModeController, so the
        // hook is wired at this point rather than at the controller's construction.)
        AutoModeController.KillSwitchToggled += engaged =>
        {
            if (engaged) MovementControl.SuspendForAutoAll();
            else MovementControl.ReleaseFromAutoAll();
        };

        // Death engine-quiescence. On our death RoomTracker fires
        // PlayerDeathObserved (both death phrasings). PlayerDeathHalt does a clean
        // stop (via this stopper) then clears the user gate — same as the Nav Stop
        // button. Stopping outright — not pausing — matters because a loop caught
        // mid-recovery (a miracle-save restores HP, clearing the HealthRecovery gate
        // and firing the loop's ResumeAfterRecovery just before the death registers)
        // sits in a Recovering state a pause doesn't cover, so the graveyard's
        // respawn-room confirm would drive a recovery-reroute straight back out. The
        // reset clears that state and every retained destination; nothing survives
        // to re-drive us into the room we died in, and a manual/remote nav action
        // afterward runs freely.
        PlayerDeathHalt.SetEngineStopper(() =>
        {
            LoopRunner.Stop("player died — halting in graveyard");
            Walker.Stop("player died — halting in graveyard");
            AutoLair.Stop("player died — halting in graveyard");
        });
        // Wipe the classifier's room view so a hostile from the room we died in
        // doesn't linger as a stale target the combat engine re-attacks when a
        // party member later walks into the graveyard. Independent of the gate
        // ordering above, so it stays a plain post-death subscriber.
        RoomTracker.PlayerDeathObserved += () => RoomClassifier.NoteRoomChanged();

        // Reset the condition observation log on death. Death is a full server-side
        // state reset (respawn at the graveyard clears knockdown / held / debuffs),
        // but a latched condition whose wear-off line we never received — most
        // dangerously MovementPrevented ("flat on your back") — otherwise survives
        // the death and stays asserted forever: SelfHeldResponder keeps HeldGate up
        // off the stale flag, so the walker sits "Paused by: Held" while the
        // character is free to move in-game (report paradigm-20260809-114444).
        // ClearAll is the same cascade the manual Reset States button uses; it's a
        // safe over-clear because any condition still genuinely active re-latches on
        // its next server line. Wired here (not in PlayerDeathMovementHalt, whose
        // concern is the movement engines) since the reset spans all conditions.
        RoomTracker.PlayerDeathObserved += () => Conditions.ClearAll("death");

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
            RoomGraph, GameData, Bfs, RoomBlacklist, Movement, Log, Favorites, Bosses);

        // MovePlayer remote-command handler.
        // Registers @goto, @loop, @lair, @stop, @rego against the
        // RemoteCommandManager. Dispatch routes to the now-existing
        // Walker / LoopRunner / AutoLairManager. The Catalog permission
        // gate ensures only players the user has granted MovePlayer
        // can issue these.
        MoveRemote = new Game.Remote.MovePlayerHandler(
            RemoteCommands, RoomSearch, RoomGraph, RoomTracker, Walker, Loops, LoopRunner,
            Lairs, AutoLair, MovementCoordinator, MovementControl, Favorites, Bosses, Bfs);

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

        // Quest-availability announcer — watches the same StatParser for level crossings.
        // The name resolver mirrors the Quest Status journal's title rule (user name, else
        // the crawler's fallback title); injected so the domain service stays out of the
        // ViewModels layer.
        QuestAvailability = new Game.Quests.QuestAvailabilityAnnouncer(
            Stats, Profile,
            currentLevel: () => PlayerStats.Level,
            eligibleAtLevel: level => Game.Quests.QuestEligibility.Resolve(
                GameData, Quests, PlayerStats, Profile,
                resolveName: static (q, def) => string.IsNullOrWhiteSpace(def.Name)
                    ? ViewModels.CharacterWorkshop.QuestTextFormatter.FallbackTitle(q)
                    : def.Name,
                level),
            log: Log);

        AutoDeposit = new Game.Cash.AutoDepositManager(
            Cash,
            readCash: () => ReadSection<Models.Profile.CashSettings>(Profile.Current, "Cash"),
            getSnapshot: () => Inventory.Snapshot,
            noteAutoDeposit: Inventory.NoteAutoDeposit,
            isBankRoom: key => Game.GameData.BankCatalog.IsBankRoom(GameData, key),
            profile: Profile,
            tracker: RoomTracker,
            walker: Walker,
            loopRunner: LoopRunner,
            autoLair: AutoLair,
            stash: Stash,
            provisioner: AutoLightProvisioner,
            lightShop: AutoLightShopRouter,
            carriedCount: CountItemCarried,
            post: action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            log: Log);
        // Return-leg light provisioning: the reroute owns the walker end-to-end, so
        // the reactive shop router is suppressed (IsRerouting) — this manager runs
        // its own bank -> shop -> origin light detour and needs the `i` dump to
        // notice the bought copy land.
        Inventory.Changed += AutoDeposit.OnInventoryChanged;
        // Bank deposits (already a copper value) join stash hides in the Session
        // Stats stashed/deposited figure. The transaction-history ledger is fed
        // separately from the `You deposit …` echo (InventoryManager.BankDeposited,
        // wired above) so a manual deposit is recorded too.
        AutoDeposit.Deposited += copper => SessionActivity.NoteCurrencyStashed(copper);

        // Shop-source routing (PR C). On a one-shot walk-to that needs an
        // uncarried Item/Ticket-gate item a shop sells, detour to the
        // fewest-added-steps shop, buy it, and resume — gated per-item by the
        // item record's "auto-obtain for path → buy if needed" flag
        // (ItemOverlay). Distances use the same movement filter
        // the walker routes with so the estimate matches the real walk; the
        // shop lookup joins ShopStock (who sells it) against the live graph
        // (which rooms host those shops). engineWalkActive suppresses the
        // detour while a loop / auto-lair run drives movement. WalkTo is
        // deferred through the dispatcher because the triggering NeedPosted
        // fires synchronously inside the walker's WalkTo. Wire-sender bound
        // by MainWindowViewModel after connect.
        // Give-source routing. On a one-shot walk-to that needs an uncarried
        // Item/Ticket-gate item a deterministic textblock `giveitem` hands over
        // for free (an `ask <noun> <keyword>` dialogue give, or a room-CMD keyword
        // give), detour to the fewest-added-steps giver, issue the command, and
        // resume once it lands — gated per-item by the same AutoObtainForPath
        // flag. Preempts both the shop and drop routers (a free, certain give
        // beats a paid buy or a percentage hunt), which stand down whenever
        // DeterministicGiveExists. Wire-sender bound by MainWindowViewModel.
        PathItemGiveRouter = new Game.Map.PathItemGiveRouter(
            giveSourcesForItem: GiveSourcesForItem,
            currentRoom: () => RoomTracker.State.CurrentRoom?.Key,
            walkDestination: () => Walker.Destination,
            distanceBetween: PathItemDetourDistance,
            carriedCount: CountItemCarried,
            itemName: ItemNames.GetName,
            isEnabled: IsAutoObtainForPath,
            engineWalkActive: () =>
                AutoLair.IsActive || LoopRunner.State != Game.Map.LoopState.Idle
                || AutoDeposit.IsRerouting,
            // Silent supersede: the detour redirect is our own, not an external
            // abort, so it must not fire a Stopped back into this router's OnWalkEvent.
            walkTo: key => Walker.WalkTo(key, supersedeSilently: true),
            post: action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            log: Log);
        Needs.NeedPosted += PathItemGiveRouter.OnNeedPosted;
        Walker.Event += PathItemGiveRouter.OnWalkEvent;
        Inventory.Changed += PathItemGiveRouter.OnInventoryChanged;

        PathItemShopRouter = new Game.Map.PathItemShopRouter(
            shopRoomsSellingItem: ShopRoomsSellingItem,
            deterministicGiveExists: DeterministicGiveExists,
            currentRoom: () => RoomTracker.State.CurrentRoom?.Key,
            walkDestination: () => Walker.Destination,
            distanceBetween: PathItemDetourDistance,
            carriedCount: CountItemCarried,
            cashOnHand: PathItemCashOnHand,
            buyCost: PathItemBuyCost,
            bankRoom: PathItemBankRoom,
            itemName: ItemNames.GetName,
            isEnabled: IsAutoObtainForPath,
            engineWalkActive: () =>
                AutoLair.IsActive || LoopRunner.State != Game.Map.LoopState.Idle
                || AutoDeposit.IsRerouting,
            // Silent supersede: the shop / bank redirect is our own, not an external
            // abort, so it must not fire a Stopped back into this router's OnWalkEvent
            // (which would abandon the detour on arrival — the "sat idle at the shop,
            // never bought" bug).
            walkTo: key => Walker.WalkTo(key, supersedeSilently: true),
            post: action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            log: Log);
        Needs.NeedPosted += PathItemShopRouter.OnNeedPosted;
        Walker.Event += PathItemShopRouter.OnWalkEvent;
        Inventory.Changed += PathItemShopRouter.OnInventoryChanged;

        // Monster-drop reroute (PR D). The no-shop counterpart to the shop
        // router: when a walk-to needs an uncarried Item/Ticket-gate item no
        // shop sells but a monster drops, prompt (ConfirmService) to reroute
        // to the nearest room that monster spawns in, then resume once the
        // drop lands — gated per-item by the item record's "auto-obtain for
        // path → source from drops" flag (ItemOverlay). The two routers are
        // mutually exclusive via anyShopSells: this one acts
        // only when no shop stocks the item. Nearest spawn is chosen with a
        // single forward BFS (ComputeDistancesFrom) since a common monster
        // spawns in hundreds of rooms; dropSpawnsForItem flattens the index's
        // droppers × their spawn rooms lazily, only for the needed item.
        MonsterDropRouter = new Game.Map.MonsterDropRouter(
            dropSpawnsForItem: DropSpawnsForItem,
            anyShopSells: ShopStock.AnyShopSells,
            deterministicGiveExists: DeterministicGiveExists,
            currentRoom: () => RoomTracker.State.CurrentRoom?.Key,
            walkDestination: () => Walker.Destination,
            distancesFrom: src => Bfs.ComputeDistancesFrom(src, Movement),
            isCarried: IsItemCarried,
            itemName: ItemNames.GetName,
            isEnabled: IsAutoObtainForPath,
            engineWalkActive: () =>
                AutoLair.IsActive || LoopRunner.State != Game.Map.LoopState.Idle
                || AutoDeposit.IsRerouting,
            confirm: (title, body) => Confirm.ConfirmAsync(title, body, "Reroute"),
            // Silent supersede: the hunt reroute is our own, not an external abort.
            walkTo: key => Walker.WalkTo(key, supersedeSilently: true),
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
        // into the profile (crash-survivable) and, on the first in-game prompt
        // after a reconnect, telepaths @comeback to the leader to re-form the
        // party. Keys on the statline prompt (not the room display) so a dark
        // room can't defer the fire past the reconnect. Gated by the Auto-All
        // kill switch like MainMenuEntry — a manual-play character that silenced
        // automation won't auto-rejoin.
        PartyRejoin = new Game.Remote.PartyRejoinCoordinator(
            PromptScanner, PartyState, RoomTracker,
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

        // Leader-side reconnect party reform (mirror of PartyRejoin). No
        // crash-survivable memory: the reform recovers an in-process reconnect
        // (nightly cleanup drops + redials while the app stays up), so the
        // disconnect snapshot lives only for the session. Same kill-switch gate.
        PartyReform = new Game.Remote.PartyReformCoordinator(
            Router, Party,
            isAutoEnabled: () => !AutoModeController.KillSwitchEngaged,
            log: Log);

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

        // DefaultTaskRunner. Starts the character's configured "Default task"
        // (loop / Auto-Lair) on the first in-game prompt with a known room,
        // holding for the party-reform window on a party-session reconnect.
        DefaultTaskRunner = new Game.DefaultTaskRunner(
            PromptScanner, RoomTracker, Profile, Loops, Lairs,
            LoopRunner, AutoLair, PartyState, Party, Log);

        // Startup profile: with Settings → General "Auto-load last profile" on,
        // reopen the last session; otherwise (the default) open a blank draft and
        // let the user pick / build one via File → Open profile / Recent profiles.
        // A last-used profile that was since deleted / renamed throws on Load, so
        // fall back to the blank draft rather than failing startup.
        if (Settings.Current.StartupProfile() is { } startup)
        {
            try
            {
                Profile.Load(startup.Bbs, startup.Name);
            }
            catch (Exception ex)
            {
                Log.Info("Startup",
                    $"Auto-load of last profile '{startup.Name}' on '{startup.Bbs}' failed " +
                    $"({ex.GetType().Name}); loading the default profile instead.");
                Profile.LoadDefaultProfile();
            }
        }
        else
        {
            Profile.LoadDefaultProfile();
        }

        // Install-global startup-animation preference: seed the splash ONCE here from
        // the Global default profile, whichever profile the block above loaded. Sourcing
        // it from the default profile (not the loaded, possibly auto-loaded named one)
        // is what makes "turn the splash off" stick across launches and profiles.
        Display.SplashAnimate = Profile.ReadDefaultProfileStartupAnimation();

        // Track which profile was last loaded so "auto-load last" has a value to
        // read next launch.
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
        LogDiagnostics.AutoCollectLogs   = dto.AutoCollect;
        LogDiagnostics.HopTiming         = dto.HopTiming;
        _suppressLogDiagnosticsPersist = false;
    }

    private void ResetLogDiagnosticsToDefaults()
    {
        _suppressLogDiagnosticsPersist = true;
        // Mirror LogDiagnosticsSettings defaults: Debug + Combat on, the heavier
        // on-disk / hop-timing traces off.
        LogDiagnostics.DebugDiagnostics  = true;
        LogDiagnostics.CombatDiagnostics = true;
        LogDiagnostics.AutoCollectLogs   = false;
        LogDiagnostics.HopTiming         = false;
        _suppressLogDiagnosticsPersist = false;
    }

    private void PersistLogDiagnostics()
    {
        if (_suppressLogDiagnosticsPersist) return;
        // No loaded character → session-only value; nothing to persist to.
        if (Profile.Current is not { } profile) return;

        Models.Profile.LogDiagnosticsSettings dto = new()
        {
            Debug      = LogDiagnostics.DebugDiagnostics,
            Combat     = LogDiagnostics.CombatDiagnostics,
            AutoCollect = LogDiagnostics.AutoCollectLogs,
            HopTiming  = LogDiagnostics.HopTiming,
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

    // Live read of the master auto-combat toggle — the same GeneralSettings
    // AutoMode flag the combat engine gates on. The navigation walk-to ETA
    // reads it to decide whether to fold lair-fight dwell into the arrival
    // estimate (auto-combat off ⇒ the walker doesn't stop to fight, so the
    // route is pure travel time).
    public bool IsAutoCombatEnabled => ReadAutoModeFlag(d => d.AutoCombat);

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
        // recast clock (CastingDirector treats the returned value as seconds). Uses
        // the wall-clock per-round length so the recast window matches the buff's REAL
        // remaining time, not the nominal Dur×3 (which recasts ~1-2 s early).
        long rounds = Game.Spells.SpellCalculator.Duration(formula, Spellbook.Level);
        return rounds > 0
            ? (long)System.Math.Round(rounds * Game.Spells.SpellCalculator.SpellRoundSecondsWallClock)
            : null;
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

    // The item the equipment manager's Default set wants worn in the given inventory
    // slot label (e.g. "Off-Hand"), or null. The item-cast buff swap uses this as its
    // restore fallback: when the buff item is still equipped in its own slot (left
    // there from a prior session), the live inventory can't say what belongs there, so
    // the swap consults the configured loadout instead of stranding the buff item.
    private string? DesiredEquipSlotItem(string slotLabel)
    {
        if (Profile.Current?.Equipment is not { } eq) return null;
        if (Game.Inventory.EquipmentSlotMap.FromWornString(slotLabel) is not { } slot) return null;
        string? name = eq.Sets
            .FirstOrDefault(s => s.Trigger == Models.Profile.EquipTriggerType.Default)?.Slots
            .FirstOrDefault(e => e.Slot == slot && !string.IsNullOrWhiteSpace(e.ItemName))?.ItemName;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    private (string Caster, long DurationSec)? BuffInfoByShort(string castCode)
    {
        if (string.IsNullOrWhiteSpace(castCode)) return null;
        string target = castCode.Trim();
        foreach (Game.Spells.KnownSpell s in Spellbook.Available)
        {
            if (!string.Equals(s.Short.Trim(), target, StringComparison.OrdinalIgnoreCase)) continue;
            Models.GameData.MessageRecord? rec = FindSpellMessage(s.Number, s.Name);
            if (rec is null || string.IsNullOrWhiteSpace(rec.CasterMessage)) return null;
            // An enemy-targeting spell (a debuff / attack scope) is never a self-buff,
            // even if it has a positive duration — so a hand-cast one (e.g. vuln,
            // Targets 8 Monster-or-User) must not arm a self-buff recast timer or show
            // up as a phantom self-buff in the Buff Watchdog. This lookup feeds the
            // self-buff recast-window logic only (report paradigm-20260817-205819).
            if (Game.Combat.DebuffTargeting.IsSingleTargetEnemy(s.Targets)
                || Game.Combat.DebuffTargeting.IsAreaEnemy(s.Targets))
                return null;
            // Duration is in spell rounds; the recast clock wants wall-clock seconds.
            // Uses the wall-clock per-round length so "recast within N s" fires at the
            // buff's REAL remaining time, not ~1-2 s early off the nominal Dur×3.
            long durSec = (long)System.Math.Round(
                Game.Spells.SpellCalculator.Duration(s.Formula, Spellbook.Level)
                * Game.Spells.SpellCalculator.SpellRoundSecondsWallClock);
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

    // Self-buff cast code → the configured PARTY-WIDE party-buff cast code that removes
    // (supersedes) it via RemovesSpell (Abil 122), while in a party. Empty when solo. In
    // a party we let a party-wide buff that removes a self-buff cover us instead of self-
    // casting the removed one — chant removes bless, so once chant is a party buff we stop
    // self-casting bless. Only PARTY-WIDE covers count: a single-target party buff never
    // lands on self, so it can't cover our self-cast. Drives the director's self-buff
    // suppression and the Buff Watchdog "covered by" label.
    public IReadOnlyDictionary<string, string> SelfBuffCoverage()
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        if (!PartyState.IsInParty) return map;

        Models.Profile.SpellsSettings spells = Resolver.Resolve<Models.Profile.SpellsSettings>("Spells");
        Models.Profile.PartySettings party = Resolver.Resolve<Models.Profile.PartySettings>("Party");

        // Configured self-buffs → (cast code, spell number). #item-cast tokens resolve to
        // no spell and are skipped (an item buff isn't a RemovesSpell target).
        List<(string Code, int Number)> selfBuffs = new();
        void AddSelf(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            if (Spellbook.FindByCastCode(code.Trim()) is { } s)
                selfBuffs.Add((s.Short, s.Number));
        }
        foreach (string code in spells.BlessSlots.Values) AddSelf(code);
        AddSelf(spells.HpRegenSpell);
        AddSelf(spells.MaRegenSpell);
        AddSelf(spells.WhenHpFullSpell);
        AddSelf(spells.WhenMaFullSpell);
        if (selfBuffs.Count == 0) return map;

        foreach (Models.Profile.PartyBlessSlot pslot in party.BlessSlots)
        {
            if (string.IsNullOrWhiteSpace(pslot.Spell)) continue;
            if (!IsPartyWideBuff(pslot.Spell)) continue;   // a single-target party buff never covers self
            HashSet<int> removed = RemovedSpellNumbers(pslot.Spell);
            if (removed.Count == 0) continue;
            foreach ((string code, int number) in selfBuffs)
                if (removed.Contains(number) && !map.ContainsKey(code))
                    map[code] = pslot.Spell.Trim();
        }
        return map;
    }

    // The spell numbers a cast code's spell removes (RemovesSpell, Abil 122 — the same
    // effect the Spell Book renders as "Removes <spell>").
    private HashSet<int> RemovedSpellNumbers(string castCode)
    {
        const int RemovesSpellAbil = 122;
        HashSet<int> nums = new();
        if (Spellbook.FindByCastCode(castCode.Trim()) is { } s)
            foreach (Game.Spells.SpellAbility a in s.Formula.Abilities)
                if (a.Code == RemovesSpellAbil) nums.Add(a.Value);
        return nums;
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

    // Send a command line to the server as if the user typed it (CR appended),
    // riding the raw engine wire-sender. Used by the Calculators tab's "Parse
    // Toplist" button to request a fresh `top N` listing. Returns false when no
    // sender is bound yet (not connected).
    public bool SendGameCommand(string command)
    {
        if (_engineWireSend is null || string.IsNullOrWhiteSpace(command)) return false;
        _engineWireSend(System.Text.Encoding.Latin1.GetBytes(command.Trim() + "\r"));
        return true;
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
        if (target.Length == 0) return null;   // link-only lookup — never name-match ""
        foreach (Models.GameData.MessageRecord m in Messages.Messages)
            if (string.Equals(m.Name.Trim(), target, StringComparison.OrdinalIgnoreCase))
                return m;
        return null;
    }

    // Compile a predicate that recognises a spell's own player-facing line —
    // the hazard-counter provisioner watches for the lapse-damage prompt (a
    // hazard's LapseSpell) and the swig confirmation (its BuffSpell). The desert
    // lines ship with no {s}/{damage} placeholder, so CasterMessageMatcher
    // declines them; those fall back to a literal case-insensitive Contains.
    // Null when the active set carries no message for the spell — the reactive
    // path then stays inert and only the predictive timer keeps the buff up.
    private Func<string, bool>? BuildSpellLinePredicate(int spellNumber)
    {
        if (spellNumber <= 0) return null;
        Models.GameData.MessageRecord? rec = FindSpellMessage(spellNumber, string.Empty);
        if (rec is null) return null;
        string text = !string.IsNullOrWhiteSpace(rec.CasterMessage)
            ? rec.CasterMessage
            : rec.TargetMessage;
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (Game.Spells.CasterMessageMatcher.TryCreate(text) is { } matcher)
            return line => matcher.TryMatch(line, out _);
        string literal = text.Trim();
        return line => line.Contains(literal, StringComparison.OrdinalIgnoreCase);
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
    // carried, worn, OR on the key ring. Possession, not pack-membership:
    // a KEY-type item (e.g. a door key) lives in the dump's separate "You
    // have the following keys:" trailer, not the pack, so a carried-only
    // check misreads a held key as absent — which false-blocks a KeyLocked
    // door's carry-the-key opener and strands the walk on the pick-only
    // stat alternative. Delegates to CountItemHeld so the key-ring logic
    // lives in one place. Backs PathItemDemand's possession check and the
    // MovementFilter key/item gate.
    private bool IsItemCarried(int itemId) => CountItemHeld(itemId) > 0;

    // Does 'name' as it appears in a spell line (e.g. "casts hold person on Jroc")
    // name a current party member? Party names can be one or two words and the wire
    // line often uses just the first, so match the full name or its first token.
    // Backs the pyramid solver's party-member hold detection.
    private bool IsPartyMemberName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        foreach (Game.PartyMember m in PartyState.Members)
        {
            string full = m.Name;
            if (full.Length == 0) continue;
            if (string.Equals(full, name, StringComparison.OrdinalIgnoreCase)) return true;
            int sp = full.IndexOf(' ');
            string first = sp > 0 ? full[..sp] : full;
            if (string.Equals(first, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // "Name (map/room)" for the room the tracker currently sits in, or null when
    // position is unknown. Stamped onto transaction-ledger rows so a deposit
    // records which bank was used and a stash records which room hid the loot.
    private string? CurrentRoomLabel()
    {
        if (RoomTracker?.State.CurrentRoom is not { } room) return null;
        return $"{room.DisplayName} ({room.Key.Map}/{room.Key.Room})";
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
        // A stacked pack entry is stored two ways: the full-inventory parse keeps it
        // as one count-prefixed token ("50 orc-head"), while the live `You took N`
        // path appends N singular entries. Split the leading count so both forms
        // count their true quantity — otherwise a parse collapses a stack to 1,
        // under-reading the held total and letting the auto-get MaxToGet cap collect
        // past its limit after any inventory refresh.
        foreach (string entry in snap.CarriedItems)
        {
            (int qty, string name) = Game.Inventory.CountedCommand.SplitLeadingCount(entry);
            if (ItemNames.FindByName(name) == itemId) count += qty;
        }
        foreach (Game.Inventory.EquippedItem worn in snap.EquippedItems)
            if (ItemNames.FindByName(worn.Name) == itemId) count++;
        return count;
    }

    // How many copies of itemId the player holds, counting the key ring on top
    // of carried + worn. KEY-type items live in the dump's separate "You have
    // the following keys:" trailer (InventorySnapshot.Keys), not the pack, so
    // CountItemCarried alone under-reads them — which let the auto-get MaxToGet
    // cap collect past its limit. Backs AutoGetItemsManager's held-count seam.
    private int CountItemHeld(int itemId)
    {
        int count = CountItemCarried(itemId);
        Game.Inventory.InventorySnapshot snap = Inventory.Snapshot;
        if (snap.Keys is { } keys)
            foreach (string entry in keys)
            {
                (int quantity, string name) = Game.Inventory.InventorySnapshot.ParseKeyEntry(entry);
                if (ItemNames.FindByName(name) == itemId) count += quantity;
            }
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

    // Route-picker helper: for a path-gate item the direct route needs, name the
    // giver the walk would actually detour to for a free hand-over — but only
    // when that detour will run. It runs only if the item is flagged
    // AutoObtainForPath (same gate PathItemGiveRouter enforces) AND a reachable
    // deterministic giver exists. The chosen giver matches the router's
    // fewest-added-steps pick (shared TrySelectGiver), so the picker's "ask X"
    // promise is the giver the run visits — not a plausible guess.
    public string? PathItemGiveName(int itemId, Game.Map.RoomKey source, Game.Map.RoomKey destination)
    {
        if (!IsAutoObtainForPath(itemId)) return null;
        System.Collections.Generic.IReadOnlyList<Game.Map.GiveSource> givers = GiveSourcesForItem(itemId);
        if (givers.Count == 0) return null;
        return Game.Map.PathItemGiveRouter.TrySelectGiver(
                givers, source, destination, (a, b) => Bfs.DistanceBetween(a, b, Movement),
                out Game.Map.GiveSource giver)
            ? giver.GiverName
            : null;
    }

    // Route-picker helper: for a path-gate item the direct route needs, name the
    // shop the walk would actually detour to buy it — but only when that detour
    // will really run. It runs only if the item is flagged AutoObtainForPath
    // (same gate PathItemShopRouter enforces), no free deterministic give
    // preempts it (a give owns the item over a buy), AND a reachable shop stocks
    // it, so all conditions must hold or we return null. The chosen shop matches
    // the router's fewest-added-steps pick (shared TrySelectShop), so the picker's
    // "buy at X" promise is the shop the run visits — not a plausible guess.
    public string? PathItemShopName(int itemId, Game.Map.RoomKey source, Game.Map.RoomKey destination)
    {
        if (!IsAutoObtainForPath(itemId)) return null;
        if (DeterministicGiveExists(itemId)) return null;   // give preempts the buy
        System.Collections.Generic.IReadOnlyList<Game.Map.RoomKey> shops = ShopRoomsSellingItem(itemId);
        if (shops.Count == 0) return null;
        if (!Game.Map.PathItemShopRouter.TrySelectShop(
                shops, source, destination, (a, b) => Bfs.DistanceBetween(a, b, Movement),
                out Game.Map.RoomKey shop))
            return null;
        return RoomGraph.GetRoom(shop)?.Name;
    }

    // Route-picker helper: for a path-gate item no give / shop covers, name the
    // monster the walk would actually reroute to hunt — but only when that hunt
    // will run. It runs only if the item is flagged AutoObtainForPath (same gate
    // MonsterDropRouter enforces), no free give and no shop cover it (those are
    // the give / buy tails' job), AND a dropper spawns in a room reachable from
    // source. The chosen monster matches the router's nearest-spawn pick (shared
    // SelectNearestSpawn from the same forward BFS), so the picker's "dropped by
    // X" promise is the lair the run visits — not a plausible guess.
    public string? PathItemDropName(int itemId, Game.Map.RoomKey source)
    {
        if (!IsAutoObtainForPath(itemId)) return null;
        if (DeterministicGiveExists(itemId)) return null;   // give preempts the hunt
        if (ShopStock.AnyShopSells(itemId)) return null;
        System.Collections.Generic.IReadOnlyList<Game.Map.MonsterDropSpawn> spawns = DropSpawnsForItem(itemId);
        if (spawns.Count == 0) return null;
        return Game.Map.MonsterDropRouter.SelectNearestSpawn(
                spawns, Bfs.ComputeDistancesFrom(source, Movement),
                out Game.Map.MonsterDropSpawn best, out _)
            ? best.MonsterName
            : null;
    }

    // Cash-on-hand read for the shop router's affordability gate: the
    // consolidated purse in copper farthings (the same wealth figure the
    // auto-deposit engine weighs). Backs the withdraw-before-buy decision.
    private long PathItemCashOnHand() => Inventory.Snapshot.Currency.TotalCopperValue;

    // Configured bank room for the shop router's withdraw leg, or null when
    // unset / unparseable. Reuses the Cash section's BankRoomKey (the
    // auto-deposit destination), so "where I bank" stays one setting.
    private Game.Map.RoomKey? PathItemBankRoom()
    {
        Models.Profile.CashSettings cash =
            ReadSection<Models.Profile.CashSettings>(Profile.Current, "Cash");
        return Game.Map.RoomKey.TryParseWire(cash.BankRoomKey, out Game.Map.RoomKey key)
            ? key
            : null;
    }

    // Per-copy buy cost in copper for the shop router's affordability gate:
    // resolve the shop hosting shopRoom, price itemId's slot with the same
    // MajorMUD markup + charm formula the room-detail readout uses, and round up
    // (the game charges whole copper). Null when the room hosts no shop, the shop
    // doesn't stock the item, or the set can't be read — the router then heads
    // straight to the shop and buys with cash on hand.
    private long? PathItemBuyCost(int itemId, Game.Map.RoomKey shopRoom)
    {
        if (RoomGraph.GetRoom(shopRoom) is not { Shop: > 0 } room) return null;
        if (Game.GameData.ShopInventoryReader.Read(GameData, room.Shop) is not { } def) return null;
        foreach (Game.GameData.ShopStockEntry entry in def.Stock)
        {
            if (entry.ItemId != itemId) continue;
            double copper = Game.Calculators.ShopPriceCalculator.BuyCopper(
                entry.BaseCopper, def.MarkupPercent, PlayerStats.Charm);
            return (long)Math.Ceiling(copper);
        }
        return null;
    }

    // Live key-possession check for DoorOpenManager's opportunistic floor grab:
    // is the player confidently carrying the key for itemId? Compared by name
    // against the inventory's key-ring + carried list, normalized (count prefix +
    // article stripped). Biased to false on any uncertainty (inventory not yet
    // parsed, name mismatch) so the door FSM errs toward a harmless `get` rather
    // than skipping it.
    private bool HoldsKeyItem(int itemId)
    {
        string? name = ItemNames.GetName(itemId);
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (!Inventory.IsLoaded) return false;
        string want = NormalizeItemName(name);
        Game.Inventory.InventorySnapshot snap = Inventory.Snapshot;
        if (snap.Keys is { } keys)
            foreach (string k in keys)
                if (NormalizeItemName(k) == want) return true;
        foreach (string c in snap.CarriedItems)
            if (NormalizeItemName(c) == want) return true;
        return false;

        static string NormalizeItemName(string s)
        {
            s = s.Trim().ToLowerInvariant();
            // Key-ring / carried entries carry a count prefix ("2 black serpent
            // key") the bare game-data name lacks; strip it so a key held in
            // multiples still matches. Only a run of digits followed by a space
            // qualifies, so a name like "3-pronged fork" is left intact.
            int d = 0;
            while (d < s.Length && char.IsDigit(s[d])) d++;
            if (d > 0 && d < s.Length && s[d] == ' ') s = s[(d + 1)..].TrimStart();
            if (s.StartsWith("the ", System.StringComparison.Ordinal)) return s[4..];
            if (s.StartsWith("an ", System.StringComparison.Ordinal)) return s[3..];
            if (s.StartsWith("a ", System.StringComparison.Ordinal)) return s[2..];
            return s;
        }
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

    // Every concrete place we can be handed itemId on demand, backing
    // PathItemGiveRouter's detour-target search. Filters ItemSourceIndex to the
    // deterministic, keyword-carrying awards (a gated turn-in / purchase / quest
    // reward or a `random` roll isn't a reliable one-command hand-over), then
    // resolves each to a room + command: a Monster giver becomes
    // `ask <noun> <keyword>` at each of its spawn rooms (Summoned By) — <noun> is
    // the last word of the name, the game's `ask` parser taking a single-word
    // target (shared GuardDoorCommandResolver.LastWord) — while a Room giver
    // becomes the bare keyword typed verbatim in that room. The GiverName kept for
    // the picker stays the full name (a readable "(ask Gnome Commander)" promise).
    // Computed lazily (only when a path-item need fires), so the fan-out is never
    // materialised at load time. Also decides DeterministicGiveExists, the
    // shop/drop stand-down.
    private System.Collections.Generic.IReadOnlyList<Game.Map.GiveSource> GiveSourcesForItem(int itemId)
    {
        System.Collections.Generic.IReadOnlyList<ItemGiver> givers = ItemSources.GiversOf(itemId);
        if (givers.Count == 0)
            return System.Array.Empty<Game.Map.GiveSource>();
        var result = new System.Collections.Generic.List<Game.Map.GiveSource>();
        foreach (ItemGiver g in givers)
        {
            if (!g.Deterministic || g.Keyword.Length == 0) continue;
            if (g.Kind == ItemGiverKind.Monster)
            {
                string noun = Game.Map.GuardDoorCommandResolver.LastWord(g.Name);
                if (noun.Length == 0) continue;   // no addressable noun — can't ask
                string command = $"ask {noun} {g.Keyword}";
                foreach (Game.Map.RoomKey room in ItemSources.GiverMonsterRoomsOf(g.Number))
                    result.Add(new Game.Map.GiveSource(room, command, g.Name));
            }
            else // Room giver — the keyword is the verbatim room CMD.
            {
                result.Add(new Game.Map.GiveSource(new Game.Map.RoomKey(g.Map, g.Room), g.Keyword, g.Name));
            }
        }
        return result;
    }

    // True when a free deterministic give can supply itemId at a resolved room —
    // the precedence gate the shop and drop routers stand down on. Mirrors the
    // give router's own "can act" test (a resolved candidate list), so the two
    // never both claim the item.
    private bool DeterministicGiveExists(int itemId) => GiveSourcesForItem(itemId).Count > 0;

    // True when a room "You notice ..." entry resolves to a real item in the
    // active set. The cash filters (GroundItemTracker.IsCashEntry /
    // CashManager.TryParseCashEntry) use this as an authoritative tiebreaker so a
    // stacked denomination-named item ("2 gold key") isn't mistaken for a coin
    // pile — currency records aren't in Items.json, so a true coin pile never
    // resolves here.
    private bool IsKnownGroundItem(string entry) => ItemNames.FindByName(entry) is not null;

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

        Models.GameData.ItemOverlay overlay = ResolveItemOverlay(number);
        return new Game.Inventory.AutoGetItemsManager.ResolvedItem(
            number, name, overlay.AutoCollect ?? false, overlay.CannotBeTaken ?? false,
            MaxCap(overlay), ItemNames.WeightOf(name) ?? 0);
    }

    // Resolve a carried entry for AutoDiscard: map the loose carry wording to an
    // item Number, read its verbatim Name, and resolve the AutoDiscard flag plus
    // keep floor. A LoyalItem is never discarded — loyalty (never-drop) wins over
    // a stray AutoDiscard flag. Returns null only when the entry isn't an item in
    // the active set (so the engine skips scenery / cash lines).
    private Game.Inventory.AutoDiscardManager.ResolvedDiscard? ResolveAutoDiscardItem(string entry)
    {
        if (ItemNames.FindByName(entry) is not int number) return null;
        string? name = ItemNames.GetName(number);
        if (string.IsNullOrWhiteSpace(name)) return null;

        Models.GameData.ItemOverlay overlay = ResolveItemOverlay(number);
        bool discard = (overlay.AutoDiscard ?? false) && !(overlay.LoyalItem ?? false);
        return new Game.Inventory.AutoDiscardManager.ResolvedDiscard(
            number, name, discard, KeepFloor(overlay));
    }

    // Resolve a shop stock-row name for AutoBuy: map it to an item Number, read
    // the verbatim Name, and resolve the AutoBuy flag plus MaxToGet cap. LIGHT
    // items are excluded — Auto-light owns their acquisition. Returns null when
    // the row name isn't an item in the active set.
    private Game.Inventory.AutoBuyManager.ResolvedBuy? ResolveAutoBuyItem(string entry)
    {
        if (ItemNames.FindByName(entry) is not int number) return null;
        string? name = ItemNames.GetName(number);
        if (string.IsNullOrWhiteSpace(name)) return null;

        Models.GameData.ItemOverlay overlay = ResolveItemOverlay(number);
        bool buy = (overlay.AutoBuy ?? false) && Lights.FindByName(name) is null;
        return new Game.Inventory.AutoBuyManager.ResolvedBuy(
            number, name, buy, MaxCap(overlay));
    }

    // Resolve a carried entry for AutoSell: map the loose carry wording to an item
    // Number, read the verbatim Name, and resolve the AutoSell flag plus keep
    // floor. A LoyalItem is never sold, and LIGHT items are excluded (Auto-light
    // owns them). Returns null only when the entry isn't an item in the active set.
    private Game.Inventory.AutoSellManager.ResolvedSell? ResolveAutoSellItem(string entry)
    {
        if (ItemNames.FindByName(entry) is not int number) return null;
        string? name = ItemNames.GetName(number);
        if (string.IsNullOrWhiteSpace(name)) return null;

        Models.GameData.ItemOverlay overlay = ResolveItemOverlay(number);
        bool sell = (overlay.AutoSell ?? false)
            && !(overlay.LoyalItem ?? false)
            && Lights.FindByName(name) is null;
        return new Game.Inventory.AutoSellManager.ResolvedSell(
            number, name, sell, KeepFloor(overlay));
    }

    // MDB ItemType for a container — the only kind auto-open acts on.
    private const int ContainerItemType = 8;

    // Resolve a carried entry for AutoOpen: map the loose carry wording to an
    // item Number, read the verbatim Name, and resolve the AutoOpen flag gated
    // on the item actually being a container (ItemType == 8) — a stale overlay
    // flag on a non-container never opens. Returns null only when the entry
    // isn't an item in the active set.
    private Game.Inventory.AutoOpenManager.ResolvedOpen? ResolveAutoOpenItem(string entry)
    {
        if (ItemNames.FindByName(entry) is not int number) return null;
        string? name = ItemNames.GetName(number);
        if (string.IsNullOrWhiteSpace(name)) return null;

        Models.GameData.ItemOverlay overlay = ResolveItemOverlay(number);
        bool open = (overlay.AutoOpen ?? false)
            && ItemNames.ItemTypeOf(number) == ContainerItemType;
        return new Game.Inventory.AutoOpenManager.ResolvedOpen(number, name, open);
    }

    // The 4-tier ItemOverlay for an item Number (Defaults seed → Global → BBS →
    // Char). Shared by the auto-collect / stash / discard / buy / sell resolvers.
    private Models.GameData.ItemOverlay ResolveItemOverlay(int number) =>
        Resolver.ResolveGameData<Models.GameData.ItemOverlay>(
            "Items",
            number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ItemOverlaySeed.GetOverlay(number));

    // Items the user explicitly chose to obtain for the current walk via the route
    // picker's "obtain then cross" option — a per-walk override of the persistent
    // AutoObtainForPath flag. An explicit pick is consent, so these source through
    // the same give/shop/drop pipeline without needing the item pre-flagged.
    // Entries are removed as they're acquired and cleared on a Stop (see the wiring
    // in the ctor); replaced wholesale on each fresh obtain-pick.
    private readonly HashSet<int> _forcedPathObtain = new();

    // Set (replacing any prior) the items the next walk should obtain for its path
    // regardless of their AutoObtainForPath flag. Called by RouteChoicePrompt when
    // the user picks the hazard "obtain then cross" route.
    public void ForcePathObtain(IEnumerable<int> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        _forcedPathObtain.Clear();
        foreach (int id in itemIds) if (id > 0) _forcedPathObtain.Add(id);
    }

    // Per-item on-demand path acquisition gate: the persistent AutoObtainForPath
    // opt-in on the item's overlay, OR a per-walk forced-obtain override. Checked
    // means every acquisition method is in play (party redistribute, textblock
    // give, shop buy, bank withdraw, drop reroute). Backs all three path-item
    // routers' isEnabled predicates and the picker's name helpers.
    private bool IsAutoObtainForPath(int itemId)
    {
        if (itemId <= 0) return false;
        if (_forcedPathObtain.Contains(itemId)) return true;
        return ResolveItemOverlay(itemId).AutoObtainForPath ?? false;
    }

    // Distance used to SCORE a path-item give/shop detour. Suspends the acquirable
    // gates so a SOLE-route gate — a hazard we'll counter, an item gate we'll buy —
    // doesn't read as infinite and reject every source: the router prices the route
    // the character walks WITH the sourced item in hand, which crosses that gate.
    // For a gate a free route already bypasses this is a no-op; it only rescues the
    // sole-route case (e.g. buying a rope to reach the hazard-gated FCCO cavern).
    private int? PathItemDetourDistance(Game.Map.RoomKey a, Game.Map.RoomKey b)
    {
        using (Movement.SuspendAcquirableGates())
            return Bfs.DistanceBetween(a, b, Movement);
    }

    // For a hazard's any-of counter set, pick the counter the run can most cheaply
    // obtain and describe how — preferring one already on the current room's floor
    // (free + here), then a free give, a shop buy, then a monster drop. Unlike the
    // PathItem*Name picker helpers this is FLAG-INDEPENDENT: the picker's "obtain
    // then cross" choice is explicit consent, so it offers a counter the run can
    // source whether or not it's flagged AutoObtainForPath. Returns the chosen
    // counter id + a source phrase, or null when none is sourceable.
    public (int ItemId, string Source, bool OnFloor)? ResolveHazardCounter(
        IReadOnlyList<int> counters, Game.Map.RoomKey source, Game.Map.RoomKey destination)
    {
        ArgumentNullException.ThrowIfNull(counters);
        // On the current floor — grabbed in place, so it never routes through the
        // detour pipeline (the caller issues a `get`); flagged OnFloor to say so.
        foreach (int id in counters)
            if (IsItemOnFloor(id)) return (id, "grab from the floor here", true);

        // The destination is hazard-gated (that is WHY a counter is needed), so the
        // shop/give/drop round-trip THROUGH it is only reachable with the acquirable
        // gates suspended — otherwise dist(source→shop→dest) is infinite and every
        // source is rejected. Suspend them for the whole resolution, matching the
        // route we'd actually walk (counter in hand crosses the hazard).
        using (Movement.SuspendAcquirableGates())
        {
            foreach (int id in counters)
                if (DeterministicGiveExists(id)
                    && Game.Map.PathItemGiveRouter.TrySelectGiver(
                        GiveSourcesForItem(id), source, destination,
                        (a, b) => Bfs.DistanceBetween(a, b, Movement), out Game.Map.GiveSource giver))
                    return (id, $"ask {giver.GiverName}", false);

            foreach (int id in counters)
            {
                System.Collections.Generic.IReadOnlyList<Game.Map.RoomKey> shops = ShopRoomsSellingItem(id);
                if (shops.Count > 0
                    && Game.Map.PathItemShopRouter.TrySelectShop(
                        shops, source, destination,
                        (a, b) => Bfs.DistanceBetween(a, b, Movement), out Game.Map.RoomKey shop)
                    && RoomGraph.GetRoom(shop)?.Name is { Length: > 0 } shopName)
                    return (id, $"buy at {shopName}", false);
            }

            foreach (int id in counters)
            {
                System.Collections.Generic.IReadOnlyList<Game.Map.MonsterDropSpawn> spawns = DropSpawnsForItem(id);
                if (spawns.Count > 0
                    && Game.Map.MonsterDropRouter.SelectNearestSpawn(
                        spawns, Bfs.ComputeDistancesFrom(source, Movement),
                        out Game.Map.MonsterDropSpawn best, out _))
                    return (id, $"dropped by {best.MonsterName}", false);
            }
        }

        return null;
    }

    // Items to provision for entering a hazard room: its single-counter mandatory
    // items always, plus any any-of counter the user forced via the route picker's
    // "obtain then cross" choice (so that one counter is sourced like a gate item).
    private System.Collections.Generic.IReadOnlyList<int> HazardAnnounceItems(Game.Map.RoomKey key)
    {
        RoomHazardIndex.RoomHazard? hazard =
            RoomHazards.HazardForSpell(RoomGraph.GetRoom(key)?.Spell ?? 0);
        if (hazard is null) return System.Array.Empty<int>();
        if (_forcedPathObtain.Count == 0) return hazard.MandatoryItems;

        System.Collections.Generic.List<int> items = new(hazard.MandatoryItems);
        foreach (System.Collections.Generic.IReadOnlyList<int> group in hazard.RequirementGroups)
            if (group.Count > 1)
                foreach (int id in group)
                    if (_forcedPathObtain.Contains(id) && !items.Contains(id))
                        items.Add(id);
        return items;
    }

    // Is item `id` lying on the current room's floor? Matched by name against the
    // ground survey (which stores the noun phrases parsed from "You notice … here."),
    // leniently both ways so an article/qualifier on either side still matches.
    private bool IsItemOnFloor(int id)
    {
        string? name = ItemNames.GetName(id);
        if (string.IsNullOrEmpty(name)) return false;
        foreach (string floor in GroundItems.Items)
            if (floor.Contains(name, StringComparison.OrdinalIgnoreCase)
                || name.Contains(floor, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // Sole-route auto-obtain decision. Given the requirements of a route that has
    // NO gate-free alternative, returns true when every gate is a single
    // carry-item or ticket the user flagged AutoObtainForPath — the walk should
    // arm the acquisition pipeline and cross the gate rather than fail. A key gate
    // (never auto-sourced), a hazard, or any unflagged item makes it false, so the
    // walk stays a plain route whose BFS fails in place naming what's missing.
    // Hazard-only sole routes never reach here — the picker offers those.
    public bool ShouldAutoObtainSoleRoute(IReadOnlyList<RouteRequirement> requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        if (requirements.Count == 0) return false;
        foreach (RouteRequirement req in requirements)
        {
            if (req.Kind is not (RouteRequirementKind.CarryItem or RouteRequirementKind.Ticket))
                return false;
            if (req.ItemIds.Count != 1 || !IsAutoObtainForPath(req.ItemIds[0]))
                return false;
        }
        return true;
    }

    // Per-person copies to provision when auto-obtaining an item for a path. Aims
    // for MaxToGet (the carry target: rope=1, a waterskin its 2–3), never below
    // the MinToKeep floor, and never below 1 — so an item with no carry policy set
    // resolves to the historical one-per-member quantity. The party-provisioning
    // gate multiplies this by the head-count for the whole-party total.
    private int PathPerPersonQuantity(int itemId)
    {
        if (itemId <= 0) return 1;
        Models.GameData.ItemOverlay overlay = ResolveItemOverlay(itemId);
        int floor = ParseCount(overlay.MinToKeep, 0);
        int cap = ParseCount(overlay.MaxToGet, 0);   // 0 = "All" / blank / unset
        int target = cap > 0 ? cap : Math.Max(1, floor);
        return Math.Max(1, Math.Max(target, floor));
    }

    // Keep floor for the discard / sell engines: MinToKeep when the user set
    // MustHaveMinimum, else zero (unbanded → drain to nothing). "None", blank, and
    // non-numeric strings resolve to zero.
    private static int KeepFloor(Models.GameData.ItemOverlay overlay) =>
        (overlay.MustHaveMinimum ?? false) ? ParseCount(overlay.MinToKeep, 0) : 0;

    // Acquisition cap for the buy engine: MaxToGet as an int, with the "All"
    // sentinel and blank / non-numeric strings meaning unbounded (int.MaxValue →
    // buy the whole affordable stock).
    private static int MaxCap(Models.GameData.ItemOverlay overlay) =>
        ParseCount(overlay.MaxToGet, int.MaxValue);

    // Parse a carry-policy count string (MinToKeep / MaxToGet). Non-negative
    // numeric strings yield their value; blank, null, and the MegaMUD sentinels
    // ("None" / "All") fall back to the caller's default.
    private static int ParseCount(string? raw, int fallback) =>
        int.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int v) && v >= 0
            ? v : fallback;

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

        Models.GameData.ItemOverlay overlay = ResolveItemOverlay(number);
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
        PartyProbe.Enabled                = dto.ProbeStatsOnPartyJoin;
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
        PartyProbe.Enabled                = defaults.ProbeStatsOnPartyJoin;
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
        // Auto-discard offload verb: hide <item> vs drop <item>.
        AutoDiscard.HideMode = dto.HideWhenDiscarding;
    }

    private void ResetOtherToDefaults()
    {
        Models.Profile.OtherSettings defaults = new();
        RemoteCommands.MaxSuicideLivesThreshold = defaults.MaxSuicideLivesThreshold;
        TrapDisarm.MaxSearchAttempts = defaults.MaxTrapSearchAttempts;
        TrapDisarm.MaxDisarmAttempts = defaults.MaxTrapDisarmAttempts;
        PartyComeback.MaxBacktrackRooms = defaults.MaxComebackBacktrackRooms;
        ComebackRequest.Enabled = defaults.AutoRequestComebackWhenLeftBehind;
        AutoDiscard.HideMode = defaults.HideWhenDiscarding;
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
        AutoLair.TravelCostModel = BuildTravelCostModel(dto);
    }

    // Map an AutoLairSettings travel-cost selection to the concrete
    // Game.Map.ITravelCostModel. Shared by the profile-load path (above) and
    // the Settings tab's live apply so the two never drift on how a mode maps
    // to a model. The realm-aware Auto mode resolves against the active
    // game-data set: the ParaMUD movement formula (live enc% + gear quickness)
    // on Paradigm, the measured encumbrance buckets on stock. Because Auto
    // reads the realm, ApplyAutoLairFromActiveProfile is re-run on
    // ActiveSetChanged so a realm switch rewires the model.
    public Game.Map.ITravelCostModel BuildTravelCostModel(Models.Profile.AutoLairSettings dto) =>
        dto.TravelCostMode switch
        {
            Models.Profile.AutoLairTravelCostMode.Flat =>
                new Game.Map.FlatTravelCostModel(Math.Max(0.1, dto.FlatSecondsPerHop)),
            Models.Profile.AutoLairTravelCostMode.EncumbranceGated =>
                new Game.Map.EncumbranceGatedTravelCostModel(PlayerState, dto.HopTimesByEncumbrance),
            _ => GameData.ActiveRealm == Game.RealmType.ParaMud
                ? new Game.Map.ParadigmMovementCostModel(() => Inventory.Snapshot, GameData)
                : new Game.Map.EncumbranceGatedTravelCostModel(PlayerState, dto.HopTimesByEncumbrance),
        };

    private void ResetAutoLairToDefaults()
    {
        Models.Profile.AutoLairSettings defaults = new();
        AutoLair.Heuristic = defaults.Heuristic;
        AutoLair.IdlePenalty = defaults.IdlePenalty;
        AutoLair.EngageTimeoutSeconds = defaults.EngageTimeoutSeconds;
        AutoLair.TravelCostModel = BuildTravelCostModel(defaults);
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

    // Whether a name is a player currently in our room — the known-player gate for
    // others'-POV actions/emotes (they're room-local, so the actor is in the room's
    // entity list). Matches the first name token so "Fujin" resolves a "Fujin
    // WuzHere" resolved name.
    private bool IsKnownRoomPlayer(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (RoomClassifier.Current is not { } obs) return false;
        foreach (Game.Combat.RoomEntity e in obs.Entities)
        {
            if (e.Kind != Game.Combat.EntityKind.Player) continue;
            if (FirstTokenEquals(e.RawName, name) || FirstTokenEquals(e.ResolvedName, name))
                return true;
        }
        return false;
    }

    private static bool FirstTokenEquals(string? full, string name)
    {
        if (string.IsNullOrEmpty(full)) return false;
        int sp = full.IndexOf(' ');
        string first = sp < 0 ? full : full[..sp];
        return string.Equals(first, name, StringComparison.OrdinalIgnoreCase);
    }

    // Parse the active BBS's nightly-cleanup time + zone into a config for the
    // cleanup-boss DEAD/ALIVE state. Null when no BBS, a blank time, or an
    // unparseable time (a bad zone id falls back to the local zone).
    private BossCleanupConfig? ResolveBossCleanupConfig()
    {
        if (ResolveActiveBbs() is not { } bbs) return null;
        if (string.IsNullOrWhiteSpace(bbs.CleanupTimeOfDay)) return null;
        if (!TimeSpan.TryParse(bbs.CleanupTimeOfDay.Trim(), out TimeSpan tod)
            || tod < TimeSpan.Zero || tod >= TimeSpan.FromDays(1)) return null;
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(bbs.CleanupTimeZoneId); }
        catch { tz = TimeZoneInfo.Local; }
        return new BossCleanupConfig(tod, tz);
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
        Display.ScrollbackLines = values.ScrollbackLines;
        Display.BackscrollWheelLines = values.BackscrollWheelLines;
        Display.TerminalCols = values.TerminalCols;
        Display.TerminalRows = values.TerminalRows;

        // Font family / size and the terminal-scaling toggle are all char-tier
        // (General), not BBS-tier, but they share this method's ProfileLoaded /
        // ProfileMutated triggers — so seed them here from the active profile.
        // The Settings → General Apply path also writes these live, since a plain
        // profile Save fires neither event.
        Models.Profile.GeneralSettings general =
            ReadSection<Models.Profile.GeneralSettings>(Profile.Current, "General");
        Display.FontFamily = string.IsNullOrWhiteSpace(general.TerminalFontFamily)
            ? DisplayConfig.DefaultFontFamily
            : general.TerminalFontFamily;
        Display.FontSize = general.TerminalFontSize ?? DisplayConfig.DefaultFontSize;
        Display.NavTooltipFontFamily = string.IsNullOrWhiteSpace(general.NavTooltipFontFamily)
            ? DisplayConfig.DefaultFontFamily
            : general.NavTooltipFontFamily;
        Display.NavTooltipFontSize = general.NavTooltipFontSize ?? DisplayConfig.DefaultNavTooltipFontSize;
        Display.ScaleToWindow = general.ScaleTerminalToWindow;
        // SplashAnimate is deliberately NOT seeded here: it's an install-global
        // attract-screen preference, sourced once at startup from the Global default
        // profile (see the seed after the startup profile load). Re-seeding it per
        // profile-load would let an auto-loaded named profile re-enable the splash the
        // user turned off, and flash the animation for a beat before connect.
        TerminalInput.Enabled = general.TypeToTerminalFromOtherWindows;

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
        Display.FontFamily = DisplayConfig.DefaultFontFamily;
        Display.FontSize = DisplayConfig.DefaultFontSize;
        Display.NavTooltipFontFamily = DisplayConfig.DefaultFontFamily;
        Display.NavTooltipFontSize = DisplayConfig.DefaultNavTooltipFontSize;
        // SplashAnimate is intentionally left untouched — it's install-global (seeded
        // once at startup from the Global default profile), so a profile close/swap
        // must not reset it back on.
        Display.ScrollbackLines = defaults.ScrollbackLines;
        Display.BackscrollWheelLines = defaults.BackscrollWheelLines;
        Display.TerminalCols = defaults.TerminalCols;
        Display.TerminalRows = defaults.TerminalRows;
        Display.ScaleToWindow = false;
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

    // Cancels a one-shot voyage DispatcherTimer when the sail completes early (or
    // the walk resets) — the walker cancels its armed deadline through this handle.
    private sealed class DispatcherTimerHandle(Avalonia.Threading.DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
