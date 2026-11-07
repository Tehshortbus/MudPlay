using System.Text.RegularExpressions;

namespace MudPlay.Game.Recovery;

// The wire text of Paradigm's `recover`, shared by our own pile's recovery
// (DeathRecoveryManager) and the hand-back of another player's corpse
// (CorpseHandoffManager). Another player's corpse prints the same lines as our own
// (GAME_MECHANICS "Corpse recovery (`recover corpse`)").
public static partial class CorpseRecoveryLines
{
    // Another player's corpse: `recover <name> <password>`, `recover corpse <name>
    // <password>` or `recover corpse of <name> <password>`, one line holding just the
    // command. A password is one word. `recover corpse <name>` and `recover corpse of
    // <name>` are the owner's own forms, not a name and a password (GAME_MECHANICS
    // "Corpse recovery (`recover corpse`)").
    public static bool TryParseRecoverWithPassword(string? command, out string name, out string password) =>
        TryParseRecoverWithPassword(command, out _, out name, out password);

    // form: what precedes the password, as typed (`recover xsboost `), so a mask or
    // a recall keeps the form the user chose.
    private static bool TryParseRecoverWithPassword(string? command, out string form, out string name, out string password)
    {
        form = name = password = string.Empty;
        if (command is null || command.IndexOf('\n') >= 0) return false;
        Match m = RecoverWithPasswordRegex().Match(command);
        if (!m.Success) return false;
        form = m.Groups["form"].Value;
        name = m.Groups["name"].Value;
        password = m.Groups["password"].Value;
        return true;
    }

    // The text with every corpse password masked, for anything that keeps what was
    // sent or seen (the sent-command trail, the program log, the chat log, a bug
    // report). Taken a line at a time, so a `recover corpse <name>` with no password
    // never reaches into the line after it. A line that is the command is masked
    // whole; one that only quotes it (`/raijin @do recover corpse …`, a log line)
    // has the word after the name masked. A quoted short form is masked only where a
    // command stands (EmbeddedShortRecoverRegex) and ends,
    // so prose with "recover" in it keeps its words.
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf("recover", StringComparison.OrdinalIgnoreCase) < 0) return text;
        if (text.IndexOf('\n') < 0) return RedactLine(text);
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++) lines[i] = RedactLine(lines[i]);
        return string.Join('\n', lines);
    }

    private static string RedactLine(string line)
    {
        if (TryParseRecoverWithPassword(line, out string form, out _, out _)) return form + Services.BugReportScrub.Mask;
        line = EmbeddedRecoverRegex().Replace(line, m => m.Groups["lead"].Value + Services.BugReportScrub.Mask);
        return EmbeddedShortRecoverRegex().Replace(line, m => m.Groups["lead"].Value + Services.BugReportScrub.Mask);
    }

    // The command with its password left off, for the Up-arrow recall: a mask sent
    // back to the game would be taken as the password.
    public static string WithoutPassword(string command) =>
        TryParseRecoverWithPassword(command, out string form, out _, out _)
            ? form.TrimEnd()
            : command;

    // `You begin to pick through the corpse of <Name>...`, the first line of a
    // recovery that went through: the name as the game prints it, or null.
    public static string? Begun(string line) =>
        BegunRegex().Match(line) is { Success: true } m ? m.Groups["name"].Value : null;

    // `You have recovered the corpse of <Name>.`, the last line: the name, or null.
    public static string? Completed(string line) =>
        CompletedRegex().Match(line) is { Success: true } m ? m.Groups["name"].Value : null;

    // `You may not recover the corpse of <Name>.`, a wrong or missing password: the
    // name, or null.
    public static string? Refused(string line) =>
        RefusedRegex().Match(line) is { Success: true } m ? m.Groups["name"].Value : null;

    // `You took <item>.`: the item, or null. One line per copy out of a corpse; a
    // Stock ground `get` prints the same line. "You took N damage." is the engine's
    // damage report, not a pickup.
    public static string? Took(string line) =>
        YouTookRegex().Match(line) is { Success: true } m ? m.Groups["item"].Value : null;

    // A corpse's name is the player's given name; a full name's first word stands
    // for it.
    public static string GivenName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim().Split(' ')[0];

    [GeneratedRegex(@"^[ \t]*(?<form>recover[ \t]+(?:corpse[ \t]+(?:of[ \t]+)?)?(?!(?:corpse|of)[ \t])(?<name>\S+)[ \t]+)(?<password>\S+)[ \t\r]*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RecoverWithPasswordRegex();

    // The password runs to the next space or separator, a quote inside it included:
    // `pa'ss` must not leave `'ss` standing.
    [GeneratedRegex(@"(?<lead>\brecover[ \t]+corpse[ \t]+(?:of[ \t]+)?(?!of[ \t])[^\s'"";|`]+[ \t]+)(?!\[redacted\])[^\s;|`]+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddedRecoverRegex();

    // The short form quoted where a command stands and ending there: after the line's
    // start, a quote, `;`, `|`, `:` (a prompt's echo) or `@do`, and followed by the
    // line's end or one of those.
    [GeneratedRegex(@"(?<lead>(?:^|['""`;|:]|@do[ \t])[ \t]*recover[ \t]+(?!corpse[ \t])[^\s'"";|`]+[ \t]+)(?!\[redacted\])[^\s;|`]+?(?=['""`]?[ \t\r]*(?:$|[;|`]))",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex EmbeddedShortRecoverRegex();

    [GeneratedRegex(@"^You begin to pick through the corpse of (?<name>.+?)\.\.\.$", RegexOptions.CultureInvariant)]
    private static partial Regex BegunRegex();

    [GeneratedRegex(@"^You have recovered the corpse of (?<name>.+?)\.$", RegexOptions.CultureInvariant)]
    private static partial Regex CompletedRegex();

    [GeneratedRegex(@"^You may not recover the corpse of (?<name>.+?)\.$", RegexOptions.CultureInvariant)]
    private static partial Regex RefusedRegex();

    [GeneratedRegex(@"^You took (?!\d+ damage\.$)(?<item>.+?)\.$", RegexOptions.CultureInvariant)]
    private static partial Regex YouTookRegex();
}
