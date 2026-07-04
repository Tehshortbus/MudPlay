using System.Text;
using System.Text.RegularExpressions;

namespace FujinTerm.Game;

// Turns a statline command string (the Settings → Statline editor value)
// into the regex WirePromptScanner matches the live prompt against. The
// editor string is the single source of truth: the same string is sent to
// the BBS (set statline …) and compiled here, so the parser can never drift
// from what the server actually prints.
//
// The produced regex exposes the named groups the scanner's decode loop
// reads — hp, type, mana, statea — so a custom statline flows through the
// exact same observation path as the default one. It is unanchored, matching
// the scanner's behaviour of catching every statline when the server chains
// several on one row.
//
// The default / blank / full command maps to Default: the permissive pattern
// that matches all three class-default shapes (HP-only / HP+MA / HP+KAI).
// Keeping that path on a fixed regex means default-statline users get
// byte-identical behaviour — only authored custom statlines compile a
// bespoke pattern.
//
// Two deliberate constraints keep the single-writer invariant intact (only
// PromptParser writes HP / MA / position):
//  - %H / %M (max HP / max mana) emit non-capturing digit runs — they render
//    on the wire but we don't capture them, so no second write path appears
//    for the max fields (they keep ratcheting from observed values in
//    PromptParser).
//  - The MA / KAI label that precedes %m in a custom statline is literal
//    text, but the decode loop reads a type group off it to set ManaType.
//    The builder special-cases the <label>=%m idiom to capture that literal
//    label as the type group; without it ManaType would silently fall to None
//    and mana display would break.
public static class StatlinePromptRegexBuilder
{
    // Permissive pattern for the class-default statline — matches HP-only,
    // HP+MA, and HP+KAI shapes, resting / meditating in either the in-bracket
    // or trailing position. Same body the scanner shipped before custom
    // statlines existed, so the default path is unchanged.
    public static Regex Default { get; } = new(
        @"\[HP=(?<hp>\d{1,4})(?:\/(?<type>MA|KAI)=(?<mana>\d{1,3}))?(?:\s\((?<statea>Resting|Meditating)\)\s)?\]:(?:\s\((?<stateb>Resting|Meditating)\))?",
        RegexOptions.Compiled);

    // Compile the scanner regex for command. Default / blank / full returns
    // Default; any other command has its wildcard template translated
    // token-by-token into a matching pattern.
    public static Regex Build(string? command)
    {
        if (StatlineSyntax.IsDefault(command)) return Default;

        // Formatting codes (%f0-7, %b0-7, %d, %B, %N, %U, %L, %R) render as ANSI
        // escapes the scanner strips before the regex ever runs, so drop them
        // from the template up front. What's left is the plain wildcard idiom.
        string template = StatlineSyntax.StripFormatting(StatlineSyntax.ExtractTemplate(command!));

        var pattern = new StringBuilder();
        var literal = new StringBuilder();

        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];
            if (c == '%' && i + 1 < template.Length && IsWildcard(template[i + 1]))
            {
                char code = template[i + 1];
                switch (code)
                {
                    case 'm':
                        // Peel a trailing MA / KAI label out of the pending
                        // literal so it captures as the type group.
                        FlushLiteralBeforeMana(pattern, literal);
                        pattern.Append(@"(?<mana>\d{1,3})");
                        break;
                    case 'n':
                        // Newline: the scanner drops CR / LF, so %n contributes
                        // no scanned character — flush the literal and emit
                        // nothing for the token itself.
                        FlushLiteral(pattern, literal);
                        break;
                    default:
                        FlushLiteral(pattern, literal);
                        pattern.Append(FragmentFor(code));
                        break;
                }
                i += 2;
            }
            else
            {
                literal.Append(c);
                i++;
            }
        }

        FlushLiteral(pattern, literal);
        return new Regex(pattern.ToString(), RegexOptions.Compiled);
    }

    private static bool IsWildcard(char code) => code switch
    {
        'h' or 'H' or 'm' or 'M' or 'r' or 'c' or 'x' or 'X' or 'w' or 'n' => true,
        _ => false,
    };

    // Non-%m, non-%n wildcards. %h is the only field this builder captures into
    // PlayerState beyond mana; %H / %M / %c / %x / %X / %w render on the wire but
    // are consumed without capture so the surrounding pattern stays aligned.
    private static string FragmentFor(char code) => code switch
    {
        'h' => @"(?<hp>\d{1,4})",
        'H' => @"\d{1,4}",
        'M' => @"\d{1,4}",
        'r' => @"(?:\s?\((?<statea>Resting|Meditating)\))?",
        'c' => @"\d+",
        'x' => @"\d+",
        'X' => @"\d+",
        'w' => @"\S*",
        _   => string.Empty,
    };

    private static void FlushLiteral(StringBuilder pattern, StringBuilder literal)
    {
        if (literal.Length == 0) return;
        pattern.Append(Regex.Escape(literal.ToString()));
        literal.Clear();
    }

    // The MA / KAI label preceding %m is literal text in a custom statline, but
    // the scanner reads a type group off it to set ManaType. Split the pending
    // literal so the trailing label (and its `=`) becomes (?<type>MA|KAI).
    private static void FlushLiteralBeforeMana(StringBuilder pattern, StringBuilder literal)
    {
        string lit = literal.ToString();
        literal.Clear();

        Match label = ManaLabelRegex.Match(lit);
        if (!label.Success)
        {
            // No MA / KAI label — degenerate template; emit the literal as-is.
            // ManaType stays None at decode, so the mana value isn't read.
            if (lit.Length != 0) pattern.Append(Regex.Escape(lit));
            return;
        }

        string before = lit[..label.Index];
        if (before.Length != 0) pattern.Append(Regex.Escape(before));
        pattern.Append("(?<type>").Append(Regex.Escape(label.Groups["label"].Value)).Append(')');
        pattern.Append(Regex.Escape(label.Groups["sep"].Value));
    }

    // Trailing MA / KAI label with an optional `=` separator (spaces tolerated).
    private static readonly Regex ManaLabelRegex =
        new(@"(?<label>MA|KAI)(?<sep>\s*=\s*)?$", RegexOptions.Compiled);
}
