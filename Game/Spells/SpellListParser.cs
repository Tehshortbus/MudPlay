using System.Collections.Generic;
using FujinTerm.Services;
using FujinTerm.Terminal;

namespace FujinTerm.Game.Spells;

/// <summary>
/// Parses the <c>spells</c> / <c>pow</c> command output into the obtained
/// set of <see cref="SpellbookState"/>. A small state machine batches the
/// table rows — the list arrives across many lines and a single row is only
/// distinguishable from chat once the header has been seen.
/// </summary>
/// <remarks>
/// <para>
/// Format + matching are a faithful port of MMUD Explorer's
/// <c>PasteSpells</c> (<c>frmMain.frm</c>). The block opens on any of the
/// header lines (case-insensitive, anchored at line start):
/// </para>
/// <list type="bullet">
///   <item><c>You have the following spells:</c></item>
///   <item><c>You have the following powers:</c></item>
///   <item><c>Level Mana Short Spell Name</c> (mana classes)</item>
///   <item><c>Level Kai  Short Spell Name</c> (Kai / monk classes)</item>
/// </list>
/// <para>
/// Each data row is <c>Level Mana Short Spell Name…</c>; MMUD Explorer
/// collapses runs of spaces, splits on space, requires the first token
/// numeric &gt; 0 and the second token <c>"0"</c> or numeric &gt; 0, then
/// <b>discards the Level / Mana / Short columns and keeps the remaining
/// Spell Name</b> — so obtained spells resolve by full Name, not Short. The
/// accumulated names are committed as an authoritative snapshot
/// (<see cref="SpellbookState.SetObtainedByNames"/>) when the block ends
/// (prompt, blank line after rows, or a non-row line).
/// </para>
/// <para>
/// <c>You have no spells.</c> / <c>You have no powers.</c> clears the
/// obtained set outright.
/// </para>
/// </remarks>
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

    /// <summary>
    /// Bind the per-session <see cref="LineExtractor"/>. Same shape as
    /// <see cref="StatParser.AttachLineExtractor"/> — the extractor is owned
    /// by the main-window VM (one per terminal session) while this parser is
    /// app-level. Calling again with a new extractor unhooks the previous.
    /// </summary>
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

    /// <summary>
    /// Test hook — drive a sequence of non-prompt text lines through the
    /// parser without a real <see cref="LineExtractor"/>. Use
    /// <see cref="FeedTestLine"/> to feed a line flagged as the prompt.
    /// </summary>
    internal void FeedTestLines(IEnumerable<string> lines)
    {
        foreach (string text in lines) HandleLine(text, isPromptLine: false);
    }

    /// <summary>Test hook — feed a single line, optionally flagged as the prompt.</summary>
    internal void FeedTestLine(string text, bool isPromptLine = false) => HandleLine(text, isPromptLine);

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

        if (IsEmptyListLine(lower))
        {
            // Authoritative "no spells/powers" — clear regardless of state.
            _namesThisBlock.Clear();
            _state = State.Idle;
            _book.ClearObtained();
            _log?.Info("SpellListParser", "spell list empty — obtained set cleared");
            return;
        }

        if (IsHeaderLine(lower))
        {
            // Header (intro or column header) opens / restarts the block.
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
        _book.SetObtainedByNames(_namesThisBlock);
        _log?.Info("SpellListParser", $"spell list complete — {_namesThisBlock.Count} spell(s) obtained");
        _namesThisBlock.Clear();
        _state = State.Idle;
    }

    /// <summary>Number of rows parsed in the most recent / in-progress block. Test/debug aid.</summary>
    internal int LastBlockRowCount => _namesThisBlock.Count;

    // ----- line classification (verbatim from MMUD Explorer PasteSpells) -

    private static bool IsHeaderLine(string lower)
        => lower.StartsWith("you have the following spells:", StringComparison.Ordinal)
        || lower.StartsWith("you have the following powers:", StringComparison.Ordinal)
        || lower.StartsWith("level mana short spell name", StringComparison.Ordinal)
        || lower.StartsWith("level kai  short spell name", StringComparison.Ordinal);

    private static bool IsEmptyListLine(string lower)
        => lower.StartsWith("you have no spell", StringComparison.Ordinal)
        || lower.StartsWith("you have no power", StringComparison.Ordinal);

    /// <summary>
    /// Parse one <c>Level Mana Short Spell Name…</c> row. Collapses spaces,
    /// splits, and requires the first token numeric &gt; 0 and the second
    /// <c>"0"</c> or numeric &gt; 0 (Level + Mana/Kai columns); the spell
    /// Name is everything past the Short column.
    /// </summary>
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
