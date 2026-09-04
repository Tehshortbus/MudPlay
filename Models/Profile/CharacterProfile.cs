using System.Text.Json;
using MudPlay.Game.Map;

namespace MudPlay.Models.Profile;

// Root DTO for Data/profiles/{char-name}.json — the Character tier of the
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
    public const int CurrentSchemaVersion = 4;

    // JSON schema version (see GlobalSettings.SchemaVersion for the contract).
    // A fresh profile is authored at CurrentSchemaVersion so it never triggers a
    // migration; older on-disk profiles carry a lower number and are upgraded on
    // load by Services.ProfileMigrations.
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    // In-game character name. Usually matches the profile filename but a user
    // may give two profiles the same in-game name on different BBSes (same
    // character name across two unrelated realms).
    public string Name { get; set; } = string.Empty;

    // Per-tab settings deltas at the Character tier — same shape as
    // GlobalSettings.Settings. Anything the user pinned to "only for this
    // character."
    public Dictionary<string, JsonElement>? Settings { get; set; }

    // User-defined incoming-text triggers. Per-character so the pattern + action
    // list follows the character that authored it. Loaded into TriggerEngine on
    // profile load. Named capture variables emitted by matches are
    // app-session-scoped in the engine, not persisted here.
    public List<GameData.Trigger>? Triggers { get; set; }

    // User-defined outgoing-text aliases. Per-character; loaded into AliasEngine
    // on profile load. Variables substitution inside an alias's expansion reads
    // from the shared session-scoped variable store the trigger engine maintains.
    public List<GameData.Alias>? Aliases { get; set; }

    // User-defined keybinds. Per-character; loaded into MacroStore on profile
    // load. The MacroManager engine intercepts keystrokes on TerminalControl +
    // ConversationWindow's input field and dispatches the matched command in
    // place of the raw key.
    public List<GameData.Macro>? Macros { get; set; }

    // User-defined scheduled / lifecycle events. Per-character; loaded into
    // Game.Events.EventManager on profile load. Trigger types: Logon / Logoff /
    // Re-log / AtTime / Every. Action types: Walk to / Loop / Auto-lair /
    // Command (with ^M / ; multi-fire). Per-event Disabled flag. null means no
    // events configured.
    public List<GameData.ScheduledEvent>? Events { get; set; }

    // Master "stop firing every event" switch. When true, EventManager.Fire
    // short-circuits for every event regardless of its own Disabled flag. Useful
    // for "switch off all automation for the next 10 minutes" without
    // un-checking every row. Persists per-character. Defaults to false on a
    // fresh profile.
    public bool EventsGloballyDisabled { get; set; }

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
    // "all unchecked" entries are pruned at save time. Per-BBS observation rows
    // live separately at Data/BBS/{name}/players.json so a customisation on
    // character A doesn't leak into character B even when both play the same BBS.
    public Dictionary<string, GameData.PlayerCustomization>? PlayerCustomizations { get; set; }

    // Persisted floating-panel layouts keyed by panel id. Populated by
    // FloatingPanelHost on profile save; consumed on profile load. null means
    // "no layouts captured yet" — panels default to PanelState.Docked.
    public Dictionary<string, PanelLayout>? PanelLayouts { get; set; }

    // Per-BBS login credentials for this character. Keyed by BBS name (matches
    // Settings.BbsProfile.Name). Username is plaintext; password lives inline on
    // BbsCredentials.EncryptedPassword (AES-GCM, decrypted via PasswordProtector).
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

    // The Navigation map's room-spell overlay mode (the "Spells" chip cycle:
    // mono → by name → off). Persisted per-character like NavLairMode. Serialized by
    // name, defaults to Mono (the original flat-purple "has a room spell" cue).
    public SpellDisplayMode NavSpellMode { get; set; } = SpellDisplayMode.Mono;

    // Persisted left-pane proportions for resizable two-pane dialogs keyed by
    // stable id (e.g. "MonsterEditDialog"). Each value is the fraction (0.0–1.0)
    // of the splittable area occupied by the LEFT pane at the user's last close.
    // Populated by SplitterLayoutStore on profile save and applied on every
    // dialog open. null / missing entries mean "use the XAML defaults".
    public Dictionary<string, double>? SplitterRatios { get; set; }

    // Snapshot of the most recent stat + exp observations. Written by
    // Game.StatParser after each successful capture; hydrated back into the live
    // Game.PlayerStats on ProfileService.ProfileLoaded so the status bar /
    // @-command query handlers / Workshop view start the next session with the
    // user's last-known values instead of zeros. null until the first capture.
    public LastKnownStats? LastKnownStats { get; set; }

    // Snapshot of the most recent carry-weight reading. Written by
    // Game.Inventory.InventoryManager on ProfileSaving and rehydrated on
    // ProfileService.ProfileLoaded so the travel-cost models / hop-timing
    // calibrator / Workshop start the next session with the last-known
    // encumbrance bracket instead of Unknown. null until the first `i` capture.
    public LastKnownEncumbrance? LastKnownEncumbrance { get; set; }

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

    // Rooms the user has flagged as drop-off / stash points. Per-character only.
    // null or empty = no stash rooms flagged.
    public List<RoomRef>? StashRooms { get; set; }

    // LEGACY Roomba Mode fields — superseded by the BBS-tier RoombaSettings
    // (Data/BBS/{bbs}/roomba.json; see GhRoomLabelStore) since every character
    // on a BBS shares one gang house. Kept ONLY so GhRoomLabelStore can lift an
    // existing user's pre-upgrade data into the new BBS-tier file on first load
    // after upgrading; cleared (nulled + saved) once migrated. Never written to
    // after that point — do not read these directly outside that migration.
    public List<GhRoomLabel>? GhRoomLabels { get; set; }
    public int? GhSearchesPerRoom { get; set; }
    public bool? GhSearchForHidden { get; set; }

    // Gang-house rooms THIS character actively manages — the ones Start Sweep /
    // Start Inventory visit. The room labels themselves are shared per-BBS (every
    // character sees the same labeled rooms), but which subset a character sweeps
    // is per-character, so alts in different gang houses on one BBS each manage
    // their own house. Each entry is a "map/room" coordinate string. Maintained by
    // GhManagedRoomStore; null or empty = this character manages nothing yet.
    public List<string>? GhManagedRooms { get; set; }

    // Recent walk-to destinations, newest first, capped at 10. Each entry is a
    // "map/room" coordinate string. Maintained by GotoHistoryStore; drives the
    // Navigation goto-button dropdown. null or empty = no history yet.
    public List<string>? GotoHistory { get; set; }

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
    // policy. Per-character. Defaults false. The item-grab side is inert until
    // the inventory tracker lands; the toggle persists now so the preference
    // survives that gap.
    public bool DeathAutoRecover { get; set; }

    // When true, items recovered from a deathpile that were equipped at the
    // moment of death are automatically re-equipped after pickup. Per-character.
    // Defaults false. Inert until inventory tracking records what was worn at
    // death.
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
    // gear sets and the auto-equip triggers between them. Drives @equip-<set>,
    // the per-slot editor, and trigger evaluation. null means nothing configured
    // yet.
    public EquipmentSettings? Equipment { get; set; }

    // Per-character party-buff plan (Party window) — the dynamic list of buff
    // slots the party-bless path casts, each with its own recast timer and (for
    // single-target buffs) its selected party members. Configured live in the
    // Party window, not the Settings tab. null means nothing configured yet.
    public BuffSettings? PartyBuffs { get; set; }

    // Per-character casting-spell profiles (Settings → Combat) — the named,
    // quick-swappable snapshots of the Combat tab's spell slots, plus which one is
    // active. Switching a profile overlays its spells onto the live Combat section
    // (Settings["Combat"]); non-spell combat settings stay shared. null / empty is
    // seeded on first load with one profile captured from the current Combat
    // settings. Managed by CombatProfileManager.
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

    // Given name of the party leader we were following, remembered so a
    // follower can auto-rejoin after an unexpected drop. Written through by
    // PartyRejoinCoordinator whenever follower membership changes (set on
    // follow, cleared on a deliberate leave) and cleared on clean shutdown, so a
    // populated value on the next launch means the client crashed mid-party —
    // the cue to telepath @comeback and let the leader own the pickup. null
    // means no party to rejoin.
    public string? PendingReconnectLeader { get; set; }
}
