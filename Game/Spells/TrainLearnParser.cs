using FujinTerm.Services;
using FujinTerm.Terminal;

namespace FujinTerm.Game.Spells;

// Marks powers obtained the moment they're learned at training. When a
// character trains to a new level the server lists any newly learned abilities
// under a header line; each following bare-name line is the full Name of a
// learned power, and the block is closed by the prompt. Each name is added
// incrementally to SpellbookState's obtained set — the same effect as the
// learn-scroll line, NOT an authoritative snapshot, so it never clears existing
// entries.
//
// Captured Kai (mystic) train output:
//   You learn the following Kai abilities:
//   way of the swan
//   [HP=34/KAI=1]:
//
// The named power resolves against the class's learnable list by full Name (a
// subsequent pow poll lists the same "way of the swan" row), so it reuses
// SpellbookState.MarkObtainedByName. Only the Kai-ability wording is recognised
// — the parallel wording mana classes emit at training hasn't been captured, so
// it is intentionally not guessed.
public sealed class TrainLearnParser : IDisposable
{
    private const string Category = "TrainLearnParser";

    private readonly SpellbookState _book;
    private readonly LogService? _log;
    private LineExtractor? _lines;
    private bool _disposed;
    private bool _reading;

    public TrainLearnParser(SpellbookState book, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(book);
        _book = book;
        _log = log;
    }

    // Bind the per-session LineExtractor. Same shape as
    // SpellListParser.AttachLineExtractor — the extractor is owned by the
    // main-window VM (one per terminal session) while this parser is app-level.
    // Calling again with a new extractor unhooks the previous.
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

    // Test hook — feed a single line, optionally flagged as the prompt.
    internal void FeedTestLine(string text, bool isPromptLine = false) => HandleLine(text, isPromptLine);

    private void OnLineEmitted(LineExtractor.EmittedLine line) => HandleLine(line.Text, line.IsPromptLine);

    private void HandleLine(string text, bool isPromptLine)
    {
        // The prompt is the universal "server done responding" marker — it
        // closes the learned-abilities block (same shape as SpellListParser).
        if (isPromptLine)
        {
            _reading = false;
            return;
        }

        string trimmed = text.Trim();

        if (IsHeaderLine(trimmed.ToLowerInvariant()))
        {
            _reading = true;
            return;
        }

        if (!_reading) return;

        // A blank line also closes the block; the prompt is the normal
        // terminator but a padding blank shouldn't get treated as a name.
        if (trimmed.Length == 0)
        {
            _reading = false;
            return;
        }

        // Each line under the header is the full Name of a learned power.
        // MarkObtainedByName is a no-op for names that don't resolve, so a
        // stray non-name line is harmless; the prompt closes the block.
        if (_book.MarkObtainedByName(trimmed) is { } learned)
            _log?.Info(Category, $"learned at training — {learned.Name} obtained");
    }

    private static bool IsHeaderLine(string lower)
        => lower.StartsWith("you learn the following kai abilities:", StringComparison.Ordinal);
}
