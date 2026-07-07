using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 7.6 coverage — per-character avoided + stash room state,
/// IRoomFilter contract, profile-event lifecycle, and the round-trip
/// through CharacterProfile so the next session sees the same list.
/// </summary>
public sealed class MovementFilterTests
{
    private static (ProfileService Profile, MovementFilter Filter) NewPair()
    {
        ProfileService profile = new();
        MovementFilter filter = new(profile);
        return (profile, filter);
    }

    [Fact]
    public void FreshFilter_IsEmpty()
    {
        (_, MovementFilter filter) = NewPair();
        Assert.False(filter.IsAvoided(new RoomKey(1, 1)));
        Assert.False(filter.IsStash(new RoomKey(1, 1)));
        Assert.Empty(filter.Avoided);
        Assert.Empty(filter.Stash);
    }

    [Fact]
    public void ProfileLoaded_HydratesFromPersistedLists()
    {
        ProfileService profile = new();
        CharacterProfile draft = profile.LoadBlank();
        draft.AvoidedRooms = new() { new RoomRef(1, 5), new RoomRef(2, 9) };
        draft.StashRooms   = new() { new RoomRef(3, 7) };

        // Subscribing the filter after the profile is already loaded
        // is the AppServices wiring order — the ctor must catch it.
        MovementFilter filter = new(profile);

        Assert.True(filter.IsAvoided(new RoomKey(1, 5)));
        Assert.True(filter.IsAvoided(new RoomKey(2, 9)));
        Assert.False(filter.IsAvoided(new RoomKey(2, 8)));
        Assert.True(filter.IsStash(new RoomKey(3, 7)));
    }

    [Fact]
    public void ProfileClosed_ClearsBothSets()
    {
        (ProfileService profile, MovementFilter filter) = NewPair();
        profile.LoadBlank();
        filter.MarkAvoided(new RoomKey(1, 1));
        filter.MarkStash(new RoomKey(1, 2));

        profile.Close();

        Assert.Empty(filter.Avoided);
        Assert.Empty(filter.Stash);
    }

    [Fact]
    public void MarkAvoided_NoProfile_IsNoOp()
    {
        (_, MovementFilter filter) = NewPair();
        filter.MarkAvoided(new RoomKey(1, 1));
        Assert.False(filter.IsAvoided(new RoomKey(1, 1)));
    }

    [Fact]
    public void MarkAvoided_AddsToSet_AndMirrorsBackToProfile()
    {
        (ProfileService profile, MovementFilter filter) = NewPair();
        CharacterProfile draft = profile.LoadBlank();

        filter.MarkAvoided(new RoomKey(1, 5));

        Assert.True(filter.IsAvoided(new RoomKey(1, 5)));
        Assert.NotNull(draft.AvoidedRooms);
        Assert.Single(draft.AvoidedRooms);
        Assert.Equal(1, draft.AvoidedRooms![0].Map);
        Assert.Equal(5, draft.AvoidedRooms[0].Room);
    }

    [Fact]
    public void MarkAvoided_Idempotent()
    {
        (ProfileService profile, MovementFilter filter) = NewPair();
        CharacterProfile draft = profile.LoadBlank();

        filter.MarkAvoided(new RoomKey(1, 5));
        filter.MarkAvoided(new RoomKey(1, 5));

        Assert.Single(filter.Avoided);
        Assert.Single(draft.AvoidedRooms!);
    }

    [Fact]
    public void UnmarkAvoided_RemovesFromBothInMemoryAndProfile()
    {
        (ProfileService profile, MovementFilter filter) = NewPair();
        CharacterProfile draft = profile.LoadBlank();

        filter.MarkAvoided(new RoomKey(1, 5));
        filter.MarkAvoided(new RoomKey(2, 9));

        filter.UnmarkAvoided(new RoomKey(1, 5));

        Assert.False(filter.IsAvoided(new RoomKey(1, 5)));
        Assert.True(filter.IsAvoided(new RoomKey(2, 9)));
        Assert.Single(draft.AvoidedRooms!);
        Assert.Equal(2, draft.AvoidedRooms![0].Map);
    }

