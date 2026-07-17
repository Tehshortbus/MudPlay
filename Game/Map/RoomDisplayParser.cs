using System.Collections.Generic;
using System.Text.RegularExpressions;
using FujinTerm.Services;
using FujinTerm.Terminal;

namespace FujinTerm.Game.Map;

// Wire-side observation parser feeding RoomTracker. Subscribes to
// LineExtractor.LineEmitted, anchors on the "Obvious exits:" line that closes
// every room display, and walks back through a rolling line buffer to recover
// the room name.
//
// Room-name detection runs in two passes over the most-recent block (delimited
// by a blank line, a movement transition, or a stray prompt-shaped line):
//   1. Colour-anchored: prefer the first line whose non-space cells are all
//      bright cyan — the canonical room-name styling in MajorMUD's display.
//      SGR 96 (index 14) and SGR 1;36 (index 6 + Bold) both qualify.
//   2. Text fallback: when colour data is absent (tests, replays without
//      attributes) or the realm renders names without bright cyan, return the
//      first non-blank line that doesn't look like a command echo (single
//      letters, "look n", "sea sword", bare direction words, etc.).
//
// Edge case — "Obvious exits: none." emits a room observation with an empty
// exit set. The graph uniqueness index keyed on (Name, ExitMask=0) still works;
// the tracker either promotes (1-of-1 dead-end), reconciles, or lands in Lost —
// all fine.
public sealed partial class RoomDisplayParser : IDisposable
{
    private const int BufferCapacity = 12;

    private readonly LineExtractor _lines;
    private readonly RoomTracker _tracker;
    private readonly LogService? _log;
    private readonly List<LineExtractor.EmittedLine> _buffer = new(BufferCapacity);

    // Fires for every fully-parsed room display, BEFORE the tracker decides what
    // to do with it — and crucially, regardless of the tracker's peek-suppression
    // window. A `look <dir>` peek is dropped at the tracker (so it doesn't desync
    // position), but its room display still parses; the teleport-maze solver
    // subscribes here to read the neighbour's obvious-exits fingerprint that the
    // look revealed. Consumers must not mutate tracker state from this handler.
    public event Action<RoomObservation>? RoomParsed;

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

    // Test seam — feed plain text lines without colour information. Lines fall
    // through to the text-heuristic path (echo filter + first non-blank line in
    // the block).
    internal void FeedTestLines(IEnumerable<string> lines, DateTimeOffset? when = null)
    {
        DateTimeOffset stamp = when ?? DateTimeOffset.UtcNow;
        foreach (string text in lines)
            HandleLine(new LineExtractor.EmittedLine(text, [], stamp, false));
    }

    // Test seam — feed pre-built EmittedLines through the real emit entry point
    // so the colour-anchored detection and prompt-boundary handling are both
    // exercised.
    internal void FeedTestEmittedLines(IEnumerable<LineExtractor.EmittedLine> lines)
    {
        foreach (LineExtractor.EmittedLine line in lines)
            OnLineEmitted(line);
    }

