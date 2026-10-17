using System.Collections.Generic;
using MudPlay.Game.GameData;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Tables;

// Game Data Browser → Classes tab. Static MDB class definitions — drives the Workshop's
// character calculators, the Spells tab's class filtering, and CastingDirector's
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

    public override IReadOnlyDictionary<string, string> ColumnHeaders { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // The column shows the class exp modifier (ExpTable + 100), not the raw field.
            ["ExpTable"] = "Exp %",
        };

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
            ["ExpTable"]   = FormatExpModifier,
        };

    // The class exp modifier as the game — and the MajorMUD reference browser — show it. The raw
    // MDB ExpTable is the class's delta ABOVE the 100% baseline, so the modifier a player reads
    // is ExpTable + 100 (ExperienceTableCalculator.CalcExpChart adds the same 100 to the class
    // term). Rendered with a trailing % so the column reads as the modifier, not the stored field;
    // the raw value still drives search, and the grid's leading-number sort ignores the %.
    internal static string? FormatExpModifier(string? raw)
        => int.TryParse(raw, System.Globalization.NumberStyles.Integer,
               System.Globalization.CultureInfo.InvariantCulture, out int v)
            ? (v + 100).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%"
            : raw;

    public ClassesSectionViewModel(GameDataCache cache, SettingsResolver? resolver = null) : base(cache, resolver) { }

    // Synthesise the "Abilities" column from each row's Abil-N / AbilVal-N pairs so the grid
    // shows every class skill at a glance (e.g. Warrior → "Bash", Ninja → "ClassStealth,
    // FindTraps, Dodge +25, Crits +10, ..." — whatever the MDB encodes). Code 59 (ClassOk) is
    // suppressed in class context: its value on a class row is internal / inert data — Druid is
    // the only stock class with an entry, AbilVal=74 with no user-facing meaning.
    private static readonly int[] _skipInClassContext = { 59 };

    protected override IReadOnlyDictionary<string, string?> ComputeRowCells(GameRow element)
        => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Abilities"] = AbilityNames.SummarizeAbilities(element, skipCodes: _skipInClassContext),
        };
}
