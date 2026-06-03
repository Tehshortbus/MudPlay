using System.IO;
using FujinTerm.Game;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Coverage for <see cref="DeathDetector"/> + the tracker's death-side
/// state (<c>NoteDeath</c>, <see cref="RoomConfidence.PendingRespawn"/>,
/// DeathRecord persistence to profile).
/// </summary>
public sealed class DeathDetectorTests : IDisposable
{
    private readonly string _root;

    public DeathDetectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-death-tests-" + Path.GetRandomFileName());
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
          { "Map Number": 1, "Room Number": 2, "Name": "Healer Hall",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private (RoomTracker Tracker, DeathDetector Detector) NewDetector()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        DeathDetector detector = new(tracker);
        return (tracker, detector);
    }

    // ----- detector pattern matching ---------------------------------

    [Theory]
    [InlineData("You now have 4 lives remaining.")]
    [InlineData("You now have 1 life remaining.")]
    [InlineData("You now have 0 lives remaining.")]
    public void DeathMessage_TriggersTrackerNoteDeath(string line)
    {
        (RoomTracker tracker, DeathDetector detector) = NewDetector();
        tracker.SetLocated(new RoomKey(1, 1));

        detector.FeedTestLine(line);

        Assert.Equal(RoomConfidence.PendingRespawn, tracker.State.Confidence);
        Assert.Null(tracker.State.CurrentRoom);                    // cleared until respawn obs
    }

    [Theory]
    [InlineData("You have 4 lives left.")]                        // miracle save — NOT death
    [InlineData("You hit the goblin for 4 damage.")]
    [InlineData("4 lives remaining.")]                            // missing "You now have" prefix
    [InlineData("Random chat line.")]
    public void NonDeathLine_DoesNotTriggerDeath(string line)
    {
        (RoomTracker tracker, DeathDetector detector) = NewDetector();
        tracker.SetLocated(new RoomKey(1, 1));

        detector.FeedTestLine(line);

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.NotNull(tracker.State.CurrentRoom);
    }

    // ----- death recording on profile --------------------------------

    [Fact]
    public void Death_CapturesRoomWhereWeDied_OnProfile()
    {
        (RoomTracker tracker, DeathDetector detector) = NewDetector();
        var profile = new CharacterProfile
        {
            LastKnownRoom = new RoomRef(1, 1),
        };
        tracker.Hydrate(profile);

        detector.FeedTestLine("You now have 3 lives remaining.");

        Assert.NotNull(profile.DeathHistory);
        Assert.Single(profile.DeathHistory!);
        DeathRecord rec = profile.DeathHistory![0];
        Assert.NotNull(rec.Room);
        Assert.Equal(1, rec.Room!.Map);
        Assert.Equal(1, rec.Room!.Room);
        Assert.Equal(3, rec.LivesRemaining);
        Assert.Equal("You now have 3 lives remaining.", rec.MessageText);
    }

    [Fact]
    public void Death_AppendsToHistory_DoesNotReplace()
    {
        (RoomTracker tracker, DeathDetector detector) = NewDetector();
        var profile = new CharacterProfile
        {
            LastKnownRoom = new RoomRef(1, 1),
        };
        tracker.Hydrate(profile);

        detector.FeedTestLine("You now have 3 lives remaining.");
        // Respawn observation lands at Healer Hall (the next obs).
        tracker.NoteRoomObserved(new RoomObservation("Healer Hall",
            new HashSet<Direction> { Direction.S }));
        Assert.Equal(new RoomKey(1, 2), tracker.State.CurrentRoom!.Key);

        // Die again.
        detector.FeedTestLine("You now have 2 lives remaining.");

        Assert.Equal(2, profile.DeathHistory!.Count);
        Assert.Equal(3, profile.DeathHistory[0].LivesRemaining);
        Assert.Equal(1, profile.DeathHistory[0].Room!.Room);   // 1st death: Town Gates (1/1)
        Assert.Equal(2, profile.DeathHistory[1].LivesRemaining);
        Assert.Equal(2, profile.DeathHistory[1].Room!.Room);   // 2nd death: Healer Hall (1/2)
    }

    // ----- PendingRespawn state semantics ----------------------------

    [Fact]
    public void PendingRespawn_NextObservation_LandsConfirmed()
    {
        (RoomTracker tracker, DeathDetector detector) = NewDetector();
        tracker.SetLocated(new RoomKey(1, 1));

        detector.FeedTestLine("You now have 4 lives remaining.");
        Assert.Equal(RoomConfidence.PendingRespawn, tracker.State.Confidence);

        tracker.NoteRoomObserved(new RoomObservation("Healer Hall",
            new HashSet<Direction> { Direction.S }));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 2), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void Death_DrainsPendingMovesAndSteps()
    {
        (RoomTracker tracker, DeathDetector detector) = NewDetector();
        var profile = new CharacterProfile
        {
            LastKnownRoom = new RoomRef(1, 1),
        };
        tracker.Hydrate(profile);
        tracker.NoteMoveSent(Direction.N);                          // step pending
        Assert.NotNull(profile.RecentSteps);

        detector.FeedTestLine("You now have 3 lives remaining.");

        // RecentSteps wiped — they're stale post-death; replay
        // shouldn't try walking from the pre-death room.
        Assert.Null(profile.RecentSteps);
    }
}
