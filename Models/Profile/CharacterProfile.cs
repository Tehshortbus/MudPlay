using System.Text.Json;
using MudPlay.Game.Map;

namespace MudPlay.Models.Profile;

// Root DTO for BBS/{bbs}/profiles/{char}/profile.json — the Character tier of the
// settings hierarchy. Per-character workspace: auth info, settings deltas,
// macros / triggers / events / death records / equipment sets / build presets /
// quest state / etc.
//
// The profile filename (sans .json) is the character's identifier inside
// MudPlay. The in-game character name may differ — see Name.
public sealed class CharacterProfile
{
    // The schema version a freshly-authored (fully-migrated) profile carries.
    // Bump in lockstep with a new Services.ProfileMigrations step.
    public const int CurrentSchemaVersion = 10;

    // JSON schema version (see GlobalSettings.SchemaVersion for the contract).
    // A fresh profile is authored at CurrentSchemaVersion so it never triggers a
    // migration; older on-disk profiles carry a lower number and are upgraded on
    // load by Services.ProfileMigrations.
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    // In-game character name. Usually matches the profile filename but a user
    // may give two profiles the same in-game name on different BBSes (same
    // character name across two unrelated realms).
    public string Name { get; set; } = string.Empty;

    // The realm on its BBS this character plays (a RealmProfile name). The realm
    // picks the game data, menu commands and death floor, and where everything
    // collected while playing is stored, shared with the other characters on the
    // same realm. null / unknown means the BBS's first realm.
    public string? Realm { get; set; }

    // Per-tab settings deltas at the Character tier — same shape as
    // GlobalSettings.Settings. Anything the user pinned to "only for this
    // character."
    public Dictionary<string, JsonElement>? Settings { get; set; }

    // User-defined incoming-text triggers. Per-character so the pattern + action
    // list follows the character that authored it. Loaded into TriggerEngine on
    // profile load. Named capture variables emitted by matches are
    // app-session-scoped in the engine, not persisted here.
    public List<GameData.Trigger>? Triggers { get; set; }

    // The trigger list is this character's whole list. False on a profile that has
    // not yet taken its copy of the triggers that used to be kept with the game data
    // (or the default ones, for a new character); TriggerEngine does that on load.
    public bool OwnsTriggers { get; set; }

    // The profile is older than per-character favourites and triggers, so the copy
    // it takes is of the list every character on its game data shared. A character
    // made since starts from the bundled starters instead. Set once by the schema
    // upgrade and never cleared.
    public bool PredatesOwnLists { get; set; }

    // GOTO favourites and the empty folders made for them. Per character, kept by
    // FavoritesStore. null = not taken yet (see PredatesOwnLists); an empty list is
    // a character with no favourites.
    public List<FavoriteRoom>? Favorites { get; set; }
    public List<string>? FavoriteFolders { get; set; }

    // The saved loops and auto-lair setups this character has favourited, by name.
    // The loops and setups themselves stay with the game data, shared; which of
    // them are favourites is each character's own, kept by LoopFavoritesStore.
    // null = not taken yet: a character from before the flags moved here takes the
    // ones its game data's files carried, the first time it loads with them.
    public List<string>? FavoriteLoops { get; set; }
    public List<string>? FavoriteLairSetups { get; set; }

    // This character's "Entire Loop Settings" (only attack in lair rooms, the wait to
    // enter lairs), by loop name. The loops themselves stay with the game data,
    // shared; how each of them is run is each character's own, kept by
    // LoopSettingsStore (user, 2026-10-10). Holds only the loops this character has
    // set either for: one with no entry runs with what its loop file holds. null =
    // none set.
    public Dictionary<string, LoopSettingsChoice>? LoopSettings { get; set; }

    // User-defined outgoing-text aliases. Per-character; loaded into AliasEngine
    // on profile load. Substitution is positional only ({0}, {1}, …); the trigger
    // engine's captured variables are not shared with aliases.
    public List<GameData.Alias>? Aliases { get; set; }

