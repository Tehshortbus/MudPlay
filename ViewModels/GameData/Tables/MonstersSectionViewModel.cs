using System.Collections.Generic;
using FujinTerm.Game.GameData;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Monsters tab. Renders the imported MajorMUD
/// <c>Monsters</c> table — the static MDB table that drives Auto-Lair
/// respawn timers (via <c>RegenTime</c>), Phase 13 CombatManager's
/// per-monster behaviour gating, and the Phase 9 Workshop COMBAT
/// preview's damage projection.
/// </summary>
/// <remarks>
/// Column names mirror the MajorMUD MDB schema verbatim (per
/// <c>data-v1.11p.mdb</c>). <c>EXP</c> is the experience reward,
/// <c>MagicRes</c> is the magic-resist score, <c>AvgDmg</c> is the
/// average per-round outgoing damage, <c>RegenTime</c> is respawn
/// cadence in ticks. <c>Type</c> and <c>Align</c> render via
/// <see cref="MmudEnums"/> ("Solo" / "Lawful Good" / etc.) and
/// <c>Undead</c> is a boolean from the MDB so it already arrives
/// as <c>"true"</c> / <c>"false"</c>.
/// </remarks>
public sealed class MonstersSectionViewModel : JsonTableSectionViewModel
{
    public override string Id => "monsters";
    public override string Title => "Monsters";

    protected override string TableName => "Monsters";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Number",
        "Name",
        "EXP",
        "HP",
        "ArmourClass",
        "DamageResist",
        "MagicRes",
        "AvgDmg",
        "Energy",
        "HPRegen",
        "RegenTime",
        "Type",
        "Align",
        "Undead",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "monster", "mob", "enemy", "creature", "lair", "regen", "respawn",
    };

    protected override IReadOnlyDictionary<string, Func<string?, string?>> ColumnFormatters { get; } =
        new Dictionary<string, Func<string?, string?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Type"]  = MmudEnums.FormatMonType,
            ["Align"] = MmudEnums.FormatMonAlignment,
        };

    public MonstersSectionViewModel(GameDataCache cache, SettingsResolver? resolver = null) : base(cache, resolver) { }
}