    [Fact]
    public void UnmarkAvoided_NotMarked_IsNoOp()
    {
        (ProfileService profile, MovementFilter filter) = NewPair();
        profile.LoadBlank();

        int events = 0;
        filter.AvoidedChanged += () => events++;

        filter.UnmarkAvoided(new RoomKey(1, 5));

        Assert.Equal(0, events);
    }

    [Fact]
    public void MarkStash_TracksSeparateSet_FromAvoided()
    {
        (ProfileService profile, MovementFilter filter) = NewPair();
        CharacterProfile draft = profile.LoadBlank();

        filter.MarkAvoided(new RoomKey(1, 1));
        filter.MarkStash(new RoomKey(1, 2));

        Assert.True(filter.IsAvoided(new RoomKey(1, 1)));
        Assert.False(filter.IsStash(new RoomKey(1, 1)));
        Assert.True(filter.IsStash(new RoomKey(1, 2)));
        Assert.False(filter.IsAvoided(new RoomKey(1, 2)));

        Assert.Single(draft.AvoidedRooms!);
        Assert.Single(draft.StashRooms!);
    }

    [Fact]
    public void Mark_Fires_ChangedEvents()
    {
        (ProfileService profile, MovementFilter filter) = NewPair();
        profile.LoadBlank();

        int avoidedFires = 0;
        int stashFires = 0;
        filter.AvoidedChanged += () => avoidedFires++;
        filter.StashChanged   += () => stashFires++;

        filter.MarkAvoided(new RoomKey(1, 1));
        filter.MarkStash(new RoomKey(1, 2));
        filter.UnmarkAvoided(new RoomKey(1, 1));
        filter.UnmarkStash(new RoomKey(1, 2));

        Assert.Equal(2, avoidedFires);
        Assert.Equal(2, stashFires);
    }

    [Fact]
    public void ProfileSwap_RehydratesIntoFreshSet()
    {
        ProfileService profile = new();
        MovementFilter filter = new(profile);

        CharacterProfile first = profile.LoadBlank();
        first.AvoidedRooms = new() { new RoomRef(1, 1) };
        // Drive a fresh ProfileLoaded so the filter rebuilds from the
        // new state — same path Settings → BBS Apply uses.
        profile.LoadBlank().AvoidedRooms = new() { new RoomRef(9, 9) };
        profile.NotifyBbsPinApplied();

        // After the second LoadBlank, only the new profile's lists
        // should appear in the filter.
        Assert.False(filter.IsAvoided(new RoomKey(1, 1)));
        // The second LoadBlank's AvoidedRooms was assigned AFTER
        // ProfileLoaded fired, so the filter snapshot from that load
        // is empty — the filter only re-snapshots on Profile events.
        // Verify the documented contract: snapshot at event time.
        Assert.Empty(filter.Avoided);
    }

    // ----- IsExitBlocked: Form-A level-gate evaluation ---------------

    private static RoomExit GatedExit(int min, int max) =>
        new(new RoomKey(1, 2), RoomExitHint.None, RawHint: null,
            MinLevel: min, MaxLevel: max);

    [Fact]
    public void IsExitBlocked_NoGate_NeverBlocks()
    {
        (_, MovementFilter filter) = NewPair();
        filter.LevelProvider = () => 1;
        Assert.False(filter.IsExitBlocked(GatedExit(0, 0)));
    }

    [Fact]
    public void IsExitBlocked_UnknownLevel_DoesNotBlock()
    {
        (_, MovementFilter filter) = NewPair();
        filter.LevelProvider = () => null;   // no stat screen parsed yet
        Assert.False(filter.IsExitBlocked(GatedExit(20, 0)));
    }

    [Fact]
    public void IsExitBlocked_NoLevelProvider_DoesNotBlock()
    {
        (_, MovementFilter filter) = NewPair();
        // LevelProvider unset (AppServices wires it; a bare filter has none).
        Assert.False(filter.IsExitBlocked(GatedExit(20, 0)));
    }

    [Fact]
    public void IsExitBlocked_BelowFloor_Blocks()
    {
        (_, MovementFilter filter) = NewPair();
        filter.LevelProvider = () => 19;
        Assert.True(filter.IsExitBlocked(GatedExit(20, 0)));   // need 20+, have 19
    }

