using System.Collections.Generic;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

// Game Data Browser → Lairs tab. Static MDB lair-aggregate table — one row per GroupIndex
// with the pre-computed averages (delay / walk / exp / dmg / HP / AC / DR / MR / dodge) across
// every room that spawns that monster group. Drives the Auto-Lair scheduler's
// expected-throughput math.
//
// Column names mirror the MajorMUD MDB schema verbatim. Joins room memberships via GroupIndex
// — the same key appears in Rooms.Lair's tail token in NMR-1.83+ MDBs.
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
