using System.Collections.Generic;
using FujinTerm.Game.GameData;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Shops tab. Static MDB shop definitions — the
/// Phase 13 CashManager reads <c>ShopType == "Bank"</c> rows as the
/// auto-deposit destinations; the Phase 7 Navigation window references
/// shop ids when surfacing per-room actions.
/// </summary>
/// <remarks>
/// Column names mirror the MajorMUD MDB schema verbatim. <c>Markup%</c>
/// is the buy/sell multiplier, <c>ClassRest</c> is a class-bitmask
/// restriction, <c>MinLVL</c> / <c>MaxLVL</c> are the customer-level
/// gates. <c>ShopType</c> renders via <see cref="MmudEnums"/>
/// ("Weapons" / "Armour" / "Bank" / "Tavern" / etc.).
/// </remarks>
public sealed class ShopsSectionViewModel : JsonTableSectionViewModel
{
    public override string Id => "shops";
    public override string Title => "Shops";

    protected override string TableName => "Shops";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Number",
        "Name",
        "ShopType",
        "MinLVL",
        "MaxLVL",
        "Markup%",
        "ClassRest",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "shop", "bank", "merchant", "buy", "sell", "markup",
    };

    protected override IReadOnlyDictionary<string, Func<string?, string?>> ColumnFormatters { get; } =
        new Dictionary<string, Func<string?, string?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ShopType"] = MmudEnums.FormatShopType,
        };

    public ShopsSectionViewModel(GameDataCache cache, SettingsResolver? resolver = null) : base(cache, resolver) { }
}
