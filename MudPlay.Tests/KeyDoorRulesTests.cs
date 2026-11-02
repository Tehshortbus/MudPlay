using System.Text;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.ViewModels.GameData.Edit;
using MudPlay.ViewModels.GameData.Tables;
using Xunit;

namespace MudPlay.Tests;

// The key doors of report paradigm-20261018-192819, at Thrag's in the Huge Cave:
// the guardroom door 1/1789 E `(Key: 475 [or any picklocks])` ↔ 1/1790 W
// `(Key: 475)`, and Thrag's door 1/1794 E `(Key: 476)` ↔ 1/1810 W
// `(Door [11 picklocks/strength])`; and a vault, 9/1, whose bare key locks no seed
// names. The walker, the loop runner, the door handler, the pack list, the room's
// roster, the route filter and the bare-lock book run together, seeded from the
// seed the client ships; the game's lines come through a ServerFeed into the
// router and the line readers.
public sealed class KeyDoorRulesTests : IDisposable
{
    private const string RoomsJson = """
        [
          { "Map Number": 1, "Room Number": 1788, "Name": "Huge Cave",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/1789", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 1789, "Name": "Huge Cave, Guardroom",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/1790 (Key: 475 [or any picklocks])", "W": "1/1788",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 1790, "Name": "Stone Passageway",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/1794", "W": "1/1789 (Key: 475)",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 1794, "Name": "Stone Passageway, Guardroom",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/1810 (Key: 476)", "W": "1/1790",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 1810, "Name": "Gigantic Cavern",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1794 (Door [11 picklocks/strength])",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 9, "Room Number": 1, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "9/4", "E": "9/2 (Key: 475)", "W": "9/3 (Key: 476)",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 9, "Room Number": 2, "Name": "East Strongroom",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "9/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 9, "Room Number": 3, "Name": "West Strongroom",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "9/6", "E": "9/1", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 9, "Room Number": 4, "Name": "Antechamber",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "9/1", "S": "0", "E": "9/5 (Door [11 picklocks])", "W": "9/6",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 9, "Room Number": 5, "Name": "Inner Chamber",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "9/4",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 9, "Room Number": 6, "Name": "Side Passage",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "9/3", "S": "0", "E": "9/4", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private static readonly RoomKey HugeCave = new(1, 1788);
    private static readonly RoomKey Passageway = new(1, 1790);
    private static readonly RoomKey ThragsGuardroom = new(1, 1794);
    private static readonly RoomKey Gigantic = new(1, 1810);
    private static readonly RoomKey Vault = new(9, 1);
    private static readonly RoomKey WestStrongroom = new(9, 3);
    private static readonly RoomKey Antechamber = new(9, 4);

    private static readonly Dictionary<int, string> Keys = new()
    {
        [475] = "green metal key",
        [476] = "large iron key",
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mudplay-keydoor-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    private sealed class Rig
    {
        public required RoomGraphManager Graph { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public required LoopRunner Loops { get; init; }
        public required DoorOpenManager Door { get; init; }
        public required InventoryManager Pack { get; init; }
        public required MovementFilter Filter { get; init; }
        public required PlayerStats Stats { get; init; }
        public required ServerFeed Server { get; init; }
        public required BareKeyLockBook Book { get; init; }
        public required BareKeyLockStore Store { get; init; }
        // The door handler's reply watchdog.
        public required ManualTimers DoorTimers { get; init; }
        public List<string> Sent { get; } = new();
        public List<WalkEvent> Events { get; } = new();
        public List<LoopEvent> LoopEvents { get; } = new();
        // Whether the realm has PvP on.
        public bool Pvp { get; set; }

        // The `i` the client read: its key ring, as the game lists it.
        public void ReadInventory(string keyRing)
        {
            Server.Line("You are carrying 77 gold crowns, rope and grapple");
            Server.Line(keyRing);
            Server.Line("Wealth: 7700 copper farthings");
            Server.Line("Encumbrance: 1734/5724 - Light [30%]");
        }

        // The room's "Also here:" line: who the bash attack could swing at.
        public void RoomShows(params string[] names) => Server.Line($"Also here: {string.Join(", ", names)}.");

        public bool Holds(int itemId) =>
            Keys.TryGetValue(itemId, out string? name)
            && Pack.Snapshot.Keys is { } ring
            && ring.Any(entry => InventorySnapshot.ParseKeyEntry(entry).Name == name);
    }

    // The seed the client ships, extracted the way a launch extracts it.
    private string ShippedSeed()
    {
        string dir = Path.Combine(_root, "seeds");
        AppPaths.ExtractEmbeddedSeeds(dir);
        return Path.Combine(dir, "BareKeyLocks.seed.json");
    }

    // Masta, the report's Paladin: Strength 111, no Picklocks. realmFolder: where the
    // realm's own record is kept, or none.
    private Rig NewRig(int strength = 111, int picklocks = 0, string? realmFolder = null)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), RoomsJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        RoomTracker tracker = new(graph);

        // Wired as AppServices wires it: the book stamps the map as each graph is
        // built and again each time it learns.
        BareKeyLockBook book = new((room, dir) =>
            graph.GetRoom(room) is { } r && r.Exits.TryGetValue(dir, out RoomExit exit) ? exit : null);
        BareKeyLockStore store = new(book);
        store.LoadSeed(ShippedSeed());
        store.OnRealmChanged(realmFolder);
        graph.SetBareLockKinds(book.Lookup, book.AnsweredAsDoorHere);
        book.Changed += () => graph.ApplyBareLockKinds(book.Lookup, book.AnsweredAsDoorHere);
        graph.OnActiveSetChanged("alpha");

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ServerFeed server = new(router);
        InventoryManager pack = new(isKey: name => Keys.ContainsValue(name));
        pack.AttachLineExtractor(server.Lines);
        RoomEntityClassifier roster = new(router, new MonsterMessageStore(),
            new PlayerDatabase(new ProfileService(), () => null), roomTracker: null);
        roster.AttachLineExtractor(server.Lines);
        PlayerStats stats = new() { Strength = strength, Picklocks = picklocks };

        Rig? rig = null;
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        ManualTimers doorTimers = new();
        DoorOpenManager door = new(router, stats,
            maxPickAttemptsProvider: () => 3,
            picklocksOverBashProvider: () => false,
            itemNameLookup: id => Keys.GetValueOrDefault(id),
            holdsKeyItem: id => rig!.Holds(id),
            scheduleDelay: doorTimers.Arm,
            statsRead: () => true);
        door.SetKeyLossSink(pack.NoteItemGone);
        door.SetConfirmedRoomProbe(() =>
            tracker.State.Confidence == RoomConfidence.Confirmed ? tracker.State.CurrentRoom?.Key : null);
        door.SetBareLockBook(book, word => BashTargetWord.NamesSomeoneIn(roster, word), pvpOn: () => rig!.Pvp);
        MovementFilter filter = new(profile)
        {
            InventoryReadyProbe = () => pack.IsLoaded,
            ItemCarriedProbe = id => rig!.Holds(id),
            StrengthProvider = () => stats.Strength,
            PicklocksProvider = () => stats.Picklocks,
            MaxBashableStrengthProvider = () => 200,
            BareLockBookVersionProvider = () => book.Version,
            BareLockTrialsAllowedProbe = () => !rig!.Pvp,
            UnpickableDoorProbe = door.IsUnpickable,
        };
        MovementCoordinator coordinator = new();
        BfsMapper bfs = new(graph);
        AutoWalkManager walker = new(graph, bfs, tracker, coordinator, filter);
        walker.SetItemNameResolver(id => Keys.GetValueOrDefault(id));
        LoopRunner loops = new(tracker, coordinator, graph: graph, bfs: bfs, filter: filter,
            postToUi: a => a(), schedule: new ManualTimers().Arm);

        rig = new Rig
        {
            Graph = graph, Tracker = tracker, Walker = walker, Loops = loops, Door = door, Pack = pack,
            Filter = filter, Stats = stats, Server = server, Book = book, Store = store, DoorTimers = doorTimers,
        };
        walker.SetWireSender(b => rig.Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        loops.SetWireSender(b => rig.Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        door.SetWireSender(b => rig.Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        walker.SetDoorEnqueuer(door.Enqueue);
        loops.SetDoorEnqueuer(door.Enqueue);
        loops.SetDoorStopper(door.StopAll);
        walker.Event += rig.Events.Add;
        loops.Event += rig.LoopEvents.Add;
        return rig;
    }

    private static RoomExit ExitOf(Rig rig, RoomKey room, Direction dir) => rig.Graph.GetRoom(room)!.Exits[dir];

    private string RealmFolder(string name)
    {
        string folder = Path.Combine(_root, "BBS", "Board", "Realms", name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    // ----- the report ------------------------------------------------

    // The report's way out of Thrag's cave: Strength 111, no Picklocks, no key, at
    // 1/1790, nobody in the room the word `w` names. The seed knows its west side for
    // a door, so the walker bashes it instead of saying the key is needed, and the
    // game's answer makes it a door on this realm.
    [Fact]
    public void ReportsWayOut_IsBashed_WithNoKey()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have the following keys: ruby key.");
        Assert.Equal(BareLockBash.Bashable, ExitOf(rig, Passageway, Direction.W).BareLockBash);
        rig.Tracker.SetLocated(Passageway);
        rig.RoomShows("Barter");

        Assert.True(rig.Walker.WalkTo(HugeCave));
        Assert.Equal("bash w", rig.Sent[^1]);
        Assert.False(rig.Book.AnsweredAsDoorHere(Passageway, Direction.W));

        rig.Server.Line("You bashed the door open.");
        Assert.Equal("w", rig.Sent[^1]);
        Assert.True(rig.Book.AnsweredAsDoorHere(Passageway, Direction.W));
    }

    // Client policy (user's rulings, 2026-10-18): with no Picklocks, a bash goes
    // ahead of the key, even a key carried. The report's walk sent the key first.
    [Fact]
    public void KeyedDoor_WithoutPicklocks_BashesBeforeTheKey()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have the following keys: green metal key, ruby key.");
        rig.Tracker.SetLocated(Passageway);
        rig.RoomShows("Barter");

        Assert.True(rig.Walker.WalkTo(HugeCave));

        Assert.Equal(new[] { "bash w" }, rig.Sent);
    }

    // As it happened in the report: the key's last charge had gone on the way in, but
    // the pack list kept it. Paradigm answered `You do not have <key>.`, which nothing
    // read: the door handler waited out its 8 s watchdog and the key stayed "held".
    // Now the line takes the key off the ring at once, and the walk stops naming it.
    [Fact]
    public void KeyTheGameSaysIsGone_LeavesTheHeldList()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have the following keys: large iron key, ruby key.");
        Assert.True(rig.Holds(476));
        rig.Tracker.SetLocated(ThragsGuardroom);

        Assert.True(rig.Walker.WalkTo(Gigantic));
        Assert.Equal("use large iron key e", rig.Sent[^1]);

        rig.Server.Line("You do not have large iron key.");

        Assert.False(rig.Holds(476));
        Assert.Equal(new[] { "ruby key" }, rig.Pack.Snapshot.Keys);
        Assert.Contains("You do not have large iron key.", rig.Door.LastKeyLoss);
        Assert.Contains(rig.Events, e => e.Kind == WalkEventKind.Failed && e.Detail.Contains("large iron key"));
    }

    // Stock's wording names the typed argument, direction and all.
    [Fact]
    public void StockKeyMissingLine_IsReadToo()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have the following keys: green metal key.");
        rig.Door.Enqueue(Direction.W, DoorPolicy.BareKeyLockPickRequirement, canBash: false, keyItemId: 475, "walker", _ => { });

        rig.Server.Line("You don't have green metal key w.");

        Assert.False(rig.Holds(475));
    }

    // A "have" line naming some other item, or a key whose name only ends this
    // one's, or anything that only contains the words, is the answer to something
    // else.
    [Theory]
    [InlineData("You do not have the required experience to train yet!")]
    [InlineData("You do not have large green metal key.")]
    [InlineData("Bob gossips: you do not have green metal key.")]
    public void KeyMissingLine_NamingAnotherItem_IsNotTheDoors(string line)
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have the following keys: green metal key.");
        DoorOpenResult? result = null;
        rig.Door.Enqueue(Direction.W, DoorPolicy.BareKeyLockPickRequirement, canBash: false, keyItemId: 475, "walker", r => result = r);

        rig.Server.Line(line);

        Assert.Null(result);
        Assert.Equal(DoorOpenManager.DoorState.WaitingUseKey, rig.Door.CurrentState);
        Assert.True(rig.Holds(475));
    }

    // The key's last charge: the game prints the key's message straight after the
    // unlock, and the key is gone from the pack (GAME_MECHANICS "Item charges").
    [Fact]
    public void KeysLastCharge_TakesTheCopyUsedOffTheRing()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have the following keys: 2 green metal key.");
        DoorOpenResult? result = null;
        rig.Door.Enqueue(Direction.W, DoorPolicy.BareKeyLockPickRequirement, canBash: false, keyItemId: 475, "walker", r => result = r);
        Assert.Equal("use green metal key w", rig.Sent[^1]);

        rig.Server.Line("You successfully unlocked the door.");
        rig.Server.Line("The key breaks and crumbles apart.");
        Assert.Equal("open w", rig.Sent[^1]);
        rig.Server.Line("The door is now open.");

        Assert.IsType<DoorOpenResult.Opened>(result);
        Assert.Equal(new[] { "green metal key" }, rig.Pack.Snapshot.Keys);
    }

    // The engine's own last-charge line, for a key with no message of its own,
    // names the key.
    [Fact]
    public void FallbackLastChargeLine_NamingTheKey_TakesItOff()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have the following keys: green metal key.");
        rig.Door.Enqueue(Direction.W, DoorPolicy.BareKeyLockPickRequirement, canBash: false, keyItemId: 475, "walker", _ => { });

        rig.Server.Line("You successfully unlocked the door.");
        rig.Server.Line("It's uses gone, green metal key disappears from your inventory!");

        Assert.False(rig.Holds(475));
    }

    // A last-charge line with no key of ours just used is about something else.
    [Fact]
    public void LastChargeLine_WithNoKeyUsed_LeavesTheRing()
    {
        Rig rig = NewRig(picklocks: 40);
        rig.ReadInventory("You have the following keys: green metal key.");
        rig.Door.Enqueue(Direction.W, DoorPolicy.BareKeyLockPickRequirement, canBash: false, keyItemId: 475, "walker", _ => { });
        Assert.Equal("open w", rig.Sent[^1]);

        rig.Server.Line("The key breaks and crumbles apart.");

        Assert.True(rig.Holds(475));
    }

    // ----- the seed --------------------------------------------------

    // The shipped seed: Stock's map file, its doors and gates bashable and its key
    // exits not, 21 sides in all.
    [Fact]
    public void ShippedSeed_KnowsStocksBareLocks()
    {
        Rig rig = NewRig();
        Assert.Equal(21, rig.Book.SeedCount);
        Assert.Equal(10, rig.Book.SeedBashable);
        Assert.Equal(BareLockBash.Bashable, rig.Book.Lookup(Passageway, Direction.W));
        Assert.Equal(BareLockBash.NotBashable, rig.Book.Lookup(ThragsGuardroom, Direction.E));
        Assert.Equal(BareLockBash.Unknown, rig.Book.Lookup(Vault, Direction.E));
    }

    // Thrag's door from outside, 1/1794 E, is a key exit: never bashed, so without
    // its key or Picklocks the route is shut, and with the key it is keyed.
    [Fact]
    public void SeededKeyExit_IsNeverBashed()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have no keys.");
        rig.Tracker.SetLocated(ThragsGuardroom);
        rig.RoomShows("Barter");

        Assert.False(rig.Walker.WalkTo(Gigantic));
        Assert.Contains(rig.Events, e => e.Kind == WalkEventKind.Failed && e.Detail.Contains("large iron key"));
        Assert.DoesNotContain(rig.Sent, c => c.StartsWith("bash", StringComparison.Ordinal));

        rig.ReadInventory("You have the following keys: large iron key.");
        Assert.True(rig.Walker.WalkTo(Gigantic));
        Assert.Equal(new[] { "use large iron key e" }, rig.Sent);
    }

    // Thrag's door from the inside, 1/1810 W: no key, an 11 picklocks/strength
    // modifier. The user bashes it open from there without the key (2026-10-18).
    [Fact]
    public void ThragsDoorFromInside_IsBashedWithoutTheKey()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have no keys.");
        rig.Tracker.SetLocated(Gigantic);

        Assert.True(rig.Walker.WalkTo(ThragsGuardroom));

        Assert.Equal("bash w", rig.Sent[^1]);
    }

    // A side the seed calls a door is still a try until this realm answers a bash
    // there as one: with the large dire wolf here, `bash w` could be the bash attack.
    // Once the game has answered as a door, it is bashed whoever is here.
    [Fact]
    public void SeededDoor_NotYetAnsweredHere_IsNotBashed_WhenTheWordNamesSomeoneHere()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have no keys.");
        rig.Tracker.SetLocated(Passageway);
        rig.RoomShows("large dire wolf");

        rig.Door.Enqueue(Direction.W, DoorPolicy.BareKeyLockPickRequirement, canBash: true, keyItemId: 475, "walker", _ => { });
        Assert.DoesNotContain(rig.Sent, c => c.StartsWith("bash", StringComparison.Ordinal));
        Assert.Contains("the seed calls it a door", rig.Door.BareLockDecision);
        rig.Door.StopAll();

        rig.Book.NoteRefusedMove(Passageway, Direction.W, bashable: true);
        rig.Door.Enqueue(Direction.W, DoorPolicy.BareKeyLockPickRequirement, canBash: true, keyItemId: 475, "walker", _ => { });
        Assert.Equal("bash w", rig.Sent[^1]);
    }

    // ----- a bare lock no list knows -----------------------------------

    // Bashed where nobody in the room answers to the direction word, and learned once
    // for the request however many bashes it takes; capped at the pick tries, then
    // the key.
    [Fact]
    public void UnknownBareLock_IsBashed_LearnedOnce_AndCapped()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have no keys.");
        rig.Tracker.SetLocated(Vault);
        rig.RoomShows("Barter");

        Assert.True(rig.Walker.WalkTo(new RoomKey(9, 2)));
        Assert.Equal("bash e", rig.Sent[^1]);
        Assert.Contains("a bash is tried", rig.Door.BareLockDecision);

        rig.Server.Line("Your attempts to bash through fail!");
        Assert.Equal(BareLockBash.Bashable, rig.Book.Lookup(Vault, Direction.E));
        Assert.Equal(BareLockBash.Bashable, ExitOf(rig, Vault, Direction.E).BareLockBash);
        Assert.Equal("bash e", rig.Sent[^1]);
        rig.Server.Line("Your attempts to bash through fail!");
        rig.Server.Line("Your attempts to bash through fail!");

        Assert.Equal(1, rig.Book.RecordOf(Vault, Direction.E)!.Value.Door);
        Assert.Equal(3, rig.Sent.Count(c => c == "bash e"));
        Assert.Equal("use green metal key e", rig.Sent[^1]);
    }

    // The bash attack answering instead — no door there, nobody to swing at — is a
    // key exit: learned, and the key tried as the last resort.
    [Fact]
    public void UnknownBareLock_WhoseBashHadNoEffect_IsLearnedAsAKeyExit()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have no keys.");
        rig.Tracker.SetLocated(Vault);
        rig.RoomShows("Barter");
        rig.Walker.WalkTo(new RoomKey(9, 2));
        Assert.Equal("bash e", rig.Sent[^1]);

        rig.Server.Line("Your command had no effect.");

        Assert.Equal(BareLockBash.NotBashable, rig.Book.Lookup(Vault, Direction.E));
        Assert.Equal("use green metal key e", rig.Sent[^1]);
        Assert.Equal("get green metal key", rig.Sent[^2]);
    }

    // A refusal about the character — no weapon, no skill — says nothing of the exit.
    [Theory]
    [InlineData("You need a weapon to bash with!")]
    [InlineData("You don't know the first thing about bashing!")]
    public void BashRefusedForTheCharacter_TeachesNothing(string answer)
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have no keys.");
        rig.Tracker.SetLocated(Vault);
        rig.RoomShows("Barter");
        rig.Walker.WalkTo(new RoomKey(9, 2));
        Assert.Equal("bash e", rig.Sent[^1]);

        rig.Server.Line(answer);

        Assert.Equal(BareLockBash.Unknown, rig.Book.Lookup(Vault, Direction.E));
        Assert.Null(rig.Book.RecordOf(Vault, Direction.E));
        Assert.Equal("use green metal key e", rig.Sent[^1]);
    }

    // `bash w` at a key exit would swing at the large dire wolf here: no bash, and
    // with neither the key nor Picklocks only the key is left to look for.
    [Fact]
    public void UnknownBareLock_IsNotBashed_WhenTheWordNamesSomeoneHere()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have no keys.");
        rig.RoomShows("Barter", "large dire wolf");
        rig.Tracker.SetLocated(Vault);
        DoorOpenResult? result = null;

        rig.Door.Enqueue(Direction.W, DoorPolicy.BareKeyLockPickRequirement, canBash: false, keyItemId: 476, "walker", r => result = r);

        Assert.DoesNotContain(rig.Sent, c => c.StartsWith("bash", StringComparison.Ordinal));
        Assert.Equal("use large iron key w", rig.Sent[^1]);
        Assert.Contains("could be taken for someone here", rig.Door.BareLockDecision);
        Assert.Null(result);
        Assert.Equal(BareLockBash.Unknown, rig.Book.Lookup(Vault, Direction.W));
    }

    // No "Also here:" read since the room was entered: someone could be here unseen.
    [Fact]
    public void UnknownBareLock_IsNotBashed_WhileTheRosterIsNotWhole()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have no keys.");
        rig.Tracker.SetLocated(Vault);

        rig.Door.Enqueue(Direction.E, DoorPolicy.BareKeyLockPickRequirement, canBash: false, keyItemId: 475, "walker", _ => { });

        Assert.DoesNotContain(rig.Sent, c => c.StartsWith("bash", StringComparison.Ordinal));
        Assert.Contains("could be taken for someone here", rig.Door.BareLockDecision);
    }

    // Someone who came in while the picks went out: the bash that was to follow
    // them is dropped, and the key tried instead.
    [Fact]
    public void SomeoneComingIn_BeforeATriedBash_StopsIt()
    {
        Rig rig = NewRig(picklocks: 40);
        rig.ReadInventory("You have no keys.");
        rig.Tracker.SetLocated(Vault);
        rig.RoomShows("Barter");
        rig.Door.Enqueue(Direction.W, DoorPolicy.BareKeyLockPickRequirement, canBash: false, keyItemId: 476, "walker", _ => { });
        rig.Server.Line("The door is locked.");
        Assert.Equal("pick w", rig.Sent[^1]);

        rig.RoomShows("Barter", "large dire wolf");
        for (int i = 0; i < 3; i++) rig.Server.Line("Your skill fails you this time.");

        Assert.DoesNotContain(rig.Sent, c => c.StartsWith("bash", StringComparison.Ordinal));
        Assert.Equal("use large iron key w", rig.Sent[^1]);
        Assert.Contains("then dropped", rig.Door.BareLockDecision);
    }

    // On a PvP realm a player can walk in between the look and the bash: a side not
    // yet answered as a door is never tried, seed or not (Client policy).
    [Fact]
    public void PvpRealm_TriesNoBash_WhereTheSideHasntAnsweredAsADoor()
    {
        Rig rig = NewRig();
        rig.Pvp = true;
        rig.ReadInventory("You have no keys.");
        rig.Tracker.SetLocated(Passageway);
        rig.RoomShows("Barter");

        rig.Door.Enqueue(Direction.W, DoorPolicy.BareKeyLockPickRequirement, canBash: true, keyItemId: 475, "walker", _ => { });
        Assert.DoesNotContain(rig.Sent, c => c.StartsWith("bash", StringComparison.Ordinal));
        Assert.Contains("PvP", rig.Door.BareLockDecision);
        rig.Door.StopAll();

        rig.Book.NoteRefusedMove(Passageway, Direction.W, bashable: true);
        rig.Door.Enqueue(Direction.W, DoorPolicy.BareKeyLockPickRequirement, canBash: true, keyItemId: 475, "walker", _ => { });
        Assert.Equal("bash w", rig.Sent[^1]);
    }

    // On a PvP realm a route doesn't count on a bash the handler won't try: the side
    // is planned as the key exit it may be, so the walk names the key to fetch. Once
    // the side has answered as a door, it is planned through and bashed.
    [Fact]
    public void PvpRealm_PlansNoBash_WhereTheSideHasntAnsweredAsADoor()
    {
        Rig rig = NewRig();
        rig.Pvp = true;
        rig.ReadInventory("You have no keys.");
        rig.Tracker.SetLocated(Passageway);
        rig.RoomShows("Barter");

        Assert.True(rig.Filter.IsExitBlocked(ExitOf(rig, Passageway, Direction.W)));
        Assert.Equal(ExitBlockReason.LockedDoor, rig.Filter.DescribeExitBlock(ExitOf(rig, Passageway, Direction.W)));
        Assert.False(rig.Walker.WalkTo(HugeCave));
        Assert.Contains(rig.Events, e => e.Kind == WalkEventKind.Failed && e.Detail.Contains("green metal key"));
        Assert.DoesNotContain(rig.Sent, c => c.StartsWith("bash", StringComparison.Ordinal));

        rig.Book.NoteRefusedMove(Passageway, Direction.W, bashable: true);
        Assert.False(rig.Filter.IsExitBlocked(ExitOf(rig, Passageway, Direction.W)));
        Assert.True(rig.Walker.WalkTo(HugeCave));
        Assert.Equal("bash w", rig.Sent[^1]);
    }

    // With the room not confirmed the book can't be asked: a request shaped like a
    // bare lock's is tried as one, and its bash capped at the pick tries.
    [Fact]
    public void BareLockRequest_WithTheRoomNotConfirmed_IsCapped()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have no keys.");
        rig.RoomShows("Barter");

        rig.Door.Enqueue(Direction.W, DoorPolicy.BareKeyLockPickRequirement, canBash: true, keyItemId: 475, "walker", _ => { });
        Assert.Equal("bash w", rig.Sent[^1]);
        Assert.Contains("room not confirmed", rig.Door.BareLockDecision);
        for (int i = 0; i < 3; i++) rig.Server.Line("Your attempts to bash through fail!");

        Assert.Equal(3, rig.Sent.Count(c => c == "bash w"));
        Assert.Equal("use green metal key w", rig.Sent[^1]);
    }

    // A loop at a bare lock it can't try a bash at, with no key and no Picklocks: the
    // door is given up on and the loop goes round it, not back to the same door.
    [Fact]
    public void Loop_AtABareLockItCantTry_GoesRoundIt()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have no keys.");
        rig.Tracker.SetLocated(Vault);
        rig.RoomShows("large dire wolf");

        Assert.True(rig.Loops.Start(new Loop("strongroom", new[] { Vault, WestStrongroom })));
        Assert.Equal("use large iron key w", rig.Sent[^1]);

        rig.Server.Line("You do not have large iron key.");

        Assert.DoesNotContain(rig.LoopEvents, e => e.Kind == LoopEventKind.Failed);
        Assert.Contains((Vault, WestStrongroom), rig.Loops.AbandonedDoors);
        Assert.Equal(1, rig.Sent.Count(c => c == "use large iron key w"));
    }

    // ----- learning ------------------------------------------------------

    // One answer can be misread, so it never overturns the seed: a second, on another
    // occasion, does.
    [Fact]
    public void OneAnswer_NeverOverturnsTheSeed()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have no keys.");
        rig.Tracker.SetLocated(Passageway);
        rig.RoomShows("Barter");

        rig.Door.Enqueue(Direction.W, DoorPolicy.BareKeyLockPickRequirement, canBash: true, keyItemId: 475, "walker", _ => { });
        Assert.Equal("bash w", rig.Sent[^1]);
        rig.Server.Line("Your command had no effect.");
        Assert.Equal(BareLockBash.Bashable, rig.Book.Lookup(Passageway, Direction.W));
        rig.Door.StopAll();

        rig.Door.Enqueue(Direction.W, DoorPolicy.BareKeyLockPickRequirement, canBash: true, keyItemId: 475, "walker", _ => { });
        Assert.Equal("bash w", rig.Sent[^1]);
        rig.Server.Line("Your command had no effect.");

        Assert.Equal(BareLockBash.NotBashable, rig.Book.Lookup(Passageway, Direction.W));
    }

    // An answer that comes once the tracker is no longer sure of the room may be
    // about some other door: the request goes by it, the realm's record doesn't.
    [Fact]
    public void AnswerAfterTheRoomWentUnsure_IsNotLearned()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have no keys.");
        rig.Tracker.SetLocated(Vault);
        rig.RoomShows("Barter");
        rig.Door.Enqueue(Direction.E, DoorPolicy.BareKeyLockPickRequirement, canBash: false, keyItemId: 475, "walker", _ => { });
        Assert.Equal("bash e", rig.Sent[^1]);

        rig.Tracker.NoteMoveSent(Direction.S);
        rig.Server.Line("Your attempts to bash through fail!");

        Assert.Null(rig.Book.RecordOf(Vault, Direction.E));
        Assert.Equal(BareLockBash.Unknown, rig.Book.Lookup(Vault, Direction.E));
    }

    // Our own move into a shut bare lock is refused in words that name its kind.
    [Theory]
    [InlineData("There is a closed door in that direction!", BareLockBash.NotBashable)]
    [InlineData("The door is closed!", BareLockBash.Bashable)]
    [InlineData("The door is Closed!", BareLockBash.Bashable)]
    public void RefusedMove_IntoABareLock_IsLearned(string refusal, BareLockBash learned)
    {
        Rig rig = NewRig();
        using MovementRefusalDetector detector = new(rig.Server.Lines, rig.Tracker);
        detector.ShutDoorBonked += rig.Book.NoteRefusedMove;
        rig.Tracker.SetLocated(Vault);

        rig.Tracker.NoteMoveSent(Direction.E);
        rig.Server.Line(refusal);

        Assert.Equal(learned, rig.Book.Lookup(Vault, Direction.E));
    }

    // A refusal can be told for ours only when one move of ours, taken by us, is in
    // flight: with none, a leader's drag, or two typed ahead, it names nothing.
    [Theory]
    [InlineData("none")]
    [InlineData("follow")]
    [InlineData("typed ahead")]
    public void RefusedMove_NotPlainlyOurs_LearnsNothing(string moves)
    {
        Rig rig = NewRig();
        using MovementRefusalDetector detector = new(rig.Server.Lines, rig.Tracker);
        detector.ShutDoorBonked += rig.Book.NoteRefusedMove;
        rig.Tracker.SetLocated(Vault);
        switch (moves)
        {
            case "follow":
                rig.Tracker.NoteFollowMove(Direction.E);
                break;
            case "typed ahead":
                rig.Tracker.NoteMoveSent(Direction.E);
                rig.Tracker.NoteMoveSent(Direction.E);
                break;
        }

        rig.Server.Line("There is a closed door in that direction!");

        Assert.Equal(BareLockBash.Unknown, rig.Book.Lookup(Vault, Direction.E));
        Assert.Null(rig.Book.RecordOf(Vault, Direction.E));
    }

    // ----- the realm's record ------------------------------------------

    // Each realm keeps its own record, and it outlives the client.
    [Fact]
    public void LearnedSides_ArePerRealm_AndKept()
    {
        string realmA = RealmFolder("A");
        string realmB = RealmFolder("B");
        Rig rig = NewRig(realmFolder: realmA);
        rig.ReadInventory("You have no keys.");
        rig.Tracker.SetLocated(Vault);
        rig.RoomShows("Barter");
        rig.Walker.WalkTo(new RoomKey(9, 2));
        rig.Server.Line("You bashed the door open.");
        Assert.True(File.Exists(AppPaths.RealmBareKeyLocksFile(realmA)));

        rig.Store.OnRealmChanged(realmB);
        Assert.Equal(BareLockBash.Unknown, rig.Book.Lookup(Vault, Direction.E));

        Rig later = NewRig(realmFolder: realmA);
        Assert.Equal(BareLockBash.Bashable, later.Book.Lookup(Vault, Direction.E));
        Assert.NotNull(later.Book.RecordOf(Vault, Direction.E)!.Value.LastAnswered);
        // The seed still says the rest.
        Assert.Equal(BareLockBash.NotBashable, later.Book.Lookup(ThragsGuardroom, Direction.E));
    }

    // The kind the user sets beats the seed and every answer, and is kept.
    [Fact]
    public void UserKind_BeatsTheSeedAndTheAnswers_AndIsKept()
    {
        string realm = RealmFolder("A");
        Rig rig = NewRig(realmFolder: realm);

        rig.Book.SetByUser(Passageway, Direction.W, BareLockBash.NotBashable);
        rig.Book.Learn(Passageway, Direction.W, bashable: true);
        rig.Book.Learn(Passageway, Direction.W, bashable: true);

        Assert.Equal(BareLockBash.NotBashable, rig.Book.Lookup(Passageway, Direction.W));
        Assert.Equal(BareLockBash.NotBashable, ExitOf(rig, Passageway, Direction.W).BareLockBash);
        Assert.False(rig.Book.AnsweredAsDoorHere(Passageway, Direction.W));
        Rig later = NewRig(realmFolder: realm);
        Assert.Equal(BareLockBash.NotBashable, later.Book.Lookup(Passageway, Direction.W));
        Assert.Equal(BareLockBash.NotBashable, later.Book.RecordOf(Passageway, Direction.W)!.Value.UserKind);
    }

    // Reset to the seed drops the side's answers and the user's kind alike.
    [Fact]
    public void ResetToSeed_DropsTheSide()
    {
        string realm = RealmFolder("A");
        Rig rig = NewRig(realmFolder: realm);
        rig.Book.Learn(Passageway, Direction.W, bashable: false);
        rig.Book.Learn(Passageway, Direction.W, bashable: false);
        rig.Book.SetByUser(Vault, Direction.E, BareLockBash.Bashable);
        Assert.Equal(BareLockBash.NotBashable, rig.Book.Lookup(Passageway, Direction.W));

        rig.Book.ResetToSeed(Passageway, Direction.W);

        Assert.Equal(BareLockBash.Bashable, rig.Book.Lookup(Passageway, Direction.W));
        Assert.Null(rig.Book.RecordOf(Passageway, Direction.W));
        Rig later = NewRig(realmFolder: realm);
        Assert.Null(later.Book.RecordOf(Passageway, Direction.W));
        Assert.Equal(BareLockBash.Bashable, later.Book.Lookup(Vault, Direction.E));
    }

    // Forgetting the realm's record leaves the seed alone standing, on disk too.
    [Fact]
    public void ForgetLearned_EmptiesTheRealmsRecord()
    {
        string realm = RealmFolder("A");
        Rig rig = NewRig(realmFolder: realm);
        rig.Book.Learn(Vault, Direction.E, bashable: true);
        rig.Book.SetByUser(Passageway, Direction.W, BareLockBash.NotBashable);

        rig.Book.ForgetLearned();

        Assert.Equal(0, rig.Book.LearnedCount);
        Rig later = NewRig(realmFolder: realm);
        Assert.Equal(0, later.Book.LearnedCount);
        Assert.Equal(BareLockBash.Bashable, later.Book.Lookup(Passageway, Direction.W));
        Assert.Equal(BareLockBash.Unknown, later.Book.Lookup(Vault, Direction.E));
    }

    // Two clients on one realm: each change is made to what the other wrote, so
    // neither's answers or resets are lost to the other's older copy.
    [Fact]
    public void AnotherClientsChanges_AreTakenIn()
    {
        string realm = RealmFolder("A");
        Rig one = NewRig(realmFolder: realm);
        Rig two = NewRig(realmFolder: realm);

        one.Book.Learn(Vault, Direction.E, bashable: true);
        two.Book.Learn(Vault, Direction.W, bashable: false);
        Assert.Equal(BareLockBash.Bashable, two.Book.Lookup(Vault, Direction.E));

        one.Book.ResetToSeed(Vault, Direction.E);

        Assert.Null(one.Book.RecordOf(Vault, Direction.E));
        Assert.Equal(BareLockBash.NotBashable, one.Book.Lookup(Vault, Direction.W));
        Rig later = NewRig(realmFolder: realm);
        Assert.Null(later.Book.RecordOf(Vault, Direction.E));
        Assert.Equal(BareLockBash.NotBashable, later.Book.Lookup(Vault, Direction.W));
    }

    // Another client's change reaches this one without this one writing: at the start
    // of its next door request, and when its Doors tab is shown.
    [Fact]
    public void AnotherClientsChanges_ReachThisOne_AtTheNextDoorOrTheTab()
    {
        string realm = RealmFolder("A");
        Rig one = NewRig(realmFolder: realm);
        Rig two = NewRig(realmFolder: realm);
        two.ReadInventory("You have no keys.");
        two.Tracker.SetLocated(Passageway);
        two.RoomShows("Barter");

        one.Book.SetByUser(Passageway, Direction.W, BareLockBash.NotBashable);
        Assert.Equal(BareLockBash.Bashable, two.Book.Lookup(Passageway, Direction.W));

        two.Door.Enqueue(Direction.W, DoorPolicy.BareKeyLockPickRequirement, canBash: true, keyItemId: 475, "walker", _ => { });
        Assert.Equal(BareLockBash.NotBashable, two.Book.Lookup(Passageway, Direction.W));
        Assert.DoesNotContain(two.Sent, c => c.StartsWith("bash", StringComparison.Ordinal));
        two.Door.StopAll();

        one.Book.ResetToSeed(Passageway, Direction.W);
        using DoorsSectionViewModel tab = new(two.Book, two.Graph, _ => null, () => realm);
        tab.OnActivated();
        Assert.Equal(BareLockBash.Bashable, two.Book.Lookup(Passageway, Direction.W));
    }

    // A change made while another client holds the record's lock isn't written
    // without it, and isn't lost: it lands with the next write, laid over what the
    // other client wrote meanwhile.
    [Fact]
    public void ChangeMadeWhileTheLockIsHeld_LandsOnTheNextWrite()
    {
        string realm = RealmFolder("A");
        Rig one = NewRig(realmFolder: realm);
        Rig two = NewRig(realmFolder: realm);
        string file = AppPaths.RealmBareKeyLocksFile(realm);

        using (SharedFileWrite.Hold(file, out _))
        {
            one.Book.SetByUser(Vault, Direction.E, BareLockBash.Bashable);
            Assert.False(File.Exists(file));
        }
        two.Book.SetByUser(Vault, Direction.W, BareLockBash.NotBashable);
        one.Book.SetByUser(Passageway, Direction.W, BareLockBash.NotBashable);

        Rig later = NewRig(realmFolder: realm);
        Assert.Equal(BareLockBash.Bashable, later.Book.Lookup(Vault, Direction.E));
        Assert.Equal(BareLockBash.NotBashable, later.Book.Lookup(Vault, Direction.W));
        Assert.Equal(BareLockBash.NotBashable, later.Book.Lookup(Passageway, Direction.W));
    }

    // ----- the Doors tab -------------------------------------------------

    // Every bare key lock on the map, with what it is taken for and where that comes
    // from; an edit from the tab reaches the book and the map at once.
    [Fact]
    public void DoorsTab_ListsEveryBareLock_AndItsEditsTakeEffect()
    {
        Rig rig = NewRig();
        using DoorsSectionViewModel tab = new(rig.Book, rig.Graph, id => Keys.GetValueOrDefault(id), () => null);

        Assert.Equal(4, tab.AllRows.Count);
        IReadOnlyList<DoorsRowInfo> sides = DoorsRowInfo.AllOn(rig.Graph.Rooms, rig.Book, id => Keys.GetValueOrDefault(id));
        DoorsRowInfo passage = sides.Single(s => s.Room == Passageway && s.Dir == Direction.W);
        Assert.Equal(("door or gate (bashes)", "seed", "green metal key"), (passage.KindText, passage.Source, passage.KeyName));
        DoorsRowInfo vault = sides.Single(s => s.Room == Vault && s.Dir == Direction.E);
        Assert.Equal(("not known", "—"), (vault.KindText, vault.Source));

        tab.Apply(passage, new DoorEdit(BareLockBash.NotBashable, ResetToSeed: false));
        Assert.Equal(BareLockBash.NotBashable, ExitOf(rig, Passageway, Direction.W).BareLockBash);
        Assert.Equal("set by you", DoorsRowInfo.AllOn(rig.Graph.Rooms, rig.Book, _ => null)
            .Single(s => s.Room == Passageway && s.Dir == Direction.W).Source);

        tab.Apply(passage, new DoorEdit(null, ResetToSeed: true));
        Assert.Equal(BareLockBash.Bashable, rig.Book.Lookup(Passageway, Direction.W));
    }

    // The edit dialog's choice is the kind saved, and a choice that leaves the side as
    // it reads saves nothing (opening and saving pins no kind).
    [Theory]
    [InlineData("door", BareLockBash.Bashable)]
    [InlineData("key exit", BareLockBash.NotBashable)]
    public void DoorEditDialog_SavesTheKindChosen(string choice, BareLockBash saved)
    {
        Rig rig = NewRig();
        DoorsRowInfo vault = DoorsRowInfo.AllOn(rig.Graph.Rooms, rig.Book, _ => null)
            .Single(s => s.Room == Vault && s.Dir == Direction.E);
        DoorEditDialogViewModel dialog = new(vault, rig.Book);
        Assert.True(dialog.IsUnknown);
        Assert.False(dialog.CanSave);
        DoorEdit? closed = null;
        dialog.CloseRequested += e => closed = e;

        if (choice == "door") dialog.IsDoor = true;
        else dialog.IsKeyExit = true;
        dialog.IsUnknown = false;
        dialog.SaveCommand.Execute(null);

        Assert.Equal(new DoorEdit(saved, ResetToSeed: false), closed);
    }

    // While the dialog is open, the side changing shows.
    [Fact]
    public void DoorEditDialog_ShowsAChangeToTheSideWhileOpen()
    {
        Rig rig = NewRig();
        DoorsRowInfo vault = DoorsRowInfo.AllOn(rig.Graph.Rooms, rig.Book, _ => null)
            .Single(s => s.Room == Vault && s.Dir == Direction.E);
        DoorEditDialogViewModel dialog = new(vault, rig.Book);

        rig.Book.Learn(Vault, Direction.E, bashable: true);

        Assert.Contains("changed since you opened it", dialog.StaleNote);
    }

    // "Forget learned…" asks first, and keeps the record when the user says no.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DoorsTab_ForgetLearned_AsksFirst(bool agree)
    {
        Rig rig = NewRig();
        rig.Book.Learn(Vault, Direction.E, bashable: true);
        string? asked = null;
        using DoorsSectionViewModel tab = new(rig.Book, rig.Graph, _ => null, () => null,
            confirm: (title, _) => { asked = title; return Task.FromResult(agree); });

        await tab.ForgetLearnedCommand.ExecuteAsync(null);

        Assert.NotNull(asked);
        Assert.Equal(agree ? 0 : 1, rig.Book.LearnedCount);
    }

    // With nothing learned, "Forget learned…" says so rather than do nothing.
    [Fact]
    public async Task DoorsTab_ForgetLearned_WithNothingLearned_SaysSo()
    {
        Rig rig = NewRig();
        bool asked = false;
        using DoorsSectionViewModel tab = new(rig.Book, rig.Graph, _ => null, () => null,
            confirm: (_, _) => { asked = true; return Task.FromResult(true); });

        await tab.ForgetLearnedCommand.ExecuteAsync(null);

        Assert.False(asked);
        Assert.Contains("Nothing learned to forget", tab.BannerText);
    }

    // ----- picking -----------------------------------------------------

    // Client policy (user, 2026-10-18): with Picklocks a keyed door is picked first,
    // even with the key carried, after an open for a door only shut. This one can't
    // be bashed, so the key follows the picks.
    [Fact]
    public void KeyedDoor_WithPicklocks_OpensFirst_ThenPicks_ThenTheKey()
    {
        Rig rig = NewRig(picklocks: 40);
        rig.ReadInventory("You have the following keys: green metal key.");
        rig.Tracker.SetLocated(new RoomKey(1, 1789));
        rig.Walker.WalkTo(Passageway);
        Assert.Equal("open e", rig.Sent[^1]);

        rig.Server.Line("The door is locked.");
        Assert.Equal("pick e", rig.Sent[^1]);
        for (int i = 0; i < 3; i++) rig.Server.Line("Your skill fails you this time.");

        Assert.Equal("use green metal key e", rig.Sent[^1]);
    }

    // A door only shut, not locked, is opened, and no pick is spent on it.
    [Fact]
    public void ShutButUnlockedDoor_IsOpened_WithNoPick()
    {
        Rig rig = NewRig(picklocks: 40);
        rig.ReadInventory("You have no keys.");
        rig.Tracker.SetLocated(new RoomKey(1, 1789));
        rig.Walker.WalkTo(Passageway);

        rig.Server.Line("The door is now open.");

        Assert.Equal(new[] { "open e", "e" }, rig.Sent);
    }

    // No Picklocks and no bash: never a pick, so the key is all there is.
    [Fact]
    public void KeyedDoor_WithoutPicklocks_OrABash_UsesTheKey()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have the following keys: green metal key.");
        rig.Tracker.SetLocated(new RoomKey(1, 1789));
        rig.Walker.WalkTo(Passageway);

        Assert.Equal(new[] { "use green metal key e" }, rig.Sent);
    }

    // A pick answered "Your command had no effect." in the room the door is in: no
    // lock to pick there. It was handed back as NotHere and the walker planned
    // through the same door on every re-plan; now the door is left out of picks for
    // the session and the route keeps off it when the pick was all it had.
    [Fact]
    public void PickWithNoEffect_IsLeftOut_AndTheRouteKeepsOff()
    {
        Rig rig = NewRig(strength: 0, picklocks: 40);
        rig.ReadInventory("You have no keys.");
        rig.Tracker.SetLocated(Antechamber);
        rig.Walker.WalkTo(new RoomKey(9, 5));
        rig.Server.Line("The door is locked.");
        Assert.Equal("pick e", rig.Sent[^1]);

        rig.Server.Line("Your command had no effect.");

        Assert.True(rig.Door.IsUnpickable(Antechamber, new RoomKey(9, 5)));
        Assert.Contains(rig.Events, e => e.Kind == WalkEventKind.Failed);
        int sent = rig.Sent.Count;
        Assert.False(rig.Walker.WalkTo(new RoomKey(9, 5)));
        Assert.Equal(sent, rig.Sent.Count);
    }

    // ----- what opens each kind of door (DoorPolicy) -------------------

    [Fact]
    public void Openers_FollowWhatIsKnown()
    {
        Assert.True(RoomExit.TryParseWire("1/1789 (Key: 475)", out RoomExit bare));
        Assert.True(bare.IsBareKeyLock);
        Assert.Equal((1, false), DoorPolicy.Openers(in bare));
        Assert.Equal((1, true), DoorPolicy.PlannedOpeners(in bare));
        RoomExit door = bare with { BareLockBash = BareLockBash.Bashable };
        Assert.Equal((1, true), DoorPolicy.Openers(in door));
        RoomExit keyExit = bare with { BareLockBash = BareLockBash.NotBashable };
        Assert.Equal((1, false), DoorPolicy.PlannedOpeners(in keyExit));

        Assert.True(RoomExit.TryParseWire("1/1790 (Key: 475 [or any picklocks])", out RoomExit any));
        Assert.False(any.IsBareKeyLock);
        Assert.Equal((0, false), DoorPolicy.Openers(in any));
    }

    // Planning through each kind without its key.
    [Fact]
    public void RouteFilter_FollowsWhatOpensEachKind()
    {
        Rig rig = NewRig(strength: 111, picklocks: 0);
        rig.ReadInventory("You have no keys.");
        RoomExit door = ExitOf(rig, Passageway, Direction.W);
        RoomExit keyExit = ExitOf(rig, ThragsGuardroom, Direction.E);
        RoomExit unknown = ExitOf(rig, Vault, Direction.E);
        Assert.True(RoomExit.TryParseWire("1/1790 (Key: 475 [or any picklocks])", out RoomExit any));

        // No key, no Picklocks: a door and an unknown lock bash; a key exit and an
        // any-picklocks key lock are shut.
        Assert.False(rig.Filter.IsExitBlocked(in door));
        Assert.False(rig.Filter.IsExitBlocked(in unknown));
        Assert.True(rig.Filter.IsExitBlocked(in keyExit));
        Assert.True(rig.Filter.IsExitBlocked(in any));
        Assert.Contains("a key exit", rig.Filter.DescribeDoorRefusal(in keyExit));

        // A fair pick opens a key exit to the plan; a poor one leaves its key to fetch.
        rig.Stats.Picklocks = 40;
        Assert.False(rig.Filter.IsExitBlocked(in keyExit));
        Assert.False(rig.Filter.IsExitBlocked(in any));
        rig.Stats.Picklocks = 10;
        Assert.True(rig.Filter.IsExitBlocked(in keyExit));
        Assert.Equal(ExitBlockReason.LockedDoor, rig.Filter.DescribeExitBlock(in keyExit));

        // The key in hand opens them whatever the skills.
        rig.Stats.Picklocks = 0;
        rig.ReadInventory("You have the following keys: green metal key, large iron key.");
        Assert.False(rig.Filter.IsExitBlocked(in keyExit));
        Assert.False(rig.Filter.IsExitBlocked(in any));
    }

    // With no stat screen read, a bare lock known to be a key exit stays shut without
    // its key; one that bashes is left to the door handler, as other doors are.
    [Fact]
    public void BareKeyLock_WithNoStatScreen()
    {
        Rig rig = NewRig();
        rig.ReadInventory("You have no keys.");
        rig.Filter.StrengthProvider = () => null;
        rig.Filter.PicklocksProvider = () => null;

        Assert.True(rig.Filter.IsExitBlocked(ExitOf(rig, ThragsGuardroom, Direction.E)));
        Assert.Contains("no stat screen read", rig.Filter.DescribeDoorRefusal(ExitOf(rig, ThragsGuardroom, Direction.E)));
        Assert.False(rig.Filter.IsExitBlocked(ExitOf(rig, Passageway, Direction.W)));
    }

    // A hand-edited record can hold a null for a side; it is passed over and the
    // rest of the realm's record loads.
    [Fact]
    public void ANullSideInTheRealmsRecord_IsPassedOver()
    {
        string realm = RealmFolder("A");
        File.WriteAllText(AppPaths.RealmBareKeyLocksFile(realm), """
            { "9/1 E": null, "9/2 W": { "Door": 2, "KeyExit": 0 } }
            """);
        BareKeyLockBook book = new((_, _) => null);
        BareKeyLockStore store = new(book);

        store.OnRealmChanged(realm);

        Assert.Equal(1, book.LearnedCount);
        Assert.Equal(2, book.RecordOf(new RoomKey(9, 2), Direction.W)!.Value.Door);
    }
}
