using System.Text;
using System.Text.RegularExpressions;

namespace MudPlay.Game.Spells;

// Compiles a game-data "caster message" template — the line YOU see when YOU cast
// a spell, e.g. You cast {s} on {s}! — into a regex and matches it against an
// observed server line, returning the ordered string captures.
//
// Its first job is confirming OUR successful cast on a party member before a
// buff-duration timer starts: when we send bles raijin and the bless
// record's caster line is You cast {s} on {s}!, the server echo You cast bless on
// Raijin! matches and yields the captures ["bless", "Raijin"]. We already know who
// we targeted, so the caller correlates by comparing the captures to the pending
// target name rather than guessing which {s} is the target.
//
// Placeholder vocabulary: a string placeholder captures an arbitrary run of text,
// a number placeholder captures a numeric span. Some string tokens are semantic —
// they tag the capture with a role so a caller can pin the exact slot instead of
// guessing by position:
//   {spellname} → the spell's full name (role: spell).
//   {target}    → who the spell was used on (role: target).
//   {source}    → who cast it; a player name or "You" for a self-cast (role: source).
//   {s}         → a generic name (spell or actor), role-agnostic; surfaced in
//                 template order. The shipped seed uses this form.
//   {d}, {dmg}, {damage} → a numeric capture (e.g. damage a spell / proc dealt).
//                 The confirmation path (TryMatch) drops it — it never needs the
//                 value — but TryMatchDamage surfaces it for the damage-message
//                 recogniser.
//
// All string captures are surfaced in template order. When a template pins both a
// {spellname} and a {target} slot, ConfirmsSpellTarget matches those exact slots —
// so the {source} (which may be "You" or another player) can never be mistaken for
// the target. For legacy {s}-only templates it falls back to requiring the spell
// name and target each appear as distinct captures, so an unrelated cast on the
// same member (e.g. a buff landing on a poisoned ally) still can't falsely confirm
// a cure. Literal text between placeholders is matched verbatim (regex-escaped).
// The pattern is unanchored so leading / trailing noise (colour resets, prompt
// fragments) on the emitted line doesn't defeat the match.
public sealed class CasterMessageMatcher
{
    // Placeholder set: semantic string slots {spellname}/{target}/{source},
    // generic legacy string slot {s}, and numeric slots {d}/{dmg}/{damage}.
    // All appear verbatim in authored / shipped templates, so they must
    // tokenize — not be matched as literal text. Keep in sync with
    // Placeholders below (the user-facing reference for the same vocabulary).
    private static readonly Regex TokenSplit =
        new(@"\{(?:spellname|target|source|damage|dmg|[sd])\}", RegexOptions.Compiled);

    // User-facing token reference for the Game Data → Messages editor: each
    // placeholder paired with what it captures. Drives the editor's legend so
    // authors know which token pins which slot.
    public static IReadOnlyList<MessagePlaceholder> Placeholders { get; } = new MessagePlaceholder[]
    {
        new("{spellname}", "The spell's full (long-form) name."),
        new("{target}",    "Who the spell was used on."),
        new("{source}",    "Who cast it — a player name, or \"You\" for a self-cast."),
        new("{damage}",    "How much damage a damage spell did (the number)."),
        new("{s}",         "Generic name — a spell or actor; matched by position (legacy)."),
        new("{d}",         "Generic number — matched then dropped (legacy)."),
    };

    private enum PlaceholderRole { Unknown, Spell, Target, Source }

    private readonly Regex _regex;
    // The template's literal text, cut at the placeholders: one run before the first
    // placeholder, one after each. A run is empty where the template opens or closes
    // with a placeholder, or two placeholders touch.
    private readonly string[] _literalRuns;
    private readonly int[] _stringGroupIndexes;
    private readonly int[] _numberGroupIndexes;
    private readonly PlaceholderRole[] _stringRoles;

    // The template this matcher was built from (verbatim).
    public string Template { get; }

    private CasterMessageMatcher(
        string template, Regex regex, string[] literalRuns,
        int[] stringGroupIndexes, int[] numberGroupIndexes, PlaceholderRole[] stringRoles)
    {
        Template = template;
        _regex = regex;
        _literalRuns = literalRuns;
        _stringGroupIndexes = stringGroupIndexes;
        _numberGroupIndexes = numberGroupIndexes;
        _stringRoles = stringRoles;
    }

