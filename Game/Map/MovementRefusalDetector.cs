using System.Linq;
using System.Text.RegularExpressions;
using MudPlay.Services;
using MudPlay.Services.Patterns;
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

    // Recognizes the line a hold we are under prints (ConditionTracker.IsActiveHoldLine).
    // Paradigm refuses a held character's move with the hold's own applied line, which
    // differs per spell, so it comes from game data too. Left null in tests that don't
    // exercise it.
    private readonly Func<string, bool>? _isActiveHoldLine;

    public MovementRefusalDetector(LineExtractor lines, RoomTracker tracker, LogService? log = null,
        Func<string, bool>? isConfuseFumbleLine = null, Func<string, bool>? isActiveHoldLine = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(tracker);
        _lines = lines;
        _tracker = tracker;
        _log = log;
        _isConfuseFumbleLine = isConfuseFumbleLine;
        _isActiveHoldLine = isActiveHoldLine;
        _lines.LineEmitted += OnLineEmitted;
        _tracker.CommandMoveRefused += OnCommandMoveRefused;
    }

    public void Dispose()
    {
        _lines.LineEmitted -= OnLineEmitted;
        _tracker.CommandMoveRefused -= OnCommandMoveRefused;
    }

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

        // Door and exit changes someone else made, which name the direction
        // (Stock DLL _cmd_open / _cmd_close / _background_update_exits; the same
        // on Paradigm). The tracker decides how each applies to where we are.
        if (DoorToDirectionChanged().Match(text) is { Success: true } doorChange
            && DirectionExtensions.TryFromLongName(doorChange.Groups["dir"].Value, out Direction doorDir))
        {
            if (doorChange.Groups["what"].Value.Equals("opened", StringComparison.OrdinalIgnoreCase))
                _tracker.NoteNamedDoorOpened(doorDir);
            else
                _tracker.NoteNamedDoorClosed(doorDir, when);
            _log?.Info("MoveRefusal", $"door to {doorDir.ToLongName()} {doorChange.Groups["what"].Value}: {text.Trim()}");
            return;
        }
        if (SeenDoorChanged().Match(text) is { Success: true } seen
            && DirectionExtensions.TryFromLongName(seen.Groups["dir"].Value, out Direction seenDir))
        {
            if (seen.Groups["what"].Value.Equals("open", StringComparison.OrdinalIgnoreCase))
                _tracker.NoteNamedDoorOpened(seenDir);
            else
                _tracker.NoteNamedDoorClosed(seenDir, when);
            _log?.Info("MoveRefusal", $"saw the door to {seenDir.ToLongName()} {seen.Groups["what"].Value}ed: {text.Trim()}");
            return;
        }
        if (ExitToDirectionChanged().Match(text) is { Success: true } exitChange
            && DirectionExtensions.TryFromLongName(exitChange.Groups["dir"].Value, out Direction exitDir))
        {
            bool opened = exitChange.Groups["what"].Value.Equals("opened", StringComparison.OrdinalIgnoreCase);
            _tracker.NoteNamedExitChanged(exitDir, opened);
            _log?.Info("MoveRefusal", $"exit to {exitDir.ToLongName()} {(opened ? "opened" : "closed")}: {text.Trim()}");
            return;
        }

        // The typing-rate limiter silently dropped the command we just sent
        // ("You are typing too quickly - command ignored"). If that command was
        // the move in flight, it never executed — un-count it so the tracker
        // doesn't run a room ahead through a same-named grid.
        // NoteCommandDropped self-guards: it reverts only a recently-sent move
        // still waiting on its answer, since this line doesn't name what it dropped.
        // "You are too afraid!" refuses whatever we sent while feared, move or not,
        // so it reverts a move only through the same self-guarding path.
        if (TypingTooQuickly().IsMatch(text) || TooAfraid().IsMatch(text))
        {
            _tracker.NoteCommandDropped(when);
            _log?.Info("MoveRefusal", $"command dropped (typing too quickly): {text.Trim()}");
            return;
        }

        // We fell (a failed jump, or a fall after a drop): the landing is probably not
        // the room the move was headed for.
        if (FellLine().IsMatch(text))
        {
            _tracker.NoteFell(when);
            _log?.Info("MoveRefusal", $"fell — re-checking where we landed: {text.Trim()}");
            return;
        }

        // A room script refused the typed exit command. The tracker only reverts a
        // typed command in flight, since several of these lines also answer
        // ordinary commands. A cardinal in flight falls through: two of the lines
        // are also item-exit refusals.
        if (RoomCommandRefused().IsMatch(text))
        {
            // The tracker says whether a command is in flight by raising
            // CommandMoveRefused ahead of its revert; OnCommandMoveRefused reads
            // this there, while the exit refused can still be told.
            _priceRefusalInHand = PriceRefusal().IsMatch(text);
            bool reverted = _tracker.NoteCommandMoveRefused(when);
            _priceRefusalInHand = false;
            if (reverted)
            {
                _log?.Info("MoveRefusal", $"room command refused: {text.Trim()}");
                return;
            }
        }

        // A gated exit or room turned the move away. Only while a move awaits its
        // answer (Pending, or a move sent while unsure of the room): a level-capped
        // room answers with two of these lines for one move, and the second must
        // not drop a later queued move.
        if (ExitGateRefused().IsMatch(text))
        {
            if (!_tracker.MoveAwaitingAnswer) return;
            // Ahead of the revert: the walker re-plans off the revert, and by then
            // the toll gate must already know of the refusal.
            if (TollRefusal().Match(text) is { Success: true } toll)
                PaidCrossingRefused?.Invoke(_tracker.ExitInFlight()?.Exit, TollCopper(toll));
            _tracker.NoteMoveBlocked(when);
            _log?.Info("MoveRefusal", $"exit refused: {text.Trim()}");
            return;
        }

        // Held: the hold's line is also what answers a move, and it prints when the
        // hold lands or is renewed with nothing sent. A hold that lands with a move
        // still unanswered refuses that move too, so any of them reverts one — but
        // only while one awaits its answer.
        if (_isActiveHoldLine?.Invoke(text) == true)
        {
            if (!_tracker.MoveAwaitingAnswer) return;
            _tracker.NoteMoveBlocked(when);
            _log?.Info("MoveRefusal", $"held, the move never left the room: {text.Trim()}");
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
        TooHeavyToMove(),
        FlatOnYourBack(),
        CantSeemToMove(),
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

    // Paralyzed / confused / stunned variants — "You are too <state> to move.";
    // the Stock engine says "You are too stunned to move anywhere!".
    [GeneratedRegex(
        @"^\s*You are too (paralyzed|confused|stunned|dazed) to move(?: anywhere)?[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TooImpairedToMove();

    // The refusals a teleporting room command prints when a condition on its script
    // line fails: no monsters in the room, a minimum level, alignment, a carried or
    // room item, a present monster, a price. Each is the message the Stock 1.11p
    // script names for that condition (GAME_MECHANICS "Room-command refusals");
    // Paradigm is assumed to share them.
    [GeneratedRegex(
        @"^\s*(?:You (?:cannot|can't) do that right now!"
        + @"|You can't get to that right now!"
        + @"|You do not see (?:that|a portal) here\."
        + @"|You want to go where\?\?"
        + @"|You do not have that\."
        + @"|A strange power holds you back!"
        + @"|The (?:dark power of the portal|swirling chaotic energy of the vortex) forces you back!"
        + @"|That would be suicide without the proper equipment\."
        + @"|You quaff the bubbling white potion, but nothing happens\."
        + @"|The Grey Lord simply stares at you in silence\."
        + @"|The Grey Lord shakes his head, ""You must first grow further, young one\."""
        + @"|Jorah exclaims, ""You do not care about Balance! Begone, fool!"""
        + @"|He says, ""I may be old, but I count quite well and you are short!"""
        + @"|He shakes his head at you, ""Stop playing tricks on an old man!"")\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex RoomCommandRefused();

    // The failed-jump fall (spell `drops`) and the pit falls (`level N fall`) —
    // GAME_MECHANICS "Damage lines — who hit whom".
    [GeneratedRegex(
        @"^\s*(?:You fall to the ground with a thud, taking \d+ damage!|You take \d+ damage from the fall!)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex FellLine();

    [GeneratedRegex(
        @"^\s*(?:" + DefaultPatterns.ExitGateRefusals + @")\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ExitGateRefused();

    // A move of ours was turned away for want of coin, at a toll or by an NPC who
    // charges for a transport. Carries the exit refused, null when the move couldn't
    // be tied to one, and the price the line named in copper, null when it names
    // none we can value.
    //
    // A toll's line words the bar as "N gold crowns" whatever coins would have paid
    // it (GAME_MECHANICS "Toll exits"); any other coin wording is left unvalued. An
    // NPC's line names no figure, so its price is the fare the data has on the exit.
    public event Action<RoomExit?, long?>? PaidCrossingRefused;

    // True while the line in hand is the price refusal of a room command.
    private bool _priceRefusalInHand;

    private void OnCommandMoveRefused()
    {
        if (!_priceRefusalInHand) return;
        // Only for an exit that charges: the same line answers a `price` on a
        // command that goes nowhere.
        if (_tracker.ExitInFlight() is { } crossing && crossing.Exit.FareCopper > 0)
            PaidCrossingRefused?.Invoke(crossing.Exit, crossing.Exit.FareCopper);
    }

    // The one `price` refusal on record for a command that teleports: the Stock
    // 1.11p message, matched on both realms like the rest of RoomCommandRefused
    // (GAME_MECHANICS "Room-command refusals"). A fare whose line names another
    // message is refused in words nothing records, and is left to the walker's
    // ordinary handling of a refused command.
    [GeneratedRegex(
        @"^\s*He says, ""I may be old, but I count quite well and you are short!""\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PriceRefusal();

    private static long? TollCopper(Match toll) =>
        toll.Groups["coin"].Value is "gold crown" or "gold crowns"
        && long.TryParse(toll.Groups["n"].Value, out long gold)
            ? gold * 100
            : null;

    [GeneratedRegex(
        @"^\s*You do not have enough to cover the toll of (?<n>\d+) (?<coin>.+)\.\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TollRefusal();

    [GeneratedRegex(
        @"^\s*You can't see well enough to move[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CantSeeWellEnoughToMove();

    [GeneratedRegex(
        @"^\s*You are too encumbered to move[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TooEncumberedToMove();

    // The over-max-encumbrance refusal: "…to move anywhere!" on Stock, the shorter
    // form on Paradigm (GAME_MECHANICS "Too heavy to move (over max encumbrance)").
    [GeneratedRegex(
        @"^\s*You are too heavy to move(?: anywhere)?[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TooHeavyToMove();

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

    // The Stock engine's one refusal of a move by a held character, whatever the
    // hold (GAME_MECHANICS "Moving while held").
    [GeneratedRegex(
        @"^\s*You can't seem to move anywhere[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CantSeemToMove();

    // Door / gate blocking — server returns this when the user issues a direction
    // whose exit is shut. Both the plain and the "in that direction" long form are
    // covered, Stock's "There is a closed door in that direction!", and "gate" as
    // well as "door" (a fortress gate opened by a `pull
    // winch` prerequisite bonks with "The gate is closed!" — without matching it,
    // the pending move never reverts and the tracker latches in Pending, swallowing
    // even the post-open redisplay: the walker stalls forever, report
    // paradigm-20260827-113513).
    [GeneratedRegex(
        @"^\s*(?:The (?:door|gate) is closed(?: in that direction)?|There is a closed door in that direction)[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DoorIsClosed();

    // A door or gate in this room changed from the other side or by itself: "The
    // door to the north just closed.", "…just opened.", "The gate to the east just
    // locked!" (a timed re-lock also shuts it).
    [GeneratedRegex(
        @"^\s*The (?:door|gate) to the (?<dir>\w+) just (?<what>closed|opened|locked)[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DoorToDirectionChanged();

    // Someone in this room opened or closed one of its doors: "You see Bob close
    // the door to the north."
    [GeneratedRegex(
        @"^\s*You see .+? (?<what>open|close) the (?:door|gate) to the (?<dir>\w+)[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeenDoorChanged();

    // A timed or action-opened exit (a lever's hidden passage) opened or shut:
    // "The exit to the west just opened!"
    [GeneratedRegex(
        @"^\s*The exit to the (?<dir>\w+) just (?<what>opened|closed)[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExitToDirectionChanged();

    // Alignment-gated exit — Paradigm refuses an exit whose "(Alignment: X to Y)"
    // band excludes the mover ("Your current alignment prevents you from entering
    // this exit."). The router isn't alignment-aware yet, so a route planned through
    // such an exit bonks here; recognizing it reverts the pending move cleanly
    // instead of stranding the tracker (report paradigm-20260827-144553).
    [GeneratedRegex(
        @"^\s*Your current alignment prevents you from entering this exit[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AlignmentBlocksExit();

    // Typing-rate limiter dropped the just-sent command. Unlike the refusals
    // above this is NOT necessarily a move (it drops whatever was typed), so it
    // routes to NoteCommandDropped — which self-guards on a recently-sent move
    // still waiting on its answer — rather than the unconditional NoteMoveBlocked. The
    // preceding "Why don't you slow down for a few seconds?" warning carries no
    // drop, so only the "command ignored" line reverts anything.
    [GeneratedRegex(
        @"^\s*You are typing too quickly - command ignored[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TypingTooQuickly();

    // Feared: the game refuses any action (attack, item use, a move) with this.
    [GeneratedRegex(
        @"^\s*You are too afraid!\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TooAfraid();

    // Confusion-fumble wordings ("You fumble in confusion!", convulsions' "You convulse
    // violently" / "You look around stupidly and do nothing") are no longer hardcoded
    // here — they live on Confused MessageRecords' ConfuseFumbleLine and reach HandleLine
    // through the _isConfuseFumbleLine predicate (ConditionTracker.IsConfuseFumbleLine),
    // so the user can correct a spell's fumble wording in game data without an engine edit.
}
