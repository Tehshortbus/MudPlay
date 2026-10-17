using MudPlay.Models.GameData;

namespace MudPlay.Game.Spells;

// "Does any message in the catalogue describe this wire line?" across every
// TEMPLATED slot in the Messages table — the {source} casts {spellname} on
// {target}! shapes whose stored text can never be compared to a real line
// directly.
//
// Exists because the unrecognized-line capture compared wire text against the raw
// slot strings. A template never string-equals an actual line, so every templated
// message in the game read as "nothing recognizes this" and perfectly well-known
// casts ("Raijin casts minor healing on Raijin!") were staged for human review.
//
// The catalogue holds thousands of slots but only around 850 DISTINCT templates,
// and running even 850 regexes per inbound line would be wasteful, so each matcher
// is filed under one literal word from its template. A template can only match a
// line containing every one of its literal words, so testing just the matchers
// filed under words the line actually contains narrows the work to a handful
// without ever missing a match that a full sweep would have found.
public sealed class MessageTemplateIndex
{
    private readonly Dictionary<string, List<CasterMessageMatcher>> _byWord =
        new(StringComparer.OrdinalIgnoreCase);

    public MessageTemplateIndex(IEnumerable<MessageRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (MessageRecord r in records)
        {
            Add(r.CasterMessage, seen);
            Add(r.TargetMessage, seen);
            // WitnessMessage can hold several wordings, one per line (a room spell's
            // ambient set) — index each so a templated witness among them still matches.
            if (!MessageRecord.IsBlankOrAbsent(r.WitnessMessage))
                foreach (string wording in r.WitnessMessage.Split('\n'))
                    Add(wording, seen);
            Add(r.AppliedMessage, seen);
            Add(r.AppliedEndsWith, seen);
        }
    }

    private void Add(string? template, HashSet<string> seen)
    {
        if (MessageRecord.IsBlankOrAbsent(template)) return;
        string text = template!.Trim();
        // Literal slots carry no placeholder, so they're compared as plain text by
        // the caller and have no business being compiled into a regex here.
        if (!text.Contains('{')) return;
        if (!seen.Add(text)) return;

        // Some shipped templates pin almost no literal text — "The {source}
        // {spellname}!" claims essentially any line starting "The" and ending "!",
        // and really did claim a room-spell trigger during review. Placeholders match
        // anything, so literal length IS the template's selectivity, and the same
        // floor the Contains-matched slots use for the same reason applies here: a
        // pattern this thin can only mis-recognize. Such a template still works for
        // its own job (confirming a cast we already know we made, where the caller
        // supplies the spell and target) — it just can't identify a line on its own.
        if (CasterMessageMatcher.LiteralTextLength(text)
            < MessageRecord.MinRecognitionPatternLength) return;
        // No literal text at all leaves nothing to file the matcher under.
        if (CasterMessageMatcher.LongestLiteralWord(text) is not { Length: > 0 } word)
            return;
        if (CasterMessageMatcher.TryCreate(text, compiled: false) is not { } matcher)
            return;

        if (!_byWord.TryGetValue(word, out List<CasterMessageMatcher>? bucket))
            _byWord[word] = bucket = new();
        bucket.Add(matcher);
    }

    // True when some catalogue template describes line.
    public bool Matches(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || _byWord.Count == 0) return false;

        // Distinct words only: "the" recurring three times shouldn't re-run its
        // bucket three times.
        HashSet<string>? tried = null;
        foreach (string word in Words(line))
        {
            if (!_byWord.TryGetValue(word, out List<CasterMessageMatcher>? bucket)) continue;
            tried ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!tried.Add(word)) continue;
            foreach (CasterMessageMatcher matcher in bucket)
                if (matcher.TryMatch(line, out _)) return true;
        }
        return false;
    }

    // Maximal letter runs — the same shape LongestLiteralWord pulls out of a
    // template, so a template's index word is findable in a line it matches.
    private static IEnumerable<string> Words(string line)
    {
        int i = 0;
        while (i < line.Length)
        {
            if (!char.IsLetter(line[i])) { i++; continue; }
            int start = i;
            while (i < line.Length && char.IsLetter(line[i])) i++;
            yield return line[start..i];
        }
    }
}
