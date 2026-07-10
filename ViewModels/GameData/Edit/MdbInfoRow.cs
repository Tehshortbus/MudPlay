using System.Collections.Generic;

namespace FujinTerm.ViewModels.GameData.Edit;

// One label/value row in a monster's read-only "Other Info (from MDB)" pane.
// Most rows are plain text; a row backed by a TBInfo textblock (currently the
// Greet row) also carries the command keywords that block responds to, so the
// view can offer a click-through popup listing them. Actions is null for plain
// rows. Key/Value are named to match the previous KeyValuePair binding so the
// existing template markup keeps working for plain rows.
public sealed record MdbInfoRow(string Key, string Value, IReadOnlyList<string>? Actions = null)
{
    // View binds this to switch between a plain TextBlock and a clickable button
    // with a keyword flyout.
    public bool HasActions => Actions is { Count: > 0 };
}
