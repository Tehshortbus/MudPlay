using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Map;

// Parses the `sysop status` room dump into SysRoomStatus. Sysop-only, so most
// characters never see one of these blocks at all.
//
// Arming is outbound-gated, the same shape StatParser and TrainerMenuTracker
// use — but here the gate is a security control rather than a noise filter.
// This parser is the input to a programmatic SetLocated: an always-on pattern
// would let anyone who can put text on our screen ("Room 2187  Map: 1" in a
// gossip) relocate the character to a room of their choosing. Without an
// outbound sysop status inside the window, every line is a no-op.
//
// Block shape (conditional lines omitted freely by the game):
//
//   Room 2187  Map: 1
//   This room as Area: Max: 0  Current: 0
//   ...
//   Monsters: None
//   Items: 521(0) 743(0) ... 891(0) 47
//   0(0) 466(0) ...
//   Hidden items: 1845(0) ... 430
//   (0) 264(0) ...
//
// The item lists wrap at the terminal margin, and the break lands wherever it
// lands — mid-id ("47" + "0(0)" is item 470) or between an id and its value
// ("430" + "(0)"). LineExtractor stitches rows the emulator marked soft-wrapped,
// but a server that emits its own CRLF at the margin produces hard rows it can't
// know to join, so the continuation is rejoined here too: a line of nothing but
// digits, parens and spaces is concatenated onto the list in progress with no
// separator, and tokenizing is a regex scan rather than a whitespace split so a
// join that lands mid-token still reads correctly.
public sealed partial class SysRoomStatusParser : IDisposable
{
    private const string LogCategory = "SysStatus";

    private LineExtractor? _lines;
    private readonly WirePromptScanner? _promptScanner;
    private readonly LogService? _log;
    private bool _disposed;

    // Window after an outbound sysop status during which lines are scanned.
    public TimeSpan ExpectingBlockWindow { get; set; } = TimeSpan.FromSeconds(5);

    // Test seam.
    public Func<DateTime> NowProvider { get; set; } = () => DateTime.UtcNow;

    private DateTime? _windowOpenedAt;

    // Block in progress. Room is set by the header line; a second header flushes
    // the block before starting the next one, so `sys st room <n>` batches read
    // as several records rather than one merged mess.
    private RoomKey? _room;
    private string _monstersRaw = string.Empty;
    private readonly StringBuilder _items = new();
    private readonly StringBuilder _hidden = new();

    // Which list a bare continuation line extends, or null when the previous
    // line ended the list.
    private StringBuilder? _activeList;

    // Fires once per completed room block.
    public event Action<SysRoomStatus>? StatusParsed;

    public SysRoomStatusParser(WirePromptScanner? promptScanner = null, LogService? log = null)
    {
        _promptScanner = promptScanner;
        _log = log;
        if (_promptScanner is not null) _promptScanner.PromptObserved += OnPromptObserved;
    }

