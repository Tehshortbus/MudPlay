using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MudPlay.Terminal;

namespace MudPlay.Services;

// Masks racist and other offensive words in the chat lines a bug report copies.
// Reports get posted on public issues; what another player typed on a channel
// goes with them. Only the report is censored: the terminal, the Conversation
// window and the logs on disk show chat as it arrived.
//
// Only chat is touched. A line is chat when ChatLineDetector says so (gossip,
// auction, gangpath, telepath, broadcast, yell, say), or, for a command the
// client sent, when it has a chat channel's form. Room, monster, item and combat
// text is never altered: the game's own vocabulary has words that are slurs
// anywhere else.
//
// Words are compared token by token against salted hashes (BugReportCensorTable),
// never as substrings, so a word that merely contains a listed one is left alone.
public sealed partial class BugReportChatCensor
{
    public const string Mask = "[censored]";

    private const string Salt = "MudPlay.BugReport.Censor.v1|";

    private readonly HashSet<ulong> _hashes;

    public static BugReportChatCensor Default { get; } = new(BugReportCensorTable.Hashes);

    public BugReportChatCensor(IEnumerable<ulong> hashes)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        _hashes = new HashSet<ulong>(hashes);
    }

    // Lower case; digits and symbols that stand in for letters read as the
    // letter; everything but a-z dropped; a run of three or more of one letter
    // cut to two (collapseAll: every run cut to one).
    public static string Normalise(string token, bool collapseAll)
    {
        ArgumentNullException.ThrowIfNull(token);
        StringBuilder sb = new(token.Length);
        char previous = '\0';
        int run = 0;
        int keep = collapseAll ? 1 : 2;
        foreach (char raw in token)
        {
            char c = char.ToLowerInvariant(raw) switch
            {
                '0' => 'o', '1' => 'i', '3' => 'e', '4' => 'a', '5' => 's', '7' => 't',
                '@' => 'a', '$' => 's', '!' => 'i',
                char other => other,
            };
            if (c is < 'a' or > 'z') continue;
            run = c == previous ? run + 1 : 1;
            previous = c;
            if (run <= keep) sb.Append(c);
        }
        return sb.ToString();
    }

    public static ulong Hash(string normalised, bool collapsedAll)
    {
        byte[] input = Encoding.UTF8.GetBytes(Salt + (collapsedAll ? "0:" : "1:") + normalised);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, digest);
        return BinaryPrimitives.ReadUInt64BigEndian(digest);
    }

    public bool IsListed(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        string normal = Normalise(token, collapseAll: false);
        if (normal.Length < 3) return false;
        if (_hashes.Contains(Hash(normal, collapsedAll: false))) return true;
        // A stretched spelling ("wooord") is also compared with every run cut to
        // one letter. Doing that for every token would turn ordinary words into
        // listed ones (a doubled letter is often the whole difference).
        if (!HasStretch(token)) return false;
        string collapsed = Normalise(token, collapseAll: true);
        return collapsed.Length >= 3 && _hashes.Contains(Hash(collapsed, collapsedAll: true));
    }

    private static bool HasStretch(string token)
    {
        int run = 1;
        for (int i = 1; i < token.Length; i++)
        {
            run = char.ToLowerInvariant(token[i]) == char.ToLowerInvariant(token[i - 1]) ? run + 1 : 1;
            if (run >= 3) return true;
        }
        return false;
    }

    // Mask every listed word in text, whatever the text is. Callers decide that
    // it is chat first.
    public string CensorText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return WordChunk().Replace(text, m =>
        {
            string chunk = m.Value;
            if (IsListed(chunk)) return Mask;
            if (chunk.IndexOfAny(Joiners) < 0) return chunk;
            // A word split by a hyphen, slash or underscore is judged part by
            // part as well. The marks between the parts stay as they were
            // written: only a listed word is ever changed.
            StringBuilder? masked = null;
            int partStart = 0;
            for (int i = 0; i <= chunk.Length; i++)
            {
                if (i < chunk.Length && Array.IndexOf(Joiners, chunk[i]) < 0) continue;
                if (IsListed(chunk[partStart..i]))
                    (masked ??= new StringBuilder(chunk.Length).Append(chunk, 0, partStart)).Append(Mask);
                else
                    masked?.Append(chunk, partStart, i - partStart);
                if (i < chunk.Length) masked?.Append(chunk[i]);
                partStart = i + 1;
            }
            return masked?.ToString() ?? chunk;
        });
    }

    private static readonly char[] Joiners = ['-', '/', '_'];

    // A run of letters, digits and the symbols written in place of letters, with
    // joiners inside it. Leading and trailing punctuation stays outside the match.
    [GeneratedRegex(@"[\p{L}\p{N}$](?:[\p{L}\p{N}@$!\-/_]*[\p{L}\p{N}$])?", RegexOptions.CultureInvariant)]
    private static partial Regex WordChunk();

    // A line of game output: masked only when it is a chat line.
    public string CensorIfChat(string line) =>
        ChatLineDetector.IsChat(line) ? CensorText(line) : line;

    // Terminal rows, oldest first. A chat line the terminal wrapped carries on
    // into the rows after it, which no longer start with the channel's words.
    // wrappedOntoNext[i] is the terminal's own record that it left row i by
    // wrapping at the right margin: only then is row i + 1 the same line, and
    // only a chat line's continuation is masked. Nothing is inferred from how
    // full a row is: a chat line can end at the margin, and the row after it is
    // then the game's.
    public void CensorRows(IList<string> rows, IReadOnlyList<bool> wrappedOntoNext)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(wrappedOntoNext);
        bool continuesChat = false;
        for (int i = 0; i < rows.Count; i++)
        {
            bool chat = continuesChat || ChatLineDetector.IsChat(rows[i]);
            if (chat) rows[i] = CensorText(rows[i]);
            continuesChat = chat && i < wrappedOntoNext.Count && wrappedOntoNext[i];
        }
    }

    // A command the client put on the wire: masked when it speaks on a channel.
    public string CensorSentCommand(string command) =>
        IsChatCommand(command) ? CensorText(command) : command;

    // The say, yell and telepath forms, and the word channels (gossip, auction,
    // broadcast, gangpath in every spelling the game takes), as AliasEngine and
    // sneak keeping already know them.
    public static bool IsChatCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        string c = command.TrimStart();
        if (c[0] is '.' or '>' or '"' or '/') return true;
        int space = c.IndexOf(' ');
        if (space <= 0) return false;
        string verb = c[..space];
        return verb.Equals("say", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("yell", StringComparison.OrdinalIgnoreCase)
            // The telepath's word form, `whi` to `whisper`
            // (GAME_MECHANICS "Telepath throttle and per-telepath acknowledgement").
            || (verb.Length >= 3 && "whisper".StartsWith(verb, StringComparison.OrdinalIgnoreCase))
            || AliasEngine.NameConflictReason(verb) is not null;
    }

    // A line of the raw wire as the Wire Inspector prints it: judged on its text
    // with the colour codes taken out, masked between them.
    public string CensorRawWireLine(string rawLine)
    {
        if (string.IsNullOrEmpty(rawLine)) return rawLine;
        string plain = WireControl().Replace(rawLine, string.Empty);
        if (!ChatLineDetector.IsChat(plain)) return rawLine;

        StringBuilder sb = new(rawLine.Length);
        int at = 0;
        foreach (Match control in WireControl().Matches(rawLine))
        {
            sb.Append(CensorText(rawLine[at..control.Index])).Append(control.Value);
            at = control.Index + control.Length;
        }
        sb.Append(CensorText(rawLine[at..]));
        return sb.ToString();
    }

    // An escape sequence or a lone control character, in WireFormatter.RenderRaw's
    // caret notation.
    [GeneratedRegex(@"\^\[\[[0-9;?]*[A-Za-z]|\^[A-Z\[\\\]^_?]", RegexOptions.CultureInvariant)]
    private static partial Regex WireControl();

    // A program-log message: masked from the point where it quotes a chat line or
    // a chat command, if it does. The rest of the log is the client's own words
    // and the game's.
    public string CensorLogMessage(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;
        if (ChatLineDetector.IsChat(message) || IsQuotedChatCommand(message)) return CensorText(message);
        for (int i = 0, checkedStarts = 0; i < message.Length - 1 && checkedStarts < 8; i++)
        {
            bool opens = (message[i] == ':' && message[i + 1] == ' ') || message[i] is '\'' or '`' or '"';
            if (!opens) continue;
            checkedStarts++;
            int start = message[i] == ':' ? i + 2 : i + 1;
            string rest = message[start..];
            if (ChatLineDetector.IsChat(rest) || IsQuotedChatCommand(rest))
                return message[..start] + CensorText(rest);
        }
        return message;
    }

    // "gos hello", "/name hello" and the like, as a log line quotes a command:
    // only the forms no ordinary sentence starts with.
    private static bool IsQuotedChatCommand(string text)
    {
        if (text.Length < 2) return false;
        int space = text.IndexOf(' ');
        // "/name hello" is a name, a space and a message. A file path a log
        // line quotes starts the same way: it has more marks in its first part,
        // or no message after it, and is the client's own text.
        if (text[0] == '/' && char.IsLetter(text[1]))
            return space > 1 && text.AsSpan(1, space - 1).IndexOfAny('/', '\\', '.') < 0;
        return space > 0 && AliasEngine.NameConflictReason(text[..space]) is not null;
    }
}