    private void OnLineEmitted(LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine)
        {
            // A prompt ends one server-output block and begins the next, so it
            // is a hard boundary for room-name recovery. Clear the buffer rather
            // than silently dropping the line — otherwise output preceding the
            // prompt (e.g. a `look <monster>` examination, whose first line is
            // the monster's name) survives into the next block and gets grabbed
            // as the room name. That misparse ("dark goblin archer" instead of
            // "Darkwood Forest") desynced the tracker to Suspect and froze the
            // walker until a manual locate.
            _buffer.Clear();
            return;
        }
        HandleLine(line);
    }

    private void HandleLine(LineExtractor.EmittedLine line)
    {
        Match exits = ObviousExitsPattern().Match(line.Text);
        if (exits.Success)
        {
            HashSet<Direction> dirs = ParseExits(exits.Groups["list"].Value,
                out HashSet<Direction> openDoors);
            string? name = FindRoomNameInBuffer();
            if (name is not null)
            {
                var observation = new RoomObservation(name, dirs,
                    openDoors.Count > 0 ? openDoors : null);
                RoomParsed?.Invoke(observation);
                _tracker.NoteRoomObserved(observation, line.Timestamp);
                _log?.Info("RoomDisplay",
                    $"observed: '{name}' exits={{{string.Join(",", dirs)}}}"
                    + (openDoors.Count > 0 ? $" openDoors={{{string.Join(",", openDoors)}}}" : ""));
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
            if (PartyChatterBoundaryPattern().IsMatch(text)) { boundaryIndex = i; break; }
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

    private static HashSet<Direction> ParseExits(string list,
        out HashSet<Direction> openDoors)
    {
        var result = new HashSet<Direction>();
        openDoors = new HashSet<Direction>();
        if (string.IsNullOrWhiteSpace(list)) return result;
        if (list.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return result;

        // Split by commas first so multi-word entries (e.g. "open door
        // south", "closed door north") survive — single-word tokens
        // like "east" still split out cleanly from the per-entry trim.
        foreach (string rawEntry in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string entry = rawEntry.Trim().ToLowerInvariant();
            // Strip a trailing "and" (e.g. "north and south" → two entries).
            // The comma-split already handled commas; "and" between the last
            // two items survives as a trailing word inside the entry.
            if (entry.StartsWith("and ", StringComparison.Ordinal)) entry = entry[4..];

            // A door/gate barrier is phrased "<open|closed> <door|gate> <dir>"
            // (e.g. "open door south", "closed gate north"). Strip the state +
            // barrier-noun prefix, recording open state so the walker can skip
            // the open-FSM on an already-open exit. Some realms render the
            // inner-gate portcullis as "gate" rather than "door" — same barrier
            // semantics, so both nouns feed OpenDoorDirections.
            bool isOpenDoor = false;
            Match barrier = BarrierPrefixPattern().Match(entry);
            if (barrier.Success)
            {
                isOpenDoor = barrier.Groups["state"].Value == "open";
                entry = entry[barrier.Length..];
            }

            // Sub-split on spaces in case the entry has trailing
            // "and <dir>" or similar (rare).
            foreach (string raw in entry.Split(
                new[] { ' ', '.', ';', '/' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                string token = raw.Trim();
                if (token == "and" || token.Length == 0) continue;
                Direction? dir = token switch
                {
                    "north" or "n"  => Direction.N,
                    "south" or "s"  => Direction.S,
                    "east"  or "e"  => Direction.E,
                    "west"  or "w"  => Direction.W,
                    "northeast" or "ne" => Direction.NE,
                    "northwest" or "nw" => Direction.NW,
                    "southeast" or "se" => Direction.SE,
                    "southwest" or "sw" => Direction.SW,
                    "up"   or "u"   => Direction.U,
                    "down" or "d"   => Direction.D,
                    _ => null,
                };
                if (dir is not { } d) continue;
                result.Add(d);
                if (isOpenDoor) openDoors.Add(d);
            }
        }
        return result;
    }

    // Matches "Obvious exits: north, south, east." / variants. Captures the
    // comma list.
    [GeneratedRegex(
        @"^\s*Obvious\s+exits?\s*:\s*(?<list>.+?)\s*\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ObviousExitsPattern();

    // Door/gate barrier prefix on an exit token — "<open|closed> <door|gate> ".
    // Entry is already lowercased, so no IgnoreCase needed.
    [GeneratedRegex(@"^(?<state>open|closed)\s+(?:door|gate)\s+", RegexOptions.CultureInvariant)]
    private static partial Regex BarrierPrefixPattern();

    // Movement-transition markers — used as block boundaries when scanning back
    // for a room name.
    [GeneratedRegex(
        @"^\s*(You|He|She|It|They)\s+(walk|head|go|run|swim|fly|crawl|climb|step|move|wander)s?\s",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MovementTransitionPattern();

    // Stray prompt segment — safety net if LineExtractor's split missed one.
    [GeneratedRegex(@"^\s*\[HP=", RegexOptions.CultureInvariant)]
    private static partial Regex PromptLikePattern();

    // Party movement/status chatter that sits between a `par` poll (or the
    // leader's drag) and the room display it precedes. Treated as a block
    // boundary so the room-name search never reaches back into `par` output.
    // A follower's party tracking polls `par` constantly, and its first line
    // ("You are following <leader>.") renders in the same bright cyan the room
    // title uses — without this boundary the colour-anchored pass grabs it as
    // the room name, poisoning every dragged step to Suspect. The leader-drag
    // line ("-- Following your Party leader <dir> --") sits immediately before
    // the room the follower lands in, so it's the natural block boundary too.
    //   - " -- Following your Party leader <dir> --"  (leader-drag line)
    //   - "You are following <name>." / "You are now following <name>."
    //   - "The following people are in your travel party:"  (par roster header)
    [GeneratedRegex(
        @"^\s*(--\s*Following your Party leader\b|You are (now\s+)?following\b|The following people are in your travel party\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartyChatterBoundaryPattern();

    // Player typed something at the prompt and the server echoed it back.
    // Catches "look n", "sea sword", "l e", "exa torch", etc.
    [GeneratedRegex(
        @"^\s*(l|look|exa|examine|sea|search|peer|peek)\s+\S+\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommandEchoPattern();

    // A bare direction word — "north.", "northeast", etc. alone on a line is
    // almost always echo or a refusal tail.
    [GeneratedRegex(
        @"^\s*(north|south|east|west|northeast|northwest|southeast|southwest|up|down)\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FullDirectionPattern();
}
