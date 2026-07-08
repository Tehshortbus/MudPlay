using System.Text.Json;

namespace FujinTerm.Models.Profile;

// Root DTO for Data/profiles/{char-name}.json — the Character tier of the
// settings hierarchy. Per-character workspace: auth info, settings deltas,
// macros / triggers / events / death records / equipment sets / build presets /
// quest state / etc.
//
// The profile filename (sans .json) is the character's identifier inside
// FujinTerm. The in-game character name may differ — see Name.
public sealed class CharacterProfile
{
    // JSON schema version (see GlobalSettings.SchemaVersion for the contract).
    public int SchemaVersion { get; set; } = 1;

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

    // User-bookmarked rooms shown in the Navigation window's GOTO pane.
    // Per-character; each entry carries the Game.Map.RoomKey wire pair plus an
    // optional custom label. Persisted as a flat list; consumed at runtime by
    // FavoritesStore. null or empty = no favorites flagged.
    public List<FavoriteRoom>? Favorites { get; set; }

    // Folder paths in the GOTO tree that the user created but which hold no
    // favourites yet (empty folders the item list alone can't reconstruct).
    // Paths use / separators, same vocabulary as FavoriteRoom.Folder. null or
    // empty = no empty folders to remember. Maintained by FavoritesStore.
    public List<string>? FavoriteFolders { get; set; }

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

    // Per-character equipment-manager state (Workshop Equipment tab) — saved
    // gear sets and the auto-equip triggers between them. Drives @equip-<set>,
    // the per-slot editor, and trigger evaluation. null means nothing configured
    // yet.
    public EquipmentSettings? Equipment { get; set; }

    // Given name of the party leader we were following, remembered so a
    // follower can auto-rejoin after an unexpected drop. Written through by
    // PartyRejoinCoordinator whenever follower membership changes (set on
    // follow, cleared on a deliberate leave) and cleared on clean shutdown, so a
    // populated value on the next launch means the client crashed mid-party —
    // the cue to telepath @comeback and let the leader own the pickup. null
    // means no party to rejoin.
    public string? PendingReconnectLeader { get; set; }
}
