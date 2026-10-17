using System.Text;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using MudPlay.Game.Spells;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Report paradigm-20261010-145529: a walk started from the graveyard after a death
// went through the town's toll gate on the coin carried before the death, was refused
// there six times and gave up. A death takes the coin carried, so from a death until
// the inventory is read again the toll gate takes the purse as empty; and a toll or
// fare the game refuses is closed to routes, whatever the record and the data said.
// The loop runner's side of the same refusals is in LoopUnpaidCrossingTests.
public sealed class TollPurseDoubtTests : IDisposable
{
    private const long CoinBeforeTheDeath = 2_059_900;
    private const string Refusal = "You do not have enough to cover the toll of 5 gold crowns.";
    private const string PriceRefusal = "He says, \"I may be old, but I count quite well and you are short!\"";

    private readonly string _root;

    public TollPurseDoubtTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-toll-purse-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // The toll is the short way (one step east); the lane and the bridge go round it.
    //
    //   1/3 Lane ──E── 1/4 Bridge
    //    │S             │S
    //   1/1 Bailey ─E (Toll: 5)─ 1/2 Road
    internal const string TollWithAWayRoundJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Bailey",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "0", "E": "1/2 (Toll: 5)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Road",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/4", "S": "0", "E": "0", "W": "1/1 (Toll: 5)",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Lane",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "1/4", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Bridge",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // The report's shape: the toll is the only way out, one room on from the start.
    //
    //   1/1 Gates ──E── 1/2 Bailey ─E (Toll: 5)─ 1/3 Road
    internal const string TollTheOnlyWayJson = """
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
            "N": "0", "S": "0", "E": "0", "W": "1/2 (Toll: 5)",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Toll A (5 gold) has a way round; toll B (1 gold), further on, has none.
    //
    //   1/3 Lane ──E── 1/4 Bridge
    //    │S             │S
    //   1/1 Bailey ─E (Toll: 5)─ 1/2 Road ─E (Toll: 1)─ 1/5 Far
    internal const string TwoTollsJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Bailey",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "0", "E": "1/2 (Toll: 5)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Road",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/4", "S": "0", "E": "1/5 (Toll: 1)", "W": "1/1 (Toll: 5)",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Lane",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "1/4", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Bridge",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Far",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2 (Toll: 1)",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // An NPC in the tavern takes whoever asks to the guild, for 10 gold: the only
    // way there.
    //
    //   1/1 Home ──N── 1/2 Tavern ~ask barmaid adventure (10 gold)~ 1/391 Guild
    internal const string FareRoomsJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Home", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "NPC": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Tavern", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "NPC": 248,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 391, "Name": "Guild", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "NPC": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "1/2", "D": "0" }
        ]
        """;

    internal const string FareMonstersJson = """
        [ { "Number": 248, "Name": "the barmaid", "GreetTXT": 40 } ]
        """;

    internal const string FareTbInfoJson = """
        [
          { "Number": 40, "LinkTo": 0, "Action": "adventure:41\n", "Called From": "Monster #248" },
          { "Number": 41, "LinkTo": 0, "Action": "price 1000 318:teleport 391 1:message 1\n", "Called From": "" }
        ]
        """;

    // A pier whose sailing (1 gold) is the only way to the island.
    //
    //   1/1 Home ──N── 1/2 Pier ~secure passage to islea~ 1/10 Isle Port ──N── 1/11 Isle Town
    internal const string BoatRoomsJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Home", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Pier", "CMD": 100,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 10, "Name": "Isle Port", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/11", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 11, "Name": "Isle Town", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/10", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    internal const string BoatTbInfoJson = """
        [
          { "Number": 100, "LinkTo": 0,
            "Action": "secure passage to islea:minlevel 1 0:price 100 0:random 200:text 0\n",
            "Called From": "Room 1/2" },
          { "Number": 200, "LinkTo": 0, "Action": "100:cast 600\n", "Called From": "rndm" }
        ]
        """;

    internal const string BoatSpellsJson = """
        [
          { "Number": 600, "Name": "isle trip", "MinBase": 0, "MaxBase": 0, "Dur": 20,
            "Abil-0": 151, "AbilVal-0": 601 },
          { "Number": 601, "Name": "disembark isle a", "MinBase": 0, "MaxBase": 0,
            "Abil-0": 140, "AbilVal-0": 10 }
        ]
        """;

    private static RoomExit TollExit(int tollGold) =>
        new(new RoomKey(1, 2), RoomExitHint.Toll, RawHint: null, TollGold: tollGold);

    private static RoomExit FareExit(long fareCopper) =>
        new(new RoomKey(16, 637), RoomExitHint.Teleport, RawHint: "greet teleport",
            TextCommands: new[] { "ask someone activate" }, FareCopper: fareCopper);

    private static MovementFilter FilterWithPurse(long? copper) =>
        new(TestProfiles.Service()) { WealthProvider = () => copper };

    // A walker over one of the graphs above, with the toll gate's filter, the line
    // detector that reads the game's refusal, and everything it sent as text. The
    // refusal and the trip's end are wired as AppServices wires them.
    private sealed class Rig
    {
        public required RoomGraphManager Graph { get; init; }
        public required BfsMapper Bfs { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required MovementFilter Filter { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public required MovementRefusalDetector Detector { get; init; }
        // The game's output, fed the way the client receives it.
        public required ServerFeed Feed { get; init; }
        public EngineRecoveryGate? Gate { get; init; }
        public List<string> Sent { get; } = new();
        public List<WalkEvent> Events { get; } = new();
        public List<(RoomKey Destination, string Reason)> Unpaid { get; } = new();
        public List<(RoomExit? Crossing, long? Named)> Refusals { get; } = new();
        // The `i` AppServices.OnPaidCrossingRefused asks for, and its bound.
        public required OwedPurseRead PurseRead { get; init; }
        public int InventoryAsked { get; set; }
        public Action? AnswerBound { get; set; }
        public long? Purse { get; set; }

        // The `i` is answered: the record now holds this much, and the gate and the
        // owed read hear of it in AppServices' order.
        public void AnswerInventory(long purse)
        {
            Purse = purse;
            Filter.NotePurseRead();
            PurseRead.Settle();
        }
    }

    private Rig NewRig(string roomsJson, long? purse, bool recovery = false,
        string? monstersJson = null, string? tbInfoJson = null, string? spellsJson = null)
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
            spellCatalog: spellsJson is null ? null : new KnownSpellCatalog(cache));
        graph.OnActiveSetChanged("alpha");
        BfsMapper bfs = new(graph);
        RoomTracker tracker = new(graph);
        MovementFilter filter = new(TestProfiles.Service());
        EngineRecoveryGate? gate = recovery ? new EngineRecoveryGate(graph, tracker) : null;
        AutoWalkManager walker = new(graph, bfs, tracker, new MovementCoordinator(), filter, recovery: gate);
        if (spellsJson is not null) walker.SetBoatPlanner(new BoatRoutePlanner(graph, bfs));
        ServerFeed feed = new();
        MovementRefusalDetector detector = new(feed.Lines, tracker);
        Rig? made = null;
        OwedPurseRead purseRead = new(
            held: () => false,
            send: () => made!.InventoryAsked++,
            schedule: (_, onBound) =>
            {
                made!.AnswerBound = onBound;
                return new NoTimer();
            });
        Rig rig = made = new()
        {
            Graph = graph, Bfs = bfs, Tracker = tracker, Filter = filter, Walker = walker, Detector = detector, Feed = feed,
            Gate = gate, Purse = purse, PurseRead = purseRead,
        };
        walker.SetPurseReadWait(purseRead.WaitForAnswer);
        filter.WealthProvider = () => rig.Purse;
        filter.TripUnderWayProbe = () => walker.State != WalkState.Idle;
        walker.SetWireSender(bytes => rig.Sent.Add(Encoding.Latin1.GetString(bytes).Trim()));
        walker.SetVoyageScheduler((_, _) => new NoTimer());
        walker.Event += evt =>
        {
            rig.Events.Add(evt);
            if (evt.Kind is WalkEventKind.Finished or WalkEventKind.Failed or WalkEventKind.Stopped)
                filter.NoteTripEnded();
        };
        walker.SetUnpaidCrossingHandler((destination, reason) => rig.Unpaid.Add((destination, reason)));
        detector.PaidCrossingRefused += (crossing, named) =>
        {
            rig.Refusals.Add((crossing, named));
            if (filter.NoteCrossingRefused(crossing, named)) purseRead.Ask();
        };
        return rig;
    }

