using System.Text;
using System.Text.RegularExpressions;

namespace MudPlay.Services;

// The last commands the client put on the wire, or meant to: when, what, where
// it came from and what became of it. A bug report prints it, because "why did it
// send that" and "why did it send nothing" are otherwise answered only by the
// game's echo, which names no sender and shows nothing for a command that was
// dropped.
//
// Filled at the places every command already passes: EngineSendGate's wrapper
// for what the client sends, MainWindowViewModel.SendUserInput for what the user
// sends (typed, or as a line from an alias, a window or the local API).
//
// Nothing is recorded while Withhold says so: outside the game (a login is typed
// there) and while a suicide password may be going out. Those lines are counted,
// never kept. The password of a `recover corpse <name> <password>` is masked, both
// here and in the line told to TypedLineEntered.
public sealed class SentCommandTrail
{
    // Sized from the bug report's budget: ~70 bytes an entry, so the whole ring
    // prints in about 35 KB and reaches back several minutes of a busy session.
    public const int Capacity = 500;

    // The game's own line limit: a command it takes whole is never longer. A
    // longer line is a paste, and is not kept in part: a line cut short can end
    // in the first half of a password, which the report's masking, looking for
    // the whole value, would then leave standing.
    public const int MaxTextLength = Terminal.LocalInputBuffer.MaxLength;

    public enum Origin
    {
        Typed,          // the user, at the terminal's keyboard
        Engine,         // an engine decided it
        UserRelayed,    // a macro, trigger or event of the user's
        RouteStep,      // a walk or loop step
        Alias,          // a step of an alias's expansion
        Api,            // the local API's send
        Window,         // a button, link or input box in one of the client's windows
    }

    public enum Outcome { Sent, HeldForLater, Dropped }

    public readonly record struct Entry(
        DateTimeOffset At, string Text, Origin Origin, string? Sender, Outcome Outcome, string? Why);

    // Guards the ring (_ring, _head, _count), the run of repeated drops and the
    // withheld count: commands are recorded from whichever thread sends them. The
    // typed line being put together is the UI thread's alone and isn't under it.
    private readonly object _ringLock = new();
    private readonly Entry[] _ring = new Entry[Capacity];
    private int _head;
    private int _count;
    private int _withheld;
    private readonly StringBuilder _typedLine = new();

    // True while a line must not be kept. Null = always kept.
    public Func<bool>? Withhold { get; set; }

    // Lines that went by while Withhold was true.
    public int Withheld { get { lock (_ringLock) { return _withheld; } } }

    public void Record(string text, Origin origin, string? sender, Outcome outcome, string? why = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        // Another player's corpse password is theirs: it is never kept.
        text = Game.Recovery.CorpseRecoveryLines.Redact(text);
        if (Withhold?.Invoke() == true)
        {
            lock (_ringLock) { _withheld++; }
            return;
        }
        if (text.Length > MaxTextLength) text = TooLong;
        // A command dropped again and again under one long hold is one entry with
        // a count, not a page of them.
        if (outcome == Outcome.Dropped)
        {
            lock (_ringLock)
            {
                if (_count > 0)
                {
                    int last = (_head - 1 + Capacity) % Capacity;
                    Entry previous = _ring[last];
                    if (previous.Outcome == Outcome.Dropped && previous.Text == text && previous.Sender == sender
                        && previous.Origin == origin && BaseWhy(previous.Why) == why)
                    {
                        _repeats++;
                        _ring[last] = previous with { Why = $"{why}; dropped {_repeats} times, the last at {DateTimeOffset.Now:HH:mm:ss.fff}" };
                        return;
                    }
                }
                _repeats = 1;
            }
        }
        Store(new Entry(DateTimeOffset.Now, text, origin, sender, outcome, why));
    }

    private int _repeats = 1;

    // What stands in the trail for a line past MaxTextLength.
    public const string TooLong = "(a line longer than the game takes: not kept)";

    private static string? BaseWhy(string? why)
    {
        int cut = why?.IndexOf("; dropped ", StringComparison.Ordinal) ?? -1;
        return cut < 0 ? why : why![..cut];
    }

