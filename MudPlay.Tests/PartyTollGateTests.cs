using System.Text;
using MudPlay.Game;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// A party at a toll exit (user, 2026-10-10): the leader's engine asks every
// follower's purse before stepping through, hands coin to whoever is short or
// silent when it can spare it, and otherwise goes round or ends the walk saying
// who can't pay. The real walker, loop runner, movement filter, wealth tracker and
// @wealth probe, wired as AppServices wires them.
//
//   way round:  1/1 Bailey ─E (Toll: 5)─ 1/2 Road, and 1/1 ─N─ 1/3 Lane ─E─ 1/4 Bridge ─S─ 1/2
//   only way:   1/1 Gates ─E─ 1/2 Bailey ─E (Toll: 5)─ 1/3 Road
public sealed class PartyTollGateTests : IDisposable
{
    private const long Toll = 500;

    private readonly string _root;

    public PartyTollGateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-party-toll-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private sealed class NoTimer : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class Rig
    {
        public required PartyState Party { get; init; }
        public required MessageRouter Router { get; init; }
        public required PartyWealthTracker Wealth { get; init; }
        public required PartyTollGate Gate { get; init; }
        public required MovementFilter Filter { get; init; }
        public required MovementCoordinator Coordinator { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public required LoopRunner Runner { get; init; }

        // The moves the walker or the loop sent, the @wealth telepaths, and the
        // give commands, each as text.
        public List<string> Moves { get; } = new();
        public List<string> Asked { get; } = new();
        public List<string> Gives { get; } = new();
        // The @wealth reply windows armed, and the hand-over's bound.
        public List<Action> Windows { get; } = new();
        public Action? GiveWindow { get; set; }
        public List<WalkEvent> WalkEvents { get; } = new();
        public List<LoopEvent> LoopEvents { get; } = new();
        public List<string> Notices { get; } = new();
        // What the check said it was handing over, for the terminal.
        public List<string> HandOverNotices { get; } = new();

        public DateTime Now { get; set; } = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        public int? Gold { get; set; } = 10;
        public bool MasterOff { get; set; }
        // Settings → Cash + Items: "Fund party members' tolls / rides if short"
        // (off until ticked) and the keep-on-hand floor, in copper.
        // The check holds both as values, as AppServices hands them to it.
        public bool Fund
        {
            get => Gate.FundsShortMembers;
            set => Gate.FundsShortMembers = value;
        }
        public long KeepOnHand
        {
            get => Gate.KeepOnHandCopper;
            set => Gate.KeepOnHandCopper = value;
        }
        public Func<string, RoomKey, RoomKey, bool> WentBack { get; set; } = (_, _, _) => false;

        // A purse of mixed coins, in place of Gold.
        public CurrencyHoldings? Coins { get; set; }
        // The members the comeback manager knows are not with us: nobody, unless a test says.
        public HashSet<string> LeftBehind { get; } = new(StringComparer.OrdinalIgnoreCase);

        public long? Purse => Coins?.TotalCopperValue ?? (Gold is { } gold ? gold * 100L : null);
        public CurrencyHoldings? Holdings =>
            Coins ?? (Gold is { } gold ? new CurrencyHoldings(0, 0, gold, 0, 0, gold * 100L) : null);

        // A telepath from a member that is not an answer to anything.
        public void Chats(string name, string words)
        {
            string line = $"{name} telepaths: {words}";
            Router.Dispatch(new LineExtractor.EmittedLine(
                line, new CellAttributes[line.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false));
        }

        public void Follower(string name) => Party.Members.Add(new PartyMember { Name = name });

        public void Lead()
        {
            Party.IsInParty = true;
            Party.SelfIsLeader = true;
        }

        // A member's answer to @wealth, as our own client words it.
        public void Says(string name, long copper)
        {
            string line = copper == 0
                ? $"{name} telepaths: no coins on hand"
                : $"{name} telepaths: some coins (= {copper} copper)";
            Router.Dispatch(new LineExtractor.EmittedLine(
                line, new CellAttributes[line.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false));
        }

        // The game confirms a hand-over, and the coins leave our purse.
        public void Confirms(string name, int gold)
        {
            Gold -= gold;
            Gate.OnCoinsGivenAway(name, gold * 100L);
        }

        // Every decision the toll check has made, one to a line.
        public string Decided => string.Join("\n", Gate.Decisions);

        public string? WalkFailure =>
            WalkEvents.Where(e => e.Kind == WalkEventKind.Failed).Select(e => e.Detail).SingleOrDefault();
        public string? LoopFailure =>
            LoopEvents.Where(e => e.Kind == LoopEventKind.Failed).Select(e => e.Detail).SingleOrDefault();
    }

    // monstersJson and tbInfoJson give the graph an NPC's paid transport;
    // tbInfoJson and spellsJson a sailing, and the walker its boat planner.
    private Rig NewRig(string roomsJson, string? monstersJson = null, string? tbInfoJson = null, string? spellsJson = null)
    {
        string set = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(set);
        File.WriteAllText(Path.Combine(set, "Rooms.json"), roomsJson);
        if (monstersJson is not null) File.WriteAllText(Path.Combine(set, "Monsters.json"), monstersJson);
        if (tbInfoJson is not null) File.WriteAllText(Path.Combine(set, "TBInfo.json"), tbInfoJson);
        if (spellsJson is not null) File.WriteAllText(Path.Combine(set, "Spells.json"), spellsJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        TBInfoStore? tbInfo = null;
        if (tbInfoJson is not null)
        {
            tbInfo = new TBInfoStore(cache);
            tbInfo.OnActiveSetChanged("alpha");
        }
        RoomGraphManager graph = new(cache, log: null, tbinfo: tbInfo,
            spellCatalog: spellsJson is null ? null : new MudPlay.Game.Spells.KnownSpellCatalog(cache));
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementFilter filter = new(new ProfileService());
        MovementCoordinator coordinator = new();

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PartyBroadcaster broadcaster = new(party);

        Rig? made = null;
        PartyWealthTracker? wealthRef = null;
        PartyWealthProbe probe = new(
            broadcaster, chat, party,
            armWindow: onElapsed => made!.Windows.Add(onElapsed),
            recordWealth: (name, copper) => wealthRef!.Record(name, copper),
            log: null);
        PartyWealthTracker wealth = wealthRef = new(
            party, probe,
            selfWealth: () => made!.Purse,
            post: act => act(),
            clock: () => made!.Now,
            log: null);
        wealth.MasterSwitchOff = () => made!.MasterOff;

        PartyTollGate gate = new(
            wealth,
            ownPurse: () => made!.Purse,
            reservedCopper: () => filter.ReservedCopper,
            holdings: () => made!.Holdings,
            runicName: () => "runic",
            send: cmd => made!.Gives.Add(cmd),
            assertGate: reason => coordinator.AssertGate(MovementCoordinator.PartyTollGate, "test", reason),
            clearGate: reason => coordinator.ClearGate(MovementCoordinator.PartyTollGate, "test", reason),
            schedule: (_, onElapsed) =>
            {
                made!.GiveWindow = onElapsed;
                return new NoTimer();
            },
            roomName: key => graph.GetRoom(key)?.Name,
            log: null,
            clock: () => made!.Now)
        {
            MasterSwitchOff = () => made!.MasterOff,
            FundsShortMembers = new MudPlay.Models.Profile.CashSettings().FundPartyTolls,
            Notice = text => made!.HandOverNotices.Add(text),
            WentBackFor = (name, from, to) => made!.WentBack(name, from, to),
        };

        AutoWalkManager walker = new(graph, bfs, tracker, coordinator, filter);
        if (spellsJson is not null) walker.SetBoatPlanner(new BoatRoutePlanner(graph, bfs));
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs, filter: filter, postToUi: act => act());

        Rig rig = made = new()
        {
            Party = party, Router = router, Wealth = wealth, Gate = gate, Filter = filter,
            Coordinator = coordinator, Tracker = tracker, Walker = walker, Runner = runner,
        };
        broadcaster.SetWireSender(bytes => rig.Asked.Add(Encoding.Latin1.GetString(bytes).Trim()));

        filter.WealthProvider = () => rig.Purse;
        filter.PartyWealthProvider = wealth.MinWealth;
        filter.WealthWarmProbe = wealth.Probe;
        filter.PartyTollClosedProbe = exit => gate.Closes(in exit);
        filter.PartyTollClosedReason = exit => gate.DescribeClosed(in exit);

        walker.SetWireSender(bytes => rig.Moves.Add(Encoding.Latin1.GetString(bytes).Trim()));
        walker.SetTollStepCheck((from, dir, exit, later) => gate.BeforeTollStep(from, dir, in exit, later));
        walker.SetSailingCheck((passage, later) => gate.BeforeSailing(in passage, later));
        walker.SetUnpaidCrossingHandler((_, reason) => rig.Notices.Add(reason));
        walker.Event += rig.WalkEvents.Add;

        runner.SetWireSender(bytes => rig.Moves.Add(Encoding.Latin1.GetString(bytes).Trim()));
        runner.SetTollStepCheck((from, dir, exit, later) => gate.BeforeTollStep(from, dir, in exit, later));
        runner.SetUnpaidCrossingHandler(rig.Notices.Add);
        runner.Event += rig.LoopEvents.Add;

        gate.KnownLeftBehind = () => rig.LeftBehind.ToArray();
        wealth.RoundSettled += () =>
        {
            gate.RefreshLeftBehind();
            walker.ReplanIfATollAheadIsClosed();
            runner.ReplanIfATollAheadIsClosed();
        };
        tracker.StateChanged += t =>
        {
            if (t.PreviousRoom is { } left && t.NewRoom is { } now && left.Key != now.Key)
                gate.NoteRoomChanged(left, now.Key);
        };
        return rig;
    }

    // A leader with two followers, standing at the toll of the way-round graph.
    private Rig PartyAtTheToll(string roomsJson = TollPurseDoubtTests.TollWithAWayRoundJson, RoomKey? at = null)
    {
        Rig rig = NewRig(roomsJson);
        rig.Follower("Bob");
        rig.Follower("Cal");
        rig.Lead();
        rig.Tracker.SetLocated(at ?? WayRoundBailey);
        return rig;
    }

    // The same party with the Cash setting ticked: the leader funds short members.
    private Rig FundingPartyAtTheToll(string roomsJson = TollPurseDoubtTests.TollWithAWayRoundJson, RoomKey? at = null)
    {
        Rig rig = PartyAtTheToll(roomsJson, at);
        rig.Fund = true;
        return rig;
    }

    private static readonly RoomKey WayRoundBailey = new(1, 1), WayRoundRoad = new(1, 2);
    private static readonly RoomKey OnlyGates = new(1, 1), OnlyBailey = new(1, 2), OnlyRoad = new(1, 3);

    private static RoomObservation Obs(string name, params Direction[] exits) => new(name, new HashSet<Direction>(exits));

    private static readonly string[] BothAsked = ["/Bob @wealth", "/Cal @wealth"];

    // ----- the judgement ---------------------------------------------------------

    private static PartyTollGate.Member Read(string name, long copper, string? unable = null) =>
        new(name, PartyWealthTracker.PurseKnowledge.Read, copper, unable);

    private static PartyTollGate.Member Silent(string name) => new(name, PartyWealthTracker.PurseKnowledge.Silent, 0);

    private static PartyTollGate.Member Unasked(string name) => new(name, PartyWealthTracker.PurseKnowledge.Unasked, 0);

    [Fact]
    public void Judge_EveryoneHoldsTheToll_IsClear()
    {
        PartyTollGate.Judgement judged = PartyTollGate.Judge(Toll, spare: 0, [Read("Bob", 500), Read("Cal", 9_000)]);
        Assert.Equal(PartyTollGate.Outcome.Clear, judged.Outcome);
        Assert.Empty(judged.Needs);
    }

    [Fact]
    public void Judge_AnUnaskedMember_MeansAskFirst_WhateverTheOthersHold()
    {
        PartyTollGate.Judgement judged = PartyTollGate.Judge(Toll, spare: 0, [Read("Bob", 0), Unasked("Cal")]);
        Assert.Equal(PartyTollGate.Outcome.Unverified, judged.Outcome);
    }

    [Fact]
    public void Judge_TheShortGetWhatTheyLack_AndTheSilentTheWholeToll_WhenItCanBeSpared()
    {
        PartyTollGate.Judgement judged = PartyTollGate.Judge(Toll, spare: 800, [Read("Bob", 200), Silent("Cal")]);
        Assert.Equal(PartyTollGate.Outcome.Fund, judged.Outcome);
        Assert.Equal(new[] { ("Bob", 300L), ("Cal", 500L) }, judged.Needs);
    }

    [Fact]
    public void Judge_MoreThanCanBeSpared_AnUnreadPurseOfOurOwn_OrAMemberWhoCantBePaidFor_Closes()
    {
        PartyTollGate.Judgement tooMuch = PartyTollGate.Judge(Toll, spare: 799, [Read("Bob", 200), Silent("Cal")]);
        Assert.Equal(PartyTollGate.Outcome.Closed, tooMuch.Outcome);
        Assert.Equal(
            "Bob holds 2 gold, 3 gold short; Cal gave no answer to @wealth; "
            + "covering that takes 8 gold and you can spare 7 gold 9 silver 9 copper after your own toll",
            tooMuch.Summary);

        Assert.Equal(PartyTollGate.Outcome.Closed, PartyTollGate.Judge(Toll, spare: null, [Read("Bob", 200)]).Outcome);

        PartyTollGate.Judgement unable = PartyTollGate.Judge(Toll, spare: 5_000, [Read("Bob", 200, "can't be paid for")]);
        Assert.Equal(PartyTollGate.Outcome.Closed, unable.Outcome);
        Assert.Contains("Bob can't be paid for", unable.Summary);
        // Unable only matters while they are short.
        Assert.Equal(PartyTollGate.Outcome.Clear,
            PartyTollGate.Judge(Toll, spare: 0, [Read("Bob", 500, "can't be paid for")]).Outcome);
    }

    // ----- all can pay -----------------------------------------------------------

    [Fact]
    public void EveryMemberCanPay_TheStepWaitsForTheirAnswers_ThenGoesThrough()
    {
        Rig rig = PartyAtTheToll();

        Assert.True(rig.Walker.WalkTo(WayRoundRoad));

        // Asked once (the route's own ask and the toll's are one round), and
        // nothing sent toward the toll while the answers are out.
        Assert.Equal(BothAsked, rig.Asked);
        Assert.Empty(rig.Moves);
        Assert.True(rig.Coordinator.IsGateAsserted(MovementCoordinator.PartyTollGate));

        rig.Says("Bob", 500);
        Assert.Empty(rig.Moves);
        rig.Says("Cal", 12_000);

        Assert.Equal(new[] { "e" }, rig.Moves);
        Assert.Empty(rig.Gives);
        Assert.False(rig.Coordinator.IsPaused);
        Assert.Contains("every party member with us can pay", rig.Decided);
    }

    // ----- one short, one silent ---------------------------------------------------

    [Fact]
    public void AShortMember_IsHandedExactlyWhatTheyLack_AndTheStepWaitsForTheGameToConfirmIt()
    {
        Rig rig = FundingPartyAtTheToll();
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 200);
        rig.Says("Cal", 500);

        Assert.Equal(new[] { "give 3 gold to Bob" }, rig.Gives);
        Assert.Empty(rig.Moves);                                  // not before the game says the coin is theirs
        Assert.True(rig.Coordinator.IsGateAsserted(MovementCoordinator.PartyTollGate));

        rig.Confirms("Bob", 3);

        Assert.Equal(new[] { "e" }, rig.Moves);
        Assert.Equal(new[] { "give 3 gold to Bob" }, rig.Gives);
        // What the left-behind judge reads of Bob is what he now holds.
        Assert.Equal(500, rig.Wealth.LastReading("Bob"));
        Assert.Contains("Handing over 3 gold to Bob", rig.Decided);
    }

    [Fact]
    public void ASilentMember_CountsAsUnableToPay_AndIsHandedTheWholeToll()
    {
        Rig rig = FundingPartyAtTheToll();
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 500);
        Assert.Empty(rig.Gives);

        rig.Windows[^1]();                                        // the @wealth window passes without Cal

        Assert.Equal(new[] { "give 5 gold to Cal" }, rig.Gives);
        Assert.Empty(rig.Moves);
        rig.Confirms("Cal", 5);
        Assert.Equal(new[] { "e" }, rig.Moves);
    }

    // ----- cannot cover ------------------------------------------------------------

    [Fact]
    public void WhenWeCantCoverThem_AndAWayRoundExists_TheWalkGoesRound()
    {
        Rig rig = FundingPartyAtTheToll();
        rig.Gold = 6;                                             // our own toll, and 1 gold to spare
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 0);
        rig.Says("Cal", 500);

        Assert.Empty(rig.Gives);
        Assert.Equal(new[] { "n" }, rig.Moves);                   // Lane, Bridge, Road
        Assert.Null(rig.WalkFailure);
        Assert.Empty(rig.Notices);
        Assert.Contains("not taken with the party", rig.Decided);
    }

