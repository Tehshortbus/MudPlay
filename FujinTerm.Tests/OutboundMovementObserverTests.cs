using System.Collections.Generic;
using System.IO;
using System.Text;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Coverage for <see cref="OutboundMovementObserver"/> + the tracker's
/// look-suppression machinery. Verifies <c>look &lt;dir&gt;</c> peeks
/// don't desync the tracker and text-exit verbs are announced as moves.
/// </summary>
public sealed class OutboundMovementObserverTests : IDisposable
{
    private readonly string _root;

    public OutboundMovementObserverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-outbound-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Town Gates",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "North Square",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private (RoomTracker Tracker, OutboundMovementObserver Observer) NewObserver()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        OutboundMovementObserver observer = new(tracker);
        return (tracker, observer);
    }

    private static byte[] Cmd(string text) => Encoding.Latin1.GetBytes(text + "\r");

    private static RoomObservation Obs(string name, params Direction[] dirs)
        => new(name, new HashSet<Direction>(dirs));

    // ----- look-direction suppression --------------------------------

    [Theory]
    [InlineData("look n")]
    [InlineData("l e")]
    [InlineData("look north")]
    [InlineData("peek north")]
    [InlineData("peer south")]
    public void LookCommand_ArmsSuppression_DropsNextObservation(string command)
    {
        (RoomTracker tracker, OutboundMovementObserver observer) = NewObserver();
        tracker.SetLocated(new RoomKey(1, 1));

        observer.ObserveOutbound(Cmd(command));

        // Server replies with a peek of the neighbouring room — the
        // tracker MUST treat this as a peek and stay at 1/1, not move
        // to 1/2.
        tracker.NoteRoomObserved(Obs("North Square", Direction.S));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void LookCommand_SuppressesOnlyOneObservation()
    {
        (RoomTracker tracker, OutboundMovementObserver observer) = NewObserver();
        tracker.SetLocated(new RoomKey(1, 1));

        observer.ObserveOutbound(Cmd("look n"));

        // First observation = the peek, dropped.
        tracker.NoteRoomObserved(Obs("North Square", Direction.S));
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);

        // Subsequent observation must process normally.
        tracker.NoteMoveSent(Direction.N);
        tracker.NoteRoomObserved(Obs("North Square", Direction.S));
        Assert.Equal(new RoomKey(1, 2), tracker.State.CurrentRoom!.Key);
    }

    // ----- text-exit movement ----------------------------------------

    [Theory]
    [InlineData("go path")]
    [InlineData("enter portal")]
    [InlineData("climb tree")]
    [InlineData("swim river")]
    [InlineData("cross bridge")]
    public void TextExitMovement_AnnouncesMoveToTracker(string command)
    {
        (RoomTracker tracker, OutboundMovementObserver observer) = NewObserver();
        tracker.SetLocated(new RoomKey(1, 1));

        observer.ObserveOutbound(Cmd(command));

        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);
    }

    [Fact]
    public void TextExitMovement_StepPersistsToProfile()
    {
        (RoomTracker tracker, OutboundMovementObserver observer) = NewObserver();
        var profile = new FujinTerm.Models.Profile.CharacterProfile
        {
            LastKnownRoom = new FujinTerm.Models.Profile.RoomRef(1, 1),
        };
        tracker.Hydrate(profile);

        observer.ObserveOutbound(Cmd("go path"));

        Assert.NotNull(profile.RecentSteps);
        Assert.Single(profile.RecentSteps!);
        Assert.Equal("go path", profile.RecentSteps![0].Command);
        Assert.Null(profile.RecentSteps![0].Cardinal);
    }

    // ----- cardinal announcement (added so manual movement updates) -

    [Theory]
    [InlineData("n",  Direction.N)]
    [InlineData("north", Direction.N)]
    [InlineData("ne", Direction.NE)]
    [InlineData("southwest", Direction.SW)]
    [InlineData("u",  Direction.U)]
    [InlineData("d",  Direction.D)]
    public void CardinalCommand_AnnouncesMoveToTracker(string command, Direction expected)
    {
        // Manual cardinal typing must reach the tracker — without it,
        // the tracker can't predict the landing when the player walks
        // through a chain of same-named rooms (e.g. Stone Street
        // corridors). Walker/loop-runner duplicate calls are debounced
        // by the tracker on the matching-direction-within-100ms rule.
        (RoomTracker tracker, OutboundMovementObserver observer) = NewObserver();
        tracker.SetLocated(new RoomKey(1, 1));

        observer.ObserveOutbound(Cmd(command));

        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);
        _ = expected;
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("inv")]
    [InlineData("who")]
    [InlineData("stat")]
    public void NonMovementCommand_LeavesTrackerConfirmed(string command)
    {
        (RoomTracker tracker, OutboundMovementObserver observer) = NewObserver();
        tracker.SetLocated(new RoomKey(1, 1));

        observer.ObserveOutbound(Cmd(command));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
    }

    [Fact]
    public void Cardinal_RapidDuplicate_DebouncedByTracker()
    {
        // Walker calls NoteMoveSent directly before the bytes flush
        // through SendUserInput, then OutboundMovementObserver fires
        // the same direction back. The tracker drops the duplicate
        // so the pending queue doesn't double-up.
        (RoomTracker tracker, OutboundMovementObserver observer) = NewObserver();
        tracker.SetLocated(new RoomKey(1, 1));

        tracker.NoteMoveSent(Direction.N);
        observer.ObserveOutbound(Cmd("n"));

        // Land on the predicted neighbour — proves the queue has one
        // entry (not two).
        tracker.NoteRoomObserved(new RoomObservation("North Square",
            new HashSet<Direction> { Direction.S }));
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 2), tracker.State.CurrentRoom!.Key);
    }

    // ----- safety guards ---------------------------------------------

    [Fact]
    public void OversizedPayload_IsIgnored()
    {
        (RoomTracker tracker, OutboundMovementObserver observer) = NewObserver();
        tracker.SetLocated(new RoomKey(1, 1));

        // 100-byte payload — past the safety cap, nothing should fire.
        observer.ObserveOutbound(Encoding.Latin1.GetBytes(new string('a', 100)));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
    }

    [Fact]
    public void EmptyPayload_IsIgnored()
    {
        (RoomTracker tracker, OutboundMovementObserver observer) = NewObserver();
        tracker.SetLocated(new RoomKey(1, 1));

        observer.ObserveOutbound(ReadOnlySpan<byte>.Empty);

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
    }
}
