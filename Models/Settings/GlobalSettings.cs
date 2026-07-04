using System.Text.Json;

namespace FujinTerm.Models.Settings;

// Root DTO for Data/Global/global.json — the Global tier of the settings
// hierarchy. Holds app-wide deltas (the things every character shares) plus
// pointers the launcher needs before any profile is loaded. Anything in
// this file must be resolvable without a character profile loaded (Global
// tier is the highest non-Char layer in SettingsResolver).
public sealed class GlobalSettings
{
    // JSON schema version. Bump whenever the on-disk format changes in a
    // non-backward-compatible way; migration logic keys off this.
    public int SchemaVersion { get; set; } = 1;

    // (BBS, character) reference of the most recently loaded profile. Used
    // at startup to auto-load the last session; null on first run. Profiles
    // are BBS-scoped now, so the bare character name is no longer a unique
    // key.
    public Profile.ProfileRef? LastUsedProfile { get; set; }

    // Up to RecentProfilesLimit profiles the user has loaded recently,
    // ordered most-recent-first. Each entry is a (BBS, character)
    // reference. Drives the File → Recent profiles submenu.
    public List<Profile.ProfileRef>? RecentProfiles { get; set; }

    // Cap on RecentProfiles retention.
    public const int RecentProfilesLimit = 5;

    // Fallback game-data set name used when no BBS is pinned (or the pinned
    // BBS has no ActiveGameDataSet of its own). Once a BBS is pinned its
    // setting takes over via the resolution chain in AppServices.
    public string? DefaultGameDataSet { get; set; }

    // Auto-cleanup window for the Players table (see
    // PlayerDatabase.PurgeStale). Records whose LastSeenUtc falls outside
    // this window get dropped at startup so the table doesn't accumulate
    // one-shot tourists from every session. Per-record DontAutoDelete opts
    // out. 0 or negative disables auto-cleanup entirely.
    public int PlayerCleanupDays { get; set; } = 90;

    // Per-tab settings deltas — keyed by tab name (Health / Combat / Talk /
    // etc.). Each value is a partial DTO for that tab containing only the
    // fields the user pinned to the Global tier. SettingsResolver merges
    // these across all four tiers.
    public Dictionary<string, JsonElement>? Settings { get; set; }
}
