using System.Collections.Concurrent;
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
        // The game's output, fed the way the client receives it.
        public ServerFeed Feed { get; } = new();
        public SysStatusProbe Probe { get; }
        public SysopPositionResolver Resolver { get; }
        public RoomTracker Tracker { get; }
        public List<string> Sent { get; } = new();
        // The wait for movement to settle before a queued locate.
        public ManualTimers Deferral { get; } = new();
        public List<RoomKey> Resolved { get; } = new();
        public int Failures { get; private set; }
        public bool CapabilityEnabled { get; set; } = true;
        public bool Suppressed { get; set; }

        private TaskCompletionSource _currentDelay = new();
        // What the resolver hands on to finish a probe, where the app hands it to the
        // UI thread.
        private readonly BlockingCollection<Action> _posted = new();

        public Harness(string root)
        {
            Parser.AttachLineExtractor(Feed.Lines);
            Directory.CreateDirectory(Path.Combine(root, "alpha"));
            File.WriteAllText(Path.Combine(root, "alpha", "Rooms.json"), GraphJson);
            GameDataCache cache = new(root);
            cache.SwitchSet("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");
            Tracker = new RoomTracker(graph);

            // Only a test can fire the timeout, so no wall clock is involved.
            Probe = new SysStatusProbe(Parser, () => CapabilityEnabled, delay: _ =>
            {
                _currentDelay = new TaskCompletionSource();
                return _currentDelay.Task;
            });
            Probe.SetWireSender(bytes =>
            {
                Sent.Add(Encoding.Latin1.GetString(bytes));
                Parser.ObserveOutbound(bytes);
            });

            Resolver = new SysopPositionResolver(
                Probe, graph, Tracker,
                suppressed: () => Suppressed,
                log: null,
                post: _posted.Add,
                schedule: Deferral.Arm);
            Resolver.PositionResolved += Resolved.Add;
            Resolver.LocateFailed += () => Failures++;
        }

        public void ReplyWithRoom(int map, int room)
        {
            int before = Resolved.Count + Failures;
            Feed.Line($"Room {room}  Map: {map}");
            Feed.Line("Monsters: None");
            Feed.Line("[HP=100]:");
            FinishTheProbe();
            Assert.Equal(before + 1, Resolved.Count + Failures);
        }

        public void FireProbeTimeout()
        {
            int before = Resolved.Count + Failures;
            _currentDelay.TrySetResult();
            FinishTheProbe();
            Assert.Equal(before + 1, Resolved.Count + Failures);
        }

        // Drives the tracker to Lost: from Unknown, an observation no graph room
        // matches leaves replay and name re-anchor with nothing to land on.
        public void DriveTrackerLost()
            => Tracker.NoteRoomObserved(new RoomObservation("Nowhere", new HashSet<Direction> { Direction.W }));

        // A reply goes on from the probe on a pool thread, which posts the finish to
        // the resolver's thread. That post is waited for, not polled for, and run here,
        // as the UI thread would run it: the limit only bounds a probe that never
        // answers, however busy the pool is.
        private void FinishTheProbe()
        {
            Assert.True(_posted.TryTake(out Action? finish, TimeSpan.FromSeconds(30)), "the probe never finished");
            finish();
            Assert.Empty(_posted);
        }

        public void Dispose()
        {
            Probe.Dispose();
            _posted.Dispose();
        }
    }

    [Fact]
    public void LocatesTheTrackerAtTheReportedRoom()
    {
        using Harness h = new(_root);

        Assert.True(h.Resolver.TryRequestLocate("test"));
        h.ReplyWithRoom(1, 7);

        Assert.Equal(new[] { "sys st\r\n" }, h.Sent);
        Assert.Equal(new RoomKey(1, 7), h.Tracker.State.CurrentRoom?.Key);
        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 7), Assert.Single(h.Resolved));
        Assert.Equal(0, h.Failures);
    }

    [Fact]
    public void TrackerGoingLostAsksWithoutBeingTold()
    {
        using Harness h = new(_root);

        h.DriveTrackerLost();
        Assert.Equal(RoomConfidence.Lost, h.Tracker.State.Confidence);

        h.ReplyWithRoom(1, 7);

        Assert.Single(h.Sent);
        Assert.Equal(new RoomKey(1, 7), h.Tracker.State.CurrentRoom?.Key);
        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
    }

    [Fact]
    public void ProbeTimeoutFailsTheLocateAndLeavesTheTrackerAlone()
    {
        using Harness h = new(_root);

        Assert.True(h.Resolver.TryRequestLocate("test"));
        h.FireProbeTimeout();

        Assert.Equal(1, h.Failures);
        Assert.Empty(h.Resolved);
        Assert.Null(h.Tracker.State.CurrentRoom);
    }

    [Fact]
    public void RoomOutsideTheActiveGraphFailsRatherThanLocating()
    {
        // The game is right and our map is wrong (wrong game-data set). A
        // confident wrong answer is worse than no answer.
        using Harness h = new(_root);

        Assert.True(h.Resolver.TryRequestLocate("test"));
        h.ReplyWithRoom(9, 999);

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
    public void LocateQueuesBehindUnconfirmedMovement()
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

        h.ReplyWithRoom(1, 7);
        Assert.Equal(new RoomKey(1, 7), Assert.Single(h.Resolved));
    }

    [Fact]
    public void DeferredLocateThatNeverSettlesFailsSoTheCallerIsNotStranded()
    {
        using Harness h = new(_root);
        h.Tracker.SetLocated(new RoomKey(1, 7));
        h.Tracker.NoteMoveSent(Direction.N);

        Assert.True(h.Resolver.TryRequestLocate("test"));
        h.Deferral.RunAll();

        Assert.Equal(1, h.Failures);
        Assert.Empty(h.Sent);
        Assert.False(h.Resolver.LocateDeferred);
    }

    [Fact]
    public void RepeatedRequestsAreThrottledIntoOneCommand()
    {
        // An oscillating tracker must not become a command loop.
        using Harness h = new(_root);

        Assert.True(h.Resolver.TryRequestLocate("first"));
        h.ReplyWithRoom(1, 7);

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

    [Fact]
    public void RecoveryCallerAsksImmediatelyEvenMidMove()
    {
        // The gate only asks because a move is stuck unconfirmed. Queuing behind
        // that move waits out the exact condition the locate is meant to break,
        // then times out and drops us into a backtrack that can't converge among
        // identically-named rooms.
        using Harness h = new(_root);
        h.Tracker.SetLocated(new RoomKey(1, 7));
        h.Tracker.NoteMoveSent(Direction.N);
        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);

        Assert.True(h.Resolver.TryRequestLocate("engine stalled", forRecovery: true));

        Assert.False(h.Resolver.LocateDeferred);
        Assert.Single(h.Sent);
        h.ReplyWithRoom(1, 7);
        Assert.Equal(new RoomKey(1, 7), Assert.Single(h.Resolved));
    }

    [Fact]
    public void PostDeathStillDefersEvenForARecoveryCaller()
    {
        // PendingRespawn is the one case where waiting is right regardless: the
        // respawn room's own observation is imminent and authoritative.
        using Harness h = new(_root);
        h.Tracker.NoteDeath(livesRemaining: 3);

        Assert.True(h.Resolver.TryRequestLocate("engine stalled", forRecovery: true));

        Assert.True(h.Resolver.LocateDeferred);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void ConsecutiveRecoveryLocatesAreNotThrottledOut()
    {
        // The stall watchdog escalates every 10s, so a second stall always lands
        // inside the 15s self-triggered throttle. Denying it drops the sweep into
        // a backtrack that can't converge among identically-named rooms — which
        // killed a live sweep 10 seconds after the first locate had just worked.
        using Harness h = new(_root);

        Assert.True(h.Resolver.TryRequestLocate("stall 1", forRecovery: true));
        h.ReplyWithRoom(1, 7);
        Assert.Single(h.Resolved);

        Assert.True(h.Resolver.TryRequestLocate("stall 2", forRecovery: true));
        h.ReplyWithRoom(1, 7);

        Assert.Equal(2, h.Resolved.Count);
        Assert.Equal(2, h.Sent.Count);
    }

}
