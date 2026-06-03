using System.Collections.Generic;
using System.Text.RegularExpressions;
using FujinTerm.Services;
using FujinTerm.Terminal;

namespace FujinTerm.Game.Map;

/// <summary>
/// Wire-side observation parser feeding <see cref="RoomTracker"/>.
/// Subscribes to <see cref="LineExtractor.LineEmitted"/>, anchors on
/// the <c>Obvious exits:</c> line that closes every room display, and
/// walks back through a rolling line buffer to recover the room name.
/// </summary>
/// <remarks>
/// <para>
/// Heuristic for room-name detection: the typical MajorMUD room
/// display is <c>[name]\n[description]+\nObvious exits: …\n</c>. The
/// parser keeps the most recent N non-prompt text lines since the last
/// emit; on the exits anchor, it picks the first non-blank line in
/// that buffer whose text isn't a movement-transition marker
/// (<c>"You walk north."</c>, <c>"You head east."</c>, etc.) as the
/// room name candidate.
/// </para>
/// <para>
/// Edge case — <c>Obvious exits: none.</c> emits a room observation
/// with an empty exit set. The graph uniqueness index keyed on
/// <c>(Name, ExitMask=0)</c> still works; the tracker either promotes
/// (1-of-1 dead-end), reconciles, or lands in Lost — all fine.
/// </para>
/// </remarks>
public sealed partial class RoomDisplayParser : IDisposable
{
    private const int BufferCapacity = 12;

    private readonly LineExtractor _lines;
    private readonly RoomTracker _tracker;
    private readonly LogService? _log;
    private readonly List<string> _buffer = new(BufferCapacity);

    public RoomDisplayParser(LineExtractor lines, RoomTracker tracker, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(tracker);
        _lines = lines;
        _tracker = tracker;
        _log = log;
        _lines.LineEmitted += OnLineEmitted;
    }

    public void Dispose() => _lines.LineEmitted -= OnLineEmitted;

    /// <summary>
    /// Test seam — feed plain text lines without spinning up a real
    /// <see cref="LineExtractor"/>. Each line is treated as a
    /// non-prompt emit at <see cref="DateTimeOffset.UtcNow"/>.
    /// </summary>
    internal void FeedTestLines(IEnumerable<string> lines, DateTimeOffset? when = null)
    {
        DateTimeOffset stamp = when ?? DateTimeOffset.UtcNow;
        foreach (string text in lines)
            HandleLine(text, stamp);
    }

    private void OnLineEmitted(LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine) return;
        HandleLine(line.Text, line.Timestamp);
    }

    private void HandleLine(string text, DateTimeOffset when)
    {
        Match exits = ObviousExitsPattern().Match(text);
        if (exits.Success)
        {
            HashSet<Direction> dirs = ParseExits(exits.Groups["list"].Value);
            string? name = FindRoomNameInBuffer();
            if (name is not null)
            {
                _tracker.NoteRoomObserved(new RoomObservation(name, dirs), when);
                _log?.Info("RoomDisplay",
                    $"observed: '{name}' exits={{{string.Join(",", dirs)}}}");
            }
            else
            {
                _log?.Warn("RoomDisplay",
                    "saw 'Obvious exits:' but couldn't recover a room name from the buffer.");
            }
            _buffer.Clear();
            return;
        }

        // Append to rolling buffer.
        if (_buffer.Count >= BufferCapacity) _buffer.RemoveAt(0);
        _buffer.Add(text);
    }

    private string? FindRoomNameInBuffer()
    {
        // Walk back from most recent to oldest. Stop at the first
        // block-boundary marker (movement transition). The most recent
        // candidate room-name is the OLDEST non-blank line in the
        // current block — but the simpler safe choice is the first
        // non-blank line we encounter walking the buffer that doesn't
        // look like a movement-transition or a system status line.
        //
        // For the common shape (name + description + exits) walking
        // back from exits, we'd want the line FURTHEST back in the
        // block. So: find the last block-boundary index, then return
        // the first non-blank line AFTER it.

        int boundaryIndex = -1;
        for (int i = _buffer.Count - 1; i >= 0; i--)
        {
            string line = _buffer[i];
            if (string.IsNullOrWhiteSpace(line)) { boundaryIndex = i; break; }
            if (MovementTransitionPattern().IsMatch(line)) { boundaryIndex = i; break; }
        }

        for (int i = boundaryIndex + 1; i < _buffer.Count; i++)
        {
            string line = _buffer[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            return trimmed;
        }
        return null;
    }

    private static HashSet<Direction> ParseExits(string list)
    {
        var result = new HashSet<Direction>();
        if (string.IsNullOrWhiteSpace(list)) return result;
        if (list.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return result;

        foreach (string raw in list.Split(
            new[] { ',', ' ', '.', ';', '/' },
            StringSplitOptions.RemoveEmptyEntries))
        {
            string token = raw.Trim().ToLowerInvariant();
            if (token == "and" || token.Length == 0) continue;
            switch (token)
            {
                case "north": case "n":  result.Add(Direction.N);  break;
                case "south": case "s":  result.Add(Direction.S);  break;
                case "east":  case "e":  result.Add(Direction.E);  break;
                case "west":  case "w":  result.Add(Direction.W);  break;
                case "northeast": case "ne": result.Add(Direction.NE); break;
                case "northwest": case "nw": result.Add(Direction.NW); break;
                case "southeast": case "se": result.Add(Direction.SE); break;
                case "southwest": case "sw": result.Add(Direction.SW); break;
                case "up": case "u":     result.Add(Direction.U);  break;
                case "down": case "d":   result.Add(Direction.D);  break;
            }
        }
        return result;
    }

    /// <summary>Matches <c>"Obvious exits: north, south, east."</c> / variants. Captures the comma list.</summary>
    [GeneratedRegex(
        @"^\s*Obvious\s+exits?\s*:\s*(?<list>.+?)\s*\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ObviousExitsPattern();

    /// <summary>Movement-transition markers — used as block boundaries when scanning back for a room name.</summary>
    [GeneratedRegex(
        @"^\s*(You|He|She|It|They)\s+(walk|head|go|run|swim|fly|crawl|climb|step|move|wander)s?\s",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MovementTransitionPattern();
}
