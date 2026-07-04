using System.Text.RegularExpressions;

namespace FujinTerm.Game;

// Pure interpretation of a MajorMUD statline command string — the literal
// argument that follows set statline on the wire. One home for the grammar so
// every consumer reads the same string the same way: the editor preview, the
// on-wire normalisation, the prompt-regex builder, and the logon reconciler
// can't drift in how they parse / emit it.
public static partial class StatlineSyntax
{
    // The class-default sentinel: set statline full tells the server to pick
    // the class-default shape (HP-only / HP+MA / HP+KAI).
    public const string Default = "full";

    // Colour / formatting wildcards (%f0-7, %b0-7, %d, %B, %N, %U, %L, %R).
    // The server renders these as ANSI escapes, so they never appear in the
    // plain prompt text the preview or the wire scanner sees — both strip them.
    [GeneratedRegex(@"%(?:[fb][0-7]|[dBNULR])", RegexOptions.Compiled)]
    private static partial Regex FormattingCodesRegex();

    // True for the class-default statline: null / blank, or the literal full
    // (case-insensitive). These mean "let the server pick the class default"
    // rather than a user-authored template.
    public static bool IsDefault(string? command)
        => string.IsNullOrWhiteSpace(command)
           || command!.Trim().Equals(Default, StringComparison.OrdinalIgnoreCase);

    // Normalise a command to the on-wire form the BBS expects. Default /
    // blank → full; anything already starting with full (e.g. full custom %h)
    // passes verbatim; a bare wildcard string is wrapped as
    // full custom <wildcards>.
    public static string NormalizeForWire(string command)
    {
        if (IsDefault(command)) return Default;
        string trimmed = command.TrimStart();
        return trimmed.StartsWith(Default, StringComparison.OrdinalIgnoreCase)
            ? command
            : $"full custom {command}";
    }

    // The bare wildcard template carried by a non-default command: the text
    // after a leading "full custom " prefix, or the command verbatim when it
    // carries no such prefix. The default case has no fixed template (it's
    // class-dependent), so callers must handle IsDefault before calling this.
    public static string ExtractTemplate(string command)
    {
        string trimmed = command.TrimStart();
        const string prefix = "full custom ";
        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[prefix.Length..]
            : command;
    }

    // Strip colour / formatting wildcards — they never reach plain prompt text.
    public static string StripFormatting(string template)
        => FormattingCodesRegex().Replace(template, string.Empty);
}
