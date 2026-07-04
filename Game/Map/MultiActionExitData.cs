using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FujinTerm.Game.Map;

// Parsed multi-action prerequisite data attached to a
// RoomExitHint.MultiActionHidden exit. Populated by RoomGraphManager's
// second-pass action-data scan from the verbose Action#N entries in adjacent
// exit fields.
//
// RequiredActionCount is how many actions the player must execute before the
// exit unlocks (from the modifier's "Needs N Actions" prefix).
// RequiresSpecificOrder is true when the modifier reads "specific order" —
// actions must run StepNumber-ascending — false on "any order". Actions is the
// sorted action list (StepNumber ascending); when it's partial
// (count < RequiredActionCount) the walker fails the exit at runtime, since
// the data is malformed for our purposes.
public sealed partial record MultiActionExitData(
    int RequiredActionCount,
    bool RequiresSpecificOrder,
    IReadOnlyList<ExitAction> Actions)
{
    // True when at least one action is remote (different room).
    public bool HasRemoteActions
    {
        get
        {
            foreach (ExitAction a in Actions) if (a.RemoteSourceRoom is not null) return true;
            return false;
        }
    }

    // Parse the modifier text following the Hidden prefix — e.g. "Needs 2
    // Actions, any order" or "Needs 1 Actions, specific order". Returns
    // (count, specific); defaults to (1, false) when the pattern doesn't match
    // (data anomaly tolerance — at least one bad row exists in the v1.11p set).
    public static (int Count, bool SpecificOrder) ParseModifier(string raw)
    {
        Match m = NeedsActionsRegex().Match(raw);
        if (!m.Success) return (1, false);
        if (!int.TryParse(m.Groups[1].Value, out int count) || count < 1) count = 1;
        bool specific = m.Groups[2].Value.Contains("specific", StringComparison.OrdinalIgnoreCase);
        // Cap absurd action counts (data anomaly: room 1/1810 has 1278)
        if (count > 20) count = 1;
        return (count, specific);
    }

    // Parse an "Action#N [on the {dir} exit of {this room | room M/R}]: cmd1,
    // cmd2" cell. Returns null when the shape doesn't match.
    public static ActionCell? ParseActionCell(string cell)
    {
        if (string.IsNullOrWhiteSpace(cell)) return null;
        Match m = ActionCellRegex().Match(cell);
        if (!m.Success) return null;
        int step = 1;
        if (m.Groups["step"].Success && !int.TryParse(m.Groups["step"].Value, out step))
            return null;

        Direction? dir = ParseDir(m.Groups["dir"].Value);
        if (dir is null) return null;

        RoomKey? remote = null;
        string roomGrp = m.Groups["room"].Value;
        if (!string.IsNullOrWhiteSpace(roomGrp))
        {
            string[] parts = roomGrp.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2
                && int.TryParse(parts[0], out int rmap)
                && int.TryParse(parts[1], out int rroom))
                remote = new RoomKey(rmap, rroom);
        }

        var cmds = new List<string>();
        foreach (string raw in m.Groups["cmds"].Value.Split(','))
        {
            string t = raw.Trim();
            // Trailing modifier on the last command (e.g. "(Item: 1289)")
            // — strip a parenthetical suffix to keep the actual command.
            int paren = t.IndexOf('(');
            if (paren > 0) t = t[..paren].TrimEnd();
            if (t.Length > 0) cmds.Add(t);
        }
        if (cmds.Count == 0) return null;
        return new ActionCell(step, dir.Value, remote, cmds);
    }

    private static Direction? ParseDir(string s) => s.Trim().ToLowerInvariant() switch
    {
        "n" or "north" => Direction.N,
        "s" or "south" => Direction.S,
        "e" or "east"  => Direction.E,
        "w" or "west"  => Direction.W,
        "ne" or "northeast" => Direction.NE,
        "nw" or "northwest" => Direction.NW,
        "se" or "southeast" => Direction.SE,
        "sw" or "southwest" => Direction.SW,
        "u" or "up"    => Direction.U,
        "d" or "down"  => Direction.D,
        _ => null,
    };

    // Intermediate parse result from ParseActionCell.
    public sealed record ActionCell(
        int StepNumber,
        Direction ExitDirection,
        RoomKey? RemoteSourceRoom,
        IReadOnlyList<string> Commands);

    [GeneratedRegex(@"\bNeeds\s+(\d+)\s+Actions?,\s*(any|specific)\s+order",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NeedsActionsRegex();

    // "#N" is optional — single-action exits (Needs 1 Actions) drop
    // the step number and use a bare "Action [on the W exit of …]"
    // prefix in the v1.11p data set. When absent, ParseActionCell
    // defaults StepNumber to 1.
    [GeneratedRegex(
        @"^Action(?:#(?<step>\d+))?\s*\[on the\s+(?<dir>\w+)\s+exit of\s+(?:this room|room\s+(?<room>\d+/\d+))\]\s*:\s*(?<cmds>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ActionCellRegex();
}
