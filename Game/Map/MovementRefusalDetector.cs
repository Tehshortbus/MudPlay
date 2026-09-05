using System.Linq;
using System.Text.RegularExpressions;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Map;

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

    // Recognizes a confusion-fumble wire line as a move-refusal, from game data rather
    // than a hardcoded regex: the fumble wordings ("You fumble in confusion!", plus a
    // spell's own wording like convulsions' "You convulse violently") live on Confused
    // MessageRecords' ConfuseFumbleLine and are queried via ConditionTracker. Left null
    // in tests that don't exercise the confusion path.
    private readonly Func<string, bool>? _isConfuseFumbleLine;

    public MovementRefusalDetector(LineExtractor lines, RoomTracker tracker, LogService? log = null,
        Func<string, bool>? isConfuseFumbleLine = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(tracker);
        _lines = lines;
        _tracker = tracker;
        _log = log;
        _isConfuseFumbleLine = isConfuseFumbleLine;
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

        // A confusion fumble consumes the just-sent command — for a MOVE the step never
        // lands, so revert like any other refusal. The wordings come from game data
        // (Confused records' ConfuseFumbleLine) via the injected predicate, not a
        // hardcoded regex; combat re-sends its own lost swing on ConditionTracker.ActionFailed.
        if (!Patterns.Any(p => p.IsMatch(text)) && _isConfuseFumbleLine?.Invoke(text) != true) return;

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
        FlatOnYourBack(),
        AlignmentBlocksExit(),
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

    // Knocked down — the server refuses the move with this while we're held.
    // SelfHeldResponder normally holds the loop before a move goes out, but a
    // move already in flight when the knockdown lands (or a manual move while
    // down) still bonks this way; recognizing it keeps the tracker from
    // stranding on the unresolved step. Also the knockdown NOTICE itself, which
    // NoteMoveBlocked treats as a no-op when nothing is pending.
    [GeneratedRegex(
        @"^\s*You are flat on your back[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FlatOnYourBack();

    // Door / gate blocking — server returns this when the user issues a direction
    // whose exit is shut. Both the plain and the "in that direction" long form are
    // covered, and "gate" as well as "door" (a fortress gate opened by a `pull
    // winch` prerequisite bonks with "The gate is closed!" — without matching it,
    // the pending move never reverts and the tracker latches in Pending, swallowing
    // even the post-open redisplay: the walker stalls forever, report
    // paradigm-20260827-113513).
    [GeneratedRegex(
        @"^\s*The (?:door|gate) is closed(?: in that direction)?[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DoorIsClosed();

    // Ambient door-shut announcement carrying the direction — "The door to the
    // north just closed." Captures the long-form direction word so the tracker
    // can gate on whether we're heading that way.
    [GeneratedRegex(
        @"^\s*The door to the (\w+) just closed[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DoorToDirectionJustClosed();

    // Alignment-gated exit — Paradigm refuses an exit whose "(Alignment: X to Y)"
    // band excludes the mover ("Your current alignment prevents you from entering
    // this exit."). The router isn't alignment-aware yet, so a route planned through
    // such an exit bonks here; recognizing it reverts the pending move cleanly
    // instead of stranding the tracker (report paradigm-20260827-144553).
    [GeneratedRegex(
        @"^\s*Your current alignment prevents you from entering this exit[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AlignmentBlocksExit();

    // Confusion-fumble wordings ("You fumble in confusion!", convulsions' "You convulse
    // violently" / "You look around stupidly and do nothing") are no longer hardcoded
    // here — they live on Confused MessageRecords' ConfuseFumbleLine and reach HandleLine
    // through the _isConfuseFumbleLine predicate (ConditionTracker.IsConfuseFumbleLine),
    // so the user can correct a spell's fumble wording in game data without an engine edit.
}
