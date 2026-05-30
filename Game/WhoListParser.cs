using System.Text.RegularExpressions;
using FujinTerm.Services;
using FujinTerm.Terminal;

namespace FujinTerm.Game;

/// <summary>
/// Stateful parser that pulls player observations out of the
/// <c>who</c> command's "Current Adventurers" table and feeds them
/// to <see cref="PlayerDatabase.RecordObservation"/>. Subscribes to
/// <see cref="LineExtractor.LineEmitted"/>; the table arrives across
/// multiple lines so a tiny state machine batches them — pure
/// per-line pattern matching can't tell a player row from arbitrary
/// chat without seeing the surrounding header / separator first.
/// </summary>
/// <remarks>
/// <para>
/// Format recap (per the user's screenshot + megamind-mud/megamind-client
/// classifier source as cross-reference):
/// </para>
/// <code>
///                  Current Adventurers
///                  ===================
///         Lawful  Debbie Schwartz       - Magebane  of Mudd Life Crisis
///                 Fujin WuzHere         - Apprentice
///           Good  Ivy Leaf              - High Druid  of what happen
///           Good  Lenneth BoxOfRocksDumb- Heroine  of what happen
/// </code>
/// <list type="bullet">
///   <item>Optional <b>alignment</b> word (one of <see cref="AlignmentWords"/>).
///         Blank = Neutral.</item>
///   <item><b>Given</b> name + optional <b>family</b> name (some realms
///         make family names optional; the column gets padded out with
///         spaces).</item>
///   <item><b>Marker</b> — <c>-</c> for regular players, <c>x</c> for
///         dead / out-of-action per megamind's pattern.</item>
///   <item><b>Title</b> — class-derived display string (e.g. "High Druid",
///         "Magebane"). Mapping back to class + level range is a future
///         feature; for now we record the raw string.</item>
///   <item>Optional <c>of {gang}</c>.</item>
///   <item>Optional trailing role marker — <c>M</c> mudop, <c>S</c>
///         sysop, <c>V</c> visiting from another realm.</item>
/// </list>
/// </remarks>
public sealed partial class WhoListParser : IDisposable
{
    private readonly LineExtractor _lines;
    private readonly PlayerDatabase _db;
    private readonly LogService? _log;
    private State _state = State.Idle;
    private int _rowsThisBlock;

    /// <summary>
    /// Alignment words that can appear in the left column of the
    /// <c>who</c> table. Order goes from most-Good to most-Evil per
    /// MajorMUD convention; <c>Neutral</c> isn't listed because the
    /// server prints a blank column for Neutral characters (the parser
    /// fills in <c>"Neutral"</c> when no alignment word is matched on
    /// a row that's otherwise well-formed).
    /// </summary>
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

    /// <summary>
    /// Test hook — drive a sequence of plain text lines through the
    /// parser without spinning up a real <see cref="LineExtractor"/>.
    /// Each line is fed as a non-prompt EmittedLine with <see cref="DateTime.UtcNow"/>.
    /// </summary>
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
        // it. Use it as the definitive block terminator: any in-flight
        // who-parse stops here, even if the table was followed by
        // mid-block padding the row parser couldn't classify.
        if (isPromptLine)
        {
            if (_state != State.Idle)
            {
                _log?.Info("WhoListParser",
                    $"End of block ({_rowsThisBlock} rows) — prompt arrived.");
                _state = State.Idle;
            }
            return;
        }

        switch (_state)
        {
            case State.Idle:
                if (HeaderPattern().IsMatch(text))
                {
                    _state = State.WaitForSeparator;
                    _log?.Info("WhoListParser", $"Saw 'Current Adventurers' header: '{Quote(text)}'");
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
                    _log?.Info("WhoListParser", $"Saw separator, reading rows: '{Quote(text)}'");
                }
                else if (!HeaderPattern().IsMatch(text))
                {
                    // Header was a false positive (chat message containing
                    // "Current Adventurers", maybe a remote command). Bail.
                    _log?.Warn("WhoListParser",
                        $"Expected separator after header but got: '{Quote(text)}' — bailing back to Idle");
                    _state = State.Idle;
                }
                break;

            case State.Reading:
                // Blank lines between the separator and the first row, or
                // mid-table padding, do NOT end the block — the server in
                // the wild emits one right after the ============ line.
                // The block only ends when we get a non-blank line that
                // doesn't parse as a player row (the prompt, the footer,
                // chat, etc.).
                if (string.IsNullOrWhiteSpace(text)) break;
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
                    _log?.Info("WhoListParser",
                        $"Parsed row: {display} [{row.Alignment}] - {row.Title}" +
                        (row.Gang is null ? "" : $" of {row.Gang}"));
                }
                else
                {
                    // First non-blank, non-row line ends the block —
                    // typically the trailing prompt or a "Total:" footer.
                    _log?.Info("WhoListParser",
                        $"End of block ({_rowsThisBlock} rows). Stopper line: '{Quote(text)}'");
                    _state = State.Idle;
                    // The terminator line might itself be the next who's
                    // header (back-to-back `who` calls). Re-feed through
                    // the fresh Idle state so we don't drop it.
                    HandleLine(text, isPromptLine, nowUtc);
                }
                break;
        }
    }

    /// <summary>Defensive quote helper — caps long lines, shows whitespace via length so the user can spot leading-space mismatches.</summary>
    private static string Quote(string s)
    {
        const int max = 120;
        string body = s.Length > max ? s[..max] + "…" : s;
        return $"[{s.Length}] {body}";
    }

    /// <summary>Number of rows recorded by the most recent <c>who</c> block. Useful for tests / debug.</summary>
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

    /// <summary>"Current Adventurers" header — case-sensitive per server output.</summary>
    [GeneratedRegex(@"^\s*Current Adventurers\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderPattern();

    /// <summary>Row of equal signs that separates the header from the data rows.</summary>
    [GeneratedRegex(@"^\s*={5,}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorPattern();

    /// <summary>
    /// One player row. Optional alignment word, given + optional family,
    /// marker, title (greedy-but-trimmed), optional <c>of {gang}</c>,
    /// optional trailing role letter. Adapted from megamind-client's
    /// who-fantasy qualifier but without the realm-specific title
    /// whitelist (we want to work on every realm).
    /// </summary>
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