    // User-defined keybinds. Per-character; loaded into MacroStore on profile
    // load. MacroDispatcher intercepts keystrokes on TerminalControl +
    // ConversationWindow's input field and dispatches the matched command in
    // place of the raw key.
    public List<GameData.Macro>? Macros { get; set; }

    // User-defined scheduled / lifecycle events. Per-character; loaded into
    // Game.Events.EventManager on profile load (EventTriggerType and
    // EventActionType list what an event can do). Per-event Disabled flag. null
    // means no events configured.
    public List<GameData.ScheduledEvent>? Events { get; set; }

    // Master "stop firing every event" switch. When true, EventManager.Fire
    // short-circuits for every event regardless of its own Disabled flag. Useful
    // for "switch off all automation for the next 10 minutes" without
    // un-checking every row. Persists per-character. Defaults to false on a
    // fresh profile.
    public bool EventsGloballyDisabled { get; set; }

    // The bounds of the queue events wait in while another event runs (Settings →
    // Events): how many may wait, for how many minutes before one is dropped, and
    // how many minutes a paused event may stand with nothing moving before it is
    // given up. Null is the default (EventManager.DefaultMaxQueued /
    // DefaultMaxQueueWaitMinutes / DefaultSuspendedIdleMinutes). Per-character,
    // like the events themselves.
    public int? EventQueueLimit { get; set; }
    public int? EventQueueWaitMinutes { get; set; }
    public int? EventSuspendedIdleMinutes { get; set; }

    // Per-character keybindings for built-in app actions (toolbar + menu
    // shortcuts). Sparse — only entries the user has overridden from the seed
    // defaults get persisted. KeybindingStore fills in the rest from
    // KeybindingStore.DefaultBindings on load, and prunes back to non-defaults
    // at save time so a fresh profile that never touched the keybind editor
    // leaves this null.
    public Dictionary<BuiltInAction, KeyChord>? BuiltInKeybindings { get; set; }

    // Per-player customisations the loaded character has authored —
    // remote-command permissions, auto-party toggles, the don't-auto-delete
    // flag, notes. Keyed by player display name (case-insensitive on read).
    // Only non-default entries are persisted: a fresh profile that's never
    // opened the player edit dialog leaves this null, and pristine
    // "all unchecked" entries are pruned at save time. The realm's observation
    // rows live separately (AppPaths.RealmPlayersFile), so a customisation on
    // character A doesn't leak into character B on the same realm.
    public Dictionary<string, GameData.PlayerCustomization>? PlayerCustomizations { get; set; }

    // Per-BBS login credentials for this character. Keyed by BBS name (matches
    // Settings.BbsProfile.Name). Username and password live inline on
    // BbsCredentials, both AES-GCM encrypted (PasswordProtector).
    public Dictionary<string, BbsCredentials>? BbsCredentials { get; set; }

    // In-game set suicide password, encrypted with PasswordProtector. Captured
    // passively while the user is in the password-entry flow (see
    // Game.SuicidePasswordTracker) and consumed by the @suicide remote-command
    // path. null when no password has been observed yet OR after the user runs
    // pro and the "no password set" line confirms the realm-side state no longer
    // matches our cached value.
    public string? EncryptedSuicidePassword { get; set; }

    // Persisted layout of the Session Stats window's panels — the user's chosen
    // panel order and hidden set. Populated by SessionStatsLayoutStore on
    // profile save and applied when the window opens. null means "use the
    // default order, all panels visible".
    public SessionStatsLayout? SessionStatsLayout { get; set; }

    // Persisted size + screen position per top-level window, keyed by stable id
    // ("main", "backscroll", "settings", etc.). Populated by WindowLayoutStore
    // on profile save and consumed on every window Opened. null / missing
    // entries mean "use the window's XAML defaults", so the user only ends up
    // with a saved position once they've actually moved / resized a window.
    public Dictionary<string, WindowBounds>? WindowBounds { get; set; }

