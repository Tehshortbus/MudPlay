using System.IO;
using System.Text;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

public sealed class HiddenExitRevealManagerTests : IDisposable
{
    private readonly string _root;

    public HiddenExitRevealManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-hidden-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // Two-room fixture: 1/1 normally has no N exit; 1/2 with N back.
    // To test the reveal we swap the graph during the test (simulate
    // the server's mid-search room redisplay carrying the new exit).
    private const string GraphNoExit = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Cliff",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Cave",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private const string GraphWithExit = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Cliff",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2 (Hidden)", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Cave",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private sealed class Harness
    {
        public GameDataCache Cache { get; }
        public RoomGraphManager Graph { get; }
        public RoomTracker Tracker { get; }
        public MessageRouter Router { get; }
        public HiddenExitRevealManager Mgr { get; }
        public List<byte[]> Sent { get; } = new();
        public int MaxAttempts { get; set; } = 5;

        public Harness(string root, bool withRouter = false)
        {
            Directory.CreateDirectory(Path.Combine(root, "alpha"));
            File.WriteAllText(Path.Combine(root, "alpha", "Rooms.json"), GraphNoExit);
            Cache = new GameDataCache(root);
            Cache.SwitchSet("alpha");
            Graph = new RoomGraphManager(Cache);
            Graph.OnActiveSetChanged("alpha");
            Tracker = new RoomTracker(Graph);
            Router = new MessageRouter();
            if (withRouter) DefaultPatterns.Seed(Router);
            Mgr = new HiddenExitRevealManager(
                Tracker, () => MaxAttempts,
                router: withRouter ? Router : null);
            Mgr.SetWireSender(Sent.Add);
        }

        public void FeedLine(string text)
        {
            Router.Dispatch(new LineExtractor.EmittedLine(
                text,
                new FujinTerm.Terminal.CellAttributes[text.Length],
                DateTimeOffset.UtcNow,
                IsPromptLine: false));
        }
    }

    [Fact]
    public void Enqueue_SendsSeaFirstAttempt()
    {
        Harness h = new(_root);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Mgr.Enqueue(Direction.N, "walker", _ => { });

        Assert.Single(h.Sent);
        Assert.Equal("sea n\r", Encoding.Latin1.GetString(h.Sent[0]));
    }

    [Fact]
    public void Reveal_OnRoomObservationWithExit_FiresRevealedCallback()
    {
        Harness h = new(_root);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        HiddenSearchResult? result = null;
        h.Mgr.Enqueue(Direction.N, "walker", r => result = r);

        // Swap the graph to one where 1/1 has the N exit, then trigger
        // a room observation that lands us at the same key but with
        // the updated exit map.
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphWithExit);
        h.Cache.EvictAll();
        h.Graph.OnActiveSetChanged("alpha");
        h.Tracker.SetLocated(new RoomKey(1, 1));

        Assert.IsType<HiddenSearchResult.Revealed>(result);
    }

    [Fact]
    public void Reveal_RetriesUpToCap_ThenFails()
    {
        Harness h = new(_root) { MaxAttempts = 3 };
        h.Tracker.SetLocated(new RoomKey(1, 1));
        HiddenSearchResult? result = null;
        h.Mgr.Enqueue(Direction.N, "walker", r => result = r);

        Assert.Single(h.Sent);

        // Each observation without the exit triggers another sea
        // attempt — until the cap. We retrigger by re-locating to
        // the same room (same key, no exit) which fires StateChanged.
        for (int i = 0; i < 2; i++)
        {
            h.Tracker.SetLocated(new RoomKey(1, 1));
        }
        Assert.Equal(3, h.Sent.Count);                  // 3 attempts (cap)

        // Next observation past the cap → terminal failure.
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.IsType<HiddenSearchResult.Failed>(result);
    }

    [Fact]
    public void Reveal_OnSearchSucceededPattern_FiresRevealed_WhenServerDoesNotRedisplayRoom()
    {
        // Live bug: server replies "You found an exit downwards!" but
        // doesn't redisplay the room. Tracker fires no StateChanged →
        // the manager's tracker-based check waited forever and the
        // walker stalled. Now the explicit UserSearchSucceeded pattern
        // resolves the request.
        Harness h = new(_root, withRouter: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        HiddenSearchResult? result = null;
        h.Mgr.Enqueue(Direction.D, "walker", r => result = r);

        h.FeedLine("You found an exit downwards!");

        Assert.IsType<HiddenSearchResult.Revealed>(result);
    }

    [Fact]
    public void Reveal_OnSearchSucceededPattern_CardinalForm_AlsoFiresRevealed()
    {
        Harness h = new(_root, withRouter: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        HiddenSearchResult? result = null;
        h.Mgr.Enqueue(Direction.N, "walker", r => result = r);

        h.FeedLine("You found an exit to the north!");

        Assert.IsType<HiddenSearchResult.Revealed>(result);
    }

    [Fact]
    public void Reveal_OnSearchSucceededPattern_DifferentDirection_DoesNotResolve()
    {
        // User typed `sea n` for their own reasons while the walker had
        // an "sea d" in flight. The cardinal success for N must not
        // resolve the D request.
        Harness h = new(_root, withRouter: true);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        HiddenSearchResult? result = null;
        h.Mgr.Enqueue(Direction.D, "walker", r => result = r);

        h.FeedLine("You found an exit to the north!");

        Assert.Null(result);
    }

    [Fact]
    public void Reveal_OnSearchFailedPattern_TriggersRetry_UntilCap()
    {
        Harness h = new(_root, withRouter: true) { MaxAttempts = 3 };
        h.Tracker.SetLocated(new RoomKey(1, 1));
        HiddenSearchResult? result = null;
        h.Mgr.Enqueue(Direction.N, "walker", r => result = r);
        Assert.Single(h.Sent);

        // Two failures → two retries → 3 total attempts.
        h.FeedLine("You notice nothing different to the north.");
        h.FeedLine("You notice nothing different to the north.");
        Assert.Equal(3, h.Sent.Count);

        // Third failure exhausts the cap.
        h.FeedLine("You notice nothing different to the north.");
        Assert.IsType<HiddenSearchResult.Failed>(result);
    }

    [Fact]
    public void Reveal_OnSearchFailedPattern_VerticalForm_TriggersRetry()
    {
        // The up/down miss uses "above you" / "below you", not the cardinal
        // "to the <dir>" form. When the failure regex only matched the cardinal
        // form, an up/down "sea" never registered as a miss, so the clean
        // pattern-driven retry never fired and the walker fell into a stall
        // loop (report paradigm-20260714-121106). "above you" is confirmed on
        // the wire; "below you" is the symmetric down form.
        Harness h = new(_root, withRouter: true) { MaxAttempts = 3 };
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.Mgr.Enqueue(Direction.U, "walker", _ => { });
        Assert.Single(h.Sent);

        h.FeedLine("You notice nothing different above you.");
        Assert.Equal(2, h.Sent.Count);

        h.FeedLine("You notice nothing different below you.");
        Assert.Equal(3, h.Sent.Count);
    }

    [Fact]
    public void StopAll_AbortsCurrent_RepliesFailed()
    {
        Harness h = new(_root);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        HiddenSearchResult? result = null;
        h.Mgr.Enqueue(Direction.N, "walker", r => result = r);

        h.Mgr.StopAll();

        Assert.IsType<HiddenSearchResult.Failed>(result);
        Assert.Equal(0, h.Mgr.QueueDepth);
    }
}
