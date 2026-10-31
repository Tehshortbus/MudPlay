using System.Text;
using System.Text.RegularExpressions;

namespace MudPlay.Services;

// Takes the account's login name, login password and suicide password, and any
// other player's corpse password seen this session, out of a bug report. Reports
// get attached to public issues, so none of them may appear anywhere in one: not
// in the scrollback, the wire, the program log, the sent-command trail, a
// trigger's text or a settings dump. The scrub runs over
// the whole rendered document, after every section is built, so a section added
// later is covered without knowing about it.
//
// A match is case-insensitive and may be broken by a line break (a row the
// terminal wrapped, with the next row's time stamp in front of it).
//
// A value shorter than MinLength is masked only where it stands as a word of
// its own: a saved "pro" masked inside every "Program" and "prompt" would
// shred the report.
//
// A login name that is also the character's name in the game, which every
// player can see and which the report can't do without, is left alone, and
// the report says so.
public static class BugReportScrub
{
    public const string Mask = "[redacted]";

    // What replaces a whole trigger / alias / macro definition that holds one of
    // the three.
    public const string OmittedDefinition = "[omitted: contains account details]";

    public const int MinLength = 4;

    // One value to mask, and the name the report uses for it in a note.
    public readonly record struct Secret(string Label, string? Value);

    // What may sit between two characters of a value: a row the terminal wrapped
    // (the line break, then the next row's time stamp or its blank padding), and
    // in the raw wire the control runs the Wire Inspector prints in caret
    // notation (a colour change mid-word, an arrival marker's row). In the
    // program log a line break inside a message is printed as ⏎
    // (BugReportLogSections), which breaks a value the same way.
    private const string RowBreak =
        @"(?:(?:\^\[\[[0-9;?]*[A-Za-z]|\^[@A-GI-Z\\\]^_])*(?:[ ]?⏎[ ]?|\r?\n(?:--- \d\d:\d\d:\d\d\.\d{3} ---\n)?(?:\d\d:\d\d:\d\d(?:\.\d{3})?)?[ ]{0,10})?)";

    // A non-ASCII value as CP437 draws its Latin-1 bytes, or null when it is all
    // ASCII (drawn the same) or holds a character Latin-1 can't carry.
    private static string? Cp437Rendering(string value)
    {
        bool any = false;
        foreach (char c in value)
        {
            if (c > 0xFF) return null;
            if (c >= 0x80) any = true;
        }
        if (!any) return null;
        try
        {
            System.Text.Encoding cp437 = System.Text.CodePagesEncodingProvider.Instance.GetEncoding(437)!;
            return cp437.GetString(System.Text.Encoding.Latin1.GetBytes(value));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or NullReferenceException)
        {
            return null;   // no CP437 table on this runtime: only the Latin-1 form is matched
        }
    }

    // Applies the game's own rule for a correction to raw-wire text in caret
    // notation: a ^H erases the character before it. A value typed with a
    // correction is echoed that way, and would otherwise read as other text.
    public static string CollapseBackspaces(string wireText)
    {
        if (!wireText.Contains("^H", StringComparison.Ordinal)) return wireText;
        StringBuilder sb = new(wireText.Length);
        for (int i = 0; i < wireText.Length; i++)
        {
            if (wireText[i] == '^' && i + 1 < wireText.Length && wireText[i + 1] == 'H')
            {
                if (sb.Length > 0 && sb[^1] != '\n') sb.Length--;
                // The game follows an erase with a space and another ^H; take them with it.
                if (i + 4 < wireText.Length && wireText[i + 2] == ' ' && wireText[i + 3] == '^' && wireText[i + 4] == 'H') i += 3;
                i++;
                continue;
            }
            sb.Append(wireText[i]);
        }
        return sb.ToString();
    }