    private sealed class NoTimer : IDisposable
    {
        public void Dispose() { }
    }

    private static RoomObservation Obs(string name, params Direction[] exits) => new(name, new HashSet<Direction>(exits));

    // ----- the toll gate after a death ------------------------------------------

    [Fact]
    public void AfterADeath_ATollIsNotTaken_OnTheCoinCarriedBeforeIt()
    {
        MovementFilter filter = FilterWithPurse(CoinBeforeTheDeath);
        Assert.False(filter.IsExitBlocked(TollExit(5)));

        filter.NotePurseLostAtDeath();

        Assert.True(filter.IsExitBlocked(TollExit(5)));
        Assert.Equal(ExitBlockReason.Toll, filter.DescribeExitBlock(TollExit(5)));
    }

    // The record is marked stale at a death, and a purse nobody has read is one a
    // toll isn't refused on. A purse known to be gone is not that.
    [Fact]
    public void AfterADeath_AStaleRecord_DoesNotReadAsAPurseNobodyKnows()
    {
        MovementFilter filter = FilterWithPurse(null);
        Assert.False(filter.IsExitBlocked(TollExit(5)));

        filter.NotePurseLostAtDeath();

        Assert.True(filter.IsExitBlocked(TollExit(5)));
    }

    [Fact]
    public void AfterADeath_AFareAndABoatFareAreRefusedToo()
    {
        MovementFilter filter = FilterWithPurse(CoinBeforeTheDeath);
        filter.NotePurseLostAtDeath();

        Assert.Equal(ExitBlockReason.Fare, filter.DescribeExitBlock(FareExit(1_000)));
        BoatPassage boat = new(
            new RoomKey(14, 759), "port", "secure passage to port", new RoomKey(14, 702),
            MinLevel: 0, FareCopper: 1_000, RequiresCheckability: false);
        Assert.False(filter.IsBoatPassable(in boat));
    }

    // Leading a party, the gate goes by the party's poorest purse. A leader who has
    // just died is the poorest whatever that figure says.
    [Fact]
    public void AfterADeath_ThePartysPurse_DoesNotOpenTheToll()
    {
        MovementFilter filter = FilterWithPurse(CoinBeforeTheDeath);
        filter.PartyWealthProvider = () => CoinBeforeTheDeath;
        filter.NotePurseLostAtDeath();

        Assert.True(filter.IsExitBlocked(TollExit(5)));
    }

    [Fact]
    public void AfterADeath_TheNextInventoryRead_PutsTheRecordBackInCharge()
    {
        long? purse = CoinBeforeTheDeath;
        MovementFilter filter = new(TestProfiles.Service()) { WealthProvider = () => purse };
        filter.NotePurseLostAtDeath();

        purse = 0;                       // what the `i` at the graveyard reads
        filter.NotePurseRead();
        Assert.Equal("you carry 0 copper", filter.DescribePurse());
        Assert.True(filter.IsExitBlocked(TollExit(5)));

        purse = 500;                     // the corpse recovered, read again
        filter.NotePurseRead();
        Assert.False(filter.IsExitBlocked(TollExit(5)));
    }

    [Fact]
    public void AfterADeath_AnotherCharactersProfile_StartsWithoutTheDoubt()
    {
        ProfileService profile = TestProfiles.Service();
        MovementFilter filter = new(profile) { WealthProvider = () => CoinBeforeTheDeath };
        filter.NotePurseLostAtDeath();
        filter.NoteCrossingRefused(FareExit(1_000), 1_000);

        profile.LoadDefaultProfile();

        Assert.False(filter.IsExitBlocked(TollExit(5)));
        Assert.False(filter.IsExitBlocked(FareExit(1_000)));
    }

    // A trip is priced with the tolls it would pay once the money is fetched, so
    // that pass stands the gate down whatever the purse.
    [Fact]
    public void AfterADeath_ATripsTollsAreStillPriced()
    {
        Rig rig = NewRig(TollTheOnlyWayJson, CoinBeforeTheDeath);
        rig.Filter.NotePurseLostAtDeath();

        Assert.Equal(500, rig.Filter.TollCopperOnRoute(rig.Bfs, new RoomKey(1, 1), new RoomKey(1, 3)));
        Assert.Null(rig.Bfs.FindPath(new RoomKey(1, 1), new RoomKey(1, 3), rig.Filter));
    }

    // ----- a death in an arena room ---------------------------------------------

    // An arena death takes no item, coin or key (user, 2026-10-10), so tolls are
    // not closed for it. The room is the one died in, not the graveyard.
    [Fact]
    public void AnArenaDeath_ClosesNoToll()
    {
        MovementFilter filter = FilterWithPurse(CoinBeforeTheDeath);

        filter.NoteDeath(new RoomKey(16, 500), tookNothing: true);
        Assert.Equal("you carry 2 runic 5 platinum 99 gold", filter.DescribePurse());
        Assert.False(filter.IsExitBlocked(TollExit(5)));

        filter.NoteDeath(new RoomKey(17, 25), tookNothing: false);
        Assert.True(filter.IsExitBlocked(TollExit(5)));
    }

