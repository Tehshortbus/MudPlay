using System.Collections.Generic;
using System.Text;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Tests;

// Server text into the client the way it arrives: bytes through a TerminalEmulator,
// rows out of the LineExtractor on it. A component under test attaches to Lines, as
// MainWindowViewModel attaches the live extractor; given a router, every row is also
// dispatched through it, chat rows included, the way MainWindowViewModel wires them.
internal sealed class ServerFeed
{
    // When the row being fed was sent; null stamps it with the clock.
    private DateTimeOffset? _at;

    // clock stamps a row fed without a time of its own; the real clock by default.
    public ServerFeed(MessageRouter? router = null, Func<DateTimeOffset>? clock = null)
    {
        Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.Now);
        Emulator = new TerminalEmulator(80, 24, now: () => _at ?? now());
        Lines = new LineExtractor(Emulator);
        if (router is null) return;
        Lines.LineEmitted += router.Dispatch;
        Lines.ChatLineEmitted += router.Dispatch;
    }

    public TerminalEmulator Emulator { get; }
    public LineExtractor Lines { get; }

    // One whole row of server output, stamped at (when the server sent it).
    public void Line(string text, DateTimeOffset? at = null) => Raw(text + "\r\n", at);

    // The game's statline prompt, the row that ends a command's output.
    public void Prompt(string statline = "[HP=100/MA=50]:", DateTimeOffset? at = null) => Line(statline, at);

    public void Rows(IEnumerable<string> rows, DateTimeOffset? at = null)
    {
        foreach (string row in rows) Line(row, at);
    }

    // One row in coloured runs, each sent as the game sends colour: an SGR escape
    // (bold or not, then the foreground 30-37) ahead of its text.
    public void Coloured(IEnumerable<(string Text, int Colour, bool Bold)> runs, DateTimeOffset? at = null)
    {
        StringBuilder row = new();
        foreach ((string text, int colour, bool bold) in runs)
            row.Append($"\u001b[{(bold ? 1 : 0)};{30 + colour}m").Append(text);
        Raw(row.Append("\u001b[0m\r\n").ToString(), at);
    }

    // Text exactly as the server sent it: escape sequences, a row left unfinished.
    // The game speaks CP437, so each character goes out as its CP437 byte.
    public void Raw(string text, DateTimeOffset? at = null)
    {
        _at = at;
        try { Emulator.Feed(ToCp437(text)); }
        finally { _at = null; }
    }

    private static readonly Dictionary<char, byte> Cp437Bytes = BuildCp437Bytes();

    private static Dictionary<char, byte> BuildCp437Bytes()
    {
        Dictionary<char, byte> bytes = new();
        for (int b = 255; b >= 0; b--) bytes[Cp437.ToUnicode((byte)b)] = (byte)b;
        return bytes;
    }

    // ASCII (the controls an escape sequence or a line end is made of included)
    // stands for itself; anything else is the CP437 byte that shows it.
    private static byte[] ToCp437(string text)
    {
        byte[] bytes = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bytes[i] = c < 0x80 ? (byte)c
                : Cp437Bytes.TryGetValue(c, out byte b) ? b
                : throw new ArgumentException($"'{c}' has no CP437 byte", nameof(text));
        }
        return bytes;
    }
}