    [Fact]
    public void WhenWeCantCoverThem_AndThereIsNoWayRound_TheWalkEnds_SayingWhichTollAndWhoIsShort()
    {
        Rig rig = FundingPartyAtTheToll(TollPurseDoubtTests.TollTheOnlyWayJson, OnlyBailey);
        rig.Gold = 6;
        Assert.True(rig.Walker.WalkTo(OnlyRoad));
        rig.Says("Bob", 0);
        rig.Windows[^1]();                                        // Cal never answers

        Assert.Empty(rig.Moves);
        Assert.Empty(rig.Gives);
        Assert.Equal(WalkState.Idle, rig.Walker.State);
        string notice = Assert.Single(rig.Notices);
        Assert.Equal(
            "all routes blocked by a toll east from 1/2 (Bailey) (5 gold) the party can't all pay: "
            + "Bob holds 0 copper, 5 gold short; Cal gave no answer to @wealth; "
            + "covering that takes 10 gold and you can spare 1 gold after your own toll",
            notice);
        Assert.Equal(notice, rig.WalkFailure);
        Assert.False(rig.Coordinator.IsPaused);
    }

    [Fact]
    public void ALoop_WhenWeCantCoverThem_GoesRoundWhereItCan_AndStopsSayingWhyWhereItCant()
    {
        Rig round = FundingPartyAtTheToll();
        round.Gold = 6;
        Assert.True(round.Runner.Start(new Loop("toll", new[] { WayRoundBailey, WayRoundRoad })));
        round.Says("Bob", 0);
        round.Says("Cal", 500);
        Assert.Equal(new[] { "n" }, round.Moves);
        Assert.Equal(LoopState.Running, round.Runner.State);
        Assert.Null(round.LoopFailure);

        Rig stuck = FundingPartyAtTheToll(TollPurseDoubtTests.TollTheOnlyWayJson, OnlyBailey);
        stuck.Gold = 6;
        Assert.True(stuck.Runner.Start(new Loop("toll", new[] { OnlyBailey, OnlyRoad })));
        Assert.Empty(stuck.Moves);                                // held at the toll for the answers
        stuck.Says("Bob", 0);
        stuck.Says("Cal", 500);

        Assert.Empty(stuck.Moves);
        Assert.Equal(LoopState.Idle, stuck.Runner.State);
        string notice = Assert.Single(stuck.Notices);
        Assert.Equal(
            "loop 'toll': no way from 1/2 to 1/3 without a toll east from 1/2 (Bailey) (5 gold) the party can't all pay: "
            + "Bob holds 0 copper, 5 gold short; covering that takes 5 gold and you can spare 1 gold after your own toll",
            notice);
        Assert.Equal(notice, stuck.LoopFailure);
    }

