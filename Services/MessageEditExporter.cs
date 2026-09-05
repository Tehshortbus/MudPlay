using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MudPlay.Models.GameData;

namespace MudPlay.Services;

// Builds the "Upload edits" export from the Incomplete Messages tab: it diffs the
// active set's live message catalogue against the bundled (shipped) seed for the same
// realm and reports every record the user has ADDED or CHANGED, keyed by the spell (or
// item) it's linked to. The dev ingests the resulting Markdown to fold the edits back
// into the seed for that game type — so a curated line a user fills in-game reaches the
// next shipped seed without a manual back-and-forth.
//
// Diff and Render are pure (no I/O) so the shape is unit-tested; the caller (the section
// VM's command) loads the bundled baseline and writes the document to the Desktop.
public static class MessageEditExporter
{
    // A single record that differs from the seed baseline.
    public sealed record RecordEdit(
        string Kind,                 // "added" | "changed"
        string LinkKey,              // "Spells#114" / "Items#784" / "name:foo"
        int? SpellNumber,            // populated when linked to a spell
        int? ItemNumber,             // populated when linked to an item
        string Name,
        IReadOnlyList<FieldEdit> Fields);

    public readonly record struct FieldEdit(string Field, string? Seed, string New);

    // The message fields a user can edit and the dev needs to ingest. Flags folds the
    // effect checkboxes (ailment / disabled / attack-prevented) into one comparable token.
    private static readonly string[] TextFields =
        { "Caster", "Target", "Witness", "Applied", "WearsOff", "ConfuseFumble", "CastResponse" };

    // Produce the ADDED / CHANGED edits of current vs baseline, keyed by primary link.
    // Records identical to the seed (or absent from current) yield nothing.
    public static IReadOnlyList<RecordEdit> Diff(
        IEnumerable<MessageRecord> current, IEnumerable<MessageRecord> baseline)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(baseline);

        Dictionary<string, MessageRecord> baseByKey = new(StringComparer.OrdinalIgnoreCase);
        foreach (MessageRecord b in baseline)
            baseByKey.TryAdd(KeyOf(b), b);   // first wins — seed has one record per link

