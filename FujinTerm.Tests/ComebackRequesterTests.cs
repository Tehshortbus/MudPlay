using System.IO;
using System.Text;
using FujinTerm.Game.Map;
using FujinTerm.Game.Remote;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 6.2 — follower-side <see cref="ComebackRequester"/>. A movement-
/// failure line (prevents-movement flag / over-encumbered) landing just
/// before <c>"You are no longer following X."</c> is the signature of
/// being left behind → telepath <c>@comeback</c> to the leader. A
/// "no longer following" with no preceding failure is a deliberate
/// uninvite / unfollow → stay silent.
/// </summary>
public sealed class ComebackRequesterTests : IDisposable
{
    private readonly string _root;

    public ComebackRequesterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-comebackreq-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private sealed class Harness : IDisposable
    {
        public required MessageRouter Router { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required ComebackRequester Requester { get; init; }

        private DateTimeOffset _clock = new(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);

        /// <summary>Advance the deterministic clock the requester reads.</summary>
        public void Advance(TimeSpan by) => _clock += by;

        public DateTimeOffset Now => _clock;

        public string? LastWire => Requester.LastSentForTests.Count == 0
            ? null
            : Encoding.Latin1.GetString(Requester.LastSentForTests[^1]).TrimEnd('\r');

        public void Feed(string text) =>
            Router.Dispatch(new LineExtractor.EmittedLine(
                text, new CellAttributes[text.Length], _clock, IsPromptLine: false));

        public void Dispose() => Requester.Dispose();
    }

    private Harness NewHarness()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);

        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);

        ComebackRequester requester = new(router, tracker);
        Harness h = new()
        {
            Router = router,
            Tracker = tracker,
            Requester = requester,
        };
        requester.NowProvider = () => h.Now;
        requester.SetWireSender(_ => { });
        return h;
    }

    // ----- left-behind fires ------------------------------------------

    [Fact]
    public void StuckThenNoLongerFollowing_SendsComeback()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1), h.Now);

        h.Feed("You can't seem to move anywhere!");
        h.Feed("You are no longer following Fujin.");

        Assert.Equal("/Fujin @comeback 1/1", h.LastWire);
    }

    [Fact]
    public void HeavyThenNoLongerFollowing_SendsComeback()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1), h.Now);

        h.Feed("Your pack is too heavy to move.");
        h.Feed("You are no longer following Fujin.");

        Assert.Equal("/Fujin @comeback 1/1", h.LastWire);
    }

    [Fact]
    public void Confirmed_NoRoom_SendsBareComeback()
    {
        using Harness h = NewHarness();
        // No SetLocated → tracker confidence stays Unknown → bare @comeback.
        h.Feed("You can't seem to move anywhere!");
        h.Feed("You are no longer following Fujin.");

        Assert.Equal("/Fujin @comeback", h.LastWire);
    }

    // ----- deliberate unfollow stays silent ---------------------------

    [Fact]
    public void NoLongerFollowing_WithoutFailure_StaysSilent()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1), h.Now);

        // Deliberate uninvite / our own unfollow — no movement failure first.
        h.Feed("You are no longer following Fujin.");

        Assert.Null(h.LastWire);
    }

    [Fact]
    public void FailureTooLongAgo_StaysSilent()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1), h.Now);

        h.Feed("You can't seem to move anywhere!");
        h.Advance(TimeSpan.FromSeconds(10));     // past the 3s window
        h.Feed("You are no longer following Fujin.");

        Assert.Null(h.LastWire);
    }

    [Fact]
    public void QuotedHeavyChatLine_DoesNotArm()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1), h.Now);

        // A player gossiping the phrase is quoted — the ^[^"]* anchor
        // means it never matches MovementFailedHeavy, so no arming.
        h.Feed("Raijin gossips \"my pack is too heavy to move lol\".");
        h.Feed("You are no longer following Fujin.");

        Assert.Null(h.LastWire);
    }

    // ----- disabled / consume guards ----------------------------------

    [Fact]
    public void Disabled_DetectsButDoesNotSend()
    {
        using Harness h = NewHarness();
        h.Requester.Enabled = false;
        h.Tracker.SetLocated(new RoomKey(1, 1), h.Now);

        h.Feed("You can't seem to move anywhere!");
        h.Feed("You are no longer following Fujin.");

        Assert.Null(h.LastWire);
    }

    [Fact]
    public void FailureIsConsumed_SecondUnfollowStaysSilent()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(new RoomKey(1, 1), h.Now);

        h.Feed("You can't seem to move anywhere!");
        h.Feed("You are no longer following Fujin.");
        Assert.Equal("/Fujin @comeback 1/1", h.LastWire);

        int sentCount = h.Requester.LastSentForTests.Count;
        // A later, unrelated unfollow must not reuse the consumed failure.
        h.Feed("You are no longer following Raijin.");
        Assert.Equal(sentCount, h.Requester.LastSentForTests.Count);
    }
}
