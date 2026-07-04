using System.Text.RegularExpressions;
using FujinTerm.Services;
using FujinTerm.Terminal;

namespace FujinTerm.Game;

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
//                 Fujin WuzHere         - Apprentice
//           Good  Ivy Leaf              - High Druid  of what happen
//           Good  Lenneth BoxOfRocksDumb- Heroine  of what happen
//
//   * Optional alignment word (one of AlignmentWords). Blank = Neutral.
//   * Given name + optional family name (some realms make family names optional;
//     the column gets padded out with spaces).
//   * Marker — '-' for regular players, 'x' for dead / out-of-action.
//   * Title — class-derived display string (e.g. "High Druid", "Magebane").
//     Mapping back to class + level range is a future feature; for now we record
//     the raw string.
//   * Optional of {gang}.
//   * Optional trailing role marker — M mudop, S sysop, V visiting from another
//     realm.
public sealed partial class WhoListParser : IDisposable
{
    private readonly LineExtractor _lines;
    private readonly PlayerDatabase _db;
    private readonly LogService? _log;
    private State _state = State.Idle;
    private int _rowsThisBlock;
    // Players.Count at block start — used to split added vs updated counts in the
    // end-of-block log line.
    private int _playersCountAtBlockStart;

    // Alignment words that can appear in the left column of the who table. Order
    // goes from most-Good to most-Evil per MajorMUD convention; Neutral isn't
    // listed because the server prints a blank column for Neutral characters (the
    // parser fills in "Neutral" when no alignment word is matched on a row that's
    // otherwise well-formed).
    public static readonly string[] AlignmentWords =
    {
        "Saint", "Lawful", "Good", "Seedy", "Outlaw", "Criminal", "Villain", "Fiend",
    };

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

    // Test hook — drive a sequence of plain text lines through the parser without
    // spinning up a real LineExtractor. Each line is fed as a non-prompt
    // EmittedLine with DateTime.UtcNow.
    internal void FeedTestLines(IEnumerable<string> lines, DateTime? nowUtc = null)
    {
        DateTime when = nowUtc ?? DateTime.UtcNow;
        foreach (string text in lines)
            HandleLine(text, isPromptLine: false, when);
    }

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
                    _log?.Info("WhoListParser", "who response started");
                }
                break;

            case State.WaitForSeparator:
                // Servers commonly emit a blank line between the header
                // and the separator — stay parked here rather than bail.
                if (string.IsNullOrWhiteSpace(text)) break;
                if (SeparatorPattern().IsMatch(text))
                {
                    _state = State.Reading;
                    _rowsThisBlock = 0;
                    _playersCountAtBlockStart = _db.Players.Count;
                }
                else if (!HeaderPattern().IsMatch(text))
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
                if (TryParseRow(text, out PlayerRowMatch row))
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
                }
                else
                {
                    // First non-blank, non-row line ends the block —
                    // typically a "Total:" footer or the next who's
                    // header (back-to-back calls). Re-feed through the
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
    }

    // Number of rows recorded by the most recent who block. Useful for tests / debug.
    internal int LastBlockRowCount => _rowsThisBlock;

    private static bool TryParseRow(string text, out PlayerRowMatch row)
    {
        Match m = PlayerRowPattern().Match(text);
        if (!m.Success)
        {
            row = default;
            return false;
        }

        string alignment = m.Groups["align"].Success && m.Groups["align"].Value.Length > 0
            ? m.Groups["align"].Value
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

    // "Current Adventurers" header — case-sensitive per server output.
    [GeneratedRegex(@"^\s*Current Adventurers\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderPattern();

    // Row of equal signs that separates the header from the data rows.
    [GeneratedRegex(@"^\s*={5,}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorPattern();

    // One player row. Optional alignment word, given + optional family, marker,
    // title (greedy-but-trimmed), optional of {gang}, optional trailing role
    // letter. No realm-specific title whitelist — we want to work on every realm.
    [GeneratedRegex(
        @"^\s*(?:(?<align>Saint|Lawful|Good|Seedy|Outlaw|Criminal|Villain|Fiend)\s+)?(?<given>[A-Za-z][A-Za-z'\-]*)(?:\s+(?<family>[A-Za-z][A-Za-z0-9'\-]*))?\s*[-x]\s+(?<title>[A-Za-z][A-Za-z '\-]*?)(?:\s+of\s+(?<gang>[A-Za-z][A-Za-z0-9 '\-]*?))?(?:\s+(?<role>[MSV]))?\s*$",
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