    // Paradigm: the room decides, as the owner ruled; no line is on record there.
    [Fact]
    public void OnParadigm_ADeathInAnArenaRoom_TookNothing()
    {
        Assert.True(ArenaDeathRooms.DeathTookNothing(new RoomKey(16, 500), colliseumLineSeen: false, paradigm: true));
        Assert.False(ArenaDeathRooms.DeathTookNothing(new RoomKey(17, 25), colliseumLineSeen: false, paradigm: true));
        Assert.False(ArenaDeathRooms.DeathTookNothing(diedIn: null, colliseumLineSeen: false, paradigm: true));
    }

    // Stock: the engine says so itself, and only with its arena switch on. An arena
    // room with the switch off prints the ordinary lines and is an ordinary death.
    [Fact]
    public void OnStock_TheColliseumLineDecides_NotTheRoom()
    {
        Assert.True(ArenaDeathRooms.DeathTookNothing(new RoomKey(16, 500), colliseumLineSeen: true, paradigm: false));
        Assert.False(ArenaDeathRooms.DeathTookNothing(new RoomKey(16, 500), colliseumLineSeen: false, paradigm: false));
        Assert.True(ArenaDeathRooms.DeathTookNothing(diedIn: null, colliseumLineSeen: true, paradigm: false));
    }

    [Fact]
    public void TheDeathDetector_TellsAColliseumDeath_ByTheEnginesLine()
    {
        Rig rig = NewRig(TollWithAWayRoundJson, CoinBeforeTheDeath);
        MudPlay.Game.DeathDetector deaths = new(rig.Tracker);
        deaths.AttachLineExtractor(rig.Feed.Lines);
        List<bool> saved = new();
        rig.Tracker.PlayerDeathObserved += () => saved.Add(deaths.LastDeathSavedInColliseum);

        rig.Tracker.SetLocated(new RoomKey(1, 1));
        rig.Feed.Line("You have been killed!");
        rig.Feed.Line("But, because you were in a colliseum, you have been saved.");
        rig.Feed.Line("You have 6 lives left.");

        rig.Tracker.SetLocated(new RoomKey(1, 1));
        rig.Feed.Line("You have been killed!");
        rig.Feed.Line("But, due to a miracle, you have been saved.");
        rig.Feed.Line("You have 5 lives left.");

        Assert.Equal(new[] { true, false }, saved);
    }

    [Theory]
    [InlineData(1, 206, true)]
    [InlineData(1, 2519, true)]
    [InlineData(6, 1351, true)]
    [InlineData(11, 1, true)]
    [InlineData(11, 34, true)]
    [InlineData(11, 35, false)]
    [InlineData(11, 36, true)]
    [InlineData(11, 43, true)]
    [InlineData(11, 44, false)]
    [InlineData(16, 478, false)]
    [InlineData(16, 479, true)]
    [InlineData(16, 542, true)]
    [InlineData(16, 543, false)]
    [InlineData(17, 2426, true)]
    [InlineData(17, 2432, true)]
    [InlineData(17, 2433, false)]
    [InlineData(17, 2572, true)]
    [InlineData(17, 2573, true)]
    [InlineData(1, 2150, false)]     // Newhaven's Arena is the engine's other room type
    [InlineData(1, 2189, false)]     // the graveyard
    public void TheArenaRooms_AreTheEnginesColliseumRooms(int map, int room, bool safe) =>
        Assert.Equal(safe, ArenaDeathRooms.Contains(new RoomKey(map, room)));

    [Fact]
    public void TheArenaRooms_NumberOneHundredAndEighteen()
    {
        int count = 0;
        for (int map = 1; map <= 20; map++)
            for (int room = 1; room <= 3000; room++)
                if (ArenaDeathRooms.Contains(new RoomKey(map, room))) count++;

        Assert.Equal(118, count);
    }

    [Fact]
    public void TheTracker_HandsADeathsListenersTheRoomDiedIn()
    {
        Rig rig = NewRig(TollWithAWayRoundJson, CoinBeforeTheDeath);
        rig.Tracker.SetLocated(new RoomKey(1, 4));
        RoomKey? heard = null;
        Room? heldWhenRaised = rig.Tracker.State.CurrentRoom;
        rig.Tracker.PlayerDeathObserved += () =>
        {
            heard = rig.Tracker.LastDeathRoom;
            heldWhenRaised = rig.Tracker.State.CurrentRoom;
        };

        rig.Tracker.NoteDeath(6, "You have 6 lives left.");

        Assert.Equal(new RoomKey(1, 4), heard);
        Assert.Null(heldWhenRaised);          // why the event can't be asked for it
    }

    // ----- a crossing the game refuses -----------------------------------------

    [Fact]
    public void ARefusedToll_TheRecordCovered_ClosesEveryTollUntilTheInventoryIsRead()
    {
        MovementFilter filter = FilterWithPurse(CoinBeforeTheDeath);

        Assert.True(filter.NoteCrossingRefused(TollExit(5), 500));

        Assert.True(filter.IsExitBlocked(TollExit(5)));
        Assert.True(filter.IsExitBlocked(TollExit(1)));
        Assert.False(filter.NoteCrossingRefused(TollExit(5), 500));    // one re-read per refusal, not two

        filter.NotePurseRead();                                        // the record did cover it
        Assert.False(filter.IsExitBlocked(TollExit(5)));
    }

    // Walking into a toll by hand with an empty purse tells the client nothing new
    // about the purse; the exit is closed all the same.
    [Fact]
    public void ARefusedToll_TheRecordAlreadyShort_IsNoContradiction()
    {
        MovementFilter filter = FilterWithPurse(499);

        Assert.False(filter.NoteCrossingRefused(TollExit(5), 500));

        Assert.True(filter.IsExitBlocked(TollExit(5)));
        Assert.False(filter.IsExitBlocked(TollExit(4)));     // 400 is still affordable
    }

    // A record that already said "short" was right, so it is believed as it goes
    // on changing: carry 3 gold, walk into the 5-gold toll, pick up coin to 50 gold.
    // The toll stayed closed until an `i` nobody was asked to send.
    [Fact]
    public void ARefusedToll_TheRecordAlreadyShort_OpensWhenTheRecordCoversThePrice_WithNoRead()
    {
        long? purse = 300;
        MovementFilter filter = new(TestProfiles.Service()) { WealthProvider = () => purse };
        Assert.False(filter.NoteCrossingRefused(TollExit(5), 500));
        Assert.True(filter.IsExitBlocked(TollExit(5)));
        Assert.Equal(
            "the game refused it, naming 5 gold: you carry 3 gold, 2 gold short",
            filter.DescribePurseFor(TollExit(5)));

        purse = 5_000;                    // picked up since: no `i`

        Assert.False(filter.IsExitBlocked(TollExit(5)));
        Assert.Equal("you carry 50 gold", filter.DescribePurseFor(TollExit(5)));
    }