    // ----- a hand-over that doesn't land -------------------------------------------

    [Fact]
    public void AHandOverTheGameRefuses_MakesThatMemberUnable_AndTheWalkGoesRound()
    {
        Rig rig = FundingPartyAtTheToll();
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 200);
        rig.Says("Cal", 500);
        Assert.Equal(new[] { "give 3 gold to Bob" }, rig.Gives);

        rig.Gate.OnGiveRefused("Bob");                            // "Bob refuses your offer."

        Assert.Equal(new[] { "n" }, rig.Moves);
        Assert.Equal(new[] { "give 3 gold to Bob" }, rig.Gives);  // not tried again
        Assert.Contains("the game refused the hand-over", rig.Decided);
    }

    [Fact]
    public void AHandOverNeverConfirmed_OrCutShortByTheGame_MakesThatMemberUnable()
    {
        Rig unconfirmed = FundingPartyAtTheToll();
        Assert.True(unconfirmed.Walker.WalkTo(WayRoundRoad));
        unconfirmed.Says("Bob", 200);
        unconfirmed.Says("Cal", 500);
        Assert.Empty(unconfirmed.Moves);
        unconfirmed.GiveWindow!();                                // no line from the game inside the bound
        Assert.Equal(new[] { "n" }, unconfirmed.Moves);
        Assert.Contains("gave no answer to the hand-over", unconfirmed.Decided);

        // Stock counts over only what the recipient has room to keep.
        Rig cutShort = FundingPartyAtTheToll();
        Assert.True(cutShort.Walker.WalkTo(WayRoundRoad));
        cutShort.Says("Bob", 200);
        cutShort.Says("Cal", 500);
        cutShort.Confirms("Bob", 1);
        Assert.Equal(new[] { "n" }, cutShort.Moves);
        Assert.Contains("was handed only 1 gold of 3 gold", cutShort.Decided);
        Assert.Equal(300, cutShort.Wealth.LastReading("Bob"));    // what he said, and the gold that did land
    }

    // ----- what we keep ------------------------------------------------------------

    [Fact]
    public void FeesATripSetAside_AndOurOwnToll_AreNotHandedOver()
    {
        Rig rig = FundingPartyAtTheToll();
        rig.Filter.ReservedCopper = 400;                          // 10 gold: 4 set aside, 5 our toll, 1 spare
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 200);
        rig.Says("Cal", 500);

        Assert.Empty(rig.Gives);
        Assert.Equal(new[] { "n" }, rig.Moves);
        Assert.Contains("you can spare 1 gold after your own toll", rig.Decided);
    }

    [Fact]
    public void WithOurOwnPurseUnread_NothingIsHandedOver()
    {
        Rig rig = FundingPartyAtTheToll();
        rig.Gold = null;                                          // the inventory isn't read
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 200);
        rig.Says("Cal", 500);

        Assert.Empty(rig.Gives);
        Assert.Equal(new[] { "n" }, rig.Moves);
        Assert.Contains("your own purse isn't known", rig.Decided);
    }

    // ----- the member the toll turned away -----------------------------------------

    // Verified as able to pay, and turned away all the same: the walk went back for
    // them once (PartyComebackManager), and the toll is not tried again on what
    // they said before it. They are asked again, and paid for now that they are short.
    [Fact]
    public void AfterGoingBackForAMember_ThePursesAreAskedAgain_BeforeTheTollIsTriedAgain()
    {
        Rig rig = FundingPartyAtTheToll();
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 500);
        rig.Says("Cal", 500);
        Assert.Equal(new[] { "e" }, rig.Moves);
        rig.Tracker.NoteRoomObserved(Obs("Road", Direction.N, Direction.W));
        Assert.Equal(BothAsked, rig.Asked);

        // Bob was left at the toll; we went back, and the walk is resumed.
        rig.WentBack = (name, from, to) => name == "Bob" && from == WayRoundBailey && to == WayRoundRoad;
        rig.Tracker.SetLocated(WayRoundBailey);
        rig.Moves.Clear();
        rig.Now += TimeSpan.FromSeconds(5);                       // well inside the freshness window
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));

        Assert.Equal(4, rig.Asked.Count);                         // asked again all the same
        Assert.Empty(rig.Moves);
        rig.Says("Bob", 100);
        rig.Says("Cal", 500);

        Assert.Equal(new[] { "give 4 gold to Bob" }, rig.Gives);
        rig.Confirms("Bob", 4);
        Assert.Equal(new[] { "e" }, rig.Moves);
    }

    // Paid for, and still turned away: not paid for a second time at that toll.
    [Fact]
    public void AMemberHandedCoinAndStillTurnedAway_IsNotHandedCoinAgain_AtThatToll()
    {
        Rig rig = FundingPartyAtTheToll();
        rig.Gold = 30;                                            // plenty: it is not for want of coin
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 200);
        rig.Says("Cal", 500);
        rig.Confirms("Bob", 3);
        rig.Tracker.NoteRoomObserved(Obs("Road", Direction.N, Direction.W));

        rig.WentBack = (name, from, to) => name == "Bob" && from == WayRoundBailey && to == WayRoundRoad;
        rig.Tracker.SetLocated(WayRoundBailey);
        rig.Moves.Clear();
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 200);                                     // short again
        rig.Says("Cal", 500);

        Assert.Equal(new[] { "give 3 gold to Bob" }, rig.Gives);  // the first hand-over only
        Assert.Equal(new[] { "n" }, rig.Moves);                   // round instead
        Assert.Contains("was handed 3 gold at this toll already and was still turned away", rig.Decided);
    }

    // The same toll on a later lap, with nobody gone back for: they got through on
    // the coin, paid it, and are short again. That is a new toll to pay.
    [Fact]
    public void AMemberWhoGotThroughOnOurCoin_IsPaidForAgain_TheNextTimeRound()
    {
        Rig rig = FundingPartyAtTheToll();
        rig.Gold = 30;
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 200);
        rig.Says("Cal", 500);
        rig.Confirms("Bob", 3);
        rig.Tracker.NoteRoomObserved(Obs("Road", Direction.N, Direction.W));

        rig.Tracker.SetLocated(WayRoundBailey);
        rig.Moves.Clear();
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 0);
        rig.Says("Cal", 500);

        Assert.Equal(new[] { "give 3 gold to Bob", "give 5 gold to Bob" }, rig.Gives);
    }

    // ----- when it asks --------------------------------------------------------------

    [Fact]
    public void AnswersNoOlderThanTheFreshnessWindow_AreNotAskedForAgainAtTheToll()
    {
        Rig rig = PartyAtTheToll(TollPurseDoubtTests.TollTheOnlyWayJson, OnlyGates);
        Assert.True(rig.Walker.WalkTo(OnlyRoad));                 // the route crosses the toll: asked at the start
        Assert.Equal(BothAsked, rig.Asked);
        Assert.Equal(new[] { "e" }, rig.Moves);                   // the first step isn't the toll, and isn't held
        rig.Says("Bob", 500);
        rig.Says("Cal", 500);

        rig.Now += TimeSpan.FromSeconds(20);
        rig.Tracker.NoteRoomObserved(Obs("Bailey", Direction.E, Direction.W));

        Assert.Equal(BothAsked, rig.Asked);                       // not asked again
        Assert.Equal(new[] { "e", "e" }, rig.Moves);
    }

    [Fact]
    public void AnswersOlderThanTheFreshnessWindow_AreAskedForAgainAtTheToll()
    {
        Rig rig = PartyAtTheToll(TollPurseDoubtTests.TollTheOnlyWayJson, OnlyGates);
        Assert.True(rig.Walker.WalkTo(OnlyRoad));
        rig.Says("Bob", 500);
        rig.Says("Cal", 500);

        rig.Now += TimeSpan.FromSeconds(31);                      // a long way to the toll
        rig.Tracker.NoteRoomObserved(Obs("Bailey", Direction.E, Direction.W));

        Assert.Equal(4, rig.Asked.Count);
        Assert.Equal(new[] { "e" }, rig.Moves);                   // held at the toll for the new answers
        rig.Says("Bob", 500);
        rig.Says("Cal", 500);
        Assert.Equal(new[] { "e", "e" }, rig.Moves);
    }

    // ----- who it doesn't concern ------------------------------------------------------

    [Fact]
    public void Solo_AFollower_AndTheMasterSwitchOff_AskNobody_AndHoldNothing()
    {
        Rig solo = NewRig(TollPurseDoubtTests.TollWithAWayRoundJson);
        solo.Tracker.SetLocated(WayRoundBailey);
        Assert.True(solo.Walker.WalkTo(WayRoundRoad));
        Assert.Equal(new[] { "e" }, solo.Moves);
        Assert.Empty(solo.Asked);

        Rig follower = NewRig(TollPurseDoubtTests.TollWithAWayRoundJson);
        follower.Follower("Bob");
        follower.Party.IsInParty = true;                          // in a party, and not its leader
        follower.Tracker.SetLocated(WayRoundBailey);
        Assert.True(follower.Walker.WalkTo(WayRoundRoad));
        Assert.Equal(new[] { "e" }, follower.Moves);
        Assert.Empty(follower.Asked);

        Rig off = PartyAtTheToll();
        off.MasterOff = true;
        Assert.True(off.Walker.WalkTo(WayRoundRoad));
        Assert.Equal(new[] { "e" }, off.Moves);
        Assert.Empty(off.Asked);
        Assert.Empty(off.Gives);
    }

    // ----- exact change, or the least above it -------------------------------------

    private static CurrencyHoldings Purse(int silver = 0, int gold = 0, int platinum = 0, int runic = 0, int copper = 0) =>
        new(copper, silver, gold, platinum, runic,
            copper + silver * 10L + gold * 100L + platinum * 10_000L + runic * 1_000_000L);

    // Bob is 3 gold short and the purse can't make 3 gold: he is handed the
    // smallest coin that covers it, and the decision says by how much it overpays
    // (user, 2026-10-10: overpay "if it doesnt make the leader unable to cross").
    [Fact]
    public void WhenExactChangeCantBeMade_TheLeastThatCoversIsHandedOver_AndTheDecisionSaysHowMuchOver()
    {
        Rig platinum = FundingPartyAtTheToll();
        platinum.Coins = Purse(platinum: 5);
        Assert.True(platinum.Walker.WalkTo(WayRoundRoad));
        platinum.Says("Bob", 200);
        platinum.Says("Cal", 500);
        Assert.Equal(new[] { "give 1 platinum to Bob" }, platinum.Gives);
        Assert.Contains("Handing over 1 platinum to Bob to cover 3 gold: 97 gold over, then going through", platinum.Decided);
        Assert.Equal(
            "a toll east from 1/1 (Bailey) (5 gold): handing over 1 platinum to Bob to cover 3 gold: 97 gold over",
            Assert.Single(platinum.HandOverNotices));
        platinum.Gate.OnCoinsGivenAway("Bob", 10_000);
        Assert.Equal(new[] { "e" }, platinum.Moves);

        Rig runic = FundingPartyAtTheToll();
        runic.Coins = Purse(runic: 3);
        Assert.True(runic.Walker.WalkTo(WayRoundRoad));
        runic.Says("Bob", 200);
        runic.Says("Cal", 500);
        Assert.Equal(new[] { "give 1 runic to Bob" }, runic.Gives);
        // No ceiling but what we keep (user, 2026-10-10: "yes no cap is fine").
        Assert.Contains("1 runic to Bob to cover 3 gold: 99 platinum 97 gold over", Assert.Single(runic.HandOverNotices));
    }

    // The one coin that would cover Bob is the coin our own toll is in: handing it
    // over would leave us unable to cross, so nothing is handed over.
    [Fact]
    public void AnOverpayThatWouldLeaveUsShortOfOurOwnToll_IsNotMade()
    {
        Rig rig = FundingPartyAtTheToll();
        rig.Coins = Purse(gold: 2, platinum: 1);                  // 102 gold: 5 our toll, 97 spare, and no 3 gold in it
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 200);
        rig.Says("Cal", 500);

        Assert.Empty(rig.Gives);
        Assert.Equal(new[] { "n" }, rig.Moves);
        Assert.Contains(
            "the coins carried don't make what Bob needs without going past the 97 gold you can spare after your own toll",
            rig.Decided);
    }

    [Fact]
    public void ExactChange_IsCountedOutOfAMixedPurse_LargestCoinsFirst()
    {
        Rig goldBesideARunic = FundingPartyAtTheToll();
        goldBesideARunic.Coins = Purse(gold: 7, runic: 1);
        Assert.True(goldBesideARunic.Walker.WalkTo(WayRoundRoad));
        goldBesideARunic.Says("Bob", 200);
        goldBesideARunic.Says("Cal", 500);
        Assert.Equal(new[] { "give 3 gold to Bob" }, goldBesideARunic.Gives);

        Rig twoCoins = FundingPartyAtTheToll();
        twoCoins.Coins = Purse(silver: 10, gold: 2, platinum: 1);
        Assert.True(twoCoins.Walker.WalkTo(WayRoundRoad));
        twoCoins.Says("Bob", 200);
        twoCoins.Says("Cal", 500);
        Assert.Equal(new[] { "give 2 gold to Bob", "give 10 silver to Bob" }, twoCoins.Gives);
        twoCoins.Gate.OnCoinsGivenAway("Bob", 200);
        Assert.Empty(twoCoins.Moves);                             // one line of two
        twoCoins.Gate.OnCoinsGivenAway("Bob", 100);
        Assert.Equal(new[] { "e" }, twoCoins.Moves);
    }

    [Fact]
    public void CountOut_GivesTheNeedExactly_ElseTheLeastAboveIt_OrComesBackShort()
    {
        Assert.Equal(300, PartyTollGate.CountOut(Purse(silver: 30), need: 300, spare: 300).Copper);
        // No exact change: one platinum, which the spare allows.
        Assert.Equal(10_000, PartyTollGate.CountOut(Purse(platinum: 5), need: 300, spare: 40_000).Copper);
        // The runic would cover it and is more than can be spared: short.
        Assert.Equal(200, PartyTollGate.CountOut(Purse(gold: 2, runic: 1), need: 300, spare: 900_000).Copper);
        // Never more than can be spared, whatever is carried.
        Assert.Equal(100, PartyTollGate.CountOut(Purse(gold: 9), need: 300, spare: 100).Copper);
        // Four silver is nearer than a gold.
        Assert.Equal(40, PartyTollGate.CountOut(Purse(silver: 9, gold: 3), need: 35, spare: 500).Copper);
    }

    // Every way of picking coins out of a small purse, against what CountOut
    // picks: the need exactly when some pick makes it, else the least above it
    // that the spare allows, else short.
    [Fact]
    public void CountOut_IsTheBestPick_AgainstEveryPickThereIs()
    {
        long[] needs = [1, 7, 10, 25, 30, 99, 100, 101, 130, 250, 300, 310, 1_000, 9_999, 10_000, 10_150];
        for (int c = 0; c <= 3; c++)
        for (int s = 0; s <= 3; s++)
        for (int g = 0; g <= 2; g++)
        for (int p = 0; p <= 1; p++)
        {
            CurrencyHoldings purse = Purse(copper: c, silver: s, gold: g, platinum: p);
            foreach (long need in needs)
            foreach (long spare in new[] { need, need + 9, need + 95, 30_000 })
            {
                long best = long.MaxValue;
                for (int pc = 0; pc <= c; pc++)
                for (int ps = 0; ps <= s; ps++)
                for (int pg = 0; pg <= g; pg++)
                for (int pp = 0; pp <= p; pp++)
                {
                    long worth = pc + ps * 10L + pg * 100L + pp * 10_000L;
                    if (worth >= need && worth <= spare && worth < best) best = worth;
                }

                (IReadOnlyList<(string Currency, long Count)> coins, long copper) = PartyTollGate.CountOut(purse, need, spare);
                string at = $"purse {c}c {s}s {g}g {p}p, need {need}, spare {spare}";
                Assert.Equal(copper, coins.Sum(coin => CurrencyHoldings.ToCopper(coin.Currency, coin.Count)));
                Assert.All(coins, coin => Assert.True(coin.Count <= coin.Currency switch
                {
                    "copper" => c, "silver" => s, "gold" => g, _ => p,
                }, at));
                if (best == long.MaxValue) Assert.True(copper < need, at);
                else Assert.True(copper == best, $"{at}: handed {copper}, best {best}");
            }
        }
    }

    // Two short members out of one purse: whenever some way of sharing the coins
    // covers both inside the spare, a plan is made, and it hands over no more than
    // the best sharing there is.
    [Fact]
    public void PlanHandOvers_CoversBothMembersWheneverTheCoinsCan_AndHandsOverTheLeast()
    {
        long[] needs = [5, 15, 100, 120, 250];
        for (int c = 0; c <= 2; c++)
        for (int s = 0; s <= 2; s++)
        for (int g = 0; g <= 2; g++)
        for (int p = 0; p <= 1; p++)
        {
            CurrencyHoldings purse = Purse(copper: c, silver: s, gold: g, platinum: p);
            foreach (long first in needs)
            foreach (long second in needs)
            foreach (long spare in new[] { first + second, first + second + 90, 30_000 })
            {
                long best = long.MaxValue;
                for (int ac = 0; ac <= c; ac++)
                for (int asv = 0; asv <= s; asv++)
                for (int ag = 0; ag <= g; ag++)
                for (int ap = 0; ap <= p; ap++)
                {
                    long a = ac + asv * 10L + ag * 100L + ap * 10_000L;
                    if (a < first) continue;
                    for (int bc = 0; bc <= c - ac; bc++)
                    for (int bs = 0; bs <= s - asv; bs++)
                    for (int bg = 0; bg <= g - ag; bg++)
                    for (int bp = 0; bp <= p - ap; bp++)
                    {
                        long b = bc + bs * 10L + bg * 100L + bp * 10_000L;
                        if (b >= second && a + b <= spare && a + b < best) best = a + b;
                    }
                }

                IReadOnlyList<PartyTollGate.HandOver>? plan = PartyTollGate.PlanHandOvers(
                    purse, [("Bob", first), ("Cal", second)], spare, out IReadOnlyList<string> uncovered);
                string at = $"purse {c}c {s}s {g}g {p}p, needs {first} and {second}, spare {spare}";
                if (best == long.MaxValue)
                {
                    Assert.True(plan is null, at);
                    Assert.NotEmpty(uncovered);
                }
                else
                {
                    Assert.True(plan is not null, at);
                    Assert.True(plan!.Sum(h => h.Copper) == best, $"{at}: handed {plan!.Sum(h => h.Copper)}, best {best}");
                    Assert.All(plan!, h => Assert.True(h.Copper >= h.Need, at));
                }
            }
        }
    }

    // ----- a toll right behind a toll ------------------------------------------------

    //   1/1 West ─E (Toll: 5)─ 1/2 Middle ─E (Toll: 5)─ 1/3 East
    private const string TwoTollsInARowJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "West",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/2 (Toll: 5)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Middle",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/3 (Toll: 5)", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "East",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // A member holding exactly one toll: what they said before the first toll is
    // not what they hold at the second, though the second step is sent from inside
    // the arrival at the first.
    [Fact]
    public void TheTollAfterATollIsAskedAfresh_NotTakenOnWhatWasHeldBeforeTheFirst()
    {
        Rig rig = FundingPartyAtTheToll(TwoTollsInARowJson, new RoomKey(1, 1));
        rig.Gold = 30;
        Assert.True(rig.Walker.WalkTo(new RoomKey(1, 3)));
        rig.Says("Bob", 500);
        rig.Says("Cal", 5_000);
        Assert.Equal(new[] { "e" }, rig.Moves);

        rig.Tracker.NoteRoomObserved(Obs("Middle", Direction.E, Direction.W));

        Assert.Equal(4, rig.Asked.Count);                         // asked again at the second toll
        Assert.Equal(new[] { "e" }, rig.Moves);                   // and held for the answers
        rig.Says("Bob", 0);
        rig.Says("Cal", 4_500);
        Assert.Equal(new[] { "give 5 gold to Bob" }, rig.Gives);
        rig.Confirms("Bob", 5);
        Assert.Equal(new[] { "e", "e" }, rig.Moves);
    }

    [Fact]
    public void ALoopThroughOneTollBothWays_AsksAgainBeforeTheWayBack()
    {
        Rig rig = PartyAtTheToll(TollPurseDoubtTests.TollTheOnlyWayJson, OnlyBailey);
        Assert.True(rig.Runner.Start(new Loop("toll", new[] { OnlyBailey, OnlyRoad })));
        rig.Says("Bob", 500);
        rig.Says("Cal", 500);
        Assert.Equal(new[] { "e" }, rig.Moves);

        rig.Tracker.NoteRoomObserved(Obs("Road", Direction.W));

        Assert.Equal(4, rig.Asked.Count);
        Assert.Equal(new[] { "e" }, rig.Moves);                   // `w` waits for what they hold now
    }

    // ----- a confirmation that comes late ----------------------------------------------

    // The window passed and the member counted as unable; then the game's line came.
    // The coin did go: it counts toward what they hold and what they were paid, and
    // they are not handed the toll a second time on the next visit.
    [Fact]
    public void AConfirmationAfterTheWindow_IsStillCredited_AndTheMemberIsNotPaidAgain()
    {
        Rig rig = FundingPartyAtTheToll();
        rig.Gold = 40;
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 500);
        rig.Windows[^1]();                                        // Cal's client never answers @wealth
        Assert.Equal(new[] { "give 5 gold to Cal" }, rig.Gives);
        rig.GiveWindow!();                                        // 4 s with no line
        Assert.Equal(new[] { "n" }, rig.Moves);

        rig.Confirms("Cal", 5);                                   // the line, late

        Assert.Equal(500, rig.Wealth.LastReading("Cal"));
        rig.Walker.Stop("test");
        rig.Gate.NoteTripEnded();
        rig.Now += TimeSpan.FromSeconds(31);
        rig.Tracker.SetLocated(WayRoundBailey);
        rig.Moves.Clear();
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 500);
        rig.Windows[^1]();                                        // silent again

        Assert.Equal(new[] { "give 5 gold to Cal" }, rig.Gives);  // the one hand-over
        Assert.Equal(new[] { "e" }, rig.Moves);                   // she holds what she was handed
    }

    // ----- closed for the trip, and turned from early -----------------------------------

    [Fact]
    public void AWalkWhoseRouteCrossesNoToll_AsksNobody()
    {
        Rig rig = PartyAtTheToll(TwoTollsInARowJson, new RoomKey(1, 3));
        Assert.True(rig.Walker.WalkTo(new RoomKey(1, 1)));        // westward both exits are free
        Assert.Equal(new[] { "w" }, rig.Moves);
        Assert.Empty(rig.Asked);
    }

    // The answers asked for at the start land while the first step is on its way:
    // the walk ends when that step lands, a room short of the toll, and nobody is
    // asked a second time.
    [Fact]
    public void AnswersThatCloseATollAhead_EndTheWalkBeforeItsGate_WhenThereIsNoWayRound()
    {
        Rig rig = FundingPartyAtTheToll(TollPurseDoubtTests.TollTheOnlyWayJson, OnlyGates);
        rig.Gold = 6;
        Assert.True(rig.Walker.WalkTo(OnlyRoad));                 // Gates → Bailey (free) → Road (toll)
        Assert.Equal(new[] { "e" }, rig.Moves);                   // the free step is on its way
        rig.Says("Bob", 0);
        rig.Says("Cal", 500);                                     // the answers: Bob can't be covered
        Assert.Equal(WalkState.Walking, rig.Walker.State);        // not torn up mid-step

        rig.Tracker.NoteRoomObserved(Obs("Bailey", Direction.E, Direction.W));

        Assert.Equal(new[] { "e" }, rig.Moves);                   // no step toward the toll
        Assert.Equal(BothAsked, rig.Asked);                       // and no second ask at it
        Assert.Equal(WalkState.Idle, rig.Walker.State);
        Assert.Contains("the party can't all pay: Bob holds 0 copper, 5 gold short", Assert.Single(rig.Notices));
        Assert.Equal(OnlyRoad, rig.Walker.LastUnpaidFailure);     // "not now" to a detour, not "never"
    }

    [Fact]
    public void AClosedToll_StaysClosedForTheTrip_AndOpensWhenTheTripEndsOrTheFactsChange()
    {
        RoomExit toll = new(WayRoundRoad, RoomExitHint.Toll, RawHint: null, TollGold: 5);

        Rig rig = FundingPartyAtTheToll();
        rig.Gold = 6;
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 0);
        rig.Says("Cal", 500);
        Assert.Equal(new[] { "n" }, rig.Moves);

        rig.Now += TimeSpan.FromSeconds(120);                     // the answers are long stale
        Assert.True(rig.Filter.IsExitBlocked(in toll));           // still closed: the trip is still on
        Assert.Equal(BothAsked, rig.Asked);

        rig.Gold = 20;                                            // we can now cover what Bob lacked
        Assert.False(rig.Filter.IsExitBlocked(in toll));

        Rig ended = FundingPartyAtTheToll();
        ended.Gold = 6;
        Assert.True(ended.Walker.WalkTo(WayRoundRoad));
        ended.Says("Bob", 0);
        ended.Says("Cal", 500);
        ended.Now += TimeSpan.FromSeconds(120);
        Assert.True(ended.Filter.IsExitBlocked(in toll));
        ended.Gate.NoteTripEnded();
        Assert.False(ended.Filter.IsExitBlocked(in toll));

        Rig joined = FundingPartyAtTheToll();
        joined.Gold = 6;
        Assert.True(joined.Walker.WalkTo(WayRoundRoad));
        joined.Says("Bob", 0);
        joined.Says("Cal", 500);
        joined.Now += TimeSpan.FromSeconds(120);
        joined.Follower("Dee");                                   // the party is not the one it was closed for
        Assert.False(joined.Filter.IsExitBlocked(in toll));
    }

    // ----- talk is not an answer ---------------------------------------------------------

    [Fact]
    public void ATelepathThatMentionsCoins_IsNotReadAsThatMembersPurse()
    {
        Rig rig = PartyAtTheToll();
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));

        rig.Chats("Bob", "can you spare 2 gold for the toll");

        Assert.Null(rig.Wealth.LastReading("Bob"));
        rig.Says("Cal", 500);
        Assert.Empty(rig.Gives);                                  // Bob has not answered yet
        Assert.Empty(rig.Moves);
        rig.Says("Bob", 500);
        Assert.Equal(new[] { "e" }, rig.Moves);
    }

    // ----- a member who isn't here ---------------------------------------------------------

    // She answers that she holds nothing, and her hand-over gets no line: hidden,
    // late into the room, or elsewhere, the client can't tell. Everyone gets through
    // or nobody does, so she is unable, not left out: the party is not taken
    // through without her, and the decision never reads as if all could pay.
    [Fact]
    public void AMemberWhoseHandOverGetsNoLine_IsUnable_NotLeftOut_AndThePartyGoesRound()
    {
        Rig rig = FundingPartyAtTheToll();
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 500);
        rig.Says("Cal", 0);
        Assert.Equal(new[] { "give 5 gold to Cal" }, rig.Gives);

        rig.GiveWindow!();

        Assert.Equal(new[] { "n" }, rig.Moves);
        Assert.Contains("Cal gave no answer to the hand-over", rig.Decided);
        Assert.DoesNotContain("can pay", rig.Decided);
    }

    // Only a member the client KNOWS is not with us is left out: one the comeback
    // manager has as left behind. She isn't asked to be covered, the rest go
    // through, and the decision says who was left out and why.
    [Fact]
    public void AMemberKnownToBeLeftBehind_IsLeftOutOfTheCount_AndThePartyGoesOn()
    {
        Rig rig = PartyAtTheToll();
        rig.LeftBehind.Add("Cal");
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 500);
        rig.Windows[^1]();                                        // she doesn't answer either

        Assert.Empty(rig.Gives);
        Assert.Equal(new[] { "e" }, rig.Moves);
        Assert.Contains("Cal left out (known to be left behind", rig.Decided);
    }

    // The Stock engine's answer to a coin give at nobody it can find for us.
    [Fact]
    public void TheGamesLineForAGiveAtNobodyHere_FailsTheOneHandOverInHand()
    {
        Rig rig = FundingPartyAtTheToll();
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 500);
        rig.Says("Cal", 0);

        rig.Gate.OnCoinGiveMisaimed();                            // "Why would you want to give to that?"

        Assert.Equal(new[] { "n" }, rig.Moves);
        Assert.Contains("nobody of that name here to give to", rig.Decided);
    }

    // ----- a step let go that bounces ----------------------------------------------------

    // A member who never answers @wealth is handed the toll and it is confirmed; the
    // party is let through and the game bounces the move. The retry asks again, she
    // is silent again, and she still counts as holding what she was handed: the
    // crossing hasn't been seen, so nothing has spent it.
    [Fact]
    public void AStepLetThroughThatBounces_DoesNotPayTheSilentMemberASecondTime()
    {
        Rig rig = FundingPartyAtTheToll();
        rig.Gold = 40;
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 500);
        rig.Windows[^1]();
        rig.Confirms("Cal", 5);
        Assert.Equal(new[] { "e" }, rig.Moves);

        rig.Tracker.NoteMoveBlocked();                            // the move never left the room

        Assert.Equal(4, rig.Asked.Count);                         // the retry asks afresh
        rig.Says("Bob", 500);
        rig.Windows[^1]();

        Assert.Equal(new[] { "give 5 gold to Cal" }, rig.Gives);  // the one hand-over
        Assert.Equal(new[] { "e", "e" }, rig.Moves);
    }

    // ----- a late line must be one of ours ---------------------------------------------------

    [Fact]
    public void ALateLine_IsCreditedOnlyWhenItMatchesAGiveThisCheckSent()
    {
        Rig rig = FundingPartyAtTheToll();
        rig.Gold = 40;
        Assert.True(rig.Walker.WalkTo(WayRoundRoad));
        rig.Says("Bob", 500);
        rig.Windows[^1]();
        rig.GiveWindow!();

        rig.Gate.OnCoinsGivenAway("Cal", 100);                    // a gold handed over by hand: not this toll's
        Assert.Null(rig.Wealth.LastReading("Cal"));

        rig.Gate.OnCoinsGivenAway("Cal", 500);                    // the give that was sent
        Assert.Equal(500, rig.Wealth.LastReading("Cal"));
        rig.Gate.OnCoinsGivenAway("Cal", 500);                    // and not a second time
        Assert.Equal(500, rig.Wealth.LastReading("Cal"));
    }

    // ----- turned at more than one crossing ---------------------------------------------------

    private static string RoomJson(int room, string name, string n = "0", string s = "0", string e = "0", string w = "0") =>
        $$"""
          { "Map Number": 1, "Room Number": {{room}}, "Name": "{{name}}",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "{{n}}", "S": "{{s}}", "E": "{{e}}", "W": "{{w}}",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        """;

    private static string RoomsJson(params string[] rooms) => "[" + string.Join(",", rooms) + "]";

    //   1/1 ─E (Toll: 5)─ 1/2 ─E (Toll: 5)─ 1/3 ─E (Toll: 5)─ 1/4, and round each toll:
    //   1/n ─N─ 1/1n ─E─ 1/2n ─S─ 1/(n+1)
    private static readonly string ThreeTollsEachWithAWayRoundJson = RoomsJson(
        RoomJson(1, "A1", n: "1/11", e: "1/2 (Toll: 5)"),
        RoomJson(2, "A2", n: "1/12", e: "1/3 (Toll: 5)"),
        RoomJson(3, "A3", n: "1/13", e: "1/4 (Toll: 5)"),
        RoomJson(4, "A4"),
        RoomJson(11, "B1", e: "1/21"), RoomJson(21, "C1", s: "1/2"),
        RoomJson(12, "B2", e: "1/22"), RoomJson(22, "C2", s: "1/3"),
        RoomJson(13, "B3", e: "1/23"), RoomJson(23, "C3", s: "1/4"));

    // Three tolls on one walk, each closed to the party at its own gate and each
    // with a way round: the walk goes round all three. The turns are counted from
    // the last step that landed, not over the whole walk.
    [Fact]
    public void AWalkTurnedAtThreeTollsOnItsWay_GoesRoundEachOfThem()
    {
        Rig rig = NewRig(ThreeTollsEachWithAWayRoundJson);
        HashSet<RoomKey> closed = new();
        rig.Filter.PartyTollClosedProbe = exit => closed.Contains(exit.Target);
        rig.Walker.SetTollStepCheck((_, _, exit, _) =>
        {
            closed.Add(exit.Target);
            return PartyTollGate.StepVerdict.Closed;
        });
        rig.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(rig.Walker.WalkTo(new RoomKey(1, 4)));

        for (int toll = 1; toll <= 3; toll++)
        {
            rig.Tracker.NoteRoomObserved(Obs($"B{toll}", Direction.E));
            rig.Tracker.NoteRoomObserved(Obs($"C{toll}", Direction.S));
            rig.Tracker.NoteRoomObserved(toll < 3 ? Obs($"A{toll + 1}", Direction.N, Direction.E) : Obs("A4"));
        }

        Assert.Equal(new[] { "n", "e", "s", "n", "e", "s", "n", "e", "s" }, rig.Moves);
        Assert.Null(rig.WalkFailure);
        Assert.Empty(rig.Notices);
        Assert.Equal(WalkState.Idle, rig.Walker.State);
        Assert.Contains(rig.WalkEvents, e => e.Kind == WalkEventKind.Finished);
    }

    // ----- the setting --------------------------------------------------------------------

    // Off until ticked: a short member is handed nothing, the toll is closed to the
    // party for the trip, and the notice says the setting is why.
    [Fact]
    public void WithFundingOff_NoCoinIsHandedOver_AndTheClosingNoticeSaysTheSettingIsOff()
    {
        Assert.False(new MudPlay.Models.Profile.CashSettings().FundPartyTolls);

        Rig round = PartyAtTheToll();
        round.Gold = 40;                                          // not for want of coin
        Assert.True(round.Walker.WalkTo(WayRoundRoad));
        round.Says("Bob", 200);
        round.Says("Cal", 500);
        Assert.Empty(round.Gives);
        Assert.Equal(new[] { "n" }, round.Moves);

        Rig stuck = PartyAtTheToll(TollPurseDoubtTests.TollTheOnlyWayJson, OnlyBailey);
        stuck.Gold = 40;
        Assert.True(stuck.Walker.WalkTo(OnlyRoad));
        stuck.Says("Bob", 200);
        stuck.Windows[^1]();                                      // Cal never answers
        Assert.Empty(stuck.Gives);
        Assert.Empty(stuck.Moves);
        Assert.Equal(
            "all routes blocked by a toll east from 1/2 (Bailey) (5 gold) the party can't all pay: "
            + "Bob holds 2 gold, 3 gold short; Cal gave no answer to @wealth; "
            + "\"Fund party members' tolls / rides if short\" is off (Settings, Cash + Items), so no coin is handed over",
            Assert.Single(stuck.Notices));
        Assert.Contains("funding short members off", stuck.Gate.LastReadings);

        // Ticked while the toll is closed: it opens to the next plan.
        RoomExit toll = new(OnlyRoad, RoomExitHint.Toll, RawHint: null, TollGold: 5);
        Assert.True(stuck.Filter.IsExitBlocked(in toll));
        stuck.Fund = true;
        Assert.False(stuck.Filter.IsExitBlocked(in toll));
    }

    // ----- what we keep ---------------------------------------------------------------------

    // The larger of what we need to cross and the keep-on-hand amount is not
    // handed over (user, 2026-10-10).
    [Fact]
    public void TheLargerOfOurOwnCrossingAndKeepOnHand_IsKept()
    {
        Rig keepIsMore = FundingPartyAtTheToll();                 // 10 gold: keep 8, so 2 spare
        keepIsMore.KeepOnHand = 800;
        Assert.True(keepIsMore.Walker.WalkTo(WayRoundRoad));
        keepIsMore.Says("Bob", 200);
        keepIsMore.Says("Cal", 500);
        Assert.Empty(keepIsMore.Gives);
        Assert.Equal(new[] { "n" }, keepIsMore.Moves);
        Assert.Contains("you can spare 2 gold after the 8 gold you keep on hand", keepIsMore.Decided);
        // What a bank stop sizes its draw by is the same amount.
        Assert.Equal(800, keepIsMore.Gate.KeptFor(500));
        Assert.Equal(1_200, keepIsMore.Gate.KeptFor(1_200));

        Rig crossingIsMore = FundingPartyAtTheToll();             // keep 3 is under our 5 gold toll: 5 spare
        crossingIsMore.KeepOnHand = 300;
        Assert.True(crossingIsMore.Walker.WalkTo(WayRoundRoad));
        crossingIsMore.Says("Bob", 200);
        crossingIsMore.Says("Cal", 500);
        Assert.Equal(new[] { "give 3 gold to Bob" }, crossingIsMore.Gives);

        Rig overpay = FundingPartyAtTheToll();                    // a platinum would cover Bob, and breaks the keep
        overpay.Coins = Purse(platinum: 5);
        overpay.KeepOnHand = 45_000;
        Assert.True(overpay.Walker.WalkTo(WayRoundRoad));
        overpay.Says("Bob", 200);
        overpay.Says("Cal", 500);
        Assert.Empty(overpay.Gives);
        Assert.Contains("going past the 50 gold you can spare after the 4 platinum 50 gold you keep on hand", overpay.Decided);
    }

    // 12 gold, two 5 gold tolls on the walk, and a member with nothing. Handing him
    // 5 at the first would leave us 2 at the second, between two tolls we can't
    // cross. Our own toll further on is kept back, so he isn't paid for at the
    // first and the walk doesn't set out through it.
    [Fact]
    public void OurOwnTollsFurtherOnTheRoute_AreKeptBack_SoWeNeverStrandOurselvesBetweenTwoTolls()
    {
        Rig stuck = FundingPartyAtTheToll(TwoTollsInARowJson, new RoomKey(1, 1));
        stuck.Gold = 12;
        Assert.True(stuck.Walker.WalkTo(new RoomKey(1, 3)));
        stuck.Says("Bob", 0);
        stuck.Says("Cal", 5_000);

        Assert.Empty(stuck.Gives);
        Assert.Empty(stuck.Moves);
        Assert.Equal(WalkState.Idle, stuck.Walker.State);
        Assert.Equal(
            "all routes blocked by a toll east from 1/1 (West) (5 gold) the party can't all pay: "
            + "Bob holds 0 copper, 5 gold short; "
            + "Bob isn't paid for here: 5 gold is kept for your own tolls and fares further on this route",
            Assert.Single(stuck.Notices));
        Assert.Contains(
            "you can spare 2 gold after your own toll and the 5 gold of your own tolls and fares further on this route",
            stuck.Decided);

        // With a way round the first toll, it is taken, and at the second, the
        // last on the route, there is the 5 gold to hand him.
        Rig round = FundingPartyAtTheToll(ThreeTollsEachWithAWayRoundJson, new RoomKey(1, 1));
        round.Gold = 12;
        Assert.True(round.Walker.WalkTo(new RoomKey(1, 3)));
        round.Says("Bob", 0);
        round.Says("Cal", 5_000);
        Assert.Empty(round.Gives);
        Assert.Equal(new[] { "n" }, round.Moves);
        round.Tracker.NoteRoomObserved(Obs("B1", Direction.E));
        round.Tracker.NoteRoomObserved(Obs("C1", Direction.S));
        round.Tracker.NoteRoomObserved(Obs("A2", Direction.N, Direction.E));
        Assert.Equal(new[] { "give 5 gold to Bob" }, round.Gives);
        round.Confirms("Bob", 5);
        Assert.Equal(new[] { "n", "e", "s", "e" }, round.Moves);
        Assert.Equal(700, round.Purse);                           // and our own 5 gold for it
    }

    // Only the tolls on the route as planned are kept back: a toll beyond the
    // destination, or on a branch the route doesn't take, is not ours to pay.
    [Fact]
    public void ATollTheRouteDoesNotTake_IsNotKeptBack()
    {
        Rig rig = FundingPartyAtTheToll(ThreeTollsEachWithAWayRoundJson, new RoomKey(1, 1));
        rig.Gold = 12;
        Assert.True(rig.Walker.WalkTo(new RoomKey(1, 2)));        // one toll; two more lie east of where we stop
        rig.Says("Bob", 0);
        rig.Says("Cal", 5_000);

        Assert.Equal(new[] { "give 5 gold to Bob" }, rig.Gives);
        rig.Confirms("Bob", 5);
        Assert.Equal(new[] { "e" }, rig.Moves);
    }

    // A loop's way back is a toll too: the toll for it is kept back at the way out.
    [Fact]
    public void ALoop_KeepsBackItsOwnTollsOnTheRestOfTheLap()
    {
        Rig rig = FundingPartyAtTheToll(TollPurseDoubtTests.TollTheOnlyWayJson, OnlyBailey);
        rig.Gold = 12;                                            // out 5, back 5, and 2 to spare
        Assert.True(rig.Runner.Start(new Loop("toll", new[] { OnlyBailey, OnlyRoad })));
        rig.Says("Bob", 200);
        rig.Says("Cal", 5_000);

        Assert.Empty(rig.Gives);
        Assert.Empty(rig.Moves);
        Assert.Equal(LoopState.Idle, rig.Runner.State);
        Assert.Contains("Bob isn't paid for here: 5 gold is kept for your own tolls and fares further on this route",
            Assert.Single(rig.Notices));
    }

    // ----- every lap -----------------------------------------------------------------------

    // A loop through a toll each way: a member who is short is covered at every
    // crossing, lap after lap, and never twice at one crossing when its step bounces.
    [Fact]
    public void ALoopThroughATollEveryLap_CoversAShortMemberEachTime_AndNeverTwiceAtOneCrossing()
    {
        Rig rig = FundingPartyAtTheToll(TollPurseDoubtTests.TollTheOnlyWayJson, OnlyBailey);
        rig.Gold = 60;
        Assert.True(rig.Runner.Start(new Loop("toll", new[] { OnlyBailey, OnlyRoad })));
        rig.Says("Bob", 200);
        rig.Says("Cal", 9_000);
        rig.Confirms("Bob", 3);
        Assert.Equal(new[] { "e" }, rig.Moves);

        rig.Tracker.NoteRoomObserved(Obs("Road", Direction.W));   // the way back is a toll too
        rig.Says("Bob", 0);
        rig.Says("Cal", 8_500);
        rig.Confirms("Bob", 5);
        Assert.Equal(new[] { "e", "w" }, rig.Moves);

        rig.Tracker.NoteRoomObserved(Obs("Bailey", Direction.E, Direction.W));   // lap two
        rig.Says("Bob", 0);
        rig.Says("Cal", 8_000);
        Assert.Equal(new[] { "give 3 gold to Bob", "give 5 gold to Bob", "give 5 gold to Bob" }, rig.Gives);
        rig.Confirms("Bob", 5);
        Assert.Equal(new[] { "e", "w", "e" }, rig.Moves);

        rig.Tracker.NoteMoveBlocked();                            // the step bounces: the same crossing again
        rig.Says("Cal", 8_000);
        rig.Windows[^1]();                                        // and Bob doesn't answer the new ask
        Assert.Equal(3, rig.Gives.Count);                         // he holds what he was handed for it
        Assert.Equal(new[] { "e", "w", "e", "e" }, rig.Moves);
    }

    // ----- a hidden member --------------------------------------------------------------------

    // A coin give at a hidden member gets the game's line for nobody of that name
    // here, which names nobody (user, 2026-10-10: a sneaking member can be given
    // coin, a hidden one can't). With two hand-overs out, the one the game then
    // confirms is funded and the other is not, and the notice says she could not
    // be given coin.
    [Fact]
    public void AHiddenMember_CantBeGivenCoin_CountsAsNotFunded_AndTheNoticeSaysSo()
    {
        Rig rig = FundingPartyAtTheToll(TollPurseDoubtTests.TollTheOnlyWayJson, OnlyBailey);
        rig.Gold = 40;
        Assert.True(rig.Walker.WalkTo(OnlyRoad));
        rig.Says("Bob", 200);
        rig.Says("Cal", 0);
        Assert.Equal(new[] { "give 3 gold to Bob", "give 5 gold to Cal" }, rig.Gives);

        rig.Gate.OnCoinGiveMisaimed();                            // "Why would you want to give to that?"
        rig.Confirms("Bob", 3);
        Assert.Empty(rig.Moves);
        rig.GiveWindow!();

        Assert.Empty(rig.Moves);
        Assert.Contains(
            "Cal could not be given coin: no line named them inside 4 s, and the game answered a give with its line "
            + "for nobody of that name here (not here, or hidden from us)",
            Assert.Single(rig.Notices));
    }

    // Two hand-overs still out at the bound, and the game's line for nobody here
    // seen once: one of the two is hidden and the other may only be slow, so
    // neither is said to be hidden. Both count as unconfirmed, and the slow one's
    // line, when it comes, is still credited.
    [Fact]
    public void WithTwoHandOversUnconfirmed_TheBoundSaysNoConfirmation_NotThatEitherIsHidden()
    {
        Rig rig = FundingPartyAtTheToll(TollPurseDoubtTests.TollTheOnlyWayJson, OnlyBailey);
        rig.Gold = 40;
        Assert.True(rig.Walker.WalkTo(OnlyRoad));
        rig.Says("Bob", 200);
        rig.Says("Cal", 0);

        rig.Gate.OnCoinGiveMisaimed();
        rig.GiveWindow!();

        string notice = Assert.Single(rig.Notices);
        Assert.Contains("Bob gave no answer to the hand-over: no line from the game inside 4 s", notice);
        Assert.Contains("Cal gave no answer to the hand-over: no line from the game inside 4 s", notice);
        Assert.DoesNotContain("hidden", notice);

        rig.Confirms("Bob", 3);                                   // his line was only late
        Assert.Equal(500, rig.Wealth.LastReading("Bob"));
    }

    // ----- rides ----------------------------------------------------------------------------

    private static readonly RoomExit Transport = new(new RoomKey(16, 637), RoomExitHint.Teleport,
        RawHint: "greet teleport", TextCommands: new[] { "ask someone activate" }, FareCopper: 700);

    // An NPC's paid transport is checked as a toll is: asked first, the short
    // member funded, then taken; and with funding off it is closed to the party.
    [Fact]
    public void AnNpcsFare_IsAskedAbout_AndFunded_AsATollIs()
    {
        Rig rig = FundingPartyAtTheToll();
        rig.Gold = 20;
        Assert.False(rig.Filter.IsExitBlocked(in Transport));     // nobody asked yet: the ask is made at the step

        Assert.Equal(PartyTollGate.StepVerdict.Hold, rig.Gate.BeforeTollStep(WayRoundBailey, Direction.Teleport, in Transport));
        Assert.Equal(BothAsked, rig.Asked);
        rig.Says("Bob", 100);
        rig.Says("Cal", 700);

        Assert.Equal(PartyTollGate.StepVerdict.Hold, rig.Gate.BeforeTollStep(WayRoundBailey, Direction.Teleport, in Transport));
        Assert.Equal(new[] { "give 6 gold to Bob" }, rig.Gives);
        rig.Confirms("Bob", 6);
        Assert.Equal(PartyTollGate.StepVerdict.Go, rig.Gate.BeforeTollStep(WayRoundBailey, Direction.Teleport, in Transport));
        Assert.Contains("a paid transport from 1/1 (Bailey) (7 gold per person): every party member with us can pay", rig.Decided);

        Rig off = PartyAtTheToll();
        Assert.Equal(PartyTollGate.StepVerdict.Hold, off.Gate.BeforeTollStep(WayRoundBailey, Direction.Teleport, in Transport));
        off.Says("Bob", 100);
        off.Says("Cal", 700);
        Assert.Equal(PartyTollGate.StepVerdict.Closed, off.Gate.BeforeTollStep(WayRoundBailey, Direction.Teleport, in Transport));
        Assert.Empty(off.Gives);
        Assert.True(off.Filter.IsExitBlocked(in Transport));      // closed to routes for the trip
        Assert.Equal(ExitBlockReason.Fare, off.Filter.DescribeExitBlock(in Transport));
        Assert.StartsWith("Bob holds 1 gold, 6 gold short;", off.Filter.DescribePurseFor(in Transport));
    }

    // A sailing: the verb is held for the purses, a short member is funded (here by
    // overpaying, the purse being all runics), and with funding off the sailing is
    // closed to the party and the filter turns it away.
    [Fact]
    public void ASailing_IsAskedAbout_AndFunded_AsATollIs()
    {
        BoatPassage sailing = new(new RoomKey(14, 759), "albion", "secure passage to albion",
            new RoomKey(14, 702), MinLevel: 0, FareCopper: 2_000_000, RequiresCheckability: false, VoyageRounds: 3);

        Rig rig = FundingPartyAtTheToll();
        rig.Coins = Purse(runic: 7);
        Assert.Equal(PartyTollGate.StepVerdict.Hold, rig.Gate.BeforeSailing(in sailing));
        rig.Says("Bob", 1_500_000);
        rig.Says("Cal", 2_000_000);
        Assert.Equal(PartyTollGate.StepVerdict.Hold, rig.Gate.BeforeSailing(in sailing));
        Assert.Equal(new[] { "give 1 runic to Bob" }, rig.Gives);
        Assert.Contains("1 runic to Bob to cover 50 platinum: 50 platinum over", rig.Decided);
        rig.Gate.OnCoinsGivenAway("Bob", 1_000_000);
        Assert.Equal(PartyTollGate.StepVerdict.Go, rig.Gate.BeforeSailing(in sailing));
        Assert.Equal(ExitBlockReason.None, rig.Filter.DescribeBoatBlock(in sailing));

        Rig off = PartyAtTheToll();
        off.Coins = Purse(runic: 7);
        Assert.Equal(PartyTollGate.StepVerdict.Hold, off.Gate.BeforeSailing(in sailing));
        off.Says("Bob", 1_500_000);
        off.Says("Cal", 2_000_000);
        Assert.Equal(PartyTollGate.StepVerdict.Closed, off.Gate.BeforeSailing(in sailing));
        Assert.Empty(off.Gives);
        Assert.Equal(ExitBlockReason.Fare, off.Filter.DescribeBoatBlock(in sailing));
    }

    // ----- rides, through the walker ------------------------------------------------------------

    // The tavern's barmaid takes whoever asks to the guild for 10 gold, the only way there.
    private Rig AtTheTavern(bool party = true)
    {
        Rig rig = NewRig(TollPurseDoubtTests.FareRoomsJson,
            monstersJson: TollPurseDoubtTests.FareMonstersJson, tbInfoJson: TollPurseDoubtTests.FareTbInfoJson);
        if (party)
        {
            rig.Follower("Bob");
            rig.Follower("Cal");
            rig.Lead();
        }
        rig.Gold = 30;
        rig.Tracker.SetLocated(new RoomKey(1, 2));
        return rig;
    }

    // The pier's sailing (1 gold) is the only way to the island.
    private Rig AtThePier(bool party = true)
    {
        Rig rig = NewRig(TollPurseDoubtTests.BoatRoomsJson,
            tbInfoJson: TollPurseDoubtTests.BoatTbInfoJson, spellsJson: TollPurseDoubtTests.BoatSpellsJson);
        if (party)
        {
            rig.Follower("Bob");
            rig.Follower("Cal");
            rig.Lead();
        }
        rig.Tracker.SetLocated(new RoomKey(1, 2));
        return rig;
    }

    private static readonly RoomKey Guild = new(1, 391), IsleTown = new(1, 11);
    private const string AskTheBarmaid = "ask barmaid adventure", Sail = "secure passage to islea";

    [Fact]
    public void AWalkThroughAnNpcsFare_WithFundingOn_GivesThenConfirmsThenAsks()
    {
        Rig rig = AtTheTavern();
        rig.Fund = true;
        Assert.True(rig.Walker.WalkTo(Guild));
        Assert.Equal(BothAsked, rig.Asked);
        Assert.Empty(rig.Moves);                                  // the keyword waits for the purses

        rig.Says("Bob", 400);
        rig.Says("Cal", 1_000);
        Assert.Equal(new[] { "give 6 gold to Bob" }, rig.Gives);
        Assert.Empty(rig.Moves);                                  // and for the game to confirm the coin

        rig.Confirms("Bob", 6);
        Assert.Equal(new[] { AskTheBarmaid }, rig.Moves);
        Assert.Null(rig.WalkFailure);
    }

    [Fact]
    public void AWalkThroughAnNpcsFare_WithFundingOff_EndsNamingTheRideAndTheSetting()
    {
        Rig rig = AtTheTavern();
        Assert.True(rig.Walker.WalkTo(Guild));
        rig.Says("Bob", 400);
        rig.Says("Cal", 1_000);

        Assert.Empty(rig.Gives);
        Assert.Empty(rig.Moves);
        Assert.Equal(WalkState.Idle, rig.Walker.State);
        Assert.Equal(
            "all routes blocked by a paid transport from 1/2 (Tavern) (10 gold per person) the party can't all pay: "
            + "Bob holds 4 gold, 6 gold short; "
            + "\"Fund party members' tolls / rides if short\" is off (Settings, Cash + Items), so no coin is handed over",
            Assert.Single(rig.Notices));
    }

    [Fact]
    public void AWalkThroughAnNpcsFare_Solo_AsAFollower_OrWithTheMasterSwitchOff_AsksNobody()
    {
        Rig solo = AtTheTavern(party: false);
        Assert.True(solo.Walker.WalkTo(Guild));
        Assert.Equal(new[] { AskTheBarmaid }, solo.Moves);
        Assert.Empty(solo.Asked);

        Rig follower = AtTheTavern(party: false);
        follower.Follower("Bob");
        follower.Party.IsInParty = true;                          // in a party, and not its leader
        Assert.True(follower.Walker.WalkTo(Guild));
        Assert.Equal(new[] { AskTheBarmaid }, follower.Moves);
        Assert.Empty(follower.Asked);

        Rig off = AtTheTavern();
        off.MasterOff = true;
        Assert.True(off.Walker.WalkTo(Guild));
        Assert.Equal(new[] { AskTheBarmaid }, off.Moves);
        Assert.Empty(off.Asked);
        Assert.Empty(off.Gives);
    }

    [Fact]
    public void AWalkThroughASailing_WithFundingOn_GivesThenConfirmsThenBoards()
    {
        Rig rig = AtThePier();
        rig.Fund = true;
        Assert.True(rig.Walker.WalkTo(IsleTown));
        Assert.Equal(BothAsked, rig.Asked);
        Assert.Empty(rig.Moves);

        rig.Says("Bob", 0);
        rig.Says("Cal", 100);
        Assert.Equal(new[] { "give 1 gold to Bob" }, rig.Gives);
        Assert.Empty(rig.Moves);

        rig.Confirms("Bob", 1);
        Assert.Equal(new[] { Sail }, rig.Moves);
        Assert.Null(rig.WalkFailure);
    }

    [Fact]
    public void AWalkThroughASailing_WithFundingOff_EndsNamingTheRideAndTheSetting()
    {
        Rig rig = AtThePier();
        Assert.True(rig.Walker.WalkTo(IsleTown));
        rig.Says("Bob", 0);
        rig.Says("Cal", 100);

        Assert.Empty(rig.Gives);
        Assert.Empty(rig.Moves);
        Assert.Equal(WalkState.Idle, rig.Walker.State);
        Assert.Equal(
            "all routes blocked by the boat 'secure passage to islea' from 1/2 (Pier) (1 gold per person) "
            + "the party can't all pay: Bob holds 0 copper, 1 gold short; "
            + "\"Fund party members' tolls / rides if short\" is off (Settings, Cash + Items), so no coin is handed over",
            Assert.Single(rig.Notices));
    }

    [Fact]
    public void AWalkThroughASailing_Solo_AsAFollower_OrWithTheMasterSwitchOff_AsksNobody()
    {
        Rig solo = AtThePier(party: false);
        Assert.True(solo.Walker.WalkTo(IsleTown));
        Assert.Equal(new[] { Sail }, solo.Moves);
        Assert.Empty(solo.Asked);

        Rig follower = AtThePier(party: false);
        follower.Follower("Bob");
        follower.Party.IsInParty = true;
        Assert.True(follower.Walker.WalkTo(IsleTown));
        Assert.Equal(new[] { Sail }, follower.Moves);
        Assert.Empty(follower.Asked);

        Rig off = AtThePier();
        off.MasterOff = true;
        Assert.True(off.Walker.WalkTo(IsleTown));
        Assert.Equal(new[] { Sail }, off.Moves);
        Assert.Empty(off.Asked);
        Assert.Empty(off.Gives);
    }

    // ----- a trip's bank stop -----------------------------------------------------------------

    // What a bank stop adds for the party: each short member what they lack of the
    // trip's tolls, a silent one all of it, and nothing with funding off.
    [Fact]
    public void FundingNeededFor_IsWhatTheShortAndTheSilentLack_AndNothingWithFundingOff()
    {
        Rig rig = FundingPartyAtTheToll();
        int reads = 0;
        rig.Gate.AfterReadingPurses(() => reads++);
        Assert.Equal(BothAsked, rig.Asked);
        Assert.Equal(0, reads);
        rig.Says("Bob", 200);
        rig.Windows[^1]();                                        // Cal never answers
        Assert.Equal(1, reads);

        (long copper, string who) = rig.Gate.FundingNeededFor(1_000);
        Assert.Equal(1_800, copper);
        Assert.Equal("Bob holds 2 gold, 8 gold short; Cal gave no answer to @wealth, 10 gold", who);

        rig.Fund = false;
        Assert.Equal(0, rig.Gate.FundingNeededFor(1_000).Copper);
        rig.Gate.AfterReadingPurses(() => reads++);               // nobody asked: nothing would be handed over
        Assert.Equal(2, reads);
        Assert.Equal(BothAsked, rig.Asked);
    }
}
