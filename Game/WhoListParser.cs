using System.Text.RegularExpressions;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game;

// Stateful parser that pulls player observations out of the who command's
// "Current Adventurers" table and feeds them to
// PlayerDatabase.RecordObservation. Subscribes to LineExtractor.LineEmitted; the
// table arrives across multiple lines so a tiny state machine batches them —
// pure per-line pattern matching can't tell a player row from arbitrary chat
// without seeing the surrounding header / separator first.
//
// Format recap:
//
//                  Current Adventurers
//                  ===================
//         Lawful  Debbie Schwartz       - Magebane  of Mudd Life Crisis
//                 MudPlay WuzHere         - Apprentice
//           Good  Ivy Leaf              - High Druid  of what happen
//           Good  Lenneth BoxOfRocksDumb- Heroine  of what happen
//
//   * Optional alignment word (one of AlignmentWords). Blank = Neutral.
//   * Given name + optional family name (some realms make family names optional;
//     the column gets padded out with spaces).
//   * Marker — '-', or 'x' for a player who has gossip switched off (the Stock
//     engine's rule; it says nothing about being dead or dropped). Read past, not
//     recorded.
//   * Title — class-derived display string (e.g. "High Druid", "Magebane"),
//     recorded raw; ClassTitleTable maps it back to a level range where that's
//     wanted.
//   * Optional of {gang}.
//   * Optional trailing role marker — M mudop, S sysop, V visiting from another
//     realm.
//   * Stock ends some rows with the word EDITED, after the gang when there is
//     one. It is dropped so it doesn't read as part of the title or the gang
//     (GAME_MECHANICS "The `who` listing").
//
// Stock's `set style technical` prints the same list as a column table:
//
//   Title           Name                    Reputation Gang/Guild
//   =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
//   Canon           Abel Brightwater        Lawful     Old People Only
//   Druid Novice    Edda Fenwick            Neutral    None
//
// Title 15 wide, name 20, two flag characters (`g` gossip off, `a` auction off),
// reputation 10, gang 19 (`None` for no gang), then a last field of 6. It names
// Neutral outright where the fantasy rows leave the left column blank; both are
// recorded as "Neutral".
public sealed partial class WhoListParser : IDisposable
{
    private readonly LineExtractor _lines;
    private readonly PlayerDatabase _db;
    private readonly LogService? _log;
    private State _state = State.Idle;
    // The block being read is the technical-style table, not the fantasy rows.
    private bool _technical;
    private int _rowsThisBlock;
    // Players.Count at block start — used to split added vs updated counts in the
    // end-of-block log line.
    private int _playersCountAtBlockStart;
    // Consecutive non-parseable rows since the last parsed one. A single odd row
    // (unusual name/title/alignment) is skipped, not treated as the table's end, so
    // it can't truncate the whole list; only a run of them (the table really ended:
    // a footer + chat) closes the block. Reset on every parsed row.
    private int _consecutiveSkips;
    private const int MaxConsecutiveSkips = 3;

    // Alignment words that can appear in the left column of the who table. Order
    // goes from most-Good to most-Evil per MajorMUD convention; Neutral isn't
    // listed because the server prints a blank column for Neutral characters (the
    // parser fills in "Neutral" when no alignment word is matched on a row that's
    // otherwise well-formed).
    public static readonly string[] AlignmentWords =
    {
        "Saint", "Lawful", "Good", "Seedy", "Outlaw", "Criminal", "Villain", "Fiend",
    };

    // A who listing has been read to its end. `who` lists everyone online, so a name
    // it didn't carry isn't a player.
    public event Action? ListRead;

    public WhoListParser(LineExtractor lines, PlayerDatabase db, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(db);
        _lines = lines;
        _db = db;
        _log = log;
        _lines.LineEmitted += OnLineEmitted;
    }

    public void Dispose() => _lines.LineEmitted -= OnLineEmitted;

    private void OnLineEmitted(LineExtractor.EmittedLine line)
    {
        HandleLine(line.Text, line.IsPromptLine, line.Timestamp.UtcDateTime);
    }

