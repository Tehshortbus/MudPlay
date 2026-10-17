using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace MudPlay.Terminal;

// Watches a TerminalEmulator and emits one EmittedLine per completed screen
// row. Every subsystem that reasons about "what did the server just say"
// subscribes here — MessageRouter, ChatRouter, the Trigger engine, the prompt
// parser, etc.
//
// Completion paths, both raised as TerminalEmulator.LineCompleted:
//   1. A line feed: a server LF, or the wrap at the right margin, whose
//      fragments are held and stitched onto the row that ends the line.
//   2. The cursor leaving a written row by a vertical move. MajorMUD paints
//      some screens with cursor positioning, and those rows never get an LF.
//
// Threading: LineEmitted fires on whatever thread fed the emulator (the
// emulator's Feed path, already on the UI dispatcher in production).
// Subscribers that hand off to background work should Task.Run from their
// handler.
public sealed partial class LineExtractor
{
    // One completed terminal line. Attributes is aligned to Text
    // position-by-position; both run from the row's first column through the
    // last non-blank cell (trailing blanks are dropped to keep matching
    // cheap). IsPromptLine is true when the emitted row is the active prompt
    // (last line of the on-screen buffer waiting for input).
    public readonly record struct EmittedLine(
        string Text,
        CellAttributes[] Attributes,
        DateTimeOffset Timestamp,
        bool IsPromptLine,
        bool IsChat = false);

    // Fired once per completed row of SERVER OUTPUT. Player-typed chat (gossip,
    // auction, broadcast, telepath, gangpath, yell, say — see ChatLineDetector) is
    // withheld: this is the event every game-state parser hangs off, and chat is
    // untrusted text that must never drive one (a broadcast quoting "You are flat
    // on your back!" latched a phantom knockdown — report paradigm-20260921-053754).
    public event Action<EmittedLine>? LineEmitted;

    // Fired once per completed CHAT row (IsChat = true), in the same order relative
    // to LineEmitted that the lines arrived. Only chat-aware consumers subscribe
    // (the MessageRouter feed that drives ChatRouter); everything else stays off it.
    public event Action<EmittedLine>? ChatLineEmitted;

    // Holds the fragments the terminal wrapped at the right margin, awaiting the
    // row that ends the line. That row is stitched onto them so downstream
    // consumers (chat / trigger / combat pattern matchers) see the whole logical
    // line the server sent instead of an 80-column slice whose tail silently
    // vanishes. Kept apart until then: a search of a room with no item cap prints
    // one line over a thousand rows, and joining row by row copied everything
    // gathered so far for each of them.
    private readonly List<EmittedLine> _pendingWrap = new();

    // The clock a line's subscribers are timed on.
    private readonly Func<long> _timestamp;

    public LineExtractor(TerminalEmulator emulator, Func<long>? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(emulator);
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        // Subscribe to the canonical "this row just finished" signal — fires
        // on every \n regardless of whether the row eventually scrolls off
        // the visible screen. The earlier Scrollback.RowAdded subscription
        // missed lines that completed via LF without ever leaving the
        // visible buffer (a partial-screen of chat the user read but never
        // scrolled past).
        emulator.LineCompleted += OnLineCompleted;
    }

    private void OnLineCompleted(ScrollbackBuffer.Row row)
    {
        if (row.SoftWrapped)
        {
            // A long server line the terminal broke at the right margin. Keep the
            // fragment untrimmed (a wrap that lands on a space must preserve it)
            // and hold it for the continuation. The prompt-split / emit below is
            // deferred until the whole line is reassembled.
            _pendingWrap.Add(BuildLine(row.Cells, row.Timestamp, isPromptLine: false, trimTrailingBlanks: false));
            return;
        }

        EmittedLine line = BuildLine(row.Cells, row.Timestamp, isPromptLine: false);
        if (_pendingWrap.Count > 0)
        {
            _pendingWrap.Add(line);
            line = Join(_pendingWrap);
            _pendingWrap.Clear();
        }

        EmitLine(line);
    }

    // Concatenate the held wrap fragments and the row that ended them, keeping
    // the attribute array aligned to the joined text. Timestamp carries the first
    // fragment's (when the message started on the wire).
    private static EmittedLine Join(List<EmittedLine> rows)
    {
        int length = 0;
        foreach (EmittedLine row in rows) length += row.Text.Length;

        StringBuilder text = new(length);
        CellAttributes[] attrs = new CellAttributes[length];
        int at = 0;
        foreach (EmittedLine row in rows)
        {
            text.Append(row.Text);
            row.Attributes.CopyTo(attrs, at);
            at += row.Attributes.Length;
        }
        return rows[0] with { Text = text.ToString(), Attributes = attrs };
    }

