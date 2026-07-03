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
    /// Per-character memory of the Session Stats window's panel order +
    /// hidden set. The window's VM reads it on open and pushes drag-reorders /
    /// visibility toggles back through it; it hydrates from
    /// <see cref="CharacterProfile.SessionStatsLayout"/> on profile load and
    /// snapshots back on save.
    /// </summary>
    public SessionStatsLayoutStore SessionStatsLayout { get; }

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

    /// <summary>
    /// Shared recall ring of the user's most-recent typed commands. The
    /// terminal line buffer and the Conversation window both record into
    /// it and read from it for Up / Down recall. App-session lifetime —
    /// see <see cref="CommandHistory"/>.
    /// </summary>
    public CommandHistory CommandHistory { get; } = new();

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
    /// Consumer of <see cref="RemoteCommands"/> for the
    /// <see cref="Models.GameData.PlayerRemoteControls.HangupDisconnect"/>
    /// permission category — <c>@relog</c>. Sends the configured
    /// <see cref="Services.GameCommands.ExitCommand"/> to gracefully log
    /// out, then arms <see cref="RelogSignal"/> so MainWindowVM forces a
    /// reconnect-and-login cycle.
    /// </summary>
    public Game.Remote.RelogHandler Relog { get; }

    /// <summary>
    /// Consumer of <see cref="RemoteCommands"/> for the
    /// <see cref="Models.GameData.PlayerRemoteControls.DivertConversations"/>
    /// category — <c>@divert &lt;player&gt;</c>. While diverting, repeats
    /// every incoming telepath to the chosen target as
    /// <c>&lt;sender&gt; telepathed: &lt;message&gt;</c>; bare <c>@divert</c>
    /// stops.
    /// </summary>
    public Game.Remote.DivertHandler Divert { get; }

    /// <summary>
    /// Consumer of <see cref="RemoteCommands"/> for the
    /// <see cref="Models.GameData.PlayerRemoteControls.QueryVersion"/>
    /// category — <c>@help</c>. Replies with the flat list of remote
    /// commands the sender's per-player permission grant allows, split
    /// across telepaths when long.
    /// </summary>
    public Game.Remote.HelpHandler Help { get; }

    /// <summary>
    /// Consumer of <see cref="RemoteCommands"/> for the
    /// <see cref="Models.GameData.PlayerRemoteControls.QueryExperience"/>
    /// category — <c>@exp</c> (session exp, rate, ETA) and <c>@level</c>
    /// (level, total exp, exp-to-next). Read-only; replies only.
    /// </summary>
    public Game.Remote.ExperienceQueryHandler ExperienceQuery { get; private set; } = null!;

    /// <summary>
    /// Tracks the items on the current room floor from the "You notice
    /// &lt;list&gt; here." survey (cash excluded). Feeds the read-side
    /// <c>@what</c> and the write-side <c>@get-all</c>; cleared on room change.
    /// </summary>
    public Game.Inventory.GroundItemTracker GroundItems { get; private set; } = null!;

    /// <summary>
    /// Consumer of <see cref="RemoteCommands"/> for the
    /// <see cref="Models.GameData.PlayerRemoteControls.QueryInventory"/>
    /// category — <c>@wealth</c> / <c>@enc</c> / <c>@have</c> / <c>@what</c>.
    /// Reads the <see cref="Game.Inventory.InventoryManager"/> snapshot and the
    /// <see cref="GroundItems"/> survey; replies only.
    /// </summary>
    public Game.Remote.InventoryQueryHandler InventoryQuery { get; private set; } = null!;

    /// <summary>
    /// Write-side consumer of <see cref="RemoteCommands"/> for the inventory /
    /// cash action commands — <c>@get-all</c> / <c>@drop-all</c> /
    /// <c>@deposit-all</c> (ExecuteCommands) and <c>@share</c> (party-whitelist).
    /// Emits <c>get</c> / <c>drop</c> / <c>dep</c> / <c>with</c> / <c>give</c> on
    /// the wire, so its sender is bound in <c>MainWindowViewModel</c>.
    /// </summary>
    public Game.Remote.InventoryActionHandler InventoryAction { get; private set; } = null!;

    /// <summary>
    /// Receive side of <c>@heal</c>: a configured party-healer polls <c>par</c>
    /// on request so <see cref="CastDirector"/> re-evaluates its party-heal
    /// thresholds against fresh member HP. The emit side is the follower
    /// flee-substitute in <see cref="Health"/> / <see cref="PartyRest"/>.
    /// Sends <c>par</c>, so its sender is bound in <c>MainWindowViewModel</c>.
    /// </summary>
    public Game.Remote.HealCommandHandler Heal { get; private set; } = null!;

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
    /// <c>@atkprio</c> / <c>@atkorder</c> remote commands — a party member
    /// changes our Target Priority (who) / Attack Order (when) via the same
    /// numbered options as the Combat tab's dropdowns. Backed by the loaded
    /// character profile's <c>Combat</c> section.
    /// </summary>
    public Game.Remote.AttackTargetingRemoteHandler AttackTargeting { get; }

    /// <summary>
    /// <c>@kill &lt;target&gt;</c> remote command — a party member asks us to
    /// engage a named monster. Retargets <see cref="Combat"/> (forcing an
    /// engage even with master auto-attack off) and stays silent on success.
    /// </summary>
    public Game.Remote.KillHandler Kill { get; }

    /// <summary>
    /// Master "Auto-All" kill-switch shared by the toolbar / Action-menu
    /// button and the <c>@auto-all</c> remote command. One press snapshots
    /// + clears every wired auto-engine; the next restores the snapshot.
    /// </summary>
    public Game.AutoModeController AutoModeController { get; }

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
    /// Party-member trap delegation — when the local character can't
    /// disarm a trapped exit but a capable party member can, broadcasts
    /// <c>@trap &lt;dir&gt;</c> on say and resumes the walk on the
    /// member's say reply. Capability via class (main gate) + race
    /// (secondary). Distinct from <see cref="TrapDisarm"/>, which owns the
    /// LOCAL self-disarm path keyed on the game's first-person signals.
    /// </summary>
    public Game.TrapDelegationManager TrapDelegation { get; }

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
    /// Helps the party leader force a door — when we observe the leader
    /// fail to bash a door we can see, send the same <c>bash</c> / <c>pick</c>
    /// verb at the same direction. Gated on
    /// <see cref="Models.Profile.PartySettings.HelpLeaderOpenDoors"/>.
    /// </summary>
    public Game.Map.LeaderDoorAssistManager LeaderDoorAssist { get; }

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
    /// <c>@train</c> handler — trains in place (no walk) on a permitted party
    /// member's request, applying the CP plan when Auto-train-stats is on.
    /// </summary>
    public Game.Remote.TrainHandler TrainRemote { get; }

    /// <summary>
    /// <c>@equip-&lt;set&gt;</c> handler — a permitted party member asks us to
    /// wear one of our saved gear sets. The set keyword is the suffix after
    /// <c>@equip-</c>; routed via <see cref="RemoteCommands"/>'s prefix handler
    /// into <see cref="Equipment"/>.
    /// </summary>
    public Game.Remote.EquipHandler EquipRemote { get; private set; } = null!;

    /// <summary>
    /// Consumer of <see cref="RemoteCommands"/> for <c>@suicide</c>.
    /// Authorised callers (Elevated-Commands permission, lives above
    /// the suicide threshold) trigger the suicide round-trip; on
    /// "Invalid password specified." the handler telepaths the
    /// caller back so they know our stored password is stale.
    /// </summary>
    public Game.Remote.SuicideHandler Suicide { get; private set; } = null!;

    /// <summary>
    /// Consumer of <see cref="RemoteCommands"/> for <c>@reset</c> — an
    /// authorised party member zeroes our Phase 11 session-stats trackers,
    /// the same wipe the Session Stats window's "Reset session" button does.
    /// </summary>
    public Game.Remote.SessionResetHandler SessionReset { get; private set; } = null!;

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
    /// Per-class learnable-spell catalogue built from the active game-data
    /// set (faithful port of MMUD Explorer's <c>SpellIsUsable</c>). Backs
    /// both the Spell Book window and the Settings spell pickers.
    /// </summary>
    public Game.Spells.KnownSpellCatalog SpellCatalog { get; }

    /// <summary>
    /// The local character's spell book — the class's full learnable list
    /// paired with the obtained set. Refreshed from <see cref="Stats"/>'
    /// class+level on every stat poll; obtained set fed by
    /// <see cref="SpellList"/>.
    /// </summary>
    public Game.Spells.SpellbookState Spellbook { get; }

    /// <summary>
    /// Parses <c>spells</c> / <c>pow</c> output into
    /// <see cref="Spellbook"/>'s obtained set. App-level; bound to the
    /// per-session <see cref="Terminal.LineExtractor"/> by
    /// <see cref="ViewModels.MainWindowViewModel"/>.
    /// </summary>
    public Game.Spells.SpellListParser SpellList { get; }

    /// <summary>
    /// Marks powers obtained the moment they're learned at training (the
    /// "You learn the following Kai abilities:" block). Incremental, like the
    /// learn-scroll line — feeds <see cref="Spellbook"/>'s obtained set
    /// without snapshotting it. Bound to the per-session
    /// <see cref="Terminal.LineExtractor"/> by
    /// <see cref="ViewModels.MainWindowViewModel"/>.
    /// </summary>
    public Game.Spells.TrainLearnParser TrainLearn { get; }

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
    /// Reasserts the editor's statline on every connect. Verifies the live
    /// prompt against the editor-built pattern and resends <c>set statline</c>
    /// when the game has drifted (e.g. a fresh character on the class default).
    /// </summary>
    public Game.StatlineReconciler StatlineReconcile { get; }

    /// <summary>
    /// Sniffs the post-IAC wire stream for "BBS shutting down in N minutes"
    /// announcements. The connect lifecycle in MainWindowViewModel reads
    /// <see cref="CleanupWarningWatcher.Latest"/> on disconnect to decide
    /// whether to arm an auto-reconnect.
    /// </summary>
    public CleanupWarningWatcher Cleanup { get; } = new();

    /// <summary>
    /// Proactive log-off engine for the nightly-cleanup cycle: on the
    /// BBS's shutdown warning it waits for a safe room, exits to the main
    /// menu, and drops the carrier — handing off to the predictive
    /// reconnect scheduler in MainWindowViewModel. Opt-in behind the
    /// active BBS's <see cref="Models.Settings.BbsProfile.ReconnectAfterCleanup"/>.
    /// </summary>
    public Game.CleanupLogoutOrchestrator CleanupLogout { get; }

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
    /// Debug-channel instrument that traces observed HP / MA regen ticks to
    /// the program log (silent unless the Log pane's Debug toggle is on). Held
    /// here purely to keep the <see cref="Regen"/> subscription alive for the
    /// app's lifetime; nothing reads it back.
    /// </summary>
    public Game.RegenDiagnosticsRecorder RegenDiagnostics { get; }

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
    /// One-shot coordinator for "relog" intent — a graceful exit plus a
    /// forced reconnect-and-login. Set by
    /// <see cref="Game.Remote.RelogHandler"/> when an authorised sender
    /// requests <c>@relog</c>; consumed by
    /// <see cref="ViewModels.MainWindowViewModel"/> to force the
    /// unconditional dial-back. Inverse of <see cref="HangupSignal"/>:
    /// relog does NOT suppress the entry automation, so login runs
    /// normally on the reconnect.
    /// </summary>
    public RelogSignal RelogSignal { get; } = new();

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
    /// Flags the local character's displayed alignment stale when the game
    /// prints "A dark cloud passes over you", clearing on the next <c>who</c>.
    /// Read by the Character Workshop's Character Info tab.
    /// </summary>
    public Game.AlignmentTracker Alignment { get; }

    /// <summary>
    /// Drives the <c>train stats</c> screen to apply the saved CP plan. Wrapped
    /// by <see cref="TrainerWalk"/>, which owns the walk-to-trainer + level-up.
    /// </summary>
    public Game.AutoTrainManager AutoTrain { get; }

    /// <summary>
    /// Trainer-walk coordinator: resolves the nearest allowed, level-appropriate
    /// trainer, walks there, trains, and applies the CP plan. Backs the CP
    /// Allocation tab's Train Now + the armed auto-train.
    /// </summary>
    public Game.TrainerWalkManager TrainerWalk { get; }

    /// <summary>
    /// Broadcasts "I can now train to level: N" on the configured channel when a
    /// live experience gain makes a new level trainable. Gated by the Settings →
    /// Auto-Trainer "Announce level-ups" toggle.
    /// </summary>
    public Game.LevelUpAnnouncer LevelUp { get; }

    /// <summary>
    /// Loaded character's <see cref="Models.GameData.Macro"/> store.
    /// Surfaced by the Game Data Browser → Macros tab; the Phase 10
    /// MacroManager engine intercepts keystrokes and dispatches from
    /// the same store.
    /// </summary>
    public MacroStore Macros { get; }

    /// <summary>
    /// Per-set quest name / visibility / edited-step overlay store. Backs the
    /// Character Workshop → Quest Status tab (the mechanical step + bonus data is
    /// crawled from <see cref="GameData"/>'s <c>TBInfo</c> at runtime). Reloads its
    /// overlay on <see cref="GameDataCache.ActiveSetChanged"/>.
    /// </summary>
    public QuestStore Quests { get; }

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
    /// Auto-greets newly-seen non-party players (Settings → Talk
    /// "Greet players when first met"). Subscribes to
    /// <see cref="RoomClassifier"/>'s observations; once-per-local-day
    /// dedup on the per-BBS player record. Off by default.
    /// </summary>
    public Game.GreetManager Greet { get; private set; } = null!;

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
    /// Phase 11 — aggregates combat lines + <see cref="RoundDamage"/> rounds
    /// into the session combat figures (hit / miss / crit / dodge rates,
    /// physical &amp; backstab damage extents, per-round damage) the Session
    /// Stats panel displays. Pure downstream subscriber; reset on the session
    /// boundary alongside <see cref="RoundDamage"/>.
    /// </summary>
    public Game.Combat.CombatSessionTracker CombatSession { get; private set; } = null!;

    /// <summary>
    /// Phase 11 — divides the session's wall-clock time across the player's
    /// activities (waiting / moving / attacking / resting HP / resting MA) plus
    /// the blinded / poisoned overlays, for the Time Analysis panel. Fed by
    /// <see cref="PlayerState"/>, <see cref="Conditions"/>, and
    /// <see cref="RoomTracker"/>; reset on the session boundary.
    /// </summary>
    public Game.Combat.TimeAnalysisTracker TimeAnalysis { get; private set; } = null!;

    /// <summary>
    /// Phase 11 — counts the session's monster kills and experience earned and
    /// keeps a rolling kill-timestamp history for the Session Stats panel's
    /// kills/hour sparkline. Fed by <see cref="MonsterDeath"/> and the
    /// experience-gain line; reset on the session boundary.
    /// </summary>
    public Game.Combat.SessionActivityTracker SessionActivity { get; private set; } = null!;

    /// <summary>
    /// Phase 12 — per-session ledger of cash/item offloads (bank deposits +
    /// stash-room hides) behind the Session Stats → Transaction history window.
    /// Fed by <see cref="AutoDeposit"/> and <see cref="Stash"/>; reset on the
    /// same session boundary as the other session-stats trackers.
    /// </summary>
    public Game.Cash.TransactionHistoryTracker TransactionHistory { get; private set; } = null!;

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

    /// <summary>Lookup of each monster's <c>Magical</c> / <c>SpellImmu</c>
    /// levels (codes 28 / 139) in the active game-data set. Drives
    /// CombatManager's deterministic weapon-vs-monster hit eligibility and
    /// spell-immunity gating.</summary>
    public Game.Combat.MonsterMagicIndex MonsterMagic { get; private set; } = null!;

    /// <summary>Lookup of each weapon's <c>HitMagic</c> level (code 142) in
    /// the active game-data set. Paired with <see cref="MonsterMagic"/> for
    /// the HitMagic ≥ Magical hit check.</summary>
    public Game.Combat.ItemMagicIndex ItemMagic { get; private set; } = null!;

    /// <summary>Lookup of each spell's <c>ReqLevel</c> by cast-code in the
    /// active game-data set. Paired with <see cref="MonsterMagic"/> for the
    /// ReqLevel ≥ SpellImmu eligibility check.</summary>
    public Game.Combat.SpellReqLevelIndex SpellReqLevel { get; private set; } = null!;

    /// <summary>Catalogue of every light-source item (<c>ItemType 6</c>) in the
    /// active set — projected illumination (<c>IlluTarget</c>) + burn budget —
    /// for computing carried illumination and provisioning a dark route.</summary>
    public Game.Light.LightItemIndex Lights { get; private set; } = null!;

    /// <summary>The highest Strength any race + class + gear build can reach on the
    /// active set — the door FSM's per-set bash ceiling, replacing the old hardcoded
    /// 200. Feeds <see cref="Game.Map.DoorOpenManager"/> via a provider so a
    /// strength-gated door is only ruled unbashable when no build could open it.</summary>
    public Game.Map.MaxStrengthIndex MaxStrength { get; private set; } = null!;

    /// <summary>The player's live carried illumination (worn <c>+illu</c> gear +
    /// the readied light's strength) — the <c>charIllu</c> input to the
    /// <see cref="Game.Light.LightModel"/> visibility bands.</summary>
    public Game.Light.PlayerIllumination PlayerIllumination { get; private set; } = null!;

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
    /// PR 10.18 — runs the equip → use → re-equip wire sequence for an
    /// item-cast Bless slot (a <see cref="Game.Spells.ItemCastToken"/>). Driven
    /// by <see cref="CastDirector"/>; wire-sender bound in the main VM.
    /// </summary>
    public Game.Spells.ItemCastSequencer ItemCast { get; private set; } = null!;

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
    /// Outbound ailment-sync engine — on a local curable ailment it
    /// announces on say (<c>.@poisoned</c> etc.) so other FujinTerm
    /// clients mirror our state, and @waits the leader; on clear it @oks.
    /// </summary>
    public Game.Conditions.AilmentSyncEngine AilmentSync { get; private set; } = null!;

    /// <summary>
    /// Inbound ailment-sync engine — mirrors a party member's
    /// <c>.@poisoned</c> / <c>.@blind</c> / <c>.@diseased</c> / <c>.@confused</c>
    /// say announce onto their party chip, and clears the chip when OUR cure
    /// spell is observed landing on them. Counterpart to
    /// <see cref="AilmentSync"/>.
    /// </summary>
    public Game.Conditions.PartyAilmentTracker PartyAilment { get; private set; } = null!;

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
    /// Active auto-light engine. Bound to the walker's route announcer: on each
    /// planned route it scans for the darkest room and readies a covering carried
    /// light (<c>use &lt;light&gt;</c>), or hands off to
    /// <see cref="AutoLightShopRouter"/> to provision one it lacks. Every action
    /// is gated by the AutoLight master toggle.
    /// </summary>
    public Game.Light.AutoLightProvisioner AutoLightProvisioner { get; private set; } = null!;

    /// <summary>
    /// Auto-light provisioning detour. On the provisioner's Buy verdict (route
    /// dark, nothing carried covers) it walks to the fewest-added-steps shop that
    /// stocks the light, buys the carry batch, and resumes — the provisioner
    /// lights it on the resumed route. Gated entirely by the AutoLight master
    /// toggle; wire-sender bound by <c>MainWindowViewModel</c> after connect.
    /// </summary>
    public Game.Light.AutoLightShopRouter AutoLightShopRouter { get; private set; } = null!;

    /// <summary>
    /// Phase 9 PR 9.I — death observation aggregator. Surfaces the loaded
    /// profile's <see cref="Models.Profile.CharacterProfile.DeathHistory"/>
    /// as the Workshop DEATH section's deathpile grid, owns the per-character
    /// Auto-Recover / Auto-Equip toggles, and drives the corpse-recovery
    /// state machine off room re-entry and pickup confirmations.
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
    /// Phase 10 — gear-set apply engine (Workshop Equipment tab). Diffs a saved
    /// <see cref="Models.Profile.EquipmentSet"/> against the live worn loadout
    /// (<see cref="Inventory"/>'s snapshot) and paces <c>wear</c> commands;
    /// virtual slots write <see cref="Models.Profile.CombatSettings"/> instead.
    /// Driven by the <c>@equip-&lt;set&gt;</c> remote command
    /// (<see cref="EquipRemote"/>) and the auto-equip triggers
    /// (<see cref="AutoEquip"/>).
    /// </summary>
    public Game.Inventory.EquipmentManager Equipment { get; private set; } = null!;

    /// <summary>
    /// Phase 10 PR 10.14 — auto-equip trigger coordinator. Subscribes to
    /// <see cref="Game.PlayerState"/>'s position / combat signals and, when the
    /// matching trigger-purposed <see cref="Models.Profile.EquipmentSet"/> is
    /// enabled, hands its id to <see cref="Equipment"/> for the moment.
    /// </summary>
    public Game.Inventory.AutoEquipCoordinator AutoEquip { get; private set; } = null!;

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
    /// Base auto-search engine — sends a bare <c>sea</c> on each room
    /// entry while the AutoSearch master toggle is on, revealing hidden
    /// items so <see cref="AutoGetItems"/> / <see cref="Cash"/> can
    /// collect them. Fired from the <see cref="RoomTracker.StateChanged"/>
    /// seam; off by default and armed manually.
    /// </summary>
    public Game.Map.AutoSearchManager AutoSearch { get; private set; } = null!;

    /// <summary>
    /// Demand-driven auto-search coordinator — posts a
    /// <see cref="NeedKind.PathItem"/> need when the walker plans a route
    /// through an Item/Ticket exit whose item we don't carry, and resolves it
    /// when the item enters inventory. While such a need is outstanding (and
    /// Settings → Other "search rooms if item needed" is on),
    /// <see cref="AutoSearch"/> arms itself via
    /// <see cref="Game.Map.PathItemDemandTracker.SearchDemandActive"/>.
    /// </summary>
    public Game.Map.PathItemDemandTracker PathItemDemand { get; private set; } = null!;

    /// <summary>
    /// Reverse index of the active set's <c>Shops.json</c> — item id → the
    /// shops that stock it. Feeds <see cref="PathItemShopRouter"/>'s shop
    /// lookup; rebuilt on <see cref="GameDataCache.ActiveSetChanged"/>.
    /// </summary>
    public ShopStockIndex ShopStock { get; private set; } = null!;

    /// <summary>
    /// Active fulfiller for <see cref="NeedKind.PathItem"/> needs backed by a
    /// shop: on a one-shot walk-to that needs an uncarried item a shop sells,
    /// detours to the fewest-added-steps shop, buys it, and resumes. Gated by
    /// Settings → Other "buy item if needed".
    /// </summary>
    public Game.Map.PathItemShopRouter PathItemShopRouter { get; private set; } = null!;

    /// <summary>
    /// Index of the active set's <c>Monsters.json</c> — which monsters drop
    /// an item and where each spawns. Feeds
    /// <see cref="MonsterDropRouter"/>'s hunt lookup; rebuilt on
    /// <see cref="GameDataCache.ActiveSetChanged"/>.
    /// </summary>
    public MonsterDropIndex MonsterDrops { get; private set; } = null!;

    /// <summary>
    /// Active fulfiller for <see cref="NeedKind.PathItem"/> needs no shop can
    /// satisfy: on a one-shot walk-to that needs an uncarried item no shop
    /// sells, prompts to reroute to the nearest room a monster that drops it
    /// spawns in, then resumes once it lands. Gated by Settings → Other
    /// "hunt item if needed".
    /// </summary>
    public Game.Map.MonsterDropRouter MonsterDropRouter { get; private set; } = null!;

    /// <summary>
    /// On-demand party-inventory probe — broadcasts <c>@have</c> and aggregates
    /// the party's replies into per-member counts. Feeds
    /// <see cref="PartyPathItemGate"/>'s give-from-surplus decision.
    /// </summary>
    public Game.Remote.PartyInventoryProbe PartyInventory { get; private set; } = null!;

    /// <summary>
    /// Party-first stage of the path-item pipeline: on a walk-to that needs an
    /// uncarried per-member Item/Ticket item, probes the party
    /// (<see cref="PartyInventory"/>) and, if a member has a spare, arranges a
    /// <c>give</c> instead of posting a need. Only a genuine shortfall falls
    /// through to <see cref="PathItemDemand"/>. Gated by Settings → Other
    /// "defer to party inventory".
    /// </summary>
    public Game.Map.PartyPathItemGate PartyPathItemGate { get; private set; } = null!;

    /// <summary>
    /// On-demand party-level probe — broadcasts <c>@level</c> and records
    /// each member's exact level into <see cref="Players"/>. Fired by
    /// <see cref="PartyLevel"/> on roster change so the players table stays
    /// the authoritative level source (superseding the title-derived band).
    /// </summary>
    public Game.Remote.PartyLevelProbe PartyLevelProbe { get; private set; } = null!;

    /// <summary>
    /// Keeps the party's level bounds warm for path planning and feeds
    /// <see cref="MovementFilter.PartyLevelBoundsProvider"/> so BFS routes a
    /// following party around <c>(Level: MIN to MAX)</c> gates a member
    /// can't clear. Gated by Settings → Other "avoid party-impassable level
    /// gates".
    /// </summary>
    public Game.Remote.PartyLevelTracker PartyLevel { get; private set; } = null!;

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
    /// Phase 9 PR 9.E follow-up — auto-deposit reroute. Subscribes to
    /// <see cref="Game.Cash.CashManager.AutoDepositRequested"/>; when a
    /// wealth / coin gate crosses while a loop or auto-lair is running,
    /// detours to the configured bank / stash room, offloads the excess
    /// coin (<c>dep</c> for a bank, <see cref="Stash"/>'s <c>hide</c> for
    /// a stash room), walks back, and restarts the captured engine.
    /// </summary>
    public Game.Cash.AutoDepositManager AutoDeposit { get; private set; } = null!;

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
    /// Party-vitals pause bridge — holds the active movement engine while
    /// a party member is below the Party-tab HP% threshold.
    /// </summary>
    public Game.PartyVitalsWatcher PartyVitals { get; private set; } = null!;

    /// <summary>
    /// Follower-movement pause bridge — holds every movement engine while
    /// we're a party follower, so the leader's drag isn't fought by our own
    /// walk / loop / auto-lair.
    /// </summary>
    public Game.PartyFollowerMovementGate PartyFollowerMovement { get; private set; } = null!;

    /// <summary>
    /// Leader-rest bridge — nudges <see cref="Health"/> to re-evaluate when
    /// the party leader's rest / meditate posture flips, so a standing-idle
    /// follower opportunistically tops off during the leader's downtime
    /// without waiting on its own next prompt tick.
    /// </summary>
    public Game.PartyLeaderRestWatcher PartyLeaderRest { get; private set; } = null!;

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
    /// Folder CRUD over the shared per-BBS Loops directory that holds
    /// both <see cref="Loops"/> and <see cref="Lairs"/>. Create / rename
    /// / delete folders; reloads both catalogues after a filesystem
    /// move so their in-memory <c>Folder</c> values stay in sync.
    /// </summary>
    public Game.Map.NavFolderManager NavFolders { get; private set; } = null!;

    /// <summary>
    /// Game Data → "Manage Sets…" backend: copy / move a set's loop
    /// library to another set, delete a set (tables + loops).
    /// </summary>
    public GameDataSetManager GameDataSetManager { get; private set; } = null!;

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
    /// Always-alive control surface over the three movement engines —
    /// coalesces their run-state and routes Pause / Resume / Stop to the
    /// right engine. Backs the toolbar movement-flow buttons.
    /// </summary>
    public Game.Map.MovementController MovementControl { get; private set; } = null!;


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
        // Phase 6 PR 6.3 — first consumer; registers the party-essential
        // handler set against the engine.
        // readCurrentRoom / readRoomEntities defer to the live RoomTracker
        // and RoomEntityClassifier (both constructed later in
        // OnGameDataLoaded) via the property on each call, so they always
        // read the current snapshot even across set-switch rebuilds.
        PartyEssentials = new Game.Remote.PartyEssentialHandlers(
            RemoteCommands, PlayerState, PartyState,
            readPartySettings: () => ReadSection<Models.Profile.PartySettings>(Profile.Current, "Party"),
            readCurrentRoom: () => RoomTracker?.State.CurrentRoom,
            readRoomEntities: () => RoomClassifier?.Current?.Entities,
            readMovement: () => Game.Remote.MovementStatus.Capture(Walker, LoopRunner, AutoLair));
        // Phase 6 PR 6.4 — drives the on-join @health exchange + the
        // periodic par poll. Wire-sender + cadence-from-settings hookup
        // happens in MainWindowViewModel / PR 6.9.
        PartyPoller = new Game.PartyPoller(Chat, PartyState, Party)
        {
            // par reads party health, so it lives under the auto-heal/rest
            // toggle like every other automatic action. AutoModeController's
            // kill-all zeroes that flag, so auto-all off silences par too.
            IsParPollEnabled = () => ReadAutoModeFlag(d => d.AutoHealRest),
        };
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
        // Full-screen forms (trainer stats / char creation) want
        // character-at-a-time input with server echo, not client-side
        // line buffering. Flip LocalInputBuffer into character-mode on
        // menu entry and back to line-mode on exit.
        TrainerMenu.MenuEntered += () => InputBuffer.CharacterMode = true;
        TrainerMenu.MenuExited  += () => InputBuffer.CharacterMode = false;
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
        // no-profile case alike. The obtained set is NOT seeded here (it
        // isn't persisted); checkmarks stay empty until a live
        // `spells`/`pow` snapshot confirms them in-game.
        void SeedSpellbook(Models.Profile.LastKnownStats? snap) =>
            Spellbook.Refresh(snap is null ? 0 : SpellCatalog.ResolveClassNumber(snap.Class) ?? 0, snap?.Level ?? 0);

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
            Stats.Hydrate(p.LastKnownStats);
            // Seed the live max ceilings from the persisted snapshot so a
            // returning session starts correct instead of re-learning the
            // high-water mark from prompts. Null / never-stat'd passes 0,
            // which ApplyStatScreenMax ignores.
            Player.ApplyStatScreenMax(p.LastKnownStats?.MaxHits ?? 0, p.LastKnownStats?.MaxMana ?? 0);
            SeedSpellbook(p.LastKnownStats);
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
        // Cluster 5d — @auto-* family. AutoMode handler mutates the
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

        // Phase 7 PR 7.23 — @goto / @loop / @lair / @stop / @rego land
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
        // Feed the player's level into Form-A exit level-gate evaluation.
        // null until a stat screen parses — IsExitBlocked never gates on
        // an unknown level, so an unparsed character walks unrestricted.
        Movement.LevelProvider = () => Stats.HasParsed ? PlayerStats.Level : (int?)null;
        Favorites = new FavoritesStore(Profile, Log);

        // Phase 7 PR 7.7 — coordinator + walker. Coordinator is the
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
        // Phase 11 CombatSessionTracker is constructed after Inventory (its
        // proc recogniser reads the worn-weapon snapshot) — see below.

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
            log: Log,
            readPartySettings: () =>
                ReadSection<Models.Profile.PartySettings>(Profile.Current, "Party"),
            isTwoHandedWeapon: IsConfiguredWeaponTwoHanded);

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
            log: Log);

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
        // Survival casts (heal / cure / buff / party heal) skip any spell the
        // player can't afford — the cost comes from the game-data Spells table
        // via the live spellbook. Combat-tab spells keep their own
        // MinManaPerCast threshold and aren't gated here.
        CastDirector.SetManaCostLookup(Spellbook.ManaCostOf);
        // Auto-Bless auto-engine gate — when off, the Buffing category is
        // suppressed (no Bless / regen / when-full buff fires).
        CastDirector.SetAutoBlessGate(() => ReadAutoModeFlag(d => d.AutoBless));
        // Feature 5 — buff-duration recast model. A buff cast (self or
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
        Tick.CombatTickElapsed += CastDirector.OnCombatTick;

        // Phase 9 PR 9.A (spell extension) — opt the combat engine into the
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
        Tick.CombatTickElapsed += Combat.OnCombatTick;

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

        // Deterministic magic eligibility — weapon HitMagic ≥ monster Magical
        // picks normal-vs-alternate, and spell ReqLevel ≥ monster SpellImmu
        // gates single-target debuff / attack spells. Both fail open when game
        // data is silent.
        MonsterMagic = new Game.Combat.MonsterMagicIndex(GameData);
        ItemMagic = new Game.Combat.ItemMagicIndex(GameData);
        SpellReqLevel = new Game.Combat.SpellReqLevelIndex(GameData);
        Combat.SetMagicEligibility(MonsterMagic, ItemMagic, SpellReqLevel);

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
            DeathWatcher, Profile, RoomTracker, Log);

        // Phase 9 — InventoryManager. Parses the full `i` dump into a
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
        // PR 10.5 — death-recovery deathpile capture. RoomTracker.NoteDeath
        // records the worn + carried items from the last-known `i` snapshot
        // onto the death record; DeathRecoveryManager.SimulateDeath captures
        // the same way for the test button.
        RoomTracker.AttachInventorySnapshot(() => Inventory.Snapshot);
        DeathRecovery.AttachInventorySnapshot(() => Inventory.Snapshot);

        // Phase 11 — CombatSessionTracker. Aggregates the same combat lines
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

        // Phase 11 — TimeAnalysisTracker. Divides the session's wall-clock time
        // across the player's activities + the affliction overlays (blinded /
        // poisoned / diseased / confused / held). It
        // owns no subscriptions (its inputs span three sources), so forward each
        // here: PlayerState carries combat / position / vitals, Conditions the
        // affliction flags, and a confirmed room change (NewRoom differs from
        // the previous) opens its movement window. Reset on the same
        // ProfileLoaded boundary as the other Phase 11 trackers.
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

        // Phase 11 — SessionActivityTracker. Counts kills + experience and keeps
        // the rolling kill history for the kills/hour sparkline. Like the other
        // Phase 11 trackers it owns no subscriptions: a kill arrives from
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

        // Phase 12 — TransactionHistory. A per-session ledger of cash/item
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

        // PR 10.18 — item-cast buffs. A Bless slot may hold a #-token naming an
        // unlimited-use cast item (surfaced in the Spell Book); the director
        // fires it by wielding + using the item, then re-wielding the displaced
        // weapon (read from Inventory's last `i` dump). Duration drives the
        // recast clock. Wire-sender bound in MainWindowViewModel.
        ItemCast = new Game.Spells.ItemCastSequencer(
            () => Spellbook.GetCastItems(), () => Inventory.Snapshot, Log);
        CastDirector.SetItemCastSource(ItemCastDurationOf, ItemCast.Execute);
        CastDirector.SetItemCastManaCost(ItemCastManaCostOf);

        // PR 10.8 — auto-train. Drives the `train stats` screen to apply the CP
        // plan (Workshop CP Allocation tab) when armed + a level-up enables it.
        // Needs Inventory (raw-base = live - gear) + TrainerMenu (screen enter/
        // exit gating, already wired to char-mode). Wire-sender bound in
        // MainWindowViewModel.
        AutoTrain = new Game.AutoTrainManager(PlayerStats, GameData, Inventory, Profile, TrainerMenu, Log);

        // Phase 10 — EquipmentManager + the @equip-<set> handler. The engine
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
            log: Log);
        EquipRemote = new Game.Remote.EquipHandler(RemoteCommands, Equipment);

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
        // Phase 11 — feed confirmed coin pickups into the Session Stats
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

        // Phase 9 PR 9.E follow-up — StashRoomManager. NOT autonomous:
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
        // Phase 11 — count stash-room hides toward the Session Stats
        // stashed/deposited figure (copper value across the dispatched coins).
        // Phase 12 — also record the hide (coins + items) in the transaction
        // ledger.
        Stash.StashExecuted += dispatch =>
        {
            long copper = 0;
            foreach ((string currency, long amount) in dispatch.Currencies)
                copper += Game.Inventory.CurrencyHoldings.ToCopper(currency, amount);
            SessionActivity.NoteCurrencyStashed(copper);
            TransactionHistory.NoteStash(dispatch.Currencies, dispatch.Items);
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
        // Phase 7 PR 7.22 — route walker over trapped exits through
        // the Phase 6 TrapDisarmManager.
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
        // PR 4.b — proactive pre-move sneak: `sn` goes out as the last
        // command before each walker move so the move itself is sneaked
        // (the reactive RoomTracker hook above only re-sneaks AFTER
        // arriving). Non-blocking; the settled-state guard in
        // StealthManager prevents a double-send when both paths fire.
        Walker.SetPreMoveHook(() => Stealth.RequestPreMoveStealth());
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

        // Phase 10 PR 10.14 — auto-equip trigger coordinator. Reads the same live
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

        // Phase 7 PR 7.8 — per-game-data-set loop catalogue. Loops live
        // under the active set's Loops/ folder, so the catalogue reloads
        // whenever the active set changes (wired below, alongside lairs,
        // since the two share one on-disk tree).
        Loops = new Game.Map.LoopManager(Bfs, RoomGraph, Log);

        // Phase 7 PR 7.9 — MegaMUD .mp loop importer. Pure resolution
        // service over the active graph; no per-profile state of its
        // own. The Manage dialog calls it on user "Import .mp".
        MpImporter = new Game.Map.MpFile.MpFileImporter(RoomGraph, Log);

        // Phase 7 PR 7.18 — Auto-Lair setup catalogue (per-set, mirrors
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
        Profile.ProfileLoaded += _ => RoomBlacklist.OnBbsPinApplied(ResolveActiveBbs()?.Name);
        Profile.BbsPinApplied += _ => RoomBlacklist.OnBbsPinApplied(ResolveActiveBbs()?.Name);
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

        // Phase 9 PR 9.E follow-up — auto-deposit reroute. Built here
        // (after the movement engines) so it can snapshot / stop / restart
        // the running Loop or Auto-Lair when CashManager's gate crosses.
        // Stop-and-restart, NOT a coordinator gate — a gate would block the
        // detour walk itself (same reasoning as PartyComebackManager). The
        // wire sender for the bank `dep` is bound by MainWindowViewModel
        // after telnet connects, alongside the Cash / Stash senders.
        // PR 10.8 — trainer-walk coordinator. Built here (after the movement
        // engines) so it can snapshot / stop / restart the running Loop or
        // Auto-Lair for a train detour, same as AutoDeposit. Manual Train Now
        // (CP tab) + the armed auto-train (live-exp threshold during a loop)
        // both route through it. Wire-sender bound in MainWindowViewModel.
        TrainerWalk = new Game.TrainerWalkManager(PlayerStats, Stats, GameData, Profile,
            RoomTracker, Bfs, Walker, LoopRunner, AutoLair, AutoTrain, Router, Log);
        // @train remote: trains in place (no walk) via the coordinator.
        TrainRemote = new Game.Remote.TrainHandler(RemoteCommands, TrainerWalk);
        // PR 10.8 — level-up announcer. Built after StatParser + the ProfileLoaded
        // Hydrate wiring so its baseline seed sees freshly-hydrated stats; watches
        // StatParser.ExperienceGained to broadcast newly-trainable levels.
        LevelUp = new Game.LevelUpAnnouncer(PlayerStats, Stats, GameData, Profile, Log);

        AutoDeposit = new Game.Cash.AutoDepositManager(
            Cash,
            readCash: () => ReadSection<Models.Profile.CashSettings>(Profile.Current, "Cash"),
            getSnapshot: () => Inventory.Snapshot,
            profile: Profile,
            tracker: RoomTracker,
            walker: Walker,
            loopRunner: LoopRunner,
            autoLair: AutoLair,
            stash: Stash,
            log: Log);
        // Phase 11 — bank deposits (already a copper value) join stash hides in
        // the Session Stats stashed/deposited figure. Phase 12 — and record the
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
    /// True when the named weapon resolves to a two-handed item in the active
    /// game-data set (<c>Items.WeaponType</c> 2H). Fed to
    /// <see cref="Game.Combat.CombatManager"/> so its weapon-swap can free the
    /// off-hand before wielding a two-hander. An unknown / unmatched name
    /// resolves to <c>false</c> — the swap then behaves as it always did.
    /// </summary>
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
    /// Live read of the master "Disable hangups" kill-switch from the
    /// char-tier General section — the same store the toolbar toggle
    /// writes. Wired into every automatic-hangup site (HangupHandler,
    /// RelogHandler, CleanupLogout; HealthManager reads it through its own
    /// General-settings provider) so flipping the toggle takes effect
    /// without restarting an engine.
    /// </summary>
    private bool ReadDisableHangups() =>
        ReadSection<Models.Profile.GeneralSettings>(Profile.Current, "General").DisableHangups;

    /// <summary>
    /// Feature 5 buff-duration source: map a 4-letter cast code to the
    /// buff's <see cref="Models.GameData.MessageRecord.CasterMessage"/>
    /// confirmation template plus its computed effect duration in
    /// seconds (<see cref="Game.Spells.SpellCalculator.Duration"/> at the
    /// live <see cref="Game.Spells.SpellbookState.Level"/>). Returns
    /// <c>null</c> for an unknown code, a code with no game-data message
    /// record, or a record with no caster line.
    /// </summary>
    /// <summary>
    /// PR 10.18 item-cast recast clock: resolve a Bless-slot
    /// <see cref="Game.Spells.ItemCastToken"/> to the cast item's spell effect
    /// duration in seconds (<see cref="Game.Spells.SpellCalculator.Duration"/>
    /// at the live <see cref="Game.Spells.SpellbookState.Level"/>). Returns
    /// <c>null</c> when the token doesn't resolve to a class cast item or the
    /// cast spell has no duration (i.e. it isn't a buff) — the director then
    /// won't fire it.
    /// </summary>
    private long? ItemCastDurationOf(string token)
    {
        if (!Game.Spells.ItemCastToken.TryResolve(token, Spellbook.GetCastItems(),
                out Game.Spells.ClassCastItem item))
            return null;
        if (SpellCatalog.GetFormulaByNumber(item.SpellNumber) is not { } formula)
            return null;
        long dur = Game.Spells.SpellCalculator.Duration(formula, Spellbook.Level);
        return dur > 0 ? dur : null;
    }

    /// <summary>
    /// Mana the item-cast buff named by <paramref name="token"/> draws on use —
    /// the cast spell's <c>Spells.ManaCost</c>, surfaced on the resolved
    /// <see cref="Game.Spells.ClassCastItem"/>. Drives the director's per-slot
    /// buff affordability: a free item-cast (cost 0) recasts regardless of mana;
    /// a paid one waits until the pool can cover it. Returns <c>null</c> when the
    /// token doesn't resolve to a class cast item (treated as free / never gated).
    /// </summary>
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
            return (rec.CasterMessage, Game.Spells.SpellCalculator.Duration(s.Formula, Spellbook.Level));
        }
        return null;
    }

    /// <summary>
    /// True when the buff with cast code <paramref name="castCode"/> targets
    /// the whole party at once. Resolved from the active set's
    /// <c>Spells.Targets</c> scope code: 13 = Full Party Area, 10 = Divided
    /// Party Area — both blanket the party in a single cast (verified against
    /// 1.11p, where every party-wide buff / heal uses 13; 10 is the divided
    /// variant). See <see cref="Game.GameData.LookupEnums.FormatSpellTargets"/>
    /// for the full label table. Unknown / non-party scopes ⇒ single-target.
    /// </summary>
    private bool IsPartyWideBuff(string castCode)
    {
        if (string.IsNullOrWhiteSpace(castCode)) return false;
        string target = castCode.Trim();
        foreach (Game.Spells.KnownSpell s in Spellbook.Available)
            if (string.Equals(s.Short.Trim(), target, StringComparison.OrdinalIgnoreCase))
                return s.Targets is 10 or 13;
        return false;
    }

    /// <summary>
    /// Build the cure-confirmation matchers
    /// <see cref="Game.Conditions.PartyAilmentTracker"/> uses to clear a
    /// member's ailment chip when OUR cure spell lands on them. Each
    /// configured cure spell (poison / disease / blindness / holds) is resolved
    /// via the live spellbook → its game-data
    /// <see cref="Models.GameData.MessageRecord.CasterMessage"/> →
    /// a <see cref="Game.Spells.CasterMessageMatcher"/>. Confusion has no
    /// cure spell, so it's never listed. Re-read on every call so
    /// re-configuring a cure spell takes effect immediately.
    /// </summary>
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

    /// <summary>
    /// Whether the player has a cure spell configured (a non-blank cast code
    /// in <see cref="Models.Profile.SpellsSettings"/>) for
    /// <paramref name="ailment"/>. The <see cref="Game.Conditions.AilmentSyncEngine"/>
    /// say-announce gate consults this — if we can self-cure an ailment we
    /// clear it silently rather than broadcasting <c>.@poisoned</c> /
    /// <c>.@held</c> to the party. Confusion has no cure field, so it always
    /// reports unconfigured.
    /// </summary>
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

    /// <summary>
    /// Resolve a cure spell's cast code to its game-data name plus the
    /// <see cref="Game.Spells.CasterMessageMatcher"/>s built from the spell's
    /// <see cref="Models.GameData.MessageRecord.CasterMessage"/> (OUR cast) and
    /// <see cref="Models.GameData.MessageRecord.WitnessMessage"/> (another
    /// member's cast we see in the room). The name is carried so the tracker
    /// confirms the spell slot, not just the target. The witness matcher is
    /// <c>null</c> when the record has no witness template. Returns <c>null</c>
    /// when the code is blank, unknown to the spellbook, has no message record,
    /// or the caster message has no string capture (nothing to confirm against).
    /// </summary>
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

    /// <summary>
    /// Feature 5 buff-duration source: map a fired AppliedMessage
    /// <see cref="Models.GameData.MessageRecord"/> back to the buff's
    /// 4-letter cast code so a confirmed self-buff starts / clears its
    /// duration timer. Resolves via the record's <c>Spells#N</c> link
    /// first, then falls back to a name match against the live spellbook.
    /// </summary>
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

    /// <summary>
    /// Find the active set's <see cref="Models.GameData.MessageRecord"/>
    /// for a spell — by <c>Spells#N</c> link first, then by name. Returns
    /// <c>null</c> when the catalogue has no record for the spell.
    /// </summary>
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

    /// <summary>
    /// Find the active set's <see cref="Models.GameData.MessageRecord"/> for an
    /// item — by <c>Items#N</c> link first, then by the item's resolved name.
    /// An item-proc record's <see cref="Models.GameData.MessageRecord.CasterMessage"/>
    /// is the line YOU see when the weapon procs. Returns <c>null</c> when no
    /// record anchors to the item. Mirrors <see cref="FindSpellMessage"/>.
    /// </summary>
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

    /// <summary>
    /// Compile the <see cref="Game.Spells.CasterMessageMatcher"/>s for the
    /// player's configured attack spells (the Combat tab's Normal + Alternate
    /// single-target damage slots) from each spell's game-data
    /// <see cref="Models.GameData.MessageRecord.CasterMessage"/>. Feeds
    /// <see cref="CombatSession"/> so a recognised cast tallies its own
    /// damage row instead of being miscounted as a melee swing. Re-read on each
    /// refresh so a slot change takes effect without a reconnect; a blank /
    /// unknown / message-less slot contributes nothing.
    /// </summary>
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

    /// <summary>
    /// Resolve one attack-spell slot name to its caster-message matcher: match
    /// the live spellbook by full name (the form a slot stores) or 4-letter
    /// cast code, take its game-data record's
    /// <see cref="Models.GameData.MessageRecord.CasterMessage"/>, and compile.
    /// Returns <c>null</c> when the name is blank, unknown to the spellbook, has
    /// no record, or the record has no usable caster template.
    /// </summary>
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

    /// <summary>
    /// Compile the <see cref="Game.Spells.CasterMessageMatcher"/> for the
    /// currently-wielded weapon's proc, from the item's game-data
    /// <see cref="Models.GameData.MessageRecord.CasterMessage"/>. Resolves the
    /// worn "Weapon Hand" item → <see cref="ItemNames"/> Number →
    /// <see cref="FindItemMessage"/>. Returns <c>null</c> when nothing's wielded
    /// or the weapon has no proc message. Cached on the weapon name.
    /// </summary>
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

    /// <summary>
    /// The given (first) name of <paramref name="fullName"/>, or <c>null</c>
    /// when unset. MajorMUD telepath / party-give syntax addresses by given
    /// name only, so <see cref="Game.Map.PartyPathItemGate"/>'s self-recipient
    /// is reduced the same way <see cref="Game.Remote.PartyBroadcaster"/>
    /// reduces its recipients.
    /// </summary>
    private static string? GivenNameOf(string? fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return null;
        int space = fullName.IndexOf(' ');
        return space >= 0 ? fullName[..space] : fullName;
    }

    /// <summary>
    /// True when the given item id is in the current inventory snapshot —
    /// carried or worn. The snapshot tracks names, so each carried / worn
    /// display-name is mapped back to its item Number via
    /// <see cref="ItemNames"/> (sharing the article/count normalization) and
    /// compared. Backs <see cref="PathItemDemand"/>'s possession check.
    /// </summary>
    private bool IsItemCarried(int itemId)
    {
        Game.Inventory.InventorySnapshot snap = Inventory.Snapshot;
        foreach (string name in snap.CarriedItems)
            if (ItemNames.FindByName(name) == itemId) return true;
        foreach (Game.Inventory.EquippedItem worn in snap.EquippedItems)
            if (ItemNames.FindByName(worn.Name) == itemId) return true;
        return false;
    }

    /// <summary>
    /// How many copies of <paramref name="itemId"/> the current snapshot holds
    /// (carried + worn). The carried list stores one entry per copy, so gives /
    /// receives accumulate as distinct entries; matching each display-name back
    /// to its Number and counting yields the live copy count the leader's
    /// party-provisioning redistribution needs. Backs
    /// <see cref="Game.Map.PartyPathItemGate"/>'s self-count seam.
    /// </summary>
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

    /// <summary>
    /// Room keys of every shop in the live graph that stocks
    /// <paramref name="itemId"/> — the join of <see cref="ShopStock"/> (which
    /// shops sell it) against <see cref="RoomGraph"/> (which rooms host those
    /// shops). Backs <see cref="PathItemShopRouter"/>'s detour-target search.
    /// Only rooms present in the active graph can be walk targets, so shops
    /// whose room isn't loaded are naturally excluded.
    /// </summary>
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

    /// <summary>
    /// Every spawn site of a monster that drops <paramref name="itemId"/> —
    /// the flatten of <see cref="MonsterDrops"/>'s droppers × each dropper's
    /// spawn rooms, tagged with the monster and drop chance for the reroute
    /// prompt. Backs <see cref="MonsterDropRouter"/>'s nearest-spawn search.
    /// Computed lazily (only when a no-shop need fires), so the per-item
    /// cross-product is never materialised at load time.
    /// </summary>
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
    /// Resolve a single carried-inventory entry for <see cref="Stash"/>:
    /// map the loose carry wording to an item Number, read its verbatim
    /// Name, and resolve the per-character
    /// <see cref="Models.GameData.ItemOverlay.AutoStash"/> override
    /// (Defaults seed → Global → BBS → Char). Returns the canonical name
    /// to <c>hide</c> when the item is flagged for auto-stash, else
    /// <c>null</c> so the stash engine leaves it in the pack. AutoStash
    /// defaults to <c>false</c> — stashing is opt-in per item.
    /// </summary>
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
        // Same "If leading, wait only" window also caps the invite-as-wait-signal
        // loop hold before we uninvite a no-show.
        AutoParty.InviteWaitWindow = TimeSpan.FromSeconds(Math.Clamp(dto.IfLeadingWaitTotalSec, 0, 3600));
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

    /// <summary>
    /// Push the loaded character's <see cref="Models.Profile.TalkSettings"/>
    /// into the live <see cref="RemoteCommands"/> engine. Same shape +
    /// rationale as <see cref="ApplyPartyFromActiveProfile"/>.
    /// </summary>
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

    /// <summary>
    /// Drop any persisted reference to a just-deleted game-data set so a
    /// later resolve doesn't point <see cref="GameData"/> at a folder
    /// that's gone. Clears the global
    /// <see cref="Models.Settings.GlobalSettings.DefaultGameDataSet"/> and
    /// every BBS profile's
    /// <see cref="Models.Settings.BbsProfile.ActiveGameDataSet"/> that
    /// named it. Wired into <see cref="GameDataSetManager"/> as its
    /// delete callback.
    /// </summary>
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
