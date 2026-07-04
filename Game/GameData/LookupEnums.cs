using System.Collections.Frozen;
using System.Globalization;

namespace FujinTerm.Game.GameData;

// Lookup tables that translate raw integer codes inside MajorMUD game-data tables
// (Items / Monsters / Spells / Shops / Classes / Races / Rooms) into human-readable
// labels for the Game Data Browser. Uses the community-standard MajorMUD code →
// label conventions so the terminology stays familiar.
//
// This is the translation layer only — damage / accuracy / DPS / experience-curve
// calculators and inventory sims are out of scope for the Browser.
//
// Every Format* method accepts the raw cell string from the game-data JSON, parses
// it as an integer, and returns the corresponding label. An unparseable value is
// returned unchanged (treat it as foreign / future data); an unmapped integer comes
// back as Unknown(N) to make the gap visible.
public static class LookupEnums
{
    private static readonly FrozenDictionary<int, string> ItemTypeNames = new Dictionary<int, string>
    {
        [0] = "Armour",     [1] = "Weapon",    [2] = "Projectile", [3] = "Sign",
        [4] = "Food",       [5] = "Drink",     [6] = "Light",      [7] = "Key",
        [8] = "Container",  [9] = "Scroll",    [10] = "Special",
    }.ToFrozenDictionary();

    // Items.ItemType → "Weapon", "Armour", "Food", ...
    public static string? FormatItemType(string? raw) => Map(raw, ItemTypeNames);

    private static readonly FrozenDictionary<int, string> WornNames = new Dictionary<int, string>
    {
        [0] = "Nowhere",   [1] = "Everywhere", [2] = "Head",     [3] = "Hands",
        [4] = "Finger",    [5] = "Feet",       [6] = "Arms",     [7] = "Back",
        [8] = "Neck",      [9] = "Legs",       [10] = "Waist",   [11] = "Torso",
        [12] = "Off-Hand", [13] = "Finger",    [14] = "Wrist",   [15] = "Ears",
        [16] = "Worn",     [17] = "Wrist",     [18] = "Eyes",    [19] = "Face",
    }.ToFrozenDictionary();

    // Items.Worn → equip slot label.
    public static string? FormatWornSlot(string? raw) => Map(raw, WornNames);

    private static readonly FrozenDictionary<int, string> WeaponTypeNames = new Dictionary<int, string>
    {
        [0] = "1H Blunt", [1] = "2H Blunt", [2] = "1H Sharp", [3] = "2H Sharp",
    }.ToFrozenDictionary();

    // Items.WeaponType → "1H Sharp", "2H Blunt", ...
    public static string? FormatWeaponType(string? raw) => Map(raw, WeaponTypeNames);

    // True when an Items.WeaponType code marks a two-handed weapon (the "2H Blunt" /
    // "2H Sharp" labels above, codes 1 and 3). Two-handers occupy the off-hand slot,
    // so wielding one needs a shield/off-hand removed first — the swap logic in
    // ItemCastSequencer and CombatManager keys off this.
    public static bool IsTwoHandedWeaponType(int weaponType) => weaponType is 1 or 3;

    private static readonly FrozenDictionary<int, string> ClassWeaponTypeNames = new Dictionary<int, string>
    {
        [0] = "1H Blunt",  [1] = "2H Blunt",   [2] = "1H Sharp",  [3] = "2H Sharp",
        [4] = "Any 1H",    [5] = "Any 2H",     [6] = "Any Sharp", [7] = "Any Blunt",
        [8] = "Any Weapon", [9] = "Staff",
    }.ToFrozenDictionary();

    // Classes.WeaponType — superset of FormatWeaponType with class-rule aggregates.
    public static string? FormatClassWeaponType(string? raw) => Map(raw, ClassWeaponTypeNames);

    private static readonly FrozenDictionary<int, string> ArmourTypeNames = new Dictionary<int, string>
    {
        [0] = "Natural", [1] = "Silk",     [2] = "Ninja",
        [3] = "Leather", [4] = "Leather",  [5] = "Leather", [6] = "Leather",
        [7] = "Chainmail", [8] = "Scalemail", [9] = "Platemail",
    }.ToFrozenDictionary();

    // Items.ArmourType / Classes.ArmourType.
    public static string? FormatArmourType(string? raw) => Map(raw, ArmourTypeNames);

    private static readonly FrozenDictionary<int, string> CurrencyNames = new Dictionary<int, string>
    {
        [0] = "Copper", [1] = "Silver", [2] = "Gold", [3] = "Platinum", [4] = "Runic",
    }.ToFrozenDictionary();

    // Items.Currency → "Copper" / "Silver" / "Gold" / "Platinum" / "Runic".
    public static string? FormatCurrency(string? raw) => Map(raw, CurrencyNames);

    private static readonly FrozenDictionary<int, string> SpellTargetsNames = new Dictionary<int, string>
    {
        [0] = "User",
        [1] = "Self",
        [2] = "Self or User",
        [3] = "Divided Area (not self)",
        [4] = "Monster",
        [5] = "Divided Area (incl self)",
        [6] = "Any",
        [7] = "Item",
        [8] = "Monster or User",
        [9] = "Divided Attack Area",
        [10] = "Divided Party Area",
        [11] = "Full Area",
        [12] = "Full Attack Area",
        [13] = "Full Party Area",
    }.ToFrozenDictionary();

    // Spells.Targets → human-readable target scope.
    public static string? FormatSpellTargets(string? raw) => Map(raw, SpellTargetsNames);

