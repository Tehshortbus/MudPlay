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
    /// Name of the BBS this character connects to. Matches a
    /// <c>BbsProfile.Name</c> stored under <c>Data/BBS/</c>. <c>null</c> when
    /// the user hasn't picked a BBS yet.
    /// </summary>
    public string? BbsName { get; set; }

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
    /// User-authored room favourites with their folder-path hierarchy.
    /// Per-character; loaded into <see cref="Services.FavoritesManager"/>
    /// on profile load. Phase 5 PR 5.25's starter bundle layers
    /// pre-seeded defaults from the active game-data set on top of
    /// whatever's stored here.
    /// </summary>
    public List<GameData.Favorite>? Favorites { get; set; }

    /// <summary>
    /// User-defined keybinds. Per-character; loaded into
    /// <see cref="Services.MacroStore"/> on profile load. The Phase 10
    /// MacroManager engine intercepts keystrokes on TerminalControl +
    /// ConversationWindow's input field and dispatches the matched
    /// command in place of the raw key.
    /// </summary>
    public List<GameData.Macro>? Macros { get; set; }

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
    /// Persisted size + screen position per top-level window, keyed by
    /// stable id ("main", "backscroll", "settings", etc.). Populated by
    /// <see cref="Services.WindowLayoutStore"/> on profile save and consumed
    /// on every window <c>Opened</c>. <c>null</c> / missing entries mean
    /// "use the window's XAML defaults", so the user only ends up with a
    /// saved position once they've actually moved / resized a window.
    /// </summary>
    public Dictionary<string, WindowBounds>? WindowBounds { get; set; }
}
