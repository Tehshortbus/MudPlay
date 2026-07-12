using System.IO;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

public sealed class MovementRefusalDetectorTests : IDisposable
{
    private readonly string _root;

    public MovementRefusalDetectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-refusal-tests-" + Path.GetRandomFileName());
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

    private (RoomTracker Tracker, MovementRefusalDetector Detector) NewDetector()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        LineExtractor lines = new(new TerminalEmulator(80, 25));
        MovementRefusalDetector detector = new(lines, tracker);
        return (tracker, detector);
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
    // Paradigm terminates refusal lines with '!' — the confirmed capture that
    // stranded a Pending move, plus representative variants of the broadened set.
    [InlineData("There is no exit in that direction!")]
    [InlineData("There is no exit that direction!")]
    [InlineData("You can't go that way!")]
    [InlineData("You are too paralyzed to move!")]
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
}