    // The game's echo of a suicide password as it is typed: `*` after one of its
    // three password prompts (GAME_MECHANICS "Character settings — `set` and the
    // `pro` sheet", under `set suicide`). A run of any length is the echo: Paradigm prints one for
    // each character sent, and nothing here counts on Stock printing fewer. The
    // run is masked whole, so a report doesn't give the password's length away.
    // Between the stars the raw wire may carry a colour change or the erase of a
    // correction (caret notation), never a line end.
    private const string EchoBreak = @"\^\[\[[0-9;?]*[A-Za-z]|\^H[ ]\^H|\^[@A-IKLN-Z\\\]^_]";
    private static readonly Regex PasswordEcho = new(
        @"(?<before>(?:Enter your suicide password:|Enter the current password:|Enter New Password:)(?:[ ]|" + EchoBreak + @")*)"
        + @"\*(?:(?:" + EchoBreak + @")*\*)*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Masks every password echo in text; how many were masked comes back.
    private static string MaskPasswordEchoes(string text, out int masked)
    {
        int count = 0;
        // No star, no echo: most text is done here.
        string result = text.Contains('*')
            ? PasswordEcho.Replace(text, m => { count++; return m.Groups["before"].Value + Mask; })
            : text;
        masked = count;
        return result;
    }

    // The same echo where a narrow terminal wrapped it: the prompt, or the run of
    // stars, carried on onto the next row. Row by row neither half is known for
    // what it is, so the rows the terminal recorded as one line
    // (wrappedOntoNext, as TranscriptSnapshot gives it) are read as one. A row
    // that merely follows a prompt and starts with a star (`*Combat Off*`) was
    // not wrapped onto, and is left. The mask is written once, where the run
    // starts; the stars on the rows after it are taken out.
    // columns: the terminal's width, which a wrapped row is as wide as before
    // its trailing blanks were dropped.
    public static void MaskWrappedPasswordEchoes(IList<string> rows, IReadOnlyList<bool> wrappedOntoNext, int columns)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(wrappedOntoNext);
        for (int first = 0; first < rows.Count; first++)
        {
            int last = first;
            while (last < rows.Count - 1 && last < wrappedOntoNext.Count && wrappedOntoNext[last]) last++;
            if (last == first) continue;

            StringBuilder line = new();
            int[] startOf = new int[last - first + 2];
            for (int r = first; r <= last; r++)
            {
                startOf[r - first] = line.Length;
                line.Append(r < last ? rows[r].PadRight(columns) : rows[r]);
            }
            startOf[^1] = line.Length;
            string joined = line.ToString();
            if (joined.Contains('*'))
            {
                MatchCollection echoes = PasswordEcho.Matches(joined);
                // Back to front, so an edit doesn't move what is still to be edited.
                for (int e = echoes.Count - 1; e >= 0; e--)
                {
                    int runStart = echoes[e].Index + echoes[e].Groups["before"].Length;
                    int runEnd = echoes[e].Index + echoes[e].Length;
                    for (int r = last; r >= first; r--)
                    {
                        int from = Math.Max(runStart, startOf[r - first]) - startOf[r - first];
                        int to = Math.Min(runEnd, startOf[r - first + 1]) - startOf[r - first];
                        if (to <= from) continue;
                        string row = r < last ? rows[r].PadRight(columns) : rows[r];
                        // The row the run's first star is on (the next one, when
                        // the prompt ended at the margin).
                        bool runStartsHere = startOf[r - first] + from == runStart;
                        rows[r] = (row[..from] + (runStartsHere ? Mask : string.Empty) + row[to..]).TrimEnd(' ');
                    }
                }
            }
            first = last;
        }
    }

    public sealed class Scrubber
    {
        public static readonly Scrubber None = new(Array.Empty<Regex>(), Array.Empty<string>(), Array.Empty<string>());

        private readonly Regex[] _patterns;
        private readonly string[] _values;

        internal Scrubber(Regex[] patterns, string[] values, IReadOnlyList<string> notes)
        {
            _patterns = patterns;
            _values = values;
            Notes = notes;
        }

        // What was left unmasked and why, for the Session section. Never holds a
        // value.
        public IReadOnlyList<string> Notes { get; }

        // How many saved values are masked, and how many saved values could not
        // be read back (and so could not be masked). Counts only.
        public int MaskedValues => _values.Length;
        public int UnreadableValues { get; init; }

        public string Apply(string text) => Apply(text, out _);