    // The report's own sequel: refused on a record that was wrong, the `i` shows 2
    // gold, then the corpse's coin comes back as pickup lines. One full read since
    // the refusal makes the record good again; no second one is waited for.
    [Fact]
    public void ARefusedToll_TheRecordWrong_OpensOnPickups_OnceOneReadHasLanded()
    {
        long? purse = CoinBeforeTheDeath;
        MovementFilter filter = new(TestProfiles.Service()) { WealthProvider = () => purse };
        Assert.True(filter.NoteCrossingRefused(TollExit(5), 500));

        purse = 20_000;                   // the record patched before any read: not believed yet
        Assert.True(filter.IsExitBlocked(TollExit(5)));

        purse = 200;
        filter.NotePurseRead();           // the `i`: 2 gold
        Assert.True(filter.IsExitBlocked(TollExit(5)));

        purse = 20_000;                   // the corpse's coin, picked up
        Assert.False(filter.IsExitBlocked(TollExit(5)));
    }

    [Fact]
    public void RefusedCrossings_AreForgotten_WithTheGameDataSetTheyWereKeyedOn()
    {
        MovementFilter filter = FilterWithPurse(1_000);
        filter.NoteCrossingRefused(TollExit(5), 5_000);
        Assert.True(filter.IsExitBlocked(TollExit(5)));

        filter.ForgetRefusedCrossings();

        Assert.False(filter.IsExitBlocked(TollExit(5)));
    }

    // The game's word outranks the data: a toll the data has at 5 gold and the
    // game refuses for 50 stays closed until the record covers 50, whatever the
    // record says of 5, and keeps that price over later reads.
    [Fact]
    public void ARefusedToll_TheGamePricesAboveTheData_StaysClosedUntilTheRecordCoversTheGamesPrice()
    {
        long? purse = 1_000;
        MovementFilter filter = new(TestProfiles.Service()) { WealthProvider = () => purse };

        Assert.False(filter.NoteCrossingRefused(TollExit(5), 5_000));   // the record agrees it is short

        Assert.True(filter.IsExitBlocked(TollExit(5)));
        Assert.False(filter.IsExitBlocked(TollExit(4)));                // another toll goes by the record
        Assert.Contains("naming 50 gold", filter.DescribePurseFor(TollExit(5)));
        Assert.Contains("you carry 10 gold", filter.DescribePurseFor(TollExit(5)));

        purse = 4_999;                    // coin picked up since
        Assert.True(filter.IsExitBlocked(TollExit(5)));
        filter.NotePurseRead();           // a full read, still short of the game's price
        Assert.True(filter.IsExitBlocked(TollExit(5)));

        purse = 5_000;
        Assert.False(filter.IsExitBlocked(TollExit(5)));

        purse = 600;                      // spent again: the data's 5 gold would pass it
        Assert.True(filter.IsExitBlocked(TollExit(5)));
    }

    // A wording that names no price leaves nothing to compare a read with: the
    // exit opens again on the data's price, but not before the trip is over.
    [Fact]
    public void ARefusedToll_InAWordingWithNoPrice_StaysClosedForTheTrip()
    {
        bool tripUnderWay = true;
        MovementFilter filter = FilterWithPurse(CoinBeforeTheDeath);
        filter.TripUnderWayProbe = () => tripUnderWay;

        Assert.True(filter.NoteCrossingRefused(TollExit(5), namedCopper: null));
        filter.NotePurseRead();

        Assert.True(filter.IsExitBlocked(TollExit(5)));
        Assert.False(filter.IsExitBlocked(TollExit(4)));     // the doubt went with the read
        Assert.Contains("isn't tried again on this trip", filter.DescribePurseFor(TollExit(5)));

        tripUnderWay = false;
        filter.NoteTripEnded();
        Assert.False(filter.IsExitBlocked(TollExit(5)));
    }

    [Fact]
    public void ARefusedToll_InAWordingWithNoPrice_WithNoTripUnderWay_OpensAtTheRead()
    {
        MovementFilter filter = FilterWithPurse(CoinBeforeTheDeath);

        filter.NoteCrossingRefused(TollExit(5), namedCopper: null);
        Assert.True(filter.IsExitBlocked(TollExit(5)));

        filter.NotePurseRead();
        Assert.False(filter.IsExitBlocked(TollExit(5)));
    }

    [Fact]
    public void ARefusedFare_ClosesTheTransport_AndDoubtsThePurse()
    {
        MovementFilter filter = FilterWithPurse(CoinBeforeTheDeath);

        Assert.True(filter.NoteCrossingRefused(FareExit(1_000), 1_000));

        Assert.Equal(ExitBlockReason.Fare, filter.DescribeExitBlock(FareExit(1_000)));
        Assert.True(filter.IsExitBlocked(TollExit(5)));
    }

    // More than one move queued: the refusal can't be tied to an exit, and still
    // says the purse is wrong.
    [Fact]
    public void ARefusal_TiedToNoExit_StillDoubtsThePurse()
    {
        MovementFilter filter = FilterWithPurse(CoinBeforeTheDeath);

        Assert.True(filter.NoteCrossingRefused(crossing: null, 500));

        Assert.True(filter.IsExitBlocked(TollExit(5)));
        filter.NotePurseRead();
        Assert.False(filter.IsExitBlocked(TollExit(5)));
    }

    // ----- what the walk says ---------------------------------------------------

    [Fact]
    public void ThePurse_IsDescribedAgainstWhatAnExitCharges()
    {
        MovementFilter filter = FilterWithPurse(300);
        Assert.Equal("you carry 3 gold, 2 gold short", filter.DescribePurseFor(TollExit(5)));
        Assert.Equal("you carry 3 gold", filter.DescribePurseFor(TollExit(3)));
        Assert.Equal("you carry 3 gold, 7 gold short", filter.DescribePurseFor(FareExit(1_000)));
        Assert.Null(filter.DescribePurseFor(new RoomExit(new RoomKey(1, 2), RoomExitHint.None, RawHint: null)));

        // A train trip's fees are set aside before a toll is judged.
        filter.ReservedCopper = 200;
        Assert.Equal(
            "you carry 3 gold, 2 gold of it set aside for fees ahead, 4 gold short",
            filter.DescribePurseFor(TollExit(5)));
        filter.ReservedCopper = 0;

        // Leading a party: a fare and a toll are both described on our own purse
        // (who is short among the party is the party's check to say), and the
        // poorest known purse is the bug report's line.
        filter.PartyWealthProvider = () => 120;
        Assert.Equal("you carry 3 gold, 2 gold short", filter.DescribePurseFor(FareExit(500)));
        Assert.Equal("you carry 3 gold, 2 gold short", filter.DescribePurseFor(TollExit(5)));
        Assert.Equal("the party's poorest known purse holds 1 gold 2 silver", filter.DescribePurse());
        filter.PartyTollClosedReason = _ => "a member holds 1 gold 2 silver, 8 silver short";
        Assert.Equal("a member holds 1 gold 2 silver, 8 silver short", filter.DescribePurseFor(FareExit(200)));
        filter.PartyTollClosedReason = null;
        filter.PartyWealthProvider = null;

        filter.NotePurseLostAtDeath();
        Assert.Contains("went with the deathpile", filter.DescribePurseFor(TollExit(5)));
        Assert.Contains("went with the deathpile", filter.DescribePurse());

        filter.NotePurseRead();
        filter.NoteCrossingRefused(TollExit(2), 200);     // the 3 gold on record covered it
        Assert.Contains("the game refused a toll or fare the client took the purse to cover", filter.DescribePurseFor(TollExit(5)));
        Assert.Contains("refused crossings: into 1/2 (the game named 2 gold, inventory not read since)", filter.DescribePurse());

        Assert.Contains("isn't known", FilterWithPurse(null).DescribePurse());
    }

