using System.Text.RegularExpressions;
using MudPlay.Terminal;

namespace MudPlay.Game.Map;

// Watches LineExtractor for the server echoing a typed command back on the
// prompt line ("[HP=..]:e") and reports it to RoomTracker. That echo is the
// causal signal that a move actually executed: the game echoes the command as it
// processes it, immediately before rendering the destination room, whereas a
// spontaneous redisplay (an NPC walking through, a regen-tick refresh, a
// post-combat redraw) carries no echo. RoomTracker uses it to tell a move's real
// landing from a stray re-look in an identically-named grid without
// relying on the fragile round-trip timing heuristic.
//
// Three ways the echoed command reaches us — two because the prompt is
// user-configurable, one because the echo can be knocked off the prompt entirely:
//
//  A) DEFAULT-shaped statline ("[HP=..]:"). LineExtractor recognises this shape
//     and splits a prompt row carrying trailing text ("[HP=..]:e") into two
//     back-to-back EmittedLines — the prompt half (IsPromptLine, "[HP=..]:") then
//     the trailing content ("e") — BOTH stamped with the same row timestamp. The
//     echoed command is the same-timestamp content line after a prompt line.
//
//  B) CUSTOM statline LineExtractor's built-in "[HP=..]:" split doesn't recognise.
//     The whole prompt row (prompt + typed command) arrives as ONE line. We match
//     the user's own configured statline pattern (StatlinePromptRegexBuilder, via
//     the injected provider — the exact matcher the reconciler forces onto the
//     wire) at the row start and take the trailing text as the echoed command. So
//     the gate works for whatever prompt the player set, not just the default.
//
//  C) DETACHED echo — the command echoed on a row of its own because something
//     displaced it from the prompt (a command the client sent in the same breath,
//     a BBS broadcast rendering between the two). Recognised only when the line is
//     exactly the token of the move in flight, so it cannot swallow ordinary text.
//
// RoomTracker.NoteInboundMoveEcho is the final authority — it keeps only an echo
// that names the move in flight — so forwarding a non-move echo here is harmless.
public sealed class InboundMoveEchoScanner : IDisposable
{
    private readonly LineExtractor _lines;
    private readonly RoomTracker _tracker;

    // Supplies the active statline prompt matcher (path B). Null leaves only the
    // default-split path.
    private readonly Func<Regex>? _promptPattern;

    private bool _prevWasPrompt;
    private DateTimeOffset _prevPromptAt;

    public InboundMoveEchoScanner(LineExtractor lines, RoomTracker tracker, Func<Regex>? promptPattern = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(tracker);
        _lines = lines;
        _tracker = tracker;
        _promptPattern = promptPattern;
        _lines.LineEmitted += OnLineEmitted;
    }

    public void Dispose() => _lines.LineEmitted -= OnLineEmitted;

    private void OnLineEmitted(LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine)
        {
            _prevWasPrompt = true;
            _prevPromptAt = line.Timestamp;
            return;
        }

        bool wasPrompt = _prevWasPrompt;
        _prevWasPrompt = false;

        // Path A — the split-off trailing content of a default prompt row: it
        // arrives right after the prompt line and carries that row's timestamp.
        if (wasPrompt && line.Timestamp == _prevPromptAt)
        {
            Forward(line.Text, line.Timestamp);
            return;
        }

        // Path B — a custom prompt LineExtractor didn't split: the prompt + command
        // are one line. Match the configured statline at the start and take what
        // follows as the echoed command.
        if (_promptPattern?.Invoke() is { } rx)
        {
            Match m = rx.Match(line.Text);
            if (m.Success && m.Index == 0 && m.Length < line.Text.Length)
            {
                Forward(line.Text[m.Length..], line.Timestamp);
                return;
            }
        }

        // Path C — the echo arrived DETACHED from the prompt, on a line of its own.
        // Two live causes, both in reports stock-20260914-000112 / -000155: a command
        // the client sent in the same breath (auto-sneak's "sn") takes the prompt slot
        // and pushes the move's type-ahead echo onto its own row, and a BBS broadcast
        // ("►►► [name] logs ON") renders between the prompt and the echo. Either way
        // paths A and B see nothing, the move's real landing then looks un-echoed, and
        // in a same-named grid the arrival gate holds it as a re-look forever.
        //
        // Scoped to the exact token of the move in flight — never a general "short bare
        // line is an echo" rule, which would let a room name or a one-word chat line
        // confirm a move. Flagged as not-statline-proof so it can't latch the tracker's
        // echo-readable gate off a signal that only appears when something goes wrong.
        if (_tracker.MatchesPendingHeadMove(line.Text))
            Forward(line.Text, line.Timestamp, provesStatlineReadable: false);
    }

    private void Forward(string commandText, DateTimeOffset at, bool provesStatlineReadable = true)
    {
        string command = commandText.Trim();
        if (command.Length == 0) return;
        _tracker.NoteInboundMoveEcho(command, at, provesStatlineReadable);
    }
}
