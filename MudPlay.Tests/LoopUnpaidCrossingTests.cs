using System.Text;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A loop whose circuit crosses a toll the purse can't pay, the loop runner's side of
// TollPurseDoubtTests. Such a loop is not started, and one refused mid-lap stops,
// each naming the toll the way a walk-to does (user, 2026-10-10); where a way round
// exists between the two waypoints the loop takes it. A loop whose every leg needed
// the toll used to expand to no steps and index into them, which threw out of the
// line pump and took the client down.
public sealed class LoopUnpaidCrossingTests : IDisposable
{
    private const long Plenty = 2_059_900;
    private const string Refusal = "You do not have enough to cover the toll of 5 gold crowns.";

    private readonly string _root;

    public LoopUnpaidCrossingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-loop-toll-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // The commoner loop shape: some legs cross the toll and others don't.
    //
    //   1/1 Gates ──E── 1/2 Bailey ─E (Toll: 5)─ 1/3 Road ──E── 1/4 Field
    private const string FourInARowJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Gates",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/2", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Bailey",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/3 (Toll: 5)", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Road",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/4", "W": "1/2 (Toll: 5)",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Field",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // 1/1 ──N── 1/2 ──N── 1/3: with 1/2 avoided, neither leg of a 1/1-1/3 loop has
    // a route.
    private const string LineJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "C",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private sealed class AvoidOneRoom(RoomKey avoided) : IRoomFilter
    {
        public bool IsAvoided(RoomKey key) => key.Equals(avoided);
    }

    private sealed class Rig
    {
        public required RoomTracker Tracker { get; init; }
        public required LoopRunner Runner { get; init; }
        public required MovementRefusalDetector Detector { get; init; }
        // The game's output, fed the way the client receives it.
        public required ServerFeed Feed { get; init; }
        public MovementFilter? Filter { get; init; }
        public List<string> Sent { get; } = new();
        public List<LoopEvent> Events { get; } = new();
        public List<string> Unpaid { get; } = new();
        // The `i` AppServices.OnPaidCrossingRefused asks for, and its bound.
        public OwedPurseRead? PurseRead { get; set; }
        public int InventoryAsked { get; set; }
        public Action? AnswerBound { get; set; }
        public long? Purse { get; set; }
        public string? Failure => Events.Where(e => e.Kind == LoopEventKind.Failed).Select(e => e.Detail).SingleOrDefault();

        // The `i` is answered: the record now holds this much, and the gate and the
        // owed read hear of it in AppServices' order.
        public void AnswerInventory(long purse)
        {
            Purse = purse;
            Filter!.NotePurseRead();
            PurseRead!.Settle();
        }
    }

    private sealed class NoTimer : IDisposable
    {
        public void Dispose() { }
    }