    private void HandleLine(string text, bool isPromptLine, DateTime nowUtc)
    {
        // The statline / prompt is the universal "server's done
        // responding" marker — every command the user sends ends with
        // it. Use it as the definitive block terminator. (Doesn't
        // always fire promptly: the prompt row doesn't complete +
        // emit until a \n closes it, which only happens when the user
        // sends the next command. The blank-line-after-rows path below
        // ends the block earlier in the common case.)
        if (isPromptLine)
        {
            if (_state != State.Idle) EndBlock();
            return;
        }

        switch (_state)
        {
            case State.Idle:
                if (HeaderPattern().IsMatch(text))
                {
                    _state = State.WaitForSeparator;
                    _technical = false;
                    _log?.Info("WhoListParser", "who response started");
                }
                else if (TechnicalHeaderPattern().IsMatch(text))
                {
                    _state = State.WaitForSeparator;
                    _technical = true;
                    _log?.Info("WhoListParser", "who response started (technical style)");
                }
                break;

            case State.WaitForSeparator:
                // Servers commonly emit a blank line between the header
                // and the separator — stay parked here rather than bail.
                if (string.IsNullOrWhiteSpace(text)) break;
                if ((_technical ? TechnicalSeparatorPattern() : SeparatorPattern()).IsMatch(text))
                {
                    _state = State.Reading;
                    _rowsThisBlock = 0;
                    _consecutiveSkips = 0;
                    _playersCountAtBlockStart = _db.Players.Count;
                }
                else if (!(_technical ? TechnicalHeaderPattern() : HeaderPattern()).IsMatch(text))
                {
                    // Header was a false positive (chat message containing
                    // "Current Adventurers", maybe a remote command). Bail
                    // silently — no log entry to spam the pane on every
                    // such false alarm.
                    _state = State.Idle;
                }
                break;

            case State.Reading:
                // Blank lines BEFORE the first row are padding (every
                // server emits one right after the ============ separator)
                // — stay in Reading. Blank lines AFTER at least one row
                // mark the end of the table; the prompt is on the very
                // next physical row but doesn't emit until a \n closes
                // it (which is why the prompt-line terminator alone made
                // the end-of-block log wait for the user's next keystroke).
                if (string.IsNullOrWhiteSpace(text))
                {
                    if (_rowsThisBlock > 0) EndBlock();
                    break;
                }
                if (_technical ? TryParseTechnicalRow(text, out PlayerRowMatch row) : TryParseRow(text, out row))
                {
                    string display = string.IsNullOrEmpty(row.Family)
                        ? row.Given
                        : $"{row.Given} {row.Family}";
                    _db.RecordObservation(
                        name:      display,
                        @class:    null,
                        race:      null,
                        alignment: row.Alignment,
                        title:     row.Title,
                        gang:      row.Gang,
                        role:      row.Role,
                        nowUtc:    nowUtc);
                    _rowsThisBlock++;
                    _consecutiveSkips = 0;
                }
                else if (++_consecutiveSkips <= MaxConsecutiveSkips)
                {
                    // A lone unparseable row (an unusual name / title / alignment)
                    // must NOT truncate the table — skip it and keep reading. The
                    // blank-line / prompt terminators still close the block at the
                    // real end; only a RUN of misses (a footer then chat) does here.
                    _log?.Debug("WhoListParser", $"skipping unparseable who row: {text.Trim()}");
                }
                else
                {
                    // Several non-row lines in a row — the table has ended (footer /
                    // next who's header on a back-to-back call). Re-feed through the
                    // now-Idle state so we don't drop it.
                    EndBlock();
                    HandleLine(text, isPromptLine, nowUtc);
                }
                break;
        }
    }

    private void EndBlock()
    {
        int added = _db.Players.Count - _playersCountAtBlockStart;
        if (added < 0) added = 0;
        int updated = _rowsThisBlock - added;
        if (updated < 0) updated = 0;
        _log?.Info("WhoListParser",
            $"who response complete — {_rowsThisBlock} player(s) seen ({added} added, {updated} updated)");
        _state = State.Idle;
        ListRead?.Invoke();
    }

    private static bool TryParseRow(string text, out PlayerRowMatch row)
    {
        Match m = PlayerRowPattern().Match(text);
        if (!m.Success)
        {
            row = default;
            return false;
        }

        // Normalize the matched align to its canonical casing (an ALL-CAPS "FIEND"
        // stores as "Fiend"); a blank column is Neutral.
        string alignment = m.Groups["align"].Success && m.Groups["align"].Value.Length > 0
            ? NormalizeAlignment(m.Groups["align"].Value)
            : "Neutral";
        string given  = m.Groups["given"].Value;
        string family = m.Groups["family"].Success ? m.Groups["family"].Value : string.Empty;
        string title  = m.Groups["title"].Value.Trim();
        string? gang  = m.Groups["gang"].Success ? m.Groups["gang"].Value.Trim() : null;
        string? role  = m.Groups["role"].Success && m.Groups["role"].Value.Length > 0
            ? m.Groups["role"].Value
            : null;
        if (string.IsNullOrEmpty(gang)) gang = null;

        row = new PlayerRowMatch(alignment, given, family, title, gang, role);
        return true;
    }