    // Ids of the windows open when the profile was last saved (the main window
    // aside), so loading it reopens them where WindowBounds puts them. null = never
    // recorded (a profile from before this was kept) — loading leaves windows alone.
    public List<string>? OpenWindows { get; set; }

    // The Navigation window's map-collapse toggle (the ◀/▶ side-panel button).
    // true = the user last left the side chrome collapsed so the map fills the
    // window. Read by NavigationViewModel when the window opens so it reopens in
    // the same mode — and because the collapsed layout lowers the window's
    // minimum size, this must be restored before WindowLayoutStore reapplies the
    // saved bounds, or an expanded min-width clamps the saved (narrower) width
    // back up. Defaults false (expanded) on a fresh profile.
    public bool NavMapCollapsed { get; set; }

    // The Navigation map's lair-highlight mode (the "Lairs" chip cycle:
    // uniform → heat → count → heat+count → off). Written by NavigationViewModel
    // whenever the user changes it, read when the window opens so the map paints
    // lairs the way they left it. Serialized by name, defaults to Uniform.
    public LairDisplayMode NavLairMode { get; set; } = LairDisplayMode.Uniform;

    // The Navigation map's room-spell overlay mode (the "Spells" chip cycle), as
    // every client reads it: mono, by name or off. Persisted per-character like
    // NavLairMode. Serialized by name, defaults to Mono (the original flat-purple
    // "has a room spell" cue). Read and written through SpellDisplayModes, which
    // keeps a later mode out of this key.
    public SpellDisplayMode NavSpellMode { get; set; } = SpellDisplayMode.Mono;

    // The name of a room-spell overlay mode NavSpellMode can't hold (by teleport),
    // or null when NavSpellMode is the whole answer. A plain string so a client
    // that doesn't know the name skips it instead of failing the profile load.
    public string? NavSpellOverlay { get; set; }

    // How the Navigation map draws a running loop (the "Loop lines" chip cycle:
    // line + numbered steps → line only → off). Persisted per-character like
    // NavLairMode. Serialized by name, defaults to Steps.
    public LoopLinesMode NavLoopLinesMode { get; set; } = LoopLinesMode.Steps;

    // The Navigation map's level-gate overlay (rooms holding a level gate,
    // marked with an amber corner wedge). Persisted per-character like the two
    // above. Defaults ON — it costs one scan per game-data set swap, and a gate
    // is worth seeing before you walk to it.
    public bool NavShowLevelGates { get; set; } = true;

    // Persisted left-pane proportions for resizable two-pane dialogs keyed by
    // stable id (e.g. "MonsterEditDialog"). Each value is the fraction (0.0–1.0)
    // of the splittable area occupied by the LEFT pane at the user's last close.
    // Populated by SplitterLayoutStore on profile save and applied on every
    // dialog open. null / missing entries mean "use the XAML defaults".
    // Which option each split toolbar button runs (its ▾ pick) — toolbar ActionId →
    // the picked sub-action's key (ToolbarItemCatalogue.SubAction.Key). Kept apart from
    // the toolbar layout so reordering or editing the layout never drops a pick.
    public Dictionary<string, string>? ToolbarSplitChoices { get; set; }

    public Dictionary<string, double>? SplitterRatios { get; set; }

    // Snapshot of the most recent stat + exp observations. Written by
    // Game.StatParser after each successful capture; hydrated back into the live
    // Game.PlayerStats on ProfileService.ProfileLoaded so the status bar /
    // @-command query handlers / Workshop view start the next session with the
    // user's last-known values instead of zeros. null until the first capture.
    public LastKnownStats? LastKnownStats { get; set; }

    // Max HP / power pool read off a `stat` screen with the Default gear set worn —
    // the rest engine's basis. null until the first such screen.
    public DefaultPoolBaseline? DefaultPoolBaseline { get; set; }

