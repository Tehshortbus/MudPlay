using MudPlay.Terminal;

namespace MudPlay.Services.Api;

// The game's completed lines, numbered, for GET /scrollback?since=. A poller
// diffing the screen tail loses or doubles lines whenever the screen scrolls
// between polls; a line numbered as it completes is handed out exactly once.
// The lines are LineExtractor's: a row the terminal wrapped is joined back into
// the one line the game sent, and chat comes in the order it arrived among the
// rest. A line is final once emitted, which a screen row is not.
//
// UI-thread confined: lines arrive on the emulator's feed path and every read
// comes through LocalApiServer.OnUiAsync.
public sealed class LocalApiLines
{
    // The most lines kept; a poller further behind than this is told how many it
    // missed.
    public const int Capacity = 5000;

    public readonly record struct Entry(long Seq, DateTimeOffset At, string Text, bool IsPrompt, bool IsChat);

    private readonly Queue<Entry> _ring = new();
    private LineExtractor? _lines;

    // The number given to the newest line; 0 before the first.
    public long NewestSeq { get; private set; }

    // Bound once per terminal; the previous extractor is let go.
    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (ReferenceEquals(_lines, lines)) return;
        if (_lines is not null)
        {
            _lines.LineEmitted -= Add;
            _lines.ChatLineEmitted -= Add;
        }
        _lines = lines;
        _lines.LineEmitted += Add;
        _lines.ChatLineEmitted += Add;
    }

    private void Add(LineExtractor.EmittedLine line)
    {
        _ring.Enqueue(new Entry(++NewestSeq, line.Timestamp, line.Text, line.IsPromptLine, line.IsChat));
        if (_ring.Count > Capacity) _ring.Dequeue();
    }

    // Every kept line numbered after since, oldest first, and how many lines after
    // since have already been let go.
    public (IReadOnlyList<Entry> Lines, long Lost) After(long since)
    {
        long oldest = _ring.TryPeek(out Entry first) ? first.Seq : NewestSeq + 1;
        long lost = Math.Max(0, oldest - 1 - since);
        return (_ring.Where(e => e.Seq > since).ToList(), lost);
    }
}
