using System.Text.Json;

namespace FujinTerm.Models.Profile;

/// <summary>
/// Root DTO for <c>Data/profiles/{char-name}.json</c> — the Character tier of
/// the settings hierarchy. Per-character workspace: auth info, settings
/// deltas, and (in later phase PRs) macros / triggers / events / death
/// records / equipment sets / build presets / quest state / etc.
/// </summary>
/// <remarks>
/// The profile filename (sans <c>.json</c>) is the character's identifier
/// inside FujinTerm. The in-game character name may differ — see <see cref="Name"/>.
/// </remarks>
public sealed class CharacterProfile
{
    /// <summary>JSON schema version (see <c>GlobalSettings.SchemaVersion</c> for the contract).</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// In-game character name. Usually matches the profile filename but a user
    /// may give two profiles the same in-game name on different BBSes
    /// (same character name across two unrelated realms).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Per-tab settings deltas at the Character tier — same shape as
    /// <see cref="Settings.GlobalSettings.Settings"/>. Anything the user
    /// pinned to "only for this character."
    /// </summary>
    public Dictionary<string, JsonElement>? Settings { get; set; }

    /// <summary>
    /// User-defined incoming-text triggers. Per-character so the
    /// pattern + action list follows the character that authored it.
    /// Loaded into <see cref="Services.TriggerEngine"/> on profile
    /// load (Phase 5 PR 5.10). Named capture variables emitted by
    /// matches are app-session-scoped in the engine, not persisted
    /// here.
    /// </summary>
    public List<GameData.Trigger>? Triggers { get; set; }

    /// <summary>
    /// User-defined outgoing-text aliases. Per-character; loaded into
    /// the Phase 5 PR 5.11 <see cref="Services.AliasEngine"/> on
    /// profile load. Variables substitution inside an alias's
    /// expansion reads from the shared session-scoped variable store
    /// the trigger engine maintains.
    /// </summary>
    public List<GameData.Alias>? Aliases { get; set; }

    /// <summary>
    /// User-defined keybinds. Per-character; loaded into
    /// <see cref="Services.MacroStore"/> on profile load. The Phase 10
    /// MacroManager engine intercepts keystrokes on TerminalControl +
    /// ConversationWindow's input field and dispatches the matched
    /// command in place of the raw key.
    /// </summary>
    public List<GameData.Macro>? Macros { get; set; }

    /// <summary>
    /// User-defined scheduled / lifecycle events (Phase 8 PR 8.1).
    /// Per-character; loaded into <see cref="Game.Events.EventManager"/>
    /// on profile load. Trigger types: Logon / Logoff / Re-log /
    /// AtTime / Every. Action types: Walk to / Loop / Auto-lair /
    /// Command (with <c>^M</c> / <c>;</c> multi-fire). Per-event
    /// Disabled flag. <c>null</c> means no events configured.
    /// </summary>
    public List<GameData.ScheduledEvent>? Events { get; set; }

    /// <summary>
    /// Master "stop firing every event" switch (Phase 8 PR 8.3). When
    /// true, <see cref="Game.Events.EventManager.Fire"/> short-circuits
    /// for every event regardless of its own Disabled flag. Useful for
    /// "switch off all automation for the next 10 minutes" without
    /// un-checking every row. Persists per-character. Defaults to
    /// false on a fresh profile.
    /// </summary>
    public bool EventsGloballyDisabled { get; set; }

    /// <summary>
    /// Per-character keybindings for built-in app actions (toolbar +
    /// menu shortcuts). Sparse — only entries the user has overridden
    /// from the seed defaults get persisted. <see cref="Services.KeybindingStore"/>
    /// fills in the rest from <c>KeybindingStore.DefaultBindings</c>
    /// on load, and prunes back to non-defaults at save time so a
    /// fresh profile that never touched the keybind editor leaves this
    /// <c>null</c>.
    /// </summary>
    public Dictionary<BuiltInAction, KeyChord>? BuiltInKeybindings { get; set; }

