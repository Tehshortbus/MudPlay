using System.Collections.Generic;
using System.Text.Json;
using FujinTerm.Game.GameData;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

// Game Data Browser → Classes tab. Static MDB class definitions — drives the Workshop
// CharacterPlanner ability previews, the Spells tab's class filtering, and CastingDirector's
// class-specific cure-spell selection.
//
// Column names mirror the MajorMUD MDB schema verbatim. MinHits / MaxHits bracket starting HP
// roll, ExpTable is the progression curve, MageryLVL is the cap on castable-spell level.
// MageryType, WeaponType, and ArmourType render via LookupEnums.
public sealed class ClassesSectionViewModel : JsonTableSectionViewModel
{
    public override string Id => "classes";
    public override string Title => "Classes";

    protected override string TableName => "Classes";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Number",
        "Name",
        "MinHits",
        "MaxHits",
        "ExpTable",
        "MageryType",
        "MageryLVL",
        "WeaponType",
        "ArmourType",
        "CombatLVL",
        "Abilities",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "class", "warrior", "mage", "priest", "rogue", "monk", "magery", "combat", "ability",
    };

    protected override IReadOnlyDictionary<string, Func<string?, string?>> ColumnFormatters { get; } =
        new Dictionary<string, Func<string?, string?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["MageryType"] = LookupEnums.FormatMagery,
            ["WeaponType"] = LookupEnums.FormatClassWeaponType,
            ["ArmourType"] = LookupEnums.FormatArmourType,
        };

    public ClassesSectionViewModel(GameDataCache cache, SettingsResolver? resolver = null) : base(cache, resolver) { }

    // Synthesise the "Abilities" column from each row's Abil-N / AbilVal-N pairs so the grid
    // shows every class skill at a glance (e.g. Warrior → "Bash", Ninja → "ClassStealth,
    // FindTraps, Dodge +25, Crits +10, ..." — whatever the MDB encodes). Code 59 (ClassOk) is
    // suppressed in class context: its value on a class row is internal / inert data — Druid is
    // the only stock class with an entry, AbilVal=74 with no user-facing meaning.
    private static readonly int[] _skipInClassContext = { 59 };

    protected override IReadOnlyDictionary<string, string?> ComputeRowCells(JsonElement element)
        => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Abilities"] = AbilityNames.SummarizeAbilities(element, skipCodes: _skipInClassContext),
        };
}
