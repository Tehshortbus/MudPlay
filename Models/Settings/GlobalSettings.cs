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
    /// Default game-data set name used when no character profile is loaded.
    /// Once a profile is loaded its own <c>ActiveGameDataSet</c> takes over.
    /// </summary>
    public string? DefaultGameDataSet { get; set; }
}
