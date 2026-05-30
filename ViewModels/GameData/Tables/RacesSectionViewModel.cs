using System.Collections.Generic;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Races tab. Static MDB race definitions —
/// drives Workshop CharacterPlanner ability-score previews and
/// new-character roll math.
/// </summary>
public sealed class RacesSectionViewModel : GameDataTableSectionViewModel
{
    public override string Id => "races";
    public override string Title => "Races";

    protected override string TableName => "Races";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Id",
        "Name",
        "Str",
        "Int",
        "Wis",
        "Con",
        "Dex",
        "Cha",
        "MaxLevel",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "race", "human", "elf", "dwarf",
    };

    public RacesSectionViewModel(GameDataCache cache) : base(cache) { }
}
