using System.Text.Json;
using System.Text.RegularExpressions;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game.GameData;

// Lays the realm's own message lines over the built-in message catalogue. A set
// exported with a Messages table (Nightmare Redux 1.8.4 and later) carries the lines
// its realm prints, built from the realm's message file, so a customised realm's
// wording reaches the catalogue: MudRev prints "A frigid shield of ice forms around
// your body!" where the built-in record says "You feel resistant to cold".
//
// Per field, the table wins: where it has a line for a slot of a spell's record, that
// line replaces the built-in one, unless the two read the same (the built-in form is
// then kept). The table never has the line a bystander sees, nor lines the engine
// prints in its standard shapes, so those slots keep the built-in text. A spell the
// built-in catalogue has no record for gets one from the table.
//
// Only rows of a spell are used. The table's other rows (traps, monster hits and
// deaths, chase and monster-entrance lines, failures) name no spell, and the
// catalogue's readers take a record without one in ways those lines don't fit: a
// trap line holding a condition's text would keep that condition from latching on it.
//
// A replaced record keeps its built-in Id, so the user's edits to it (flags, links)
// still find it: MessageStore lays them over this result as it would over the seed.
public static partial class MdbMessageOverlay
{
    // The table's file in the set: "Messages.json" would land on the client's own
    // messages.json, so MdbImporter writes it under its prefix.
    public const string TableName = MdbImporter.RenamedPrefix + "Messages";

    public sealed record Result(List<MessageRecord> Records, int RecordsChanged, int LinesReplaced, int RecordsAdded);

    private sealed record Row(string Name, string Kind, string Msg, string EndMsg, string[] Effects, int Spell);

    // The record a spell's rows fill, and which row goes in each slot.
    private sealed record Slots(Row? Cast, Row? Incoming, Row? Status);

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

    // seed with the table's lines laid over it. spellName gives a spell's name by
    // number; spellFlags the conditions its ability codes prove, which a record the
    // table gives a status line takes on beside its own (the catalogue's flags come
    // from the linked spell's codes, GAME_MECHANICS "Condition Effects flags derive
    // from the linked spell's ability codes").
    public static Result Apply(
        IReadOnlyList<MessageRecord> seed, GameTable table,
        Func<int, string?> spellName, Func<int, MessageFlags> spellFlags)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(spellName);
        ArgumentNullException.ThrowIfNull(spellFlags);

        Dictionary<int, List<Row>> bySpell = new();
        foreach (Row row in ReadRows(table))
        {
            if (row.Spell <= 0) continue;
            if (!bySpell.TryGetValue(row.Spell, out List<Row>? rows)) bySpell[row.Spell] = rows = [];
            rows.Add(row);
        }

        HashSet<Row> used = new(ReferenceEqualityComparer.Instance);
        List<MessageRecord> records = new(seed.Count);
        int changed = 0, replaced = 0;
        foreach (MessageRecord record in seed)
        {
            List<(int Spell, Row Row)> rows = SpellsOf(record)
                .Where(bySpell.ContainsKey)
                .SelectMany(n => bySpell[n].Select(r => (n, r)))
                .ToList();
            if (rows.Count == 0) { records.Add(record); continue; }

            Slots slots = new(
                Pick(rows, IsCast, (r, spell) => Same(record.CasterMessage, r.Msg, spell), spellName),
                Pick(rows, IsIncoming, (r, spell) => Same(record.TargetMessage, r.Msg, spell), spellName),
                Pick(rows, IsStatus, (r, _) => Same(record.AppliedMessage, r.Msg, null)
                                               && Same(record.AppliedEndsWith, r.EndMsg, null), spellName));
            (MessageRecord merged, int lines) = Fill(record, slots, spellName, spellFlags, used);
            records.Add(merged);
            if (lines > 0) { changed++; replaced += lines; }
        }