    // Set on a copied profile: its settings came from another character, so what the
    // game reports about this one (stats, the Default-gear maxima, inventory) hasn't
    // been read yet. Cleared by Game.ProfileStateVerifier once it has.
    public bool StateUnverified { get; set; }

    // Snapshot of the most recent carry-weight reading. Written by
    // Game.Inventory.InventoryManager on ProfileSaving and rehydrated on
    // ProfileService.ProfileLoaded so the travel-cost models / hop-timing
    // calibrator / Workshop start the next session with the last-known
    // encumbrance bracket instead of Unknown. null until the first `i` capture.
    public LastKnownEncumbrance? LastKnownEncumbrance { get; set; }

    // The items held the last time the character was known to be in the game.
    // Written by Game.Inventory.HangupItemRecheck on ProfileSaving and read by it
    // on the next connect, on a realm that drops items for a hang-up. null until
    // an inventory has been read in the game, and again from a death to the next
    // inventory read.
    public HeldAtDisconnect? HeldAtDisconnect { get; set; }

    // The buff timers the character had when it was last out of the game, each with
    // the time it had left. Written by Game.Spells.BuffTimerKeeper on ProfileSaving
    // while the character is out of the game and read by it on the next load; null
    // while the character is in the game, and when it had none.
    public SavedBuffTimers? BuffTimers { get; set; }

    // Full names of the spells this character has learned — the persisted Spell
    // Book obtained set, so the learned checkmarks survive across sessions
    // instead of blanking until the next in-game `spells` / `pow` poll. Stored
    // as names (not Spells.Number) so they re-resolve cleanly even if the active
    // game-data set version renumbers rows. Captured on ProfileSaving from
    // Game.Spells.SpellbookState and restored on ProfileLoaded once the class is
    // seeded. null / empty means nothing learned yet (or a non-magery class).
    public List<string>? LearnedSpells { get; set; }

    // Rooms the walker / loop / auto-lair scheduler must not route through.
    // Per-character only (each player picks their own no-go list) — does not
    // flow through SettingsResolver. Persisted as a flat list of RoomRef;
    // consumed at runtime by MovementFilter. null or empty = no rooms avoided.
    public List<RoomRef>? AvoidedRooms { get; set; }

    // This character's Stop before / Grab All choices on the Bosses tab, by boss
    // Name. The boss list itself is the realm's; these two ticks are each
    // character's own (user, 2026-10-08). null = never set: the first load takes
    // the choices the realm's list held from when it carried them (BossStore).
    public Dictionary<string, BossFlagChoice>? BossFlags { get; set; }

    // Rooms the user has flagged as drop-off / stash points. Per-character only.
    // null or empty = no stash rooms flagged.
    public List<RoomRef>? StashRooms { get; set; }

    // Believed copper per stash room, keyed "map/room", from when the tally was kept
    // per character. Read once on load and moved to the realm's shared tally
    // (Services.StashBalanceStore); nothing writes it any more.
    public Dictionary<string, long>? StashedCopper { get; set; }

    // Client-tracked use counts for limited-use items, keyed by item number. Populated
    // on STOCK realms (which print no "Uses remaining" on look) so remaining = max −
    // Used can be shown; a rechargeable item restocks at cleanup. null / empty = none
    // tracked. See ItemUseCountTracker.
    public Dictionary<int, ItemUseRecord>? ItemUseCounts { get; set; }

    // Client-tracked remaining charges for limited-use items, keyed by item number.
    // Populated ON PARADIGM (which prints "Uses remaining: N" on look) — the count is
    // read from the game, not derived. A rechargeable item restocks to max at cleanup;
    // a finite item's count persists depleted forever. null / empty = none tracked.
    // See ItemChargeTracker. (Stock uses ItemUseCounts above instead.)
    public Dictionary<int, ItemChargeRecord>? ItemCharges { get; set; }

    // Say what each opened chest gave (items and coin) to the room. The checkbox in
    // the Chest Offload tab; off unless the player ticks it. See ChestOpenTracker.
    public bool SayChestLootToRoom { get; set; }

