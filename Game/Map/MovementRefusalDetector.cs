using System.Linq;
using System.Text.RegularExpressions;
using FujinTerm.Services;
using FujinTerm.Terminal;

namespace FujinTerm.Game.Map;

// Watches for the canonical MajorMUD "your move didn't happen" lines and
// notifies RoomTracker so a Pending move reverts to Located at the previous
// room. The pattern set is extended as new refusal phrasings turn up in real
// sessions — keep the patterns anchored (^…$) so chat lines that quote these
// phrases don't false-trigger.
public sealed partial class MovementRefusalDetector : IDisposable
{
    private readonly LineExtractor _lines;
    private readonly RoomTracker _tracker;
    private readonly LogService? _log;

    public MovementRefusalDetector(LineExtractor lines, RoomTracker tracker, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(tracker);
        _lines = lines;
        _tracker = tracker;
        _log = log;
        _lines.LineEmitted += OnLineEmitted;
    }

    public void Dispose() => _lines.LineEmitted -= OnLineEmitted;

    internal void FeedTestLine(string text, DateTimeOffset? when = null)
        => HandleLine(text, when ?? DateTimeOffset.UtcNow);

    private void OnLineEmitted(LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine) return;
        HandleLine(line.Text, line.Timestamp);
    }

    private void HandleLine(string text, DateTimeOffset when)
    {
        // A closed-door refusal reverts the move like any other, but ALSO clears
        // the stale "door open" flag for the attempted direction — the door shut
        // since we last saw the room, so the next attempt must re-open it rather
        // than bonk the shut door again (the mid-combat door-closed bonk loop).
        if (DoorIsClosed().IsMatch(text))
        {
            _tracker.NoteDoorClosed(when);
            _log?.Info("MoveRefusal", $"door closed: {text.Trim()}");
            return;
        }

        // Ambient "The door to the <dir> just closed." — the tracker gates on
        // whether that direction is the one we're heading before reacting.
        if (DoorToDirectionJustClosed().Match(text) is { Success: true } namedClose
            && DirectionExtensions.TryFromLongName(namedClose.Groups[1].Value, out Direction closedDir))
        {
            _tracker.NoteNamedDoorClosed(closedDir, when);
            _log?.Info("MoveRefusal", $"door to {closedDir.ToLongName()} just closed: {text.Trim()}");
            return;
        }

        if (!Patterns.Any(p => p.IsMatch(text))) return;

        _tracker.NoteMoveBlocked(when);
        _log?.Info("MoveRefusal", $"blocked: {text.Trim()}");
    }

    // Refusal patterns. Anchored to the whole line so quoted chat doesn't
    // false-trigger. Terminators tolerate both '.' and '!' — Paradigm ends its
    // refusal lines with '!' ("There is no exit in that direction!") where stock
    // uses '.', and a bonked move that never matched left the tracker's Pending
    // move stranded. Add new variants here as we observe them in real sessions.
    private static readonly Regex[] Patterns =
    {
        CantMoveDirection(),
        CantGoThatWay(),
        NoExitThatDirection(),
        TooImpairedToMove(),
        CantSeeWellEnoughToMove(),
        TooEncumberedToMove(),
    };

    [GeneratedRegex(
        @"^\s*You can't move (in )?that direction[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CantMoveDirection();

    [GeneratedRegex(
        @"^\s*You can't go that way[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CantGoThatWay();

    [GeneratedRegex(
        @"^\s*There is no exit (in )?that direction[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoExitThatDirection();

    // Paralyzed / confused / stunned variants — "You are too <state> to move."
    [GeneratedRegex(
        @"^\s*You are too (paralyzed|confused|stunned|dazed) to move[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TooImpairedToMove();

    [GeneratedRegex(
        @"^\s*You can't see well enough to move[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CantSeeWellEnoughToMove();

    [GeneratedRegex(
        @"^\s*You are too encumbered to move[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TooEncumberedToMove();

    // Door blocking — server returns this when the user issues a direction
    // whose exit has a closed door.
    [GeneratedRegex(
        @"^\s*The door is closed[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DoorIsClosed();

    // Ambient door-shut announcement carrying the direction — "The door to the
    // north just closed." Captures the long-form direction word so the tracker
    // can gate on whether we're heading that way.
    [GeneratedRegex(
        @"^\s*The door to the (\w+) just closed[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DoorToDirectionJustClosed();
}
