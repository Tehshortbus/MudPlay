using System.Collections.Generic;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Spells;

// Parses the spells / pow command output into the obtained set of
// SpellbookState. A small state machine batches the table rows — the list
// arrives across many lines and a single row is only distinguishable from chat
// once the header has been seen.
//
// The block opens on any of the header lines (case-insensitive, anchored at line
// start):
//   - "You have the following spells:"
//   - "You have the following powers:"
//   - "Level Mana Short Spell Name" (mana classes)
//   - "Level Kai  Short Spell Name" (Kai / monk classes)
//
// Each data row is "Level Mana Short Spell Name…": collapse runs of spaces,
// split on space, require the first token numeric > 0 and the second token "0"
// or numeric > 0, then discard the Level / Mana / Short columns and keep the
// remaining Spell Name — so obtained spells resolve by full Name, not Short. The
// accumulated names are committed as an authoritative snapshot
// (SetObtainedByNames) when the block ends (prompt, blank line after rows, or a
// non-row line).
//
// "You have no spells." / "You have no powers." clears the obtained set outright.
public sealed class SpellListParser : IDisposable
{
    private readonly SpellbookState _book;
    private readonly LogService? _log;
    private LineExtractor? _lines;
    private bool _disposed;

    private State _state = State.Idle;
    private readonly List<string> _namesThisBlock = new();

    public SpellListParser(SpellbookState book, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(book);
        _book = book;
        _log = log;
    }

    // True when text is a line of the `spells` / `pow` output — a header/intro
    // line, the authoritative "you have no spells" line, or a parseable table row.
    // The unrecognized-line capture asks this because the spell list is read here
    // as a stateful block rather than through a router pattern, so
    // MessageRouter.AnyPatternMatches can't vouch for these rows and a `spells`
    // poll staged the whole list for review. Reuses the same classifiers the block
    // parser itself runs, so the recognizer can't drift from what gets consumed.
    public static bool IsSpellListLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string trimmed = text.Trim();
        string lower = trimmed.ToLowerInvariant();
        return IsHeaderLine(NormalizeSpaces(lower))
            || IsEmptyListLine(lower)
            || TryParseRow(trimmed, out _);
    }

    // Bind the per-session LineExtractor. Same shape as
    // StatParser.AttachLineExtractor — the extractor is owned by the main-window
    // VM (one per terminal session) while this parser is app-level. Calling again
    // with a new extractor unhooks the previous.
    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (_lines is not null) _lines.LineEmitted -= OnLineEmitted;
        _lines = lines;
        _lines.LineEmitted += OnLineEmitted;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_lines is not null) _lines.LineEmitted -= OnLineEmitted;
    }

    private void OnLineEmitted(LineExtractor.EmittedLine line) => HandleLine(line.Text, line.IsPromptLine);

    private void HandleLine(string text, bool isPromptLine)
    {
        // The prompt is the universal "server done responding" marker —
        // close any open block on it (same shape as WhoListParser).
        if (isPromptLine)
        {
            if (_state == State.Reading) Commit();
            return;
        }

        string trimmed = text.Trim();
        string lower = trimmed.ToLowerInvariant();
        // Collapse runs of whitespace before matching the header. The column
        // header is padding-aligned, and the padding varies by class and realm —
        // "Level Mana Short Spell Name" vs the Kai classes' "Level Kai  Short …" —
        // so a fixed single-space match misses a realm whose mana header is padded
        // differently, and the unrecognised line then aborts the block (see below).
        string headerProbe = NormalizeSpaces(lower);

        if (IsEmptyListLine(lower))
        {
            // Authoritative "no spells/powers" — clear regardless of state.
            _namesThisBlock.Clear();
            _state = State.Idle;
            _book.ClearObtained();
            _log?.Info("SpellListParser", "spell list empty — obtained set cleared");
            return;
        }

        if (IsHeaderLine(headerProbe))
        {
            // Header (intro or column header) opens / restarts the block. Log the
            // raw header so a future "spellbook didn't update" report shows exactly
            // what the realm sent (and whether the space-normalisation was needed).
            _log?.Info("SpellListParser", $"spell list block opened — header \"{trimmed}\"");
            _state = State.Reading;
            _namesThisBlock.Clear();
            return;
        }

        if (_state != State.Reading) return;

        if (trimmed.Length == 0)
        {
            // Padding blank before the first row stays in Reading; a blank
            // after rows ends the table.
            if (_namesThisBlock.Count > 0) Commit();
            return;
        }

        if (TryParseRow(trimmed, out string name))
        {
            _namesThisBlock.Add(name);
            return;
        }

        // First non-blank, non-row line ends the block (footer / next
        // output). Re-feed it through the now-Idle state so an
        // immediately-following empty-list or header isn't dropped.
        Commit();
        HandleLine(text, isPromptLine);
    }

    private void Commit()
    {
        // A block that opened on a header but yielded no parsed rows is a format
        // miss, NOT an authoritative "no spells" (that path is IsEmptyListLine) —
        // do NOT overwrite the book with an empty set (that would wipe a good
        // spellbook on a header/row-format mismatch). Log it at Warning so a
        // recurring miss is visible in a report.
        if (_namesThisBlock.Count == 0)
        {
            _log?.Log(LogSeverity.Warn, "SpellListParser",
                "spell list block ended with 0 rows parsed — book left unchanged "
                + "(header matched but no rows parsed; likely a row-format mismatch)");
            _state = State.Idle;
            return;
        }

        _book.SetObtainedByNames(_namesThisBlock);
        _log?.Info("SpellListParser", $"spell list complete — {_namesThisBlock.Count} spell(s) obtained");
        _log?.Log(LogSeverity.Debug, "SpellListParser", $"obtained spells: {string.Join(", ", _namesThisBlock)}");
        _namesThisBlock.Clear();
        _state = State.Idle;
    }

    // ----- line classification -----------------------------------------

    private static bool IsHeaderLine(string probe)
        => probe.StartsWith("you have the following spells:", StringComparison.Ordinal)
        || probe.StartsWith("you have the following powers:", StringComparison.Ordinal)
        || probe.StartsWith("level mana short spell name", StringComparison.Ordinal)
        || probe.StartsWith("level kai short spell name", StringComparison.Ordinal);   // probe is space-normalised

    // Collapse every run of whitespace to a single space (and trim). The spell-list
    // column header is padding-aligned and the padding varies by class and realm
    // (the two hard-coded variants above are evidence of that), so normalising lets
    // one canonical form match them all rather than hard-coding each alignment.
    private static string NormalizeSpaces(string s)
        => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsEmptyListLine(string lower)
        => lower.StartsWith("you have no spell", StringComparison.Ordinal)
        || lower.StartsWith("you have no power", StringComparison.Ordinal);

    // Parse one "Level Mana Short Spell Name…" row. Collapses spaces, splits, and
    // requires the first token numeric > 0 and the second "0" or numeric > 0
    // (Level + Mana/Kai columns); the spell Name is everything past the Short
    // column.
    private static bool TryParseRow(string trimmed, out string name)
    {
        name = string.Empty;
        string[] tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 4) return false;

        if (!int.TryParse(tokens[0], out int level) || level <= 0) return false;
        bool manaOk = tokens[1] == "0" || (int.TryParse(tokens[1], out int mana) && mana > 0);
        if (!manaOk) return false;

        // tokens[2] is the Short cast-code; the Name is tokens[3..].
        name = string.Join(' ', tokens, 3, tokens.Length - 3);
        return name.Length > 0;
    }

    private enum State { Idle, Reading }
}
