using System.IO;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// LairTimerStore's default respawn, read from a lair room's Delay by realm, the
// game-data-cache invalidation, and the NextReadyAt projection off the arrival
// and kill anchors.
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

    // Two lair rooms in the imported shape, 5/100 (Delay 30) and 7/50 (Delay 15),
    // and a non-lair room at 1/1 for the empty-tag negative test.
    private const string RoomsJson = """
        [
          { "Map Number": 5, "Room Number": 100, "Name": "Sewer Lair",
            "Light": 0, "Shop": 0, "Lair": "(Max 2): 12,[120-5-1-2]", "Delay": 30,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 1, "Name": "Lobby",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 7, "Room Number": 50, "Name": "Old Lair",
            "Light": 0, "Shop": 0, "Lair": "(Max 1): 34,[7-50-50-1]", "Delay": 15,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Info.Legit 2 marks a Paradigm set; no Info table reads as Stock.
    private const string ParadigmInfoJson = """[ { "Legit": 2 } ]""";

    private (GameDataCache cache, RoomGraphManager graph, RoomTracker tracker)
        BuildFixture(RealmType realm = RealmType.Stock)
    {
        string setRoot = Path.Combine(AppPaths.GameDataRoot, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"),    RoomsJson);
        if (realm == RealmType.ParaMud)
            File.WriteAllText(Path.Combine(setRoot, "Info.json"), ParadigmInfoJson);

        GameDataCache cache = new();
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        RoomTracker tracker = new(graph);
        return (cache, graph, tracker);
    }

    // ----- DefaultRespawnSeconds — per-room Delay (primary) -------

    [Theory]
    [InlineData(RealmType.ParaMud, 270)]   // (5-1)*60 + 30
    [InlineData(RealmType.Stock, 330)]     // 5 to 6 min after the last kill → 5*60 + 30
    public void DefaultRespawnSeconds_PerRoomDelay_ReadByRealm(RealmType realm, int expected)
    {
        // Rooms.json with Delay=5, read by realm: the tooltip's source of truth
        // and the actual server behaviour.
        const string roomsWithDelay = """
            [
              { "Map Number": 1, "Room Number": 35, "Name": "Intersection",
                "Light": 0, "Shop": 0, "Lair": "(Max 1): 12,[1-1-1-1]", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        string setRoot = Path.Combine(AppPaths.GameDataRoot, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"), roomsWithDelay);
        if (realm == RealmType.ParaMud)
            File.WriteAllText(Path.Combine(setRoot, "Info.json"), ParadigmInfoJson);

        GameDataCache cache = new();
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        RoomTracker tracker = new(graph);
        using LairTimerStore store = new(cache, graph, tracker);

        Assert.Equal(expected, store.DefaultRespawnSeconds(new RoomKey(1, 35)));
    }

    [Theory]
    [InlineData(RealmType.ParaMud, 30)]   // (1-1)*60 + 30
    [InlineData(RealmType.Stock, 90)]     // 1 to 2 min → 1*60 + 30
    public void DefaultRespawnSeconds_PerRoomDelayOne(RealmType realm, int expected)
    {
        // Edge of the formula: Delay=1.
        const string roomsJson = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "FastRespawn",
                "Light": 0, "Shop": 0, "Lair": "(Max 1): 12,[1-1-1-1]", "Delay": 1,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        string setRoot = Path.Combine(AppPaths.GameDataRoot, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"),    roomsJson);
        File.WriteAllText(Path.Combine(setRoot, "Lairs.json"),    "[]");
        File.WriteAllText(Path.Combine(setRoot, "Monsters.json"), "[]");
        if (realm == RealmType.ParaMud)
            File.WriteAllText(Path.Combine(setRoot, "Info.json"), ParadigmInfoJson);

        GameDataCache cache = new();
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        RoomTracker tracker = new(graph);
        using LairTimerStore store = new(cache, graph, tracker);

        Assert.Equal(expected, store.DefaultRespawnSeconds(new RoomKey(1, 1)));
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

    // ----- Cache invalidation ---------------------------------------

    [Fact]
    public void ActiveSetChanged_DropsCachedRespawnLookups()
    {
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);

        // Prime the cache then rewrite the room's Delay and switch sets — the
        // new value should be observed.
        Assert.Equal(1830, store.DefaultRespawnSeconds(new RoomKey(5, 100)));

        string setRoot = Path.Combine(AppPaths.GameDataRoot, _setName);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"),
            RoomsJson.Replace("\"Delay\": 30", "\"Delay\": 90"));
        // Swap to null then back — cleanest way to force the cache to
        // re-read without poking private state.
        cache.SwitchSet(null);
        cache.SwitchSet(_setName);
        graph.OnActiveSetChanged(_setName);

        Assert.Equal(5430, store.DefaultRespawnSeconds(new RoomKey(5, 100)));
    }

    // ----- MaxDefaultRespawnSeconds ---------------------------------

    [Fact]
    public void MaxDefaultRespawnSeconds_ReturnsLongestLairInSet()
    {
        // Fixture has two lairs: 5/100 (Delay 30 = 1830s) and 7/50 (Delay 15 =
        // 930s), plus non-lair 1/1. The whole-set max is the 1830s lair.
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);

        Assert.Equal(1830, store.MaxDefaultRespawnSeconds());
    }

    [Fact]
    public void MaxDefaultRespawnSeconds_NoLairs_ReturnsNull()
    {
        // A set with only non-lair rooms resolves no respawn at all.
        const string roomsNoLair = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "Lobby",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        string setRoot = Path.Combine(AppPaths.GameDataRoot, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"),    roomsNoLair);
        File.WriteAllText(Path.Combine(setRoot, "Lairs.json"),    "[]");
        File.WriteAllText(Path.Combine(setRoot, "Monsters.json"), "[]");

        GameDataCache cache = new();
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        RoomTracker tracker = new(graph);
        using LairTimerStore store = new(cache, graph, tracker);

        Assert.Null(store.MaxDefaultRespawnSeconds());
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

    // ----- Clock start — Stock last kill vs Paradigm entry ----------

    [Fact]
    public void Stock_ClockRunsFromTheLastKill_NotTheEntry()
    {
        // Stock restarts one room clock on every kill, so a fight that ran after
        // the entry pushes the ready-time out by the fight's length.
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);
        RoomKey lair = new(5, 100);
        tracker.SetLocated(lair);
        DateTimeOffset entered = store.LastEntered(lair)!.Value;

        DateTimeOffset firstKill = entered.AddSeconds(10);
        DateTimeOffset lastKill = entered.AddSeconds(25);
        store.NoteKill(firstKill);
        store.NoteKill(lastKill);

        Assert.Equal(lastKill, store.ClockStart(lair));
        Assert.Equal(lastKill.AddSeconds(1830), store.NextReadyAt(lair));
        Assert.Equal(lastKill.AddSeconds(60), store.NextReadyAt(lair, overrideRespawnSeconds: 60));
    }

    [Fact]
    public void Stock_NoKillSeen_FallsBackToTheEntry()
    {
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);
        RoomKey lair = new(5, 100);
        tracker.SetLocated(lair);

        Assert.Equal(store.LastEntered(lair), store.ClockStart(lair));
    }

    [Fact]
    public void Paradigm_ClockStillRunsFromTheEntry()
    {
        // Paradigm's clock is unsettled (issue #839); it keeps timing from the entry.
        var (cache, graph, tracker) = BuildFixture(realm: RealmType.ParaMud);
        using LairTimerStore store = new(cache, graph, tracker);
        RoomKey lair = new(5, 100);
        tracker.SetLocated(lair);
        DateTimeOffset entered = store.LastEntered(lair)!.Value;

        store.NoteKill(entered.AddSeconds(25));

        Assert.Equal(entered, store.ClockStart(lair));
        Assert.Equal(entered.AddSeconds(1770), store.NextReadyAt(lair));   // Paradigm: (30-1)*60 + 30
    }

    [Fact]
    public void NoteKill_OutsideALairRoom_IsIgnored()
    {
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);
        tracker.SetLocated(new RoomKey(1, 1));   // the non-lair lobby

        store.NoteKill(DateTimeOffset.UtcNow);

        Assert.Null(store.LastKilled(new RoomKey(1, 1)));
        Assert.Null(store.LastKilled(new RoomKey(5, 100)));
    }

    [Fact]
    public void ResetArrivalsFor_DropsKillsToo()
    {
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);
        RoomKey lair = new(5, 100);
        tracker.SetLocated(lair);
        store.NoteKill(DateTimeOffset.UtcNow);

        store.ResetArrivalsFor(new[] { lair });

        Assert.Null(store.LastKilled(lair));
        Assert.Null(store.ClockStart(lair));
        Assert.Null(store.NextReadyAt(lair));
    }

    // An entry is stamped in UTC and a kill in local time. Both are instants, so the
    // timer is the same whichever offset the kill arrives with, and the log prints
    // both on the one clock (report paradigm-20261010-145330: the entry read four
    // hours from the kill beside it).
    [Fact]
    public void KillStampedWithALocalOffset_TimesAndPrintsAsTheSameInstant()
    {
        var (cache, graph, tracker) = BuildFixture();
        using LairTimerStore store = new(cache, graph, tracker);
        RoomKey lair = new(5, 100);
        tracker.SetLocated(lair);
        DateTimeOffset entered = store.LastEntered(lair)!.Value;
        Assert.Equal(TimeSpan.Zero, entered.Offset);

        DateTimeOffset killUtc = entered.AddSeconds(25);
        DateTimeOffset killLocal = killUtc.ToOffset(TimeSpan.FromHours(-4));
        store.NoteKill(killLocal);

        Assert.Equal(killUtc.AddSeconds(1830), store.NextReadyAt(lair));
        Assert.Equal(TimeSpan.FromSeconds(25), store.LastKilled(lair)!.Value - entered);
        Assert.Equal(LairTimerStore.LogTime(killUtc), LairTimerStore.LogTime(killLocal));
        Assert.Equal(
            killUtc.ToLocalTime().ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture),
            LairTimerStore.LogTime(killUtc));
    }
}