        // Spells the seed links no record to, and lines of a spell its records had no
        // slot left for (a second cast line, printed once the spell lands). A spell's
        // first new record starts from the seed record of its name, when there is one,
        // so it keeps the lines the table doesn't carry.
        Dictionary<string, MessageRecord> seedByName = new(StringComparer.OrdinalIgnoreCase);
        foreach (MessageRecord record in seed) seedByName.TryAdd(record.Name.Trim(), record);
        HashSet<int> linkedInSeed = seed.SelectMany(SpellsOf).ToHashSet();
        int added = 0;
        foreach ((int spell, List<Row> rows) in bySpell.OrderBy(kv => kv.Key))
        {
            Queue<Row> casts = new(rows.Where(r => !used.Contains(r) && IsCast(r)));
            Queue<Row> incoming = new(rows.Where(r => !used.Contains(r) && IsIncoming(r)));
            Queue<Row> statuses = new(rows.Where(r => !used.Contains(r) && IsStatus(r)));
            string name = spellName(spell) is { Length: > 0 } n ? n : rows[0].Name;
            bool first = !linkedInSeed.Contains(spell);
            while (casts.Count + incoming.Count + statuses.Count > 0)
            {
                Slots slots = new(Next(casts), Next(incoming), Next(statuses));
                MessageRecord start = first && seedByName.TryGetValue(name, out MessageRecord? sameName)
                    ? sameName with { Links = null }
                    : new MessageRecord("", name, MessageFlags.None, 0, "", "", "", "", "");
                first = false;
                (MessageRecord filled, _) = Fill(start, slots, spellName, spellFlags, used);
                if (IsEmpty(filled)) continue;
                filled = filled with
                {
                    Id = MessageRecord.ComputeId(filled.Name, filled.CasterMessage, filled.TargetMessage,
                        filled.WitnessMessage, filled.AppliedMessage, filled.AppliedEndsWith),
                    Links = [new GameDataLink("Spells", spell)],
                };
                records.Add(filled);
                added++;
            }
        }

        return new Result(records, changed, replaced, added);

        static Row? Next(Queue<Row> rows) => rows.Count > 0 ? rows.Dequeue() : null;

