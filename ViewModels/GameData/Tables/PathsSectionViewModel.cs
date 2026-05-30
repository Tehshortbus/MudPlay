using System.Collections.Generic;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Paths tab. Renders the imported MajorMUD
/// <c>Paths</c> table — directed edges between rooms; consumed by
/// Phase 7 BfsMapper for the planar walk-to layout.
/// </summary>
public sealed class PathsSectionViewModel : GameDataTableSectionViewModel
{
    public override string Id => "paths";
    public override string Title => "Paths";

    protected override string TableName => "Paths";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Id",
        "FromRoom",
        "ToRoom",
        "Direction",
        "Distance",
        "Special",
        "Prereq",
    };

    public override string SearchKeyColumn => "FromRoom";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "path", "edge", "direction", "exit",
    };

    public PathsSectionViewModel(GameDataCache cache) : base(cache) { }
}