    // Build a matcher for the template, or null when the template is blank or
    // carries no placeholder at all. A string placeholder is required for the
    // confirmation helpers (ConfirmsTarget / ConfirmsSpellTarget) to have anything
    // to match a name against; a numeric-only template (e.g. ... bursts for {damage}
    // damage!) is still accepted so the damage-message recogniser can compile it —
    // the confirmation helpers then simply find no capture and decline.
    // compiled=false builds an interpreted regex instead. The handful of matchers a
    // pending cast needs are worth compiling; MessageTemplateIndex builds one per
    // DISTINCT template in the whole catalogue (~850), where emitting IL for each
    // would cost far more in startup time and memory than the interpreter costs in
    // per-line matching.
    public static CasterMessageMatcher? TryCreate(string? template, bool compiled = true)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;

        StringBuilder pattern = new();
        List<string> literalRuns = new();
        List<int> stringGroups = new();
        List<int> numberGroups = new();
        List<PlaceholderRole> stringRoles = new();
        int group = 0;
        int last = 0;
        bool sawString = false;
        bool sawNumber = false;

        foreach (Match tok in TokenSplit.Matches(template))
        {
            string literal = template.Substring(last, tok.Index - last);
            literalRuns.Add(literal);
            pattern.Append(Regex.Escape(literal));
            group++;
            if (tok.Value is "{d}" or "{dmg}" or "{damage}")
            {
                pattern.Append("(-?\\d[\\d,]*)");
                numberGroups.Add(group);
                sawNumber = true;
            }
            else
            {
                // Non-greedy so adjacent literals delimit each name; the
                // trailing literal (e.g. " on " / "!") forces a boundary.
                pattern.Append("(.+?)");
                stringGroups.Add(group);
                stringRoles.Add(RoleOf(tok.Value));
                sawString = true;
            }
            last = tok.Index + tok.Length;
        }
        literalRuns.Add(template[last..]);
        pattern.Append(Regex.Escape(template[last..]));

        if (!sawString && !sawNumber) return null;

