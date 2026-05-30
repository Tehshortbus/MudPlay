using System.Collections.Generic;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → TextBlocks tab. Static MDB text-blob table —
/// quest text, NPC dialogue, signs. Referenced by the Phase 9
/// Workshop QUESTS section's checkbox list (resolves quests by
/// MinLevel from the active set's TextBlocks).
/// </summary>
public sealed class TextBlocksSectionViewModel : GameDataTableSectionViewModel
{
    public override string Id => "textblocks";
    public override string Title => "TextBlocks";

    protected override string TableName => "TextBlocks";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Id",
        "Name",
        "Type",
        "MinLevel",
        "Text",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "textblock", "quest", "dialogue", "sign",
    };

    public TextBlocksSectionViewModel(GameDataCache cache) : base(cache) { }
}
