using System.Collections.Generic;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Rooms tab. Renders the imported MajorMUD
/// <c>Rooms</c> table — fuel for the Phase 7 RoomGraphManager (seeded
/// from Rooms + Paths at import time) and the Workshop's room name
/// lookups.
/// </summary>
public sealed class RoomsSectionViewModel : GameDataTableSectionViewModel
{
    public override string Id => "rooms";
    public override string Title => "Rooms";

    protected override string TableName => "Rooms";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Id",
        "Name",
        "ShortName",
        "Area",
        "ShopId",
        "Light",
        "Special",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "room", "area", "shop",
    };

    public RoomsSectionViewModel(GameDataCache cache) : base(cache) { }
}
