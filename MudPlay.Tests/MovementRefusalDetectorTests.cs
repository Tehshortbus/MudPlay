using System;
using System.IO;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class MovementRefusalDetectorTests : IDisposable
{
    // The game's output, fed the way the client receives it.
    private readonly ServerFeed _feed = new();

    private readonly string _root;

    public MovementRefusalDetectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-refusal-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Origin",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Beyond",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private (RoomTracker Tracker, MovementRefusalDetector Detector) NewDetector(
        Func<string, bool>? isConfuseFumble = null, Func<string, bool>? isActiveHoldLine = null)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        MovementRefusalDetector detector = new(_feed.Lines, tracker, null, isConfuseFumble, isActiveHoldLine);
        return (tracker, detector);
    }

    // Test stand-in for ConditionTracker.IsConfuseFumbleLine: the three wordings the seed
    // carries on Confused records, matched whole-line with the trailing-'!'/'.' tolerance
    // the real predicate applies.
    private static bool IsTestConfuseFumble(string text)
    {
        string n = text.Trim().TrimEnd('.', '!').TrimEnd();
        return n.Equals("You fumble in confusion", StringComparison.OrdinalIgnoreCase)
            || n.Equals("You convulse violently", StringComparison.OrdinalIgnoreCase)
            || n.Equals("You look around stupidly and do nothing", StringComparison.OrdinalIgnoreCase);
    }

    private void SetupPending(RoomTracker tracker)
    {
        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteMoveSent(Direction.N);
        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);
    }

    [Theory]
    [InlineData("You can't move that direction.")]
    [InlineData("You can't move in that direction.")]
    [InlineData("You can't go that way.")]
    [InlineData("There is no exit that direction.")]
    [InlineData("There is no exit in that direction.")]
    [InlineData("You are too paralyzed to move.")]
    [InlineData("You are too confused to move.")]
    [InlineData("You are too stunned to move.")]
    [InlineData("You are too dazed to move.")]
    [InlineData("You can't see well enough to move.")]
    [InlineData("You are too encumbered to move.")]
    // Over max encumbrance: the Stock wording, and the shorter Paradigm one.
    [InlineData("You are too heavy to move anywhere!")]
    [InlineData("You are too heavy to move!")]
    [InlineData("The door is closed.")]
    [InlineData("The gate is closed!")]
    [InlineData("The gate is closed.")]
    [InlineData("The door is closed in that direction!")]
    // Stock engine wordings.
    [InlineData("You are too stunned to move anywhere!")]
    [InlineData("There is a closed door in that direction!")]
    // Paradigm terminates refusal lines with '!' — the confirmed capture that
    // stranded a Pending move, plus representative variants of the broadened set.
    [InlineData("There is no exit in that direction!")]
    [InlineData("There is no exit that direction!")]
    [InlineData("You can't go that way!")]
    [InlineData("You are too paralyzed to move!")]
    // Knocked down — the move sent just before the knockdown lands bonks this
    // way; recognizing it keeps the tracker from stranding on the unresolved step.
    [InlineData("You are flat on your back!")]
    [InlineData("You are flat on your back.")]
    // Alignment-gated exit refusal (report -144553) — a route planned through an
    // exit our alignment can't use bonks here; revert cleanly, don't strand.
    [InlineData("Your current alignment prevents you from entering this exit.")]
    [InlineData("Your current alignment prevents you from entering this exit!")]
    public void RefusalLines_RevertPendingToLocated(string line)
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        SetupPending(tracker);

        _feed.Line(line);

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void UnrelatedLine_DoesNotTrigger()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        SetupPending(tracker);

        _feed.Line("The goblin growls at you.");

        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);
    }

    // The typing-rate limiter dropped the move we just sent ("You are typing too
    // quickly - command ignored") — it never executed, so un-count the pending
    // move and stay put. This is the net-miscount that strands a homogeneous grid
    // in Lost (issue #478). A move sent moments before the drop is plausibly the
    // contended command, so the revert fires.
    [Theory]
    [InlineData("You are typing too quickly - command ignored.")]
    [InlineData("You are typing too quickly - command ignored!")]
    [InlineData("You are typing too quickly - command ignored")]
    public void TypingTooQuickly_WithinWindow_RevertsPendingMove(string line)
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        var sent = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        tracker.SetLocated(new RoomKey(1, 1), sent);
        tracker.NoteMoveSent(Direction.N, sent);
        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);

        _feed.Line(line, sent.AddMilliseconds(300));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    // A pending move outstanding longer than the attribution window is more likely
    // a slow landing than the command the limiter just dropped — leave it pending
    // so a genuinely-in-flight move still confirms when its room arrives.
    [Fact]
    public void TypingTooQuickly_OutsideWindow_LeavesPendingMove()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        var sent = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        tracker.SetLocated(new RoomKey(1, 1), sent);
        tracker.NoteMoveSent(Direction.N, sent);

        _feed.Line("You are typing too quickly - command ignored.", sent.AddSeconds(5));

        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);
    }

    // No move in flight → the dropped command wasn't a move of ours; no-op.
    [Fact]
    public void TypingTooQuickly_NotPending_NoOp()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        tracker.SetLocated(new RoomKey(1, 1));

        _feed.Line("You are typing too quickly - command ignored.");

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    // The limiter's preceding warning ("Why don't you slow down...") carries no
    // drop — only the "command ignored" line un-counts anything. The warning must
    // leave a pending move untouched.
    [Fact]
    public void SlowDownWarning_DoesNotRevert()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        SetupPending(tracker);

        _feed.Line("Why don't you slow down for a few seconds?");

        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);
    }

    // Confusion fumbles the just-sent move — it never executes, so the pending step
    // must revert or the tracker strands (report -080223). The wordings are no longer
    // hardcoded: they come from Confused records' ConfuseFumbleLine via the injected
    // predicate (ConditionTracker.IsConfuseFumbleLine in the app), so a match still bonks.
    [Theory]
    [InlineData("You fumble in confusion!")]
    [InlineData("You convulse violently!")]
    [InlineData("You look around stupidly and do nothing!")]
    public void ConfuseFumbleLine_RevertsPendingToLocated(string line)
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector(IsTestConfuseFumble);
        SetupPending(tracker);

        _feed.Line(line);

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    // Report paradigm-20261006-221209: a roar held the character with a step just
    // sent. Paradigm answered the step with the hold's own line, the move stayed
    // Pending, and combat stood down for "a move in flight" until the character died.
    [Fact]
    public void ActiveHoldLine_RevertsThePendingMove()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector(
            isActiveHoldLine: t => t.Trim() == "You are too scared to flee!");
        SetupPending(tracker);

        _feed.Line("You are too scared to flee!");

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    // The same line prints when the hold lands or is renewed with nothing sent.
    [Fact]
    public void ActiveHoldLine_WithNoMovePending_ChangesNothing()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector(
            isActiveHoldLine: t => t.Trim() == "You are too scared to flee!");
        tracker.SetLocated(new RoomKey(1, 1));
        int blocked = 0;
        tracker.MoveBlocked += () => blocked++;

        _feed.Line("You are too scared to flee!");

        Assert.Equal(0, blocked);
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
    }

    // The Stock engine's refusal of a move by a character who can't move.
    [Fact]
    public void CantSeemToMoveAnywhere_RevertsThePendingMove()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        SetupPending(tracker);

        _feed.Line("You can't seem to move anywhere!");

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
    }

    // Fully data-driven: with no predicate wired, a confuse-fumble line no longer bonks
    // on its own — the recognition lives in game data, not a hardcoded regex.
    [Fact]
    public void ConfuseFumbleLine_NotRecognizedWithoutPredicate()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        SetupPending(tracker);

        _feed.Line("You fumble in confusion!");

        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);
    }

    // "You are in convulsions!" is the condition's ambient onset/round-tick line,
    // distinct from "You convulse violently!" (its action-fumble reply) — the predicate
    // matches only the fumble wordings, so the onset must not revert a landed move.
    [Fact]
    public void ConvulsionsOnsetLine_DoesNotTrigger()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector(IsTestConfuseFumble);
        SetupPending(tracker);

        _feed.Line("You are in convulsions!");

        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);
    }

    [Fact]
    public void ChatLineQuotingPhrase_DoesNotTrigger()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        SetupPending(tracker);

        // Anchored patterns mean a quote inside a chat-prefixed line
        // doesn't false-trigger.
        _feed.Line("[Gossip] Bob: I told him 'You can't go that way.' lol");

        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);
    }

    // A gated exit or room refused the move. These used to reach only the
    // DirectionFailed router pattern, which does nothing while a move is Pending, so
    // the refused move stranded the walker until its stall watchdog.
    [Theory]
    [InlineData("You have progressed too far for this room.")]
    [InlineData("You are not permitted in that room!")]
    [InlineData("You do not have the appropriate item to go that direction!")]
    [InlineData("You do not have enough to cover the toll of 5 gold crowns.")]
    // An item exit's own refusal, printed in place of the generic item line.
    [InlineData("You do not have a room ticket.")]
    // Also a room-command refusal; with a cardinal in flight it falls through here.
    [InlineData("A strange power holds you back!")]
    public void ExitGateRefusal_RevertsPendingCardinal(string line)
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        SetupPending(tracker);

        _feed.Line(line);

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    // A level-capped room answers one move with two lines; the second must not
    // drop a move queued after the refused one.
    [Fact]
    public void ExitGateRefusal_SecondLineForSameMove_LeavesLaterMoveQueued()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        SetupPending(tracker);

        _feed.Line("You have progressed too far for this room.");
        _feed.Line("You are not permitted in that room!");

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.False(tracker.HasQueuedMoves);
    }

    // A room script refused the typed exit command (touch altar / enter portal).
    [Theory]
    [InlineData("You cannot do that right now!")]
    [InlineData("You do not see that here.")]
    [InlineData("The dark power of the portal forces you back!")]
    [InlineData("The Grey Lord simply stares at you in silence.")]
    public void RoomCommandRefusal_RevertsPendingTypedCommand(string line)
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteMoveSent("touch altar", cardinal: Direction.N);

        _feed.Line(line);

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    // The generic refusals also answer ordinary commands, so a cardinal in flight
    // is never reverted by one.
    [Fact]
    public void RoomCommandRefusal_LeavesPendingCardinal()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        SetupPending(tracker);

        _feed.Line("You do not have that.");

        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);
    }

    // A typed command outstanding longer than the attribution window isn't what
    // the refusal answered.
    [Fact]
    public void RoomCommandRefusal_OutsideWindow_LeavesPendingCommand()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        var sent = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        tracker.SetLocated(new RoomKey(1, 1), sent);
        tracker.NoteMoveSent("touch altar", cardinal: Direction.N, whenUtc: sent);

        _feed.Line("You cannot do that right now!", sent.AddSeconds(5));

        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);
    }

    // A fall usually means the move landed somewhere else: drop the prediction and
    // re-check, whether the move is still pending or already confirmed.
    [Theory]
    [InlineData("You fall to the ground with a thud, taking 12 damage!")]
    [InlineData("You take 12 damage from the fall!")]
    public void FallLine_DropsThePredictionToSuspect(string line)
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteMoveSent("jump roof", cardinal: Direction.N);

        _feed.Line(line);

        Assert.Equal(RoomConfidence.Suspect, tracker.State.Confidence);
        Assert.False(tracker.HasQueuedMoves);
    }

    [Fact]
    public void RefusalFromNonPending_StateIsNoOp()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        tracker.SetLocated(new RoomKey(1, 1));   // Located, not Pending

        _feed.Line("You can't go that way.");

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
    }

    // A door that read "open" but shut mid-combat: the "The door is closed!"
    // refusal must clear the stale open-door flag so the next move re-opens the
    // door via the FSM instead of bonking the shut door again (the reported
    // mid-combat bonk loop).
    [Fact]
    public void DoorClosedRefusal_ClearsStaleOpenDoorFlag()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteRoomObserved(new RoomObservation(
            "Origin",
            new HashSet<Direction> { Direction.N },
            new HashSet<Direction> { Direction.N }));
        Assert.Contains(Direction.N, tracker.State.OpenDoorDirections!);

        tracker.NoteMoveSent(Direction.N);
        _feed.Line("The door is closed!");

        Assert.Null(tracker.State.OpenDoorDirections);   // only entry cleared
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
    }

    // A non-door refusal must leave the open-door cache untouched — only a
    // closed-door line invalidates the "already open" reading.
    [Fact]
    public void NonDoorRefusal_LeavesOpenDoorFlagIntact()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteRoomObserved(new RoomObservation(
            "Origin",
            new HashSet<Direction> { Direction.N },
            new HashSet<Direction> { Direction.N }));

        tracker.NoteMoveSent(Direction.N);
        _feed.Line("You are too encumbered to move.");

        Assert.Contains(Direction.N, tracker.State.OpenDoorDirections!);
    }

    // "The door to the <dir> just closed." names its direction. When it matches
    // the direction we're heading, it acts like the bare closed-door refusal:
    // clear the stale open-door flag and revert the pending move so the retry
    // re-opens the door via the FSM.
    [Fact]
    public void NamedDoorJustClosed_HeadingThatWay_ClearsFlagAndReverts()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteRoomObserved(new RoomObservation(
            "Origin",
            new HashSet<Direction> { Direction.N },
            new HashSet<Direction> { Direction.N }));
        Assert.Contains(Direction.N, tracker.State.OpenDoorDirections!);

        tracker.NoteMoveSent(Direction.N);
        _feed.Line("The door to the north just closed.");

        Assert.Null(tracker.State.OpenDoorDirections);
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
    }

    // Moving another way, the line may belong to the room being left or entered —
    // leave the pending move and the open-door cache untouched.
    [Fact]
    public void NamedDoorJustClosed_NotHeadingThatWay_Ignored()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteRoomObserved(new RoomObservation(
            "Origin",
            new HashSet<Direction> { Direction.N },
            new HashSet<Direction> { Direction.N }));

        tracker.NoteMoveSent(Direction.N);   // heading north
        _feed.Line("The door to the south just closed.");

        Assert.Contains(Direction.N, tracker.State.OpenDoorDirections!);
        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);
    }

    // Standing still, the line is about this room's own exit: clear its open flag so
    // the next move through it opens the door first instead of bouncing off it
    // (report paradigm-20260928-110224).
    [Fact]
    public void NamedDoorJustClosed_StandingStill_ClearsThatDoorsOpenFlag()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteRoomObserved(new RoomObservation(
            "Origin",
            new HashSet<Direction> { Direction.N },
            new HashSet<Direction> { Direction.N }));
        Assert.Contains(Direction.N, tracker.State.OpenDoorDirections!);

        _feed.Line("The door to the north just closed.");

        Assert.Null(tracker.State.OpenDoorDirections);
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
    }

    // Door and exit changes someone else made (Stock DLL _cmd_open / _cmd_close /
    // _background_update_exits), applied to the room we're standing in.
    [Fact]
    public void DoorOpenedOrClosedByOthers_KeepsTheRoomsDoorsInStep()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteRoomObserved(new RoomObservation(
            "Origin",
            new HashSet<Direction> { Direction.N, Direction.E },
            null,
            new HashSet<Direction> { Direction.N, Direction.E }));

        _feed.Line("The door to the north just opened.");
        Assert.Contains(Direction.N, tracker.State.OpenDoorDirections!);

        _feed.Line("You see Bob close the door to the north.");
        Assert.Null(tracker.State.OpenDoorDirections);

        _feed.Line("You see Bob open the gate to the east.");
        Assert.Contains(Direction.E, tracker.State.OpenDoorDirections!);

        _feed.Line("The gate to the east just locked!");
        Assert.Null(tracker.State.OpenDoorDirections);
    }

    // A lever-opened / timed exit opening or shutting while we stand here keeps the
    // room's shown exits right, so the engines can cross it without the lever.
    [Fact]
    public void ExitJustOpenedOrClosed_UpdatesTheShownExits()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteRoomObserved(new RoomObservation("Origin", new HashSet<Direction> { Direction.N }));

        _feed.Line("The exit to the west just opened!");
        Assert.Contains(Direction.W, tracker.ShownOpenExits()!);

        _feed.Line("The exit to the west just closed!");
        Assert.DoesNotContain(Direction.W, tracker.ShownOpenExits()!);
    }
}
