using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Ground-truth location recovery: the resolver asks `sys st`, and either the
// tracker ends up located at the reported room or every caller falls back to
// what it did before. The fallback paths matter as much as the happy one —
// they are what keeps the feature additive on a BBS with no sysop access.
public sealed class SysopPositionResolverTests : IDisposable
{
    private readonly string _root;

    public SysopPositionResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-sysoplocate-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 7, "Name": "Gang House",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private sealed class Harness : IDisposable
    {
        public SysRoomStatusParser Parser { get; } = new();
        public SysStatusProbe Probe { get; }
        public SysopPositionResolver Resolver { get; }
        public RoomTracker Tracker { get; }
        public List<string> Sent { get; } = new();
        public List<RoomKey> Resolved { get; } = new();
        public int Failures { get; private set; }
        public bool CapabilityEnabled { get; set; } = true;
        public bool Suppressed { get; set; }

        private TaskCompletionSource _currentDelay = new();

        public Harness(string root)
        {
            Directory.CreateDirectory(Path.Combine(root, "alpha"));
            File.WriteAllText(Path.Combine(root, "alpha", "Rooms.json"), GraphJson);
            GameDataCache cache = new(root);
            cache.SwitchSet("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");
            Tracker = new RoomTracker(graph);

            Probe = new SysStatusProbe(Parser, () => CapabilityEnabled)
            {
                // Only a test can fire the timeout, so no wall clock is involved.
                DelayProvider = _ =>
                {
                    _currentDelay = new TaskCompletionSource();
                    return _currentDelay.Task;
                },
            };
            Probe.SetWireSender(bytes =>
            {
                Sent.Add(Encoding.Latin1.GetString(bytes));
                Parser.ObserveOutbound(bytes);
            });

            Resolver = new SysopPositionResolver(
                Probe, graph, Tracker,
                suppressed: () => Suppressed,
                log: null,
                post: null,
                useTimer: false);
            Resolver.PositionResolved += Resolved.Add;
            Resolver.LocateFailed += () => Failures++;
        }

        // The probe completes off a continuation, so every test that drives a
        // reply has to let it run before asserting.
        public async Task ReplyWithRoom(int map, int room)
        {
            Parser.FeedTestLine($"Room {room}  Map: {map}");
            Parser.FeedTestLine("Monsters: None");
            Parser.FeedTestLine("[HP=100]:", isPromptLine: true);
            await Settle();
        }

        public async Task FireProbeTimeout()
        {
            _currentDelay.TrySetResult();
            await Settle();
        }

        // Drives the tracker to Lost: from Unknown, an observation no graph room
        // matches leaves replay and name re-anchor with nothing to land on.
        public void DriveTrackerLost()
            => Tracker.NoteRoomObserved(new RoomObservation("Nowhere", new HashSet<Direction> { Direction.W }));

        private static async Task Settle()
        {
            for (int i = 0; i < 8; i++) await Task.Yield();
        }

        public void Dispose() => Probe.Dispose();
    }

    [Fact]
    public async Task LocatesTheTrackerAtTheReportedRoom()
    {
        using Harness h = new(_root);

        Assert.True(h.Resolver.TryRequestLocate("test"));
        await h.ReplyWithRoom(1, 7);

        Assert.Equal(new[] { "sys st\r\n" }, h.Sent);
        Assert.Equal(new RoomKey(1, 7), h.Tracker.State.CurrentRoom?.Key);
        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 7), Assert.Single(h.Resolved));
        Assert.Equal(0, h.Failures);
    }

    [Fact]
    public async Task TrackerGoingLostAsksWithoutBeingTold()
    {
        using Harness h = new(_root);

        h.DriveTrackerLost();
        Assert.Equal(RoomConfidence.Lost, h.Tracker.State.Confidence);

        await h.ReplyWithRoom(1, 7);

        Assert.Single(h.Sent);
        Assert.Equal(new RoomKey(1, 7), h.Tracker.State.CurrentRoom?.Key);
        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
    }

    [Fact]
    public async Task ProbeTimeoutFailsTheLocateAndLeavesTheTrackerAlone()
    {
        using Harness h = new(_root);

        Assert.True(h.Resolver.TryRequestLocate("test"));
        await h.FireProbeTimeout();

        Assert.Equal(1, h.Failures);
        Assert.Empty(h.Resolved);
        Assert.Null(h.Tracker.State.CurrentRoom);
    }

    [Fact]
    public async Task RoomOutsideTheActiveGraphFailsRatherThanLocating()
    {
        // The game is right and our map is wrong (wrong game-data set). A
        // confident wrong answer is worse than no answer.
        using Harness h = new(_root);

        Assert.True(h.Resolver.TryRequestLocate("test"));
        await h.ReplyWithRoom(9, 999);

        Assert.Equal(1, h.Failures);
        Assert.Empty(h.Resolved);
        Assert.Null(h.Tracker.State.CurrentRoom);
        Assert.Contains("999", h.Resolver.LastOutcome);
    }

    [Fact]
    public void CapabilityOffSendsNothing()
    {
        using Harness h = new(_root) { CapabilityEnabled = false };

        Assert.False(h.Resolver.TryRequestLocate("test"));
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void SuppressedSendsNothing()
    {
        using Harness h = new(_root) { Suppressed = true };

        Assert.False(h.Resolver.TryRequestLocate("maze"));
        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task LocateQueuesBehindUnconfirmedMovement()
    {
        using Harness h = new(_root);
        h.Tracker.SetLocated(new RoomKey(1, 7));
        h.Tracker.NoteMoveSent(Direction.N);
        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);

        // Accepted (the caller may pause on it) but not yet on the wire: a dump
        // that arrives mid-move would describe the room we just left, and
        // locating would throw away the pending confirmation.
        Assert.True(h.Resolver.TryRequestLocate("test"));
        Assert.Empty(h.Sent);
        Assert.True(h.Resolver.LocateDeferred);

        // Movement settles → the queued locate goes out.
        h.Tracker.NoteRoomObserved(new RoomObservation("Gang House", new HashSet<Direction>()));
        Assert.Single(h.Sent);
        Assert.False(h.Resolver.LocateDeferred);

        await h.ReplyWithRoom(1, 7);
        Assert.Equal(new RoomKey(1, 7), Assert.Single(h.Resolved));
    }

    [Fact]
    public void DeferredLocateThatNeverSettlesFailsSoTheCallerIsNotStranded()
    {
        using Harness h = new(_root);
        h.Tracker.SetLocated(new RoomKey(1, 7));
        h.Tracker.NoteMoveSent(Direction.N);

        Assert.True(h.Resolver.TryRequestLocate("test"));
        h.Resolver.FireDeferralExpiryForTests();

        Assert.Equal(1, h.Failures);
        Assert.Empty(h.Sent);
        Assert.False(h.Resolver.LocateDeferred);
    }

    [Fact]
    public async Task RepeatedRequestsAreThrottledIntoOneCommand()
    {
        // An oscillating tracker must not become a command loop.
        using Harness h = new(_root);

        Assert.True(h.Resolver.TryRequestLocate("first"));
        await h.ReplyWithRoom(1, 7);

        Assert.False(h.Resolver.TryRequestLocate("second"));
        Assert.Single(h.Sent);
    }

    [Fact]
    public void ConcurrentRequestsShareTheOneInFlightProbe()
    {
        using Harness h = new(_root);

        Assert.True(h.Resolver.TryRequestLocate("first"));
        Assert.True(h.Resolver.TryRequestLocate("second"));

        Assert.Single(h.Sent);
    }
}
