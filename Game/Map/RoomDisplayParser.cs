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
/// Room-name detection runs in two passes over the most-recent block
/// (delimited by a blank line, a movement transition, or a stray
/// prompt-shaped line):
/// </para>
/// <list type="number">
///   <item><description>
///   <b>Colour-anchored</b>: prefer the first line whose non-space
///   cells are all bright cyan — the canonical room-name styling in
///   MajorMUD's display. SGR 96 (index 14) and SGR 1;36 (index 6 +
///   Bold) both qualify.
///   </description></item>
///   <item><description>
///   <b>Text fallback</b>: when colour data is absent (tests, replays
///   without attributes) or the realm renders names without bright
///   cyan, return the first non-blank line that doesn't look like a
///   command echo (single letters, "look n", "sea sword", bare
///   direction words, etc.).
///   </description></item>
/// </list>
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
    private readonly List<LineExtractor.EmittedLine> _buffer = new(BufferCapacity);

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
    /// Test seam — feed plain text lines without colour information.
    /// Lines fall through to the text-heuristic path (echo filter +
    /// first non-blank line in the block).
    /// </summary>
    internal void FeedTestLines(IEnumerable<string> lines, DateTimeOffset? when = null)
    {
        DateTimeOffset stamp = when ?? DateTimeOffset.UtcNow;
        foreach (string text in lines)
            HandleLine(new LineExtractor.EmittedLine(text, [], stamp, false));
    }

    /// <summary>
    /// Test seam — feed pre-built <see cref="LineExtractor.EmittedLine"/>s
    /// so the colour-anchored detection path can be exercised.
    /// </summary>
    internal void FeedTestEmittedLines(IEnumerable<LineExtractor.EmittedLine> lines)
    {
        foreach (LineExtractor.EmittedLine line in lines)
            HandleLine(line);
    }

    private void OnLineEmitted(LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine) return;
        HandleLine(line);
    }

    private void HandleLine(LineExtractor.EmittedLine line)
    {
        Match exits = ObviousExitsPattern().Match(line.Text);
        if (exits.Success)
        {
            HashSet<Direction> dirs = ParseExits(exits.Groups["list"].Value);
            string? name = FindRoomNameInBuffer();
            if (name is not null)
            {
                _tracker.NoteRoomObserved(new RoomObservation(name, dirs), line.Timestamp);
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

        if (_buffer.Count >= BufferCapacity) _buffer.RemoveAt(0);
        _buffer.Add(line);
    }

    private string? FindRoomNameInBuffer()
    {
        // Walk back to find the block boundary (blank line, movement
        // transition, or a prompt-shaped line that slipped past the
        // LineExtractor split).
        int boundaryIndex = -1;
        for (int i = _buffer.Count - 1; i >= 0; i--)
        {
            string text = _buffer[i].Text;
            if (string.IsNullOrWhiteSpace(text)) { boundaryIndex = i; break; }
            if (MovementTransitionPattern().IsMatch(text)) { boundaryIndex = i; break; }
            if (PromptLikePattern().IsMatch(text)) { boundaryIndex = i; break; }
        }

        // Colour-anchored pass: prefer a bright-cyan line.
        for (int i = boundaryIndex + 1; i < _buffer.Count; i++)
        {
            LineExtractor.EmittedLine line = _buffer[i];
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            if (IsLineDominantlyBrightCyan(line))
                return line.Text.Trim();
        }

        // Text fallback: first non-blank, non-command-echo line.
        for (int i = boundaryIndex + 1; i < _buffer.Count; i++)
        {
            string text = _buffer[i].Text;
            if (string.IsNullOrWhiteSpace(text)) continue;
            string trimmed = text.Trim();
            if (LooksLikeCommandEcho(trimmed)) continue;
            return trimmed;
        }

        return null;
    }

    private static bool IsLineDominantlyBrightCyan(LineExtractor.EmittedLine line)
    {
        if (line.Attributes is null || line.Attributes.Length == 0) return false;
        int total = 0;
        int cyan = 0;
        int len = System.Math.Min(line.Text.Length, line.Attributes.Length);
        for (int i = 0; i < len; i++)
        {
            if (char.IsWhiteSpace(line.Text[i])) continue;
            total++;
            if (IsBrightCyan(line.Attributes[i])) cyan++;
        }
        return total > 0 && cyan == total;
    }

    private static bool IsBrightCyan(CellAttributes attr)
    {
        if (attr.Foreground.Kind != ColorKind.Indexed) return false;
        int idx = (int)attr.Foreground.Value;
        bool bold = (attr.Flags & CellFlags.Bold) != 0;
        // SGR 96 → palette index 14. SGR 1;36 → index 6 with Bold flag.
        return idx == 14 || (idx == 6 && bold);
    }

    private static bool LooksLikeCommandEcho(string text)
    {
        if (text.Length <= 2) return true;                     // "n", "ne", "u", ...
        if (CommandEchoPattern().IsMatch(text)) return true;
        if (FullDirectionPattern().IsMatch(text)) return true;
        return false;
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

    /// <summary>Stray prompt segment — safety net if LineExtractor's split missed one.</summary>
    [GeneratedRegex(@"^\s*\[HP=", RegexOptions.CultureInvariant)]
    private static partial Regex PromptLikePattern();

    /// <summary>
    /// Player typed something at the prompt and the server echoed it
    /// back. Catches <c>"look n"</c>, <c>"sea sword"</c>, <c>"l e"</c>,
    /// <c>"exa torch"</c>, etc.
    /// </summary>
    [GeneratedRegex(
        @"^\s*(l|look|exa|examine|sea|search|peer|peek)\s+\S+\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommandEchoPattern();

    /// <summary>A bare direction word — <c>"north."</c>, <c>"northeast"</c>, etc.
    /// alone on a line is almost always echo or a refusal tail.</summary>
    [GeneratedRegex(
        @"^\s*(north|south|east|west|northeast|northwest|southeast|southwest|up|down)\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FullDirectionPattern();
}
