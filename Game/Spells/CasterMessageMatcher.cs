using System.Text;
using System.Text.RegularExpressions;

namespace FujinTerm.Game.Spells;

/// <summary>
/// Compiles a game-data "caster message" template — the line YOU see when
/// YOU cast a spell, e.g. <c>You cast {s} on {s}!</c> — into a regex and
/// matches it against an observed server line, returning the ordered
/// <c>{s}</c> (string) captures.
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
/// templates): <c>{s}</c> = an arbitrary string (spell / target name),
/// <c>{d}</c> = a number (damage / heal amount). Only <c>{s}</c> captures
/// are surfaced — <c>{d}</c> spans are consumed but dropped since the buff
/// confirmation path never needs the numeric value. Literal text between
/// placeholders is matched verbatim (regex-escaped). The pattern is
/// unanchored so leading / trailing noise (colour resets, prompt fragments)
/// on the emitted line doesn't defeat the match.
/// </para>
/// </remarks>
public sealed class CasterMessageMatcher
{
    private static readonly Regex TokenSplit =
        new(@"\{[sd]\}", RegexOptions.Compiled);

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
            if (tok.Value == "{s}")
            {
                // Non-greedy so adjacent literals delimit each name; the
                // trailing literal (e.g. " on " / "!") forces a boundary.
                pattern.Append("(.+?)");
                stringGroups.Add(group);
                sawString = true;
            }
            else
            {
                pattern.Append("(-?\\d[\\d,]*)");
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
}
