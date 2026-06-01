using System.Collections.Generic;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Lairs tab. Static MDB lair-aggregate table —
/// one row per <c>GroupIndex</c> with the pre-computed averages
/// (delay / walk / exp / dmg / HP / AC / DR / MR / dodge) across
/// every room that spawns that monster group. Drives the Phase 7
/// Auto-Lair scheduler's expected-throughput math.
/// </summary>
/// <remarks>
/// Column names mirror the MajorMUD MDB schema verbatim. Joins room
/// memberships via <c>GroupIndex</c> — the same key appears in
/// <c>Rooms.Lair</c>'s tail token in NMR-1.83+ MDBs.
/// </remarks>
public sealed class LairsSectionViewModel : JsonTableSectionViewModel
{
    public override string Id => "lairs";
    public override string Title => "Lairs";

    protected override string TableName => "Lairs";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "GroupIndex",
        "MobList",
        "Mobs",
        "TotalLairs",
        "AvgDelay",
        "AvgWalk",
        "AvgExp",
        "AvgDmg",
        "AvgDmgPhys",
        "AvgDmgSpell",
        "AvgHP",
        "AvgAC",
        "AvgDR",
        "AvgMR",
        "AvgDodge",
    };

    public override string SearchKeyColumn => "GroupIndex";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "lair", "group", "respawn", "mob",
    };

    public LairsSectionViewModel(GameDataCache cache, SettingsResolver? resolver = null) : base(cache, resolver) { }
}
