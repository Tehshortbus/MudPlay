using System.Collections.Generic;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Classes tab. Static MDB class definitions —
/// drives the Workshop CharacterPlanner ability previews, the Spells
/// tab's class filtering, and Phase 13 CastingDirector's
/// class-specific cure-spell selection.
/// </summary>
public sealed class ClassesSectionViewModel : GameDataTableSectionViewModel
{
    public override string Id => "classes";
    public override string Title => "Classes";

    protected override string TableName => "Classes";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Id",
        "Name",
        "ShortName",
        "HpPerLevel",
        "MaPerLevel",
        "ManaType",
        "MaxLevel",
        "Alignment",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "class", "warrior", "mage", "priest", "rogue", "monk",
    };

    public ClassesSectionViewModel(GameDataCache cache) : base(cache) { }
}
