using System.IO;
using System.Text;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Covers <see cref="ParadigmPositionResolver"/>'s realm gating, the `rm`
/// request/reply loop, throttling, and the timeout fallback. Uses an isolated
/// temp game-data root per test and the no-timer test ctor so nothing depends
/// on a real Avalonia dispatcher.
/// </summary>
public sealed class ParadigmPositionResolverTests : IDisposable
{
    private readonly string _root;

    public ParadigmPositionResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-paradigm-resync-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Void",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private sealed record Harness(
        ParadigmPositionResolver Resolver,
        RoomTracker Tracker,
        EngineRecoveryGate Gate,
        List<byte[]> Sent);

    private Harness Build(bool paradigm)
    {
        string dir = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Rooms.json"), GraphJson);
        // Legit == 2 flags the set as ParaMud; omit it for a stock realm.
        if (paradigm)
            File.WriteAllText(Path.Combine(dir, "Info.json"), "[{\"Legit\":2}]");

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        EngineRecoveryGate gate = new(graph, tracker);

        MessageRouter router = new();
        FujinTerm.Services.Patterns.DefaultPatterns.Seed(router);   // Subscribe needs the pattern in the catalog
        ParadigmPositionResolver resolver = new(router, tracker, gate, cache, log: null, useTimer: false);
        List<byte[]> sent = new();
        resolver.SetWireSender(sent.Add);
        return new Harness(resolver, tracker, gate, sent);
    }

    [Fact]
    public void Stock_TryRequestResync_ReturnsFalse_AndSendsNothing()
    {
        Harness h = Build(paradigm: false);

        Assert.False(h.Resolver.TryRequestResync("drift"));
        Assert.Empty(h.Sent);
        Assert.False(h.Resolver.RequestInFlight);
        Assert.False(h.Resolver.Enabled);
    }

    [Fact]
    public void Paradigm_FirstRequest_SendsRm_AndFlagsInFlight()
    {
        Harness h = Build(paradigm: true);

        Assert.True(h.Resolver.TryRequestResync("drift"));
        Assert.Equal("rm\r", Encoding.Latin1.GetString(Assert.Single(h.Sent)));
        Assert.True(h.Resolver.RequestInFlight);
        Assert.True(h.Resolver.Enabled);
    }

    [Fact]
    public void Paradigm_RequestWhileInFlight_Coalesces_NoSecondSend()
    {
        Harness h = Build(paradigm: true);

        Assert.True(h.Resolver.TryRequestResync("first"));
        Assert.True(h.Resolver.TryRequestResync("second"));   // coalesced onto the in-flight one
        Assert.Single(h.Sent);
    }

    [Fact]
    public void Paradigm_LocationWhileInFlight_LocatesTracker_ClearsInFlight()
    {
        Harness h = Build(paradigm: true);
        h.Resolver.TryRequestResync("drift");

        h.Resolver.FeedLocationForTests(1, 1);

        Assert.False(h.Resolver.RequestInFlight);
        Assert.Equal(new RoomKey(1, 1), h.Resolver.LastResolved);
        Assert.Equal(new RoomKey(1, 1), h.Tracker.State.CurrentRoom?.Key);
        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
    }

    [Fact]
    public void Paradigm_UnsolicitedLocation_StillLatchesTracker()
    {
        // A user typing `rm` by hand after a bonk lost the heuristic position:
        // no request is in flight, but `rm` is authoritative so the reply must
        // still re-anchor the tracker.
        Harness h = Build(paradigm: true);

        h.Resolver.FeedLocationForTests(1, 1);

        Assert.False(h.Resolver.RequestInFlight);
        Assert.Equal(new RoomKey(1, 1), h.Resolver.LastResolved);
        Assert.Equal(new RoomKey(1, 1), h.Tracker.State.CurrentRoom?.Key);
        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
    }

    [Fact]
    public void Paradigm_RequestAfterReply_IsThrottled_NoSecondSend()
    {
        Harness h = Build(paradigm: true);
        h.Resolver.TryRequestResync("first");
        h.Resolver.FeedLocationForTests(1, 1);   // clears in-flight, but the throttle window is still open

        Assert.False(h.Resolver.TryRequestResync("second"));
        Assert.Single(h.Sent);
    }

    [Fact]
    public void Paradigm_Timeout_FiresResyncFailed_AndClearsInFlight()
    {
        Harness h = Build(paradigm: true);
        bool failed = false;
        h.Resolver.ResyncFailed += () => failed = true;
        h.Resolver.TryRequestResync("drift");

        h.Resolver.FireTimeoutForTests();

        Assert.True(failed);
        Assert.False(h.Resolver.RequestInFlight);
    }
}