    // hearsRefusals false is a client where nothing but the tracker reads the toll
    // line, as before this change.
    private Rig NewRig(string roomsJson, long? purse, bool hearsRefusals = true, IRoomFilter? otherFilter = null)
    {
        string set = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(set);
        File.WriteAllText(Path.Combine(set, "Rooms.json"), roomsJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        MovementFilter? filter = otherFilter is null ? new MovementFilter(new ProfileService()) : null;
        LoopRunner runner = new(tracker, new MovementCoordinator(), graph: graph, bfs: new BfsMapper(graph),
            filter: otherFilter ?? filter, postToUi: act => act());
        ServerFeed feed = new();
        MovementRefusalDetector detector = new(feed.Lines, tracker);
        Rig rig = new() { Tracker = tracker, Runner = runner, Detector = detector, Feed = feed, Filter = filter, Purse = purse };
        if (filter is not null)
        {
            filter.WealthProvider = () => rig.Purse;
            if (hearsRefusals)
            {
                OwedPurseRead purseRead = rig.PurseRead = new OwedPurseRead(
                    held: () => false,
                    send: () => rig.InventoryAsked++,
                    schedule: (_, onBound) =>
                    {
                        rig.AnswerBound = onBound;
                        return new NoTimer();
                    });
                runner.SetPurseReadWait(purseRead.WaitForAnswer);
                detector.PaidCrossingRefused += (crossing, named) =>
                {
                    if (filter.NoteCrossingRefused(crossing, named)) purseRead.Ask();
                };
            }
        }
        runner.SetWireSender(bytes => rig.Sent.Add(Encoding.Latin1.GetString(bytes).Trim()));
        runner.Event += rig.Events.Add;
        runner.SetUnpaidCrossingHandler(rig.Unpaid.Add);
        return rig;
    }

    private static Loop LoopOf(params RoomKey[] rooms) => new("toll", rooms);

    private static readonly RoomKey Bailey = new(1, 2), Road = new(1, 3);

    // ----- not started ------------------------------------------------------------

    [Fact]
    public void ALoop_WhoseLegNeedsATollThePurseCantPay_IsNotStarted_AndNamesTheToll()
    {
        Rig rig = NewRig(TollPurseDoubtTests.TollTheOnlyWayJson, purse: 0);
        rig.Tracker.SetLocated(Bailey);

        Assert.False(rig.Runner.Start(LoopOf(Bailey, Road)));

        Assert.Empty(rig.Sent);
        Assert.Equal(LoopState.Idle, rig.Runner.State);
        Assert.Equal(
            "loop 'toll': no way from 1/2 to 1/3 without a toll east from 1/2 (Bailey) (5 gold) you can't pay: "
            + "you carry 0 copper, 5 gold short",
            rig.Failure);
        Assert.Equal(rig.Failure, Assert.Single(rig.Unpaid));
        // Not started at all: no session reset, no party @reset.
        Assert.DoesNotContain(rig.Events, e => e.Kind is LoopEventKind.Started or LoopEventKind.ReachedFirstWaypoint);
    }

    [Fact]
    public void ALoop_StartedAfterADeath_OverATollLeg_IsNotStarted()
    {
        Rig rig = NewRig(TollPurseDoubtTests.TollTheOnlyWayJson, Plenty);
        rig.Filter!.NotePurseLostAtDeath();
        rig.Tracker.SetLocated(Bailey);

        Assert.False(rig.Runner.Start(LoopOf(Bailey, Road)));

        Assert.Empty(rig.Sent);
        Assert.Contains("a toll east from 1/2 (Bailey) (5 gold) you can't pay", rig.Failure);
        Assert.Contains("went with the deathpile", rig.Failure);
        Assert.Single(rig.Unpaid);
    }

    // What the route prompt asks before it walks anyone to the loop.
    [Fact]
    public void ALoopsRefusal_IsKnownBeforeTheWalkToIt()
    {
        Rig rig = NewRig(TollPurseDoubtTests.TollTheOnlyWayJson, purse: 0);

        Assert.Contains("you can't pay", rig.Runner.RefusalFor(LoopOf(Bailey, Road)));

        rig.Purse = 500;
        Assert.Null(rig.Runner.RefusalFor(LoopOf(Bailey, Road)));
    }

    // ----- refused mid-lap --------------------------------------------------------

    [Fact]
    public void ALoop_RefusedAtATollTheRecordCovered_ReadsThePurse_ThenStops_NamingTheToll_OnOneSend()
    {
        Rig rig = NewRig(TollPurseDoubtTests.TollTheOnlyWayJson, Plenty);
        rig.Tracker.SetLocated(Bailey);
        Assert.True(rig.Runner.Start(LoopOf(Bailey, Road)));
        Assert.Equal(new[] { "e" }, rig.Sent);

        rig.Feed.Line(Refusal);
        Assert.Equal(LoopState.Recovering, rig.Runner.State);    // the reroute waits for the read
        Assert.Equal(1, rig.InventoryAsked);

        rig.AnswerInventory(200);

        Assert.Equal(new[] { "e" }, rig.Sent);
        Assert.Equal(1, rig.InventoryAsked);
        Assert.Equal(LoopState.Idle, rig.Runner.State);
        Assert.Equal(
            "loop 'toll': no way from 1/2 to 1/3 without a toll east from 1/2 (Bailey) (5 gold) you can't pay: "
            + "you carry 2 gold, 3 gold short",
            rig.Failure);
        Assert.Equal(rig.Failure, Assert.Single(rig.Unpaid));
    }

    // The read never answers: past its bound the loop re-plans on the purse in
    // doubt, and stops saying so.
    [Fact]
    public void ALoop_RefusedAtAToll_WhoseReadNeverAnswers_StopsWhenTheBoundPasses()
    {
        Rig rig = NewRig(TollPurseDoubtTests.TollTheOnlyWayJson, Plenty);
        rig.Tracker.SetLocated(Bailey);
        Assert.True(rig.Runner.Start(LoopOf(Bailey, Road)));
        rig.Feed.Line(Refusal);

        rig.AnswerBound!();

        Assert.Equal(new[] { "e" }, rig.Sent);
        Assert.Equal(LoopState.Idle, rig.Runner.State);
        Assert.Contains("a toll east from 1/2 (Bailey) (5 gold) you can't pay", rig.Failure);
        Assert.Contains("the game refused a toll or fare the client took the purse to cover", rig.Failure);
    }

    [Fact]
    public void ALoop_RefusedAtAToll_WithAPurseNobodyHasRead_Stops()
    {
        Rig rig = NewRig(TollPurseDoubtTests.TollTheOnlyWayJson, purse: null);
        rig.Tracker.SetLocated(Bailey);
        Assert.True(rig.Runner.Start(LoopOf(Bailey, Road)));

        rig.Feed.Line(Refusal);
        rig.AnswerInventory(0);

        Assert.Equal(new[] { "e" }, rig.Sent);
        Assert.Equal(1, rig.InventoryAsked);
        Assert.Equal(LoopState.Idle, rig.Runner.State);
        Assert.Single(rig.Unpaid);
    }

    // A loop over toll A (a way round) and toll B (none), with a record of plenty
    // and 2 gold really carried: enough for B, not for A. Re-planned ahead of the
    // read, every toll was closed and the loop stopped naming B, which it could pay.
    [Fact]
    public void ALoop_RefusedAtOneToll_RePlansOnThePurseAsRead_AndKeepsTheTollItCanPay()
    {
        Rig rig = NewRig(TollPurseDoubtTests.TwoTollsJson, Plenty);
        RoomKey bailey = new(1, 1), road = new(1, 2), far = new(1, 5);
        rig.Tracker.SetLocated(bailey);
        Assert.True(rig.Runner.Start(LoopOf(bailey, road, far)));
        Assert.Equal(new[] { "e" }, rig.Sent);

        rig.Feed.Line(Refusal);
        rig.AnswerInventory(200);

        Assert.Equal(new[] { "e", "n" }, rig.Sent);         // round A; B stays on the circuit
        Assert.Equal(LoopState.Running, rig.Runner.State);
        Assert.Null(rig.Failure);
        Assert.Empty(rig.Unpaid);
    }

    // A loop stopped while its reroute waits is not started again by the answer.
    [Fact]
    public void ALoop_StoppedWhileItWaitsForTheRead_StaysStopped()
    {
        Rig rig = NewRig(TollPurseDoubtTests.TollWithAWayRoundJson, Plenty);
        RoomKey bailey = new(1, 1), road = new(1, 2);
        rig.Tracker.SetLocated(bailey);
        Assert.True(rig.Runner.Start(LoopOf(bailey, road)));
        rig.Feed.Line(Refusal);

        rig.Runner.Stop("test");
        rig.AnswerInventory(0);

        Assert.Equal(new[] { "e" }, rig.Sent);
        Assert.Equal(LoopState.Idle, rig.Runner.State);
    }

    // Leading a party, our own purse plenty and the party-toll check's verdict that
    // not everyone gets through (a follower short, and more than we can spare):
    // with no way round, the loop is refused for the toll, naming who can't pay
    // (user, 2026-10-10). Until that ruling a follower's short purse left the loop
    // with no toll refusal and the plain "no leg" failure.
    [Fact]
    public void ALoop_OverATollThePartyCantAllPay_IsRefusedNamingWho()
    {
        Rig rig = NewRig(TollPurseDoubtTests.TollTheOnlyWayJson, Plenty);
        rig.Filter!.PartyTollClosedProbe = _ => true;
        rig.Filter.PartyTollClosedReason = _ => "Bob holds 0 copper, 5 gold short";
        rig.Tracker.SetLocated(Bailey);

        const string refusal =
            "loop 'toll': no way from 1/2 to 1/3 without a toll east from 1/2 (Bailey) (5 gold) "
            + "the party can't all pay: Bob holds 0 copper, 5 gold short";
        Assert.Equal(refusal, rig.Runner.RefusalFor(LoopOf(Bailey, Road)));
        Assert.False(rig.Runner.Start(LoopOf(Bailey, Road)));

        Assert.Equal(refusal, rig.Failure);
        Assert.Equal(new[] { refusal }, rig.Unpaid);
    }

    // The coin ran out part-way round, and the record knows it: with nothing reading
    // the refusal the re-plan expanded to no steps, and the first of them was
    // indexed.
    [Fact]
    public void ALoop_WhoseCoinRanOutMidLap_Stops_AndDoesNotThrow()
    {
        Rig rig = NewRig(TollPurseDoubtTests.TollTheOnlyWayJson, Plenty, hearsRefusals: false);
        rig.Tracker.SetLocated(Bailey);
        Assert.True(rig.Runner.Start(LoopOf(Bailey, Road)));

        rig.Purse = 0;
        rig.Feed.Line(Refusal);

        Assert.Equal(new[] { "e" }, rig.Sent);
        Assert.Equal(LoopState.Idle, rig.Runner.State);
        Assert.Contains("you carry 0 copper, 5 gold short", rig.Failure);
        Assert.Single(rig.Unpaid);
    }

    // Four waypoints, the toll between the second and third: the step went out four
    // times and the loop failed "recovery exhausted" with no toll named.
    [Fact]
    public void ALoop_WithSomeLegsOverTheToll_RefusedThere_StopsAtTheFirstRefusal()
    {
        Rig rig = NewRig(FourInARowJson, Plenty);
        rig.Tracker.SetLocated(Bailey);
        Assert.True(rig.Runner.Start(LoopOf(new RoomKey(1, 1), Bailey, Road, new RoomKey(1, 4))));
        Assert.Equal(new[] { "e" }, rig.Sent);

        rig.Feed.Line(Refusal);
        rig.AnswerInventory(200);

        Assert.Equal(new[] { "e" }, rig.Sent);
        Assert.Equal(1, rig.InventoryAsked);
        Assert.Equal(LoopState.Idle, rig.Runner.State);
        Assert.StartsWith(
            "loop 'toll': no way from 1/2 to 1/3 without a toll east from 1/2 (Bailey) (5 gold) you can't pay",
            rig.Failure);
        Assert.Single(rig.Unpaid);
    }

    [Fact]
    public void ALoop_RefusedAtAToll_WithAWayRoundBetweenTheWaypoints_TakesIt()
    {
        Rig rig = NewRig(TollPurseDoubtTests.TollWithAWayRoundJson, Plenty);
        RoomKey bailey = new(1, 1), road = new(1, 2);
        rig.Tracker.SetLocated(bailey);
        Assert.True(rig.Runner.Start(LoopOf(bailey, road)));
        Assert.Equal(new[] { "e" }, rig.Sent);

        rig.Feed.Line(Refusal);
        rig.AnswerInventory(200);

        Assert.Equal(new[] { "e", "n" }, rig.Sent);
        Assert.Equal(LoopState.Running, rig.Runner.State);
        Assert.Null(rig.Failure);
        Assert.Empty(rig.Unpaid);
    }

    // ----- nothing to walk, whatever the reason ----------------------------------

    [Fact]
    public void ALoop_WithNoLegThatCanBeWalked_Fails_AndDoesNotThrow()
    {
        Rig rig = NewRig(LineJson, purse: null, otherFilter: new AvoidOneRoom(new RoomKey(1, 2)));
        rig.Tracker.SetLocated(new RoomKey(1, 1));

        // False: the caller is told the loop never ran (it used to hear true, and
        // went on as if a run had begun).
        Assert.False(rig.Runner.Start(LoopOf(new RoomKey(1, 1), new RoomKey(1, 3))));

        Assert.Empty(rig.Sent);
        Assert.Equal(LoopState.Idle, rig.Runner.State);
        Assert.Equal("loop 'toll' has no leg that can be walked: no route from 1/1 to 1/3", rig.Failure);
        Assert.Empty(rig.Unpaid);
        Assert.DoesNotContain(rig.Events, e => e.Kind is LoopEventKind.Started or LoopEventKind.ReachedFirstWaypoint);
    }
}