    // ----- the game's refusal lines --------------------------------------------

    [Fact]
    public void TheTollLine_ReportsTheExitAndItsPrice_BeforeTheMoveIsReverted()
    {
        Rig rig = NewRig(TollWithAWayRoundJson, purse: null);
        rig.Tracker.SetLocated(new RoomKey(1, 1));
        rig.Tracker.NoteMoveSent(Direction.E);
        RoomConfidence? whenRaised = null;
        rig.Detector.PaidCrossingRefused += (_, _) => whenRaised = rig.Tracker.State.Confidence;

        rig.Feed.Line(Refusal);

        (RoomExit? crossing, long? named) = Assert.Single(rig.Refusals);
        Assert.Equal(new RoomKey(1, 2), crossing!.Value.Target);
        Assert.Equal(5, crossing.Value.TollGold);
        Assert.Equal(500, named);
        // The walker re-plans off the revert, so the gate has to hear first.
        Assert.Equal(RoomConfidence.Pending, whenRaised);
        Assert.Equal(RoomConfidence.Confirmed, rig.Tracker.State.Confidence);
    }

    [Fact]
    public void TheTollLine_WithNoMoveInFlight_ReportsNothing()
    {
        Rig rig = NewRig(TollWithAWayRoundJson, purse: null);
        rig.Tracker.SetLocated(new RoomKey(1, 1));

        rig.Feed.Line(Refusal);

        Assert.Empty(rig.Refusals);
    }

    [Fact]
    public void TheTollLine_InAnotherCoin_NamesNoPrice()
    {
        Rig rig = NewRig(TollWithAWayRoundJson, purse: null);
        rig.Tracker.SetLocated(new RoomKey(1, 1));
        rig.Tracker.NoteMoveSent(Direction.E);

        rig.Feed.Line("You do not have enough to cover the toll of 5 platinum pieces.");

        (RoomExit? crossing, long? named) = Assert.Single(rig.Refusals);
        Assert.NotNull(crossing);
        Assert.Null(named);
    }

    // The NPC's line also answers a `price` on a command that goes nowhere, and a
    // cardinal in flight is not what it refused.
    [Fact]
    public void ThePriceLine_WithACardinalInFlight_ReportsNothing()
    {
        Rig rig = NewRig(TollWithAWayRoundJson, purse: null);
        rig.Tracker.SetLocated(new RoomKey(1, 1));
        rig.Tracker.NoteMoveSent(Direction.N);

        rig.Feed.Line(PriceRefusal);

        Assert.Empty(rig.Refusals);
        Assert.Equal(RoomConfidence.Pending, rig.Tracker.State.Confidence);
    }

    // ----- the walk --------------------------------------------------------------

    [Fact]
    public void AWalk_WithThePurseOnRecord_PaysTheToll()
    {
        Rig rig = NewRig(TollWithAWayRoundJson, CoinBeforeTheDeath);
        rig.Tracker.SetLocated(new RoomKey(1, 1));

        Assert.True(rig.Walker.WalkTo(new RoomKey(1, 2)));

        Assert.Equal(new[] { "e" }, rig.Sent);
    }

    [Fact]
    public void AWalk_StartedAfterADeath_GoesRoundTheToll()
    {
        Rig rig = NewRig(TollWithAWayRoundJson, CoinBeforeTheDeath);
        rig.Tracker.SetLocated(new RoomKey(1, 1));
        rig.Filter.NotePurseLostAtDeath();

        Assert.True(rig.Walker.WalkTo(new RoomKey(1, 2)));

        Assert.Equal(new[] { "n" }, rig.Sent);
        Assert.Empty(rig.Unpaid);
    }