        List<RecordEdit> edits = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (MessageRecord c in current)
        {
            string key = KeyOf(c);
            if (!seen.Add(key)) continue;    // a duplicate current key: report the first

            if (!baseByKey.TryGetValue(key, out MessageRecord? b))
            {
                // No seed counterpart — a wholly new record the user authored.
                List<FieldEdit> all = FieldsOf(c)
                    .Where(f => f.Value.Length > 0)
                    .Select(f => new FieldEdit(f.Label, null, f.Value))
                    .ToList();
                edits.Add(Make("added", key, c, all));
                continue;
            }

            // Existing record: per-field seed → current where they differ.
            Dictionary<string, string> cur = FieldsOf(c).ToDictionary(f => f.Label, f => f.Value);
            Dictionary<string, string> old = FieldsOf(b).ToDictionary(f => f.Label, f => f.Value);
            List<FieldEdit> changed = new();
            foreach (string label in AllLabels())
            {
                string cv = cur.GetValueOrDefault(label, string.Empty);
                string bv = old.GetValueOrDefault(label, string.Empty);
                if (!string.Equals(cv, bv, StringComparison.Ordinal))
                    changed.Add(new FieldEdit(label, bv, cv));
            }
            if (changed.Count > 0)
                edits.Add(Make("changed", key, c, changed));
        }
        return edits;
    }

    // Render the edits as a Markdown document: a human-readable section per record plus a
    // trailing machine-readable JSON block for scripted ingestion into the seed.
    public static string Render(
        IReadOnlyList<RecordEdit> edits, string realm, string setName, DateTime capturedAt)
    {
        ArgumentNullException.ThrowIfNull(edits);
        StringBuilder sb = new();
        sb.Append("# MudPlay message edits — ").Append(realm).Append('\n');
        sb.Append("Game-data set: `").Append(setName).Append("`  \n");
        sb.Append("Captured: ").Append(capturedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("  \n");
        sb.Append("Edits vs the shipped seed: **").Append(edits.Count).Append("**\n\n");

        if (edits.Count == 0)
        {
            sb.Append("_No message edits to report — the catalogue matches the shipped seed._\n");
            return sb.ToString();
        }

        foreach (RecordEdit e in edits)
        {
            string id = e.SpellNumber is { } sp ? $"Spell #{sp}"
                      : e.ItemNumber is { } it ? $"Item #{it}"
                      : "(unlinked)";
            sb.Append("## ").Append(id).Append(" — ").Append(e.Name)
              .Append("  (").Append(e.Kind).Append(")\n\n");
            foreach (FieldEdit f in e.Fields)
            {
                sb.Append("- **").Append(f.Field).Append("**: ");
                if (f.Seed is null) sb.Append("*(new)* → `").Append(f.New).Append("`\n");
                else sb.Append('`').Append(f.Seed).Append("` → `").Append(f.New).Append("`\n");
            }
            sb.Append('\n');
        }

        // Machine-readable block: realm-tagged, keyed by link, so an ingest script can apply
        // each edit to the right seed record without re-parsing the prose above.
        sb.Append("## Ingest data\n\n```json\n");
        sb.Append("{\n  \"realm\": \"").Append(realm).Append("\",\n  \"edits\": [\n");
        for (int i = 0; i < edits.Count; i++)
        {
            RecordEdit e = edits[i];
            sb.Append("    {\"link\": \"").Append(e.LinkKey).Append("\", ");
            if (e.SpellNumber is { } sp) sb.Append("\"spell\": ").Append(sp).Append(", ");
            if (e.ItemNumber is { } it) sb.Append("\"item\": ").Append(it).Append(", ");
            sb.Append("\"name\": ").Append(JsonStr(e.Name)).Append(", \"kind\": \"").Append(e.Kind).Append("\", \"fields\": {");
            sb.Append(string.Join(", ", e.Fields.Select(f => $"{JsonStr(f.Field)}: {JsonStr(f.New)}")));
            sb.Append("}}");
            sb.Append(i < edits.Count - 1 ? ",\n" : "\n");
        }
        sb.Append("  ]\n}\n```\n");
        return sb.ToString();
    }

    // Desktop file name for an export: message-edits-{realm}-{yyyyMMdd-HHmmss}.md.
    public static string FileName(string realm, DateTime capturedAt) =>
        $"message-edits-{realm}-{capturedAt:yyyyMMdd-HHmmss}.md";

    private static RecordEdit Make(string kind, string key, MessageRecord r, IReadOnlyList<FieldEdit> fields)
    {
        int? spell = FirstLink(r, "Spells");
        int? item = FirstLink(r, "Items");
        return new RecordEdit(kind, key, spell, item, r.Name, fields);
    }

    // Primary link key: the spell it recognizes, else the item, else its name — the same
    // identity the seed pairs records on, so current and baseline align.
    private static string KeyOf(MessageRecord r)
    {
        if (FirstLink(r, "Spells") is { } sp) return $"Spells#{sp}";
        if (FirstLink(r, "Items") is { } it) return $"Items#{it}";
        return "name:" + r.Name.Trim().ToLowerInvariant();
    }

    private static int? FirstLink(MessageRecord r, string table)
    {
        if (r.Links is null) return null;
        foreach (GameDataLink l in r.Links)
            if (string.Equals(l.Table, table, StringComparison.OrdinalIgnoreCase))
                return l.Number;
        return null;
    }

    private static IEnumerable<(string Label, string Value)> FieldsOf(MessageRecord r)
    {
        yield return ("Caster", r.CasterMessage ?? string.Empty);
        yield return ("Target", r.TargetMessage ?? string.Empty);
        yield return ("Witness", r.WitnessMessage ?? string.Empty);
        yield return ("Applied", r.AppliedMessage ?? string.Empty);
        yield return ("WearsOff", r.AppliedEndsWith ?? string.Empty);
        yield return ("ConfuseFumble", r.ConfuseFumbleLine ?? string.Empty);
        yield return ("CastResponse", r.CastResponse ?? string.Empty);
        yield return ("Flags", r.Flags == MessageFlags.None ? string.Empty : r.Flags.ToString());
    }

    private static IEnumerable<string> AllLabels()
    {
        foreach (string f in TextFields) yield return f;
        yield return "Flags";
    }

    private static string JsonStr(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
}
