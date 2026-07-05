using System.IO;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Covers the PR 7.4 contract: seeding from <c>Rooms.json</c>, exit
/// parsing (Door / Trap / unknown hint), uniqueness index, lair-flag
/// recognition, and the per-set-swap reload.
/// </summary>
public sealed class RoomGraphManagerTests : IDisposable
{
    private readonly string _root;

    public RoomGraphManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-roomgraph-tests-" + Path.GetRandomFileName());
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

    [Theory]
    [InlineData("1/2 (Level: 30 to 999)",       "Level: 30 to 999")]
    [InlineData("1/2 (Alignment: Saint to Outlaw)", "Alignment: Saint to Outlaw")]
    public void RoomExit_GatedExits_FallThroughToNoneButPreserveRaw(string wire, string rawHint)
    {
        // Level / Alignment / Class / Race / Ability restrictions
        // aren't classified yet — they round-trip via RawHint until
        // the path-time gate land in a later PR.
        Assert.True(RoomExit.TryParseWire(wire, out RoomExit exit));
        Assert.Equal(RoomExitHint.None, exit.Hint);
        Assert.Equal(rawHint, exit.RawHint);
    }

    [Fact]
    public void RoomExit_KeyHint_NowClassified()
    {
        // Previously fell through to None; commit-1 schema extension
        // promotes (Key: N) to KeyLocked with item id captured.
        Assert.True(RoomExit.TryParseWire("1/2 (Key: 5)", out RoomExit exit));
        Assert.Equal(RoomExitHint.KeyLocked, exit.Hint);
        Assert.Equal(5, exit.KeyItemId);
    }

    [Fact]
    public void RoomExit_HiddenHint_NowClassifiedAsSearchable()
    {
        // Previously fell through to None; commit-1 schema extension
        // promotes plain (Hidden) to SearchableHidden so the walker
        // knows to `sea <dir>` before stepping.
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

    [Fact]
    public void RoomExit_ZeroSentinel_NotAnExit()
    {
        Assert.False(RoomExit.TryParseWire("0", out _));
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
        // of truth; the raw JsonDocument is dropped from the cache.
        Assert.DoesNotContain("Rooms", cache.LoadedTables);
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

    [Fact]
    public void IsUnique_TrueForOneOfOneTuple()
    {
        SeedRooms("alpha", TwinRoomsJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");

        // All three have distinct (name, exit-mask) tuples even though
        // the names are all identical.
        Assert.True(graph.IsUnique(new RoomKey(1, 10)));
        Assert.True(graph.IsUnique(new RoomKey(1, 11)));
        Assert.True(graph.IsUnique(new RoomKey(1, 12)));
    }

    [Fact]
    public void IsUnique_FalseForAmbiguousTuple()
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
        Assert.False(graph.IsUnique(new RoomKey(2, 1)));
        Assert.False(graph.IsUnique(new RoomKey(2, 3)));

        // 2/2 has Hallway + {S}; nothing else collides. Unique.
        Assert.True(graph.IsUnique(new RoomKey(2, 2)));
    }

    [Fact]
    public void IsUnique_FalseForUnknownKey()
    {
        SeedRooms("alpha", TwoRoomJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");

        Assert.False(graph.IsUnique(new RoomKey(999, 999)));
    }

    // ----- unreachable filter (CannotBeReached) ----------------------

    // Same fixture as IsUnique_FalseForAmbiguousTuple: 2/1 and 2/3 are both
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

    [Fact]
    public void Item_OnExit_WithCmdOnSourceRoom_PromotesToTeleport()
    {
        // Source room CMD=5 + (Item: 474) on the exit → Teleport.
        const string json = """
            [
              { "Map Number": 7, "Room Number": 130, "Name": "Grove",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 5, "Lair": "",
                "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "7/131 (Item: 474)", "SW": "0",
                "U": "0", "D": "0" }
            ]
            """;
        SeedRooms("alpha", json);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");

        Room? room = graph.GetRoom(new RoomKey(7, 130));
        Assert.NotNull(room);
        Assert.True(room!.Exits.TryGetValue(Direction.SE, out RoomExit ex));
        Assert.Equal(RoomExitHint.Teleport, ex.Hint);
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
}
