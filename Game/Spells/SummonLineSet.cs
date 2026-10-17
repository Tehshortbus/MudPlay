using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Spells;

// Recognises the line a summon spell prints when someone else casts it — "The {source}
// shouts for aid!" — from the message records linked to every spell with the Summon
// ability. Only the Casting-on-you and witness wordings are read: the caster wording is
// our own cast, never a monster's. One wording can belong to several summon spells
// (510, 528 and 593 all shout for aid), which is fine: any of them means a monster is
// on its way.
public sealed class SummonLineSet
{
    private const int AbilSummon = 12;

    private readonly (CasterMessageMatcher Matcher, string Anchor)[] _lines;

    public SummonLineSet(IEnumerable<int> summonSpells, IEnumerable<MessageRecord> records)
    {
        HashSet<int> numbers = new(summonSpells);
        HashSet<string> templates = new(StringComparer.Ordinal);
        foreach (MessageRecord r in records)
        {
            if (r.Links is null || !r.Links.Any(l => l.Table.Equals("Spells", StringComparison.OrdinalIgnoreCase)
                                                    && numbers.Contains(l.Number)))
                continue;
            Add(r.TargetMessage);
            if (!MessageRecord.IsBlankOrAbsent(r.WitnessMessage))
                foreach (string w in r.WitnessMessage.Split('\n')) Add(w);
        }

        List<(CasterMessageMatcher, string)> lines = new();
        foreach (string t in templates)
            if (CasterMessageMatcher.LongestLiteralWord(t) is { Length: > 0 } word
                && CasterMessageMatcher.TryCreate(t) is { } matcher)
                lines.Add((matcher, word));
        _lines = lines.ToArray();

        // A wording with barely any literal text ("The {source} {spellname}!") would
        // claim unrelated lines, the same floor the message catalogue applies.
        void Add(string? template)
        {
            if (MessageRecord.IsBlankOrAbsent(template)) return;
            string t = template!.Trim();
            if (CasterMessageMatcher.LiteralTextLength(t) < MessageRecord.MinRecognitionPatternLength) return;
            templates.Add(t);
        }
    }

    // The Spells-table numbers of every spell with a Summon ability.
    public static IReadOnlyList<int> SummonSpells(GameTable spellsTable)
    {
        List<int> summons = new();
        foreach (GameRow row in spellsTable.Rows)
        {
            SpellFormulaInput formula = SpellFormulaReader.Read(row);
            if (formula.Abilities.Any(a => a.Code == AbilSummon)) summons.Add(formula.Number);
        }
        return summons;
    }

    // True when line is a summon wording; source is the caster the line names, or null
    // when the wording doesn't name one.
    public bool TryMatch(string? line, out string? source)
    {
        source = null;
        if (string.IsNullOrEmpty(line)) return false;
        foreach ((CasterMessageMatcher matcher, string anchor) in _lines)
        {
            if (!line.Contains(anchor, StringComparison.Ordinal)) continue;
            if (!matcher.TryMatchCaptures(line, out MessageCaptures captures)) continue;
            source = captures.Source;
            return true;
        }
        return false;
    }
}