    /// <summary>
    /// Per-player customisations the loaded character has authored —
    /// remote-command permissions, auto-party toggles, the
    /// don't-auto-delete flag, notes. Keyed by player display name
    /// (case-insensitive on read). <b>Only non-default entries are
    /// persisted</b>: a fresh profile that's never opened the player
    /// edit dialog leaves this <c>null</c>, and pristine
    /// "all unchecked" entries are pruned at save time.
    /// Per-BBS observation rows live separately at
    /// <c>Data/BBS/{name}/players.json</c> so a customisation on
    /// character A doesn't leak into character B even when both play
    /// the same BBS.
    /// </summary>
    public Dictionary<string, GameData.PlayerCustomization>? PlayerCustomizations { get; set; }

    /// <summary>
    /// Persisted floating-panel layouts keyed by panel id. Populated by
    /// <see cref="Services.FloatingPanelHost"/> on profile save; consumed on
    /// profile load. <c>null</c> means "no layouts captured yet" — panels
    /// default to <see cref="PanelState.Docked"/>.
    /// </summary>
    public Dictionary<string, PanelLayout>? PanelLayouts { get; set; }

    /// <summary>
    /// Per-BBS login credentials for this character. Keyed by BBS name
    /// (matches <see cref="Settings.BbsProfile.Name"/>). Username is plaintext;
    /// password lives inline on <see cref="BbsCredentials.EncryptedPassword"/>
    /// (AES-GCM, decrypted via <see cref="Services.PasswordProtector"/>).
    /// </summary>
    public Dictionary<string, BbsCredentials>? BbsCredentials { get; set; }

    /// <summary>
    /// In-game <c>set suicide</c> password, encrypted with
    /// <see cref="Services.PasswordProtector"/>. Captured passively
    /// while the user is in the password-entry flow (see
    /// <see cref="Game.SuicidePasswordTracker"/>) and consumed by the
    /// Phase 6 <c>@suicide</c> remote-command path. <c>null</c> when
    /// no password has been observed yet OR after the user runs
    /// <c>pro</c> and the "no password set" line confirms the
    /// realm-side state no longer matches our cached value.
    /// </summary>
    public string? EncryptedSuicidePassword { get; set; }

    /// <summary>
    /// Persisted size + screen position per top-level window, keyed by
    /// stable id ("main", "backscroll", "settings", etc.). Populated by
    /// <see cref="Services.WindowLayoutStore"/> on profile save and consumed
    /// on every window <c>Opened</c>. <c>null</c> / missing entries mean
    /// "use the window's XAML defaults", so the user only ends up with a
    /// saved position once they've actually moved / resized a window.
    /// </summary>
    public Dictionary<string, WindowBounds>? WindowBounds { get; set; }

    /// <summary>
    /// Persisted left-pane proportions for resizable two-pane dialogs
    /// keyed by stable id (e.g. <c>"MonsterEditDialog"</c>). Each value
    /// is the fraction (0.0–1.0) of the splittable area occupied by the
    /// LEFT pane at the user's last close. Populated by
    /// <see cref="Services.SplitterLayoutStore"/> on profile save and
    /// applied on every dialog open. <c>null</c> / missing entries mean
    /// "use the XAML defaults".
    /// </summary>
    public Dictionary<string, double>? SplitterRatios { get; set; }

    /// <summary>
    /// Snapshot of the most recent <c>stat</c> + <c>exp</c>
    /// observations. Written by <see cref="Game.StatParser"/> after
    /// each successful capture; hydrated back into the live
    /// <see cref="Game.PlayerStats"/> on
    /// <see cref="Services.ProfileService.ProfileLoaded"/> so the
    /// status bar / @-command query handlers / Workshop view
    /// (Phase 9) start the next session with the user's last-known
    /// values instead of zeros. <c>null</c> until the first capture.
    /// </summary>
    public LastKnownStats? LastKnownStats { get; set; }

    /// <summary>
    /// Rooms the walker / loop / auto-lair scheduler must not route
    /// through. Per-character only (each player picks their own no-go
    /// list) — does not flow through <see cref="SettingsResolver"/>.
    /// Persisted as a flat list of <see cref="RoomRef"/>; consumed at
    /// runtime by <see cref="Services.MovementFilter"/>. <c>null</c>
    /// or empty = no rooms avoided.
    /// </summary>
    public List<RoomRef>? AvoidedRooms { get; set; }

