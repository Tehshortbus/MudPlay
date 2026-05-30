using System.Text.Json;

namespace FujinTerm.Models.Settings;

/// <summary>
/// Root DTO for <c>Data/Global/global.json</c> — the Global tier of the
/// settings hierarchy. Holds app-wide deltas (the things every character
/// shares) plus pointers the launcher needs before any profile is loaded.
/// </summary>
/// <remarks>
/// Fields grow as later phase PRs land. Anything in this file must be
/// resolvable without a character profile loaded (Global tier is the highest
/// non-Char layer in <c>SettingsResolver</c>).
/// </remarks>
public sealed class GlobalSettings
{
    /// <summary>
    /// JSON schema version. Bump whenever the on-disk format changes in a
    /// non-backward-compatible way; migration logic keys off this.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Filename (without path or extension) of the most recently loaded
    /// character profile. Used at startup to auto-load the last session;
    /// <c>null</c> on first run.
    /// </summary>
    public string? LastUsedProfileName { get; set; }

    /// <summary>
    /// Up to <see cref="RecentProfilesLimit"/> profile filenames the user
    /// has loaded recently, ordered most-recent-first. Drives the
    /// File → Recent profiles submenu.
    /// </summary>
    public List<string>? RecentProfiles { get; set; }

    /// <summary>Cap on <see cref="RecentProfiles"/> retention.</summary>
    public const int RecentProfilesLimit = 5;

    /// <summary>
    /// Fallback game-data set name used when no BBS is pinned (or the
    /// pinned BBS has no <c>ActiveGameDataSet</c> of its own). Once a
    /// BBS is pinned its setting takes over via the resolution chain
    /// in <see cref="Services.AppServices"/>.
    /// </summary>
    public string? DefaultGameDataSet { get; set; }

    /// <summary>
    /// Auto-cleanup window for the Players table (see
    /// <see cref="Services.PlayerDatabase.PurgeStale"/>). Records whose
    /// <c>LastSeenUtc</c> falls outside this window get dropped at
    /// startup so the table doesn't accumulate one-shot tourists from
    /// every session. Per-record <c>DontAutoDelete</c> opts out.
    /// <c>0</c> or negative disables auto-cleanup entirely.
    /// </summary>
    public int PlayerCleanupDays { get; set; } = 90;

    /// <summary>
    /// Per-tab settings deltas — keyed by tab name (Health / Combat / Talk /
    /// etc.). Each value is a partial DTO for that tab containing only the
    /// fields the user pinned to the Global tier. <see cref="Services.SettingsResolver"/>
    /// merges these across all four tiers.
    /// </summary>
    public Dictionary<string, JsonElement>? Settings { get; set; }
}
