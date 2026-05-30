using System.Collections.Generic;
using FujinTerm.Game.GameData;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Items tab. Renders the imported MajorMUD
/// <c>Items</c> table — drives equipment validation on the Phase 9
/// Workshop EQUIP grid, shop-price lookups for the Phase 13 Cash
/// auto-deposit math, and ability-effect tooltips throughout.
/// </summary>
/// <remarks>
/// Column names mirror the MajorMUD MDB schema verbatim (per
/// <c>data-v1.11p.mdb</c>): <c>Number</c> is the canonical item ID,
/// <c>Encum</c> is encumbrance, <c>Accy</c> is to-hit modifier,
/// <c>StrReq</c> is strength prerequisite. Numeric enum cells
/// (<c>ItemType</c>, <c>Worn</c>, <c>WeaponType</c>, <c>ArmourType</c>,
/// <c>Currency</c>) are formatted via <see cref="MmudEnums"/> so the
/// grid shows "Weapon" / "Feet" / "1H Sharp" / "Gold" rather than the
/// raw integers.
/// </remarks>
public sealed class ItemsSectionViewModel : JsonTableSectionViewModel
{
    public override string Id => "items";
    public override string Title => "Items";

    protected override string TableName => "Items";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Number",
        "Name",
        "ItemType",
        "Worn",
        "WeaponType",
        "ArmourType",
        "Min",
        "Max",
        "ArmourClass",
        "DamageResist",
        "Speed",
        "Accy",
        "StrReq",
        "Encum",
        "Price",
        "Currency",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "item", "weapon", "armor", "armour", "worn", "slot",
        "encumbrance", "price", "currency", "ability",
    };

    protected override IReadOnlyDictionary<string, Func<string?, string?>> ColumnFormatters { get; } =
        new Dictionary<string, Func<string?, string?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ItemType"]   = MmudEnums.FormatItemType,
            ["Worn"]       = MmudEnums.FormatWornSlot,
            ["WeaponType"] = MmudEnums.FormatWeaponType,
            ["ArmourType"] = MmudEnums.FormatArmourType,
            ["Currency"]   = MmudEnums.FormatCurrency,
        };

    public ItemsSectionViewModel(GameDataCache cache, SettingsResolver? resolver = null) : base(cache, resolver) { }
}
