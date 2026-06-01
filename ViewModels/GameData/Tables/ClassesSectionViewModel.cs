using System.Collections.Generic;
using System.Text.Json;
using FujinTerm.Game.GameData;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Classes tab. Static MDB class definitions —
/// drives the Workshop CharacterPlanner ability previews, the Spells
/// tab's class filtering, and Phase 13 CastingDirector's
/// class-specific cure-spell selection.
/// </summary>
/// <remarks>
/// Column names mirror the MajorMUD MDB schema verbatim. <c>MinHits</c>
/// / <c>MaxHits</c> bracket starting HP roll, <c>ExpTable</c> is the
/// progression curve, <c>MageryLVL</c> is the cap on castable-spell
/// level. <c>MageryType</c>, <c>WeaponType</c>, and <c>ArmourType</c>
/// render via <see cref="MmudEnums"/>.
/// </remarks>
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
            ["MageryType"] = MmudEnums.FormatMagery,
            ["WeaponType"] = MmudEnums.FormatClassWeaponType,
            ["ArmourType"] = MmudEnums.FormatArmourType,
        };

    public ClassesSectionViewModel(GameDataCache cache, SettingsResolver? resolver = null) : base(cache, resolver) { }

    /// <summary>
    /// Synthesise the "Abilities" column from each row's <c>Abil-N</c> /
    /// <c>AbilVal-N</c> pairs so the grid shows every class skill at a
    /// glance (e.g. Warrior → "Bash", Ninja → "Stealth, ShadowStealth, ..."
    /// — whatever the MDB encodes).
    /// </summary>
    protected override IReadOnlyDictionary<string, string?> ComputeRowCells(JsonElement element)
        => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Abilities"] = AbilityNames.SummarizeAbilities(element),
        };
}