    // The Chest Offload list: chest loot not yet sold, dropped or cleared, and the
    // chests' coin. null = nothing listed. See ChestOpenTracker.
    public ChestLootRecord? ChestLoot { get; set; }

    // LEGACY Roomba Mode fields — superseded by the realm's RoombaSettings
    // (AppPaths.RealmRoombaFile; see GhRoomLabelStore) since every character
    // on a realm shares one gang house. Kept ONLY so GhRoomLabelStore can lift an
    // existing user's pre-upgrade data into the realm file on first load
    // after upgrading; cleared (nulled + saved) once migrated. Never written to
    // after that point — do not read these directly outside that migration.
    public List<GhRoomLabel>? GhRoomLabels { get; set; }
    public int? GhSearchesPerRoom { get; set; }
    public bool? GhSearchForHidden { get; set; }

    // Gang-house rooms THIS character actively manages — the ones Start Sweep /
    // Start Inventory visit. The room labels themselves are shared per realm (every
    // character sees the same labeled rooms), but which subset a character sweeps
    // is per-character, so alts in different gang houses on one realm each manage
    // their own house. Each entry is a "map/room" coordinate string. Maintained by
    // GhManagedRoomStore; null or empty = this character manages nothing yet.
    public List<string>? GhManagedRooms { get; set; }

    // What a Roomba sweep still had to do when it ended — items already in the
    // pack plus moves it hadn't got to yet. Persisted because an aborted sweep
    // otherwise leaves its load in your inventory with only the sweep (now gone)
    // knowing where any of it belonged, and throws away a survey that costs a full
    // lap of the circuit to rebuild. Carried entries are re-adopted (after an
    // inventory check) by any sweep; the rest are what Resume picks up to skip the
    // scan. Maintained by GhSuspendedSweepStore; null or empty = the last sweep
    // finished cleanly, which is the normal case.
    public List<GhSuspendedMove>? GhUnfinishedSweep { get; set; }

    // The game-data set GhUnfinishedSweep's rooms are numbers of. Null on a record
    // saved before it was stamped.
    public string? GhUnfinishedSweepGameData { get; set; }

    // Recent walk-to destinations, newest first, capped at 10. Each entry is a
    // "map/room" coordinate string. Maintained by GotoHistoryStore; drives the
    // Navigation goto-button dropdown. null or empty = no history yet.
    public List<string>? GotoHistory { get; set; }

    // Widths the user dragged the Bosses tab's columns to, by column key. A column
    // with no entry keeps its built-in width.
    public Dictionary<string, double>? BossColumnWidths { get; set; }

    // Per-table column-visibility choices in the Game Data Browser, keyed by the
    // table's Title (e.g. "Monsters"). Value = the ordered list of column keys the
    // user wants shown for that table. A missing table entry = its built-in default
    // columns. Lets each character tailor which columns each table surfaces (e.g.
    // opt a monster's per-element resist columns into the grid). Maintained by the
    // shared table view-model's column picker; null = every table at its defaults.
    public Dictionary<string, List<string>>? TableColumnLayouts { get; set; }

    // Column keys the character has switched OFF in the Workshop's Level Projection
    // grid (LevelProjectionColumn.All keys). Stored as the exceptions rather than
    // the selection so a column added in a later release shows up for an existing
    // character instead of staying hidden because its key predates the save.
    // null = the picker was never touched, so the built-in default set applies.
    public List<string>? LevelProjectionHiddenColumns { get; set; }

    // Last room the character was known to be standing in. Hydrated from
    // Game.Map.RoomTracker on a successful manual or auto locate; saved with the
    // rest of the profile and used as the initial Navigation map origin on the
    // next session so the user opens the map already centred on where they left
    // off. null until the first successful locate.
    public RoomRef? LastKnownRoom { get; set; }

