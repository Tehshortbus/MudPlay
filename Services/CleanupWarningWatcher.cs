using System.Text;
using System.Text.RegularExpressions;

namespace FujinTerm.Services;

/// <summary>
/// Sniffs the post-IAC wire stream for the BBS's "shutting down for
/// nightly cleanup" warning and captures the most recent
/// (observed_at, minutes_remaining) tuple. The connect / disconnect
/// lifecycle in <c>MainWindowViewModel</c> reads <see cref="Latest"/>
/// to decide whether to arm an auto-reconnect after the BBS comes
/// back online.
/// </summary>
/// <remarks>
/// <para>
/// Pattern: case-insensitive substring match on <c>"shutting down in
/// N minute"</c>, with the integer captured. Tolerates ANSI CSI escapes
/// in the wire stream (stripped inline before regex). Only one phrasing
/// today — we'll grow the regex as we observe other realm-specific
/// variants in the wild.
/// </para>
/// <para>
/// Last warning wins: a 5-minute warning followed by a 2-minute one
/// updates <see cref="Latest"/> to the 2-minute observation. The
/// estimated shutdown moment (<see cref="CleanupWarning.EstimatedShutdownAt"/>)
/// is recomputed from the latest sample, not anchored to the first.
/// </para>
/// </remarks>
public sealed partial class CleanupWarningWatcher
{
    private const int BufferCap = 4096;

    private readonly StringBuilder _buffer = new(BufferCap);
    private StripState _state;

    /// <summary>Most-recently observed warning, or <c>null</c> if none in this session.</summary>
    public CleanupWarning? Latest { get; private set; }

    /// <summary>Fires every time a warning line is matched. Payload = the new <see cref="Latest"/>.</summary>
    public event Action<CleanupWarning>? WarningObserved;

    /// <summary>Wipe in-flight buffer + cached warning. Called on new connect.</summary>
    public void Reset()
    {
        _buffer.Clear();
        _state = StripState.Normal;
        Latest = null;
    }

    /// <summary>
    /// Feed post-IAC display bytes. Strips ANSI escapes inline, appends
    /// to a rolling buffer, and fires <see cref="WarningObserved"/> for
    /// every fresh regex match.
    /// </summary>
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

        if (lastEnd > 0) _buffer.Remove(0, lastEnd);
        if (_buffer.Length > BufferCap)
            _buffer.Remove(0, _buffer.Length - BufferCap);
    }

    [GeneratedRegex(@"shutting down in (\d+)\s+minute",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex WarningRegex();

    private enum StripState : byte { Normal, EscSeen, Csi }
}

/// <summary>One observed cleanup-warning sample.</summary>
public readonly record struct CleanupWarning(DateTimeOffset ObservedAt, int MinutesRemaining)
{
    /// <summary>When the BBS is expected to actually go offline.</summary>
    public DateTimeOffset EstimatedShutdownAt => ObservedAt.AddMinutes(MinutesRemaining);
}