    private static readonly FrozenDictionary<int, string> SpellAttackTypeNames = new Dictionary<int, string>
    {
        [0] = "Cold", [1] = "Fire",  [2] = "Stone", [3] = "Lightning",
        [4] = "Normal", [5] = "Water", [6] = "Poison",
    }.ToFrozenDictionary();

    // Spells.AttType → damage element ("Cold", "Fire", ...).
    public static string? FormatSpellAttackType(string? raw) => Map(raw, SpellAttackTypeNames);

    private static readonly FrozenDictionary<int, string> ShopTypeNames = new Dictionary<int, string>
    {
        [0] = "General",  [1] = "Weapons",   [2] = "Armour",    [3] = "Items",
        [4] = "Spells",   [5] = "Hospital",  [6] = "Tavern",    [7] = "Bank",
        [8] = "Training", [9] = "Inn",       [10] = "Specific", [11] = "Gang Shop",
        [12] = "Deed Shop",
    }.ToFrozenDictionary();

    // Shops.ShopType — the CashManager auto-deposit path consults this.
    public static string? FormatShopType(string? raw) => Map(raw, ShopTypeNames);

    private static readonly FrozenDictionary<int, string> MonAttackTypeNames = new Dictionary<int, string>
    {
        [0] = "None", [1] = "Normal", [2] = "Spell", [3] = "Rob",
    }.ToFrozenDictionary();

    // Monsters.AttType-N — per-attack kind on a monster's attack profile.
    public static string? FormatMonAttackType(string? raw) => Map(raw, MonAttackTypeNames);

    private static readonly FrozenDictionary<int, string> MonTypeNames = new Dictionary<int, string>
    {
        [0] = "Solo", [1] = "Leader", [2] = "Follower", [3] = "Stationary",
    }.ToFrozenDictionary();

    // Monsters.Type — Solo / Leader / Follower / Stationary group role.
    public static string? FormatMonType(string? raw) => Map(raw, MonTypeNames);

    private static readonly FrozenDictionary<int, string> MonAlignmentNames = new Dictionary<int, string>
    {
        [0] = "Good",
        [1] = "Evil",
        [2] = "Chaotic Evil",
        [3] = "Neutral",
        [4] = "Lawful Good",
        [5] = "Neutral Evil",
        [6] = "Lawful Evil",
    }.ToFrozenDictionary();

    // Monsters.Align.
    public static string? FormatMonAlignment(string? raw) => Map(raw, MonAlignmentNames);

    private static readonly FrozenDictionary<int, string> MageryNames = new Dictionary<int, string>
    {
        [0] = "None", [1] = "Mage", [2] = "Priest", [3] = "Druid", [4] = "Bard", [5] = "Kai",
    }.ToFrozenDictionary();

    // Spells.Magery / Classes.MageryType — the casting school. Magery and MageryLVL
    // live in distinct cells, so the level suffix stays a separate column here.
    public static string? FormatMagery(string? raw) => Map(raw, MageryNames);

    // Items / Monsters / Spells / Classes / Races Abil-N → ability name (e.g. "AC",
    // "Heal", "Strength"). 0 renders as empty so unused slots don't clutter the row.
    public static string? FormatAbilityCode(string? raw)
    {
        if (!TryParseInt(raw, out int code)) return raw;
        if (code == 0) return string.Empty;
        return AbilityNames.GetName(code) ?? $"Unknown({code})";
    }

    // Pairs with FormatAbilityCode on AbilVal-N — render 0 as blank so the paired
    // ability slot's empty value is uncluttered.
    public static string? FormatAbilityValue(string? raw)
    {
        if (!TryParseInt(raw, out int value)) return raw;
        return value == 0 ? string.Empty : value.ToString(CultureInfo.InvariantCulture);
    }

    private static readonly FrozenDictionary<int, string> AbilityValueTableRefs = new Dictionary<int, string>
    {
        // → Spells.Number (LearnSp / CastsSp / RemovesSpell / EndCast / KillSpell / GiveTempSpell)
        [42] = "Spells", [43] = "Spells", [122] = "Spells",
        [151] = "Spells", [153] = "Spells", [160] = "Spells",
        // → Classes.Number (ClassOk)
        [59] = "Classes",
        // → Monsters.Number (Summon / MonsGuards)
        [12] = "Monsters", [146] = "Monsters",
        // → Items.Number (NoAttackIfItemNum / NoFirstKillDrop)
        [185] = "Items", [1115] = "Items",
        // → TextBlocks.Number (TextBlock)
        [148] = "TextBlocks",
    }.ToFrozenDictionary();

    // For an ability code whose AbilVal-N is a foreign key into another game-data
    // table, returns that table's name (the identifier
    // GameDataCache.FindNameByNumber expects); otherwise null. The name-resolving
    // codes: Spells (42/43/122/151/153/160), Classes (59), Monsters (12/146), Items
    // (185/1115), TextBlocks (148).
    //
    // Resolution itself stays with the caller because turning a record number into a
    // name needs the live GameDataCache; this map owns only the static code → table
    // half. Codes 73/124 (value is another ability code, not a table row — resolve
    // via AbilityNames) and 140/141 (raw room/map number, never name-resolved) are
    // intentionally excluded.
    public static string? ReferencedTable(int abilityCode) =>
        AbilityValueTableRefs.TryGetValue(abilityCode, out string? table) ? table : null;

    // ----- private helpers --------------------------------------------------

    private static string? Map(string? raw, FrozenDictionary<int, string> table)
    {
        if (!TryParseInt(raw, out int n)) return raw;
        return table.TryGetValue(n, out string? label) ? label : $"Unknown({n})";
    }

    private static bool TryParseInt(string? raw, out int value)
    {
        if (string.IsNullOrWhiteSpace(raw)) { value = 0; return false; }
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