    // Ordered list of move commands sent since LastKnownRoom was Confirmed — the
    // tracker's replay-from-last-Confirmed input. Written by Game.Map.RoomTracker
    // on every successful move, cleared on the next Confirmed transition (the new
    // LastKnownRoom takes over). Hydrated on profile load so the next session can
    // replay through the graph and recover position without manual intervention.
    // null or empty = no pending steps to replay.
    public List<DirectionDto>? RecentSteps { get; set; }

    // Append-only history of deaths observed for this character. Written by
    // Game.DeathDetector when the "You now have N lives remaining." message
    // arrives; consumed by the Workshop DEATH section. null / empty means no
    // deaths yet (the lucky case).
    public List<DeathRecord>? DeathHistory { get; set; }

    // Other players' corpses this character recovered and has not handed back yet
    // (Game.Recovery.CorpseHandoffManager). null / empty when there are none.
    public List<CorpseHandoffRecord>? CorpseHandoffs { get; set; }

    // Per-character log of players this character has seen — one aggregated row
    // per player (given name), each carrying the last-seen time, the last room
    // they were seen in, and the running total sighting count. Written by
    // Game.PlayerSightingTracker on "Also here:" matches and room walk-ins;
    // surfaced by the Session Stats → Players Seen window. null / empty means no
    // players seen yet.
    public List<PlayerSighting>? PlayersSeen { get; set; }

    // Per-character log of actual combat outcomes observed against specific
    // monsters — landed/whiffed swing counts and damage extent, and confirmed
    // "no effect" discoveries (physical Magical-requirement gate, spell
    // SpellImmunity gate). Written by Game.Combat.MonsterObservationTracker;
    // surfaced by Monster Intel's "Your Observations" section, kept visibly
    // separate from the authoritative MDB facts the rest of that window
    // shows. null / empty means no observations yet.
    public List<MonsterObservation>? MonsterObservations { get; set; }

    // When true, the DEATH-recovery flow grabs lost items (and re-equips what
    // was worn at death) automatically whenever the character re-enters a room
    // holding one of their own deathpiles — regardless of the item's auto-get
    // policy. Per-character. Defaults false.
    public bool DeathAutoRecover { get; set; }

    // When true, items recovered from a deathpile that were equipped at the
    // moment of death are automatically re-equipped after pickup. Per-character.
    // Defaults false.
    public bool DeathAutoEquip { get; set; }

    // The editable per-level CP-allocation plan (Workshop CP Allocation tab) —
    // the target stats at each planned level above the current one, oldest →
    // newest. Drives the CP grid now and auto-train / @train. null / empty means
    // no plan saved.
    public List<CpPlanEntry>? CharacterPlan { get; set; }

    // Per-character quest completion state (Workshop Quest Status tab), keyed by
    // the crawler's (flag, step) quest identity. Records which quests / alignment
    // bands the character has finished and, for single-part quests, which steps
    // are ticked. Drives the bonus fold into Character Info. null / empty means
    // nothing completed yet.
    public List<QuestProgress>? QuestLog { get; set; }

    // The step ticks in QuestLog were saved before the drafted checklists gained their
    // kill lines, so they still count checkboxes the way the draft read then. Set by the
    // schema upgrade for a profile that holds ticks; QuestTickMigration moves them onto
    // the same steps the first time a game-data set is there to draft from, and clears
    // this.
    public bool QuestTicksPredateKillSteps { get; set; }

    // The shape of the drafted checklists the step ticks in QuestLog were made against
    // (QuestTickMigration.CurrentDraftRevision). A profile from before it was recorded
    // reads 0; QuestTickMigration moves its ticks onto the same steps of today's draft
    // the first time a game-data set is there to draft from, and sets this. A new
    // profile, holding no ticks yet, goes through the same pass as a no-op.
    public int QuestDraftRevision { get; set; }

    // Local date the login-time quest-flag sync last ran for this character. The
    // sync (the `abil` burst on Paradigm / `sys ... abil` on Stock) runs at most
    // ONCE PER DAY — a relog later the same day skips it — so it's stamped here
    // whenever a real check completes and compared against today on the next run.
    // null = never synced (so the next eligible login runs it).
    public System.DateOnly? LastQuestFlagSyncDate { get; set; }

