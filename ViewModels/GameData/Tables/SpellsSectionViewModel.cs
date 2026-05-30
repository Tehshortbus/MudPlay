using System.Collections.Generic;
using FujinTerm.Game.GameData;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Spells tab. Renders the imported MajorMUD
/// <c>Spells</c> table — fuel for the Phase 13 CastingDirector + the
/// Settings → Spells / Party spell pickers + the Phase 9 Workshop
/// Spell Book.
/// </summary>
/// <remarks>
/// Column names mirror the MajorMUD MDB schema verbatim. <c>Short</c>
/// is the cast-name shortcode (e.g. <c>"star"</c>), <c>ReqLevel</c> is
/// the cast prerequisite, <c>Diff</c> is the cast-difficulty score.
/// <c>Magery</c>, <c>AttType</c>, and <c>Targets</c> render via
/// <see cref="MmudEnums"/> ("Mage" / "Cold" / "Full Area" / etc.).
/// </remarks>
public sealed class SpellsSectionViewModel : JsonTableSectionViewModel
{
    public override string Id => "spells";
    public override string Title => "Spells";

    protected override string TableName => "Spells";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Number",
        "Name",
        "Short",
        "Magery",
        "MageryLVL",
        "ReqLevel",
        "ManaCost",
        "EnergyCost",
        "Diff",
        "AttType",
        "Targets",
        "MinBase",
        "MaxBase",
        "Dur",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "spell", "magery", "mana", "cast", "level", "code", "short", "target",
    };

    protected override IReadOnlyDictionary<string, Func<string?, string?>> ColumnFormatters { get; } =
        new Dictionary<string, Func<string?, string?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Magery"]  = MmudEnums.FormatMagery,
            ["AttType"] = MmudEnums.FormatSpellAttackType,
            ["Targets"] = MmudEnums.FormatSpellTargets,
        };

    public SpellsSectionViewModel(GameDataCache cache) : base(cache) { }
}
