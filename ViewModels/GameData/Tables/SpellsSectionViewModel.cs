using System.Collections.Generic;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Spells tab. Renders the imported MajorMUD
/// <c>Spells</c> table — fuel for the Phase 13 CastingDirector + the
/// Settings → Spells / Party spell pickers + the Phase 9 Workshop
/// Spell Book.
/// </summary>
/// <remarks>
/// PR 5.7 ships the listing only. The "key UX improvement over
/// MegaMUD" — the inline Spell-fields-left / Spell-Messages-right
/// editor — lives in the edit dialog opened from this tab's row
/// double-click, which lands once the listing for every table is
/// in place.
/// </remarks>
public sealed class SpellsSectionViewModel : GameDataTableSectionViewModel
{
    public override string Id => "spells";
    public override string Title => "Spells";

    protected override string TableName => "Spells";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Id",
        "Name",
        "Code",
        "Level",
        "Class",
        "MaCost",
        "PrepTime",
        "Range",
        "TargetType",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "spell", "level", "mana", "cost", "class", "code",
    };

    public SpellsSectionViewModel(GameDataCache cache) : base(cache) { }
}
