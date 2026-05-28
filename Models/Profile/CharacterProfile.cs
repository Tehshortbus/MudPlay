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
    /// Game-data set this character expects. On profile load the
    /// <c>GameDataCache</c> (Phase 5) switches to this set; if the user
    /// manually switches sets later this field is rewritten and persisted.
    /// </summary>
    public string? ActiveGameDataSet { get; set; }

    /// <summary>
    /// Per-tab settings deltas at the Character tier — same shape as
    /// <see cref="Settings.GlobalSettings.Settings"/>. Anything the user
    /// pinned to "only for this character."
    /// </summary>
    public Dictionary<string, JsonElement>? Settings { get; set; }

    /// <summary>
    /// Per-record game-data overrides at the Character tier. Same shape as
    /// <see cref="Settings.GlobalSettings.GameDataOverrides"/>.
    /// </summary>
    public Dictionary<string, Dictionary<string, JsonElement>>? GameDataOverrides { get; set; }

    /// <summary>
    /// Persisted floating-panel layouts keyed by panel id. Populated by
    /// <see cref="Services.FloatingPanelHost"/> on profile save; consumed on
    /// profile load. <c>null</c> means "no layouts captured yet" — panels
    /// default to <see cref="PanelState.Docked"/>.
    /// </summary>
    public Dictionary<string, PanelLayout>? PanelLayouts { get; set; }
}