    [Fact]
    public void IsExitBlocked_AtFloor_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        filter.LevelProvider = () => 20;
        Assert.False(filter.IsExitBlocked(GatedExit(20, 0)));  // exactly meets floor
    }

    [Fact]
    public void IsExitBlocked_AboveCap_Blocks()
    {
        (_, MovementFilter filter) = NewPair();
        filter.LevelProvider = () => 4;
        Assert.True(filter.IsExitBlocked(GatedExit(0, 3)));    // cap 3, have 4
    }

    [Fact]
    public void IsExitBlocked_AtCap_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        filter.LevelProvider = () => 3;
        Assert.False(filter.IsExitBlocked(GatedExit(0, 3)));   // exactly at cap
    }

    [Fact]
    public void IsExitBlocked_WithinWindow_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        filter.LevelProvider = () => 15;
        Assert.False(filter.IsExitBlocked(GatedExit(10, 25))); // 10..25, have 15
    }

    [Fact]
    public void IsExitBlocked_OutsideWindow_Blocks()
    {
        (_, MovementFilter filter) = NewPair();
        filter.LevelProvider = () => 30;
        Assert.True(filter.IsExitBlocked(GatedExit(10, 25)));  // 10..25, have 30
    }

    // ----- IsExitBlocked: (Class: N OK) class-gate evaluation --------

    private static RoomExit ClassGatedExit(int classNumber) =>
        new(new RoomKey(1, 2), RoomExitHint.None, RawHint: null,
            ClassGate: classNumber);

    [Fact]
    public void IsExitBlocked_ClassGate_UnknownClass_DoesNotBlock()
    {
        (_, MovementFilter filter) = NewPair();
        filter.ClassNumberProvider = () => null;   // no stat screen parsed yet
        Assert.False(filter.IsExitBlocked(ClassGatedExit(13)));
    }

    [Fact]
    public void IsExitBlocked_ClassGate_NoProvider_DoesNotBlock()
    {
        (_, MovementFilter filter) = NewPair();
        // ClassNumberProvider unset (AppServices wires it; bare filter has none).
        Assert.False(filter.IsExitBlocked(ClassGatedExit(13)));
    }

    [Fact]
    public void IsExitBlocked_ClassGate_WrongClass_Blocks()
    {
        (_, MovementFilter filter) = NewPair();
        filter.ClassNumberProvider = () => 1;    // Warrior at a Druid (13) hall
        Assert.True(filter.IsExitBlocked(ClassGatedExit(13)));
    }

    [Fact]
    public void IsExitBlocked_ClassGate_MatchingClass_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        filter.ClassNumberProvider = () => 13;   // Druid at the Druid hall
        Assert.False(filter.IsExitBlocked(ClassGatedExit(13)));
    }

    [Fact]
    public void IsExitBlocked_ClassGate_IndependentOfLevelAndToll()
    {
        (_, MovementFilter filter) = NewPair();
        // A pure class gate carries no level window or toll, so those branches
        // must never rescue or block it.
        filter.LevelProvider = () => 1;
        filter.WealthProvider = () => 0;
        filter.ClassNumberProvider = () => 13;
        Assert.False(filter.IsExitBlocked(ClassGatedExit(13)));   // right class → allowed
    }

    [Fact]
    public void IsExitBlocked_ClassGate_DoesNotAffectPlainExit()
    {
        (_, MovementFilter filter) = NewPair();
        // Wrong class, but the exit has no class gate — must not block.
        filter.ClassNumberProvider = () => 1;
        Assert.False(filter.IsExitBlocked(GatedExit(0, 0)));
    }

    // ----- IsExitBlocked: party-bounds branch ------------------------

    [Fact]
    public void PartyBounds_TakePrecedence_OverSelfLevel()
    {
        (_, MovementFilter filter) = NewPair();
        // Self clears the floor, but the party's lowest member doesn't —
        // route around so we don't leave that member behind.
        filter.LevelProvider = () => 50;
        filter.PartyLevelBoundsProvider = () => (Low: 18, High: 50);
        Assert.True(filter.IsExitBlocked(GatedExit(20, 0)));   // need 20+, party low 18
    }

    [Fact]
    public void PartyBounds_WholePartyClearsFloor_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        filter.PartyLevelBoundsProvider = () => (Low: 22, High: 40);
        Assert.False(filter.IsExitBlocked(GatedExit(20, 0)));  // everyone ≥ 20
    }

    [Fact]
    public void PartyBounds_HighestMemberAboveCap_Blocks()
    {
        (_, MovementFilter filter) = NewPair();
        filter.PartyLevelBoundsProvider = () => (Low: 3, High: 30);
        Assert.True(filter.IsExitBlocked(GatedExit(0, 25)));   // cap 25, someone is 30
    }

    [Fact]
    public void PartyBounds_WholePartyWithinWindow_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        filter.PartyLevelBoundsProvider = () => (Low: 12, High: 24);
        Assert.False(filter.IsExitBlocked(GatedExit(10, 25))); // 10..25 covers 12..24
    }

    [Fact]
    public void PartyBounds_Null_FallsBackToSelfLevel()
    {
        (_, MovementFilter filter) = NewPair();
        // Not leading / nobody's level known — provider returns null, so
        // the self-only branch decides.
        filter.PartyLevelBoundsProvider = () => null;
        filter.LevelProvider = () => 19;
        Assert.True(filter.IsExitBlocked(GatedExit(20, 0)));   // self-only: 19 < 20
    }

    [Fact]
    public void PartyBounds_NoProvider_FallsBackToSelfLevel()
    {
        (_, MovementFilter filter) = NewPair();
        // PartyLevelBoundsProvider unset entirely (solo character).
        filter.LevelProvider = () => 30;
        Assert.False(filter.IsExitBlocked(GatedExit(20, 0)));  // self-only: 30 ≥ 20
    }

    [Fact]
    public void PartyBounds_NoGate_NeverBlocks()
    {
        (_, MovementFilter filter) = NewPair();
        filter.PartyLevelBoundsProvider = () => (Low: 1, High: 1);
        Assert.False(filter.IsExitBlocked(GatedExit(0, 0)));   // ungated exit
    }

    // ----- IsExitBlocked: (Toll: N) wealth-gate evaluation ----------

    private static RoomExit TollExit(int tollGold) =>
        new(new RoomKey(1, 2), RoomExitHint.Toll, RawHint: null,
            TollGold: tollGold);

    [Fact]
    public void IsExitBlocked_Toll_NoWealthProvider_DoesNotBlock()
    {
        (_, MovementFilter filter) = NewPair();
        // WealthProvider unset (AppServices wires it; a bare filter has none).
        Assert.False(filter.IsExitBlocked(TollExit(5)));
    }

    [Fact]
    public void IsExitBlocked_Toll_UnknownWealth_DoesNotBlock()
    {
        (_, MovementFilter filter) = NewPair();
        filter.WealthProvider = () => null;   // no inventory parsed yet
        Assert.False(filter.IsExitBlocked(TollExit(5)));
    }

    [Fact]
    public void IsExitBlocked_Toll_CanAfford_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        // Toll 5 needs 5*100 = 500 copper-value on hand.
        filter.WealthProvider = () => 500;
        Assert.False(filter.IsExitBlocked(TollExit(5)));   // exactly meets the bar
    }

    [Fact]
    public void IsExitBlocked_Toll_CannotAfford_Blocks()
    {
        (_, MovementFilter filter) = NewPair();
        filter.WealthProvider = () => 499;
        Assert.True(filter.IsExitBlocked(TollExit(5)));    // one short of 500
    }

    [Fact]
    public void IsExitBlocked_Toll_ZeroTollGold_DoesNotBlock()
    {
        (_, MovementFilter filter) = NewPair();
        filter.WealthProvider = () => 0;
        // A Toll-hint exit with no parsed amount can't gate — nothing to owe.
        Assert.False(filter.IsExitBlocked(TollExit(0)));
    }

    [Fact]
    public void IsExitBlocked_Toll_IndependentOfLevelGate()
    {
        (_, MovementFilter filter) = NewPair();
        // A pure toll exit carries no level window, so the level branch must
        // never block it even when the level provider says we're low.
        filter.LevelProvider = () => 1;
        filter.WealthProvider = () => 1000;
        Assert.False(filter.IsExitBlocked(TollExit(5)));
    }

    [Fact]
    public void IsExitBlocked_LevelGate_UnaffectedByWealthProvider()
    {
        (_, MovementFilter filter) = NewPair();
        // A pure level gate carries no toll, so the wealth branch must never
        // rescue a walk the level window forbids.
        filter.LevelProvider = () => 19;
        filter.WealthProvider = () => 1_000_000;
        Assert.True(filter.IsExitBlocked(GatedExit(20, 0)));
    }

    // ----- IsExitBlocked: party-wealth toll branch ------------------

    [Fact]
    public void PartyWealth_TakesPrecedence_OverSelfWealth()
    {
        (_, MovementFilter filter) = NewPair();
        // We can afford the toll, but the party's poorest member can't —
        // route around so we don't strand them at the gate.
        filter.WealthProvider = () => 1000;
        filter.PartyWealthProvider = () => 100;
        Assert.True(filter.IsExitBlocked(TollExit(5)));   // need 500, party min 100
    }

    [Fact]
    public void PartyWealth_WholePartyAffords_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        filter.PartyWealthProvider = () => 500;
        Assert.False(filter.IsExitBlocked(TollExit(5)));  // everyone has >= 500
    }

    [Fact]
    public void PartyWealth_PoorestMemberCannotAfford_Blocks()
    {
        (_, MovementFilter filter) = NewPair();
        filter.PartyWealthProvider = () => 499;
        Assert.True(filter.IsExitBlocked(TollExit(5)));   // one short of 500
    }

    [Fact]
    public void PartyWealth_Null_FallsBackToSelfWealth()
    {
        (_, MovementFilter filter) = NewPair();
        // Not leading / our own wallet unknown — provider returns null, so the
        // self-only branch decides.
        filter.PartyWealthProvider = () => null;
        filter.WealthProvider = () => 499;
        Assert.True(filter.IsExitBlocked(TollExit(5)));   // self-only: 499 < 500
    }

    [Fact]
    public void PartyWealth_NoProvider_FallsBackToSelfWealth()
    {
        (_, MovementFilter filter) = NewPair();
        // PartyWealthProvider unset entirely (solo character).
        filter.WealthProvider = () => 500;
        Assert.False(filter.IsExitBlocked(TollExit(5)));  // self-only: 500 >= 500
    }

    [Fact]
    public void PartyWealth_ZeroTollGold_DoesNotBlock()
    {
        (_, MovementFilter filter) = NewPair();
        filter.PartyWealthProvider = () => 0;
        // A Toll-hint exit with no parsed amount can't gate — nothing to owe.
        Assert.False(filter.IsExitBlocked(TollExit(0)));
    }

    [Fact]
    public void PartyWealth_DoesNotAffectLevelGate()
    {
        (_, MovementFilter filter) = NewPair();
        // The party-wealth branch fires only for a toll exit; a pure level
        // gate must be untouched even when the party is flat broke.
        filter.PartyWealthProvider = () => 0;
        filter.LevelProvider = () => 30;
        Assert.False(filter.IsExitBlocked(GatedExit(20, 0)));  // level OK, no toll
    }

    // ----- IRoomFilter integration with BfsMapper -------------------

    [Fact]
    public void BfsMapper_HonoursMovementFilter()
    {
        // Two-row strip: 1/1 ──N── 1/2 ──N── 1/3.
        // With 1/2 avoided, no path from 1/1 to 1/3.
        string root = Path.Combine(Path.GetTempPath(),
            "fujinterm-movementfilter-bfs-" + Path.GetRandomFileName());
        try
        {
            string setDir = Path.Combine(root, "alpha");
            Directory.CreateDirectory(setDir);
            File.WriteAllText(Path.Combine(setDir, "Rooms.json"), """
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
                """);
            GameDataCache cache = new(root);
            cache.SwitchSet("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");
            BfsMapper bfs = new(graph);

            ProfileService profile = new();
            profile.LoadBlank();
            MovementFilter filter = new(profile);
            filter.MarkAvoided(new RoomKey(1, 2));

            Assert.Null(bfs.FindPath(new RoomKey(1, 1), new RoomKey(1, 3), filter));
            Assert.NotNull(bfs.FindPath(new RoomKey(1, 1), new RoomKey(1, 3), filter: null));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    // ----- WarmForRoute: route-scoped @wealth probe -----------------

    // A ──E── B ──E(Toll:5)── C, plus an off-path A ──N(Toll:5)── D branch.
    // The A→C route crosses a toll; the A→B route does not (but the N→D toll
    // edge sits in the BFS frontier, which is exactly the off-path case that
    // used to fire a spurious probe).
    private const string TollStripJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/4 (Toll: 5)", "S": "0", "E": "1/2", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/3 (Toll: 5)", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "C",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "D",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private static void WithGraph(string roomsJson, Action<BfsMapper> body)
    {
        string root = Path.Combine(Path.GetTempPath(),
            "fujinterm-warmroute-" + Path.GetRandomFileName());
        try
        {
            string setDir = Path.Combine(root, "alpha");
            Directory.CreateDirectory(setDir);
            File.WriteAllText(Path.Combine(setDir, "Rooms.json"), roomsJson);
            GameDataCache cache = new(root);
            cache.SwitchSet("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");
            body(new BfsMapper(graph));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    [Fact]
    public void WarmForRoute_TollOnRoute_Probes()
    {
        WithGraph(TollStripJson, bfs =>
        {
            (_, MovementFilter filter) = NewPair();
            int probes = 0;
            filter.PartyWealthProvider = () => 100;   // leading a party
            filter.TollWealthProbe = () => probes++;

            filter.WarmForRoute(bfs, new RoomKey(1, 1), new RoomKey(1, 3));

            Assert.Equal(1, probes);   // A→C crosses the B→C toll
        });
    }

    [Fact]
    public void WarmForRoute_TollOffRoute_DoesNotProbe()
    {
        WithGraph(TollStripJson, bfs =>
        {
            (_, MovementFilter filter) = NewPair();
            int probes = 0;
            filter.PartyWealthProvider = () => 100;
            filter.TollWealthProbe = () => probes++;

            // A→B is a plain E; the N→D toll is in the frontier but not on
            // the route, so it must not trigger a poll (the reported bug).
            filter.WarmForRoute(bfs, new RoomKey(1, 1), new RoomKey(1, 2));

            Assert.Equal(0, probes);
        });
    }

    [Fact]
    public void WarmForRoute_NoPartyGate_DoesNotProbe()
    {
        WithGraph(TollStripJson, bfs =>
        {
            (_, MovementFilter filter) = NewPair();
            int probes = 0;
            // PartyWealthProvider unset (solo / not leading) → nothing to warm.
            filter.TollWealthProbe = () => probes++;

            filter.WarmForRoute(bfs, new RoomKey(1, 1), new RoomKey(1, 3));

            Assert.Equal(0, probes);
        });
    }

    [Fact]
    public void WarmForRoute_NoProbeWired_IsNoOp()
    {
        WithGraph(TollStripJson, bfs =>
        {
            (_, MovementFilter filter) = NewPair();
            filter.PartyWealthProvider = () => 100;
            // TollWealthProbe unset — must not throw.
            filter.WarmForRoute(bfs, new RoomKey(1, 1), new RoomKey(1, 3));
        });
    }

    [Fact]
    public void WarmForRoute_RestoresTollGate()
    {
        WithGraph(TollStripJson, bfs =>
        {
            (_, MovementFilter filter) = NewPair();
            filter.PartyWealthProvider = () => 100;   // party can't cover 500
            filter.TollWealthProbe = () => { };

            filter.WarmForRoute(bfs, new RoomKey(1, 1), new RoomKey(1, 3));

            // The suspend flag is cleared in the finally: a toll the party
            // can't afford still blocks after warming.
            Assert.True(filter.IsExitBlocked(TollExit(5)));
        });
    }
}