        static bool IsEmpty(MessageRecord r) =>
            MessageRecord.IsBlankOrAbsent(r.CasterMessage) && MessageRecord.IsBlankOrAbsent(r.TargetMessage)
            && MessageRecord.IsBlankOrAbsent(r.WitnessMessage) && MessageRecord.IsBlankOrAbsent(r.AppliedMessage)
            && MessageRecord.IsBlankOrAbsent(r.AppliedEndsWith);
    }

    // The record with each slot a row is given for taken from the table, and how many
    // slots changed. A status line also brings the spell's condition flags.
    private static (MessageRecord Record, int Lines) Fill(
        MessageRecord record, Slots slots, Func<int, string?> spellName,
        Func<int, MessageFlags> spellFlags, HashSet<Row> used)
    {
        int lines = 0;
        string caster = record.CasterMessage, target = record.TargetMessage;
        string applied = record.AppliedMessage, endsWith = record.AppliedEndsWith;
        MessageFlags flags = record.Flags;

        if (slots.Cast is { } cast)
        {
            used.Add(cast);
            string? spell = spellName(cast.Spell);
            Take(ref caster, CatalogueForm(cast.Msg, spell), spell);
        }
        if (slots.Incoming is { } hit)
        {
            used.Add(hit);
            string? spell = spellName(hit.Spell);
            Take(ref target, CatalogueForm(hit.Msg, spell), spell);
        }
        if (slots.Status is { } status)
        {
            used.Add(status);
            Take(ref applied, status.Msg, null);
            Take(ref endsWith, status.EndMsg, null);
            MessageFlags proven = spellFlags(status.Spell);
            if ((flags & (MessageFlags.MovementPrevented | MessageFlags.AttackPrevented)) != 0)
                proven &= ~MessageFlags.Confused;
            flags |= proven;
        }

        if (lines == 0 && flags == record.Flags) return (record, 0);
        return (record with
        {
            CasterMessage   = caster,
            TargetMessage   = target,
            AppliedMessage  = applied,
            AppliedEndsWith = endsWith,
            Flags           = flags,
            RawFlagsHex     = (ushort)(record.RawFlagsHex | (ushort)flags),
        }, lines);

        // A line with almost no text of its own ("{spellname} {target} {damage}", a
        // monster spell's) would match nearly any line, so it never replaces one.
        void Take(ref string slot, string line, string? spell)
        {
            if (CasterMessageMatcher.LiteralTextLength(line) < MessageRecord.MinRecognitionPatternLength
                || Same(slot, line, spell)) return;
            slot = line;
            lines++;
        }
    }

    // The row of the record's spells to fill a slot from: one that reads the same as
    // what the slot holds when there is one, so a spell with two cast lines keeps the
    // one the record already has, else the first.
    private static Row? Pick(
        List<(int Spell, Row Row)> rows, Func<Row, bool> fits, Func<Row, string?, bool> readsAsSlot,
        Func<int, string?> spellName)
    {
        Row? first = null;
        foreach ((int spell, Row row) in rows)
        {
            if (!fits(row)) continue;
            if (readsAsSlot(row, spellName(spell))) return row;
            first ??= row;
        }
        return first;
    }

    private static bool IsCast(Row row) => row.Kind == "Cast" && row.Msg.Length > 0;

    // A line printed to me when the spell lands, with no wear-off line: the incoming
    // line. A damage-shield line is printed when my shield hits back, and a fail line
    // when an action of mine fails, so neither is the spell landing on me.
    private static bool IsIncoming(Row row) =>
        row.Kind == "Self" && row.EndMsg.Length == 0 && row.Msg.Length > 0
        && Array.IndexOf(row.Effects, "DamageShield") < 0 && Array.IndexOf(row.Effects, "Fail") < 0;

    private static bool IsStatus(Row row) => row.Kind == "Self" && row.EndMsg.Length > 0;

    // A table line in the catalogue's placeholders: {dmg} becomes {damage}, an
    // argument the export couldn't fill ({?}) a generic name, and the spell's own
    // name, which the export writes out, {spellname}, so a line that names its spell
    // still pins it (a cure is confirmed by the spell and the target it names).
    internal static string CatalogueForm(string line, string? spell)
    {
        string form = line.Replace("{dmg}", "{damage}", StringComparison.Ordinal)
                          .Replace("{?}", "{s}", StringComparison.Ordinal);
        if (string.IsNullOrEmpty(spell)) return form;
        Match m = Regex.Match(form, $@"(?<![A-Za-z]){Regex.Escape(spell)}(?![A-Za-z])", RegexOptions.CultureInvariant);
        return m.Success ? form[..m.Index] + "{spellname}" + form[(m.Index + m.Length)..] : form;
    }

    // True when a slot's text and a table line read the same once the spell's name is
    // put in for {spellname}, the damage placeholders are one, and a closing "!" or "."
    // is ignored: the built-in status lines leave it off, as they're matched as part
    // of a line.
    internal static bool Same(string slot, string line, string? spell)
    {
        if (MessageRecord.IsBlankOrAbsent(slot)) return false;
        return Comparable(slot, spell) == Comparable(line, spell);
    }

    private static string Comparable(string text, string? spell)
    {
        string t = DamageToken().Replace(text, "{damage}");
        if (!string.IsNullOrEmpty(spell)) t = t.Replace("{spellname}", spell, StringComparison.Ordinal);
        return t.TrimEnd().TrimEnd('!', '.');
    }

    [GeneratedRegex(@"\{(?:dmg|d)\}")]
    private static partial Regex DamageToken();

    private static IEnumerable<int> SpellsOf(MessageRecord record) =>
        record.Links?.Where(l => string.Equals(l.Table, "Spells", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Number).Distinct() ?? [];

    private static IEnumerable<Row> ReadRows(GameTable table)
    {
        foreach (GameRow row in table.Rows)
        {
            string[] effects = Text(row, "Effects").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            yield return new Row(Text(row, "Name"), Text(row, "Kind"), Text(row, "Msg"), Text(row, "EndMsg"), effects, Int(row, "Spell"));
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
