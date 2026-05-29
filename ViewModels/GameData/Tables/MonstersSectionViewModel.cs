using System.Collections.Generic;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Monsters tab. Renders the imported MajorMUD
/// <c>Monsters</c> table — the static MDB table that drives Auto-Lair
/// respawn timers (via <c>Respawn</c>), Phase 13 CombatManager's
/// per-monster behaviour gating, and the Phase 9 Workshop COMBAT
/// preview's damage projection.
/// </summary>
/// <remarks>
/// PR 5.5 ships a read-only listing — every per-table tab is a
/// listing-first PR; Add / Modify / Remove + per-record tier picker
/// land in a Phase 5 follow-up that wires the
/// <see cref="Models.Import.ImportConflict"/> infrastructure into the
/// table-row editor.
/// </remarks>
public sealed class MonstersSectionViewModel : GameDataTableSectionViewModel
{
    public override string Id => "monsters";
    public override string Title => "Monsters";

    protected override string TableName => "Monsters";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Id",
        "Name",
        "Level",
        "Hp",
        "Race",
        "Damage",
        "AC",
        "ExpValue",
        "Respawn",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "monster", "level", "hp", "race", "damage", "respawn",
    };

    public MonstersSectionViewModel(GameDataCache cache) : base(cache) { }
}