    private void EmitLine(EmittedLine line)
    {
        // Common BBS shape: the previous prompt is still sitting on the row
        // when fresh output (chat echo, combat hit, etc.) gets appended
        // inline. Without splitting, the chat regex never matches because
        // the line starts with "[HP=...]:" instead of the speaker name.
        // Slice the row at the prompt boundary and emit both halves so
        // PromptParser sees the prompt and ChatRouter / combat / triggers see
        // the actual content.
        Match m = PromptPrefix().Match(line.Text);
        if (m.Success && m.Length > 0 && m.Length < line.Text.Length)
        {
            EmittedLine prompt = line with
            {
                Text = line.Text[..m.Length],
                Attributes = line.Attributes[..m.Length],
                IsPromptLine = true,
            };
            EmittedLine content = line with
            {
                Text = line.Text[m.Length..],
                Attributes = line.Attributes[m.Length..],
            };
            Publish(prompt);
            Publish(content);
            return;
        }

        if (m.Success && m.Length == line.Text.Length)
        {
            // Row IS a bare prompt — flag it.
            line = line with { IsPromptLine = true };
        }

        Publish(line);
    }

    // The single fan-out point: chat rows go ONLY to ChatLineEmitted, everything
    // else to LineEmitted. Keeping the split here (rather than a guard in each of
    // the ~35 LineEmitted subscribers) means a new parser is chat-proof by default.
    private void Publish(EmittedLine line)
    {
        long started = _timestamp();
        if (!line.IsPromptLine && ChatLineDetector.IsChat(line.Text))
        {
            line = line with { IsChat = true };
            ChatLineEmitted?.Invoke(line);
        }
        else
        {
            LineEmitted?.Invoke(line);
        }

        TimeSpan took = Stopwatch.GetElapsedTime(started, _timestamp());
        if (took >= SlowLineThreshold) SlowLine?.Invoke(line, took);
    }

    // Every subscriber reads a line on the UI thread before the next one is looked
    // at, so the time one line takes is time the whole client stands still. Past
    // this much it is felt as a freeze.
    public static readonly TimeSpan SlowLineThreshold = TimeSpan.FromMilliseconds(250);

    // Fired after a line whose subscribers together took SlowLineThreshold or longer,
    // with how long they took, so a freeze a user reports shows in the program log
    // (report paradigm-20261009-164508 took timing gaps between unrelated log entries
    // to find).
    public event Action<EmittedLine, TimeSpan>? SlowLine;

    // What the program log says of a slow line: how long it took, how long it is and
    // what kind it is. Never any of its text. A line can be another player's telepath,
    // and the first lines of a session, which a cold start makes the likeliest to be
    // slow, are the board's login, where a name or a password may be on the row. The
    // entry's time finds the line in the scrollback.
    public static string SlowLineNotice(EmittedLine line, TimeSpan took)
    {
        string kind = line.IsChat ? "chat line" : line.IsPromptLine ? "prompt line" : "line";
        return $"the client stood still for {took.TotalMilliseconds:F0} ms reading a "
            + $"{line.Text.Length}-character {kind}";
    }

    // Leading status-line prompt — covers [HP=…]: in all the MajorMUD shapes
    // (with or without the MA/KAI suffix, with or without the parenthesised
    // status). Anchored at line start. The trailing "(Resting)"/"(Meditating)"
    // tag (DefaultPatterns.StatusLine's stateb group) sits AFTER "]:", not
    // inside the brackets — without consuming it here too, a move typed while
    // that tag is showing splits as content " (Meditating) e" instead of "e",
    // so InboundMoveEchoScanner forwards an unparseable echo and RoomTracker
    // never sees the move as echoed (report paradigm-20260913-210626: the
    // walker held Pending in an identically-named room until a manual `rm`).
    [GeneratedRegex(@"^\[HP=[^\]]*\]:(?:\s\((?:Resting|Meditating)\))?", RegexOptions.CultureInvariant)]
    private static partial Regex PromptPrefix();

    // Converts a raw cell row into the EmittedLine the
    // event surfaces. Trims trailing blank cells (unless trimTrailingBlanks is
    // false — a soft-wrapped fragment is full to the margin and a trailing space
    // at the wrap point must survive the stitch); the attribute array is sliced
    // to match.
    public static EmittedLine BuildLine(ReadOnlySpan<Cell> cells, DateTimeOffset timestamp, bool isPromptLine, bool trimTrailingBlanks = true)
    {
        int end = cells.Length;
        while (trimTrailingBlanks && end > 0 && cells[end - 1].Char == ' ' && cells[end - 1].Attr.Background.Kind == ColorKind.Default)
        {
            end--;
        }

        char[] chars = new char[end];
        CellAttributes[] attrs = new CellAttributes[end];
        for (int i = 0; i < end; i++)
        {
            chars[i] = cells[i].Char;
            attrs[i] = cells[i].Attr;
        }

        return new EmittedLine(new string(chars), attrs, timestamp, isPromptLine);
    }
}
