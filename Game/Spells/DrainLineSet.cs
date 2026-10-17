using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Spells;

// Recognises a drain spell's damage lines. A drain heals whoever lands it by the damage
// it did (user, 2026-09-29; GAME_MECHANICS "Heal lines — who was healed, and by how
// much"), so a party member's drain is a heal on them too. Built from every spell with
// a drain ability and the damage-bearing caster / witness wordings of its record —
// necromantic bolt's chained "{target}'s life is drained for {damage} damage!", or
// "You cast vampiric touch on {target}, draining {damage} hit points!".
//
// Only asked about damage lines a party member dealt, so it's far off the hot path;
// each template still sits under its longest literal word, as in HealLineReader.
public sealed class DrainLineSet
{
    private const int AbilDrain = 8;

    private readonly string[] _anchors;
    private readonly (CasterMessageMatcher Matcher, int Anchor)[] _lines;

    public DrainLineSet(IEnumerable<int> drainSpells, IEnumerable<MessageRecord> records)
    {
        HashSet<int> numbers = new(drainSpells);
        HashSet<string> templates = new(StringComparer.Ordinal);
        foreach (MessageRecord r in records)
        {
            if (r.Links is null || !r.Links.Any(l => l.Table.Equals("Spells", StringComparison.OrdinalIgnoreCase)
                                                    && numbers.Contains(l.Number)))
                continue;
            Add(r.CasterMessage);
            if (!MessageRecord.IsBlankOrAbsent(r.WitnessMessage))
                foreach (string w in r.WitnessMessage.Split('\n')) Add(w);
        }

        List<string> anchors = new();
        List<(CasterMessageMatcher, int)> lines = new();
        foreach (string t in templates)
        {
            if (CasterMessageMatcher.LongestLiteralWord(t) is not { } word
                || CasterMessageMatcher.TryCreate(t) is not { } matcher) continue;
            int anchor = anchors.IndexOf(word);
            if (anchor < 0) { anchor = anchors.Count; anchors.Add(word); }
            lines.Add((matcher, anchor));
        }
        _anchors = anchors.ToArray();
        _lines = lines.ToArray();

        void Add(string? template)
        {
            if (MessageRecord.IsBlankOrAbsent(template)) return;
            string t = template!.Trim();
            if (t.Contains("{damage}") || t.Contains("{dmg}") || t.Contains("{d}")) templates.Add(t);
        }
    }

    // The Spells-table numbers of every spell with a drain ability.
    public static IReadOnlyList<int> DrainSpells(GameTable spellsTable)
    {
        List<int> drains = new();
        foreach (GameRow row in spellsTable.Rows)
        {
            SpellFormulaInput formula = SpellFormulaReader.Read(row);
            if (formula.Abilities.Any(a => a.Code == AbilDrain)) drains.Add(formula.Number);
        }
        return drains;
    }

    public bool IsDrain(string? line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        foreach ((CasterMessageMatcher matcher, int anchor) in _lines)
            if (line.Contains(_anchors[anchor], StringComparison.Ordinal) && matcher.TryMatch(line, out _))
                return true;
        return false;
    }
}
