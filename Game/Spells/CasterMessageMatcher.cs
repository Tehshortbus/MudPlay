using System.Text;
using System.Text.RegularExpressions;

namespace FujinTerm.Game.Spells;

/// <summary>
/// Compiles a game-data "caster message" template — the line YOU see when
/// YOU cast a spell, e.g. <c>You cast {s} on {s}!</c> — into a regex and
/// matches it against an observed server line, returning the ordered
/// string captures.
/// </summary>
/// <remarks>
/// <para>
/// Used by <see cref="CastingDirector"/> to confirm OUR successful cast on
/// a party member before starting the buff-duration timer: when we send
/// <c>bles raijin</c> and the bless record's caster line is
/// <c>You cast {s} on {s}!</c>, the server echo <c>You cast bless on
/// Raijin!</c> matches and yields the captures <c>["bless", "Raijin"]</c>.
/// We already know who we targeted, so the caller correlates by comparing
/// the captures to the pending target name rather than guessing which
/// <c>{s}</c> is the target.
/// </para>
/// <para>
/// Placeholder vocabulary (matches the wcc-derived <c>messages.json</c>
/// templates). A <b>string</b> placeholder captures an arbitrary run of
/// text, a <b>number</b> placeholder captures a numeric span:
/// <list type="bullet">
/// <item><c>{s}</c> (spell or actor name), <c>{target}</c>, <c>{source}</c>
/// compile to a string capture.</item>
/// <item><c>{d}</c>, <c>{dmg}</c> compile to a numeric capture, consumed but
/// dropped — confirmation never needs the value.</item>
/// </list>
/// All string captures are surfaced in template order; the matcher does not
/// assume which slot is the spell vs the target vs the actor. Callers that
/// know the expected spell name and target use
/// <see cref="ConfirmsSpellTarget"/>, which requires both to appear as
/// distinct captures — so an unrelated cast on the same member (e.g. a buff
/// landing on a poisoned ally) can't falsely confirm a cure. Literal text
/// between placeholders is matched verbatim (regex-escaped). The pattern is
/// unanchored so leading / trailing noise (colour resets, prompt fragments)
/// on the emitted line doesn't defeat the match.
/// </para>
/// </remarks>
public sealed class CasterMessageMatcher
{
    // The seed's full placeholder set: string slots {s}/{target}/{source} and
    // numeric slots {d}/{dmg}. The named slots already appear verbatim in the
    // shipped seed, so they must tokenize — not be matched as literal text.
    private static readonly Regex TokenSplit =
        new(@"\{(?:target|source|dmg|[sd])\}", RegexOptions.Compiled);

    private readonly Regex _regex;
    private readonly int[] _stringGroupIndexes;

    /// <summary>The template this matcher was built from (verbatim).</summary>
    public string Template { get; }

    private CasterMessageMatcher(string template, Regex regex, int[] stringGroupIndexes)
    {
        Template = template;
        _regex = regex;
        _stringGroupIndexes = stringGroupIndexes;
    }

    /// <summary>
    /// Build a matcher for <paramref name="template"/>, or <c>null</c> when
    /// the template is blank or contains no <c>{s}</c> placeholder (nothing
    /// to confirm a target against — such a record can't drive party-buff
    /// confirmation).
    /// </summary>
    public static CasterMessageMatcher? TryCreate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;

        StringBuilder pattern = new();
        List<int> stringGroups = new();
        int group = 0;
        int last = 0;
        bool sawString = false;

        foreach (Match tok in TokenSplit.Matches(template))
        {
            pattern.Append(Regex.Escape(template.Substring(last, tok.Index - last)));
            group++;
            if (tok.Value is "{d}" or "{dmg}")
            {
                pattern.Append("(-?\\d[\\d,]*)");
            }
            else
            {
                // Non-greedy so adjacent literals delimit each name; the
                // trailing literal (e.g. " on " / "!") forces a boundary.
                pattern.Append("(.+?)");
                stringGroups.Add(group);
                sawString = true;
            }
            last = tok.Index + tok.Length;
        }
        pattern.Append(Regex.Escape(template[last..]));

        if (!sawString) return null;

        Regex regex = new(pattern.ToString(),
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
        return new CasterMessageMatcher(template, regex, stringGroups.ToArray());
    }

    /// <summary>
    /// Try to match <paramref name="line"/>. On success, populates
    /// <paramref name="stringCaptures"/> with the <c>{s}</c> group values in
    /// template order and returns <c>true</c>.
    /// </summary>
    public bool TryMatch(string? line, out IReadOnlyList<string> stringCaptures)
    {
        stringCaptures = System.Array.Empty<string>();
        if (string.IsNullOrEmpty(line)) return false;

        Match m = _regex.Match(line);
        if (!m.Success) return false;

        string[] caps = new string[_stringGroupIndexes.Length];
        for (int i = 0; i < _stringGroupIndexes.Length; i++)
            caps[i] = m.Groups[_stringGroupIndexes[i]].Value;
        stringCaptures = caps;
        return true;
    }

    /// <summary>
    /// True when <paramref name="line"/> matches AND one of the <c>{s}</c>
    /// captures equals <paramref name="target"/> (case-insensitive,
    /// trimmed). The caller passes the given-name it actually cast on, so a
    /// stray line that happens to fit the template but names someone else
    /// doesn't falsely confirm the pending cast.
    /// </summary>
    public bool ConfirmsTarget(string? line, string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        if (!TryMatch(line, out IReadOnlyList<string> caps)) return false;
        foreach (string c in caps)
            if (string.Equals(c.Trim(), target.Trim(), System.StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// True when <paramref name="line"/> matches AND <paramref name="spell"/>
    /// and <paramref name="target"/> each equal a string capture at a
    /// <i>distinct</i> position (case-insensitive, trimmed). Requiring the
    /// spell name to appear — not just the target — is what stops a different
    /// spell landing on the same member from confirming: casting
    /// <c>bless</c> on a poisoned ally yields <c>You cast bless on Forged!</c>,
    /// whose captures are <c>["bless", "Forged"]</c>; matching the cure record
    /// for <c>cure poison</c> fails because <c>cure poison</c> isn't a
    /// capture. Position-agnostic so the same call confirms a caster line
    /// (<c>You cast {spell} on {target}!</c>) and a witness line
    /// (<c>{src} casts {spell} on {target}!</c>) without knowing which slot is
    /// which.
    /// </summary>
    public bool ConfirmsSpellTarget(string? line, string spell, string target)
    {
        if (string.IsNullOrWhiteSpace(spell) || string.IsNullOrWhiteSpace(target))
            return false;
        if (!TryMatch(line, out IReadOnlyList<string> caps)) return false;

        int spellIdx = -1;
        for (int i = 0; i < caps.Count; i++)
            if (string.Equals(caps[i].Trim(), spell.Trim(), System.StringComparison.OrdinalIgnoreCase))
            {
                spellIdx = i;
                break;
            }
        if (spellIdx < 0) return false;

        for (int i = 0; i < caps.Count; i++)
            if (i != spellIdx
                && string.Equals(caps[i].Trim(), target.Trim(), System.StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
