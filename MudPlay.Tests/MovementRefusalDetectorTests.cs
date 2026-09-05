using System;
using System.IO;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

public sealed class MovementRefusalDetectorTests : IDisposable
{
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
        Func<string, bool>? isConfuseFumble = null)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        LineExtractor lines = new(new TerminalEmulator(80, 25));
        MovementRefusalDetector detector = new(lines, tracker, null, isConfuseFumble);
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
    [InlineData("The door is closed.")]
    [InlineData("The gate is closed!")]
    [InlineData("The gate is closed.")]
    [InlineData("The door is closed in that direction!")]
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

        detector.FeedTestLine(line);

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void UnrelatedLine_DoesNotTrigger()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        SetupPending(tracker);

        detector.FeedTestLine("The goblin growls at you.");

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

        detector.FeedTestLine(line);

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    // Fully data-driven: with no predicate wired, a confuse-fumble line no longer bonks
    // on its own — the recognition lives in game data, not a hardcoded regex.
    [Fact]
    public void ConfuseFumbleLine_NotRecognizedWithoutPredicate()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        SetupPending(tracker);

        detector.FeedTestLine("You fumble in confusion!");

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

        detector.FeedTestLine("You are in convulsions!");

        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);
    }

    [Fact]
    public void ChatLineQuotingPhrase_DoesNotTrigger()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        SetupPending(tracker);

        // Anchored patterns mean a quote inside a chat-prefixed line
        // doesn't false-trigger.
        detector.FeedTestLine("[Gossip] Bob: I told him 'You can't go that way.' lol");

        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);
    }

    [Fact]
    public void RefusalFromNonPending_StateIsNoOp()
    {
        (RoomTracker tracker, MovementRefusalDetector detector) = NewDetector();
        tracker.SetLocated(new RoomKey(1, 1));   // Located, not Pending

        detector.FeedTestLine("You can't go that way.");

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
        detector.FeedTestLine("The door is closed!");

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
        detector.FeedTestLine("You are too encumbered to move.");

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
        detector.FeedTestLine("The door to the north just closed.");

        Assert.Null(tracker.State.OpenDoorDirections);
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
    }

    // A door closing in a direction we're NOT heading is someone else's door —
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
        detector.FeedTestLine("The door to the south just closed.");

        Assert.Contains(Direction.N, tracker.State.OpenDoorDirections!);
        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);
    }
}
