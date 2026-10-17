using System.Collections.Generic;
using System.Text.RegularExpressions;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Map;

// Wire-side observation parser feeding RoomTracker. Subscribes to
// LineExtractor.LineEmitted, anchors on the "Obvious exits:" line that closes
// every room display, and walks back through a rolling line buffer to recover
// the room name.
//
// Room-name detection runs in two passes over the most-recent block (delimited
// by a blank line, a movement transition, or a stray prompt-shaped line):
//   1. Colour-anchored: prefer the last line whose non-space cells are all
//      bright cyan — the canonical room-name styling in MajorMUD's display.
//      SGR 96 (index 14) and SGR 1;36 (index 6 + Bold) both qualify.
//      The nearest candidate wins because a bright-cyan line is NOT uniquely a
//      room name: an engine-side palette that remaps spell/ability text from
//      dark blue to bright cyan makes every "<player> invokes …" line collide
//      with the title colour, and such a line arrives immediately before the
//      real title in the arrival burst. Colour can't disambiguate under that
//      palette, so we anchor on POSITION — the title is the last bright-cyan
//      line before "Obvious exits:" (the room display is one contiguous block,
//      so async broadcasts land before it, not between the title and exits).
//   2. Text fallback: when colour data is absent (replays without
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
    // Rolling line buffer the room-name recovery scans back through. It has to
    // hold the LONGEST room display a realm can print, because a name line that
    // has been evicted cannot be recovered by any later pass.
    //
    // 12 was exactly the length of a Bank of Godfrey (1/297) arrival measured on
    // a live 1.11p board — name + 4 description lines + a blank + the 5-line
    // currency table + one "Also here:" line — i.e. zero slack. A floor item, a
    // second occupant, or an "Also here:" list long enough to wrap evicted the
    // name and stranded the tracker. 32 keeps that display comfortably buffered
    // along with a longer description and several occupant / item lines.
    //
    // Raising it is safe: the buffer is cleared on every prompt line and on
    // every "Obvious exits:" line, so it never spans more than one server burst,
    // and the name scan stops at the first block boundary it walks back onto.
    private const int BufferCapacity = 32;

    private readonly LineExtractor _lines;
    private readonly RoomTracker _tracker;
    private readonly LogService? _log;
    private readonly List<LineExtractor.EmittedLine> _buffer = new(BufferCapacity);

    // Crowded rooms can emit far more than BufferCapacity wrapped item rows
    // between their cyan title and the closing exits line. Preserve that
    // colour-anchored title independently of the rolling text buffer.
    private string? _brightCyanRoomName;

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
            _brightCyanRoomName = null;
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
                out HashSet<Direction> openDoors, out HashSet<Direction> closedDoors);
            string? name = _brightCyanRoomName ?? FindRoomNameInBuffer();
            if (name is not null)
            {
                var observation = new RoomObservation(name, dirs,
                    openDoors.Count > 0 ? openDoors : null,
                    closedDoors.Count > 0 ? closedDoors : null);
                RoomParsed?.Invoke(observation);
                _tracker.NoteRoomObserved(observation, line.Timestamp);
                _log?.Info("RoomDisplay",
                    $"observed: '{name}' exits={{{string.Join(",", dirs)}}}"
                    + (openDoors.Count > 0 ? $" openDoors={{{string.Join(",", openDoors)}}}" : "")
                    + (closedDoors.Count > 0 ? $" closedDoors={{{string.Join(",", closedDoors)}}}" : ""));
            }
            else
            {
                _log?.Warn("RoomDisplay",
                    "saw 'Obvious exits:' but couldn't recover a room name from the buffer.");
            }
            _buffer.Clear();
            _brightCyanRoomName = null;
            return;
        }

        // Keep the retained title scoped to the same output block as the
        // rolling buffer. A boundary belongs to the old block. Within the new
        // block retain the MOST RECENT bright-cyan line: player abilities such
        // as "Astro invokes the way of the monkey!" use the same styling and
        // can precede the real room title in an arrival burst. The title is the
        // nearest bright-cyan line before "Obvious exits:", so last wins.
        if (IsBlockBoundary(line.Text))
            _brightCyanRoomName = null;
        else if (IsLineDominantlyBrightCyan(line))
            _brightCyanRoomName = line.Text.Trim();

        if (_buffer.Count >= BufferCapacity) _buffer.RemoveAt(0);
        _buffer.Add(line);
    }

    private string? FindRoomNameInBuffer()
    {
        // Walk back for the two block boundaries this scan needs.
        //
        // SOFT — the nearest boundary of any kind, blank line included. This is
        // the conservative start: a blank line usually does separate one server
        // block from the next, so scanning forward from it is what keeps a stale
        // room name from an earlier block out of the current observation.
        //
        // HARD — the nearest boundary that is NOT a blank line (a prompt, a
        // movement transition, party chatter). These genuinely end a block; a
        // blank line does not always, because a room display can contain one.
        // The Bank of Godfrey (1/297) proves it: the lobby renders its currency
        // table as part of the display, separated from the description by a
        // blank line, so the room name sits BEFORE the soft boundary while
        // "Obvious exits:" lands after it.
        int softBoundary = -1;
        int hardBoundary = -1;
        for (int i = _buffer.Count - 1; i >= 0; i--)
        {
            string text = _buffer[i].Text;
            bool blank = string.IsNullOrWhiteSpace(text);
            bool hard = !blank
                     && (MovementTransitionPattern().IsMatch(text)
                      || PromptLikePattern().IsMatch(text)
                      || PartyChatterBoundaryPattern().IsMatch(text));
            if (!blank && !hard) continue;

            if (softBoundary < 0) softBoundary = i;
            if (hard) { hardBoundary = i; break; }
        }

        // Colour-anchored pass: prefer a bright-cyan line.
        if (FindBrightCyanLine(softBoundary + 1) is { } anchored) return anchored;

        // Same pass again, but reaching back across blank lines to the hard
        // boundary. Only runs when the block after the soft boundary carries no
        // room-name styling at all — i.e. the blank line fell INSIDE a room
        // display. The bank's table is dim cyan (SGR 0;36, no bold), which
        // IsBrightCyan already rejects, so this recovers "Bank of Godfrey"
        // rather than the table's heading. Without it the heading is handed to
        // the tracker as a room name no room can match: an inherent mismatch no
        // recovery path can undo (replay, 1-of-1 candidate and name re-anchor
        // all key on the name), which strands the marker and leaves a walker
        // mid-step waiting for an arrival that never confirms.
        if (hardBoundary < softBoundary
            && FindBrightCyanLine(hardBoundary + 1) is { } acrossBlank)
            return acrossBlank;

        // Text fallback: first non-blank, non-command-echo line. Deliberately
        // still bounded by the SOFT boundary — with no colour to tell a room
        // name from body text, reaching further back would guess.
        for (int i = softBoundary + 1; i < _buffer.Count; i++)
        {
            string text = _buffer[i].Text;
            if (string.IsNullOrWhiteSpace(text)) continue;
            string trimmed = text.Trim();
            if (LooksLikeCommandEcho(trimmed)) continue;
            return trimmed;
        }

        return null;
    }

    private static bool IsBlockBoundary(string text) =>
        string.IsNullOrWhiteSpace(text)
        || MovementTransitionPattern().IsMatch(text)
        || PromptLikePattern().IsMatch(text)
        || PartyChatterBoundaryPattern().IsMatch(text);

    // Last bright-cyan line at or after start, or null when the range holds
    // none. The line nearest "Obvious exits:" is the room title when an
    // asynchronous bright-cyan ability message precedes the display. Blank
    // lines are skipped, never treated as a stop.
    private string? FindBrightCyanLine(int start)
    {
        for (int i = _buffer.Count - 1; i >= System.Math.Max(start, 0); i--)
        {
            LineExtractor.EmittedLine line = _buffer[i];
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            if (IsLineDominantlyBrightCyan(line))
                return line.Text.Trim();
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
        out HashSet<Direction> openDoors, out HashSet<Direction> closedDoors)
    {
        var result = new HashSet<Direction>();
        openDoors = new HashSet<Direction>();
        closedDoors = new HashSet<Direction>();
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
            // barrier-noun prefix, recording the state: an OPEN barrier lets the
            // walker skip the open-FSM; a CLOSED one flags a shut barrier the
            // asylum solver must open before it can peek/move that way. Some realms
            // render the inner-gate portcullis as "gate" rather than "door" — same
            // barrier semantics, so both nouns feed the open/closed sets alike.
            bool isOpenDoor = false;
            bool isClosedDoor = false;
            Match barrier = BarrierPrefixPattern().Match(entry);
            if (barrier.Success)
            {
                isOpenDoor = barrier.Groups["state"].Value == "open";
                isClosedDoor = !isOpenDoor;
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
                else if (isClosedDoor) closedDoors.Add(d);
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