        public string Apply(string text, out int replaced)
        {
            replaced = 0;
            if (string.IsNullOrEmpty(text)) return text;
            // The echo is masked whether or not a password is saved here: one
            // typed by hand is echoed all the same.
            text = MaskPasswordEchoes(text, out int echoes);
            // A corpse password in a line that quotes the command (a telepathed
            // `@do`), whether or not it was typed here.
            text = Game.Recovery.CorpseRecoveryLines.Redact(text);
            replaced = echoes;
            if (_patterns.Length == 0) return text;
            int count = echoes;
            // A raw-wire line that shows a value only once its backspace
            // corrections are applied is rewritten with them applied, so the
            // passes below see it.
            if (text.Contains("^H", StringComparison.Ordinal))
            {
                string[] lines = text.Split('\n');
                bool changed = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].Contains("^H", StringComparison.Ordinal)) continue;
                    string collapsed = CollapseBackspaces(lines[i]);
                    foreach (Regex pattern in _patterns)
                    {
                        if (!pattern.IsMatch(collapsed) || pattern.IsMatch(lines[i])) continue;
                        lines[i] = collapsed;
                        changed = true;
                        break;
                    }
                }
                if (changed) text = string.Join('\n', lines);
            }
            // Every value is looked for in the text as it stands, and what was
            // found is masked in one go. Masking value by value let the second
            // value be found inside the mask written for the first: a password
            // that is part of the word "redacted" showed itself in every mask.
            // Text that is already a mask is passed over for the same reason, so
            // masking a text twice changes nothing the second time.
            List<int>? masks = null;
            for (int at = text.IndexOf(Mask, StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf(Mask, at + Mask.Length, StringComparison.Ordinal))
                (masks ??= new()).Add(at);

            List<(int Start, int End)> found = new();
            foreach (Regex pattern in _patterns)
            {
                foreach (Match m in pattern.Matches(text))
                {
                    if (m.Length == 0 || InsideAMask(masks, m.Index, m.Index + m.Length)) continue;
                    found.Add((m.Index, m.Index + m.Length));
                }
            }
            if (found.Count == 0) return text;
            found.Sort();

            StringBuilder sb = new(text.Length);
            int copied = 0;
            for (int i = 0; i < found.Count;)
            {
                (int start, int end) = found[i++];
                // Two values that overlap (one's end is the other's start) are
                // one mask: neither leaves its tail showing.
                while (i < found.Count && found[i].Start < end) end = Math.Max(end, found[i++].End);
                sb.Append(text, copied, start - copied).Append(Mask);
                // A match that swallowed row breaks keeps every one of them, so
                // the rows after it stay rows and a caller that masks rows as
                // one text gets as many back as it gave.
                int breaks = text.AsSpan(start, end - start).Count('\n');
                if (breaks > 0) sb.Append('\n', breaks);
                copied = end;
                count++;
            }
            sb.Append(text, copied, text.Length - copied);
            replaced = count;
            return sb.ToString();
        }

        // Whether [start, end) lies within one of the masks already in the text
        // (their start offsets, ascending).
        private static bool InsideAMask(List<int>? masks, int start, int end)
        {
            if (masks is null) return false;
            int at = masks.BinarySearch(start);
            if (at < 0) at = ~at - 1;
            return at >= 0 && end <= masks[at] + Mask.Length;
        }

        // Whether the text holds one of the values, for a definition that is left
        // out whole rather than masked in part. Found as Apply finds it, so a
        // short value inside another word doesn't drop the definition.
        public bool Contains(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (Regex pattern in _patterns)
                if (pattern.IsMatch(text)) return true;
            return false;
        }

        // A record that holds one must not print the values with its members.
        public override string ToString() => "(account-detail scrubber)";
    }

    // publicNames are the character's names as the game shows them: a login name
    // equal to one of them is not masked.
    // compiled is for the scrubber a report builds once and runs over a megabyte.
    // One built for every response of the local API masks a few short strings and
    // is dropped: compiling its patterns would cost far more than running them.
    public static Scrubber Build(IEnumerable<Secret> secrets, IEnumerable<string?> publicNames, int unreadable = 0,
        bool compiled = true)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(publicNames);
        RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            | (compiled ? RegexOptions.Compiled : RegexOptions.None);

        HashSet<string> shown = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? name in publicNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            string trimmed = name.Trim();
            shown.Add(trimmed);
            // The stat sheet prints "First Last"; the login is matched against
            // the first name too, which is what the game shows everywhere else.
            int space = trimmed.IndexOf(' ');
            if (space > 0) shown.Add(trimmed[..space]);
        }

        List<Regex> patterns = new();
        List<string> values = new();
        List<string> notes = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (Secret secret in secrets)
        {
            string? value = secret.Value?.Trim();
            if (string.IsNullOrEmpty(value)) continue;
            if (secret.Label == LoginNameLabel && shown.Contains(value))
            {
                notes.Add("the login name is the same as the character's name, which every player sees, and is not masked");
                continue;
            }
            if (!seen.Add(value)) continue;
            values.Add(value);
            patterns.Add(new Regex(Pattern(value), options));
            // The same value as the terminal shows it when its bytes are read as
            // CP437 rather than Latin-1 (what the scrollback holds).
            if (Cp437Rendering(value) is { } shownAs && seen.Add(shownAs))
                patterns.Add(new Regex(Pattern(shownAs), options));
        }
        return new Scrubber(patterns.ToArray(), values.ToArray(), notes) { UnreadableValues = unreadable };
    }

    public const string LoginNameLabel = "login name";
    public const string LoginPasswordLabel = "login password";
    public const string SuicidePasswordLabel = "suicide password";
    public const string CorpsePasswordLabel = "corpse recovery password";

    // A value under MinLength is matched only as a word of its own. Nothing in
    // the report says which values were, since that would tell a reader how
    // long they are.
    private static string Pattern(string value) =>
        value.Length < MinLength ? WordStart + SplitTolerant(value) + WordEnd : SplitTolerant(value);

    // A word starts after anything but a letter or digit, or straight after a
    // colour change or control in the raw wire's caret notation (`^[[1;37mpro`),
    // whose last character is a letter.
    private const string WordStart = @"(?:(?<=\^\[\[[0-9;?]*[A-Za-z]|\^[@A-Z\\\]^_])|(?<![\p{L}\p{N}]))";
    private const string WordEnd = @"(?![\p{L}\p{N}])";

    private static string SplitTolerant(string value)
    {
        StringBuilder sb = new(value.Length * (RowBreak.Length + 2));
        for (int i = 0; i < value.Length; i++)
        {
            if (i > 0) sb.Append(RowBreak);
            // A space that fell at the end of a terminal row is not in the
            // transcript at all (a row's trailing blanks are dropped): the row
            // break stands for it.
            sb.Append(value[i] == ' ' ? @"(?:[ ]|(?<=\n))" : Regex.Escape(value[i].ToString()));
        }
        return sb.ToString();
    }
}
