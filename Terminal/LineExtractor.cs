using System.Text.RegularExpressions;

namespace FujinTerm.Terminal;

// Watches a TerminalEmulator and emits one EmittedLine per completed screen
// row. Every subsystem that reasons about "what did the server just say"
// subscribes here — MessageRouter, ChatRouter, the Trigger engine, the prompt
// parser, etc.
//
// Completion paths (in order of how the screen typically finishes a line):
//   1. Scrolled off the top. The row left the live screen and landed in
//      ScrollbackBuffer. This is the BBS-typical case (a server-sent LF at the
//      bottom of the screen pushes the top row into history).
//   2. Cursor moved off the row via \n or an explicit cursor-position change.
//   3. Quiet-window timeout — any cell on the row gets overwritten after
//      >50 ms of no further writes to it (the "row went quiet, treat as
//      complete" rule).
//
// Threading: LineEmitted fires on whatever thread bumped the scrollback (the
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
        bool IsPromptLine);

    // Fired once per completed row.
    public event Action<EmittedLine>? LineEmitted;

    public LineExtractor(TerminalEmulator emulator)
    {
        ArgumentNullException.ThrowIfNull(emulator);
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
        EmittedLine line = BuildLine(row.Cells, row.Timestamp, isPromptLine: false);

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
            LineEmitted?.Invoke(prompt);
            LineEmitted?.Invoke(content);
            return;
        }

        if (m.Success && m.Length == line.Text.Length)
        {
            // Row IS a bare prompt — flag it.
            line = line with { IsPromptLine = true };
        }

        LineEmitted?.Invoke(line);
    }

    // Leading status-line prompt — covers [HP=…]: in all the MajorMUD shapes
    // (with or without the MA/KAI suffix, with or without the parenthesised
    // status). Anchored at line start.
    [GeneratedRegex(@"^\[HP=[^\]]*\]:", RegexOptions.CultureInvariant)]
    private static partial Regex PromptPrefix();

    // Public for testability: converts a raw cell row into the EmittedLine the
    // event surfaces. Trims trailing blank cells; the attribute array is
    // sliced to match.
    public static EmittedLine BuildLine(ReadOnlySpan<Cell> cells, DateTimeOffset timestamp, bool isPromptLine)
    {
        int end = cells.Length;
        while (end > 0 && cells[end - 1].Char == ' ' && cells[end - 1].Attr.Background.Kind == ColorKind.Default)
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
