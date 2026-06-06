using System.IO;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 7.18 — LairTimerStore default-respawn lookup paths. We exercise
/// both shapes the MDB exports: the NMR 1.83+ <c>Lairs.AvgDelay</c>
/// path (resolved via the GroupIndex back-reference on the room's
/// lair tag) and the pre-1.83 fallback that picks the slowest
/// <c>Monsters.RegenTime</c> across the listed mob ids.
/// </summary>
/// <remarks>
/// In-session arrival tracking is wired through
/// <see cref="RoomTracker.StateChanged"/> — that path is exercised by
/// the integration smoke once the scheduler PR (7.19) lands; this
/// suite focuses on the deterministic JSON-only lookup, the
/// game-data-cache invalidation contract, and the <c>NextReadyAt</c>
/// projection.
/// </remarks>
public sealed class LairTimerStoreTests : IDisposable
{
    private readonly string _setName;

    public LairTimerStoreTests()
    {
        string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
        _setName = "test-lairtimer-" + suffix;
    }

    public void Dispose()
    {
        try
        {
            string setFolder = Path.Combine(AppPaths.GameDataRoot, _setName);
            if (Directory.Exists(setFolder)) Directory.Delete(setFolder, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // ----- fixture builders -----------------------------------------

    // One lair room at 5/100 keyed to GroupIndex "120-005-001"; one
    // non-lair room at 1/1 for the empty-tag negative test.
    private const string RoomsJson = """
        [
          { "Map Number": 5, "Room Number": 100, "Name": "Sewer Lair",
            "Light": 0, "Shop": 0, "Lair": "[120-005-001][2]Group(lair): 5/100", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 1, "Name": "Lobby",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 7, "Room Number": 50, "Name": "Old Lair",
            "Light": 0, "Shop": 0, "Lair": "12, 34, 56", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private const string LairsJson = """
        [
          { "GroupIndex": "120-005-001", "AvgDelay": 30 }
        ]
        """;

    private const string MonstersJson = """
        [
          { "Number": 12, "RegenTime": 5 },
          { "Number": 34, "RegenTime": 15 },
          { "Number": 56, "RegenTime": 10 }
        ]
        """;

    private (GameDataCache cache, RoomGraphManager graph, RoomTracker tracker)
        BuildFixture(string? lairsOverride = null, string? monstersOverride = null)
    {
        string setRoot = Path.Combine(AppPaths.GameDataRoot, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"),    RoomsJson);
        File.WriteAllText(Path.Combine(setRoot, "Lairs.json"),    lairsOverride    ?? LairsJson);
        File.WriteAllText(Path.Combine(setRoot, "Monsters.json"), monstersOverride ?? MonstersJson);

        GameDataCache cache = new();
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        RoomTracker tracker = new(graph);
        return (cache, graph, tracker);
    }

    // ----- DefaultRespawnSeconds — per-room Delay (primary) -------

    [Fact]
    public void DefaultRespawnSeconds_PerRoomDelay_TakesPrecedence()
    {
        // Rooms.json with Delay=5 → GreaterMUD formula (D-1)*60+30
        // = 270s. Lairs.json says 30 min (1800s) for the same group
        // index — the per-room Delay must win because that's the
        // tooltip's source of truth + the actual server behaviour.
        const string roomsWithDelay = """
            [
              { "Map Number": 1, "Room Number": 35, "Name": "Intersection",
                "Light": 0, "Shop": 0, "Lair": "[1-1-1][1]Group(lair): 1/35", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        string setRoot = Path.Combine(AppPaths.GameDataRoot, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"), roomsWithDelay);
        File.WriteAllText(Path.Combine(setRoot, "Lairs.json"),
            """[ { "GroupIndex": "1-1-1", "AvgDelay": 30 } ]""");

        GameDataCache cache = new();
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        RoomTracker tracker = new(graph);
        using LairTimerStore store = new(cache, graph, tracker);

        Assert.Equal(270, store.DefaultRespawnSeconds(new RoomKey(1, 35)));
    }

    [Fact]
    public void DefaultRespawnSeconds_PerRoomDelayOne_ReturnsThirtySeconds()
    {
        // Edge of the formula: Delay=1 → (1-1)*60 + 30 = 30 s.
        const string roomsJson = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "FastRespawn",
                "Light": 0, "Shop": 0, "Lair": "[1-1-1][1]Group(lair): 1/1", "Delay": 1,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        string setRoot = Path.Combine(AppPaths.GameDataRoot, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"),    roomsJson);
        File.WriteAllText(Path.Combine(setRoot, "Lairs.json"),    "[]");
        File.WriteAllText(Path.Combine(setRoot, "Monsters.json"), "[]");

        GameDataCache cache = new();
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        RoomTracker tracker = new(graph);
        using LairTimerStore store = new(cache, graph, tracker);

        Assert.Equal(30, store.DefaultRespawnSeconds(new RoomKey(1, 1)));
    }

    // ----- DefaultRespawnSeconds — NMR 1.83+ path -------------------

    [Fact]
    public void DefaultRespawnSeconds_NmrTag_ResolvesViaGroupIndexAvgDelay()
    {
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);

        int? seconds = store.DefaultRespawnSeconds(new RoomKey(5, 100));

        // AvgDelay = 30 minutes × 60s/min = 1800s under the stock-realm
        // unit conversion (see LairTimerStore.AvgDelayUnitSeconds).
        Assert.Equal(1800, seconds);
    }

    [Fact]
    public void DefaultRespawnSeconds_NonLairRoom_ReturnsNull()
    {
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);

        Assert.Null(store.DefaultRespawnSeconds(new RoomKey(1, 1)));
    }

    [Fact]
    public void DefaultRespawnSeconds_UnknownRoom_ReturnsNull()
    {
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);

        Assert.Null(store.DefaultRespawnSeconds(new RoomKey(999, 999)));
    }

    // ----- DefaultRespawnSeconds — pre-1.83 fallback ----------------

    [Fact]
    public void DefaultRespawnSeconds_PreNmrTag_ResolvesToSlowestMonsterRegen()
    {
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);

        int? seconds = store.DefaultRespawnSeconds(new RoomKey(7, 50));

        // Slowest of {5, 15, 10} = 15 minutes × 60s/min = 900s.
        Assert.Equal(900, seconds);
    }

    [Fact]
    public void DefaultRespawnSeconds_PreNmrTag_NoMatchingMonsters_ReturnsNull()
    {
        // Monsters table missing the listed ids — we can't resolve a
        // respawn, return null so the scheduler treats it as "ready
        // now" rather than guessing.
        var (cache, graph, tracker) = BuildFixture(monstersOverride: "[]");
        using LairTimerStore store = new(cache, graph, tracker);

        Assert.Null(store.DefaultRespawnSeconds(new RoomKey(7, 50)));
    }

    // ----- Cache invalidation ---------------------------------------

    [Fact]
    public void ActiveSetChanged_DropsCachedRespawnLookups()
    {
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);

        // Prime the cache then rewrite Lairs.json with a different
        // delay and switch sets — the new value should be observed.
        Assert.Equal(1800, store.DefaultRespawnSeconds(new RoomKey(5, 100)));

        string setRoot = Path.Combine(AppPaths.GameDataRoot, _setName);
        File.WriteAllText(Path.Combine(setRoot, "Lairs.json"),
            """[ { "GroupIndex": "120-005-001", "AvgDelay": 90 } ]""");
        // Swap to null then back — cleanest way to force the cache to
        // re-read without poking private state.
        cache.SwitchSet(null);
        cache.SwitchSet(_setName);
        graph.OnActiveSetChanged(_setName);

        Assert.Equal(5400, store.DefaultRespawnSeconds(new RoomKey(5, 100)));
    }

    // ----- NextReadyAt / LastEntered no-arrival path ----------------

    [Fact]
    public void NextReadyAt_NoArrival_ReturnsNull()
    {
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);

        // No arrival recorded → no anchor for the projection.
        // Scheduler treats null as "ready now" per the API contract.
        Assert.Null(store.NextReadyAt(new RoomKey(5, 100)));
        Assert.Null(store.LastEntered(new RoomKey(5, 100)));
    }

    [Fact]
    public void NextReadyAt_NoRespawnAndOverrideNull_ReturnsNull()
    {
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);

        // Non-lair room — no respawn timer available at all.
        Assert.Null(store.NextReadyAt(new RoomKey(1, 1)));
    }

    [Fact]
    public void ResetArrivals_NoCrash_OnEmptyStore()
    {
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);

        store.ResetArrivals();
        Assert.Null(store.LastEntered(new RoomKey(5, 100)));
    }
}