        RegexOptions options = RegexOptions.CultureInvariant;
        if (compiled) options |= RegexOptions.Compiled;
        Regex regex = new(pattern.ToString(), options);
        return new CasterMessageMatcher(
            template, regex, literalRuns.ToArray(),
            stringGroups.ToArray(), numberGroups.ToArray(), stringRoles.ToArray());
    }

    // False when the line cannot match: the template's literal runs must all sit in
    // it, in order, each placeholder between them taking at least one character.
    // The regex is tried from every position of the line and its wildcards stretch
    // to the line's end from each, so on a line that doesn't match its time grows
    // with the square of the line's length, or worse. A search's reply is one
    // unbroken line however long the floor list is, and 240 cast-line templates
    // spent seconds refusing a 2,200-character one (report
    // paradigm-20261009-164508). This walk is one pass per run, and only a line
    // that gets through it is handed to the regex.
    internal bool LiteralRunsOccurInOrder(string line)
    {
        int at = 0;
        for (int i = 0; i < _literalRuns.Length; i++)
        {
            if (i > 0)
            {
                at++;   // the placeholder ahead of this run
                if (at > line.Length) return false;
            }
            string run = _literalRuns[i];
            if (run.Length == 0) continue;
            int found = line.IndexOf(run, at, StringComparison.Ordinal);
            if (found < 0) return false;
            at = found + run.Length;
        }
        return true;
    }

    private Match? MatchLine(string? line)
    {
        if (string.IsNullOrEmpty(line) || !LiteralRunsOccurInOrder(line)) return null;
        Match m = _regex.Match(line);
        return m.Success ? m : null;
    }

    // The longest run of letters in the template's LITERAL text (placeholders
    // removed), or null when the template is nothing but placeholders and
    // separators. Any line this template matches must contain that word, which is
    // what lets MessageTemplateIndex shortlist candidate matchers per line instead
    // of running the whole catalogue. Longest wins because it's the most selective —
    // keying on "casts" narrows far better than "the".
    //
    // A null return means the template carries no literal text at all ("{s} {s}"),
    // so it would match virtually any line and can never identify one — callers
    // must skip such templates rather than treat them as always-matching.
    public static string? LongestLiteralWord(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;

        string literals = TokenSplit.Replace(template, "\n");
        string? best = null;
        int i = 0;
        while (i < literals.Length)
        {
            if (!char.IsLetter(literals[i])) { i++; continue; }
            int start = i;
            while (i < literals.Length && char.IsLetter(literals[i])) i++;
            if (best is null || i - start > best.Length) best = literals[start..i];
        }
        return best;
    }

    // How much LITERAL text the template pins down, placeholders excluded. This is
    // the whole of a template's selectivity: the placeholders match anything, so two
    // templates differing only in literal text differ entirely in how many lines they
    // claim. "The {source} {spellname}!" pins just 7 characters and therefore matches
    // almost any exclamation, which is why bulk recognizers need to weigh this.
    public static int LiteralTextLength(string? template) =>
        string.IsNullOrEmpty(template) ? 0 : TokenSplit.Replace(template, string.Empty).Length;

    private static PlaceholderRole RoleOf(string token) => token switch
    {
        "{spellname}" => PlaceholderRole.Spell,
        "{target}"    => PlaceholderRole.Target,
        "{source}"    => PlaceholderRole.Source,
        _             => PlaceholderRole.Unknown, // {s}
    };

    // Try to match the line. On success, populates stringCaptures with the {s}
    // group values in template order and returns true.
    public bool TryMatch(string? line, out IReadOnlyList<string> stringCaptures)
    {
        stringCaptures = System.Array.Empty<string>();
        if (MatchLine(line) is not { } m) return false;

        string[] caps = new string[_stringGroupIndexes.Length];
        for (int i = 0; i < _stringGroupIndexes.Length; i++)
            caps[i] = m.Groups[_stringGroupIndexes[i]].Value;
        stringCaptures = caps;
        return true;
    }

    // Match the line and say where in it the message sits, for a caller that has to
    // know whether some other text of the line lies inside the message or beside it.
    public bool TryMatchSpan(string? line, out int start, out int length)
    {
        start = 0;
        length = 0;
        if (MatchLine(line) is not { } m) return false;
        start = m.Index;
        length = m.Length;
        return true;
    }

    // Match the line and surface the first numeric ({d} / {dmg} / {damage}) capture
    // as damage. Returns true when the line matches the template — even if the
    // template has no numeric slot, in which case damage stays 0 (a recognised cast
    // that simply did no numeric damage); false only when the line doesn't match.
    // This is the value the confirmation-only TryMatch drops; the damage recogniser
    // uses it to tally proc / attack-spell rows.
    public bool TryMatchDamage(string? line, out int damage)
    {
        damage = 0;
        if (MatchLine(line) is not { } m) return false;

        damage = FirstNumber(m) ?? 0;
        return true;
    }

    // How many name captures (every placeholder but the numeric ones) the template has.
    public int NameCaptureCount => _stringGroupIndexes.Length;

    // Whether the template pins each semantic slot, and whether it carries a number.
    public bool PinsTarget => IndexOfRole(PlaceholderRole.Target) >= 0;
    // True when some slot of the template can hold the name of whoever the spell
    // was cast on: a pinned {target}, or a generic {s}. A template made only of the
    // spell's and the caster's names (You cast {spellname}!) reads the same whoever
    // the spell lands on, so no line built from it can tell one target from another.
    public bool CanNameTarget =>
        IndexOfRole(PlaceholderRole.Target) >= 0 || IndexOfRole(PlaceholderRole.Unknown) >= 0;
    public bool PinsSpell => IndexOfRole(PlaceholderRole.Spell) >= 0;
    public bool HasNumber => _numberGroupIndexes.Length > 0;

    // Match the line and hand back every capture by what it is: the names in template
    // order, the pinned {spellname} / {target} / {source} slots (null when the template
    // doesn't pin one), and the first number (null when there's none). For a reader
    // that has to work out from the line alone who was involved, rather than confirm a
    // cast it already knows about.
    public bool TryMatchCaptures(string? line, out MessageCaptures captures)
    {
        captures = default;
        if (MatchLine(line) is not { } m) return false;

        string[] names = new string[_stringGroupIndexes.Length];
        for (int i = 0; i < _stringGroupIndexes.Length; i++)
            names[i] = m.Groups[_stringGroupIndexes[i]].Value.Trim();
        captures = new MessageCaptures(
            names,
            Spell:  CaptureOf(names, PlaceholderRole.Spell),
            Target: CaptureOf(names, PlaceholderRole.Target),
            Source: CaptureOf(names, PlaceholderRole.Source),
            Number: FirstNumber(m));
        return true;
    }

    private string? CaptureOf(string[] names, PlaceholderRole role)
    {
        int i = IndexOfRole(role);
        return i >= 0 ? names[i] : null;
    }

    private int? FirstNumber(Match m)
    {
        foreach (int gi in _numberGroupIndexes)
        {
            string raw = m.Groups[gi].Value;
            if (raw.Length == 0) continue;
            if (int.TryParse(
                    raw.Replace(",", string.Empty),
                    System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int v))
                return v;
        }
        return null;
    }

    // True when the line matches AND a capture equals target (case-insensitive,
    // trimmed). When the template pins a {target} slot, only that slot is checked;
    // otherwise any capture may satisfy it. The caller passes the given-name it
    // actually cast on, so a stray line that happens to fit the template but names
    // someone else doesn't falsely confirm the pending cast.
    public bool ConfirmsTarget(string? line, string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        if (!TryMatch(line, out IReadOnlyList<string> caps)) return false;

        // A pinned {target} slot wins — match only that capture so a {source}
        // (the caster) can't satisfy the target check.
        int t = IndexOfRole(PlaceholderRole.Target);
        if (t >= 0) return Same(caps[t], target);

        foreach (string c in caps)
            if (Same(c, target)) return true;
        return false;
    }

    // True when the line matches AND it names both the spell and the target. When
    // the template pins both a {spellname} and a {target} slot, those exact slots
    // are matched — so the {source} (which may be "You" or another player) can't be
    // mistaken for the target. Otherwise (legacy {s}-only templates) spell and
    // target must each equal a string capture at a distinct position. Requiring the
    // spell name — not just the target — is what stops a different spell landing on
    // the same member from confirming: casting bless on a poisoned ally yields You
    // cast bless on Forged!, whose captures are ["bless", "Forged"]; matching the
    // cure record for cure poison fails because cure poison isn't a capture.
    public bool ConfirmsSpellTarget(string? line, string spell, string target)
    {
        if (string.IsNullOrWhiteSpace(spell) || string.IsNullOrWhiteSpace(target))
            return false;
        if (!TryMatch(line, out IReadOnlyList<string> caps)) return false;

        // Semantic template: match the pinned spell and target slots exactly.
        int s = IndexOfRole(PlaceholderRole.Spell);
        int t = IndexOfRole(PlaceholderRole.Target);
        if (s >= 0 && t >= 0)
            return Same(caps[s], spell) && Same(caps[t], target);

        // Legacy {s}-only template: spell + target each match a distinct capture.
        int spellIdx = -1;
        for (int i = 0; i < caps.Count; i++)
            if (Same(caps[i], spell)) { spellIdx = i; break; }
        if (spellIdx < 0) return false;

        for (int i = 0; i < caps.Count; i++)
            if (i != spellIdx && Same(caps[i], target))
                return true;
        return false;
    }

    // Pull the FULL target name out of a matched caster line when we only know the
    // shorthand the user typed after the cast code — a hand-typed single-target cast
    // (`gbls fuj`) whose success line names the resolved target in full (`You cast
    // greater bless on Fujin!` → "Fujin"). Prefers a pinned {target} slot; otherwise
    // the capture that STARTS WITH what we typed (the spell-name capture won't, so the
    // shorthand disambiguates target from spell). False when the line doesn't match or
    // nothing fits the shorthand (a different line, or an ambiguous / failed cast).
    public bool TryResolveTarget(string? line, string shorthand, out string target)
    {
        target = string.Empty;
        if (string.IsNullOrWhiteSpace(shorthand)) return false;
        if (!TryMatch(line, out IReadOnlyList<string> caps)) return false;

        string sh = shorthand.Trim();
        int t = IndexOfRole(PlaceholderRole.Target);
        if (t >= 0)
        {
            if (!StartsWithFold(caps[t], sh)) return false;   // pinned target must fit what we typed
            target = caps[t].Trim();
            return true;
        }
        foreach (string c in caps)
            if (StartsWithFold(c, sh)) { target = c.Trim(); return true; }
        return false;
    }

    private static bool StartsWithFold(string capture, string prefix) =>
        capture.Trim().StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase);

    // First capture carrying the given role, or -1. Index aligns with the TryMatch
    // capture array (both ordered by template position).
    private int IndexOfRole(PlaceholderRole role)
    {
        for (int i = 0; i < _stringRoles.Length; i++)
            if (_stringRoles[i] == role) return i;
        return -1;
    }

    private static bool Same(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), System.StringComparison.OrdinalIgnoreCase);
}
