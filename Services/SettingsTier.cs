namespace MudPlay.Services;

// The four layers of the settings hierarchy, ordered lowest priority (Defaults)
// to highest (Character). Higher tiers override lower ones at read time, per
// MegaMUD-parity vocabulary.
public enum SettingsTier
{
    // "installed defaults" — app-shipped fallback values + imported game-data tables.
    Defaults = 0,

    // "for all characters" — Data/Global/global.json.
    Global = 1,

    // "only for this BBS" — Data/BBS/{name}.json.
    Bbs = 2,

    // "only for this character" — Data/profiles/{name}.json.
    Character = 3,
}

// Short labels for the Game Data Browser "Use" column — MegaMUD parity
// (Def / Glob / BBS / Char).
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

    // Long MegaMUD-parity labels for the Game Data edit dialogs' Use-tier picker.
    // "Installed defaults" is the reset target — picking it wipes the record's
    // higher-tier overrides and restores the seeded value.
    public static string ToPickerLabel(this SettingsTier tier) => tier switch
    {
        SettingsTier.Defaults  => "Installed defaults",
        SettingsTier.Global    => "For all characters (global)",
        SettingsTier.Bbs       => "Only for this BBS",
        SettingsTier.Character => "Only for this character",
        _ => tier.ToString(),
    };
}