    // Bytes the user typed. The terminal sends a whole line at a time, or a key
    // at a time while a form owns the keyboard: either way an entry is one line,
    // made when its Enter goes out. Keys that aren't text (arrows, escapes) are
    // not part of it.
    public void ObserveTyped(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            // An escape sequence (an arrow key, a function key) is a key, not
            // text: it runs from ESC to its final letter.
            if (_inEscape)
            {
                if (b is >= 0x40 and <= 0x7E && b != (byte)'[' && b != (byte)'O') _inEscape = false;
                continue;
            }
            switch (b)
            {
                case 0x1B:
                    _inEscape = true;
                    break;
                case 0x0D or 0x0A:
                    if (_typedLine.Length > 0 || _pendingWithheld)
                    {
                        TypedLineSent(_typedLine.ToString());
                        _typedLine.Clear();
                    }
                    break;
                case 0x08 or 0x7F:
                    if (_typedLine.Length > 0) _typedLine.Length--;
                    break;
                case < 0x20:
                    break;
                default:
                    // Keys pressed while nothing is kept (a login, a hot key at
                    // the board's menu) don't wait here to head the first line
                    // typed in the game.
                    if (Withhold?.Invoke() == true) { _typedLine.Clear(); _pendingWithheld = true; break; }
                    if (_typedLine.Length <= MaxTextLength) _typedLine.Append((char)b);
                    break;
            }
        }
    }

    private bool _inEscape;
    private bool _pendingWithheld;

    // A whole line of the user's that didn't come from the terminal's keyboard:
    // an alias's step, a window's button or input box, the local API's send. It
    // is named for where it came from and held to the same rule as a typed line
    // (the game reads it at whatever prompt is up, a password prompt included).
    // Kept apart from the line being typed, which it must not be joined to.
    public void ObserveUserLine(ReadOnlySpan<byte> data, Origin origin)
    {
        string text = Encoding.Latin1.GetString(data);
        foreach (string line in text.Split(LineEnds, StringSplitOptions.RemoveEmptyEntries))
            UserLineSent(line, origin);
    }

    private static readonly char[] LineEnds = ['\r', '\n'];

    // Whether the terminal's cursor row is the game's own prompt as the typed
    // line's Enter goes out; the argument is the line. Anything else on that row
    // (a password prompt, a menu, a question, a prompt we don't know) means the
    // line answers it, and what answers a prompt is never kept: only that
    // something was typed there. Null = always the game prompt.
    public Func<string, bool>? TypedAtGamePrompt { get; set; }

    public const string WithheldAtPrompt = "(typed at a prompt: withheld)";

    private void TypedLineSent(string line)
    {
        if (_pendingWithheld)
        {
            // Part of this line was typed while nothing is kept.
            _pendingWithheld = false;
            lock (_ringLock) { _withheld++; }
            return;
        }
        line = Game.Recovery.CorpseRecoveryLines.Redact(line);
        if (UserLineSent(line, Origin.Typed)) TypedLineEntered?.Invoke(line);
    }

    // Told each line the user typed at the terminal, as its Enter goes out: the
    // line their keys made up, whether the terminal sent it whole or a key at a
    // time. This is the one account of what the user typed that does not come off
    // the screen, so whatever must know that a line was the user's own typing
    // (the chat log, for a request telepathed to another client) is told from
    // here. Told only a line this trail itself keeps in the clear. One it
    // withholds for any reason (typed while nothing is kept, or at a prompt that
    // is not the game's own) may be a password, and is told to nobody. Neither is
    // one past MaxTextLength, which the trail does not keep either and holds only
    // the head of.
    public Action<string>? TypedLineEntered { get; set; }

    // True when the line was kept as it was sent, false when it was withheld or
    // stood in for (TooLong).
    private bool UserLineSent(string line, Origin origin)
    {
        bool nothingIsKept = Withhold?.Invoke() == true;
        if (!nothingIsKept && TypedAtGamePrompt is { } atGamePrompt && !atGamePrompt(line))
        {
            Store(new Entry(DateTimeOffset.Now, WithheldAtPrompt, origin, null, Outcome.Sent, null));
            return false;
        }
        Record(line, origin, sender: null, Outcome.Sent);   // counts the line and keeps none of it while nothing is kept
        return !nothingIsKept && line.Length <= MaxTextLength;
    }

    private void Store(Entry entry)
    {
        lock (_ringLock)
        {
            _ring[_head] = entry;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    // The rule TypedAtGamePrompt applies to a cursor row: the statline prompt,
    // with nothing after it but what the game has echoed of the line being typed.
    // statline is the pattern built from Settings -> Statline (the one the prompt
    // scanner reads HP with); left out, it is the class default that same scanner
    // uses until a custom statline is installed. A row the pattern doesn't read
    // is not the game's prompt, whatever it looks like.
    public static bool RowIsGamePrompt(string? cursorRow, string typedLine, Regex? statline = null)
    {
        statline ??= Game.StatlinePromptRegexBuilder.Default;
        if (!Game.StatlinePromptRegexBuilder.TrySplitPromptRow(statline, cursorRow?.TrimStart(), out string rest)) return false;
        rest = rest.Trim();
        return rest.Length == 0 || typedLine.Trim().StartsWith(rest, StringComparison.OrdinalIgnoreCase);
    }

    // Oldest first.
    public Entry[] Snapshot()
    {
        lock (_ringLock)
        {
            Entry[] result = new Entry[_count];
            int start = (_head - _count + Capacity) % Capacity;
            for (int i = 0; i < _count; i++) result[i] = _ring[(start + i) % Capacity];
            return result;
        }
    }
}
