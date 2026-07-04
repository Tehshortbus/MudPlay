using System.Text;
using System.Text.RegularExpressions;

namespace FujinTerm.Game.Spells;

// Compiles a game-data "caster message" template — the line YOU see when YOU cast
// a spell, e.g. You cast {s} on {s}! — into a regex and matches it against an
// observed server line, returning the ordered string captures.
//
// Used by CastingDirector to confirm OUR successful cast on a party member before
// starting the buff-duration timer: when we send bles raijin and the bless
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
    private readonly int[] _stringGroupIndexes;
    private readonly int[] _numberGroupIndexes;
    private readonly PlaceholderRole[] _stringRoles;

    // The template this matcher was built from (verbatim).
    public string Template { get; }

    private CasterMessageMatcher(
        string template, Regex regex,
        int[] stringGroupIndexes, int[] numberGroupIndexes, PlaceholderRole[] stringRoles)
    {
        Template = template;
        _regex = regex;
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
    public static CasterMessageMatcher? TryCreate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;

        StringBuilder pattern = new();
        List<int> stringGroups = new();
        List<int> numberGroups = new();
        List<PlaceholderRole> stringRoles = new();
        int group = 0;
        int last = 0;
        bool sawString = false;
        bool sawNumber = false;

        foreach (Match tok in TokenSplit.Matches(template))
        {
            pattern.Append(Regex.Escape(template.Substring(last, tok.Index - last)));
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
        pattern.Append(Regex.Escape(template[last..]));

        if (!sawString && !sawNumber) return null;

        Regex regex = new(pattern.ToString(),
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
        return new CasterMessageMatcher(
            template, regex,
            stringGroups.ToArray(), numberGroups.ToArray(), stringRoles.ToArray());
    }

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
        if (string.IsNullOrEmpty(line)) return false;

        Match m = _regex.Match(line);
        if (!m.Success) return false;

        string[] caps = new string[_stringGroupIndexes.Length];
        for (int i = 0; i < _stringGroupIndexes.Length; i++)
            caps[i] = m.Groups[_stringGroupIndexes[i]].Value;
        stringCaptures = caps;
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
        if (string.IsNullOrEmpty(line)) return false;

        Match m = _regex.Match(line);
        if (!m.Success) return false;

        foreach (int gi in _numberGroupIndexes)
        {
            string raw = m.Groups[gi].Value;
            if (raw.Length == 0) continue;
            if (int.TryParse(
                    raw.Replace(",", string.Empty),
                    System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int v))
            {
                damage = v;
                break;
            }
        }
        return true;
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
