using System.Collections.Generic;
using System.Text.Json;
using FujinTerm.Game.GameData;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

// Game Data Browser → Races tab. Static MDB race definitions — drives Workshop
// CharacterPlanner ability-score previews and new-character roll math.
//
// Column names mirror the MajorMUD MDB schema verbatim. m* columns are minimum stat rolls and
// x* are maximums (the stat distribution range a fresh character of this race can roll into).
// HPPerLVL is HP gained per level, BaseCP is starting character points, ExpTable is the
// progression curve.
public sealed class RacesSectionViewModel : JsonTableSectionViewModel
{
    public override string Id => "races";
    public override string Title => "Races";

    protected override string TableName => "Races";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Number",
        "Name",
        "HPPerLVL",
        "ExpTable",
        "BaseCP",
        "mSTR", "xSTR",
        "mINT", "xINT",
        "mWIL", "xWIL",
        "mHEA", "xHEA",
        "mAGL", "xAGL",
        "mCHM", "xCHM",
        "Abilities",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "race", "human", "elf", "dwarf", "hobbit", "gnome", "goblin", "orc", "racial",
    };

    public RacesSectionViewModel(GameDataCache cache, SettingsResolver? resolver = null) : base(cache, resolver) { }

    // Synthesise the "Abilities" column from each row's Abil-N / AbilVal-N pairs so the grid
    // shows every racial perk at a glance (e.g. Dark-Elf → "Illu +80, RaceStealth, Crits +1,
    // Accuracy3 +3").
    protected override IReadOnlyDictionary<string, string?> ComputeRowCells(JsonElement element)
        => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Abilities"] = AbilityNames.SummarizeAbilities(element),
        };
}
