using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Covers seeding from Rooms.json, exit
// parsing (Door / Trap / unknown hint), uniqueness index, lair-flag
// recognition, and the per-set-swap reload.
public sealed class RoomGraphManagerTests : IDisposable
{
    private readonly string _root;

    public RoomGraphManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-roomgraph-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ----- helpers ---------------------------------------------------

    private GameDataCache NewCache() => new(_root);

    private void SeedRooms(string setName, string json)
    {
        string dir = Path.Combine(_root, setName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Rooms.json"), json);
    }

    // Route to the Necromancer (9/1431) needs two special gates the graph learns
    // here: an item-use teleport (3/1 → 9/1009 via `use potion of levitation`,
    // item 992, anchored on gate fixture 993's "Obtained From: Room 3/1") and a
    // room-CMD reveal lever (9/1012 N `clear rubble`, CMD 1422).
    private const string NecromancerRoomsJson = """
        [
          { "Map Number": 3, "Room Number": 1, "Name": "Mossy Cave, Waterfall",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "3/2", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "1/2362" },
          { "Map Number": 9, "Room Number": 1009, "Name": "Waterfall, Dirt Path",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "9/1010", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "3/1" },
          { "Map Number": 9, "Room Number": 1012, "Name": "Crumbling Ruin, Entrance",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 1422, "Lair": "", "Delay": 0,
            "N": "9/1013 (Hidden/Needs 1 Actions, any order)", "S": "9/1011",
            "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 9, "Room Number": 1013, "Name": "Crumbling Ruin",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "9/1014", "S": "9/1012", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;
    private const string NecromancerItemsJson = """
        [
          { "Number": 992, "Name": "potion of levitation", "Abil-0": 43, "AbilVal-0": 607,
            "Obtained From": "NPC #401" },
          { "Number": 993, "Name": "waterfall", "Obtained From": "Room 3/1" }
        ]
        """;
    private const string NecromancerSpellsJson = """
        [ { "Number": 607, "Name": "potion of levitation", "Abil-0": 148, "AbilVal-0": 1421 } ]
        """;
    private const string NecromancerTbInfoJson = """
        [
          { "Number": 1421, "LinkTo": 0,
            "Action": "roomitem 993 1834:message 1835:teleport 1009 9:message 1836\n",
            "Called From": "Spell #607" },
          { "Number": 1422, "LinkTo": 0,
            "Action": "clear rubble:testskill strength 0 1423:remoteaction 1012 1840 0 0\nmove rubble:testskill strength 0 1423:remoteaction 1012 1840 0 0\n",
            "Called From": "Room 9/1012" }
        ]
        """;

    [Fact]
    public void OnActiveSetChanged_WiresItemUseTeleport_And_RoomCmdRevealLever()
    {
        SeedRooms("alpha", NecromancerRoomsJson);
        SeedTable("alpha", "Items", NecromancerItemsJson);
        SeedTable("alpha", "Spells", NecromancerSpellsJson);
        SeedTable("alpha", "TBInfo", NecromancerTbInfoJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        TBInfoStore store = new(cache);
        store.OnActiveSetChanged("alpha");
        RoomGraphManager graph = new(cache, log: null, tbinfo: store, spellCatalog: new KnownSpellCatalog(cache));

        graph.OnActiveSetChanged("alpha");

        // Gate 1 — item-use teleport minted at the fixture's room (3/1), pointing at
        // the teleport destination, gated on carrying the holder item, crossing by
        // using it.
        Room? mossy = graph.GetRoom(new RoomKey(3, 1));
        Assert.NotNull(mossy);
        Assert.True(mossy!.Exits.TryGetValue(Direction.Teleport, out RoomExit tele));
        Assert.Equal(new RoomKey(9, 1009), tele.Target);
        Assert.Equal(RoomExitHint.Teleport, tele.Hint);
        Assert.Equal(992, tele.KeyItemId);
        Assert.Equal("use potion of levitation", tele.TextCommands![0]);

        // Gate 2 — the room-CMD `clear rubble` promoted the N exit's empty
        // MultiActionHidden into a satisfiable, same-room action BFS can route.
        Room? entrance = graph.GetRoom(new RoomKey(9, 1012));
        Assert.NotNull(entrance);
        Assert.True(entrance!.Exits.TryGetValue(Direction.N, out RoomExit hidden));
        Assert.Equal(RoomExitHint.MultiActionHidden, hidden.Hint);
        Assert.NotNull(hidden.MultiAction);
        Assert.True(hidden.MultiAction!.IsSatisfiable);
        Assert.False(hidden.MultiAction.HasRemoteActions);
        Assert.Equal("clear rubble", hidden.MultiAction.Actions[0].Commands[0]);
    }

    // ----- RoomKey.TryParseWire --------------------------------------

    [Theory]
    [InlineData("1/3",          1, 3)]
    [InlineData("12/345",       12, 345)]
    [InlineData("1/1381 (Door)", 1, 1381)]   // door hint stripped before parse
    [InlineData("  5/7  ",      5, 7)]
    public void RoomKey_TryParseWire_Valid(string wire, int map, int room)
    {
        Assert.True(RoomKey.TryParseWire(wire, out RoomKey key));
        Assert.Equal(new RoomKey(map, room), key);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("1")]                // no slash
    [InlineData("1/")]               // trailing slash
    [InlineData("/3")]               // leading slash
    [InlineData("0/3")]              // map = 0 is the no-exit sentinel half
    [InlineData("abc/3")]
    public void RoomKey_TryParseWire_Invalid(string? wire)
    {
        Assert.False(RoomKey.TryParseWire(wire, out _));
    }

    // ----- RoomExit.TryParseWire -------------------------------------

    [Fact]
    public void RoomExit_PlainExit_HasNoHint()
    {
        Assert.True(RoomExit.TryParseWire("1/3", out RoomExit exit));
        Assert.Equal(new RoomKey(1, 3), exit.Target);
        Assert.Equal(RoomExitHint.None, exit.Hint);
        Assert.Null(exit.RawHint);
    }

    [Fact]
    public void RoomExit_DoorHint_Classified()
    {
        Assert.True(RoomExit.TryParseWire("1/1381 (Door)", out RoomExit exit));
        Assert.Equal(RoomExitHint.Door, exit.Hint);
        Assert.Equal("Door", exit.RawHint);
    }

    [Fact]
    public void RoomExit_TrapHint_Classified()
    {
        Assert.True(RoomExit.TryParseWire("9/42 (Trap)", out RoomExit exit));
        Assert.Equal(RoomExitHint.Trap, exit.Hint);
        Assert.Equal("Trap", exit.RawHint);
    }

    [Theory]
    [InlineData("1/2 (Trap, 30 damage)",   "Trap, 30 damage")]
    [InlineData("1/2 (Trap, 45 damage)",   "Trap, 45 damage")]
    [InlineData("1/2 (Trap, 120 damage)",  "Trap, 120 damage")]
    [InlineData("1/2 (Spell Trap: 905)",   "Spell Trap: 905")]
    public void RoomExit_TrapVariants_ClassifyAsTrap(string wire, string rawHint)
    {
        Assert.True(RoomExit.TryParseWire(wire, out RoomExit exit));
        Assert.Equal(RoomExitHint.Trap, exit.Hint);
        Assert.Equal(rawHint, exit.RawHint);
    }

    [Theory]
    [InlineData("1/2 (Door)",           "Door")]
    [InlineData("1/2 (Door 1234)",      "Door 1234")]
    public void RoomExit_DoorVariants_ClassifyAsDoor(string wire, string rawHint)
    {
        Assert.True(RoomExit.TryParseWire(wire, out RoomExit exit));
        Assert.Equal(RoomExitHint.Door, exit.Hint);
        Assert.Equal(rawHint, exit.RawHint);
    }

    // A level or alignment restriction is a gate, not a hint: Hint stays None,
    // the gate parses, and the raw text is kept.
    [Theory]
    [InlineData("1/2 (Level: 30 to 999)",       "Level: 30 to 999", true, false)]
    [InlineData("1/2 (Alignment: Saint to Outlaw)", "Alignment: Saint to Outlaw", false, true)]
    public void RoomExit_GatedExits_ParseTheGate_AndPreserveRaw(string wire, string rawHint, bool level, bool alignment)
    {
        Assert.True(RoomExit.TryParseWire(wire, out RoomExit exit));
        Assert.Equal(RoomExitHint.None, exit.Hint);
        Assert.Equal(rawHint, exit.RawHint);
        Assert.Equal(level, exit.HasLevelGate);
        Assert.Equal(alignment, exit.HasAlignmentGate);
    }

    [Fact]
    public void RoomExit_KeyHint_IsKeyLocked()
    {
        // (Key: N) is KeyLocked with the item id captured.
        Assert.True(RoomExit.TryParseWire("1/2 (Key: 5)", out RoomExit exit));
        Assert.Equal(RoomExitHint.KeyLocked, exit.Hint);
        Assert.Equal(5, exit.KeyItemId);
    }

    [Fact]
    public void RoomExit_HiddenHint_IsSearchable()
    {
        // Plain (Hidden) is SearchableHidden, so the walker knows to
        // `sea <dir>` before stepping.
        Assert.True(RoomExit.TryParseWire("1/2 (Hidden)", out RoomExit exit));
        Assert.Equal(RoomExitHint.SearchableHidden, exit.Hint);
    }

    [Fact]
    public void RoomExit_UnknownHint_FallsThroughToNoneButPreservesRaw()
    {
        Assert.True(RoomExit.TryParseWire("3/7 (Climb)", out RoomExit exit));
        Assert.Equal(new RoomKey(3, 7), exit.Target);
        Assert.Equal(RoomExitHint.None, exit.Hint);
        Assert.Equal("Climb", exit.RawHint);
    }

    // ----- RoomGraphManager seeding ----------------------------------
    //
    // Tests below use an empty-string Lair field rather than the real
    // MDB's NUL sentinel because JsonDocument.Parse rejects raw NUL
    // bytes inside strings. IsLairSentinel treats empty, whitespace,
    // and NUL equivalently as no-lair, so coverage stays the same.

    private const string TwoRoomJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Town Gates",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/3", "S": "0", "E": "1/1381 (Door)", "W": "1/101",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "North Square",
            "Light": 0, "Shop": 5, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void OnActiveSetChanged_LoadsRooms_FromActiveSet()
    {
        SeedRooms("alpha", TwoRoomJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);

        graph.OnActiveSetChanged("alpha");

        Assert.Equal("alpha", graph.ActiveSet);
        Assert.Equal(2, graph.RoomCount);

        Room? r1 = graph.GetRoom(new RoomKey(1, 1));
        Assert.NotNull(r1);
        Assert.Equal("Town Gates", r1!.Name);
        Assert.False(r1.HasLair);
        Assert.Equal(3, r1.Exits.Count);                       // N, E, W
        Assert.Equal(new RoomKey(1, 3), r1.Exits[Direction.N].Target);
        Assert.Equal(RoomExitHint.Door, r1.Exits[Direction.E].Hint);
        Assert.Equal(RoomExitHint.None, r1.Exits[Direction.W].Hint);
        Assert.Equal(
            (1u << (int)Direction.N) | (1u << (int)Direction.E) | (1u << (int)Direction.W),
            r1.ExitMask);

        Room? r3 = graph.GetRoom(new RoomKey(1, 3));
        Assert.NotNull(r3);
        Assert.Equal("North Square", r3!.Name);
        Assert.Equal(5, r3.Shop);
        Assert.Single(r3.Exits);
        Assert.Equal(new RoomKey(1, 1), r3.Exits[Direction.S].Target);
    }

    [Fact]
    public void OnActiveSetChanged_ParsesNpcPlacement()
    {
        // The NPC field is the placed-monster (fixed-spawn) home-room
        // pointer — drives the Monsters tab's "Placed In" lookup. Room 1
        // places monster 1076; room 3 has no placement (NPC absent → 0).
        const string json = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "Throne",
                "Light": 0, "Shop": 0, "NPC": 1076, "Lair": "", "Delay": 0,
                "N": "1/3", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "Hall",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/1", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        SeedRooms("alpha", json);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);

        graph.OnActiveSetChanged("alpha");

        Assert.Equal(1076, graph.GetRoom(new RoomKey(1, 1))!.Npc);
        Assert.Equal(0, graph.GetRoom(new RoomKey(1, 3))!.Npc);   // absent → 0
    }

    [Fact]
    public void OnActiveSetChanged_FiresGraphReloaded()
    {
        SeedRooms("alpha", TwoRoomJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);

        int fires = 0;
        graph.GraphReloaded += () => fires++;

        graph.OnActiveSetChanged("alpha");
        graph.OnActiveSetChanged(null);
        graph.OnActiveSetChanged("alpha");

        Assert.Equal(3, fires);
        Assert.Equal(2, graph.RoomCount);
    }

    [Fact]
    public void OnActiveSetChanged_NullClearsGraph()
    {
        SeedRooms("alpha", TwoRoomJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        Assert.Equal(2, graph.RoomCount);

        graph.OnActiveSetChanged(null);

        Assert.Null(graph.ActiveSet);
        Assert.Equal(0, graph.RoomCount);
        Assert.Null(graph.GetRoom(new RoomKey(1, 1)));
    }

    [Fact]
    public void OnActiveSetChanged_EvictsRawTable_AfterLoad()
    {
        SeedRooms("alpha", TwoRoomJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);

        graph.OnActiveSetChanged("alpha");

        // Memory-hygiene pattern: typed Rooms collection is the source
        // of truth; the Rooms table is released, and gone at the cache's
        // next idle sweep since nothing reads it again.
        cache.EvictIdle(GameDataCache.IdleEvictAfter);
        Assert.False(cache.IsTableLoaded("Rooms"));
    }

    [Fact]
    public void OnActiveSetChanged_MissingTable_LeavesGraphEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);

        graph.OnActiveSetChanged("alpha");

        Assert.Equal(0, graph.RoomCount);
        Assert.Equal("alpha", graph.ActiveSet);
    }

    // ----- Lair tag recognition --------------------------------------

    private const string LairJson = """
        [
          { "Map Number": 5, "Room Number": 1, "Name": "Shadowmere Town Gates, Inner Bailey",
            "Light": 0, "Shop": 0,
            "Lair": "(Max 2): 1141,2175,2176,[5-6-8-2]",
            "Delay": 3,
            "N": "0", "S": "0", "E": "5/2 (Door)", "W": "5/849",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 5, "Room Number": 2, "Name": "Quiet Hall",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 1,
            "N": "0", "S": "0", "E": "0", "W": "5/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void Lair_Tag_Recognised_AsHasLair()
    {
        SeedRooms("alpha", LairJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");

        Room? lair = graph.GetRoom(new RoomKey(5, 1));
        Assert.NotNull(lair);
        Assert.True(lair!.HasLair);
        Assert.StartsWith("(Max 2):", lair.RawLairTag);

        Room? noLair = graph.GetRoom(new RoomKey(5, 2));
        Assert.NotNull(noLair);
        Assert.False(noLair!.HasLair);
        Assert.Null(noLair.RawLairTag);
    }

    // ----- name + uniqueness indexes ---------------------------------

    private const string TwinRoomsJson = """
        [
          { "Map Number": 1, "Room Number": 10, "Name": "Sewer Tunnel",
            "Light": -200, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/11", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 11, "Name": "Sewer Tunnel",
            "Light": -200, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/12", "S": "1/10", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 12, "Name": "Sewer Tunnel",
            "Light": -200, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/11", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void FindByName_ReturnsAllMatches()
    {
        SeedRooms("alpha", TwinRoomsJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");

        var matches = graph.FindByName("Sewer Tunnel");
        Assert.Equal(3, matches.Count);
    }

    [Fact]
    public void FindByName_IsCaseInsensitive()
    {
        SeedRooms("alpha", TwinRoomsJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");

        Assert.Equal(3, graph.FindByName("sewer tunnel").Count);
    }

    [Fact]
    public void FindByName_Empty_Returns_EmptyList()
    {
        SeedRooms("alpha", TwinRoomsJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");

        Assert.Empty(graph.FindByName("Nowhere"));
        Assert.Empty(graph.FindByName(""));
    }

    [Fact]
    public void FindCandidates_DistinctExitMasks_AreSeparateBuckets()
    {
        // Twin Rooms 10 and 12 each have exactly one exit (N for #10, S for #12)
        // — same name but different exit masks. Room #11 has {N, S}.
        SeedRooms("alpha", TwinRoomsJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");

        var northOnly = graph.FindCandidates("Sewer Tunnel", new HashSet<Direction> { Direction.N });
        Assert.Single(northOnly);
        Assert.Equal(new RoomKey(1, 10), northOnly[0]);

        var southOnly = graph.FindCandidates("Sewer Tunnel", new HashSet<Direction> { Direction.S });
        Assert.Single(southOnly);
        Assert.Equal(new RoomKey(1, 12), southOnly[0]);

        var both = graph.FindCandidates("Sewer Tunnel",
            new HashSet<Direction> { Direction.N, Direction.S });
        Assert.Single(both);
        Assert.Equal(new RoomKey(1, 11), both[0]);
    }

    // The room's own name and exits find it and nothing else, as a sighting of it
    // would.
    private static bool FoundAlone(RoomGraphManager graph, RoomKey key)
    {
        Room room = graph.GetRoom(key)!;
        return graph.FindCandidates(room.Name, new HashSet<Direction>(room.Exits.Keys))
            .SequenceEqual(new[] { key });
    }

    [Fact]
    public void FindCandidates_OneOfOneTuple_FindsTheRoomAlone()
    {
        SeedRooms("alpha", TwinRoomsJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");

        // All three have distinct (name, exit-mask) tuples even though
        // the names are all identical.
        Assert.True(FoundAlone(graph, new RoomKey(1, 10)));
        Assert.True(FoundAlone(graph, new RoomKey(1, 11)));
        Assert.True(FoundAlone(graph, new RoomKey(1, 12)));
    }

    [Fact]
    public void FindCandidates_AmbiguousTuple_FindsEveryRoomInIt()
    {
        // Two rooms with same name AND same exit set.
        const string AmbiguousJson = """
            [
              { "Map Number": 2, "Room Number": 1, "Name": "Hallway",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "2/2", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 2, "Room Number": 2, "Name": "Hallway",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "2/1", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 2, "Room Number": 3, "Name": "Hallway",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "2/4", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 2, "Room Number": 4, "Name": "Other Hall",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "2/3", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        SeedRooms("beta", AmbiguousJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet("beta");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("beta");

        // 2/1 has Hallway + {N}; 2/3 has Hallway + {N}. Ambiguous.
        Assert.False(FoundAlone(graph, new RoomKey(2, 1)));
        Assert.False(FoundAlone(graph, new RoomKey(2, 3)));

        // 2/2 has Hallway + {S}; nothing else collides. Unique.
        Assert.True(FoundAlone(graph, new RoomKey(2, 2)));
    }

    // ----- unreachable filter (CannotBeReached) ----------------------

    // Same fixture as FindCandidates_AmbiguousTuple_FindsEveryRoomInIt: 2/1 and 2/3 are both
    // "Hallway" + {N}, so they share a candidate bucket the ConfigureUnreachable
    // predicate can prune.
    private const string HallwayAmbiguousJson = """
        [
          { "Map Number": 2, "Room Number": 1, "Name": "Hallway",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "2/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 2, "Room Number": 3, "Name": "Hallway",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "2/4", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void FindCandidates_NoPredicate_ReturnsWholeBucket()
    {
        SeedRooms("beta", HallwayAmbiguousJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet("beta");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("beta");

        var hits = graph.FindCandidates("Hallway", new HashSet<Direction> { Direction.N });
        Assert.Equal(2, hits.Count);
        Assert.Contains(new RoomKey(2, 1), hits);
        Assert.Contains(new RoomKey(2, 3), hits);
    }

    [Fact]
    public void FindCandidates_ExcludesUnreachable_ReturnsRemainingInOrder()
    {
        SeedRooms("beta", HallwayAmbiguousJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet("beta");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("beta");

        // Flag 2/3 unreachable → the ambiguous bucket collapses to the single
        // reachable room, so the tracker can promote to it cleanly.
        graph.ConfigureUnreachable(k => k.Equals(new RoomKey(2, 3)));

        var hits = graph.FindCandidates("Hallway", new HashSet<Direction> { Direction.N });
        Assert.Single(hits);
        Assert.Equal(new RoomKey(2, 1), hits[0]);
    }

    [Fact]
    public void FindCandidates_AllUnreachable_ReturnsEmpty()
    {
        SeedRooms("beta", HallwayAmbiguousJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet("beta");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("beta");

        // Both rooms flagged → the bucket resolves to zero, so the tracker
        // replays / stays Lost rather than stranding the player.
        graph.ConfigureUnreachable(_ => true);

        Assert.Empty(graph.FindCandidates("Hallway", new HashSet<Direction> { Direction.N }));
    }

    [Fact]
    public void FindCandidates_ClearingPredicate_RestoresFullBucket()
    {
        SeedRooms("beta", HallwayAmbiguousJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet("beta");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("beta");

        graph.ConfigureUnreachable(_ => true);
        Assert.Empty(graph.FindCandidates("Hallway", new HashSet<Direction> { Direction.N }));

        // Null predicate means "no exclusions" — the stored bucket returns as-is.
        graph.ConfigureUnreachable(null);
        Assert.Equal(2, graph.FindCandidates("Hallway", new HashSet<Direction> { Direction.N }).Count);
    }

    // ----- FindByNameCoveringExits (door-tolerant re-anchor) ---------

    // A name-unique room with three exits — the re-anchor path resolves it
    // even when a closed door hides one or two of them.
    private const string FoyerJson = """
        [
          { "Map Number": 1, "Room Number": 20, "Name": "Grand Foyer",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/21", "S": "1/22", "E": "1/23", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 21, "Name": "Corridor",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/20", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void FindByNameCoveringExits_SubsetOfGraphExits_MatchesUniqueRoom()
    {
        SeedRooms("alpha", FoyerJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");

        // Only {N} observed (S/E doors closed) — still a subset of {N,S,E}, and
        // the name is unique, so the room re-latches where the exact-mask
        // FindCandidates would have missed.
        var hits = graph.FindByNameCoveringExits("Grand Foyer",
            new HashSet<Direction> { Direction.N });
        Assert.Single(hits);
        Assert.Equal(new RoomKey(1, 20), hits[0]);

        Assert.Empty(graph.FindCandidates("Grand Foyer",
            new HashSet<Direction> { Direction.N }));
    }

    [Fact]
    public void FindByNameCoveringExits_ObservedExitNotInGraph_NoMatch()
    {
        SeedRooms("alpha", FoyerJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");

        // {N, W}: W isn't a graph exit of the Foyer. A door only ever hides an
        // exit, never invents one, so this observation can't be this room.
        var hits = graph.FindByNameCoveringExits("Grand Foyer",
            new HashSet<Direction> { Direction.N, Direction.W });
        Assert.Empty(hits);
    }

    [Fact]
    public void FindByNameCoveringExits_EmptyObservation_MatchesByNameAlone()
    {
        SeedRooms("alpha", FoyerJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");

        var hits = graph.FindByNameCoveringExits("Grand Foyer", new HashSet<Direction>());
        Assert.Single(hits);
        Assert.Equal(new RoomKey(1, 20), hits[0]);
    }

    [Fact]
    public void FindByNameCoveringExits_SubsetMatchesMultiple_ReturnsAll()
    {
        // Sewer Tunnel bucket: #10 {N}, #11 {N,S}, #12 {S}. Observing {N} is a
        // subset of both #10 and #11, so the door-tolerant match is ambiguous —
        // the caller (RoomTracker) only re-anchors on a single hit.
        SeedRooms("alpha", TwinRoomsJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");

        var hits = graph.FindByNameCoveringExits("Sewer Tunnel",
            new HashSet<Direction> { Direction.N });
        Assert.Equal(2, hits.Count);
        Assert.Contains(new RoomKey(1, 10), hits);
        Assert.Contains(new RoomKey(1, 11), hits);
    }

    [Fact]
    public void FindByNameCoveringExits_ExcludesUnreachable()
    {
        SeedRooms("alpha", TwinRoomsJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");

        // Prune #11 → the {N} subset collapses to the single reachable #10, so
        // the tracker can re-anchor cleanly.
        graph.ConfigureUnreachable(k => k.Equals(new RoomKey(1, 11)));
        var hits = graph.FindByNameCoveringExits("Sewer Tunnel",
            new HashSet<Direction> { Direction.N });
        Assert.Single(hits);
        Assert.Equal(new RoomKey(1, 10), hits[0]);
    }

    [Fact]
    public void FindByNameCoveringExits_UnknownName_ReturnsEmpty()
    {
        SeedRooms("alpha", FoyerJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");

        Assert.Empty(graph.FindByNameCoveringExits("Nowhere",
            new HashSet<Direction> { Direction.N }));
        Assert.Empty(graph.FindByNameCoveringExits("",
            new HashSet<Direction> { Direction.N }));
    }

    // ----- swap reload -----------------------------------------------

    [Fact]
    public void SwapBetweenSets_RebuildsGraph()
    {
        SeedRooms("alpha", TwoRoomJson);
        SeedRooms("beta", TwinRoomsJson);
        GameDataCache cache = NewCache();
        RoomGraphManager graph = new(cache);

        cache.SwitchSet("alpha");
        graph.OnActiveSetChanged("alpha");
        Assert.Equal(2, graph.RoomCount);
        Assert.NotNull(graph.GetRoom(new RoomKey(1, 1)));

        cache.SwitchSet("beta");
        graph.OnActiveSetChanged("beta");
        Assert.Equal(3, graph.RoomCount);
        Assert.Null(graph.GetRoom(new RoomKey(1, 1)));         // alpha-only key gone
        Assert.NotNull(graph.GetRoom(new RoomKey(1, 10)));     // beta-only key present
    }

    // ----- malformed input -------------------------------------------

    [Fact]
    public void MalformedRow_Skipped_DoesNotThrow()
    {
        const string PartlyBrokenJson = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "Good Room",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Name": "Missing Keys" },
              { "Map Number": 1, "Room Number": 2, "Name": "",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        SeedRooms("alpha", PartlyBrokenJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);

        graph.OnActiveSetChanged("alpha");

        // Rows without primary-key fields are still skipped, but rows
        // with an empty/null Name are kept and surfaced via the
        // null-name learning flow (Display "???"). The mid-row
        // "Missing Keys" still drops.
        Assert.Equal(2, graph.RoomCount);
        Assert.NotNull(graph.GetRoom(new RoomKey(1, 1)));
        Room? nameless = graph.GetRoom(new RoomKey(1, 2));
        Assert.NotNull(nameless);
        Assert.True(nameless!.HasUnknownName);
        Assert.Equal("???", nameless.DisplayName);
    }

    // ----- Room.Cmd + Item → Teleport promotion ----------------------

    [Fact]
    public void Room_Cmd_FieldIsReadFromJson()
    {
        const string json = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "Casino",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 997, "Lair": "",
                "Delay": 5, "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        SeedRooms("alpha", json);

        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");

        Room? room = graph.GetRoom(new RoomKey(1, 1));
        Assert.NotNull(room);
        Assert.Equal(997, room!.Cmd);
    }

    private const string ItemCmdRooms = """
        [
          { "Map Number": 7, "Room Number": 130, "Name": "Grove",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 5, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "7/131 (Item: 474)", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void Item_OnExit_WithCmdTeleportingToTarget_PromotesToTeleport()
    {
        // Source room CMD=5 whose TBInfo chain teleports to the (Item: 474) exit's
        // target (7/131) → the item-gated exit is re-hinted Teleport (a real
        // party-breaking emblem/chime teleport).
        const string tbInfo = """
            [ { "Number": 5, "Action": "wave emblem:teleport 131 7\n" } ]
            """;
        var (graph, _) = BuildWithTbInfo("alpha", ItemCmdRooms, tbInfo);

        Room? room = graph.GetRoom(new RoomKey(7, 130));
        Assert.NotNull(room);
        Assert.True(room!.Exits.TryGetValue(Direction.SE, out RoomExit ex));
        Assert.Equal(RoomExitHint.Teleport, ex.Hint);
        Assert.Equal(474, ex.KeyItemId);
    }

    [Fact]
    public void Item_OnExit_WithCmdNotTeleportingToTarget_StaysItem()
    {
        // Regression (report paradigm-20260729-221044): a non-zero CMD that is NOT
        // a teleport to the exit's target (here a make-emblem action) must NOT
        // promote the item-gated exit — it's a walkable item passage, and promoting
        // it stranded the walk-to on step 1 resolving a nonexistent teleport keyword.
        const string tbInfo = """
            [ { "Number": 5, "Action": "make emblem:roomitem 474 1\n" } ]
            """;
        var (graph, _) = BuildWithTbInfo("alpha", ItemCmdRooms, tbInfo);

        Room? room = graph.GetRoom(new RoomKey(7, 130));
        Assert.NotNull(room);
        Assert.True(room!.Exits.TryGetValue(Direction.SE, out RoomExit ex));
        Assert.Equal(RoomExitHint.Item, ex.Hint);
        Assert.Equal(474, ex.KeyItemId);
    }

    [Fact]
    public void Item_OnExit_WithoutCmd_StaysItem()
    {
        // Source room CMD=0 + (Item: 474) on the exit → Item check
        // only (inventory requirement), not a teleport.
        const string json = """
            [
              { "Map Number": 7, "Room Number": 131, "Name": "Stone Arch",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "",
                "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "7/133 (Item: 474)", "NW": "0", "SE": "0", "SW": "0",
                "U": "0", "D": "0" }
            ]
            """;
        SeedRooms("alpha", json);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");

        Room? room = graph.GetRoom(new RoomKey(7, 131));
        Assert.NotNull(room);
        Assert.True(room!.Exits.TryGetValue(Direction.NE, out RoomExit ex));
        Assert.Equal(RoomExitHint.Item, ex.Hint);
        Assert.Equal(474, ex.KeyItemId);
    }

    // ----- lever-door promotion (Action cells on Door exits) ---------

    [Fact]
    public void RemoteLeverAnnotation_OnDoorExit_PromotesToMultiActionHidden()
    {
        // Inner-gate portcullis: 1/1331's N Door lifts only when levers in the
        // two flanking guardrooms (1/1345, 1/1339) are pulled. Both guardrooms
        // annotate the N exit of 1/1331. Without promotion the door imports as a
        // 301-picklock Door and the route picker routes around it.
        const string json = """
            [
              { "Map Number": 1, "Room Number": 1331, "Name": "Inner Gate",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "1/1375 (Door [301 picklocks/strength])", "S": "1/1322",
                "E": "1/1339", "W": "1/1345",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 1345, "Name": "Guardroom",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "Action [on the N exit of room 1/1331]: pull lever, push lever, move lever",
                "S": "0", "E": "1/1331", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 1339, "Name": "Guardroom",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "Action [on the N exit of room 1/1331]: pull lever, push lever, move lever",
                "S": "0", "E": "0", "W": "1/1331",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 1375, "Name": "Courtyard",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 1322, "Name": "Entrance",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        SeedRooms("alpha", json);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");

        Room? gate = graph.GetRoom(new RoomKey(1, 1331));
        Assert.NotNull(gate);
        Assert.True(gate!.Exits.TryGetValue(Direction.N, out RoomExit ex));
        Assert.Equal(RoomExitHint.MultiActionHidden, ex.Hint);          // promoted off Door
        Assert.Equal(new RoomKey(1, 1375), ex.Target);
        Assert.NotNull(ex.MultiAction);
        // Both bare "Action" cells share StepNumber 1, so they're redundant
        // alternatives — one lever opens the gate. The required count reflects the
        // distinct steps (1), though both cells are still recorded as the choices.
        Assert.Equal(1, ex.MultiAction!.RequiredActionCount);
        Assert.Equal(2, ex.MultiAction.Actions.Count);
        Assert.True(ex.MultiAction.HasRemoteActions);
        // Both action steps live in the guardrooms, not the gate room.
        Assert.All(ex.MultiAction.Actions, a => Assert.NotNull(a.RemoteSourceRoom));

        // Reverse index: each guardroom knows it holds a lever governing the
        // gate room's N exit, so its own tooltip can say so — the gate's
        // MultiAction attaches to 1/1331, not to the guardroom.
        IReadOnlyList<RoomGraphManager.RemoteLeverRef> fromEast =
            graph.LeversControlledFrom(new RoomKey(1, 1339));
        Assert.Single(fromEast);
        Assert.Equal(new RoomKey(1, 1331), fromEast[0].ControlledRoom);
        Assert.Equal(Direction.N, fromEast[0].Direction);
        Assert.Contains("pull lever", fromEast[0].Commands);

        Assert.Single(graph.LeversControlledFrom(new RoomKey(1, 1345)));
        // The gate room itself holds no remote lever.
        Assert.Empty(graph.LeversControlledFrom(new RoomKey(1, 1331)));

        // The guardroom tooltip now names the gate its lever opens.
        Room? guard = graph.GetRoom(new RoomKey(1, 1339));
        string tip = RoomTooltipBuilder.Build(guard!, graph, data: null);
        Assert.Contains("Levers here:", tip);
        Assert.Contains("pull lever", tip);
        Assert.Contains("Inner Gate", tip);
    }

    [Fact]
    public void LeverGate_FullRoute_FindsPath_AndEmitsOneLeverPull()
    {
        // End-to-end regression for the inner-gate lever route (bug reports
        // paradigm-20260714-091000 / -091244): with the gate room's N Door
        // promoted to a lever-operated MultiActionHidden exit, BFS must find a
        // path from the gate (1/1331) out to 1/1367, and the path expander must
        // splice in a guardroom lever pull as a go-act-return detour. A raw
        // 301-picklock Door with Strength 100 / Picklocks 0 would block — a found
        // route + a lever CommandStep proves the promotion + detour. The two
        // guardroom levers are redundant alternatives (same StepNumber, identical
        // commands, no "Needs N Actions" count), so only ONE is pulled
        // (paradigm-20260728-180730).
        const string json = """
            [
              { "Map Number": 1, "Room Number": 1322, "Name": "Monastery Entrance",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "1/1331 (Alignment: Saint to Neutral)", "S": "1/141",
                "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 1331, "Name": "Inner Gate",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "1/1375 (Door [301 picklocks/strength])", "S": "1/1322",
                "E": "1/1339", "W": "1/1345",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 1339, "Name": "Guardroom",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "Action [on the N exit of room 1/1331]: pull lever, push lever, move lever",
                "S": "0", "E": "0", "W": "1/1331",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 1345, "Name": "Guardroom",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "Action [on the N exit of room 1/1331]: pull lever, push lever, move lever",
                "S": "0", "E": "1/1331", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 1375, "Name": "Courtyard",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "1/1331 (Door [201 picklocks/strength])",
                "E": "0", "W": "0",
                "NE": "1/1373", "NW": "1/1374", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 1374, "Name": "Courtyard",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "1/1371", "SE": "1/1375", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 1371, "Name": "Outer Keep",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "1/1370", "NW": "0", "SE": "1/1374", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 1370, "Name": "Outer Keep, Hallway",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "1/1368", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "1/1371", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 1368, "Name": "Outer Keep, Intersection",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "1/1367 (Hidden/Searchable)", "E": "0", "W": "1/1370",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 1367, "Name": "Outer Keep, Stairwell Up",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "1/1368", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "1/1361", "D": "0" }
            ]
            """;
        SeedRooms("alpha", json);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");

        // Strength 100 / Picklocks 0 — a raw Door 301 is unpassable, so any
        // found route depends on the lever promotion.
        ProfileService profile = new();
        MovementFilter filter = new(profile)
        {
            StrengthProvider = () => 100,
            PicklocksProvider = () => 0,
            MaxBashableStrengthProvider = () => 200,
        };
        BfsMapper bfs = new(graph);

        var path = bfs.FindPath(new RoomKey(1, 1331), new RoomKey(1, 1367), filter);
        Assert.NotNull(path);

        var expanded = RemoteActionPathExpander.Expand(
            graph, new RoomKey(1, 1331), path!, bfs, filter);
        int leverPulls = expanded.Count(s => s is CommandStep cs && cs.Command.Contains("lever"));
        Assert.Equal(1, leverPulls);   // redundant levers — one suffices

        // Approaching from the alignment-gated entrance (1/1322) routes too.
        Assert.NotNull(bfs.FindPath(new RoomKey(1, 1322), new RoomKey(1, 1367), filter));
    }

    [Fact]
    public void SameRoomLeverAnnotation_OnDoorExit_PromotesToMultiActionHidden()
    {
        // The reverse side: 1/1375's S Door back to the inner gate is opened by a
        // lever pulled in 1/1375 itself (the action cell sits in the W slot and
        // targets "the S exit of this room"). Same-room action → not remote.
        const string json = """
            [
              { "Map Number": 1, "Room Number": 1375, "Name": "Courtyard",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "1/1331 (Door [201 picklocks/strength])",
                "E": "0",
                "W": "Action [on the S exit of this room]: pull lever, push lever, move lever",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 1331, "Name": "Inner Gate",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        SeedRooms("alpha", json);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");

        Room? courtyard = graph.GetRoom(new RoomKey(1, 1375));
        Assert.NotNull(courtyard);
        Assert.True(courtyard!.Exits.TryGetValue(Direction.S, out RoomExit ex));
        Assert.Equal(RoomExitHint.MultiActionHidden, ex.Hint);
        Assert.NotNull(ex.MultiAction);
        Assert.Single(ex.MultiAction!.Actions);
        Assert.False(ex.MultiAction.HasRemoteActions);                  // pulled here
        Assert.Null(ex.MultiAction.Actions[0].RemoteSourceRoom);
    }

    [Fact]
    public void HiddenUnknown_WithSameRoomAction_IsActionGatedNotSearched()
    {
        // The Secret Library's D exit imports as "(Hidden/Unknown)" and is opened by
        // `move rug` from an Action cell in its E slot. It must not be walked as a
        // `sea d` search — the search never reveals it.
        const string json = """
            [
              { "Map Number": 12, "Room Number": 2248, "Name": "Golden Spire, Secret Library",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "0",
                "E": "Action [on the D exit of this room]: move rug, pull rug",
                "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0",
                "D": "12/2255 (Hidden/Unknown)" },
              { "Map Number": 12, "Room Number": 2255, "Name": "Under the Palace Library",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        SeedRooms("alpha", json);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");

        Assert.True(graph.GetRoom(new RoomKey(12, 2248))!.Exits.TryGetValue(Direction.D, out RoomExit down));
        Assert.Equal(RoomExitHint.MultiActionHidden, down.Hint);
        Assert.Equal(new[] { "move rug", "pull rug" }, down.MultiAction!.Actions.Single().Commands);
    }

    [Fact]
    public void MultiActionHidden_WithItemModifier_CapturesRequiredItemId()
    {
        // 2/687's N exit is a properly-modeled hidden exit; its S-slot action
        // gates the crossing on holding the amber talisman (item 815).
        const string json = """
            [
              { "Map Number": 2, "Room Number": 687, "Name": "Dragon's Teeth Hills",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "2/2578 (Hidden/Needs 1 Actions, any order)",
                "S": "Action [on the N exit of this room]: hold up talisman, hold up amber talisman, lift up talisman (Item: 815)",
                "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 2, "Room Number": 2578, "Name": "Secret Passage",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        SeedRooms("alpha", json);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");

        Room? room = graph.GetRoom(new RoomKey(2, 687));
        Assert.NotNull(room);
        Assert.True(room!.Exits.TryGetValue(Direction.N, out RoomExit ex));
        Assert.Equal(RoomExitHint.MultiActionHidden, ex.Hint);
        Assert.NotNull(ex.MultiAction);
        Assert.Single(ex.MultiAction!.Actions);
        Assert.Equal(815, ex.MultiAction.Actions[0].RequiredItemId);
        Assert.True(ex.MultiAction.RequiresUnheldItem(_ => false));      // lacking → gated
        Assert.False(ex.MultiAction.RequiresUnheldItem(id => id == 815)); // holding → clear
    }

    // ----- synthesised routable teleport edges -----------------------
    //
    // A CMD teleport that drops the player into a room with NO cardinal edge
    // from here (a `go hole` hop) becomes a routable Direction.Teleport exit so
    // BFS can plan through it. Distinct from PromoteCmdTeleportExits, which
    // re-hints an EXISTING Door/KeyLocked cardinal the teleport shadows.

    // ----- Newhaven Arena entry block ---------------------------------

    // 1/2146 is the Narrow Road above the Arena: W carries an ordinary exported
    // gate to the Healer, D drops into the Arena (1/2150) with nothing on it —
    // the real shape of the stock 1.11p data.
    private const string ArenaRoomsJson = """
        [
          { "Map Number": 1, "Room Number": 2146, "Name": "Newhaven, Narrow Road",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2190 (Level: 1 to 3)",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "1/2150" },
          { "Map Number": 1, "Room Number": 2150, "Name": "Newhaven, Arena",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "1/2146", "D": "0" },
          { "Map Number": 1, "Room Number": 2190, "Name": "Newhaven, Healer",
            "Light": 0, "Shop": 0, "NPC": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/2146", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private RoomGraphManager ArenaGraph(string roomsJson, int? legit)
    {
        SeedRooms("alpha", roomsJson);
        if (legit is { } code) SeedTable("alpha", "Info", $"[ {{ \"Legit\": {code} }} ]");
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return graph;
    }

    private static RoomExit ArenaDown(RoomGraphManager graph)
    {
        Room? road = graph.GetRoom(new RoomKey(1, 2146));
        Assert.NotNull(road);
        Assert.True(road!.Exits.TryGetValue(Direction.D, out RoomExit down));
        return down;
    }

    // Stock with a blank D (the real 1.11p data): the Arena's engine-side level
    // block is surfaced as the window the MDB would have exported, so the router
    // reads it exactly like the Healer gate on the same room's W exit.
    [Fact]
    public void ArenaEntryBlock_StockWithNoExportedGate_SurfacesLevel1To3()
    {
        RoomGraphManager graph = ArenaGraph(ArenaRoomsJson, legit: null);

        RoomExit down = ArenaDown(graph);
        Assert.True(down.HasLevelGate);
        Assert.Equal(1, down.MinLevel);
        Assert.Equal(3, down.MaxLevel);
        Assert.Equal(new RoomKey(1, 2150), down.Target);
    }

    // An edited stock realm that DOES export a window on the down exit keeps its
    // own values — the hardcoded block only fills a blank.
    [Fact]
    public void ArenaEntryBlock_StockWithExportedGate_KeepsTheImportedWindow()
    {
        RoomGraphManager graph = ArenaGraph(
            ArenaRoomsJson.Replace("\"D\": \"1/2150\"", "\"D\": \"1/2150 (Level: 2 to 7)\""),
            legit: null);

        RoomExit down = ArenaDown(graph);
        Assert.Equal(2, down.MinLevel);
        Assert.Equal(7, down.MaxLevel);
    }

    // Paradigm edits this exit itself, so the stock-only block must not fire there.
    [Fact]
    public void ArenaEntryBlock_ParaMud_IsNotApplied()
    {
        RoomGraphManager graph = ArenaGraph(ArenaRoomsJson, legit: 2);

        RoomExit down = ArenaDown(graph);
        Assert.False(down.HasLevelGate);
    }

    private void SeedTable(string setName, string table, string json)
    {
        string dir = Path.Combine(_root, setName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, table + ".json"), json);
    }

    private (RoomGraphManager Graph, BfsMapper Bfs) BuildWithTbInfo(
        string setName, string roomsJson, string tbInfoJson)
    {
        SeedTable(setName, "Rooms", roomsJson);
        SeedTable(setName, "TBInfo", tbInfoJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet(setName);
        TBInfoStore tbinfo = new(cache);
        tbinfo.OnActiveSetChanged(setName);
        RoomGraphManager graph = new(cache, log: null, tbinfo);
        graph.OnActiveSetChanged(setName);
        return (graph, new BfsMapper(graph));
    }

    private (RoomGraphManager Graph, BfsMapper Bfs) BuildWithTbInfoAndMonsters(
        string setName, string roomsJson, string tbInfoJson, string monstersJson)
    {
        SeedTable(setName, "Rooms", roomsJson);
        SeedTable(setName, "TBInfo", tbInfoJson);
        SeedTable(setName, "Monsters", monstersJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet(setName);
        TBInfoStore tbinfo = new(cache);
        tbinfo.OnActiveSetChanged(setName);
        RoomGraphManager graph = new(cache, log: null, tbinfo);
        graph.OnActiveSetChanged(setName);
        return (graph, new BfsMapper(graph));
    }

    private (RoomGraphManager Graph, BfsMapper Bfs) BuildWithTbInfoAndSpells(
        string setName, string roomsJson, string tbInfoJson, string spellsJson)
    {
        SeedTable(setName, "Rooms", roomsJson);
        SeedTable(setName, "TBInfo", tbInfoJson);
        SeedTable(setName, "Spells", spellsJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet(setName);
        TBInfoStore tbinfo = new(cache);
        tbinfo.OnActiveSetChanged(setName);
        KnownSpellCatalog spells = new(cache);
        RoomGraphManager graph = new(cache, log: null, tbinfo, spells);
        graph.OnActiveSetChanged(setName);
        return (graph, new BfsMapper(graph));
    }

    // ----- guard-door promotion (lair monster greet opens a home-room door) --
    //
    // The grove shadow guard (#503) stands in the lair of 9/1423; its greet
    // (1433) lifts that room's W door to Morukai's chamber (9/1425) when a
    // Phoenix-quest character asks about "morukai". The door imports as a
    // 1000-picklock Door that no pick/bash opens, so without the greet-derived
    // ask command the route picker discards it.

    private const string GuardRooms = """
        [
          { "Map Number": 9, "Room Number": 1423, "Name": "Grove",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0,
            "Lair": "(Max 2): 503,[30-24-24-2]", "Delay": 5,
            "N": "0", "S": "9/1422 (Door)", "E": "9/1424",
            "W": "9/1425 (Door [1000 picklocks/strength])",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 9, "Room Number": 1425, "Name": "Morukai's Chamber",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "NPC": 504, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "9/1423 (Door)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private const string GuardTbInfo = """
        [
          { "Number": 1433, "LinkTo": 0,
            "Action": "morukai:1435\norfeo:1435\npassage:1435\nphoenix:1435\nprophecy:1435\n",
            "Called From": "Monster #503" },
          { "Number": 1435, "LinkTo": 1436, "Action": null, "Called From": "" },
          { "Number": 1436, "LinkTo": 0,
            "Action": "checkability 133 4:remoteaction 1423 66 0 3:message 1841\n",
            "Called From": "" }
        ]
        """;

    private const string GuardMonsters = """
        [
          { "Number": 503, "Name": "shadow guard", "GreetTXT": 1433 },
          { "Number": 504, "Name": "Morukai", "GreetTXT": 0 }
        ]
        """;

    [Fact]
    public void GuardMonsterGreet_OnDoorExit_PromotesToMultiActionHidden()
    {
        (RoomGraphManager graph, _) =
            BuildWithTbInfoAndMonsters("alpha", GuardRooms, GuardTbInfo, GuardMonsters);

        Room? grove = graph.GetRoom(new RoomKey(9, 1423));
        Assert.NotNull(grove);
        Assert.True(grove!.Exits.TryGetValue(Direction.W, out RoomExit ex));

        // Promoted off the unpickable Door so the ask-then-move dispatch crosses
        // it instead of the door FSM bonking on a 1000-picklock door.
        Assert.Equal(RoomExitHint.MultiActionHidden, ex.Hint);
        Assert.Equal(new RoomKey(9, 1425), ex.Target);
        Assert.NotNull(ex.MultiAction);
        Assert.Single(ex.MultiAction!.Actions);
        Assert.False(ex.MultiAction.HasRemoteActions);              // spoken here, no detour
        Assert.Equal("ask shadow guard morukai", ex.MultiAction.Actions[0].Commands[0]);
        Assert.Equal(0, ex.MultiAction.Actions[0].RequiredItemId);  // no held-item gate
    }

    [Fact]
    public void GuardDoor_PromotedExit_IsRoutableByBfs()
    {
        // End-to-end: with the guarded door promoted, BFS routes 9/1423 → 9/1425
        // even for a build that can never pick/bash a 1000-picklock door.
        (RoomGraphManager graph, BfsMapper bfs) =
            BuildWithTbInfoAndMonsters("alpha", GuardRooms, GuardTbInfo, GuardMonsters);

        ProfileService profile = new();
        MovementFilter filter = new(profile)
        {
            StrengthProvider = () => 100,
            PicklocksProvider = () => 0,
            MaxBashableStrengthProvider = () => 200,
        };

        var path = bfs.FindPath(new RoomKey(9, 1423), new RoomKey(9, 1425), filter);
        Assert.NotNull(path);
        Assert.Equal(new[] { Direction.W }, path!);
    }

    [Fact]
    public void GuardDoor_WithoutMonstersTable_LeavesDoorUnpromoted()
    {
        // No Monsters table → the greet is never scanned, so the door stays a
        // plain (unpickable) Door — proves the promotion is the Monsters-driven
        // path, not something the room/TBInfo data alone triggers.
        (RoomGraphManager graph, _) = BuildWithTbInfo("alpha", GuardRooms, GuardTbInfo);

        Room? grove = graph.GetRoom(new RoomKey(9, 1423));
        Assert.True(grove!.Exits.TryGetValue(Direction.W, out RoomExit ex));
        Assert.Equal(RoomExitHint.Door, ex.Hint);
        Assert.Null(ex.MultiAction);
    }

    // ----- NPC ask-transport teleport edges (greet ports player out) ---------
    //
    // The Floating Citadel's Grey Lord (#251) stands in the Great Hall (1/160), a
    // sealed pocket whose only cardinal is S. His greet (366) exposes an UNGATED
    // `teleport` topic that ports to Town Square (1/224) and a quest-GATED
    // `code`/`word` topic to the same room. Only the ungated topic is synthesised
    // into a routable Direction.Teleport edge.

    private const string GreyLordRooms = """
        [
          { "Map Number": 1, "Room Number": 160, "Name": "Floating Citadel, Great Hall",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "NPC": 251, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/158", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 158, "Name": "Floating Citadel, Landing",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 5,
            "N": "1/160", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 224, "Name": "Town Square",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private const string GreyLordTbInfo = """
        [
          { "Number": 366, "LinkTo": 0, "Action": "teleport:369\ncode:368\nword:368\n",
            "Called From": "Monster #251" },
          { "Number": 369, "LinkTo": 370, "Action": null, "Called From": "" },
          { "Number": 370, "LinkTo": 0, "Action": "teleport 224 1:message 837\n", "Called From": "" },
          { "Number": 368, "LinkTo": 371, "Action": null, "Called From": "" },
          { "Number": 371, "LinkTo": 0,
            "Action": "checkability 127 2:giveability 127 3:teleport 224 1:message 837\n",
            "Called From": "" }
        ]
        """;

    private const string GreyLordMonsters = """
        [ { "Number": 251, "Name": "The Grey Lord", "GreetTXT": 366 } ]
        """;

    [Fact]
    public void GreetTeleport_UngatedNpcTransport_SynthesisesTeleportEdge()
    {
        (RoomGraphManager graph, _) =
            BuildWithTbInfoAndMonsters("alpha", GreyLordRooms, GreyLordTbInfo, GreyLordMonsters);

        Room? hall = graph.GetRoom(new RoomKey(1, 160));
        Assert.NotNull(hall);
        Assert.True(hall!.Exits.TryGetValue(Direction.Teleport, out RoomExit tele));
        Assert.Equal(RoomExitHint.Teleport, tele.Hint);
        Assert.Equal(new RoomKey(1, 224), tele.Target);          // ungated topic → Town Square
        Assert.NotNull(tele.TextCommands);
        Assert.Equal("ask Grey Lord teleport", tele.TextCommands![0]); // noun = last word of the name
        Assert.Equal(0, tele.MinLevel);
        // The synthetic edge never touches the cardinal fingerprint (only S).
        Assert.Equal(1u << (int)Direction.S, hall.ExitMask);
    }

    [Fact]
    public void GreetTeleport_PocketEgress_IsRoutableByBfs()
    {
        // End-to-end: the sealed pocket 1/160 now routes to 1/224 through the
        // ask-transport edge, where it couldn't before (no cardinal egress).
        (RoomGraphManager graph, BfsMapper bfs) =
            BuildWithTbInfoAndMonsters("alpha", GreyLordRooms, GreyLordTbInfo, GreyLordMonsters);

        ProfileService profile = new();
        MovementFilter filter = new(profile) { StrengthProvider = () => 100 };

        var path = bfs.FindPath(new RoomKey(1, 160), new RoomKey(1, 224), filter);
        Assert.NotNull(path);
        Assert.Equal(new[] { Direction.Teleport }, path!);
    }

    [Fact]
    public void GreetTeleport_GatedOnlyGreet_MintsNoEdge()
    {
        // A greet whose only teleport topic is quest-gated yields no edge — the
        // client can't verify the gate, so the pocket stays unrouted rather than
        // routing through a transport that might silently fail.
        const string gatedTbInfo = """
            [
              { "Number": 366, "LinkTo": 0, "Action": "code:368\nword:368\n",
                "Called From": "Monster #251" },
              { "Number": 368, "LinkTo": 371, "Action": null, "Called From": "" },
              { "Number": 371, "LinkTo": 0,
                "Action": "checkability 127 2:teleport 224 1:message 837\n", "Called From": "" }
            ]
            """;
        (RoomGraphManager graph, _) =
            BuildWithTbInfoAndMonsters("alpha", GreyLordRooms, gatedTbInfo, GreyLordMonsters);

        Room? hall = graph.GetRoom(new RoomKey(1, 160));
        Assert.False(hall!.Exits.ContainsKey(Direction.Teleport));
    }

    [Fact]
    public void GreetTeleport_WithoutMonstersTable_MintsNoEdge()
    {
        // No Monsters table → the greet is never scanned, so no edge is minted —
        // proves the edge is the Monsters-driven greet path, not room/TBInfo alone.
        (RoomGraphManager graph, _) = BuildWithTbInfo("alpha", GreyLordRooms, GreyLordTbInfo);

        Room? hall = graph.GetRoom(new RoomKey(1, 160));
        Assert.False(hall!.Exits.ContainsKey(Direction.Teleport));
    }

    [Fact]
    public void CmdTeleport_SingleDestination_SynthesisesTeleportEdge()
    {
        // Room 1/10 has CMD=100 and only a S cardinal to 1/11; its `go hole`
        // teleport lands in 2/487, which no cardinal reaches — so a
        // Direction.Teleport edge is minted carrying the crossing keyword.
        const string rooms = """
            [
              { "Map Number": 1, "Room Number": 10, "Name": "Cavern Mouth",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 100, "Lair": "", "Delay": 5,
                "N": "0", "S": "1/11", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 11, "Name": "Dead End",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 5,
                "N": "1/10", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 2, "Room Number": 487, "Name": "Far Shore",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        const string tbinfo = """
            [ { "Number": 100, "LinkTo": 0,
                "Action": "go hole:message 767:teleport 487 2:message 768\n",
                "Called From": "Room 1/10" } ]
            """;
        (RoomGraphManager graph, _) = BuildWithTbInfo("alpha", rooms, tbinfo);

        Room? room = graph.GetRoom(new RoomKey(1, 10));
        Assert.NotNull(room);
        Assert.True(room!.Exits.TryGetValue(Direction.Teleport, out RoomExit tele));
        Assert.Equal(RoomExitHint.Teleport, tele.Hint);
        Assert.Equal(new RoomKey(2, 487), tele.Target);
        Assert.NotNull(tele.TextCommands);
        Assert.Equal("go hole", tele.TextCommands![0]);
        Assert.Equal(0, tele.MinLevel);
        // The synthetic edge never touches the cardinal fingerprint.
        Assert.Equal(1u << (int)Direction.S, room.ExitMask);
    }

    [Fact]
    public void CmdTeleport_WithMinLevel_CapturesGateOnEdge()
    {
        // `go vortex` gates on minlevel 20 before teleporting to 3/669 — the
        // synthetic edge carries that so MovementFilter skips it under-level.
        const string rooms = """
            [
              { "Map Number": 1, "Room Number": 613, "Name": "Whirlpool",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 100, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        const string tbinfo = """
            [ { "Number": 100, "LinkTo": 0,
                "Action": "go vortex:adddelay 5:minlevel 20 1220:message 1205:teleport 669 3:message 1221\n",
                "Called From": "Room 1/613" } ]
            """;
        (RoomGraphManager graph, _) = BuildWithTbInfo("alpha", rooms, tbinfo);

        Room? room = graph.GetRoom(new RoomKey(1, 613));
        Assert.True(room!.Exits.TryGetValue(Direction.Teleport, out RoomExit tele));
        Assert.Equal(new RoomKey(3, 669), tele.Target);
        Assert.Equal("go vortex", tele.TextCommands![0]);
        Assert.Equal(20, tele.MinLevel);
    }

    [Fact]
    public void CmdTeleport_DestinationAlreadyCardinalExit_NoSyntheticEdge()
    {
        // The teleport lands in 1/11, which a plain S cardinal already reaches.
        // No synthetic edge — the walker gets there the normal way.
        const string rooms = """
            [
              { "Map Number": 1, "Room Number": 10, "Name": "Cavern Mouth",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 100, "Lair": "", "Delay": 5,
                "N": "0", "S": "1/11", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 11, "Name": "Dead End",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 5,
                "N": "1/10", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        const string tbinfo = """
            [ { "Number": 100, "LinkTo": 0,
                "Action": "go hole:teleport 11 1\n",
                "Called From": "Room 1/10" } ]
            """;
        (RoomGraphManager graph, _) = BuildWithTbInfo("alpha", rooms, tbinfo);

        Room? room = graph.GetRoom(new RoomKey(1, 10));
        Assert.False(room!.Exits.ContainsKey(Direction.Teleport));
    }

    [Fact]
    public void CmdTeleport_SynthesisedEdge_IsRoutableByBfs()
    {
        // End-to-end: BFS must route 1/10 → 2/487 through the synthesised
        // teleport hop, and the single planned step is Direction.Teleport.
        const string rooms = """
            [
              { "Map Number": 1, "Room Number": 10, "Name": "Cavern Mouth",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 100, "Lair": "", "Delay": 5,
                "N": "0", "S": "1/11", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 11, "Name": "Dead End",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 5,
                "N": "1/10", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 2, "Room Number": 487, "Name": "Far Shore",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        const string tbinfo = """
            [ { "Number": 100, "LinkTo": 0,
                "Action": "go hole:teleport 487 2\n",
                "Called From": "Room 1/10" } ]
            """;
        (RoomGraphManager graph, BfsMapper bfs) = BuildWithTbInfo("alpha", rooms, tbinfo);

        var path = bfs.FindPath(new RoomKey(1, 10), new RoomKey(2, 487));
        Assert.NotNull(path);
        Assert.Equal(new[] { Direction.Teleport }, path!);
    }

    [Fact]
    public void NoTbInfo_LeavesTeleportEdgesUnsynthesised()
    {
        // Parameterless / no-TBInfo construction can't resolve keywords, so a
        // CMD room stays a plain graph node with no Direction.Teleport edge.
        const string rooms = """
            [
              { "Map Number": 1, "Room Number": 10, "Name": "Cavern Mouth",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 100, "Lair": "", "Delay": 5,
                "N": "0", "S": "1/11", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 11, "Name": "Dead End",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 5,
                "N": "1/10", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        SeedRooms("alpha", rooms);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);          // no TBInfo
        graph.OnActiveSetChanged("alpha");

        Room? room = graph.GetRoom(new RoomKey(1, 10));
        Assert.False(room!.Exits.ContainsKey(Direction.Teleport));
    }

    // Paradigm map 9's `go portal` (CMD 1462) is quest-gated: it casts "lower
    // portal (invited)" (spell 620, fixed → 9/1424) for flagged characters but
    // "lower portal (uninvited)" (spell 621, random → the Caves of Chaos) for
    // everyone else, all under the one keyword. The client can't read the flag,
    // so the fixed branch is NOT minted as a plain routable shortcut — it becomes
    // a GATEWAY edge instead: BFS's deterministic pass ignores it (the
    // narrow-stair climb wins from inside the cluster, so unflagged characters
    // don't get random-dumped mid-route), and only the fallback pass crosses it
    // from the overworld, where the portal is the sole way up.
    private const string PortalSpells = """
        [
          { "Number": 620, "Name": "lower portal (invited)", "Short": "lpi",
            "MinBase": 0, "MaxBase": 0,
            "Abil-0": 140, "AbilVal-0": 1424, "Abil-1": 141, "AbilVal-1": 9 },
          { "Number": 621, "Name": "lower portal (uninvited)", "Short": "lpu",
            "MinBase": 1292, "MaxBase": 1327,
            "Abil-0": 140, "AbilVal-0": 0, "Abil-1": 141, "AbilVal-1": 9 }
        ]
        """;

    [Fact]
    public void CmdCastTeleport_FixedAndRandomBranchesShareKeyword_SynthesisesGateway()
    {
        const string rooms = """
            [
              { "Map Number": 9, "Room Number": 1291, "Name": "Ancient Darkwood Tree, Portal",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 1462, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "9/1413", "D": "0" },
              { "Map Number": 9, "Room Number": 1413, "Name": "Narrow Stair",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "9/1291" },
              { "Map Number": 9, "Room Number": 1424, "Name": "Mystical Chamber",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        const string tbinfo = """
            [ { "Number": 1462, "LinkTo": 0,
                "Action": "go portal:checkability 133 5:cast 620\ngo portal:testability 133 4:cast 621\ngo portal:failability 133:cast 621\n",
                "Called From": "Room 9/1291" } ]
            """;
        (RoomGraphManager graph, _) = BuildWithTbInfoAndSpells("alpha", rooms, tbinfo, PortalSpells);

        Room? room = graph.GetRoom(new RoomKey(9, 1291));
        Assert.NotNull(room);
        // The fixed branch's random sibling makes the keyword non-deterministic —
        // it's minted as a GATEWAY (routable last resort), not a plain shortcut.
        Assert.True(room!.Exits.TryGetValue(Direction.Teleport, out RoomExit tele));
        Assert.True(tele.GatewayTeleport);
        Assert.Equal(new RoomKey(9, 1424), tele.Target);   // nominal landing = fixed branch
        Assert.Equal("go portal", tele.TextCommands![0]);
    }

    [Fact]
    public void CmdCastTeleport_SoleFixedBranch_SynthesisesTeleportEdge()
    {
        // Positive control: a keyword whose only branch is a deterministic fixed
        // cast still mints a plain (non-gateway) routable Teleport edge — the
        // grouping rule only flags keywords with a random / disagreeing sibling.
        const string rooms = """
            [
              { "Map Number": 9, "Room Number": 1500, "Name": "Sealed Gate",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 1500, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 9, "Room Number": 1424, "Name": "Mystical Chamber",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        const string tbinfo = """
            [ { "Number": 1500, "LinkTo": 0,
                "Action": "go gate:cast 620\n", "Called From": "Room 9/1500" } ]
            """;
        (RoomGraphManager graph, _) = BuildWithTbInfoAndSpells("alpha", rooms, tbinfo, PortalSpells);

        Room? room = graph.GetRoom(new RoomKey(9, 1500));
        Assert.True(room!.Exits.TryGetValue(Direction.Teleport, out RoomExit tele));
        Assert.Equal(new RoomKey(9, 1424), tele.Target);
        Assert.Equal("go gate", tele.TextCommands![0]);
        Assert.False(tele.GatewayTeleport);
    }

    [Fact]
    public void Gateway_NotCrossedWhenDeterministicPathExists()
    {
        // The loop fix: from inside the cluster a deterministic cardinal route to
        // the chamber exists (9/1291 U→9/1413 N→9/1424), so BFS takes the stair
        // climb and never routes through the gateway — even though the gateway is
        // a shorter single hop. This is what stops the walker looping down through
        // the random portal for an unflagged character.
        const string rooms = """
            [
              { "Map Number": 9, "Room Number": 1291, "Name": "Ancient Darkwood Tree, Portal",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 1462, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "9/1413", "D": "0" },
              { "Map Number": 9, "Room Number": 1413, "Name": "Narrow Stair",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 5,
                "N": "9/1424", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "9/1291" },
              { "Map Number": 9, "Room Number": 1424, "Name": "Mystical Chamber",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        const string tbinfo = """
            [ { "Number": 1462, "LinkTo": 0,
                "Action": "go portal:checkability 133 5:cast 620\ngo portal:testability 133 4:cast 621\ngo portal:failability 133:cast 621\n",
                "Called From": "Room 9/1291" } ]
            """;
        (RoomGraphManager graph, BfsMapper bfs) = BuildWithTbInfoAndSpells("alpha", rooms, tbinfo, PortalSpells);

        // Gateway edge is present...
        Room? room = graph.GetRoom(new RoomKey(9, 1291));
        Assert.True(room!.Exits.TryGetValue(Direction.Teleport, out RoomExit tele));
        Assert.True(tele.GatewayTeleport);

        // ...but BFS prefers the deterministic cardinal climb, no Teleport step.
        var path = bfs.FindPath(new RoomKey(9, 1291), new RoomKey(9, 1424));
        Assert.Equal(new[] { Direction.U, Direction.N }, path!);
    }

    [Fact]
    public void Gateway_CrossedWhenItIsTheOnlyRoute()
    {
        // The overworld crossing: no cardinal route to the chamber exists, so BFS
        // falls back to crossing the gateway (a single Direction.Teleport step).
        const string rooms = """
            [
              { "Map Number": 9, "Room Number": 1291, "Name": "Ancient Darkwood Tree, Portal",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 1462, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "9/1413", "D": "0" },
              { "Map Number": 9, "Room Number": 1413, "Name": "Narrow Stair",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "9/1291" },
              { "Map Number": 9, "Room Number": 1424, "Name": "Mystical Chamber",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        const string tbinfo = """
            [ { "Number": 1462, "LinkTo": 0,
                "Action": "go portal:checkability 133 5:cast 620\ngo portal:testability 133 4:cast 621\ngo portal:failability 133:cast 621\n",
                "Called From": "Room 9/1291" } ]
            """;
        (RoomGraphManager graph, BfsMapper bfs) = BuildWithTbInfoAndSpells("alpha", rooms, tbinfo, PortalSpells);

        var path = bfs.FindPath(new RoomKey(9, 1291), new RoomKey(9, 1424));
        Assert.Equal(new[] { Direction.Teleport }, path!);

        // The "can I get there at all?" reach crosses it too, as FindPath's fallback
        // does; the walk-only distances keep it out.
        Assert.Equal(1, bfs.ComputeDistancesFrom(new RoomKey(9, 1291), viaBoats: true)[new RoomKey(9, 1424)]);
        Assert.False(bfs.ComputeDistancesFrom(new RoomKey(9, 1291)).ContainsKey(new RoomKey(9, 1424)));
    }

    // ----- the build's shared work between passes ---------------------------

    // One map-1 Rooms row; exits as "N=1/2" pairs, every other direction "0".
    private static string RoomRow(int number, int cmd, params string[] exits)
    {
        var cells = new[] { "N", "S", "E", "W", "NE", "NW", "SE", "SW", "U", "D" }
            .Select(dir => exits.FirstOrDefault(e => e.StartsWith(dir + "=", StringComparison.Ordinal)) is { } exit
                ? $"\"{dir}\": \"{exit[(dir.Length + 1)..]}\""
                : $"\"{dir}\": \"0\"");
        return $"{{ \"Map Number\": 1, \"Room Number\": {number}, \"Name\": \"Room {number}\", "
            + $"\"Light\": 0, \"Shop\": 0, \"NPC\": 0, \"CMD\": {cmd}, \"Lair\": \"\", \"Delay\": 0, "
            + string.Join(", ", cells) + " }";
    }

    private const string Cast = " (Cast: pre-0, post-5)";

    // The pocket-entrance searches lean on what earlier ones proved: a search that
    // ran out leaves behind a region nothing leads out of, and a later search stops
    // at its edge. Rooms 1-5 are a loop every one of them can walk round; 10, 11
    // and 12 drop into it one way; 20-21 is a sink off it with two mouths. The row
    // order puts the searches that run out first, so the two that must still be
    // walked (3 → 4, which comes back round the loop, and the sink's second mouth)
    // meet regions already proven closed.
    [Fact]
    public void CastPocketEntrances_MatchPlainReachability_WhenSearchesShareWhatTheyProved()
    {
        string rooms = "[" + string.Join(",\n",
            RoomRow(10, 0, "E=1/1" + Cast),
            RoomRow(11, 0, "E=1/10" + Cast),
            RoomRow(12, 0, "E=1/2" + Cast),
            RoomRow(1, 0, "N=1/2"),
            RoomRow(2, 0, "S=1/1", "N=1/3", "W=1/20" + Cast),
            RoomRow(3, 0, "S=1/2", "E=1/4" + Cast),
            RoomRow(4, 0, "N=1/5"),
            RoomRow(5, 0, "W=1/1", "E=1/21" + Cast),
            RoomRow(20, 0, "E=1/21"),
            RoomRow(21, 0, "W=1/20")) + "]";
        SeedRooms("alpha", rooms);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");

        // Every cast exit, checked against a walk of the finished graph.
        bool Reaches(RoomKey from, RoomKey goal)
        {
            HashSet<RoomKey> seen = new() { from };
            Stack<RoomKey> open = new();
            open.Push(from);
            while (open.Count > 0)
            {
                if (graph.GetRoom(open.Pop()) is not { } room) continue;
                foreach (RoomExit exit in room.Exits.Values)
                {
                    if (exit.Target == goal) return true;
                    if (seen.Add(exit.Target)) open.Push(exit.Target);
                }
            }
            return false;
        }
        List<(RoomKey Source, Direction Direction)> oneWay = new();
        foreach (Room room in graph.Rooms)
            foreach ((Direction dir, RoomExit exit) in room.Exits)
            {
                if (!exit.CastsOnWalk) continue;
                Assert.Equal(!Reaches(exit.Target, room.Key), exit.CastPocketEntrance);
                if (exit.CastPocketEntrance) oneWay.Add((room.Key, dir));
            }

        Assert.Equal(
            new[]
            {
                (new RoomKey(1, 10), Direction.E),
                (new RoomKey(1, 11), Direction.E),
                (new RoomKey(1, 12), Direction.E),
                (new RoomKey(1, 2), Direction.W),
                (new RoomKey(1, 5), Direction.E),
            },
            oneWay);
        Assert.False(graph.GetRoom(new RoomKey(1, 3))!.Exits[Direction.E].CastPocketEntrance);

        // The list the maze index starts from is the same exits in the same order.
        Assert.Equal(oneWay, graph.CastPocketEntrances.ToList());
    }

    // Two rows for one room: the later row is the room, in the earlier row's
    // place. The passes work from lists of rooms found once up front, so those
    // lists have to describe the rooms that ended up in the graph.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LaterRowForTheSameRoom_IsTheOneThePassesSee(bool laterRowHasTheCommand)
    {
        const string tbinfo = """
            [ { "Number": 100, "LinkTo": 0, "Action": "go hole:teleport 9 1\n", "Called From": "" } ]
            """;
        string rooms = "[" + string.Join(",\n",
            RoomRow(1, laterRowHasTheCommand ? 0 : 100, "N=1/2", "E=1/3" + Cast),
            RoomRow(2, 0, "S=1/1"),
            RoomRow(1, laterRowHasTheCommand ? 100 : 0, "N=1/2"),
            RoomRow(9, 0, "N=1/2")) + "]";
        (RoomGraphManager graph, _) = BuildWithTbInfo("alpha", rooms, tbinfo);

        Assert.Equal(new[] { new RoomKey(1, 1), new RoomKey(1, 2), new RoomKey(1, 9) },
            graph.Rooms.Select(r => r.Key));
        Room room = graph.GetRoom(new RoomKey(1, 1))!;
        Assert.Equal(laterRowHasTheCommand ? 100 : 0, room.Cmd);
        Assert.False(room.Exits.ContainsKey(Direction.E));                // the earlier row's cast exit is gone
        Assert.Empty(graph.CastPocketEntrances);
        Assert.Equal(laterRowHasTheCommand, room.Exits.ContainsKey(Direction.Teleport));
        if (laterRowHasTheCommand)
            Assert.Equal(new RoomKey(1, 9), room.Exits[Direction.Teleport].Target);
    }

    // A realm repeats a room name fifteen times over, and its lair tags and exit
    // hints as often: the graph keeps one string for each, and a room's exits in
    // the compact set, through the passes that replace an exit as well.
    [Fact]
    public void Rooms_KeepTheirExitsCompactly_AndShareTextThatReadsTheSame()
    {
        const string rooms = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "Dark Cave", "Lair": "(Max 2): 503,[30-24-24-2]",
                "N": "1/2 (Door)", "S": "0", "E": "1/3 (Cast: pre-0, post-5)", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "Dark Cave", "Lair": "(Max 2): 503,[30-24-24-2]",
                "N": "0", "S": "1/1 (Door)", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "Dead End", "Lair": "",
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        SeedRooms("alpha", rooms);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);

        graph.OnActiveSetChanged("alpha");

        Room first = graph.GetRoom(new RoomKey(1, 1))!, second = graph.GetRoom(new RoomKey(1, 2))!;
        Assert.Same(first.Name, second.Name);
        Assert.Same(first.RawLairTag, second.RawLairTag);
        Assert.Same(first.Exits[Direction.N].RawHint, second.Exits[Direction.S].RawHint);
        // 1 E is a one-way cast exit, so the pocket pass has swapped room 1's exits.
        Assert.True(first.Exits[Direction.E].CastPocketEntrance);
        Assert.All(graph.Rooms, room => Assert.IsType<RoomExitSet>(room.Exits));
        Assert.Same(RoomExitSet.Empty, graph.GetRoom(new RoomKey(1, 3))!.Exits);
    }

    // A damaged set: room commands that lead into empty text blocks linked round
    // in a circle (100 to itself, 300 and 301 to each other). The boat pass reads
    // every room command, and one that follows those links blindly never comes
    // back. The build has to finish, and say which blocks are broken once, however
    // many rooms share them.
    [Fact]
    public void EmptyTextBlocksLinkedInACircle_BuildFinishes_AndAreNamedOnce()
    {
        const string tbinfo = """
            [
              { "Number": 100, "LinkTo": 100, "Action": "", "Called From": "" },
              { "Number": 300, "LinkTo": 301, "Action": "", "Called From": "" },
              { "Number": 301, "LinkTo": 300, "Action": "", "Called From": "" },
              { "Number": 400, "LinkTo": 0, "Action": "go hole:teleport 9 1\n", "Called From": "" }
            ]
            """;
        string rooms = "[" + string.Join(",\n",
            RoomRow(1, 100, "N=1/2"),
            RoomRow(2, 100, "S=1/1", "N=1/3"),
            RoomRow(3, 100, "S=1/2", "N=1/4"),
            RoomRow(4, 300, "S=1/3", "N=1/5"),
            RoomRow(5, 301, "S=1/4"),
            RoomRow(6, 400, "N=1/1"),
            RoomRow(9, 0, "N=1/1")) + "]";
        SeedTable("alpha", "Rooms", rooms);
        SeedTable("alpha", "TBInfo", tbinfo);
        SeedTable("alpha", "Spells", "[]");
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        TBInfoStore store = new(cache);
        store.OnActiveSetChanged("alpha");
        LogService log = new();
        RoomGraphManager graph = new(cache, log, store, new KnownSpellCatalog(cache));

        BoundedRun.Finish(() => { graph.OnActiveSetChanged("alpha"); return true; });

        Assert.Equal(7, graph.RoomCount);
        // The whole room command next to them is still read.
        Assert.Equal(new RoomKey(1, 9), graph.GetRoom(new RoomKey(1, 6))!.Exits[Direction.Teleport].Target);
        Assert.Empty(graph.AllBoatPassages);

        LogEntry warning = Assert.Single(log.SnapshotProblems());
        Assert.Equal(LogSeverity.Warn, warning.Severity);
        Assert.Contains("100, 300, 301", warning.Message);
    }
}
