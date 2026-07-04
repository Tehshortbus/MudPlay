using System.Collections.Generic;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

// Game Data Browser → TextBlocks tab. Reads MajorMUD's TBInfo table — the per-textblock
// metadata index (link target / action / callsite). The full text body lives in a separate Jet
// table that some MDB exports omit; this listing surfaces the index so quests, signs, and NPC
// dialogue triggers stay browseable.
//
// Column names mirror the MajorMUD MDB schema verbatim. Number is the textblock id (referenced
// by Rooms.CMD / Rooms.Spell / monster greet text), LinkTo chains textblocks together, Action is
// the script string the engine evaluates when the textblock fires (encrypted in some MDB
// versions), Called From is the source-side reverse pointer.
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

    public TextBlocksSectionViewModel(GameDataCache cache, SettingsResolver? resolver = null) : base(cache, resolver) { }
}