    // Announce "[<quest> Quest is Now Available]" to the terminal when training crosses a
    // quest's minimum level, and dump the currently-available list once at login. Toggled
    // from the top of the Quest Status tab. Defaults on.
    public bool AnnounceAvailableQuests { get; set; } = true;

    // Which alignment quest chains this character is doing — the Evil/Neutral/Good
    // checkboxes at the top of the Quest Status tab. In game you commit to one
    // alignment quest chain and are then locked to it regardless of your live
    // alignment, so which alignment quests you can complete is a declared player
    // choice, not something inferred. An alignment-gated quest (RequiredAlignment)
    // is eligible only when its bucket is enabled here. All default off — a fresh
    // character declares nothing, so no alignment quests show until the player opts
    // into a path (never auto-checked).
    public bool QuestAlignGood { get; set; }
    public bool QuestAlignNeutral { get; set; }
    public bool QuestAlignEvil { get; set; }

    // Per-character equipment-manager state (Workshop Equipment tab) — saved
    // gear sets and the auto-equip triggers between them. Drives @equip <set>,
    // the per-slot editor, and trigger evaluation. null means nothing configured
    // yet.
    public EquipmentSettings? Equipment { get; set; }

    // Per-character buff plan — every automated buff, self and party alike, each
    // with its own recast timer and targeting (BuffSettings). Configured live in
    // the Buff Watchdog window, not the Settings tab. The property keeps its old
    // name for storage compatibility. null means nothing configured yet.
    public BuffSettings? PartyBuffs { get; set; }

    // Per-character combat profiles (Settings → Combat) — quick-swap snapshots of
    // a whole combat posture (spells, verbs, thresholds, action order, weapons,
    // the Health tab), plus which one is active. Targeting stays shared, and any
    // settings group can be shared instead (CombatProfileSettings.SharedGroups).
    // null / empty is seeded on first load with one profile captured from the
    // current settings. Managed by CombatProfileManager.
    public CombatProfileSettings? CombatProfiles { get; set; }

    // Per-character route-Details monster colouring — the "Color monsters by hit %"
    // toggle and the green / yellow / red band split, set from the Details window's
    // own checkbox + slider. Saved here so each character keeps its own preference
    // and the window opens the way that character last left it. null means off with
    // the factory 15 / 45 bands.
    public MonsterHitColorSettings? MonsterHitColors { get; set; }

    // How the Buff Watchdog window arranges its config table vs the timer bars —
    // stacked (config top / bottom) or side-by-side (config left / right).
    public BuffWatchdogLayout BuffWatchdogLayout { get; set; } = BuffWatchdogLayout.ConfigTop;

    // Whether the Buff Watchdog's config panel is collapsed (hidden, bars-only), toggled
    // by the button on the timer-bar side. Sticky per character so the window reopens the
    // way it was left.
    public bool BuffWatchdogConfigCollapsed { get; set; }

    // Where the user dragged the Buff Watchdog's config/bars splitter — the config pane's
    // fixed extent in DIPs, plus whether it was a vertical (config top/bottom → a height)
    // or horizontal (config left/right → a width) split, so a persisted height isn't
    // applied as a width after a layout change. 0 = never dragged (use the default).
    public double BuffWatchdogConfigExtent { get; set; }
    public bool BuffWatchdogConfigExtentVertical { get; set; }

    // Given name of the party leader we were following, remembered so a
    // follower can auto-rejoin after an unexpected drop. Written through by
    // PartyRejoinCoordinator whenever follower membership changes (set on
    // follow, cleared on a deliberate leave) and cleared on clean shutdown, so a
    // populated value on the next launch means the client crashed mid-party —
    // the cue to telepath @comeback and let the leader own the pickup. null
    // means no party to rejoin.
    public string? PendingReconnectLeader { get; set; }
}
