using System.Collections.Generic;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Lairs tab. Static MDB lair definitions —
/// referenced by the Phase 7 Auto-Lair scheduler when the user marks
/// rooms for the looping mode.
/// </summary>
public sealed class LairsSectionViewModel : GameDataTableSectionViewModel
{
    public override string Id => "lairs";
    public override string Title => "Lairs";

    protected override string TableName => "Lairs";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Id",
        "Name",
        "RoomId",
        "MonsterId",
        "Quantity",
        "Respawn",
        "Special",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "lair", "respawn", "monster", "room",
    };

    public LairsSectionViewModel(GameDataCache cache) : base(cache) { }
}
