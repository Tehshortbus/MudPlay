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
        Assert.Equal(2, ex.MultiAction!.RequiredActionCount);           // both levers
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
    public void LeverGate_FullRoute_FindsPath_AndEmitsTwoLeverPulls()
    {
        // End-to-end regression for the inner-gate lever route (bug reports
        // paradigm-20260714-091000 / -091244): with the gate room's N Door
        // promoted to a lever-operated MultiActionHidden exit, BFS must find a
        // path from the gate (1/1331) out to 1/1367, and the path expander must
        // splice in the two guardroom lever pulls as a go-act-return detour.
        // A raw 301-picklock Door with Strength 100 / Picklocks 0 would block —
        // a found route + two lever CommandSteps proves the promotion + detour.
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
        Assert.Equal(2, leverPulls);

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
        Assert.Equal("ask guard morukai", ex.MultiAction.Actions[0].Commands[0]);
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
}