    [Fact]
    public void AWalk_StartedAfterADeath_WithNoWayRoundTheToll_DoesNotStart_AndNamesTheTollAndThePurse()
    {
        Rig rig = NewRig(TollTheOnlyWayJson, CoinBeforeTheDeath);
        rig.Tracker.SetLocated(new RoomKey(1, 1));
        rig.Filter.NotePurseLostAtDeath();

        Assert.False(rig.Walker.WalkTo(new RoomKey(1, 3)));

        Assert.Empty(rig.Sent);
        WalkEvent failed = Assert.Single(rig.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Equal(
            "all routes blocked by a toll east from 1/2 (Bailey) (5 gold) you can't pay: "
            + "your coin went with the deathpile and the inventory hasn't been read since (type i)",
            failed.Detail);
        (RoomKey destination, string reason) = Assert.Single(rig.Unpaid);
        Assert.Equal(new RoomKey(1, 3), destination);
        Assert.Equal(failed.Detail, reason);
    }

    [Fact]
    public void AWalk_WithAPurseKnownShort_NamesTheShortfall()
    {
        Rig rig = NewRig(TollTheOnlyWayJson, purse: 300);
        rig.Tracker.SetLocated(new RoomKey(1, 1));

        Assert.False(rig.Walker.WalkTo(new RoomKey(1, 3)));

        WalkEvent failed = Assert.Single(rig.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Equal(
            "all routes blocked by a toll east from 1/2 (Bailey) (5 gold) you can't pay: you carry 3 gold, 2 gold short",
            failed.Detail);
        Assert.Single(rig.Unpaid);
    }

    // The report's walk, with a record that is wrong for any reason: the first
    // refusal asks for the inventory once, and when it answers the walk re-plans
    // round the toll rather than sending the same step again.
    [Fact]
    public void AWalk_RefusedAtATollTheRecordCovered_ReadsThePurse_ThenRePlansRoundIt_WithoutASecondTry()
    {
        Rig rig = NewRig(TollWithAWayRoundJson, CoinBeforeTheDeath);
        rig.Tracker.SetLocated(new RoomKey(1, 1));
        rig.Walker.WalkTo(new RoomKey(1, 2));
        Assert.Equal(new[] { "e" }, rig.Sent);

        rig.Feed.Line(Refusal);
        Assert.Equal(new[] { "e" }, rig.Sent);              // the re-plan waits for the read
        Assert.Equal(1, rig.InventoryAsked);
        Assert.Equal(WalkState.Walking, rig.Walker.State);

        rig.AnswerInventory(0);

        Assert.Equal(new[] { "e", "n" }, rig.Sent);
        Assert.Equal(1, rig.InventoryAsked);
        Assert.Equal(WalkState.Walking, rig.Walker.State);
        Assert.DoesNotContain(rig.Events, e => e.Kind == WalkEventKind.Failed);
    }

    // Two tolls on the way, the first with a way round and the second without, and
    // a purse that covers the second but not the first. Re-planned ahead of the
    // read, every toll was closed and the walk stopped at the second, which the
    // purse could pay.
    [Fact]
    public void AWalk_RefusedAtAToll_RePlansOnThePurseAsRead_NotOnTheDoubt()
    {
        Rig rig = NewRig(TwoTollsJson, CoinBeforeTheDeath);
        rig.Tracker.SetLocated(new RoomKey(1, 1));
        rig.Walker.WalkTo(new RoomKey(1, 5));
        Assert.Equal(new[] { "e" }, rig.Sent);

        rig.Feed.Line(Refusal);
        rig.AnswerInventory(200);                           // 2 gold: short of 5, enough for 1

        Assert.Equal(new[] { "e", "n" }, rig.Sent);         // round the first, on to the second
        Assert.DoesNotContain(rig.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Empty(rig.Unpaid);
    }

    // The read never answers: past its bound the walk re-plans all the same, on
    // the purse in doubt.
    [Fact]
    public void AWalk_RefusedAtAToll_WhoseReadNeverAnswers_RePlansWhenTheBoundPasses()
    {
        Rig rig = NewRig(TollWithAWayRoundJson, CoinBeforeTheDeath);
        rig.Tracker.SetLocated(new RoomKey(1, 1));
        rig.Walker.WalkTo(new RoomKey(1, 2));
        rig.Feed.Line(Refusal);
        Assert.Equal(new[] { "e" }, rig.Sent);

        rig.AnswerBound!();

        Assert.Equal(new[] { "e", "n" }, rig.Sent);
    }

    // A walk stopped while its re-plan waits is not set walking again by the answer.
    [Fact]
    public void AWalk_StoppedWhileItWaitsForTheRead_StaysStopped()
    {
        Rig rig = NewRig(TollWithAWayRoundJson, CoinBeforeTheDeath);
        rig.Tracker.SetLocated(new RoomKey(1, 1));
        rig.Walker.WalkTo(new RoomKey(1, 2));
        rig.Feed.Line(Refusal);

        rig.Walker.Stop("test");
        rig.AnswerInventory(0);

        Assert.Equal(new[] { "e" }, rig.Sent);
        Assert.Equal(WalkState.Idle, rig.Walker.State);
    }

    [Fact]
    public void AWalk_RefusedAtTheOnlyTollOut_StopsThere_AndSaysWhy()
    {
        Rig rig = NewRig(TollTheOnlyWayJson, CoinBeforeTheDeath);
        rig.Tracker.SetLocated(new RoomKey(1, 1));
        rig.Walker.WalkTo(new RoomKey(1, 3));
        rig.Tracker.NoteRoomObserved(Obs("Bailey", Direction.E, Direction.W));
        Assert.Equal(new[] { "e", "e" }, rig.Sent);

        rig.Feed.Line(Refusal);
        rig.AnswerInventory(200);

        Assert.Equal(new[] { "e", "e" }, rig.Sent);         // not a third
        Assert.Equal(WalkState.Idle, rig.Walker.State);
        WalkEvent failed = Assert.Single(rig.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Equal(
            "all routes blocked by a toll east from 1/2 (Bailey) (5 gold) you can't pay: you carry 2 gold, 3 gold short",
            failed.Detail);
        Assert.Single(rig.Unpaid);
    }

    // The same with no answer inside the bound: stopped on the doubt, and saying so.
    [Fact]
    public void AWalk_RefusedAtTheOnlyTollOut_WithNoAnswer_StopsOnTheDoubt()
    {
        Rig rig = NewRig(TollTheOnlyWayJson, CoinBeforeTheDeath);
        rig.Tracker.SetLocated(new RoomKey(1, 2));
        rig.Walker.WalkTo(new RoomKey(1, 3));
        rig.Feed.Line(Refusal);

        rig.AnswerBound!();

        Assert.Equal(new[] { "e" }, rig.Sent);
        WalkEvent failed = Assert.Single(rig.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Contains("the game refused a toll or fare the client took the purse to cover", failed.Detail);
    }

    // The game charges more than the data says (50 gold where the data has 5) and
    // the record holds 10: nothing contradicts the record, so no `i`, and the step
    // went out six times before the walk gave up. Now once; and the next walk
    // doesn't plan through the toll either, until a read shows the game's price.
    [Fact]
    public void AWalk_RefusedAtATollTheGamePricesAboveTheData_IsNotSentThereAgain_NorIsTheNextWalk()
    {
        Rig rig = NewRig(TollWithAWayRoundJson, purse: 1_000);
        rig.Tracker.SetLocated(new RoomKey(1, 1));
        rig.Walker.WalkTo(new RoomKey(1, 2));

        rig.Feed.Line("You do not have enough to cover the toll of 50 gold crowns.");

        Assert.Equal(new[] { "e", "n" }, rig.Sent);
        Assert.Equal(0, rig.InventoryAsked);
        Assert.DoesNotContain(rig.Events, e => e.Kind == WalkEventKind.Failed);

        rig.Walker.Stop("test");
        rig.Tracker.SetLocated(new RoomKey(1, 1));
        rig.Sent.Clear();
        rig.Walker.WalkTo(new RoomKey(1, 2));
        Assert.Equal(new[] { "n" }, rig.Sent);

        rig.Walker.Stop("test");
        rig.Tracker.SetLocated(new RoomKey(1, 1));
        rig.Purse = 5_000;
        rig.Filter.NotePurseRead();
        rig.Sent.Clear();
        rig.Walker.WalkTo(new RoomKey(1, 2));
        Assert.Equal(new[] { "e" }, rig.Sent);
    }

    // Refused by hand with 3 gold, with no way round: the walk says what the game
    // named and what is carried. Coin picked up to 50 gold, with no `i`: the next
    // walk pays the toll (it used to stay closed for want of a read nobody sent).
    [Fact]
    public void AWalk_AfterATollRefusedByHand_PaysIt_OnceTheRecordCoversThePrice()
    {
        Rig rig = NewRig(TollTheOnlyWayJson, purse: 300);
        rig.Tracker.SetLocated(new RoomKey(1, 2));
        rig.Tracker.NoteMoveSent(Direction.E);
        rig.Feed.Line(Refusal);
        Assert.Equal(0, rig.InventoryAsked);

        Assert.False(rig.Walker.WalkTo(new RoomKey(1, 3)));
        Assert.Equal(
            "all routes blocked by a toll east from 1/2 (Bailey) (5 gold) you can't pay: "
            + "the game refused it, naming 5 gold: you carry 3 gold, 2 gold short",
            Assert.Single(rig.Events, e => e.Kind == WalkEventKind.Failed).Detail);

        rig.Purse = 5_000;
        Assert.True(rig.Walker.WalkTo(new RoomKey(1, 3)));
        Assert.Equal(new[] { "e" }, rig.Sent);
        Assert.Equal(0, rig.InventoryAsked);
    }

    // A wording that names no price, a record that covers the data's price, and
    // Paradigm's `rm` between the refusal and the re-plan: the `i` answer came back
    // first and re-opened the toll, so the re-plan went straight back into it
    // (three sends, three `i`). The exit stays closed for the walk, on one `i`.
    [Fact]
    public void AWalk_RefusedInAWordingWithNoPrice_DoesNotGoBackOnTheSameWalk()
    {
        Rig rig = NewRig(TollWithAWayRoundJson, CoinBeforeTheDeath, recovery: true);
        List<Action> locateAnswers = new();
        rig.Gate!.TryResyncOnce = (_, onResolved, _) =>
        {
            locateAnswers.Add(() =>
            {
                rig.Tracker.SetLocated(new RoomKey(1, 1));
                onResolved(new RoomKey(1, 1));
            });
            return true;
        };
        rig.Tracker.SetLocated(new RoomKey(1, 1));
        rig.Walker.WalkTo(new RoomKey(1, 2));

        rig.Feed.Line("You do not have enough to cover the toll of 5 platinum pieces.");
        Assert.Equal(new[] { "e" }, rig.Sent);             // waiting on the locate
        rig.AnswerInventory(CoinBeforeTheDeath);            // the `i` answer: the record unchanged
        foreach (Action answer in locateAnswers.ToArray()) answer();

        Assert.Equal(new[] { "e", "n" }, rig.Sent);
        Assert.Equal(1, rig.InventoryAsked);

        // The walk over, the toll is judged on the data again.
        rig.Walker.Stop("test");
        rig.Tracker.SetLocated(new RoomKey(1, 1));
        rig.Sent.Clear();
        rig.Walker.WalkTo(new RoomKey(1, 2));
        Assert.Equal(new[] { "e" }, rig.Sent);
    }

    // ----- an NPC's fare ----------------------------------------------------------

    [Fact]
    public void AWalk_WhoseOnlyWayIsAFareItCantPay_DoesNotStart_AndNamesTheFare()
    {
        Rig rig = NewRig(FareRoomsJson, purse: 300, monstersJson: FareMonstersJson, tbInfoJson: FareTbInfoJson);
        rig.Tracker.SetLocated(new RoomKey(1, 1));

        Assert.False(rig.Walker.WalkTo(new RoomKey(1, 391)));

        Assert.Empty(rig.Sent);
        WalkEvent failed = Assert.Single(rig.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Equal(
            "all routes blocked by a paid transport from 1/2 (Tavern) (10 gold per person) you can't pay: "
            + "you carry 3 gold, 7 gold short",
            failed.Detail);
        Assert.Single(rig.Unpaid);
    }

    [Fact]
    public void AWalk_RefusedByAnNpcForItsFare_AsksOnce_AndStopsNamingTheFare()
    {
        Rig rig = NewRig(FareRoomsJson, CoinBeforeTheDeath, monstersJson: FareMonstersJson, tbInfoJson: FareTbInfoJson);
        rig.Tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(rig.Walker.WalkTo(new RoomKey(1, 391)));
        rig.Tracker.NoteRoomObserved(Obs("Tavern", Direction.S));
        Assert.Equal(new[] { "n", "ask barmaid adventure" }, rig.Sent);

        rig.Feed.Line(PriceRefusal);
        rig.AnswerInventory(300);

        (RoomExit? crossing, long? named) = Assert.Single(rig.Refusals);
        Assert.Equal(new RoomKey(1, 391), crossing!.Value.Target);
        Assert.Equal(1_000, named);
        Assert.Equal(1, rig.InventoryAsked);
        Assert.Equal(new[] { "n", "ask barmaid adventure" }, rig.Sent);     // not asked twice
        Assert.Equal(WalkState.Idle, rig.Walker.State);
        WalkEvent failed = Assert.Single(rig.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Contains("a paid transport from 1/2 (Tavern) (10 gold per person) you can't pay", failed.Detail);
        Assert.Single(rig.Unpaid);
    }

    // ----- a sailing's fare -------------------------------------------------------

    [Fact]
    public void AWalk_WhoseOnlyWayIsASailingItCanPay_Sails()
    {
        Rig rig = NewRig(BoatRoomsJson, CoinBeforeTheDeath, tbInfoJson: BoatTbInfoJson, spellsJson: BoatSpellsJson);
        rig.Tracker.SetLocated(new RoomKey(1, 1));

        Assert.True(rig.Walker.WalkTo(new RoomKey(1, 11)));
        rig.Tracker.NoteRoomObserved(Obs("Pier", Direction.S));

        Assert.Equal(new[] { "n", "secure passage to islea" }, rig.Sent);
    }

    // The captain leaves whoever can't pay on the dock, so a walk whose only way is
    // a sailing it can't pay for doesn't go and stand there.
    [Fact]
    public void AWalk_WhoseOnlyWayIsASailingItCantPay_DoesNotSetOut_AndNamesTheFare()
    {
        Rig rig = NewRig(BoatRoomsJson, CoinBeforeTheDeath, tbInfoJson: BoatTbInfoJson, spellsJson: BoatSpellsJson);
        rig.Tracker.SetLocated(new RoomKey(1, 1));
        rig.Filter.NotePurseLostAtDeath();

        Assert.False(rig.Walker.WalkTo(new RoomKey(1, 11)));

        Assert.Empty(rig.Sent);
        WalkEvent failed = Assert.Single(rig.Events, e => e.Kind == WalkEventKind.Failed);
        Assert.Equal(
            "all routes blocked by the boat 'secure passage to islea' from 1/2 (Pier) (1 gold per person) you can't pay: "
            + "your coin went with the deathpile and the inventory hasn't been read since (type i)",
            failed.Detail);
        Assert.Single(rig.Unpaid);
    }

    // Leading a party, with our own purse plenty: a follower's short reading alone
    // closes nothing, since the party's check asks at the dock and may pay for
    // them. Once that check has closed the sailing to the party, the walk doesn't
    // set out for it, and says who can't pay (user, 2026-10-10: a ride is checked
    // as a toll is).
    [Fact]
    public void AWalk_WhoseOnlyWayIsASailingThePartyCantAllPay_DoesNotSetOut_AndNamesWho()
    {
        Rig rig = NewRig(BoatRoomsJson, CoinBeforeTheDeath, tbInfoJson: BoatTbInfoJson, spellsJson: BoatSpellsJson);
        rig.Filter.PartyWealthProvider = () => 50;
        rig.Tracker.SetLocated(new RoomKey(1, 1));

        Assert.True(rig.Walker.WalkTo(new RoomKey(1, 11)));
        Assert.Equal(new[] { "n" }, rig.Sent);
        Assert.Empty(rig.Unpaid);

        rig.Walker.Stop("test");
        rig.Sent.Clear();
        rig.Filter.PartyTollClosedProbe = exit => exit.FareCopper == 100;
        rig.Filter.PartyTollClosedReason = exit => exit.FareCopper == 100 ? "a member holds 5 silver, 5 silver short" : null;
        rig.Tracker.SetLocated(new RoomKey(1, 1));

        Assert.False(rig.Walker.WalkTo(new RoomKey(1, 11)));
        Assert.Empty(rig.Sent);
        Assert.Equal(
            "all routes blocked by the boat 'secure passage to islea' from 1/2 (Pier) (1 gold per person) "
            + "the party can't all pay: a member holds 5 silver, 5 silver short",
            Assert.Single(rig.Unpaid).Reason);
    }

    // ----- a queued walk's route card ------------------------------------------
    // Run on a queued destination plans through RouteChoicePlanner with the same
    // filter before it hands the walk to the walker.

    [Fact]
    public void AQueuedWalk_AfterADeath_IsPlannedRoundTheToll_WithNoCard()
    {
        Rig rig = NewRig(TollWithAWayRoundJson, CoinBeforeTheDeath);
        rig.Filter.NotePurseLostAtDeath();
        RoomKey from = new(1, 1), to = new(1, 2);

        Assert.Null(RouteChoicePlanner.Evaluate(rig.Bfs, rig.Filter, rig.Graph, from, to));
        Assert.Null(RouteChoicePlanner.PlanBlocked(rig.Bfs, rig.Filter, rig.Graph, from, to));
        Assert.Equal(
            new[] { Direction.N, Direction.E, Direction.S },
            rig.Bfs.FindPath(from, to, rig.Filter));
    }

    [Fact]
    public void AQueuedWalk_AfterADeath_WithNoWayRound_IsOfferedOnlyAsFarAsTheToll()
    {
        Rig rig = NewRig(TollTheOnlyWayJson, CoinBeforeTheDeath);
        rig.Filter.NotePurseLostAtDeath();

        BlockedRoutePlan? plan = RouteChoicePlanner.PlanBlocked(
            rig.Bfs, rig.Filter, rig.Graph, new RoomKey(1, 1), new RoomKey(1, 3));

        Assert.NotNull(plan);
        Assert.Equal(new RoomKey(1, 2), plan!.StopRoom);
        Assert.Equal(Direction.E, plan.BlockDir);
        RoomExit toll = plan.BlockExit;
        Assert.Equal(ExitBlockReason.Toll, rig.Filter.DescribeExitBlock(in toll));
        Assert.Contains("went with the deathpile", rig.Filter.DescribePurseFor(in toll));
    }

    // ----- the inventory read a refusal asks for --------------------------------

    // With Auto-All off the move was the user's own and nothing is sent (user,
    // 2026-10-10), but nothing sent the read when the switch came back on either,
    // and every toll stayed closed until an `i` was typed.
    [Fact]
    public void TheReadARefusalAsksFor_HeldByTheMasterSwitch_GoesOutWhenItIsBackOn()
    {
        bool held = true;
        int sent = 0;
        OwedPurseRead read = new(() => held, () => sent++, (_, _) => new NoTimer());

        read.Ask();
        Assert.Equal(0, sent);
        Assert.True(read.Owed);

        read.Retry();                    // the send gate let go, the switch still off
        Assert.Equal(0, sent);

        held = false;
        read.Retry();
        Assert.Equal(1, sent);
        Assert.False(read.Owed);

        read.Retry();                    // one `i` for the refusal
        Assert.Equal(1, sent);
    }

    [Fact]
    public void TheReadARefusalAsksFor_IsNotSent_OnceAReadHasComeAnotherWay()
    {
        bool held = true;
        int sent = 0;
        OwedPurseRead read = new(() => held, () => sent++, (_, _) => new NoTimer());
        read.Ask();

        read.Settle();                   // the user typed `i`
        held = false;
        read.Retry();

        Assert.Equal(0, sent);
    }

    [Fact]
    public void TheReadARefusalAsksFor_WithNothingHoldingIt_GoesOutAtOnce()
    {
        int sent = 0;
        OwedPurseRead read = new(() => false, () => sent++, (_, _) => new NoTimer());

        read.Ask();

        Assert.Equal(1, sent);
        Assert.False(read.Owed);
    }

    // A death's own `i` going out covers a refusal's: one `i`, and its answer is
    // still what a re-plan waits for.
    [Fact]
    public void TheReadARefusalAsksFor_IsNotSentBesideADeathsRead_ButItsAnswerIsStillAwaited()
    {
        bool held = true;
        int sent = 0;
        OwedPurseRead read = new(() => held, () => sent++, (_, _) => new NoTimer());
        read.Ask();

        read.CoveredByAnotherRead();
        held = false;
        read.Retry();
        Assert.Equal(0, sent);

        int ran = 0;
        Assert.True(read.WaitForAnswer(() => ran++));
        read.Settle();
        Assert.Equal(1, ran);
    }

    // What a re-plan after a refusal waits on: the answer, or the bound.
    [Fact]
    public void AReadStillUnanswered_HoldsWhatWaitsOnIt_UntilItAnswersOrItsBoundPasses()
    {
        Action? bound = null;
        TimeSpan asked = TimeSpan.Zero;
        OwedPurseRead read = new(() => false, () => { }, (delay, onBound) =>
        {
            asked = delay;
            bound = onBound;
            return new NoTimer();
        });
        int ran = 0;

        Assert.False(read.WaitForAnswer(() => ran++));      // nothing asked: nothing to wait for
        Assert.Equal(0, ran);

        read.Ask();
        Assert.True(read.WaitForAnswer(() => ran++));
        Assert.True(read.WaitForAnswer(() => ran++));
        Assert.Equal(0, ran);
        Assert.Equal(OwedPurseRead.AnswerBound, asked);

        read.Settle();                                      // the read answered
        Assert.Equal(2, ran);
        Assert.False(read.WaitForAnswer(() => ran++));

        read.Ask();
        Assert.True(read.WaitForAnswer(() => ran++));
        bound!();                                           // it never did
        Assert.Equal(3, ran);
        read.Settle();
        Assert.Equal(3, ran);                               // not run twice
    }

    // The post-death `i` went through the engine send gate, which drops what it is
    // handed while held: dropped there it was never asked again.
    [Fact]
    public void TheReadAfterADeath_StaysOwed_WhileTheSendGateIsHeld()
    {
        bool gateHeld = true;
        int asked = 0;
        PostDeathInventoryRefresh refresh = new(() => { }, () => asked++) { SendHeld = () => gateHeld };
        refresh.OnDeath();

        refresh.OnRoomKnown();
        Assert.Equal(0, asked);
        Assert.True(refresh.Due);

        gateHeld = false;
        refresh.OnRoomKnown();           // asked again when the gate lets go
        refresh.OnRoomKnown();
        Assert.Equal(1, asked);
        Assert.False(refresh.Due);
    }
}
