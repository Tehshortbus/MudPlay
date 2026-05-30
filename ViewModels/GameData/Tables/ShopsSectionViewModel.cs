using System.Collections.Generic;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Shops tab. Static MDB shop definitions — the
/// Phase 13 CashManager reads <c>ShopType == 7</c> rows as the bank
/// destinations for auto-deposit; the Phase 7 Navigation window
/// references shop ids when surfacing per-room actions.
/// </summary>
public sealed class ShopsSectionViewModel : GameDataTableSectionViewModel
{
    public override string Id => "shops";
    public override string Title => "Shops";

    protected override string TableName => "Shops";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Id",
        "Name",
        "RoomId",
        "ShopType",
        "BuyPercent",
        "SellPercent",
        "Hours",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "shop", "bank", "merchant", "buy", "sell",
    };

    public ShopsSectionViewModel(GameDataCache cache) : base(cache) { }
}
