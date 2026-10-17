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

    // Owns Global/global.json — the Global settings tier.
    public SettingsService Settings { get; }

    // Owns the currently loaded character profile (Character tier).
    public ProfileService Profile { get; }

    // Owns BBS/{bbs}/bbs.json — the BBS tier.
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

    // Same indirection for the Rooms section — lets an item's clickable
    // bought/sold shop line jump to the host room's Rooms-tab record (by
    // Map Number + Room Number) without a back-reference to the main VM.
    private Action<int, int>? _roomGameDataOpener;
    public void SetRoomGameDataOpener(Action<int, int> opener) => _roomGameDataOpener = opener;
    public void OpenRoomGameData(int map, int room) => _roomGameDataOpener?.Invoke(map, room);

    // Opens (or re-focuses) the Navigation window and centres the map on a
    // given room. No-op until the main VM binds it.
    private Action<Game.Map.RoomKey>? _navigateToRoomOpener;
    public void SetNavigateToRoomOpener(Action<Game.Map.RoomKey> opener) => _navigateToRoomOpener = opener;
    public void NavigateToRoom(Game.Map.RoomKey key) => _navigateToRoomOpener?.Invoke(key);

    // Opens (or re-focuses) the Navigation window and ARMS a walk to a room —
    // sets QueuedDestination exactly as picking a search result does, so the user
    // then clicks Run. No-op until the main VM binds it.
    private Action<Game.Map.RoomKey>? _queueWalkOpener;
    public void SetQueueWalkOpener(Action<Game.Map.RoomKey> opener) => _queueWalkOpener = opener;
    public void QueueWalkTo(Game.Map.RoomKey key) => _queueWalkOpener?.Invoke(key);

    // Opens (or re-focuses) the Navigation window and STARTS an immediate walk to a
    // room — the full "Walk here" path (stop conflicting engines, route picker for a
    // gated/hazard/trap crossing, GOTO history), not merely arming it. No-op until
    // the main VM binds it.
    private Action<Game.Map.RoomKey>? _goWalkOpener;
    public void SetGoWalkOpener(Action<Game.Map.RoomKey> opener) => _goWalkOpener = opener;
    public void GoWalkTo(Game.Map.RoomKey key) => _goWalkOpener?.Invoke(key);

    // The same walk started on the player's behalf (a Sell Tour stop): it takes the
    // default route without the route picker, which shows only when the player's
    // avoid rooms are in the way. The task ends once the walk is under way or was
    // called off, and says which. Not under way until the main VM binds it.
    private Func<Game.Map.RoomKey, Task<bool>>? _errandWalkOpener;
    public void SetErrandWalkOpener(Func<Game.Map.RoomKey, Task<bool>> opener) => _errandWalkOpener = opener;
    public Task<bool> ErrandWalkTo(Game.Map.RoomKey key) => _errandWalkOpener?.Invoke(key) ?? Task.FromResult(false);

    // Type text at the game through the SAME path the terminal / Conversation input
    // uses — macro split, alias expansion, and the outbound cast/attack/chat/movement
    // observers — so a programmatic send is indistinguishable from the user typing
    // it. Distinct from SendGameCommand, which rides the raw wire-sender with none of
    // that. No-op until the main VM binds it.
    // The origin is for the sent-command trail only, which names where a line of
    // the user's came from: a link or button in a window unless the caller says
    // otherwise (the local API does).
    private Action<string, SentCommandTrail.Origin>? _typedInputSender;
    public void SetTypedInputSender(Action<string, SentCommandTrail.Origin> sender) => _typedInputSender = sender;
    public void SendTypedInput(string text) => SendTypedInput(text, SentCommandTrail.Origin.Window);
    public void SendTypedInput(string text, SentCommandTrail.Origin origin)
    {
        if (!string.IsNullOrWhiteSpace(text)) _typedInputSender?.Invoke(text, origin);
    }

    // Drop a bracketed yellow status line into the terminal scrollback — the same
    // "[…]" notice cadence quest-availability / roomba-complete use. The text is
    // written verbatim (no auto-bracketing): callers supply their own "[…]" so a
    // multi-line report reads exactly as they compose it. No-op until the main VM
    // binds it; the sink already marshals to the UI thread.
    private Action<string>? _terminalNotice;
    public void SetTerminalNotice(Action<string> sink) => _terminalNotice = sink;
    public void WriteTerminalNotice(string text)
    {
        if (!string.IsNullOrWhiteSpace(text)) _terminalNotice?.Invoke(text);
    }

    // The hang-up carve-out as the engines read it (the character's own tier),
    // for the bug report.
    public bool AllowHangupInAllOffMode =>
        ReadSection<Models.Profile.GeneralSettings>(Profile.Current, "General").AllowHangupInAllOffMode;

    // A walk, loop or Auto-Lair started by hand with the master switch off is
    // refused up front, with a notice naming what could not start (user,
    // 2026-10-09), where it used to plan and then sit frozen with nothing said.
    // True when refused: the caller starts nothing, and stops nothing for it
    // either. what is "Walk", "Loop" or "Auto-Lair"; verb is "resume" for a
    // paused run the user tries to carry on. One already running when the switch
    // goes off is not stopped here: it freezes on MovementCoordinator.AutoAllGate
    // and resumes.
    public bool RefuseStartForMasterSwitch(string what, string verb = "start")
    {
        if (!AutoModeController.Blocks("Hand-started navigation", $"{what} ({verb})")) return false;
        Log.Info("Navigation", $"{what} cannot {verb}: the master switch is off.");
        WriteTerminalNotice($"[{what} cannot {verb}: the master switch (Auto-All) is off]");
        return true;
    }

    // Toggle the Profile Management window. The window is owned by the main VM
    // (it borrows that VM's connection gate + profile-swap path), so non-main
    // surfaces — the Settings → BBS tab's "Open Profile Management" button —
    // route through this bridge rather than reaching MainWindowViewModel. No-op
    // until the main VM binds it.
    private Action? _openProfileManager;
    public void SetOpenProfileManager(Action open) => _openProfileManager = open;
    public void OpenProfileManager() => _openProfileManager?.Invoke();

    // A status bar that draws a layout with sample values, for the preview in
    // Settings → BBS + Display. The bar's chip and connection light read the main
    // VM's state, so the main VM makes it; null until it binds this.
    private Func<ViewModels.StatusBar.StatusBarViewModel>? _statusBarPreviewFactory;
    public void SetStatusBarPreviewFactory(Func<ViewModels.StatusBar.StatusBarViewModel> create) =>
        _statusBarPreviewFactory = create;
    public ViewModels.StatusBar.StatusBarViewModel? CreateStatusBarPreview() => _statusBarPreviewFactory?.Invoke();

    // Opens (or re-focuses) the single Navigation Management dialog. Both the map
    // window's "Navigation Management" button and the toolbar Start button route
    // here so there's only ever one instance — no two identical windows. The bool
    // picks the default tab: the toolbar entry lands on Go To, the map entry on
    // Loops. No-op until the main VM binds it.
    private Action<bool>? _navManagerOpener;
    public void SetNavManagerOpener(Action<bool> opener) => _navManagerOpener = opener;
    public void OpenNavManager(bool startOnGotoTab = false) => _navManagerOpener?.Invoke(startOnGotoTab);

    // Centres the map on a room ONLY if the Navigation window is already open —
    // never force-opens it, so following a room elsewhere doesn't hijack the
    // screen when the map is closed.
    private Action<Game.Map.RoomKey>? _centerNavigationIfOpenOpener;
    public void SetCenterNavigationIfOpenOpener(Action<Game.Map.RoomKey> opener) => _centerNavigationIfOpenOpener = opener;
    public void CenterNavigationIfOpen(Game.Map.RoomKey key) => _centerNavigationIfOpenOpener?.Invoke(key);

    // While a Create/Edit Loop dialog is open it registers a sink here, so a left-click
    // on the Navigation map appends the clicked room to that dialog's waypoint list. The
    // map lives in the Navigation window and the loop editor is a separate modeless
    // dialog, so they coordinate through this holder (the dialog's code-behind sets the
    // sink on Opened, clears it on Closed). Null = no editor capturing map clicks.
    private Action<Game.Map.RoomKey>? _loopWaypointCaptureSink;
    public void SetLoopWaypointCaptureSink(Action<Game.Map.RoomKey>? sink) => _loopWaypointCaptureSink = sink;
    // Returns true when an open loop editor consumed the click (so the caller skips its
    // own map-click handling); false when no editor is capturing.
    public bool TryCaptureLoopWaypoint(Game.Map.RoomKey key)
    {
        if (_loopWaypointCaptureSink is not { } sink) return false;
        sink(key);
        return true;
    }

    // Flashes a room green on the map and centres on it for a few seconds ONLY if
    // the Navigation window is already open — never force-opens it. Driven by
    // WhereReplyTracker when an @where reply telepath lands, so an answered
    // "where are you?" lights up on the map. No-op until the main VM binds it.
    private Action<Game.Map.RoomKey>? _highlightWhereOpener;
    public void SetHighlightWhereOpener(Action<Game.Map.RoomKey> opener) => _highlightWhereOpener = opener;
    public void HighlightWhereRoom(Game.Map.RoomKey key) => _highlightWhereOpener?.Invoke(key);

    // Shows another player's route from their @path reply on the Navigation map — ONLY
    // if the window is already open, like the @where flash. Driven by PathReplyTracker.
    // No-op until the main VM binds it.
    private Action<string, Game.Remote.PathReport>? _leaderRouteOpener;
    public void SetLeaderRouteOpener(Action<string, Game.Remote.PathReport> opener) => _leaderRouteOpener = opener;
    public void ShowLeaderRoute(string sender, Game.Remote.PathReport report) => _leaderRouteOpener?.Invoke(sender, report);

    // Same, for our party leader's reply to an @goto they accepted.
    private Action<string, Game.Map.RoomKey>? _leaderGotoOpener;
    public void SetLeaderGotoOpener(Action<string, Game.Map.RoomKey> opener) => _leaderGotoOpener = opener;
    public void ShowLeaderGoto(string sender, Game.Map.RoomKey dest) => _leaderGotoOpener?.Invoke(sender, dest);

    // Single source of truth for "are you sure?" prompts (exit /
    // hangup / save / delete). Lives at Global tier; mirrored from
    // SettingsService on startup and every save.
    public ConfirmService Confirm { get; }

    // App-wide severity-tagged ring-buffer log. Status bar + log pane subscribe.
    public LogService Log { get; }

    // Self-update against GitHub Releases (check + user-triggered download/replace).
    // A startup check plus a twice-daily re-check (both gated on
    // GlobalSettings.AutoCheckForUpdates) set its availability flag; the splash banner
    // and the main window's title-bar crawl read it. It never installs on its own.
    public Services.Update.UpdateService Update { get; }

    // Tees Log to a rolling on-disk file (Logs/{ts}-program.log) so a
    // hard hang / kill leaves a post-mortem trail the in-memory ring can't.
    // Only writes while LogDiagnostics.AutoCollectLogs is on (default off).
    public ProgramLogFile ProgramLog { get; }

    // Samples the process memory footprint a-minute-at-a-time to its own
    // Logs/{ts}-memory.log, kept out of the program log, so an all-night
    // session leaves a trail that tells a managed-heap leak from working-set creep.
    // Only writes while LogDiagnostics.AutoCollectLogs is on (default off).
    public MemoryUsageLog MemoryLog { get; }

    // The Session Statistics window's figures, written to their own file under Logs
    // every few minutes while in the game. Only while
    // LogDiagnostics.LogSessionStatistics is on (default off).
    public SessionStatsLog SessionStatsLog { get; private set; } = null!;
    // UI-thread stall probe and work timings, written to their own log while
    // Auto-collect logs is on.
    public PerformanceMonitor Performance { get; }

    // Background memory hygiene: compacts the LOH once a game-data set settles
    // (reclaiming the startup JSON-parse fragmentation) and periodically returns
    // glibc's free native pages to the OS, so a loop-mode session running for days
    // doesn't hold a working set far larger than its live heap. Invisible — no
    // toggle; see the class comment for the timing that keeps it unnoticed.
    public MemoryMaintenance Memory { get; }

    // Diagnostic switches surfaced in the Log pane: DebugDiagnostics and
    // CombatDiagnostics gate in-memory Debug/Combat channel generation;
    // AutoCollectLogs gates whether the on-disk diagnostic files (program /
    // memory / combat trace) are written at all. Consumers
    // (e.g. Game.Combat.RoundDamageTracker) read this rather than a settings
    // section. The live state is mirrored to GlobalSettings.LogDiagnostics:
    // applied at startup, saved on Changed (see the Apply/Persist helpers below).
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

    // Edge-snapping + main-window cluster-move for the panel windows. Reads its
    // on/off from the Global "Snap windows together" setting; fed each window via
    // WindowLayoutStore.AttachWindow.
    public WindowSnapManager WindowSnap { get; }

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
    // connection. Feeds the Wire Inspector window and the bug report's raw-wire
    // capture.
    public WireBuffer Wire { get; }
    public Game.InGameCapture InGameCapture { get; private set; } = null!;
    private const string BoardMenuHold = "at the board's menu";

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

    // True while a boss-timer-sync merge window is open. Set by BossTimerSyncViewModel
    // (ctor/Dispose); read by the main window's auto-open so a user-typed `@timer sync`
    // doesn't spawn a second window when one is already collecting.
    public bool TimerSyncWindowActive { get; set; }

    // App-singleton chat history. Survives profile swap / connect /
    // disconnect; cleared only on app exit or explicit
    // Game.ChatHistoryStore.Clear.
    public Game.ChatHistoryStore ChatHistory { get; }

    // Persists the Conversation window + Transaction history to per-character
    // rolling files under Logs. Constructed once the chat router and the
    // transaction tracker exist.
    public SessionLogService SessionLog { get; private set; } = null!;

    // Live player state — HP / mana / position / mana type. Updated by
    // Player from every prompt line; bound by the status
    // bar, the Workshop STATS section, and automation
    // engines that gate on HP / MP thresholds.
    public Game.PlayerState PlayerState { get; }

    // True when the character's Combat-tab min-mana threshold is read as a percentage
    // (vs an absolute value). Surfaced so per-monster override editors can present the
    // same min-mana control (% cap vs absolute ceiling) as Settings → Combat.
    public bool CombatSpellManaModeIsPercentage =>
        ReadSection<Models.Profile.CombatSettings>(Profile.Current, "Combat")
            .SpellManaThresholdMode == Models.Profile.ThresholdMode.Percentage;

    // Parses MajorMUD status-line prompts into PlayerState.
    // Sole writer of the state's HP / MA / position / mana-type fields
    // (the single-writer IL scan enforces this).
    public Game.PromptParser Player { get; }

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

    // Master enable for inventory Tab-completion (Settings -> General), read by
    // every input widget that offers it (the terminal, the Conversation window).
    // Each widget owns its own InventoryAutoCompleter instance — the cycling
    // state (stem/tail/candidates/index) is per-widget, like
    // CommandHistoryNavigator over the shared CommandHistory above — but the
    // on/off setting is one value they all share, so it lives here instead of
    // being pushed into every instance separately.
    public bool InventoryTabCompleteEnabled { get; set; } = true;

    // Live party-membership state — roster, leader, per-member HP%/MA%/
    // position/status-flags. Updated by Party from
    // follows-you / stops-following messages and the multi-line
    // par table. Bound by the PartyWindow and read by the
    // remote-command engine to gate the @party <sub> whitelist.
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

    // Registers the party-essential @-command handlers against
    // RemoteCommands: @version, @health, @status, @where, @who, @path,
    // @party (status query + sub-command dispatch), @wait, @ok, @waiting,
    // @lives, @invite, @join. Other handlers register with the engine
    // directly.
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
    // Built beside Inventory; wire bound in MainWindowViewModel.
    public Game.TooHeavyWaitSignal TooHeavyWait { get; private set; } = null!;

    // One-to-many @-command sender: the @Reset on loop start and the
    // @have / @level / @wealth party probes.
    public Game.Remote.PartyBroadcaster PartyBroadcaster { get; }

    // Paces every outgoing telepath and resends the ones the server's throttle
    // refused ("--- Telepath Not Sent ---"). Sits in front of the socket write.
    public Game.Remote.TelepathPacer Telepaths { get; }

    // Live mirror of the active realm's game-menu commands
    // (GameCommands.EntryCommand / GameCommands.ExitCommand), re-read on
    // profile load and realm re-pin. HangupHandler, RelogHandler,
    // MainMenuEntryAutomation, the cleanup log-off and the low-HP hang-up
    // read from here instead of the profile.
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
    // Models.GameData.PlayerRemoteControls.SysopCommands
    // category — @dupe. Copies the sender's permission set onto a
    // known player (telepath / gangpath only).
    public Game.Remote.DupeHandler Dupe { get; }

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

    // Collects a demanded path item (NeedKind.PathItem) the moment a floor survey
    // reveals it — so search-en-route is a real sourcing method, independent of the
    // Auto-Get engine's master toggle + per-item AutoCollect flag.
    public Game.Map.PathItemFloorCollector PathItemFloor { get; private set; } = null!;

    // Consumer of RemoteCommands for the
    // Models.GameData.PlayerRemoteControls.QueryInventory
    // category — @wealth / @enc / @have / @what.
    // Reads the Game.Inventory.InventoryManager snapshot and the
    // GroundItems survey; replies only.
    public Game.Remote.InventoryQueryHandler InventoryQuery { get; private set; } = null!;

    // Paradigm transport-token charge tracker + its @token read-only query handler.
    public Game.Tokens.TokenTracker Tokens { get; private set; } = null!;
    public Game.Remote.TokenQueryHandler TokenQuery { get; private set; } = null!;

    // General limited-use item charge tracker — captures "Uses remaining: N" from any
    // `look` (Paradigm-only line), surfaced in Character Info. Tokens populate via their
    // login look; other limited-use items when the player looks at them.
    public Game.Inventory.ItemChargeTracker ItemCharges { get; private set; } = null!;

    // Stock-realm counterpart: counts `use` sends for limited-use items (stock prints
    // no charge line) so Character Info can show remaining = max − used; rechargeables
    // restock at the BBS cleanup time. Persisted on the character profile.
    public Game.Inventory.ItemUseCountTracker ItemUseCounts { get; private set; } = null!;

    // Typed `open <target>` watcher — lets the Chest Offload tab track a chest
    // opened from the terminal.
    public Game.Inventory.OutboundOpenObserver OutboundOpen { get; } = new();

    // Every chest opened (window button or typed): the loot list the Chest Offload
    // window shows, saved on the profile, and the room announcement of what dropped.
    public Game.Inventory.ChestOpenTracker ChestOpens { get; private set; } = null!;

    // Walks and sells the Chest Offload list shop by shop, never selling more of an
    // item than the chests gave.
    public Game.Inventory.ChestSellTour ChestSellTour { get; private set; } = null!;

    // Realm-aware charge lookup over the two trackers above, shared by Character Info
    // and the @uses remote query so their readouts never diverge.
    public Game.Inventory.CarriedChargeReadout CarriedCharges { get; private set; } = null!;

    // @uses [item] — read-only remaining-charges report off CarriedCharges.
    public Game.Remote.ItemUsesQueryHandler ItemUsesQuery { get; private set; } = null!;

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

    // The master switch, shared by the toolbar / Action-menu "Auto-All" button
    // and the @auto-all remote command. Off silences every automatic system.
    public Game.AutoModeController AutoModeController { get; }

    // An enable probe for an automatic system that has no toggle of its own:
    // true while the master switch is off, counting the skip under the system's
    // name (AutoModeController.Blocks).
    private Func<bool> MasterSwitchOff(string system) => () => AutoModeController.Blocks(system);

    // Leader-side @comeback party-pickup flow — pauses the
    // running movement engine, walks to recover a stranded follower
    // (explicit room or backtrack along the just-walked path), re-
    // invites + awaits follow, then resumes the captured engine.
    public Game.Remote.PartyComebackManager PartyComeback { get; private set; } = null!;

    // Recognises an @where reply telepath and flashes its room on the nav map.
    public Game.Remote.WhereReplyTracker WhereReply { get; private set; } = null!;

    // Sends one `i` after a death, when the character stands in a room again.
    public Game.Inventory.PostDeathInventoryRefresh InventoryAfterDeath { get; private set; } = null!;

    // The `i` a refused toll or fare asks for, kept owed while it can't be sent.
    public Game.Inventory.OwedPurseRead PurseRead { get; private set; } = null!;

    // A leader's check at a toll exit: every follower's purse asked, the short
    // ones paid for when we can spare it, the exit closed to the party otherwise.
    public Game.Map.PartyTollGate PartyToll { get; private set; } = null!;
    public Game.Remote.PathReplyTracker PathReply { get; private set; } = null!;
    public Game.Remote.LeaderBossTravelProbe LeaderBossTravel { get; private set; } = null!;

    // Follower-side @comeback sender: telepaths @comeback to the leader once
    // when the party walks off without us.
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

    // On a realm that drops items for a hang-up: compares what was held before the
    // link dropped with the login's inventory read, and picks up what is short and
    // lying in the room. Armed from the Connected handler; wire bound in
    // MainWindowViewModel.
    public Game.Inventory.HangupItemRecheck HangupItems { get; private set; } = null!;

    // Where the dead of a room were seen to arrive, kept with the game-data set,
    // and the watcher that learns a pair from a death the client witnesses. The
    // hang-up death check reads them for the rooms no list covers.
    public LearnedArrivalRoomStore LearnedArrivals { get; private set; } = null!;
    public Game.Recovery.LearnedArrivalLookup LearnedArrivalLands { get; private set; } = null!;
    public Game.Recovery.ArrivalRoomLearner ArrivalLearner { get; private set; } = null!;

    // Leader-side reconnect party reform — the mirror of PartyRejoin. Snapshots the
    // followers we're leading at disconnect and, on the first in-game room after the
    // reconnect, rebases the grace window + holds the loop so a nightly-cleanup
    // reconnect waits for them to return and re-party instead of stranding them.
    public Game.Remote.PartyReformCoordinator PartyReform { get; private set; } = null!;

    // Drives the @trap <direction> / walker auto-disarm flow: direct
    // `disarm trap <dir>` + FIFO request queue + Traps-skill gate. Bound by
    // TrapRemote's handler at dispatch time, configured via
    // Models.Profile.OtherSettings.MaxTrapDisarmAttempts in Settings → Other.
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

    // Winch-gate crossing FSM (pull → turn → wait for gate → move), shared by the
    // walker + loop the same way Door / HiddenSearch are.
    public Game.Map.WinchManager Winch { get; }

    // Auth boundary + queue gate for @trap: parses the
    // direction, runs the channel-aware Traps-skill gate, and hands
    // off to TrapDisarm. @trap stop drains the
    // queue + aborts the in-flight request.
    public Game.Remote.TrapHandler TrapRemote { get; }

    // @train handler — trains in place (no walk) on a permitted party
    // member's request, applying the CP plan when Auto-train-stats is on.
    public Game.Remote.TrainHandler TrainRemote { get; }

    // Party auto-train: member readiness reports, the leader's quorum + trip, and the
    // @ptrain handshake that carries them between MudPlay clients.
    public Game.Train.PartyTrainCoordinator PartyTrain { get; }
    public Game.Remote.PartyTrainHandler PartyTrainRemote { get; }

    // @equip <set> [update] / @equip-all handler — a permitted party member asks
    // us to wear one of our saved gear sets, or to save what we're wearing into
    // one. The older dashed @equip-<set> still routes via RemoteCommands's prefix
    // handler into Equipment.
    public Game.Remote.EquipHandler EquipRemote { get; private set; } = null!;

    // @profile — swap the active casting spell profile (AlterSettings-gated).
    public Game.Remote.ProfileSwapHandler ProfileSwap { get; private set; } = null!;

    // @events / @event — list the character's Events and switch one on or off
    // (AlterSettings-gated).
    public Game.Remote.EventRemoteHandler EventRemote { get; private set; } = null!;

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

    // The exact prompt matcher built from the active profile's statline (the same
    // one installed on PromptScanner). The StatlineReconciler forces the live
    // statline to this setting, so this pattern is what actually prints on the
    // wire — InboundMoveEchoScanner reads it to spot the echoed command after ANY
    // configured prompt, not just the default "[HP=..]:". Resets to the class
    // default on profile close.
    public System.Text.RegularExpressions.Regex CurrentStatlinePromptRegex { get; private set; }
        = Game.StatlinePromptRegexBuilder.Default;

    // Reasserts the editor's statline on every connect. Verifies the live
    // prompt against the editor-built pattern, resends set statline when the
    // game has drifted (e.g. a fresh character on the class default), and
    // flags the mismatch to the user when the resends don't take.
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
    // When each combat round, HP / mana gain and posture change was seen, for working
    // out a realm's tick cycle from a bug report.
    public Game.TickTimingLog TickTiming { get; }

    // The HP amounts passive regen can pay the live character, which the regen cycles
    // use to leave out heals and other HP sources by their size.
    public Game.HpRegenExpectationSource HpRegenExpected { get; private set; } = null!;

    // Live mirror of the loaded character profile's Display settings.
    // The Settings → Display section writes through to this so changes
    // (font size in particular) apply without restarting the app.
    public DisplayConfig Display { get; } = new();

    // Char-tier toolbar visibility mirror. MainWindow toolbar buttons
    // bind their IsVisible here; hydrated from the character's "Toolbar"
    // section on every profile load / mutate and reset on close.
    public ToolbarConfig Toolbar { get; } = new();

    // Char-tier live mirror of the customizable terminal right-click menu. The
    // MainWindow code-behind rebuilds the ContextMenu from ContextMenu.Layout;
    // hydrated on every profile load / mutate and reset on close, mirroring
    // Toolbar above.
    public ContextMenuConfig ContextMenu { get; } = new();

    // AES-GCM encrypt / decrypt for short secrets (BBS passwords).
    // Ciphertext is stored inline on the owning record (e.g.
    // Models.Profile.BbsCredentials.EncryptedPassword),
    // so profile JSON stays fully self-contained for backup. The
    // per-user key lives at <data root>/.credkey.
    public PasswordProtector Passwords { get; } = new();

    // One-flag pause switch wrapping every engine's wire-sender.
    // Raised by Game.SuicidePasswordTracker while a
    // password-entry prompt is active so engine auto-sends don't
    // pollute the input.
    public EngineSendGate EngineGate { get; } = new();

    // The last commands sent, held or dropped, with who sent each: a bug
    // report's answer to "why did it send that" (SentCommandTrail).
    public SentCommandTrail SentCommands { get; } = new();

    // The last minutes of the program log, always on disk for a hang or a forced
    // kill; and the startup block and periodic status line that make that log
    // readable on its own.
    public RecentLogFile RecentLog { get; }
    public ClientHeartbeat Heartbeat { get; }

    // What each last-minutes file opens with. Read on the writer's thread, any
    // time from the first log entry on: before the settings and game data exist
    // their halves are simply not known yet.
    private IReadOnlyList<string> RecentLogHeader()
    {
        string? set = null, profile = null;
        try
        {
            set = GameData?.ActiveSet;
            profile = Settings?.Current.LastUsedProfile?.Name;
        }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException)
        {
            // Still constructing: the header goes without them.
        }
        List<string> lines = new() { "# MudPlay last-minutes log: the program log's last few minutes, kept for a hang or a forced kill" };
        foreach (string line in ClientHeartbeat.StartupLines(set, profile)) lines.Add("# " + line);
        return lines;
    }

    // How long each engine has been in its state, sampled once a second (probes
    // are registered by BugReportHoldsAndWaits once every engine exists).
    public EngineStateWatch StateWatch { get; } = new();

    // Two-flag one-shot coordinator for "intentional hangup" intent.
    // Set by every engine that deliberately drops the carrier
    // (Game.Remote.HangupHandler, HealthManager's low-HP hang-up).
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

    // The board log-off command the player last sent (`;o` to come straight back,
    // `=x` to stay off), read by ViewModels.MainWindowViewModel at the disconnect.
    public SentExitCommand SentExit { get; } = new();

    // Passive observer for the in-game set suicide /
    // suicide password flows. Locks
    // EngineGate for the duration of each prompt and
    // captures the user-typed new password (committed to the
    // profile's Models.Profile.CharacterProfile.EncryptedSuicidePassword
    // on the server-side Password Changed confirmation).
    public Game.SuicidePasswordTracker SuicidePassword { get; private set; } = null!;

    // Live cache of imported MajorMUD game data. Loads JSON tables on
    // demand from game data/{set}/; the active set follows the active
    // realm's Models.Settings.RealmProfile.ActiveGameDataSet
    // (falling back to Models.Settings.GlobalSettings.DefaultGameDataSet
    // when no realm is pinned). Per-tab consumers
    // convert a table's rows into typed model collections
    // and call EvictTable to hand the table back.
    public GameDataCache GameData { get; } = new();

    // Loopback-only HTTP control API — live state, program log, scrollback and a
    // bug-report-equivalent dump for inspecting the client while something is
    // going wrong, rather than after. Opt-in via Settings → General.
    public Api.LocalApiServer LocalApi { get; private set; } = null!;

    // How LocalApi reaches the terminal transcript. Set by MainWindowViewModel,
    // which owns the emulator and is constructed after AppServices.
    private Func<Terminal.TerminalEmulator?>? _emulatorProvider;

    // Hand the API a way to read the live terminal. Called once, by the main
    // view-model's constructor.
    public void SetEmulatorProvider(Func<Terminal.TerminalEmulator?> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _emulatorProvider = provider;
    }

    // Whether there's a live session, and if not whether a redial is armed. The
    // TelnetClient is per-connection and owned by the main view-model, so this
    // arrives as a provider the same way the emulator does.
    //
    // Worth its own accessor because "is it even connected?" is the first
    // question asked of a client that appears stuck, and without it a consumer
    // has to infer a dropped session from the absence of log traffic — which
    // looks identical to a client that's simply quiet.
    public sealed record ConnectionSnapshot(bool Connected, bool Connecting, bool ReconnectPending);

    private Func<ConnectionSnapshot>? _connectionProvider;

    public void SetConnectionProvider(Func<ConnectionSnapshot> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _connectionProvider = provider;
    }

    public ConnectionSnapshot Connection
        => _connectionProvider?.Invoke() ?? new ConnectionSnapshot(false, false, false);

    // Show the whole first-run setup tour as a demo (every step as if nothing were
    // configured) — the Program Log test button. MainWindow wires this on open;
    // null before the window exists. Invoke on the UI thread.
    public Action? StartSetupTourDemo { get; set; }

    // ----- Visual Guides cross-window bridge ------------------------------
    // The guide runner lives on MainWindowViewModel, but guide steps play out in
    // other windows (Profile Management, Settings, Navigation, the Game Data
    // Browser, …). Those report progress by calling NotifyTourAction with a
    // GuideActions key, and glow the right control by reading CurrentTourAction
    // (the action the guide wants next) and subscribing to TourActionChanged.
    // String keys only — no view-model coupling.
    public Action<string>? NotifyTourAction { get; set; }

    public string? CurrentTourAction { get; private set; }
    public event Action? TourActionChanged;

    public void SetCurrentTourAction(string? key)
    {
        if (string.Equals(CurrentTourAction, key, StringComparison.Ordinal)) return;
        CurrentTourAction = key;
        TourActionChanged?.Invoke();
    }

    private void ApplyLocalApiFromGlobalSettings()
        => LocalApi.ApplySettings(Settings.Current.LocalApiEnabled, Settings.Current.LocalApiPort);

    // In-memory cache of the active character's
    // Models.GameData.Trigger list + the session-scoped named-variable
    // store the triggers capture into. Drives MessageRouter integration +
    // runtime action dispatch.
    public TriggerEngine Triggers { get; }

    // In-memory cache of the active character's
    // Models.GameData.Alias entries. Outgoing-text
    // mirror of Triggers; matches on the first token of typed input.
    public AliasEngine Aliases { get; }

    // Observed + edited Models.GameData.PlayerRecord
    // store. Game.WhoListParser records each `who` row into it via
    // RecordObservation.
    public PlayerDatabase Players { get; }

    // Flags the local character's displayed alignment stale when the game
    // prints "A dark cloud passes over you", clearing on the next who.
    // Read by the Player Workshop's Character Info tab.
    public Game.AlignmentTracker Alignment { get; }

    // Add / rename / remove a BBS's realms (Profile Management, Settings → BBS + Display).
    public RealmCatalog Realms { get; }

    // Drives the train stats screen to apply the saved CP plan. Wrapped
    // by TrainerWalk, which owns the walk-to-trainer + level-up.
    public Game.AutoTrainManager AutoTrain { get; }

    // What each effect line a `stat` screen listed does to the six trainable
    // stats, by the message catalogue and the Spells table.
    public Game.Spells.ListedEffectCatalog ListedEffects { get; }

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

    // Reads the character's live quest-flag values off the wire (realm-aware). Its send is
    // bound after telnet connects and its LineExtractor is attached in MainWindowViewModel,
    // like the other probes.
    public Game.Quests.QuestFlagProbe QuestFlagReader { get; private set; } = null!;

    // A SECOND, on-demand quest-flag probe dedicated to the @quest remote command, so a
    // remote query mid-login never clobbers the daily QuestFlagSync's shared reader (both
    // BeginCollect on the same instance would cross-clear). Line extractor attached in
    // MainWindowViewModel alongside QuestFlagReader.
    public Game.Quests.QuestFlagProbe QuestQueryReader { get; private set; } = null!;

    // Login-time quest-flag completion sync (opt-in per character): reads the flags via
    // QuestFlagReader and marks newly-complete quests. Run by MainWindowViewModel before the
    // availability dump.
    public Game.Quests.QuestFlagSyncManager QuestFlagSync { get; private set; } = null!;

    // The @quest remote command handler (QueryQuests category).
    public Game.Remote.QuestQueryHandler QuestQuery { get; private set; } = null!;

    // Loaded character's Models.GameData.Macro store.
    // Surfaced by the Game Data Browser → Macros tab; MacroDispatcher
    // intercepts keystrokes and dispatches from the same store.
    public MacroStore Macros { get; }

    // Per-set quest name / visibility / edited-step overlay store. Backs the
    // Player Workshop → Quest Status tab (the mechanical step + bonus data is
    // crawled from GameData's TBInfo at runtime). Reloads its
    // overlay on GameDataCache.ActiveSetChanged.
    public QuestStore Quests { get; }

    // User-defined conversation emotes (Global tier). Layers on the built-in emoji /
    // Pepe set and publishes the merged scanner to EmoteRuntime for the convo window.
    public EmoteStore Emotes { get; }

    // Realm-wide boss catalog (seed + the realm's overlay); timer values resolve from
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

    // @roomba read-only query handler — reports an item's last-seen gang-house
    // room from GhItemLocations. App-lifetime, like the other query handlers.
    public Game.Remote.RoombaQueryHandler RoombaQuery { get; private set; } = null!;
    public Game.Remote.LoopShareHandler LoopShare { get; private set; } = null!;
    public Game.Remote.LoopShareReceiver LoopShareInbox { get; private set; } = null!;

    // Requester-side @roomba sync listener — merges another MudPlay client's
    // sighting log into GhItemLocations as replies arrive. App-lifetime; unlike
    // BossTimerSyncCollector it isn't gated to a merge-review window (see the
    // class comment), so it's always live once constructed.
    public Game.Remote.RoombaSyncReceiver RoombaSync { get; private set; } = null!;

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
    // Logoff events fire once per session when CleanupWarningWatcher sees
    // the BBS's shutdown warning (EventScheduler subscribes to it); a user
    // disconnect does not fire them.
    public Game.Events.EventScheduler EventScheduler { get; private set; } = null!;

    // Fires State-triggered events (money / encumbrance / exp / level conditions).
    public Game.Events.EventStateWatcher EventStateWatcher { get; private set; } = null!;
    public Game.Events.EventBossWatcher EventBoss { get; private set; } = null!;

    // Sound cues (Settings → Sounds). SoundPlayer hands a sound to the operating
    // system's own player on a pool thread; Sounds decides which cues play and how
    // loud. Both are built in WireSounds.
    public SoundPlayer SoundPlayer { get; private set; } = null!;
    public Game.Sounds.SoundCueEngine Sounds { get; private set; } = null!;

    // The watcher is UI-thread-confined like the rest of the events stack; stat and
    // inventory changes can be raised off it.
    private void EvaluateEventStates()
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) EventStateWatcher.Evaluate();
        else Avalonia.Threading.Dispatcher.UIThread.Post(EventStateWatcher.Evaluate);
    }

    // What State-triggered events compare against. Money and encumbrance stay
    // unknown until the inventory has been read; exp and level until the stat
    // screen has.
    internal Game.Events.EventConditionEvaluator.Readings ReadEventReadings()
    {
        bool invKnown = Inventory.IsLoaded;
        Game.Inventory.EncumbranceReading enc = Inventory.Snapshot.Encumbrance;
        bool statsKnown = PlayerStats.Level > 0;
        return new(
            Copper: invKnown ? Inventory.Snapshot.Currency.TotalCopperValue : null,
            EncumbrancePercent: invKnown && enc.MaxWeight > 0
                ? (int)((long)enc.CurrentWeight * 100 / enc.MaxWeight)
                : null,
            Experience: statsKnown ? PlayerStats.Exp : null,
            Level: statsKnown ? PlayerStats.Level : null);
    }

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
    // from the wcc-derived JSON at Global/Messages.seed.json
    // (bootstrapped from the bundled Defaults/ copy on first
    // launch), persisted per set at game data/{set}/messages.json.
    // Surfaced by the Game Data Browser → Messages tab; the
    // HealthManager / CastingDirector consume the same catalogue at
    // runtime to gate on observed conditions.
    public MessageStore Messages { get; private set; } = null!;

    // Active game-data set's per-monster name records: the canonical
    // name the room classifier resolves a display name to, linked back
    // to its Monsters row. Per-set edits land at
    // game data/{set}/monster-messages.json.
    public MonsterMessageStore MonsterMessages { get; private set; } = null!;

    // Staged, unrecognized-message candidates for the active set —
    // Game.MessageCandidateWatcher's output, reviewed via the LogPane
    // double-click flow or the Game Data Browser's Unrecognized Lines tab. Pure
    // runtime-observed state, not curated data — no seed-file fallback.
    public MessageCandidateStore MessageCandidates { get; private set; } = null!;

    // Watches the wire for lines the Messages catalogue doesn't recognize and
    // stages them in MessageCandidates. AttachLineExtractor lands in
    // MainWindowViewModel alongside the other line consumers.
    public Game.MessageCandidateWatcher MessageCandidateWatcher { get; private set; } = null!;

    // Per-set editable vocabulary of monster flavor adjectives the room classifier
    // strips to resolve a prefixed display name. Defaults to the built-in stock list;
    // edited in the Game Data Browser's Flavor Prefixes section.
    public FlavorPrefixStore FlavorPrefixes { get; private set; } = null!;

    // Turns the wire's Also here: line into
    // a classified Player / Monster / Unknown list. Feeds
    // CombatTracker's gate decisions and the LogPane's
    // unknown-entity click-to-fix dialog.
    public Game.Combat.RoomEntityClassifier RoomClassifier { get; private set; } = null!;

    // Answers a monster on the room roster whose relationship is Hangup (a hang-up)
    // or Flee (a run).
    public Game.Combat.MonsterRelationshipWatcher MonsterWatch { get; private set; } = null!;

    // Auto-greets newly-seen non-party players (Settings → Talk
    // "Greet players when first met"). Subscribes to
    // RoomClassifier's observations; once-per-local-day
    // dedup on the realm's player record. Off by default.
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

    // Per-character log of actual combat outcomes observed against specific
    // monsters — landed/whiffed swing damage extent and confirmed "no effect"
    // (Magical / SpellImmunity gate) discoveries. Feeds Monster Intel's "Your
    // Observations" section, kept visibly separate from MonsterCatalog's
    // authoritative MDB facts. Persists on the loaded profile.
    public Game.Combat.MonsterObservationTracker MonsterObservations { get; private set; } = null!;

    // Owns PlayerState.InCombat and
    // the Game.Map.MovementCoordinator.CombatGate hold
    // state. Cleared automatically when the room is free of
    // engageable monsters.
    public Game.Combat.CombatStateTracker CombatTracker { get; private set; } = null!;

    // Aggregates combat lines into per-round
    // Game.Combat.RoundSummary records, keeping the
    // last 50 in a ring buffer. CombatSessionTracker, the Round Totals
    // board and the par-after-round poll consume the RoundComplete event.
    public Game.Combat.RoundDamageTracker RoundDamage { get; private set; } = null!;
    public Game.Combat.RecentFoeNames RecentFoes { get; private set; } = null!;

    // Party members' HP between `par` polls, from the damage and heals seen landing on
    // them. Set right after RoundDamage, whose damage lines it reads.
    public Game.PartyHpEstimator PartyHp { get; private set; } = null!;

    // Aggregates combat lines + RoundDamage rounds
    // into the session combat figures (hit / miss / crit / dodge rates,
    // physical & backstab damage extents, per-round damage) the Session
    // Stats panel displays. Pure downstream subscriber; reset on the session
    // boundary alongside RoundDamage.
    public Game.Combat.CombatSessionTracker CombatSession { get; private set; } = null!;

    // The latest round's damage table for the Round Totals window (View → Round
    // Totals). Built just before the round-complete hook that feeds it.
    public Game.Combat.RoundTotalsBoard RoundTotals { get; private set; } = null!;

    // Generic colour + wording combat-line recognizer (no per-monster data). Its
    // static ClassifyAsCombat is what the engine reads; the instance keeps each
    // in-combat-window line's classification (a Game.Combat.CombatLineKind) for
    // the Wire Inspector's classified view and the bug report.
    public Game.Combat.CombatLineClassifier CombatClassifier { get; private set; } = null!;

    // Divides the session's wall-clock time across the player's
    // activities (waiting / moving / attacking / resting HP / resting MA) plus
    // the blinded / poisoned overlays, for the Time Analysis panel. Fed by
    // PlayerState, Conditions, and
    // RoomTracker; reset on the session boundary.
    public Game.Combat.TimeAnalysisTracker TimeAnalysis { get; private set; } = null!;

    // Counts the session's kills, experience, copper / items, sneak entries and
    // walk steps for the Session Stats panel's Session Statistics section, and
    // keeps the rolling histories behind its per-hour rates and sparklines.
    // Reset on the session boundary.
    public Game.Combat.SessionActivityTracker SessionActivity { get; private set; } = null!;

    // Our own time to next level: the banked-aware estimate at the session exp/hour,
    // run through ONE countdown clock that Session Stats, the status bar and the
    // Party window's self row all read — so they show the same figure, counting down
    // like a timer instead of each recomputing (and jumping) on its own.
    private readonly Game.Calculators.TnlCountdown _selfTnl = new();

    public (Game.Calculators.TimeToLevelEstimator.Result Estimate, TimeSpan? Remaining) SelfTimeToLevel()
    {
        Game.Calculators.TimeToLevelEstimator.Result est = Game.Calculators.TimeToLevelEstimator.Estimate(
            PlayerStats, GameData, SessionActivity.Snapshot().ExperiencePerHour);
        return (est, _selfTnl.Remaining(est.Eta, DateTimeOffset.UtcNow));
    }

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

    // Structured per-room tally of coin we believe is stashed, persisted per realm
    // (StashStore). Backs auto-train funding's "is that stash worth a detour"
    // question, which the display-oriented TransactionHistory above can't answer.
    public Game.Cash.StashLedger StashBalances { get; } = new();
    // Persists StashBalances in the active realm's folder, shared by its characters.
    public StashBalanceStore StashStore { get; private set; } = null!;

    // The member's side of a leader's stash transfer: @get-stash.
    public Game.Remote.GetStashHandler GetStash { get; private set; } = null!;

    // Carries a stash room's coin to a bank, trip by trip (the map's right-click
    // "Transfer Stash to Bank").
    public Game.Cash.StashTransferRunner StashTransfer { get; private set; } = null!;

    // Stops whatever loop or Auto-Lair is running and starts the transfer. Null when
    // it is under way; otherwise why not.
    public string? StartStashTransfer(Game.Map.RoomKey stash, Game.GameData.BankShop bank)
    {
        if (StashTransfer.IsBusy) return "a stash transfer is already running";
        // A trip started by hand is a walk started by hand; the caller prints the
        // refusal. An Event's transfer never gets here with the switch off.
        if (AutoModeController.Blocks("Hand-started navigation", "Stash transfer"))
            return "the master switch (Auto-All) is off";
        if (ErrandHasTheWalker) return "another trip is using the walker — stop it first";
        if (AutoLair.IsActive) AutoLair.Stop("stash transfer started");
        if (LoopRunner.State != Game.Map.LoopState.Idle) LoopRunner.Stop("stash transfer started");
        return StashTransfer.Start(stash, bank.Key, bank.Name);
    }

    // The map menu's Stop Stash Transfer. Cancel alone ends the transfer and leaves
    // the walker to finish the leg it was on (report paradigm-20261002-142509).
    public void StopStashTransfer()
    {
        StashTransfer.Cancel("stopped from the map menu");
        MovementControl.Stop();
    }

    // The members a stash transfer shares the carrying with: the rest of the party,
    // when Settings → Cash + Items has the option on and we lead it. Only a leader's moves
    // bring the others along to the stash and the bank.
    private IReadOnlyList<string> StashTransferPartyMembers()
    {
        if (!ReadSection<Models.Profile.CashSettings>(Profile.Current, "Cash").StashTransferPartyShare
            || !PartyState.IsInParty || !PartyState.SelfIsLeader)
            return Array.Empty<string>();
        List<string> givens = new();
        foreach (Game.PartyMember m in PartyState.Members)
        {
            if (m.IsSelf || m.IsInvited || string.IsNullOrEmpty(m.Name)) continue;
            (string given, _) = Models.GameData.PlayerObservation.SplitName(m.Name);
            if (!string.IsNullOrEmpty(given)) givens.Add(given);
        }
        return givens;
    }

    // The "Resume first?" question while it is on screen.
    private ViewModels.ConfirmDialogViewModel? _resumeFirstQuestion;

    // The game data was read in afresh, another set or the active one again
    // (MovementController.StopForGameDataChange, ahead of its stop): end what holds a
    // run of the old data's rooms that the stop doesn't end by itself, so nothing
    // walks those numbers again unasked. Each is ended through its own cancel, with
    // the reason, and named for the notice when it was under way. The money and
    // training errands are ended by the controller through the errand hook, and the
    // token route, the loop hand-off, the path-item routers and a deferred corpse
    // sweep by its Stopping event. Reset States does more than this (exit FSMs,
    // outstanding needs, party waits, stealth, combat and gear latches, a flee):
    // none of those puts a stopped run back, and new game data is no reason to
    // reset them.
    private void EndHeldRunsForGameDataChange(string reason, List<string> stopped)
    {
        // An event standing in a wait or a rest, or suspended behind a detour, with
        // its Then and everything queued behind it.
        if (Events.RunningEventName is { } running) stopped.Add($"event '{running}'");
        Events.NoteStop(reason);
        // Waiting for its member, the pick-up holds the loop, Auto-Lair or walk it
        // interrupted and puts it back when they join.
        if (PartyComeback.RecoveringMember is not null) stopped.Add("the party pick-up");
        PartyComeback.Cancel(reason);
        // Selling at a shop, the tour walks to its next one when the sales are in.
        if (ChestSellTour.IsRunning) stopped.Add("the sell tour");
        ChestSellTour.Cancel(reason);
        if (GhSweep.Phase != Game.Map.GhSweepManager.SweepPhase.Idle) stopped.Add("the Roomba sweep");
        GhSweep.Stop(reason);
        // A leader's party training trip waits for minutes between its walks (the
        // members' trains), and walks on to the next trainer it planned when the
        // wait ends. The errand hook has named it ("the training trip") and ended
        // the trainer's side; this ends the trip itself.
        // The errand's end, ahead of this, may have called it off for the same stop.
        if ((PartyTrain.CallOffTrip(reason) | PartyTrain.TakeCalledOffFor(reason))
            && !stopped.Contains("the training trip"))
            stopped.Add("the party training trip");
        // Detours that hold a room to walk to, or back to, afterwards.
        MonsterDropRouter.Cancel();
        ShortcutSource.Cancel();
        AutoLightShopRouter.Cancel();
        // Route cards on screen were planned on the last set's rooms (each says so as
        // it closes), and the "Resume first?" question is about a run of them.
        ViewModels.Navigation.RouteChoicePrompt.CloseOpenCards();
        _resumeFirstQuestion?.NoCommand.Execute(null);
    }

    // Another game-data set became the active one, and the stop for it has been
    // made (StopOnGameDataChange). What a route still wanted, and what the party
    // was counted to hold or was handed for one, is kept by item number, and under
    // another set a number is another item: left standing, a need had the floor
    // pickup `get` whatever now carries its number, and a party count still out
    // went on to hand that item round. The same set read in again keeps its
    // numbers, and these with them.
    //
    // After the stop, not with what is ended ahead of it
    // (EndHeldRunsForGameDataChange): forgetting the party count lets go of the
    // walk it holds, which would take a step before its stop reached it.
    private void ForgetRouteItemsOfOtherGameData(Game.Map.GameDataLoadChange change)
    {
        if (change.ReadInAgain) return;
        string reason = change.Why;
        int needs = Needs.Clear(NeedKind.PathItem);
        if (needs > 0) Log.Info("Needs", $"{needs} route item need(s) dropped: {reason}");
        PartyPathItemGate.Clear();
        PartyHandOvers.Clear(reason);
        ClearPartyShortGateItems(reason);
    }

    // The errand a user's Stop holds instead of ending, as it reads in a sentence;
    // null when none is under way. The money and training trips only: a party
    // comeback or a key detour is over in a few steps and is simply stopped.
    private string? SuspendableErrand() =>
        StashTransfer.IsBusy ? "the stash transfer"
        : TrainerWalk.IsBusy || TrainFunding.IsBusy ? "the training trip"
        : SellDetour.IsDetouring ? "the sell trip"
        : AutoDeposit.IsRerouting ? "the bank trip"
        : null;

    // The same, for a saved bank room (an Event's stash transfer). The room has to
    // still hold a bank in the active game data.
    public string? StartStashTransfer(Game.Map.RoomKey stash, Game.Map.RoomKey bankRoom)
    {
        foreach (Game.GameData.BankShop bank in Game.GameData.BankCatalog.Enumerate(GameData))
            if (bank.Key.Equals(bankRoom)) return StartStashTransfer(stash, bank);
        return $"{bankRoom.Map}/{bankRoom.Room} is not a bank in the active game data";
    }

    // The active set's banks, nearest to `from` first. Reach is counted the way the
    // transfer's own walks plan: through gates whose key or item can be acquired, and
    // across sailings (one step each). A walk-only count called a bank behind a
    // key-door or a boat unreachable.
    public IReadOnlyList<(Game.GameData.BankShop Bank, int? Steps)> BanksNearestFirst(Game.Map.RoomKey from)
    {
        IReadOnlyDictionary<Game.Map.RoomKey, int> distances;
        using (Movement.SuspendAcquirableGatesButUnprotectableHazards())
            distances = Bfs.ComputeDistancesFrom(from, Movement, viaBoats: true);
        return Game.GameData.BankCatalog.ByDistance(Game.GameData.BankCatalog.Enumerate(GameData), distances);
    }

    // The other way round, for a bank room's menu: the character's stash rooms,
    // nearest that bank first and counted the way BanksNearestFirst counts, with the
    // coin each is believed to hold.
    public IReadOnlyList<(Game.Map.RoomKey Stash, int? Steps, long Copper)> StashesNearestFirst(Game.Map.RoomKey bank)
    {
        IReadOnlyDictionary<Game.Map.RoomKey, int> distances;
        using (Movement.SuspendAcquirableGatesButUnprotectableHazards())
            distances = Bfs.ComputeDistancesFrom(bank, Movement, viaBoats: true);
        var rows = new List<(Game.Map.RoomKey Stash, int? Steps, long Copper)>();
        foreach (Game.Map.RoomKey stash in Movement.Stash)
            rows.Add((stash, distances.TryGetValue(stash, out int steps) ? steps : null, StashBalances.Believed(stash)));
        rows.Sort((a, b) =>
        {
            int byReach = (a.Steps ?? int.MaxValue).CompareTo(b.Steps ?? int.MaxValue);
            if (byReach != 0) return byReach;
            int byMap = a.Stash.Map.CompareTo(b.Stash.Map);
            return byMap != 0 ? byMap : a.Stash.Room.CompareTo(b.Stash.Room);
        });
        return rows;
    }

    // In-memory force of cash COLLECTION while an auto-train funding errand runs.
    // Deliberately not a write to the saved AutoGetCash setting: that would move the
    // user's persisted preference twice per train and could race a profile save,
    // leaving it flipped if the errand ended down an unexpected path. An override
    // the gate consults can't survive the errand, let alone the session.
    private bool? _autoGetCashOverride;

    // True while that override is in force. Collection and STASHING share the one
    // AutoGetCash toggle ("cash automation is one mental toggle"), which makes the
    // naive "force the flag on" wrong: the errand's first stop is a stash room, and
    // an armed auto-stash would hide the very coin we walked there to collect. So
    // the override is asymmetric — collection on, stashing off — for its duration.
    internal bool FundingErrandActive => _autoGetCashOverride == true;

    // Whether coin is collected right now: the errand's override, else the Auto Get
    // Cash toggle. The master switch outranks both. The override stays set while
    // the switch is off, so a funding trip it froze collects again once it is on.
    private bool CashCollectionOn() =>
        !AutoModeController.KillSwitchEngaged
        && (_autoGetCashOverride ?? ReadAutoModeFlag(d => d.AutoGetCash));

    // An errand engine is driving the walker for one of its own legs — a bank / stash
    // trip, a sell detour, a trainer or funding trip, a token route, a party pickup, a
    // path-item / light / drop detour, a maze or pyramid solve, a Roomba sweep. Its
    // walks aren't the user's, so their ends aren't the user's walk ending.
    public bool ErrandHasTheWalker =>
        AutoDeposit.IsRerouting || SellDetour.IsDetouring
        || TrainerWalk.IsBusy || TrainFunding.IsBusy || StashTransfer.IsBusy
        || TokenRoute.Active || PartyComeback.RecoveringMember is not null
        || PathItemShopRouter.DetourActive || PathItemGiveRouter.DetourActive
        || PathItemSummonRouter.DetourActive || MonsterDropRouter.DetourActive
        || AutoLightShopRouter.DetourActive
        || MazeSolver.Active || PyramidSolver.Active || GhSweep.IsActive;

    // Collects the money for a train before the run commits to a trainer.
    public Game.Train.TrainFundingRouter TrainFunding { get; private set; } = null!;

    // Everywhere the funding errand could draw from, with what we believe is
    // there. Stash figures are a belief (another player can empty a room without us
    // seeing it); bank figures are certain, tracked from the deposit / withdrawal
    // echoes between `bank` queries. A bank name maps to its room(s) through the
    // shop catalogue — a multi-branch bank contributes one source per branch, since
    // `dep` / `with` are room actions and only the branch you stand in will pay out.
    private List<Game.Train.TrainFundingSource> BuildTrainFundingSources()
    {
        List<Game.Train.TrainFundingSource> sources = new();

        foreach ((Game.Map.RoomKey room, long copper) in StashBalances.NonEmpty())
            sources.Add(new(Game.Train.TrainFundingSourceKind.Stash, room,
                $"stash {room.Map}/{room.Room}", copper));

        IReadOnlyList<Game.GameData.BankShop> banks = Game.GameData.BankCatalog.Enumerate(GameData);
        foreach ((string name, long deposit) in BankBalance.LastKnown)
        {
            if (deposit <= 0) continue;
            foreach (Game.GameData.BankShop b in banks)
                if (string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase))
                    sources.Add(new(Game.Train.TrainFundingSourceKind.Bank, b.Key, b.Name, deposit));
        }

        return Game.Train.TrainFundingSourceFilter.Apply(sources, ReadAutoTrainerSettings());
    }

    private Models.Profile.AutoTrainerSettings ReadAutoTrainerSettings() =>
        ReadSection<Models.Profile.AutoTrainerSettings>(Profile.Current, "AutoTrainer");

    // The shop leg of a train trip: buys and reads the scrolls for spells the
    // character can now learn.
    public Game.Train.ShopSpellErrand ShopSpells { get; private set; } = null!;

    // What ShopSpellOffers was last built from. The build is an Items scan, a Shops
    // read per selling shop and a pass over the room graph, and both the planner and
    // the Auto-Trainer tab ask for it repeatedly. The set is told by its load, not
    // its name: a set imported again over itself keeps the name.
    private (GameDataCache.Load Set, int Class, int Align, int Charm, int Rooms) _shopSpellOffersKey;
    private IReadOnlyList<Game.Train.ShopSpellOffer> _shopSpellOffers = Array.Empty<Game.Train.ShopSpellOffer>();

    // Every spell the loaded character's class can learn from a scroll some shop
    // restocks, with the rooms that sell it and their price at the character's Charm.
    public IReadOnlyList<Game.Train.ShopSpellOffer> ShopSpellOffers()
    {
        GameDataCache.Load load = GameData.ActiveLoad;
        (GameDataCache.Load, int, int, int, int) key = (load, Spellbook.ClassNumber, Spellbook.CharAlign,
            PlayerStats.Charm, RoomGraph.Rooms.Count());
        if (key == _shopSpellOffersKey) return _shopSpellOffers;
        // The cache's load moves before the room graph and the shop stock are
        // rebuilt for it (the spell book is reseeded ahead of both). Asked in
        // between, this answers from the last load's rooms and stock and keeps
        // nothing, so the next ask builds from the load's own.
        bool roomsAndStockAreThisLoads = RoomGraph.LoadedFor == load && ShopStock.LoadedFor == load;

        Dictionary<int, List<Game.Map.RoomKey>> roomsByShop = new();
        foreach (Game.Map.Room room in RoomGraph.Rooms)
        {
            if (room.Shop == 0) continue;
            if (!roomsByShop.TryGetValue(room.Shop, out List<Game.Map.RoomKey>? rooms))
                roomsByShop[room.Shop] = rooms = new();
            rooms.Add(room.Key);
        }

        IReadOnlyList<Game.Train.ShopSpellOffer> offers = Game.Train.ShopSpellCatalog.Build(
            GameData, Spellbook.Available, SpellCatalog.GetTeachingItems(Spellbook.ClassNumber),
            Spellbook.ClassNumber, PlayerStats.Charm,
            ShopStock.ShopsSelling,
            shop => roomsByShop.TryGetValue(shop, out List<Game.Map.RoomKey>? rooms)
                ? rooms
                : Array.Empty<Game.Map.RoomKey>());
        if (!roomsAndStockAreThisLoads) return offers;
        _shopSpellOffers = offers;
        _shopSpellOffersKey = key;
        return _shopSpellOffers;
    }

    // budgetCopper null prices the whole wish list (the funding estimate); the trip
    // itself passes what the purse holds above keep-on-hand.
    private Game.Train.ShopSpellPlan PlanShopSpells(
        Game.Map.RoomKey from, Game.Map.RoomKey returnTo, int level, long? budgetCopper,
        IReadOnlyCollection<Game.Map.RoomKey>? visited = null, IReadOnlyCollection<int>? gaveUp = null) =>
        Game.Train.ShopSpellPlanner.Plan(
            ShopSpellOffers(), level,
            spell => Spellbook.IsObtained(spell) || gaveUp?.Contains(spell) == true,
            SkippedShopSpells(),
            from, returnTo, (a, b) => Bfs.DistanceBetween(a, b, Movement), budgetCopper, visited);

    // What a spell trip starting where the character stands would go for, for the
    // bug report: the stops with their scrolls, and what was left out and why.
    public string DescribeShopSpellTripFromHere()
    {
        if (RoomTracker.State.CurrentRoom is not { } cur) return "(current room unknown)";
        if (PlayerStats.Level <= 0) return "(level unknown)";
        Game.Train.ShopSpellPlan plan = PlanShopSpells(cur.Key, cur.Key, PlayerStats.Level, SpendableCopper());
        List<string> parts = new();
        foreach (Game.Train.ShopSpellStop stop in plan.Stops)
            parts.Add($"{stop.ShopName} ({stop.Room.Map}/{stop.Room.Room}): "
                + string.Join(", ", stop.Purchases.Select(p => $"{p.SpellName} {p.PriceCopper:N0}c")));
        if (plan.Unaffordable.Count > 0) parts.Add("can't afford: " + string.Join(", ", plan.Unaffordable));
        if (plan.Unreachable.Count > 0) parts.Add("no route: " + string.Join(", ", plan.Unreachable));
        return parts.Count == 0
            ? $"nothing to buy at level {PlayerStats.Level} ({ShopSpellOffers().Count} shop-sold spell(s) for the class)"
            : string.Join("; ", parts);
    }

    private IReadOnlyCollection<string> SkippedShopSpells() =>
        ReadAutoTrainerSettings().SkippedShopSpells is { } skipped ? skipped : Array.Empty<string>();

    // Scrolls already in the pack for spells the character can learn now, hasn't,
    // and wants — bought on an earlier trip and never read.
    private IReadOnlyList<Game.Train.ShopSpellPurchase> CarriedSpellScrolls(int level)
    {
        HashSet<string> skipped = new(SkippedShopSpells(), StringComparer.OrdinalIgnoreCase);
        List<Game.Train.ShopSpellPurchase> carried = new();
        foreach (Game.Train.ShopSpellOffer offer in ShopSpellOffers())
        {
            if (offer.ReqLevel > level || Spellbook.IsObtained(offer.SpellNumber) || skipped.Contains(offer.SpellName))
                continue;
            foreach (Game.Train.ShopSpellSource source in offer.Sources)
            {
                if (CountCarriedByName(source.ItemNumber) <= 0) continue;
                carried.Add(new(offer.SpellNumber, offer.SpellName, offer.ReqLevel,
                    source.ItemNumber, source.ItemName, 0));
                break;
            }
        }
        return carried;
    }

    private long SpendableCopper()
    {
        Models.Profile.CashSettings cash = ReadSection<Models.Profile.CashSettings>(Profile.Current, "Cash");
        long keep = (long)cash.KeepOnHandWealth
                    * Game.Inventory.CurrencyHoldings.CopperUnit(cash.KeepOnHandDenomination);
        return Math.Max(0, Inventory.Snapshot.Currency.TotalCopperValue - Math.Max(0, keep));
    }

    // Observes the "You have been slain by..."
    // line and emits Game.Combat.DeathLineWatcher.PlayerDied.
    // DeathRecoveryManager is the primary consumer; other
    // engines subscribe for their own death-clean-up paths.
    public Game.Combat.DeathLineWatcher DeathWatcher { get; private set; } = null!;

    // Refines the active realm's negative-HP death floor
    // (Models.Settings.RealmProfile.PlayerDiesAtHp) from observed slow deaths by
    // watching the local HP trajectory into each death.
    public Game.Health.DeathFloorTracer DeathFloorTracer { get; private set; } = null!;

    // Auto-attack engine. Picks a target from
    // RoomClassifier's last observation and sends the
    // configured attack command while the Auto-Combat auto-mode toggle
    // (GeneralSettings.AutoMode.AutoCombat) is on. Wire sender is bound by
    // MainWindowViewModel alongside the other engines once the telnet client
    // is up.
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

    // A spell's target-class restriction (living / undead / animals-only) by cast-code.
    // Paired with MonsterLife to skip an attack spell the target's type makes ineffective.
    public Game.Combat.SpellTargetTypeIndex SpellTargetType { get; private set; } = null!;

    // Number → max-HP lookup in the active game-data set. Feeds the look-target
    // HP-range readout (MonsterLookParser turns a wound descriptor into an
    // absolute HP window).
    public Game.Combat.MonsterHpIndex MonsterHp { get; private set; } = null!;
    public Game.Combat.MonsterHpTracker MonsterHpEstimates { get; private set; } = null!;

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

    // Typed, parsed-once view of the active game-data set's Monsters table —
    // every raw field this codebase reads somewhere, plus the elemental-resist /
    // Magical / SpellImmu / Dodge / spell-cast-element lookups the individual
    // Monster*Index classes above compute independently. Feeds Monster Intel.
    // Not yet a replacement for those indexes — see MonsterCatalog's own
    // class comment for why they stay separate for now.
    public Game.Combat.MonsterCatalog MonsterCatalog { get; private set; } = null!;

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
    // Other players attacking us, and the Neutral-to-Enemy marking that follows.
    public Game.Pvp.PvpAttackWatcher PvpAttacks { get; private set; } = null!;
    // Who dropped out of our party a moment ago (a teleport split it).
    public Game.Pvp.PartySplitTracker PartySplit { get; private set; } = null!;
    // Asks `who` about a name we have no player record for.
    public Game.Pvp.PvpStrangerLookup PvpStrangers { get; private set; } = null!;
    // The PvP response to an Enemy (hang up, flee), and its flee to a chosen room.
    public Game.Pvp.PvpResponder PvpResponse { get; private set; } = null!;
    public Game.Pvp.PvpFleeWalk PvpFlee { get; private set; } = null!;
    // A fight with another player: attack, chase, hitting back, a leader's @kill.
    public Game.Pvp.PvpFight PvpFight { get; private set; } = null!;

    // Observes mid-room departure lines
    // ("<name> walks out of the room to <dir>.")
    // and removes the departing monster from RoomClassifier's
    // observation so CombatStateTracker drops the Combat gate the
    // departed mob was holding (fleeing player dragging our engaged
    // mob out with them — see report paradigm-20260711-180449).
    public Game.Combat.RoomDepartureWatcher RoomDeparture { get; private set; } = null!;

    // Recognises a monster death from the experience line, with our own
    // targeting naming the mob. On a match, the dead monster is removed
    // from RoomClassifier's observation so CombatManager re-picks
    // correctly instead of sitting on a stale entry.
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

    // Room attacks on a realm with PvP on: who ours would hit, and whether someone
    // else is rooming in a room that was theirs first.
    public Game.Pvp.PvpRoomSafety PvpRoom { get; private set; } = null!;
    // Engages a monster hidden by darkness. A dark room prints no "Also here:"
    // line, so the only tell a hostile shares it is the mob's dark-cyan attack
    // line; this watcher reads the name off that line (gated on
    // RoomTracker.IsInDarkRoom) and injects it into RoomClassifier so
    // CombatManager engages it as if it had been listed.
    public Game.Combat.DarkRoomCombatWatcher DarkRoomCombat { get; private set; } = null!;
    public Game.Combat.MonsterSummonWatcher MonsterSummons { get; private set; } = null!;

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

    // Stops every tracked buff's timer while the character is out of the game, and
    // carries what each had left across a client restart in the character's profile.
    public Game.Spells.BuffTimerKeeper BuffTimers { get; private set; } = null!;

    // Parser for abil <code> breakdown output. Attached to the
    // live line stream in the main VM; feeds ManaRegen the
    // rolled spells: slice of an abil 145 mana-regen read.
    public Game.AbilBreakdownParser AbilBreakdown { get; private set; } = null!;

    // Parser for the sysop `sys st` room dump. Attached to the live
    // line stream in the main VM, and armed only by an outbound sysop
    // status — it writes location, so an unarmed match must never land.
    public Game.Map.SysRoomStatusParser SysRoomStatus { get; private set; } = null!;

    // Request/response wrapper over SysRoomStatus: sends the command and
    // awaits the block, or resolves null when the capability is off or the
    // BBS didn't answer. Sysop-gated per BBS.
    public Game.Map.SysStatusProbe SysStatus { get; private set; } = null!;

    // Turns a sysop room dump into a located position: consulted by the
    // recovery gate before it starts reversing moves, and self-triggered
    // whenever the tracker goes Lost.
    public Game.Map.SysopPositionResolver SysopLocate { get; private set; } = null!;

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

    // Who can cast each spell of the active set (monsters, rooms, or something the
    // client can't see). Conditions reads an applied line several spells share by
    // it. Rebuilt on a worker each time a set's room graph has loaded.
    public Game.GameData.SpellCasterIndex SpellCasters { get; private set; } = null!;

    // Where the character stands, as SpellCasters is asked of it.
    public Game.Conditions.CastReach CastReach { get; private set; } = null!;

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

    // Inbound @panic handler — when a party member says the bare "@panic" signal
    // and we don't ignore it, bail the same way our own low-HP emergency would
    // (via Health.RespondToReceivedPanic). The leader-side broadcast lives on
    // HealthManager.
    public Game.Conditions.PanicResponder PanicResponse { get; private set; } = null!;

    // Stealth state tracker and auto-sneak / auto-hide driver. Owns
    // PlayerState.IsSneaking / PlayerState.IsHidden, emits FSM-state
    // transitions + silent-loss detection on room change, and resends
    // sn / hide while the auto toggles are on.
    public Game.Stealth.StealthManager Stealth { get; private set; } = null!;

    // Holds automation that would end a sneak while keeping it matters (see its wiring).
    public Game.Stealth.SneakGuard SneakGuard { get; private set; } = null!;
    public Game.Stealth.CarriedStealthPenalty CarriedStealth { get; private set; } = null!;

    // The hit magic the character's class and race carry of their own, from the
    // active game data. Asked on every combat decision, so the last answer is kept
    // until the class, race or data set changes. The set is told by its load, not
    // its name: a set imported again over itself keeps the name.
    private (string? Class, string? Race, GameDataCache.Load Set, int Value) _innateHitMagic = (null, null, default, 0);
    public int InnateHitMagic()
    {
        string? cls = PlayerStats.Class, race = PlayerStats.Race;
        GameDataCache.Load set = GameData.ActiveLoad;
        if (_innateHitMagic.Class == cls && _innateHitMagic.Race == race && _innateHitMagic.Set == set)
            return _innateHitMagic.Value;
        int value = 0;
        if (!string.IsNullOrWhiteSpace(cls))
            value += Game.Calculators.ClassCapabilities.InnateHitMagic(GameData.FindRowByName("Classes", cls));
        if (!string.IsNullOrWhiteSpace(race))
            value += Game.Calculators.ClassCapabilities.InnateHitMagic(GameData.FindRowByName("Races", race));
        _innateHitMagic = (cls, race, set, value);
        return value;
    }

    // The rooms of every boss flagged "stop before entering" on the active realm.
    // Resolved live, so a realm swap or an edit on the Bosses tab takes effect at
    // the next walk.
    public IReadOnlySet<Game.Map.RoomKey> BossStopRooms()
    {
        var set = new HashSet<Game.Map.RoomKey>();
        foreach (Models.Profile.BossDef b in Bosses.ResolveForRealm(GameData.ActiveRealm))
        {
            if (!b.StopBefore) continue;
            foreach (string wire in b.Rooms)
                if (Game.Map.RoomKey.TryParseWire(wire, out Game.Map.RoomKey k)) set.Add(k);
        }
        return set;
    }

    // The boss whose room this is, for a route card or a notice. Null when none.
    public string? BossInRoom(Game.Map.RoomKey room)
    {
        foreach (Models.Profile.BossDef b in Bosses.ResolveForRealm(GameData.ActiveRealm))
            foreach (string wire in b.Rooms)
                if (Game.Map.RoomKey.TryParseWire(wire, out Game.Map.RoomKey k) && k.Equals(room))
                    return b.Name;
        return null;
    }

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

    // Death observation aggregator. Surfaces the loaded profile's
    // Models.Profile.CharacterProfile.DeathHistory as the deathpile grid of
    // the Player Workshop's Death Recovery tab, owns the per-character
    // Auto-Recover / Auto-Equip toggles, and drives the corpse-recovery
    // state machine off room re-entry and pickup confirmations.
    public Game.Recovery.DeathRecoveryManager DeathRecovery { get; private set; } = null!;

    // Other players' corpses recovered with their password, kept to hand back.
    public Game.Recovery.CorpseHandoffManager CorpseHandoff { get; private set; } = null!;

    // Runtime inventory parser. Folds the full i
    // dump into a currency + numeric-encumbrance
    // Game.Inventory.InventorySnapshot and patches it
    // incrementally on coin pickups / drops / bank moves. Feeds
    // Cash's encumbrance gate the live carry weight.
    public Game.Inventory.InventoryManager Inventory { get; private set; } = null!;

    // Gear-set apply engine (Player Workshop → Equipment Manager tab). Diffs a
    // saved Models.Profile.EquipmentSet against the live worn loadout
    // (Inventory's snapshot) and paces wear commands;
    // virtual slots write Models.Profile.CombatSettings instead.
    // Driven by the @equip <set> remote command
    // (EquipRemote) and the auto-equip triggers
    // (AutoEquip).
    public Game.Inventory.EquipmentManager Equipment { get; private set; } = null!;

    // Sends `who` to verify our alignment when a gear set disagrees with it.
    public Game.Inventory.AlignmentGearCheck AlignmentCheck { get; private set; } = null!;

    // Location-based auto-equip (Settings → Other). Wears a rule's item while
    // we're inside its matched map area (room number(s) and/or room-name
    // substring) and reverts on exit; driven off RoomTracker transitions so every
    // movement runner honours it, and coordinated with Equipment's per-slot
    // override so gear-set applies don't clobber the location item.
    public Game.Inventory.LocationEquipManager LocationEquip { get; private set; } = null!;

    // Wears the carried item that negates a room's own spell (the phoenix feather
    // against magma heat) before the character steps into the room and gives the
    // slot back once no such room is near, through the same per-slot override the
    // location rules use.
    public Game.Inventory.RoomSpellCounterWear CounterWear { get; private set; } = null!;

    // Casting-spell profiles (Settings → Combat) — the named, quick-swap snapshots
    // of the Combat tab's spell slots. Owns the list, the active pointer, CRUD, and
    // the @profile / toolbar / chip swap, overlaying a profile's spells onto the
    // live Combat section.
    public Game.Combat.CombatProfileManager CombatProfiles { get; private set; } = null!;

    // Router subscriptions feeding the Equipment Manager's unwearable-slot blocks
    // (wear-confirmed / armor-refused / weapon-refused). Held for the app lifetime
    // — AppServices is the singleton, so these live as long as the router.
    private IDisposable? _equipWearOkSub;
    private IDisposable[]? _alignmentMovedSubs;
    private IDisposable? _equipWearFailSub;
    private IDisposable? _equipWieldFailSub;
    private IDisposable? _equipCannotBeWornSub;
    private IDisposable[]? _wornPieceRemovedSubs;

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
    // cascade run off Inventory's snapshot; AutoDeposit owns the
    // walker-driven reroute.
    public Game.Cash.CashManager Cash { get; private set; } = null!;

    // Runtime source-of-truth for the active realm's runic-currency word. Read live
    // by every cash parser / command builder (Cash, Stash, GroundItems) and
    // refreshed on profile / realm swap; defaults to stock "runic".
    public Game.Cash.CurrencyNaming Currency { get; private set; } = null!;

    // Auto-get items engine. Parses the room
    // "You notice ... here." survey, resolves each entry against the
    // active set's items + the per-character
    // Models.GameData.ItemOverlay.AutoCollect flag, and
    // sends get <name> per flagged item. Gated by the
    // AutoGetItems master toggle; defer-until-combat-finished follows
    // Settings → Cash + Items (CollectAfterCombatFinished).
    public Game.Inventory.AutoGetItemsManager AutoGetItems { get; private set; } = null!;

    // Auto-discard engine. On every inventory change, drops each carried item
    // flagged Models.GameData.ItemOverlay.AutoDiscard down to its keep floor —
    // one drop <name> per excess copy. Cleans chest dumps and unwanted collected
    // loot. Gated by the Auto Get Items master toggle plus the per-item
    // AutoDiscard flag; a LoyalItem is never dropped.
    public Game.Inventory.AutoDiscardManager AutoDiscard { get; private set; } = null!;

    // Auto-buy engine. Watches the emitted line stream for a shop `list` readout,
    // parses its stock table, and buys each stocked item flagged
    // Models.GameData.ItemOverlay.AutoBuy up to its MaxToGet cap — one buy <name>
    // per unit, advancing off the live purchase / can't-afford result. Gated by
    // the Auto Get Items master toggle plus the per-item AutoBuy flag; LIGHT
    // items are excluded (Auto-light owns them).
    public Game.Inventory.AutoBuyManager AutoBuy { get; private set; } = null!;

    // Auto-sell engine. When a shop `list` readout surfaces, sells each carried
    // item flagged Models.GameData.ItemOverlay.AutoSell down to its keep floor at
    // the merchant standing in — one sell <name> per unit, advancing off the live
    // sold / can't-sell-here result. Gated by the Auto-Get Items auto-mode toggle
    // (master) plus the per-item ItemOverlay.AutoSell flag; a LoyalItem and LIGHT
    // items are never sold.
    public Game.Inventory.AutoSellManager AutoSell { get; private set; } = null!;

    // Auto-open engine. On every inventory change, each container item
    // (ItemType == Container) flagged Models.GameData.ItemOverlay.AutoOpen that
    // newly entered the pack is opened through ChestOpens, so what it gave joins
    // the Chest Offload list. Shares the AutoGetItems master toggle; the per-item
    // AutoOpen flag is the real gate.
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
    // when the item enters inventory. While such a need is outstanding and
    // the Auto-Search toggle is on, AutoSearch arms itself via
    // Game.Map.PathItemDemandTracker.SearchDemandActive.
    public Game.Map.PathItemDemandTracker PathItemDemand { get; private set; } = null!;

    // Reverse index of the active set's Shops.json — item id → the
    // shops that stock it. Feeds PathItemShopRouter's shop
    // lookup; rebuilt on GameDataCache.ActiveSetChanged.
    public ShopStockIndex ShopStock { get; private set; } = null!;

    // Active fulfiller for NeedKind.PathItem needs backed by a
    // shop: on a one-shot walk-to that needs an uncarried item a shop sells,
    // detours to the fewest-added-steps shop, buys it, and resumes. Gated per
    // item by the item record's AutoObtainForPath flag.
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

    // The givers of ItemSources resolved to a room and a command, split into free
    // hand-overs and trades. Every path-item give decision reads this.
    public PathItemGiveSources GiveSources { get; private set; } = null!;

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

    // Guaranteed-summon acquisition. When a walk needs a gate item no give or shop
    // supplies but a room command summons a monster that drops it outright, detours
    // there, types the command, waits out the fight, re-surveys the floor, and
    // resumes. The only router that can source a LOCKED DOOR's key — everything
    // else leaves a key gate to fail in place, because only a deterministic spawn +
    // guaranteed drop makes a key worth routing for. Gated per item by the same
    // AutoObtainForPath flag.
    public Game.Map.PathItemSummonRouter PathItemSummonRouter { get; private set; } = null!;

    // The final destination a path-item detour is currently holding aside — the
    // walk the user asked for while a give / shop / summon router temporarily
    // re-points the walker to fetch a gate item. Null when no detour is running.
    // Lets the nav map + Route Details draw the whole journey (current → detour
    // waypoint → this) instead of stopping the route line at the waypoint.
    public Game.Map.RoomKey? PathDetourOnwardDestination =>
        PathItemGiveRouter.OnwardDestination
        ?? PathItemShopRouter.OnwardDestination
        ?? PathItemSummonRouter.OnwardDestination;

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

    // The active set's room-entry spells classed by whether they teleport. Feeds
    // the Navigation map's by-teleport overlay; rebuilt on
    // GameDataCache.ActiveSetChanged.
    public RoomSpellTeleportIndex RoomSpellTeleports { get; private set; } = null!;

    // The active set's room-entry spells classed by whether they damage whoever
    // stands in the room. Feeds the rule that no rest is started in such a room;
    // rebuilt on GameDataCache.ActiveSetChanged.
    public RoomSpellDamageIndex RoomSpellDamage { get; private set; } = null!;

    // Active fulfiller for NeedKind.PathItem needs no shop can
    // satisfy: on a one-shot walk-to that needs an uncarried item no shop
    // sells, prompts to reroute to the nearest room a monster that drops it
    // spawns in, then resumes once it lands. Gated per item by the item
    // record's AutoObtainForPath flag, set in the item-edit dialog.
    public Game.Map.MonsterDropRouter MonsterDropRouter { get; private set; } = null!;
    // Drives the route picker's "take the shortcut" pick when the shortcut item isn't
    // held: walk to its source, let the room settle, grab the ground drop, then one
    // live-filter walk to the destination that self-selects shortcut vs long route.
    public Game.Map.ShortcutSourceCoordinator ShortcutSource { get; private set; } = null!;

    // Executes a picked Paradigm token route: use the transport token (a leader sends
    // the party across first and regroups them), then resume the walk from its
    // landing — deferring the use to the first monster-free room. Begun only by the
    // route picker's explicit token pick.
    public Game.Tokens.TokenRouteCoordinator TokenRoute { get; private set; } = null!;

    // On-demand party-inventory probe — broadcasts @have and aggregates
    // the party's replies into per-member counts. Feeds
    // PartyPathItemGate's give-from-surplus decision.
    public Game.Remote.PartyInventoryProbe PartyInventory { get; private set; } = null!;

    // Party-first stage of the path-item pipeline: on a walk-to that needs an
    // uncarried per-member Item/Ticket item, probes the party
    // (PartyInventory) and, if a member has a spare, arranges a
    // give instead of posting a need. Only a genuine shortfall falls
    // through to PathItemDemand. Gated per item by the item record's
    // AutoObtainForPath flag.
    public Game.Map.PartyPathItemGate PartyPathItemGate { get; private set; } = null!;

    // The gate items this leader handed to party members and the game confirmed,
    // so a member who never answers an @have count isn't fetched another copy on
    // every trip. In memory only, and gone when the party changes.
    public Game.Map.PartyHandOverMemory PartyHandOvers { get; private set; } = null!;

    // On-demand party-level probe — broadcasts @level and records
    // each member's exact level into Players. Fired by
    // PartyLevel on roster change so the players table stays
    // the authoritative level source (superseding the title-derived band).
    public Game.Remote.PartyLevelProbe PartyLevelProbe { get; private set; } = null!;

    // Keeps the party's level bounds warm for path planning and feeds
    // MovementFilter.PartyLevelBoundsProvider so BFS routes a
    // following party around (Level: MIN to MAX) gates a member
    // can't clear. Always on: a stranded member is never the wanted
    // behaviour.
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

    // Self-only bank-balance probe + passive parser. Parses the `bank` command's
    // per-bank "On deposit: N copper farthings" listing (a global account query —
    // it shows every bank we've used, from any room) so the route picker can weigh
    // a buy the purse can't cover against money on deposit. Bank name = shop name,
    // so a balance maps to its room(s) via BankCatalog for the withdraw detour.
    public Game.Remote.BankBalanceProbe BankBalance { get; private set; } = null!;

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

    // Stash dispatch for user-marked stash rooms
    // (CharacterProfile.StashRooms): one hide N <coin> per denomination at or
    // below CashSettings.StashCoinCutoff, then one hide <item> per carried item
    // flagged AutoStash. Fired as an auto-deposit step or when an active loop /
    // lair route passes through a stash room.
    public Game.Cash.StashRoomManager Stash { get; private set; } = null!;

    // Auto-deposit reroute. Subscribes to
    // Game.Cash.CashManager.AutoDepositRequested; when a
    // wealth / coin gate crosses while a loop or auto-lair is running,
    // detours to the configured bank / stash room, offloads the excess
    // coin (dep for a bank, Stash's hide for
    // a stash room), walks back, and restarts the captured engine.
    public Game.Cash.AutoDepositManager AutoDeposit { get; private set; } = null!;

    // The Default-gear max HP / pool the rest engine resolves against (recorded from a
    // `stat` screen with the Default set on).
    public Game.Health.DefaultPoolBaselineKeeper PoolBaseline { get; private set; } = null!;
    // Reads a copied profile's character (stat + inventory) on its first entry.
    public Game.ProfileStateVerifier StateVerifier { get; private set; } = null!;

    // Sell detours — an item flagged "Make detours to sell this item" turns a walk,
    // loop or Auto-Lair aside to a shop that trades it (see SellDetourManager).
    public Game.Inventory.SellDetourManager SellDetour { get; private set; } = null!;

    // Decides when a sell / deposit detour holds Auto-Combat off (outside the loop's rooms).
    public Game.Cash.DetourCombatHold DetourCombat { get; private set; } = null!;

    // Active set's MonsterOverlay seed — Defaults-tier baseline for
    // per-monster automation behavior (relationship / priority /
    // DontBackstab). Realm flavor is auto-picked from
    // the active set's Info.json[0].Legit; bundled seeds for
    // each realm ship at Defaults/MonsterOverlay.{realm}.seed.json
    // and bootstrap to the per-install Global/ copy at
    // startup. Consulted by Monsters-tab editing and, as the Defaults tier
    // under ResolveMonsterOverlay, by the combat engines
    // (MonsterOverlaySeedStore.GetOverlay(int)).
    public MonsterOverlaySeedStore MonsterOverlaySeed { get; private set; } = null!;

    // Active set's ItemOverlay seed — Defaults-tier baseline for
    // per-item automation behavior (9 Options flags + MinToKeep /
    // MaxToGet). Realm flavor is auto-picked from the active set's
    // Info.json[0].Legit; bundled seeds for each realm ship at
    // Defaults/ItemOverlay.{realm}.seed.json and bootstrap to
    // the per-install Global/ copy at startup. Consulted
    // by Items-tab editing and, as the Defaults tier under
    // ResolveItemOverlay, by the auto-collect / stash / discard / buy / sell
    // engines (ItemOverlaySeedStore.GetOverlay(int)).
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

    // How far the Stock map file's list of where the dead arrive goes for the
    // active game data: Paradigm by its Info row (Legit 2), an unmodified Stock
    // set by Legit 1 with Custom "Default", and anything else (a board whose
    // sysop may have edited its rooms, or no set) unverified.
    //
    // A board can run edited rooms and still be played on the standard data set.
    // Nothing in the set says so; deaths seen there that end where the list
    // doesn't allow do, and are counted on the board (StandardListContradictions).
    // A board marked by them is unverified too, whatever its set.
    public Game.Recovery.HangupArrivalListTrust HangupArrivalListTrust =>
        GameData.ActiveRealm == Game.RealmType.ParaMud ? Game.Recovery.HangupArrivalListTrust.Paradigm
        : GameData.ActiveSetIsVanillaStock && !StandardListContradictions.Marked
            ? Game.Recovery.HangupArrivalListTrust.VanillaStock
        : Game.Recovery.HangupArrivalListTrust.Unverified;

    // The active board's count of deaths the standard list of arrival rooms
    // doesn't allow, against the set in play.
    public Game.Recovery.StandardListContradictions StandardListContradictions { get; private set; } = null!;

    // Paradigm's Albion: the rooms that can be walked to from the Burial Vault,
    // where its dead arrive. The walk is made the first time a hang-up in one of
    // Paradigm's own rooms is judged and kept with the graph (Map.WalkReach).
    public bool InParadigmAlbion(Game.Map.RoomKey room) =>
        Game.Map.WalkReach.From(RoomGraph, Game.Recovery.HangupDeathArrivalRooms.ParadigmAlbionVault).Contains(room);

    // The teleport spots in the loaded game data, for the
    // setting that says which of them automatic walks may use. Worked out from the
    // room graph on first use and again after the graph reloads.
    public IReadOnlyList<Game.Map.TeleportChoice> TeleportChoices =>
        _teleportChoices ??= Game.Map.TeleportCatalog.Build(RoomGraph, (from, to) =>
            RoomGraph.GetRoom(from) is { Cmd: > 0 } room
                ? Game.Map.TBInfoTeleportResolver.Resolve(TBInfo, room.Cmd, to)
                : null);
    private IReadOnlyList<Game.Map.TeleportChoice>? _teleportChoices;

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

    // The names seen on the realm for rooms the game data gives none, kept in the
    // realm's folder and put back on the room map each time it is built. The
    // MainWindowViewModel name-learned prompt hands it a name the user keeps.
    public Game.Map.LearnedRoomNameStore LearnedRoomNames { get; private set; } = null!;

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

    // The character's `set follow` mode, read off the `pro` sheet and the command's
    // replies. In Blind mode a follow move prints no room for FollowMove's
    // prediction to be confirmed against.
    public Game.FollowModeTracker FollowModes { get; private set; } = null!;

    // Recognises a manually-typed spell cast-code on the wire and arms the
    // combat engine's between-round-cast resume, so a hand-cast that breaks
    // combat mid-fight re-attacks a still-alive target at once instead of
    // idling until the next round. Hooked from MainWindowViewModel.SendUserInput.
    public Game.Combat.OutboundCastObserver OutboundCast { get; private set; } = null!;

    // Any command that ends a sneak (GAME_MECHANICS "Sneaking — commands, equip
    // order, and the sneak state machine" → "What ends a sneak") marks it
    // broken so the next move re-sneaks — the engine's own sends through the send
    // gate, and hand-typed lines through SendUserInput (report
    // paradigm-20260928-163051: a typed `sea` left the client believing it still
    // sneaked, so sneak keeping held every buff for a quarter of an hour).
    //
    // The same commands end a hide, a few excepted. An item command (`use`, `read`,
    // `eat`, `drink`, `light`) ends either only when the item's spell is cast, which
    // _itemUseStealth reads off the pack and the game data.
    //
    // Each command is reported here once: a typed line by SendUserInput, the client's
    // own by the send gate.
    public void NoteSentForSneak(string command) =>
        Stealth.NoteCommandSent(command, CharacterHasShadowRest, _itemUseStealth);

    // A command of the room we stand in that casts a dispel takes a tracked hazard
    // buff with it. Reported from the same two places as the sneak note above, so
    // each command is judged once, typed or the client's own.
    public void NoteSentForRoomCommand(string command)
    {
        if (RoomTracker.State.CurrentRoom is { Cmd: > 0 } room)
            AutoHazardCounterProvisioner.OnRoomCommandSent(room, command);
    }

    private Game.Stealth.ItemUseStealthRule? _itemUseStealth;

    // Sniffs a hand-typed PHYSICAL attack verb so Combat treats it as a user override
    // (holds the auto attack until next round). Hooked from SendUserInput.
    public Game.Combat.OutboundAttackObserver OutboundAttack { get; private set; } = null!;

    // Sniffs a hand-typed gear command (eq / wear / wield / rem) so Combat re-attacks
    // on the *Combat Off* it draws mid-fight. Hooked from SendUserInput, typed lines only.
    public Game.Combat.OutboundGearObserver OutboundGear { get; private set; } = null!;

    // Sniffs the user's own `break`, which holds Combat's attack on that monster, and
    // the attack of theirs that lets it go. Hooked from SendUserInput, their lines only.
    public Game.Combat.OutboundBreakObserver OutboundBreak { get; private set; } = null!;

    // Classifies a cast-code as a combat spell (round energy 1–1000) vs an in-between
    // spell — drives whether a hand-typed cast is a user override or keeps the resume.
    public Game.Combat.CombatSpellIndex CombatSpells { get; private set; } = null!;

    // Death-message detector — watches lines for either post-death lives
    // readout (You now have N lives remaining. / You have N lives left.,
    // the latter the miracle-save death) and fires
    // Game.Map.RoomTracker.NoteDeath. Captures
    // a Models.Profile.DeathRecord on the loaded profile
    // for the Player Workshop's Death Recovery tab and pivots the tracker
    // into Game.Map.RoomConfidence.PendingRespawn.
    // Bound to the per-session LineExtractor by
    // MainWindowViewModel.
    public Game.DeathDetector Death { get; private set; } = null!;

    // "Sysop god lives" recovery — sends `sys god <name> add life` on the
    // character's own death when that per-BBS power is enabled.
    public Game.SysopGodLifeRecovery SysopGodLife { get; private set; } = null!;

    // "Sysop goto" — gates + fires `sys goto <name>` to a curated location and
    // re-anchors position on the landing. Enabled per-BBS (SysopGoto credential).
    public Game.SysopGotoManager SysopGoto { get; private set; } = null!;

    // BFS pathfinding + planar layout over the active
    // RoomGraph. Consumed by the walker, loop runner,
    // auto-lair scheduler (pathfinding), and the Navigation
    // MapControl (layout).
    public Game.Map.BfsMapper Bfs { get; private set; } = null!;

    // The destination of the most recent walk-to the user requested, remembered
    // past the walk's end so the bug report can re-plan and explain a route that
    // failed or was declined at the picker. Set by RouteChoicePrompt.WalkAsync;
    // null until the first walk-to this session.
    public Game.Map.RoomKey? LastRequestedWalkTo { get; set; }

    // Per-character avoided + stash room set. Implements
    // Game.Map.IRoomFilter so pathing layers can plug
    // it into Bfs without further wiring.
    public MovementFilter Movement { get; private set; } = null!;

    // Per-realm gang-house room labels for Roomba Mode (right-click map labeling +
    // the Player Workshop's Roomba tab read/write through this) — shared by every
    // character on the realm.
    public Game.Map.GhRoomLabelStore GhRoomLabels { get; private set; } = null!;

    // Which of the shared per-realm labels THIS character actively sweeps. Per-character
    // (so alts in different gang houses on one BBS each manage their own house);
    // labels stay per-realm above. See GhManagedRoomStore.
    public Game.Map.GhManagedRoomStore GhManagedRooms { get; private set; } = null!;

    // What the last Roomba sweep still had to do when it stopped. Persisted per
    // character so its load is delivered by the next sweep instead of being left
    // in the player's pack, and so Resume can skip the scan even after a restart.
    public Game.Map.GhSuspendedSweepStore GhSuspendedSweep { get; private set; } = null!;

    // Per-realm "last seen this item in this room" log, fed by GhSweep and read by
    // RoombaQuery's @roomba handler.
    public Game.Map.GhItemLocationStore GhItemLocations { get; private set; } = null!;

    // Per-character favourite-room bookmarks. Wires Navigation's
    // GOTO pane + the map's "Save as Go To" context menu;
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

    // Self-fear bridge — holds our navigation (FearGate) while we're afraid, so our
    // moves don't fight the fear's random running; RoomTracker follows its moves.
    public Game.Conditions.SelfFearMovementGate SelfFear { get; private set; } = null!;

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

    // The .mp import review's map comparison, drawn by the Navigation map.
    public MapComparisonOverlay MapComparison { get; } = new();

    // Walk-to engine — sends one move at a time, waits for the room
    // tracker to confirm before advancing, and honours
    // MovementCoordinator pause gates.
    public Game.Map.AutoWalkManager Walker { get; private set; } = null!;

    // Per-game-data-set saved-loop catalogue. CRUD over
    // game data/{set}/Loops/; reloads when the active set changes.
    public Game.Map.LoopManager Loops { get; private set; } = null!;
    public LoopFavoritesStore LoopFavorites { get; private set; } = null!;
    public LoopSettingsStore LoopSettings { get; private set; } = null!;

    // MegaMUD .mp loop-file importer. Stateless w.r.t. the
    // profile; takes the active RoomGraph at construct
    // time and resolves anchors against whatever it currently
    // contains.
    public Game.Map.MpFile.MpFileImporter MpImporter { get; private set; } = null!;

    // Per-set Auto-Lair setup catalogue, kept in the set's shared Loops
    // folder and reloaded with Loops on a set change. The Manage
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

    // The Simulator window's state for the bug report, registered by the Navigation
    // view-model that owns it; null until a Navigation window has opened.
    public Func<Game.Simulation.SimulatorSnapshot?>? SimulatorSnapshotProvider { get; set; }

    // The Simulator's finished area rankings, by the game data and character each was
    // made on. Here and not on the Simulator's view-model, which goes with the
    // Navigation window: a ranking is the pairing's for as long as the client runs.
    public Game.Simulation.AreaRankingStore SimulatorRankings { get; private set; } = null!;

    // Reads the file side of what a kept ranking is compared by (table stamps, the
    // seed, the user's record overrides), remembering content hashes between asks.
    private readonly RankingStampReader _rankingStamps = new();

    // The data of set was replaced by a Manage Sets change (its deletion, the
    // user's edits copied or moved into or out of it): what the Simulator ranked on
    // the old data is stale, whichever set is active. An import over a set is not
    // told here: the rankings' own table stamps say whether its files changed.
    public void ForgetSimulatorRankings(string set, string why) =>
        LogStaleSimulatorRankings(set, why, SimulatorRankings.ForgetSet(set));

    private void LogStaleSimulatorRankings(string? set, string why, int dropped)
    {
        if (dropped > 0)
            Log.Info("Simulator", $"game data '{set}' {why}: {dropped} area ranking(s) made on it dropped as stale");
    }

    // The Navigation window's mode and loop-builder state, for the bug report. Set by
    // the window's view model while it is open; cleared when it closes.
    public Func<string>? NavigationModeProvider { get; set; }

    // The walk queued in the Navigation window for Go, while the window is open: what
    // it holds as it reads in a notice, or null for none, and its drop. A change of
    // game data drops it with the other held starts: a later Go would walk its room
    // number on the other data (owner, 2026-10-16: "Clear it at the switch").
    public (Func<string?> Held, Action<string> Drop)? NavigationQueuedWalk { get; set; }

    // Sets a user-started run out in the chosen mode (Auto-Combat off, or Sprint Mode
    // on, for the trip there). The auto-engine toggles live on the main window's
    // view-model, which registers this; the Navigation window, the route picker and
    // Navigation Management call it right before the run starts.
    public Action<Game.Map.RunStartMode>? ApplyRunStartMode { get; set; }

    // The user stopped a walk-to / loop / Auto-Lair by hand (a Stop chip or the
    // toolbar Stop): a Run / Sprint start's turned-off autos come back or stay off
    // per Settings → Other. Registered by the main window's view-model.
    public Action? NoteUserStoppedRun { get; set; }

    // A remote @stop paused movement. Set by MainWindowViewModel, which resets the
    // auto toggles to the character's base modes as it does for the Stop button.
    public Action? NoteRemoteStop { get; set; }

    // A change of game data stopped or dropped a run (the reason, as it reads in a
    // log line). Set by MainWindowViewModel, which settles the auto toggles as it
    // does for the Stop button.
    public Action<string>? NoteGameDataStop { get; set; }

    // Folder CRUD over the active set's shared Loops directory that holds
    // both Loops and Lairs. Create / rename
    // / delete folders; reloads both catalogues after a filesystem
    // move so their in-memory Folder values stay in sync.
    public Game.Map.NavFolderManager NavFolders { get; private set; } = null!;

    // Follows what other clients on this set do to the shared Loops folder.
    public Game.Map.NavFolderWatcher NavWatcher { get; private set; } = null!;

    // Game Data → "Manage Game Data…" backend: copy / move a set's loop
    // library to another set, delete a set (tables + loops).
    public GameDataSetManager GameDataSetManager { get; private set; } = null!;

    // Sole writer of Game.PlayerState.Encumbrance.
    // Subscribes the enc line via MessageRouter.
    public Game.EncumbranceParser Encumbrance { get; private set; } = null!;

    // Debug instrumentation logging measured per-hop times tagged
    // with the current Game.EncumbranceLevel. Off by
    // default; flipped on by the Log pane's Hop timing toggle.
    public Game.HopTimingCalibrator HopCalibrator { get; private set; } = null!;

    // Per-realm room blacklist — hides target rooms from the
    // Navigation map render and the search box. Consumed by
    // Game.Map.BfsMapper (skip placement, keep edge
    // for dangling stub) and the right-click "Add to blacklist"
    // + "Modify Blacklist…" flows.
    public RoomBlacklistStore RoomBlacklist { get; private set; } = null!;

    // Per-realm captured "top N" leaderboard history, read by the Calculators tab's
    // XP/HR table. Grows communally — every character on the realm feeds and reads
    // the one shared list.
    public LeaderboardSnapshotStore Leaderboards { get; private set; } = null!;

    // Passive capture tracker that snapshots a "top N" listing off the live
    // terminal into Leaderboards. Bound to the per-session LineExtractor in
    // the MainWindowViewModel constructor.
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

    // The loop the user started from off it, waiting on its walk-to to arrive.
    public Game.Map.LoopWalkHandoff LoopHandoff { get; private set; } = null!;

    // Roomba Mode: sorts labeled gang-house rooms by building a Loop from
    // GhRoomLabels and driving it through LoopRunner — see GhSweepManager.
    public Game.Map.GhSweepManager GhSweep { get; private set; } = null!;


    // Construct and register the singleton. Idempotent — repeated calls return
    // the existing instance. Touches AppPaths to force
    // directory creation before any service tries to read or write a file.
    public static AppServices Initialize()
    {
        if (_current is not null) return _current;

        // Read any AppPaths member to fire its static constructor and create
        // the data tree on disk before anyone else needs it.
        _ = AppPaths.DataRoot;

        // Copy any missing seed files from the bundled Defaults/ next to
        // the exe into the user-writable Global/ location. Runs
        // once per launch; pre-existing Global seeds (user-edited or
        // user-curated) are never overwritten.
        AppPaths.EnsureGlobalSeedsBootstrapped();

        // Best-effort log rotation at the default retention window
        // (DebugLogWriter.DefaultRetentionDays).
        DebugLogWriter.PruneOldLogs();

        // One-shot migration: move legacy flat BBS/{name}.json files into
        // per-BBS folders. Runs BEFORE any store touches disk; idempotent on
        // already-migrated trees.
        LogService bootstrapLog = new(LogService.SessionCapacity);
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
        // Tee the program log to disk, gated on AutoCollectLogs: the writer only
        // opens once the toggle turns on (applied from the profile below or
        // flipped from the Log pane), so a normal session leaves no file.
        ProgramLog = new ProgramLogFile(Log, LogDiagnostics);
        // The always-written last minutes of the log, for a hang or a forced kill
        // (RecentLogFile). Older clients' pairs go first, so with this one's the
        // folder holds ProcessesKept of them.
        RecentLogFile.PruneOldProcesses(AppPaths.LogsDir, RecentLogFile.ProcessesKept - 1);
        string recentA = AppPaths.NewDebugLogFile(RecentLogFile.TopicA);
        string recentB = recentA[..^(RecentLogFile.TopicA.Length + ".log".Length)] + RecentLogFile.TopicB + ".log";
        RecentLog = new RecentLogFile(Log, LogDiagnostics, recentA, recentB, RecentLogHeader);
        Heartbeat = new ClientHeartbeat(Log, () => ClientHeartbeat.Describe(this));
        // Surface a one-time legacy data migration (pre-3.0 FujinTerm → MudPlay)
        // now that logging is up — AppPaths ran it at static-init.
        if (AppPaths.MigrationNote is { } migrationNote)
            Log.Info("Migration", migrationNote);
        // A BBS, realm, profile or set name that can't be used as a folder's is
        // refused wherever a path is made from one (PathGuard); each refusal is
        // said once, here, before any store that might meet one is built.
        PathGuard.Refused += line => Log.Warn("Paths", line);
        // Same gating for the memory-footprint sampler: the timer runs for the
        // whole process, but samples land on disk only while AutoCollectLogs is on.
        MemoryLog = new MemoryUsageLog(LogDiagnostics);
        Performance = new PerformanceMonitor(LogDiagnostics);
        // Self-update checker. Constructed early (only needs Log); the startup check
        // itself is kicked off at the end of construction, gated on the setting.
        Update = new Services.Update.UpdateService(Log);
        // Background memory hygiene. CombatTracker is bound later in construction;
        // the combat-active probe is lazy and the first periodic tick is minutes
        // out, so it's always assigned before the Func is ever invoked.
        Memory = new MemoryMaintenance(Log, GameData, () => CombatTracker.HasEngageableHostiles);
        // Late-bind the cache's log sink so SwitchSet emits the swap
        // audit entries (load / unload / swap) without coupling the
        // cache to AppServices construction order.
        GameData.Log = bootstrapLog;
        GameData.Performance = Performance;
        Settings = new SettingsService();
        // The Program Log switches are global: applied here, before any character,
        // so Auto-collect logs captures the session from launch.
        ApplyLogDiagnostics(Settings.Current.LogDiagnostics ?? new Models.Settings.LogDiagnosticsSettings());
        Profile = new ProfileService();
        // Same late-bind pattern as GameData.Log above: the profile-lifecycle
        // audit (load / swap / close / re-home) rides the always-on Info stream.
        Profile.Log = bootstrapLog;
        Profile.Performance = Performance;
        // Built ahead of every engine: each one's enable probe reads the master
        // switch, and a probe must never run before its target exists.
        AutoModeController = new Game.AutoModeController(Profile, Log);
        Bbs = new BbsProfileStore(() => Settings.Current.DefaultGameDataSet, bootstrapLog);
        Realms = new RealmCatalog(Bbs, Profile, bootstrapLog);

        // Startup head start: parse the big MDB tables for whatever profile "Auto-load
        // last profile" is about to bring in, on a background thread, before Profile.Load
        // below triggers the real (synchronous) GameData.SwitchSet — the same resolution
        // ApplyActiveGameDataSet does later, just computed early from the not-yet-loaded
        // startup profile's BBS pin. RoomGraphManager's set-switch rebuild is the biggest
        // single cost on a cold launch (a full Rooms.json parse + graph build over
        // thousands of rooms, done synchronously before the window even exists); this
        // just gets GameDataCache's tables read ahead of time so that
        // work finds the parse already done. A wrong guess (auto-load off, or the
        // predicted BBS/set doesn't match what actually loads) just wastes the
        // background parse — GetTable falls back to its normal on-demand read either
        // way, so this can't make startup any slower than it already is.
        if (Settings.Current.StartupProfile() is { } startupPrediction)
        {
            string? predictedRealm = JsonStore.Load<Models.Profile.CharacterProfile>(
                AppPaths.CharacterProfileFile(startupPrediction.Bbs, startupPrediction.Name))?.Realm;
            string? predictedSet = Bbs.Get(startupPrediction.Bbs)?.RealmFor(predictedRealm)?.ActiveGameDataSet
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
        // every subsequent global-settings save (Settings → BBS + Display's
        // confirm checkboxes write to Global through this path).
        ApplyConfirmFromGlobalSettings();
        Settings.GlobalSettingsChanged += _ => ApplyConfirmFromGlobalSettings();
        // The terminal's 16 base colours follow the Global setting the same way.
        ApplyTerminalColorsFromGlobalSettings();
        Settings.GlobalSettingsChanged += _ => ApplyTerminalColorsFromGlobalSettings();
        // Log already set by ctor parameter — bootstrap log carries the
        // DataMigration entries from before AppServices was constructed.
        Panels = new FloatingPanelHost();
        // Loopback control API. Constructed always, listening only while the
        // Global setting says so — and it follows that setting live, so toggling
        // it opens / closes the socket without a restart. The emulator is fetched
        // through a provider because the main view-model owns it and is built
        // after us; until it exists the transcript endpoints answer 503.
        LocalApi = new Api.LocalApiServer(this, Log, () => _emulatorProvider?.Invoke());
        // Constructed here so the property is never null, but NOT started here:
        // starting subscribes to MovementCoordinator, which this ctor doesn't
        // build until much further down. The initial start is at the end of the
        // ctor; this only arms the follow-the-setting behaviour.
        Settings.GlobalSettingsChanged += _ => ApplyLocalApiFromGlobalSettings();
        // Window snapping reads its master on/off live from the Global setting.
        WindowSnap = new WindowSnapManager(() => Settings.Current.SnapWindows);
        WindowLayouts = new WindowLayoutStore(Profile, WindowSnap);
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
        Chat = new Game.ChatRouter(Router, () => GameData.ActiveRealm == Game.RealmType.ParaMud, Log);
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
        Party = new Game.PartyManager(Router, PartyState, Log)
        {
            // Stock keeps a follower an exit turned away on the leader's member
            // list, where Paradigm drops them (GameData is read when asked).
            OnStock = () => GameData.ActiveRealm != Game.RealmType.ParaMud,
        };
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
        TickTiming = new Game.TickTimingLog(PlayerState, Regen);
        Tick.CombatTickElapsed += () =>
        {
            bool seen = Tick.LastCombatTickWasDamageDriven;
            TickTiming.NoteRound(seen);
            if (seen && Tick.LastCombatTick is { } at) Regen.NoteRound(at);
        };
        Tick.DamageOffTheRound += TickTiming.NoteDamageOffTheRound;
        // The game pays passive regen on a round boundary, so a gain keeps the round
        // grid true while no fight is printing damage lines. A meditate tick is
        // counted from the command on Stock and untimed on Paradigm: left out.
        Regen.MaTickObserved += sample =>
        {
            if (sample.Position != Game.PlayerPosition.Meditating)
                Tick.NoteGridTick(sample.Timestamp, authoritative: true);
        };
        Regen.HpTickObserved += sample =>
        {
            if (Regen.HpGainIsOnRoundGrid(sample.Position))
                Tick.NoteGridTick(sample.Timestamp, authoritative: false);
        };
        RegenDiagnostics = new Game.RegenDiagnosticsRecorder(Regen, PlayerState, Log,
            () => TickTiming.SinceLastSeenRound);
        Triggers = new TriggerEngine(Profile, Chat, Log, ProfileGameDataSet)
        {
            BlockedByMasterSwitch = MasterSwitchOff("Triggers"),
        };
        Aliases = new AliasEngine(Profile);
        Macros = new MacroStore(Profile);
        MacroDispatcher = new MacroDispatcher(Macros);
        Keybindings = new KeybindingStore(Profile);
        // PlayerDatabase: realm-tier observations + Char-tier customisations.
        // Wires its own subscriptions (ProfileLoaded / ProfileClosed /
        // BbsPinApplied / ProfileSaving) and reads the active realm's folder
        // through ActiveRealmFolder.
        Players = new PlayerDatabase(Profile, ActiveRealmFolder);
        Tick.HeartbeatElapsed += () => Players.TakeInOutsideChanges();
        // The settings files every client shares: global.json, and the game-data
        // edits made for all characters or for a realm.
        Tick.HeartbeatElapsed += () =>
        {
            Settings.TakeInOutsideChanges();
            Resolver.TakeInOutsideOverrideChanges();
        };
        // Board-specific disconnect line: PartyManager reads the active BBS's
        // custom DisconnectPattern live (empty on boards that use the standard
        // lines) and resolves a captured presence name — which on some boards is
        // the account name, not the character name — back to a given name via the
        // player account-name overrides.
        Party.DisconnectPatternProvider = ActiveDisconnectPattern;
        Party.PresenceNameResolver = Players.ResolveGivenNameFromPresenceName;
        // Same custom-disconnect source feeds the conversation window's realm
        // category — otherwise a board with a non-standard logoff line evicts the
        // roster member but never logs the disconnect in the conversation.
        Chat.DisconnectPatternProvider = ActiveDisconnectPattern;
        // Known-player gate for others'-POV actions/emotes: the actor of a room-local
        // social is a player in our room's entity list. Rejects room names, monsters
        // and ambient flavour that share the action-green colour.
        Chat.IsKnownPlayer = IsKnownRoomPlayer;
        // Engine only — other subsystems register additional
        // handlers without touching the engine.
        RemoteCommands = new Game.Remote.RemoteCommandManager(Chat, PartyState, Players, Log)
        {
            BlockedByMasterSwitch = what => AutoModeController.Blocks("Remote commands", what),
        };
        // Reserve the party ailment-sync announces (@poisoned / @blind / @held …)
        // so the engine swallows them instead of bouncing a "{command invalid}"
        // reply at the member who announced — PartyAilmentTracker consumes them on
        // its own ChatRouter subscription.
        foreach (string token in Game.Conditions.PartyAilmentTracker.AnnounceTokens)
            RemoteCommands.RegisterIgnored(token);
        // @panic rides the say channel as a party bail-out signal, not an
        // @-command — reserve it so the engine swallows it instead of bouncing a
        // "{command invalid}" reply. PanicResponder consumes it on its own
        // ChatRouter subscription (gated by PartySettings.IgnorePanics).
        RemoteCommands.RegisterIgnored("@panic");
        // Boss-timer sync responses ride the chat as `@timerdata …` lines the requester
        // scrapes itself (BossTimerSyncCollector); reserve the token so the engine
        // swallows it instead of bouncing "{command invalid}" at each responder.
        RemoteCommands.RegisterIgnored(Game.Remote.BossTimerQueryHandler.SyncResponseToken);
        RemoteCommands.RegisterIgnored(Game.Remote.RoombaQueryHandler.SyncResponseToken);
        // The two lines of a `@loop send` that are answers and never commands. They
        // travel in braces, which keeps them from the engine; reserved as well, so
        // that one typed by hand without its braces is swallowed like the others.
        RemoteCommands.RegisterIgnored(Game.Remote.LoopShareHandler.DataToken);
        RemoteCommands.RegisterIgnored(Game.Remote.LoopShareHandler.ResultToken);
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
        // Player Workshop's displayed alignment stale until the next `who`
        // re-observes our own row. Long-lived so the line is caught even when
        // the Workshop is closed.
        Alignment = new Game.AlignmentTracker(Router, PlayerStats, Players, Log);
        // Watches "<leader> is dragging you around." so a downed member's @join /
        // @invite reply can name who's already hauling it out.
        Dragged = new Game.DraggedTracker(Router, PlayerState);
        // First consumer; registers the party-essential handler set against the
        // engine. readCurrentRoom / readRoomEntities read RoomTracker /
        // RoomClassifier (constructed further down this constructor) through the
        // property on each call, so they always see the current snapshot across
        // set-switch rebuilds.
        PartyEssentials = new Game.Remote.PartyEssentialHandlers(
            RemoteCommands, PlayerState, PartyState,
            readPartySettings: () => ReadSection<Models.Profile.PartySettings>(Profile.Current, "Party"),
            readCurrentRoom: () => RoomTracker?.State.CurrentRoom,
            readRoomEntities: () => RoomClassifier?.Current?.Entities,
            readMovement: ReadMovementStatus,
            readDraggedBy: () => Dragged.DraggedBy,
            readAilments: () => Conditions?.ActiveFlags ?? Models.GameData.MessageFlags.None,
            // HealthManager is built further down this constructor; the lambda reads
            // it lazily so a @status after startup sees the live flee state.
            readFleeing: () => Health?.IsFleeing ?? false,
            readHoldingWait: () => PartyRest?.IsHoldingWait ?? false);
        // Drives the on-join @health exchange + the
        // periodic par poll. Wire-sender + cadence-from-settings hookup
        // happens in MainWindowViewModel.
        PartyPoller = new Game.PartyPoller(Chat, PartyState, Party, Log)
        {
            // par reads party health for the party heals, so it lives under the
            // auto-heal toggle like every other automatic action.
            // AutoModeController's kill-all zeroes that flag, so auto-all off
            // silences par too.
            IsParPollEnabled = () => ReadAutoModeFlag(d => d.AutoHeal),
        };
        // Emit side of @wait/@ok. Observes our own
        // position transitions and telepaths the leader when we enter
        // / leave a rest state. Wire-sender hookup in MainWindowVM.
        PartyRest = new Game.PartyRestSync(PartyState, Log);
        // One-to-many @-command sender: the @Reset on loop start (LoopRunner's
        // ReachedFirstWaypoint handler below) and the @have / @level / @wealth
        // party probes.
        PartyBroadcaster = new Game.Remote.PartyBroadcaster(PartyState);
        Telepaths = new Game.Remote.TelepathPacer(
            armTimer: (delay, action) => _ = System.Threading.Tasks.Task.Delay(delay)
                .ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(action),
                    System.Threading.Tasks.TaskScheduler.Default),
            log: Log);
        // A telepath the game would not send, or whose letters fit two players, is
        // not left pending in the chat log for a later confirmation to take.
        Telepaths.GaveUp += Chat.TelepathGivenUp;
        Chat.LastTelepathOnWire = () => Telepaths.LastWrittenTelepath;
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
        // Leader-side reconnect reform: a leader-drop dissolves the party and leaves the
        // followers sitting in the room, so re-invite each once AutoParty observes them
        // present (they never "re-enter the Realm" to trip the grace-window auto-invite).
        Party.LeaderReconnectReformInvites += AutoParty.NoteLeaderReconnectReform;
        // A Stock member an exit left behind is on our list because they joined:
        // an invite wait that runs out for them doesn't end in an uninvite.
        AutoParty.LeftBehindOnOurList = Party.IsLeftBehindOnOurList;
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
        // Stats itself is constructed above, ahead of this hookup.
        RemoteCommands.LivesProvider = () => Stats.HasParsed ? PlayerStats.Lives : (int?)null;
        // SelfNameProvider — lets the engine recognise its own gangpath echo (public
        // channels tag the sender's real name, not "You") and skip it instead of bouncing
        // a denial at the gang. Party.LocalCharacterName tracks PlayerStats.Name, falling
        // back to the profile name.
        RemoteCommands.SelfNameProvider = () => Party.LocalCharacterName;
        RemoteCommands.StealthedProvider = () => Stealth?.IsStealthed == true;

        // Rebuild the Spell Book's available list from a class+level
        // snapshot. Unknown / null class resolves to 0 (no class), which
        // yields an empty book — correct for non-magery classes and the
        // no-profile case alike. The obtained set is restored separately in
        // the ProfileLoaded handler below (after this seeds the class list),
        // so the learned checkmarks survive across sessions.
        //
        // Alignment comes from the SAME source as the Equipment Manager's own
        // GoodOnly/EvilOnly gating (CanCharacterEquipItem / IsEquipRestricted
        // above) — AlignmentTracker.SelfAlignment, our own row in the realm's
        // players list (a `who` rewrites it), not the stat screen (which doesn't
        // report alignment at all). Unknown until a `who` has shown us, in which case
        // charAlign stays 0 and IsUsable skips alignment filtering entirely
        // rather than guessing.
        void SeedSpellbook(Models.Profile.LastKnownStats? snap, bool reseed = false)
        {
            int classNumber = snap is null ? 0 : SpellCatalog.ResolveClassNumber(snap.Class) ?? 0;
            int level = snap?.Level ?? 0;
            Game.RealmType realm = GameData.ActiveRealm;
            Game.Calculators.AlignmentBucket? alignment = Game.Inventory.ItemEquipFilter.GearBucketForWord(
                Alignment.SelfAlignment, realm);
            int charAlign = Game.Spells.KnownSpellCatalog.CharAlignFor(alignment, Alignment.SelfEvilPoints(realm));
            // reseed = the active game-data set changed under us: force a rebuild
            // even when the class number is unchanged, since the Spells table
            // itself was replaced. Refresh alone skips the rebuild on an
            // unchanged class number and would leave Available stale.
            if (reseed) Spellbook.Reseed(classNumber, level, charAlign);
            else Spellbook.Refresh(classNumber, level, charAlign);
        }

        // A game-data set swap replaces the Spells / Classes tables under the
        // live character. Re-resolve the class number from the persisted class
        // NAME (a set may renumber classes) and reseed the Spell Book so
        // Available and the learned checkmarks re-resolve against the new set
        // instead of blanking. The obtained set is name-backed, so it survives
        // the renumber — no need to re-apply the profile's persisted names here.
        GameData.ActiveSetChanged += _ => SeedSpellbook(Profile.Current?.LastKnownStats, reseed: true);

        // Nav-seed additive apply on set-activate: starter loops / GOTO favourites
        // added in a LATER build reach an already-imported set on launch, never
        // re-adding ones the user deleted (per-set ledger in NavSeedBootstrapper).
        // Off the UI thread — it enumerates the bundle + set folder — and a no-op
        // (nothing new) touches no disk. Import still seeds a fresh set synchronously.
        GameData.ActiveSetChanged += _ =>
        {
            string? set = GameData.ActiveSet;
            if (!string.IsNullOrWhiteSpace(set))
                System.Threading.Tasks.Task.Run(() => NavSeedBootstrapper.SeedIfNeeded(set, Log));
        };

        // Persist the learned-spell set with the rest of the profile. Snapshot
        // only when the book has a resolved class — with no class the obtained
        // set is empty for lack of a spell list, and blindly writing that would
        // wipe a previously-persisted set we simply can't re-resolve right now.
        Profile.ProfileSaving += p =>
        {
            if (Spellbook.ClassNumber < 1) return;
            IReadOnlyList<string> learned = Spellbook.ObtainedNames;
            // Only persist when we actually have names. Never overwrite a populated
            // saved set with null just because the live obtained set is transiently
            // empty — the immediate save that runs right after a profile-schema
            // migration fires before the game-data set is active and the first
            // `spells` poll, so ObtainedNames is momentarily empty; the old code
            // wrote null there and wiped the learned set on upgrade (report
            // paradigm-20260820-055007). A genuine reroll-to-zero clears via its own
            // explicit path, not this passive save.
            if (learned.Count > 0) p.LearnedSpells = new List<string>(learned);
        };

        // Persist stat captures onto the loaded profile so the next
        // session starts hydrated with the last-observed values
        // (Save-on-close at MainWindow.Closing flushes the in-memory
        // profile to disk). Drafts (no name) are still snapshotted —
        // ProfileService.Save no-ops on them, so the data just lives
        // for the rest of the session.
        Stats.ScreenParsed += snapshot =>
        {
            if (Profile.Current is { } p)
            {
                p.LastKnownStats = snapshot;
                // Persist immediately so the next profile load hydrates these
                // stats into PlayerStats (and the Player Workshop reads them)
                // — without this the snapshot lived only in memory and was lost
                // on reload, leaving the Workshop blank. No-op on unnamed drafts.
                Profile.Save();
            }
            // The status line carries only current HP / MA, so PromptParser
            // learns the maxima as a high-water mark that reads low until the
            // character is seen at full. The stat screen reports the true
            // ceilings — snap PlayerState.MaxHp/MaxMa to them (routed through
            // PromptParser to keep it the sole writer of the max fields) — but only the
            // ones this screen actually showed: an `exp` screen's snapshot still carries
            // the last `stat`'s maxima, read under whatever gear was worn then.
            Player.ApplyStatScreenMax(
                Stats.LastCaptureReadHits ? snapshot.MaxHits : 0,
                Stats.LastCaptureReadPool ? snapshot.MaxMana : 0);
            NotePoolType(snapshot.MaxMana, snapshot.MaxKai);
            // A full `stat` with the Default set on records the rest engine's basis.
            if (Stats.LastCaptureReadHits && Stats.LastCaptureReadPool)
            {
                PoolBaseline.OnStatScreen(snapshot.MaxHits,
                    snapshot.MaxMana > 0 ? snapshot.MaxMana : snapshot.MaxKai);
                StateVerifier.OnStatScreen();
            }
            SeedSpellbook(snapshot);
            ReadSpellListIfNeverSeen();
        };
        // Alignment doesn't come from `stat` (see SeedSpellbook above) — it's only
        // ever refreshed by a `who` re-observing our own row. Without this, a
        // character whose alignment wasn't yet known at the last `stat` stays
        // unfiltered (GoodOnly/EvilOnly spells both visible) until the NEXT `stat`
        // happens to run after a `who`. Mirrors AlignmentTracker's own self-row
        // match (same PlayerObservation.SplitName + given-name comparison).
        Players.ObservationRecorded += givenName =>
        {
            (string self, _) = Models.GameData.PlayerObservation.SplitName(PlayerStats.Name);
            if (!string.IsNullOrEmpty(self)
                && string.Equals(self, givenName, StringComparison.OrdinalIgnoreCase))
                SeedSpellbook(Profile.Current?.LastKnownStats);
        };
        // A `pro` reading or a refused evil-only item moves our evil points, which
        // decides evil-only spells.
        Alignment.Refreshed += () => SeedSpellbook(Profile.Current?.LastKnownStats);
        // The compact `health` command (Reset States, or a manual `health`) re-anchors
        // the HP + power-pool ceilings without the full stat-screen scroll. Snap
        // PlayerState.MaxHp/MaxMa to them — through PromptParser, the sole max-field
        // writer — and persist the refreshed snapshot so the next session hydrates the
        // corrected ceilings. poolMax is whichever pool the class carries (mana or kai),
        // so it re-latches a kai ceiling the stat-screen path (which passes only MaxMana)
        // never could. It carries no class/level, so no spellbook reseed.
        Stats.HealthReanchored += (maxHits, poolMax) =>
        {
            Player.ApplyStatScreenMax(maxHits, poolMax);
            if (Profile.Current is { } p)
            {
                p.LastKnownStats = Stats.Snapshot();
                Profile.Save();
            }
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
            // Wipe the OUTGOING character's live status state before hydrating
            // the incoming one. Otherwise the previous character's current HP
            // lingers in PlayerState while the new character's MaxHp is
            // re-seeded below (ApplyStatScreenMax), and any HP-driven engine —
            // notably the low-HP emergency hangup — acts on that mismatched body
            // the instant the swap runs (paradigm-20260909-172633: Fujin's 66 HP
            // measured against FujinPVP's 324 max fired a hangup while already
            // disconnected). Also drop any pending hangup intent: it belonged to
            // the character that hung up, and a stale suppress-entry flag would
            // otherwise make the next connect skip realm auto-entry for THIS,
            // different, character.
            Player.ResetForProfileSwap();
            if (HangupSignal.Reset())
                Log.Info("HangupSignal", "Cleared stale hangup intent on profile swap.");
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
            NotePoolType(p.LastKnownStats?.MaxMana ?? 0, p.LastKnownStats?.MaxKai ?? 0);
            SeedSpellbook(p.LastKnownStats);
            // Restore the learned checkmarks. Seed the names AUTHORITATIVELY (not
            // resolve-and-drop): profile load can run before the game-data set is
            // active (Available still empty), where SetObtainedByNames would drop
            // everything and the ensuing migration save would persist the wipe
            // (report paradigm-20260820-055007). The numbers re-derive on the
            // ActiveSetChanged reseed once Available is built.
            if (learned is not null) Spellbook.SeedObtainedNames(learned);
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
        Hangup.SetHangupPenaltyLog(() => LogHangupPenalty(pvpResponse: false));
        // @relog handler — graceful exit (GameCommands.ExitCommand) +
        // RelogSignal so MainWindowVM forces an unconditional reconnect
        // and the normal login automation logs the character back in.
        Relog = new Game.Remote.RelogHandler(RemoteCommands, GameCommands, RelogSignal);
        Relog.SetHangupPenaltyLog(() => LogHangupPenalty(pvpResponse: false));
        // @divert handler — subscribes to ChatRouter telepaths and repeats
        // them to a target while diverting. Wire-sender bound in
        // MainWindowVM after the telnet client is up.
        Divert = new Game.Remote.DivertHandler(RemoteCommands, Chat);
        // @dupe — copies the sender's permission grid onto a known player. Refuses
        // the local character, so it reads the live self-name like the engine's
        // own self-echo guard does.
        Dupe = new Game.Remote.DupeHandler(RemoteCommands, Players, () => Party.LocalCharacterName, Log);
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
        // The remote handler shares AutoModeController with the Auto-All button,
        // so button + telepath drive one master switch. ResetSnapshot on load so
        // a freshly loaded character doesn't inherit the previous one's switch.
        // A loop held to start by itself is dropped for another character ahead of
        // that reset, which lets go of a restart held for the switch without a word.
        Game.Map.HeldLoopsOnProfileSwap heldLoopsOnSwap = new(
            () => MovementControl,
            () => Profile.Current is null ? null : $"{Profile.CurrentBbsName}/{Profile.CurrentProfileName}",
            notice => Avalonia.Threading.Dispatcher.UIThread.Post(() => WriteTerminalNotice(notice)),
            action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        Profile.ProfileLoaded += _ => heldLoopsOnSwap.OnProfileLoaded();
        Profile.ProfileClosed += heldLoopsOnSwap.OnProfileClosed;
        Profile.ProfileRenamed += (_, _) => heldLoopsOnSwap.OnProfileRenamed();
        Profile.ProfileLoaded += _ => AutoModeController.ResetSnapshot();
        // A `par` asked for to check who is in the party isn't a health read, so
        // the auto-heal toggle doesn't hold it. The master switch does.
        PartyPoller.IsAutomationEnabled = () => !AutoModeController.KillSwitchEngaged;
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
            RemoteCommands,
            // A player named by @kill is a fight with a player, or a refusal when
            // we won't fight them; anything else is the combat engine's.
            name => PvpFight.TakeKillOrder(name, Combat.RetargetTo),
            Log);
        // @trap auto-disarm flow — manager owns the state machine,
        // handler owns the @-command auth boundary. Wire-sender +
        // OtherSettings cadence knobs bind in MainWindowVM /
        // ApplyOtherFromActiveProfile.
        // UI-thread one-shot, same as the door FSM's response watchdog below.
        Func<TimeSpan, Action, IDisposable> trapDelay = (delay, callback) =>
        {
            var timer = new Avalonia.Threading.DispatcherTimer { Interval = delay };
            timer.Tick += (_, _) => { timer.Stop(); callback(); };
            timer.Start();
            return new DispatcherTimerHandle(timer);
        };
        TrapDisarm = new Game.TrapDisarmManager(Router, PlayerStats, GameData, Log, scheduleDelay: trapDelay);
        TrapDelegation = new Game.TrapDelegationManager(Party, Players, GameData, Router, Log, trapDelay);
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
            maxPickAttemptsProvider:       () => Resolver.Resolve<Models.Profile.OtherSettings>("Other").MaxPickAttempts,
            picklocksOverBashProvider:     () => Resolver.Resolve<Models.Profile.OtherSettings>("Other").PicklocksOverBash,
            itemNameLookup:                id => ItemNames.GetName(id),
            maxBashableStrengthProvider:   () => MaxStrength.MaxAchievableStrength,
            statsRead:                     () => Stats.HasParsed,
            // Read lazily at door-open time — Inventory is constructed after Door.
            holdsKeyItem:                  HoldsKeyItem,
            // Rest-interleave for bashing (bashing drains HP): pause a bash once HP
            // falls to the Health-tab rest-if-below trigger, resume once it climbs
            // back to rest-max. HealthManager owns the actual rest/stand cycle — the
            // door FSM only gates its swings on these. Reuses PoolThreshold so the
            // percentage/absolute mode matches the rest engine exactly.
            bashRestNeeded:                () => BashRestGate(recovered: false),
            bashRestRecovered:             () => BashRestGate(recovered: true),
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
        // Resume a bash rest-pause the moment live HP climbs back to rest-max,
        // rather than waiting on the door FSM's periodic watchdog re-check.
        PlayerState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Game.PlayerState.Hp)) Door.NotifyHealthChanged();
        };
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
        Suicide = new Game.Remote.SuicideHandler(RemoteCommands, Router, Profile, Passwords, PromptScanner, Log)
        {
            SendGate = EngineGate,
            Telepaths = Telepaths,
        };
        // Main-menu entry automation — armed by MainWindowVM when
        // LoginAutomator.LoggedIntoGame fires; observes the
        // MainMenuEnterRealm pattern and sends GameCommands.EntryCommand
        // exactly once per arm, followed by the post-entry refresh
        // sequence (CR + stat + exp + i) to seed PlayerStats. Closed
        // by default so in-game chat matching the menu pattern can
        // never trick it; ALSO skips on the first connect after a
        // hangup (HangupSignal.ConsumeSuppressEntry) so the user can
        // read the screen before they decide to act.
        // Auto-entry is the last step of logging in, and login automation works
        // whatever the master switch says (user, 2026-10-09), so it is not given
        // the switch as its enable probe: a reconnect made under the BBS's
        // reconnect settings ends in the realm, as a first login does.
        MainMenuEntry = new Game.MainMenuEntryAutomation(
            Router, GameCommands, HangupSignal,
            log: Log);
        // Cleanup-driven proactive log-off. Subscribes to the same
        // CleanupWarningWatcher the reconnect scheduler reads; its safe
        // predicate + connection check + disconnect callback are wired by
        // MainWindowViewModel (they depend on VM-level connection state).
        CleanupLogout = new Game.CleanupLogoutOrchestrator(Cleanup, Router, Log);
        CleanupLogout.SetMasterSwitchCheck(MasterSwitchOff("Cleanup log-off"));
        PromptScanner.RealmLeftPromptObserved += () => CleanupLogout.NoteRealmLeftPrompt();

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
            if (e.PropertyName != nameof(Game.PlayerStats.Name) || string.IsNullOrWhiteSpace(PlayerStats.Name))
                return;
            Party.LocalCharacterName = PlayerStats.Name;
            // Heal a stale CharacterProfile.Name from the authoritative stat screen.
            // Name is defined as the in-game character name (distinct from the profile
            // FILE label, CurrentProfileName) and is otherwise only written on
            // create/rename — so a profile COPIED from another character keeps the old
            // name and every self-identity consumer of Current.Name mis-identifies self
            // (report stock-20260828-104653: copied Fujin → renamed to Raijin, but
            // Current.Name stayed "Fujin", hiding the real Fujin from player records).
            // Healing it here fixes them all centrally the moment the user sees `stat`.
            // Store the FULL "Given Family" name; HealedCharacterName returns null when
            // it already matches, so there's no per-screen Save churn. Touches no
            // filename or BBS folder — that's CurrentProfileName.
            if (Profile.Current is { } cur
                && ProfileService.HealedCharacterName(cur.Name, PlayerStats.Name) is { } healed)
            {
                Log.Info("Profile",
                    $"healing profile character name '{cur.Name}' → '{healed}' from stat screen");
                cur.Name = healed;
                Profile.Save();
            }
        };
        Profile.ProfileClosed += () => Panels.ApplyLayouts(layouts: null);
        Profile.ProfileSaving += p => p.PanelLayouts = Panels.SnapshotLayouts();

        // Bridge: keep the live DisplayConfig in sync with the active BBS.
        // Font size + scrollback are BBS-tier (different BBSes warrant
        // different legibility tuning) so we re-resolve on every profile
        // load AND on every ProfileMutated tick (which fires from the BBS
        // section's Apply path after a save).
        Profile.ProfileLoaded += _ => ApplyDisplayFromActiveBbs();
        // A realm assignment / realm edit re-pins: the game-menu commands are the realm's.
        Profile.BbsPinApplied += _ => ApplyDisplayFromActiveBbs();
        Profile.ProfileClosed += ResetDisplayToDefaults;
        Profile.ProfileMutated += _ => ApplyDisplayFromActiveBbs();

        // Bridge: compile the prompt scanner's regex from the active
        // character's statline command (Char-tier). The same string is sent
        // to the BBS via `set statline`, so building the parser from it keeps
        // them in lockstep. Re-hydrates on load AND on every ProfileMutated
        // tick (the Statline section's Apply path fires one after a save);
        // profile close drops back to the permissive class-default pattern.
        Profile.ProfileLoaded += _ => ApplyStatlineRegex();
        Profile.ProfileClosed += () =>
        {
            PromptScanner.ResetRegexToDefault();
            CurrentStatlinePromptRegex = Game.StatlinePromptRegexBuilder.Default;
        };
        Profile.ProfileMutated += _ => ApplyStatlineRegex();

        // Bridge: keep the live ToolbarConfig in sync with the loaded
        // character profile (Char-tier — each character can have its own
        // toolbar layout). Re-hydrates on every profile load AND on every
        // ProfileMutated tick (which fires from the Settings → Toolbar + Shortcuts
        // Apply path).
        Profile.ProfileLoaded += _ => ApplyToolbarFromActiveProfile();
        Profile.ProfileClosed += ResetToolbarToDefaults;
        Profile.ProfileMutated += _ => ApplyToolbarFromActiveProfile();

        // Same bridge for the customizable terminal right-click menu (Char-tier).
        Profile.ProfileLoaded += _ => ApplyContextMenuFromActiveProfile();
        Profile.ProfileClosed += ResetContextMenuToDefaults;
        Profile.ProfileMutated += _ => ApplyContextMenuFromActiveProfile();

        // Log-diagnostic toggles are Global: a character's old Char-tier section
        // is adopted once on load when Global has none, and every Log-pane flip
        // persists straight to Global (the LogPane is the only editor — no
        // Settings-tab Apply path, so we persist on Changed directly).
        Profile.ProfileLoaded += _ => AdoptCharacterLogDiagnostics();
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
        // Dump the configured buff plan on load / edit so a "buffs aren't working"
        // report shows exactly how they're set up.
        Profile.ProfileLoaded  += LogBuffConfiguration;
        Profile.ProfileMutated += LogBuffConfiguration;
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

        // Bridge: follow the active realm's preferred game-data set (every
        // character on the realm shares the same MDB). Resolution chain:
        //   the realm's ActiveGameDataSet
        //     → GlobalSettings.DefaultGameDataSet
        //       → null (no set active).
        // Re-resolve on every signal that could change the answer:
        // a fresh profile load, an explicit BBS pin from Settings →
        // BBS Apply, a re-pin via ProfileMutated, and profile close.
        Profile.ProfileLoaded  += _ => ApplyActiveGameDataSet("a profile was loaded");
        Profile.BbsPinApplied  += _ => ApplyActiveGameDataSet();
        Profile.ProfileMutated += _ => ApplyActiveGameDataSet();
        Profile.ProfileClosed  += () => ApplyActiveGameDataSet("the profile was closed");

        // Messages catalogue is paired per game-data set on disk
        // (game data/{set}/messages.json, a delta over the realm seed) —
        // reload whenever the active set changes so the Browser tab and
        // runtime engines see the right realm's catalogue.
        Messages = new MessageStore(Log);
        Messages.Messages.CollectionChanged += (_, _) => _spellMessages = null;
        GameData.ActiveSetChanged += Messages.Load;
        GameData.ActiveSetChanged += _ => CheckActiveSetForImportDamage();
        // The apply-cast matcher list + spell-formula cache (PartyAilmentTracker's
        // witness-SET + duration clear) are derived from Messages + the Spells
        // table, so drop them when either changes: a set switch (reseeds both) or a
        // message edit in the Game Data Browser. Rebuilt lazily on next use.
        _cureSpells = new Game.GameData.CureSpellIndex(GameData, DiseaseApplySpellNumbers);
        GameData.ActiveSetChanged += _ => { _applyMatchers = null; _spellFormulas = null; _cureSpells.Invalidate(); _cureMatchers = null; };
        Messages.Messages.CollectionChanged += (_, _) => { _applyMatchers = null; _cureSpells.Invalidate(); _cureMatchers = null; };
        // Monster-message catalogue parallels the spell-message one —
        // same per-set storage + universal seed fallback pattern.
        MonsterMessages = new MonsterMessageStore(Log);
        GameData.ActiveSetChanged += MonsterMessages.Load;
        // Staged candidates are what the characters on a realm saw there, so they
        // load per realm and take in what another client on it recorded.
        MessageCandidates = new MessageCandidateStore(Log);
        Profile.ProfileLoaded += _ => MessageCandidates.Load(ActiveRealmFolder(), ProfileGameDataSet());
        Profile.BbsPinApplied += _ => MessageCandidates.Load(ActiveRealmFolder(), ProfileGameDataSet());
        Profile.ProfileClosed += () => MessageCandidates.Load(ActiveRealmFolder(), ProfileGameDataSet());
        MessageCandidates.Load(ActiveRealmFolder(), ProfileGameDataSet());
        Tick.HeartbeatElapsed += () => MessageCandidates.TakeInOutsideChanges();
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
        // graph reads the typed store, so evicting the TBInfo table here is fine.
        TBInfo = new TBInfoStore(GameData, Log);
        // MonsterSpawns — reverse RoomKey -> monster-ids index for the room
        // tooltip's Also Here line. Same lazy-but-warmed-in-the-background shape
        // as ItemSourceIndex below: a first build walks every Monsters.json row
        // and self-invalidates by comparing the cache's ActiveLoad to the load it
        // last built from, so there's no explicit invalidation subscription —
        // only the warm trigger below, so a query never pays the build cost on
        // the UI thread at an inconvenient moment (a room hover mid-walk).
        MonsterSpawns = new MonsterSpawnIndex(GameData, Log);
        GameData.ActiveSetChanged += _ => Task.Run(MonsterSpawns.Warm);
        if (GameData.ActiveSet is not null) Task.Run(MonsterSpawns.Warm);
        GameData.ActiveSetChanged += TimedSetIndex("textblocks", TBInfo.OnActiveSetChanged);
        if (GameData.ActiveSet is not null)
            TBInfo.OnActiveSetChanged(GameData.ActiveSet);

        // ItemSourceIndex — reverse item-acquisition (containers, textblock
        // giveitem awards, guaranteed summon drops), read by the Game Data browser
        // and by the path-item acquisition routers. Reads TBInfo's typed entries, so
        // it's constructed after the store above.
        //
        // Still lazy and self-invalidating, but warmed in the background on a set
        // change: the first build is ~600 ms (it walks every Rooms and Monsters row),
        // and left purely lazy that lands on whichever walk first crosses a gate —
        // a stall exactly when the user is watching the character move. The warm
        // builds into locals and publishes by reference, and GameDataCache guards its
        // own tables, so it's safe off-thread. A query that beats the warm simply
        // builds it itself.
        //
        // The warm is queued where the room graph is built, further down, on its
        // GraphReloaded: after TBInfo above has reloaded, and after the learned
        // room names are on the map, since a giver in a room the data leaves
        // unnamed is labelled with the name learned for it. Queued at the set
        // switch itself, the build read the map before those names went on and was
        // done a second time.
        ItemSources = new ItemSourceIndex(GameData, TBInfo, Log);

        // RoomFloorItemIndex — the room→floor-item (`roomitem`) mapping for the
        // Navigation Room Info panel. Reads TBInfo's typed entries like ItemSources;
        // self-invalidating, so no ActiveSetChanged subscription, and warmed with the
        // game-data lists once the room graph has loaded (WarmGameDataLists).
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
        // The realm's learned room names go on the map before anything else hears
        // of a rebuilt graph: this must stay the first GraphReloaded subscription,
        // so every later listener (the list warm-up below, the tracker, the map
        // layout, the maze index, room search) is handed rooms already named. The
        // profile handlers sit here too, ahead of the tracker's, so a loaded
        // character's last room is looked up under the name its realm has for it.
        //
        // The lists that label rooms from the Rooms table (item, spell and quest
        // records, the trainer and bank lists, item givers) ask the map for a room
        // the table leaves unnamed, and the ones that keep the answer are told when
        // a realm's names go on or come off, and once a walk that learned names goes
        // quiet (RoomGraph.TellKeptListsWhenQuiet, on the heartbeat). Both are set
        // before the store is built, so its first apply already reaches them;
        // ItemSources exists above.
        GameData.RoomNameWhenDataHasNone = (map, room) =>
            RoomGraph.LearnedNames.GetValueOrDefault(new Game.Map.RoomKey(map, room));
        RoomGraph.LearnedNamesChanged += () =>
        {
            GameData.LearnedRoomNamesChanged(
                Game.GameData.TrainerCatalog.DerivedKey, Game.GameData.BankCatalog.DerivedKey);
            // Rebuilt off the UI thread, as at a set switch, so the next walk that
            // asks who gives an item doesn't pay for it.
            Task.Run(ItemSources.Warm);
        };
        LearnedRoomNames = new Game.Map.LearnedRoomNameStore(RoomGraph, ActiveRealmFolder, Log);
        RoomGraph.GraphReloaded += LearnedRoomNames.OnGraphReloaded;
        // After the store's, so the names are on the map: one build per set load.
        // (A second warm queued by LearnedNamesChanged finds the first's result.)
        RoomGraph.GraphReloaded += () => Task.Run(ItemSources.Warm);
        Profile.ProfileLoaded += _ => LearnedRoomNames.OnRealmChanged();
        Profile.BbsPinApplied += _ => LearnedRoomNames.OnRealmChanged();
        Profile.ProfileClosed += LearnedRoomNames.OnRealmChanged;
        // A BBS or realm renamed: the realm's folder moved under the new name, and
        // the store goes with it (a BBS renamed from Profile Management raises no
        // pin at all).
        Profile.BbsRenamed += (from, to) =>
            LearnedRoomNames.OnFolderRenamed(AppPaths.BbsFolder(from), AppPaths.BbsFolder(to));
        Profile.RealmRenamed += (bbs, from, to) =>
            LearnedRoomNames.OnFolderRenamed(AppPaths.RealmFolder(bbs, from), AppPaths.RealmFolder(bbs, to));
        LearnedRoomNames.OnRealmChanged();
        Tick.HeartbeatElapsed += () => LearnedRoomNames.TakeInOutsideChanges();
        Tick.HeartbeatElapsed += RoomGraph.TellKeptListsWhenQuiet;
        // Once a set's graph is loaded, build the lists Navigation, Settings and the
        // map's right-click menu show, off the UI thread, so the first open doesn't
        // pay for them (the trainer list alone re-read the 22 MB Rooms table).
        RoomGraph.GraphReloaded += () => Task.Run(WarmGameDataLists);
        // The Workshop's Quest tab needs every quest and its steps for the character's
        // class, and is built with the window: crawled here, off the UI thread, once
        // the class is known, the first open doesn't pay for it.
        Profile.ProfileLoaded += profile =>
        {
            string? className = profile.LastKnownStats?.Class;
            Task.Run(() => WarmQuestData(className));
        };
        // Game data read in afresh stops whatever is walking by the old data's room
        // numbers (Game.Map.StopOnGameDataChange decides which changes those are, and
        // makes the stop). Subscribed ahead of the room graph's own handler below,
        // so the stop comes before the graph is rebuilt and nothing re-plans those
        // numbers on the new rooms first. The movement controller is built further
        // down: before it exists nothing is running.
        Game.Map.StopOnGameDataChange gameDataStop = new(
            () => GameData.ActiveLoad, () => MovementControl, ForgetRouteItemsOfOtherGameData);
        GameData.ActiveSetChanged += _ =>
        {
            if (gameDataStop.OnActiveSetChanged(_gameDataSwitchCause) is not { } stop) return;
            // Posted: a set change can come from inside the line pump, where writing
            // to the terminal re-enters it.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => WriteTerminalNotice(stop.Notice));
            // The client is left as the Stop button leaves it: the autos a Run or
            // Sprint start turned off for the trip are settled, as for any stop.
            NoteGameDataStop?.Invoke(stop.Why);
        };
        GameData.ActiveSetChanged += RoomGraph.OnActiveSetChanged;
        if (GameData.ActiveSet is not null)
            RoomGraph.OnActiveSetChanged(GameData.ActiveSet);

        // Quest name / visibility overlay — the active set's quests.json (which
        // quests a character finished is kept on the character); reloads on a
        // set or realm change. The mechanical step + bonus data the Quest Status
        // tab shows is crawled from TBInfo at runtime, not stored here.
        Quests = new QuestStore(Profile, ProfileGameDataSet, ActiveRealmFolder, Log);
        // A set picked from the Game Data menu changes with no profile event.
        GameData.ActiveSetChanged += set => Quests.OnActiveSetChanged(set, ActiveRealmFolder());
        Tick.HeartbeatElapsed += () => Quests.TakeInOutsideChanges();
        Emotes = new EmoteStore(log: Log);
        Profile.ProfileLoaded += _ => Emotes.OnBbsChanged(ResolveActiveBbs()?.Name);
        Profile.BbsPinApplied += _ => Emotes.OnBbsChanged(ResolveActiveBbs()?.Name);
        Profile.ProfileClosed += () => Emotes.OnBbsChanged(ResolveActiveBbs()?.Name);
        Emotes.OnBbsChanged(ResolveActiveBbs()?.Name);
        Tick.HeartbeatElapsed += () => Emotes.TakeInOutsideChanges();

        // Boss catalog — the realm's list (seed + the realm's overlay); timer values
        // are looked up from game data at runtime. Reloads its overlay on a realm
        // change, ahead of the timers below, which read it.
        Bosses = new BossStore(Log);
        Profile.ProfileLoaded += _ => Bosses.OnRealmChanged(ActiveRealmFolder(), ProfileGameDataSet());
        Profile.BbsPinApplied += _ => Bosses.OnRealmChanged(ActiveRealmFolder(), ProfileGameDataSet());
        Profile.ProfileClosed += () => Bosses.OnRealmChanged(ActiveRealmFolder(), ProfileGameDataSet());
        Bosses.OnRealmChanged(ActiveRealmFolder(), ProfileGameDataSet());
        // Stop before and Grab All are each character's own, kept in its profile.
        Bosses.SetCharacterFlags(
            hasCharacter: () => Profile.Current is not null,
            read: () => Profile.Current?.BossFlags,
            write: choices =>
            {
                if (Profile.Current is not { } profile) return;
                profile.BossFlags = choices;
                Profile.Save();
            });
        GameData.ActiveSetChanged += _ => Bosses.NoteGameDataChanged();
        Tick.HeartbeatElapsed += () => Bosses.TakeInOutsideChanges();

        // Persisted boss kill-times, per realm. Kill detection is wired later (needs
        // MonsterDeath + RoomTracker); here we just load the active realm's saved
        // timers so a restart resumes mid-countdown.
        BossTimers = new BossTimerStore(Bosses, GameData, Log);
        Profile.ProfileLoaded += _ => BossTimers.OnRealmChanged(ActiveRealmFolder());
        Profile.BbsPinApplied += _ => BossTimers.OnRealmChanged(ActiveRealmFolder());
        Profile.ProfileClosed += () => BossTimers.OnRealmChanged(ActiveRealmFolder());
        BossTimers.OnRealmChanged(ActiveRealmFolder());
        // Several clients share the realm's boss timers, one file; pick up what
        // another client wrote within a heartbeat.
        Tick.HeartbeatElapsed += () => BossTimers.TakeInOutsideChanges();
        // Cleanup-boss DEAD/ALIVE state reads the active realm's nightly-cleanup time.
        BossTimers.SetCleanupConfig(ResolveBossCleanupConfig);

        // ItemNameStore — int→name index for the active Items.json so
        // the keyed-door FSM can resolve KeyItemId → in-game name and
        // send `use <name> <dir>`.
        ItemNames = new ItemNameStore(GameData, Log);
        GameData.ActiveSetChanged += TimedSetIndex("item names", ItemNames.OnActiveSetChanged);
        if (GameData.ActiveSet is not null)
            ItemNames.OnActiveSetChanged(GameData.ActiveSet);

        // ShopStockIndex — item id → shops stocking it, from Shops.json.
        // Feeds PathItemShopRouter's "who sells this?" lookup.
        ShopStock = new ShopStockIndex(GameData, Log);
        GameData.ActiveSetChanged += TimedSetIndex("shop stock", ShopStock.OnActiveSetChanged);
        if (GameData.ActiveSet is not null)
            ShopStock.OnActiveSetChanged(GameData.ActiveSet);

        // MonsterDropIndex — item id → dropping monsters + their spawn rooms,
        // from Monsters.json. Feeds MonsterDropRouter's "who drops this, and
        // where?" lookup for items no shop sells.
        MonsterDrops = new MonsterDropIndex(GameData, Log);
        GameData.ActiveSetChanged += TimedSetIndex("monster drops", MonsterDrops.OnActiveSetChanged);
        if (GameData.ActiveSet is not null)
            MonsterDrops.OnActiveSetChanged(GameData.ActiveSet);

        // RoomHazardIndex — room-entry Spell → item(s) that make the room safe,
        // from Rooms/Spells/Items/TBInfo. Feeds the walker's hazard-gating pass.
        RoomHazards = new RoomHazardIndex(GameData, Log);
        GameData.ActiveSetChanged += TimedSetIndex("room hazards", RoomHazards.OnActiveSetChanged);
        if (GameData.ActiveSet is not null)
            RoomHazards.OnActiveSetChanged(GameData.ActiveSet);

        // RoomBuffStripIndex — room-entry Spell that removes/dispels buffs on
        // entry, from Rooms/Spells. Feeds CastingDirector's buff-suppression gate.
        RoomBuffStrip = new RoomBuffStripIndex(GameData, Log);
        GameData.ActiveSetChanged += TimedSetIndex("room buff strips", RoomBuffStrip.OnActiveSetChanged);
        if (GameData.ActiveSet is not null)
            RoomBuffStrip.OnActiveSetChanged(GameData.ActiveSet);

        // RoomSpellTeleportIndex — room-entry Spell → teleports / may / doesn't, for
        // the map's by-teleport overlay. Subscribed after TBInfo and RoomGraph above:
        // it reads the set's rooms and textblocks through them.
        RoomSpellTeleports = new RoomSpellTeleportIndex(GameData, RoomGraph, SpellCatalog, TBInfo, Log);
        GameData.ActiveSetChanged += TimedSetIndex("room-spell teleports", RoomSpellTeleports.OnActiveSetChanged);
        if (GameData.ActiveSet is not null)
            RoomSpellTeleports.OnActiveSetChanged(GameData.ActiveSet);

        // RoomSpellDamageIndex — room-entry Spell → damages whoever stands there, for
        // the no-rest-in-a-damaging-room rule. Subscribed here for the same reason.
        RoomSpellDamage = new RoomSpellDamageIndex(GameData, RoomGraph, SpellCatalog, TBInfo, Log);
        GameData.ActiveSetChanged += TimedSetIndex("room-spell damage", RoomSpellDamage.OnActiveSetChanged);
        if (GameData.ActiveSet is not null)
            RoomSpellDamage.OnActiveSetChanged(GameData.ActiveSet);
        // After the last of the per-set indexes timed above.
        GameData.ActiveSetChanged += SetIndexTimes.LogLoad;

        // Room tracker. Resets to Unknown on every
        // graph reload because per-room references are invalidated
        // when the active set rebuilds.
        RoomTracker = new Game.Map.RoomTracker(RoomGraph, Log);
        RoomGraph.GraphReloaded += () => RoomTracker.OnGraphReloaded();

        // Shared engine-level recovery gate. The walker and the loop runner
        // (which also drives Auto-Lair) attach themselves to it when they start.
        Recovery = new Game.Map.EngineRecoveryGate(RoomGraph, RoomTracker, Log);
        // The tracker's passive grid re-localiser recovers position in same-name
        // grids when NO engine is driving; it stands down while one is attached so
        // the gate's tier-2 forward localiser is the sole re-anchor.
        RoomTracker.SetEngineAttachedProbe(() => Recovery.HasAttachedEngine);
        // How long a move typed while unsure is waited for scales with the
        // character's hop (Auto-Lair's travel model: Paradigm's movement formula or
        // Stock's encumbrance buckets). AutoLair is built later in init; the lambda
        // only reads it once moves are going out.
        RoomTracker.SetExpectedHopProbe(() => AutoLair?.TravelCostModel.EstimateTravel(1) ?? TimeSpan.Zero);
        // Only on Stock is it recorded that a typed-ahead command is echoed when it
        // runs; read live so a set swap is honoured.
        RoomTracker.SetEchoMarksExecutionProbe(() => GameData.ActiveRealm != Game.RealmType.ParaMud);
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
        // @where position re-fix: Paradigm `rm` first, then a sysop `sys st` where
        // that's the available power (SysopLocate is constructed below but the
        // lambda only reads it at call time, long after connect).
        PartyEssentials.SetPositionRefix((reason, onResolved, onFailed) =>
            ParadigmResync.RequestResyncOnce(reason, onResolved, onFailed)
            || SysopLocate.RequestLocateOnce(reason, onResolved, onFailed));
        // Recovery.TryResync is wired below once MazeSolver exists: a maze solve
        // suppresses `rm` so the asylum is driven by the realm-agnostic look-sweep
        // (stock parity) rather than rm short-circuiting the solver's relocalize.

        // Random-teleport maze index. Rebuilds itself on every graph reload (it
        // subscribes to RoomGraph.GraphReloaded in its ctor), so it's built once
        // at app scope like the tracker. The solver that consumes it is built
        // after the Walker below.
        MazeIndex = new Game.Map.TeleportMazeIndex(RoomGraph, Log);

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
        // LineExtractor in the MainWindowViewModel constructor.
        Death = new Game.DeathDetector(RoomTracker, Log)
        {
            IsParadigm = () => GameData.ActiveRealm == Game.RealmType.ParaMud,
        };

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
        FollowModes = new Game.FollowModeTracker(Router, Log);
        Profile.ProfileLoaded += _ => FollowModes.Reset();

        // HiddenExitRevealManager — walker's sea-retry loop for
        // SearchableHidden exits. Subscribes to RoomTracker.StateChanged
        // for the "exit now visible" signal. Constructed here (after
        // RoomTracker exists); the walker's enqueuer binding and the
        // wire-sender land in MainWindowVM.
        HiddenSearch = new Game.Map.HiddenExitRevealManager(
            RoomTracker,
            maxAttemptsProvider: () => Resolver.Resolve<Models.Profile.OtherSettings>("Other").MaxHiddenSearchAttempts,
            router: Router,
            log: Log,
            isBlinded: () => Conditions.IsBlinded);   // Conditions is built later; read live

        // Winch gates — the nav engines pull a winch, wait for it to turn AND the
        // gate to open (polling the room's open-gate exit, since there's no gate-open
        // line), then move. isGateOpen reads the live open-door directions the room
        // display parses "open gate <dir>" into; scheduleDelay is the same UI-thread
        // one-shot the door FSM uses (null in tests — they drive lines synchronously).
        Winch = new Game.Map.WinchManager(
            Router,
            isGateOpen: dir => RoomTracker.State.OpenDoorDirections?.Contains(dir) == true,
            scheduleDelay: (delay, callback) =>
            {
                var timer = new Avalonia.Threading.DispatcherTimer { Interval = delay };
                timer.Tick += (_, _) => { timer.Stop(); callback(); };
                timer.Start();
                return new DispatcherTimerHandle(timer);
            },
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
        // Its lookups are read when a walk asks, by which time the inventory and
        // the indexes they name exist.
        GiveSources = new PathItemGiveSources(
            ItemSources, Bfs, Movement,
            unwornCount: CountItemUnworn,
            itemName: id => ItemNames.GetName(id),
            isKey: id => ItemNames.ItemTypeOf(id) == Game.Inventory.InventoryManager.KeyItemType,
            soldOrSummoned: id => ShopStock.AnyShopSells(id) || SummonSourcesForItem(id).Count > 0,
            alwaysDroppedBy: AlwaysDroppedBy);
        // GH room labels + the Roomba item-sighting log are BBS-tier (not
        // per-character) — every character on a BBS shares the same gang house.
        // Loaded/reloaded via OnRealmChanged, same pattern as RoomBlacklist.
        GhRoomLabels = new Game.Map.GhRoomLabelStore(Profile, Log);
        GhManagedRooms = new Game.Map.GhManagedRoomStore(Profile, Log);
        GhSuspendedSweep = new Game.Map.GhSuspendedSweepStore(Profile, Log);
        GhItemLocations = new Game.Map.GhItemLocationStore(ItemNames, Log);
        Profile.ProfileLoaded += _ => GhRoomLabels.OnRealmChanged(ActiveRealmFolder());
        Profile.BbsPinApplied += _ => GhRoomLabels.OnRealmChanged(ActiveRealmFolder());
        Profile.ProfileLoaded += _ => GhItemLocations.OnRealmChanged(ActiveRealmFolder());
        Profile.BbsPinApplied += _ => GhItemLocations.OnRealmChanged(ActiveRealmFolder());
        Tick.HeartbeatElapsed += () =>
        {
            GhRoomLabels.TakeInOutsideChanges();
            GhItemLocations.TakeInOutsideChanges();
        };
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
        // Race number for the "(Race: N OK)" exit gate, off the stat screen's race
        // name. null until stats parse or when the name isn't in the Races table.
        Movement.RaceNumberProvider = () =>
        {
            if (!Stats.HasParsed || string.IsNullOrWhiteSpace(PlayerStats.Race)) return null;
            return GameData.FindRowByName("Races", PlayerStats.Race) is { } raceRow
                && raceRow.TryGetProperty("Number", out GameValue raceNumber)
                && raceNumber.ValueKind == System.Text.Json.JsonValueKind.Number
                && raceNumber.TryGetInt32(out int n) && n > 0 ? n : (int?)null;
        };
        Movement.PartyAlignmentsProvider = PartyAlignmentValues;
        // Acquirable-gate providers — feed inventory / stats / hazard data into
        // item, ticket, locked-door, and hazard-room routing. Inventory readiness
        // and stat parsing gate each check so an unknown build walks unrestricted
        // (same rule as level / wealth / class above).
        Movement.InventoryReadyProbe = () => Inventory.IsLoaded;
        Movement.ItemCarriedProbe = IsItemCarried;
        Movement.NegatingItemUsableProbe = NegatingItemUsable;
        Movement.StandingRoomProbe = () => RoomTracker.State.Confidence == Game.Map.RoomConfidence.Confirmed
            ? RoomTracker.State.CurrentRoom?.Key
            : null;
        Movement.PartyShortOfItemProbe = IsPartyShortOfGateItem;
        Movement.StrengthProvider = () => Stats.HasParsed ? PlayerStats.Strength : (int?)null;
        Movement.PicklocksProvider = () => Stats.HasParsed ? PlayerStats.Picklocks : (int?)null;
        // Same bash ceiling the door FSM uses, so the filter and DoorOpenManager
        // never disagree on whether a strength-gated door is bashable.
        Movement.MaxBashableStrengthProvider = () => MaxStrength.MaxAchievableStrength;
        Movement.RoomEntrySpellProbe = key => RoomGraph.GetRoom(key)?.Spell ?? 0;
        Movement.Hazards = RoomHazards;
        Movement.SpellTeleportsAtRandomProbe =
            spell => RoomSpellTeleports.ClassOf(spell) == Game.Map.RoomSpellTeleport.Sudden;
        Favorites = new FavoritesStore(Profile, GameData, ProfileGameDataSet, Log);
        GotoHistory = new GotoHistoryStore(Profile);

        // Coordinator + walker. The coordinator is the single pause-gate hub
        // for every movement engine (walker, loop, Auto-Lair). The walker's
        // wire sender is bound by MainWindowViewModel once the telnet client is
        // up (matching the PartyPoller / AutoPartyManager pattern).
        MovementCoordinator = new Game.Map.MovementCoordinator(Log);

        // Door, winch, hidden-exit and trap tries wait out a rest or a meditate:
        // bash, pick, search and disarm each stand a resting character up, so a try
        // sent mid-rest only breaks a rest that has to start again (and for a trap
        // that just fired, risks it again on low HP). The loop or walk is held for
        // the rest anyway, so nothing is lost by waiting.
        // The master switch holds them the same way: a door, search, winch or
        // disarm try caught mid-way when it goes off waits and picks up when it is
        // back on, with its walk, where an early return would strand the walk's
        // callback.
        bool RestHeld() =>
            MovementCoordinator.IsGateAsserted(Game.Map.MovementCoordinator.HealthRecoveryGate)
            || MovementCoordinator.IsGateAsserted(Game.Map.MovementCoordinator.ManaRecoveryGate)
            || MovementCoordinator.IsGateAsserted(Game.Map.MovementCoordinator.AutoAllGate);
        Door.SetRestHold(RestHeld);
        // A door request belongs to the room it was asked in; once the tracker is
        // sure of another, the door manager drops it rather than work some other
        // room's exit.
        Door.SetConfirmedRoomProbe(() =>
            RoomTracker.State.Confidence == Game.Map.RoomConfidence.Confirmed
                ? RoomTracker.State.CurrentRoom?.Key
                : null);
        Winch.SetRestHold(RestHeld);
        HiddenSearch.SetRestHold(RestHeld);
        TrapDisarm.SetRestHold(RestHeld);
        MovementCoordinator.GatesChanged += () =>
        {
            Door.NotifyRestHoldChanged();
            Winch.NotifyRestHoldChanged();
            HiddenSearch.NotifyRestHoldChanged();
            TrapDisarm.NotifyRestHoldChanged();
        };

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

        // Needs registry: the cross-engine hub where auto-light and the
        // path-item demand tracker post what they need, and the path-item
        // routers and floor collector act on it. Cleared on character swap so
        // pending needs don't leak across profiles.
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
        // Built before the combat tracker and engine so it reads each room roster
        // ahead of them: they ask it about the room inside their own handlers.
        PvpRoom = new Game.Pvp.PvpRoomSafety(
            Router, RoomClassifier,
            pvpEnabled: () => ResolveActiveRealm()?.Realm.PvpEnabled == true,
            inParty: PartyState.HasMember,
            classOf: ResolveKnownPlayerClass,
            levelOf: given => Players.Find(given)?.Level,
            roomAttackFromLevel: cls => SpellCatalog.RoomAttackFromLevel(cls),
            realm: () => GameData.ActiveRealm,
            lastMoveSentAt: () => RoomTracker.LastMoveSentAt,
            log: Log);
        GameData.ActiveSetChanged += _ => PvpRoom.ResetClassCache();
        // Built ahead of the combat tracker and engine, like PvpRoom: a monster whose
        // relationship is Hangup or Flee is answered before their handlers can start
        // a fight in the room. Health, the PvP services and InGameCapture are built
        // further down, so they are reached through lambdas; a method group would be
        // read here, while they are still null.
        MonsterWatch = new Game.Combat.MonsterRelationshipWatcher(
            RoomClassifier,
            resolveOverlay: ResolveMonsterOverlay,
            hangUp: reason => Health.HangUpForMonster(reason),
            flee: (reason, stillHere) => Health.FleeFromMonster(reason, stillHere),
            fleeInFlight: () => Health.IsFleeInFlight,
            masterSwitchOff: () => AutoModeController.KillSwitchEngaged,
            hangupsDisabled: () =>
                ReadSection<Models.Profile.GeneralSettings>(Profile.Current, "General").DisableHangups,
            // The fight's end re-issues the roster (PvpFight.ActiveChanged, below),
            // and a player leaving it is a roster event of its own: either is when
            // a monster held for PvP is answered.
            pvpHandles: roster => PvpFight.IsActive || PvpResponse.IsAnswering(roster),
            atBoardMenu: () => InGameCapture.AtBoardMenu,
            describeRoom: DescribeRosterRoom,
            // UI-thread one-shot, for the once-a-second countdown of the hold.
            schedule: (delay, callback) =>
            {
                var timer = new Avalonia.Threading.DispatcherTimer { Interval = delay };
                timer.Tick += (_, _) => { timer.Stop(); callback(); };
                timer.Start();
            },
            log: Log);
        // The minute's hold after a hang-up from here starts at the first game
        // prompt once the character is back, and belongs to the character that
        // hung up.
        PromptScanner.PromptObserved += _ => MonsterWatch.NoteInGamePrompt();
        Profile.ProfileLoaded += _ => MonsterWatch.Reset();
        Profile.ProfileClosed += () => MonsterWatch.Reset();
        MonsterWatch.HoldNotice += text => WriteTerminalNotice($"[{text}]");
        // Another player's room attack shows as a line, not a room observation, so
        // nothing re-asks the combat gate on its own. Posted: the line is still being
        // dispatched, and the re-check can send a break.
        PvpRoom.RoomAttackSeen += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (Recovery.AttachedEngine is null) return;
            CombatTracker.OnAutoAttackChanged();
            RoomClassifier.ReemitCurrent();
        });
        CombatTracker = new Game.Combat.CombatStateTracker(
            Router, MovementCoordinator, RoomClassifier, MonsterMessages,
            PlayerState,
            isAutoAttackEnabled: () => ReadAutoModeFlag(d => d.AutoCombat) && !CombatSuppressedInCurrentRoom(),
            // Same overlay-resolve helper CombatManager uses — keeps the
            // engageable predicate consistent so the gate and the swing
            // decision can't diverge on the same room state.
            resolveOverlay: ResolveMonsterOverlay,
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
            shouldWriteTrace: () => LogDiagnostics.AutoCollectLogs,
            // UI-thread one-shot, same as the door FSM's: ends a round once its lines go quiet.
            scheduleDelay: (delay, callback) =>
            {
                var timer = new Avalonia.Threading.DispatcherTimer { Interval = delay };
                timer.Tick += (_, _) => { timer.Stop(); callback(); };
                timer.Start();
                return new DispatcherTimerHandle(timer);
            });
        // Drive round boundaries off the 5-second combat heartbeat so each round
        // closes (and is counted) in real time rather than lagging until the next
        // damage line or *Combat Off*. Both are app-lifetime singletons, so no
        // unsubscribe is needed.
        Tick.CombatTickElapsed += RoundDamage.OnCombatTick;
        // The ledger names combatants from the room roster, the party and ourselves.
        RoomClassifier.EntitiesObserved += RoundDamage.NoteRoomEntities;
        // Wire Inspector → Classified shows how the ledger read each damage line.
        RoundDamage.LineAttributed += CombatClassifier.NoteLedger;
        RoundDamage.SetNameSources(
            partyNames: () => PartyState.Members.Select(m => m.Name),
            selfName: () => Party.LocalCharacterName ?? Profile.Current?.Name);
        // Party HP between polls: the ledger's damage on members, the heals seen landing
        // on them, and the drains they land. The heal reader comes from the Spells table + message
        // catalogue, so a set switch or a message edit rebuilds it on next use.
        PartyHp = new Game.PartyHpEstimator(Router, RoundDamage, Party,
            buildReader: BuildHealLineReader,
            buildDrains: () => new Game.Spells.DrainLineSet(
                GameData.GetTable("Spells") is { } doc
                    ? Game.Spells.DrainLineSet.DrainSpells(doc)
                    : Array.Empty<int>(),
                Messages.Messages),
            ownLevel: () => Stats.HasParsed ? PlayerStats.Level : 0,
            log: Log);
        GameData.ActiveSetChanged += _ => PartyHp.Invalidate();
        Messages.Messages.CollectionChanged += (_, _) => PartyHp.Invalidate();
        // Every round's ledger goes to the Round Totals window's board, which picks
        // its rows by the window's own options.
        RoundTotals = new Game.Combat.RoundTotalsBoard(() =>
            ReadSection<Models.Profile.RoundTotalsWindowSettings>(
                Profile.Current, Models.Profile.RoundTotalsWindowSettings.SectionKey));
        Profile.ProfileLoaded += _ => RoundTotals.Clear();
        // Settings → Combat "Show combat round totals": also print each round's ledger
        // in the terminal as a table (read per round, so the checkboxes apply at once),
        // with the rows its Me / Party / Other players / Monsters boxes pick. One
        // notice for all its lines, so no blank line falls between them.
        RoundDamage.RoundComplete += round =>
        {
            RoundTotals.Publish(round);
            Models.Profile.CombatSettings combat = ReadSection<Models.Profile.CombatSettings>(Profile.Current, "Combat");
            if (!combat.ShowCombatRoundTotals) return;
            List<Game.Combat.CombatantKind> shown = new(4);
            if (combat.ShowsRoundTotalsRow(combat.ShowCombatRoundTotalsSelf)) shown.Add(Game.Combat.CombatantKind.Self);
            if (combat.ShowsRoundTotalsRow(combat.ShowCombatRoundTotalsParty)) shown.Add(Game.Combat.CombatantKind.Party);
            if (combat.ShowsRoundTotalsRow(combat.ShowCombatRoundTotalsPlayers)) shown.Add(Game.Combat.CombatantKind.Player);
            if (combat.ShowsRoundTotalsRow(combat.ShowCombatRoundTotalsMonsters)) shown.Add(Game.Combat.CombatantKind.Monster);
            IReadOnlyList<string> table = Game.Combat.RoundTotalsFormatter.Table(round, shown,
                combat.ShowCombatRoundTotalsEachMonster);
            if (table.Count > 0) WriteTerminalNotice(string.Join("\r\n", table));
        };
        // Settings → Party "Send par": after a combat round, and on a round with
        // unknown damage. After the totals above, so they print first.
        RoundDamage.RoundComplete += PartyPoller.NoteRoundComplete;
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
            PlayerState, ResolveActiveRealm, Bbs.Save, Log);
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
        // them, holds movement via AllyDownGate for as long as the climb back to
        // positive HP can take (bounded by the realm's death floor), then polls their
        // off-roster vitals via @health and re-invites once they're up when we lead.
        // The heal-by-name is delegated to CastDirector via the downed-ally
        // provider wired below. Gated on AutoHeal (shared party-heal master).
        AllyDropped = new Game.AllyDroppedHandler(
            Router, PartyState, Party, Chat, MovementCoordinator,
            readParty: () => ReadSection<Models.Profile.PartySettings>(Profile.Current, "Party"),
            isEnabled: () => ReadAutoModeFlag(d => d.AutoHeal),
            log: Log,
            readDeathFloor: () => ResolveActiveRealm()?.Realm.PlayerDiesAtHp ?? -25);

        // Mid-room arrival watcher. Subscribes to the
        // RoomEntryArrival pattern + appends to the classifier so the
        // Combat gate / CombatManager react to spawns immediately.
        RoomEntry = new Game.Combat.RoomEntryWatcher(Router, RoomClassifier, Log);
        RoomEntry.ArrivalObserved += PvpRoom.NoteArrival;
        PartySplit = new Game.Pvp.PartySplitTracker(
            PartyState,
            hold: () => TimeSpan.FromSeconds(Math.Max(0,
                ReadSection<Models.Profile.PvpSettings>(Profile.Current, "Pvp").PartySplitHoldSeconds)),
            ownGivenName: () => Party.LocalCharacterName ?? Profile.Current?.Name);
        PvpRoom.SetPartySplit(PartySplit);
        PvpAttacks = new Game.Pvp.PvpAttackWatcher(
            Router, RoomClassifier, PvpRoom, Players, PartyState, PartySplit,
            pvpEnabled: () => ResolveActiveRealm()?.Realm.PvpEnabled == true,
            flipFriends: () => ReadSection<Models.Profile.PvpSettings>(Profile.Current, "Pvp")
                .FlipFriendToEnemyIfAttacked,
            ownGivenName: () => Party.LocalCharacterName ?? Profile.Current?.Name,
            log: Log);
        PvpAttacks.Attacked += attack =>
        {
            if (attack.MarkedEnemy)
                WriteTerminalNotice($"[PvP: {attack.Player} attacked you and is now marked Enemy]");
        };
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

        // Monster death watcher: an exp gain followed by *Combat Off*, or a
        // kill our own room spell made (raised by CombatManager), is a death.
        // A death names no monster, so the roster-resync handler below
        // re-displays the room and CombatManager re-picks from the true roster
        // instead of being blocked by a stale entry for the just-killed mob
        // (the "kobold thief arrived but no attack" bug).
        MonsterDeath = new Game.Combat.MonsterDeathWatcher(Router, Log);
        // The damage lines name the monster when nothing else does (a den too dark
        // to list anyone): kept for the last couple of rounds.
        RecentFoes = new Game.Combat.RecentFoeNames(isPlayer: name =>
            PartyState.Members.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
            || Players.Find(name) is not null);
        RoundDamage.Attributed += line => RecentFoes.Note(line.Sides);
        RoomTracker.StateChanged += t =>
        {
            if (t.NewRoom?.Key != t.PreviousRoom?.Key) RecentFoes.Clear();
        };
        // Boss-timer auto-start. MUST be the FIRST MonsterDied subscriber: it reads
        // the engaged target name (CombatManager.CurrentTarget) live, and a later
        // subscriber (the roster-resync below) clears it via NoteUnattributedDeath.
        // The in-game signal is "engaged a named monster, it died (awarded exp)"; the
        // room comes from the live tracker (the event carries neither), and the death
        // is attributed through the engaged name.
        // DeathAttributionTarget covers an exp-inferred kill, which nulls CurrentTarget
        // before this fires, so "we attacked the boss, then gained exp" still attributes.
        // A room spell's kill names nobody: the engaged target is only the monster the
        // round was anchored to, which may be the boss while an add is the one that
        // died. It goes in unnamed, and is the boss's only on the exp it paid or the
        // roster re-read finding the boss gone.
        MonsterDeath.MonsterDied += evt =>
            BossTimers.OnMonsterDied(evt, RoomTracker.State.CurrentRoom?.Key,
                evt.RoomSpellRoster is null ? Combat.DeathAttributionTarget : null,
                RecentFoes.Within(TimeSpan.FromSeconds(12)));
        // What the boss is worth, and what everything else in its room is: an unnamed
        // death there is told apart by the exp it paid.
        BossTimers.SetRoomExpResolver((def, room) =>
        {
            // A boss can have two records under one name: the neutral one standing
            // in its room, worth nothing, and the hostile one it becomes when
            // attacked, which is what a kill pays (Lallim Whitemane, Sharh'Kur). The
            // boss list names the first, so the boss is worth the most any record of
            // its name gives.
            long bossExp = def.MonsterNumber is { } n ? MonsterCatalog.Get(n)?.EffectiveExp ?? 0 : 0;
            foreach (Game.Combat.MonsterCatalogEntry sameName in MonsterCatalog.All)
                if (string.Equals(sameName.Name, def.MatchName, StringComparison.OrdinalIgnoreCase))
                    bossExp = Math.Max(bossExp, sameName.EffectiveExp);

            List<long> others = new();
            foreach (int id in MonsterSpawns.MonsterIdsSummonedAt(room))
            {
                if (id == def.MonsterNumber || MonsterCatalog.Get(id) is not { } other) continue;
                if (string.Equals(other.Name, def.MatchName, StringComparison.OrdinalIgnoreCase)) continue;
                others.Add(other.EffectiveExp);
            }
            return (bossExp, others);
        });
        // Grab-All: the moment a tracked boss with GrabAll set dies, blindly `get`
        // every item in its game-data drop table — no room re-parse. BossKilled fires
        // for any matched boss; we gate on the flag here, where the catalog + item
        // names + wire sender are all reachable.
        BossTimers.BossLootDropped += FireBossGrabAll;
        BossTimers.SetDeathSummonResolver(def =>
            BossDeathChain(def).Skip(1).Select(m => m.Name).ToList());
        // Surface recognized deaths in the Wire Inspector's Classified view (a passive
        // display side-effect) — the exp gained marks the kill.
        MonsterDeath.MonsterDied += evt =>
            CombatClassifier.NoteMonsterDeath(evt.ExperienceGained);
        // Temp death-spell recovery: when a monster whose DeathSpell is a silent "…temp"
        // spell dies, those spells stall the game engine, so send that spell's message
        // CastResponse (seeded "^M^M" = two carriage returns) to unstick it. Subscribes
        // BEFORE the roster-resync below so Combat.CurrentTarget — the death's identity,
        // since the event names no monster — is still set.
        _tempDeathBurst = new Game.Combat.TempDeathBurst(n => TempDeathSpellOf(n) is not null);
        RoomClassifier.EntitiesObserved += _tempDeathBurst.NoteRoomListed;
        MonsterDeath.MonsterDied += FireTempDeathResponse;
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
        // Summon-on-death recheck. MUST subscribe to MonsterDied BEFORE the roster-
        // resync handler below: on a kill whose DeathSpell summons, it asserts a
        // hold + sends a CR to re-scan the room, and that hold has to be in place
        // before the resync's RemoveDeadEntity clears the Combat gate and steps the
        // walker (both synchronous). Wire-sender bound per-session by the VM.
        SummonSettle = new Game.Combat.SummonOnDeathSettle(
            MonsterDeath, RoomClassifier, MovementCoordinator, MonsterDeathSummon,
            currentTargetName: () => Combat.DeathAttributionTarget,
            // A run frozen by the master switch still reads as active, and its
            // redisplay must not go out then.
            movementActive: () => MovementControl.IsActive && !AutoModeController.KillSwitchEngaged,
            log: Log);
        // A drop lands on the ground with no line of its own, so the kill of a monster
        // that can drop something flagged for auto-collect re-displays the room. Wired
        // ahead of the roster resync below for the reason the summon settle is: the
        // walker hold has to be up before the resync clears the Combat gate.
        MonsterDeath.MonsterDied += ReLookForDrops;
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
            //
            // Not for a room spell's kill: the combat manager raised that one itself,
            // from the exp line it is still handling, and asks for the re-display
            // there. What it was fighting is no guide to which monster died.
            if (evt.RoomSpellRoster is not null) return;
            Log.Info(Game.Combat.MonsterDeathWatcher.LogCategory,
                "death — forcing roster resync");
            Combat.NoteUnattributedDeath();
        };

        // CombatManager. Picks a target on each
        // classifier emit and sends the configured attack command via
        // the bound wire sender. Reads CombatSettings live (same
        // pattern as CombatStateTracker) so toggling Master / changing
        // TargetOrder / etc. mid-session takes effect on the next
        // Also-Here line.
        Combat = new Game.Combat.CombatManager(
            Router, RoomClassifier, MonsterMessages,
            // Resolve per-monster overlay through the shared tier-merge helper
            // (seed Defaults + Global / BBS / Char overrides).
            resolveOverlay: ResolveMonsterOverlay,
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
            isEnabled: () => ReadAutoModeFlag(d => d.AutoCombat) && !CombatSuppressedInCurrentRoom(),
            readOwnGivenName: () => Profile.CurrentProfileName,
            post: action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            log: Log,
            readPartySettings: () =>
                ReadSection<Models.Profile.PartySettings>(Profile.Current, "Party"),
            roomAwareResolve: RoomAwareMonster.ResolveInCurrentRoom,
            // Resolve a debuff slot's cast-code to its catalog row (energy cost +
            // targeting scope) so a mis-slotted spell is rejected before it casts.
            resolveSpellByCode: code => Spellbook.FindByCastCode(code));
        // A kill of our own room spell is a death like any other, raised on its exp
        // line: the watcher's exp + *Combat Off* pairing can't see one that leaves a
        // survivor, since the game prints no *Combat Off* for it (Paradigm).
        Combat.RoomSpellKill += (experience, roster) => MonsterDeath.NoteRoomSpellKill(experience, roster);

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

        // A monster's mid-fight summon ("The fat half-orc sentry shouts for aid!")
        // re-displays the room so the summoned monster reaches the roster before its
        // summoner dies. The wordings come from the Spells table + message catalogue,
        // so a set switch or a message edit rebuilds them on next use.
        MonsterSummons = new Game.Combat.MonsterSummonWatcher(Router, RoomClassifier,
            build: () => new Game.Spells.SummonLineSet(
                GameData.GetTable("Spells") is { } doc
                    ? Game.Spells.SummonLineSet.SummonSpells(doc)
                    : Array.Empty<int>(),
                Messages.Messages),
            requestRoomRefresh: Combat.RequestRoomRefresh,
            log: Log);
        GameData.ActiveSetChanged += _ => MonsterSummons.Invalidate();
        Messages.Messages.CollectionChanged += (_, _) => MonsterSummons.Invalidate();

        // Our own say echo ("You say \"…\"") only reaches the chat router — it's chat
        // by shape — so the combat engine hears about an attack the server read as a
        // say from here.
        Chat.EntryClassified += e =>
        {
            if (e.Channel == Game.ChatChannel.Local && e.Speaker is null) Combat.NoteOwnSay(e.Message);
        };

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
        // Tells a roster emptied where we stand from a move out of a fight.
        CombatTracker.SetCurrentRoomProbe(() => RoomTracker.State.CurrentRoom?.Key);

        // The Combat → Min/Max Monsters window only makes sense while a
        // walker / loop / auto-lair is actively trying to move us past a
        // room — standing here idle with nothing else going on (freshly
        // logged in, no route queued) should fight back regardless of room
        // population rather than stand undefended. Same probe wired to both
        // so combat's engage decision and the walker's gate never disagree.
        Combat.SetMovementActiveGate(() => Recovery.AttachedEngine is not null);
        CombatTracker.SetMovementActiveGate(() => Recovery.AttachedEngine is not null);

        // While an AttackPrevented message is active (stun / petrify / bind), the
        // server rejects every attack the player issues — weapon and spell — so the
        // combat engine holds all offensive output until the wear-off clears it.
        Combat.SetAttackPreventedGate(() => Conditions.IsAttackPrevented);
        Combat.SetFearGate(() => Conditions.IsFeared);

        // A combat-spell engage can lose its initial send to a self-buff that just
        // spent the cast slot. On a fresh process there may be no combat-tick anchor
        // yet, and because no attack reached the server there is no engagement output
        // guaranteed to create one. Seed TickEngine's timer fallback so the owed
        // attack gets a deterministic next-round retry.
        Combat.SetCombatTickAnchor(Tick.EnsureCombatTickAnchor);

        Combat.SetDeathSummonProbe(MonsterDeathSummon.SummonsOnDeath);
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

        // Attack-order re-fire quiet window: a UI-thread one-shot so attack-last /
        // attack-after re-fire once the party's staggered announces have gone quiet.
        Combat.SetRefireSettleScheduler((delay, callback) =>
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

        // HealthManager. It runs while either Auto-Heal or Auto-Rest is on
        // (GeneralSettings.AutoMode): its flee and emergency hangup protect a
        // character under either. The resting itself follows Auto-Rest alone
        // (SetRestEnabledGate below). With both off, every threshold check +
        // rest/stand emit short-circuits.
        Health = new Game.Health.HealthManager(
            PlayerState, MovementCoordinator,
            readSettings: () =>
                ReadSection<Models.Profile.HealthSettings>(Profile.Current, "Health"),
            isEnabled: () => ReadAutoModeFlag(d => d.AutoHeal || d.AutoRest),
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
            readDeathFloor: () => ResolveActiveRealm()?.Realm.PlayerDiesAtHp ?? -25,
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
            post: action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            // @panic send gate: broadcast ".@panic" on say when we're leading and
            // PartySettings.UsePanicWhileLeading is set, at the instant the
            // emergency-hangup floor is crossed.
            readPartySettings: () => ReadSection<Models.Profile.PartySettings>(Profile.Current, "Party"),
            selfIsPartyLeader: () => PartyState.IsInParty && PartyState.SelfIsLeader);
        Health.IsMovePending = () => RoomTracker.State.Confidence == Game.Map.RoomConfidence.Pending;
        // MovementControl is built further down; the lambda reads it when a Flee
        // monster is seen.
        Health.IsNavigationPausedByUser = () => MovementControl.IsUserPaused;
        // A flee does not outlive what cuts it off (user, 2026-10-10: "if we died,
        // the flee needs to end"). Health is built once, so a profile swap is one of
        // those; the disconnect is MainWindowViewModel's to tell it. The death
        // halt's own subscription, made earlier, has stopped the engines by now.
        RoomTracker.PlayerDeathObserved += () => Health.EndFlee("died");
        // The walk or loop a cut-off flee had paused goes with the character: left
        // paused it would wait for a resume nobody will give.
        Health.StopMovementEngine = (engine, reason) =>
        {
            if (engine is Game.Map.LoopRunner loop) loop.Stop(reason);
            else if (engine is Game.Map.AutoWalkManager walk) walk.Stop(reason);
        };
        Profile.ProfileLoaded += _ => Health.EndFlee("another profile was loaded", stopItsEngine: true);
        Profile.ProfileClosed += () => Health.EndFlee("the profile was closed", stopItsEngine: true);
        // The first game prompt after a reconnect hands back a walk that a flee cut
        // off by the disconnect had paused.
        PromptScanner.PromptObserved += _ => Health.NoteInGamePrompt();
        Health.IsServerEngaged = () => CombatTracker.IsServerEngaged;
        RoomTracker.MoveBlocked += Health.NoteMoveBlocked;
        // The room we came from — the Backward flee's retreat when there's no trail
        // to the loop's origin (we're standing on it).
        Health.PreviousRoom = () =>
        {
            IReadOnlyList<Game.Map.RoomKey> history = RoomTracker.GetHistory();
            for (int i = 1; i < history.Count; i++)
                if (!history[i].Equals(history[0])) return history[i];
            return null;
        };
        Health.RoomExits = key => RoomGraph.GetRoom(key)?.Exits.ToDictionary(e => e.Key, e => e.Value.Target);
        Health.RoomRisk = key => (IsBossRoomLive(key),
            Game.Map.RoomTooltipBuilder.TryParseLairMax(RoomGraph.GetRoom(key)?.RawLairTag, out int lairMax) ? lairMax : 0);
        Health.IsClosedToRoutes = Movement.IsClosedToRoutes;

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

        // Boss-timer fallback: a tracked boss that was in the room roster and then
        // vanishes from a same-room re-parse (with no departure line) is a kill we
        // never engaged — start its timer. The room comes from the live tracker.
        RoomClassifier.EntitiesObserved += obs =>
            BossTimers.OnRoomEntitiesObserved(obs, RoomTracker.State.CurrentRoom?.Key,
                RoomTracker.State.CurrentRoom?.Name,
                movePending: RoomTracker.State.Confidence == Game.Map.RoomConfidence.Pending);

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
            // HealthManager decides when to (re-)ask; resend so a wait the leader has
            // timed out on is re-sent rather than deduped as already held.
            // The note names the pool when it's HP; read as the wait is sent, inside
            // HealthManager's own evaluate, so its gate flags are current.
            requestPartyWait: () => PartyRest.RequestWait(
                Game.WaitReason.Health, resend: true, note: Health.PartyWaitNote),
            requestPartyOk: () => PartyRest.RequestOk(Game.WaitReason.Health),
            isLeaderResting: () => PartyLeaderRest.LeaderIsResting,
            requestPartyHeal: () => PartyRest.RequestHeal(),
            isLeaderWaited: () => PartyState.SelfIsLeader && PartyEssentials.IsPaused,
            isSelfPoisoned: () => Conditions.IsPoisoned);
        Router.Subscribe(Services.Patterns.KnownPatterns.RestRefusedSick, _ => Health.NoteRestRefusedSick());
        Router.Subscribe(Services.Patterns.KnownPatterns.MeditateNotNeeded, _ => Health.NoteMeditateNotNeeded());

        // Wait-edge nudge: a standing-idle leader's PlayerState may not change
        // between prompt ticks, so without this poke the leader-waited downtime rest
        // wouldn't start until the next tick (and an HP-only deficit with no regen
        // ticks could stall). Mirror the leader-rest nudge above — Evaluate on both
        // the raise edge (start resting) and the clear edge (@ok / timer → post-rest
        // stand). No-op for solo / followers (isLeaderWaited gates on SelfIsLeader).
        PartyEssentials.PauseGateChanged += _ => Health.Evaluate();

        Health.SetRestEnabledGate(() => ReadAutoModeFlag(d => d.AutoRest));
        Health.SetMasterSwitch(
            () => AutoModeController.KillSwitchEngaged,
            what => AutoModeController.Blocks("Hang-ups", what));
        Health.SetHangupPenaltyLog(LogHangupPenalty);
        // Rest-skip has two independent sources, either one suppresses both rest
        // gates: (1) Sprint Mode — a global "never pause to rest" toggle (see
        // ReadSprintMode); (2) the per-waypoint "do not rest in this room" flag —
        // true while a loop is running and the room we're standing in is one of
        // its waypoints flagged DoNotRest. Matched by room key (per-room), so it
        // clears the instant the loop steps into any other room. Loops only.
        Health.SetDoNotRestSelector(() =>
            ReadSprintMode()
            || (LoopRunner.State != Game.Map.LoopState.Idle
                && RoomTracker.State.CurrentRoom is { } here
                && LoopRunner.CurrentLoop?.Waypoints is { } wps
                && wps.Any(w => w.DoNotRest && w.Key.Equals(here.Key))));
        // A third source, the game's own: a room whose spell damages us (magma heat
        // with no feather on) breaks every rest on its six-second tick.
        Health.SetRoomSpellDamageProbe(() =>
            RoomSpellHurtingUs() is { } spell ? $"{spell.Name} (#{spell.Number})" : null);
        // The loop's "rest up here" rooms: rest to rest-max there before moving on.
        // A Rest-up event makes wherever we stand one (Events is built later).
        Health.SetRestHereSelector(() =>
        {
            if (Events is not null && Events.RestUpRequested) return (true, true);
            if (LoopRunner.State == Game.Map.LoopState.Idle
                || RoomTracker.State.CurrentRoom is not { } here
                || LoopRunner.CurrentLoop?.Waypoints is not { } wps) return default;
            bool hp = false, mana = false;
            foreach (Game.Map.LoopWaypoint w in wps)
                if (w.Key.Equals(here.Key)) { hp |= w.RestHereHp; mana |= w.RestHereMana; }
            return (hp, mana);
        });

        // Server-side resting state clears on move; drop our latch
        // too so the next threshold breach actually fires `rest`
        // again instead of skipping it on a stale _restInFlight.
        RoomTracker.StateChanged += t =>
        {
            if (ReferenceEquals(t.PreviousRoom, t.NewRoom)) return;
            if (t.PreviousRoom is null || t.NewRoom is null)
            {
                // Out of a placed room into one the map doesn't hold, or back: no
                // move to report, but a rest put off for the old room's spell is no
                // longer owed "here".
                Health.NoteRoomPlacementChanged(t.NewRoom?.Key);
                return;
            }
            if (t.PreviousRoom.Key.Equals(t.NewRoom.Key)) return;
            Health.NoteRoomChanged(t.NewRoom.Key);
            // A move retries any party-buff targets we'd backed off as hidden.
            CastDirector.NoteRoomChanged();
        };

        // Item-boss Grab-All: walking into a room that holds a Grab-All *item* boss
        // (a box, not a monster) fires a blind `get` for it — item bosses never die,
        // so they're grabbed on entry instead of on death. Separate handler with a
        // looser guard so it fires on the very first room entry too (a teleport-in).
        RoomTracker.StateChanged += t =>
        {
            if (t.NewRoom is not { } nr) return;
            if (t.PreviousRoom is { } pr && pr.Key.Equals(nr.Key)) return;
            FireItemBossGrabOnEntry(nr.Key);
        };

        // CastCoordinator. Subscribes to spell-failure
        // patterns directly; tick-clears its block latch + cooldown via
        // TickEngine.CombatTickElapsed so the next round can cast.
        Cast = new Game.Spells.CastCoordinator(Router, Log);
        Tick.CombatTickElapsed += () => Cast.OnCombatTick(Tick.LastCombatTickWasPlaced);
        // The line is "<cast code>[ <target>]"; only a spell that costs round energy
        // is an attack cast.
        Cast.CastSent += line =>
        {
            int space = line.IndexOf(' ');
            RoundDamage.NoteOwnCast(attack: CombatSpells.IsCombatSpell(space < 0 ? line : line[..space]));
        };

        // ConditionTracker reads MessageStore + line-side patterns to surface
        // ActiveFlags; CastingDirector reads them for its Curing slot.
        // AttachLineExtractor lands in MainWindowViewModel alongside the other
        // line consumers.
        Conditions = new Game.Conditions.ConditionTracker(Messages, Log);
        // Stock's `stat` lists each active effect as its bare applied line; the stat
        // screen around it is what marks it a readout, not a cast.
        Conditions.SetStatScreenProbe(() => Stats.InStatScreen);
        // An applied line several spells print is read by which of them the room or
        // a monster in it casts. What of the room and its roster can be gone by is
        // CastReach.Standing's to say.
        SpellCasters = new Game.GameData.SpellCasterIndex(GameData, () => RoomGraph.Rooms, Log, TBInfo);
        RoomGraph.GraphReloaded += () => Task.Run(SpellCasters.Warm);
        if (GameData.ActiveSet is not null) Task.Run(SpellCasters.Warm);
        CastReach = new Game.Conditions.CastReach(SpellCasters, () =>
            Game.Conditions.CastReach.Standing(
                RoomTracker.State.Confidence, RoomTracker.State.CurrentRoom,
                inDarkRoom: RoomTracker.IsInDarkRoom, enteredBlind: RoomTracker.EnteredBlind,
                blinded: Conditions.IsBlinded,
                RoomClassifier.Current, RoomClassifier.RosterIsWhole, SpellCasters.KnowsMonster,
                pvpRealm: ResolveActiveRealm()?.Realm.PvpEnabled == true, isPartyMember: PartyState.HasMember));
        Conditions.SetCastReach(CastReach);
        Conditions.CasterNamed += RoomClassifier.NoteCasterNamed;
        // Watches the same wire for lines neither the message catalogue above nor any
        // registered Router pattern recognizes, staging them as review candidates.
        // AttachLineExtractor lands in MainWindowViewModel alongside the other line
        // consumers; Enabled mirrors LogDiagnostics.CaptureUnrecognizedMessages below.
        // currentRoom copies out the live position so each candidate is tagged with
        // where it was first seen — a locator hint for tracking down the source.
        MessageCandidateWatcher = new Game.MessageCandidateWatcher(
            Router, Messages, MessageCandidates,
            currentRoom: () => RoomTracker.State.CurrentRoom?.Key, log: Log,
            // A room-display title line is not a server message — the room-display
            // parser reads it directly and registers no router pattern, so exclude
            // any line that's a known room name in the active set. Asks the room graph's
            // name index, not the Rooms table — a lookup there re-reads and keeps
            // the set's largest table for the whole session after the graph
            // build evicted it.
            isKnownRoomName: text => RoomGraph.FindByName(text).Count > 0,
            // Stateful block parsers read the wire directly and register no router
            // pattern, so AnyPatternMatches can't speak for the lines they consume.
            // Each contributes its OWN matcher here rather than have the shapes
            // restated in the watcher: the `par` roster (PartyManager), the stat /
            // exp / health sheet (StatParser), and the `spells` / `pow` listing
            // (SpellListParser) — otherwise every such poll flooded the queue.
            isRecognizedByDirectParser: text =>
                Game.PartyManager.IsRosterRow(text)
                || Game.StatParser.IsStatScreenLine(text)
                || Game.Spells.SpellListParser.IsSpellListLine(text)
                // The full-'i' inventory dump: its "You are carrying …" anchor plus every
                // word-wrapped continuation row (items / keys / wealth), which InventoryManager
                // consumes as a gated block with no per-row router pattern. Anchor by prefix
                // (the flag isn't set yet on that first line — see IsCapturing) and let the
                // live capture flag cover the rest.
                || text.StartsWith("You are carrying ", StringComparison.Ordinal)
                || Inventory.IsCapturing
                // Another character handing us an item or coins: InventoryManager
                // reads the line itself and files it.
                || Inventory.IsReceivedHandOverLine(text)
                // "Uses remaining: N" off an item look — ItemChargeTracker reads it via
                // TokenCatalog with no router pattern, so reuse that same recognizer.
                || Game.Tokens.TokenCatalog.ParseUsesRemaining(text) >= 0
                // Another KNOWN player changing gear ("X wears / removes …!") — roster-gated
                // so a same-shaped monster / spell line can't be suppressed.
                || Game.BenignChatterMatcher.IsOtherPlayerGearSwap(text, IsRoomOrPartyPlayer)
                // A prompt the statline pattern can't read arrives as an ordinary line,
                // with the echo of whatever was typed glued on. The reconciler holds the
                // last such prompt for as long as the mismatch lasts.
                || (StatlineReconcile.LastPromptMatched == false
                    && StatlineReconcile.LastUnmatchedPrompt?.Trim() is { Length: >= 3 } unreadPrompt
                    && text.StartsWith(unreadPrompt, StringComparison.Ordinal)),
            // Colour-aware: a BBS action / emote is told from a spell line only by its
            // all-green colouring plus a known-player check — the exact recognizer
            // ChatRouter uses to file these under the SAY channel.
            isRecognizedLine: line =>
                Game.ActionEmoteClassifier.IsAllGreen(line)
                && Game.ActionEmoteClassifier.Classify(line.Text, IsKnownRoomPlayer, out _)
                   != Game.ActionEmoteClassifier.Kind.None,
            // "<Actor> <verb> an <ammo> at <target>!" reads identically whether it's
            // archery or a projectile spell, so the shape can't be a router pattern —
            // it needs to know who acted. A no-magery class settles it.
            isNonCasterPhysicalAction: text =>
                Game.Combat.NonCasterAttackLine.Matches(text, PlayerCanCast),
            // An item's `look` leads with its bare name, then free-text description
            // rows up to the prompt.
            isListingHeader: text =>
                text.Length <= 40 && text[^1] != '.' && ItemNames.FindByName(text) is not null,
            // A named exit ("go manhole") prints its own passage flavour, which the
            // game data doesn't carry; the room's exit commands identify the cause.
            isRoomExitCommand: IsExitCommandHere);
        // Subscribed after the message and candidate stores' own loads, so the queue is
        // re-checked against the set's freshly loaded catalogue.
        GameData.ActiveSetChanged += _ => PruneMessageCandidatesWhenIdle();

        // AilmentSyncEngine — outbound ailment broadcast. On catching a VERBOSE
        // ailment (blind / confused / diseased / held) it announces a BARE token
        // ".@blind" etc. on say at apply (MegaMUD parity — no 'on'/'off'), and
        // again on following a leader while still afflicted, so other MudPlay
        // clients mirror our state and a cure-holds caster can free us, and
        // @waits the leader for each. POISON is
        // never announced on say — it's par-owned (PartyManager). On CLEAR nothing
        // is said (MegaMUD sends no 'off'); only the @ok telepath releases the
        // leader's wait. The say only fires when we're in a party AND have no cure
        // spell configured for that ailment (we self-cure silently otherwise);
        // held announces AND @waits like the curable four (no Ignore gate). Each
        // per-ailment SpellsSettings Ignore* flag is the single toggle that gates
        // BOTH the say and the @wait for the curable four. Wire-sender for the say
        // bound in MainWindowViewModel; the @wait routes via PartyRest's own sender.
        AilmentSync = new Game.Conditions.AilmentSyncEngine(
            Conditions, PartyRest,
            readSpells: () => ReadSection<Models.Profile.SpellsSettings>(Profile.Current, "Spells"),
            isInParty: () => PartyState.IsInParty,
            hasCureConfigured: HasCureConfigured,
            log: Log);
        Party.FollowingLeader += AilmentSync.NoteFollowingLeader;

        // PartyAilmentTracker — inbound counterpart. Mirrors a member's
        // ".@poisoned" / ".@held" etc. say announce onto their party chip (via
        // PartyManager, the chip-field owner), pauses the leader on ".@held"
        // (via PartyEssentials.NotePause), and clears the chip when OUR cure
        // spell is observed landing on them. The cure matchers are read live
        // each line so re-configuring a cure spell takes effect without
        // rebuilding the tracker. AttachLineExtractor lands in
        // MainWindowViewModel alongside the other line consumers.
        PartyAilment = new Game.Conditions.PartyAilmentTracker(
            Chat, Party, PartyEssentials, CureCastMatchers,
            readApplyMatchers: ApplyCastMatchers,
            resolveDurationSeconds: ResolveAilmentDurationSeconds,
            log: Log);

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

        // Self-fear bridge — the same local hold while afraid; the tracker reads the
        // fear's echo-less moves through the obvious exits.
        SelfFear = new Game.Conditions.SelfFearMovementGate(Conditions, MovementCoordinator, Log);
        RoomTracker.SetFearProbe(() => Conditions.IsFeared);

        // Self-ailment chip bridge — the pure-chip sibling of the two responders
        // above for poison / blindness / disease (no movement gate). Lights the
        // self party-window chip off ConditionTracker so our own poison shows the
        // same way an other member's announced poison does.
        SelfAilmentChip = new Game.Conditions.SelfAilmentChipResponder(
            Conditions, Party, log: Log);

        // PanicResponder — inbound @panic. When a partymate says the bare "@panic"
        // and PartySettings.IgnorePanics is off, bail the same way our own low-HP
        // emergency would (Health.RespondToReceivedPanic: sys-goto-wimpy if
        // configured, else hang up). The leader-side broadcast is HealthManager's.
        PanicResponse = new Game.Conditions.PanicResponder(
            Chat, PartyState,
            readPartySettings: () => ReadSection<Models.Profile.PartySettings>(Profile.Current, "Party"),
            respond: who => Health.RespondToReceivedPanic(who),
            log: Log);

        // CastingDirector. Sits on top of Cast,
        // decides which heal / cure / buff (if any) to issue based on
        // PlayerState + Spells/Health settings. AutoHeal gates the heal /
        // cure / debuff casts.
        CastDirector = new Game.Spells.CastingDirector(
            PlayerState, Cast, Conditions, PartyState,
            readSpells: () => ReadSection<Models.Profile.SpellsSettings>(Profile.Current, "Spells"),
            readHealth: () => ReadSection<Models.Profile.HealthSettings>(Profile.Current, "Health"),
            readPartySettings: () => ReadSection<Models.Profile.PartySettings>(Profile.Current, "Party"),
            isEnabled: () => ReadAutoModeFlag(d => d.AutoHeal),
            log: Log);
        // Survival casts (heal / cure / buff / party heal) skip any spell the
        // player can't afford — the cost comes from the game-data Spells table
        // via the live spellbook. Combat-tab spells keep their own
        // MinManaPerCast threshold and aren't gated here.
        CastDirector.SetManaCostLookup(Spellbook.ManaCostOf);
        // Combat chooser affordability floor — same cost source, so an attack/drain
        // spell whose slot has MinManaPerCast=0 still won't be cast below its real
        // mana cost (report paradigm-20260820-082741).
        Combat.SetSpellManaCost(Spellbook.ManaCostOf);
        // Auto-Bless auto-engine gate — when off, the Buffing category is
        // suppressed (no Bless / regen / when-full buff fires).
        CastDirector.SetAutoBlessGate(() => ReadAutoModeFlag(d => d.AutoBless));
        CastDirector.SetTriggeredRestGate(() => Health.IsRecoveringRest);
        // In a room whose own spell does damage no rest is started, so the rest-time
        // heal fires standing while an HP rest is owed there.
        CastDirector.SetRestDeferredGate(() => Health.HpRestDeferredByRoomSpell);
        // Mana-rest lock for "cast before resting for mana" slots — held while the
        // mana-recovery gate is asserted (mana below target), durable across a combat
        // interruption, released when mana tops back up.
        CastDirector.SetManaRestGate(() => Health.MaGateAsserted);
        // Buff-strip-room gate — the current room casts a buff-removal spell on
        // entry (RemovesSpell / DispellMagic), so suppress buffs here rather than
        // burn mana on a buff the room tears straight back off.
        CastDirector.SetBuffStripRoomGate(
            () => RoomBuffStrip.StripsBuffs(RoomTracker.State.CurrentRoom?.Spell ?? 0));
        // Token buff-pause — hold buffing while a transport-token use is imminent (its
        // negate magic wipes buffs); TokenTracker opens the window on an outbound token
        // use and closes it on the success line or a 30s timeout.
        CastDirector.SetTokenBuffPauseGate(() => Tokens.IsBuffPausedForToken);
        // Sneak keeping: one rule for every automation that would end a sneak
        // (GAME_MECHANICS "Sneaking — commands, equip order, and the sneak state
        // machine" → "What ends a sneak"). Fighting here with a backstab owed →
        // hold until it fires; sneaking past NPCs we won't fight (auto-combat off, or
        // the room suppressed) → hold until a room with none (a re-sneak won't take
        // with any NPC here), where the action can go out and we re-sneak; our sneaked move in flight → hold until it lands
        // (a command sent then lands in the room we're entering — report
        // paradigm-20260927-121050). A backstab counts as owed only with someone here
        // to open on: the combat engine reports it pending whenever we're sneaking.
        bool FightingHere() => CombatTracker.HasEngageableHostiles && !CombatSuppressedInCurrentRoom();
        SneakGuard = new Game.Stealth.SneakGuard(
            autoSneak:    () => ReadAutoModeFlag(d => d.AutoSneak),
            backstabOwed: () => Combat.IsBackstabRoundUnresolved || (Combat.IsBackstabOpenerPending() && FightingHere()),
            moveInFlight: () => RoomTracker.State.Confidence == Game.Map.RoomConfidence.Pending,
            npcHere:      () => CombatTracker.HasRoomNpc,
            fightingHere: FightingHere,
            inCombat:     () => PlayerState.InCombat,
            stealthed:    () => Stealth?.IsStealthed == true,
            log: Log);
        SneakGuard.SetWireSender(cmd => _engineWireSend?.Invoke(System.Text.Encoding.Latin1.GetBytes(cmd + "\r")));
        Tick.HeartbeatElapsed += SneakGuard.Poll;
        RoomTracker.StateChanged += _ => SneakGuard.Poll();
        Profile.ProfileLoaded += _ => SneakGuard.Reset();
        // In-between spells (heals included, per the user) wait on the same rule;
        // debuffs have their own backstab gate (CombatSpellChooser.WouldBackstab). A
        // flee from the health gates is the exception: its emergency heal goes out,
        // and before the re-sneak (user, 2026-09-28).
        CastDirector.SetStealthMaintenanceDeferGate(() => SneakGuard.Holds);
        CastDirector.SetEmergencyHealBypassProbe(() => Health.IsGateFleeing);
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
        CastDirector.SetBuffDurationSources(BuffInfoByShort, ShortFromAppliedRecord, RemovesShortsFor);
        // A fresh character starts with no buffs assumed — clear any timers carried over
        // (e.g. paused from a prior character's disconnect) so a character switch doesn't
        // resurrect the old character's buffs. A same-character reconnect does NOT reload
        // the profile, so its paused timers survive to be resumed.
        Profile.ProfileLoaded += _ => CastDirector.ResetBuffTracking();
        // Party-buff plan (Party window) — the dynamic list of buff slots the
        // party-bless path casts, read live so a Party-window edit takes effect at once.
        CastDirector.SetPartyBuffSource(() => Profile.Current?.PartyBuffs);
        // A negated member buff is marked on its slot for good (character tier), so
        // the mark is saved with the profile the moment it lands.
        CastDirector.SetSpellNameLookup(code => Spellbook.FindByCastCode(code)?.Name);
        CastDirector.BuffNegated += (_, _) => Profile.Save();
        // Room-presence gate for single-target party buffs: a member is only blessed
        // when they're both in the party AND in the room. Backed by the live
        // room-occupant list (RoomEntityClassifier), matched by given name.
        CastDirector.SetRoomPresenceCheck(IsGivenNameInRoom);
        // A party-wide buff (Spells.Targets = Full / Divided Party Area) is
        // cast once for the whole party; the picker checks this to skip the
        // per-member loop.
        CastDirector.SetPartyWideBuffCheck(IsPartyWideBuff);
        // Self-buff supersession: in a party, a configured party-wide buff that removes a
        // self-buff (RemovesSpell) covers us, so the director stops self-casting the
        // removed one — the Buff Watchdog shows that slot "covered by" the party buff.
        CastDirector.SetSelfBuffCoverage(SelfBuffCoverage);
        // A buff a configured winner PERMANENTLY removes one-directionally (Paradigm
        // continuous removal — e.g. greater bless keeps stripping chant) is never
        // maintained on any target; the Buff Watchdog shows it "covered by" the winner.
        CastDirector.SetSuppressedBuffs(SuppressedBuffCoverage);
        // Stock counterpart: instead of dropping a one-directional loser, order its remover
        // ahead of it so both stay up (removes fire only at cast on stock). Empty on Paradigm.
        CastDirector.SetCollisionOrder(CollisionOrderConstraints);
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
        // Tell CastDirector whether the tick it's handling was fired by a server combat
        // line (the round's burst still landing) vs the 5s timer fallback, so it can wait
        // for HP to settle before picking a between-round cast (reports
        // paradigm-20260904-214056, paradigm-20260928-131549). The settled pass runs off
        // a UI-thread one-shot, same shape as the combat settle schedulers.
        CastDirector.SetCombatTickSource(() => Tick.LastCombatTickWasDamageDriven);
        // A tick the client can't place against the game's rounds (nothing seen for a
        // long while) frees no cast slot.
        CastDirector.SetCombatTickPlacement(() => Tick.LastCombatTickWasPlaced);
        CastDirector.SetSettledPassScheduler((delay, callback) =>
        {
            var timer = new Avalonia.Threading.DispatcherTimer { Interval = delay };
            timer.Tick += (_, _) => { timer.Stop(); callback(); };
            timer.Start();
        });
        Tick.CombatTickElapsed += CastDirector.OnCombatTick;
        // Out of combat the combat tick doesn't free-run (it's only anchored once a
        // combat line lands), so drive the between-round loop off the 1 s heartbeat
        // while idle — buffs/cures then queue up one-per-cooldown from login instead
        // of trickling in on sparse events. In combat OnIdleHeartbeat no-ops (the
        // combat tick owns the cadence), so the combat engine's per-round economy is
        // untouched. This drives ONLY the cast loop, not the whole combat tick, so
        // CombatManager's per-round work never runs out of combat.
        Tick.HeartbeatElapsed += CastDirector.OnIdleHeartbeat;

        // AbilBreakdown parses Paradigm's `abil 145` reply for the mana-regen
        // reroller built below.
        AbilBreakdown = new Game.AbilBreakdownParser(Log);

        // Sysop room dump. The parser is armed by the outbound `sys st` (routed
        // from the main VM's send path); the probe turns it into a request the
        // recovery and sweep engines can await. Gated on the character's per-BBS
        // "Sysop status" power, so an ordinary account never sends one.
        SysRoomStatus = new Game.Map.SysRoomStatusParser(PromptScanner, Log);
        SysStatus = new Game.Map.SysStatusProbe(
            SysRoomStatus,
            capabilityEnabled: SysopStatusEnabledHere,
            log: Log);
        // A fresh character starts with a clean slate — an earlier session's
        // failed probe shouldn't keep the capability off for the next one.
        Profile.ProfileLoaded += _ => SysStatus.ResetAutoDisable();

        // Ground-truth position recovery. The gate asks before it commits to
        // reversing moves, and the resolver asks for itself when the tracker
        // goes Lost — the two cases that otherwise end at the "I am here" map
        // click. Suppressed during a maze solve for the same reason the
        // Paradigm resync is: the solver drives its own relocalization per
        // landing and a second uncoordinated one would race it.
        SysopLocate = new Game.Map.SysopPositionResolver(
            SysStatus, RoomGraph, RoomTracker,
            // With the master switch off no position fix is asked for either.
            suppressed: () => MazeSolver.Active || AutoModeController.KillSwitchEngaged,
            log: Log,
            post: action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        SysopLocate.PositionResolved += Recovery.NoteAuthoritativePosition;
        SysopLocate.LocateFailed += Recovery.OnAuthoritativeResyncFailed;

        // "Sysop god lives": auto-recover the life just spent on the character's
        // own death (gated on the per-BBS power). Rides the raw wire so the command
        // goes out even while dead. Hangs off the canonical RoomTracker.PlayerDeathObserved
        // signal (fired by RoomTracker.NoteDeath, which DeathDetector calls for both
        // death phrasings) — the same one the movement-halt / loop-stop bridges use.
        SysopGodLife = new Game.SysopGodLifeRecovery(
            enabled: () => SysopGodLivesEnabledHere() && !AutoModeController.Blocks("Sysop life recovery"),
            characterName: () => PlayerStats.Name,
            send: cmd => SendGameCommand(cmd),
            log: Log);
        // Not for an arena death, on either realm: it leaves the lives as they
        // were (GAME_MECHANICS "Death threshold & consequences"), and the command
        // would add a life that was never spent.
        RoomTracker.PlayerDeathObserved += () => SysopGodLife.OnDeath(RoomTracker.LastDeathTookNothing);

        // "Sysop goto": gate `sys goto <name>` (per-BBS power + active-combat block +
        // table + level) and, on a fired jump, re-anchor position when the landing
        // room displays. A hostile merely present in the room does NOT block — only
        // active combat does. The fire also forces a bare Enter (a sys-goto shows no
        // room on its own, just a statline) so the landing room displays and the
        // resync can match it. The commit uses RoomTracker.SetLocated (the tier-3 "I am
        // here" hard set), NOT Recovery.NoteAuthoritativePosition — the latter no-ops
        // unless the recovery gate is already awaiting a resync, which a user-fired
        // goto from a normal state isn't. The status write is Posted because a refusal
        // can surface from inside the message pump (re-entering the emulator's Feed).
        SysopGoto = new Game.SysopGotoManager(
            enabled: SysopGotoEnabledHere,
            locations: ActiveBbsSysopGotos,
            inCombat: () => PlayerState.InCombat,
            knownLevel: () => Stats.HasParsed ? PlayerStats.Level : (int?)null,
            roomName: key => RoomGraph.GetRoom(key)?.Name,
            // Raw (gate-piercing) wire for BOTH the `sys goto` command and the bare
            // Enter: sys commands are honoured at any HP, so they must survive the
            // mortally-wounded send-gate hold (the wimpy escape fires while bleeding
            // out). The gated SendGameCommand would drop them at HP <= 0.
            send: cmd => SendGameCommandRaw(cmd),
            forceRoomDisplay: () => _rawWireSend?.Invoke(System.Text.Encoding.Latin1.GetBytes("\r")),
            writeStatus: msg => Avalonia.Threading.Dispatcher.UIThread.Post(() => WriteTerminalNotice(msg)),
            commitLocated: key => RoomTracker.SetLocated(key),
            log: Log);
        // Late-wire HealthManager's "sys goto wimpy instead of hanging" escape now
        // that SysopGoto exists (Health is built earlier). When the emergency low-HP
        // path would hang up, it calls this instead: break combat + jump to the
        // configured escape location. Returns false (→ normal hangup) when the power
        // is off here or the location isn't in the table.
        Health.SetWimpyGoto(name => SysopGoto.TryFireForWimpy(name));
        // The gate asks only from a recovery escalation, where the move being
        // unconfirmed IS the problem — so don't queue behind it.
        Recovery.TrySysopLocate = reason => SysopLocate.TryRequestLocate(reason, forRecovery: true);
        // Mana-regen roll-spell reroll. After each nature-tap / mana-flux landing
        // ManaRegen reads the landed roll (on Paradigm the `spells:` slice of
        // `abil 145`; on Stock off the next natural mana tick) and recasts a
        // below-threshold roll up to the cap, hard-stopping at the buff mana floor.
        // The abil query goes out on the shared engine sender (bound in the main
        // VM); the RECAST is staged on CastDirector so it runs through the same
        // between-round priority pass as every other 0-energy cast — it competes by
        // PriorityBuffing against a due heal/cure and spends the one-cast-per-round
        // slot, rather than firing directly on the wire and bypassing both.
        ManaRegen = new Game.Spells.ManaRegenReroller(
            AbilBreakdown,
            readConfig: () =>
            {
                Models.Profile.BuffSlot? slot = ManaRegenRerollSlot();
                if (slot is not null) ConvertLegacyStockRerollThreshold(slot);
                return new Game.Spells.ManaRegenRerollConfig(
                    slot?.RerollThreshold, slot?.RerollCount ?? 0, slot?.RerollInfinite ?? false);
            },
            sendAbilQuery: () =>
                _engineWireSend?.Invoke(System.Text.Encoding.Latin1.GetBytes("abil 145\r")),
            recast: shortCode => CastDirector.RequestManaRegenReroll(shortCode),
            canAffordReroll: CanAffordManaRegenReroll,
            // Stock has no `abil 145` — read the roll back off the observed natural
            // mana tick instead (fed below from RegenTracker).
            useTickMonitor: () => GameData.ActiveRealm != Game.RealmType.ParaMud,
            log: Log,
            inCombat: () => PlayerState.InCombat,
            stockContext: StockManaRollContext);
        CastDirector.SetSelfBuffCastSink(OnSelfBuffCastForReroll);
        // Resume a reroll cycle suspended at the mana floor once meditation refills the
        // pool — the 1s heartbeat re-checks affordability and fires the next reroll,
        // so it spends the full cap instead of quitting when it ran out mid-cycle.
        Tick.HeartbeatElapsed += ManaRegen.OnRecoveryTick;
        // Feed the reroller every mana uptick (Stock's roll signal). It separates a
        // meditate tick (the unscaled base) from base + natural itself, and skips a
        // tick that filled the pool (cut short).
        Regen.MaTickObserved += sample =>
            ManaRegen.OnManaTickObserved(sample, PlayerState.MaxMa > 0 && PlayerState.Ma >= PlayerState.MaxMa);
        Stats.ScreenParsed += _ => NoteStatScreenGear();

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
        Combat.SetRoomAttackHold(PvpRoom.RoomAttackHeldBy);
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
        Combat.SetBetweenRoundSlotMarker(CastDirector.NotePreAttackDebuffSent);
        Combat.SetBetweenRoundSlotQuery(() => CastDirector.BetweenRoundSlotUsed);
        // A between-round survival cast stops our auto-attack; let the combat
        // engine resume the weapon attack on the resulting *Combat Off*
        // instead of idling until the next round.
        CastDirector.CastFired += Combat.NoteBetweenRoundCast;
        // A cast breaks sneak / hide (GAME_MECHANICS) with no line to latch, so after
        // an out-of-combat auto-cast re-establish sneak in place — StealthManager
        // self-gates on auto-sneak being on, being out of combat, and no NPC present,
        // so this no-ops for a non-stealth character or an in-combat cast.
        CastDirector.CastFired += () => Stealth.ReSneakAfterCast();
        Combat.SetCastBeforeBackstabReannounce(() => CastDirector.Evaluate() is not null);
        // Same resume, but for a HAND-typed cast: a manual cast-code never
        // routes through CastDirector, so sniff the wire for one and arm the
        // identical signal. A cast-code is any Spells.Short in the active
        // class's available list.
        OutboundCast = new Game.Combat.OutboundCastObserver(
            isCastCode: c => Spellbook.FindByCastCode(c) is not null,
            // A hand-typed cast feeds BOTH the combat resume signal and the buff-recast
            // clock: NoteManualBuffCast arms the timer (by cast code) for a hand-cast buff
            // so the Buff Watchdog + recast engine track it the same as an engine cast.
            // A hand cast ends a sneak like an engine one, so it re-sneaks the same way.
            onManualCast: (c, target) =>
            {
                RoundDamage.NoteOwnCast(attack: CombatSpells.IsCombatSpell(c));
                Combat.OnManualCastObserved(c, target);
                CastDirector.NoteManualBuffCast(c, target);
                Stealth.ReSneakAfterCast();
            });
        // Classify a hand-typed cast: a combat spell (round energy 1–1000) is the user
        // taking the round's attack — a user override — while an in-between spell (heal
        // / buff / cure, energy 0) keeps the resume-after-cast. See CombatSpellIndex.
        CombatSpells = new Game.Combat.CombatSpellIndex(GameData);
        Combat.SetCombatSpellPredicate(CombatSpells.IsCombatSpell);
        // A hand-typed PHYSICAL attack (any word of AttackCommandWords) is likewise a
        // user override — the observer forwards every recognised verb and Combat
        // drops its own swing's echo via a one-shot claim. A line that is a text exit
        // of the room we stand in is a move, whatever its first word (`jump pool`).
        OutboundAttack = new Game.Combat.OutboundAttackObserver(
            (verb, target) => Combat.NoteAttackCommandObserved(verb, target),
            isExitCommandHere: IsExitCommandHere);
        // A hand-typed eq / wear / wield / rem mid-fight stops the fight like a cast:
        // arm the re-attack for the *Combat Off* it draws.
        OutboundGear = new Game.Combat.OutboundGearObserver(Combat.NoteTypedGearCommand);
        // The user's own `break` holds the engine's attack on the monster it was
        // fighting until they attack again, it dies or leaves, or we leave the room
        // (user, 2026-10-10). The hold is the combat manager's; what follows is what
        // it needs told from outside.
        OutboundBreak = new Game.Combat.OutboundBreakObserver(
            isAttackSpell: CombatSpells.IsCombatSpell,
            onBreak: Combat.NoteUserBreak,
            onAttack: Combat.NoteUserAttack,
            isExitCommandHere: IsExitCommandHere);
        Combat.UserBreakHoldNotice += text => WriteTerminalNotice($"[{text}]");
        RoomTracker.StateChanged += t =>
        {
            if (t.NewRoom?.Key != t.PreviousRoom?.Key) Combat.NoteRoomLeft(RoomTracker.LastMoveSentAt);
        };
        Profile.ProfileLoaded += _ => Combat.ClearUserBreakHold("another profile was loaded");
        // The room read a held monster's silent round asked for is sent from here
        // when another re-display's cooldown held it back.
        Tick.HeartbeatElapsed += Combat.OnHeartbeat;
        // On Stock a room spell's kill prints *Combat Off* and the spell runs on.
        Combat.SetStockRealmProbe(() => GameData.ActiveRealm != Game.RealmType.ParaMud);
        Tick.CombatTickElapsed += Combat.OnCombatTick;
        // Count attack-spell MaxCasts off Combat's own ConfirmedAttackCastCount —
        // incremented directly off each observed cast-result line — instead of
        // RoundDamageTracker's timer-driven RoundCount. That tracker's 5s window is
        // sized for DPS/session stats, not per-cast precision, and can bundle more
        // than one real cast into a single round for a fast caster, under-counting
        // MaxCasts (report paradigm-20260822-003106). See ReadRoundCount's
        // declaration comment on CombatManager for the full reasoning.
        Combat.ReadRoundCount = () => Combat.ConfirmedAttackCastCount;
        // Third-person-shaped attack-spell casts (e.g. "Spiritual power strikes X for
        // N damage!") need the spell's own caster-message template to confirm — see
        // CombatManager.ResolveAttackSpellMatchers' declaration comment.
        Combat.ResolveAttackSpellMatchers = ResolveAttackSpellMatchersCached;
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
        _itemUseStealth = new Game.Stealth.ItemUseStealthRule(
            HeldItemNames,
            name => Game.Stealth.ItemUseFactsIndex.Lookup(GameData, name),
            debug: line => Log.Debug(Game.Stealth.StealthManager.LogCategory, line));
        Stealth.SetSneakHoldForHeal(() => Health.IsGateFleeing && CastDirector.IsEmergencyHealDue);
        // A buff cast mid-rest doesn't re-sneak unless ShadowRest keeps it through the rest.
        Stealth.SetReSneakSkipForRest(() => (Health.IsRecoveringRest || Health.RestInFlight) && !Health.UsesShadowRest);
        // A cast sneak keeping held on the way stops the walk in the next NPC-free room.
        Stealth.SetHeldCastCheck(() => CastDirector.HasSneakHeldCast);
        // Sneak keeping at the engine send gate: a command that can wait (an invite, a
        // say) is held while SneakGuard keeps the sneak, and any sent command that ends
        // a sneak marks it broken so the next move re-sneaks.
        EngineGate.SetSneakHooks(
            takeForLater: cmd => Game.Stealth.SneakBreakingCommands.CanWait(cmd) && SneakGuard.TakeIfHeld(cmd),
            sent: command =>
            {
                NoteSentForSneak(command);
                NoteSentForRoomCommand(command);
            });
        Stealth.SetAutoToggles(
            isAutoSneakEnabled: () => ReadAutoModeFlag(d => d.AutoSneak),
            isAutoHideEnabled:  () => ReadAutoModeFlag(d => d.AutoHide));
        // Any NPC in the room prevents sneak, so
        // suppress the doomed `sn` instead of firing it into a rejection.
        Stealth.SetSneakBlockCheck(() => CombatTracker.HasRoomNpc);
        Stealth.SetMovementCoordinator(MovementCoordinator);
        // With no walk, loop or auto-lair driving the moves (walking by hand), a
        // broken sneak is re-taken in place; an engine re-takes it at its pre-move hook.
        Stealth.SetEngineDrivingCheck(() => ResolveActiveMovementEngine() is not null);
        // In place, a rest short of rest-max isn't broken for a sneak. A ShadowRest
        // character sneaks over it: there the `sn` leaves the rest standing.
        Stealth.SetIdleRestChecks(
            restUnderWay: () => Health.RestingShortOfRestMax,
            restKeepsSneak: () => Health.UsesShadowRest);
        // A refused move never left the room — Stealth drops its arrival-confirm wait.
        RoomTracker.MoveBlocked += Stealth.NoteMoveBlocked;
        // Auto-hide is suppressed in a party — a hidden member falls off the
        // Also-here line and can't be single-target-healed/buffed until revealed.
        Stealth.SetPartyCheck(() => PartyState.IsInParty);
        // Combat spends stealth (attacking reveals you) but emits no line the FSM
        // can key on, so a room cleared by winning leaves IsSneaking stale-true.
        // Reset it the instant combat ends — before the Combat gate releases the
        // walker — so the pre-move re-sneak re-establishes stealth for the step out.
        CombatTracker.CombatSpentStealth += Stealth.NoteCombatEndedStealthReset;
        // A monster coming in right behind us means it's following: no sn until we
        // shake it (StealthManager "followed").
        RoomEntry.ArrivalObserved += e =>
        {
            if (e.Kind == Game.Combat.EntityKind.Monster) Stealth.NoteMonsterArrival();
        };

        // Backstab window — CombatManager opens with `bs` on the first swing while
        // stealthed: either a sneak-approach into the monster's room, or a monster
        // walking into a room the character is (optimistically) hidden in. Skipped
        // when a seehidden monster is present (which reveals us to the whole room).
        SeeHidden = new Game.Combat.SeeHiddenIndex(GameData);
        Combat.SetBackstabHooks(
            isStealthed:  () => Stealth.IsStealthedHere,
            hasSeeHidden: n => SeeHidden.Has(n));
        Combat.SetSneakBrokeOnEntryProbe(Stealth.TakeSneakBrokeOnEntry);
        // Self-defense stands down only while ACTIVELY walking a plain walk-to (travel):
        // the walker is stepping AND we're neither looping nor Auto-Lairing. Looping and
        // Auto-Lair are farming modes where we want to fight back; a plain destination walk
        // (e.g. an evil character crossing a guarded town) should keep running past
        // attackers. But a walk PAUSED for a rest / hold / wait is stationary and
        // vulnerable — self-defense must re-arm there, so gate on `Walking`, not merely
        // `!= Idle` (a Paused walk was wrongly still counting as travelling, so a monster
        // attacking you mid-rest was ignored).
        Combat.SetSelfDefenseTravelGate(() =>
            Walker.State == Game.Map.WalkState.Walking
            && LoopRunner.State == Game.Map.LoopState.Idle
            && !AutoLair.IsActive);
        // A Hangup-relationship monster is fought back only while no hang-up will
        // come for it, and a Flee one only while no run will.
        Combat.SetNoAnswerComingProbe(relationship => MonsterWatch.NoAnswerComing(relationship));
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
        Combat.SetHitAndRunHooks(Health.BackstabLanded, Health.RunInsteadOfFight);
        Combat.SetFleeInFlightProbe(() => Health.IsFleeInFlight);
        Combat.SetKeepRunning(Health.KeepRunning);
        // A held character's move can't land, so one still unanswered is no reason
        // to hold a fight back.
        Combat.SetMoveInFlightProbe(
            () => RoomTracker.State.Confidence == Game.Map.RoomConfidence.Pending
                && !Conditions.IsMovementPrevented,
            moveSentAt: () => RoomTracker.LastMoveSentAt);
        RoomTracker.MoveBlocked += () => Combat.NoteMoveRefused();

        // ShadowRest (Paradigm): a race or class carrying ability code 1103 can rest
        // while hidden/sneaking in a room with monsters without being attacked, and
        // the rest keeps the stealth. The rest engine relaxes its hostiles guard when
        // solo + stealthed + capable + opted in; combat stands down (reads
        // ShadowRestHolding) so the rest isn't broken, and HealthManager fires
        // ResumeAfterShadowRest at rest-max to re-open with the held-back backstab.
        Health.SetShadowRest(
            shadowRestClass: CharacterHasShadowRest,
            isStealthed:     () => Stealth.IsStealthed,
            isSolo:          () => !PartyState.IsInParty,
            onRecovered:     Combat.ResumeAfterShadowRest);
        Combat.SetShadowRestSuppression(() => Health.ShadowRestHolding);
        // Likewise a monster left standing by the user's own `break`: the quiet is
        // what they asked for, and the watchdog's force-clear would let a walk leave.
        CombatTracker.SetCombatHeldOnPurposeProbe(
            () => Health.ShadowRestHolding || Combat.AttackHeldByUserBreak);
        SneakGuard.SetShadowRestProbe(() => Health.ShadowRestHolding);
        Health.SetSneakKeptProbe(() => SneakGuard.Holds);
        Health.SetMeditateWhilePoisonedProbe(() => GameData.ActiveRealm == Game.RealmType.ParaMud);
        Health.SetSneakBeforeRestProbe(() => Stealth.SneakBeforeRest());
        // The sneak a ShadowRest waits on has answered: rest now. Posted so the
        // answer line finishes its dispatch first.
        Stealth.StateChanged += (prev, _) =>
        {
            if (prev == Game.Stealth.StealthState.AttemptingSneak && Health.RestWaitingOnSneak)
                Avalonia.Threading.Dispatcher.UIThread.Post(Health.Evaluate);
        };

        // Passive-neutral recovery hold: engage a KillOnSight neutral only once we're
        // at/above the rest trigger, so we can rest between kills (a neutral won't
        // attack until we hit it). Never holds when an on-sight attacker is present.
        Combat.SetNeutralRecoveryHold(
            recoveryPending:     () => Health.IsRecoveringRest,
            hasAttackingHostile: () => CombatTracker.HasHostileMonster,
            clearInCombat:       CombatTracker.ClearInCombatForRecoveryHold);
        // Recovery topped off to rest-max (a held rest gate cleared): re-open a held
        // neutral engage AND — while still resting in the room, before the loop's
        // deferred step-out — swap back to the Default set, so the swap streams here
        // and holds the loop via the gear-swap gate instead of landing in the next
        // room mid-combat (report paradigm-20260826-140341). AutoEquip resolves later
        // than this wiring point but the callback reads the property at fire time.
        Health.SetRecoveryCompleteCallback(() =>
        {
            Combat.ResumeAfterRecovery();
            AutoEquip.OnRecoveryComplete();
        });

        // Deterministic magic eligibility — weapon HitMagic ≥ monster Magical
        // picks normal-vs-alternate, spell ReqLevel ≥ monster SpellImmu gates
        // single-target debuff / attack spells, and the resist pair skips an attack
        // spell whose element the target resists ≥ 100%. All fail open when game
        // data is silent.
        MonsterMagic = new Game.Combat.MonsterMagicIndex(GameData);
        MonsterHp = new Game.Combat.MonsterHpIndex(GameData);
        // Running HP estimate per monster in the room: max HP less the damage the round
        // ledger saw, plus regen, pulled into the wound band by every `look`.
        MonsterHpEstimates = new Game.Combat.MonsterHpTracker(
            MonsterHp.MaxHp, MonsterHp.HpRegen, log: Log);
        RoomClassifier.EntitiesObserved += MonsterHpEstimates.NoteRoomEntities;
        // The round ledger numbers same-named monsters off these estimates, and with
        // Settings → Combat "Cap at monster HP" caps a monster's damage taken at the HP
        // it had left.
        RoundDamage.SetMonsterHp(MonsterHpEstimates.TargetOf, MonsterHpEstimates.RoomMonsters,
            capAtHp: () => ReadSection<Models.Profile.CombatSettings>(Profile.Current, "Combat").CapRoundTotalsAtMonsterHp);
        RoundDamage.SetDamageShieldCheck(number =>
            MonsterCatalog.Get(number) is { } monster && monster.Abilities.Any(a => a.Code == 137));
        RoundDamage.Attributed += line =>
        {
            if (line.Foes > 0) MonsterHpEstimates.NoteAreaDamage(line.Sides.Amount);
            else MonsterHpEstimates.NoteDamage(line.Sides);
        };
        ItemMagic = new Game.Combat.ItemMagicIndex(GameData);
        SpellReqLevel = new Game.Combat.SpellReqLevelIndex(GameData);
        MonsterResist = new Game.Combat.MonsterResistIndex(GameData);
        SpellAttackType = new Game.Combat.SpellAttackTypeIndex(GameData);
        Combat.SetMagicEligibility(
            MonsterMagic, ItemMagic, SpellReqLevel, MonsterResist, SpellAttackType);
        MonsterCatalog = new Game.Combat.MonsterCatalog(GameData, RoomGraph.GetRoom);
        // Room tooltips and room panels leave out what the Unobtainable list holds.
        MonsterSpawns.OutOfPlay = MonsterCatalog.IsOutOfPlay;
        // A room spell's damage is no part of a fight, told by the spell on the room
        // we stand in and by which wordings are a monster's attack spell
        // (OffRoundDamageLines). One test for everything that would otherwise take
        // the line for a hit: the round clock (until here TickEngine goes by the
        // wording alone), the in-combat flag, the engine's re-attack and "room
        // appears empty" re-display, and Round Totals.
        Tick.SetOffRoundDamageProbe(IsRoomOrEffectDamage);
        CombatTracker.SetNotCombatLineProbe(IsRoomOrEffectDamage);
        Combat.SetNotCombatLineProbe(IsRoomOrEffectDamage);
        RoundDamage.SetOffRoundDamageCheck(IsRoomOrEffectDamage);
        GameData.ActiveSetChanged += _ => _offRoundDamage = null;
        Messages.Messages.CollectionChanged += (_, _) => _offRoundDamage = null;

        // Drain-life eligibility — a drain spell can only affect a living, non-undead
        // target; the index tells the chooser which mobs to skip (fall back to the
        // normal attack). Fails open when game data is silent.
        MonsterLife = new Game.Combat.MonsterLifeIndex(GameData);
        SpellTargetType = new Game.Combat.SpellTargetTypeIndex(GameData);
        Combat.SetDrainEligibility(MonsterLife, SpellTargetType);

        // Per-monster spell overrides store a Spell.Number; the engine casts the
        // Short. Wire the resolver so the chooser can substitute a numbered
        // override in place of the global Combat-tab cast-code slot.
        SpellShort = new Game.Combat.SpellShortIndex(GameData);
        Combat.SetSpellShortResolver(SpellShort.ShortByNumber);

        // Shared monster-record opener — opens the monster edit dialog by Number from any
        // surface (the Navigation Room Info panel), reusing the browser's read-only "Other
        // Info" assembly (MonsterMdbInfoBuilder). Constructed here so RoomGraph + SpellShort
        // (both above) are ready.
        MonsterRecord = new MonsterRecordDialogService(
            GameData, Resolver, Dialogs, MonsterOverlaySeed, RoomGraph, TBInfo, SpellShort);

        // Shared spell-record opener — opens the spell's Message / Game-Data dialog by Number
        // (the Room Info room-spell link), reusing the Spells tab's message-link flow + the
        // shared SpellInfoRowsBuilder. Messages is built earlier in this method.
        SpellRecord = new SpellRecordDialogService(GameData, Messages, Dialogs);

        // Item-side mirror of SpellRecord — opens an item's on-use / proc message
        // editor from the item dialog's Message section. A casting item delegates to
        // SpellRecord on its CastsSp spell (the shared record), so it takes that here.
        ItemMessage = new ItemMessageDialogService(GameData, Messages, Dialogs, SpellRecord);

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

        // "Kill all engaged" needs the tracker to know when we've committed to the
        // current room (its count met the engage window), so it can hold the walker
        // below the Min floor to finish the survivors — read straight off
        // CombatManager's own room-commitment state.
        CombatTracker.SetRoomCommittedGate(() => Combat.HasCommittedToCurrentRoom);

        // Combat-off "clear hostiles when seen Hidden" override —
        // a stealth runner (AutoSneak on) sprinting a route with combat
        // OFF that hits a SeeHidden room must stop and clear it rather than
        // drag/stack monsters onward. CombatStateTracker owns the latch +
        // holds the walker gate; CombatManager reads the latch to engage
        // despite combat-off.
        CombatTracker.SetSeeHiddenClearGate(
            clearWhenSeenHidden: () =>
            {
                Models.Profile.CombatSettings combat = ReadSection<Models.Profile.CombatSettings>(Profile.Current, "Combat");
                return Game.Combat.CombatStateTracker.SeeHiddenClearApplies(
                    combat.ClearHostilesWhenSeenHidden, combat.SeenHiddenClearWhileSolo,
                    combat.SeenHiddenClearWhileInParty, PartyState.IsInParty);
            },
            isAutoSneakEnabled:  () => ReadAutoModeFlag(d => d.AutoSneak),
            hasSeeHidden:        n => SeeHidden.Has(n));
        // Clear hostiles when sneak fails: a failed sneaked entry into a room inside the
        // Min/Max window latches the same kind of clear. The tracker hears each sneaked
        // arrival, re-runs the room for a silent loss (known only after the display),
        // and forgets the failure once the next move goes out.
        CombatTracker.SetSneakFailClearGate(() => ReadSection<Models.Profile.CombatSettings>(
            Profile.Current, "Combat").ClearHostilesWhenSneakFails);
        Stealth.SneakEntry += CombatTracker.NoteSneakEntry;
        Stealth.SilentSneakLost += CombatTracker.NoteSilentSneakLoss;
        // A see-hidden break is carried from room to room until a fresh `sn` is
        // answered cleanly: only then is the character sneaking again.
        Stealth.StateChanged += (prev, next) =>
        {
            if (prev == Game.Stealth.StealthState.AttemptingSneak && next == Game.Stealth.StealthState.Sneaking)
                CombatTracker.NoteSneakRegained();
        };
        // Both force-clear gates engage with Auto Combat off, so they read the
        // master switch themselves: a latch set before it went off must not fire
        // an attack after.
        Combat.SetSeeHiddenClearGate(() => !AutoModeController.KillSwitchEngaged
            && (CombatTracker.SeeHiddenClearActive || CombatTracker.SneakFailClearActive));

        // Engage-to-clear a rest-blocker with Auto-Combat OFF (report
        // paradigm-20260901-093301): HealthManager owns the decision (it has the
        // rest/flee thresholds + hostile-present), CombatManager engages when it
        // signals — the deadlock where a mob keeps us InCombat so we can't rest, but
        // combat's off so we won't fight, and HP's above the flee trigger so we won't
        // run. HealthManager pokes RequestRestClearEngage to fire the first attack.
        Health.SetRestClearEngage(
            // Effective auto-combat: a loop room the user marked "do not attack"
            // (or a non-lair room under "only attack in lair rooms") reads as OFF
            // here, so a rest triggered in a suppressed room arms the rest-clear
            // and fights to clear it — the do-not-attack rest exception.
            isAutoCombatEnabled: () => ReadAutoModeFlag(d => d.AutoCombat) && !CombatSuppressedInCurrentRoom(),
            requestEngage: Combat.RequestRestClearEngage);
        // The walker and the loop ask for the same clear while a room command that
        // only works in an empty room waits on a monster (AwaitingEmptyRoom on each).
        Combat.SetRestClearGate(() => !AutoModeController.KillSwitchEngaged
            && (Health.ForceClearForRest
                || Walker is { AwaitingEmptyRoom: true } || LoopRunner is { AwaitingEmptyRoom: true }));

        // Break-before-run: turning auto-attack OFF mid-fight releases the Combat
        // gate so the walker resumes — send `break` first when the user has
        // CombatSettings.BreakBeforeFleeing on, mirroring the flee path's disengage.
        CombatTracker.SetBreakBeforeRunGate(
            () => ReadSection<Models.Profile.CombatSettings>(Profile.Current, "Combat").BreakBeforeFleeing);
        // A toggle can land before the server's *Combat Engaged* comes back, so the
        // engine's own target answers "have we swung here?" when InCombat can't yet.
        // Both modes count: a weapon attack sets CurrentTarget, a combat spell sets
        // CastingSpellTarget, and only one is live at a time.
        CombatTracker.SetAttackInFlightGate(
            () => Combat.CurrentTarget is not null || Combat.CastingSpellTarget is not null);
        RoomTracker.StateChanged += t =>
        {
            if (t.PreviousRoom is null || t.NewRoom is null) return;
            if (ReferenceEquals(t.PreviousRoom, t.NewRoom)) return;
            if (t.PreviousRoom.Key.Equals(t.NewRoom.Key)) return;
            Stealth.NoteRoomChanged();
            // An arrival is a chance to hide, taken only if the character is still
            // here and at rest once StealthManager.HideDwell has passed. The other
            // chance, whatever was walking it stopping, is wired with the dwell below.
            Stealth.NoteIdleOpportunity();
        };

        // AutoLightManager. Posts a LightSource need to
        // the registry on a "can't see" room-light line. Gated by the
        // AutoLight master toggle (Settings → General checkbox + the toolbar
        // Toggle button write the same flag; the delegate is queried per
        // dark-room line so toggling takes effect immediately).
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
        // read ItemNames, already loaded above); the record-name check tells a
        // player's "gives you" hand-over from an NPC's flavour line, and the key
        // check sends a handed-over key to the key ring. MarkStale on profile swap
        // so the new character's first gate evaluation waits for a fresh `i`.
        Inventory = new Game.Inventory.InventoryManager(
            Log,
            ItemNames.WeightOf,
            name => ItemNames.WornCodeOf(name) is int worn
                ? Game.Inventory.EquipmentSlotMap.InventorySlotForWornCode(worn)
                : null,
            ItemNames.IsRecordName,
            name => ItemNames.FindByName(name) is int number
                && ItemNames.ItemTypeOf(number) == Game.Inventory.InventoryManager.KeyItemType,
            // A light whose record is Retain After Uses stays in the pack when it
            // burns out; any other is gone.
            staysWhenSpent: name => ItemNames.FindByName(name) is int number
                && Game.Inventory.ItemChargeMeta.Read(GameData, number) is { Recharges: true });
        Profile.ProfileLoaded += _ => Inventory.MarkStale();
        // Something in the pack that takes Stealth to nothing (a log raft) stands
        // Auto-Sneak down until it is gone, instead of `sn` being resent into a
        // refusal in every room.
        CarriedStealth = new Game.Stealth.CarriedStealthPenalty(
            carried: () => Inventory.Snapshot.CarriedItems,
            packStealthOf: ItemNames.PackStealthOf,
            stealthReading: () => PlayerStats.Stealth,
            encumbrancePercent: () => Inventory.Snapshot.Encumbrance is { MaxWeight: > 0 } load
                ? load.CurrentWeight * 100 / load.MaxWeight : 0,
            perfectStealth: () => Profile.Current?.QuestLog?.Any(q =>
                q.Complete && q.Flag == Game.Stealth.SneakChance.PerfectStealthAbility) == true,
            hopelessChance: () => Resolver.Resolve<Models.Profile.OtherSettings>("Other").SneakStandDownChance);
        Stealth.SetCarriedPenaltyCheck(
            () => CarriedStealth.Current() is { Hopeless: true } verdict ? verdict.Items : null,
            msg => Avalonia.Threading.Dispatcher.UIThread.Post(() => WriteTerminalNotice(msg)));
        Stats.ScreenParsed += _ =>
        {
            if (Stats.LastCaptureReadStealth) CarriedStealth.NoteStealthRead();
            Stealth.NoteCarriedChanged();
        };
        Inventory.Changed += () => Stealth.NoteCarriedChanged();
        HpRegenExpected = new Game.HpRegenExpectationSource(PlayerStats, Inventory, GameData,
            () => Game.Quests.CompletedQuestBonuses.Resolve(GameData,
                Game.Quests.CompletedQuestBonuses.ResolveClassId(GameData, PlayerStats.Class), Profile.Current?.QuestLog));
        Regen.SetHpExpectation(() => HpRegenExpected.Current);
        Profile.ProfileLoaded += _ => HpRegenExpected.Invalidate();
        TooHeavyWait = new Game.TooHeavyWaitSignal(Router, Inventory, PartyRest, MovementCoordinator, Log);
        // A spell that cuts carrying capacity (weakness, frail) can leave us over our
        // max; `i` is the only place that shows. Records sharing one applied line each
        // raise the event, and the signal reads once.
        Game.GameData.EncumbranceDebuffIndex capacityDebuffs = new(GameData);
        Conditions.ConditionApplied += rec =>
        {
            if (capacityDebuffs.LowersMaxEncumbrance(SpellNumberOf(rec))) TooHeavyWait.NoteCapacityDebuffApplied();
        };
        Conditions.ConditionEnded += rec =>
        {
            if (capacityDebuffs.LowersMaxEncumbrance(SpellNumberOf(rec))) TooHeavyWait.NoteCapacityDebuffEnded();
        };
        // The Stock mana-regen reroll skips a tick that lands mid gear-set swap.
        Inventory.Changed += NoteWornChange;

        // Equipment-driven max HP/mana pool sync. A worn item can carry a flat
        // pool bonus (Items.Abil 88 = +Max HP, Abil 69 = +Max Mana — e.g. the
        // severed head of Goru-Nezar's +50 mana); PromptParser's high-water
        // ratchet and periodic stat-screen resync don't react to that changing
        // mid-session, so equip/remove could leave the health engine's rest and
        // "pool is full" checks reading a stale ceiling. Reused
        // CharacterCalculator.AggregateEquipmentStats (already the Character
        // Info tab's live worn-set bonus reader) resolves the current total;
        // reseeded (no delta applied) on profile load / active game-data set
        // change so a character or realm swap doesn't diff against a
        // now-meaningless prior total.
        var equipmentMaxSync = new Game.Health.EquipmentMaxPoolSync(
            equipped =>
            {
                Game.Calculators.EquipmentStatSummary totals =
                    Game.Calculators.CharacterCalculator.AggregateEquipmentStats(equipped, GameData).Totals;
                return (totals.PlusMaxHp, totals.PlusMaxMana);
            },
            Player.ApplyEquipmentMaxDelta);
        Inventory.Changed += () =>
        {
            if (Inventory.IsLoaded) equipmentMaxSync.OnEquippedItemsChanged(Inventory.Snapshot.EquippedItems);
        };
        Profile.ProfileLoaded += _ => equipmentMaxSync.Reset();
        GameData.ActiveSetChanged += _ => equipmentMaxSync.Reset();

        // Death-recovery deathpile capture. RoomTracker.NoteDeath
        // records the worn + carried items from the last-known `i` snapshot
        // onto the death record; DeathRecoveryManager.SimulateDeath captures
        // the same way for the test button.
        RoomTracker.AttachInventorySnapshot(() => Inventory.Snapshot);
        DeathRecovery.AttachInventorySnapshot(() => Inventory.Snapshot);
        // Which landing an item-gated cast-on-walk teleport takes (the golden idol's
        // passage in the Earthen Catacombs). Unknown until an inventory has parsed.
        RoomTracker.SetItemHeldProbe(itemId => Inventory.IsLoaded ? IsItemCarried(itemId) : null);

        // CombatSessionTracker: our own Session Stats figures, off RoundDamage's
        // ledger plus the swing / miss / dodge patterns. Its spell matchers refresh on
        // the boundaries that move them: connect / char switch (ProfileLoaded, which
        // also zeroes the session in lockstep with RoundDamage), a Combat-tab edit
        // (ProfileMutated), a game-data set swap, and a spellbook change.
        CombatSession = new Game.Combat.CombatSessionTracker(Router, RoundDamage, OwnSpellMatchers, IsPartyMemberOrSelfName);
        RoundDamage.SetOwnSpellLineCheck(CombatSession.MatchesOwnSpell, CombatSession.MatchesOwnRoomSpell);
        RoundDamage.SetOwnSpellRepeating(() => Combat.IsRepeatingSpell);
        Combat.BackstabResolved += CombatSession.OnBackstabResolved;
        Profile.ProfileLoaded  += _ => { CombatSession.Reset(); CombatSession.RefreshMatchers(); _attackSpellMatcherCache.Clear(); };
        Profile.ProfileMutated += _ => { CombatSession.RefreshMatchers(); _attackSpellMatcherCache.Clear(); };
        GameData.ActiveSetChanged += _ => { _ownSpellLineCache.Clear(); CombatSession.RefreshMatchers(); _attackSpellMatcherCache.Clear(); };
        Spellbook.Changed += CombatSession.RefreshMatchers;

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
        // Entering a rest posture confirms the room is genuinely cleared of hostiles (a
        // rest only starts once nothing is left to fight), so the AoE area-debuff room
        // tags reset — a same-room respawn after this is debuffed afresh (report
        // paradigm-20260903-070438), without disturbing the mid-fight survivor case.
        Game.PlayerPosition lastPosForAoe = PlayerState.Position;
        PlayerState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(Game.PlayerState.Position)) return;
            Game.PlayerPosition pos = PlayerState.Position;
            bool wasResting = lastPosForAoe is Game.PlayerPosition.Resting or Game.PlayerPosition.Meditating;
            bool nowResting = pos is Game.PlayerPosition.Resting or Game.PlayerPosition.Meditating;
            lastPosForAoe = pos;
            if (nowResting && !wasResting) Combat.NoteRoomClearedByRest();
        };
        Conditions.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(Game.Conditions.ConditionTracker.ActiveFlags)) return;
            TimeAnalysis.NoteAfflictions(
                Conditions.IsBlinded, Conditions.IsPoisoned, Conditions.IsDiseased,
                Conditions.IsConfused, Conditions.IsMovementPrevented);
            HiddenSearch.OnBlindnessChanged();
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
        // Release the trainer/creation form's character-mode the instant the
        // returning statline prompt lands off the wire — the committed StatusLine
        // pattern can't see it (it redraws in place with no CR until the user
        // types), so without this the keyboard stays captured and the next
        // command is sent byte-by-byte (report paradigm-20260906-090057).
        PromptScanner.PromptObserved += _ => TrainerMenu.NotifyLivePromptObserved();
        // A bug report copies the terminal only from the time spent in the game, so a
        // login screen never rides along in one.
        InGameCapture = new Game.InGameCapture(Router, PromptScanner, Wire, Log);
        // The sent-command trail keeps nothing typed outside the game (a login is)
        // and nothing that may be a suicide password, typed or sent by @suicide.
        EngineGate.Trail = SentCommands;
        SentCommands.Withhold = () =>
            !InGameCapture.InGame || SuicidePassword.PasswordEntryLikely || Suicide.SendingPassword;
        // A line typed at the terminal is the user's own send. The chat log is told
        // of it from the keys typed, never from the row the board shows back: it
        // pairs a telepath with its confirmation, and remembers whose send it was.
        SentCommands.TypedLineEntered = line =>
            Chat.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes(line + "\r"), usersOwn: true);
        // Out to the board's menu with the link still up: nothing automatic may be
        // sent (it would be a menu selection), a typed selection isn't a move, the
        // menu's lines aren't unknown game lines, and its prompt isn't a broken
        // statline to repair. Re-arming the statline check makes it wait for the
        // next room display, as it does at login.
        OutboundMovement.AtBoardMenu = () => InGameCapture.AtBoardMenu;
        InGameCapture.AtBoardMenuChanged += atMenu =>
        {
            if (!atMenu)
            {
                EngineGate.Release(BoardMenuHold);
                // The room is displayed ahead of the first prompt on the way back
                // in: a Hangup or Flee monster read off it while still "at the
                // menu" is answered now.
                MonsterWatch.NoteBackInGame();
                return;
            }
            EngineGate.Hold(BoardMenuHold);
            MessageCandidateWatcher.NotifyLeftForMenu();
            StatlineReconcile.Arm();
            // Whoever was in the room is not known to be there on the way back in.
            RoomClassifier.NoteGameLeft();
        };
        // Same in-game gate arms unrecognized-line capture: nothing before the first
        // realm prompt (splash / login menu / connect banner) stages a candidate.
        PromptScanner.PromptObserved += _ => MessageCandidateWatcher.NotifyInGame();
        // Same in-game gate lifts the cast pass's out-of-the-game latch and lets its
        // buff timers run on: they stood still from the moment the game was left, and
        // each expiry is moved on by the time spent away, so every buff has what it
        // had left. Asked on every prompt; a no-op unless they were paused.
        PromptScanner.PromptObserved += _ => CastDirector.ResumeBuffTimers();
        // A fresh character starts disarmed: zero the counters, then Suspend so
        // accrual waits for that character's first in-game prompt. (Disconnect
        // disarms via MainWindowVM; @reset / the window button keep counting.)
        Profile.ProfileLoaded += _ => { TimeAnalysis.Reset(); TimeAnalysis.Suspend(); };

        // SessionActivityTracker. Counts kills + experience and keeps
        // the rolling kill history for the kills/hour sparkline. Like the other
        // session-stats trackers it owns no subscriptions: a kill arrives from
        // MonsterDeath and experience from the gain line. Reset on the same
        // session boundary.
        SessionActivity = new Game.Combat.SessionActivityTracker();
        MonsterDeath.MonsterDied += _ => SessionActivity.NoteKill();
        Router.Subscribe(Services.Patterns.KnownPatterns.UserGainExperience, m =>
        {
            if (m.Groups.Count > 0 && int.TryParse(m.Groups[0], out int exp))
                SessionActivity.NoteExperience(exp);
        });
        Profile.ProfileLoaded += _ => SessionActivity.Reset();
        // The rest of the Session Statistics inputs: rooms entered while sneaking
        // (held or broke), disarm trap attempts, items picked up, and shop sales (items + proceeds). Walk
        // steps are wired with the loop runner below; stash hides and bank deposits
        // with their engines.
        Stealth.SneakEntry += held => SessionActivity.NoteSneakEntry(held);
        TrapDisarm.DisarmAttempted += SessionActivity.NoteDisarmAttempt;
        Inventory.ItemTaken += (_, count) => SessionActivity.NoteItemsCollected(count);
        Inventory.ItemSold += (name, count, copper) =>
        {
            SessionActivity.NoteSale(count, copper);
            TransactionHistory.NoteSale(name, count, copper, CurrentRoomLabel());
            Log.Debug("SessionStats", $"sale counted: {count} x {name} for {copper} copper");
        };
        Inventory.ItemBought += (name, count, copper) =>
            TransactionHistory.NotePurchase(name, count, copper, CurrentRoomLabel());

        // HpMaHistoryTracker. Accumulates per-loop-step min/max HP + mana for the
        // Session Stats "HP/MA History" band graph. Its inputs need LoopRunner
        // (built later in the movement layer) to gate sampling and supply the step
        // index, so the prompt-scanner subscription + loop-start reset are wired
        // in the LoopRunner block below; here we just construct it and clear on the
        // connect / character-switch boundary like the other session trackers.
        HpMaHistory = new Game.Combat.HpMaHistoryTracker();
        Profile.ProfileLoaded += _ => HpMaHistory.Reset();

        // TransactionHistory. A per-session ledger of bank deposits, stash-room
        // hides and withdrawals, and shop sales / purchases, fed from the server's
        // echoes. Feeds the
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
            RemoteCommands, PlayerStats, SessionActivity, GameData);

        // Per-realm runic-currency naming. Reads the active realm's RunicCurrencyName
        // live (via ResolveActiveRealm) and re-reads on profile / realm swap. Injected
        // into every cash parser / command builder so a board-renamed runic word
        // is matched on the wire and sent back on outgoing get/drop/hide commands.
        Currency = new Game.Cash.CurrencyNaming(() => ResolveActiveRealm()?.Realm.RunicCurrencyName);
        // Program Log → "Log session statistics". The block is built when one is
        // due, so the trackers it reads only have to exist by then.
        SessionStatsLog = new SessionStatsLog(LogDiagnostics, () => InGameCapture.InGame, SessionStatsBlock, Log);
        InGameCapture.InGameChanged += SessionStatsLog.NoteInGameChanged;
        Profile.ProfileLoaded += _ => Currency.Refresh();
        Profile.BbsPinApplied += _ => Currency.Refresh();

        // Room-floor loot snapshot from the "You notice <list> here." survey,
        // cash filtered out. Feeds @what (read) and @get-all (get each).
        // LineExtractor attached + OnRoomChanged wired below (and in MainWindowVM).
        // isKnownItem gives the cash filter an authoritative item-table
        // tiebreaker so a stacked denomination-named item ("2 gold key") isn't
        // mistaken for coin (see IsCashEntry).
        GroundItems = new Game.Inventory.GroundItemTracker(Router, Currency,
            isKnownItem: IsKnownGroundItem,
            isRoomName: text => RoomGraph.FindByName(text).Count > 0);
        // Auto-recover reads the floor survey to confirm our corpse is in the room
        // before sending `recover corpse` (and arms off its SurveyUpdated event).
        DeathRecovery.AttachGroundItems(GroundItems);
        // Demand-aware floor collector: `get` a still-needed path item the moment a
        // survey reveals it (search-en-route sourcing), independent of Auto-Get.
        PathItemFloor = new Game.Map.PathItemFloorCollector(
            Needs, IsItemOnFloor, ItemNames.GetName, cmd => SendGameCommand(cmd), Log);
        PathItemFloor.Attach(GroundItems);
        // Realm picks the recovery mechanic: Paradigm packs the pile into a corpse
        // (`recover corpse`), Stock scatters it loose on the floor (per-item `get`).
        DeathRecovery.SetRealmProbe(() => GameData.ActiveRealm == Game.RealmType.ParaMud);
        DeathRecovery.SetGameDataInstalledProbe(set =>
            GameData.AvailableSets.Contains(set, StringComparer.OrdinalIgnoreCase));
        // Match our own corpse by the LIVE in-game name, not a copied profile's stale
        // Current.Name (report stock-20260828-104653).
        DeathRecovery.AttachLiveSelfName(() => Party.LocalCharacterName);

        // Read-only inventory queries — @wealth / @enc / @have report off the
        // InventoryManager snapshot; @what reports the GroundItems survey. No
        // wire output either.
        InventoryQuery = new Game.Remote.InventoryQueryHandler(RemoteCommands, Inventory, GroundItems, Currency);

        // Paradigm transport-token daily-charge tracking. Reads each held token's
        // "Uses remaining: N" via `look` (on login + after a `use`), Paradigm-only.
        // The sender is SendGameCommand, as QuestFlagProbe's is; the outbound tap +
        // line feed are wired in MainWindowViewModel. Cleared on profile / set swap so a new
        // character re-reads from scratch.
        Tokens = new Game.Tokens.TokenTracker(
            send: cmd => SendGameCommand(cmd),
            carried: () => Inventory.Snapshot.CarriedItems,
            onParadigm: () => GameData.ActiveRealm == Game.RealmType.ParaMud,
            // Recognise the token use lines through the seeded token spell messages —
            // the wording lives in the Messages catalogue, not a second hardcoded regex.
            matchSelfUse: MatchTokenSelfUse,
            matchMemberDeparted: MatchTokenMemberDeparted,
            // Safety-release timer for the pre-token buff-pause window.
            schedule: (ms, action) =>
            {
                var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
                timer.Tick += (_, _) => { timer.Stop(); action(); };
                timer.Start();
            },
            log: Log);
        Profile.ProfileLoaded += _ => Tokens.Clear();
        // A set swap changes which tokens exist, where they teleport, and their spell
        // messages — clear the charge tracker AND drop the cached teleport map + use
        // matchers so they re-read the new set.
        GameData.ActiveSetChanged += _ => { Tokens.Clear(); _tokenTeleports = null; _tokenUseMatchers = null; };
        // @token <name> — read-only remaining-charges report off the tracker.
        TokenQuery = new Game.Remote.TokenQueryHandler(RemoteCommands, Tokens);

        // Paradigm limited-use item charges from look replies ("Uses remaining: N"),
        // persisted per-character (CharacterProfile.ItemCharges): auto-looks an unknown
        // charged item, then counts each use down on the item's own use line (a look
        // only when a use can't be confirmed). Rechargeables restock
        // at the BBS cleanup time. The look sender is SendGameCommand (rides the same
        // outbound tap so its looks re-arm capture); the line feed + outbound tap are
        // wired in MainWindowViewModel alongside Tokens.
        ItemCharges = new Game.Inventory.ItemChargeTracker(
            gameData: GameData,
            profile: Profile,
            heldItems: HeldItemNames,
            itemNumberOf: ItemNumberByName,
            onParadigm: () => GameData.ActiveRealm == Game.RealmType.ParaMud,
            cleanupConfig: ResolveBossCleanupConfig,
            useConfirmLine: BuildItemUseLinePredicate,
            sendLook: cmd => SendGameCommand(cmd),
            schedule: (ms, action) =>
            {
                var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
                timer.Tick += (_, _) => { timer.Stop(); action(); };
                timer.Start();
            },
            log: Log);
        Profile.ProfileLoaded += _ => ItemCharges.ResetSession();

        // Stock use-counting for limited-use items (persisted on the profile; rechargeables
        // restock at the BBS cleanup time — reuses the boss-timer cleanup config). Only
        // SUCCESSFUL uses count: a use is confirmed by the item's use-spell caster message
        // (from its game-data on-use record), so a bonked/blocked use burns nothing. The
        // line feed is attached in MainWindowViewModel alongside ItemCharges.
        ItemUseCounts = new Game.Inventory.ItemUseCountTracker(
            gameData: GameData,
            heldItems: HeldItemNames,
            itemNumberOf: ItemNumberByName,
            onStock: () => GameData.ActiveRealm != Game.RealmType.ParaMud,
            cleanupConfig: ResolveBossCleanupConfig,
            profile: Profile,
            useConfirmLine: BuildItemUseLinePredicate,
            schedule: (ms, action) =>
            {
                var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
                timer.Tick += (_, _) => { timer.Stop(); action(); };
                timer.Start();
            },
            log: Log);

        ChestOpens = new Game.Inventory.ChestOpenTracker(
            Inventory, Profile, OutboundOpen,
            isContainer: name => ItemNames.FindByName(name) is int n
                && ItemNames.ItemTypeOf(n) == Game.Inventory.ChestOffloadPlanner.ContainerItemType,
            send: cmd => SendGameCommand(cmd),
            schedule: (ms, action) =>
            {
                var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
                timer.Tick += (_, _) => { timer.Stop(); action(); };
                timer.Start();
            },
            post: action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            runicName: () => Currency.RunicName,
            log: Log);

        // One realm-aware charge lookup shared by Character Info and @uses.
        CarriedCharges = new Game.Inventory.CarriedChargeReadout(
            GameData, ItemCharges, ItemUseCounts, ItemNumberByName, HeldItemNames);

        // Fill in charges for any carried charged item we don't know yet, whenever the
        // carry list changes (Paradigm-only inside the tracker). Idempotent + paced.
        Inventory.Changed += () => ItemCharges.EnsureChargesKnown();

        // @uses [item] — remaining charges of a carried limited-use item; bare lists all.
        ItemUsesQuery = new Game.Remote.ItemUsesQueryHandler(RemoteCommands, CarriedCharges);

        // @timer — read-only report of the boss respawn timers being tracked. Reads
        // the boss catalog + persisted kill-times; no wire output beyond its reply.
        BossTimerQuery = new Game.Remote.BossTimerQueryHandler(RemoteCommands, Bosses, BossTimers, GameData, Log);
        DeathQuery = new Game.Remote.DeathQueryHandler(RemoteCommands, () => DeathRecovery.Records,
            r => DeathRecovery.OtherGameDataRefusal(r) is not null);
        // Paced-send scheduler: a UI-thread one-shot (same shape as the combat
        // switch-dispatch delay) so an @roomba sync / @loop send trickles its lines
        // out ~800ms apart instead of flooding the channel.
        Action<TimeSpan, Action> pacedReplyScheduler = (delay, callback) =>
        {
            if (delay <= TimeSpan.Zero) { Avalonia.Threading.Dispatcher.UIThread.Post(callback); return; }
            var timer = new Avalonia.Threading.DispatcherTimer { Interval = delay };
            timer.Tick += (_, _) => { timer.Stop(); callback(); };
            timer.Start();
        };
        RoombaQuery = new Game.Remote.RoombaQueryHandler(RemoteCommands, GhItemLocations, GhRoomLabels, Log,
            paceScheduler: pacedReplyScheduler);
        // Adopt an @roomba sync reply only inside the window our own outbound
        // `@roomba sync` opens (NoteSyncRequested, wired from the outbound-chat
        // watcher in MainWindowViewModel). The permission gate is on the responder
        // side, so a reply arriving already proves we're authorized.
        RoombaSync = new Game.Remote.RoombaSyncReceiver(Chat, GhItemLocations, GhRoomLabels, Log);

        // Rate-limit clobber watcher: the game drops a command when we type too
        // fast — stock says "You are typing too quickly - command ignored",
        // paradigm "Too many messages sent - please wait …". Either one during a
        // paced @roomba sync / @loop send means the last telepath was lost, so poke
        // the senders to back off and resend it (no-op when nothing is draining).
        Router.LineDispatched += line =>
        {
            string t = line.Text;
            if (t.Contains("typing too quickly", StringComparison.OrdinalIgnoreCase)
                || t.Contains("Too many messages sent", StringComparison.OrdinalIgnoreCase))
            {
                RoombaQuery.NoteRateLimitClobber();
                LoopShare?.NoteRateLimitClobber();   // built later in this constructor
                InventoryAction?.NoteRateLimited();  // built just below
            }
        };

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
            naming: Currency,
            isParadigm: () => GameData.ActiveRealm == Game.RealmType.ParaMud,
            cannotDrop: GameRefusesToDrop,
            isCursed: name => Game.Inventory.ItemDropRule.IsCursed(ItemAbilityCodes(name)),
            scheduleAfter: (delay, action) =>
            {
                var timer = new Avalonia.Threading.DispatcherTimer { Interval = delay };
                timer.Tick += (_, _) => { timer.Stop(); action(); };
                timer.Start();
            },
            log: Log)
        {
            // @deposit-all answers "depositing" only where a deposit can start. The
            // tracker is built later; a room it can't place or isn't sure of is
            // unknown, not "no bank" (BankCatalog.StandingInBank).
            InBank = () => RoomTracker?.State is { } at
                ? Game.GameData.BankCatalog.StandingInBank(GameData, at.CurrentRoom?.Key, at.Confidence)
                : null,
        };
        PromptScanner.PromptObserved += _ => InventoryAction.NotePrompt();

        // @get-stash: a leader's stash transfer has us search and carry a load too.
        // Cash is built further down; these only run when a command arrives.
        GetStash = new Game.Remote.GetStashHandler(
            RemoteCommands,
            onHandCopper: () => Inventory.Snapshot.Currency.TotalCopperValue,
            send: cmd => SendGameCommand(cmd),
            armTimer: (delay, action) => _ = System.Threading.Tasks.Task.Delay(delay)
                .ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(action),
                    System.Threading.Tasks.TaskScheduler.Default),
            limitCollection: copper => Cash.SetCollectLimit(copper),
            surveyedCopper: () => Cash.SurveyedCopperUnderLimit,
            collectSurveyed: copper => Cash.CollectSurveyed(copper),
            forceAutoGetCash: on => _autoGetCashOverride = on ? true : null,
            collectionInUse: () => Cash.CollectLimitCopper is not null || _autoGetCashOverride == true,
            log: Log);

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
            // further down; this lambda reads it at fire time). See NoteItemCastSwap.
            onSwap: () => AutoEquip?.NoteItemCastSwap(),
            // A "(Worn)"-bucketed item can still occupy the off-hand mechanically
            // (Items.Worn == Off-Hand); OffHandNames is built straight from every
            // Items.json row (not the collision-prone by-name index), so it answers
            // correctly even for a display name shared with a non-wearable item.
            isOffHandItem: name => ItemNames.OffHandNames.Contains(name, StringComparer.OrdinalIgnoreCase),
            // A two-handed wielded weapon fills both hands, so an off-hand buff item
            // can't be equipped until it's removed — the reverse of a two-handed cast
            // item. Same game-data 2H check the combat weapon-swap uses.
            isWornWeaponTwoHanded: IsConfiguredWeaponTwoHanded,
            // Defer the whole sequence until a full 'i' is parsed this session, so it
            // never fires against an empty / stale snapshot on login or reconnect
            // (report paradigm-20260826-150242). Same signal AutoEquip gates on.
            wornLoadoutKnown: () => Inventory.IsLoaded);
        CastDirector.SetItemCastSource(ItemCastDurationOf, ItemCast.Execute);
        CastDirector.SetItemCastManaCost(ItemCastManaCostOf);
        CastDirector.SetItemDrawSource(ItemDrawOutcomesOf, ItemDrawCanRedraw);

        // Auto-train. Drives the `train stats` screen to apply the CP
        // plan (Workshop CP Allocation tab) when armed + a level-up enables it.
        // Needs Inventory (raw-base = live - gear) + TrainerMenu (screen enter/
        // exit gating, already wired to char-mode). Wire-sender bound in
        // MainWindowViewModel.
        ListedEffects = new Game.Spells.ListedEffectCatalog(Messages, GameData);
        AutoTrain = new Game.AutoTrainManager(PlayerStats, Stats, GameData, Inventory, Profile, TrainerMenu,
            ListedEffects, Router, Log);

        // EquipmentManager + the @equip <set> handler. The engine
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
            restrictsEquip: IsEquipRestricted,
            log: Log);
        // Automatic gear swaps wait while a sneak is kept (SneakGuard) and re-run after.
        Equipment.SetGearHold(() => SneakGuard.Holds);
        SneakGuard.Released += Equipment.RunHeldGear;
        Profile.ProfileLoaded += _ => Equipment.DropHeldGear();
        // Realm picks which physical slot a full paired-family eq/wear evicts —
        // Paradigm slot 1 (first-listed), Stock slot 2 — so the swap builder rems
        // the right odd-out (see EquipmentManager.ComposePairedSlotCommands).
        Equipment.SetRealmProbe(() => GameData.ActiveRealm == Game.RealmType.ParaMud);
        // @equip <set> update rewrites a set on the character profile — persist it.
        Equipment.SetEquipmentSaver(() => Profile.Save());
        // A gear swap never takes off the worn counter to a hazard of the room we
        // are in, or of the next room over.
        Equipment.SetRoomProtectionProbe(WornRoomHazardCounters);
        EquipRemote = new Game.Remote.EquipHandler(RemoteCommands, Equipment);

        // Location-based auto-equip: re-evaluated on every room transition (fires
        // regardless of who drove the move), so the walker / loop / Auto-Lair all
        // honour it. Drops ownership on a profile swap.
        LocationEquip = new Game.Inventory.LocationEquipManager(
            readOther: () => Resolver.Resolve<Models.Profile.OtherSettings>("Other"),
            equipment: Equipment,
            log: Log);
        RoomTracker.StateChanged += t => LocationEquip.OnRoomChanged(t.NewRoom);
        Profile.ProfileLoaded += _ => LocationEquip.OnProfileSwapped();

        // Room-spell counters: an item that negates a room's spell only works worn
        // (GAME_MECHANICS "Room-spell hazard shape 1 — direct damage, negated by an
        // item's `NegateSpell-N`"), so it goes on before the
        // step into such a room and the slot goes back to the gear sets afterwards.
        // After the location rules on a room change, so a rule that wears the same
        // piece has claimed it first and nothing is sent twice.
        CounterWear = new Game.Inventory.RoomSpellCounterWear(
            equipment: Equipment,
            coordinator: MovementCoordinator,
            enabled: () => ReadSection<Models.Profile.PeriodicDamageRoomSpellSettings>(
                Profile.Current, Models.Profile.PeriodicDamageRoomSpellSettings.TabKey).WearCounterBeforeEntering,
            roomOf: key => RoomGraph.GetRoom(key),
            // Only a room the tracker is sure of: a pending move still shows the
            // room being left, and a suspect reading may not be where we are.
            currentRoom: () => RoomTracker.State.Confidence == Game.Map.RoomConfidence.Confirmed
                ? RoomTracker.State.CurrentRoom?.Key
                : null,
            negatorsOf: spell => RoomHazards.HazardForSpell(spell)?.NegatingItems ?? Array.Empty<int>(),
            describe: DescribeCounterItem,
            isWorn: IsItemWorn,
            isCarried: IsItemCarried,
            wornIn: WornPiecesIn,
            spellName: SpellCatalog.GetSpellNameByNumber,
            // Sneaking past things it won't fight: the one case a gear command waits
            // for a room with no NPC in it (the test SneakGuard holds a command on).
            sneakingPast: () => ReadAutoModeFlag(d => d.AutoSneak) && !ReadAutoModeFlag(d => d.AutoCombat),
            roomEmpty: () => !CombatTracker.HasRoomNpc,
            plannedAhead: PlannedRoomsAhead,
            schedule: (delay, action) => ScheduleOnce(delay, action),
            // A wear mid-fight draws *Combat Off*; the engine picks the fight up again.
            gearCommandSent: () => Combat.NoteGearSwapInterrupt(),
            log: Log);
        // A wear the locked send gate would drop is not claimed and not waited for.
        Equipment.SetSendGateProbe(() => EngineGate.IsLocked);
        CounterWear.UsabilityChanged += () => _negatorUsable.Clear();
        // Confirmed rooms only, as the gear sets' own room hook has it: a restore
        // acted on a predicted or doubtful room could take the counter off in lava.
        RoomTracker.StateChanged += t =>
        {
            if (t.NewConfidence == Game.Map.RoomConfidence.Confirmed && t.NewRoom is { } arrived)
                CounterWear.OnArrived(arrived.Key);
        };
        Profile.ProfileLoaded += _ => CounterWear.Reset();
        // Standing in such a room already: the counter goes on when it turns up in
        // the pack, when the pack is first read, and when commands held back (a
        // password prompt, the trainer form) go out again. A restore that waited
        // for a kept sneak is decided again when the sneak ends.
        Inventory.Changed += () =>
        {
            _negatorUsable.Clear();
            CounterWear.Recheck();
        };
        Inventory.FullInventoryParsed += CounterWear.NoteInventoryRead;
        EngineGate.Released += CounterWear.Recheck;
        SneakGuard.Released += CounterWear.Recheck;
        // Waiting for the room to empty (sneaking past things): asked again as
        // the roster changes, which has no event of its own.
        Tick.HeartbeatElapsed += CounterWear.Poll;
        // What the game refused may be wearable on another level or alignment.
        Stats.ScreenParsed += _ => CounterWear.LiftRefusals("the stat screen was read");
        // The tracker raises Refreshed on every read; the counter lifts on a change.
        Alignment.Refreshed += () => CounterWear.NoteAlignmentRead(Alignment.SelfAlignment);
        PlayerStats.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Game.PlayerStats.Level)) CounterWear.LiftRefusals("the level changed");
        };

        // Combat profiles: a full-posture quick-swap. A switch overlays the profile's
        // spell/verb/room fields onto the live Combat section the engine re-reads each
        // round, writes the profile's whole Health section, and writes its weapons into
        // the Workshop Default gear set (the surface EquipmentWeaponSync +
        // AutoEquipCoordinator already read). Seeded per character (first profile
        // captured from the current combat + health settings and the Default-set
        // weapons) on every ProfileLoaded.
        CombatProfiles = new Game.Combat.CombatProfileManager(
            profile: () => Profile.Current,
            readCombat: () => ReadSection<Models.Profile.CombatSettings>(Profile.Current, "Combat"),
            writeCombat: combat =>
            {
                if (Profile.Current is not { } p) return;
                p.Settings ??= new();
                p.Settings["Combat"] = System.Text.Json.JsonSerializer.SerializeToElement(combat);
                Profile.Save();
            },
            readHealth: () => ReadSection<Models.Profile.HealthSettings>(Profile.Current, "Health"),
            writeHealth: health =>
            {
                if (Profile.Current is not { } p) return;
                p.Settings ??= new();
                p.Settings["Health"] = System.Text.Json.JsonSerializer.SerializeToElement(health);
                Profile.Save();
            },
            readSpells: () => ReadSection<Models.Profile.SpellsSettings>(Profile.Current, "Spells"),
            writeSpells: spells =>
            {
                if (Profile.Current is not { } p) return;
                p.Settings ??= new();
                p.Settings["Spells"] = System.Text.Json.JsonSerializer.SerializeToElement(spells);
                Profile.Save();
            },
            // Ensure the blob exists so a profile's weapon writes land on a live
            // reference Save persists — the Equipment Manager seeds an empty one the
            // same way. Null only when no profile is loaded.
            equipment: () =>
            {
                if (Profile.Current is not { } p) return null;
                return p.Equipment ??= new Models.Profile.EquipmentSettings();
            },
            save: () => Profile.Save(),
            log: Log,
            // The Party-tab subset (party healing + bless) a profile can carry.
            readParty: () => ReadSection<Models.Profile.PartySettings>(Profile.Current, "Party"),
            writeParty: party =>
            {
                if (Profile.Current is not { } p) return;
                p.Settings ??= new();
                p.Settings["Party"] = System.Text.Json.JsonSerializer.SerializeToElement(party);
                Profile.Save();
            });
        CombatProfiles.EnsureSeeded();
        Profile.ProfileLoaded += _ => CombatProfiles.OnProfileLoaded();
        ProfileSwap = new Game.Remote.ProfileSwapHandler(RemoteCommands, CombatProfiles);

        // Anchor each fight to the combat profile driving it, on the InCombat
        // false→true edge, so a log read pins which profile fought and how it was
        // configured without waiting for a swap.
        PlayerState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Game.PlayerState.InCombat) && PlayerState.InCombat)
                CombatProfiles.NoteCombatEngaged();
        };

        // Unwearable-slot blocks: keep the Equipment tab's block set in sync with
        // the live character. A profile swap clears the in-memory blocks; a `who`
        // that refreshes OUR alignment re-evaluates every set (a drift re-blocks,
        // a realignment lifts the proactive blocks). The game's own wear-result
        // lines feed the reactive latch — a confirmed wear clears its pending
        // attempt; a refusal ("You may not wear that item!" armor / "You may not
        // use that weapon." weapon) blocks the slot it concerns so a swap stops
        // re-bonking a piece the character can't wear (e.g. after an EP-zap).
        Profile.ProfileLoaded += _ =>
        {
            Alignment.ResetForProfile();
            Equipment.ResetBlocks();
        };
        Players.ObservationRecorded += givenName =>
        {
            (string self, _) = Models.GameData.PlayerRecord.SplitName(PlayerStats.Name);
            if (!string.IsNullOrEmpty(self)
                && string.Equals(self, givenName, StringComparison.OrdinalIgnoreCase))
                Equipment.ReevaluateAllBlocks();
        };

        // Ask the game for our alignment when our gear says the record may be wrong —
        // Paradigm's `pro` shows the exact evil points, Stock's doesn't, so Stock asks
        // `who`. No timers: it checks when the game says alignment moved (gear taken
        // off, a wear refused, a forgive, a dark cloud out of Good) or when a gear set
        // disagrees with the record. Wire-sender bound by MainWindowVM.
        AlignmentCheck = new Game.Inventory.AlignmentGearCheck(
            GearAlignmentNeedsCheck,
            verifyCommand: () => GameData.ActiveRealm == Game.RealmType.ParaMud ? "pro" : "who",
            log: Log);
        Alignment.Refreshed += Equipment.ReevaluateAllBlocks;
        Equipment.BlocksChanged += AlignmentCheck.RequestCheck;
        Equipment.SetsEdited += AlignmentCheck.RequestCheck;
        Equipment.SetsEdited += CombatProfiles.SyncActiveWeaponsFromGear;
        Profile.ProfileLoaded += _ => AlignmentCheck.RequestCheck();
        // A dark cloud out of Good blocks Good-only gear at once and asks where we
        // landed; one from Neutral or worse changes nothing.
        Alignment.LeftGood += () =>
        {
            Equipment.ReevaluateAllBlocks();
            AlignmentCheck.RequestVerify();
        };
        // Gear taken off on a band change, or a victim's forgive: the record is old.
        _alignmentMovedSubs = new[]
        {
            Router.Subscribe(Services.Patterns.KnownPatterns.AlignmentGearRemoved, _ => AlignmentCheck.RequestVerify()),
            Router.Subscribe(Services.Patterns.KnownPatterns.AlignmentForgiven, _ => AlignmentCheck.RequestVerify()),
        };
        PromptScanner.PromptObserved += _ => AlignmentCheck.OnPrompt();
        _equipWearOkSub = Router.Subscribe(Services.Patterns.KnownPatterns.UserEquipped, m =>
        {
            if (m.Groups.Count == 0) return;
            Equipment.NoteEquipSucceeded(m.Groups[0]);
            // The line itself too: an answer that came after the counter's wait
            // was given up is no longer paired with a wear by the manager above.
            CounterWear.NoteWorn(m.Groups[0]);
        });
        // A refused wear / wield may be our alignment having moved: check it too.
        // A refusal of a room-spell counter's wear reaches CounterWear through
        // Equipment.SlotWearAnswered, paired with the wear it answers.
        _equipWearFailSub = Router.Subscribe(
            Services.Patterns.KnownPatterns.UserEquipFailed, _ =>
            {
                LearnFromEvilOnlyRefusal(Equipment.NoteWearRefused());
                AlignmentCheck.RequestVerify();
            });
        _equipWieldFailSub = Router.Subscribe(
            Services.Patterns.KnownPatterns.UserWieldFailed, _ =>
            {
                LearnFromEvilOnlyRefusal(Equipment.NoteWeaponRefused());
                AlignmentCheck.RequestVerify();
            });
        // A piece that can't be worn at all blocks its slot and teaches nothing about
        // alignment: no learning, no alignment check.
        _equipCannotBeWornSub = Router.Subscribe(
            Services.Patterns.KnownPatterns.UserEquipCannotBeWorn,
            m => Equipment.NoteCannotBeWorn(m.Groups.Count > 0 ? m.Groups[0] : null));
        // A "no more room" block lasts only while every worn slot is taken. The
        // removed piece's name is also how a counter learns what its wear pushed out.
        _wornPieceRemovedSubs = new[]
        {
            Router.Subscribe(Services.Patterns.KnownPatterns.UserRemoved, m =>
            {
                Equipment.NoteWornPieceRemoved();
                if (m.Groups.Count > 0) CounterWear.NoteRemoved(m.Groups[0]);
            }),
            Router.Subscribe(Services.Patterns.KnownPatterns.AlignmentGearRemoved, _ => Equipment.NoteWornPieceRemoved()),
            // Two answers that put nothing on, each to the slot claim it answers.
            Router.Subscribe(Services.Patterns.KnownPatterns.UserEquipNotInPack, m =>
            {
                if (m.Groups.Count > 0) Equipment.NoteNotLeftUnequipped(m.Groups[0]);
            }),
            Router.Subscribe(Services.Patterns.KnownPatterns.UserEquipOccupantStuck, m =>
            {
                if (m.Groups.Count > 0) Equipment.NoteOccupantNotRemovable(m.Groups[0]);
            }),
        };

        // Hold auto-rest while a gear-set swap streams its paced wear/rem commands —
        // each stands the character up, and without this the rest engine re-sends
        // `rest` between every command (the rest/stand thrash of a pre-rest gear swap,
        // report paradigm-20260825-103537).
        Health.SetEquipmentApplyingProbe(() => Equipment.IsApplyingSet);
        // `rest` waits for the rest gear to go on first — a wear after the sit breaks
        // the rest (AutoEquip is built further down; the lambda reads it at call time).
        Health.SetRestGearFirst(() => AutoEquip.WearRestGearBeforeResting());
        // Anchor rest triggers/targets to the DEFAULT gear set's max HP/mana (so a
        // Pre-rest set that swaps a +MaxHP/+MaxMana item doesn't move the target the
        // user tuned against their normal loadout), capped by the current gear's real
        // stat-screen max (so a rest set that LOWERS the pool can never strand the rest
        // out of reach — report paradigm-20260902-052036).
        // The basis is the recorded Default-gear baseline (PoolBaseline) once one exists.
        PoolBaseline = new Game.Health.DefaultPoolBaselineKeeper(
            read: () => Profile.Current?.DefaultPoolBaseline,
            write: b => { if (Profile.Current is { } p) { p.DefaultPoolBaseline = b; Profile.Save(); } },
            level: () => PlayerStats.Level,
            defaultGearBonus: DefaultGearPoolBonus,
            defaultWorn: DefaultSetWorn,
            defaultSetMissing: () => Inventory.IsLoaded && DefaultSetEquippedItems().Count == 0,
            clear: () => { if (Profile.Current is { } p) { p.DefaultPoolBaseline = null; Profile.Save(); } },
            // A stat already on its way (auto-train's after a level-up) answers the
            // same question — don't send a second (report paradigm-20260928-231447).
            canCheckNow: () => PlayerState.HasPromptData && !PlayerState.InCombat
                && !Equipment.IsApplyingSet && !TrainerMenu.MenuOwnsKeyboard
                && !Stats.ScreenExpected
                && !AutoModeController.Blocks("Info polls"),
            sendStat: () => _engineWireSend?.Invoke(System.Text.Encoding.Latin1.GetBytes("stat\r")),
            log: Log);
        Tick.HeartbeatElapsed += PoolBaseline.Poll;
        // A copied profile reads its own character before anything is trusted.
        StateVerifier = new Game.ProfileStateVerifier(
            pending: () => Profile.Current is { StateUnverified: true },
            markVerified: () => { if (Profile.Current is { } p) { p.StateUnverified = false; Profile.Save(); } },
            canAskNow: () => PlayerState.HasPromptData && !PlayerState.InCombat
                && !Equipment.IsApplyingSet && !TrainerMenu.MenuOwnsKeyboard
                && !Stats.ScreenExpected
                && !AutoModeController.Blocks("Info polls"),
            inventoryLoaded: () => Inventory.IsLoaded,
            send: command => _engineWireSend?.Invoke(System.Text.Encoding.Latin1.GetBytes(command + "\r")),
            log: Log);
        Tick.HeartbeatElapsed += StateVerifier.Poll;
        Profile.ProfileLoaded += _ => StateVerifier.Reset();
        Profile.ProfileLoaded += _ => _spellListAsked = false;
        // A rested follower's @ok waits until the Pre-rest set is off again; once the
        // swap back to Default lands, a CR re-reads the pools (after the max-pool settle
        // window) and the re-evaluation sends it.
        Health.SetPartyOkHold(() => CurrentEquippedIsPreRestSet() || Equipment.IsApplyingSet);
        Health.SetScheduler(pacedReplyScheduler);
        Equipment.ApplyingChanged += applying =>
        {
            if (applying || !Health.IsPartyOkHeldForGear) return;
            var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _engineWireSend?.Invoke(System.Text.Encoding.Latin1.GetBytes("\r"));
                Health.Evaluate();
            };
            timer.Start();
        };
        Health.SetRestPoolMaxProviders(
            () => DefaultBasisMaxHp(),
            () => DefaultBasisMaxMa(),
            () => PlayerStats.MaxHits,
            () => PlayerStats.MaxMana);
        // Self-heal HP triggers anchor to the Default set too (same basis as rest).
        CastDirector.SetRestPoolMaxHp(
            () => DefaultBasisMaxHp(),
            () => PlayerStats.MaxHits);

        // Hold every movement engine while a paced gear-set apply streams, so the
        // loop never steps out of a room mid-swap — the "finished resting, moved,
        // then swapped to Default in the next room mid-combat" report
        // (paradigm-20260826-140341). The gate clears the instant the swap finishes,
        // so the step-out lands already in the new set. Engine-wait tier — doesn't
        // touch the toolbar's user-pause face.
        Equipment.ApplyingChanged += applying =>
        {
            if (applying)
                MovementCoordinator.AssertGate(
                    Game.Map.MovementCoordinator.GearSwapGate, "EquipmentManager", "gear-set swap streaming");
            else
            {
                MovementCoordinator.ClearGate(
                    Game.Map.MovementCoordinator.GearSwapGate, "EquipmentManager", "gear-set swap complete");
                // Re-evaluate health the instant the swap finishes so a held rest
                // fires now instead of waiting for the next incidental prompt — the
                // ~8-second swap→rest gap in report paradigm-20260826-142625 (rest is
                // held while a swap streams, and nothing re-triggered Evaluate when it
                // ended).
                Health.Evaluate();
                // A rest that completes in the SAME tick it starts fires the pre-rest
                // swap AND the recovery-complete Default revert together; the revert
                // runs first and no-ops against the not-yet-streamed pre-rest set, then
                // the pre-rest swap lands last and strands the medi/pre-rest gear
                // (report paradigm-20260903-111227). Now that the pre-rest set has gone
                // out, the coordinator re-fires the Default revert — only when a rest
                // really did just finish, never for a set worn with no rest behind it.
                // The Default swap it fires re-enters here with Default current, so
                // this terminates after one correction.
                if (CurrentEquippedIsPreRestSet())
                    AutoEquip.OnPreRestSetStreamed();
                // If this swap streamed during a live fight (swap-to-Default-on-combat),
                // its wear/eq burst breaks the swing on Paradigm — arm combat's
                // interrupt resume so the imminent *Combat Off* re-engages instead of
                // waiting on the mob's next swing (reports paradigm-20260908-051035 /
                // -095552). Self-guards on auto-combat + a live engageable roster, so an
                // ordinary out-of-combat swap is a no-op.
                Combat.NoteGearSwapInterrupt();
            }
        };

        // EquipmentManager is the sole gear actuator: the combat engine decides
        // which weapon it wants and hands the act off here. The backstab-set
        // armor (deltas only, synchronous) and the weapon swap both fire from the
        // pre-move sequence, before the sn — equipping breaks sneak.
        Combat.SetWeaponActuator(Equipment.SwapWeapon, () => Equipment.ApplyBackstabArmor(),
            () => Equipment.WornWeapon);
        // Carried or worn, by name — unknown until the inventory's first read.
        Combat.SetCarriedCheck(name =>
        {
            Game.Inventory.InventorySnapshot pack = Inventory.Snapshot;
            return pack.LastUpdated == default ? null : pack.IsCarriedOrWorn(name);
        });
        // The class's and race's own hit magic (a Mystic's strikes, a Witchunter's
        // swings). Stock adds a weapon's magic to it; Paradigm takes the higher.
        Combat.SetInnateHitMagic(InnateHitMagic, () => GameData.ActiveRealm != Game.RealmType.ParaMud);

        // Let an auto-fire gear-set apply defer the weapon slot to combat while it
        // holds a per-monster alternate-weapon override, so the Default set's
        // combat-entry trigger can't clobber the swap (the weapon-flap report).
        Equipment.SetCombatWeaponOwnershipProbe(() => Combat.IsWeaponOverrideActive);

        // Confusion-fumble retry: a fumble consumes the just-sent command without it
        // executing, so the client re-sends it. Combat re-sends a weapon swing first
        // (it owns the engage-verification bookkeeping); if this isn't a weapon fight it
        // can act on — a fumbled attack SPELL, an item-use, or any other client command —
        // fall through to re-firing the last client command generically. Movement is NOT
        // re-fired here (ReplayLastClientCommand skips bare moves): a fumbled step already
        // reverts + re-sends via the walker, so a second send would desync position. A
        // command the USER typed is never re-fired — it never flowed through the gate.
        // An attack is re-sent only until `*Combat Engaged*` answers it: once the fight
        // is under way a fumble line starts no fresh attack, weapon or spell (user,
        // 2026-10-09). Everything else is re-fired as before.
        // With the master switch off nothing is re-fired at all: the last engine
        // command is from before it went off, and a fumble of the user's own is
        // theirs to repeat.
        Conditions.ActionFailed += _ =>
        {
            if (AutoModeController.Blocks("Fumble replay")) return;
            Combat.HandleFumble(EngineGate.LastClientCommandText, EngineGate.ReplayLastClientCommand);
        };

        // CashManager. Subscribes to cash-on-ground
        // / cash-picked-up / cash-dropped patterns and dispatches
        // per-currency policy. AutoGetCash gates the whole engine
        // (Settings -> General toggle + toolbar Toggle command).
        Cash = new Game.Cash.CashManager(Router,
            readSettings: () => ReadSection<Models.Profile.CashSettings>(Profile.Current, "Cash"),
            // An auto-train funding errand forces collection on regardless of the
            // toggle — the stash leg only searches, and it's this engine that takes
            // what the search reveals.
            isEnabled: CashCollectionOn,
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
                Game.Inventory.CurrencyHoldings.ToCopper(Currency.Canonicalize(currency), count), count);
        // The auto-deposit gates read the authoritative inventory snapshot
        // (wealth value + coin count), so re-evaluate whenever the parser
        // updates holdings — this is the only path that catches buy / sell
        // wealth swings (CashManager's own patterns see get / drop only).
        Inventory.Changed += Cash.OnInventoryChanged;

        // Another player's corpse recovered with their password: recorded off the
        // recovery's lines and handed back through the item-sweep pacer, each give
        // struck off on the game's confirmation.
        CorpseHandoff = new Game.Recovery.CorpseHandoffManager(
            Profile,
            selfName: () => Party.LocalCharacterName ?? Profile.Current?.Name,
            currentRoom: () => RoomTracker.State.CurrentRoom is { } here
                ? (new Models.Profile.RoomRef(here.Key.Map, here.Key.Room), here.Name)
                : (null, null),
            canonicalCurrency: Currency.Canonicalize,
            isParadigm: () => GameData.ActiveRealm == Game.RealmType.ParaMud,
            runicName: () => Currency.RunicName,
            sendPaced: (commands, owner, mayGo) => InventoryAction.SendPaced(commands, owner, mayGo),
            recallPaced: owner => InventoryAction.RecallPaced(owner, _ => true),
            scheduleAfter: (delay, action) =>
            {
                var timer = new Avalonia.Threading.DispatcherTimer { Interval = delay };
                timer.Tick += (_, _) => { timer.Stop(); action(); };
                timer.Start();
            },
            log: Log);
        Cash.CoinCollected += CorpseHandoff.NoteCoinsPicked;
        Inventory.ItemGivenAway += CorpseHandoff.OnItemGivenAway;
        Inventory.CoinsGivenAway += CorpseHandoff.OnCoinsGivenAway;
        Inventory.GiveRefused += CorpseHandoff.OnGiveRefused;
        Inventory.CoinGiveMisaimed += CorpseHandoff.OnCoinGiveRefused;
        Inventory.CoinGiveLackedCoins += CorpseHandoff.OnCoinGiveRefused;
        RoomTracker.StateChanged += t =>
        {
            if (t.NewRoom is not null && t.PreviousRoom is not null && !t.PreviousRoom.Key.Equals(t.NewRoom.Key))
                CorpseHandoff.NoteRoomChanged();
        };

        // Paradigm (ParaMud) accepts one counted action per item — the loot engines
        // batch a pile into a single get/drop/hide/sell/buy N <item>; Stock can only
        // act on one at a time. Read live so an active-set swap flips it.
        Func<bool> onParadigm = () => GameData.ActiveRealm == Game.RealmType.ParaMud;

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
            // The other half of the funding-errand override: while one runs, stashing
            // is SUPPRESSED. Without this the errand's stash-room stop would hide the
            // coin it just collected and walk away empty.
            isEnabled: () => !FundingErrandActive && ReadAutoModeFlag(d => d.AutoGetCash),
            log: Log,
            naming: Currency,
            isParadigm: onParadigm);
        // Transaction-history ledger and Session Stats (Stashed, Deposit/Sold) sources —
        // the server-confirmation echoes, which fire for a manual `dep` / `hide` and an
        // automated reroute alike (so both are counted), and arrive one per
        // denomination / item:
        //   coin stash   -> CashManager.CoinHidden       ("You hid N <coin>.")
        //   item stash   -> InventoryManager.ItemHidden  ("You hid <item>.")
        //   bank deposit -> InventoryManager.BankDeposited ("You deposit …", wrap-merged there)
        // Each echo captures the room it fired in — the stash room for a hide,
        // the bank room for a deposit — so the ledger records where excess went.
        Cash.CoinHidden += (currency, count) =>
        {
            TransactionHistory.NoteStash(
                new[] { (currency, (long)count) }, Array.Empty<string>(), CurrentRoomLabel());
            SessionActivity.NoteCurrencyStashed(
                Game.Inventory.CurrencyHoldings.ToCopper(Currency.Canonicalize(currency), count), count);
        };

        // Funding's structured tally of the same echoes. Separate from the ledger
        // above on purpose: that one is a rolling display log that formats amounts
        // into prose, this one has to add up.
        Cash.CoinHidden += (currency, count) =>
        {
            if (RoomTracker?.State.CurrentRoom is not { } room) return;
            StashBalances.NoteHidden(room.Key,
                Game.Inventory.CurrencyHoldings.ToCopper(Currency.Canonicalize(currency), count));
        };
        Cash.CoinCollected += (currency, count) =>
        {
            // Only rooms we believe hold a stash draw down — picking coin up off a
            // corpse in an ordinary room isn't a withdrawal from anything.
            if (RoomTracker?.State.CurrentRoom is not { } room) return;
            if (StashBalances.Believed(room.Key) <= 0) return;
            StashBalances.NoteRecovered(room.Key,
                Game.Inventory.CurrencyHoldings.ToCopper(Currency.Canonicalize(currency), count));
        };

        // A bank balance only ever moves by a deposit or a withdrawal, and the game
        // echoes both — so track it from those instead of re-asking `bank` before
        // every decision. Attributed to the bank we're standing in: `dep` / `with`
        // are room actions, so the current room IS the account that moved.
        Inventory.BankDeposited += copper =>
        {
            if (RoomTracker?.State.CurrentRoom is { } room && BankNameForRoom(room.Key) is { } name)
                BankBalance.NoteDeposit(name, copper);
        };
        Inventory.BankWithdrew += copper =>
        {
            if (RoomTracker?.State.CurrentRoom is { } room && BankNameForRoom(room.Key) is { } name)
                BankBalance.NoteWithdrawal(name, copper);
        };

        // Stash beliefs outlive a session because a stash does, and belong to the
        // realm: every character on it can reach the same rooms. A profile saved
        // while they were per character hands its tally over on load.
        StashStore = new StashBalanceStore(StashBalances, Log);
        void LoadRealmStash()
        {
            StashStore.OnRealmChanged(ActiveRealmFolder());
            if (Profile.Current is not { StashedCopper: { Count: > 0 } carried } p) return;
            if (StashStore.ActiveRealmFolder is null)
            {
                // No realm to hold them (no BBS yet): this session works from the profile's.
                StashBalances.Hydrate(carried);
                return;
            }
            if (!StashStore.Adopt(carried, p.Name)) return;
            p.StashedCopper = null;
            Profile.Save();
        }
        Profile.ProfileLoaded += _ => LoadRealmStash();
        Profile.BbsPinApplied += _ => LoadRealmStash();
        Profile.ProfileClosed += () => StashStore.OnRealmChanged(ActiveRealmFolder());
        Inventory.ItemHidden += item =>
        {
            // A discard (an auto-discard offload, a Chest Offload Drop) uses
            // `hide <item>` in HideMode — that's a discard, not a stash, so it
            // claims its own confirmation here and is kept out of the ledger.
            // Manual / stash-room hides were never registered, so they still record.
            if (AutoDiscard.TryConsumeSuppressedHide(item)) return;
            TransactionHistory.NoteStash(
                Array.Empty<(string, long)>(), new[] { item }, CurrentRoomLabel());
            SessionActivity.NoteItemsStashed(1);
        };
        Inventory.BankDeposited += copper =>
        {
            TransactionHistory.NoteBankDeposit(copper, CurrentRoomLabel());
            SessionActivity.NoteCurrencyDeposited(copper);
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
            heldCount: CountItemHeld,
            encumbrance: () => Inventory.Snapshot.Encumbrance,
            itemEncGates: () =>
            {
                Models.Profile.CashSettings c =
                    ReadSection<Models.Profile.CashSettings>(Profile.Current, "Cash");
                return (c.SkipGetItemIfMakesLight, c.SkipGetItemIfMakesMedium, c.SkipGetItemIfMakesHeavy,
                        c.SkipGetItemPast90Percent);
            },
            log: Log,
            isParadigm: onParadigm,
            dropCoinForWeight: (weight, item) => Cash.TryDropCoinForWeight(weight, item));
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
            // AutoSearch defers its per-room search "until combat clears" the same way;
            // without this it never fires the deferred `sea` and the Search gate sticks
            // held, wedging the walker on "waiting — searching the room" when combat
            // ended via the idle-stall watchdog rather than a clean room re-display
            // (report paradigm-20260820-090254).
            AutoSearch.OnRoomObserved();
        };

        // The force-clear is optimistic (a resync CR re-display re-confirms a beat
        // later). Hold resting until that re-confirm so a monster still in the room
        // doesn't get a `rest` sent at it (paradigm-20260814-225055).
        CombatTracker.CombatForceCleared += Health.NoteCombatForceCleared;
        // Drop CombatManager's stale target on a force-clear too — otherwise the
        // between-round debuff director fires an AoE debuff at the just-abandoned
        // mob as the walker steps away (report paradigm-20260902-053911).
        CombatTracker.CombatForceCleared += Combat.OnCombatForceCleared;

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
            isParadigm: onParadigm,
            // Worn gear counts toward an item's keep amount. The lit light is a pack
            // copy to the game, though the listing sets it apart; it is known from
            // the last full read only.
            wornItems: () => Inventory.Snapshot.EquippedItems.Select(e => e.Name),
            litLight: () => Inventory.Snapshot.ReadiedLight?.Name,
            carriedForJourney: id => PartyPathItemGate is { } gate ? gate.CopiesToCarryFor(id) : 0);
        // Auto-discard re-evaluates the pack on every inventory change — the
        // seam that surfaces chest dumps and freshly collected loot.
        Inventory.Changed += AutoDiscard.OnInventoryChanged;
        Inventory.FullInventoryParsed += AutoDiscard.OnFullInventoryRead;
        // A hide the room had no room for is sent again on arriving somewhere
        // else. Only a confirmed room counts: a pending move still shows the room
        // being left, and a suspect reading may not be a move at all.
        RoomTracker.StateChanged += t =>
        {
            if (t.NewConfidence == Game.Map.RoomConfidence.Confirmed && t.NewRoom is { } arrived)
                AutoDiscard.OnRoomEntered(arrived.Key);
        };
        // The engine's commands go into the pacer under its own tags and with its
        // own last-moment check, so it can take back what is still waiting when
        // the rules change under them; nothing else in the queue is its to take.
        AutoDiscard.PacedSender = (cmds, owner, mayGo) => InventoryAction.SendPaced(cmds, owner, mayGo);
        AutoDiscard.RecallQueued = (owner, take) => InventoryAction.RecallPaced(owner, take);
        // An item's flag unticked or its keep amount raised while a pile waits.
        Resolver.GameDataChanged += _ => AutoDiscard.OnRulesChanged();
        AutoDiscard.SendsQueued = () => InventoryAction.HasPacedCommandsQueued;
        AutoDiscard.CancelQueuedSends = () => InventoryAction.CancelPaced();
        // A discard sent while the send gate is up is dropped unsent. And on Stock
        // a hide gets no answer of its own in the dark or blind (GAME_MECHANICS
        // "Hiding items in a room"; Paradigm isn't recorded, so sight doesn't hold
        // its hides back).
        AutoDiscard.SendGateOpen = () => !EngineGate.IsLocked;
        AutoDiscard.CanSeeToHide = () =>
            onParadigm() || (!RoomTracker.IsInDarkRoom && !Conditions.IsBlinded);
        // Either clearing may be what a discard was waiting on.
        EngineGate.Released += AutoDiscard.OnInventoryChanged;
        Conditions.ConditionEnded += _ => AutoDiscard.OnInventoryChanged();
        // A held hide is for a copy still to be got rid of; sold, it is gone.
        Inventory.ItemSold += (name, count, _) => AutoDiscard.ReleaseHeld(name, count);
        // Discards sent to another character's game, or before a death emptied
        // the pack, will never be answered. (A dropped connection is the main
        // window's to report.)
        Profile.ProfileLoaded += _ => AutoDiscard.Reset("profile loaded");
        RoomTracker.PlayerDeathObserved += () => AutoDiscard.Reset("death");

        AutoBuy = new Game.Inventory.AutoBuyManager(Router,
            resolve: ResolveAutoBuyItem,
            countCarried: CountItemCarried,
            isEnabled: () => ReadAutoModeFlag(d => d.AutoGetItems),
            log: Log,
            isParadigm: onParadigm);

        // A shop's stock readout ends at the prompt after it, a line of its own that
        // the line stream only emits once something else is printed. Posted, so the
        // rows that arrived in the same read are in before the readout is closed.
        PromptScanner.PromptObserved += _ => Avalonia.Threading.Dispatcher.UIThread.Post(AutoBuy.NotePromptSeen);

        AutoSell = new Game.Inventory.AutoSellManager(Router,
            carriedItems: () => Inventory.Snapshot.CarriedItems,
            resolve: ResolveAutoSellItem,
            shopTradesItem: (shop, item) => ShopStock.ShopsSelling(item).Contains(shop),
            isEnabled: () => ReadAutoModeFlag(d => d.AutoGetItems),
            log: Log,
            isParadigm: onParadigm);

        // The engine holds no wire sender of its own: each open goes to the Chest
        // Offload tracker, which sends it and reads what it gave. Nothing is left
        // for the main window to bind, and so nothing to leave unbound.
        AutoOpen = new Game.Inventory.AutoOpenManager(
            carriedItems: () => Inventory.Snapshot.CarriedItems,
            resolve: ResolveAutoOpenItem,
            // With the Auto-All switch off nothing automatic is sent.
            isEnabled: () => ReadAutoModeFlag(d => d.AutoGetItems) && !AutoModeController.KillSwitchEngaged,
            isLoaded: () => Inventory.IsLoaded,
            open: name => ChestOpens.TryOpenNow(name),
            log: Log)
        {
            // An `open` typed at the board's menus would be a menu choice.
            SendGateOpen = () => !EngineGate.IsLocked && InGameCapture.InGame,
            InCombat = () => PlayerState.InCombat,
            // `open` stands a resting character up, as the door and trap tries do.
            Resting = RestHeld,
            SneakKept = () => SneakGuard.Holds,
            ComingBack = name => HangupItems.BeingPickedUp(name),
            // The pile of a death is open until its record is recovered, found
            // missing or cleared. A minute's slack: the record and the engine each
            // stamp the death themselves.
            DeathpileOpenSince = since => Profile.Current?.DeathHistory?.Any(r =>
                r.At >= since.AddMinutes(-1)
                && r.Status is Models.Profile.DeathRecoveryStatus.Active
                    or Models.Profile.DeathRecoveryStatus.Partial) == true,
        };
        // Auto-open re-evaluates the pack on every inventory change — the seam
        // that surfaces a container the moment it enters inventory.
        Inventory.Changed += AutoOpen.OnInventoryChanged;
        Inventory.FullInventoryParsed += AutoOpen.OnFullInventoryRead;
        ChestOpens.OpenSettled += AutoOpen.OnOpenSettled;
        ChestOpens.PlayerOpened += AutoOpen.OnPlayerOpened;
        // Each of these may be what an owed open was waiting on.
        EngineGate.Released += AutoOpen.Recheck;
        SneakGuard.Released += AutoOpen.Recheck;
        MovementCoordinator.GatesChanged += AutoOpen.Recheck;
        InGameCapture.InGameChanged += inGame =>
        {
            if (inGame) AutoOpen.OnEnteredGame();
            AutoOpen.Recheck();
        };
        PlayerState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Game.PlayerState.InCombat) && !PlayerState.InCombat) AutoOpen.Recheck();
        };
        // Switched off, an owed open is forgotten then and there: back on a moment
        // later, with nothing having moved in the pack, it must not go out. The
        // Auto Get Items toggle does the same from the main window.
        AutoModeController.KillSwitchToggled += _ => AutoOpen.Recheck();
        Profile.ProfileLoaded += _ => AutoOpen.Reset();
        RoomTracker.PlayerDeathObserved += AutoOpen.OnPlayerDied;
        // Not for an arena death: it took nothing, so what the entry read found
        // gone didn't go into a pile.
        RoomTracker.UnwitnessedDeathRecorded += () =>
        {
            if (Profile.Current?.DeathHistory?.LastOrDefault() is { NothingLost: false } record)
                AutoOpen.OnUnwitnessedDeath(record.At);
        };
        // Settings → Talk auto-greet. Self name resolves through the
        // PartyManager's LocalCharacterName first (set on connect), then
        // the loaded profile name as a fallback. Wire-sender bound by
        // MainWindowViewModel after telnet connects.
        Greet = new Game.GreetManager(RoomClassifier, Players, Party.State,
            selfNameProvider: () => Party.LocalCharacterName ?? Profile.Current?.Name);
        // Settings → Talk reactive-look automation. Shares Greet's self-name
        // resolution; RoomEntry (built earlier) supplies the arrival hook.
        // Wire-sender bound by MainWindowViewModel after telnet connects.
        PlayerLook = new Game.PlayerLookManager(Router, RoomEntry, Players, Party.State,
            selfNameProvider: () => Party.LocalCharacterName ?? Profile.Current?.Name);
        // Players Seen log. Records off the same room-presence hooks (Also-here
        // classification + room walk-ins) and shares the self-name resolution;
        // persists the aggregated rows on the loaded character's profile. Owns no
        // room-source subscriptions of its own — we wire the two hooks here.
        PlayerSightings = new Game.PlayerSightingTracker(
            () => RoomTracker.State.CurrentRoom, Profile,
            selfNameProvider: () => Party.LocalCharacterName ?? Profile.Current?.Name,
            isPartyMember: Party.State.HasMember);
        RoomClassifier.EntitiesObserved += PlayerSightings.NoteAlsoHere;
        RoomEntry.ArrivalObserved += PlayerSightings.NoteArrival;
        // Monster Intel's "Your Observations" — subscribes to the same fixed
        // combat-line patterns CombatSessionTracker does, attributed per
        // monster instead of session-wide; persists on the loaded profile.
        MonsterObservations = new Game.Combat.MonsterObservationTracker(
            Router, RoomClassifier, () => Combat.CurrentTarget, Profile, log: Log, isPartyMember: IsPartyMemberOrSelfName);
        // One judge of what was a swing: a miss the session figures take back as a
        // spell's cast line comes off the monster's record too.
        CombatSession.CastLineMissRetracted += () => MonsterObservations.RetractLastMiss();
        // Demand-driven auto-search. Posts a PathItem need when the
        // walker plans a route through an Item/Ticket exit whose item we
        // don't carry; resolves it when the item enters inventory. The
        // enabled gate reads Settings → Other live through the resolver so a
        // toggle takes effect without a profile reload. Walker's announce
        // seam is bound after the walker is built (below).
        PathItemDemand = new Game.Map.PathItemDemandTracker(
            Needs,
            carriedCount: CountPathItemCoverage,
            inventoryLoaded: () => Inventory.IsLoaded,
            // POSTING gate. The route picker's explicit "obtain then cross" / "search
            // en route" pick forces a per-walk obtain regardless of the global
            // search-if-needed preference — the pick IS the consent, so register the
            // need whenever a forced obtain is live. This drives the shop/give/drop
            // fulfillers (the reliable acquire path), so it must stay open on a forced
            // obtain even with master auto-search off — that's what lets the buy-at-shop
            // fallback fire when searching is off or turns nothing up.
            isEnabled: () => JourneyHasFetchOrder,
            // Searching is only a plausible way to get an item nobody hands over on
            // demand. An NPC keyword give (free, or a trade agreed to) or a guaranteed
            // room-command summon is already being walked to, so a `sea` in every
            // room en route is pure noise. A shop item or a percentage drop stays
            // search-worthy — finding one loose beats paying or grinding for it.
            isSearchWorthy: id =>
                !GiveRouterHasSource(id) && SummonSourcesForItem(id).Count == 0,
            // SEARCH-DEMAND gate. The master Auto-Search toggle is the driver of the
            // `sea` while moving (the retired "search rooms if item needed" setting
            // used to be an independent arm). So the demand-search only rides along
            // when Auto-Search is on: the route picker's "Search en route" card turns
            // it on for the leg, and toggling it off mid-route stops the `sea` live
            // while the shop-buy fallback carries on. This still feeds the search
            // settle-hold (AutoSearchManager holds the walker so a revealed counter is
            // collected before it steps on).
            searchEnabled: () => ReadAutoModeFlag(d => d.AutoSearch),
            log: Log);
        Inventory.Changed += PathItemDemand.OnInventoryChanged;

        // Party-inventory awareness. The probe broadcasts @have and
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
        PartyHandOvers = new Game.Map.PartyHandOverMemory(
            // Only an item the data says has unlimited uses is kept for good. One
            // with a charge count, or with no record to read, may be used up.
            hasLimitedUses: id => Game.Inventory.ItemChargeMeta.Read(GameData, id) is not { MaxUses: <= 0 },
            journey: () => Walker.Journey,
            log: Log);
        Inventory.ItemGivenAway += PartyHandOvers.OnItemGivenAway;
        PartyPathItemGate = new Game.Map.PartyPathItemGate(
            isCarried: IsItemCarried,
            selfCount: CountItemCarried,
            // The count a route card took a moment ago is the one its walk decides on.
            query: (id, name) => TakeCardCount(id) is { } counted
                ? Task.FromResult(counted)
                : PartyInventory.QueryAsync(id, name),
            itemName: ItemNames.GetName,
            isEnabled: IsAutoObtainForPath,
            perPersonQuantity: PathPerPersonQuantity,
            searchEnabled: () => ReadAutoModeFlag(d => d.AutoSearch),
            inParty: () => PartyState.IsInParty,
            selfIsLeader: () => PartyState.SelfIsLeader,
            selfGivenName: () => GivenNameOf(Party.LocalCharacterName ?? Profile.Current?.Name),
            forward: PathItemDemand.OnPathItemsRequired,
            post: action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            log: Log,
            substitutes: PathItemSubstitutes.For,
            agreedTrade: id => AgreedTradeFor(id) is not null,
            // A gate of its own: a named gate has one owner, and no other gate's
            // owner knows when a party count starts or ends.
            holdWalk: reason => MovementCoordinator.AssertGate(
                Game.Map.MovementCoordinator.PartyItemCountGate, nameof(PartyPathItemGate), reason),
            releaseWalk: reason => MovementCoordinator.ClearGate(
                Game.Map.MovementCoordinator.PartyItemCountGate, nameof(PartyPathItemGate), reason),
            // The answer turns a walk aside only through a posted need and a router
            // free to act on it. Every other walk that crosses a ticked item's gate
            // (a bank run, a sell trip, a trainer trip, a flee) would stand out the
            // count and then go exactly where it was going.
            canTurnWalkAside: () => JourneyHasFetchOrder && !ErrandOwnsWalk()
                && (LoopRunner.State == Game.Map.LoopState.Idle || LoopRunner.IsApproachInFlight),
            armHoldCap: expired => ScheduleOnce(PartyInventory.QueryWindow + TimeSpan.FromSeconds(2), expired),
            journey: () => Walker.Journey,
            handOvers: PartyHandOvers,
            askPartyList: PartyPoller.RequestPar,
            // An `[Invited]` row is on the list and not following: not with us.
            followingMembers: () => PartyState.Members
                .Where(m => !m.IsSelf && !m.IsInvited && GivenNameOf(m.Name) is { Length: > 0 })
                .Select(m => GivenNameOf(m.Name)!)
                .ToArray());
        // The answer to the `par` the gate asks after a crossing.
        Party.ParReplyRead += PartyPathItemGate.OnPartyListRead;
        // The leader coordinates redistribution once acquisition makes the
        // party whole — re-check on every inventory change.
        Inventory.Changed += PartyPathItemGate.OnInventoryChanged;
        // Another character: the counts and the hold belong to the one that left.
        Profile.ProfileLoaded += _ =>
        {
            PartyPathItemGate.Clear();
            PartyHandOvers.Clear("another character was loaded");
        };
        // Handed out: the gate the party was short for is open again.
        PartyPathItemGate.Provisioned += ClearPartyShortGateItem;
        // A count is about one roster; a member joining or leaving voids it. What
        // was handed to a member is theirs alone, so it goes only when they do.
        PartyState.Members.CollectionChanged += (_, _) =>
        {
            ClearPartyShortGateItems("the party changed");
            PartyPathItemGate.ForgetCounts();
            PartyHandOvers.KeepOnly(PartyState.Members
                .Where(m => !m.IsSelf && GivenNameOf(m.Name) is { Length: > 0 })
                .Select(m => GivenNameOf(m.Name)!)
                .ToArray());
        };
        // The hand-overs were this leader's. Under another leader the party is not
        // the one they were made in.
        PartyState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Game.PartyState.SelfIsLeader) && !PartyState.SelfIsLeader)
                PartyHandOvers.Clear("this character no longer leads the party");
        };
        // A copy with a limited number of uses may be used up by the gate it was
        // fetched for, in every pack that crossed with the leader's move. A kept
        // one has the party list asked instead. The room changes here only on the
        // display that confirms the move, never on the move being sent.
        RoomTracker.StateChanged += t =>
        {
            if (t.PreviousRoom is not { } from || t.NewRoom is not { } to || from.Key.Equals(to.Key)) return;
            foreach (Game.Map.RoomExit exit in from.Exits.Values)
                if (exit.KeyItemId > 0 && exit.Target.Equals(to.Key))
                    PartyPathItemGate.OnGateCrossed(exit.KeyItemId);
        };
        // The line that refuses a give ends the wait for the one that confirms it.
        Inventory.GiveRefused += PartyHandOvers.OnGiveRefused;

        // Per-walk forced-obtain (the route picker's "obtain then cross" choice):
        // drop an item from the override once it's covered — the item itself or
        // any route substitute (a canoe covers a raft on the river). The demand
        // tracker resolves the matching need on the same coverage count, so a
        // crosser who picks up a different boat mid-route stops being chased for
        // the one the picker chose. The abandon-clear on Walker.Event is wired after
        // the walker is constructed (see below).
        // An item the party is short of stays forced though the leader holds a copy:
        // the copies still to come are for the members.
        Inventory.Changed += () => Fetch?.DropCovered(
            id => IsPathItemCovered(id) && !IsPartyShortOfGateItem(id));

        // Registered AFTER the forced-obtain draining handler above so the set is
        // fully emptied before this checks it: once the route counter lands (found on
        // the floor or bought), the forced set drains → flip auto-search back off if
        // WE turned it on for a "search en route" pick.
        Inventory.Changed += () => RestoreRouteSearchAutoSearchIfDone("counter obtained");

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
        // it drifts with loot / spend — so @wealth is asked only when a walk's
        // route crosses a paid exit (Probe, from MovementFilter.WarmForRoute) and
        // at a toll, fare or sailing itself (PartyTollGate, further down), which is
        // what judges the party there. The tracker records each reply and exposes
        // the party's poorest known wallet, which tells MovementFilter's warm-ups
        // a party is led and goes in the bug report. The probe forwards replies
        // straight to the tracker (not the players table). The recordWealth closure
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

        // Self bank-balance probe — sends `bank` and parses the deposit listing.
        BankBalance = new Game.Remote.BankBalanceProbe(
            send: cmd => SendGameCommand(cmd),
            inBankRoom: () => RoomTracker.State.CurrentRoom?.Key is { } here
                && Game.GameData.BankCatalog.IsBankRoom(GameData, here),
            log: Log);

        // Quest-flag reader — sends `abil <flag>` (paradigm) / `sys god <name> abil` (stock)
        // and parses the flag values. Consumed by QuestFlagSync at login.
        QuestFlagReader = new Game.Quests.QuestFlagProbe(send: cmd => SendGameCommand(cmd), log: Log);
        QuestQueryReader = new Game.Quests.QuestFlagProbe(send: cmd => SendGameCommand(cmd), log: Log);

        // Base auto-search — a room-wide `sea` reveals hidden items for the
        // auto-get engines. Armed by the persisted master toggle OR the transient
        // path-item demand gate above. A search won't run mid-combat, so the engine
        // defers past a fight and holds the walker via the Search gate until the
        // room clears (see AutoSearchManager). Wire-sender bound by
        // MainWindowViewModel after connect.
        // GhSweep (Roomba Mode) does NOT feed this demand gate — recon drives its
        // own `sea` sends directly (BeginRoomSearches), the same way Sorting
        // drives get/drop directly, rather than piggybacking on AutoSearchManager's
        // single-fire-per-arrival demand mechanism.
        AutoSearch = new Game.Map.AutoSearchManager(
            isEnabled: () => ReadAutoModeFlag(d => d.AutoSearch),
            // A route's demand for a search is not the Auto Search toggle, so it
            // reads the master switch itself.
            isDemandActive: () => !AutoModeController.KillSwitchEngaged
                && (PathItemDemand.SearchDemandActive || PartyPathItemGate.SearchDemandActive),
            // Probe THIS room's live roster (not CombatTracker.HasEngageableHostiles —
            // the sticky cross-room gate, which stays asserted while combat winds down
            // on a left-behind target and made AutoSearch skip empty rooms; report
            // paradigm-20260820-090736).
            hasEngageableHostiles: () => Combat.HasEngageableIn(RoomClassifier.Current),
            // Only defer the search for a fight the client will actually prosecute:
            // the CombatGate is asserted only when auto-attack is armed. With
            // auto-combat off, a hostile in the room never gets fought/cleared, so
            // holding the search would deadlock the walker (report -074607).
            isCombatEngaging: () => CombatTracker.HasEngageableHostiles,
            hasGetEngineArmed: () =>
                ReadAutoModeFlag(d => d.AutoGetItems) || ReadAutoModeFlag(d => d.AutoGetCash),
            // Don't `sea` a transit room the player has already queued past — search
            // only where movement settles (RoomTracker's pending-move queue is empty).
            hasQueuedMoves: () => RoomTracker.HasQueuedMoves,
            coordinator: MovementCoordinator,
            log: Log);

        AutoSearch.SetSneakKeptProbe(() => SneakGuard.Holds);
        // Combat-clear seam: fires the deferred `sea` once the room is clear.
        // Wired after AutoGetItems.OnRoomObserved (above) so the search's revealed
        // loot is collected after the fight's own drops; CombatStateTracker's
        // handler ran first, so the hostile flag is current.
        RoomClassifier.EntitiesObserved += _ => AutoSearch.OnRoomObserved();

        // Empty-search seam: an empty room's `sea` prints "Your search revealed
        // nothing." — release the walker hold at once rather than idling the settle,
        // so auto-search doesn't tax every empty transit room (a fruitful search
        // surfaces the "You notice … here." survey the get engines handle instead).
        Router.Subscribe(Services.Patterns.KnownPatterns.SearchRevealedNothing, _ => AutoSearch.NotifySearchRevealedNothing());

        // Drop the stale queue / ground snapshot when we actually change rooms.
        //
        // Registered here — before LoopRunner exists (constructed further below) —
        // specifically so these reactors get first crack at the SAME RoomTransition
        // LoopRunner's own OnTrackerStateChanged also subscribes to. Multicast
        // delegates fire in registration order: anything that needs to assert a
        // MovementCoordinator gate in reaction to a room arrival (AutoSearch's
        // Search gate, GhSweep's GhSort gate) MUST be registered before LoopRunner's
        // subscription, or LoopRunner's own confirm-and-advance-to-the-next-step
        // path always wins the race and sends the next move before the reactor
        // gets a turn — this is what let a Roomba sweep leave a room before
        // picking anything up. GhSweep is assigned later in this constructor (it
        // needs the LoopRunner instance), but the property is read lazily inside
        // the lambda body rather than captured at registration time — safe, since
        // this lambda only ever runs long after the constructor finishes and
        // GhSweep is assigned, the same forward-reference pattern AutoSearch /
        // AutoGetItems / GroundItems / Cash above already rely on.
        RoomTracker.StateChanged += t =>
        {
            // Same-room refresh (resync CR re-display) — not a genuine change; skip.
            if (t.NewRoom is not null && t.PreviousRoom is not null
             && t.PreviousRoom.Key.Equals(t.NewRoom.Key)) return;
            // AutoSearch hears every genuine change INCLUDING a null room (death →
            // respawn-pending), so it can key its owed search and clear a search
            // deferred in the room we died in (report paradigm-20260820-090736).
            AutoSearch.OnRoomChanged(t.NewRoom?.Key);
            // A buy queue is for the shop it was read in; a death leaves that room too.
            AutoBuy.OnRoomChanged();
            if (t.NewRoom is null) return;   // the other engines have nothing to do on death
            AutoGetItems.OnRoomChanged();
            GroundItems.OnRoomChanged();
            Cash.OnRoomChanged();
            GhSweep.OnRoomChanged(t);
            // Pass-through stash runs here — ahead of LoopRunner's StateChanged
            // handler — so its `hide` reaches the wire before the loop's next move
            // (else the coins hide in the NEXT room; report paradigm-20260819-054200).
            AutoDeposit?.OnRoomEntered(t);
            // Auto-sell in a shop room that trades a flagged item — here, ahead of the
            // movement engines, so its Selling gate is up before the next step.
            AutoSell.OnRoomEntered(t.NewRoom.Shop);
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
            enabled: () => Settings.Current.AsylumSolverEnabled,
            coordinator: MovementCoordinator,
            // Open a closed door/gate blocking a relocalization peek before looking
            // through it, via the shared door FSM. Asylum barriers are plain-bashable
            // (no key, no strength gate the resolver can read while Lost), so request
            // a bashable no-key open and report back whether it opened.
            openDoor: (dir, done) => Door.Enqueue(
                dir, statRequirement: 0, canBash: true, keyItemId: 0, sender: "maze",
                reply: r => done(r is Game.Map.DoorOpenResult.Opened)),
            stopDoor: () => Door.StopAll());
        Walker.SetMazeSolver(MazeSolver);
        // Teleports on walks the client starts by itself (a walk the user starts states
        // its own preference and never reads this). Read live from the character, so a
        // change reaches the next walk and a profile swap brings its own list.
        Walker.SetAutomaticWalkTeleports(() => Game.Map.TeleportCatalog.ParseKeys(
            ReadSection<Models.Profile.TeleportSettings>(Profile.Current, "Teleports").AutomaticWalkTeleports));
        // A refused automatic walk names the line to tick by its title on that tab.
        Walker.SetTeleportChoices(() => TeleportChoices);
        RoomGraph.GraphReloaded += () => _teleportChoices = null;
        RoomGraph.GraphReloaded += () => _simulatorGateItems = null;
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
            isPartyMember: IsPartyMemberName,
            // Paradigm's `rm`, or a sysop's locate where that is the power to hand.
            askPosition: (reason, resolved, failed) =>
                ParadigmResync.RequestResyncOnce(reason, resolved, failed)
                || SysopLocate.RequestLocateOnce(reason, resolved, failed, forRecovery: true),
            openDoor: (dir, statRequirement, canBash, keyItemId, sender, reply) =>
                Door.Enqueue(dir, statRequirement, canBash, keyItemId, sender, reply),
            holdsItem: HoldsKeyItem,
            gateEngineOn: gate => ReadAutoModeFlag(d => Game.Map.PyramidRunThrough.GateEngineOn(gate, d)),
            stopDoor: () => Door.StopAll());
        Walker.SetPyramidSolver(PyramidSolver);
        // Data-driven boat routing. When a walk's goal is cheaper (or only)
        // reachable by a sea-captain sailing, the planner stitches the two land
        // legs around the boat hop and the walker inserts a BoatStep. The planner
        // pulls its candidate sailings from RoomGraph's data-driven boat index, so
        // it no-ops on realms without docks.
        Walker.SetBoatPlanner(new Game.Map.BoatRoutePlanner(RoomGraph, Bfs, Log));
        // Sys-goto shortcut planner + fire: weighs a `sys goto` jump against the land
        // route (empty locations when the power's off → no shortcuts) and fires the
        // chosen jump through SysopGotoManager. The router excludes level-gated
        // locations when the level is unknown (unlike a manual fire).
        Walker.SetSysGotoPlanner(
            new Game.Map.SysopGotoRoutePlanner(
                RoomGraph, Bfs,
                () => SysopGoto.UsableNow,
                () => Stats.HasParsed ? PlayerStats.Level : (int?)null,
                Log),
            loc => SysopGoto.FireForRoute(loc));
        // Voyage timer: the boat step waits out the sail — from boarding in the
        // captain's room, through the buff-locked transit legs, to landing at the
        // arrival shore — on a wall-clock deadline it sizes from the passage's
        // transit-spell rounds. Wire a UI-thread one-shot so OnBoatDeadline runs on
        // the same thread the walker's tracker events do; the injected shape keeps
        // the Game/Map layer UI-free (tests drive a fake clock instead).
        Func<TimeSpan, Action, IDisposable> uiOneShot = (delay, callback) =>
        {
            var timer = new Avalonia.Threading.DispatcherTimer { Interval = delay };
            timer.Tick += (_, _) => { timer.Stop(); callback(); };
            timer.Start();
            return new DispatcherTimerHandle(timer);
        };
        Walker.SetVoyageScheduler(uiOneShot);
        // The tracker's wait for a room to be shown after a step through a
        // teleporting exit runs on the same clock.
        RoomTracker.SetDelayScheduler(uiOneShot);
        // While a maze solve is Active the tracker legitimately churns Lost/Suspect
        // between same-named teleport landings — relocalizing that is the solver's
        // job. On Paradigm the solver drives its OWN `rm` after each landing (see
        // TeleportMazeSolver); keep the recovery gate's proactive `rm` suppressed
        // for the duration so it can't fire a second, uncoordinated `rm` that races
        // the solver's. On stock (no `rm`) the solver uses the look-sweep and this
        // gate no-ops anyway.
        Recovery.TryResync = reason => !MazeSolver.Active && ParadigmResync.TryRequestResync(reason);
        // Forced variant used at the gate's give-up boundaries (before the
        // heuristic backtrack, and again before the "Lost" dialog): skips the
        // resolver's anti-storm throttle so a client about to fail out always gets
        // one authoritative `rm` first (report paradigm-20260902-223159). Same
        // maze-solver guard — the solver owns `rm` during a solve.
        Recovery.TryResyncForced = reason => !MazeSolver.Active && ParadigmResync.TryRequestResync(reason, force: true);
        // Confusion awareness: `rm` only fails to answer when a confusion fumble
        // eats the command, so a timed-out forced resync while confused is re-asked
        // (confusion self-clears) rather than dropped to Lost. Same source the
        // walker / loop-runner confusion exemptions read.
        Recovery.IsConfused = () => Conditions.IsConfused;
        // Same maze-solver guard as TryResync above — a caller's one-shot re-fix
        // (LoopRunner / AutoWalkManager leaning on rm before trusting a possibly
        // mis-anchored belief) must not race the solver's own rm during a solve.
        // Paradigm's `rm` first, then sysop `sys st` — the same authoritative
        // answer by a different route. Without the second, a stock realm's
        // "blocked at source" recovery had no locator at all: it rerouted from a
        // room the tracker had wrong, three times in one second, and failed the
        // loop (report stock-20260904-143436).
        Recovery.TryResyncOnce = (reason, onResolved, onFailed) =>
            !MazeSolver.Active
            && (ParadigmResync.RequestResyncOnce(reason, onResolved, onFailed)
                // Sysop mirror of the loop/replan one-shot `rm`. forRecovery bypasses
                // the locate throttle: a loop blocked at source re-enters recovery on
                // the 2s attempt spacing (stock-20260904-143436 showed three reroutes
                // in one second before that spacing existed), so the 15s convenience
                // throttle would deny every retry after the first and drop us to a
                // backtrack that can't converge in a gang house of identically-named
                // rooms.
                || SysopLocate.RequestLocateOnce(reason, onResolved, onFailed, forRecovery: true));
        // Engine-less resync gap: the recovery gate above asks for an `rm` on a
        // mid-walk mismatch, but no-ops with no engine attached. A manual boat ride
        // (no engine) that disembarks into a duplicated-name room strands the tracker
        // in Suspect until the user hand-types `rm` (report paradigm-20260827-081044).
        // Let the tracker request the fix itself, but ONLY in that no-engine gap so it
        // can't race the gate's own resync; the maze solver drives its own `rm`, so
        // stay out of its way too.
        // This is the fix asked for with no engine running, so on a move made by
        // hand: with the master switch off it is not sent (no `rm`, no `sys st`).
        // The tracker goes on reading the rooms it is shown.
        RoomTracker.RequestAuthoritativeResync = reason =>
            Recovery.AttachedEngine is null && !MazeSolver.Active
            && !AutoModeController.Blocks("Position fixes")
            && (ParadigmResync.TryRequestResync(reason)
                || SysopLocate.TryRequestLocate(reason));   // sysop mirror of the no-engine `rm` gap (throttled)
        // DeathRecoveryManager's Walk-to-Room / Recover-Now actions route
        // through the walker — attached here since the walker is built
        // after the manager.
        DeathRecovery.AttachWalker(Walker);
        // The Stock spill sweep plans from the room graph, tells a leg a movement gate
        // is holding from one that has stalled, and never searches a stash room. The
        // pile list leaves out what a death doesn't drop.
        DeathRecovery.AttachSpillSweep(
            roomLookup: RoomGraph.GetRoom,
            movementHeld: () => MovementCoordinator.IsPaused,
            isStashRoom: Movement.IsStash,
            // The sweep the user asked for gives way to whatever else drives the
            // character: a loop, Auto-Lair or an errand's walk, any other errand or
            // solver that has the walker, and a party leader being followed.
            // A loop being walked to (its handoff pending) is a loop for this purpose:
            // the walker's arrival hands over to it a moment later.
            // A fight with a player and a flee from one are engines too: neither
            // stops a sweep's leg when it takes over (the leg isn't a walk it can save
            // and resume), so the sweep has to see them and stand down.
            otherEngineDrives: () =>
                ErrandOwnsWalk() || ErrandHasTheWalker
                || LoopRunner.State != Game.Map.LoopState.Idle || LoopHandoff.Pending is not null
                || PvpFight.IsActive || PvpFlee.IsActive
                || MovementCoordinator.IsGateAsserted(Game.Map.MovementCoordinator.FollowerGate),
            // A flee, or the walk back from one, that ended here was an engine's walk
            // though it is over by the time anyone asks.
            engineWalkEndedAt: room => PvpFlee.WalkJustEndedAt(room),
            // It sends nothing during a rest (helper actions wait one out) or while
            // the user has paused; Auto-All is its own probe below.
            restHeld: RestHeld,
            userPaused: () => MovementCoordinator.IsGateAsserted(Game.Map.MovementCoordinator.UserGate),
            autoSearchesRooms: () => ReadAutoModeFlag(d => d.AutoSearch)
                || PathItemDemand.SearchDemandActive || PartyPathItemGate.SearchDemandActive,
            noteRoomSearched: room => AutoSearch.NoteSearchedByOther(room));
        DeathRecovery.SetStaysOnDeathProbe(
            EveryItemOfThisNameStaysOnDeath,
            isKeyItem: name => ItemNames.FindByName(name) is int number
                && ItemNames.ItemTypeOf(number) == Game.Inventory.InventoryManager.KeyItemType);
        // The walker's abandoned-combat halt: the sweep ends in place on it.
        CombatTracker.EngagedTargetAbandoned += _ => DeathRecovery.NoteEngagedTargetAbandoned();
        // Combat-aware re-equip interleaving: recovering a corpse in a room with a
        // live hostile paces the wear/eq burst across combat rounds (each equip
        // breaks the round, same as a between-round cast) instead of firing it all
        // at once. Probes the combat engine for hostiles, re-arms the attack via
        // the same NoteBetweenRoundCast signal a cast uses, holds the walker on the
        // CorpseRecovery gate while pieces are pending, and reads item ArmourClass
        // for the highest-AC-first ordering. The tick drives the pacing/flush.
        DeathRecovery.AttachCombatInterleave(
            () => CombatTracker.HasEngageableHostiles,
            () => Combat.NoteBetweenRoundCast(),
            () => MovementCoordinator.AssertGate(
                Game.Map.MovementCoordinator.CorpseRecoveryGate, "DeathRecovery",
                "recovering — pacing re-equip across combat rounds"),
            () => MovementCoordinator.ClearGate(
                Game.Map.MovementCoordinator.CorpseRecoveryGate, "DeathRecovery",
                "re-equip complete"),
            name => GameData.FindRowByName("Items", name) is { } row
                    && row.TryGetProperty("ArmourClass", out GameValue ac)
                    && ac.ValueKind == System.Text.Json.JsonValueKind.Number
                    && ac.TryGetInt32(out int acv) ? acv : 0);
        Tick.CombatTickElapsed += DeathRecovery.OnRecoveryCombatRound;
        Tick.HeartbeatElapsed += DeathRecovery.OnRecoveryHeartbeat;
        // Gear a party member recovered for us and handed back counts as our deathpile
        // coming home: struck off the pile and re-equipped (Auto-equip on recovery).
        Inventory.ItemReceived += DeathRecovery.OnItemReceived;
        // Route walker over trapped exits (RoomExitHint.Trap) through the
        // TrapDisarmManager.
        Walker.SetTrapEnqueuer(TrapDisarm.Enqueue);
        TrapDisarm.SetCurrentRoom(() => RoomTracker.State.CurrentRoom?.Key);
        // Worn gear's trap abilities, which split the disarm skill from the Traps
        // `stat` shows (the odds shown in route details and map tooltips).
        TrapDisarm.SetWornTrapBonuses(() =>
        {
            Game.Calculators.EquipmentStatSummary t = Game.Calculators.CharacterCalculator
                .AggregateEquipmentStats(Inventory.Snapshot.EquippedItems, GameData).Totals;
            return (t.PlusTraps, t.PlusDisarmTraps);
        });
        // Settings → Other "Utilize disarm traps if able": gate the
        // walker's trap-disarm on the toggle AND a real local capability
        // (a positive Traps stat, or a class/race game-data trap-skill
        // grant when the value hasn't been captured yet). When the gate is
        // false the walker tries party delegation, else steps through.
        Func<bool> trapDisarmGate = () =>
            Resolver.Resolve<Models.Profile.OtherSettings>("Other").UtilizeDisarmTrapsIfAble
            && TrapDisarm.CanDisarm;
        Walker.SetTrapDisarmGate(trapDisarmGate);
        // Party-delegation half of "if able": same toggle, but the LOCAL
        // character can't disarm AND a capable party member can. The
        // walker tries the local gate first, then this; the delegation
        // manager broadcasts @trap on say and resumes on the member's
        // say reply (a signal source kept distinct from the self path).
        Walker.SetTrapDelegator(TrapDelegation.Delegate);
        Func<bool> trapDelegateGate = () =>
            Resolver.Resolve<Models.Profile.OtherSettings>("Other").UtilizeDisarmTrapsIfAble
            && !TrapDisarm.CanDisarm
            && TrapDelegation.AnyPartyMemberCanDisarm();
        Walker.SetTrapDelegateGate(trapDelegateGate);
        Walker.SetTrapDelegateStopper(TrapDelegation.Cancel);
        // Proactive pre-move approach sequence: gear then `sn`, both as the last
        // commands before each walker move so the move itself is sneaked (the
        // reactive RoomTracker hook above only re-sneaks AFTER arriving).
        // Backstab gear goes out FIRST — equipping breaks sneak, so the loadout
        // must land before the sn (weapon → armor → sn → move). PrepBackstabForMove
        // no-ops unless backstab is enabled. Non-blocking; the settled-state
        // guard in StealthManager prevents a double sn when both paths fire.
        // The pre-move gear goes out from the ready check, ahead of its `sn` (equip
        // ends a sneak, so gear then sneak then move); the pre-move hook only covers a
        // move that skipped the ready check. Once per step either way.
        Walker.SetMoveReadyCheck(() =>
        {
            // First of all: a counter for the next room's own spell goes on, and
            // the step waits for it. A wear ends a sneak, so it has to be ahead of
            // the `sn` below.
            if (!CounterWear.ReadyToEnter(NextPlannedRoomForEquip(Walker.PeekNextPlannedDirection()))) return false;
            PreMoveGearOnce(ref _walkerPreMoveGearFor, Walker.PeekNextPlannedDirection());
            if (!HazardLastCallBeforeStep(Walker.PeekNextPlannedDirection())) return false;
            return Stealth.ReadyToMoveSneaking();
        });
        Walker.SetRoomActionHook(cmd => Stealth.NoteSneakBroken($"room command '{cmd}'"));
        Walker.SetPreMoveHook(() =>
        {
            // Swap gear BEFORE the step (queues ahead of the move on the serialized
            // wire, so we land already geared) when the next room is a boss room or a
            // lair the movement set wants pre-swapped, then the backstab loadout.
            PreMoveGearOnce(ref _walkerPreMoveGearFor, Walker.PeekNextPlannedDirection());
            _walkerPreMoveGearFor = null;
            // Clear the per-room AoE-debuff / attack caps so the next room's crabs
            // aren't read as "already debuffed" from the room we're leaving (report
            // paradigm-20260827-082106).
            Combat.NotePreMove();
            Stealth.RequestPreMoveStealth();
        });
        // Announce the route's possession-gated item ids at walk-start so the
        // demand tracker arms auto-search for anything we lack. The
        // party-inventory gate sits ahead of the tracker: it forwards anything
        // the party can't cover to PathItemDemand.OnPathItemsRequired, so with
        // the item's Auto-obtain for path off (or solo) the behaviour is
        // unchanged.
        Walker.SetPathItemAnnouncer(ids =>
        {
            // The hazard resolver below staged this pass's substitutes room by
            // room; commit them before anything counts coverage. An item still
            // being obtained keeps its substitutes across a detour's own announce.
            PathItemSubstitutes.Commit(keep: FetchesForJourney);
            PartyPathItemGate.OnPathItemsRequired(ids);
        });
        // A party count holds the walk that announced it. A walk that ends some
        // other way (stopped, failed, replaced by one the user started) takes the
        // hold with it; a router's own detour replaces the walk silently and keeps it.
        Walker.Event += e =>
        {
            if (e.Kind is Game.Map.WalkEventKind.Stopped or Game.Map.WalkEventKind.Failed
                or Game.Map.WalkEventKind.Finished)
            {
                PartyPathItemGate.OnWalkEnded();
                // The walker ends a journey before it raises the event that ended it.
                PartyHandOvers.ForgetEndedTrips();
            }
            // A card's count is for the walk that card starts. Not on Stopped: the
            // walk a card replaces stops just before the card's own walk announces.
            if (e.Kind is Game.Map.WalkEventKind.Failed or Game.Map.WalkEventKind.Finished)
                _cardCounts.Clear();
        };

        // Fold each entered hazard room's counter into the same walk-start item
        // announce, so a route the user chose to run through a hazard room
        // provisions its counter like an Item/Ticket gate. Single-counter
        // (no-substitute) items always announce; an any-of group's counter
        // announces only when the user forced one via the route picker's "obtain
        // then cross" choice (otherwise the group stays a manual counter choice).
        Walker.SetHazardItemResolver(key => HazardAnnounceItems(key, stage: true));
        // The same list tells route planning which hazard rooms an unpicked
        // through-gates walk will have a counter for by the time it gets there.
        // Planning explores rooms the walk may never enter, so it reads the list
        // without staging substitutes: a staged any-of group (a climbing harness for
        // a rope and grapple) would otherwise reach the party count of a route whose
        // gates take only the one item.
        Movement.HazardProvisionProbe = key => HazardAnnounceItems(key, stage: false);

        // Admit a locked door's key into that same announce, but ONLY when a room
        // command can summon a guaranteed dropper for it — the one key-acquisition
        // chain with no RNG in it. Every other key gate stays unannounced and fails
        // in place, which is what keeps a low-drop lair key (the black star key)
        // from sending a walk on an open-ended hunt.
        // A key an NPC hands over for the asking is as fetchable as one a summoned
        // monster always drops (the old hermit's jagged bone key for the Library).
        Walker.SetDoorKeySourceProbe(DoorKeyIsFetchable);
        // The game counts every monster record in the room, an NPC as much as a
        // hostile, so the roster is read the same way.
        bool RoomHasMonster() => RoomClassifier.Current is { } obs
            && obs.Entities.Any(e => e.Kind == Game.Combat.EntityKind.Monster);
        bool CommandNeedsEmptyRoom(Game.Map.Room room, string command)
            => Game.Map.TBInfoActionResolver.NeedsEmptyRoom(TBInfo, room.Cmd, command);
        Walker.SetRoomClearHooks(
            roomHasMonster: RoomHasMonster,
            requestRoomClear: () => Combat.RequestRestClearEngage(),
            abortPartyReform: () => AutoParty.AbortReformWaits("the teleport was refused"),
            commandNeedsEmptyRoom: CommandNeedsEmptyRoom);
        RoomClassifier.EntitiesObserved += _ => Walker.NoteRoomObserved();

        // Hold a crossing whose gate item is missing but already being fetched,
        // rather than sending an opener and a move that can only fail. Requires a
        // detour to actually be in flight, so a gate nothing can source still fails
        // the normal way instead of parking the walk.
        Walker.SetGateItemHoldProbe(id =>
            id > 0
            && !IsPathItemCovered(id)
            && (PathItemGiveRouter.DetourActive || PathItemShopRouter.DetourActive
                || PathItemSummonRouter.DetourActive || MonsterDropRouter.DetourActive)
            && HasOutstandingPathItemNeed(id));

        // What a walk was told to fetch rides on its journey and is gone with it
        // (JourneyFetch), so there is no list here to clear. What hangs off that list
        // is tidied once a walk ends with nothing left to fetch: the walker ends the
        // journey before it raises the event, so this reads the state after it. The
        // per-item drop on acquisition is wired to Inventory.Changed above.
        Walker.Event += e =>
        {
            if (e.Kind is not (Game.Map.WalkEventKind.Stopped or Game.Map.WalkEventKind.Failed
                or Game.Map.WalkEventKind.Finished))
                return;
            if (JourneyHasFetchOrder) return;
            PathItemSubstitutes.Clear();
            // Walk over before the counter landed — undo a "search en route"
            // auto-search flip so it doesn't leak on past the leg it was for.
            RestoreRouteSearchAutoSearchIfDone("walk ended");
        };
        // A journey can stand with the walker idle (between two of its legs), where
        // no walk event ends it. Another character's trip is not this one's.
        Profile.ProfileLoaded += _ => Walker.EndJourney();
        Profile.ProfileClosed += Walker.EndJourney;

        // Search the room a walk / loop / auto-lair STARTS from. Auto-search fires on
        // room entry, but the room the walker steps out of at the start of a run was
        // entered earlier (before auto-search was armed, or at login) and so never got
        // its entry search (report paradigm-20260909-055045). On the Started event —
        // which fires before the walker's first SendNextStep, so asserting the Search
        // gate here holds that step — search the current room, but only when it's
        // Confirmed (the deferred/Pending Started is skipped; the walk re-raises Started
        // once it settles). Loops route each leg through the walker too, so this also
        // covers a loop's first room; the manager dedupes so later legs (starting from a
        // room already searched on arrival) are no-ops.
        Walker.Event += e =>
        {
            if (e.Kind != Game.Map.WalkEventKind.Started) return;
            if (RoomTracker.State.Confidence != Game.Map.RoomConfidence.Confirmed) return;
            AutoSearch.OnMovementStarting(RoomTracker.State.CurrentRoom?.Key);
        };

        // Boss "stop before" rooms — the walker halts one room short of any boss
        // room flagged StopBefore on the active realm. Resolved live so realm swaps
        // + tab edits take effect without re-wiring; only the point-to-point walker
        // consults it (loops / auto-lair route through boss rooms untouched).
        Walker.SetBossStopRooms(BossStopRooms);
        // A walk the user started reached the room before a stop-before boss room it
        // only passes through: wait there until they press Play, step in, or walk on.
        Walker.SetBossRoomHaltHandler(room =>
        {
            if (!MovementControl.PauseBeforeBossRoom()) return;
            string boss = BossInRoom(room) is { } name ? $"{name}'s room" : "a boss room";
            Avalonia.Threading.Dispatcher.UIThread.Post(() => WriteTerminalNotice(
                $"[Navigation paused before {boss} ({room.Map}/{room.Room}), marked stop before entering - "
                + "Resume to walk through, or step in yourself]"));
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
        // Mirrors LoopRunner.SetConfusedCheck below — same Conditions.IsConfused
        // source, so the walker's replan budget gets the identical confusion
        // exemption as the loop's recovery budget.
        Walker.SetConfusedCheck(() => Conditions.IsConfused);

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
            roomLightSpellIllu: () => RoomLightSpell.IlluForSpell(RoomLightSlotSpell()),
            roomLightSpellName: RoomLightSlotSpell,
            castRoomLightSpell: name => Cast.TryCast(name),
            settings:    () => ReadSection<Models.Profile.AutoLightSettings>(Profile.Current, "AutoLight"),
            log:         Log);
        AutoLightProvisioner.SetSneakKeptProbe(() => SneakGuard.Holds);
        Walker.SetRouteAnnouncer(AutoLightProvisioner.OnRoutePlanned);

        // Keeps a checkspell hazard buff up as the walker crosses a hazard room.
        // Shares the approach-room hook below with the light provisioner: on each
        // committed step it resolves the room's hazard and, for a carried buff
        // source (the desert waterskin), `use`s it so the buff is up on arrival —
        // re-`use`ing only when the buff's own duration would have lapsed so a fast
        // traverse spends one charge. No toggle of its own: a route the user chose to
        // run through a hazard room must survive it. Only the master switch stops it
        // (WireMasterSwitch).
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
            log:            Log,
            // A follower is carried through a hazard by the leader's route, with no
            // walk of its own for the approach hook to ride — it raises on arrival.
            followingLeader: () => PartyState.IsInParty && !PartyState.SelfIsLeader,
            roomsAhead:     HazardRoomsAhead,
            allCounters:    () => RoomHazards.Hazards.SelectMany(h => h.BuffCounters),
            // Asked at the send, for "this `use` spent a sneak". The guard alone holds
            // for a move in flight whether or not the character is sneaking.
            sneakKept:      () => SneakGuard.Holds && Stealth.IsStealthed,
            chargesLeft:    id => ItemNames.GetName(id) is { } name ? CarriedCharges.RemainingForName(name) : null,
            sendBlocked:    () => EngineGate.IsLocked,
            // The early window and the look ahead are for finding a room with no
            // NPCs to drink in, so they come with Auto-Sneak and not without.
            autoSneakOn:    () => ReadAutoModeFlag(d => d.AutoSneak),
            // The buff has no wear-off line, so a room that strips it is known by
            // its spell: the index the cast pass skips buffing by.
            roomStripsBuff: (roomSpell, buffSpell) => RoomBuffStrip.StripsBuff(roomSpell, buffSpell),
            commandStripsBuff: (roomCmd, command, buffSpell) => RoomBuffStrip.CommandStripsBuff(roomCmd, command, buffSpell));
        // The step into a hazard room waits for the answer to the `use` sent ahead
        // of it, and through the round when that answer is that the round's cast was
        // already made.
        AutoHazardCounterProvisioner.SetStepHold(MovementCoordinator, (delay, callback) =>
        {
            var timer = new Avalonia.Threading.DispatcherTimer { Interval = delay };
            timer.Tick += (_, _) => { timer.Stop(); callback(); };
            timer.Start();
        });
        // The refresh inside its window is sent by the between-round cast pass: one
        // cast a round, held while a sneak is being kept, then the fight's resume and
        // the re-sneak. Nothing is offered while the master switch is off. A `use`
        // sent by another road (the step into a hazard room, a lapse prompt) is
        // reported to the same pass, so it counts as the round's cast too.
        CastDirector.SetClientUseSource(
            due: () => AutoModeController.KillSwitchEngaged ? null : AutoHazardCounterProvisioner.DueNow(),
            fire: AutoHazardCounterProvisioner.FireDue,
            heldForSneak: AutoHazardCounterProvisioner.NoteHeldForSneak);
        AutoHazardCounterProvisioner.UseSent += CastDirector.NoteClientUseSent;
        // A `use` that could not wait for a room with no NPCs spent the sneak where
        // the character stands: the same thing, to whatever reacts to a lost sneak,
        // as a sneak that failed on the way in.
        AutoHazardCounterProvisioner.SneakSpentHere += CombatTracker.NoteSilentSneakLoss;
        // What is believed about the buff is dropped wherever it can't be vouched
        // for: a death wipes it, and a new profile or game-data set is another
        // character or other data. A death that happened while the link was down is
        // the inferred one, raised for a verdict reached at the login only: one
        // reached later would wipe a buff raised since (as for the cast pass's
        // timers).
        RoomTracker.PlayerDeathObserved += () => AutoHazardCounterProvisioner.Forget("death");
        RoomTracker.PlayerDeathInferred += () => AutoHazardCounterProvisioner.Forget("death");
        // Leaving the game only stops its clock (BuffTimerKeeper, with the cast
        // pass's timers; MainWindowViewModel's disconnect handler for a drop outside
        // it); the first prompt back in the game starts it again. No-op unless it
        // was stopped.
        PromptScanner.PromptObserved += _ => AutoHazardCounterProvisioner.Resume();
        Profile.ProfileLoaded += _ => AutoHazardCounterProvisioner.Forget("profile loaded");
        GameData.ActiveSetChanged += _ => AutoHazardCounterProvisioner.Forget("game data changed");
        // Every arrival: a room whose own spell strips the buff ends it, and a
        // follower raises the buff of a hazard room it was carried into.
        RoomTracker.StateChanged += t =>
        {
            if (t.NewRoom is { } arrived && !Equals(arrived.Key, t.PreviousRoom?.Key))
                AutoHazardCounterProvisioner.OnArrivedInRoom(arrived.Key);
        };

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
            walkDestination: LightDetourWalkDestination,
            distanceBetween: (a, b) => Bfs.DistanceBetween(a, b, Movement),
            carriedCount: CountItemCarried,
            isEnabled: () => ReadAutoModeFlag(d => d.AutoLight),
            engineWalkActive: () =>
                ErrandOwnsWalk() || LoopRunner.State != Game.Map.LoopState.Idle,
            walkTo: LightDetourWalkTo,
            post: action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            log: Log);
        AutoLightProvisioner.SetProvisioner(AutoLightShopRouter.OnBuyRequested);
        Walker.Event += AutoLightShopRouter.OnWalkEvent;

        ChestSellTour = new Game.Inventory.ChestSellTour(
            currentShop: () => RoomTracker.State.CurrentRoom?.Shop,
            goWalk: ErrandWalkTo,
            chestCount: name => ChestOpens.Loot(Inventory.Snapshot.CarriedItems)
                .FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)).Count,
            sendPaced: cmds => InventoryAction.SendPaced(cmds),
            isParadigm: () => GameData.ActiveRealm == Game.RealmType.ParaMud,
            inventory: Inventory,
            schedule: (ms, action) =>
            {
                var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
                timer.Tick += (_, _) => { timer.Stop(); action(); };
                timer.Start();
            },
            post: action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            log: Log);
        Walker.Event += ChestSellTour.OnWalkerEvent;
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

        // A readied light burning out ("Your <light> flickers and goes out."). The
        // inventory takes it off what is held first, so everything that reads the
        // snapshot (the death record among them) sees the light gone at once and not
        // at the next `i` dump. The provisioner then drops its own latches on that
        // light, so the dark-room line that follows re-readies a carried spare.
        Router.Subscribe(Services.Patterns.KnownPatterns.LightBurnedOut,
            _ => Inventory.NoteLitLightBurnedOut());
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

        // One-shot UI-thread timer; the returned handle cancels it (a flicker's
        // *Combat Off* disposes it before it fires).
        static IDisposable ScheduleOnce(TimeSpan delay, Action callback)
        {
            Avalonia.Threading.DispatcherTimer timer = new() { Interval = delay };
            timer.Tick += (_, _) => { timer.Stop(); callback(); };
            timer.Start();
            return new DispatcherTimerHandle(timer);
        }

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
            log: Log,
            // Room predicates for the While Moving / Bossing sets. Boss rooms resolve
            // live off the active realm's Bosses table (all rooms, not just StopBefore);
            // a lair is the game-data lair tag on the room. isMoving reads the coalesced
            // run-state (Running = an engine is travelling, not held by combat/rest).
            isBossRoom: IsBossRoomLive,
            isLair: k => RoomGraph.GetRoom(k)?.HasLair == true,
            isMoving: () => MovementControl.State == Game.Map.MovementEngineState.Running,
            // Settle the travelling→Default combat swap so Paradigm's rapid
            // *Combat Off* / *Combat Engaged* flicker doesn't thrash movement / Default
            // gear on every brief engage; a fight that outlasts the window still gears up.
            scheduleCombatGearSwap: cb => ScheduleOnce(TimeSpan.FromSeconds(1.5), cb),
            // Hand movement: a typed move only counts when no engine is running (Idle —
            // a paused loop / walk still owns its gear), and its set comes off after the
            // user's idle delay.
            navIdle: () => MovementControl.State == Game.Map.MovementEngineState.Idle,
            scheduleAfter: ScheduleOnce,
            // A sit confirmed within a few seconds of a finished rest is that rest's
            // tail, not a new one.
            restJustEnded: () => Health.RecoveredWithin(TimeSpan.FromSeconds(5)),
            // The Bossing set's "keep on between bosses": is this trip to another boss?
            bossTravel: BossTravelNow,
            resumeRest: Health.ResendRestAfterGearSwap,
            healthOwnsRest: () => Health.RestInFlight);
        // A set held from the Equip menu belongs to the character that picked it, and
        // any other equip asked for by hand (Workshop Equip Now, @equip, Equip All)
        // replaces it: the hold drops and that set goes on.
        Profile.ProfileLoaded += _ => AutoEquip.ReleaseHeldSet(quiet: true);
        Equipment.ManualEquipStarting += () => AutoEquip.ReleaseHeldSet(quiet: true);
        OutboundMovement.MoveSent += AutoEquip.OnMoveSent;
        // A hand-typed move sneaks first, like an engine's own step (ObserveOutbound
        // runs before the typed bytes go out, so the `sn` leaves ahead of them). After
        // the gear hook above: equipping ends a sneak, so any swap goes out first.
        OutboundMovement.MoveSent += Stealth.NoteTypedMove;
        OutboundMovement.DirectionalMoveSent += Stealth.NoteDirectionalMoveSent;
        // Every move re-opens the backstab surprise round, typed moves included.
        OutboundMovement.MoveSent += Combat.NoteMoveSent;
        OutboundMovement.MoveSent += CombatTracker.NoteMoveSent;

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
        MonsterDeath.MonsterDied += evt => LairTimers.NoteKill(evt.At);
        ExpResolver = new Game.Map.RouteExpResolver(RoomGraph, Bfs, LairTimers, GameData, Log);

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

        // The Simulator's rankings follow the set too, but nothing reloads: a switch
        // leaves each set's where they are. A set made active, by a switch to it or by
        // its name coming round again, is a new load of it, with the same tables or
        // other ones: the store is told, and compares each ranking's tables with the
        // new load's when it is next listed. Only one that carries no record of its
        // tables goes here, at any new load of its set.
        // Each ranking is also kept in a file in its set's folder, so it is there
        // after a restart; what a read finds is handed back on the UI thread.
        SimulatorRankings = new Game.Simulation.AreaRankingStore(
            new AreaRankingFiles(), act => Avalonia.Threading.Dispatcher.UIThread.Post(act), Log);
        SimulatorRankings.NoteActiveSet(GameData.ActiveSet);
        // Once the launch has nothing left to do: old ranking files go, off this
        // thread, for characters that may never be loaded again.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            SimulatorRankings.SweepOld, Avalonia.Threading.DispatcherPriority.ApplicationIdle);
        GameData.ActiveSetChanged += setName =>
        {
            int dropped = SimulatorRankings.NoteActiveSet(setName);
            if (dropped > 0)
                Log.Info("Simulator",
                    $"game data '{setName}' was loaded afresh: {dropped} area ranking(s) made on it with no record of its tables dropped, there being nothing to compare them by");
        };
        // A saved ranking is stamped with the table files this client read, and the
        // watch has to see each table's first read of a load before the cache can
        // drop the table and read it again: once a second is far inside that.
        _simulatorWorld = Game.Simulation.RankingWorldWatch.Over(GameData);
        Tick.HeartbeatElapsed += () => _simulatorWorld.Observe(GameData.ActiveLoad);
        // They are a character's as well. One that comes to go by another name is
        // the same character: its rankings go with it, and none stay under the name
        // it left for the next profile given that name.
        Profile.ProfileRenamed += (oldBbs, oldName) =>
        {
            string? was = Game.Simulation.SimulatorOwner.CharacterKey(oldBbs, oldName);
            string? now = Game.Simulation.SimulatorOwner.CharacterKey(Profile.CurrentBbsName, Profile.CurrentProfileName);
            // Save As from a named profile leaves that profile where it was and
            // carries on as a copy of it. A copy is another character: the rankings
            // stay with the one they were made for, and what an earlier profile of
            // the copy's name had kept is not the copy's either.
            if (Profile.LoadedIsACopyOf(oldBbs, oldName))
            {
                SimulatorRankings.ForgetCharacter(now);
                return;
            }
            int carried = SimulatorRankings.RenameCharacter(was, now);
            if (carried > 0)
                Log.Info("Simulator", $"character '{was ?? "(none)"}' is now '{now ?? "(none)"}': {carried} area ranking(s) kept for it go with it");
        };
        // The files are named by BBS and profile, so the same holds for a profile
        // that is not loaded, and a deleted profile's go with it: the name may be
        // given to another character later. A copied profile is another character
        // and takes none.
        Profile.StoredProfileMoved += (was, now) => SimulatorRankings.RenameCharacter(
            Game.Simulation.SimulatorOwner.CharacterKey(was.Bbs, was.Name),
            Game.Simulation.SimulatorOwner.CharacterKey(now.Bbs, now.Name));
        Profile.ProfileDeleted += gone => SimulatorRankings.ForgetCharacter(
            Game.Simulation.SimulatorOwner.CharacterKey(gone.Bbs, gone.Name));
        Profile.BbsRenamed += SimulatorRankings.RenameBbs;
        Profile.BbsDeleted += SimulatorRankings.ForgetBbs;
        // What a kept ranking is checked against is the character as the game last
        // showed it, not as the profile remembers it from an earlier session
        // (SimulatorCharacterAt). Subscribed here, ahead of any window, so the
        // Simulator's own handlers for the same reads find these already set.
        Stats.ScreenParsed += _ => _simulatorStatRead |= Stats.LastCaptureReadHits;
        Inventory.FullInventoryParsed += () => _simulatorInventoryRead = true;
        Profile.ProfileLoaded += _ => ForgetSimulatorCharacterReads();

        // Which loops and auto-lair setups are favourites is each character's own.
        // Built after the first LoadAll above so a profile already loaded finds its
        // game data's loops in hand; it plugs into both managers.
        LoopFavorites = new LoopFavoritesStore(Profile, Loops, Lairs, ProfileGameDataSet, Log);

        // So is how each loop is run: only attack in lair rooms and the lair wait.
        LoopSettings = new LoopSettingsStore(Profile, Loops, Log);

        // Shared folder CRUD over the Loops directory (loops + lairs
        // live in the same on-disk tree). Owns the filesystem move once
        // and reloads both managers, instead of either racing the dir.
        NavFolders = new Game.Map.NavFolderManager(Loops, Lairs, Log);

        // Every client on this set shares the Loops folder: pick up what another one
        // saved, renamed, moved or deleted. The walk runs off the UI thread; the
        // watcher hands its result back there, where a local change raises the same
        // events.
        NavWatcher = new Game.Map.NavFolderWatcher(Loops, Lairs, NavFolders, Log);
        Tick.HeartbeatElapsed += NavWatcher.OnHeartbeat;

        // Game Data → "Manage Sets…" backend. The reload callback re-pulls what
        // a copy/move changed in the active set: the loop/lair caches alone when
        // only the library moved, otherwise the whole set, the way a re-import
        // does, since every per-set store reloads on that. The delete callback
        // clears any profile / global reference that still names a deleted set.
        // The Simulator's rankings are held per set whether or not it is active, so
        // they are told of every set whose data an op replaced.
        GameDataSetManager = new GameDataSetManager(
            GameData,
            reloadActive: changed =>
            {
                if (changed == GameDataSetPart.Loops)
                {
                    Loops.LoadAll(GameData.ActiveSet);
                    Lairs.LoadAll(GameData.ActiveSet);
                    return;
                }
                if (changed.HasFlag(GameDataSetPart.RecordOverrides)) Resolver.DropOverrideCache();
                GameData.ReloadActiveSet();
            },
            onSetDeleted: ClearGameDataSetReferences,
            Log,
            onSetDataReplaced: set => ForgetSimulatorRankings(set, "was changed in Manage Sets"));

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
        // Same live-gate pattern for message-candidate capture.
        MessageCandidateWatcher.Enabled = LogDiagnostics.CaptureUnrecognizedMessages;
        LogDiagnostics.Changed += () =>
            MessageCandidateWatcher.Enabled = LogDiagnostics.CaptureUnrecognizedMessages;

        // Per-BBS room blacklist — hides ganghouse / dead-end rooms
        // from the map render + room search. Loaded on BBS pin so
        // BFS picks it up via the Changed event before the first
        // layout build for the new BBS.
        RoomBlacklist = new RoomBlacklistStore(Log);
        Profile.ProfileLoaded += _ => RoomBlacklist.OnRealmChanged(ActiveRealmFolder());
        Profile.BbsPinApplied += _ => RoomBlacklist.OnRealmChanged(ActiveRealmFolder());
        Tick.HeartbeatElapsed += () => RoomBlacklist.TakeInOutsideChanges();

        // Per-BBS "top N" leaderboard history + its live capture tracker. The
        // store loads on BBS pin (same shape as the blacklist); the tracker binds
        // to the per-session LineExtractor in the MainWindowViewModel constructor
        // and passively snapshots the block whenever the player runs `top <N>`.
        Leaderboards = new LeaderboardSnapshotStore(Log);
        Profile.ProfileLoaded += _ => Leaderboards.OnRealmChanged(ActiveRealmFolder());
        Profile.BbsPinApplied += _ => Leaderboards.OnRealmChanged(ActiveRealmFolder());
        Tick.HeartbeatElapsed += () => Leaderboards.TakeInOutsideChanges();
        LeaderboardCapture = new Game.Leaderboard.LeaderboardCaptureTracker(Leaderboards, PromptScanner, Log);
        // The top list states each listed player's class outright; put it on the
        // records of the players we know. Off the capture, not the store's load: a
        // realm switch loads the list before the players, and the old realm's
        // records must not take the new realm's classes.
        Leaderboards.Captured += snapshot =>
        {
            // The top list names players `who` may not be showing right now, with
            // their class: a record is made for one we had none for.
            DateTime now = DateTime.UtcNow;
            foreach (Game.Leaderboard.LeaderboardEntry entry in snapshot.Entries)
            {
                if (Players.Find(entry.Name) is null)
                    Players.RecordObservation(entry.Name, entry.Class, null, null, null, null, null, now);
                else
                    Players.RecordStatedClass(entry.Name, entry.Class);
            }
        };
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
        Loops.NamesInUse = () => new[] { LoopRunner.CurrentLoop?.Name, LoopRunner.StagedLoop?.Name }
            .OfType<string>().ToArray();
        // A waypoint's command block: the loop moves on once the game has answered it,
        // and stays while a fight it started runs.
        LoopRunner.SetInCombatProbe(() => PlayerState.InCombat);
        LoopRunner.SetRoomClearHooks(
            roomHasMonster: RoomHasMonster,
            requestRoomClear: () => Combat.RequestRestClearEngage(),
            abortPartyReform: () => AutoParty.AbortReformWaits("the teleport was refused"),
            commandNeedsEmptyRoom: CommandNeedsEmptyRoom,
            schedule: ScheduleOnce);
        RoomClassifier.EntitiesObserved += _ => LoopRunner.NoteRoomObserved();
        // A confusion fumble ("You convulse violently!" / "You fumble in
        // confusion!") can bonk several consecutive moves in a row well inside
        // the loop's bounded recovery budget; EnterRecovery reads this to avoid
        // charging those against it (report paradigm-20260902-113201).
        LoopRunner.SetConfusedCheck(() => Conditions.IsConfused);
        // Trapped exits mid-circuit: the walker's disarm / delegate / walk-through
        // decision, through the same managers.
        LoopRunner.SetTrapHandling(TrapDisarm.Enqueue, trapDisarmGate,
            TrapDelegation.Delegate, trapDelegateGate, TrapDelegation.Cancel);
        // A loop set to wait for its debuff stands one step short of a lair until the
        // round's between-round cast is free (LairEntryDebuffHold).
        LairDebuffHold = new Game.Map.LairEntryDebuffHold(
            MovementCoordinator,
            slotUsed: () => CastDirector.BetweenRoundSlotUsed,
            castDue: () => CastDirector.HasCastDue(),
            scheduleAfter: (delay, action) =>
            {
                var timer = new Avalonia.Threading.DispatcherTimer { Interval = delay };
                timer.Tick += (_, _) => { timer.Stop(); action(); };
                timer.Start();
            },
            log: Log,
            buffsDue: priority => CastDirector.BuffsDue(priority),
            stillWanted: LairWaitStillWanted,
            healOrCureDue: () => CastDirector.HealOrCureDue(),
            debuffMana: LairEntryDebuffMana,
            inCombat: () => PlayerState.InCombat,
            manaIsFull: () => Health.ManaIsAtGameFull,
            // Where the combat engine itself judges a fight to be: the room a loop
            // step in flight is entering, else the one the tracker has us in.
            fightRoom: () => CombatSuppressionVerdict().Room,
            // The loop's own "do not attack" / "only attack in lair rooms" alone: a
            // fight with a player reads as suppressed to the engage gates too, and is
            // a fight all the same. A force-clear fights where the loop doesn't.
            fightAnswered: () => !CombatSuppressionVerdict().Suppressed || Combat.ForceClearActive);
        // The room at the door is fought in while the loop stands there for mana
        // (LoopCombatSuppression.LoopIsDriving), and nothing re-asks the engage gates
        // when that starts: a monster already in the room would be left until its
        // next swing. Posted: the hold says so from inside the loop's own step check.
        LairDebuffHold.ManaWaitStarted += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            CombatTracker.OnAutoAttackChanged();
            RoomClassifier.ReemitCurrent();
        });
        CastDirector.SetLairEntryBuffHold(LairDebuffHold);
        CastDirector.SetManaRegenBuffProbe(IsManaRegenRollSpell);
        // An engage with no debuff to cast first: the hold has nothing to keep the
        // round's cast for, and lets the buffs through.
        Combat.SetNoDebuffOnEngageSink(() => LairDebuffHold.NoteNoDebuffThisFight());
        // A loop that stops or fails never asks again, so its wait is ended here rather
        // than left to the hold's own next look.
        LoopRunner.Event += e =>
        {
            if (e.Kind is Game.Map.LoopEventKind.Stopped or Game.Map.LoopEventKind.Failed)
                LairDebuffHold.Reset("the loop stopped");
        };
        // Same proactive pre-move approach sequence for loop circuits — backstab
        // gear before the sneak (equipping breaks sneak), then the move.
        LoopRunner.SetMoveReadyCheck(() =>
        {
            // A "rest up here" room holds the step until the rest is done.
            if (Health.HoldForRestHere()) return false;
            // The next room's counter, ahead of the lair's entry buffs, the pre-move
            // gear and the `sn`: the buffs are raised for the step, and a wear that
            // held it afterwards would spend their time; a wear also ends a sneak.
            if (!CounterWear.ReadyToEnter(NextPlannedRoomForEquip(LoopRunner.PeekNextPlannedDirection()))) return false;
            Game.Map.LairEntryDebuffMode lairWait = LairEntryDebuffModeForNextStep(out Game.Map.RoomKey? lair);
            if (!LairDebuffHold.ReadyToEnter(lairWait, lair)) return false;
            PreMoveGearOnce(ref _loopPreMoveGearFor, LoopRunner.PeekNextPlannedDirection());
            if (!HazardLastCallBeforeStep(LoopRunner.PeekNextPlannedDirection())) return false;
            return Stealth.ReadyToMoveSneaking();
        });
        LoopRunner.SetPreMoveHook(() =>
        {
            // Pre-step gear swap for a boss / lair room on a loop lap, then the
            // backstab loadout (see the walker hook above for the wire ordering).
            PreMoveGearOnce(ref _loopPreMoveGearFor, LoopRunner.PeekNextPlannedDirection());
            _loopPreMoveGearFor = null;
            // Same per-room cap reset the walker does — a loop circuit that hunts the
            // same species room-to-room otherwise fires its AoE debuff only in the
            // first room (report paradigm-20260827-082106).
            Combat.NotePreMove();
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
        // party so every follower re-anchors to the new circuit.
        LoopRunner.Event += e =>
        {
            if (e.Kind != Game.Map.LoopEventKind.ReachedFirstWaypoint) return;
            // No loop starts by itself with the master switch off, but one frozen
            // on it can still be restarted under it (an avoid-list edit re-routes
            // by Stop + Start): that must not swap gear, wipe the session's stats
            // or telepath @reset to the party.
            if (AutoModeController.Blocks("Loop start reset")) return;
            // A loop actually beginning is one of the moments the Default gear set
            // may auto-equip (we're moving out under normal combat gear). Auto-Lair
            // start does the same via AutoLair.ActiveChanged below.
            AutoEquip.OnLoopStarted();
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

        // Session Stats walk pace: time each walk / loop step from its move going
        // out (the tracker drops Confirmed -> Pending) to the new room landing. Only
        // that span counts, so fights, rests and gates between steps stay out of
        // the average. A transition that leaves the room unconfirmed or unchanged
        // (a refused move, a re-look, Suspect / Lost) drops the step uncounted.
        RoomTracker.StateChanged += t =>
        {
            bool moved = t.PreviousRoom is { } from && t.NewRoom is { } to && !from.Key.Equals(to.Key);
            if (moved && t.NewConfidence is Game.Map.RoomConfidence.Confirmed or Game.Map.RoomConfidence.Pending)
            {
                SessionActivity.NoteStepArrived();
                return;
            }
            if (!moved && t.NewConfidence == Game.Map.RoomConfidence.Pending)
            {
                if (t.PreviousConfidence == Game.Map.RoomConfidence.Confirmed
                    && (Walker.State == Game.Map.WalkState.Walking || LoopRunner.State == Game.Map.LoopState.Running))
                    SessionActivity.NoteStepSent();
                return;
            }
            if (SessionActivity.NoteStepAbandoned())
                Log.Debug("SessionStats", $"walk step not timed: {t.PreviousConfidence} -> {t.NewConfidence} without a new room");
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
        // Auto-sell holds movement while it sells, with a result timeout off a
        // UI-thread one-shot.
        AutoSell.SetMovementGate(MovementCoordinator);
        AutoSell.SetScheduler((delay, callback) =>
        {
            var timer = new Avalonia.Threading.DispatcherTimer { Interval = delay };
            timer.Tick += (_, _) => { timer.Stop(); callback(); };
            timer.Start();
        });

        // Deterministic Auto-Lair scheduler — picks the next marked
        // lair to enter based on respawn timers + travel cost, parks
        // at a wait-room one hop short, then steps in on the tick.
        AutoLair = new Game.Map.AutoLairManager(
            Walker, RoomTracker, RoomGraph, Bfs, LairTimers, Log, MovementCoordinator);
        // Nothing made of one game data's room numbers is saved under another: lair
        // marks on the map are dropped, with a notice, when the set changes to another,
        // as a loop being built is. Posted: a set change can come from inside the line
        // pump, where writing to the terminal re-enters it.
        GameData.ActiveSetChanged += set => AutoLair.DropMarksOfOtherGameData(set);
        AutoLair.MarksOfOtherGameDataDropped += (markedOn, active) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => WriteTerminalNotice(
                $"[Lair marks were dropped: their rooms belonged to game data '{markedOn ?? "(none)"}', and '{active ?? "(none)"}' is active now]"));

        // Auto-Lair beginning a run is a loop-start for gear purposes — swap to the
        // Default set, same as LoopRunner's ReachedFirstWaypoint above.
        AutoLair.ActiveChanged += active => { if (active) AutoEquip.OnLoopStarted(); };

        PvpFlee = new Game.Pvp.PvpFleeWalk(
            Walker, LoopRunner, AutoLair, pacedReplyScheduler,
            startSprint: () => ApplyRunStartMode?.Invoke(Game.Map.RunStartMode.Sprint),
            Log);
        _pvpFightHeld = new Game.Map.HeldDetourResume(
            Walker, LoopRunner, AutoLair, heldFor: "a fight", stillWaiting: () => PvpFight.IsActive, Log);
        PvpFight = new Game.Pvp.PvpFight(
            Router, RoomClassifier,
            pvpEnabled: () => ResolveActiveRealm()?.Realm.PvpEnabled == true,
            inParty: PartyState.HasMember,
            readSettings: () => ReadSection<Models.Profile.PvpSettings>(Profile.Current, "Pvp"),
            attackCommandFor: PvpAttackCommandFor,
            send: text => _engineWireSend?.Invoke(System.Text.Encoding.Latin1.GetBytes(text + "\r")),
            cast: (code, target) => Cast.TryCast(code, target),
            spellInfo: PvpSpellInfoFor,
            manaMeets: PvpManaMeets,
            stepToward: PvpStepToward,
            exitsHere: PvpChaseExits,
            // Stop-and-restart, as for the errand detours: the fight may walk (a
            // chase), which a movement gate would hold.
            suspendEngines: why => _pvpFightHeld.Suspend(why),
            // A fight that interrupted nothing has nothing to get back to, and nor
            // has one whose way back a change of game data dropped.
            backOnTask: () => _pvpFightHeld.Interrupted == Game.Map.DetourResumeKind.None
                || MovementControl.State != Game.Map.MovementEngineState.Idle,
            leadingParty: () => PartyState.SelfIsLeader && PartyState.Members.Count > 1,
            schedule: pacedReplyScheduler,
            log: Log);
        PvpFight.Reported += what => WriteTerminalNotice($"[PvP: {what}]");
        PvpFight.Started += given => PvpResponse.NoteWeAttack(given);
        // A sweep's leg is not a walk the fight's suspend can see and stop, and a
        // neighbours-only sweep doesn't give way to engines by itself: the fight
        // ends either at once, before the walker takes another step out of the room.
        // The sweep only: a Recover Now still walking to the death room is a journey
        // the fight suspends and resumes, and stays the Recover Now's.
        PvpFight.Started += _ => DeathRecovery.EndSpillSweep("a fight with a player began");
        PvpStrangers = new Game.Pvp.PvpStrangerLookup(
            Router, RoomClassifier, Players,
            pvpEnabled: () => ResolveActiveRealm()?.Realm.PvpEnabled == true,
            sendWho: () => _engineWireSend?.Invoke(System.Text.Encoding.Latin1.GetBytes("who\r")),
            schedule: pacedReplyScheduler,
            log: Log);
        PvpRoom.SetStrangerProbe(PvpStrangers.MayBePlayer);
        // The combat engine stands down for the fight and picks the room back up
        // after it; either way it only re-decides on a room observation.
        PvpFight.ActiveChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(RoomClassifier.ReemitCurrent);
        // The chase's own steps go through the walker; its finish is the arrival.
        Walker.Event += e =>
        {
            if (!PvpFight.IsActive) return;
            if (e.Kind == Game.Map.WalkEventKind.Finished) PvpFight.NoteStepLanded();
            else if (e.Kind is Game.Map.WalkEventKind.Failed or Game.Map.WalkEventKind.Stopped)
                PvpFight.NoteStepFailed();
        };
        RoomDeparture.PlayerDeparted += PvpFight.NotePlayerDeparted;
        RoomTracker.PlayerDeathObserved += () => PvpFight.Stop("we died", resume: false);
        PvpResponse = new Game.Pvp.PvpResponder(
            RoomClassifier, PvpAttacks, Players,
            pvpEnabled: () => ResolveActiveRealm()?.Realm.PvpEnabled == true,
            inParty: PartyState.HasMember,
            readSettings: () => ReadSection<Models.Profile.PvpSettings>(Profile.Current, "Pvp"),
            hangUp: Health.HangUpForPvp,
            fleeRooms: Health.FleeFromPlayer,
            fleeTo: (room, comeBackAfter, why) =>
                PvpFlee.Start(new Game.Map.RoomKey(room.Map, room.Room), comeBackAfter, why),
            fight: (given, chase, why) => PvpFight.Engage(given, chase, why),
            // `bg` is the gang channel's speak verb (GAME_MECHANICS "Gang channel speak verb").
            sendGang: text => _engineWireSend?.Invoke(System.Text.Encoding.Latin1.GetBytes($"bg {text}\r")),
            roomName: () => RoomTracker.State.CurrentRoom?.Name,
            schedule: pacedReplyScheduler,
            log: Log,
            isPartyFollower: () => PartyState.IsInParty && !PartyState.SelfIsLeader)
        {
            IsMasterSwitchOff = () => AutoModeController.KillSwitchEngaged,
            SkippedForMasterSwitch = why => AutoModeController.Blocks("PvP response", why),
        };
        PvpResponse.Responded += what => WriteTerminalNotice($"[PvP: {what}]");

        // Always-alive control surface over the three movement engines.
        // Backs the toolbar Start / Pause / Stop buttons (which outlive
        // the window-scoped NavigationViewModel) and stays in sync with
        // the Nav window because both act on the same engine primitives.
        MovementControl = new Game.Map.MovementController(
            Walker, LoopRunner, AutoLair, MovementCoordinator, Log);
        // A loop the user starts from off it is a walk-to to the loop first; the loop
        // runner takes over when that walk arrives. Stop, a profile change or another
        // loop starting drops the one waiting.
        LoopHandoff = new Game.Map.LoopWalkHandoff(
            LoopRunner, action => Avalonia.Threading.Dispatcher.UIThread.Post(action), Log);
        Walker.Event += LoopHandoff.OnWalkerEvent;
        LoopRunner.Event += LoopHandoff.OnLoopEvent;
        MovementControl.Stopping += () => LoopHandoff.Cancel("stopped");
        Profile.ProfileLoaded += _ => LoopHandoff.Cancel("another character was loaded");
        Profile.ProfileClosed += () => LoopHandoff.Cancel("the character was closed");
        // A pyramid climb or an asylum maze solve counts as navigation running: the
        // toolbar's Pause holds it on the user gate and Stop ends it.
        MovementControl.AddSolver(
            active: () => PyramidSolver.Active, held: () => PyramidSolver.IsHeld, stop: PyramidSolver.Cancel);
        PyramidSolver.StateChanged += MovementControl.NoteSolverStateChanged;
        MovementControl.AddSolver(
            active: () => MazeSolver.Active, held: () => MazeSolver.IsHeld, stop: MazeSolver.Cancel);
        MazeSolver.StateChanged += MovementControl.NoteSolverStateChanged;
        // So does a Stock spill sweep: the walker is idle while it looks through
        // exits, gets and searches, and Stop and Pause must reach those stretches too.
        MovementControl.AddSolver(
            active: () => DeathRecovery.SpillSweepActive,
            held: () => DeathRecovery.SpillSweepHeld,
            stop: DeathRecovery.StopSpillSweep);
        DeathRecovery.SpillSweepStateChanged += MovementControl.NoteSolverStateChanged;
        // A sweep still waiting to start isn't running, so the solver list doesn't
        // reach it; Stop calls it off here.
        MovementControl.Stopping += DeathRecovery.DropDeferredSweep;

        // Auto-hide hides a character that has come to rest, not one being walked:
        // at rest is no walk, loop or Auto-Lair running (held for a rest or a lair
        // wait counts as stopped) and no move of ours still unanswered. Whatever was
        // walking it stopping is a chance to hide, as an arrival is.
        //
        // Nor one with a fight on or about to start. "In combat" comes up only when
        // the game answers the attack, so a `hid` went out between our attack and its
        // *Combat Engaged*, to be refused (GAME_MECHANICS "Hiding — sneak vs hide,
        // the hide state machine, and search reveals"). The combat gate is up from
        // the room's roster and the engine's target from the attack, both before that.
        //
        // Nor one sharing the room with a monster that could attack: the game
        // refuses that hide on both realms, whether or not we fight it (same topic;
        // user, 2026-10-12). Told off the room's hostiles with Auto-Combat on or off.
        Stealth.SetHideDwell(
            isAtRest: () => MovementControl.State != Game.Map.MovementEngineState.Running
                            && RoomTracker.State.Confidence != Game.Map.RoomConfidence.Pending
                            && !MovementCoordinator.IsGateAsserted(Game.Map.MovementCoordinator.CombatGate)
                            && Combat.CurrentTarget is null && Combat.CastingSpellTarget is null
                            && !CombatTracker.HasHostileMonster,
            hideAfter: (delay, action) => ScheduleOnce(delay, action));
        MovementControl.StateChanged += () =>
        {
            if (MovementControl.State != Game.Map.MovementEngineState.Running) Stealth.NoteIdleOpportunity();
        };

        // Gear driven by movement + room, for the While Moving / Bossing sets. Both
        // no-op unless the user enabled + filled the set (AutoEquipCoordinator guards).
        // The coalesced run-state drives the movement set: Running = travelling (a
        // pause for combat/rest is Paused, not Idle, so those keep their own gear);
        // Idle = a walk-to arrived or a run stopped → revert to Default. The tracked
        // room drives the Bossing set on confirmed entry/exit.
        MovementControl.StateChanged += () =>
        {
            switch (MovementControl.State)
            {
                case Game.Map.MovementEngineState.Running: AutoEquip.OnMovementStarted(); break;
                case Game.Map.MovementEngineState.Idle:    AutoEquip.OnMovementStopped(); break;
            }
            // After the set revert above. A walk's plan is what a counter is put on
            // ahead of when sneaking past things: one worn for a room the walk
            // never reached goes back to that set now.
            CounterWear.Recheck();
        };
        RoomTracker.StateChanged += t =>
        {
            if (t.NewConfidence != Game.Map.RoomConfidence.Confirmed || t.NewRoom is not { } nr) return;
            AutoEquip.OnRoomChanged(t.PreviousRoom?.Key, nr.Key);
            // Following, a new room means the leader moved: an idle leader gets asked again.
            if (t.PreviousRoom?.Key != nr.Key) LeaderBossTravel?.NoteMoved();
        };

        // Roomba Mode — see GhSweepManager. Built on the same LoopRunner
        // rather than its own navigation engine; refuses to start while
        // MovementControl shows another engine (walk / loop / auto-lair)
        // active.
        GhSweep = new Game.Map.GhSweepManager(
            GhRoomLabels, LoopRunner, RoomTracker, Bfs, GroundItems, ItemNames, Router, MovementCoordinator,
            isOtherEngineBusy: () => MovementControl.IsActive,
            log: Log,
            isParadigm: onParadigm,
            inventory: Inventory,
            itemLocations: GhItemLocations,
            isRoomActivelyManaged: GhManagedRooms.IsManaged,
            // Meters the get/drop batch: one command per prompt, so a room full of
            // items can't outrun the game's command-rate limit and have the whole
            // batch — plus the loop's next move — silently dropped.
            promptScanner: PromptScanner,
            // Carries an interrupted sweep forward: the items it was holding are
            // still in the pack with only its queue knowing where each belonged,
            // and its remaining plan is a full lap of the circuit to rebuild.
            suspendedStore: GhSuspendedSweep,
            activeGameData: () => RoomGraph.ActiveSet);

        // The sweep manager is app-scoped but a resumable sweep is per-character:
        // drop the in-memory leftover on a character switch so Resume doesn't offer
        // one character's load to the next (the persisted manifest is already keyed
        // per profile). Same reset intent as SysStatus.ResetAutoDisable above.
        Profile.ProfileLoaded += _ => GhSweep.OnProfileLoaded();

        // A typed step into a room whose spell a carried item negates: the wear goes
        // out here, ahead of the step's own bytes, as a typed step's `sn` does. Only
        // a compass step names its room; any other move is countered on arrival.
        RoomTracker.ManualMoveObserved += command =>
        {
            if (Game.Map.DirectionExtensions.TryFromToken(command, out Game.Map.Direction direction)
                && NextPlannedRoomForEquip(direction) is { } next)
                CounterWear.BeforeTypedStep(next);
        };
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
        //
        // Nobody pressed Pause, so the pause says what caused it: in the terminal, on
        // the Navigation window's hold chips, and in the bug report.
        RoomTracker.ManualMoveObserved += command =>
        {
            if (!MovementControl.IsActive || MovementControl.IsUserPaused) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!MovementControl.IsActive || MovementControl.IsUserPaused) return;
                Log.Info("Navigation",
                    $"manual movement command '{command}' — pausing navigation (user override; press Start to resume)");
                MovementControl.PauseForTypedMove(command);
                if (MovementControl.PausedByTypedMove is not null)
                    WriteTerminalNotice($"[Navigation paused: you typed '{command}' - Resume to carry on]");
            });
        };

        // Auto-All kill switch also parks navigation: engaging it suspends any
        // in-flight walk / loop / auto-lair (retaining where it is), and restoring
        // it resumes exactly that. Both the toolbar button and the @auto-all remote
        // funnel through AutoModeController.ToggleAll, so this one bridge covers
        // both. The switch's own handler is OnMasterSwitchChanged, subscribed in
        // WireMasterSwitch once every service it reaches exists.
        // Auto-equip on recovery follows the same switch: a corpse recovered by hand
        // with Auto-All off keeps its worn gear in the pack until it is back on.
        DeathRecovery.SetAutoEnabledProbe(() => !AutoModeController.KillSwitchEngaged);

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
            MovementControl.DropQueuedRun();
            // After the engines: a run their stop didn't end (a wait, a rest) and
            // the events queued behind it end here.
            Events.NoteDeath();
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

        // The death record has taken its copy of the pile by the time this is raised,
        // so the inventory record can be marked stale here and re-read at the graveyard.
        InventoryAfterDeath = new Game.Inventory.PostDeathInventoryRefresh(
            markStale: Inventory.MarkStale,
            requestInventory: () =>
            {
                Log.Info(Game.Inventory.InventoryManager.LogCategory,
                    "Re-reading the inventory after a death: what was worn and carried went with the pile.");
                SendGameCommand("i");
            });
        RoomTracker.PlayerDeathObserved += InventoryAfterDeath.OnDeath;
        RoomTracker.StateChanged += _ =>
        {
            if (RoomTracker.State.CurrentRoom is not null) InventoryAfterDeath.OnRoomKnown();
        };
        Profile.ProfileLoaded += _ => InventoryAfterDeath.Reset();
        // Sent into a held send gate, or with the character out of the game (a link
        // dropped from the board's menu lets the gate go), the `i` was lost and
        // never asked again.
        InventoryAfterDeath.SendHeld = () => EngineGate.IsLocked || !InGameCapture.InGame;

        // The toll gate hears of the death too, with the room died in (an arena
        // death takes nothing). The stale record above reads as "purse unknown",
        // which a toll is not refused on, and the coin is known gone: until the
        // re-read lands a walk from the graveyard would head for a toll it can't pay
        // (report paradigm-20261010-145529).
        RoomTracker.PlayerDeathObserved += () => Movement.NoteDeath(
            RoomTracker.LastDeathRoom, RoomTracker.LastDeathTookNothing);
        Movement.TripUnderWayProbe = () => MovementControl.IsActive;
        MovementControl.StateChanged += () =>
        {
            if (MovementControl.IsIdle) Movement.NoteTripEnded();
        };
        GameData.ActiveSetChanged += _ => Movement.ForgetRefusedCrossings();

        // The read a refused toll asks for: owed while the master switch is off,
        // the character is out of the game or the send gate is held, and sent when
        // that ends (SendOwedInventoryReads).
        PurseRead = new Game.Inventory.OwedPurseRead(
            held: () => AutoModeController.Blocks("Info polls") || EngineGate.IsLocked || !InGameCapture.InGame,
            send: () =>
            {
                Log.Info(Game.Inventory.InventoryManager.LogCategory,
                    "Re-reading the inventory: the game refused a toll or fare the purse on record covered.");
                SendGameCommand("i");
            },
            schedule: uiOneShot);
        // In this order: the gate believes the record again before a re-plan held
        // for the read (below) is let go by it.
        Inventory.FullInventoryParsed += () =>
        {
            Movement.NotePurseRead();
            PurseRead.Settle();
        };
        Profile.ProfileLoaded += _ => PurseRead.Settle();
        EngineGate.Released += SendOwedInventoryReads;
        InGameCapture.InGameChanged += inGame =>
        {
            if (inGame) SendOwedInventoryReads();
        };
        // A walk's or a loop's re-plan after a refused toll waits for that read.
        Walker.SetPurseReadWait(PurseRead.WaitForAnswer);
        LoopRunner.SetPurseReadWait(PurseRead.WaitForAnswer);

        // A walk or loop with no route for want of a toll or fare says so on the
        // terminal. Once per reason until a walk gets going: an engine that keeps
        // asking for the same trip would otherwise repeat it.
        string? lastUnpaidNotice = null;
        void NoticeUnpaid(string notice)
        {
            if (notice == lastUnpaidNotice) return;
            lastUnpaidNotice = notice;
            // Posted: a re-plan can fail from inside the emulator's message pump.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => WriteTerminalNotice(notice));
        }
        Walker.SetUnpaidCrossingHandler((destination, reason) =>
        {
            string where = RoomGraph.GetRoom(destination)?.Name is { Length: > 0 } name
                ? $"{destination} ({name})" : destination.ToString();
            NoticeUnpaid($"[Navigation: no walk to {where} - {reason}]");
        });
        LoopRunner.SetUnpaidCrossingHandler(reason => NoticeUnpaid($"[Navigation: {reason}]"));
        Walker.Event += e =>
        {
            if (e.Kind == Game.Map.WalkEventKind.Started) lastUnpaidNotice = null;
        };

        // A party at a toll exit, an NPC's paid transport or a sailing (user,
        // 2026-10-10): every follower's purse is asked before the leader's engine
        // goes through, whoever is short or silent is handed the coin when the
        // Cash setting allows it and we can spare it, and otherwise the crossing
        // is closed to the party, so the route goes round or the walk ends saying
        // why. What we spare leaves our own toll or fare and a trip's reserved
        // fees alone.
        PartyToll = new Game.Map.PartyTollGate(
            PartyWealth,
            ownPurse: () => Inventory.IsLoaded ? Inventory.Snapshot.Currency.TotalCopperValue : (long?)null,
            reservedCopper: () => Movement.ReservedCopper,
            holdings: () => Inventory.IsLoaded ? Inventory.Snapshot.Currency : null,
            runicName: () => Currency.RunicName,
            send: cmd => SendGameCommand(cmd),
            assertGate: reason => MovementCoordinator.AssertGate(
                Game.Map.MovementCoordinator.PartyTollGate, nameof(PartyToll), reason),
            clearGate: reason => MovementCoordinator.ClearGate(
                Game.Map.MovementCoordinator.PartyTollGate, nameof(PartyToll), reason),
            schedule: uiOneShot,
            roomName: key => RoomGraph.GetRoom(key)?.Name,
            log: Log)
        {
            MasterSwitchOff = MasterSwitchOff("Party polls"),
            // Read when asked: the comeback manager is built further down.
            WentBackFor = (given, from, to) => PartyComeback.WentBackFor(given, from, to),
            KnownLeftBehind = () => PartyComeback.KnownLeftBehind(),
            // Posted: the hand-over starts from inside the message pump.
            Notice = text => Avalonia.Threading.Dispatcher.UIThread.Post(
                () => WriteTerminalNotice($"[Party toll: {text}]")),
        };
        Movement.PartyTollClosedProbe = exit => PartyToll.Closes(in exit);
        Movement.PartyTollClosedReason = exit => PartyToll.DescribeClosed(in exit);
        Walker.SetTollStepCheck((from, dir, exit, later) => PartyToll.BeforeTollStep(from, dir, in exit, later));
        LoopRunner.SetTollStepCheck((from, dir, exit, later) => PartyToll.BeforeTollStep(from, dir, in exit, later));
        Walker.SetSailingCheck((passage, later) => PartyToll.BeforeSailing(in passage, later));
        Inventory.CoinsGivenAway += (recipient, copper) => PartyToll.OnCoinsGivenAway(recipient, copper);
        Inventory.GiveRefused += recipient => PartyToll.OnGiveRefused(recipient);
        Inventory.CoinGiveMisaimed += () => PartyToll.OnCoinGiveMisaimed();
        Inventory.CoinGiveLackedCoins += () => PartyToll.OnCoinGiveLackedCoins();
        // A member paid for at a toll has spent it once the room beyond is confirmed.
        RoomTracker.StateChanged += t =>
        {
            if (t.PreviousRoom is { } left && t.NewRoom is { } now && left.Key != now.Key)
                PartyToll.NoteRoomChanged(left, now.Key);
        };
        Profile.ProfileLoaded += _ => PartyToll.Reset();
        // The check's two Cash settings are handed to it here, on the UI thread,
        // when the profile loads, is saved (a Settings save writes the section and
        // then saves) or closes: a route search asks the check from its own
        // thread, where the profile's settings dictionary is not safe to read.
        // The character's own Cash section, as every engine reads its settings;
        // the floor by the conversion the deposit and the train funding use.
        void RefreshPartyTollSettings()
        {
            Models.Profile.CashSettings cash = ReadSection<Models.Profile.CashSettings>(Profile.Current, "Cash");
            PartyToll.FundsShortMembers = cash.FundPartyTolls;
            PartyToll.KeepOnHandCopper =
                cash.KeepOnHandWealth * Game.Inventory.CurrencyHoldings.CopperUnit(cash.KeepOnHandDenomination);
        }
        RefreshPartyTollSettings();
        Profile.ProfileLoaded += _ => RefreshPartyTollSettings();
        Profile.ProfileSaving += _ => RefreshPartyTollSettings();
        Profile.ProfileClosed += RefreshPartyTollSettings;
        // A toll closed to the party stays closed for the trip it was closed on.
        MovementControl.StateChanged += () =>
        {
            if (MovementControl.IsIdle) PartyToll.NoteTripEnded();
        };
        // The purses asked for as a route was planned are in: a toll further on
        // that the party can't be taken through is turned from now, not at its gate.
        PartyWealth.RoundSettled += () =>
        {
            PartyToll.RefreshLeftBehind();
            Walker.ReplanIfATollAheadIsClosed();
            LoopRunner.ReplanIfATollAheadIsClosed();
        };

        // A held or knocked-down character can't walk and isn't dragged by a leader,
        // so a move that lands proves a latched hold is stale (its wear-off line was
        // missed). Without this the walker sits "Paused by: Held" and the hold cure
        // re-casts every window for as long as the session lasts (report
        // paradigm-20261003-161904).
        RoomTracker.MoveConfirmed += () =>
            Conditions.ClearFlag(Models.GameData.MessageFlags.MovementPrevented, "a move went through");

        // Same reasoning as the condition reset above, for the attack-spell
        // cascade and buff-duration tracking: death is a full server-side reset
        // (every buff drops, whatever spell was mid-flight is moot), but nothing
        // previously told CombatManager or CastingDirector that. A stale
        // spell-attack-owed latch or a buff timer for a duration the server
        // already cleared otherwise survives indefinitely — the former silently
        // blocks every automatic heal/cure/bless, the latter suppresses a
        // legitimate recast (report paradigm-20260824-012300).
        RoomTracker.PlayerDeathObserved += () => Combat.OnPlayerDeath();
        // Our death wipes only OUR buffs — clear the self timers; party members stayed
        // alive, so their buff timers we hold are kept (don't re-bless them because we
        // died). A party MEMBER's death wipes THEIR buffs — clear the timers we hold on
        // that name ("<Name> has died." also fires for mobs, but that's a no-op since we
        // hold no timer for them).
        RoomTracker.PlayerDeathObserved += () => CastDirector.ClearSelfBuffTracking();
        Router.Subscribe(Services.Patterns.KnownPatterns.PartyMemberDied, r =>
        {
            if (r.Groups.Count > 0) CastDirector.ClearMemberBuffTimers(r.Groups[0]);
        });

        // Death drops us from the party server-side — a follower is removed, a
        // leader's party disbands. PlayerDroppedGate already clears our roster on the
        // HP<=0 drop, but an INSTANT death (`suicide`) skips mortally-wounded, so that
        // hook never fires and a leader gets no "no longer following" line either.
        // Clear on the death event too, so `@join`/`@invite` don't keep replying
        // "I'm following someone; denied." (report: died via suicide, still following).
        RoomTracker.PlayerDeathObserved += () => Party.NoteSelfDropped();

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

        // @loop send — sender side paces its @loopdata lines the same way as @roomba sync; the
        // receiver saves a loop we asked for (window opened by our own outbound
        // `@loop send yes`, wired from the outbound-chat watcher in MainWindowViewModel).
        LoopShare = new Game.Remote.LoopShareHandler(Loops, Log, paceScheduler: pacedReplyScheduler,
            runningLoop: () => LoopRunner.State is not Game.Map.LoopState.Idle ? LoopRunner.CurrentLoop : null,
            notice: msg => Avalonia.Threading.Dispatcher.UIThread.Post(() => WriteTerminalNotice(msg)));
        // What the asker's client answers when a loop we sent has arrived there.
        Chat.EntryClassified += LoopShare.NoteChat;
        // The receiver's one answer to the sender goes out as every remote reply
        // does, and is held back by the master switch and the Warn setting there.
        LoopShareInbox = new Game.Remote.LoopShareReceiver(Chat, Loops,
            notice: msg => Avalonia.Threading.Dispatcher.UIThread.Post(() => WriteTerminalNotice(msg)), Log,
            tell: RemoteCommands.SendOutcome);

        // MovePlayer remote-command handler.
        // Registers @goto, @loop, @lair, @stop, @rego against the
        // RemoteCommandManager. Dispatch routes to the now-existing
        // Walker / LoopRunner / AutoLairManager. The Catalog permission
        // gate ensures only players the user has granted MovePlayer
        // can issue these.
        MoveRemote = new Game.Remote.MovePlayerHandler(
            RemoteCommands, RoomSearch, RoomGraph, RoomTracker, Walker, Loops, LoopRunner,
            Lairs, AutoLair, MovementCoordinator, MovementControl, Favorites, Bosses, Bfs, LoopShare);
        MoveRemote.Stopped = () => NoteRemoteStop?.Invoke();

        // Leader-side @comeback. Snapshots the running movement
        // engine, stops it (stop-and-restart, NOT a coordinator gate —
        // a gate would block the recovery walk itself), walks to recover
        // the stranded follower (explicit room or backtrack along the
        // just-walked RoomTracker trail), re-invites + awaits follow,
        // then resumes the captured engine. Its reach (ReturnDistanceRooms)
        // comes from Settings → Party.
        PartyComeback = new Game.Remote.PartyComebackManager(
            RemoteCommands, Party, RoomTracker, RoomClassifier, Walker, LoopRunner, AutoLair, Router, Bfs, Log);
        // A follower we backtracked for couldn't move — hold for their @ok as if
        // they'd sent @held (chip + full wait window).
        PartyComeback.LeftBehindRejoined = (given, ignoreOk) => PartyAilment?.NoteInferredHold(given, ignoreOk);
        // Stock keeps a follower an exit turned away on the leader's member list,
        // where Paradigm drops them: what brings such a member back differs.
        PartyComeback.OnStock = () => GameData.ActiveRealm != Game.RealmType.ParaMud;
        PartyComeback.OkedWithin = PartyEssentials.OkedWithin;
        // A loop doesn't go back for a member an exit of its circuit turned away.
        // What tells that from a hold: a wait or hold the member has out with us,
        // and what we hold of them against the exit's own conditions (the @level
        // probe, their class and race from `who`, the last purse they reported, the
        // items handed to them or counted).
        PartyComeback.MemberSignalledHold = given => PartyEssentials.WaitingMembers.Contains(given);
        // Their pending @wait would park the walk back to them behind the party-wait
        // gate. Let go by the manager itself, after it has read what the wait says.
        PartyComeback.ReleaseMemberWait = PartyEssentials.ReleaseWait;
        PartyComeback.Notice = text =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => WriteTerminalNotice(text));
        PartyComeback.MemberCanPass = (given, exit) =>
        {
            Models.GameData.PlayerRecord? known = Players.Find(given);
            return Game.Remote.MemberGateJudge.CanPass(in exit, new Game.Remote.MemberGateJudge.Facts(
                Level: known?.Level is > 0 and int level ? level : null,
                ClassNumber: TableNumberByName("Classes", known?.Class),
                RaceNumber: TableNumberByName("Races", known?.Race),
                PurseCopper: PartyWealth.LastReading(given),
                CopiesHeld: itemId => PartyHandOvers.CopiesRememberedFor(given, itemId)));
        };
        // During a train trip nobody is gone back for; they are fetched at its end.
        // A party train trip, or the leader's own train or spell run with the
        // party in tow (both are built further down, and read only when asked).
        PartyComeback.TrainTripRunning = () =>
            PartyTrain?.TripRunning == true || TrainerWalk?.OwnRunActive == true;
        // A dropped member's reconnect hold (or their @wait) would park the walk to
        // pick them up — the leader never moves while they wait on it.
        PartyComeback.ReleaseHolds = (given, reason) =>
        {
            PartyDisconnectMovement.Release(given, reason);
            PartyEssentials.ReleaseWait(given);
        };

        // @where reply → nav-map flash. Recognises the wrapped location reply an
        // @where'd MudPlay client telepaths back and routes it to the (open) map;
        // HighlightWhereRoom no-ops when the window is closed.
        WhereReply = new Game.Remote.WhereReplyTracker(Router, Log);
        WhereReply.TargetLocated += (_, room) => HighlightWhereRoom(room);

        // @path reply → the other player's route on the nav map (see
        // NavigationViewModel.LeaderRoute). Built from the reply alone; nothing is sent.
        PathReply = new Game.Remote.PathReplyTracker(Router,
            isPartyLeader: sender => PartyState.IsInParty && !PartyState.SelfIsLeader
                && string.Equals(GivenNameOf(PartyState.LeaderName), sender, StringComparison.OrdinalIgnoreCase),
            log: Log);
        PathReply.PathReported += ShowLeaderRoute;
        PathReply.GotoReported += ShowLeaderGoto;

        // A follower's side of the Bossing set's "keep on between bosses": ask the
        // leader's client where it's going. Wire sender bound per-session by the VM.
        LeaderBossTravel = new Game.Remote.LeaderBossTravelProbe(
            PathReply,
            leaderGivenName: () => PartyState.IsInParty && !PartyState.SelfIsLeader
                ? GivenNameOf(PartyState.LeaderName) : null,
            headsToBoss: LeaderPathHeadsToBoss,
            isBossRoom: IsBossRoomLive,
            stillNeeded: () => AutoEquip.IsKeepingBossing,
            schedule: ScheduleOnce,
            log: Log);
        LeaderBossTravel.Resolved += AutoEquip.OnBossTravelResolved;

        // Funding errand for the train bill. Built before the coordinator that owns
        // it: the coordinator has already snapshotted + stopped the running engine
        // by the time this drives the walker, so it never touches the loop itself.
        TrainFunding = new Game.Train.TrainFundingRouter(
            currentRoom: () => RoomTracker.State.CurrentRoom?.Key,
            onHandCopper: () => Inventory.Snapshot.Currency.TotalCopperValue,
            sources: BuildTrainFundingSources,
            newDistanceLookup: () => Bfs.DistanceMemo(Movement),
            // Same gate planning the trainer walk uses — a stash or bank can sit
            // behind a key-door or hidden exit a plain walk can't route through.
            walkTo: key => Walker.WalkTo(key, planThroughAcquirableGates: true),
            send: cmd => SendGameCommand(cmd),
            armTimer: (delay, action) => _ = System.Threading.Tasks.Task.Delay(delay)
                .ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(action),
                    System.Threading.Tasks.TaskScheduler.Default),
            reconcileStash: (room, copper) => StashBalances.Reconcile(room, copper),
            limitCollection: copper => Cash.SetCollectLimit(copper),
            surveyedCopper: () => Cash.SurveyedCopperUnderLimit,
            collectSurveyed: copper => Cash.CollectSurveyed(copper),
            autoGetCash: CashCollectionOn,
            setAutoGetCash: on => _autoGetCashOverride = on ? true : null,
            log: Log,
            // Keep-on-hand is an amount of a chosen denomination; the router wants it
            // in copper. The same conversion AutoDepositManager uses for the deposit
            // floor, so the two agree on what "keep" means.
            reserveCopper: () =>
            {
                Models.Profile.CashSettings cash =
                    ReadSection<Models.Profile.CashSettings>(Profile.Current, "Cash");
                return (long)cash.KeepOnHandWealth
                       * Game.Inventory.CurrencyHoldings.CopperUnit(
                             cash.KeepOnHandDenomination);
            },
            requestInventory: () => SendGameCommand("i"),
            // A run that may not draw on a bank has no use for a `bank` listing.
            bankBalancesKnown: () => BankBalance.HasListing
                || !Game.Train.TrainFundingSourceFilter.UsesBanks(ReadAutoTrainerSettings().FundingMode),
            // `bank` is a global query, so this needs no walk. The probe completes on
            // its reply window; the router hears it back on the UI thread.
            requestBankBalances: () => _ = BankBalance.QueryAsync().ContinueWith(
                _ => Avalonia.Threading.Dispatcher.UIThread.Post(() => TrainFunding.NoteBankRefreshed()),
                TaskScheduler.Default),
            // A bank stop the trip makes anyway also draws what a party in tow is
            // short of the trip's tolls and fares (Cash: fund party members' tolls).
            partyTollFunding: tolls => PartyToll.FundingNeededFor(tolls),
            readPartyPurses: then => PartyToll.AfterReadingPurses(then),
            // A stash room picked as the Bank in Settings → Cash + Items.
            stashIsTheBank: room => Movement.IsStash(room) && PathItemBankRoom() == room);
        // The full parse that answers that `i`: an incremental pickup / drop patch is
        // exactly the drifting figure the refresh exists to replace. Harmless at any
        // other time, since the router only listens while it is holding for one.
        Inventory.FullInventoryParsed += () => TrainFunding.NoteInventoryRefreshed();

        // Stash → bank transfer. Shares the funding errand's hooks into the collect
        // engine; StartStashTransfer refuses while another errand has the walker, so
        // the two never hold the pickup ceiling or the borrowed toggle together.
        StashTransfer = new Game.Cash.StashTransferRunner(
            currentRoom: () => RoomTracker.State.CurrentRoom?.Key,
            onHandCopper: () => Inventory.Snapshot.Currency.TotalCopperValue,
            walkTo: key => Walker.WalkTo(key, planThroughAcquirableGates: true),
            send: cmd => SendGameCommand(cmd),
            armTimer: (delay, action) => _ = System.Threading.Tasks.Task.Delay(delay)
                .ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(action),
                    System.Threading.Tasks.TaskScheduler.Default),
            limitCollection: copper => Cash.SetCollectLimit(copper),
            surveyedCopper: () => Cash.SurveyedCopperUnderLimit,
            collectSurveyed: copper => Cash.CollectSurveyed(copper),
            forceAutoGetCash: on => _autoGetCashOverride = on ? true : null,
            reconcileStash: (room, copper) => StashBalances.Reconcile(room, copper),
            // Walker events and timers can land inside the message pump.
            notice: msg => Avalonia.Threading.Dispatcher.UIThread.Post(() => WriteTerminalNotice(msg)),
            log: Log,
            partyMembers: StashTransferPartyMembers,
            // The same floor Deposit All and the auto-deposit leave in the purse.
            keepOnHandCopper: () =>
            {
                Models.Profile.CashSettings cash =
                    ReadSection<Models.Profile.CashSettings>(Profile.Current, "Cash");
                return (long)cash.KeepOnHandWealth
                       * Game.Inventory.CurrencyHoldings.CopperUnit(cash.KeepOnHandDenomination);
            },
            coinLoad: () => Cash.CoinLoad(),
            // The Navigation chip's tooltip: the pile by coin, what is left of it,
            // and a rough time from the walk between the two rooms.
            surveyedCoins: () => Cash.SurveyedCoinsUnderLimit,
            purse: () => Inventory.Snapshot.Currency,
            walkTime: (from, to) => Bfs.DistanceBetween(from, to, Movement) is { } hops
                ? AutoLair.TravelCostModel.EstimateTravel(hops) : null,
            believedCopper: room => StashBalances.Believed(room),
            // Reported from the stash room, so the row is the one its hides built.
            noteStashSeen: (took, left) => TransactionHistory.NoteStashWithdrawal(
                CoinWords(took), CoinWords(left), CurrentRoomLabel()));
        Walker.Event += e => StashTransfer.OnWalkEvent(e.Kind);
        // A member's {reply} to @get-stash / @deposit-all says that member is done.
        Chat.EntryClassified += e =>
        {
            if (e.Channel == Game.ChatChannel.TelepathIncoming && e.Speaker is { } speaker
                && e.Message.TrimStart().StartsWith('{'))
                StashTransfer.NoteMemberReply(speaker, e.Message);
        };

        // Trainer-walk coordinator. Built here (after the movement
        // engines) so it can snapshot / stop / restart the running Loop or
        // Auto-Lair for a train detour, same as AutoDeposit. Manual Train Now
        // (CP tab) + the armed auto-train (live-exp threshold during a loop)
        // both route through it. Wire-sender bound in MainWindowViewModel.
        TrainerWalk = new Game.TrainerWalkManager(PlayerStats, Stats, GameData, Profile,
            RoomTracker, Bfs, Walker, LoopRunner, AutoLair, AutoTrain, Router, Log);
        TrainerWalk.SetFundingRouter(TrainFunding);
        TrainerWalk.RouteTolls = (a, b) => Movement.TollCopperOnRoute(Bfs, a, b);
        TrainerWalk.HasTollFreeRoute = (a, b) => Movement.HasTollFreeRoute(Bfs, a, b);
        TrainerWalk.ReserveForTraining = copper => Movement.ReservedCopper = copper;
        // The errand drives the walker itself, so it needs the same event stream the
        // coordinator watches. TrainerWalkManager ignores walk events while its phase
        // is Funding, so the two never both act on one event.
        Walker.Event += e => TrainFunding.OnWalkEvent(e.Kind);
        // Shortfall wording comes from the live session earn rate + lap time, which
        // live out here rather than in the train coordinator.
        TrainerWalk.DescribeShortfall = shortfall =>
            Game.Train.TrainFundingForecast.Describe(
                shortfall,
                SessionActivity.Snapshot().CurrencyPerHour,
                LoopRunner.AverageLapTime);
        TrainerWalk.EstimateWaitToAfford = shortfall =>
            Game.Train.TrainFundingForecast.TimeToAfford(
                shortfall, SessionActivity.Snapshot().CurrencyPerHour);

        ShopSpells = new Game.Train.ShopSpellErrand(
            currentRoom: () => RoomTracker.State.CurrentRoom?.Key,
            plan: (from, returnTo, level, visited, gaveUp) =>
                PlanShopSpells(from, returnTo, level, SpendableCopper(), visited, gaveUp),
            carriedScrolls: CarriedSpellScrolls,
            carriedCount: CountCarriedByName,
            isObtained: spell => Spellbook.IsObtained(spell),
            walkTo: key => Walker.WalkTo(key, planThroughAcquirableGates: true),
            send: cmd => SendGameCommand(cmd),
            armTimer: (delay, action) => _ = System.Threading.Tasks.Task.Delay(delay)
                .ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(action),
                    System.Threading.Tasks.TaskScheduler.Default),
            log: Log,
            // A toll on the way to a shop is taken only when the scrolls can still
            // be paid for after it.
            reserve: copper => Movement.ReservedCopper = copper);
        Walker.Event += e => ShopSpells.OnWalkEvent(e);
        AutoBuy.StockListed += stock => ShopSpells.OnShopListed(stock);
        Inventory.Changed += () => ShopSpells.OnInventoryChanged();
        Spellbook.Changed += () => ShopSpells.OnSpellbookChanged();
        Router.Subscribe(Services.Patterns.KnownPatterns.UserBuyFailed, m =>
        {
            if (m.Groups.Count > 0) ShopSpells.OnBuyRefused(m.Groups[0]);
        });
        // The line names no spell, so it can only be tied to one while a read of
        // ours is out. The spell goes into the book: the game has just said it's known.
        Router.Subscribe(Services.Patterns.KnownPatterns.LearnSpellAlreadyKnown, _ =>
        {
            if (ShopSpells.OnScrollAlreadyKnown() is { } spell) Spellbook.MarkObtainedByName(spell);
        });
        TrainerWalk.SetSpellErrand(ShopSpells);
        TrainerWalk.PlanShopSpells = (from, returnTo, level) =>
            PlanShopSpells(from, returnTo, level, budgetCopper: null);

        // @train remote: trains in place (no walk) via the coordinator.
        TrainRemote = new Game.Remote.TrainHandler(RemoteCommands, TrainerWalk);

        PartyTrain = new Game.Train.PartyTrainCoordinator(
            PartyState, TrainerWalk,
            expPerHour: () => SessionActivity.Snapshot().ExperiencePerHour,
            holdings: () => Inventory.IsLoaded ? Inventory.Snapshot.Currency : null,
            keepOnHandCopper: () =>
            {
                Models.Profile.CashSettings cash = ReadSection<Models.Profile.CashSettings>(Profile.Current, "Cash");
                return cash.KeepOnHandWealth * Game.Inventory.CurrencyHoldings.CopperUnit(cash.KeepOnHandDenomination);
            },
            largestDeposit: () => BankBalance.LastKnown
                .OrderByDescending(kv => kv.Value)
                .Select(kv => ((string?)kv.Key, kv.Value))
                .FirstOrDefault(),
            runicName: () => Currency.RunicName,
            currentRoom: () => RoomTracker.State.CurrentRoom?.Key,
            distance: (a, b) => Bfs.DistanceBetween(a, b, Movement),
            trainers: () => Game.GameData.TrainerCatalog.Enumerate(GameData),
            nearestBankBranch: NearestBankBranch,
            walkTo: key => Walker.WalkTo(key, planThroughAcquirableGates: true),
            send: cmd => SendGameCommand(cmd),
            // The on-join @version probe's reading — who's on MudPlay (and new enough
            // to speak @ptrain) and who isn't worth asking.
            recordedVersion: name => Players.Find(name) is { } p ? (p.Version, p.VersionAt) : (null, null),
            // The leader's own train disbands the party; this is the same re-collect
            // a leader reconnect uses — re-invite the followers standing with us and
            // hold the resumed loop until they're back.
            reformParty: givens => Party.BeginLeaderReconnectReform(givens),
            armTimer: (delay, action) => _ = System.Threading.Tasks.Task.Delay(delay)
                .ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(action),
                    System.Threading.Tasks.TaskScheduler.Default),
            // Outside a trip nothing is telepathed mid-fight — a line at the wrong
            // moment costs a round.
            inCombat: () => PlayerState.InCombat,
            selfLevel: () => PlayerStats.Level,
            selfExp: () => PlayerStats.Exp,
            // The @level probe's last reading — lets the Party window put a level in
            // front of the class for members that don't report.
            recordedLevel: name => Players.Find(name)?.Level,
            selfTimeToLevel: () => SelfTimeToLevel().Remaining,
            telepathsPending: () => Telepaths.Queued + Telepaths.InFlight > 0,
            log: Log,
            // The trip is over: whoever it came back without is gone back for, or
            // left to the player when the player took the trip over.
            tripEnded: (setOut, byItself) => PartyComeback.TrainTripEnded(byItself, setOut));
        // The same for a train or spell run of the leader's own with the party in
        // tow, which keeps no roll.
        TrainerWalk.OwnRunOver = byItself => PartyComeback.TrainTripEnded(byItself, []);
        Walker.Event += e => PartyTrain.OnWalkEvent(e.Kind);
        PartyTrainRemote = new Game.Remote.PartyTrainHandler(RemoteCommands, PartyTrain);
        PartyLevelProbe.ProgressObserved += PartyTrain.NoteLevelProgress;
        // A member's "I can now train to level: N", on any channel they announce on.
        Chat.EntryClassified += entry =>
        {
            if (entry.Channel is Game.ChatChannel.Local or Game.ChatChannel.TelepathIncoming
                    or Game.ChatChannel.Gangpath or Game.ChatChannel.Gossip or Game.ChatChannel.Yell
                && !string.IsNullOrEmpty(entry.Speaker)
                && Game.Train.PartyTrainCoordinator.TryParseTrainableAnnounce(entry.Message, out int level))
                PartyTrain.NoteTrainableAnnounce(entry.Speaker, level);
        };
        PlayerStats.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Game.PlayerStats.Exp)) PartyTrain.OnOwnExpChanged();
        };
        // After a run that trained: re-form a party a solo-fallback train disbanded,
        // then bank the excess on the way back into the circuit. AutoDeposit's own
        // check decides whether there's anything worth a trip, against the user's
        // keep-on-hand floor — this just gives it the prompt to look.
        TrainerWalk.AfterTrainRun = () =>
        {
            PartyTrain.AfterSoloRun();
            AutoDeposit.OnInventoryChanged();
        };
        // Training drops you out of and back into the realm, which disbands a party
        // server-side, so the solo run stays out of a group — a party trains through
        // PartyTrain instead. The exception is a leader with Auto-train party on
        // and nobody to train with: it trains by its solo settings and re-forms.
        TrainerWalk.CanStartRun = () => PartyTrain.AllowSoloRun();
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
            // In the realm only once a status line has been observed — keeps the
            // login dump off the character-select / main menu.
            isInRealm: () => PlayerState.HasPromptData,
            log: Log);

        // Login-time quest-flag completion sync. Reads flags via QuestFlagReader, marks any
        // quest whose flag has reached its (crawl-derived or user-overridden) complete value.
        // Sysop-gate: the stock bulk read needs sys-god powers on the active BBS.
        QuestFlagSync = new Game.Quests.QuestFlagSyncManager(
            GameData, Profile, Quests, QuestFlagReader,
            realm: () => GameData.ActiveRealm,
            // Stock's bulk `sys god <name> abil` read needs sys-god access — the same
            // capability the god-lives recovery uses.
            hasSysopPowers: SysopGodLivesEnabledHere,
            characterName: () => PlayerStats.Name,
            classId: () => Game.Quests.CompletedQuestBonuses.ResolveClassId(GameData, PlayerStats.Class),
            enabled: () => ReadSection<Models.Profile.GeneralSettings>(Profile.Current, "General").AutoSyncQuestFlagsOnLogin,
            // In-realm only (a status line has been seen) so turning the toggle on at the
            // character-select menu doesn't fire; re-report re-lists the now-current quests.
            isInRealm: () => PlayerState.HasPromptData,
            reannounce: () => QuestAvailability.AnnounceLoginAvailable(),
            // The eligible + level-met + incomplete set at the current level — the same
            // resolver the availability announce uses. Bounds the sync to quests the
            // character can actually complete now (not every quest in the realm).
            availableQuests: () => Game.Quests.QuestEligibility.Resolve(
                GameData, Quests, PlayerStats, Profile,
                resolveName: static (q, def) => string.IsNullOrWhiteSpace(def.Name)
                    ? ViewModels.CharacterWorkshop.QuestTextFormatter.FallbackTitle(q)
                    : def.Name,
                PlayerStats.Level),
            log: Log);

        QuestQuery = new Game.Remote.QuestQueryHandler(
            RemoteCommands,
            profile: () => Profile.Current,
            quests: Quests,
            probe: QuestQueryReader,
            sync: QuestFlagSync,
            isParadigm: () => GameData.ActiveRealm == Game.RealmType.ParaMud,
            // Stock's live flag read is the same gated `sys god <name> abil` the daily
            // sync uses — sys-god access, not the separate sys-status capability.
            canStockRead: SysopGodLivesEnabledHere,
            characterName: () => PlayerStats.Name,
            log: Log);

        // Auto-deposit reroute. Built here
        // (after the movement engines) so it can snapshot / stop / restart
        // the running Loop or Auto-Lair when CashManager's gate crosses.
        // Stop-and-restart, NOT a coordinator gate — a gate would block the
        // detour walk itself (same reasoning as PartyComebackManager). The
        // wire sender for the bank `dep` is bound by MainWindowViewModel
        // after telnet connects, alongside the Cash / Stash senders.
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
            log: Log,
            // Party follower = in a party and not the leader; gates the opt-in
            // follower pass-through stash (Cash → "stash as follower").
            isFollower: () => PartyState.IsInParty && !PartyState.SelfIsLeader,
            nearestLoopRoom: NearestLoopRoom);
        // A split-teleport reform waits for the leader to leave the room it started in.
        AutoParty.SetRoomProbe(() => RoomTracker.State.CurrentRoom?.Key);
        // Players listed by a `look <direction>` peek stand in the next room.
        AutoParty.SetPeekProbe(() => RoomTracker.IsPeekSuppressed());
        // Settings → Other "Only auto-invite while navigation is running": a walk,
        // loop or auto-lair (running or paused), or a bank, sell, train,
        // train-funding or stash-transfer errand.
        AutoParty.SetNavigationProbe(() =>
            MovementControl.IsActive || AutoDeposit.IsRerouting || SellDetour.IsDetouring
            || TrainerWalk.IsBusy || TrainFunding.IsBusy || StashTransfer.IsBusy);
        MovementControl.StateChanged += AutoParty.OnNavigationStateChanged;
        // Sell detours: walk / loop / lair → the chosen shop → Auto-sell → carry on.
        // Blocked while anything else owns movement or holds it (combat, rest, a user
        // pause, following a leader, the other errand engines).
        SellDetour = new Game.Inventory.SellDetourManager(
            candidates: SellDetourCandidates,
            distance: (a, b) => Bfs.DistanceBetween(a, b, Movement),
            tracker: RoomTracker, walker: Walker, loops: LoopRunner, lair: AutoLair,
            sell: AutoSell, coordinator: MovementCoordinator,
            isEnabled: () => ReadAutoModeFlag(d => d.AutoGetItems),
            blocked: () => PlayerState.InCombat
                || (PartyState.IsInParty && !PartyState.SelfIsLeader)
                || MovementCoordinator.AssertedGates.Any(g => g != Game.Map.MovementCoordinator.SellDetourGate)
                || AutoSell.IsSelling
                || AutoDeposit.IsRerouting || TrainerWalk.IsBusy || TrainFunding.IsBusy
                || StashTransfer.IsBusy
                || TokenRoute.Active || PartyComeback.RecoveringMember is not null
                || PathItemShopRouter.DetourActive || PathItemGiveRouter.DetourActive
                || PathItemSummonRouter.DetourActive || MonsterDropRouter.DetourActive
                || AutoLightShopRouter.DetourActive
                || MazeSolver.Active || PyramidSolver.Active || GhSweep.IsActive
                // A spill sweep's leg is not a walk to pick back up: the sweep ends when
                // its walk is taken, and the detour would walk on to a stop nobody wants.
                || DeathRecovery.SpillSweepActive,
            nearestLoopRoom: NearestLoopRoom,
            nearBankSteps: () => ReadSection<Models.Profile.CashSettings>(Profile.Current, "Cash").SellOnBankRunWithinSteps,
            log: Log);

        // Stop holds a money or training errand instead of ending it, and the next
        // walk, loop or Auto-Lair the user starts asks whether to finish it first.
        MovementControl.SetErrandHooks(
            activeErrand: SuspendableErrand,
            abandonErrand: reason =>
            {
                StashTransfer.Cancel(reason);
                // A leader's party trip waits on its own train with no walk to stop,
                // and the trainer never answers a train it was told to cancel.
                PartyTrain.CallOffTrip(reason);
                TrainerWalk.Cancel(reason);
                TrainFunding.Cancel(reason);
                SellDetour.Cancel();
                AutoDeposit.Cancel();
            },
            askResume: async errand =>
            {
                ViewModels.ConfirmDialogViewModel question = new(
                    "Resume first?",
                    $"Stop is holding {errand}.\n\nResume it first? What you just started will begin when it is done.\n\n"
                    + $"No ends {errand} and starts this now.",
                    yesLabel: "Resume it first", noLabel: "No, drop it");
                // Kept while it is up, so a change of game data can take it down.
                _resumeFirstQuestion = question;
                try { return await Dialogs.OpenWindowAsync<ViewModels.ConfirmDialogViewModel, bool>(question); }
                finally { if (ReferenceEquals(_resumeFirstQuestion, question)) _resumeFirstQuestion = null; }
            },
            post: run => Avalonia.Threading.Dispatcher.UIThread.Post(run));
        MovementControl.EndingForGameDataChange += EndHeldRunsForGameDataChange;
        // The same stop lets go of what a fight with a player would put back at
        // its end: the engines are idle for the fight, so the stop finds nothing.
        MovementControl.AddHeldStart(() => _pvpFightHeld.Held, reason => _pvpFightHeld.Drop(reason));
        MovementControl.AddHeldStart(
            () => NavigationQueuedWalk?.Held(), reason => NavigationQueuedWalk?.Drop(reason));
        StashTransfer.StateChanged += MovementControl.NoteErrandStateChanged;
        TrainerWalk.StateChanged += MovementControl.NoteErrandStateChanged;
        TrainFunding.Finished += _ => MovementControl.NoteErrandStateChanged();
        SellDetour.DetouringChanged += MovementControl.NoteErrandStateChanged;
        AutoDeposit.ReroutingChanged += MovementControl.NoteErrandStateChanged;
        // The same errands count as navigation for "Only auto-invite while navigation
        // is running", and one can start with no walk or loop under it: its own start
        // is what releases a held invite then. Train funding only ever begins inside
        // a train trip.
        StashTransfer.StateChanged += AutoParty.OnNavigationStateChanged;
        TrainerWalk.StateChanged += AutoParty.OnNavigationStateChanged;
        SellDetour.DetouringChanged += AutoParty.OnNavigationStateChanged;
        AutoDeposit.ReroutingChanged += AutoParty.OnNavigationStateChanged;
        MovementControl.SuspendedErrandChanged += () =>
        {
            if (MovementControl.SuspendedErrand is { } held)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => WriteTerminalNotice(
                    $"[Stop is holding {held} - Resume carries it on, Stop again ends it]"));
        };
        SellDetour.HandOffToBank = AutoDeposit.TakeOverFromDetour;
        // And the other way round: a bank run coming due lets a sale near the bank go
        // first, which then hands the run back as above.
        AutoDeposit.SellFirst = SellDetour.SellAheadOfBankRun;
        SellDetour.BankRunNotTaken = Cash.NotifyAutoDepositAborted;
        Tick.HeartbeatElapsed += SellDetour.Evaluate;
        Inventory.Changed += SellDetour.Evaluate;
        // Settings → Cash + Items "No combat during a detour": holds the real Auto-Combat
        // toggle off once a detour leaves its loop's rooms (the main window flips it).
        DetourCombat = new Game.Cash.DetourCombatHold(
            selling: () => SellDetour.IsDetouring, sellResume: () => SellDetour.ResumePlan,
            depositing: () => AutoDeposit.IsRerouting, depositResume: () => AutoDeposit.ResumePlan,
            readCash: () => ReadSection<Models.Profile.CashSettings>(Profile.Current, "Cash"),
            loopRooms: loop => Game.Map.LoopExpander.ResolveCycleRoomKeys(loop.Waypoints, Bfs, RoomGraph, Movement),
            currentRoom: () => RoomTracker.State.CurrentRoom?.Key,
            log: Log);
        RoomTracker.StateChanged += _ => DetourCombat.Evaluate();
        Tick.HeartbeatElapsed += DetourCombat.Evaluate;
        // Return-leg light provisioning: the reroute owns the walker end-to-end, so
        // the reactive shop router is suppressed (IsRerouting) — this manager runs
        // its own bank -> shop -> origin light detour and needs the `i` dump to
        // notice the bought copy land.
        Inventory.Changed += AutoDeposit.OnInventoryChanged;
        // In a stash room mid-loop, suppress cash + item auto-collect ONLY while an
        // auto-search reveal is in flight — the `sea` round-trip that re-exposes the
        // pile the pass-through stash just hid (reports paradigm-20260819-121516,
        // -20260820-055720). Gating on the reveal window (not merely "we're in a
        // stash room") is what lets the character still collect coin that's plainly
        // visible on entry or dropped by a kill, in the stash room AND in the room
        // after it: a room's entry survey is parsed BEFORE the room is confirmed, so
        // reading live CurrentRoom alone mis-attributes the next room's coin to the
        // stash room we just left and dropped it on the floor (report
        // paradigm-20260829-212158 — 1788 gold in the room south of a stash room).
        // The settle gate holds the walker through the reveal, so the window never
        // straddles a room change. AutoDeposit owns the room/stash/running-engine
        // state; AutoSearch owns the reveal window; both read live per survey line.
        Cash.SuppressCollectInStashRoom =
            () => (AutoDeposit?.IsPassingThroughStashRoom() ?? false) && AutoSearch.IsRevealInFlight;
        AutoGetItems.SuppressCollectInStashRoom =
            () => (AutoDeposit?.IsPassingThroughStashRoom() ?? false) && AutoSearch.IsRevealInFlight;
        // Hold auto-collect and auto-discard off while a Roomba sweep is sorting the
        // house: an auto-collect would eat the carry headroom Roomba budgets for its
        // moves (shrinking it until pre-planned sorts no longer fit, so the sweep
        // can never finish), and an auto-discard would bin an item Roomba is
        // relocating. Roomba sorts flagged items itself; both engines resume the
        // moment the sweep ends.
        AutoGetItems.SuppressDuringSweep = () => GhSweep.IsActive;
        AutoDiscard.SuppressDuringSweep = () => GhSweep.IsActive;
        // A container the sweep carries is being moved, not looted.
        AutoOpen.SuppressDuringSweep = () => GhSweep.IsActive;
        // A sweep starting takes back the engine's piles still waiting to be sent.
        // One ending may have left the character somewhere with room for the hides
        // a full room refused, and lets the engine's own discards go again.
        GhSweep.PhaseChanged += AutoDiscard.OnInventoryChanged;

        // Give-source routing. On a one-shot walk-to that needs an uncarried
        // Item/Ticket-gate item a deterministic textblock `giveitem` hands over
        // for free (an `ask <noun> <keyword>` dialogue give, or a room-CMD keyword
        // give), detour to the fewest-added-steps giver, issue the command, and
        // resume once it lands — gated per-item by the same AutoObtainForPath
        // flag. Preempts both the shop and drop routers (a free, certain give
        // beats a paid buy or a percentage hunt), which stand down whenever
        // DeterministicGiveExists. It also makes the one kind of give that isn't
        // free: a trade for a door key, agreed to on a route card
        // (GiveSourcesForItem). Wire-sender bound by MainWindowViewModel.
        PathItemGiveRouter = new Game.Map.PathItemGiveRouter(
            giveSourcesForItem: GiveSourcesForItem,
            currentRoom: () => RoomTracker.State.CurrentRoom?.Key,
            walkDestination: () => Walker.Destination,
            distanceBetween: PathItemDetourDistance,
            carriedCount: CountPathItemCoverage,
            itemName: ItemNames.GetName,
            isEnabled: IsAutoObtainForPath,
            // A loop's approach is an ordinary walk to its entry room, so a give can
            // be fetched on the way in; once the loop is circling, its own steps
            // drive and a detour would pull it off its lap. An approach held at its
            // start for the party's count is still an approach: the need is posted
            // while that hold stands.
            engineWalkActive: () =>
                ErrandOwnsWalk()
                || (LoopRunner.State != Game.Map.LoopState.Idle && !LoopRunner.IsApproachInFlight),
            walkTo: WalkToForPathItemDetour,
            post: action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            log: Log);
        Needs.NeedPosted += PathItemGiveRouter.OnNeedPosted;
        Walker.Event += PathItemGiveRouter.OnWalkEvent;
        Inventory.Changed += PathItemGiveRouter.OnInventoryChanged;
        Router.LineDispatched += line => PathItemGiveRouter.OnLine(line.Text);
        LoopRunner.SetPathItemDetourRoomProbe(() => PathItemGiveRouter.LastGiverRoom);
        LoopRunner.SetGatedApproachFetch(LoopApproachFetch);

        // Shop-source routing. On a one-shot walk-to that needs an
        // uncarried Item/Ticket-gate item a shop sells, detour to the
        // fewest-added-steps shop, buy it, and resume — gated per-item by the
        // item record's Auto-obtain for path flag
        // (ItemOverlay.AutoObtainForPath). Distances use the same movement filter
        // the walker routes with so the estimate matches the real walk; the
        // shop lookup joins ShopStock (who sells it) against the live graph
        // (which rooms host those shops). engineWalkActive suppresses the
        // detour while a loop / auto-lair run drives movement. WalkTo is
        // deferred through the dispatcher because the triggering NeedPosted
        // fires synchronously inside the walker's WalkTo. Wire-sender bound
        // by MainWindowViewModel after connect.
        PathItemShopRouter = new Game.Map.PathItemShopRouter(
            shopRoomsSellingItem: ShopRoomsSellingItem,
            deterministicGiveExists: DeterministicGiveExists,
            currentRoom: () => RoomTracker.State.CurrentRoom?.Key,
            walkDestination: () => Walker.Destination,
            distanceBetween: PathItemDetourDistance,
            carriedCount: CountPathItemCoverage,
            cashOnHand: PathItemCashOnHand,
            buyCost: PathItemBuyCost,
            bankRoom: PathItemBankRoom,
            itemName: ItemNames.GetName,
            isEnabled: IsAutoObtainForPath,
            engineWalkActive: () =>
                ErrandOwnsWalk() || LoopRunner.State != Game.Map.LoopState.Idle,
            // The shared detour walk supersedes silently — without that, arriving at
            // the shop fired a Stopped into this router and abandoned the detour on
            // arrival (the "sat idle at the shop, never bought" bug).
            walkTo: WalkToForPathItemDetour,
            post: action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            log: Log,
            // The withdrawal for the item also draws what a party in tow is short
            // of the tolls on the rest of the walk; their purses are read first,
            // and only when there is a toll on it to read them for.
            partyTollTopUp: PathItemPartyTollTopUp,
            readPartyPurses: (bank, shop, destination, then) =>
            {
                if (PathItemTripTolls(bank, shop, destination) > 0) PartyToll.AfterReadingPurses(then);
                else then();
            });
        Needs.NeedPosted += PathItemShopRouter.OnNeedPosted;
        Walker.Event += PathItemShopRouter.OnWalkEvent;
        Inventory.Changed += PathItemShopRouter.OnInventoryChanged;

        // Guaranteed-summon routing. Between the free routers and the gamble: when
        // a walk-to needs an uncarried gate item that no give or shop supplies but
        // a ROOM COMMAND summons a monster which drops it outright, detour there,
        // type the command, let the fight resolve, re-survey the floor, and resume.
        // Unlike the drop hunt below this needs no prompt — both the spawn and the
        // drop are deterministic. A locked door's key can come from it as from a
        // give or a shop (DoorKeyIsFetchable). Wire-sender bound by
        // MainWindowViewModel.
        PathItemSummonRouter = new Game.Map.PathItemSummonRouter(
            summonSourcesForItem: SummonSourcesForItem,
            cheaperSourceExists: id => DeterministicGiveExists(id) || ShopStock.AnyShopSells(id),
            // A multi-item route posts one need per item back-to-back, so a sibling
            // may already own the walk by the time ours fires.
            siblingDetourActive: () =>
                PathItemGiveRouter.DetourActive || PathItemShopRouter.DetourActive
                || MonsterDropRouter.DetourActive,
            currentRoom: () => RoomTracker.State.CurrentRoom?.Key,
            walkDestination: () => Walker.Destination,
            distanceBetween: PathItemDetourDistance,
            carriedCount: CountPathItemCoverage,
            itemName: ItemNames.GetName,
            isEnabled: IsAutoObtainForPath,
            engineWalkActive: () =>
                ErrandOwnsWalk() || LoopRunner.State != Game.Map.LoopState.Idle,
            walkTo: WalkToForPathItemDetour,
            post: action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            log: Log);
        Needs.NeedPosted += PathItemSummonRouter.OnNeedPosted;
        Walker.Event += PathItemSummonRouter.OnWalkEvent;
        Inventory.Changed += PathItemSummonRouter.OnInventoryChanged;
        MonsterDeath.MonsterDied += PathItemSummonRouter.OnMonsterDied;

        // Monster-drop reroute. The no-shop counterpart to the shop
        // router: when a walk-to needs an uncarried Item/Ticket-gate item no
        // shop sells but a monster drops, prompt (ConfirmService) to reroute
        // to the nearest room that monster spawns in, then resume once the
        // drop lands — gated per-item by the item record's Auto-obtain for path
        // flag (ItemOverlay.AutoObtainForPath). The two routers are
        // mutually exclusive via anyShopSells: this one acts
        // only when no shop stocks the item. Nearest spawn is chosen with a
        // single forward BFS (ComputeDistancesFrom) since a common monster
        // spawns in hundreds of rooms; dropSpawnsForItem flattens the index's
        // droppers × their spawn rooms lazily, only for the needed item.
        MonsterDropRouter = new Game.Map.MonsterDropRouter(
            dropSpawnsForItem: DropSpawnsForItem,
            anyShopSells: ShopStock.AnyShopSells,
            // A guaranteed summon counts as a certain source here, and so does a
            // trade the user agreed to: either beats asking the user to gamble on a
            // lair roll.
            certainSourceExists: id =>
                GiveRouterHasSource(id) || SummonSourcesForItem(id).Count > 0,
            currentRoom: () => RoomTracker.State.CurrentRoom?.Key,
            walkDestination: () => Walker.Destination,
            distancesFrom: src => Bfs.ComputeDistancesFrom(src, Movement),
            isCarried: IsPathItemCovered,
            itemName: ItemNames.GetName,
            isEnabled: IsAutoObtainForPath,
            engineWalkActive: () =>
                ErrandOwnsWalk() || LoopRunner.State != Game.Map.LoopState.Idle,
            confirm: (title, body) => Confirm.ConfirmAsync(title, body, "Reroute"),
            walkTo: WalkToForPathItemDetour,
            post: action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            log: Log);
        Needs.NeedPosted += MonsterDropRouter.OnNeedPosted;
        Walker.Event += MonsterDropRouter.OnWalkEvent;
        Inventory.Changed += MonsterDropRouter.OnInventoryChanged;

        // Shortcut-source coordinator. When the user picks the route picker's shortcut
        // card without holding the shortcut item, this walks to the item's source,
        // waits for the room to settle (auto-combat clears any dropper, dropping the
        // item on the ground), grabs it, then re-walks to the destination on the LIVE
        // filter — which takes the shortcut if the item turned up and the long route if
        // it didn't. Both legs reuse the detour walk (no PathItem need outstanding, so
        // it plans on the live filter). RoomTracker.StateChanged re-checks the settle on
        // each room re-survey (a kill re-displays the room). No auto-obtain: the pick is
        // the consent, and a shortcut item may have no reliable source.
        ShortcutSource = new Game.Map.ShortcutSourceCoordinator(
            resolveSource: ResolveShortcutItemSourceRoom,
            isCarried: IsItemCarried,
            itemName: ItemNames.GetName,
            hasHostiles: () => CombatTracker.HasHostileMonster,
            inCombat: () => PlayerState.InCombat,
            walkToSource: WalkToForPathItemDetour,
            liveWalkToDest: WalkToForPathItemDetour,
            sendGet: name => SendGameCommand($"get {name}"),
            schedule: (ms, action) =>
            {
                var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
                timer.Tick += (_, _) => { timer.Stop(); action(); };
                timer.Start();
            },
            log: Log);
        Walker.Event += ShortcutSource.OnWalkEvent;
        Inventory.Changed += ShortcutSource.OnInventoryChanged;
        RoomTracker.StateChanged += _ => ShortcutSource.OnCombatStateChanged();

        // Solo token-route execution: on a picked token card, use the token then walk
        // from its landing to the destination — walking overland to the first clear
        // room first when the current room has NPCs. The resume walk goes straight to
        // Walker.WalkTo (never back through the picker) so it can't re-offer a token.
        TokenRoute = new Game.Tokens.TokenRouteCoordinator(
            inParty: () => PartyState.IsInParty,
            isLeader: () => PartyState.IsInParty && PartyState.SelfIsLeader,
            // Party members (never self) to bring across, by given name.
            partyMembers: () => PartyState.Members
                .Where(m => !m.IsSelf && !string.IsNullOrWhiteSpace(m.Name))
                .Select(m => GivenNameOf(m.Name) ?? m.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            // Of those, the ones still standing in the leader's room (i.e. not yet
            // ported) — the "check the room" the retry loop re-directs.
            membersStillHere: () => PartyState.Members
                .Where(m => !m.IsSelf && !string.IsNullOrWhiteSpace(m.Name))
                .Select(m => GivenNameOf(m.Name) ?? m.Name)
                .Where(IsGivenNameInRoom)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            useWhenIncomplete: () => Settings.Current.TokenUseWhenPartyIncomplete,
            roomHasNpc: () => CombatTracker.HasRoomNpc,
            walkToDest: dest => Walker.WalkTo(dest, supersedeSilently: true, preferTeleportFree: true),
            stopWalker: () => Walker.Stop("token route: using token in a clear room"),
            send: cmd => SendGameCommand(cmd),
            schedule: (ms, action) =>
            {
                var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
                timer.Tick += (_, _) => { timer.Stop(); action(); };
                timer.Start();
            },
            log: Log);
        Tokens.TokenUsed += TokenRoute.OnTokenUsed;
        MovementControl.Stopping += TokenRoute.Cancel;
        // A stash transfer is between walks while it searches, collects or deposits,
        // so the walker's own Stopped wouldn't reach it there.
        MovementControl.Stopping += () => StashTransfer.Cancel(MovementControl.GameDataChangeStop ?? "stopped by the user");
        // So is a walk between two of its legs: standing at a giver, a shop or an
        // item's source, with the coordinator that brought it there about to issue
        // the next leg. The walker says Stopped for a journey left standing, which
        // stands them down; this covers Stop reaching them with no journey to say it
        // for, so a leg never goes out after the user stopped.
        MovementControl.Stopping += () =>
        {
            PathItemGiveRouter.Cancel();
            PathItemShopRouter.Cancel();
            PathItemSummonRouter.Cancel();
            MonsterDropRouter.Cancel();
            ShortcutSource.Cancel();
            AutoLightShopRouter.Cancel();
            PvpFlee.CancelComeBack();
        };
        // After the coordinator has seen it: a token the route didn't send (the user
        // used one by hand) ends all movement where we land — nothing walks on from
        // there — and either way the followers it drops aren't left-behind members.
        // A token's textblock casts negate magic (#310) ahead of its teleport, which
        // takes a tracked hazard buff with everything else.
        Tokens.TokenUsed += place => AutoHazardCounterProvisioner.NoteNegateMagic($"token of {place}");
        Tokens.TokenUsed += place =>
        {
            PartyComeback.NoteOwnTeleport();
            if (TokenRoute.AwaitingLanding) return;
            Log.Info("Tokens", $"token of {place} used by hand — stopping movement where we land");
            TokenRoute.Cancel();
            MovementControl.Stop();
        };
        Tokens.MemberDeparted += TokenRoute.OnMemberDeparted;
        Walker.Event += TokenRoute.OnWalkEvent;
        RoomTracker.StateChanged += t =>
        {
            if (t.NewConfidence == Game.Map.RoomConfidence.Confirmed)
                TokenRoute.OnRoomChanged(t.NewRoom?.Key);
        };

        // Follower-side @comeback: one telepath to the leader when the party walks
        // off without us (the triggers and what is never one are on the class).
        // Enabled is pushed from Settings → Other by ApplyOtherFromActiveProfile,
        // and the master switch is set with every other system's
        // (ComebackRequest.MasterSwitchOff).
        ComebackRequest = new Game.Remote.ComebackRequester(Router, RoomTracker, Log,
            isMovementPrevented: () => Conditions.IsMovementPrevented,
            party: PartyState,
            isSelfDown: () => PlayerState.IsMortallyWounded,
            sendBlocked: () => InGameCapture.AtBoardMenu ? "we are at the board's menu"
                : EngineGate.IsLocked ? "the client's sends are held (a trainer screen or a password prompt)"
                : null,
            // During the leader's party train trip the request waits for the trip to
            // end (PartyTrain is built before this).
            inTrainTrip: leader => PartyTrain.InTripOf(leader));
        // A leader who refused, or never came: the follow the game ended without a
        // line is ended on our side too.
        ComebackRequest.FollowGivenUp = (leader, _) => Party.NoteFollowGivenUp(leader);
        // Stock leaves a follower an exit turned away on the leader's member list,
        // where an invite prints them nothing and a `join` of theirs needs none: the
        // follower's client joins by itself once the leader it asked is with it.
        ComebackRequest.OnStock = () => GameData.ActiveRealm != Game.RealmType.ParaMud;
        ComebackRequest.ListedWith = () => Party.ListedWithLeader;
        ComebackRequest.NotOnTheirList = leader => Party.NoteNotListedWith(leader);
        // One join, whichever of the two sends it: ours, or the one the leader's
        // @join asks for. The game's answer to either is read in one place. Not
        // keyed on the realm: a second @join inside 3 s is passed over anywhere
        // (PartyEssentialHandlers.OnJoin says why that is safe).
        PartyEssentials.JoinSent = ComebackRequest.NoteJoinSent;
        PartyEssentials.JoinAlreadySent = ComebackRequest.JoinJustSentTo;
        RoomClassifier.EntitiesObserved += observation =>
        {
            foreach (Game.Combat.RoomEntity entity in observation.Entities)
            {
                if (entity.Kind != Game.Combat.EntityKind.Player) continue;
                ComebackRequest.NotePlayerInRoom(Models.GameData.PlayerObservation.SplitName(entity.ResolvedName).Given);
            }
        };
        // The trip's leader may tell us it is over after an exit has turned us out
        // of the party on our own side.
        RemoteCommands.PartyTrainEligibility = sender => PartyTrain.InTripOf(sender);
        // The leader's relay of a teleport keyword is the leader's own split.
        PartyEssentials.PartyDirectiveRelayed += (sender, command) => ComebackRequest.NotePartyRelay(sender, command);
        Party.LeaderListedAsInvited += leader => ComebackRequest.NoteLeaderListedAsInvited(leader);

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
        // A loop restarting after a reconnect waits for that reform to see the room,
        // so its first step can't go out ahead of the reform's hold.
        LoopRunner.SetReconnectReformProbe(() => PartyReform.PendingReform.Count > 0);

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
        // The same holds for the leader we asked to come back for us: a member an
        // exit turned away is out of the party and needs that invite to rejoin.
        AutoParty.ForceAcceptFrom = name =>
            PartyRejoin.IsRememberedLeader(name) || ComebackRequest.IsLeaderWeAsked(name);
        RemoteCommands.ForgetEligibility = s =>
            Party.WasRecentlyPartied(s) || PartyRejoin.IsRememberedLeader(s);
        PartyComeback.ForgetLeaderCallback = PartyRejoin.ForgetRememberedLeader;

        // EventManager. Holds the loaded character's
        // scheduled / lifecycle events, dispatches actions into the
        // existing movement / command stack, and reconciles saved Loop /
        // AutoLair target references against their managers'
        // collections.
        Events = new Game.Events.EventManager(
            Profile, Loops, Lairs, LoopRunner, AutoLair, Walker, Log)
        {
            BlockedByMasterSwitch = what => AutoModeController.Blocks("Events", what),
            IsMasterSwitchOff = () => AutoModeController.KillSwitchEngaged,
        };

        // A loop held outside the catalogue can be a copy the catalogue no longer
        // holds (a snapshot of the last run, a loop re-read from disk under a run).
        // A change to the character's settings for a loop, or another character
        // loading, reaches those too, or a run would start or go on with the
        // settings the copy was made with. Hooked up here, once every holder exists.
        Loops.SettingsChanged += name => Loops.CopySettingsTo(LoopsHeldApartFromTheCatalogue(), name);

        // @events / @event: the events seen and switched by a trusted player.
        EventRemote = new Game.Remote.EventRemoteHandler(RemoteCommands, Events, Log);

        // EventScheduler. Owns the AtTime ticker +
        // per-event Every-timers + connection-aware Logon / Re-log
        // latch. Subscribes to the stable WirePromptScanner singleton
        // for in-game detection; MainWindowVM signals Connected /
        // Disconnected via NotifyConnected / NotifyDisconnected since
        // the TelnetClient itself is per-connection.
        EventScheduler = new Game.Events.EventScheduler(
            Events, PromptScanner, Cleanup, Profile, Log);

        // State-triggered events: re-checked whenever money, encumbrance, exp or
        // level changes, and on game entry. No polling.
        EventStateWatcher = new Game.Events.EventStateWatcher(
            Events, () => EventScheduler.IsInGame, ReadEventReadings, Log);
        EventScheduler.EnteredGame += () => EvaluateEventStates();
        Profile.ProfileLoaded += _ => EventStateWatcher.Reset();
        Inventory.Changed += () => EvaluateEventStates();
        PlayerStats.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Game.PlayerStats.Exp) or nameof(Game.PlayerStats.Level))
                EvaluateEventStates();
        };
        Events.SetRoombaStarter(mode =>
            GhSweep.Start(mode == Models.GameData.EventRoombaMode.InventoryOnly
                ? Game.Map.GhSweepManager.SweepMode.InventoryOnly
                : Game.Map.GhSweepManager.SweepMode.Sort)
                ? null
                : GhSweep.LastStartError ?? "the sweep refused to start");
        // What the other event actions start, and the signals that say they're done.
        Events.SetBankTripStarter(AutoDeposit.StartEventTrip);
        AutoDeposit.EventTripEnded += Events.NoteBankTripEnded;
        Events.SetStashTransferHooks(StartStashTransfer, () => StashTransfer.Cancel("another event took over"));
        StashTransfer.Ended += Events.NoteStashTransferEnded;
        Events.SetRestHooks(() => Health.IsRecoveringRest || Health.RestInFlight, () => Health.Evaluate());
        // Posted: the engine event that reports a refused walk back can arrive from
        // inside the message pump, where a terminal write re-feeds the emulator.
        Events.SetNotice(msg => Avalonia.Threading.Dispatcher.UIThread.Post(() => WriteTerminalNotice(msg)));
        // Stop with no engine running to report it (an event waiting or resting, or
        // suspended behind a detour) still ends the event and its queue. Wired here,
        // after Events exists; MovementControl is built further up.
        MovementControl.Stopping += () => Events.NoteStop(MovementControl.GameDataChangeStop ?? "stopped by the user");
        Events.SetStatsReader(ReadEventReadings);
        GhSweep.SweepCompleted += report => Events.NoteRoombaFinished(report.Finished);
        BossTimers.BossKilled += def => Events.NoteBossKilled(def.Name);
        // Boss-timer triggers: timed moments on the scheduler's 30 s clock, kills as
        // they're recorded.
        EventBoss = new Game.Events.EventBossWatcher(
            Events, Bosses, BossTimers, GameData, () => GameData.ActiveRealm, () => EventScheduler.IsInGame, Log);
        EventScheduler.ClockTick += EventBoss.Evaluate;
        EventScheduler.BossNextFire = EventBoss.NextFire;
        Events.SetBossStopCheck(EventBoss.StopReached);
        BossTimers.BossKilled += EventBoss.OnBossKilled;

        WireSounds();
        WireMasterSwitch();

        // DefaultTaskRunner. Starts the character's configured "Default task"
        // (loop / Auto-Lair) on the first in-game prompt with a known room,
        // holding for the party-reform window on a party-session reconnect.
        DefaultTaskRunner = new Game.DefaultTaskRunner(
            PromptScanner, RoomTracker, Profile, Loops, Lairs,
            LoopRunner, AutoLair, PartyState, Party, Log)
        {
            BlockedByMasterSwitch = what => AutoModeController.Blocks("Default task", what),
        };
        // A change of game data calls off a task still waiting out the party-rebuild
        // window: the loop or Auto-Lair it would start was picked from the old
        // data's catalogue.
        MovementControl.AddHeldStart(() => DefaultTaskRunner.HeldStart, reason => DefaultTaskRunner.DropHold(reason));

        // Where the dead of a room arrive, learned from the deaths this client sees
        // and kept with the game-data set, which every board and character on it
        // shares. Read by the hang-up death check below for the rooms no list
        // covers. Nothing is sent for it, so the master switch has no say.
        LearnedArrivals = new LearnedArrivalRoomStore(Log);
        GameData.ActiveSetChanged += LearnedArrivals.OnActiveSetChanged;
        if (GameData.ActiveSet is not null) LearnedArrivals.OnActiveSetChanged(GameData.ActiveSet);
        Tick.HeartbeatElapsed += () => LearnedArrivals.TakeInOutsideChanges();
        // The land a room stands on, for what a death seen in one room says of
        // the rooms around it: a map's lands are worked out the first time one of
        // its rooms is asked about, and kept with the graph.
        LearnedArrivalLands = new Game.Recovery.LearnedArrivalLookup(
            LearnedArrivals,
            room => Game.Map.WalkReach.KnotOnItsMap(RoomGraph, room),
            (from, room) => Game.Map.WalkReach.OnItsMap(RoomGraph, from, room));
        RoomGraph.GraphReloaded += LearnedArrivalLands.Forget;
        StandardListContradictions = new Game.Recovery.StandardListContradictions(
            () => ResolveActiveRealm()?.Realm, () => GameData.ActiveSet,
            () => { if (ResolveActiveBbs() is { } board) Bbs.Save(board); }, Log);
        ArrivalLearner = new Game.Recovery.ArrivalRoomLearner(
            RoomTracker, LearnedArrivals, LearnedArrivalLands, () => HangupArrivalListTrust,
            RoomGraph.CouldBeTakenFor, InParadigmAlbion, RoomGraph.IsExitCommandSomewhere,
            boardContradicted: StandardListContradictions.Note, log: Log);
        RoomTracker.PlayerDeathObserved += ArrivalLearner.OnPlayerDied;
        RoomTracker.StateChanged += ArrivalLearner.OnRoomStateChanged;
        OutboundMovement.MoveSent += ArrivalLearner.NoteMoveSent;
        // Out of the game before the room was drawn (the link dropped, the game was
        // left): the room the next entry shows is not known to be the same one.
        InGameCapture.InGameChanged += inGame => { if (!inGame) ArrivalLearner.Forget(); };

        // What a penalised hang-up dropped, looked for on the way back in. Gated on
        // the realm's own settings, and on Auto-All for anything it sends. Its gets
        // go out through the auto-get engine, and gear that comes back is the
        // equipment manager's to put on (the set last applied is applied again),
        // so each keeps one sender. Built last among the engines: every one it
        // reads or is driven by is above this line.
        HangupItems = new Game.Inventory.HangupItemRecheck(
            MovementCoordinator,
            // A named character only: the default profile is the template new
            // characters are made from, and a list there would be handed to each.
            profile: () => Profile.CurrentProfileName is null ? null : Profile.Current,
            // One of these runs inside the Disconnected handler. A save that threw
            // there would skip everything after it, the reconnect scheduling included.
            saveProfile: () =>
            {
                try { Profile.Save(); }
                catch (Exception ex)
                {
                    Log.Warn(Game.Inventory.HangupItemRecheck.LogCategory,
                        $"Couldn't save the list of what is held ({ex.GetType().Name}: {ex.Message}).");
                }
            },
            realmKey: () => ResolveActiveRealm() is { } played ? $"{played.Bbs.Name}/{played.Realm.Name}" : null,
            maxItemsDropped: () => Game.Health.HangupPenaltyNotice.MaxItemsDropped(ResolveActiveRealm()?.Realm),
            inventory: () => Inventory.Snapshot,
            confirmedRoom: () => RoomTracker.State.Confidence == Game.Map.RoomConfidence.Confirmed
                ? RoomTracker.State.CurrentRoom?.Key
                : null,
            floor: () => GroundItems.Items,
            fighting: () => CombatTracker.HasEngageableHostiles,
            hostilePresent: () => CombatTracker.HasHostileMonster,
            isAutoEnabled: () => !AutoModeController.KillSwitchEngaged,
            roomRedisplayFree: RoomRedisplay.ShouldSend,
            collect: (name, count) => AutoGetItems.CollectNamed(name, count, "dropped by a hang-up penalty"),
            reapplyGearSet: pickedUp => AutoEquip.ReapplySetAfterItemsReturned(Equipment.CurrentSetId, pickedUp),
            // Posted: these are raised while a line is being handled, and a terminal
            // write from inside that feeds the emulator back into itself.
            notice: msg => Avalonia.Threading.Dispatcher.UIThread.Post(() => WriteTerminalNotice(msg)),
            // The item table's number where it knows the item, so a floor entry and
            // a held name of one item meet however the floor words it.
            itemKey: name => ItemNames.FindByName(name) is int number ? $"#{number}" : ItemNameStore.Normalize(name),
            post: run => Avalonia.Threading.Dispatcher.UIThread.Post(run),
            log: Log,
            // What tells a hang-up the penalty could have killed for from an ordinary
            // reconnect, and a death from either. HP is the statline's; the maximum
            // is a `stat` read's or the highest HP seen, and 0 until there is one (a
            // character still dropped after a restart has shown none).
            vitals: () => PlayerState.HasPromptData
                ? (PlayerState.Hp, PlayerState.MaxHp > 0 ? PlayerState.MaxHp : null)
                : null,
            lives: () => PlayerStats.Lives > 0 ? PlayerStats.Lives : null,
            // A player's attack counts for a while after it: the fight engine may
            // not be fighting back (its response can be to do nothing, or to hang up).
            pvpFight: () => PvpFight.IsActive
                || PvpAttacks.Recent.Any(a => DateTimeOffset.Now - a.At < HangupPvpAttackWindow),
            monsterFight: () => PlayerState.InCombat || CombatTracker.HasHostileMonster,
            hpShareTop: (pvp, inFight) =>
                Game.Health.HangupPenaltyNotice.HpShareTop(ResolveActiveRealm()?.Realm, pvp, inFight),
            stockRealm: () => GameData.ActiveRealm == Game.RealmType.Stock,
            recordDeath: RoomTracker.NoteUnwitnessedDeath,
            staysOnDeath: EveryItemOfThisNameStaysOnDeath,
            // Whether the room the character came back in is where the dead of the
            // room it hung up in arrive. The list is the Stock map file's, and how
            // far it goes depends on the game data (HangupArrivalListTrust). The
            // graph says which rooms the map can't tell an arrival room from, and
            // what was learned from deaths seen speaks for the rooms no list covers.
            entryRoomSays: (hungUpIn, enteredIn) => Game.Recovery.HangupDeathArrivalRooms.Read(
                hungUpIn, enteredIn, HangupArrivalListTrust, RoomGraph.CouldBeTakenFor, InParadigmAlbion,
                LearnedArrivalLands.For),
            roomLabel: room => RoomGraph.GetRoom(room)?.Name is { Length: > 0 } name ? $"{name} ({room})" : $"room {room}",
            roomsGameData: () => RoomTracker.RoomsGameData);
        Profile.ProfileSaving += HangupItems.StampForSave;
        Profile.ProfileLoaded += _ => HangupItems.OnProfileLoaded();
        // The lives a `stat` on this connection gave: the count carried over a
        // reconnect is the one from before the link dropped.
        Stats.ScreenParsed += screen =>
        {
            // The name is on the same row of the screen as the lives.
            if (Stats.LastCaptureReadLives) HangupItems.NoteLivesRead(screen.Lives, screen.Name);
        };
        // Stock's word, on the way in, that the last exit was a hang-up it didn't
        // let go free. Without it a life lost isn't taken as lost to that hang-up.
        Router.Subscribe(Services.Patterns.KnownPatterns.HangupLoginNotice, _ => HangupItems.NoteHangupLoginLine());
        // Two things change the game's lives count with no screen telling the
        // client: a life asked back after a death, and a level trained (which
        // gives lives). The count the list carries must not be the stale one.
        SysopGodLife.LifeRequested += () => HangupItems.NoteLivesChangedUnread("a life was asked back");
        Router.Subscribe(Services.Patterns.KnownPatterns.TrainAttainLevel,
            _ => HangupItems.NoteLivesChangedUnread("a level was trained"));
        Router.Subscribe(Services.Patterns.KnownPatterns.TrainAttainNextLevel,
            _ => HangupItems.NoteLivesChangedUnread("a level was trained"));
        // A death the check works out after the fact (RoomTracker.NoteUnwitnessedDeath)
        // reaches only the handlers that still make sense minutes later, in the room
        // the character woke in: the engine stop (PlayerDeathMovementHalt), Death
        // Recovery's grid, the default task (DefaultTaskRunner) and these two. The
        // life is as spent as in a death that was seen, whenever it is found out;
        // it is asked for under the master switch like everything else this check
        // sends: a `stat` the user types can bring the verdict, and with the switch
        // off nothing automatic goes out. An arena death spent none.
        RoomTracker.UnwitnessedDeathRecorded += () =>
        {
            if (!AutoModeController.KillSwitchEngaged) SysopGodLife.OnDeath(RoomTracker.LastDeathTookNothing);
        };
        // The buff timers were only frozen when the link dropped. All of our own are
        // cleared for a death found at the login only: one found later would wipe the
        // timers of buffs cast since.
        RoomTracker.PlayerDeathInferred += () => CastDirector.ClearSelfBuffTracking();
        // A death found later (the verdict waited on a `stat`, typed or sent once
        // Auto-All was back on) still wiped the buffs that were on when the game was
        // left. Those timers were carried over the time away and are known by it, so
        // exactly they go, the hazard buff's with them, and what was raised since
        // stays. Raised for a death found at the login as well, where the blanket
        // clear above follows.
        RoomTracker.UnwitnessedDeathRecorded += () =>
        {
            CastDirector.ClearCarriedSelfTimers("a death while out of the game, found after the login");
            AutoHazardCounterProvisioner.ForgetCarried("a death while out of the game, found after the login");
        };
        // The event the other engines take a death of our own from (both wordings).
        RoomTracker.PlayerDeathObserved += HangupItems.OnPlayerDied;
        InGameCapture.InGameChanged += HangupItems.OnInGameChanged;

        // Buff timers stand still while the character is out of the game, and what
        // they had left is kept in its profile between sessions. Wired here on
        // purpose:
        //  - after the hang-up check's own handler, so the profile save made on the
        //    way out of the game finds that check already out of it and leaves its
        //    list alone;
        //  - after the cast pass's and the hazard counter's profile-load resets
        //    (both subscribed far above), so the restore lands on a clean slate with
        //    the character's game data already the active set;
        //  - before the start-up profile is loaded below.
        BuffTimers = new Game.Spells.BuffTimerKeeper(
            CastDirector,
            AutoHazardCounterProvisioner,
            // A named character only, as for the hang-up list above.
            profile: () => Profile.CurrentProfileName is null ? null : Profile.Current,
            profileName: () => Profile.CurrentProfileName,
            realmKey: () => ResolveActiveRealm() is { } played ? $"{played.Bbs.Name}/{played.Realm.Name}" : null,
            // One of these runs inside the Disconnected handler, where a save that
            // threw would skip the reconnect scheduling.
            saveProfile: () =>
            {
                try { Profile.Save(); }
                catch (Exception ex)
                {
                    Log.Warn(Game.Spells.BuffTimerKeeper.LogCategory,
                        $"Couldn't save the buff timers ({ex.GetType().Name}: {ex.Message}).");
                }
            },
            log: Log);
        InGameCapture.InGameChanged += inGame =>
        {
            if (!inGame) BuffTimers.NoteLeftGame("the link dropped or the game was left");
        };
        // Back in on a prompt, and told of every one: the first after time out of the
        // game runs the timers on, and any of them means a client that had begun to
        // close did not. Subscribed after InGameCapture (which the hang-up check
        // hears through), so that check is already in the game when the keeper's
        // save on the way in runs.
        PromptScanner.PromptObserved += _ => BuffTimers.NoteBackInGame();
        Profile.ProfileSaving += BuffTimers.WriteTo;
        Profile.ProfileClosed += BuffTimers.NoteProfileClosed;
        Profile.ProfileLoaded += BuffTimers.RestoreFrom;

        // The opens owed for what a hang-up took end with the check that would
        // have picked it back up (AutoOpen is built far above).
        HangupItems.CheckFinished += AutoOpen.OnHangupCheckFinished;
        Inventory.FullInventoryParsed += HangupItems.OnInventoryRead;
        Inventory.ItemTaken += HangupItems.OnItemTaken;
        GroundItems.SurveyUpdated += HangupItems.NoteFloorSurveyed;
        // After CombatTracker's own handler (subscribed far above), so the fight
        // reads as over on the observation that ended it.
        RoomClassifier.EntitiesObserved += _ => HangupItems.OnRoomObserved();
        CombatTracker.CombatForceCleared += HangupItems.OnRoomObserved;
        Tick.HeartbeatElapsed += HangupItems.OnHeartbeat;
        OutboundMovement.MoveSent += HangupItems.NoteMoveSent;
        RoomTracker.StateChanged += t =>
        {
            if (!Nullable.Equals(t.PreviousRoom?.Key, t.NewRoom?.Key)) HangupItems.OnRoomChanged();
        };

        // Once, with the version that stopped the default profile being written: it
        // goes back to the installed defaults. Ahead of the load below, which may be
        // of that profile.
        Models.Profile.CharacterProfile? defaultProfileBeforeReset = Profile.ResetDefaultProfileOnce();

        // Startup profile priority: a --profile launch argument wins over the
        // "Auto-load last profile" setting, which wins over a blank draft. The CLI
        // token is resolved here (not in Program.Main) because resolving a bare
        // name needs the saved-profile list, and Profile is live by now. An
        // unresolved token logs a warning and falls through to the normal path
        // rather than failing to launch.
        bool cliRequested = StartupOptions.RequestedProfileToken is not null;
        Models.Profile.ProfileRef? cliStartup = null;
        if (StartupOptions.RequestedProfileToken is { } cliToken)
        {
            cliStartup = StartupOptions.ResolveToken(
                cliToken, System.Linq.Enumerable.ToList(Profile.ListAll()), out string? cliError);
            if (cliStartup is null)
            {
                // Token given but didn't resolve (typo / ambiguous bare name). Stash
                // the reason for the main window to show on the terminal, and open a
                // blank draft — deliberately NOT auto-load-last, so we never quietly
                // launch a *different* character when the requested one didn't load.
                StartupOptions.ProfileNotice = $"--profile \"{cliToken}\" did not load: {cliError}";
                Log.Warn("Startup", StartupOptions.ProfileNotice + " Opened a blank profile instead.");
            }
            else
                Log.Info("Startup",
                    $"--profile: loading '{cliStartup.Name}' on '{cliStartup.Bbs}'" +
                    (StartupOptions.SpawnedSiblings > 0 ? $" (+{StartupOptions.SpawnedSiblings} sibling instance(s) launched)" : ""));
        }

        if (cliStartup is { } cli)
        {
            try
            {
                Profile.Load(cli.Bbs, cli.Name);
            }
            catch (Exception ex)
            {
                // Shown on the terminal too: coming up on the default profile with no
                // word why reads as "my profile won't load".
                StartupOptions.ProfileNotice =
                    $"Couldn't load '{cli.Name}' on '{cli.Bbs}' ({ex.GetType().Name}: {ex.Message}); opened the default profile instead.";
                Log.Warn("Startup", StartupOptions.ProfileNotice);
                Profile.LoadDefaultProfile();
            }
        }
        // A --profile was given but didn't resolve: blank draft (the notice is already
        // stashed for the terminal), and we skip auto-load-last on purpose.
        else if (cliRequested)
        {
            Profile.LoadDefaultProfile();
        }
        // Auto-load last profile: with File → "Auto-load last profile on startup"
        // on, reopen the last session; otherwise (the default) open a blank draft and
        // let the user pick / build one via File → Profile Management… or the
        // recent-profile entries.
        // A last-used profile that was since deleted / renamed throws on Load, so
        // fall back to the blank draft rather than failing startup.
        else if (Settings.Current.StartupProfile() is { } startup)
        {
            try
            {
                Profile.Load(startup.Bbs, startup.Name);
            }
            catch (Exception ex)
            {
                StartupOptions.ProfileNotice =
                    $"Couldn't auto-load '{startup.Name}' on '{startup.Bbs}' ({ex.GetType().Name}: {ex.Message}); opened the default profile instead.";
                Log.Warn("Startup", StartupOptions.ProfileNotice);
                Profile.LoadDefaultProfile();
            }
        }
        // Coming up on the unnamed draft in the launch that reset the default
        // profile: what it held is loaded as that draft, so whoever was playing on
        // it finds their setup where it was and is asked to name it.
        else if (defaultProfileBeforeReset is { } held)
        {
            Profile.LoadDraftFromBeforeTheReset(held);
            StartupOptions.ProfileNotice =
                "The default profile has been put back to the installed defaults, and can no longer be changed. "
                + "What yours held is loaded here as an unnamed profile: Save (Ctrl+S) names it and keeps it as a "
                + "character, and closing the client will ask. Discarded, it is gone.";
            Log.Info("Startup", StartupOptions.ProfileNotice);
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
        // and editable from Settings → Other ("Cleanup Player Database after").
        int cleanupDays = Settings.Current.PlayerCleanupDays;
        if (cleanupDays > 0)
        {
            int removed = Players.PurgeStale(cleanupDays, DateTime.UtcNow);
            if (removed > 0)
                Log.Info("PlayerDatabase",
                    $"Pruned {removed} stale player record(s) older than {cleanupDays} day(s).");
        }

        // Last, because starting the control API subscribes to services this ctor
        // builds along the way (the movement coordinator in particular). Anything
        // added below this line is NOT visible to the API's event stream.
        ApplyLocalApiFromGlobalSettings();

        // Startup update check — fire-and-forget off the UI thread, gated on the
        // Global "Check for updates automatically" toggle. It only sets the
        // availability flag (splash banner + title-bar crawl read it); nothing
        // installs on its own.
        if (Settings.Current.AutoCheckForUpdates)
            _ = Update.CheckAsync();

        // ...and again twice a day, so a client left running for days doesn't keep
        // reporting whatever was true at launch. Armed unconditionally and gated per
        // tick, so toggling the setting takes effect without a restart.
        Update.StartAutoChecks(() => Settings.Current.AutoCheckForUpdates);
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

    private void ApplyContextMenuFromActiveProfile()
    {
        Models.Profile.ContextMenuSettings dto = ReadSection<Models.Profile.ContextMenuSettings>(Profile.Current, "ContextMenu");
        ContextMenu.ApplyFrom(dto);
    }

    private void ResetContextMenuToDefaults()
    {
        ContextMenu.ApplyFrom(new Models.Profile.ContextMenuSettings());
    }

    // Guards the persist-on-Changed handler while we're pushing values INTO
    // LogDiagnostics from disk — otherwise applying the loaded state would
    // immediately write it straight back.
    private bool _suppressLogDiagnosticsPersist;

    // Re-checks the staged unrecognized lines against the set's catalogue a few
    // milliseconds at a time, whenever the UI has nothing else to do. Done in one go
    // inside the set switch it was half a second of the launch freeze, for
    // housekeeping nothing waits on. A newer set switch abandons the pass.
    private int _candidatePruneRun;

    private void PruneMessageCandidatesWhenIdle()
    {
        int run = ++_candidatePruneRun;
        List<Models.GameData.MessageCandidateRecord> pending = MessageCandidates.Candidates.ToList();
        int next = 0, pruned = 0;
        Avalonia.Threading.Dispatcher.UIThread.Post(Slice, Avalonia.Threading.DispatcherPriority.ApplicationIdle);

        void Slice()
        {
            if (run != _candidatePruneRun) return;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            while (next < pending.Count
                   && System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds < 8)
                if (MessageCandidateWatcher.PruneIfRecognized(pending[next++])) pruned++;
            if (next < pending.Count)
                Avalonia.Threading.Dispatcher.UIThread.Post(Slice, Avalonia.Threading.DispatcherPriority.ApplicationIdle);
            else
                MessageCandidateWatcher.ReportPruned(pruned);
        }
    }

    // Times the per-set index rebuilds of each set load for the program log. Made on
    // first use: the first of those rebuilds is wired midway through the constructor.
    private SetIndexTimings? _setIndexTimings;
    private SetIndexTimings SetIndexTimes => _setIndexTimings ??= new SetIndexTimings(GameData, Log);

    private Action<string?> TimedSetIndex(string name, Action<string?> rebuild) => SetIndexTimes.Timed(name, rebuild);

    private void WarmGameDataLists()
    {
        try
        {
            Game.GameData.TrainerCatalog.Enumerate(GameData);
            Game.GameData.BankCatalog.Enumerate(GameData);
            Game.Map.LevelGatedRooms.Compute(RoomGraph);
            // Both read the Rooms table, which is in memory while a set loads and
            // dropped a few minutes on: left to the first item window opened or room
            // hovered, each read all of it back on the UI thread first.
            Game.GameData.ItemUsedOnIndex.Warm(GameData);
            RoomFloorItems.Warm();
            // Reading Rooms here restarts its idle clock, and it is the largest
            // table: hand it back so these two builds don't hold it minutes longer.
            GameData.EvictTable("Rooms");
        }
        catch (InvalidOperationException ex)
        {
            // The set switched again mid-build and the graph was cleared under us;
            // that reload warms its own lists, and nothing half-built is kept.
            Log.Debug("GameData", $"list warm-up overtaken by a set switch ({ex.Message})");
        }
    }

    private void WarmQuestData(string? className)
    {
        int? classId = Game.Quests.CompletedQuestBonuses.ResolveClassId(GameData, className);
        foreach (Game.Quests.CrawledQuest quest in Game.Quests.QuestCrawler.Crawl(GameData, classId))
            Game.Quests.QuestStepGraph.Build(GameData, quest.Flag, quest.ProgressByValue);
    }

    // One block for the session-statistics log: the Session Statistics window's
    // three sections as they stand now, headed by whose they are.
    private string SessionStatsBlock(DateTimeOffset at, string why)
    {
        string who = Profile.CurrentProfileName ?? "{default}";
        if (ResolveActiveRealm() is { } active) who += $" — {active.Bbs.Name}:{active.Realm.Name}";
        Game.Map.LoopRunner runner = LoopRunner;
        bool anyLaps = runner.LapHistory.Count > 0;
        bool running = runner.State != Game.Map.LoopState.Idle;
        return SessionStatsLogFormatter.Format(at, who, why,
            CombatSession.Snapshot(), TimeAnalysis.Snapshot(), SessionActivity.Snapshot(),
            SelfTimeToLevel(), PlayerStats.Level, Currency.RunicName,
            new SessionStatsLogFormatter.Laps(
                runner.CurrentLoop?.Name ?? runner.LastRunLoopName, running, runner.CompletedLaps,
                anyLaps ? runner.LapHistory[^1] : null,
                anyLaps ? runner.AverageLapTime : null,
                running ? runner.CurrentLapTime : null));
    }

    private void ApplyLogDiagnostics(Models.Settings.LogDiagnosticsSettings dto)
    {
        _suppressLogDiagnosticsPersist = true;
        LogDiagnostics.DebugDiagnostics  = dto.Debug;
        LogDiagnostics.CombatDiagnostics = dto.Combat;
        LogDiagnostics.AutoCollectLogs   = dto.AutoCollect;
        LogDiagnostics.HopTiming         = dto.HopTiming;
        LogDiagnostics.CaptureUnrecognizedMessages = dto.CaptureUnrecognizedMessages;
        LogDiagnostics.SessionStatisticsMinutes = dto.SessionStatisticsMinutes;
        LogDiagnostics.LogSessionStatistics = dto.SessionStatistics;
        _suppressLogDiagnosticsPersist = false;
    }

    // The switches used to be saved per character. Until they've been saved
    // globally, the first character loaded hands over its own, so whatever the
    // user had set carries across the move.
    private void AdoptCharacterLogDiagnostics()
    {
        if (Settings.Current.LogDiagnostics is not null) return;
        if (Profile.Current?.Settings is not { } sections || !sections.ContainsKey("LogDiagnostics")) return;
        ApplyLogDiagnostics(ReadSection<Models.Settings.LogDiagnosticsSettings>(Profile.Current, "LogDiagnostics"));
        PersistLogDiagnostics();
    }

    private void PersistLogDiagnostics()
    {
        if (_suppressLogDiagnosticsPersist) return;
        Settings.Current.LogDiagnostics = new Models.Settings.LogDiagnosticsSettings
        {
            Debug      = LogDiagnostics.DebugDiagnostics,
            Combat     = LogDiagnostics.CombatDiagnostics,
            AutoCollect = LogDiagnostics.AutoCollectLogs,
            HopTiming  = LogDiagnostics.HopTiming,
            CaptureUnrecognizedMessages = LogDiagnostics.CaptureUnrecognizedMessages,
            SessionStatistics = LogDiagnostics.LogSessionStatistics,
            SessionStatisticsMinutes = LogDiagnostics.SessionStatisticsMinutes,
        };
        Settings.Save();
    }

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

    // Sound cues: every hook is a listener on a signal the client already raises, and
    // SoundCueEngine.Fire returns at once (the player works on a pool thread), so no
    // handler here adds work to the path that raised it. Called once every service
    // below exists.
    private void WireSounds()
    {
        SoundPlayer = new SoundPlayer(AppPaths.SoundsDir, Log);
        Sounds = new Game.Sounds.SoundCueEngine(
            () => ReadSection<Models.Profile.SoundSettings>(Profile.Current, ViewModels.Settings.SoundsSectionViewModel.TabKey),
            SoundPlayer.Play, log: Log);
        Triggers.PlaySound = file => Sounds.FireWith(Game.Sounds.SoundCues.Trigger, file);

        // Progress.
        Router.Subscribe(Patterns.KnownPatterns.TrainAttainLevel, _ => Sounds.Fire(Game.Sounds.SoundCues.LevelUp));
        Router.Subscribe(Patterns.KnownPatterns.TrainAttainNextLevel, _ => Sounds.Fire(Game.Sounds.SoundCues.LevelUp));
        MonsterDeath.MonsterDied += _ => Sounds.NoteKill();
        LoopRunner.Event += e =>
        {
            if (e.Kind == Game.Map.LoopEventKind.RepeatStarted) Sounds.NoteLap(LoopRunner.CompletedLaps);
            else if (e.Kind == Game.Map.LoopEventKind.Failed) Sounds.Fire(Game.Sounds.SoundCues.NavigationStopped);
        };
        Walker.Event += e =>
        {
            // A walk-to the player asked for, not a loop's approach or a detour's leg.
            if (e.Kind == Game.Map.WalkEventKind.Finished)
            {
                if (LoopRunner.State == Game.Map.LoopState.Idle && !AutoDeposit.IsRerouting
                    && !SellDetour.IsDetouring && !TrainerWalk.IsBusy && !StashTransfer.IsBusy)
                    Sounds.Fire(Game.Sounds.SoundCues.WalkFinished);
            }
            else if (e.Kind == Game.Map.WalkEventKind.Failed) Sounds.Fire(Game.Sounds.SoundCues.NavigationStopped);
        };
        RoomTracker.StateChanged += t =>
        {
            if (t.NewConfidence == Game.Map.RoomConfidence.Lost && t.PreviousConfidence != Game.Map.RoomConfidence.Lost)
                Sounds.Fire(Game.Sounds.SoundCues.NavigationStopped);
        };
        Profile.ProfileLoaded += _ => Sounds.ResetCounts();
        Profile.ProfileClosed += Sounds.Invalidate;

        // Automation.
        bool training = false;
        TrainerWalk.StateChanged += () =>
        {
            bool busy = TrainerWalk.IsBusy;
            if (busy && !training) Sounds.Fire(Game.Sounds.SoundCues.AutoTrain);
            training = busy;
        };
        SellDetour.DetouringChanged += () =>
        {
            if (SellDetour.IsDetouring) Sounds.Fire(Game.Sounds.SoundCues.AutoSell);
        };
        Events.Fired += e =>
        {
            if (!string.IsNullOrWhiteSpace(e.Sound)) Sounds.FireWith(Game.Sounds.SoundCues.EventFired, e.Sound);
        };

        // Bosses.
        BossTimers.BossKilled += _ => Sounds.Fire(Game.Sounds.SoundCues.BossKilled);
        Game.Sounds.SoundBossWatcher bossSounds = new(Sounds, Bosses, BossTimers, GameData, () => EventScheduler.IsInGame);
        EventScheduler.ClockTick += bossSounds.Evaluate;

        // Chat and party. An "@" telepath is a remote command, not someone talking.
        Chat.EntryClassified += entry =>
        {
            if (entry.Channel == Game.ChatChannel.TelepathIncoming
                && !entry.Message.TrimStart().StartsWith('@'))
                Sounds.Fire(Game.Sounds.SoundCues.Telepath);
        };
        Router.Subscribe(Patterns.KnownPatterns.PartyInviteReceived, _ => Sounds.Fire(Game.Sounds.SoundCues.PartyInvite));
        AllyDropped.AllyDown += _ => Sounds.Fire(Game.Sounds.SoundCues.PartyMemberDown);

        // Danger.
        DeathWatcher.PlayerDied += _ => Sounds.Fire(Game.Sounds.SoundCues.Death);
        bool down = false;
        PlayerState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(Game.PlayerState.Hp)) return;
            bool now = PlayerState.IsMortallyWounded;
            if (now && !down) Sounds.Fire(Game.Sounds.SoundCues.MortallyWounded);
            down = now;
        };
        Health.FleeStarted += () => Sounds.Fire(Game.Sounds.SoundCues.Flee);
    }

    // The master switch reaches the automatic systems that have no toggle of their
    // own. Each is handed the probe here, under the name its skips are counted by
    // (bug report, switch-on log line). The toggled engines read the switch
    // through ReadAutoModeFlag; remote commands, triggers, events, hang-ups, the
    // default task and the hand-started runs are wired where they are built.
    // Called once every service below exists: a probe handed to a service that is
    // not built yet would crash at startup.
    private void WireMasterSwitch()
    {
        // Party signals and party upkeep.
        PartyRest.MasterSwitchOff = MasterSwitchOff("Party wait signals");
        Profile.ProfileLoaded += _ => PartyRest.ForgetLeaderHold();
        PartyWaitMovement.MasterSwitchOff = () => AutoModeController.KillSwitchEngaged;
        AilmentSync.MasterSwitchOff = MasterSwitchOff("Ailment announces");
        TooHeavyWait.MasterSwitchOff = MasterSwitchOff("Info polls");
        PartyPoller.MasterSwitchOff = MasterSwitchOff("Party polls");
        PartyProbe.MasterSwitchOff = MasterSwitchOff("Party polls");
        PartyLevel.MasterSwitchOff = MasterSwitchOff("Party polls");
        PartyWealth.MasterSwitchOff = MasterSwitchOff("Party polls");
        LeaderBossTravel.MasterSwitchOff = MasterSwitchOff("Party polls");
        Party.MasterSwitchOff = MasterSwitchOff("Party upkeep");
        AutoParty.MasterSwitchOff = MasterSwitchOff("Party upkeep");
        PartyComeback.MasterSwitchOff = MasterSwitchOff("Party upkeep");
        ComebackRequest.MasterSwitchOff = MasterSwitchOff("Party upkeep");
        PartyDeathCleanup.MasterSwitchOff = MasterSwitchOff("Party upkeep");
        PartyTrain.MasterSwitchOff = MasterSwitchOff("Party training");
        LeaderDoorAssist.MasterSwitchOff = MasterSwitchOff("Party upkeep");
        TrapDelegation.MasterSwitchOff = MasterSwitchOff("Party polls");

        // Info polls, token looks, quest syncs, position fixes.
        Tokens.MasterSwitchOff = MasterSwitchOff("Token looks");
        ItemCharges.MasterSwitchOff = MasterSwitchOff("Info polls");
        QuestFlagSync.MasterSwitchOff = MasterSwitchOff("Quest sync");
        InventoryAfterDeath.MasterSwitchOff = MasterSwitchOff("Info polls");
        AlignmentCheck.MasterSwitchOff = MasterSwitchOff("Info polls");
        PvpStrangers.MasterSwitchOff = MasterSwitchOff("Info polls");
        PlayerDeathHalt.MasterSwitchOff = MasterSwitchOff("Position fixes");
        CombatTracker.MasterSwitchOff = MasterSwitchOff("Position fixes");

        // Social.
        Greet.MasterSwitchOff = MasterSwitchOff("Greets and looks");
        PlayerLook.MasterSwitchOff = MasterSwitchOff("Greets and looks");
        LevelUp.MasterSwitchOff = MasterSwitchOff("Level-up announce");
        Divert.MasterSwitchOff = MasterSwitchOff("Telepath divert");

        // Training, money, gear, loot, PvP.
        TrainerWalk.MasterSwitchOff = MasterSwitchOff("Auto-train");
        AutoTrain.MasterSwitchOff = MasterSwitchOff("Auto-train");
        Cash.MasterSwitchOff = MasterSwitchOff("Bank and stash trips");
        LocationEquip.MasterSwitchOff = MasterSwitchOff("Location gear");
        CounterWear.MasterSwitchOff = MasterSwitchOff("Room-spell counter");
        // The route planner asks about every hazard exit it weighs: a plain read,
        // so a plan made with the switch off doesn't count hundreds of skips.
        CounterWear.MasterSwitchIsOff = () => AutoModeController.KillSwitchEngaged;
        AutoModeController.KillSwitchToggled += _ => _negatorUsable.Clear();
        PathItemFloor.MasterSwitchOff = MasterSwitchOff("Route item pickup");
        ChestOpens.MasterSwitchOff = MasterSwitchOff("Chest open read");
        ChestOpens.RefuseOpen = name =>
        {
            if (!AutoModeController.Blocks("Chest open read", $"Open button ({name})")) return false;
            Log.Info(Game.Inventory.ChestOpenTracker.LogCategory,
                $"Open button for {name} refused: the master switch is off.");
            WriteTerminalNotice("[Chest Offload cannot open and read a chest: the master switch (Auto-All) is off]");
            return true;
        };
        AutoDiscard.MasterSwitchOff = () => AutoModeController.KillSwitchEngaged;
        AutoHazardCounterProvisioner.MasterSwitchOff = MasterSwitchOff("Hazard counter item");
        ManaRegen.MasterSwitchOff = MasterSwitchOff("Mana-regen reroll");
        PvpFight.MasterSwitchOff = MasterSwitchOff("PvP response");

        MovementControl.RefuseResume = () => RefuseStartForMasterSwitch("Run", "resume");
        LoopRunner.MasterSwitchOff = () => AutoModeController.KillSwitchEngaged;

        AutoModeController.DescribeInFlight = DescribeInFlightForMasterSwitch;
        AutoModeController.KillSwitchToggled += OnMasterSwitchChanged;
        AutoModeController.ResetByProfileLoad += OnMasterSwitchResetByProfileLoad;
        // A switch-on that arrived outside the game is finished on the way back in.
        // Another character loaded first ends it as a profile load ends the switch.
        // Posted: the prompt that says so is read off the wire ahead of the room it
        // came with, and the engines re-run here decide on that room.
        InGameCapture.InGameChanged += inGame =>
        {
            if (inGame && _masterSwitchSettleOwed)
                Avalonia.Threading.Dispatcher.UIThread.Post(FinishSwitchOnOwedOutsideTheGame);
        };
        Profile.ProfileLoaded += _ =>
        {
            if (_masterSwitchSettleOwed) OnMasterSwitchResetByProfileLoad();
            RemoteCommands.DropHeldComebacks("another profile was loaded");
        };
        SneakGuard.MasterSwitchOff = () => AutoModeController.KillSwitchEngaged;
    }

    // What the master switch is about to stop or hold, for its one log line.
    private string DescribeInFlightForMasterSwitch()
    {
        List<string> parts = new();
        if (AutoLair.IsActive) parts.Add("Auto-Lair frozen");
        else if (LoopRunner.State != Game.Map.LoopState.Idle) parts.Add($"loop '{LoopRunner.CurrentLoop?.Name}' frozen");
        else if (Walker.State != Game.Map.WalkState.Idle) parts.Add("walk frozen");
        List<string> holds = MovementCoordinator.AssertedGates
            .Where(g => g != Game.Map.MovementCoordinator.UserGate && g != Game.Map.MovementCoordinator.AutoAllGate)
            .ToList();
        if (holds.Count > 0) parts.Add($"holds released: {string.Join(", ", holds)}");
        if (PartyRest.IsHoldingWait) parts.Add($"a @wait is out ({string.Join(", ", PartyRest.HeldReasons)}): nothing more is sent");
        if (Events.RunSummary != "(none)") parts.Add($"event held: {Events.RunSummary}");
        if (PvpFight.IsActive) parts.Add("PvP fight ended");
        if (AllyDropped.DownedGivenNames.Count > 0) parts.Add("downed-ally rescue dropped");
        return parts.Count == 0 ? "Nothing was in flight." : "In flight: " + string.Join("; ", parts) + ".";
    }

    // The room a post-fight collect was cancelled in as the master switch went
    // off; null when none was.
    private Game.Map.RoomKey? _collectOwedInRoom;

    // A profile load cleared a switch that was off. Only the freeze and the parked
    // holds are let go: the rest of the switch-on work re-runs engines and talks
    // to the party, and this arrives in the middle of the load, with half the
    // services still holding the last character's state.
    private void OnMasterSwitchResetByProfileLoad()
    {
        _collectOwedInRoom = null;
        _masterSwitchSettleOwed = false;
        _masterSwitchEventsOwed = false;
        LoopRunner.DropResumeHeldForMasterSwitch("another profile was loaded");
        MovementControl.ReleaseFromAutoAll();
    }

    // The switch came back on with the link down or the character at the board's
    // menus. Everything the switch-on does sends or starts something, so it waits
    // for the first game prompt, with movement still frozen until then.
    private bool _masterSwitchSettleOwed;
    // Set with it when the events have yet to be told the switch is back on, and
    // whether that switch-on was a remote one.
    private bool _masterSwitchEventsOwed;
    private bool _masterSwitchEventsOwedRemote;

    // The first game prompt after a switch-on made outside the game.
    private void FinishSwitchOnOwedOutsideTheGame()
    {
        if (!_masterSwitchSettleOwed || !InGameCapture.InGame) return;
        SettleAfterMasterSwitchOn();
        if (!_masterSwitchEventsOwed) return;
        _masterSwitchEventsOwed = false;
        Events.NoteMasterSwitchChanged(_masterSwitchEventsOwedRemote);
    }

    // The master switch changed. Off: freeze movement and park the holds, then
    // drop what would otherwise go on sending by itself. On: put back every hold
    // still owed and re-run the engines that derive one from current state, all
    // before the freeze lifts, so nothing moves ahead of a hold that came due
    // meanwhile; then settle the party signals.
    private void OnMasterSwitchChanged(bool off)
    {
        if (off)
        {
            _masterSwitchSettleOwed = false;
            _masterSwitchEventsOwed = false;
            // Ahead of everything else: the leader is let go before this client
            // goes quiet.
            PartyRest.ReleaseForMasterSwitch();
            MovementControl.SuspendForAutoAll();
            // The events' clocks stop now, not at their next tick.
            Events.NoteMasterSwitchChanged();
            // Each thing ended here is either held where it stands or put back
            // when the switch comes on. Held: the sneak hold's queue (SneakGuard
            // stops re-checking) and a comeback recovery (its walk freezes, its
            // timers wait). Put back: a downed ally's rescue, whose aid and
            // @health poll would go on by themselves, and a collect put off until
            // the fight ends, which would go out when it does.
            AllyDropped.HoldForMasterSwitch();
            // A PvP fight is ended outright. An Enemy still in the room when the
            // switch is back on draws the response afresh from the roster.
            if (PvpFight.IsActive) PvpFight.Stop("the master switch went off");
            bool collectOwed = Cash.CancelDeferredCollect("master switch off");
            collectOwed |= AutoGetItems.CancelDeferredCollect("master switch off");
            _collectOwedInRoom = collectOwed ? RoomTracker.State.CurrentRoom?.Key : null;
            ReevaluateEnginesForMasterSwitch();
            return;
        }

        // Not the user's own press (a party member's `@auto-all on`, the local
        // API): nobody is taken to be at the keyboard, so the events ask nothing.
        bool remote = !AutoModeController.SwitchedByUserPress;
        // InGameCapture, not the event scheduler's latch: that one stays set at the
        // board's menu after an `exit` with the link still up.
        if (!InGameCapture.InGame)
        {
            _masterSwitchSettleOwed = true;
            Log.Info("AutoMode",
                "Master switch back on outside the game: movement stays frozen and nothing is sent or started until the first game prompt.");
            // With the link down the events know it: their clocks restart now, they
            // may ask which waiting events to run, and they start nothing. At the
            // board's menu they do not know (to them the link is up), so they hear
            // of the switch on the way back in.
            _masterSwitchEventsOwed = InGameCapture.AtBoardMenu;
            _masterSwitchEventsOwedRemote = remote;
            if (!_masterSwitchEventsOwed) Events.NoteMasterSwitchChanged(remote);
            return;
        }
        SettleAfterMasterSwitchOn();
        // Last, with movement free again: an event's Then that landed meanwhile
        // runs, and with nothing running the next waiting event starts.
        Events.NoteMasterSwitchChanged(remote);
    }

    // Everything the switch-on sends or starts, run at once in the game and at the
    // first game prompt otherwise. The events are not told from here: they hear of
    // the switch when it is switched and of the way back in from the scheduler.
    private void SettleAfterMasterSwitchOn()
    {
        _masterSwitchSettleOwed = false;
        MovementControl.RestoreHoldsBeforeAutoAllRelease();
        ReevaluateEnginesForMasterSwitch();
        AllyDropped.ResumeOwedRescues();
        // A hide refused in one room and held for the next was not retried
        // while the switch was off.
        AutoDiscard.RecheckHeldHides();
        PartyComeback.SettleAfterMasterSwitch();
        RemoteCommands.ReplayHeldComebacks();
        // The collect cancelled on the way off: if we still stand in that room,
        // show it once more, and the get engines take what is still on the floor.
        if (_collectOwedInRoom is { } owedIn && RoomTracker.State.CurrentRoom?.Key == owedIn
            && (ReadAutoModeFlag(d => d.AutoGetItems) || CashCollectionOn()))
        {
            Log.Info("AutoMode", "Master switch back on — showing the room again for the pickup it put off.");
            _engineWireSend?.Invoke(new[] { (byte)'\r' });
        }
        _collectOwedInRoom = null;
        // A follower standing in a hazard room whose arrival was skipped while off
        // raises the buff now; the per-item timer was never stamped by the skip, and
        // an own walk is left to its next approach hook.
        if (RoomTracker.State.CurrentRoom is { } hazardRoom)
        {
            AutoHazardCounterProvisioner.OnArrivedInRoom(hazardRoom.Key);
        }
        // The same for a counter that has to be worn: put on if the room stood in
        // needs it, given back if one worn before the switch went off is no longer
        // needed here. Neither was done while it was off. Routes planned while it
        // was off counted no carried counter; the next plan asks again.
        _negatorUsable.Clear();
        CounterWear.Recheck();
        AilmentSync.ReevaluateWaits();
        PartyRest.ResyncAfterMasterSwitch();
        // A loop the last reconnect set aside, not restarted while the switch was
        // off. Behind the freeze still, so its first step waits for the holds.
        LoopRunner.ResumeAfterMasterSwitch();
        // Ahead of the release: the walk it frees plans on a purse that is being
        // read, with its tolls closed, not on one known to be wrong.
        SendOwedInventoryReads();
        MovementControl.ReleaseFromAutoAll();
        DeathRecovery.OnAutoAllRestored();
    }

    // The switch moves the toggles through a profile reseed, which is not a
    // genuine flip, so nothing re-decides on its own. The engines that only
    // decide on an event are told here, as a hand flip of a toggle tells them:
    // going off, a hold in progress is let go; coming on, a monster already in
    // the room is fought and a rest that is due starts.
    private void ReevaluateEnginesForMasterSwitch()
    {
        CombatTracker.OnAutoAttackChanged();
        RoomClassifier.ReemitCurrent();
        Health.Evaluate();
        CastDirector.Evaluate();
    }

    // Settings → Health reads its mana thresholds as raw amounts rather than percents;
    // a buff slot's own mana floor is in the same unit.
    public bool ManaThresholdsAreAbsolute =>
        ReadSection<Models.Profile.HealthSettings>(Profile.Current, "Health").MaThresholdMode
            == Models.Profile.ThresholdMode.Absolute;

    // Generic per-section settings reader. Returns a fresh default-
    // constructed DTO when the profile is null, has no Settings dict,
    // is missing the named entry, or the JSON is malformed — the
    // callers all want a non-null DTO they can apply unconditionally.
    private static T ReadSection<T>(Models.Profile.CharacterProfile? profile, string key)
        where T : new()
    {
        if (profile?.Settings is null) return new T();
        if (!profile.Settings.TryGetValue(key, out System.Text.Json.JsonElement json)) return new T();
        try
        {
            // Straight from the element: copying it out as text first cost a string
            // the size of the section on every read, and engines read on every line.
            return System.Text.Json.JsonSerializer.Deserialize<T>(json) ?? new T();
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
        if (!row.TryGetProperty("WeaponType", out GameValue wt)) return false;
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
        if (GameData.FindRowByName("Items", itemName) is not GameRow row)
            return null;
        return Game.Inventory.EquipmentSlotMap.SlotForItem(row);
    }

    // True when the live character can actually wear the named carried item —
    // gated by level / class / alignment against the active game-data set. Feeds
    // the inventory-fallback planner so it never queues gear the game would reject.
    // An unknown item (no Items row) resolves false: don't queue what we can't verify.
    private bool CanCharacterEquipItem(string itemName)
    {
        if (GameData.FindRowByName("Items", itemName) is not GameRow row)
            return false;
        Game.Inventory.ClassEquipProfile cls =
            Game.Inventory.ItemEquipFilter.ResolveClassProfile(GameData, PlayerStats.Class);
        Game.RealmType realm = GameData.ActiveRealm;
        Game.Calculators.AlignmentBucket? bucket =
            Game.Inventory.ItemEquipFilter.GearBucketForWord(Alignment.SelfAlignment, realm);
        return Game.Inventory.ItemEquipFilter.CanEquip(row, PlayerStats.Level, cls, bucket, realm,
            Alignment.SelfEvilPoints(realm));
    }

    // A gear set disagrees with our recorded alignment: it holds an item blocked on
    // alignment alone (wearable at our level and class), or alignment-gated gear
    // while no alignment is known yet.
    private bool GearAlignmentNeedsCheck()
    {
        if (Profile.Current?.Equipment?.Sets is not { Count: > 0 } sets) return false;
        Game.Inventory.ClassEquipProfile cls =
            Game.Inventory.ItemEquipFilter.ResolveClassProfile(GameData, PlayerStats.Class);
        Game.RealmType realm = GameData.ActiveRealm;
        Game.Calculators.AlignmentBucket? bucket =
            Game.Inventory.ItemEquipFilter.GearBucketForWord(Alignment.SelfAlignment, realm);
        foreach (Models.Profile.EquipmentSet set in sets)
        foreach (Models.Profile.EquipmentSlotEntry slot in set.Slots)
        {
            if (string.IsNullOrWhiteSpace(slot.ItemName)) continue;
            if (GameData.FindRowByName("Items", slot.ItemName.Trim()) is not GameRow row) continue;
            bool Fits(Game.Calculators.AlignmentBucket? b) =>
                Game.Inventory.ItemEquipFilter.CanEquip(row, PlayerStats.Level, cls, b, realm);
            if (!Fits(null)) continue;   // unwearable for another reason
            bool alignmentGated = bucket is { } known
                ? !Fits(known)
                : !(Fits(Game.Calculators.AlignmentBucket.Good) && Fits(Game.Calculators.AlignmentBucket.Neutral)
                    && Fits(Game.Calculators.AlignmentBucket.Evil));
            if (alignmentGated) return true;
        }
        return false;
    }

    // The game refused to let us wear itemName. When it's an evil-only item with an
    // evil-point value, we're already Outlaw or worse, and nothing else about us bars
    // it, the value is what we're short of: our evil points are below it
    // (GAME_MECHANICS "Item wear restrictions (ability-code flags)").
    private void LearnFromEvilOnlyRefusal(string? itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return;
        if (GameData.FindRowByName("Items", itemName) is not GameRow row) return;
        if (Game.Inventory.ItemEquipFilter.EvilOnlyValue(row) is not { } value || value <= 0) return;
        Game.RealmType realm = GameData.ActiveRealm;
        if (Alignment.SelfEvilPoints(realm) is not { } range
            || range.Lo < Game.Calculators.EvilPointRange.OutlawFloor)
            return;
        Game.Inventory.ClassEquipProfile cls =
            Game.Inventory.ItemEquipFilter.ResolveClassProfile(GameData, PlayerStats.Class);
        Game.Calculators.AlignmentBucket? bucket =
            Game.Inventory.ItemEquipFilter.GearBucketForWord(Alignment.SelfAlignment, realm);
        if (!Game.Inventory.ItemEquipFilter.CanEquip(row, PlayerStats.Level, cls, bucket, realm)) return;
        Alignment.NoteEvilOnlyRefused(value, itemName.Trim());
    }

    // True when the item EXISTS in game data but the live character can't wear it
    // (alignment / level / class) — the Equipment Manager's block predicate. Unlike
    // CanCharacterEquipItem, an UNKNOWN item resolves false here (not a block): a
    // name that isn't in the active set's Items table just isn't a wearability
    // problem to flag — it simply never queues. Only a real restriction blocks.
    private bool IsEquipRestricted(string itemName)
    {
        if (GameData.FindRowByName("Items", itemName) is not GameRow row)
            return false;
        Game.Inventory.ClassEquipProfile cls =
            Game.Inventory.ItemEquipFilter.ResolveClassProfile(GameData, PlayerStats.Class);
        Game.RealmType realm = GameData.ActiveRealm;
        Game.Calculators.AlignmentBucket? bucket =
            Game.Inventory.ItemEquipFilter.GearBucketForWord(Alignment.SelfAlignment, realm);
        return !Game.Inventory.ItemEquipFilter.CanEquip(row, PlayerStats.Level, cls, bucket, realm,
            Alignment.SelfEvilPoints(realm));
    }

    // The auto-mode flags are read by engines on every line; deserializing the
    // General section each time was a sixth of the line-handling time. The DTO is
    // kept privately (the selector only reads it) and re-read whenever the profile's
    // General entry is replaced, which is how every write to it lands.
    private (Models.Profile.CharacterProfile? Profile, System.Text.Json.JsonElement Entry, Models.Profile.GeneralSettings Dto)? _generalForFlags;

    // Read a single boolean off the active profile's
    // Models.Profile.GeneralSettings.AutoMode. Used by
    // the engine isEnabled delegates so toggling Settings →
    // General → Auto-Combat (or the toolbar Toggle button) takes
    // effect immediately — no event subscription needed since each
    // engine queries on every tick / classifier emit.
    private bool ReadAutoModeFlag(Func<Models.Profile.AutoActionDefaults, bool> selector)
    {
        // The master switch outranks the toggles: one ticked by hand while it is
        // off stays ticked for when it comes back on, but runs nothing meanwhile.
        if (AutoModeController.KillSwitchEngaged) return false;
        Models.Profile.CharacterProfile? profile = Profile.Current;
        System.Text.Json.JsonElement entry = default;
        bool present = profile?.Settings?.TryGetValue("General", out entry) == true;
        if (_generalForFlags is not { } cached || !ReferenceEquals(cached.Profile, profile)
            || !JsonEntryIdentity.Same(cached.Entry, present ? entry : default))
        {
            cached = (profile, present ? entry : default,
                ReadSection<Models.Profile.GeneralSettings>(profile, "General"));
            _generalForFlags = cached;
        }
        return selector(cached.Dto.AutoMode);
    }

    // The pinned BBS's custom disconnect line, read by the chat and party routers on
    // every line. Each read used to load and parse bbs.json from disk, a quarter of
    // all line handling. It's kept until the file's write time changes, so an edit
    // from Settings (or from another running client) is still picked up; even the
    // write-time check costs a file-system call, so it's made at most every two
    // seconds.
    private (string Name, DateTime Written, long CheckedAt, string? Pattern)? _disconnectPattern;
    private const long DisconnectPatternRecheckMs = 2000;

    private string? ActiveDisconnectPattern()
    {
        string? name = Profile.CurrentBbsName;
        if (string.IsNullOrEmpty(name)) return ResolveActiveBbs()?.DisconnectPattern;
        long now = Environment.TickCount64;
        if (_disconnectPattern is { } recent && recent.Name == name && now - recent.CheckedAt < DisconnectPatternRecheckMs)
            return recent.Pattern;
        DateTime written = File.GetLastWriteTimeUtc(AppPaths.BbsProfileFile(name));
        if (_disconnectPattern is { } memo && memo.Name == name && memo.Written == written)
        {
            _disconnectPattern = memo with { CheckedAt = now };
            return memo.Pattern;
        }
        string? pattern = ResolveActiveBbs()?.DisconnectPattern;
        _disconnectPattern = (name, written, now, pattern);
        return pattern;
    }

    // @status / @path movement snapshot with the specific pause reason attached.
    // Capture() flags THAT the walker is paused; the plain-English WHY (resting,
    // meditating, held, party wait, a manual pause) lives in the MovementCoordinator
    // gates, which are a ViewModel-tier concern the Game.Remote snapshot mustn't reach
    // into — so we resolve it here (via NavActivity, the same mapping the Navigation top
    // bar uses) and fold it onto the snapshot. Only meaningful when Paused; otherwise
    // the snapshot passes through unchanged.
    private Game.Remote.MovementStatus ReadMovementStatus()
    {
        Game.Remote.MovementStatus mv =
            Game.Remote.MovementStatus.Capture(Walker, LoopRunner, AutoLair);
        if (!mv.Paused)
            return mv;

        (string text, ViewModels.Navigation.NavActivityKind kind) =
            ViewModels.Navigation.NavActivity.Describe(
                MovementCoordinator.AssertedGates,
                MovementCoordinator.IsPaused,
                Conditions.IsMovementPrevented);

        // Map the top-bar activity to the reason word the reply folds in. A manual
        // user pause is "paused"; mid-fight is "fighting"; every recovery / hold beat
        // carries its own detail after "Waiting — " (resting (low HP), meditating
        // (low mana), held, party asked to wait, …). Moving/None leave it null so the
        // phrase falls back to a bare "paused".
        string? reason = kind switch
        {
            ViewModels.Navigation.NavActivityKind.Paused => "paused",
            ViewModels.Navigation.NavActivityKind.Fighting => "fighting",
            ViewModels.Navigation.NavActivityKind.Waiting =>
                ViewModels.Navigation.NavActivity.HoldSuffix(text, kind),
            _ => null,
        };

        return mv with { PauseReason = reason };
    }

    // Live read of the master auto-combat toggle — the same GeneralSettings
    // AutoMode flag the combat engine gates on. The navigation walk-to ETA
    // reads it to decide whether to fold lair-fight dwell into the arrival
    // estimate (auto-combat off ⇒ the walker doesn't stop to fight, so the
    // route is pure travel time).
    public bool IsAutoCombatEnabled => ReadAutoModeFlag(d => d.AutoCombat);

    // True when the running loop suppresses combat in the room we're standing
    // in — a per-waypoint "do not attack here" or the loop-wide "only attack in
    // lair rooms" (non-lair room). The three engage-gate delegates AND it into
    // their effective auto-combat read so combat treats the room as if the
    // master toggle were off (skip + walk on). Because HealthManager's rest-clear
    // arms exactly when it sees auto-combat off, a triggered rest in a suppressed
    // room automatically fires ForceClearForRest → CombatManager's rest-clear
    // override fights to clear it (the same rest exception that lets a rest clear
    // its room with Auto-Combat off). Loops only; the raw toggle
    // (IsAutoCombatEnabled, toolbar/Settings display) is
    // left untouched so it still shows the user's real ON/OFF.
    private bool CombatSuppressedInCurrentRoom()
    {
        (Game.Map.RoomKey? evalKey, bool suppressed, _) = CombatSuppressionVerdict();

        // Edge-trigger a Combat-log line on transition — the three gate Funcs
        // each call this per observation, so log only when (room, suppressed)
        // actually changes to avoid per-line spam. Explains a "loop walked past
        // hostiles" in the program log.
        if (suppressed != _lastCombatSuppressed || !Equals(evalKey, _lastCombatSuppressedRoom))
        {
            _lastCombatSuppressed = suppressed;
            _lastCombatSuppressedRoom = evalKey;
            if (suppressed && evalKey is { } rk)
                Log.Combat("Combat", $"combat suppressed in {rk} — loop 'do not attack' / 'only lair rooms'");
        }
        // A fight with a player is the fight: the engine's own pick would replace
        // the attack on them.
        return suppressed || PvpFight is { IsActive: true } || PvpLeaveRoomReason() is not null;
    }

    // The combat profile's attack, aimed at a player: its normal attack spell, else
    // its alternate, each while our mana meets its floor, else the normal attack
    // command. Anything usable on a monster is usable on a player except a spell
    // that only takes monsters (GAME_MECHANICS "Damage lines"); a room spell can't
    // be aimed at all.
    private string PvpAttackCommandFor(string given)
    {
        Models.Profile.CombatSettings combat =
            ReadSection<Models.Profile.CombatSettings>(Profile.Current, "Combat");
        foreach (Models.Profile.CombatSpellSlot slot in new[] { combat.NormalAttackSpell, combat.AlternateAttackSpell })
        {
            string? spell = slot.SpellName?.Trim();
            if (string.IsNullOrEmpty(spell) || Spellbook.FindByCastCode(spell) is not { } known) continue;
            if (known.Targets == MonsterOnlyTarget || Game.Combat.DebuffTargeting.IsAreaEnemy(known.Targets)) continue;
            if (!PvpManaMeets(slot.MinManaPerCast)) continue;
            return $"{spell} {given}";
        }
        string verb = string.IsNullOrWhiteSpace(combat.NormalAttackCommand) ? "a" : combat.NormalAttackCommand.Trim();
        return $"{verb} {given}";
    }

    // Spells.Targets 4: a monster and nothing else (the charm family).
    private const int MonsterOnlyTarget = 4;

    private bool PvpManaMeets(int minManaPerCast) =>
        Game.Combat.CombatSpellChooser.ManaMeetsReserve(
            minManaPerCast, PlayerState.Ma, PlayerState.MaxMa,
            ReadSection<Models.Profile.CombatSettings>(Profile.Current, "Combat").SpellManaThresholdMode);

    // What a PvP spell slot's code is: between-round or a combat spell, how long it
    // lasts at our level, and whether it takes a player at all.
    private Game.Pvp.PvpSpellInfo? PvpSpellInfoFor(string code)
    {
        if (Spellbook.FindByCastCode(code) is not { } spell) return null;
        long rounds = Game.Spells.SpellCalculator.Duration(spell.Formula, PlayerStats.Level);
        return new Game.Pvp.PvpSpellInfo(
            BetweenRound: spell.Formula.EnergyCost == 0,
            Duration: TimeSpan.FromSeconds(Math.Max(0, rounds) * Game.Spells.SpellCalculator.SpellRoundSecondsWallClock),
            MonsterOnly: spell.Targets == MonsterOnlyTarget);
    }

    // The ways out of this room a chase may take: an open way, or a door that just
    // opens. A door that needs a key, picking or strength is left out, and so is
    // any exit that takes a command, a search or a trap disarm.
    private IReadOnlyCollection<Game.Pvp.PvpChaseExit> PvpChaseExits()
    {
        List<Game.Pvp.PvpChaseExit> ways = new();
        if (RoomTracker.State.CurrentRoom is not { } here) return ways;
        foreach ((Game.Map.Direction way, Game.Map.RoomExit exit) in here.Exits)
        {
            if (!Game.Map.DirectionExtensions.IsCardinal(way)) continue;
            if (exit.Hint == Game.Map.RoomExitHint.None) ways.Add(new Game.Pvp.PvpChaseExit(way, Door: false));
            else if (exit is { Hint: Game.Map.RoomExitHint.Door, StatRequirement: 0 })
                ways.Add(new Game.Pvp.PvpChaseExit(way, Door: true));
        }
        return ways;
    }

    // What the fight with a player stopped and puts back at its end, for telling
    // when we are back at it, and for a change of game data to drop. Set where the
    // fight is built, ahead of anything that could suspend.
    private Game.Map.HeldDetourResume _pvpFightHeld = null!;

    // One step after a player who left, walked through the walker so a door on the
    // way is handled like any other.
    private bool PvpStepToward(Game.Map.Direction direction)
    {
        if (RoomTracker.State.CurrentRoom is not { } here) return false;
        if (!here.Exits.TryGetValue(direction, out Game.Map.RoomExit exit)) return false;
        return Walker.WalkTo(exit.Target);
    }

    // Why a running walk or loop carries on out of the room we're in rather than
    // fight beside another player's room attack, or null. Moving by hand there is
    // no next room to carry on to, so the fight is left to the user.
    public string? PvpLeaveRoomReason() =>
        Recovery.AttachedEngine is null ? null : PvpRoom.LeaveRoomReason();
    private bool _lastCombatSuppressed;
    private Game.Map.RoomKey? _lastCombatSuppressedRoom;

    // The loop combat-suppression decision the engage gates act on: the room it was
    // judged against, whether combat is suppressed there, and whether that room is the
    // one an in-flight loop move is entering (vs the tracker's current room). Shared
    // with the bug report so the capture shows exactly what the engine decided.
    public (Game.Map.RoomKey? Room, bool Suppressed, bool EnteringRoom) CombatSuppressionVerdict()
    {
        // Which room to judge suppression against. Normally the room we're standing in.
        // BUT during a loop move the RoomTracker is still Pending on the room we're
        // LEAVING (RoomConfidence.Pending: CurrentRoom lags until the next observation
        // confirms the landing), while the monsters that just arrived belong to the room
        // we're ENTERING. Keying off CurrentRoom there decides suppression for the wrong
        // room, so a 'do not attack' / non-lair room leaks one attack on the entry pass
        // — before the room confirms and suppression flips (report paradigm-20260915-122832,
        // whether the flag was live-edited or configured before the run). So while a loop
        // move is in flight — running, or paused mid-move by the very fight it walked
        // into — judge against the loop's expected target room instead.
        Game.Map.RoomKey? evalKey;
        bool evalIsLair;
        bool entering = false;
        if (Game.Map.LoopCombatSuppression.JudgeEnteringRoom(
                LoopRunner.State, LoopRunner.IsStepInFlight,
                RoomTracker.State.Confidence == Game.Map.RoomConfidence.Pending,
                LoopRunner.ExpectedMoveTarget is not null)
            && LoopRunner.ExpectedMoveTarget is { } target)
        {
            evalKey = target;
            evalIsLair = RoomGraph.GetRoom(target)?.HasLair ?? false;
            entering = true;
        }
        else if (RoomTracker.State.CurrentRoom is { } here)
        {
            evalKey = here.Key;
            evalIsLair = here.HasLair;
        }
        else
        {
            evalKey = null;
            evalIsLair = false;
        }

        bool heldByUser = MovementCoordinator.IsGateAsserted(Game.Map.MovementCoordinator.UserGate);
        bool following = MovementCoordinator.IsGateAsserted(Game.Map.MovementCoordinator.FollowerGate);

        // A bank or sell trip stops the loop it will resume, and may walk out and back
        // through that loop's rooms: the loop's own room settings still apply there.
        if (LoopRunner.State == Game.Map.LoopState.Idle
            && Game.Map.LoopCombatSuppression.DetourLoop(
                AutoDeposit.IsRerouting, AutoDeposit.ResumePlan,
                SellDetour.IsDetouring, SellDetour.ResumePlan) is { } detourLoop)
        {
            // The trip's walker has the same lag the loop's moves do: judge the room
            // its move in flight is entering.
            if (RoomTracker.State.Confidence == Game.Map.RoomConfidence.Pending
                && Walker.InFlightMoveTarget is { } walkTarget)
            {
                evalKey = walkTarget;
                evalIsLair = RoomGraph.GetRoom(walkTarget)?.HasLair ?? false;
                entering = true;
            }
            // Lair-only belongs to the loop's own rooms: the rest of the way to the
            // bank isn't a non-lair room of the loop.
            bool detourSuppressed = !heldByUser && !following
                && evalKey is { } room
                && DetourCombat.InLoopArea(detourLoop, room)
                && Game.Map.LoopCombatSuppression.IsSuppressed(detourLoop, room, evalIsLair);
            return (evalKey, detourSuppressed, entering);
        }

        bool suppressed =
            Game.Map.LoopCombatSuppression.LoopIsDriving(
                LoopRunner.State,
                heldByUser: heldByUser,
                following: following,
                standingForMana: LairDebuffHold is { WaitingForMana: true })
            && LoopRunner.CurrentLoop is { } loop
            && evalKey is { } key
            && Game.Map.LoopCombatSuppression.IsSuppressed(loop, key, evalIsLair);
        return (evalKey, suppressed, entering);
    }

    // Per-monster overlay resolve: seed-store value forms the Defaults tier,
    // SettingsResolver overlays Global / BBS / Char-tier user overrides on top.
    // The single copy every consumer shares — the combat manager, the
    // combat-state tracker, the monster-relationship watcher, the loop
    // simulation and the lair-fight ETA predicate — so the engageable decision
    // can't diverge on the same room state.
    private Models.GameData.MonsterOverlay ResolveMonsterOverlay(int number) =>
        Resolver.ResolveGameData<Models.GameData.MonsterOverlay>(
            "Monsters",
            number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            MonsterOverlaySeed.GetOverlay(number));

    // Seconds per room the character walks right now, with lagSeconds of lag on
    // Paradigm: the server move timer from live encumbrance and gear quickness there;
    // on Stock, Auto-Lair's live travel model — the user's hop times by encumbrance
    // (lag already in them) or their flat pace.
    public double LoopSimulationWalkSeconds(double lagSeconds) =>
        (GameData.ActiveRealm == Game.RealmType.ParaMud
            ? new Game.Map.ParadigmMovementCostModel(() => Inventory.Snapshot, GameData, lagSeconds)
            : AutoLair.TravelCostModel).EstimateTravel(1).TotalSeconds;

    public Game.Simulation.SimulationSource LoopSimulationSource =>
        _loopSimulationSource ??= new(BuildLoopSimulation, () => PlayerStats.Name, () => PlayerStats.Level,
            LoopSimulationWalkSeconds, () => RoomTracker.State.CurrentRoom?.Key, AppPaths.LogsDir,
            () => SimulatorWorld(forSaving: false), () => SimulatorWorld(forSaving: true), SimulatorCharacterAt, SimulatorLoad);
    private Game.Simulation.SimulationSource? _loopSimulationSource;

    // The game data this client is running on, for a ranking kept between sessions
    // (RankingWorld): the table files as they were when read (RankingWorldWatch) and
    // the monster seed as loaded, not what is on disk now. Another client can import
    // the set or an update replace the seed while this one runs; a ranking made here
    // is still a ranking of the old data.
    //
    // forSaving: it is to stamp a ranking just made, so a table not read yet is
    // read now and one that can't be read leaves no world (RankingWorldWatch);
    // otherwise it is to compare a saved ranking with, and nothing is read for it.
    //
    // Null with no set active, and when the watch finds the tables in memory are not
    // of one version of the set, or a table could not be read: then no ranking is
    // saved or read back until the set is read in again, said once for each load and
    // reason. A ranking is still made and listed for the session.
    private Game.Simulation.RankingWorld? SimulatorWorld(bool forSaving)
    {
        if (GameData.ActiveSet is not { } set) return null;
        GameDataCache.Load load = GameData.ActiveLoad;
        string seed = MonsterOverlaySeed.ContentStamp;
        string? why;
        Game.Simulation.RankingWorld? world = forSaving
            ? _simulatorWorld.NowForSaving(load, seed, out why)
            : _simulatorWorld.Now(load, seed, out why);
        if (world is not null) return world;
        if (_simulatorWorldProblemSaid != (load, why))
        {
            _simulatorWorldProblemSaid = (load, why);
            Log.Info("Simulator",
                $"the tables of '{set}' in memory are not one version of it ({why}): area rankings are not saved or " +
                "read back until the set is read in again (restart, or import it here)");
        }
        return null;
    }

    // The load and reason the line above was last logged for.
    private (GameDataCache.Load Load, string? Why) _simulatorWorldProblemSaid = (GameDataCache.Load.None, null);

    // Set where the Simulator's rankings are wired, before any window can ask.
    private Game.Simulation.RankingWorldWatch _simulatorWorld = null!;

    // Why no ranking is being saved for the active set as loaded, for the bug
    // report; null when they are.
    public string? SimulatorWorldUnfit => _simulatorWorld.WhyUnfit;

    // Which load of the active set is in hand, for a ranking to tell whether the one
    // it finishes in is the one it started in.
    private object? SimulatorLoad() => GameData.ActiveLoad;

    // The character as a ranking at `level` would simulate it now, as the groups a
    // kept ranking is compared by (RankingCharacter). Null until the game has shown
    // both a `stat` screen and a full inventory on the latest connection. Before
    // that the stats are the ones the profile remembers from an earlier session and
    // the worn gear and gate items carried are unknown or as they were before the
    // link dropped: a ranking checked against those would be told it is current, or
    // changed, on figures nobody has read.
    private Game.Simulation.RankingCharacter? SimulatorCharacterAt(int level)
    {
        if (!_simulatorStatRead || !_simulatorInventoryRead || !Inventory.IsLoaded
            || GameData.ActiveSet is not { } set || BuildLoopSimulation(level) is not { } built)
            return null;
        // The record overrides that apply to this character: Global, its realm's,
        // its own.
        var tiers = new List<string> { Path.Combine(AppPaths.DataRoot, "Global") };
        if (ActiveRealmFolder() is { } realm) tiers.Add(realm);
        if (Profile.CurrentProfileName is { } name && Profile.CurrentBbsName is { } bbs)
            tiers.Add(AppPaths.ProfileFolder(bbs, name));
        _simulatorGateItems ??= Game.Simulation.RankingReach.GateItems(RoomGraph.Rooms);
        return Game.Simulation.RankingCharacter.Of(
            built.Character,
            _rankingStamps.GameDataEdits(tiers, set),
            Game.Simulation.RankingReach.Hash(
                Movement.Avoided, RoomBlacklist.Blacklisted,
                Movement.ClassNumberProvider?.Invoke(), Movement.RaceNumberProvider?.Invoke(),
                _simulatorGateItems.Where(IsItemCarried)));
    }

    // Every item an exit of the loaded map wants carried; rebuilt after the map is.
    private IReadOnlySet<int>? _simulatorGateItems;

    // A `stat` screen (one that gave the hit points) and a full inventory have been
    // read since the latest connection began, for the loaded profile. They stand
    // after the link drops, since they are still the last the game said, and are
    // forgotten when the next connection begins (MainWindowViewModel's Connected
    // handler) or another profile loads: what is true by then has to be read again.
    private bool _simulatorStatRead, _simulatorInventoryRead;

    public void ForgetSimulatorCharacterReads() => _simulatorStatRead = _simulatorInventoryRead = false;

    // For the bug report: why a kept ranking says "not checked against the character yet".
    public string SimulatorCharacterReads =>
        $"`stat` {(_simulatorStatRead ? "read" : "not read")}, inventory {(_simulatorInventoryRead && Inventory.IsLoaded ? "read" : "not read")}";

    // The player's live defence against a monster's swing, from the live loadout by the
    // shared IncomingHitEstimator — what route Details colours monsters by and the bug
    // report prints. Null before a `stat` capture.
    public Game.Calculators.PlayerDefenseProfile? LiveIncomingHitDefense()
    {
        Game.PlayerStats stats = PlayerStats;
        if (stats is null || stats.Level <= 0) return null;
        Game.Inventory.InventorySnapshot snapshot = Inventory.Snapshot;
        Models.Profile.CharacterProfile? profile = Profile.Current;
        int? classId = Game.Quests.CompletedQuestBonuses.ResolveClassId(GameData, stats.Class);
        IReadOnlyList<Game.Quests.QuestBonus> quests =
            Game.Quests.CompletedQuestBonuses.Resolve(GameData, classId, profile?.QuestLog);
        return Game.Calculators.IncomingHitEstimator.BuildLiveDefense(
            stats, snapshot.EquippedItems, snapshot.Encumbrance, GameData,
            profile?.PartyBuffs, Spellbook?.Available, quests);
    }

    // The live character and game data a loop simulation plays, read the same way the
    // combat and casting engines read them (the Combat / Health / Spells sections, the
    // shared monster-overlay resolve, worn gear, obtained spells, the Default-gear rest
    // basis) — moved to atLevel when one is given (SimLevelProjection). Null before a
    // `stat` screen has told us the character's level and pools.
    private (Game.Simulation.SimCharacter Character, Game.Simulation.SimWorld World)? BuildLoopSimulation(int? atLevel = null)
    {
        if (PlayerStats.Level <= 0 || PlayerStats.MaxHits <= 0) return null;
        int level = atLevel is > 0 ? atLevel.Value : PlayerStats.Level;
        Game.PlayerStats stats = level == PlayerStats.Level
            ? PlayerStats : Game.Simulation.SimLevelProjection.StatsAt(PlayerStats, level, GameData);
        Game.Inventory.InventorySnapshot inv = Inventory.Snapshot;
        IReadOnlyList<Game.Spells.KnownSpell> obtained = Game.Simulation.SimLevelProjection.SpellsAt(
            Spellbook.Available.Where(k => Spellbook.IsObtained(k.Number)).ToList(), level);
        string? weapon = inv.EquippedItems.FirstOrDefault(w => w.Slot == "Weapon Hand").Name;
        Models.Profile.CharacterProfile? profile = Profile.Current;
        IReadOnlyList<Game.Quests.QuestBonus> quests = Game.Quests.CompletedQuestBonuses.Resolve(
            GameData, Game.Quests.CompletedQuestBonuses.ResolveClassId(GameData, PlayerStats.Class), profile?.QuestLog);

        // The Default-gear rest basis is today's `stat`; at another level it moves by the
        // same amount the projection moved max HP / mana, so thresholds keep their meaning.
        int basisHp = DefaultBasisMaxHp(), basisMa = DefaultBasisMaxMa();
        if (!ReferenceEquals(stats, PlayerStats))
        {
            if (basisHp > 0) basisHp = Math.Max(1, basisHp + stats.MaxHits - PlayerStats.MaxHits);
            if (basisMa > 0) basisMa = Math.Max(0, basisMa + stats.MaxMana - PlayerStats.MaxMana);
        }

        // The sneak opener, when the character opens with one: Backstab on, Auto-Sneak on
        // and some Stealth, with the Backstab set's weapon in hand as the equipment
        // manager wields it for the stab (Monster Intel's backstab line reads the same).
        Models.Profile.CombatSettings combat = ReadSection<Models.Profile.CombatSettings>(profile, "Combat");
        Game.Calculators.PlayerMatchupProfile? backstab = null;
        int backstabMagic = 0;
        if (combat.DoBackstab && ReadAutoModeFlag(d => d.AutoSneak) && stats.Stealth > 0
            && Game.Calculators.CharacterCalculator.UsableMeleeAttacks(stats, GameData).Contains(Game.Calculators.MudAttackType.Backstab))
        {
            IReadOnlyList<Game.Inventory.EquippedItem> bsWorn = Game.Inventory.EquippedItem.WithWeapon(
                inv.EquippedItems, profile?.Equipment?.BackstabSetWeapon());
            backstab = Game.Calculators.CharacterCalculator.BuildMeleeAttackProfile(
                Game.Calculators.MudAttackType.Backstab, stats, bsWorn, inv.Encumbrance, GameData);
            string? bsWeapon = bsWorn.FirstOrDefault(w => w.Slot == Game.Inventory.EquippedItem.WeaponHand).Name;
            backstabMagic = string.IsNullOrEmpty(bsWeapon) ? 0 : ItemMagic.HitMagic(bsWeapon);
        }

        Game.Simulation.SimCharacter character = Game.Simulation.SimCharacterBuilder.Build(
            stats, inv.EquippedItems, inv.Encumbrance, obtained, GameData,
            string.IsNullOrEmpty(weapon) ? 0 : ItemMagic.HitMagic(weapon),
            combat,
            ReadSection<Models.Profile.HealthSettings>(profile, "Health"),
            ReadSection<Models.Profile.SpellsSettings>(profile, "Spells"),
            profile?.PartyBuffs, quests, ResolveMonsterOverlay, SpellShort.ShortByNumber,
            Game.Simulation.SimCharacterBuilder.AlignmentValue(Alignment.EvilPoints, Alignment.SelfAlignment),
            basisHp, basisMa) with
        {
            HangupsDisabled = ReadSection<Models.Profile.GeneralSettings>(profile, "General").DisableHangups,
            Backstab = backstab,
            BackstabHitMagic = backstabMagic,
        };
        var world = new Game.Simulation.SimWorld(
            MonsterCatalog.Get, MonsterMagic, SpellReqLevel, MonsterResist, SpellAttackType, SpellTargetType, MonsterLife,
            ExpResolver.DeathSummonsOf,
            n => SpellCatalog.GetFormulaByNumber(n) is { } f ? Game.Simulation.SimProc.From(f, GameData.ActiveRealm) : null);
        return (character, world);
    }

    // Whether the walker would actually FIGHT a lair room's occupants — the gate
    // RouteEtaEstimator uses so an ETA only charges combat dwell for lairs it'll
    // stop and clear. Resolves each lair monster through the same tier merge combat
    // uses and asks MonsterEngagement, so a friendly-guardsman / passive-neutral
    // "lair" (common on town routes) is a free walk-through. An unparseable tag falls
    // back to "will fight" so a real lair is never under-counted. Shared by every ETA
    // surface (walk-status line, route-picker cards, Details title) so they agree.
    public bool LairWillBeFought(Game.Map.Room room)
    {
        if (string.IsNullOrEmpty(room.RawLairTag)) return false;
        Game.Map.RoomTooltipBuilder.ParseLairTag(room.RawLairTag, out _, out IReadOnlyList<int> monsterIds);
        if (monsterIds.Count == 0) return true;
        foreach (int id in monsterIds)
            if (Game.Combat.MonsterEngagement.IsEngageable(ResolveMonsterOverlay(id))) return true;
        return false;
    }

    // Where a room roster was read, for a log line. A room display prints its
    // "Also here:" line ahead of the exits line that confirms the move, so with a
    // move in flight the tracked room is still the one we were leaving, and the
    // roster may belong to either.
    private string DescribeRosterRoom()
    {
        if (RoomTracker.State.CurrentRoom is not { } room) return "in an unknown room";
        string named = $"{room.Name} ({room.Key})";
        return RoomTracker.State.Confidence == Game.Map.RoomConfidence.Pending
            ? $"at {named} or the room one move on (a move was not yet confirmed)"
            : $"in {named}";
    }

    // Says in the program log what the realm's hang-up penalty (Settings → BBS + Display)
    // makes of a hang-up the client has just sent: the health settings', the PvP
    // response's, a Hangup-relationship monster's, or an @panic / @hangup /
    // @relog. Which side applies comes from what is already tracked, a fight
    // with a player (PvpFight) and PlayerState.InCombat; pvpResponse is the one
    // thing only the caller knows.
    // A record for the reader. Nothing is decided on it.
    private void LogHangupPenalty(bool pvpResponse)
    {
        if (Game.Health.HangupPenaltyNotice.ForHangup(
                ResolveActiveRealm()?.Realm,
                pvp: pvpResponse || PvpFight.IsActive,
                inCombat: PlayerState.InCombat) is { } line)
            Log.Info(Game.Health.HangupPenaltyNotice.LogCategory, line);
    }

    // How long after a player's attack a dropped link still counts as a hang-up in
    // a fight with a player, for the list HangupItemRecheck writes. A client-side
    // window: what the board itself counts as PvP combat isn't known to it.
    private static readonly TimeSpan HangupPvpAttackWindow = TimeSpan.FromSeconds(30);

    // Live read of Sprint Mode from the char-tier General section — the same
    // store the toolbar toggle writes. Wired into HealthManager's rest-skip
    // selector (see the SetDoNotRestSelector call above) so flipping the
    // toggle takes effect without restarting an engine.
    private bool ReadSprintMode() =>
        ReadSection<Models.Profile.GeneralSettings>(Profile.Current, "General").SprintMode;

    // Item-cast recast clock: resolve a Bless-slot
    // Game.Spells.ItemCastToken to the cast item's spell effect
    // duration in seconds (Game.Spells.SpellCalculator.Duration
    // rounds × Game.Spells.SpellCalculator.SpellRoundSecondsWallClock
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

    // The buffs a draw item (a deck of cards) can deal, each with its length in
    // seconds at the item's use level; null for any other token.
    private IReadOnlyList<(int SpellNumber, string Name, long DurationSec)>? ItemDrawOutcomesOf(string token)
    {
        if (!Game.Spells.ItemCastToken.TryResolve(token, Spellbook.GetCastItems(),
                out Game.Spells.ClassCastItem item) || item.Outcomes is not { Count: > 0 } outcomes)
            return null;
        return outcomes
            .Select(o => (o.SpellNumber, o.Name,
                (long)System.Math.Round(o.DurationRounds * Game.Spells.SpellCalculator.SpellRoundSecondsWallClock)))
            .ToList();
    }

    // Whether using the draw item named by token again replaces the card that is up
    // (ClassCastItem.CanRedraw). True for anything that doesn't resolve.
    private bool ItemDrawCanRedraw(string token)
        => !Game.Spells.ItemCastToken.TryResolve(token, Spellbook.GetCastItems(),
               out Game.Spells.ClassCastItem item) || item.CanRedraw;

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

    // The DEFAULT gear set's max HP / mana for the Settings rest-preview conversions
    // — the same basis the rest engine anchors to — so the displayed "= N/M" figures
    // stay put while a Pre-rest set that alters the pool is worn. Falls back to the
    // live pool max before a stat screen / when none of the Default set's items is
    // owned; FromDefaultSet says which basis it is, for the "(def)" / "(live)" marker.
    public (int Max, bool FromDefaultSet) RestPreviewMaxHp()
        => DefaultBasisMaxHp() is int v and > 0 ? (v, true) : (PlayerState.MaxHp, false);
    public (int Max, bool FromDefaultSet) RestPreviewMaxMa()
        => DefaultBasisMaxMa() is int v and > 0 ? (v, true) : (PlayerState.MaxMa, false);

    // The Default-gear max the rest engine resolves against: the recorded baseline
    // (a `stat` read with the Default set on — see DefaultPoolBaselineKeeper), kept
    // even while stale until a fresh one lands; before the first one, estimated from
    // the live max and the gear bonuses (DefaultSetMaxPool).
    private int DefaultBasisMaxHp()
        => PoolBaseline.Current is { MaxHp: > 0 } b ? b.MaxHp
            : DefaultSetMaxPool(static t => t.PlusMaxHp, PlayerState.MaxHp);
    private int DefaultBasisMaxMa()
        => PoolBaseline.Current is { MaxMa: > 0 } b ? b.MaxMa
            : DefaultSetMaxPool(static t => t.PlusMaxMana, PlayerState.MaxMa);

    // The Default set's summed +MaxHP / +MaxMana item bonus (owned items only, like
    // DefaultSetMaxPool), or null when no Default set is configured.
    private (int Hp, int Ma)? DefaultGearPoolBonus()
    {
        IReadOnlyList<Game.Inventory.EquippedItem> items = DefaultSetEquippedItems();
        if (items.Count == 0) return null;
        Game.Calculators.EquipmentStatSummary t = Game.Calculators.CharacterCalculator
            .AggregateEquipmentStats(items, GameData).Totals;
        return (t.PlusMaxHp, t.PlusMaxMana);
    }

    // The Default set is on: every Default item we own is worn, and the worn gear's
    // max-pool bonus matches the Default set's (so nothing else worn shifts the maxima).
    private bool DefaultSetWorn()
    {
        IReadOnlyList<Game.Inventory.EquippedItem> items = DefaultSetEquippedItems();
        if (items.Count == 0) return false;
        List<string> worn = Inventory.Snapshot.EquippedItems.Select(w => w.Name.Trim()).ToList();
        foreach (Game.Inventory.EquippedItem item in items)
        {
            int i = worn.FindIndex(n => string.Equals(n, item.Name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return false;
            worn.RemoveAt(i);
        }
        Game.Calculators.EquipmentStatSummary w = Game.Calculators.CharacterCalculator
            .AggregateEquipmentStats(Inventory.Snapshot.EquippedItems, GameData).Totals;
        return DefaultGearPoolBonus() is { } d && d.Hp == w.PlusMaxHp && d.Ma == w.PlusMaxMana;
    }

    // The max HP or mana the DEFAULT gear set would give (selector picks the pool
    // from an equipment-stat summary). Re-bases the LIVE gear-aware pool max off the
    // CURRENTLY-worn flat pool bonus onto the DEFAULT set's, so the rest engine anchors
    // to the loadout the user's rest %s are tuned for regardless of any Pre-rest set
    // swapped in. It MUST use the live max (PlayerState.MaxHp/MaxMa, kept in step with
    // worn gear by EquipmentMaxPoolSync), NOT the stat-screen max: the live max minus
    // the currently-worn bonus is the gear-independent bare base, so `live - worn + def`
    // stays fixed across a swap. The stat screen stays frozen at whatever gear was worn
    // when the last stat check landed, so subtracting the LIVE worn bonus from it
    // double-counts a swap — a Pre-rest MANA set (which ADDS mana) then drove the basis
    // DOWN, dragging the rest target below the rest trigger and flapping the mana gate
    // every room, thrashing meditate↔move↔gear-swap (report paradigm-20260909-095419).
    // Returns 0 (→ HealthManager falls back to its own real / live max) before a pool
    // max is known or when no Default set is configured.
    private int DefaultSetMaxPool(Func<Game.Calculators.EquipmentStatSummary, int> pool, int liveMax)
    {
        if (liveMax <= 0) return 0;
        IReadOnlyList<Game.Inventory.EquippedItem> defaultItems = DefaultSetEquippedItems();
        if (defaultItems.Count == 0) return 0;
        int worn = pool(Game.Calculators.CharacterCalculator
            .AggregateEquipmentStats(Inventory.Snapshot.EquippedItems, GameData).Totals);
        int def = pool(Game.Calculators.CharacterCalculator
            .AggregateEquipmentStats(defaultItems, GameData).Totals);
        return Math.Max(1, liveMax - worn + def);
    }

    // Whether the gear set the engine last equipped is a pre-rest swap set (HP / Mana)
    // — used to detect a stranded pre-rest loadout after a same-tick rest completion.
    private bool CurrentEquippedIsPreRestSet()
    {
        if (Equipment.CurrentSetId is not { } id) return false;
        return Profile.Current?.Equipment?.Sets.FirstOrDefault(s => s.Id == id)
            is { Trigger: Models.Profile.EquipTriggerType.PreRestHp
                       or Models.Profile.EquipTriggerType.PreRestMana };
    }

    // Whether the trip under way is a walk-to that ends at a boss room other than the
    // one just left. Walk-to only (user, 2026-10-03): a loop or an Auto-Lair run
    // reverts between bosses as before, even when it passes through another one. With
    // no engine running, a party follower asks its leader (the answer arrives later
    // through LeaderBossTravel.Resolved).
    private Game.Inventory.BossTravel BossTravelNow(Game.Map.RoomKey? leftBossRoom)
    {
        if (MovementControl is { } control && control.State != Game.Map.MovementEngineState.Idle)
        {
            // A loop's approach leg and an Auto-Lair run both drive the walker too.
            bool plainWalk = !AutoLair.IsActive && LoopRunner.CurrentLoop is null;
            bool heading = plainWalk && Walker.Destination is { } dest
                && dest != leftBossRoom && IsBossRoomLive(dest);
            return heading ? Game.Inventory.BossTravel.Yes : Game.Inventory.BossTravel.No;
        }
        return LeaderBossTravel is { } probe && probe.Ask()
            ? Game.Inventory.BossTravel.Asking
            : Game.Inventory.BossTravel.No;
    }

    // Whether the leader's @path reply is a walk-to ending at a boss room other than
    // the one it's standing in. A leader on a loop or an Auto-Lair run reports no
    // destination, so that's a no, as it is for our own.
    private bool LeaderPathHeadsToBoss(Game.Remote.PathReport report) =>
        report.Destination is { } dest && dest != report.LeaderRoom && IsBossRoomLive(dest);

    // Whether RoomKey is a boss room on the active realm — any room listed for any
    // boss in the Bosses table (all rooms, not just the StopBefore subset). Resolved
    // live so a realm swap or Bosses-tab edit takes effect without re-wiring; the
    // Bossing gear set consults it, called at most once per room change / combat entry.
    private bool IsBossRoomLive(Game.Map.RoomKey key)
    {
        foreach (Models.Profile.BossDef b in Bosses.ResolveForRealm(GameData.ActiveRealm))
            foreach (string wire in b.Rooms)
                if (Game.Map.RoomKey.TryParseWire(wire, out Game.Map.RoomKey k) && k == key)
                    return true;
        return false;
    }

    public Game.Map.LairEntryDebuffHold LairDebuffHold { get; private set; } = null!;

    // Every loop some engine holds on to outside the loop catalogue: the runner's
    // (running, last run, staged, to restart after a reconnect), the one waiting on
    // its walk-to, and the one a running event goes back to.
    private IEnumerable<Game.Map.Loop> LoopsHeldApartFromTheCatalogue()
    {
        foreach (Game.Map.Loop held in LoopRunner.HeldLoops) yield return held;
        if (LoopHandoff.Pending is { } waitingOnItsWalk) yield return waitingOnItsWalk;
        if (Events.ResumeLoop is { } eventGoesBackTo) yield return eventGoesBackTo;
    }

    // How the running loop wants its next step taken with respect to the debuff:
    // the loop's own setting when that step enters a lair we would fight in and the
    // combat profile has a debuff to cast on entry, else Off (nothing to wait for).
    // lair is the room that step enters.
    private Game.Map.LairEntryDebuffMode LairEntryDebuffModeForNextStep(out Game.Map.RoomKey? lair)
    {
        lair = null;
        if (LoopRunner.CurrentLoop is not { LairEntryDebuff: not Game.Map.LairEntryDebuffMode.Off } loop)
            return Game.Map.LairEntryDebuffMode.Off;
        if (!IsAutoCombatEnabled) return Game.Map.LairEntryDebuffMode.Off;
        if (NextPlannedRoomForEquip(LoopRunner.PeekNextPlannedDirection()) is not { } next
            || RoomGraph.GetRoom(next) is not { HasLair: true })
            return Game.Map.LairEntryDebuffMode.Off;
        lair = next;
        // A lair the loop is told not to fight in gets no debuff either.
        if (Game.Map.LoopCombatSuppression.IsSuppressed(loop, next, currentIsLair: true))
            return Game.Map.LairEntryDebuffMode.Off;

        Models.Profile.CombatSettings combat =
            ReadSection<Models.Profile.CombatSettings>(Profile.Current, "Combat");
        bool debuffConfigured = !string.IsNullOrWhiteSpace(combat.AreaDebuffSpell.SpellName)
                                || !string.IsNullOrWhiteSpace(combat.SingleTargetDebuffSpell.SpellName);
        return debuffConfigured ? loop.LairEntryDebuff : Game.Map.LairEntryDebuffMode.Off;
    }

    // Asked by a lair wait that already stands: is the loop still about to enter the
    // lair it waits outside? No once the loop is gone, its option or Auto-Combat (the
    // master switch with it) is off, or its next step is known and is no longer into
    // a lair it fights in. A room the tracker can't place at the moment says nothing
    // either way: ending the wait on that would restart it, and its counts, at once.
    private bool LairWaitStillWanted()
    {
        if (LoopRunner.CurrentLoop is not { LairEntryDebuff: not Game.Map.LairEntryDebuffMode.Off } loop
            || !IsAutoCombatEnabled)
            return false;
        if (NextPlannedRoomForEquip(LoopRunner.PeekNextPlannedDirection()) is not { } next) return true;
        return RoomGraph.GetRoom(next) is { HasLair: true }
            && !Game.Map.LoopCombatSuppression.IsSuppressed(loop, next, currentIsLair: true);
    }

    // The debuff a lair wait is for and the mana it needs (the higher-asking of the
    // profile's two debuffs, by the cost the combat engine checks a cast against),
    // against what the character has. Null when there is nothing to wait for: no
    // debuff that needs mana, or mana not read yet.
    private (string Spell, int Needed, int Mana, int MaxMana, bool IsSpellCost)? LairEntryDebuffMana()
    {
        if (PlayerState.MaxMa <= 0) return null;
        Models.Profile.CombatSettings combat =
            ReadSection<Models.Profile.CombatSettings>(Profile.Current, "Combat");
        return Game.Combat.CombatSpellChooser.EntryDebuffReserve(
                combat, ReadAutoModeFlag(d => d.AutoNuke), PlayerState.MaxMa, Spellbook.ManaCostOf) is { } reserve
            ? (reserve.Spell, reserve.Mana, PlayerState.Ma, PlayerState.MaxMa, reserve.IsSpellCost)
            : null;
    }

    // The RoomKey a planned cardinal step will land in — the graph edge from the
    // current room in `dir`. Null when there's no planned cardinal step, no known
    // current room, or the direction isn't a graph exit (a command / boat / special
    // step). Feeds the pre-move gear swap for boss / lair rooms.
    private Game.Map.RoomKey? NextPlannedRoomForEquip(Game.Map.Direction? dir)
    {
        if (dir is not { } d || RoomTracker.State.CurrentRoom is not { } cur) return null;
        return cur.Exits.TryGetValue(d, out Game.Map.RoomExit exit) ? exit.Target : null;
    }

    // The hazard counter's last call for the step about to be taken, asked ahead of
    // the sneak check. A `use` it sends ends the sneak, and sent here the `sn` that
    // follows is the held kind, answered before the step goes. Sent from the approach
    // hook, behind the check, its `sn` went out in one burst with the move.
    // False while the step has to wait: for the answer to that `use`, or for the next
    // round's cast when the game refused it for this round's. The provisioner holds
    // its own movement gate meanwhile, and the gate's release re-drives the step.
    private bool HazardLastCallBeforeStep(Game.Map.Direction? direction)
    {
        if (NextPlannedRoomForEquip(direction) is { } next)
            AutoHazardCounterProvisioner.OnApproachingRoom(next);
        return !AutoHazardCounterProvisioner.HoldingStep;
    }

    // The room the character stands in, then the rooms the running walk or loop
    // plans to enter next: where the hazard counter looks for a room its buff matters
    // in. A follower has no plan of its own, nor has a character standing where the
    // player left it, so for them this is the one room.
    private IReadOnlyList<Game.Map.RoomKey> HazardRoomsAhead()
    {
        if (RoomTracker.State.CurrentRoom is not { } here) return Array.Empty<Game.Map.RoomKey>();
        List<Game.Map.RoomKey> rooms = new(1 + Game.Map.AutoHazardCounterProvisioner.LookaheadSteps) { here.Key };
        rooms.AddRange(PlannedRoomsAhead(Game.Map.AutoHazardCounterProvisioner.LookaheadSteps));
        return rooms;
    }

    // The rooms the moving engine's plan enters next, nearest first: its own next
    // steps followed through the map's exits, as far as they can be. Empty with
    // nothing moving the character.
    private IReadOnlyList<Game.Map.RoomKey> PlannedRoomsAhead(int count)
    {
        if (ResolveActiveMovementEngine() is not { } engine || RoomTracker.State.CurrentRoom is not { } at)
            return Array.Empty<Game.Map.RoomKey>();
        List<Game.Map.RoomKey> rooms = new(count);
        foreach (Game.Map.Direction step in engine.PeekPlannedDirections(count))
        {
            if (!at.Exits.TryGetValue(step, out Game.Map.RoomExit exit) || RoomGraph.GetRoom(exit.Target) is not { } next)
                break;
            rooms.Add(next.Key);
            at = next;
        }
        return rooms;
    }

    // The DEFAULT gear set's item-bearing slots as EquippedItems, for summing their
    // flat +MaxHP/+MaxMana bonuses. Skips empty slots and the two virtual
    // alternate-weapon slots (never worn — they write CombatSettings, not the wire).
    //
    // Only items the character actually has (worn or carried) count: the Default set is
    // the loadout the rest %s are tuned for, but a set item that's gone — lost to a
    // deathpile, sold, never obtained — can't be worn, so basing the pool on it made the
    // rest engine and the Settings / Workshop "N/M" figures chase a max the character
    // can't reach (report paradigm-20260925-112819: 458/380 shown against a live
    // 483/272, with the gear in a deathpile). Before the first 'i' dump nothing is known
    // to be owned, so the basis falls back to the live max.
    private IReadOnlyList<Game.Inventory.EquippedItem> DefaultSetEquippedItems()
    {
        if (Profile.Current?.Equipment is not { } eq || !Inventory.IsLoaded)
            return Array.Empty<Game.Inventory.EquippedItem>();
        Game.Inventory.InventorySnapshot pack = Inventory.Snapshot;
        return eq.Sets
            .FirstOrDefault(s => s.Trigger == Models.Profile.EquipTriggerType.Default)?.Slots
            .Where(e => !string.IsNullOrWhiteSpace(e.ItemName)
                     && e.Slot != Models.Profile.EquipmentSlot.AlternateWeapon
                     && e.Slot != Models.Profile.EquipmentSlot.AlternateOffHand
                     && pack.Has(e.ItemName!))
            .Select(e => new Game.Inventory.EquippedItem(e.ItemName!.Trim(), string.Empty))
            .ToList()
            ?? (IReadOnlyList<Game.Inventory.EquippedItem>)Array.Empty<Game.Inventory.EquippedItem>();
    }

    // Buff-duration source: a cast code's caster-line template and its effect
    // duration in seconds (Duration rounds × the wall-clock round length at the
    // spellbook level). Null for an unknown code, an enemy-targeting spell or one
    // with no duration; a missing caster line leaves the template empty.
    private (string Caster, long DurationSec)? BuffInfoByShort(string castCode)
    {
        if (string.IsNullOrWhiteSpace(castCode)) return null;
        string target = castCode.Trim();
        foreach (Game.Spells.KnownSpell s in Spellbook.Available)
        {
            if (!string.Equals(s.Short.Trim(), target, StringComparison.OrdinalIgnoreCase)) continue;
            // An enemy-targeting spell (a debuff / attack scope) is never a self-buff,
            // even if it has a positive duration — so a hand-cast one (e.g. vuln,
            // Targets 8 Monster-or-User) must not arm a self-buff recast timer or show
            // up as a phantom self-buff in the Buff Watchdog. This lookup feeds the
            // self-buff recast-window logic only (report paradigm-20260817-205819).
            if (Game.Combat.DebuffTargeting.IsSingleTargetEnemy(s.Targets)
                || Game.Combat.DebuffTargeting.IsAreaEnemy(s.Targets))
                return null;
            // The real duration ALWAYS comes from game data (Spells.Dur formula), never
            // the Messages caster line: a buff with no caster message (e.g. bladed
            // sphere / blsh) still has a real duration and must not fall back to the
            // 60s default — the fallback made it expire every 60s and, as bless-slot 1,
            // starve the lower slots at login (report paradigm-20260826-142652). Wall-
            // clock per-round length so "recast within N s" fires at the buff's REAL
            // remaining time, not ~1-2 s early off the nominal Dur×3.
            long durSec = (long)System.Math.Round(
                Game.Spells.SpellCalculator.Duration(s.Formula, Spellbook.Level)
                * Game.Spells.SpellCalculator.SpellRoundSecondsWallClock);
            if (durSec <= 0) return null;   // not a timed buff (an instant / combat spell)
            // The caster message is only for message-based landing DETECTION (party
            // confirm + applied-line) — optional; an empty template just skips it, the
            // computed duration stays authoritative for the recast clock.
            Models.GameData.MessageRecord? rec = FindSpellMessage(s.Number, s.Name);
            return (rec?.CasterMessage ?? string.Empty, durSec);
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
        // #item-cast slot: the item casts a spell on `use`, so classify by that
        // spell's Targets scope (a whole-party item cast blankets everyone in one use).
        if (Game.Spells.ItemCastToken.IsToken(target))
            return Spellbook.IsTokenWholeParty(target);
        foreach (Game.Spells.KnownSpell s in Spellbook.Available)
            if (string.Equals(s.Short.Trim(), target, StringComparison.OrdinalIgnoreCase))
                return s.Targets is 10 or 13;
        return false;
    }

    // True when a player with the given name is listed in the live "Also here:"
    // (RoomEntityClassifier). Case-insensitive on the resolved given name. This is NOT
    // a party-buff cast gate — party membership already means same room; it's only used
    // to CLEAR a hidden-target back-off when the member reappears in Also-here (a member
    // absent from Also-here but present in 'par' is simply hiding — including the leader
    // we follow, who never appears there). Null observation ⇒ not listed.
    private bool IsGivenNameInRoom(string givenName)
    {
        string g = givenName.Trim();
        if (RoomClassifier?.Current?.Entities is not { } entities) return false;
        foreach (Game.Combat.RoomEntity e in entities)
            if (e.Kind == Game.Combat.EntityKind.Player
                && string.Equals(e.ResolvedName, g, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // Self-buff cast code → the configured PARTY-WIDE party-buff cast code that removes
    // (supersedes) it via RemovesSpell (Abil 122), while in a party. Empty when solo. In
    // a party we let a party-wide buff that removes a self-buff cover us instead of self-
    // casting the removed one — chant removes bless, so once chant is a party buff we stop
    // self-casting bless. Only PARTY-WIDE covers count: a single-target party buff never
    // lands on self, so it can't cover our self-cast. Drives the director's self-buff
    // suppression and the Buff Watchdog "covered by" label.
    //
    // Layer when possible, cover only when not: stock applies RemovesSpell only at cast,
    // so a ONE-WAY remover (the self-buff doesn't remove the party buff back) layers —
    // party buff first, then the self-buff (CollisionOrderConstraints orders them, and the
    // clobber-clear re-arms the self-buff after each party recast). Only a mutual pair,
    // or Paradigm's per-tick removal, can't coexist, so only those are covered.
    public IReadOnlyDictionary<string, string> SelfBuffCoverage()
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        if (!PartyState.IsInParty) return map;

        Models.Profile.SpellsSettings spells = Resolver.Resolve<Models.Profile.SpellsSettings>("Spells");

        // Configured self-buffs → (cast code, spell number). #item-cast tokens resolve to
        // no spell and are skipped (an item buff isn't a RemovesSpell target).
        List<(string Code, int Number)> selfBuffs = new();
        void AddSelf(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            if (Spellbook.FindByCastCode(code.Trim()) is { } s)
                selfBuffs.Add((s.Short, s.Number));
        }
        Models.Profile.BuffSettings? buffs = Profile.Current?.PartyBuffs;
        // The unified list's self-cast slots are the covered candidates (bless +
        // when-full folded here). Whole-party / member-target slots aren't self-casts.
        if (buffs is not null)
            foreach (Models.Profile.BuffSlot pslot in buffs.Slots)
                if (pslot.CastOnSelf && !IsPartyWideBuff(pslot.Spell ?? string.Empty))
                    AddSelf(pslot.Spell);
        // HP regen still lives on the Spells tab (mana regen is a unified slot, already
        // covered by the CastOnSelf loop above).
        AddSelf(spells.HpRegenSpell);
        if (selfBuffs.Count == 0) return map;

        if (buffs is null) return map;
        bool layerOneWay = GameData.ActiveRealm != Game.RealmType.ParaMud;
        foreach (Models.Profile.BuffSlot pslot in buffs.Slots)
        {
            if (string.IsNullOrWhiteSpace(pslot.Spell)) continue;
            if (!pslot.WholePartyOn) continue;             // toggled off → not cast → can't cover
            if (!IsPartyWideBuff(pslot.Spell)) continue;   // a single-target party buff never covers self
            HashSet<int> removed = RemovedSpellNumbers(pslot.Spell);
            if (removed.Count == 0) continue;
            int partyNumber = Spellbook.FindByCastCode(pslot.Spell.Trim())?.Number ?? 0;
            foreach ((string code, int number) in selfBuffs)
            {
                if (!removed.Contains(number) || map.ContainsKey(code)) continue;
                if (layerOneWay && !RemovedSpellNumbers(code).Contains(partyNumber)) continue;
                map[code] = pslot.Spell.Trim();
            }
        }
        return map;
    }

    // Configured buffs that can never stay up because another configured buff PERMANENTLY
    // removes them: loser cast code → the winning buff's name (for a "covered by" label).
    // A one-directional conflict only (Y removes X, X does NOT remove Y back) — Y is up, so
    // X is stripped and re-stripped forever; the client shouldn't waste casts maintaining it
    // or show a live timer for it. Mutual pairs (each removes the other, e.g. bless ↔ greater
    // bless) are NOT suppressed — those are last-cast-wins, left to the normal clobber-clear.
    //
    // PARADIGM ONLY: Paradigm re-enforces a buff's RemovesSpell continuously (~3s), so the
    // loser truly can't coexist. Stock checks RemovesSpell only at cast time, so cast order
    // lets both stay (CollisionOrderConstraints) — this returns empty off Paradigm.
    //
    // Distinct from SelfBuffCoverage (which is the in-party, whole-party-covers-self case,
    // including mutual pairs): this is the general one-directional winner, self-cast winners
    // and solo included. The picker skips a suppressed slot for ANY target; the Buff Watchdog
    // shows the loser as "covered by" instead of a stuck "conflict".
    public IReadOnlyDictionary<string, string> SuppressedBuffCoverage()
    {
        // PARADIGM ONLY: only there is a buff's RemovesSpell re-enforced continuously (~3s),
        // so the one-directional loser truly can't coexist. Stock checks it only at cast
        // time, so both can stay — suppress nothing there.
        if (GameData.ActiveRealm != Game.RealmType.ParaMud)
            return new Dictionary<string, string>();
        return Game.Spells.BuffConflictAnalyzer.OneDirectionalLosers(BuffSlotOverwritePairs());
    }

    // STOCK ONLY: the stock counterpart to SuppressedBuffCoverage. On stock a buff's
    // RemovesSpell fires only at cast (not the Paradigm ~3s re-enforcement), so a one-
    // directional loser CAN coexist with its remover if the remover is cast first — and
    // re-applied after each remover recast. This map (loser cast-code → remover cast-code)
    // lets CastingDirector order the remover ahead of the loser instead of dropping it.
    // Empty on Paradigm (SuppressedBuffCoverage owns that realm) and for mutual pairs.
    public IReadOnlyDictionary<string, string> CollisionOrderConstraints()
    {
        if (GameData.ActiveRealm == Game.RealmType.ParaMud)
            return new Dictionary<string, string>();
        return Game.Spells.BuffConflictAnalyzer.OneDirectionalRemoverCodes(BuffSlotOverwritePairs());
    }

    // The spell numbers a cast code's spell removes (RemovesSpell, Abil 122 — the same
    // effect the Spell Book renders as "Removes <spell>"). LITERAL: a spell strips exactly
    // the spells its own list names, with no transitive/family inference. The game data is
    // authoritative here — chant removes bless/curse/blight but NOT greater bless, even
    // though bless and greater bless remove each other (so casting chant leaves an active
    // greater bless alone; greater bless removes chant directly). An earlier "bless-family
    // exclusivity slot" expansion inferred chant→greater-bless transitively and was wrong
    // (user-confirmed in-game, Paradigm — report paradigm-20260910-012303 follow-up).
    private HashSet<int> RemovedSpellNumbers(string castCode)
    {
        if (Spellbook.FindByCastCode(castCode.Trim()) is not { } s) return new HashSet<int>();
        return Game.Spells.BuffConflictAnalyzer.RemovedSpellNumbers(s.Formula);
    }

    // Every pair of configured, resolvable buff slots where one's spell removes the
    // other's via RemovesSpell (Abil 122) and their targeting can land on the same
    // character (self, a shared member, or anyone via a whole-party cast). Besides
    // the Buff Panel's per-row warning and the Buff Watchdog's "covered by" label, it
    // drives CastingDirector through SuppressedBuffCoverage (Paradigm: a
    // one-directional loser is never cast) and CollisionOrderConstraints (Stock: the
    // remover is cast first). #item-cast slots don't resolve to a KnownSpell and are
    // skipped, same as SelfBuffCoverage's self-buff collection.
    public IReadOnlyList<Game.Spells.BuffOverwritePair> BuffSlotOverwritePairs()
    {
        List<Game.Spells.BuffOverwritePair> pairs = new();
        System.Collections.Generic.List<Models.Profile.BuffSlot>? slots = Profile.Current?.PartyBuffs?.Slots;
        if (slots is null || slots.Count == 0) return pairs;

        List<(Game.Spells.KnownSpell Spell, Game.Spells.BuffAffectSet Affect)> resolved = new();
        foreach (Models.Profile.BuffSlot slot in slots)
        {
            if (string.IsNullOrWhiteSpace(slot.Spell)) continue;
            string code = slot.Spell.Trim();
            if (Spellbook.FindByCastCode(code) is not { } spell) continue;
            bool isWholeParty = IsPartyWideBuff(code);
            // Judge co-landing by SCOPE — what the slot COULD ever land on — not the live
            // on/off toggles. The ⚠ is a heads-up about the configured PAIR: two buffs
            // that remove each other still clobber whenever both are up, no matter which
            // Self / Party / member boxes are ticked right now. (A whole-party buff can
            // hit everyone; a self / single-target buff can hit you and/or any member.)
            // The timer-side "conflict" call, by contrast, reads the live cast snapshot —
            // so an un-cast buff never falsely marks another as clobbered.
            Game.Spells.BuffAffectSet affect = isWholeParty
                ? new Game.Spells.BuffAffectSet { Everyone = true, Members = System.Array.Empty<string>() }
                : new Game.Spells.BuffAffectSet { Self = true, AllMembers = true, Members = System.Array.Empty<string>() };
            resolved.Add((spell, affect));
        }

        for (int i = 0; i < resolved.Count; i++)
        {
            HashSet<int> removes = RemovedSpellNumbers(resolved[i].Spell.Short);
            if (removes.Count == 0) continue;
            for (int j = 0; j < resolved.Count; j++)
            {
                if (i == j || !removes.Contains(resolved[j].Spell.Number)) continue;
                if (!Game.Spells.BuffConflictAnalyzer.CanCoLand(resolved[i].Affect, resolved[j].Affect)) continue;
                pairs.Add(new Game.Spells.BuffOverwritePair(
                    resolved[i].Spell.Short, resolved[i].Spell.Name,
                    resolved[j].Spell.Short, resolved[j].Spell.Name));
            }
        }
        return pairs;
    }

    // Apply-cast matchers for PartyAilmentTracker's witness-SET path: every Messages
    // record that inflicts a witnessable ailment (blind / confuse / disease / held —
    // poison is par-owned, excluded), carries a WitnessMessage (a monster casting it
    // on a member, seen in the room), and links to its Spells row (so we know the
    // spell number for its duration). Cached; invalidated on a Messages change / set
    // switch (wired where Messages is constructed). Rebuilt lazily here.
    private IReadOnlyList<Game.Conditions.ApplyCastMatcher>? _applyMatchers;

    // Game-data index of which spells REMOVE which ailment (cure poison / disease /
    // blindness / paralysis, heal+cures). Drives the party chip cure-clear path so a
    // party-mate's cure is recognized regardless of the local character's config.
    // Cache keyed on the active set; Invalidated when the Messages catalogue changes
    // (its Diseased-flagged records are the disease-apply source).
    private readonly Game.GameData.CureSpellIndex _cureSpells;

    // Compiled cure-confirmation matchers, cached like _applyMatchers — the party
    // tracker re-reads them on every inbound line, and rebuilding scans the Messages
    // catalogue once per cure spell, so caching avoids that sweep per line. Depends
    // only on the active set + the Messages catalogue (no per-character input), so
    // the same two invalidations that reset _applyMatchers / _cureSpells cover it.
    private IReadOnlyList<Game.Conditions.CureCastMatcher>? _cureMatchers;

    private IReadOnlyList<Game.Conditions.ApplyCastMatcher> ApplyCastMatchers()
    {
        if (_applyMatchers is { } cached) return cached;

        const Models.GameData.MessageFlags Witnessable =
            Models.GameData.MessageFlags.Blinded | Models.GameData.MessageFlags.Confused
            | Models.GameData.MessageFlags.Diseased | Models.GameData.MessageFlags.MovementPrevented;
        Models.GameData.MessageFlags[] bits =
        {
            Models.GameData.MessageFlags.Blinded, Models.GameData.MessageFlags.Confused,
            Models.GameData.MessageFlags.Diseased, Models.GameData.MessageFlags.MovementPrevented,
        };

        List<Game.Conditions.ApplyCastMatcher> list = new();
        foreach (Models.GameData.MessageRecord rec in Messages.Messages)
        {
            if ((rec.Flags & Witnessable) == Models.GameData.MessageFlags.None) continue;
            if (rec.Flags.HasFlag(Models.GameData.MessageFlags.Disabled)) continue;
            if (Game.Spells.CasterMessageMatcher.TryCreate(rec.WitnessMessage) is not { } witness) continue;
            int spellNumber = SpellNumberOf(rec);
            if (spellNumber <= 0) continue;
            foreach (Models.GameData.MessageFlags bit in bits)
                if (rec.Flags.HasFlag(bit))
                    list.Add(new Game.Conditions.ApplyCastMatcher(bit, rec.Name, spellNumber, witness));
        }
        return _applyMatchers = list;
    }

    // The Spells-table Number a message record is anchored to (via its back-links),
    // or 0 when it isn't spell-linked.
    private static int SpellNumberOf(Models.GameData.MessageRecord rec)
    {
        if (rec.Links is null) return 0;
        foreach (Models.GameData.GameDataLink link in rec.Links)
            if (link.Table.Equals("Spells", StringComparison.OrdinalIgnoreCase))
                return link.Number;
        return 0;
    }

    // Spell-formula cache for duration lookups; rebuilt lazily, invalidated on set
    // switch (wired with the apply-matcher cache).
    private Dictionary<int, Game.Spells.SpellFormulaInput>? _spellFormulas;

    private Game.Spells.SpellFormulaInput? SpellFormulaFor(int spellNumber)
    {
        if (_spellFormulas is null)
        {
            Dictionary<int, Game.Spells.SpellFormulaInput> map = new();
            if (GameData.GetTable("Spells") is { } doc)
                foreach (GameRow row in doc.Rows)
                    if (row.TryGetProperty("Number", out GameValue n)
                        && n.TryGetInt32(out int num))
                        map[num] = Game.Spells.SpellFormulaReader.Read(row);
            _spellFormulas = map;
        }
        return _spellFormulas.TryGetValue(spellNumber, out Game.Spells.SpellFormulaInput f) ? f : null;
    }

    // Duration (seconds) a witnessed ailment spell will last on a party member —
    // deterministic from the CASTING monster's cast level (a "resist" prints no
    // apply line, so never reaches here). When the witness line named its caster
    // and a monster of that name in the room carries the spell, that monster alone
    // decides. Otherwise take the LONGEST among in-room monsters that carry it —
    // clearing a chip late is safe, clearing it early (stopping a still-needed
    // cure) is not. An on-hit effect has no cast level, so it runs the spell's base
    // duration (GAME_MECHANICS "Monster on-hit procs (`AttHitSpell-N`) are physical
    // attacks, not casts"). Null when no in-room monster carries the spell or it
    // has no duration — the tracker falls back to its cap.
    private double? ResolveAilmentDurationSeconds(int spellNumber, string? source)
    {
        if (SpellFormulaFor(spellNumber) is not { } formula) return null;
        if (RoomClassifier.Current is not { } obs) return null;

        long bestRounds = 0, namedRounds = 0;
        foreach (Game.Combat.RoomEntity e in obs.Entities)
        {
            if (e.MonsterNumber is not { } mn) continue;
            if (MonsterCatalog.Get(mn) is not { } entry) continue;
            // Slot-type handling lives on the entry — Accuracy only means a spell
            // number on an AttType-2 slot, and reading it off a physical slot used
            // to match unrelated spells and feed their damage in as a cast level.
            long rounds = entry.CastLevelFor(spellNumber) is var level and > 0
                ? Game.Spells.SpellCalculator.Duration(formula, level)
                : entry.HasHitSpell(spellNumber) ? formula.Dur : 0;
            if (rounds <= 0) continue;
            if (rounds > bestRounds) bestRounds = rounds;
            if (rounds > namedRounds && IsNamed(e, source)) namedRounds = rounds;
        }
        long chosen = namedRounds > 0 ? namedRounds : bestRounds;
        return chosen > 0 ? chosen * Game.Spells.SpellCalculator.SpellRoundSecondsWallClock : null;
    }

    // Whether a witness line's caster capture is this room monster. The capture can
    // carry the article the template didn't swallow ("The kobold shaman").
    private static bool IsNamed(Game.Combat.RoomEntity e, string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        string s = source.Trim();
        foreach (string article in new[] { "the ", "a ", "an " })
            if (s.StartsWith(article, StringComparison.OrdinalIgnoreCase)) { s = s[article.Length..]; break; }
        return s.Equals(e.ResolvedName, StringComparison.OrdinalIgnoreCase)
            || s.Equals(e.RawName, StringComparison.OrdinalIgnoreCase);
    }

    // Cure-confirmation matchers for the party ailment tracker's chip-clear path.
    // Built from game data (CureSpellIndex), NOT the local character's configured
    // cure slots: a party-mate cures with their own class's spells, which the local
    // character may never learn, so keying recognition on local config missed every
    // cross-caster cure (a Priest's cure poison never cleared the member's chip
    // until the next `par`). Each cure spell's CasterMessage (our cast) and
    // WitnessMessage (another member's cast seen in the room) compile to matchers
    // that pin the spell name + target, so a cure landing on a member clears that
    // member's chip. Cached; dropped on a game-data set swap or a Messages edit, so
    // either takes effect.
    private IReadOnlyList<Game.Conditions.CureCastMatcher> CureCastMatchers()
    {
        if (_cureMatchers is { } cached) return cached;

        List<Game.Conditions.CureCastMatcher> list = new();
        foreach (Game.GameData.CureSpellIndex.CureSpell cure in _cureSpells.AllCures())
        {
            Models.GameData.MessageRecord? rec = FindSpellMessage(cure.Number, cure.Name);
            if (rec is null) continue;
            // Needs a caster matcher that pins {spellname}+{target}; a record whose
            // cast line has no placeholder (a bare NPC-healer variant) is skipped.
            if (Game.Spells.CasterMessageMatcher.TryCreate(rec.CasterMessage) is not { } caster) continue;
            Game.Spells.CasterMessageMatcher? witness =
                Game.Spells.CasterMessageMatcher.TryCreate(rec.WitnessMessage);
            foreach (Models.GameData.MessageFlags ailment in CuredAilmentBits(cure.Cures))
                list.Add(new Game.Conditions.CureCastMatcher(ailment, cure.Name, caster, witness));
        }
        return _cureMatchers = list;
    }

    // Split a spell's combined cured-flags value into the individual ailment bits the
    // tracker clears — a heal+cure removes several at once, each its own matcher.
    private static IEnumerable<Models.GameData.MessageFlags> CuredAilmentBits(Models.GameData.MessageFlags cured)
    {
        if ((cured & Models.GameData.MessageFlags.Poisoned) != 0) yield return Models.GameData.MessageFlags.Poisoned;
        if ((cured & Models.GameData.MessageFlags.Diseased) != 0) yield return Models.GameData.MessageFlags.Diseased;
        if ((cured & Models.GameData.MessageFlags.Blinded) != 0) yield return Models.GameData.MessageFlags.Blinded;
        if ((cured & Models.GameData.MessageFlags.MovementPrevented) != 0) yield return Models.GameData.MessageFlags.MovementPrevented;
    }

    // Spell numbers that APPLY disease — the Diseased-flagged message records'
    // linked spells. Disease has no ability code of its own, so this is how a
    // "cure disease" spell's RemovesSpell targets are recognized as disease (see
    // CureSpellIndex). Same Diseased-flag source ConditionTracker uses to detect it.
    private IReadOnlySet<int> DiseaseApplySpellNumbers()
    {
        HashSet<int> set = new();
        foreach (Models.GameData.MessageRecord rec in Messages.Messages)
        {
            if (!rec.Flags.HasFlag(Models.GameData.MessageFlags.Diseased)) continue;
            int n = SpellNumberOf(rec);
            if (n > 0) set.Add(n);
        }
        return set;
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

    // The cast codes of the buffs a given cast code's spell REMOVES (RemovesSpell / Abil
    // 122). Lets the CastingDirector re-attribute a wear-off that lands right after a
    // clobbering cast to its victim rather than the just-cast survivor (bless & chant
    // share the wear-off message, so the shared line can't disambiguate on its own).
    private IReadOnlyCollection<string> RemovesShortsFor(string castShort)
    {
        if (string.IsNullOrWhiteSpace(castShort)) return System.Array.Empty<string>();
        // LITERAL removes (RemovedSpellNumbers) — a spell strips exactly the spells its
        // own RemovesSpell list names, no family inference. Realm-agnostic: it's what lets
        // a landed remover clear its victim's timer on ANY realm (so on stock a collision-
        // order loser is re-cast after the remover, even if the loser wasn't otherwise due).
        HashSet<int> removed = RemovedSpellNumbers(castShort);
        if (removed.Count == 0) return System.Array.Empty<string>();
        List<string> shorts = new();
        foreach (Game.Spells.KnownSpell s in Spellbook.Available)
            if (removed.Contains(s.Number)) shorts.Add(s.Short);
        return shorts;
    }

    // ----- Mana-regen reroll glue ---------------------------------------
    // The shared, gate-wrapped engine wire-sender that SendGameCommand and the
    // engines built here send through — the reroll engine's abil query and its
    // deliberate cooldown-bypassing recast among them. Bound in the main VM
    // alongside the per-service SetWireSender calls; null until the first connect.
    private Action<byte[]>? _engineWireSend;

    // Bind the shared, gate-wrapped engine wire-sender that SendGameCommand and
    // the engines built here send through. Same engineSend the per-service
    // SetWireSender calls receive.
    public void SetEngineWireSender(Action<byte[]> send)
    {
        ArgumentNullException.ThrowIfNull(send);
        _engineWireSend = send;
    }

    // Whether SendGameCommand has a wire to send on, for the bug report.
    public bool EngineWireBound => _engineWireSend is not null;

    // Un-wrapped wire sender that pierces the EngineSendGate — bound to the same raw
    // SendUserInput the emergency hangup uses (NOT the gate-wrapped engine sender).
    // `sys` commands ride this because they're honoured at ANY HP, mortally-wounded
    // included (confirmed mechanic), so they must survive the HP <= 0 send-gate hold
    // rather than being dropped like ordinary engine sends. Null until first connect.
    private Action<byte[]>? _rawWireSend;

    public void SetRawWireSender(Action<byte[]> send)
    {
        ArgumentNullException.ThrowIfNull(send);
        _rawWireSend = send;
    }

    // Send a command line on the raw (gate-piercing) wire, CR appended. Returns
    // false when no sender is bound.
    private bool SendGameCommandRaw(string command)
    {
        if (_rawWireSend is null || string.IsNullOrWhiteSpace(command)) return false;
        SendingEngineRawCommand = true;
        try { _rawWireSend(System.Text.Encoding.Latin1.GetBytes(command.Trim() + "\r")); }
        finally { SendingEngineRawCommand = false; }
        return true;
    }

    // Whether a line is a text exit of the room the tracker has us in.
    private bool IsExitCommandHere(string command) =>
        RoomTracker.State.CurrentRoom is { } room && room.HasExitCommand(command);

    // True while SendGameCommandRaw is sending. The raw wire goes round the
    // EngineSendGate, so to an outbound observer its lines look typed; the `break`
    // `sys goto` sends ahead of a jump is the client's, not the user's, and must not
    // be taken for the one that holds the combat engine's attack.
    public bool SendingEngineRawCommand { get; private set; }

    // Send a command line to the server as if the user typed it (CR appended),
    // riding the shared engine wire-sender. Returns false when no sender is bound
    // yet (not connected).
    public bool SendGameCommand(string command)
    {
        if (_engineWireSend is null || string.IsNullOrWhiteSpace(command)) return false;
        _engineWireSend(System.Text.Encoding.Latin1.GetBytes(command.Trim() + "\r"));
        return true;
    }

    // MovementRefusalDetector.PaidCrossingRefused. The exit is closed to routes from
    // here on, and when the purse on record covered the price the record is wrong
    // (coin can go with nothing printed, as a room script's price does:
    // GAME_MECHANICS "How a charge takes coins, and when the purse is re-bucketed"):
    // the toll gate stops believing it at once and the inventory is read again, one
    // `i` for the refusal. With Auto-All off the move was the user's own and nothing
    // is sent (user, 2026-10-10); the read is owed until the switch is back on.
    public void OnPaidCrossingRefused(Game.Map.RoomExit? crossing, long? namedCopper)
    {
        if (!Movement.NoteCrossingRefused(crossing, namedCopper)) return;
        PurseRead.Ask();
    }

    // The inventory reads still owed (after a death, after a refused toll) go out
    // now if they may: the master switch is back on, or the send gate has let go.
    // One `i` serves both.
    private void SendOwedInventoryReads()
    {
        bool deathReadDue = InventoryAfterDeath.Due;
        if (deathReadDue && RoomTracker.State.CurrentRoom is not null) InventoryAfterDeath.OnRoomKnown();
        if (deathReadDue && !InventoryAfterDeath.Due) PurseRead.CoveredByAnotherRead();
        else PurseRead.Retry();
    }

    // A Grab-All boss's loot just hit the floor: fire a blind `get <item>` for every
    // item in the dead monster's game-data drop table (no room re-parse). Gated here
    // on the per-boss flag; the event fires for every matched boss regardless. The
    // dead monster is the boss's own record or one its death summoned, and the loot
    // is usually on the last of them (BossDeathLoot).
    private void FireBossGrabAll(Models.Profile.BossDef def, string? deadName, int? expGained)
    {
        if (!def.GrabAll) return;
        if (AutoModeController.Blocks("Boss Grab All", def.Name)) return;
        IReadOnlyList<Game.Inventory.BossDeathLoot.ChainMonster> chain = BossDeathChain(def);
        if (chain.Count == 0)
        {
            Log.Info("GrabAll", $"'{def.Name}' died but has no monster number — can't read its drop table");
            return;
        }
        IReadOnlyList<int> died = Game.Inventory.BossDeathLoot.RecordsThatDied(chain, deadName, expGained);
        List<string> cmds = new();
        TimeSpan stall = TimeSpan.Zero;
        foreach (int num in died)
        {
            foreach (string cmd in Game.Inventory.BossGrabAllCommands.Build(MonsterCatalog.Get(num)?.Drops, ItemNames.GetName))
                if (!cmds.Contains(cmd)) cmds.Add(cmd);
            // A boss whose death casts a "... temp" spell leaves the room unable to
            // act until that spell runs out, and the game throws away what is sent
            // before then (GAME_MECHANICS "Silent death spells that stall the room
            // (`… temp`)").
            // The grab waits it out and goes once.
            TimeSpan own = DeathStallOf(num);
            if (own > stall) stall = own;
        }
        string who = $"'{def.Name}' ({string.Join(", ", died.Select(n => $"#{n} {MonsterCatalog.Get(n)?.Name}"))})";
        if (cmds.Count == 0)
        {
            Log.Info("GrabAll", $"{who} died — no known droppable items to grab");
            return;
        }
        if (stall > TimeSpan.Zero)
        {
            Log.Info("GrabAll",
                $"{who} died — grabbing {cmds.Count} drop{(cmds.Count == 1 ? "" : "s")} in {stall.TotalMilliseconds:F0} ms, once its death spell has run out");
            HoldThroughDeathStall(stall, $"Grab All for '{def.Name}'",
                () => { foreach (string cmd in cmds) SendGameCommand(cmd); });
            return;
        }
        Log.Info("GrabAll", $"{who} died — grabbing {cmds.Count} drop{(cmds.Count == 1 ? "" : "s")}");
        foreach (string cmd in cmds) SendGameCommand(cmd);
    }

    // A boss as the monsters it is, in order: its own record, then everything its
    // death summons, and theirs. Empty when the boss names no monster in this game
    // data. Bounded, since a summon chain in edited data could loop.
    private IReadOnlyList<Game.Inventory.BossDeathLoot.ChainMonster> BossDeathChain(Models.Profile.BossDef def)
    {
        const int MaxChain = 12;
        List<Game.Inventory.BossDeathLoot.ChainMonster> chain = new();
        if ((def.MonsterNumber ?? ResolveMonsterNumberByName(def.MatchName)) is not { } root) return chain;
        Queue<int> next = new();
        HashSet<int> seen = new() { root };
        next.Enqueue(root);
        while (next.Count > 0 && chain.Count < MaxChain)
        {
            int num = next.Dequeue();
            if (MonsterCatalog.Get(num) is not { } m) continue;
            chain.Add(new Game.Inventory.BossDeathLoot.ChainMonster(num, m.Name, m.EffectiveExp));
            foreach (int summoned in ExpResolver?.DeathSummonsOf(num) ?? Array.Empty<int>())
                if (seen.Add(summoned)) next.Enqueue(summoned);
        }
        return chain;
    }

    // What it takes to hurt a boss: the hit-magic level a weapon needs (the monster's
    // Magical) and the level a spell needs (its spell immunity). A boss that turns
    // into something else as it dies isn't dead until that is, so it's the highest
    // any monster in the chain asks for. Both 0 for a box, or a boss this game data
    // has no monster for.
    public (int HitMagic, int SpellLevel) BossReach(Models.Profile.BossDef def)
    {
        int hitMagic = 0, spellLevel = 0;
        foreach (Game.Inventory.BossDeathLoot.ChainMonster link in BossDeathChain(def))
        {
            if (MonsterCatalog.Get(link.Number) is not { } m) continue;
            hitMagic = Math.Max(hitMagic, m.Magical);
            spellLevel = Math.Max(spellLevel, m.SpellImmunity);
        }
        return (hitMagic, spellLevel);
    }

    // How long a monster's death leaves the room unable to act: the length of its
    // "... temp" death spell, or zero when it has none.
    private TimeSpan DeathStallOf(int monsterNumber)
    {
        int deathSpell = MonsterCatalog.Get(monsterNumber)?.DeathSpell ?? 0;
        if (deathSpell <= 0) return TimeSpan.Zero;
        if (GameData.FindRowByNumber("Spells", deathSpell) is not GameRow spell) return TimeSpan.Zero;
        string? name = spell.TryGetProperty("Name", out GameValue n) ? n.GetString() : null;
        if (!Game.Combat.TempDeathResponse.IsTempSpell(name)) return TimeSpan.Zero;
        int dur = spell.TryGetProperty("Dur", out GameValue d) && d.TryGetInt32(out int rounds) ? rounds : 0;
        return Game.Combat.TempDeathResponse.StallTime(dur);
    }

    // Run a pickup once a death spell has run out, keeping the walker in the room
    // until then. Counted: a grab and a coin re-look can wait on the same death.
    private int _deathStallHolds;

    private void HoldThroughDeathStall(TimeSpan stall, string reason, Action pickUp)
    {
        if (_deathStallHolds++ == 0)
            MovementCoordinator.AssertGate(Game.Map.MovementCoordinator.DeathStallGate, "DeathStall", reason);
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = stall };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { pickUp(); }
            finally
            {
                if (--_deathStallHolds == 0)
                    MovementCoordinator.ClearGate(Game.Map.MovementCoordinator.DeathStallGate, "DeathStall",
                        "the death spell has run out");
            }
        };
        timer.Start();
    }

    // The Monsters-table Numbers a death could belong to: for a room spell's kill,
    // every kind the room listed; otherwise the monster we were fighting. The fought
    // monster is read off the room roster first, which knows the Number behind a
    // flavoured name ("fierce kobold thief") and which same-named record lives in this
    // room; the name lookup is the fallback. Only valid before the roster resync drops
    // the dead entity.
    private HashSet<int> DyingMonsterNumbers(Game.Combat.MonsterDeathEvent evt)
    {
        HashSet<int> numbers = new();
        // A room spell's kill is one of the kinds the room listed, and the monster we
        // were fighting is only the one the round was anchored to.
        if (evt.RoomSpellRoster is { } listed)
        {
            foreach (Game.Combat.MonsterDeathIdentity id in listed)
                if (id.Number is { } n) numbers.Add(n);
            return numbers;
        }
        if (Combat.DeathAttributionTarget is not { Length: > 0 } dying) return numbers;

        if (RoomClassifier.Current is { } roster)
            foreach (Game.Combat.RoomEntity e in roster.Entities)
                if (e.Kind == Game.Combat.EntityKind.Monster && e.MonsterNumber is { } inRoom
                    && (string.Equals(e.RawName, dying, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(e.ResolvedName, dying, StringComparison.OrdinalIgnoreCase)))
                    numbers.Add(inRoom);
        if (ResolveMonsterNumberByName(dying) is { } byName) numbers.Add(byName);
        return numbers;
    }

    // Re-display the room after a kill when the dead monster can drop an item the
    // auto-get engine would pick up. Nothing is sent for a monster with no such drop,
    // so ordinary kills cost no extra Enter.
    private void ReLookForDrops(Game.Combat.MonsterDeathEvent evt)
    {
        if (!ReadAutoModeFlag(d => d.AutoGetItems)) return;
        foreach (int number in DyingMonsterNumbers(evt))
        {
            if (MonsterCatalog.Get(number) is not { } monster) continue;
            foreach (Game.Combat.MonsterDropSlot drop in monster.Drops)
            {
                Models.GameData.ItemOverlay overlay = ResolveItemOverlay(drop.ItemId);
                if (!(overlay.AutoCollect ?? false) || (overlay.CannotBeTaken ?? false)) continue;
                Log.Debug(Game.Inventory.AutoGetItemsManager.LogCategory,
                    $"killed '{monster.Name}' (#{number}), which can drop "
                    + $"'{ItemNames.GetName(drop.ItemId)}' — checking the floor");
                AutoGetItems.RequestDropReLook();
                return;
            }
        }
    }

    // A monster whose DeathSpell is a silent "…temp" spell just died: those spells emit no
    // wire line but stall the game engine, so send the temp spell's MessageRecord.CastResponse
    // (seeded "^M^M" = two carriage returns) to nudge the engine past the stall. Identity is
    // best-effort, since the event names no monster: the engaged target, or for a room
    // spell's kill the kinds the room listed; a stray extra CR is harmless. Fires at most
    // one response per death.
    private void FireTempDeathResponse(Game.Combat.MonsterDeathEvent evt)
    {
        if (_engineWireSend is null) return;
        // The nudge is ours to send, not the game's to need: with the master
        // switch off the stall runs its length, or the user's own Enter ends it.
        if (AutoModeController.KillSwitchEngaged) return;
        // One response, one coin hold: TempDeathBurst answers a room spell's kills
        // once however many of the listed monsters the round killed.
        if (_tempDeathBurst.KindToAnswer(evt, DyingMonsterNumbers(evt)) is not { } num) return;
        if (TempDeathSpellOf(num) is not var (deathSpell, spellName)) return;
        string occasion = evt.RoomSpellRoster is null
            ? "death-cast"
            : "death-cast among our room spell's kills, answered once for the room";

        // The coin get went out on the drop line, before the death was known,
        // so the game threw it away. Once the death spell has run out the room
        // is drawn again, and the coins still lying there are asked for then.
        Cash.NoteDeathStall();
        TimeSpan stall = DeathStallOf(num);
        if (stall > TimeSpan.Zero && Cash.HasUnansweredGet)
            HoldThroughDeathStall(stall, "coins dropped at the kill",
                () => _engineWireSend?.Invoke(new[] { (byte)'\r' }));

        foreach (Models.GameData.MessageRecord r in Messages.Messages)
        {
            if (string.IsNullOrEmpty(r.CastResponse) || r.Links is null) continue;
            bool linked = false;
            foreach (Models.GameData.GameDataLink l in r.Links)
                if (l.Table == "Spells" && l.Number == deathSpell) { linked = true; break; }
            if (!linked) continue;
            if (Game.Combat.TempDeathResponse.ExpandToWireBytes(r.CastResponse) is not { } bytes) continue;
            _engineWireSend(bytes);
            Log.Info("TempDeath",
                $"'{spellName}' (#{deathSpell}) {occasion} — sent cast response to unstick the engine");
            return;
        }

        // No message record names a response for this one (37 of the Paradigm
        // bosses' temp spells had none): the same two carriage returns every
        // listed one is given.
        if (Game.Combat.TempDeathResponse.ExpandToWireBytes(Game.Combat.TempDeathResponse.DefaultResponse)
            is { } fallback)
        {
            _engineWireSend(fallback);
            Log.Info("TempDeath",
                $"'{spellName}' (#{deathSpell}) {occasion} — no cast response on record, sent the default to unstick the engine");
        }
    }

    // The room-spell bookkeeping behind FireTempDeathResponse. Set in the
    // constructor, ahead of the death subscription that reads it.
    private readonly Game.Combat.TempDeathBurst _tempDeathBurst;

    // A monster's DeathSpell when it is one of the silent "... temp" spells.
    private (int Number, string Name)? TempDeathSpellOf(int monsterNumber)
    {
        int deathSpell = MonsterCatalog.Get(monsterNumber)?.DeathSpell ?? 0;
        if (deathSpell <= 0) return null;
        string? name = GameData.FindNameByNumber("Spells", deathSpell);
        return Game.Combat.TempDeathResponse.IsTempSpell(name) ? (deathSpell, name!) : null;
    }

    // The Monsters-table Number for a boss whose BossDef didn't carry one — resolved
    // by its game-data name. Null when the active set has no such monster.
    private int? ResolveMonsterNumberByName(string name) => TableNumberByName("Monsters", name);

    // The Number of a game-data table's row, found by its name (a monster, a
    // class, a race). Null when the active set has no such row.
    private int? TableNumberByName(string table, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (GameData.FindRowByName(table, name) is not GameRow row) return null;
        return row.TryGetProperty("Number", out GameValue el)
               && el.TryGetInt32(out int n) ? n : null;
    }

    // Walking into a room that holds a Grab-All ITEM boss (a box that just sits there,
    // not a monster that dies) — blindly `get` it. Fires on every entry; a harmless
    // no-op when the box isn't currently there.
    private void FireItemBossGrabOnEntry(Game.Map.RoomKey room)
    {
        foreach (Models.Profile.BossDef def in Bosses.Resolve())
        {
            if (!def.GrabAll) continue;
            if (BossGrabClassifier.Classify(GameData, def) != Game.Inventory.BossGrabKind.Item) continue;
            if (!BossDefRoomsContain(def, room)) continue;
            if (AutoModeController.Blocks("Boss Grab All", def.Name)) continue;
            string getName = BossGrabClassifier.ItemGetName(GameData, def.MatchName) ?? def.MatchName.Trim();
            SendGameCommand($"get {getName}");
            Log.Info("GrabAll", $"entered {room} — grabbing item boss '{def.Name}'");
        }
    }

    private static bool BossDefRoomsContain(Models.Profile.BossDef def, Game.Map.RoomKey key)
    {
        foreach (string wire in def.Rooms)
            if (Game.Map.RoomKey.TryParseWire(wire, out Game.Map.RoomKey k) && k == key) return true;
        return false;
    }

    // A self-buff of ours was just CAST (fired from StartSelfBuffTimer, after the cast
    // reached the wire). If it's the configured mana-regen roll spell (nature tap /
    // mana flux, a code-145 rolled affect — not a HoT like chaos surge), hand it to the
    // reroll engine. On Paradigm the engine reads abil 145; on Stock it waits for the
    // next observed passive mana tick. Either way it rerolls a bad value. Keyed to the
    // cast (not the AppliedMessage confirm) because a roll spell confirms via the shared
    // "mana regenerating" condition, which never maps back to the specific spell — so a
    // confirm-keyed reroll never fired at all (paradigm-20260830-110918).
    private void OnSelfBuffCastForReroll(string shortCode)
    {
        if (string.IsNullOrWhiteSpace(shortCode)) return;

        if (ManaRegenRerollSlot()?.Spell?.Trim() is not { Length: > 0 } maRegen) return;
        if (!string.Equals(maRegen, shortCode.Trim(), StringComparison.OrdinalIgnoreCase)) return;

        ManaRegen.OnRollSpellLanded(maRegen);
    }

    // The unified-list slot that drives mana-regen rerolling: a slot whose spell is a
    // code-145 rolled regen-rate spell (nature tap / mana flux / prfl). One per
    // character; null when none is configured. (The reroll config — threshold / count /
    // infinite — rides on this slot.) NOT gated on CastOnSelf: these roll spells are
    // self-only casts (they can't target others), so the CASTER always receives the
    // roll whenever the slot fires — rerolling must not hinge on a target flag the user
    // may have left on whole-party (report paradigm-20260909-113655).
    private Models.Profile.BuffSlot? ManaRegenRerollSlot()
    {
        if (Profile.Current?.PartyBuffs is not { } buffs) return null;
        foreach (Models.Profile.BuffSlot s in buffs.Slots)
            if (!string.IsNullOrWhiteSpace(s.Spell) && IsManaRegenRollSpell(s.Spell.Trim()))
                return s;
        return null;
    }

    // A reroll-config edit may now warrant rerolling the roll spell that's already up
    // (report paradigm-20260909-113655: user bumped flux 0→20 expecting the active -2 to
    // reroll). Only re-roll a spell that is CURRENTLY active — we're improving a live
    // roll, not spawning a fresh cast. A roll spell is self-only, so its timer is
    // matched by cast code alone.
    public void ReconsiderManaRegenRerollAfterConfigChange()
    {
        if (ManaRegenRerollSlot()?.Spell?.Trim() is not { Length: > 0 } shortCode) return;
        bool active = CastDirector.SnapshotActiveBuffs()
            .Any(t => string.Equals(t.Short, shortCode, System.StringComparison.OrdinalIgnoreCase));
        if (!active) return;
        ManaRegen.ReconsiderActiveRoll(shortCode);
    }

    // The Stock tick inputs for the configured mana-regen roll spell: level, stats,
    // magery, worn +ManaRgn%, the spell's level-scaled roll range and the meditate tick
    // (the unscaled base). Null before the first stat parse, for a non-caster, or when
    // no roll spell is configured — the reroller then can't read a roll back.
    private Game.Spells.StockManaRollContext? StockManaRollContext()
    {
        if (ManaRegenRerollSlot()?.Spell?.Trim() is not { Length: > 0 } code) return null;
        return StockManaRollContextFor(code);
    }

    private Game.Spells.StockManaRollContext? StockManaRollContextFor(string spellCode)
    {
        if (!Stats.HasParsed) return null;
        if (Spellbook.FindByCastCode(spellCode.Trim()) is not { } spell) return null;
        if (!Game.Spells.ManaRegenReroller.IsRollSpell(spell.Formula)) return null;
        GameRow? classRow = GameData.FindRowByName("Classes", PlayerStats.Class);
        int mageryType = RowInt(classRow, "MageryType");
        if (mageryType == 0) return null;
        int level = System.Math.Max(1, PlayerStats.Level);
        (long a, long b) = Game.Spells.SpellCalculator.AffectMagnitude(spell.Formula, level);
        var worn = Game.Calculators.CharacterCalculator
            .AggregateEquipmentStats(Inventory.Snapshot.EquippedItems, GameData).Totals;
        // The `stat` screen counted the gear worn when it was read; a gear-set swap
        // since (a meditate / mana set) changes the stats the tick uses, so swap that
        // gear's stat bonuses for what's worn now.
        (int atInt, int atWil, int atCha) = _statScreenGearStats ?? (worn.PlusIntellect, worn.PlusWillpower, worn.PlusCharm);
        Game.Calculators.ManaRegenBreakpointCalculator.Inputs inputs = new(
            Level: level, MageryType: mageryType,
            Intellect: PlayerStats.Intellect - atInt + worn.PlusIntellect,
            Willpower: PlayerStats.Willpower - atWil + worn.PlusWillpower,
            MageryLevel: RowInt(classRow, "MageryLVL"),
            GearRegenPercent: worn.MpRegenPercent,
            Realm: GameData.ActiveRealm, Charm: PlayerStats.Charm - atCha + worn.PlusCharm);
        int meditate = Game.Calculators.CharacterCalculator.CalcManaRegen(
            level, inputs.Intellect, inputs.Willpower, inputs.Charm, mageryType, inputs.MageryLevel,
            0, isMeditating: true, inputs.Realm);
        return new Game.Spells.StockManaRollContext(inputs, (int)System.Math.Min(a, b), (int)System.Math.Max(a, b),
            meditate, PlayerState.MaxMa > 0 && PlayerState.Ma < PlayerState.MaxMa,
            GearSettled: DateTimeOffset.Now - _wornChangedAt >= WornSettleTime);
    }

    // The worn gear's INT / WIL / CHA bonuses when `stat` was last read, and when the
    // worn list last changed — the Stock reroll's defence against gear-set swaps.
    private (int Int, int Wil, int Cha)? _statScreenGearStats;
    private string _wornSignature = string.Empty;
    private DateTimeOffset _wornChangedAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan WornSettleTime = TimeSpan.FromSeconds(3);

    private void NoteStatScreenGear()
    {
        var worn = Game.Calculators.CharacterCalculator
            .AggregateEquipmentStats(Inventory.Snapshot.EquippedItems, GameData).Totals;
        _statScreenGearStats = (worn.PlusIntellect, worn.PlusWillpower, worn.PlusCharm);
    }

    private void NoteWornChange()
    {
        string signature = string.Join('|', Inventory.Snapshot.EquippedItems.Select(e => e.Name));
        if (signature == _wornSignature) return;
        _wornSignature = signature;
        _wornChangedAt = DateTimeOffset.Now;
    }

    // The step (room + direction) whose pre-move gear already went out, per mover — the
    // ready check runs again while a sneak settles, and a repeat would resend the
    // swap before the first confirmed. Cleared as the move goes out.
    private string? _walkerPreMoveGearFor;
    private string? _loopPreMoveGearFor;

    private void PreMoveGearOnce(ref string? doneFor, Game.Map.Direction? direction)
    {
        string key = $"{RoomTracker.State.CurrentRoom}:{direction}";
        if (doneFor == key) return;
        doneFor = key;
        if (NextPlannedRoomForEquip(direction) is { } next)
            AutoEquip.OnAboutToEnterRoom(next);
        Combat.PrepBackstabForMove();
    }

    // True when the character's race or class carries ShadowRest (ability 1103, a
    // Paradigm ability): resting keeps its stealth (GAME_MECHANICS "ShadowRest").
    private bool CharacterHasShadowRest() =>
        Stats.HasParsed
        && ((GameData.FindRowByName("Classes", PlayerStats.Class) is { } classRow
                && Game.GameData.AbilityNames.HasShadowRest(classRow))
            || (GameData.FindRowByName("Races", PlayerStats.Race) is { } raceRow
                && Game.GameData.AbilityNames.HasShadowRest(raceRow)));

    // The Stock reroll's tick inputs, for the bug report.
    public string DescribeStockManaRollContext() => StockManaRollContext() is { } c
        ? $"level {c.Inputs.Level}, magery type {c.Inputs.MageryType} tier {c.Inputs.MageryLevel}, " +
          $"INT {c.Inputs.Intellect} WIL {c.Inputs.Willpower} CHA {c.Inputs.Charm}, worn ManaRgn {c.Inputs.GearRegenPercent}%, " +
          $"meditate (base) tick {c.MeditateTick}, roll range {c.RollMin}..{c.RollMax}, mana below max {c.ManaBelowMax}"
        : "(unknown — no roll spell configured, or stats / class not known yet)";

    // What each Stock natural-tick amount needs from the roll, for the Add-buff dialog:
    // the tick is truncated, so only these step values change what you're paid. Null
    // when the spell's context isn't known yet.
    public string? ManaRegenTickSteps(string? spellCode)
    {
        if (string.IsNullOrWhiteSpace(spellCode)) return null;
        if (StockManaRollContextFor(spellCode) is not { } ctx) return null;
        Game.Calculators.ManaRegenBreakpointCalculator.Result r =
            Game.Calculators.ManaRegenBreakpointCalculator.Compute(ctx.Inputs, ctx.RollMin, ctx.RollMax);
        var parts = new List<string> { $"{r.WorstTick} MP/tick at worst" };
        foreach (Game.Calculators.ManaRegenBreakpointCalculator.Breakpoint bp in r.Breakpoints)
            parts.Add($"{bp.Tick} from {bp.RollValueNeeded}");
        return string.Join(" · ", parts);
    }

    // Stock thresholds used to be a desired mana tick; they're now the rolled percent,
    // the same unit as Paradigm. Convert a saved tick threshold once, to the smallest
    // roll that pays that tick, as soon as the character's tick inputs are known.
    public void ConvertLegacyStockRerollThreshold(Models.Profile.BuffSlot slot)
    {
        if (slot.RerollThresholdIsRoll || slot.RerollThreshold is not { } tick) return;
        if (GameData.ActiveRealm == Game.RealmType.ParaMud)
        {
            slot.RerollThresholdIsRoll = true;   // Paradigm's was always the roll
            return;
        }
        if (slot.Spell?.Trim() is not { Length: > 0 } code || StockManaRollContextFor(code) is not { } ctx) return;
        Game.Calculators.ManaRegenBreakpointCalculator.Result r =
            Game.Calculators.ManaRegenBreakpointCalculator.Compute(ctx.Inputs, ctx.RollMin, ctx.RollMax);
        int roll = tick <= r.WorstTick ? ctx.RollMin
            : r.Breakpoints.FirstOrDefault(bp => bp.Tick >= tick) is { Tick: > 0 } bp ? bp.RollValueNeeded
            : ctx.RollMax;
        slot.RerollThreshold = roll;
        slot.RerollThresholdIsRoll = true;
        Log.Info(Game.Spells.ManaRegenReroller.LogCategory,
            $"converted the {code} reroll threshold from a {tick} MP tick to a roll of {roll}");
        Profile.Save();
    }

    // The level-scaled range a mana-regen roll spell can roll at the character's current
    // level — on Paradigm the reroll threshold is compared against this rolled value
    // (its `abil 145` spells contribution), so the Add-buff dialog's threshold box spans
    // exactly it, negatives included. Null when the spell isn't a resolvable roll spell.
    public (int Min, int Max)? ManaRegenRollRange(string? spellCode)
    {
        if (string.IsNullOrWhiteSpace(spellCode)) return null;
        if (Spellbook.FindByCastCode(spellCode.Trim()) is not { } spell) return null;
        if (!Game.Spells.ManaRegenReroller.IsRollSpell(spell.Formula)) return null;
        (long min, long max) = Game.Spells.SpellCalculator.AffectMagnitude(
            spell.Formula, System.Math.Max(1, PlayerStats.Level));
        return ((int)System.Math.Min(min, max), (int)System.Math.Max(min, max));
    }

    // The character's natural passive mana-regen per 30 s tick — level / stats /
    // magery with worn +ManaRgn% folded in, NOT meditating — the "mana gained per
    // tick" the Buff Watchdog shows against its per-tick maintenance cost so you can
    // see at a glance whether a buff set is self-sustaining. Deliberately excludes any
    // mana-regen roll spell (nature tap / flux): its magnitude is a variable roll, and
    // the spell itself is already counted on the maintenance side. Uses the same
    // engine formula (CharacterCalculator.CalcManaRegen) the Level Projection grid
    // trusts. Null for a non-caster (mageryType 0) or before the first stat parse.
    public int? PassiveManaRegenTick()
    {
        if (!Stats.HasParsed) return null;
        GameRow? classRow = GameData.FindRowByName("Classes", PlayerStats.Class);
        int mageryType = RowInt(classRow, "MageryType");
        if (mageryType == 0) return null;   // non-caster: no mana pool worth planning
        int mageryLevel = RowInt(classRow, "MageryLVL");
        int gearRegen = Game.Calculators.CharacterCalculator
            .AggregateEquipmentStats(Inventory.Snapshot.EquippedItems, GameData).Totals.MpRegenPercent;
        return Game.Calculators.CharacterCalculator.CalcManaRegen(
            System.Math.Max(1, PlayerStats.Level), PlayerStats.Intellect, PlayerStats.Willpower,
            PlayerStats.Charm, mageryType, mageryLevel, gearRegen, isMeditating: false, GameData.ActiveRealm);
    }

    private static int RowInt(GameRow? row, string property)
    {
        if (row is not GameRow el) return 0;
        return el.TryGetProperty(property, out GameValue v)
            && v.ValueKind == System.Text.Json.JsonValueKind.Number
            && v.TryGetInt32(out int n) ? n : 0;
    }

    // Dump the character's configured buff plan (the unified list) to the program log
    // on profile load / edit, so a "my buffs aren't working" report shows exactly how
    // they're set up — target(s), recast lead, and any per-slot conditions.
    private void LogBuffConfiguration(Models.Profile.CharacterProfile profile)
    {
        if (profile.PartyBuffs is not { Slots.Count: > 0 } buffs)
        {
            Log.Info("Buffs", "Buff plan: none configured.");
            return;
        }

        Log.Info("Buffs", $"Buff plan — {buffs.Slots.Count} slot(s):");
        int n = 0;
        foreach (Models.Profile.BuffSlot s in buffs.Slots)
        {
            n++;
            if (string.IsNullOrWhiteSpace(s.Spell)) { Log.Info("Buffs", $"  {n}. (empty)"); continue; }

            System.Collections.Generic.List<string> who = new();
            bool wholeParty = IsPartyWideBuff(s.Spell);
            if (s.CastOnSelf) who.Add("self");
            if (s.WholePartyOn && wholeParty)
            {
                who.Add("party-wide");
                who.Add(s.CastSolo ? "solo" : "party-only");
            }
            else if (wholeParty)
            {
                who.Add("off");
            }
            if (s.AllMembers) who.Add("all-members");
            else if (s.Targets.Count > 0) who.Add(string.Join("+", s.Targets));

            System.Collections.Generic.List<string> cond = new();
            if (s.OnlyWhenHpFull) cond.Add("hp-full");
            if (s.OnlyWhenMaFull) cond.Add("ma-full");
            if (s.OnlyWhenDark) cond.Add("only-dark");
            if (s.CastBeforeRestingForMana) cond.Add("pre-rest");
            cond.Add($"ma>={s.BlessIfAboveMa}");
            if (s.BlessWhileResting) cond.Add("while-resting");
            if (s.BlessDuringCombat) cond.Add("in-combat");
            if (s.RerollCount > 0) cond.Add($"reroll<{s.RerollThreshold?.ToString() ?? "-"} x{s.RerollCount}");

            string target = who.Count > 0 ? string.Join("/", who) : "no target";
            string condStr = cond.Count > 0 ? $" [{string.Join(", ", cond)}]" : string.Empty;
            Log.Info("Buffs", $"  {n}. {s.Spell.Trim()} → {target}, recast@{s.RecastMarginSec}s{condStr}");
        }
    }

    // The unified-list "only when dark" light spell the auto-light system casts on
    // entering a dark room — a CastOnSelf slot flagged OnlyWhenDark. Null when none.
    private string? RoomLightSlotSpell()
    {
        if (Profile.Current?.PartyBuffs is not { } buffs) return null;
        foreach (Models.Profile.BuffSlot s in buffs.Slots)
            if (s.CastOnSelf && s.OnlyWhenDark && !string.IsNullOrWhiteSpace(s.Spell))
                return s.Spell!.Trim();
        return null;
    }

    // Total illumination the character's configured buffs would add if their light
    // spells were up — every Buff Watchdog slot whose spell grants light (an
    // Illu/RoomIllu buff or a light-ball), summed. Feeds the ROOM INFO "Your Illu"
    // projection alongside worn-gear illumination. Buff slots store the 4-letter
    // cast code, so resolve each to its spell name (what RoomLightSpellResolver
    // matches on) before the illu lookup; non-light spells contribute 0.
    public int ConfiguredLightSpellIllu()
    {
        if (Profile.Current?.PartyBuffs is not { } buffs) return 0;
        int total = 0;
        foreach (Models.Profile.BuffSlot s in buffs.Slots)
        {
            if (s.Spell?.Trim() is not { Length: > 0 } code) continue;
            string name = Spellbook.FindByCastCode(code)?.Name ?? code;
            total += RoomLightSpell.IlluForSpell(name);
        }
        return total;
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
    // configured mana-regen spell drop mana below that buff's own floor
    // (Models.Profile.BuffSlot.BlessIfAboveMa, a percent of max or a raw amount per
    // the Health tab's mana threshold mode)? An unknown cost is
    // treated as free. Returns false when the pool is unknown or the recast would
    // breach the floor.
    private bool CanAffordManaRegenReroll()
    {
        int maxMa = PlayerState.MaxMa;
        if (maxMa <= 0) return false;

        if (ManaRegenRerollSlot() is not { } slot || slot.Spell?.Trim() is not { Length: > 0 } shortCode) return false;

        int cost = Spellbook.ManaCostOf(shortCode) ?? 0;
        int floor = Game.Health.PoolThreshold.Resolve(
            ReadSection<Models.Profile.HealthSettings>(Profile.Current, "Health").MaThresholdMode,
            slot.BlessIfAboveMa, maxMa);
        return PlayerState.Ma - cost >= floor;
    }

    private Game.Spells.HealLineReader BuildHealLineReader()
    {
        IReadOnlyList<Game.Spells.HealSpell> heals = GameData.GetTable("Spells") is { } doc
            ? Game.Spells.HealLineReader.InstantHeals(doc)
            : Array.Empty<Game.Spells.HealSpell>();
        return new Game.Spells.HealLineReader(heals, Messages.Messages);
    }

    // Built on the first damage line after a set switch or a message edit: the
    // monster catalogue it reads is itself built on first use.
    private Game.Combat.OffRoundDamageLines? _offRoundDamage;

    private Game.Combat.OffRoundDamageLines OffRoundDamage()
    {
        if (_offRoundDamage is { } built) return built;
        built = new Game.Combat.OffRoundDamageLines(
            MonsterCatalog.All
                .SelectMany(static m => m.Attacks)
                .Where(static a => a.Type == 2 && a.Percent > 0 && a.Accuracy > 0)
                .Select(static a => a.Accuracy),
            Messages.Messages);
        Log.Debug("RoundClock",
            $"Room-spell damage rule built: {built.MonsterAttackTextCount} monster attack text(s) with no dealer named stay on the round.");
        return _offRoundDamage = built;
    }

    // True when a "… for N damage!" line is a room spell's damage or an effect paying
    // out, not a hit in a fight (OffRoundDamageLines). The one test the round clock,
    // the in-combat flag, the combat engine and Round Totals are all given.
    public bool IsRoomOrEffectDamage(string line) =>
        OffRoundDamage().IsOffRound(line, RoomTracker.State.CurrentRoom?.Spell ?? 0, MonsterHereCasts);

    // Whether a monster in the room we stand in casts the spell in an attack slot.
    private bool MonsterHereCasts(int spell) =>
        RoomClassifier.Current is { } here
        && here.Entities.Any(e => e.MonsterNumber is { } number && MonsterCatalog.Get(number)?.CastLevelFor(spell) > 0);

    // The spell on the room we stand in, when it keeps a rest from starting: it is
    // set to bar resting (Settings → Periodic Damage Room Spells; by default the
    // spells that damage on every tick) and nothing worn or held counters it
    // (RoomHazardIndex). Null in a room that isn't placed, so an unknown room rests
    // as it always did.
    public (int Number, string Name)? RoomSpellHurtingUs()
    {
        if (RoomTracker.State.CurrentRoom is not { Spell: > 0 } here) return null;
        if (!RoomSpellBarsResting(here.Spell)) return null;
        if (RoomSpellCounteredNow(here.Spell)) return null;
        return (here.Spell, SpellCatalog.GetSpellNameByNumber(here.Spell) ?? "room spell");
    }

    // The loaded character's choice for the spell, or the default for its class.
    // Read off the character tier like the Health tab's rest settings, each time it
    // is asked, so a save in Settings is in effect at the next rest decision.
    public bool RoomSpellBarsResting(int spell) =>
        RoomSpellDamage.BarsResting(spell, () => ReadSection<Models.Profile.PeriodicDamageRoomSpellSettings>(
            Profile.Current, Models.Profile.PeriodicDamageRoomSpellSettings.TabKey).BarsResting);

    public bool RoomSpellCounteredNow(int spell) =>
        RoomHazards.HazardForSpell(spell) is { } hazard && hazard.IsCounteredNow(IsItemWorn, IsItemCarried);

    // The damaging room spells of the loaded game data, for Settings → Periodic
    // Damage Room Spells: every tick first, then by how many rooms carry each.
    public IReadOnlyList<Game.Map.PeriodicDamageRoomSpell> PeriodicDamageRoomSpells() =>
        RoomSpellDamage.Readings
            .Select(entry => new Game.Map.PeriodicDamageRoomSpell(
                entry.Key,
                SpellCatalog.GetSpellNameByNumber(entry.Key) ?? "room spell",
                entry.Value,
                RoomHazards.HazardForSpell(entry.Key)?.DescribeCounters(ItemNames.GetName) ?? string.Empty,
                RoomSpellDamage.RoomsOf(entry.Key)))
            .OrderByDescending(static s => s.Reading.Kind)
            .ThenByDescending(static s => s.Rooms.Count)
            .ThenBy(static s => s.Number)
            .ToList();

    private bool IsItemWorn(int itemId)
    {
        foreach (Game.Inventory.EquippedItem e in Inventory.Snapshot.EquippedItems)
            if (ItemNames.FindByName(e.Name) == itemId) return true;
        return false;
    }

    // A negating item is usable on a route when it is on, or when the client will
    // put it on before the step (RoomSpellCounterWear.WillWear: the setting, the
    // master switch, and whether this character can wear it). What a route is
    // planned on and what the wear does are the same test.
    //
    // A route search asks this for every hazard exit it weighs, and the answer
    // reads the settings, the item's record and the character's build each time.
    // So an answer is kept for the search: until the counter's own state, the
    // master switch, the worn list or the setting changes, and for no longer than
    // NegatorUsableKeptFor in any case (the build and the data set have no one
    // event to drop it on; a search is over in far less).
    private bool NegatingItemUsable(int itemId)
    {
        long now = Environment.TickCount64;
        if (now - _negatorUsableSince > NegatorUsableKeptFor)
        {
            _negatorUsable.Clear();
            _negatorUsableSince = now;
        }
        if (!_negatorUsable.TryGetValue(itemId, out bool usable))
            _negatorUsable[itemId] = usable = IsItemWorn(itemId) || CounterWear.WillWear(itemId);
        return usable;
    }

    private const long NegatorUsableKeptFor = 500;
    private readonly Dictionary<int, bool> _negatorUsable = new();
    private long _negatorUsableSince;

    // The setting was saved on the Periodic Damage Room Spells tab: routes are
    // planned on the new answer, and a counter needed in the room stood in goes on.
    public void NoteCounterWearSettingSaved()
    {
        _negatorUsable.Clear();
        CounterWear.Recheck();
    }

    // A room-spell counter as RoomSpellCounterWear weighs it, null for an item the
    // game data has no wearable record of.
    private Game.Inventory.RoomSpellCounterItem? DescribeCounterItem(int itemId) =>
        ItemNames.GetName(itemId) is { Length: > 0 } name ? DescribeGear(itemId, name) : null;

    // The pieces worn in a slot now, both of a pair (fingers, wrists): what a
    // counter going on there could push out, and whether a place is free.
    private IReadOnlyList<Game.Inventory.RoomSpellCounterItem> WornPiecesIn(Models.Profile.EquipmentSlot slot)
    {
        List<Game.Inventory.RoomSpellCounterItem> pieces = new();
        foreach (Game.Inventory.EquippedItem e in Inventory.Snapshot.EquippedItems)
        {
            if (ResolveEquipItemSlot(e.Name) is not { } worn
                || !Game.Inventory.EquipmentManager.SharePlaces(worn, slot)) continue;
            if (DescribeGear(ItemNames.FindByName(e.Name) ?? 0, e.Name) is { } piece) pieces.Add(piece);
        }
        return pieces;
    }

    // By number when the number is in hand: two items can share a name, and the
    // name lookup would weigh the wrong one.
    private Game.Inventory.RoomSpellCounterItem? DescribeGear(int itemId, string name)
    {
        GameRow? found = itemId > 0
            ? GameData.FindRowByNumber("Items", itemId)
            : GameData.FindRowByName("Items", name);
        if (found is not GameRow row
            || Game.Inventory.EquipmentSlotMap.SlotForItem(row) is not { } slot)
            return null;
        static int Int(GameRow row, string field) =>
            row.TryGetProperty(field, out GameValue v) && v.TryGetInt32(out int n) ? n : 0;
        // Before the stat screen is read the class and level aren't known, and an
        // unknown build is not restricted (the rule the route gates follow): the
        // game's own refusal settles it then.
        bool canWear = !Stats.HasParsed || CanCharacterEquipItem(name);
        return new Game.Inventory.RoomSpellCounterItem(
            itemId, name, slot, Int(row, "ArmourClass"), Int(row, "DamageResist"), canWear);
    }

    // Find the active set's Models.GameData.MessageRecord
    // for a spell — by Spells#N link first, then by name. Returns
    // null when the catalogue has no record for the spell.
    private Models.GameData.MessageRecord? FindSpellMessage(int spellNumber, string spellName)
    {
        SpellMessageIndex index = SpellMessages();
        if (index.ByLink.TryGetValue(spellNumber, out Models.GameData.MessageRecord? linked)) return linked;

        string target = spellName.Trim();
        if (target.Length == 0) return null;   // link-only lookup — never name-match ""
        return index.ByName.TryGetValue(target, out List<Models.GameData.MessageRecord>? named) ? named[0] : null;
    }

    // The message catalogue by the spell a record is linked to (the first record
    // linking each spell) and by record name (every record of a name, in catalogue
    // order). Looking a spell's message up used to scan all ~1,100 records, twice,
    // and the matcher build does that for every spell: over a second of the launch
    // freeze. Rebuilt after any change to the catalogue.
    private sealed record SpellMessageIndex(
        Dictionary<int, Models.GameData.MessageRecord> ByLink,
        Dictionary<string, List<Models.GameData.MessageRecord>> ByName);

    private SpellMessageIndex? _spellMessages;

    private SpellMessageIndex SpellMessages()
    {
        if (_spellMessages is { } built) return built;
        Dictionary<int, Models.GameData.MessageRecord> byLink = new();
        Dictionary<string, List<Models.GameData.MessageRecord>> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (Models.GameData.MessageRecord m in Messages.Messages)
        {
            if (m.Links is not null)
                foreach (Models.GameData.GameDataLink link in m.Links)
                    if (string.Equals(link.Table, "Spells", StringComparison.OrdinalIgnoreCase))
                        byLink.TryAdd(link.Number, m);
            string name = m.Name.Trim();
            if (!byName.TryGetValue(name, out List<Models.GameData.MessageRecord>? sameName))
                byName[name] = sameName = new();
            sameName.Add(m);
        }
        return _spellMessages = new SpellMessageIndex(byLink, byName);
    }

    // Compile a predicate that recognises a spell's own player-facing line —
    // the hazard-counter provisioner watches for the lapse-damage prompt (a
    // hazard's LapseSpell) and the swig confirmation (its BuffSpell). The desert
    // lines ship with no {s}/{damage} placeholder, so CasterMessageMatcher
    // declines them; those fall back to a literal case-insensitive Contains.
    // Null when the active set carries no message for the spell — the reactive
    // path then stays inert and only the predictive timer keeps the buff up.
    private Func<string, bool>? BuildSpellLinePredicate(int spellNumber)
        => spellNumber <= 0 ? null : LinePredicateFor(FindSpellMessage(spellNumber, string.Empty));

    // A line predicate for a message record's player-facing line: a placeholder template
    // compiles to a CasterMessageMatcher; a plain literal (no {s}/{damage}) falls back to
    // a case-insensitive Contains. Null when the record is missing or carries no text.
    private static Func<string, bool>? LinePredicateFor(Models.GameData.MessageRecord? rec)
    {
        if (rec is null) return null;
        string text = !string.IsNullOrWhiteSpace(rec.CasterMessage) ? rec.CasterMessage : rec.TargetMessage;
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (Game.Spells.CasterMessageMatcher.TryCreate(text) is { } matcher)
            return line => matcher.TryMatch(line, out _);
        string literal = text.Trim();
        return line => line.Contains(literal, StringComparison.OrdinalIgnoreCase);
    }

    // A line predicate recognising an item's use-spell caster message — the line you see
    // when a `use <item>` actually fires. The stock use-counter arms on the send and
    // counts only when this confirms, so a bonked / blocked use burns nothing.
    private Func<string, bool>? BuildItemUseLinePredicate(int itemNumber)
        => LinePredicateFor(FindItemMessage(itemNumber));

    // Find the active set's Models.GameData.MessageRecord for an
    // item — the line YOU see when the item procs / is used. Resolution order:
    // (1) the item's CAST SPELL record (Spells#N via CastsSp) — the canonical home
    // for a casting item's on-use / proc wording, shared across every item casting
    // that spell; (2) a legacy Items#N-linked record (worn trinkets with a
    // wield/remove message that cast nothing); (3) the item's resolved name. Returns
    // null when no record anchors to the item. Mirrors FindSpellMessage.
    private Models.GameData.MessageRecord? FindItemMessage(int itemNumber)
    {
        if (Game.GameData.ItemCastSpells.PrimaryCastSpell(GameData, itemNumber) is int spell)
        {
            foreach (Models.GameData.MessageRecord m in Messages.Messages)
            {
                if (m.Links is null) continue;
                foreach (Models.GameData.GameDataLink link in m.Links)
                    if (string.Equals(link.Table, "Spells", StringComparison.OrdinalIgnoreCase)
                        && link.Number == spell)
                        return m;
            }
        }

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

    // Every spell of ours CombatSession can recognise off its game-data caster
    // message, so a cast tallies its own damage row instead of counting as a melee
    // swing. The configured attack slots come first — the first is where a resisted
    // cast goes before any spell has landed (CombatSessionTracker.ResolvePendingSpellMiss)
    // — then every other spell the class can learn whose message carries a damage
    // figure, so a hand-cast spell gets its row too. A spell in two slots is added once.
    private IReadOnlyList<Game.Spells.SpellLineMatcher> OwnSpellMatchers()
    {
        Models.Profile.CombatSettings combat =
            ReadSection<Models.Profile.CombatSettings>(Profile.Current, "Combat");
        List<Game.Spells.SpellLineMatcher> list = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        // Each spell by its name and its short code, the first spell to carry either:
        // the same spell a scan of the spellbook for that text would stop at.
        Dictionary<string, Game.Spells.KnownSpell> byNameOrShort = new(StringComparer.OrdinalIgnoreCase);
        foreach (Game.Spells.KnownSpell spell in Spellbook.Available)
        {
            byNameOrShort.TryAdd(spell.Name.Trim(), spell);
            byNameOrShort.TryAdd(spell.Short.Trim(), spell);
        }
        Add(combat.NormalAttackSpell?.SpellName);
        Add(combat.AlternateAttackSpell?.SpellName);
        Add(combat.MultiAttackSpell?.SpellName);
        Add(combat.MultiAttack2Spell?.SpellName);
        Add(combat.DrainSpell?.SpellName);
        foreach (Game.Spells.KnownSpell s in Spellbook.Available)
            Add(s.Name);
        return list;

        void Add(string? spellName)
        {
            if (string.IsNullOrWhiteSpace(spellName)) return;
            if (!byNameOrShort.TryGetValue(spellName.Trim(), out Game.Spells.KnownSpell spell)) return;
            string name = spell.Name.Trim();
            if (seen.Add(name))
                list.AddRange(OwnSpellLines(spell).Select(l => l with { Name = name }));
        }
    }

    // One spell's damage-line matchers, cached by spell number since the whole class
    // list is rebuilt on every refresh. Cleared on a game-data set swap, which can
    // change the messages.
    //   * Its caster line, when that carries the damage. A spell's own record can lack
    //     the damage line while a record of the same name has it (Paradigm hail of
    //     stones #5080 has no caster line; #772 carries "Your foes are battered by a
    //     hail of stones for {damage} damage!"), so every same-named damage wording
    //     counts — only ours follows our own cast.
    //   * The damage line of each spell it chains to, as a follow-up (necromantic
    //     bolt's "{target}'s life is drained for {damage} damage!").
    private readonly Dictionary<int, IReadOnlyList<Game.Spells.SpellLineMatcher>> _ownSpellLineCache = new();

    private IReadOnlyList<Game.Spells.SpellLineMatcher> OwnSpellLines(Game.Spells.KnownSpell spell)
    {
        if (_ownSpellLineCache.TryGetValue(spell.Number, out IReadOnlyList<Game.Spells.SpellLineMatcher>? cached))
            return cached;
        List<Game.Spells.SpellLineMatcher> lines = new();
        HashSet<string> templates = new(StringComparer.Ordinal);
        string? own = FindSpellMessage(spell.Number, spell.Name)?.CasterMessage;
        if (HasDamageSlot(own)) AddLine(own!, followUp: false);
        else if (SpellMessages().ByName.TryGetValue(spell.Name.Trim(), out List<Models.GameData.MessageRecord>? sameName))
            foreach (Models.GameData.MessageRecord m in sameName)
                if (HasDamageSlot(m.CasterMessage))
                    AddLine(m.CasterMessage, followUp: false);
        // The whole chain, not just the next link: elemental fury casts lightning,
        // which casts fire, which casts ice, each with its own line (GAME_MECHANICS
        // "Damage lines — who hit whom"). The visited set stops a chain that loops.
        HashSet<int> chain = new() { spell.Number };
        Queue<int> pending = new();
        pending.Enqueue(spell.Number);
        while (pending.Count > 0)
        {
            if (SpellFormulaFor(pending.Dequeue()) is not { } formula) continue;
            foreach (Game.Spells.SpellAbility ability in formula.Abilities)
            {
                if (ability.Code != 151 || ability.Value <= 0 || !chain.Add(ability.Value)) continue;
                pending.Enqueue(ability.Value);
                if (FindSpellMessage(ability.Value, string.Empty)?.CasterMessage is { } chained
                    && HasDamageSlot(chained))
                    AddLine(chained, followUp: true);
            }
        }
        _ownSpellLineCache[spell.Number] = lines;
        return lines;

        void AddLine(string template, bool followUp)
        {
            if (templates.Add(template) && Game.Spells.CasterMessageMatcher.TryCreate(template) is { } matcher)
                lines.Add(new Game.Spells.SpellLineMatcher(spell.Name.Trim(), matcher, followUp,
                    HitsRoom: !followUp && Game.Combat.DebuffTargeting.IsAreaEnemy(spell.Targets)));
        }
    }

    private static bool HasDamageSlot(string? template)
        => template is not null
           && (template.Contains("{d}") || template.Contains("{dmg}") || template.Contains("{damage}"));

    // Resolve one attack-spell slot name to the lines that show its cast landing:
    // match the live spellbook by full name (the form a slot stores) or 4-letter cast
    // code, and take the same damage wordings Session Stats recognises it by
    // (OwnSpellLines). A spell's own record can lack the damage line, with it on a
    // same-named record instead (Paradigm hail of stones): matched on its own record
    // alone, such a spell's cast never counted toward Max casts.
    // A chained spell's follow-up line is part of the same cast, so it's left out.
    // Falls back to the own record's caster line when no damage wording is recorded.
    // Empty when the name is blank, unknown to the spellbook, or has no usable line.
    private IReadOnlyList<Game.Spells.CasterMessageMatcher> AttackSpellMatchersFor(string? spellName)
    {
        if (string.IsNullOrWhiteSpace(spellName)) return Array.Empty<Game.Spells.CasterMessageMatcher>();
        string target = spellName.Trim();
        foreach (Game.Spells.KnownSpell s in Spellbook.Available)
        {
            if (!string.Equals(s.Name.Trim(), target, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(s.Short.Trim(), target, StringComparison.OrdinalIgnoreCase))
                continue;
            List<Game.Spells.CasterMessageMatcher> lines = OwnSpellLines(s)
                .Where(l => !l.FollowUp).Select(l => l.Matcher).ToList();
            if (lines.Count == 0
                && Game.Spells.CasterMessageMatcher.TryCreate(FindSpellMessage(s.Number, s.Name)?.CasterMessage) is { } own)
                lines.Add(own);
            return lines;
        }
        return Array.Empty<Game.Spells.CasterMessageMatcher>();
    }

    // CombatManager.ResolveAttackSpellMatchers' wiring — confirms an attack-spell cast
    // (MaxCastsPerRoom tally) whose damage line uses the spell's own (often
    // third-person) wording instead of the physical "You ... for N damage!" skeleton
    // (see that property's declaration comment). Keyed by cast-code and cached: this
    // runs off OnAttackCastConfirmed, on every UserHits / UserMisses line while a
    // spell round is in flight. Cleared alongside CombatSession's own
    // RefreshMatchers() — a profile load or game-data set swap can change which spell
    // a cast-code resolves to.
    private readonly Dictionary<string, IReadOnlyList<Game.Spells.CasterMessageMatcher>> _attackSpellMatcherCache =
        new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<Game.Spells.CasterMessageMatcher> ResolveAttackSpellMatchersCached(string spellCode)
    {
        if (_attackSpellMatcherCache.TryGetValue(spellCode, out IReadOnlyList<Game.Spells.CasterMessageMatcher>? cached))
            return cached;
        IReadOnlyList<Game.Spells.CasterMessageMatcher> matchers = AttackSpellMatchersFor(spellCode);
        _attackSpellMatcherCache[spellCode] = matchers;
        return matchers;
    }

    // Long cells of the active set that an old import left damaged (see
    // GameDataLongTextCheck); 0 for a sound set. For the bug report.
    public int ActiveSetDamagedCells { get; private set; }

    // A set imported before the Access reader was corrected keeps its damaged cells:
    // room command scripts, spawn lists and item sources with characters missing.
    // Nothing can mend them in place, so say so, once per time the set is made
    // active, and name the cure. The scan reads every long cell of two tables, so it
    // runs off the UI thread; the result is posted back.
    private void CheckActiveSetForImportDamage()
    {
        ActiveSetDamagedCells = 0;
        if (GameData.ActiveSet is not { } set) return;
        GameDataCache.Load load = GameData.ActiveLoad;
        System.Threading.Tasks.Task.Run(() =>
        {
            int damaged;
            try { damaged = GameDataLongTextCheck.CountDamagedCells(GameData); }
            catch (Exception ex)
            {
                Log.Warn("GameData", $"could not check set '{set}' for import damage ({ex.GetType().Name}: {ex.Message}).");
                return;
            }
            if (damaged == 0) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Another set by now, or this one imported again: its own scan answers for it.
                if (GameData.ActiveLoad != load) return;
                ActiveSetDamagedCells = damaged;
                Log.Warn("GameData",
                    $"set '{set}' was imported by an older version that damaged long text: {damaged} cell(s) "
                    + "have characters missing (monster spawn lists, item sources, room command scripts). "
                    + "Import its MDB again to repair it.");
                WriteTerminalNotice(
                    $"[Game data '{set}' was imported by an older MudPlay that damaged long text. "
                    + "Import its MDB again (Manage Game Data Sets) to repair it.]");
            });
        });
    }

    // The command that lists what the character has learned: `sp` for a mana class,
    // `pow` for a kai one (SpellListParser reads either).
    public string SpellListCommand => PlayerStats.MaxKai > 0 ? "pow" : "sp";

    // A new profile knows every spell its class can learn but not which of them this
    // character has: that comes only from the game's own list, which nothing asked
    // for. Until it was read the Buff Watchdog had no buff to offer and no way to say
    // why. Asked once per session, after a `stat` has named the class, and only while
    // nothing learned is known.
    private bool _spellListAsked;
    private void ReadSpellListIfNeverSeen()
    {
        if (_spellListAsked || Spellbook.ObtainedCount > 0 || Spellbook.ClassSpells.Count == 0) return;
        // Before the latch, so the read is still owed at the next stat screen.
        if (AutoModeController.Blocks("Info polls")) return;
        _spellListAsked = true;
        string command = SpellListCommand;
        Log.Info("Spellbook", $"no learned spells are known for this character — sending `{command}` to read them");
        SendGameCommand(command);
    }

    // Whether the character's pool is mana or kai, from its stat screen — how a custom
    // statline's %m reads when no MA= / KAI= label sits in front of it.
    private void NotePoolType(int maxMana, int maxKai)
    {
        if (maxKai > 0) PromptScanner.UnlabeledManaType = Game.ManaType.Kai;
        else if (maxMana > 0) PromptScanner.UnlabeledManaType = Game.ManaType.Mana;
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
    // lives in one place.
    public bool IsItemCarried(int itemId) => CountItemHeld(itemId) > 0;

    // Numeric alignment of every crosser for an "(Alignment: X to Y)" exit gate:
    // the controlling character always, plus each follower when we LEAD the party
    // through the gate together (whole-party — the game stops the party at the
    // tightest member). Each entry is the member's alignment value resolved from the
    // PlayerDatabase (who-title band → number via the confirmed ladder), or null
    // when we don't know it yet. MovementFilter routes around a gate a KNOWN member
    // can't cross and leaves an unknown member for the walker to halt on at the gate.
    private System.Collections.Generic.IReadOnlyList<int?> PartyAlignmentValues()
    {
        var vals = new System.Collections.Generic.List<int?> { Game.Calculators.AlignmentBands.ValueOf(Alignment.SelfAlignment) };
        if (PartyState.IsInParty && PartyState.SelfIsLeader)
            foreach (Game.PartyMember m in PartyState.Members)
            {
                if (m.IsSelf) continue;
                vals.Add(AlignmentValueOf(m.Name));
            }
        return vals;
    }

    private int? AlignmentValueOf(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : Game.Calculators.AlignmentBands.ValueOf(Players.Find(name!)?.Alignment);

    // A party member's name, or our own in game (party or solo): our cast at either is
    // a buff, never a swing at a monster, so the combat stats skip its negate / resist.
    private bool IsPartyMemberOrSelfName(string name) =>
        IsPartyMemberOrSelfName(name, IsPartyMemberName, PlayerStats.Name);

    internal static bool IsPartyMemberOrSelfName(string name, Func<string, bool> isPartyMember, string selfName)
    {
        if (isPartyMember(name)) return true;
        static string First(string n) => n.Trim().Split(' ')[0];
        return selfName.Trim().Length > 0 && name.Trim().Length > 0
            && string.Equals(First(name), First(selfName), StringComparison.OrdinalIgnoreCase);
    }

    // Does 'name' as it appears in a spell line (e.g. "casts hold person on Jroc")
    // name a current party member? Party names can be one or two words and the wire
    // line often uses just the first, so match the full name or its first token.
    private bool IsPartyMemberName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        // Members now carries a lone self row even when solo (PartyWindow self-
        // display); only treat a name as a party member when we're actually in a
        // party, so a "hold person on <self>" line while solo isn't misread.
        if (!PartyState.IsInParty) return false;
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

    // Coin counts in the words the game's own echoes use for them (the board's name
    // for its top coin), dearest first — what the transaction history keys coin by.
    private List<(string Currency, long Amount)> CoinWords(
        IReadOnlyDictionary<Models.Profile.CoinDenomination, long> coins)
    {
        List<(string, long)> words = new();
        foreach (Models.Profile.CoinDenomination coin in Enum.GetValues<Models.Profile.CoinDenomination>().Reverse())
            if (coins.TryGetValue(coin, out long count) && count > 0)
                words.Add((coin == Models.Profile.CoinDenomination.Runic && !string.IsNullOrWhiteSpace(Currency.RunicName)
                    ? Currency.RunicName : coin.ToString().ToLowerInvariant(), count));
        return words;
    }

    // "Name (map/room)" for the room the tracker currently sits in, or null when
    // position is unknown. Stamped onto transaction-ledger rows so a deposit
    // records which bank was used and a stash records which room hid the loot.
    private string? CurrentRoomLabel()
    {
        if (RoomTracker?.State.CurrentRoom is not { } room) return null;
        return $"{room.DisplayName} ({room.Key.Map}/{room.Key.Room})";
    }

    // How many of an item are in the pack, told by its name rather than its record
    // number. Two item records can share a name — "scroll of resist lightning" is
    // both #149 and #1993 — and the pack shows only the name, which resolves back to
    // one of the numbers. Counted by number, the other record reads as never
    // carried: the spell errand bought that scroll, never saw it arrive, and moved
    // on without reading it (report paradigm-20261005-091552). The game takes the
    // name in `read` either way.
    private int CountCarriedByName(int itemId)
    {
        if (ItemNames.GetName(itemId) is not { Length: > 0 } wanted) return 0;
        string key = ItemNameStore.Normalize(wanted);
        int count = 0;
        foreach (string entry in Inventory.Snapshot.CarriedItems)
        {
            (int qty, string name) = Game.Inventory.CountedCommand.SplitLeadingCount(entry);
            if (string.Equals(ItemNameStore.Normalize(name), key, StringComparison.OrdinalIgnoreCase)) count += qty;
        }
        return count;
    }

    // How many copies of itemId the current snapshot holds (carried + worn), each
    // name matched back to its Number. The key ring isn't counted here;
    // CountItemHeld adds it.
    private int CountItemCarried(int itemId)
    {
        Game.Inventory.InventorySnapshot snap = Inventory.Snapshot;
        int count = CountInPack(snap, itemId);
        foreach (Game.Inventory.EquippedItem worn in snap.EquippedItems)
            if (ItemNames.FindByName(worn.Name) == itemId) count++;
        return count;
    }

    private int CountInPack(Game.Inventory.InventorySnapshot snap, int itemId)
    {
        int count = 0;
        // A pile is one pack entry under its count ("50 orc-head"), so each entry
        // counts for its copies. Read as one item it under-reads the held total and
        // lets the auto-get MaxToGet cap collect past its limit.
        foreach (string entry in snap.CarriedItems)
        {
            (int qty, string name) = Game.Inventory.CountedCommand.SplitLeadingCount(entry);
            if (ItemNames.FindByName(name) == itemId) count += qty;
        }
        return count;
    }

    private int CountOnKeyRing(Game.Inventory.InventorySnapshot snap, int itemId)
    {
        int count = 0;
        if (snap.Keys is { } keys)
            foreach (string entry in keys)
            {
                (int quantity, string name) = Game.Inventory.InventorySnapshot.ParseKeyEntry(entry);
                if (ItemNames.FindByName(name) == itemId) count += quantity;
            }
        return count;
    }

    // How many copies of itemId the player holds, counting the key ring on top
    // of carried + worn. KEY-type items live in the dump's separate "You have
    // the following keys:" trailer (InventorySnapshot.Keys), not the pack, so
    // CountItemCarried alone under-reads them — which let the auto-get MaxToGet
    // cap collect past its limit. Backs AutoGetItemsManager's held-count seam.
    private int CountItemHeld(int itemId) =>
        CountItemCarried(itemId) + CountOnKeyRing(Inventory.Snapshot, itemId);

    // Copies in the pack or on the key ring, worn ones left out: what there is to
    // hand over in a trade. The game would take a worn copy too (GAME_MECHANICS
    // "Room-command refusals"); a walk doesn't strip what the user has on.
    private int CountItemUnworn(int itemId)
    {
        Game.Inventory.InventorySnapshot snap = Inventory.Snapshot;
        return CountInPack(snap, itemId) + CountOnKeyRing(snap, itemId);
    }

    // A monster that drops itemId every time, or null: where a trade's item comes
    // from, for the route card's note.
    private string? AlwaysDroppedBy(int itemId)
    {
        foreach (MonsterDropIndex.MonsterDrop d in MonsterDrops.DroppersOf(itemId))
            if (d.DropPercent >= 100) return d.MonsterName;
        return null;
    }

    // The nearest reachable room where a shortcut item can be obtained — a dropping
    // monster's lair, a shop that sells it, or a deterministic giver — for the "walk
    // to the source" leg of a shortcut pick. Null when nothing sources it reachably.
    // Flag-independent by design: an explicit shortcut pick IS the consent to go get
    // it, so the item's AutoObtainForPath flag doesn't gate this (unlike the auto
    // detour routers). Drop first (the amber-talisman case), then shop, then give.
    private Game.Map.RoomKey? ResolveShortcutItemSourceRoom(int itemId)
    {
        if (itemId <= 0) return null;
        if (RoomTracker.State.CurrentRoom?.Key is not { } cur) return null;
        System.Collections.Generic.IReadOnlyDictionary<Game.Map.RoomKey, int> dist =
            Bfs.ComputeDistancesFrom(cur, Movement);

        if (Game.Map.MonsterDropRouter.SelectNearestSpawn(
                DropSpawnsForItem(itemId), dist, out Game.Map.MonsterDropSpawn spawn, out _))
            return spawn.Room;

        if (NearestReachableRoom(ShopRoomsSellingItem(itemId), dist) is { } shop)
            return shop;

        var giveRooms = new System.Collections.Generic.List<Game.Map.RoomKey>();
        foreach (Game.Map.GiveSource g in GiveSources.Free(itemId)) giveRooms.Add(g.Room);
        return NearestReachableRoom(giveRooms, dist);
    }

    private static Game.Map.RoomKey? NearestReachableRoom(
        System.Collections.Generic.IEnumerable<Game.Map.RoomKey> rooms,
        System.Collections.Generic.IReadOnlyDictionary<Game.Map.RoomKey, int> distances)
    {
        Game.Map.RoomKey? best = null;
        int bestDistance = int.MaxValue;
        foreach (Game.Map.RoomKey r in rooms)
            if (distances.TryGetValue(r, out int d) && d < bestDistance)
            {
                bestDistance = d;
                best = r;
            }
        return best;
    }

    // Room keys of every shop in the live graph that stocks
    // itemId — the join of ShopStock (which
    // shops sell it) against RoomGraph (which rooms host those
    // shops). Only rooms present in the active graph can be walk targets, so shops
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

    // What picking a route card's gated route does about each of its gate items:
    // the source tail each requirement clause carries, and the keys it would trade
    // for. Each tail names what the walk that pick starts would really do: the
    // giver, shop or lair comes from the routers' own selection rules (shared
    // TrySelectGiver / TrySelectShop / SelectNearestSpawn) and their distances, so
    // it is the place the run visits and not a plausible guess.
    //
    // pickFetches is false for a pick that only walks somewhere and stops.
    // closedGates are the gate items the card's route goes round: its walk keeps
    // those gates closed, so a source is priced the same way.
    public GatePickSources PlanGatePick(
        IReadOnlyList<RouteRequirement> requirements,
        Game.Map.RoomKey source, Game.Map.RoomKey destination, bool pickFetches,
        IReadOnlyCollection<int>? closedGates = null)
        => GatePickSources.Build(
            requirements, pickFetches,
            keyHasOtherSource: DoorKeyHasUntradedSource,
            flaggedAutoObtain: IsAutoObtainForPath,
            giver: (id, offerTrades) => GiveSources.Choose(id, source, destination, offerTrades, closedGates),
            buyPhrase: id => PathItemShopPhrase(id, source, destination, closedGates),
            dropper: id => PathItemDropName(id, source),
            tradeNote: GiveSources.TradeNote);

    // For the RoutePick log line: what a pick of the gated route can fetch, and how
    // a key that is traded for is come by. Worked out from the indexes alone, with
    // no route search, since the plan may be running off the UI thread.
    public string GatePickLogNote(IReadOnlyList<RouteRequirement> requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        IReadOnlyList<int> fetched = SourceableGateItems(requirements);
        var notes = new List<string>();
        foreach (RouteRequirement req in requirements)
            if (req is { Kind: RouteRequirementKind.DoorKey, Carried: false, ItemIds.Count: 1 }
                && GiveSources.TradeNote(req.ItemIds[0]) is { } note)
                notes.Add($"{ItemNames.GetName(req.ItemIds[0]) ?? $"item #{req.ItemIds[0]}"}: {note}");
        string head = fetched.Count > 0
            ? $"a pick of it can fetch item(s) {string.Join("/", fetched)}"
            : "nothing on it is fetched for you";
        return notes.Count > 0 ? $"{head}; {string.Join("; ", notes)}" : head;
    }

    // The full "buy at <shop>" clause for a gate item a walk would buy, with the
    // bank-run / shortfall note appended when the crosser can't cover it from cash
    // on hand (see PathItemBuyPhrase). Null when a free give preempts the buy (a
    // give owns the item over a purchase) or no shop the walk can use stocks it.
    private string? PathItemShopPhrase(
        int itemId, Game.Map.RoomKey source, Game.Map.RoomKey destination, IReadOnlyCollection<int>? closedGates)
    {
        if (DeterministicGiveExists(itemId)) return null;   // give preempts the buy
        System.Collections.Generic.IReadOnlyList<Game.Map.RoomKey> shops = ShopRoomsSellingItem(itemId);
        if (shops.Count == 0) return null;
        if (!Game.Map.PathItemShopRouter.TrySelectShop(
                shops, source, destination, (a, b) => GiveSources.DetourDistance(a, b, closedGates),
                out Game.Map.RoomKey shop))
            return null;
        return RoomGraph.GetRoom(shop)?.Name is { Length: > 0 } shopName
            ? PathItemBuyPhrase(itemId, shop, shopName)
            : null;
    }

    // The monster a walk would reroute to hunt for a gate item nothing certain
    // covers: no free give, no shop and no guaranteed summon (the same stand-downs
    // MonsterDropRouter enforces), and a dropper spawning in a room reachable from
    // source.
    private string? PathItemDropName(int itemId, Game.Map.RoomKey source)
    {
        if (DeterministicGiveExists(itemId)) return null;   // give preempts the hunt
        if (ShopStock.AnyShopSells(itemId)) return null;
        // A guaranteed summon preempts it too — same stand-down MonsterDropRouter
        // enforces, so the tail can't promise a lair the run won't visit.
        if (SummonSourcesForItem(itemId).Count > 0) return null;
        System.Collections.Generic.IReadOnlyList<Game.Map.MonsterDropSpawn> spawns = DropSpawnsForItem(itemId);
        if (spawns.Count == 0) return null;
        return Game.Map.MonsterDropRouter.SelectNearestSpawn(
                spawns, Bfs.ComputeDistancesFrom(source, Movement),
                out Game.Map.MonsterDropSpawn best, out _)
            ? best.MonsterName
            : null;
    }

    // The purse in copper farthings (the same wealth figure the auto-deposit
    // engine weighs): what a route's buy, a token fare or a bank-run decision is
    // checked against.
    private long PathItemCashOnHand() => Inventory.Snapshot.Currency.TotalCopperValue;

    // The active set's token teleports, keyed by normalized place — read once and
    // cached, dropped on a set swap (see the ActiveSetChanged handler). Null until
    // first use.
    private System.Collections.Generic.IReadOnlyDictionary<string, Game.Tokens.TokenTeleportInfo>? _tokenTeleports;

    private System.Collections.Generic.IReadOnlyDictionary<string, Game.Tokens.TokenTeleportInfo> TokenTeleportMap()
        => _tokenTeleports ??= Game.Tokens.TokenTeleportReader.ReadAll(GameData);

    // Compiled matchers for the token spells' seeded messages, so the token use lines
    // are recognised from the Messages catalogue (single source of the wording) rather
    // than a hardcoded regex. One per token record (Name "token of <place>"): the
    // CasterMessage (self-use → place) and the WitnessMessage (someone else's use →
    // the {source} actor name). Built once, dropped on a set swap.
    private System.Collections.Generic.List<(string Place, Game.Spells.CasterMessageMatcher Caster, Game.Spells.CasterMessageMatcher Witness)>? _tokenUseMatchers;

    private System.Collections.Generic.List<(string Place, Game.Spells.CasterMessageMatcher Caster, Game.Spells.CasterMessageMatcher Witness)> TokenUseMatchers()
    {
        if (_tokenUseMatchers is { } cached) return cached;
        var list = new System.Collections.Generic.List<(string, Game.Spells.CasterMessageMatcher, Game.Spells.CasterMessageMatcher)>();
        foreach (Models.GameData.MessageRecord r in Messages.Messages)
        {
            if (Game.Tokens.TokenCatalog.PlaceOf(r.Name) is not { } place) continue;
            if (Game.Spells.CasterMessageMatcher.TryCreate(r.CasterMessage) is not { } caster) continue;
            if (Game.Spells.CasterMessageMatcher.TryCreate(r.WitnessMessage) is not { } witness) continue;
            list.Add((place, caster, witness));
        }
        _tokenUseMatchers = list;
        return list;
    }

    // A token self-use line → its place (via the CasterMessage), or null.
    private string? MatchTokenSelfUse(string line)
    {
        foreach ((string place, Game.Spells.CasterMessageMatcher caster, _) in TokenUseMatchers())
            if (caster.TryMatch(line, out _)) return place;
        return null;
    }

    // Someone else's token use line → the actor's name (the WitnessMessage's {source}
    // capture), or null.
    private string? MatchTokenMemberDeparted(string line)
    {
        foreach ((_, _, Game.Spells.CasterMessageMatcher witness) in TokenUseMatchers())
            if (witness.TryMatch(line, out System.Collections.Generic.IReadOnlyList<string> caps) && caps.Count > 0)
                return caps[0];
        return null;
    }

    // Plan a token route for a user walk-to (Paradigm only): if a held transport token
    // reaches the destination enough rooms sooner than walking, return a Token
    // RouteChoice the picker surfaces as the blue card alongside the overland walk.
    // Null when disabled, off Paradigm, no held token qualifies, or none beats the
    // room-savings threshold. Only offers a token the character can actually use — the
    // level and gold gates are checked here so the card never proposes a doomed use.
    // Reads the graph / inventory / stats like the other planners; called from
    // RouteChoicePrompt.PlanRouteChoice (which owns the off-thread-race handling).
    public RouteChoice? TryPlanTokenRoute(Game.Map.RoomKey src, Game.Map.RoomKey destination)
    {
        if (GameData.ActiveRealm != Game.RealmType.ParaMud) return null;
        if (!Settings.Current.EnableTokenRoutes) return null;
        if (!Inventory.IsLoaded) return null;

        System.Collections.Generic.IReadOnlyDictionary<string, Game.Tokens.TokenTeleportInfo> map = TokenTeleportMap();
        if (map.Count == 0) return null;

        long cash = PathItemCashOnHand();
        int? level = Stats.HasParsed ? PlayerStats.Level : null;

        // Held tokens the character can actually use (level + gold gates), paired with
        // their live daily-charge counts (null = not looked yet — still offered).
        var usable = new System.Collections.Generic.List<(Game.Tokens.TokenTeleportInfo Info, int? Charges)>();
        foreach ((string _, string place) in Game.Tokens.TokenCatalog.HeldTokens(Inventory.Snapshot.CarriedItems))
        {
            if (!map.TryGetValue(Game.Tokens.TokenCatalog.NormalizePlace(place), out Game.Tokens.TokenTeleportInfo info))
                continue;
            if (level is { } lvl && lvl < info.MinLevel) continue;   // can't use it yet
            if (cash < info.CostCopper) continue;                    // can't afford it
            usable.Add((info, Tokens.ChargesFor(place)));
        }
        if (usable.Count == 0) return null;

        // The overland walk this token route competes against (acquirable gates honoured —
        // the walk the user takes as-is).
        System.Collections.Generic.IReadOnlyList<Game.Map.Direction>? overland = Bfs.FindPath(src, destination, Movement);
        if (overland is null || overland.Count == 0) return null;

        // The BEST OBTAINABLE overland: the same walk with the acquirable gates suspended,
        // i.e. how short the destination gets once you buy/obtain the gate item the direct
        // route needs (a raft at the boatman, a door key). A token whose onward walk isn't
        // shorter than this should step aside for the item-gate fork's "buy and cross"
        // rather than teleport away — the same deferral EvaluateTeleport / EvaluateAvoidOverride
        // already make (report paradigm-20260917-233549). Null when nothing acquirable is on
        // the way (suspending changes nothing), leaving the plain comparison unchanged.
        int? obtainableSteps;
        using (Movement.SuspendAcquirableGatesButUnprotectableHazards())
        {
            int? obt = Bfs.FindPath(src, destination, Movement)?.Count;
            obtainableSteps = obt is { } o && o < overland.Count ? o : null;
        }

        Game.Tokens.TokenRouteCandidate? best = Game.Tokens.TokenRouteEvaluator.Best(
            overland.Count, obtainableSteps, usable,
            landing => Bfs.FindPath(landing, destination, Movement)?.Count,
            System.Math.Max(1, Settings.Current.TokenRouteMinRoomsShorter));
        if (best is not { } b) return null;

        // The onward walk from the landing (for the card's preview / ETA / details).
        System.Collections.Generic.IReadOnlyList<Game.Map.Direction>? fromLanding =
            Bfs.FindPath(b.Landing, destination, Movement);
        if (fromLanding is null) return null;

        return new RouteChoice(
            FreeStepCount: overland.Count,
            GatedStepCount: b.TokenWalkSteps,
            Requirements: System.Array.Empty<RouteRequirement>(),
            FreePath: RouteChoicePlanner.BuildKeyPath(RoomGraph, src, overland),
            GatedPath: RouteChoicePlanner.BuildKeyPath(RoomGraph, b.Landing, fromLanding),
            Kind: RouteChoiceKind.Token,
            TeleportLanding: TokenLandingLabel(b.Landing),
            TokenPlace: b.Place,
            TokenLanding: b.Landing,
            TokenCostCopper: b.CostCopper,
            TokenMinLevel: b.MinLevel,
            TokenCharges: b.Charges);
    }

    // "Silvermere (1/1813)" — the token landing's display label for the picker.
    private string TokenLandingLabel(Game.Map.RoomKey landing)
        => RoomGraph.GetRoom(landing)?.Name is { Length: > 0 } name
            ? $"{name} ({landing})"
            : landing.ToString();

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

    // The tolls and fares on what is left of a path-item detour at its bank stop:
    // on to the shop, then on to the walk's destination. Each crosser's own.
    private long PathItemTripTolls(Game.Map.RoomKey bank, Game.Map.RoomKey shop, Game.Map.RoomKey destination) =>
        Movement.TollCopperOnRoute(Bfs, bank, shop) + Movement.TollCopperOnRoute(Bfs, shop, destination);

    // The deposit this session has seen at the bank whose room this is. Null when
    // none has: no `bank` listing named it, or the room is no bank's (a stash room
    // set as the bank, where a `with` draws nothing).
    private long? KnownDepositAt(Game.Map.RoomKey bankRoom)
    {
        IReadOnlyDictionary<string, long> deposits = BankBalance.LastKnown;
        foreach (Game.GameData.BankShop bank in Game.GameData.BankCatalog.Enumerate(GameData))
            if (bank.Key == bankRoom && deposits.TryGetValue(bank.Name, out long copper))
                return copper;
        return null;
    }

    // What a path-item detour's withdrawal adds for a party in tow (user,
    // 2026-10-10: the leader's cash "from hand or bank if its making a bank
    // trip", and "yes" to this detour drawing it too). The item takes all that
    // was carried and drawn for it, so the leader needs what it keeps for the
    // tolls ahead (PartyTollGate.KeptFor) and what the members are short on top.
    // Never more than the bank is known to hold beyond the item's own draw, since
    // a `with` for more than the balance draws nothing; with no balance known,
    // nothing is added.
    private (long Copper, string Said) PathItemPartyTollTopUp(
        Game.Map.RoomKey bank, Game.Map.RoomKey shop, Game.Map.RoomKey destination, long drawing)
    {
        long tolls = PathItemTripTolls(bank, shop, destination);
        if (tolls <= 0) return (0, string.Empty);
        (long needed, string who) = PartyToll.FundingNeededFor(tolls);
        if (needed <= 0) return (0, string.Empty);

        string lacking = $"The party in tow is {needed:N0} copper short of the walk's {tolls:N0} copper in tolls and fares each ({who}).";
        if (KnownDepositAt(bank) is not { } balance)
            return (0, lacking + " Nothing is drawn for them: no balance is known for the bank here.");
        long wanted = PartyToll.KeptFor(tolls) + needed;
        long add = Math.Min(wanted, Math.Max(0, balance - drawing));
        return (add, lacking + (add >= wanted
            ? $" Drawing {add:N0} copper more: what you keep for your own crossings and their share."
            : add > 0
                ? $" Drawing {add:N0} copper more, {wanted - add:N0} short of what that takes: the balance here is {balance:N0}."
                : $" Nothing more drawn: the balance here ({balance:N0}) has none left beyond the item's {drawing:N0}."));
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

    // Route-picker buy clause for a path / hazard counter the run would purchase:
    // "buy at <shop>" when cash on hand covers it; "buy at <shop> (withdraw ~<N>
    // copper at <bank> first)" when it's short but a bank is configured to draw
    // from; "buy at <shop> — short ~<N> copper, set a bank in Settings → Cash + Items" when
    // short with no bank. Mirrors PathItemShopRouter.NeedsBankRun so the card
    // previews the exact bank-run / shortfall the walk will hit. quantity is how
    // many copies the run buys (a per-person × head-count party provision, else 1).
    // Amounts are in copper farthings — the same denomination the game prices in
    // ("You bought lantern for 396 copper farthings.").
    private string PathItemBuyPhrase(
        int itemId, Game.Map.RoomKey shopRoom, string shopName, int quantity = 1)
    {
        string basePhrase = $"buy at {shopName}";
        if (PathItemBuyCost(itemId, shopRoom) is not { } unit || unit <= 0) return basePhrase;
        long cost = unit * Math.Max(1, quantity);
        long cash = PathItemCashOnHand();
        if (cash >= cost) return basePhrase;   // affordable from the purse — no bank leg
        string amount = $"~{cost - cash:N0} copper";
        if (PathItemBankRoom() is { } bank && RoomGraph.GetRoom(bank)?.Name is { Length: > 0 } bankName)
            return $"{basePhrase} (withdraw {amount} at {bankName} first)";
        return $"{basePhrase} — short {amount}, set a bank in Settings → Cash + Items";
    }

    // The branch of a named bank nearest from, or null when none is reachable — a
    // withdraw only pays out at a branch of the bank holding the deposit.
    private Game.Map.RoomKey? NearestBankBranch(string bankName, Game.Map.RoomKey from)
    {
        Game.Map.RoomKey? best = null;
        int bestDist = int.MaxValue;
        foreach (Game.GameData.BankShop b in Game.GameData.BankCatalog.Enumerate(GameData))
        {
            if (!string.Equals(b.Name, bankName, StringComparison.OrdinalIgnoreCase)) continue;
            if (Bfs.DistanceBetween(from, b.Key, Movement) is { } d && d < bestDist)
            {
                best = b.Key;
                bestDist = d;
            }
        }
        return best;
    }

    // The bank name (= its shop name, what `bank` lists) hosting a room, via the
    // BankCatalog reverse index — so a configured/nearest bank room resolves to the
    // name BankBalanceProbe keys deposits on.
    private string? BankNameForRoom(Game.Map.RoomKey room)
    {
        foreach (Game.GameData.BankShop b in Game.GameData.BankCatalog.Enumerate(GameData))
            if (b.Key.Equals(room)) return b.Name;
        return null;
    }

    // Last-known deposit (copper) at the configured auto-deposit bank, 0 when
    // unset / never seen in a `bank` listing.
    private long ConfiguredBankDepositCopper() =>
        PathItemBankRoom() is { } room && BankNameForRoom(room) is { } name
            ? BankBalance.Balance(name) ?? 0
            : 0;

    // Used banks (seen in a `bank` listing with a positive deposit) whose room is
    // reachable from source, nearest-first, EXCLUDING the configured bank (counted
    // separately as the auto-withdraw source). Each bank name maps to its room(s)
    // via BankCatalog; a bank assigned to several rooms uses its nearest.
    private List<(string Name, long Deposit)> ReachableUsedBankDeposits(
        Game.Map.RoomKey source, Game.Map.RoomKey? exclude)
    {
        var scored = new List<(string Name, long Deposit, int Dist)>();
        IReadOnlyList<Game.GameData.BankShop> banks = Game.GameData.BankCatalog.Enumerate(GameData);
        foreach ((string name, long deposit) in BankBalance.LastKnown)
        {
            if (deposit <= 0) continue;
            int? best = null;
            foreach (Game.GameData.BankShop b in banks)
            {
                if (!string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (exclude is { } ex && b.Key.Equals(ex)) continue;
                if (Bfs.DistanceBetween(source, b.Key, Movement) is { } d && (best is null || d < best))
                    best = d;
            }
            if (best is { } dist) scored.Add((name, deposit, dist));
        }
        scored.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        return scored.ConvertAll(s => (s.Name, s.Deposit));
    }

    // Pick-time economy read for a route that needs BUYING counter/gate items.
    // Actively refreshes own bank (`bank`) and — in a party — the members' on-hand
    // cash (`@wealth`) and whether a member already holds a needed item (`@have`),
    // THEN classifies who can pay (per the user's "check before surfacing options").
    // buys are the item+shop pairs the run would purchase; neededItems are the
    // items to ask the party about. Best-effort: a probe that errors/times out just
    // leaves that dimension at zero (degrades to the self/solo view).
    public async Task<Game.Map.RouteBuyEconomy> AssessRouteBuyAsync(
        IReadOnlyList<(int ItemId, Game.Map.RoomKey ShopRoom)> buys,
        IReadOnlyList<(int ItemId, string Name)> neededItems,
        Game.Map.RoomKey source)
    {
        ArgumentNullException.ThrowIfNull(buys);
        ArgumentNullException.ThrowIfNull(neededItems);

        long cost = 0;
        Game.Map.RoomKey? nearestShop = null;
        int? nearestDist = null;
        foreach ((int itemId, Game.Map.RoomKey shopRoom) in buys)
        {
            cost += PathItemBuyCost(itemId, shopRoom) ?? 0;
            int? d = Bfs.DistanceBetween(source, shopRoom, Movement);
            if (nearestShop is null || (d is { } dd && (nearestDist is null || dd < nearestDist)))
            {
                nearestShop = shopRoom;
                nearestDist = d;
            }
        }

        // Cash on hand is live (parsed inventory — no round-trip). If it already
        // covers the buy, there's nothing to look up: skip the `bank` query AND the
        // party @wealth/@have probes entirely. Those round-trips were making the
        // route picker wait on the network before it could pop, even with a full
        // purse — only reach for the bank/party when cash actually falls short.
        long ownCash = PathItemCashOnHand();
        if (ownCash >= cost)
        {
            Log.Info("RouteBuy",
                $"buy cost ~{cost:N0}c covered by cash on hand ({ownCash:N0}c) — no bank/party probe");
            return new Game.Map.RouteBuyEconomy(
                Game.Map.RouteBuyAffordabilityCalculator.Classify(
                    cost, ownCash, 0, new List<(string Name, long Deposit)>(), 0),
                nearestShop, null);
        }

        // Cash falls short → find the rest: refresh the bank listing, then read deposits.
        try { await BankBalance.QueryAsync(); } catch { /* degrade to last-known */ }
        Game.Map.RoomKey? configured = PathItemBankRoom();
        long configuredDeposit = ConfiguredBankDepositCopper();
        List<(string Name, long Deposit)> otherBanks = ReachableUsedBankDeposits(source, configured);

        // Party money + item holders (bank is self-only, so party wealth is on-hand).
        long partyOnHand = 0;
        string? holder = null;
        if (PartyState.IsInParty)
        {
            try
            {
                Game.Remote.PartyWealthProbe.PartyWealthResult w = await PartyWealthProbe.QueryAsync();
                foreach (KeyValuePair<string, long> kv in w.WealthByMember) partyOnHand += kv.Value;
            }
            catch { /* degrade: party cash unknown */ }

            foreach ((int itemId, string name) in neededItems)
            {
                try
                {
                    Game.Remote.PartyInventoryProbe.PartyItemResult have =
                        await PartyInventory.QueryAsync(itemId, name);
                    if (have.AnyHeld)
                    {
                        foreach (KeyValuePair<string, int> kv in have.CountsByMember)
                            if (kv.Value > 0) { holder = kv.Key; break; }
                        if (holder is not null) break;
                    }
                }
                catch { /* degrade: party inventory unknown */ }
            }
        }

        Game.Map.RouteBuyAffordability afford = Game.Map.RouteBuyAffordabilityCalculator.Classify(
            cost, ownCash, configuredDeposit, otherBanks, partyOnHand);
        Log.Info("RouteBuy",
            $"buy cost ~{cost:N0}c: cash {ownCash:N0}, configured-bank {configuredDeposit:N0}, "
            + $"{otherBanks.Count} other reachable bank(s), party on-hand {partyOnHand:N0} → {afford.Source}"
            + (holder is { } h ? $"; party member {h} holds a needed item" : ""));
        return new Game.Map.RouteBuyEconomy(afford, nearestShop, holder);
    }

    // HP rest gate for DoorOpenManager's bash-interleave. recovered=false → "HP has
    // fallen to the Health-tab rest-if-below trigger, pause bashing"; recovered=true
    // → "HP has climbed back to rest-max, resume". Reuses PoolThreshold so the
    // percentage/absolute mode matches HealthManager's own rest cycle exactly. No
    // vitals yet (MaxHp<=0) reads as not-needed / already-recovered so a fresh login
    // never stalls a bash.
    private bool BashRestGate(bool recovered)
    {
        int max = PlayerState.MaxHp;
        if (max <= 0) return recovered;
        Models.Profile.HealthSettings hs = Resolver.Resolve<Models.Profile.HealthSettings>("Health");
        return recovered
            ? PlayerState.Hp >= Game.Health.PoolThreshold.Resolve(hs.HpThresholdMode, hs.RestMaxHp, max)
            : PlayerState.Hp <= Game.Health.PoolThreshold.Resolve(hs.HpThresholdMode, hs.RestIfBelowHp, max);
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
    // prompt. Computed lazily (only when a no-shop need fires), so the per-item
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

    // What the give router may detour to for itemId (PathItemGiveSources): the free
    // hand-overs, or failing those the one trade the user agreed to on the route
    // card of the journey now under way (AgreedTradeFor, the only place a trade is
    // ever allowed from). Computed when a path-item need fires, so the fan-out is
    // never materialised at load time.
    private System.Collections.Generic.IReadOnlyList<Game.Map.GiveSource> GiveSourcesForItem(int itemId)
        => GiveSources.ForRouter(itemId, agreedTakes: AgreedTradeFor(itemId));

    // True when a free deterministic give can supply itemId at a resolved room —
    // the precedence gate the shop and summon routers stand down on. A trade never
    // counts here: it is used only where no shop sells the item and no room
    // command summons its dropper, so it has nothing to stand anyone down from.
    private bool DeterministicGiveExists(int itemId) => GiveSources.Free(itemId).Count > 0;

    // True when the give router has something to act on for itemId on this walk: a
    // free give, or an agreed trade. Mirrors the router's own "can act" test (a
    // resolved candidate list), so a drop hunt or a room-by-room search never runs
    // beside a detour that is already certain.
    private bool GiveRouterHasSource(int itemId) => GiveSourcesForItem(itemId).Count > 0;

    // Every room command that can conjure itemId's dropper on demand. ItemSourceIndex
    // has already done the filtering that matters — only a monster a room CMD
    // summons, and only its 100% drop slots — so this is a straight projection onto
    // SummonSource. Computed lazily, like the give sources, so the fan-out never
    // materialises at load time. A summonable item is a certain source: the drop
    // router stands down for it, an en-route search isn't worth it, and a door key
    // it yields is never traded for (PathItemGiveSources.Trades).
    internal System.Collections.Generic.IReadOnlyList<Game.Map.SummonSource> SummonSourcesForItem(int itemId)
    {
        System.Collections.Generic.IReadOnlyList<SummonDropSource> sources =
            ItemSources.SummonDropsOf(itemId);
        if (sources.Count == 0)
            return System.Array.Empty<Game.Map.SummonSource>();
        var result = new System.Collections.Generic.List<Game.Map.SummonSource>(sources.Count);
        foreach (SummonDropSource s in sources)
        {
            if (s.Command.Length == 0) continue;   // nothing to type — not routable
            result.Add(new Game.Map.SummonSource(
                new Game.Map.RoomKey(s.Map, s.Room), s.Command, s.MonsterName));
        }
        return result;
    }

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
            MaxCap(overlay), ItemNames.WeightOf(name) ?? 0,
            AutoSell: ResolveAutoSellItem(name) is { Sell: true });
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
            number, name, sell, SellFloor(overlay));
    }

    // MDB ItemType for a container — the only kind auto-open acts on.
    private const int ContainerItemType = 8;

    // Resolve a carried entry for AutoOpen: map the loose carry wording to an
    // item Number, read the verbatim Name and the AutoOpen flag. Null unless the
    // item is a container (ItemType == 8), so a stale overlay flag on anything
    // else never opens. A container comes back whether it is flagged or not: the
    // engine counts them all, so that ticking the flag on one already carried is
    // not a copy arriving.
    private Game.Inventory.AutoOpenManager.ResolvedOpen? ResolveAutoOpenItem(string entry)
    {
        if (ItemNames.FindByName(entry) is not int number) return null;
        if (ItemNames.ItemTypeOf(number) != ContainerItemType) return null;
        string? name = ItemNames.GetName(number);
        if (string.IsNullOrWhiteSpace(name)) return null;

        return new Game.Inventory.AutoOpenManager.ResolvedOpen(
            number, name, ResolveItemOverlay(number).AutoOpen ?? false);
    }

    // The 4-tier ItemOverlay for an item Number (Defaults seed → Global → BBS →
    // Char). Shared by the auto-collect / stash / discard / buy / sell resolvers.
    private Models.GameData.ItemOverlay ResolveItemOverlay(int number) =>
        Resolver.ResolveGameData<Models.GameData.ItemOverlay>(
            "Items",
            number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ItemOverlaySeed.GetOverlay(number));

    // What the journey under way was told to fetch, or null. The items are those
    // the user chose to obtain via the picker's "obtain then cross" option: a
    // per-walk override of the persistent AutoObtainForPath flag, since an explicit
    // pick is consent, so they source through the same give/shop/drop pipeline
    // without needing the item pre-flagged. It rides on the walker's journey, so
    // it is there for that journey's legs and restarts and gone when it ends:
    // nothing here has to remember to clear it.
    private Game.Map.JourneyFetch? Fetch => Walker.Journey?.Fetch;

    // The journey under way still has something it was told to fetch. This is the
    // gate on posting a path-item need at all: without it nothing is fetched and no
    // walk is turned aside.
    private bool JourneyHasFetchOrder => Fetch is { HasItems: true };

    // An errand has the walker. No detour router (a path item's, the light's) takes
    // a walk from one, so the party's count can't turn such a walk aside either.
    private bool ErrandOwnsWalk() => AutoLair.IsActive || AutoDeposit.IsRerouting || SellDetour.IsDetouring;

    private bool FetchesForJourney(int itemId) => Fetch?.Fetches(itemId) == true;

    // The item the user agreed to hand over for keyId on the journey under way, or
    // null. A trade hands an item of the user's over, so this is the one place one
    // is allowed from, and it says yes only for the journey of a route the user
    // picked on a card (the card that named the trade), for the key and the item it
    // named, on the connection it was agreed on. A walk no card was shown for (an
    // item flagged Auto-obtain on a sole route, a loop's approach) is not a picked
    // route, whatever its fetch holds.
    public int? AgreedTradeFor(int keyId) =>
        Walker.Journey is { PickedRoute: true, Fetch: { } fetch } ? fetch.AgreedTradeFor(keyId, _tradeSession) : null;

    // Counts the connections lost. A trade agreed before a drop is not made after it.
    private int _tradeSession;
    public void EndTradeSession() => _tradeSession++;

    // What a walk about to be committed is to fetch on its way regardless of the
    // items' AutoObtainForPath flag, and the trades its route card named, for that
    // walk to carry on its journey (CommitWalk hands it to the walker). Called by
    // RouteChoicePrompt for an "obtain then cross" pick. Everything a pick orders
    // goes in together: its hazard counters and its gate items used to be two
    // orders, and the second replaced the first.
    public Game.Map.JourneyFetch NewJourneyFetch(
        IEnumerable<int> itemIds, IReadOnlyList<(int KeyId, int TakesItemId)>? trades = null)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        PathItemSubstitutes.Clear();
        if (trades is { Count: > 0 })
            Log.Info(Game.Map.AutoSearchManager.LogCategory,
                "route card picked — trade agreed: " + string.Join("; ", trades.Select(t =>
                    $"item {t.KeyId} for item {t.TakesItemId} ({GiveSources.TradeNote(t.KeyId) ?? "no trade on offer now"})")));
        return new Game.Map.JourneyFetch(itemIds, trades, _tradeSession);
    }

    // The route's any-of substitutes for each forced hazard counter — a canoe
    // stands in for the raft the picker chose on the river, but not on Crystal
    // Lake. Staged by HazardAnnounceItems, committed by the path-item announcer.
    public Game.Map.PathItemSubstitutes PathItemSubstitutes { get; } = new();

    // Copies carried that satisfy a path item on this route: the item itself plus
    // any substitute. The count every path-item fulfiller reads, so none of them
    // chases the picker's chosen item once a different valid one is in hand.
    // The key ring counts: a key handed over or bought goes straight onto it, and
    // read from the pack alone it never arrived, so the detour that fetched it
    // waited out its whole window before going on.
    private int CountPathItemCoverage(int itemId) =>
        PathItemSubstitutes.Coverage(itemId, CountItemHeld);

    private bool IsPathItemCovered(int itemId) => CountPathItemCoverage(itemId) > 0;

    // ----- Gate items the party is short of ------------------------------
    //
    // An (Item: N) / (Ticket: N) exit carries across only the member holding the
    // item, so a leader with a copy who leads followers without one crosses alone
    // and splits the party (report paradigm-20261007-183903: two darkwood rings
    // for three people, no route card, the walk failed only after the split).
    // Before a walk the user starts is planned, the leader asks the party how many
    // each holds; an item the party is short of then gates its exits for planning,
    // so the route card names it. Swapped whole, never edited in place: the plan
    // reads it from a background thread.
    private volatile IReadOnlyDictionary<int, (int Need, int OthersHeld)> _partyGateCounts =
        new Dictionary<int, (int, int)>();

    // The members' answers behind each count above, kept for the walk the card
    // starts: PartyPathItemGate decides on the numbers the card showed instead of
    // asking the same question a moment later and holding the walk for it. Taken
    // once, since the hand-offs and purchases that follow move the numbers, and
    // dropped with the counts (the party changed, another walk is being planned).
    // Nothing ties an entry to the card's own walk: it goes to the first walk that
    // asks about the item. So one nobody took is dropped when its card is closed
    // without a pick, and refused once it is two minutes old; a bank run an hour
    // later decided its hand-off from the numbers of a card long gone.
    // Read and written on the UI thread only.
    private readonly Dictionary<int, (Game.Remote.PartyInventoryProbe.PartyItemResult Count, DateTimeOffset TakenAt)>
        _cardCounts = new();

    private static readonly TimeSpan CardCountGoodFor = TimeSpan.FromMinutes(2);

    // The card counts no walk has taken yet, for the bug report.
    public string CardCountSummary => _cardCounts.Count == 0 ? "(none)"
        : string.Join(", ", _cardCounts.Select(kv =>
            $"{ItemNames.GetName(kv.Key) ?? $"item #{kv.Key}"} ({(DateTimeOffset.UtcNow - kv.Value.TakenAt).TotalSeconds:0}s old)"));

    private Game.Remote.PartyInventoryProbe.PartyItemResult? TakeCardCount(int itemId)
    {
        if (!_cardCounts.Remove(itemId,
                out (Game.Remote.PartyInventoryProbe.PartyItemResult Count, DateTimeOffset TakenAt) card))
            return null;
        TimeSpan age = DateTimeOffset.UtcNow - card.TakenAt;
        if (age > CardCountGoodFor)
        {
            Log.Info(Game.Map.AutoSearchManager.LogCategory,
                $"party gate count: item {itemId} — the route card's count is {age.TotalSeconds:0}s old, so the party is asked again");
            return null;
        }
        Log.Info(Game.Map.AutoSearchManager.LogCategory,
            $"party gate count: item {itemId} — the walk uses the route card's count "
            + $"({card.Count.Replied}/{card.Count.Expected} answered, {card.Count.TotalCount} held) and doesn't ask again");
        return card.Count;
    }

    // The route card was closed without a pick: its counts were for a walk that
    // never started.
    public void DropCardCounts(string why)
    {
        if (_cardCounts.Count == 0) return;
        _cardCounts.Clear();
        Log.Info(Game.Map.AutoSearchManager.LogCategory, $"party gate count: the route card's answers dropped — {why}");
    }

    // Short while the leader's own copies plus what the members reported don't
    // reach one each. The leader's count is read live, so buying the missing
    // copies opens the gate without another round of asking.
    private bool IsPartyShortOfGateItem(int itemId) =>
        _partyGateCounts.TryGetValue(itemId, out (int Need, int OthersHeld) c)
        && CountItemCarried(itemId) + c.OthersHeld < c.Need;

    // "darkwood ring (one each for your party of 3; 2 held)" on a route card, or
    // the plain name when the party isn't short of it.
    public string? RouteItemLabel(int itemId)
    {
        string? name = ItemNames.GetName(itemId);
        if (name is null || !IsPartyShortOfGateItem(itemId)) return name;
        (int need, int othersHeld) = _partyGateCounts[itemId];
        return $"{name} (one each for your party of {need}; {CountItemCarried(itemId) + othersHeld} held)";
    }

    // The last party count per gate item, for the bug report.
    public string PartyGateCountSummary
    {
        get
        {
            IReadOnlyDictionary<int, (int Need, int OthersHeld)> counts = _partyGateCounts;
            if (counts.Count == 0) return "(none)";
            return string.Join("; ", counts.Select(kv =>
            {
                int held = CountItemCarried(kv.Key) + kv.Value.OthersHeld;
                return $"{ItemNames.GetName(kv.Key) ?? $"item #{kv.Key}"}: party of {kv.Value.Need} holds {held}"
                    + (held < kv.Value.Need ? " (short — gates closed to the plan)" : "");
            }));
        }
    }

    private void ClearPartyShortGateItem(int itemId)
    {
        _cardCounts.Remove(itemId);
        if (!_partyGateCounts.ContainsKey(itemId)) return;
        var next = new Dictionary<int, (int, int)>(_partyGateCounts);
        next.Remove(itemId);
        _partyGateCounts = next;
        Log.Info(Game.Map.AutoSearchManager.LogCategory,
            $"party gate count: item {itemId} handed out — its gates are open to the party again");
    }

    internal void ClearPartyShortGateItems(string why)
    {
        _cardCounts.Clear();
        if (_partyGateCounts.Count == 0) return;
        _partyGateCounts = new Dictionary<int, (int, int)>();
        Log.Info(Game.Map.AutoSearchManager.LogCategory, $"party gate count: dropped — {why}");
    }

    // Count the party's copies of every per-member gate item on the way to
    // destination that the leader holds, so the plan that follows can tell a gate
    // the whole party clears from one only the leader does. A no-op unless we lead
    // followers. Closing one gate can send the route through another, so the route
    // is re-read until it crosses nothing uncounted (three rounds at most).
    public async Task CountPartyGateItemsAsync(Game.Map.RoomKey source, Game.Map.RoomKey destination)
    {
        ClearPartyShortGateItems("a new walk is being planned");
        if (!PartyState.IsInParty || !PartyState.SelfIsLeader || PartyState.Members.Count <= 1) return;
        if (!Inventory.IsLoaded) return;

        // Read in afresh mid-count, the route and its items are other data's: the
        // walk being planned is dropped (RoutePlanInFlight), and asks nothing more.
        GameDataCache.Load countedOn = GameData.ActiveLoad;
        var counted = new HashSet<int>();
        for (int round = 0; round < 3; round++)
        {
            if (GameData.ActiveLoad != countedOn) return;
            List<int> toCount = PerMemberGateItemsOnRoute(source, destination, counted);
            if (toCount.Count == 0) return;

            var next = new Dictionary<int, (int, int)>(_partyGateCounts);
            foreach (int id in toCount)
            {
                if (GameData.ActiveLoad != countedOn) return;
                counted.Add(id);
                if (ItemNames.GetName(id) is not { Length: > 0 } name) continue;
                Game.Remote.PartyInventoryProbe.PartyItemResult asked = await PartyInventory.QueryAsync(id, name);
                // The card counts one each, so a silent member the leader handed
                // a copy to is credited with one. The walk is given the answers
                // as they came and reads them against the hand-overs itself; it
                // logs the credit then, and the line below names it for the card.
                Game.Remote.PartyInventoryProbe.PartyItemResult r =
                    PartyHandOvers.Reconcile(asked, perPerson: 1, noteCredits: false);
                int need = 1 + r.Expected;
                int own = CountItemCarried(id);
                next[id] = (need, r.TotalCount);
                _cardCounts[id] = (asked, DateTimeOffset.UtcNow);
                string members = r.CountsByMember.Count == 0 ? "nobody answered"
                    : string.Join(", ", r.CountsByMember.Select(kv => asked.CountsByMember.ContainsKey(kv.Key)
                        ? $"{kv.Key} {kv.Value}"
                        : $"{kv.Key} {kv.Value} (didn't answer: handed over earlier)"));
                if (r.Unanswered.Count > 0)
                    members += $"; {string.Join(", ", r.Unanswered)} didn't answer: counted as holding none";
                Log.Info(Game.Map.AutoSearchManager.LogCategory,
                    $"party gate count: {name} — party of {need} holds {own + r.TotalCount} (you {own}; {members}; "
                    + $"{r.Replied}/{r.Expected} answered)"
                    + (own + r.TotalCount < need ? " — short, its gates are closed to the plan" : " — enough"));
            }
            _partyGateCounts = next;
        }
    }

    // The (Item: N) / (Ticket: N) gate items on the route as it plans now that the
    // leader holds and hasn't counted yet.
    private List<int> PerMemberGateItemsOnRoute(
        Game.Map.RoomKey source, Game.Map.RoomKey destination, HashSet<int> counted)
    {
        var ids = new List<int>();
        if (Bfs.FindPath(source, destination, Movement) is not { } path) return ids;
        Game.Map.RoomKey cur = source;
        foreach (Game.Map.Direction dir in path)
        {
            if (RoomGraph.GetRoom(cur) is not { } room || !room.Exits.TryGetValue(dir, out Game.Map.RoomExit exit)) break;
            if (exit.Hint is Game.Map.RoomExitHint.Item or Game.Map.RoomExitHint.Ticket
                && exit.KeyItemId > 0 && !counted.Contains(exit.KeyItemId) && !ids.Contains(exit.KeyItemId))
                ids.Add(exit.KeyItemId);
            cur = exit.Target;
        }
        return ids;
    }

    // True only while WE enabled auto-search for a route picker's "Search en route"
    // leg — so the restore below flips it back off once the counter lands and never
    // touches a toggle the user set themselves.
    private bool _autoSearchFlippedForRouteSearch;

    // UI bridge to flip the master Auto-Search toggle (the observable that persists +
    // updates the toolbar); bound by MainWindowViewModel after construction. Null in
    // headless/test contexts, where the route-search flip is simply a no-op.
    public Action<bool>? SetAutoSearchEnabled { get; set; }

    // Picking the route picker's "Search en route" card turns Auto-Search on for the
    // leg so the card always actually searches — even when the user had it off. The
    // counter's arrival (found on the floor OR bought) drains the card's fetch order, which
    // flips it back off (RestoreRouteSearchAutoSearchIfDone); an abandoned walk clears
    // the same way. Only flips when it was OFF, so a user who already had Auto-Search
    // on keeps it on afterward.
    public void BeginRouteSearchAutoSearch()
    {
        if (_autoSearchFlippedForRouteSearch) return;   // already flipped for an in-flight leg
        if (SetAutoSearchEnabled is null) return;
        if (ReadAutoModeFlag(d => d.AutoSearch)) return;   // already on — nothing to flip / restore
        _autoSearchFlippedForRouteSearch = true;
        SetAutoSearchEnabled(true);
        Log.Info(Game.Map.AutoSearchManager.LogCategory,
            "route 'search en route' picked — auto-search enabled for this leg");
    }

    // Flip auto-search back off if WE turned it on and the route counter is no longer
    // outstanding (obtained, or the walk was abandoned and the forced set cleared).
    private void RestoreRouteSearchAutoSearchIfDone(string reason)
    {
        if (!_autoSearchFlippedForRouteSearch || JourneyHasFetchOrder) return;
        _autoSearchFlippedForRouteSearch = false;
        SetAutoSearchEnabled?.Invoke(false);
        Log.Info(Game.Map.AutoSearchManager.LogCategory,
            $"route search {reason} — auto-search restored to off");
    }

    // Per-item on-demand path acquisition gate: the persistent AutoObtainForPath
    // opt-in on the item's overlay, OR a per-walk forced-obtain override. Checked
    // means every acquisition method is in play (party redistribute, textblock
    // give, shop buy, bank withdraw, guaranteed summon, drop reroute).
    private bool IsAutoObtainForPath(int itemId)
    {
        if (itemId <= 0) return false;
        if (FetchesForJourney(itemId)) return true;
        return ResolveItemOverlay(itemId).AutoObtainForPath ?? false;
    }

    // Distance used to SCORE a path-item give/shop detour. Suspends the acquirable
    // gates so a SOLE-route gate — a hazard we'll counter, an item gate we'll buy —
    // doesn't read as infinite and reject every source: the router prices the route
    // the character walks WITH the sourced item in hand, which crosses that gate.
    // For a gate a free route already bypasses this is a no-op; it only rescues the
    // sole-route case (e.g. buying a rope to reach the hazard-gated FCCO cavern).
    // The gates the journey's route goes round stay closed, as they do for the
    // walk the detour will make: a source that can only be reached through one
    // would be picked and then never arrived at.
    private int? PathItemDetourDistance(Game.Map.RoomKey a, Game.Map.RoomKey b)
    {
        using (SuspendGatesAsTheJourneyDoes())
            return Bfs.DistanceBetween(a, b, Movement);
    }

    // The walk a light-buying detour interrupts, as the journey it was. The detour's
    // walk to the shop is a walk of its own and ends that journey, so it is kept
    // here for the walk back: resumed from the bare room the walker had been heading
    // for, the walk had lost its route (the gates it went round, how it takes
    // teleports), and caught on a side trip it went back to the giver's room.
    private Game.Map.WalkJourney? _lightDetourJourney;

    private Game.Map.RoomKey? LightDetourWalkDestination()
    {
        // A spill sweep's leg reports no journey, and its bare destination is a stop
        // that means nothing once the detour has taken the walk and ended the sweep.
        // No destination, no detour: the light is left to the other provisioning.
        if (DeathRecovery.SpillSweepActive) return null;
        _lightDetourJourney = Walker.State != Game.Map.WalkState.Idle ? Walker.Journey : null;
        return _lightDetourJourney?.Destination ?? Walker.Destination;
    }

    private void LightDetourWalkTo(Game.Map.RoomKey key)
    {
        if (_lightDetourJourney is { } interrupted && interrupted.Destination.Equals(key)
            && !ReferenceEquals(Walker.Journey, interrupted))
        {
            _lightDetourJourney = null;
            Walker.ResumeJourney(interrupted);
            return;
        }
        Walker.WalkTo(key);
    }

    // The acquirable gates stood down the way the walker stands them down for the
    // journey under way: all of them, but for the gates its picked route goes round.
    public IDisposable SuspendGatesAsTheJourneyDoes()
        => Movement.SuspendAcquirableGatesExcept(Walker.Journey?.ClosedGates ?? Array.Empty<int>());

    // For a hazard's any-of counter set, pick the counter the run can most cheaply
    // obtain and describe how — preferring one already on the current room's floor
    // (free + here), then a free give, a shop buy, then a monster drop. It is
    // FLAG-INDEPENDENT: the picker's "obtain then cross" choice is explicit
    // consent, so it offers a counter the run can source whether or not it's
    // flagged AutoObtainForPath. Returns the chosen counter id + a source phrase,
    // or null when none is sourceable.
    public (int ItemId, string Source, bool OnFloor, Game.Map.RoomKey? ShopRoom)? ResolveHazardCounter(
        IReadOnlyList<int> counters, Game.Map.RoomKey source, Game.Map.RoomKey destination)
    {
        ArgumentNullException.ThrowIfNull(counters);
        // On the current floor — grabbed in place, so it never routes through the
        // detour pipeline (the caller issues a `get`); flagged OnFloor to say so.
        foreach (int id in counters)
            if (IsItemOnFloor(id)) return (id, "grab from the floor here", true, null);

        // The destination is hazard-gated (that is WHY a counter is needed), so the
        // shop/give/drop round-trip THROUGH it is only reachable with the acquirable
        // gates suspended — otherwise dist(source→shop→dest) is infinite and every
        // source is rejected. Suspend them for the whole resolution, matching the
        // route we'd actually walk (counter in hand crosses the hazard).
        using (Movement.SuspendAcquirableGates())
        {
            // Free hand-overs only: a hazard counter is never traded for.
            foreach (int id in counters)
                if (Game.Map.PathItemGiveRouter.TrySelectGiver(
                        GiveSources.Free(id), source, destination,
                        (a, b) => Bfs.DistanceBetween(a, b, Movement), out Game.Map.GiveSource giver))
                    return (id, $"ask {giver.GiverName}", false, null);

            int? Dist(Game.Map.RoomKey a, Game.Map.RoomKey b) => Bfs.DistanceBetween(a, b, Movement);
            // Among the counters buyable at a reachable shop, pick the CHEAPEST by
            // base Price (deterministic — a log raft over a river punt), not just the
            // first in the any-of list.
            (int Id, string ShopName, Game.Map.RoomKey Shop, int Price)? bestBuy = null;
            foreach (int id in counters)
            {
                System.Collections.Generic.IReadOnlyList<Game.Map.RoomKey> shops = ShopRoomsSellingItem(id);
                if (shops.Count == 0) continue;
                if (Game.Map.PathItemShopRouter.TrySelectShop(
                        shops, source, destination, Dist, out Game.Map.RoomKey shop)
                    && RoomGraph.GetRoom(shop)?.Name is { Length: > 0 } shopName)
                {
                    int price = ItemNames.PriceOf(id) ?? int.MaxValue;
                    if (bestBuy is null || price < bestBuy.Value.Price)
                        bestBuy = (id, shopName, shop, price);
                    continue;
                }

                // A shop stocks the counter but the router found no reachable detour.
                // Log each candidate's two legs (gates suspended here) so a repro
                // shows which leg is unreachable — the shop-side sourcing gap.
                foreach (Game.Map.RoomKey sr in shops)
                    Log.Debug("HazardCounter",
                        $"item {id} shop at {sr.Map}/{sr.Room}: src→shop={Dist(source, sr)?.ToString() ?? "∞"}, "
                        + $"shop→dest={Dist(sr, destination)?.ToString() ?? "∞"}");
            }
            // Name the shop AND preview the bank-run / shortfall the buy leg will hit
            // (mirrors PathItemShopRouter.NeedsBankRun) so a broke crosser sees the
            // withdraw the run already does — or a "set a bank" warning when short.
            if (bestBuy is { } b)
                return (b.Id, PathItemBuyPhrase(b.Id, b.Shop, b.ShopName), false, b.Shop);

            foreach (int id in counters)
            {
                System.Collections.Generic.IReadOnlyList<Game.Map.MonsterDropSpawn> spawns = DropSpawnsForItem(id);
                if (spawns.Count > 0
                    && Game.Map.MonsterDropRouter.SelectNearestSpawn(
                        spawns, Bfs.ComputeDistancesFrom(source, Movement),
                        out Game.Map.MonsterDropSpawn best, out _))
                    return (id, $"dropped by {best.MonsterName}", false, null);
            }
        }

        // Nothing sourceable — record it so a "why is there no obtain-then-cross
        // card?" repro is a log read, not a re-investigation. The per-shop Debug
        // lines above pinpoint an unreachable-detour cause.
        Log.Info("HazardCounter",
            $"no reachable counter source for items [{string.Join(",", counters)}] "
            + $"from {source.Map}/{source.Room} to {destination.Map}/{destination.Room}");
        return null;
    }

    // The worn items that counter a room-entry hazard of the room we stand in or of
    // a room one step away (so a swap at the edge of a lava field doesn't strip the
    // feather on the way in). A counter only protects while it is worn (GAME_MECHANICS
    // "Room-spell hazard shape 1 — direct damage, negated by an item's
    // `NegateSpell-N`"), so these are what a gear swap must leave alone.
    public IReadOnlyCollection<string> WornRoomHazardCounters()
    {
        if (RoomTracker.State.CurrentRoom is not { } here) return Array.Empty<string>();
        HashSet<int>? counters = null;
        AddCounters(here.Spell);
        foreach (Game.Map.RoomExit exit in here.Exits.Values)
            AddCounters(RoomGraph.GetRoom(exit.Target)?.Spell ?? 0);
        if (counters is null) return Array.Empty<string>();

        List<string>? worn = null;
        foreach (Game.Inventory.EquippedItem e in Inventory.Snapshot.EquippedItems)
            if (ItemNames.FindByName(e.Name) is int id && counters.Contains(id))
                (worn ??= new List<string>()).Add(e.Name);
        return worn ?? (IReadOnlyCollection<string>)Array.Empty<string>();

        void AddCounters(int spell)
        {
            if (spell <= 0 || RoomHazards.HazardForSpell(spell) is not { } hazard) return;
            foreach (int item in hazard.ProtectingItems) (counters ??= new HashSet<int>()).Add(item);
        }
    }

    // True when every room-entry hazard on `path` that the player has NO counter
    // for is survivable damage (a river / heat crossing you can just take), so a
    // "cross unprotected" walk is a damage risk the user may take — not a walk into
    // a drown / freeze death or a forced teleport that a counter is the only way
    // past. Rooms with no hazard, or a hazard the player already counters, don't
    // gate it. Empty / null path → false (nothing to cross unprotected).
    public bool UnprotectedHazardsAllSurvivable(
        System.Collections.Generic.IReadOnlyList<Game.Map.RoomKey>? path)
    {
        if (path is not { Count: > 0 }) return false;
        bool sawUnprotected = false;
        foreach (Game.Map.RoomKey key in path)
        {
            int spell = RoomGraph.GetRoom(key)?.Spell ?? 0;
            if (spell <= 0) continue;
            if (RoomHazards.HazardForSpell(spell) is not { } hazard) continue;
            if (hazard.IsSatisfiedBy(IsItemCarried, NegatingItemUsable) && MovementFilter.HazardCounterProtects(hazard))
                continue;                                         // player counters it → survives
            if (!hazard.IsSurvivableDamage) return false;         // an unprotected grave hazard
            sawUnprotected = true;
        }
        return sawUnprotected;
    }

    // The room to stop at just SHORT of the first room-entry hazard on `path` that
    // the player can't currently survive — the "hazard's edge" the base picker card
    // walks to when the user won't cross unprotected and no counter can be sourced.
    // Null when the path crosses no such hazard (nothing to stop before). When the
    // hazard is the very first room, returns the path's start (walk nowhere).
    public Game.Map.RoomKey? HazardApproachRoom(
        System.Collections.Generic.IReadOnlyList<Game.Map.RoomKey>? path)
    {
        if (path is not { Count: > 0 }) return null;
        for (int i = 0; i < path.Count; i++)
        {
            int spell = RoomGraph.GetRoom(path[i])?.Spell ?? 0;
            if (spell <= 0) continue;
            if (RoomHazards.HazardForSpell(spell) is not { } hazard) continue;
            if (hazard.IsSatisfiedBy(IsItemCarried, NegatingItemUsable) && MovementFilter.HazardCounterProtects(hazard))
                continue;                                         // player survives it
            return i > 0 ? path[i - 1] : path[0];
        }
        return null;
    }

    // Items to provision for entering a hazard room: its single-counter mandatory
    // items always, plus any any-of counter the user forced via the route picker's
    // "obtain then cross" choice (so that one counter is sourced like a gate item).
    private System.Collections.Generic.IReadOnlyList<int> HazardAnnounceItems(Game.Map.RoomKey key, bool stage)
    {
        RoomHazardIndex.RoomHazard? hazard =
            RoomHazards.HazardForSpell(RoomGraph.GetRoom(key)?.Spell ?? 0);
        if (hazard is null) return System.Array.Empty<int>();
        // A boat is no counter on Crystal Lake: nothing is provisioned, or asked of
        // the party, for a room its item doesn't make safe.
        if (!MovementFilter.HazardCounterProtects(hazard)) return System.Array.Empty<int>();
        if (!JourneyHasFetchOrder) return hazard.MandatoryItems;

        System.Collections.Generic.List<int> items = new(hazard.MandatoryItems);
        foreach (System.Collections.Generic.IReadOnlyList<int> group in hazard.RequirementGroups)
            if (group.Count > 1)
                foreach (int id in group)
                    if (FetchesForJourney(id))
                    {
                        // Every hazard room the forced counter is announced for
                        // narrows what may stand in for it on this route.
                        if (stage) PathItemSubstitutes.Record(id, group);
                        if (!items.Contains(id)) items.Add(id);
                    }
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
    // carry-item / ticket the user flagged AutoObtainForPath, or a door key a room
    // command can summon a guaranteed dropper for — the walk should arm the
    // acquisition pipeline and cross the gate rather than fail. A hazard, an
    // unflagged item, or a key with no deterministic source makes it false, so the
    // walk stays a plain route whose BFS fails in place naming what's missing.
    // Hazard-only sole routes never reach here — the picker offers those.
    public bool ShouldAutoObtainSoleRoute(IReadOnlyList<RouteRequirement> requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        if (requirements.Count == 0) return false;
        int obtainable = 0;
        foreach (RouteRequirement req in requirements)
        {
            // Already satisfied (carried) or an optional shortcut → nothing to source;
            // don't let it veto the auto-obtain, and never source it.
            if (req.Carried || req.Optional) continue;
            if (req.Kind is not (RouteRequirementKind.CarryItem or RouteRequirementKind.Ticket
                or RouteRequirementKind.DoorKey))
                return false;
            if (req.ItemIds.Count != 1 || !IsAutoObtainForPath(req.ItemIds[0]))
                return false;
            // A key is only ever auto-sourced off a guaranteed summon, a free give
            // or a shop; a flagged key with none would otherwise send the walk
            // hunting. A key that is only traded for is never fetched from here:
            // this path shows no card, and a trade is made only from a card that
            // names it. Returning false sends that route to the picker.
            if (req.Kind == RouteRequirementKind.DoorKey && !DoorKeyHasUntradedSource(req.ItemIds[0]))
                return false;
            obtainable++;
        }
        return obtainable > 0;
    }

    // The walk every path-item router drives, for both legs of a detour: out to the
    // source and back to the original destination.
    //
    // Acquirable gates stay SUSPENDED while any path item is still owed. The whole
    // premise of a gated route is "plan as if we'll be carrying it" — that's what
    // the user consented to in the picker — but the resume re-planned with gates
    // live, so a route with TWO gates died the moment the first item landed: the
    // orb was collected, the walk re-planned, and BFS refused the still-locked gate
    // door outright. Worse, failing at plan time happens BEFORE the walk-start
    // announce, so the remaining need was never re-offered and the fulfiller that
    // would have fetched the key was never asked (report paradigm-20260911-110624).
    //
    // Crossing is still guarded: the walker holds any step whose gate item is
    // missing while a fulfiller is fetching it, so suspending gates here plans the
    // route without ever walking into one unprepared.
    //
    // Silent supersede: the redirect is our own, not an external abort, so it must
    // not fire a Stopped back into the routers' own OnWalkEvent.
    private void WalkToForPathItemDetour(Game.Map.RoomKey key)
        => Walker.WalkTo(
            key,
            planThroughAcquirableGates: Needs.Outstanding(NeedKind.PathItem).Count > 0,
            supersedeSilently: true);

    // Reset States: put every engine back where it would be if the player were
    // standing idle in a room. Stops the running movement engines and every detour,
    // recovery, FSM, solver and pending "resume / return-to" they hold, and releases
    // the movement / send holds they own — through each owner, so its own
    // bookkeeping agrees and it re-asserts on the next real trigger. The auto-mode
    // toggles, Auto-All, lair markers, buff timers and user settings are left as
    // they are. Conditions, combat state and gear are reset by the caller.
    public void ResetEngineStates(string reason)
    {
        // Movement engines first, so nothing below races a live walk.
        MovementControl.Stop();
        Combat.ClearUserBreakHold("states were reset");
        GhSweep.Stop(reason);
        Walker.ReleaseAbandonedCombatHold();
        LoopRunner.ClearPendingReconnectResume();
        MazeSolver.Cancel(reason);
        PyramidSolver.Cancel(reason);

        // Exit FSMs (doors, hidden exits, winches, traps).
        Door.StopAll();
        HiddenSearch.StopAll();
        Winch.StopAll();
        TrapDisarm.StopAll();
        TrapDelegation.Cancel();

        // Detours and trips that hold a destination to walk back to afterwards.
        PathItemShopRouter.Cancel();
        PathItemGiveRouter.Cancel();
        PathItemSummonRouter.Cancel();
        MonsterDropRouter.Cancel();
        ShortcutSource.Cancel();
        AutoLightShopRouter.Cancel();
        TokenRoute.Cancel();
        AutoDeposit.Cancel();
        SellDetour.Cancel();
        AutoSell.Cancel();
        PartyTrain.CallOffTrip(reason);
        TrainerWalk.Cancel(reason);
        TrainFunding.Cancel(reason);
        StashTransfer.Cancel(reason);
        GetStash.Cancel();
        TrainerMenu.ForceExit(reason);
        Events.CancelRun();
        PartyComeback.Cancel(reason);
        DeathRecovery.CancelTrip();
        HangupItems.Cancel(reason);

        // Outstanding needs and deferred pickups / searches.
        Needs.Clear();
        PartyPathItemGate.Clear();
        PartyHandOvers.Clear(reason);
        ClearPartyShortGateItems(reason);
        Cash.CancelDeferredCollect(reason);
        AutoGetItems.CancelDeferredCollect(reason);
        AutoSearch.OnRoomChanged(null);

        // Party waits and holds.
        AutoParty.AbortReformWaits(reason);
        PartyEssentials.ClearAllWaits();
        PartyDisconnectMovement.Clear();
        AllyDropped.Clear(reason);

        // Stealth, combat, casting, gear and send latches.
        Stealth.ReleaseMovementHolds(reason);
        SneakGuard.Reset();
        Combat.ClearBackstabLatch();
        Equipment.DropHeldGear();
        ManaRegen.Reset();
        // The cast pass's out-of-the-game latch, only when we are in it: out of the
        // game the buff timers are standing still, and letting them run from here
        // would count the rest of the time away against every buff.
        if (InGameCapture.InGame) CastDirector.ResumeBuffTimers();
        SuicidePassword.Cancel(reason);
        Health.CancelFlee();

        Log.Info("Navigation", $"{reason} — every engine returned to idle (walks, loops, lair, detours, "
            + "recoveries, exit FSMs, solvers, party waits and movement holds); auto toggles untouched.");
    }

    // True when a PathItem need for itemId is still outstanding. Scanned rather
    // than indexed: the list holds one entry per gate item on the current route, so
    // it's a handful at most even on a long gated walk.
    private bool HasOutstandingPathItemNeed(int itemId)
    {
        string descriptor = itemId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (Need n in Needs.Outstanding(NeedKind.PathItem))
            if (string.Equals(n.Descriptor, descriptor, StringComparison.Ordinal))
                return true;
        return false;
    }

    // The gate items on a chosen route that the acquisition pipeline can actually
    // source, for the picker to force-obtain when the user commits to crossing.
    // Hazard counters are excluded — the picker resolves those itself (it may grab
    // one off the current floor instead) and forces them on its own path.
    //
    // This is the item-gate twin of the hazard-counter force: an explicit "obtain
    // then cross" pick IS the consent, so the ids come back whether or not they
    // carry the per-item AutoObtainForPath flag. Without it the pick armed nothing
    // whenever Settings → Other → "search rooms if item needed" was off, and the
    // walk crossed unprovisioned — it would `rub bloodstone orb` while carrying no
    // orb and bonk on the hidden exit (report paradigm-20260911-095404).
    //
    // A door key is admitted only when a pick of the card has a reliable source for
    // it (DoorKeyFetchableIfPicked); any other key has none to arm, so forcing it
    // would only switch on a per-room `sea` that can never succeed.
    public IReadOnlyList<int> SourceableGateItems(IReadOnlyList<RouteRequirement> requirements)
        => RouteChoicePlanner.SourceableGateItems(requirements, DoorKeyFetchableIfPicked);

    // A door key the walk can go and get without trading an item for it: a room
    // command summons a monster that always drops it, an NPC hands it over for the
    // asking (the old hermit's jagged bone key for the Library), or a shop sells
    // it (the Thieves' Guild's skeleton key). A shop was left out, so a route
    // through a door the character could neither pick nor bash walked up to it
    // keyless and failed there (report paradigm-20261007-192215).
    private bool DoorKeyHasUntradedSource(int itemId)
        => SummonSourcesForItem(itemId).Count > 0 || DeterministicGiveExists(itemId)
           || ShopStock.ShopsSelling(itemId).Count > 0;

    // What a route card's pick can fetch: the above, or a key an NPC trades for
    // an item in the pack (the sleazy shopkeeper's glowing key for the opal
    // brooch; report paradigm-20261008-175938). The card names the trade and the
    // pick agrees to it (NewJourneyFetch).
    private bool DoorKeyFetchableIfPicked(int itemId)
        => DoorKeyHasUntradedSource(itemId) || GiveSources.Trades(itemId).Count > 0;

    // Whether the walk under way should post a need for a locked door's key: a
    // free source, or a trade agreed to for this walk.
    private bool DoorKeyIsFetchable(int itemId)
        => DoorKeyHasUntradedSource(itemId) || GiveRouterHasSource(itemId);

    // A loop is about to approach through gates because nothing on it can be
    // reached as things stand. What the way in needs fetched, exactly as a go-to
    // over a sole gated route fetches it: only items flagged Auto-obtain for path
    // with a reliable source, and no trade, since no card is shown. Asked just
    // before the approach walk, which carries the answer and posts its needs as it
    // starts.
    private Game.Map.JourneyFetch? LoopApproachFetch(Game.Map.RoomKey from, Game.Map.RoomKey entry)
    {
        if (RouteChoicePlanner.Evaluate(Bfs, Movement, RoomGraph, from, entry) is not { } route) return null;
        if (!ShouldAutoObtainSoleRoute(route.Requirements)) return null;
        if (SourceableGateItems(route.Requirements) is not { Count: > 0 } items) return null;
        Log.Info(Game.Map.AutoSearchManager.LogCategory,
            $"loop approach {from} -> {entry} needs item(s) {string.Join(", ", items)} — fetching on the way in");
        return NewJourneyFetch(items);
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

    // The room on a loop's cycle that's fewest steps from `from`, or null when none
    // can be walked to. One search covers every room on the cycle.
    private Game.Map.RoomKey? NearestLoopRoom(Game.Map.RoomKey from, Game.Map.Loop loop)
    {
        System.Collections.Generic.IReadOnlyList<Game.Map.RoomKey> rooms =
            Game.Map.LoopExpander.ResolveCycleRoomKeys(loop.Waypoints, Bfs, RoomGraph, Movement);
        if (rooms.Count == 0) return null;
        System.Collections.Generic.IReadOnlyDictionary<Game.Map.RoomKey, int> steps =
            Bfs.ComputeDistancesTo(from, rooms, Movement);
        Game.Map.RoomKey? best = null;
        int fewest = int.MaxValue;
        foreach (Game.Map.RoomKey room in rooms)
            if (steps.TryGetValue(room, out int n) && n < fewest) { best = room; fewest = n; }
        return best;
    }

    // Carried items a trip to a shop could sell: flagged Auto-sell (and sellable — not
    // loyal, not a light), with their counts, keep floor, detour count and the shop
    // rooms they may use — the ticked "Sell here" shops that trade the item, or every
    // shop that trades it when none are ticked. One without Make detours carries no
    // detour count: it never starts a trip, but a bank run can still sell it.
    private System.Collections.Generic.IReadOnlyList<Game.Inventory.SellDetourManager.Candidate> SellDetourCandidates()
    {
        System.Collections.Generic.Dictionary<int, (Game.Inventory.AutoSellManager.ResolvedSell Item, int Count)> carried = new();
        foreach (string entry in Inventory.Snapshot.CarriedItems)
        {
            if (ResolveAutoSellItem(entry) is not { Sell: true } item) continue;
            // A stack is one entry carrying its count ("2 crude stone club").
            int copies = Game.Inventory.CountedCommand.SplitLeadingCount(entry).Count;
            carried[item.Number] = carried.TryGetValue(item.Number, out var g) ? (g.Item, g.Count + copies) : (item, copies);
        }
        var result = new System.Collections.Generic.List<Game.Inventory.SellDetourManager.Candidate>();
        foreach ((int number, (Game.Inventory.AutoSellManager.ResolvedSell item, int count)) in carried)
        {
            Models.GameData.ItemOverlay overlay = ResolveItemOverlay(number);
            // "Detour to sell if above" left blank means no detour (user, 2026-09-29);
            // 0 is a real count — go once above Min. to keep.
            int? above = overlay.SellDetour == true && !string.IsNullOrWhiteSpace(overlay.SellDetourAbove)
                ? ParseCount(overlay.SellDetourAbove, 0)
                : null;
            System.Collections.Generic.IReadOnlyList<Game.Map.RoomKey> trading = ShopRoomsSellingItem(number);
            var picks = new System.Collections.Generic.HashSet<Game.Map.RoomKey>();
            foreach (string wire in (overlay.SellShops ?? string.Empty).Split(',',
                         System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries))
                if (Game.Map.RoomKey.TryParseWire(wire, out Game.Map.RoomKey k)) picks.Add(k);
            System.Collections.Generic.IReadOnlyList<Game.Map.RoomKey> shops = picks.Count == 0
                ? trading
                : trading.Where(picks.Contains).ToList();
            result.Add(new Game.Inventory.SellDetourManager.Candidate(number, item.Name, count, item.KeepCount, above, shops));
        }
        return result;
    }

    // What selling leaves carried — auto-sell in passing and a sell detour alike:
    // Min. to keep when it's above 0, else everything goes (user, 2026-09-28). Unlike
    // KeepFloor it doesn't wait on Must have minimum.
    private static int SellFloor(Models.GameData.ItemOverlay overlay) =>
        ParseCount(overlay.MinToKeep, 0);

    // Keep floor for the discard and stash engines: MinToKeep when the user set
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

    // Resolve a single carried-inventory entry for Stash: map the loose carry
    // wording to an item Number, read its verbatim Name, and resolve the
    // per-character ItemOverlay.AutoStash override (Defaults seed → Global → BBS →
    // Char) plus the keep floor. Null when the item isn't flagged, so the stash
    // engine leaves it in the pack: stashing is opt-in per item.
    private Game.Cash.StashRoomManager.ResolvedStash? ResolveAutoStashItem(string entry)
    {
        if (ItemNames.FindByName(entry) is not int number) return null;
        string? name = ItemNames.GetName(number);
        if (string.IsNullOrWhiteSpace(name)) return null;

        Models.GameData.ItemOverlay overlay = ResolveItemOverlay(number);
        return overlay.AutoStash ?? false
            ? new Game.Cash.StashRoomManager.ResolvedStash(name, KeepFloor(overlay))
            : null;
    }

    // "If leading, accept @comeback for up to" — the leader's eligibility window,
    // how long a failed search keeps its engine to resume, and the follower's own
    // cut-off for sending @comeback after a drop.
    public void ApplyComebackWindow(int minutes)
    {
        TimeSpan window = TimeSpan.FromMinutes(Math.Clamp(minutes, 0, 60));
        Party.ComebackWindow = window;
        PartyComeback.ComebackWindow = window;
        PartyRejoin.ComebackWindow = window;
        ComebackRequest.RetryWindow = window;
        RemoteCommands.ComebackWindow = window;
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
        PartyPoller.ApplyParSettings(dto);
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
        // And how long a recovery waits for a re-invited follower to follow again.
        PartyComeback.FollowWaitWindow = TimeSpan.FromSeconds(Math.Clamp(dto.IfLeadingWaitTotalSec, 0, 3600));
        // Leader-side recovery reach — the farthest we'll BFS-walk to re-collect a
        // returning member before declining via @forget.
        PartyComeback.ReturnDistanceRooms = Math.Clamp(dto.ReturnDistanceRooms, 1, 500);
        ApplyComebackWindow(dto.AcceptComebackMinutes);
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
        PartyComeback.SendJoinToInvited = dto.SendJoinToInvited;
        PartyPoller.HealthNagInitialDelay = nagInitial;
        PartyPoller.HealthNagFrequency    = nagFreq;
        PartyPoller.HealthNagMaxTotal     = nagMax;
        PartyPoller.HealthNagEnabled      = dto.SendHealthToMembers;
        PartyProbe.Enabled                = dto.ProbeStatsOnPartyJoin;
    }

    private void ResetPartyToDefaults()
    {
        Models.Profile.PartySettings defaults = new();
        PartyPoller.ApplyParSettings(defaults);
        Party.AutoInviteEnabled = defaults.AutoInviteReconnecting;
        Party.DisconnectGraceWindow = TimeSpan.FromSeconds(defaults.IfLeadingWaitTotalSec);
        AutoParty.InviteWaitWindow = TimeSpan.FromSeconds(defaults.IfLeadingWaitTotalSec);
        PartyWaitMovement.WaitWindow = TimeSpan.FromSeconds(defaults.IfLeadingWaitTotalSec);
        PartyDisconnectMovement.GraceWindow = TimeSpan.FromSeconds(defaults.IfLeadingWaitTotalSec);
        PartyComeback.FollowWaitWindow = TimeSpan.FromSeconds(defaults.IfLeadingWaitTotalSec);
        PartyComeback.ReturnDistanceRooms = defaults.ReturnDistanceRooms;
        ApplyComebackWindow(defaults.AcceptComebackMinutes);
        Party.LocalRankPreference = defaults.Rank;
        PartyBroadcaster.AutoExpResetEnabled = defaults.ResetStatisticsOnLoopStart;
        TimeSpan nagInitial = TimeSpan.FromSeconds(defaults.JoinNagInitialDelaySec);
        TimeSpan nagFreq    = TimeSpan.FromSeconds(defaults.JoinNagFrequencySec);
        TimeSpan nagMax     = TimeSpan.FromSeconds(defaults.JoinNagMaxTotalSec);
        AutoParty.JoinNagInitialDelay = nagInitial;
        AutoParty.JoinNagFrequency    = nagFreq;
        AutoParty.JoinNagMaxTotal     = nagMax;
        AutoParty.JoinNagEnabled      = defaults.SendJoinToInvited;
        PartyComeback.SendJoinToInvited = defaults.SendJoinToInvited;
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
        RemoteCommands.DisableBroadcastChannel = dto.DisallowRemoteFromBroadcast;
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
        RemoteCommands.DisableBroadcastChannel = defaults.DisallowRemoteFromBroadcast;
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
        TrapDisarm.MaxDisarmAttempts = Math.Clamp(dto.MaxTrapDisarmAttempts, 1, 50);
        // Follower-side auto-@comeback toggle.
        ComebackRequest.Enabled = dto.AutoRequestComebackWhenLeftBehind;
        // Discard verb (auto-discard, Chest Offload's drops): hide <item> vs drop <item>.
        AutoDiscard.HideMode = dto.HideWhenDiscarding;
        AutoParty.OnlyWhileNavigating = dto.AutoInviteOnlyWhileNavigating;
    }

    private void ResetOtherToDefaults()
    {
        Models.Profile.OtherSettings defaults = new();
        RemoteCommands.MaxSuicideLivesThreshold = defaults.MaxSuicideLivesThreshold;
        TrapDisarm.MaxDisarmAttempts = defaults.MaxTrapDisarmAttempts;
        ComebackRequest.Enabled = defaults.AutoRequestComebackWhenLeftBehind;
        AutoDiscard.HideMode = defaults.HideWhenDiscarding;
        AutoParty.OnlyWhileNavigating = defaults.AutoInviteOnlyWhileNavigating;
    }

    // Push the loaded character's
    // Models.Profile.AutoLairSettings into
    // AutoLair — heuristic, engage timeout,
    // and the chosen Game.Map.ITravelCostModel
    // implementation. Same shape as
    // ApplyOtherFromActiveProfile.
    public void ApplyAutoLairFromActiveProfile()
    {
        Models.Profile.AutoLairSettings dto =
            ReadSection<Models.Profile.AutoLairSettings>(Profile.Current, "AutoLair");
        AutoLair.Heuristic = dto.Heuristic;
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
        AutoLair.EngageTimeoutSeconds = defaults.EngageTimeoutSeconds;
        AutoLair.TravelCostModel = BuildTravelCostModel(defaults);
    }

    // Push the saved colour scheme (Settings → General) to the terminal's palette.
    // The palette raises its own change only when the 16 colours really differ, so
    // the saves of every other global setting cost nothing here.
    private string _terminalColorsApplied = "Standard";
    private void ApplyTerminalColorsFromGlobalSettings()
    {
        Models.Settings.TerminalColorSettings? saved = Settings.Current.TerminalColors;
        Terminal.AnsiPalette.SetBaseColors(Terminal.AnsiColorSchemes.Resolve(saved?.Scheme, saved?.Colors));
        string now = DescribeTerminalColors();
        if (now == _terminalColorsApplied) return;
        _terminalColorsApplied = now;
        Log.Info("Settings", $"Terminal colours: {now}.");
    }

    // The colour scheme in use, for the program log and the bug report: the scheme's
    // name, and for Custom each colour the user changed.
    public string DescribeTerminalColors()
    {
        Models.Settings.TerminalColorSettings? saved = Settings.Current.TerminalColors;
        Terminal.AnsiColorScheme scheme = Terminal.AnsiColorSchemes.Parse(saved?.Scheme);
        if (scheme != Terminal.AnsiColorScheme.Custom) return scheme.ToString();
        Dictionary<int, string>? changed =
            Terminal.AnsiColorSchemes.CustomDelta(Terminal.AnsiColorSchemes.CustomColors(saved?.Colors));
        return changed is null
            ? "Custom (every colour at the standard one)"
            : "Custom (" + string.Join(", ", changed.OrderBy(kv => kv.Key)
                .Select(kv => $"{Terminal.AnsiColorSchemes.NameOf(kv.Key)} {kv.Value}")) + ")";
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

    // The realm being played: the loaded character's (CharacterProfile.Realm) on the
    // active BBS, else that BBS's first realm. Null only when no BBS resolves. Every
    // realm setting (game data, menu commands, death floor, cleanup time, currency
    // name) and every store of collected data keys on it.
    public (Models.Settings.BbsProfile Bbs, Models.Settings.RealmProfile Realm)? ResolveActiveRealm()
    {
        if (ResolveActiveBbs() is not { } bbs) return null;
        if (bbs.RealmFor(Profile.Current?.Realm) is not { } realm) return null;
        return (bbs, realm);
    }

    // The active realm's data folder, or null with no BBS or when a name in the way
    // can't be a folder (AppPaths.UsableRealmFolder).
    public string? ActiveRealmFolder() =>
        ResolveActiveRealm() is { } r ? AppPaths.UsableRealmFolder(r.Bbs.Name, r.Realm.Name) : null;

    // Whether the loaded character has the "Sysop status" power on the active BBS
    // — the Settings → BBS credentials checkbox. Sysop powers are granted to an
    // account on a board, so the flag lives per character per BBS. Gates the
    // sysop-status probe: unticked, no `sys st` command is ever sent. Credential
    // keys are normalised case-insensitively on profile load, so the lookup
    // matches however the BBS name was cased when it was saved.
    private bool SysopStatusEnabledHere() => SysopPowerHere(static c => c.SysopStatus);

    // Whether the loaded character has the "Sysop god lives" power on the active
    // BBS — gates the auto `sys god <name> add life` on death.
    private bool SysopGodLivesEnabledHere() => SysopPowerHere(static c => c.SysopGodLives);

    // Whether the loaded character has the "Sysop goto" power on the active BBS —
    // gates every `sys goto` surface (typed command, menus).
    private bool SysopGotoEnabledHere() => SysopPowerHere(static c => c.SysopGoto);

    // The active BBS credential's goto table, or empty when no character / BBS / row
    // is set. Both the manager and the menus read through this so they share one table.
    private IReadOnlyList<Models.Profile.SysopGotoLocation> ActiveBbsSysopGotos()
        => ResolveActiveBbs()?.Name is { Length: > 0 } bbs
           && Profile.Current?.BbsCredentials is { } creds
           && creds.TryGetValue(bbs, out Models.Profile.BbsCredentials? cred)
           && cred.SysopGotos is { } list
            ? list
            : System.Array.Empty<Models.Profile.SysopGotoLocation>();

    private bool SysopPowerHere(Func<Models.Profile.BbsCredentials, bool> pick)
        => ResolveActiveBbs()?.Name is { Length: > 0 } bbs
           && Profile.Current?.BbsCredentials is { } creds
           && creds.TryGetValue(bbs, out Models.Profile.BbsCredentials? cred)
           && pick(cred);

    // A player the room lists, or one of our party. A party member is in the room
    // whether or not the last room display listed them (hidden, or read mid-move),
    // and their gear swaps were staged as unknown lines by the hundred.
    private bool IsRoomOrPartyPlayer(string name)
    {
        if (IsKnownRoomPlayer(name)) return true;
        foreach (Game.PartyMember m in PartyState.Members)
            if (!m.IsSelf && FirstTokenEquals(m.Name, name)) return true;
        return false;
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

    // Whether a named player's class can cast anything: true for a magery class,
    // false for the no-magery ones, null when we don't know the player or their
    // class. Consumers must treat null as "don't know" — never as "can't cast".
    //
    // Class sources in precedence order, strongest first: the party roster (`par`
    // states a member's class outright), our own stat screen, then the player
    // database — a class the game stated or showed (the party roster, the top list,
    // `look`) — then the latest top list itself for a player with no record, else
    // the class implied by their title when exactly one class uses it (a shared
    // title tells us nothing).
    private bool? PlayerCanCast(string givenName) =>
        SpellCatalog.ClassCanCast(ResolveKnownPlayerClass(givenName));

    private string? ResolveKnownPlayerClass(string givenName)
    {
        if (string.IsNullOrWhiteSpace(givenName)) return null;

        foreach (Game.PartyMember m in Party.State.Members)
            if (FirstTokenEquals(m.Name, givenName) && m.Class.Length > 0)
                return m.Class;

        if (FirstTokenEquals(PlayerStats.Name, givenName)
            && PlayerStats.Class is { Length: > 0 } own)
            return own;

        Models.GameData.PlayerRecord? record = Players.Find(givenName);
        if (record?.Class is { Length: > 0 } observed) return observed;
        if (Leaderboards.Snapshots.Count > 0)
            foreach (Game.Leaderboard.LeaderboardEntry entry in Leaderboards.Snapshots[0].Entries)
                if (FirstTokenEquals(entry.Name, givenName) && entry.Class.Length > 0)
                    return entry.Class;
        // A title only identifies a class when it isn't shared across classes.
        return Game.GameData.ClassTitleTable.LookupClasses(record?.Title) is { Count: 1 } implied
            ? implied[0]
            : null;
    }

    // Names of every item the player currently holds — carried pack, worn/wielded gear,
    // AND key-ring keys — for charge tracking. Cast-on-use rechargeables (a wielded mace,
    // a worn amulet) live in EquippedItems and some charged items are keys, so a
    // carried-only list would never count their uses; the resolvers match a `use`/`look`
    // against this combined list. Key entries carry a stack-count prefix ("3 iron key"),
    // stripped here so the name resolves to its item number.
    private System.Collections.Generic.IReadOnlyList<string> HeldItemNames()
    {
        Game.Inventory.InventorySnapshot snap = Inventory.Snapshot;
        int keyCount = snap.Keys?.Count ?? 0;
        var names = new System.Collections.Generic.List<string>(
            snap.CarriedItems.Count + snap.EquippedItems.Count + keyCount);
        names.AddRange(snap.CarriedItems);
        foreach (Game.Inventory.EquippedItem e in snap.EquippedItems) names.Add(e.Name);
        if (snap.Keys is { } keys)
            foreach (string k in keys) names.Add(Game.Inventory.InventorySnapshot.ParseKeyEntry(k).Name);
        return names;
    }

    // Whether the game would refuse to drop or hide the named item (ItemDropRule).
    // An item the game data doesn't know is sent, and the game decides.
    private bool GameRefusesToDrop(string name, bool worn)
    {
        if (string.IsNullOrWhiteSpace(name) || GameData.FindRowByName("Items", name) is not { } row) return false;
        bool notDroppable = row.TryGetProperty("Not Droppable", out GameValue nd)
            && nd.ValueKind == System.Text.Json.JsonValueKind.Number && nd.GetInt32() != 0;
        return Game.Inventory.ItemDropRule.Refused(notDroppable, ItemAbilityCodes(row), worn);
    }

    // Whether an item of this name stays on the character through a death: every
    // item that bears the name must (DeathPileRules.EveryItemOfTheNameStays). The
    // indexed lookup answers for nearly every name; the table is only walked for a
    // name whose first item does stay.
    private bool EveryItemOfThisNameStaysOnDeath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || GameData.FindRowByName("Items", name) is not { } first) return false;
        if (!Game.Recovery.DeathPileRules.StaysWithCharacter(ItemAbilityCodes(first))) return false;
        if (GameData.GetTable("Items") is not { } items) return true;
        string wanted = name.Trim();
        return Game.Recovery.DeathPileRules.EveryItemOfTheNameStays(items.Rows
            .Where(row => row.TryGetProperty("Name", out GameValue n)
                && n.ValueKind == System.Text.Json.JsonValueKind.String
                && string.Equals(n.GetString()?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            .Select(ItemAbilityCodes));
    }

    // The ability codes an item carries, by name; empty for an item the game data
    // doesn't know.
    private List<int> ItemAbilityCodes(string name)
        => !string.IsNullOrWhiteSpace(name) && GameData.FindRowByName("Items", name) is { } row
            ? ItemAbilityCodes(row)
            : new List<int>();

    private static List<int> ItemAbilityCodes(GameRow row)
    {
        List<int> abilities = new();
        for (int i = 0; i < 20; i++)
            if (row.TryGetProperty($"Abil-{i}", out GameValue a)
                && a.ValueKind == System.Text.Json.JsonValueKind.Number && a.GetInt32() is int code and not 0)
                abilities.Add(code);
        return abilities;
    }

    // Item number for an item name in the active set, 0 when unresolved, so
    // charges can be keyed by number.
    private int ItemNumberByName(string name)
        => !string.IsNullOrWhiteSpace(name)
           && GameData.FindRowByName("Items", name) is { } row
           && row.TryGetProperty("Number", out GameValue n)
           && n.ValueKind == System.Text.Json.JsonValueKind.Number
            ? n.GetInt32() : 0;

    // Parse the active realm's nightly-cleanup time + zone into a config for the
    // cleanup-boss DEAD/ALIVE state. Null when no BBS, a blank time, or an
    // unparseable time (a bad zone id falls back to the local zone).
    private BossCleanupConfig? ResolveBossCleanupConfig()
    {
        if (ResolveActiveRealm() is not { Realm: var realm }) return null;
        if (string.IsNullOrWhiteSpace(realm.CleanupTimeOfDay)) return null;
        if (!TimeSpan.TryParse(realm.CleanupTimeOfDay.Trim(), out TimeSpan tod)
            || tod < TimeSpan.Zero || tod >= TimeSpan.FromDays(1)) return null;
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(realm.CleanupTimeZoneId); }
        catch { tz = TimeZoneInfo.Local; }
        return new BossCleanupConfig(tod, tz);
    }

    // Recompute the active game-data set from the active realm (else the Global
    // default) and flip GameData if it differs. Idempotent — the cache
    // short-circuits no-op switches so calling this on every profile / BBS /
    // mutate signal is cheap.
    //
    // because: the profile event that asked, when it is one that changes whose
    // game data this is (a close, a load). Loading another profile closes the one
    // loaded, and with no profile the set is the first BBS's (ResolveActiveBbs), so
    // a swap can switch the set twice, even between two characters on one set. What
    // the stop for such a switch drops (StopOnGameDataChange) was held for the
    // character that left, and is said to have gone with the profile: the user
    // changed that, not the game data.
    private void ApplyActiveGameDataSet(string? because = null)
    {
        string? outer = _gameDataSwitchCause;
        _gameDataSwitchCause = because;
        try { GameData.SwitchSet(ProfileGameDataSet()); }
        finally { _gameDataSwitchCause = outer; }
    }

    // Set while a profile event is switching the set, for the stop that switch makes.
    private string? _gameDataSwitchCause;

    // The game-data set the loaded profile runs on: its realm's, else the default.
    // Read straight from the settings, so it is right inside a profile-load handler
    // that runs before the set has been switched.
    //
    // A name that can't be a folder in the game-data folder is no set: null, with the
    // reason in the program log and on the terminal (GameDataSetRefused), rather than
    // a name every store keyed on the set would be handed.
    private string? ProfileGameDataSet()
    {
        string? set = ResolveActiveRealm()?.Realm.ActiveGameDataSet ?? Settings.Current.DefaultGameDataSet;
        string? usable = GameData.UsableSet(set, out string? whyNot);
        if (whyNot is null)
        {
            // A usable set in between, so the refused one is said again when it is
            // next met: loading that profile again is worth a word.
            Interlocked.Exchange(ref _refusedGameDataSetNotice, null);
            return usable;
        }
        // Asked again on every profile event: said once each time the name is met.
        string notice = GameDataCache.RefusedSetNotice(set, whyNot);
        if (!string.Equals(Interlocked.Exchange(ref _refusedGameDataSetNotice, notice), notice, StringComparison.Ordinal))
        {
            Log.Warn("GameData", notice);
            GameDataSetRefused?.Invoke(notice);
        }
        return null;
    }

    // Why the loaded profile runs with no game data, while its set's name can't be
    // used; null otherwise. Read by the main window at start-up, where the profile
    // is loaded before anything can hear GameDataSetRefused, and by the bug report.
    public string? RefusedGameDataSetNotice => Volatile.Read(ref _refusedGameDataSetNotice);
    private string? _refusedGameDataSetNotice;

    // Raised with RefusedGameDataSetNotice each time it is newly said. May be raised
    // off the UI thread.
    public event Action<string>? GameDataSetRefused;

    // Drop any persisted reference to a just-deleted game-data set so a
    // later resolve doesn't point GameData at a folder
    // that's gone. Clears the global
    // Models.Settings.GlobalSettings.DefaultGameDataSet and
    // every realm's Models.Settings.RealmProfile.ActiveGameDataSet that
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
            if (Bbs.Get(name) is not { } p) continue;
            bool changed = false;
            foreach (Models.Settings.RealmProfile realm in p.Realms)
            {
                if (!Matches(realm.ActiveGameDataSet)) continue;
                realm.ActiveGameDataSet = null;
                changed = true;
            }
            if (changed) Bbs.Save(p);
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

        // Conversation row font is char-tier Talk, not General — but it shares the
        // same ProfileLoaded / ProfileMutated triggers, so seed it here too. Stored
        // as the raw delta ("" / 0 mean default); the Conversation window resolves
        // the fallback and observes these for a live re-font on Settings Apply.
        Models.Profile.TalkSettings talk =
            ReadSection<Models.Profile.TalkSettings>(Profile.Current, "Talk");
        Display.ConvoFontFamily = talk.ConvoFont ?? "";
        Display.ConvoFontSize = talk.ConvoFontSize;
        Display.ConvoChannelColors = talk.ChannelColors;
        Display.ConvoShowEmotes = talk.ConvoShowEmotes;
        // SplashAnimate is deliberately NOT seeded here: it's an install-global
        // attract-screen preference, sourced once at startup from the Global default
        // profile (see the seed after the startup profile load). Re-seeding it per
        // profile-load would let an auto-loaded named profile re-enable the splash the
        // user turned off, and flash the animation for a beat before connect.
        TerminalInput.Enabled = general.TypeToTerminalFromOtherWindows;
        InventoryTabCompleteEnabled = general.InventoryTabCompleteEnabled;

        // Game-menu commands are the realm's — HangupHandler consumes ExitCommand
        // synchronously on @hangup; MainMenuEntryAutomation + the cleanup-logout
        // flow consume both. Blank entries fall back to the DTO defaults (E / =x)
        // so a misconfiguration can't leave the engine with empty wire-sends.
        Models.Settings.RealmProfile defaults = new();
        Models.Settings.RealmProfile realm = ResolveActiveRealm()?.Realm ?? defaults;
        GameCommands.EntryCommand = string.IsNullOrWhiteSpace(realm.GameEntryCommand)
            ? defaults.GameEntryCommand
            : realm.GameEntryCommand;
        GameCommands.ExitCommand = string.IsNullOrWhiteSpace(realm.GameExitCommand)
            ? defaults.GameExitCommand
            : realm.GameExitCommand;
    }

    private void ResetDisplayToDefaults()
    {
        Models.Settings.BbsProfile defaults = new();
        Display.FontFamily = DisplayConfig.DefaultFontFamily;
        Display.FontSize = DisplayConfig.DefaultFontSize;
        Display.NavTooltipFontFamily = DisplayConfig.DefaultFontFamily;
        Display.NavTooltipFontSize = DisplayConfig.DefaultNavTooltipFontSize;
        Display.ConvoFontFamily = "";
        Display.ConvoFontSize = 0;
        Display.ConvoChannelColors = null;
        Display.ConvoShowEmotes = true;
        // SplashAnimate is intentionally left untouched — it's install-global (seeded
        // once at startup from the Global default profile), so a profile close/swap
        // must not reset it back on.
        Display.ScrollbackLines = defaults.ScrollbackLines;
        Display.BackscrollWheelLines = defaults.BackscrollWheelLines;
        Display.TerminalCols = defaults.TerminalCols;
        Display.TerminalRows = defaults.TerminalRows;
        Display.ScaleToWindow = false;
        Models.Settings.RealmProfile realmDefaults = new();
        GameCommands.EntryCommand = realmDefaults.GameEntryCommand;
        GameCommands.ExitCommand = realmDefaults.GameExitCommand;
    }

    private void ApplyStatlineRegex()
    {
        Models.Profile.StatlineSettings statline =
            ReadSection<Models.Profile.StatlineSettings>(Profile.Current, "Statline");
        System.Text.RegularExpressions.Regex rx = Game.StatlinePromptRegexBuilder.Build(statline.Command);
        PromptScanner.InstallRegex(rx);
        CurrentStatlinePromptRegex = rx;
    }

    private void OnProfileLoaded(Models.Profile.CharacterProfile profile)
    {
        if (Profile.CurrentProfileName is null || Profile.CurrentBbsName is null) return;

        Models.Profile.ProfileRef loaded = new(Profile.CurrentBbsName, Profile.CurrentProfileName);
        if (Settings.Current.LastUsedProfile == loaded) return;

        Settings.Current.LastUsedProfile = loaded;
        Settings.Save();
    }

    // Cancel handle for a UI-thread one-shot DispatcherTimer: disposing it stops the
    // timer before it fires (a trap or door wait, a voyage deadline the sail beat,
    // the tracker's room wait).
    private sealed class DispatcherTimerHandle(Avalonia.Threading.DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
