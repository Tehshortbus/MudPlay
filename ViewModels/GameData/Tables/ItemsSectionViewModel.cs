using System.Collections.Generic;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Items tab. Renders the imported MajorMUD
/// <c>Items</c> table — drives equipment validation on the Phase 9
/// Workshop EQUIP grid, shop-price lookups for the Phase 13 Cash
/// auto-deposit math, and ability-effect tooltips throughout.
/// </summary>
/// <remarks>
/// Ability columns (<c>Ab1</c>-<c>Ab10</c>) render as raw integers
/// until Phase 5 PR 5.19 adds the <c>AbilityNames</c> helper; consumers
/// that need human-readable labels swap then.
/// </remarks>
public sealed class ItemsSectionViewModel : GameDataTableSectionViewModel
{
    public override string Id => "items";
    public override string Title => "Items";

    protected override string TableName => "Items";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Id",
        "Name",
        "ItemType",
        "Slot",
        "Weight",
        "Damage",
        "AC",
        "Price",
        "Ab1Code",
        "Ab1Pow",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "item", "weapon", "armor", "slot", "weight", "price", "ability",
    };

    public ItemsSectionViewModel(GameDataCache cache) : base(cache) { }
}