    // The prompt that terminates the dump parks on its row with no newline, so
    // LineExtractor doesn't emit it until the player next presses enter — which
    // for a probe means the block would sit unflushed until well past its
    // timeout. The wire scanner sees the prompt the instant it lands, so the
    // block also closes from here. Deferred a tick for the same reason
    // LeaderboardCaptureTracker defers: the scanner fires on the read thread,
    // ahead of the lines it precedes being pumped through the extractor.
    private void OnPromptObserved(PromptObservation _)
    {
        if (_windowOpenedAt is null) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed && _windowOpenedAt is not null) CloseWindow("wire prompt");
        });
    }

    // Bind to the current session's line stream, re-binding across session
    // swaps (same shape as the other line-fed parsers).
    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (_lines is not null) _lines.LineEmitted -= OnLineEmitted;
        _lines = lines;
        _lines.LineEmitted += OnLineEmitted;
    }

    // Called by the wire-send path. Arms the scan window when the outbound line
    // is a sysop status in any abbreviation the game accepts: the first word a
    // prefix of "sysop" from 3 chars, the second a prefix of "status" from 2.
    // A trailing `room <n>` argument is ignored here — the reply carries the
    // room identity itself, so the parser never has to correlate.
    public void ObserveOutbound(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > 40) return;
        string line = Encoding.Latin1.GetString(bytes).TrimEnd('\r', '\n', '\0').Trim();
        if (line.Length == 0) return;

        string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return;
        if (!IsPrefixOf(words[0], "sysop", minLength: 3)) return;
        if (!IsPrefixOf(words[1], "status", minLength: 2)) return;

        Reset();
        _windowOpenedAt = NowProvider();
        _log?.Log(LogSeverity.Info, LogCategory,
            $"Observed outbound `{line}` — armed {ExpectingBlockWindow.TotalSeconds:0}s scan window.");
    }

    private static bool IsPrefixOf(string candidate, string full, int minLength)
        => candidate.Length >= minLength
           && candidate.Length <= full.Length
           && full.StartsWith(candidate, StringComparison.OrdinalIgnoreCase);

    // Test hook — drive one line through the parser without a live LineExtractor.
    internal void FeedTestLine(string text, bool isPromptLine = false)
        => HandleLine(text, isPromptLine);

    private void OnLineEmitted(LineExtractor.EmittedLine line)
        => HandleLine(line.Text, line.IsPromptLine);

    private void HandleLine(string text, bool isPromptLine)
    {
        if (_windowOpenedAt is not { } opened) return;

        if (NowProvider() - opened > ExpectingBlockWindow)
        {
            CloseWindow("window expired");
            return;
        }

        // The prompt is the universal end-of-response marker.
        if (isPromptLine)
        {
            CloseWindow("prompt");
            return;
        }

        if (HeaderPattern().Match(text) is { Success: true } header)
        {
            Flush();
            _room = new RoomKey(
                int.Parse(header.Groups["map"].Value, CultureInfo.InvariantCulture),
                int.Parse(header.Groups["room"].Value, CultureInfo.InvariantCulture));
            return;
        }

        // Everything below only makes sense inside a block.
        if (_room is null) return;

        if (StartsWith(text, "Hidden items:", out string hiddenRest))
        {
            _hidden.Append(hiddenRest);
            _activeList = _hidden;
            return;
        }

        if (StartsWith(text, "Items:", out string itemsRest))
        {
            _items.Append(itemsRest);
            _activeList = _items;
            return;
        }

        if (StartsWith(text, "Monsters:", out string monsterRest))
        {
            _monstersRaw = monsterRest.Trim();
            _activeList = null;
            return;
        }

        // A wrapped continuation of the list in progress: digits, parens and
        // spaces only. Concatenated with no separator so a break that landed
        // mid-token rejoins into the original id. Anything else — a chat line
        // that arrived mid-block, the next labelled field — ends the list.
        if (_activeList is not null && ContinuationPattern().IsMatch(text))
        {
            _activeList.Append(text);
            return;
        }

        _activeList = null;
    }

    private static bool StartsWith(string text, string label, out string rest)
    {
        if (text.StartsWith(label, StringComparison.OrdinalIgnoreCase))
        {
            rest = text[label.Length..];
            return true;
        }
        rest = string.Empty;
        return false;
    }

    // Emit the block in progress, if it got as far as a room header.
    private void Flush()
    {
        if (_room is not { } room) { ResetBlock(); return; }

        SysRoomStatus status = new(
            room,
            _monstersRaw,
            Tokenize(_items),
            Tokenize(_hidden));

        ResetBlock();

        _log?.Log(LogSeverity.Info, LogCategory,
            $"Parsed room {status.Room} — {status.Items.Count} item(s), "
            + $"{status.HiddenItems.Count} hidden, monsters '{status.MonstersRaw}'.");
        StatusParsed?.Invoke(status);
    }

    private static IReadOnlyList<SysRoomItem> Tokenize(StringBuilder buffer)
    {
        if (buffer.Length == 0) return Array.Empty<SysRoomItem>();
        var items = new List<SysRoomItem>();
        foreach (Match m in ItemPattern().Matches(buffer.ToString()))
        {
            if (!int.TryParse(m.Groups["id"].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int id) || id <= 0)
                continue;
            if (!int.TryParse(m.Groups["value"].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int value))
                continue;
            items.Add(new SysRoomItem(id, value));
        }
        return items;
    }

    private void CloseWindow(string why)
    {
        Flush();
        _windowOpenedAt = null;
        _log?.Log(LogSeverity.Debug, LogCategory, $"Scan window closed — {why}.");
    }

    private void ResetBlock()
    {
        _room = null;
        _monstersRaw = string.Empty;
        _items.Clear();
        _hidden.Clear();
        _activeList = null;
    }

    private void Reset()
    {
        ResetBlock();
        _windowOpenedAt = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_lines is not null) _lines.LineEmitted -= OnLineEmitted;
        if (_promptScanner is not null) _promptScanner.PromptObserved -= OnPromptObserved;
    }

    [GeneratedRegex(@"^Room\s+(?<room>\d+)\s+Map:\s*(?<map>\d+)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HeaderPattern();

    [GeneratedRegex(@"^[\d()\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ContinuationPattern();

    [GeneratedRegex(@"(?<id>\d+)\((?<value>\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex ItemPattern();
}
