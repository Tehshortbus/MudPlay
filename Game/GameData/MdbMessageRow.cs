using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Game.GameData;

// One row of a set's Messages table, as an NMR 1.8.4 export writes it (GAME_MECHANICS
// "The export's `Messages` table (Nightmare Redux 1.8.4)"). Kind is Cast, Self or
// Other; Spell is 0 on a row that belongs to no spell.
public sealed record MdbMessageRow(string Name, string Kind, string Msg, string EndMsg, IReadOnlyList<string> Effects, int Spell)
{
    public bool HasEffect(string effect) => Effects.Contains(effect, StringComparer.Ordinal);

    private static readonly string[] RequiredColumns = ["Name", "Kind", "Msg", "EndMsg", "Effects", "Spell"];

    // Why the table can't be read as a Messages table, or null when it can. Another
    // exporter's table of that name has other columns.
    public static string? Unreadable(GameTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        foreach (GameRow row in table.Rows)
        {
            foreach (string column in RequiredColumns)
                if (!row.TryGetProperty(column, out _))
                    return $"it has no {column} column";
            return null;
        }
        return "it has no rows";
    }

    public static IEnumerable<MdbMessageRow> Read(GameTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        foreach (GameRow row in table.Rows)
        {
            string[] effects = Text(row, "Effects").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            yield return new MdbMessageRow(Text(row, "Name"), Text(row, "Kind"), Text(row, "Msg"), Text(row, "EndMsg"),
                effects, Int(row, "Spell"));
        }
    }

    // A text cell. The export writes an empty value as a lone NUL.
    private static string Text(GameRow row, string field) =>
        row.TryGetProperty(field, out GameValue value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text
            ? text.Trim('\0', ' ', '\t', '\r', '\n')
            : "";

    private static int Int(GameRow row, string field) =>
        row.TryGetProperty(field, out GameValue value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number)
            ? number
            : 0;
}
