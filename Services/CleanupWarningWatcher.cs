using System.Text;
using System.Text.RegularExpressions;

namespace FujinTerm.Services;

// Sniffs the post-IAC wire stream for the BBS's "shutting down for nightly
// cleanup" warning and captures the most recent (observed_at,
// minutes_remaining) tuple. The connect / disconnect lifecycle in
// MainWindowViewModel reads Latest to decide whether to arm an auto-reconnect
// after the BBS comes back online.
//
// Pattern: case-insensitive substring match on "shutting down in N minute",
// with the integer captured. Tolerates ANSI CSI escapes in the wire stream
// (stripped inline before regex). Only one phrasing today — we'll grow the
// regex as we observe other realm-specific variants in the wild.
//
// Last warning wins: a 5-minute warning followed by a 2-minute one updates
// Latest to the 2-minute observation. The estimated shutdown moment
// (CleanupWarning.EstimatedShutdownAt) is recomputed from the latest sample,
// not anchored to the first.
public sealed partial class CleanupWarningWatcher
{
    private const int BufferCap = 4096;

    private readonly StringBuilder _buffer = new(BufferCap);
    private StripState _state;

    // Most-recently observed warning, or null if none in this session.
    public CleanupWarning? Latest { get; private set; }

    // True after we've observed the in-cleanup "this system is not available at
    // the moment, please try again later" rejection message. Distinct from
    // Latest (the pre-shutdown warning) — that one tells the user "the BBS is
    // GOING down"; this flag tells us "the BBS IS down right now and we just
    // connected mid-cleanup". Reset on next successful Reset (i.e. on next
    // outgoing connect attempt).
    public bool InCleanupMode { get; private set; }

    // Fires every time a pre-shutdown warning line is matched. Payload = the new Latest.
    public event Action<CleanupWarning>? WarningObserved;

    // Fires once per InCleanupMode activation — i.e. when the watcher sees the
    // "system not available" rejection message for the first time since the last
    // Reset.
    public event Action? CleanupModeDetected;

    // Wipe in-flight buffer + cached warning + cleanup-mode flag. Called on new connect.
    public void Reset()
    {
        _buffer.Clear();
        _state = StripState.Normal;
        Latest = null;
        InCleanupMode = false;
    }

    // Feed post-IAC display bytes. Strips ANSI escapes inline, appends to a
    // rolling buffer, and fires WarningObserved for every fresh regex match.
    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;

        foreach (byte b in data)
        {
            switch (_state)
            {
                case StripState.Normal:
                    if (b == 0x1B) _state = StripState.EscSeen;
                    else if ((b >= 0x20 && b < 0x7F) || b == (byte)'\r' || b == (byte)'\n')
                        _buffer.Append((char)b);
                    break;

                case StripState.EscSeen:
                    _state = b == (byte)'[' ? StripState.Csi : StripState.Normal;
                    break;

                case StripState.Csi:
                    if (b >= 0x40 && b <= 0x7E) _state = StripState.Normal;
                    break;
            }
        }

        string text = _buffer.ToString();
        int lastEnd = 0;
        foreach (Match m in WarningRegex().Matches(text))
        {
            if (!int.TryParse(m.Groups[1].Value, out int minutes)) continue;
            CleanupWarning warning = new(DateTimeOffset.Now, minutes);
            Latest = warning;
            lastEnd = m.Index + m.Length;
            WarningObserved?.Invoke(warning);
        }

        // "Sorry, this system is not available at the moment, please
        // try again later." — the BBS dumps this when you connect
        // during the cleanup window. There's no minutes-remaining
        // payload (cleanup has already started), so we just flip the
        // flag + fire CleanupModeDetected once. Caller (MainWindowVM)
        // uses this to switch the redial scheduler from the 5s
        // reactive cycle to a single long-delay attempt using the
        // BBS's CleanupPeriodMinutes setting.
        if (!InCleanupMode && CleanupModeRegex().IsMatch(text))
        {
            InCleanupMode = true;
            CleanupModeDetected?.Invoke();
        }

        if (lastEnd > 0) _buffer.Remove(0, lastEnd);
        if (_buffer.Length > BufferCap)
            _buffer.Remove(0, _buffer.Length - BufferCap);
    }

    // Inter-word gaps are \s+ (not literal spaces) because the BBS
    // hard-wraps the warning line at its column width — the break can
    // land between any two words (observed: "shutting\r\ndown in 19
    // minutes"), so a literal space would miss the wrapped phrasing.
    [GeneratedRegex(@"shutting\s+down\s+in\s+(\d+)\s+minute",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex WarningRegex();

    [GeneratedRegex(@"this system is not available",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CleanupModeRegex();

    private enum StripState : byte { Normal, EscSeen, Csi }
}

// One observed cleanup-warning sample.
public readonly record struct CleanupWarning(DateTimeOffset ObservedAt, int MinutesRemaining)
{
    // When the BBS is expected to actually go offline.
    public DateTimeOffset EstimatedShutdownAt => ObservedAt.AddMinutes(MinutesRemaining);
}
