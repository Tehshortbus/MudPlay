namespace FujinTerm.Services;

/// <summary>
/// The four layers of the settings hierarchy, ordered lowest priority (Defaults)
/// to highest (Character). Higher tiers override lower ones at read time, per
/// MegaMUD-parity vocabulary.
/// </summary>
public enum SettingsTier
{
    /// <summary>"installed defaults" — app-shipped fallback values + imported game-data tables.</summary>
    Defaults = 0,

    /// <summary>"for all characters" — <c>Data/Global/global.json</c>.</summary>
    Global = 1,

    /// <summary>"only for this BBS" — <c>Data/BBS/{name}.json</c>.</summary>
    Bbs = 2,

    /// <summary>"only for this character" — <c>Data/profiles/{name}.json</c>.</summary>
    Character = 3,
}