    // One row of the technical-style table. The columns are fixed, so the title
    // and the gang are cut by position; the reputation has to be an alignment word
    // (or Neutral), which keeps anything else in the block from reading as a row.
    private static bool TryParseTechnicalRow(string text, out PlayerRowMatch row)
    {
        row = default;
        Match m = TechnicalRowPattern().Match(text);
        if (!m.Success) return false;

        string title = m.Groups["title"].Value.Trim();
        string name = m.Groups["name"].Value.Trim();
        string reputation = m.Groups["rep"].Value;
        if (title.Length == 0 || name.Length == 0) return false;
        bool neutral = reputation.Equals("Neutral", StringComparison.OrdinalIgnoreCase);
        if (!neutral && !AlignmentWords.Contains(reputation, StringComparer.OrdinalIgnoreCase)) return false;

        int space = name.IndexOf(' ');
        string given = space < 0 ? name : name[..space];
        string family = space < 0 ? string.Empty : name[(space + 1)..].Trim();

        // The gang column is 19 wide; what follows it is the row's last field.
        string tail = m.Groups["tail"].Value;
        string gangText = (tail.Length > TechnicalGangWidth ? tail[..TechnicalGangWidth] : tail).Trim();
        string? gang = gangText.Length == 0 || gangText.Equals("None", StringComparison.Ordinal) ? null : gangText;

        row = new PlayerRowMatch(neutral ? "Neutral" : NormalizeAlignment(reputation), given, family, title, gang, null);
        return true;
    }

    private const int TechnicalGangWidth = 19;

    // Map a matched align token (any casing) to its canonical AlignmentWords entry,
    // e.g. "FIEND" -> "Fiend". Unrecognized falls back to Neutral (the regex only
    // feeds recognized words, so that branch is defensive).
    private static string NormalizeAlignment(string raw)
    {
        foreach (string w in AlignmentWords)
            if (string.Equals(w, raw, StringComparison.OrdinalIgnoreCase)) return w;
        return "Neutral";
    }

    // "Current Adventurers" header — case-sensitive per server output.
    [GeneratedRegex(@"^\s*Current Adventurers\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderPattern();

    // The technical-style table's header and the rule under it.
    [GeneratedRegex(@"^\s*Title\s+Name\s+Reputation\s+Gang/Guild\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalHeaderPattern();

    [GeneratedRegex(@"^\s*(?:=-){5,}=?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalSeparatorPattern();

    // A technical-style row: title (15), a space, name (20), the two flag
    // characters, a space, the reputation word, then the gang column onward. The
    // name is allowed one more column than the engine's format gives it: the one
    // capture on record has the reputation a column further right than the format
    // puts it.
    [GeneratedRegex(
        @"^(?<title>.{15}) (?<name>.{20,21}?)(?<flags>[ ga]{2}) (?<rep>[A-Za-z]{1,10}) *(?<tail>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalRowPattern();

    // Row of equal signs that separates the header from the data rows.
    [GeneratedRegex(@"^\s*={5,}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorPattern();

    // One player row. Optional alignment word, given + optional family, marker,
    // title (greedy-but-trimmed), optional of {gang}, optional trailing role
    // letter. No realm-specific title whitelist — we want to work on every realm.
    //
    // The gang group is deliberately permissive (\S then any non-newline run,
    // lazy): on Paradigm the alignment column is absent and guild names are
    // player-chosen freeform text — "harsh.beast", "MakingTheLogosGay!",
    // "Fuck Commies", "House of Rage" — so a letters-and-digits-only class
    // dropped those rows. A dropped row used to abort the whole block (the
    // Reading state ends on the first non-row line), so one such guild
    // truncated the table after a handful of names. The lazy quantifier still
    // peels a trailing single-letter role (M/S/V) off the end for realms that
    // print one, and Stock's trailing EDITED word; .Trim() in TryParseRow strips
    // any edge whitespace.
    // The align alternation is case-insensitive: the extreme alignments print in
    // ALL CAPS (e.g. "FIEND", "SAINT") as a highlight while the rest are title case,
    // and a case-sensitive match dropped the all-caps rows. A dropped row used to
    // truncate the whole table (see the Reading state's skip tolerance), so one FIEND
    // player cut the list off after a handful of names (report paradigm-20260827-103227).
    [GeneratedRegex(
        @"^\s*(?:(?<align>(?i:Saint|Lawful|Good|Seedy|Outlaw|Criminal|Villain|Fiend))\s+)?(?<given>[A-Za-z][A-Za-z'\-]*)(?:\s+(?<family>[A-Za-z][A-Za-z0-9'\-]*))?\s*[-x]\s+(?<title>[A-Za-z][A-Za-z '\-]*?)(?:\s+of\s+(?<gang>\S[^\r\n]*?))?(?:\s+(?<role>[MSV]))?(?:\s+EDITED)?\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PlayerRowPattern();

    private readonly record struct PlayerRowMatch(
        string Alignment,
        string Given,
        string Family,
        string Title,
        string? Gang,
        string? Role);

    private enum State { Idle, WaitForSeparator, Reading }
}
