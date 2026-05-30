using System.Collections.Generic;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → TextBlocks tab. Reads MajorMUD's <c>TBInfo</c>
/// table — the per-textblock metadata index (link target / action /
/// callsite). The full text body lives in a separate Jet table that
/// some MDB exports omit; this listing surfaces the index so quests,
/// signs, and NPC dialogue triggers stay browseable.
/// </summary>
/// <remarks>
/// Column names mirror the MajorMUD MDB schema verbatim. <c>Number</c>
/// is the textblock id (referenced by Rooms.CMD / Rooms.Spell /
/// monster greet text), <c>LinkTo</c> chains textblocks together,
/// <c>Action</c> is the script string the engine evaluates when the
/// textblock fires (encrypted in some MDB versions), <c>Called From</c>
/// is the source-side reverse pointer.
/// </remarks>
public sealed class TextBlocksSectionViewModel : JsonTableSectionViewModel
{
    public override string Id => "textblocks";
    public override string Title => "TextBlocks";

    protected override string TableName => "TBInfo";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Number",
        "LinkTo",
        "Action",
        "Called From",
    };

    public override string SearchKeyColumn => "Action";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "textblock", "tb", "quest", "dialogue", "sign", "action",
    };

    public TextBlocksSectionViewModel(GameDataCache cache) : base(cache) { }
}
