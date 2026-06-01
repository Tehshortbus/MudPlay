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

/// <summary>Short labels for the Game Data Browser "Use" column — MegaMUD parity (<c>Def</c> / <c>Glob</c> / <c>BBS</c> / <c>Char</c>).</summary>
public static class SettingsTierExtensions
{
    public static string ToShortLabel(this SettingsTier tier) => tier switch
    {
        SettingsTier.Defaults  => "Def",
        SettingsTier.Global    => "Glob",
        SettingsTier.Bbs       => "BBS",
        SettingsTier.Character => "Char",
        _ => tier.ToString(),
    };
}
