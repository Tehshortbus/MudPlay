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
/// Placeholder vocabulary. A <b>string</b> placeholder captures an arbitrary
/// run of text, a <b>number</b> placeholder captures a numeric span. Some
/// string tokens are <b>semantic</b> — they tag the capture with a role so a
/// caller can pin the exact slot instead of guessing by position:
/// <list type="bullet">
/// <item><c>{spellname}</c> → the spell's full name (role: spell).</item>
/// <item><c>{target}</c> → who the spell was used on (role: target).</item>
/// <item><c>{source}</c> → who cast it; a player name or <c>"You"</c> for a
/// self-cast (role: source).</item>
/// <item><c>{s}</c> → a generic name (spell or actor), role-agnostic; surfaced
/// in template order. The shipped seed uses this form.</item>
/// <item><c>{d}</c>, <c>{dmg}</c>, <c>{damage}</c> → a numeric capture (e.g.
/// damage a spell dealt), consumed but dropped — confirmation never needs the
/// value.</item>
/// </list>
/// All string captures are surfaced in template order. When a template pins
/// both a <c>{spellname}</c> and a <c>{target}</c> slot,
/// <see cref="ConfirmsSpellTarget"/> matches those exact slots — so the
/// <c>{source}</c> (which may be <c>"You"</c> or another player) can never be
/// mistaken for the target. For legacy <c>{s}</c>-only templates it falls back
/// to requiring the spell name and target each appear as distinct captures, so
/// an unrelated cast on the same member (e.g. a buff landing on a poisoned
/// ally) still can't falsely confirm a cure. Literal text between placeholders
/// is matched verbatim (regex-escaped). The pattern is unanchored so leading /
/// trailing noise (colour resets, prompt fragments) on the emitted line doesn't
/// defeat the match.
/// </para>
/// </remarks>
public sealed class CasterMessageMatcher
{
    // Placeholder set: semantic string slots {spellname}/{target}/{source},
    // generic legacy string slot {s}, and numeric slots {d}/{dmg}/{damage}.
    // All appear verbatim in authored / shipped templates, so they must
    // tokenize — not be matched as literal text. Keep in sync with
    // Placeholders below (the user-facing reference for the same vocabulary).
    private static readonly Regex TokenSplit =
        new(@"\{(?:spellname|target|source|damage|dmg|[sd])\}", RegexOptions.Compiled);

    /// <summary>
    /// User-facing token reference for the Game Data → Messages editor: each
    /// placeholder paired with what it captures. Drives the editor's legend so
    /// authors know which token pins which slot.
    /// </summary>
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
    private readonly int[] _stringGroupIndexes;
    private readonly PlaceholderRole[] _stringRoles;

    /// <summary>The template this matcher was built from (verbatim).</summary>
    public string Template { get; }

    private CasterMessageMatcher(
        string template, Regex regex, int[] stringGroupIndexes, PlaceholderRole[] stringRoles)
    {
        Template = template;
        _regex = regex;
        _stringGroupIndexes = stringGroupIndexes;
        _stringRoles = stringRoles;
    }

    /// <summary>
    /// Build a matcher for <paramref name="template"/>, or <c>null</c> when
    /// the template is blank or contains no string placeholder (nothing
    /// to confirm a target against — such a record can't drive party-buff
    /// confirmation).
    /// </summary>
    public static CasterMessageMatcher? TryCreate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;

        StringBuilder pattern = new();
        List<int> stringGroups = new();
        List<PlaceholderRole> stringRoles = new();
        int group = 0;
        int last = 0;
        bool sawString = false;

        foreach (Match tok in TokenSplit.Matches(template))
        {
            pattern.Append(Regex.Escape(template.Substring(last, tok.Index - last)));
            group++;
            if (tok.Value is "{d}" or "{dmg}" or "{damage}")
            {
                pattern.Append("(-?\\d[\\d,]*)");
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
        pattern.Append(Regex.Escape(template[last..]));

        if (!sawString) return null;

        Regex regex = new(pattern.ToString(),
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
        return new CasterMessageMatcher(
            template, regex, stringGroups.ToArray(), stringRoles.ToArray());
    }

    private static PlaceholderRole RoleOf(string token) => token switch
    {
        "{spellname}" => PlaceholderRole.Spell,
        "{target}"    => PlaceholderRole.Target,
        "{source}"    => PlaceholderRole.Source,
        _             => PlaceholderRole.Unknown, // {s}
    };

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
    /// True when <paramref name="line"/> matches AND a capture equals
    /// <paramref name="target"/> (case-insensitive, trimmed). When the template
    /// pins a <c>{target}</c> slot, only that slot is checked; otherwise any
    /// capture may satisfy it. The caller passes the given-name it actually cast
    /// on, so a stray line that happens to fit the template but names someone
    /// else doesn't falsely confirm the pending cast.
    /// </summary>
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

    /// <summary>
    /// True when <paramref name="line"/> matches AND it names both the spell and
    /// the target. When the template pins both a <c>{spellname}</c> and a
    /// <c>{target}</c> slot, those exact slots are matched — so the
    /// <c>{source}</c> (which may be <c>"You"</c> or another player) can't be
    /// mistaken for the target. Otherwise (legacy <c>{s}</c>-only templates)
    /// <paramref name="spell"/> and <paramref name="target"/> must each equal a
    /// string capture at a <i>distinct</i> position. Requiring the spell name —
    /// not just the target — is what stops a different spell landing on the same
    /// member from confirming: casting <c>bless</c> on a poisoned ally yields
    /// <c>You cast bless on Forged!</c>, whose captures are
    /// <c>["bless", "Forged"]</c>; matching the cure record for
    /// <c>cure poison</c> fails because <c>cure poison</c> isn't a capture.
    /// </summary>
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

    // First capture carrying <paramref name="role"/>, or -1. Index aligns with
    // the TryMatch capture array (both ordered by template position).
    private int IndexOfRole(PlaceholderRole role)
    {
        for (int i = 0; i < _stringRoles.Length; i++)
            if (_stringRoles[i] == role) return i;
        return -1;
    }

    private static bool Same(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), System.StringComparison.OrdinalIgnoreCase);
}
