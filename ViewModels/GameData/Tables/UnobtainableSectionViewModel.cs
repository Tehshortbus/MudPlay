using System.Collections.Generic;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Tables;

// Game Data Browser → Unobtainable tab. Lists the Items and Monsters rows the game marks
// out of play ("In Game" == 0) — sysop-only, unimplemented, or duplicate test rows ("bow of
// silver", the "large rock" placeholders, "longsword1..5", the extra "dark cleric" NPCs).
// The Item Finder catalogue skips the items (ItemFinderEntry.IsObtainable) and the Monsters
// table leaves the monsters out, so nothing only a sysop can produce passes for a real
// spawn; rather than leaving them simply hidden, this read-only view collects them so they
// can be inspected. The Kind column says which table a row came from, and Reason why it's
// here: flagged out of play by the game data, or a monster flagged in play that can never
// spawn (StrayMonsterRule).
public sealed class UnobtainableSectionViewModel : JsonTableSectionViewModel
{
    private const string FlaggedReason = "Flagged out of play";
    private const string StrayReason = "Only listed under a room that spawns a different NPC";

    private static readonly IReadOnlyDictionary<string, string?> ItemKind =
        new Dictionary<string, string?> { ["Kind"] = "Item", ["Reason"] = FlaggedReason };
    private static readonly IReadOnlyDictionary<string, string?> FlaggedMonster =
        new Dictionary<string, string?> { ["Kind"] = "Monster", ["Reason"] = FlaggedReason };
    private static readonly IReadOnlyDictionary<string, string?> StrayMonster =
        new Dictionary<string, string?> { ["Kind"] = "Monster", ["Reason"] = StrayReason };

    private readonly RoomGraphManager? _roomGraph;

    public UnobtainableSectionViewModel(GameDataCache cache, SettingsResolver? resolver = null,
                                        RoomGraphManager? roomGraph = null)
        : base(cache, resolver)
    {
        _roomGraph = roomGraph;
    }

    public override string Id => "unobtainable";
    public override string Title => "Unobtainable";

    // The primary table; PopulateRows adds the Monsters rows as well.
    protected override string TableName => "Items";

    // An unobtainable row carries the same fields as the record it came from, so the table
    // shows them all (read-only) rather than a curated slice: the item block mirrors
    // ItemsSectionViewModel.Columns, then the monster stats. ArmourClass / DamageResist are
    // fields of both, so they share a column.
    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Number", "Name", "Kind", "Reason",
        "ItemType", "Worn", "WeaponType", "ArmourType", "Min", "Max",
        "ArmourClass", "DamageResist", "Speed", "Accy", "StrReq", "Encum", "Price", "Currency",
        "HP", "EXP", "AvgDmg", "Align",
    };

    public override IReadOnlyDictionary<string, string>? ColumnHeaders { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EXP"]    = "Exp",
            ["AvgDmg"] = "Avg Damage",
            ["Align"]  = "Alignment",
        };

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "unobtainable", "in game", "sysop", "unimplemented", "placeholder", "test item", "never spawns",
        "monster", "mob", "npc",
    };

    protected override IReadOnlyDictionary<string, Func<string?, string?>> ColumnFormatters { get; } =
        new Dictionary<string, Func<string?, string?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ItemType"]   = LookupEnums.FormatItemType,
            ["Worn"]       = LookupEnums.FormatWornSlot,
            ["WeaponType"] = LookupEnums.FormatWeaponType,
            ["ArmourType"] = LookupEnums.FormatArmourType,
            ["Currency"]   = LookupEnums.FormatCurrency,
            ["HP"]         = MonstersSectionViewModel.FormatThousands,
            ["EXP"]        = MonstersSectionViewModel.FormatThousands,
            ["Align"]      = LookupEnums.FormatMonAlignment,
        };

    // Items whose "In Game" is explicitly 0 (the inverse of ItemFinderEntry.IsObtainable),
    // then monsters that are either that or a stray — the inverse of the Monsters table's
    // filter. Absent / non-numeric / non-zero "In Game" means in play.
    protected override void PopulateRows(IList<GameDataRow> rows)
    {
        Func<RoomKey, Room?>? getRoom = _roomGraph is null ? null : _roomGraph.GetRoom;
        AddTableRows(rows, "Items", InGameFlag.IsOutOfPlay, static _ => ItemKind);
        AddTableRows(rows, "Monsters", el => StrayMonsterRule.IsOutOfPlay(el, getRoom),
            el => InGameFlag.IsOutOfPlay(el) ? FlaggedMonster : StrayMonster);
    }
}