    /// <summary>
    /// Rooms the user has flagged as drop-off / stash points (Phase 7
    /// — basic flag; downstream consumers like the cash auto-deposit
    /// flow and the Workshop EQUIP sets land later). Per-character
    /// only. <c>null</c> or empty = no stash rooms flagged.
    /// </summary>
    public List<RoomRef>? StashRooms { get; set; }

    /// <summary>
    /// User-bookmarked rooms shown in the Navigation window's GOTO
    /// pane. Per-character; each entry carries the
    /// <see cref="Game.Map.RoomKey"/> wire pair plus an optional
    /// custom label. Persisted as a flat list; consumed at runtime by
    /// <see cref="Services.FavoritesStore"/>. <c>null</c> or empty =
    /// no favorites flagged.
    /// </summary>
    public List<FavoriteRoom>? Favorites { get; set; }

    /// <summary>
    /// Folder paths in the GOTO tree that the user created but which
    /// hold no favourites yet (empty folders the item list alone can't
    /// reconstruct). Paths use <c>/</c> separators, same vocabulary as
    /// <see cref="FavoriteRoom.Folder"/>. <c>null</c> or empty = no
    /// empty folders to remember. Maintained by
    /// <see cref="Services.FavoritesStore"/>.
    /// </summary>
    public List<string>? FavoriteFolders { get; set; }

    /// <summary>
    /// Last room the character was known to be standing in. Hydrated
    /// from <see cref="Game.Map.RoomTracker"/> on a successful manual
    /// or auto locate; saved with the rest of the profile and used as
    /// the initial Navigation map origin on the next session so the
    /// user opens the map already centred on where they left off.
    /// <c>null</c> until the first successful locate.
    /// </summary>
    public RoomRef? LastKnownRoom { get; set; }

    /// <summary>
    /// Ordered list of move commands sent since <see cref="LastKnownRoom"/>
    /// was Confirmed — the tracker's replay-from-last-Confirmed input.
    /// Written by <see cref="Game.Map.RoomTracker"/> on every successful
    /// move, cleared on the next Confirmed transition (the new
    /// <c>LastKnownRoom</c> takes over). Hydrated on profile load so the
    /// next session can replay through the graph and recover position
    /// without manual intervention. <c>null</c> or empty = no pending
    /// steps to replay.
    /// </summary>
    public List<DirectionDto>? RecentSteps { get; set; }

    /// <summary>
    /// Append-only history of deaths observed for this character.
    /// Written by <see cref="Game.DeathDetector"/> when the
    /// <c>You now have N lives remaining.</c> message arrives;
    /// consumed by the Phase 9 Workshop DEATH section. <c>null</c> /
    /// empty means no deaths yet (the lucky case).
    /// </summary>
    public List<DeathRecord>? DeathHistory { get; set; }

    /// <summary>
    /// When true, the DEATH-recovery flow grabs lost items (and re-equips
    /// what was worn at death) automatically whenever the character
    /// re-enters a room holding one of their own deathpiles — regardless
    /// of the item's auto-get policy. Per-character. Defaults false.
    /// The item-grab side is inert until the inventory tracker lands; the
    /// toggle persists now so the preference survives that gap.
    /// </summary>
    public bool DeathAutoRecover { get; set; }

    /// <summary>
    /// When true, items recovered from a deathpile that were equipped at
    /// the moment of death are automatically re-equipped after pickup.
    /// Per-character. Defaults false. Inert until inventory tracking
    /// records what was worn at death.
    /// </summary>
    public bool DeathAutoEquip { get; set; }

    /// <summary>
    /// The editable per-level CP-allocation plan (Workshop CP Allocation tab) —
    /// the target stats at each planned level above the current one, oldest →
    /// newest. Drives the CP grid now and auto-train / <c>@train</c> in later
    /// Phase-10 PRs. <c>null</c> / empty means no plan saved.
    /// </summary>
    public List<CpPlanEntry>? CharacterPlan { get; set; }

    /// <summary>
    /// Per-character quest completion state (Workshop Quest Status tab), keyed by
    /// the crawler's (flag, step) quest identity. Records which quests / alignment
    /// bands the character has finished and, for single-part quests, which steps
    /// are ticked. Drives the bonus fold into Character Info. <c>null</c> / empty
    /// means nothing completed yet.
    /// </summary>
    public List<QuestProgress>? QuestLog { get; set; }
}
