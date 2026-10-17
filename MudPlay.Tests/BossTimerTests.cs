using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.ViewModels.CharacterWorkshop;
using Xunit;

namespace MudPlay.Tests;

// Boss respawn-timer math (per realm, expiry, exact-spawn), the persisted
// BossTimerStore (mark / reset / kill-detection / active list), and the @timer
// remote report (format / substring filter / "expired").
public sealed class BossTimerTests : IDisposable
{
    // The router each engine reads, which a test's chat lines go through.
    private readonly Dictionary<RemoteCommandManager, MessageRouter> _routers = new();

    private readonly string _set = "boss-timer-test-" + Path.GetRandomFileName();
    private readonly string _seedPath =
        Path.Combine(Path.GetTempPath(), "bts-" + Path.GetRandomFileName() + ".json");

    public void Dispose()
    {
        try { string d = AppPaths.GameDataSetDir(_set); if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        try { if (File.Exists(_seedPath)) File.Delete(_seedPath); } catch { }
    }

    // ----- BossTimerMath (pure) ---------------------------------------------

    [Theory]
    [InlineData(0.80, "-20%")]
    [InlineData(0.90, "-10%")]
    [InlineData(0.95, "-5%")]
    [InlineData(1.00, "full")]
    public void WindowLabel_Paradigm_UsesDiscount(double f, string expected)
        => Assert.Equal(expected, BossTimerMath.WindowLabel(RealmType.ParaMud, f));

    [Theory]
    [InlineData(0.875, "87.5%")]
    [InlineData(1.00, "full")]
    public void WindowLabel_Stock_UsesElapsedFraction(double f, string expected)
        => Assert.Equal(expected, BossTimerMath.WindowLabel(RealmType.Stock, f));

    [Fact]
    public void Describe_Paradigm_WalksThresholdsInOrder()
    {
        // full = 24h. Early points at 19.2 / 21.6 / 22.8h, then 24h.
        Assert.Equal("-20%", BossTimerMath.Describe(RealmType.ParaMud, 24, TimeSpan.FromHours(0)).NextLabel);
        Assert.Equal("-10%", BossTimerMath.Describe(RealmType.ParaMud, 24, TimeSpan.FromHours(20)).NextLabel);
        Assert.Equal("-5%", BossTimerMath.Describe(RealmType.ParaMud, 24, TimeSpan.FromHours(22)).NextLabel);
        Assert.Equal("full", BossTimerMath.Describe(RealmType.ParaMud, 24, TimeSpan.FromHours(23)).NextLabel);
    }

    [Fact]
    public void Describe_Stock_HasSingleEarlyPoint()
    {
        Assert.Equal("87.5%", BossTimerMath.Describe(RealmType.Stock, 24, TimeSpan.FromHours(0)).NextLabel);
        Assert.Equal("full", BossTimerMath.Describe(RealmType.Stock, 24, TimeSpan.FromHours(22)).NextLabel);
    }

    [Fact]
    public void Describe_PastFullTimer_IsExpired()
    {
        Assert.True(BossTimerMath.Describe(RealmType.ParaMud, 24, TimeSpan.FromHours(24)).Expired);
        Assert.True(BossTimerMath.Describe(RealmType.ParaMud, 24, TimeSpan.FromHours(30)).Expired);
        Assert.False(BossTimerMath.Describe(RealmType.ParaMud, 24, TimeSpan.FromHours(23.9)).Expired);
    }

    [Fact]
    public void Describe_FullRemaining_CountsDownToGuaranteedSpawn()
    {
        BossWindowState s = BossTimerMath.Describe(RealmType.ParaMud, 24, TimeSpan.FromHours(6));
        Assert.Equal(18, Math.Round(s.FullRemaining.TotalHours));
    }

    [Fact]
    public void EarlyWindows_Paradigm_ListsAllUnpassedWindowsSoonestFirst()
    {
        // full = 24h. Early points at 19.2 / 21.6 / 22.8h. Fresh timer → all three,
        // soonest first, each remaining = its point.
        var w = BossTimerMath.EarlyWindows(RealmType.ParaMud, 24, TimeSpan.FromHours(0));
        Assert.Equal(new[] { "-20%", "-10%", "-5%" }, w.Select(x => x.Label).ToArray());
        Assert.Equal(new[] { 19.2, 21.6, 22.8 }, w.Select(x => Math.Round(x.Remaining.TotalHours, 1)).ToArray());
    }

    [Fact]
    public void EarlyWindows_DropOffAsTheyPass()
    {
        // Past -20% (19.2h): only -10% and -5% remain.
        var w = BossTimerMath.EarlyWindows(RealmType.ParaMud, 24, TimeSpan.FromHours(20));
        Assert.Equal(new[] { "-10%", "-5%" }, w.Select(x => x.Label).ToArray());

        // Past every early point: none left (the full timer covers the rest).
        Assert.Empty(BossTimerMath.EarlyWindows(RealmType.ParaMud, 24, TimeSpan.FromHours(23)));
    }

    [Fact]
    public void EarlyWindows_Stock_HasSingleWindow()
    {
        var w = BossTimerMath.EarlyWindows(RealmType.Stock, 24, TimeSpan.FromHours(0));
        Assert.Equal(new[] { "87.5%" }, w.Select(x => x.Label).ToArray());
    }

    // ----- BossTimerStore ----------------------------------------------------

    private void SeedGameData(RealmType realm, params (string Name, int Number, int Regen, int GameLimit)[] monsters)
    {
        string dir = AppPaths.GameDataSetDir(_set);
        Directory.CreateDirectory(dir);
        var rows = monsters.Select(m => new
        {
            m.Name,
            m.Number,
            RegenTime = m.Regen,
            GameLimit = m.GameLimit,
        });
        File.WriteAllText(Path.Combine(dir, "Monsters.json"), JsonSerializer.Serialize(rows));
        File.WriteAllText(Path.Combine(dir, "Info.json"),
            JsonSerializer.Serialize(new[] { new { Legit = realm == RealmType.ParaMud ? 2 : 0 } }));
    }

    private void SeedBosses(params BossDef[] defs) => JsonStore.Save(_seedPath, defs.ToList());

    private static BossDef Boss(string name, int? number = 1, BossRespawnType type = BossRespawnType.Timed,
        bool stock = true, bool para = true, params string[] rooms) => new()
    {
        Name = name, MonsterNumber = number, Rooms = rooms.ToList(),
        InStock = stock, InParadigm = para, RespawnType = type,
    };

    private (BossStore bosses, BossTimerStore timers, GameDataCache cache) NewStores()
    {
        GameDataCache cache = new();
        cache.SwitchSet(_set);
        BossStore bosses = new(seedPath: _seedPath);
        bosses.OnRealmChanged(AppPaths.GameDataSetDir(_set));
        BossTimerStore timers = new(bosses, cache);
        timers.OnRealmChanged(AppPaths.GameDataSetDir(_set));
        return (bosses, timers, cache);
    }

    [Fact]
    public void MarkKilled_StartsActiveTimer_AndPersistsAcrossReload()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (_, timers, _) = NewStores();

        timers.MarkKilled("ogre king");
        Assert.NotNull(timers.StatusFor(Boss("ogre king", number: 50, rooms: "3/300"), RealmType.ParaMud));

        // A fresh store for the same realm reloads the persisted kill time.
        BossStore bosses2 = new(seedPath: _seedPath); bosses2.OnRealmChanged(AppPaths.GameDataSetDir(_set));
        GameDataCache cache2 = new(); cache2.SwitchSet(_set);
        BossTimerStore reloaded = new(bosses2, cache2);
        reloaded.OnRealmChanged(AppPaths.GameDataSetDir(_set));
        Assert.NotNull(reloaded.KilledAt("ogre king"));
    }

    // Two characters on one realm, a client each, share the realm's timers file. Each
    // used to write the file from its own copy alone, so a kill on one client erased
    // the other's from the file, and after a restart a character had the other's
    // timers and none of its own.
    [Fact]
    public void TwoClientsOnOneRealm_KeepEachOthersTimers()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1), ("guardian golem", 51, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"), Boss("guardian golem", number: 51, rooms: "3/301"));
        var (_, first, _) = NewStores();
        var (_, second, _) = NewStores();

        first.MarkKilled("ogre king");
        second.MarkKilled("guardian golem");     // written on top of the first client's kill

        var (_, restarted, _) = NewStores();
        Assert.NotNull(restarted.KilledAt("ogre king"));
        Assert.NotNull(restarted.KilledAt("guardian golem"));

        // The first client picks the other's kill up on its next poll, and says so.
        int changed = 0;
        first.Changed += () => changed++;
        Assert.Null(first.KilledAt("guardian golem"));
        Assert.True(first.TakeInOutsideChanges());
        Assert.NotNull(first.KilledAt("guardian golem"));
        Assert.Equal(1, changed);
        Assert.False(first.TakeInOutsideChanges());   // nothing new since
    }

    [Fact]
    public void ResetOnOneClient_LeavesTheOtherClientsNewerKillInPlace()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1), ("guardian golem", 51, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"), Boss("guardian golem", number: 51, rooms: "3/301"));
        var (_, first, _) = NewStores();
        var (_, second, _) = NewStores();
        first.MarkKilled("ogre king");
        second.MarkKilled("guardian golem");

        first.Reset("ogre king");

        var (_, restarted, _) = NewStores();
        Assert.Null(restarted.KilledAt("ogre king"));
        Assert.NotNull(restarted.KilledAt("guardian golem"));
    }

    [Fact]
    public void BossRow_LastKilled_ReflectsTimerStore()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (_, timers, _) = NewStores();

        BossRowViewModel row = new(
            Boss("ogre king", number: 50, rooms: "3/300"), RealmType.ParaMud, 24, timers,
            onEdit: () => { }, onMarkRequested: _ => { });

        // No kill recorded yet → blank + sentinel sort key (sorts last).
        Assert.Equal(string.Empty, row.LastKilledDisplay);
        Assert.Equal(long.MinValue, row.LastKilledSortKey);

        // A recorded kill (button or auto-detect, both land on MarkKilled) surfaces.
        DateTimeOffset at = DateTimeOffset.UtcNow.AddMinutes(-30);
        timers.MarkKilled("ogre king", at);
        row.RefreshStatus();

        Assert.NotEqual(string.Empty, row.LastKilledDisplay);
        Assert.Equal(at.ToUnixTimeSeconds(), row.LastKilledSortKey);
    }

    [Fact]
    public void Reset_ClearsTimer()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (_, timers, _) = NewStores();

        timers.MarkKilled("ogre king");
        timers.Reset("ogre king");
        Assert.Null(timers.KilledAt("ogre king"));
        Assert.Null(timers.StatusFor(Boss("ogre king", number: 50, rooms: "3/300"), RealmType.ParaMud));
    }

    [Fact]
    public void StatusFor_Null_ForCleanupBoss()
    {
        SeedGameData(RealmType.ParaMud, ("lord feyr", 60, 24, 1));
        SeedBosses(Boss("lord feyr", number: 60, type: BossRespawnType.Cleanup, rooms: "17/2718"));
        var (_, timers, _) = NewStores();

        timers.MarkKilled("lord feyr");
        Assert.Null(timers.StatusFor(
            Boss("lord feyr", number: 60, type: BossRespawnType.Cleanup, rooms: "17/2718"), RealmType.ParaMud));
    }

    [Fact]
    public void StatusFor_Null_WhenGameDataHasNoTimer()
    {
        SeedGameData(RealmType.ParaMud, ("some mob", 1, 0, 0));   // not a boss (no regen, no limit)
        SeedBosses(Boss("ghost king", number: 999, rooms: "1/1"));
        var (_, timers, _) = NewStores();

        timers.MarkKilled("ghost king");
        Assert.Null(timers.StatusFor(Boss("ghost king", number: 999, rooms: "1/1"), RealmType.ParaMud));
    }

    private static MonsterDeathEvent Death() => new(ExperienceGained: 100, At: DateTimeOffset.UtcNow);

    [Fact]
    public void OnMonsterDied_EngagedNameInBossRoom_StartsTimer()
    {
        // The canonical signal: engaged the boss by name, then a death fires in
        // its room.
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (_, timers, _) = NewStores();

        timers.OnMonsterDied(Death(), new RoomKey(3, 300), engagedName: "ogre king");
        Assert.NotNull(timers.KilledAt("ogre king"));
    }

    [Fact]
    public void OnMonsterDied_EngagedNameWithArticle_Matches()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (_, timers, _) = NewStores();

        timers.OnMonsterDied(Death(), new RoomKey(3, 300), engagedName: "The Ogre King");
        Assert.NotNull(timers.KilledAt("ogre king"));
    }

    [Fact]
    public void OnMonsterDied_MultiWordNameWithWrapDoubledSpace_Matches()
    {
        // A multi-word monster name split across a word-wrap boundary in the death
        // line / roster comes back with a doubled internal space; the name match must
        // collapse it or a genuinely-killed boss never marks a timer (user report:
        // "colossal midnight dragon" auto-capture missed — same wrap-noise as @uses).
        SeedGameData(RealmType.ParaMud, ("colossal midnight dragon", 851, 24, 1));
        SeedBosses(Boss("colossal midnight dragon", number: 851, rooms: "17/1772"));
        var (_, timers, _) = NewStores();

        timers.OnMonsterDied(Death(), new RoomKey(17, 1772), engagedName: "colossal  midnight dragon");
        Assert.NotNull(timers.KilledAt("colossal midnight dragon"));
    }

    [Fact]
    public void OnMonsterDied_NoEngagedName_Ignored()
    {
        // A death with nothing to attribute it to — left to manual override.
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (_, timers, _) = NewStores();

        timers.OnMonsterDied(Death(), new RoomKey(3, 300), engagedName: null);
        Assert.Null(timers.KilledAt("ogre king"));
    }

    [Fact]
    public void OnMonsterDied_EngagedNameNotABoss_Ignored()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (_, timers, _) = NewStores();

        timers.OnMonsterDied(Death(), new RoomKey(3, 300), engagedName: "giant rat");
        Assert.Null(timers.KilledAt("ogre king"));
    }

    [Fact]
    public void OnMonsterDied_WrongRoom_Ignored()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (_, timers, _) = NewStores();

        timers.OnMonsterDied(Death(), new RoomKey(9, 999), engagedName: "ogre king");
        Assert.Null(timers.KilledAt("ogre king"));
    }

    private static RoomEntitiesObservation Roster(
        RoomObservationSource source, params string[] monsterNames) => new(
        "Also here: …",
        monsterNames.Select(n => new RoomEntity(n, n, EntityKind.Monster, null)).ToList(),
        DateTimeOffset.UtcNow,
        source);

    [Fact]
    public void RosterFallback_BossVanishesFromReparse_MarksKilled()
    {
        // The user's fallback: the boss is in the "Also here:" list, then a
        // same-room re-parse no longer shows it — a kill we never engaged.
        SeedGameData(RealmType.ParaMud, ("giant spider", 52, 5, 1));
        SeedBosses(Boss("giant spider", number: 52, rooms: "1/1678"));
        var (_, timers, _) = NewStores();
        RoomKey room = new(1, 1678);

        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.AlsoHere, "giant spider"), room, "Spider Lair");
        Assert.Null(timers.KilledAt("giant spider"));   // still present, not yet a kill

        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.AlsoHere), room, "Spider Lair");
        Assert.Null(timers.KilledAt("giant spider"));   // held until the display names the room
        timers.OnRoomDisplayed("Spider Lair");
        Assert.NotNull(timers.KilledAt("giant spider"));   // same room re-shown without it → killed
    }

    // Report paradigm-20260928-163621: "Also here:" arrives before "Obvious exits:", so
    // a room command out of the boss's room (`go manhole`) had the next room's roster
    // read against the boss's room while the tracker still held it.
    [Fact]
    public void RosterFallback_NextRoomsRoster_DoesNotMark()
    {
        SeedGameData(RealmType.ParaMud, ("mayor godfrey", 784, 2, 1));
        SeedBosses(Boss("mayor godfrey", number: 784, rooms: "1/2"));
        var (_, timers, _) = NewStores();
        RoomKey square = new(1, 2);

        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.AlsoHere, "Mayor Godfrey"), square, "Town Square");
        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.AlsoHere, "large giant rat"), square, "Town Square");
        timers.OnRoomDisplayed("Sewer Tunnel, Junction");

        Assert.Null(timers.KilledAt("mayor godfrey"));
    }

    [Fact]
    public void RosterFallback_MoveInFlight_DoesNotMark()
    {
        SeedGameData(RealmType.ParaMud, ("giant spider", 52, 5, 1));
        SeedBosses(Boss("giant spider", number: 52, rooms: "1/1678"));
        var (_, timers, _) = NewStores();
        RoomKey room = new(1, 1678);

        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.AlsoHere, "giant spider"), room, "Spider Lair");
        // A same-named neighbour's roster while our step is still landing.
        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.AlsoHere), room, "Spider Lair", movePending: true);
        timers.OnRoomDisplayed("Spider Lair");

        Assert.Null(timers.KilledAt("giant spider"));
    }

    [Fact]
    public void RosterFallback_Departure_DoesNotMark()
    {
        // A boss that walks out (Departure) instead of dying must NOT start a timer.
        SeedGameData(RealmType.ParaMud, ("giant spider", 52, 5, 1));
        SeedBosses(Boss("giant spider", number: 52, rooms: "1/1678"));
        var (_, timers, _) = NewStores();
        RoomKey room = new(1, 1678);

        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.AlsoHere, "giant spider"), room);
        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.Departure), room);
        Assert.Null(timers.KilledAt("giant spider"));

        // A later AlsoHere without it must not resurrect the false kill either —
        // the Departure already dropped it from the present-set.
        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.AlsoHere), room);
        Assert.Null(timers.KilledAt("giant spider"));
    }

    [Fact]
    public void RosterFallback_LeftTheRoom_DoesNotMark()
    {
        // Absence after we changed rooms isn't a kill — the boss just isn't here.
        SeedGameData(RealmType.ParaMud, ("giant spider", 52, 5, 1));
        SeedBosses(Boss("giant spider", number: 52, rooms: "1/1678"));
        var (_, timers, _) = NewStores();

        timers.OnRoomEntitiesObserved(
            Roster(RoomObservationSource.AlsoHere, "giant spider"), new RoomKey(1, 1678));
        // A re-parse in a DIFFERENT room (we walked out) — the boss's room no longer
        // matches, so nothing is marked.
        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.AlsoHere), new RoomKey(1, 1679));
        Assert.Null(timers.KilledAt("giant spider"));
    }

    [Fact]
    public void ActiveTimers_FiltersByRealm_AndOrdersBySoonestFull()
    {
        SeedGameData(RealmType.ParaMud,
            ("short boss", 1, 2, 1),
            ("long boss", 2, 48, 1),
            ("para only", 3, 10, 1));
        SeedBosses(
            Boss("short boss", number: 1, rooms: "1/1"),
            Boss("long boss", number: 2, rooms: "1/2"),
            Boss("para only", number: 3, stock: false, para: true, rooms: "1/3"));
        var (_, timers, _) = NewStores();

        timers.MarkKilled("short boss");
        timers.MarkKilled("long boss");
        timers.MarkKilled("para only");

        var para = timers.ActiveTimers(RealmType.ParaMud);
        Assert.Equal(new[] { "short boss", "para only", "long boss" },
            para.Select(a => a.Def.Name).ToArray());   // ordered by soonest full (2h < 10h < 48h)

        // Stock hides the paradigm-only boss.
        var stock = timers.ActiveTimers(RealmType.Stock);
        Assert.DoesNotContain(stock, a => a.Def.Name == "para only");
    }

    // ----- @timer handler ----------------------------------------------------

    private static readonly DateTime Now = new(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc);

    private (RemoteCommandManager engine, BossTimerStore timers) SetupHandler(
        RealmType realm, params (string Name, int Number, int Regen)[] bosses)
    {
        SeedGameData(realm, bosses.Select(b => (b.Name, b.Number, b.Regen, 1)).ToArray());
        SeedBosses(bosses.Select(b => Boss(b.Name, number: b.Number, rooms: "1/1")).ToArray());
        var (store, timers, cache) = NewStores();

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new(new ProfileService(), () => null);
        RemoteCommandManager engine = new(chat, party, players, now: () => _engineNow);
        _routers[engine] = router;
        engine.SetWireSender(_replies.Add);
        players.RecordObservation("Bob", null, null, null, null, null, null, Now);
        players.EditCustomization("Bob", new PlayerCustomization(RemoteControls: PlayerRemoteControls.QueryBossTimers));
        // The broadcast channel opened, as with its Talk-tab box unticked.
        engine.DisableBroadcastChannel = false;
        _ = new BossTimerQueryHandler(engine, store, timers, cache);
        return (engine, timers);
    }

    // What the remote-command engine put on the wire, through the sender the client binds.
    private readonly List<byte[]> _replies = new();

    // The engine's clock: it bounds what it says on the broadcast channel by the minute.
    private DateTimeOffset _engineNow = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    private static ChatLine Telepath(string msg) =>
        new(ChatChannel.TelepathIncoming, "Bob", msg);

    private string Reply() =>
        _replies.Select(b => Encoding.Latin1.GetString(b)).Select(StripWire).Single();

    private List<string> Replies() =>
        _replies.Select(b => Encoding.Latin1.GetString(b)).Select(StripWire).ToList();

    private static string StripWire(string wire)
    {
        string s = wire.TrimEnd('\r');
        int open = s.IndexOf('{');
        int close = s.LastIndexOf('}');
        return open >= 0 && close > open ? s[(open + 1)..close] : s;
    }

    // A timer sync may be asked for wherever players talk: said, telepathed, on the
    // gang channel or on the broadcast channel. It is answered on that channel, and
    // what is answered decodes back to the timer.
    [Theory]
    [InlineData(ChatChannel.TelepathIncoming, "/Bob {@timerdata ")]
    [InlineData(ChatChannel.Gangpath, "bg {@timerdata ")]
    [InlineData(ChatChannel.Local, ">Bob {@timerdata ")]
    [InlineData(ChatChannel.Broadcast, "br {@timerdata ")]
    public void TimerSync_OnEveryChannelItMayUse_IsAnsweredThere(ChatChannel channel, string opening)
    {
        var (engine, timers) = SetupHandler(RealmType.ParaMud, ("ogre king", 50, 24));
        timers.MarkKilled("ogre king");

        ChatLines.Hear(_routers[engine], new ChatLine(channel, "Bob", "@timer sync"));

        string[] wire = _replies.Select(b => Encoding.Latin1.GetString(b)).ToArray();
        Assert.NotEmpty(wire);
        Assert.All(wire, line => Assert.StartsWith(opening, line));
        string blob = string.Concat(Replies().Select(r => r.Split(' ', 3)[2]));
        Assert.Equal(50, Assert.Single(BossTimerSyncCodec.Decode(blob)).MonsterNumber);
    }

    // Sneaking, the answer to a said request goes by telepath; the asker takes a
    // telepathed answer after a said request for that reason
    // (BossTimerSyncCollectorTests).
    [Fact]
    public void TimerSync_SaidToASneakingClient_IsAnsweredByTelepath()
    {
        var (engine, timers) = SetupHandler(RealmType.ParaMud, ("ogre king", 50, 24));
        timers.MarkKilled("ogre king");
        engine.StealthedProvider = () => true;

        ChatLines.Hear(_routers[engine], new ChatLine(ChatChannel.Local, "Bob", "@timer sync"));

        Assert.All(_replies.Select(b => Encoding.Latin1.GetString(b)),
            line => Assert.StartsWith("/Bob {@timerdata ", line));
    }

    // On the broadcast channel an answer is several lines to everyone there, from
    // every client the asker may ask. The whole answer goes out, as one answer; a
    // second ask from the same player inside the minute draws nothing, and counts
    // for nothing against the other players there.
    [Fact]
    public void TimerSync_OnTheBroadcastChannel_GoesOutWhole_OnceAMinuteToEachAsker()
    {
        (string Name, int Number, int Regen)[] bosses =
            Enumerable.Range(1, 30).Select(i => ($"boss number {i}", 1000 + i, 24)).ToArray();
        var (engine, timers) = SetupHandler(RealmType.ParaMud, bosses);
        foreach ((string name, _, _) in bosses) timers.MarkKilled(name);
        ChatLine Asks(string who, ChatChannel channel = ChatChannel.Broadcast) => new(channel, who, "@timer sync");
        string[] Sent()
        {
            string[] wire = _replies.Select(b => Encoding.Latin1.GetString(b)).ToArray();
            _replies.Clear();
            return wire;
        }

        // Every line of the set, in order, decoding to all thirty timers.
        void SentTheTimersWhole(string opening)
        {
            string[] wire = Sent();
            Assert.True(wire.Length >= 3, $"only {wire.Length} line(s): the answer must be several for this test");
            for (int i = 0; i < wire.Length; i++)
                Assert.StartsWith($"{opening}{{@timerdata {i + 1}/{wire.Length} ", wire[i]);
            string blob = string.Concat(wire.Select(StripWire).Select(r => r.Split(' ', 3)[2]));
            Assert.Equal(30, BossTimerSyncCodec.Decode(blob).Count);
        }

        ChatLines.Hear(_routers[engine], Asks("Bob"));
        SentTheTimersWhole("br ");

        _engineNow += TimeSpan.FromSeconds(59);
        ChatLines.Hear(_routers[engine], Asks("Bob"));
        Assert.Empty(Sent());

        // The several lines were one answer, and an answer to a player who holds the
        // permission: the refusals to those who don't are counted apart, so the
        // others on the channel still have all of theirs this minute, the channel's
        // few, and no more.
        for (int i = 1; i <= BroadcastAnswerBudget.AnswersPerWindow; i++)
        {
            ChatLines.Hear(_routers[engine], Asks($"Passerby{i}"));
            Assert.Equal("br {command invalid or not allowed}\r", Sent().Single());
        }
        ChatLines.Hear(_routers[engine], Asks("OneMore"));
        Assert.Empty(Sent());

        // The bound is on that channel alone: the same player is answered by telepath.
        ChatLines.Hear(_routers[engine], Asks("Bob", ChatChannel.TelepathIncoming));
        SentTheTimersWhole("/Bob ");

        // A minute after the answer, it is given again.
        _engineNow += TimeSpan.FromSeconds(1);
        ChatLines.Hear(_routers[engine], Asks("Bob"));
        SentTheTimersWhole("br ");
    }

    // The broadcast channel is off until the user opens it: nothing asked there is
    // read, timers included.
    [Fact]
    public void TimerSync_OnTheBroadcastChannelSwitchedOff_IsNotAnswered()
    {
        var (engine, timers) = SetupHandler(RealmType.ParaMud, ("ogre king", 50, 24));
        timers.MarkKilled("ogre king");
        engine.DisableBroadcastChannel = true;

        ChatLines.Hear(_routers[engine], new ChatLine(ChatChannel.Broadcast, "Bob", "@timer sync"));

        Assert.Empty(_replies);
    }

    // ----- the merge window: what is written without a pick -----------------

    // Nine hours ago, to the second (the sync data carries whole seconds). On the
    // bosses' 24-hour respawn it is a running timer, as are the one held ten hours
    // before it and any a test offers up to eight hours after it: an expired timer
    // counts as none held.
    private static readonly DateTimeOffset Killed = new DateTimeOffset(
        DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero).AddHours(-9);

    // A timer of ours, when a test holds one before the answers come.
    private static readonly DateTimeOffset Held = Killed.AddHours(-10);

    // The merge window over real stores, tracking one boss it holds no timer for.
    // askedOn: the channel of the request that opened the window (null: opened from
    // the Bosses tab, nothing asked yet); to: who a telepathed one reached; byUser:
    // the request was the user's own. What the window sends goes out through the
    // chat log as a line of the user's, as it does in the app.
    // beforeOpen: what happens before the window exists, its chat log and timer
    // store already there (the requests that went out before the echo opened it).
    // killTimesWhenAsked: the window is handed our kill times as the user's
    // request went out, as the app hands an auto-opened one. broadcastOff: the
    // Talk tab's "Disallow @commands from broadcast" is ticked.
    private (BossTimerSyncViewModel window, BossTimerStore timers) NewSyncWindow(
        RemoteChannel? askedOn = RemoteChannel.Broadcast, string? to = null, bool byUser = true,
        DateTimeOffset? heldWhenAsked = null, Action<BossTimerStore>? beforeOpen = null,
        Func<BossStore, BossTimerStore, IReadOnlyDictionary<string, DateTimeOffset?>>? killTimesWhenAsked = null,
        bool broadcastOff = false)
    {
        // The golem is in the game data and not on our list.
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1), ("guardian golem", 51, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "1/1"));
        var (bosses, timers, cache) = NewStores();
        // A window opened by a request is opened with that request already out: a
        // timer of ours it is to find held is marked before it opens.
        if (heldWhenAsked is { } held) timers.MarkKilled("ogre king", held);
        _syncBosses = bosses;
        _syncRouter = new MessageRouter();
        DefaultPatterns.Seed(_syncRouter);
        _syncChat = new ChatRouter(_syncRouter, clock: _syncClock);
        _syncLastOnWire = null;
        _syncChat.LastTelepathOnWire = () => _syncLastOnWire;
        _syncSent.Clear();
        _syncTimers.Clear();
        IReadOnlyDictionary<string, DateTimeOffset?>? killTimes = killTimesWhenAsked?.Invoke(bosses, timers);
        beforeOpen?.Invoke(timers);
        BossTimerSyncViewModel window = new(bosses, timers, cache, _syncChat,
            send: line =>
            {
                _syncSent.Add(line);
                _syncLastOnWire = line;
                _syncChat.ObserveOutbound(Encoding.Latin1.GetBytes(line + "\r"), usersOwn: true);
            },
            preArmedOn: askedOn, _syncLog = new LogService(), broadcastOff: () => broadcastOff,
            isOurName: name => name == "Us", windowActive: _ => { }, preArmedTo: to, onUiThread: act => act(),
            preArmedByUser: byUser, armTimer: (delay, fire) => _syncTimers.Add((delay, fire)),
            killTimesWhenAsked: killTimes);
        return (window, timers);
    }

    // The quiet-period timers the window has armed and that have not run out yet.
    private readonly List<(TimeSpan Delay, Action Fire)> _syncTimers = new();

    // The quiet period runs out with no reply in it: every timer armed so far
    // fires, oldest first. Only the last one armed judges anything.
    private void QuietPasses()
    {
        var due = _syncTimers.ToList();
        _syncTimers.Clear();
        foreach ((_, Action fire) in due) fire();
    }

    // What the merge window put on the wire, the boss list it works on, the chat
    // log it reads, and that log's clock (null: the real one).
    private readonly List<string> _syncSent = new();
    private LogService _syncLog = null!;        // these four set by NewSyncWindow
    private BossStore _syncBosses = null!;
    private MessageRouter _syncRouter = null!;
    private ChatRouter _syncChat = null!;
    private Func<DateTimeOffset>? _syncClock;
    // The last line put on the wire, as the telepath pacer tells the chat log.
    private string? _syncLastOnWire;

    private static MudPlay.Terminal.LineExtractor.EmittedLine Shown(string text) =>
        new(text, new MudPlay.Terminal.CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    // What the user types at the terminal is told as it goes out, as the user's
    // own; the board then shows the line back.
    private void UserTypes(string line)
    {
        UserPressesEnter(line);
        BoardShows(line);
    }

    // The same before the board has shown anything for it.
    private void UserPressesEnter(string line)
    {
        _syncLastOnWire = line;
        _syncChat.ObserveOutbound(Encoding.Latin1.GetBytes(line + "\r"), usersOwn: true);
    }

    // A line the client put on the wire by itself or for another player.
    private void ClientSends(string line)
    {
        _syncLastOnWire = line;
        _syncChat.ObserveOutbound(Encoding.Latin1.GetBytes(line + "\r"), usersOwn: false);
    }

    // A row on the screen: the board showing back a line that was sent, whoever
    // sent it and however it comes out.
    private void BoardShows(string row) => _syncRouter.Dispatch(Shown(row));

    // The game's `--- Telepath Sent to <name> ---`.
    private void GameConfirms(string name) => BoardShows($"--- Telepath sent to {name} ---");

    // One responder's timer for the boss, and what it is to the user's requests.
    private static BossTimerSyncResponse Offers(
        string sender, RemoteChannel heardOn, TimerSyncStanding standing = TimerSyncStanding.OneOfSeveral, double hoursLater = 0) =>
        new(sender, new[] { new BossTimerSyncRecord(50, null, Killed.AddHours(hoursLater)) }, heardOn, standing);

    // The same timer as the chat lines the window's own collector reads, heard on a
    // channel from a player.
    private void Hears(ChatChannel channel, string sender, double hoursLater = 0)
    {
        string blob = BossTimerSyncCodec.Encode(new[] { new BossTimerSyncRecord(50, null, Killed.AddHours(hoursLater)) });
        string line = $"{{{BossTimerQueryHandler.SyncResponseToken} 1/1 {blob}}}";
        ChatLines.Hear(_syncRouter, channel, sender, line);
    }

    // Our own request coming back on a shared channel, as the chat log shows it.
    private void SeesUsAsk(ChatChannel channel) =>
        ChatLines.Hear(_syncRouter, channel, "Us", "@timer sync");

    // The window's Request Timers button. 0 gang, 1 telepath, 2 say, 3 broadcast.
    private static void Requests(BossTimerSyncViewModel window, int channelIndex, string target = "")
    {
        window.ChannelIndex = channelIndex;
        window.TelepathTarget = target;
        window.SendRequestCommand.Execute(null);
    }

    // A timer for a boss we track and hold no timer for is merged in on the
    // broadcast channel too, like on any channel we asked on: anyone who answers
    // our request there with well-formed data is compared like anyone else. Not as
    // it arrives, since several can answer there: once the replies have settled.
    // Closing the window then keeps it and says a timer was written.
    [Fact]
    public void SyncWindow_ATimerHeardOnBroadcast_IsMergedInLikeOnAnyChannel()
    {
        var (window, timers) = NewSyncWindow();
        using (window)
        {
            bool? closedWithWrites = null;
            window.CloseRequested += wrote => closedWithWrites = wrote;

            window.Fold(Offers("Stranger", RemoteChannel.Broadcast));
            Assert.Null(timers.KilledAt("ogre king"));
            QuietPasses();

            Assert.Equal(Killed, timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.False(row.HasConflict);
            Assert.True(row.WasAutoMerged);
            Assert.Empty(row.Responders);
            Assert.Contains("Stranger", row.ResolvedStatus);
            Assert.Contains("0 to pick, 1 merged in, 0 synced, 0 already in sync", window.Status);

            window.CancelCommand.Execute(null);

            Assert.True(closedWithWrites);
        }
    }

    // A timer heard there that differs from one we hold is listed, marked with
    // where it was heard, with "keep ours" chosen and nothing written. Picked, it
    // is written by Apply like any other.
    [Fact]
    public void SyncWindow_ADifferingTimerHeardOnBroadcast_IsListedMarked_AndWrittenWhenPicked()
    {
        var (window, timers) = NewSyncWindow();
        using (window)
        {
            timers.MarkKilled("ogre king", Held);

            window.Fold(Offers("Stranger", RemoteChannel.Broadcast));

            Assert.Equal(Held, timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.True(row.HasConflict);
            Assert.True(row.KeepOursSelected);
            Assert.Null(row.SelectedKilledAt);
            BossTimerSyncCellViewModel offer = Assert.Single(row.Responders);
            Assert.Equal("Stranger", offer.Responder);
            Assert.False(offer.IsSelected);
            Assert.EndsWith("heard on broadcast", offer.Text);
            Assert.Contains("1 to pick, 0 merged in, 0 synced", window.Status);

            offer.AdoptCommand.Execute(null);
            window.ApplyCommand.Execute(null);

            Assert.Equal(Killed, timers.KilledAt("ogre king"));
        }
    }

    // On the other channels the same timer, one of several that may come, is merged
    // in the same way: not as it arrives, but once the replies have settled.
    [Theory]
    [InlineData(RemoteChannel.Gangpath)]
    [InlineData(RemoteChannel.Local)]
    [InlineData(RemoteChannel.Telepath)]
    public void SyncWindow_TheSameTimerOnAnotherChannel_IsMergedInOnceTheRepliesSettle(RemoteChannel heardOn)
    {
        var (window, timers) = NewSyncWindow();
        using (window)
        {
            window.Fold(Offers("Bob", heardOn));
            Assert.Null(timers.KilledAt("ogre king"));
            QuietPasses();

            Assert.Equal(Killed, timers.KilledAt("ogre king"));
            Assert.True(Assert.Single(window.Rows).WasAutoMerged);
            Assert.DoesNotContain("broadcast", window.Status);
        }
    }

    // ----- the merge window: each answer compared with the timer we hold ------

    // A timer that is the same as ours (co-kills seconds apart) is left alone,
    // whoever it is from and whatever it is to our requests.
    [Theory]
    [InlineData(TimerSyncStanding.TheOneAsked)]
    [InlineData(TimerSyncStanding.OneOfSeveral)]
    [InlineData(TimerSyncStanding.NotAsked)]
    public void SyncWindow_TheSameTimerAsOurs_IsLeftAlone(TimerSyncStanding standing)
    {
        var (window, timers) = NewSyncWindow();
        using (window)
        {
            bool? closedWithWrites = null;
            window.CloseRequested += wrote => closedWithWrites = wrote;
            timers.MarkKilled("ogre king", Killed.AddSeconds(-20));

            window.Fold(Offers("Bob", RemoteChannel.Telepath, standing));

            Assert.Equal(Killed.AddSeconds(-20), timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.False(row.HasConflict);
            Assert.Contains("in sync with Bob", row.ResolvedStatus);
            Assert.Contains("0 to pick, 0 merged in, 0 synced, 1 already in sync", window.Status);
            window.CancelCommand.Execute(null);
            Assert.False(closedWithWrites);
        }
    }

    // A timer that differs from ours syncs when it is the answer of the one player
    // asked: ours is replaced as it arrives, with no pick, and closing keeps it.
    [Fact]
    public void SyncWindow_ADifferingTimerFromTheOnePlayerAsked_Syncs()
    {
        var (window, timers) = NewSyncWindow(RemoteChannel.Telepath, to: "Bob", heldWhenAsked: Held);
        using (window)
        {
            bool? closedWithWrites = null;
            window.CloseRequested += wrote => closedWithWrites = wrote;

            window.Fold(Offers("Bob", RemoteChannel.Telepath, TimerSyncStanding.TheOneAsked));

            Assert.Equal(Killed, timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.False(row.HasConflict);
            Assert.True(row.WasSynced);
            Assert.False(row.WasAutoMerged);
            Assert.Empty(row.Responders);
            Assert.StartsWith("Synced to Bob's timer", row.ResolvedStatus);
            Assert.Contains("0 to pick, 0 merged in, 1 synced, 0 already in sync", window.Status);
            window.CancelCommand.Execute(null);
            Assert.True(closedWithWrites);
        }
    }

    // Where several can or did answer, a timer that differs from ours is listed:
    // nothing is written for that boss until one is picked and applied.
    [Theory]
    [InlineData(RemoteChannel.Gangpath)]
    [InlineData(RemoteChannel.Local)]
    [InlineData(RemoteChannel.Telepath)]
    [InlineData(RemoteChannel.Broadcast)]
    public void SyncWindow_ADifferingTimerWhereSeveralCanAnswer_IsListed(RemoteChannel heardOn)
    {
        var (window, timers) = NewSyncWindow();
        using (window)
        {
            bool? closedWithWrites = null;
            window.CloseRequested += wrote => closedWithWrites = wrote;
            timers.MarkKilled("ogre king", Held);

            window.Fold(Offers("Bob", heardOn));
            window.Fold(Offers("Sue", heardOn, hoursLater: 3));

            Assert.Equal(Held, timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.True(row.HasConflict);
            Assert.False(row.WasSynced);
            Assert.Equal("Keep ours", row.KeepOursLabel);
            Assert.Equal(new[] { "Bob", "Sue" }, row.Responders.Select(c => c.Responder));
            Assert.Contains("1 to pick, 0 merged in, 0 synced", window.Status);

            // The replies settling changes nothing: no outcome here writes.
            QuietPasses();
            Assert.Equal(Held, timers.KilledAt("ogre king"));
            Assert.Contains("2 responder(s): 1 to pick, 0 merged in, 0 synced", window.Status);

            row.Responders[1].AdoptCommand.Execute(null);
            window.ApplyCommand.Execute(null);

            Assert.Equal(Killed.AddHours(3), timers.KilledAt("ogre king"));
            Assert.True(closedWithWrites);
        }
    }

    // Two responders with different timers for a boss we hold none for: neither is
    // merged in, as they come or once the replies have settled. Both are listed,
    // with "take none" chosen. A third who agrees with one of them does not make
    // all of them agree.
    [Fact]
    public void SyncWindow_TwoRespondersWhoDiffer_WithNoneHeld_AreBothListed_AndNothingIsWritten()
    {
        var (window, timers) = NewSyncWindow(RemoteChannel.Gangpath);
        using (window)
        {
            bool? closedWithWrites = null;
            window.CloseRequested += wrote => closedWithWrites = wrote;
            window.Fold(Offers("Bob", RemoteChannel.Gangpath));
            window.Fold(Offers("Sue", RemoteChannel.Gangpath, hoursLater: 3));
            Assert.Null(timers.KilledAt("ogre king"));
            QuietPasses();

            Assert.Null(timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.False(row.WasAutoMerged);
            Assert.True(row.HasConflict);
            Assert.True(row.KeepOursSelected);
            Assert.Equal("Take none", row.KeepOursLabel);
            Assert.Equal(new[] { "Bob", "Sue" }, row.Responders.Select(c => c.Responder));
            Assert.Equal("2 responder(s): 1 to pick, 0 merged in, 0 synced, 0 already in sync.", window.Status);

            window.Fold(Offers("Ann", RemoteChannel.Gangpath, hoursLater: 10.0 / 3600));
            QuietPasses();
            Assert.Equal(3, row.Responders.Count);
            Assert.Null(timers.KilledAt("ogre king"));
            window.CancelCommand.Execute(null);
            Assert.False(closedWithWrites);
        }
    }

    // What answers no request of the user's is left out of that judgment: the
    // replies to the user's request agree and are merged in, and the other stays
    // listed, against the timer merged in.
    [Fact]
    public void SyncWindow_WhatAnswersNoRequestOfTheUsers_IsLeftOutOfTheGather()
    {
        var (window, timers) = NewSyncWindow(RemoteChannel.Gangpath);
        using (window)
        {
            window.Fold(Offers("Mallory", RemoteChannel.Telepath, TimerSyncStanding.NotAsked, hoursLater: 3));
            window.Fold(Offers("Bob", RemoteChannel.Gangpath));
            window.Fold(Offers("Sue", RemoteChannel.Gangpath, hoursLater: 20.0 / 3600));
            Assert.Null(timers.KilledAt("ogre king"));
            QuietPasses();

            Assert.Equal(Killed, timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.Equal("Mallory", Assert.Single(row.Responders).Responder);
            Assert.Equal("Keep ours", row.KeepOursLabel);
            Assert.EndsWith("merged in from Bob, Sue", row.OursText);
            Assert.Equal("3 responder(s): 1 to pick, 1 merged in, 0 synced, 0 already in sync.", window.Status);
        }
    }

    // What answers no request of the user's is only ever listed: not merged in when
    // we hold no timer, not synced when ours differs. Closing writes nothing.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SyncWindow_WhatAnswersNoRequestOfTheUsers_IsOnlyEverListed(bool weHoldOne)
    {
        var (window, timers) = NewSyncWindow();
        using (window)
        {
            bool? closedWithWrites = null;
            window.CloseRequested += wrote => closedWithWrites = wrote;
            DateTimeOffset? ours = weHoldOne ? Held : null;
            if (weHoldOne) timers.MarkKilled("ogre king", Held);

            window.Fold(Offers("Mallory", RemoteChannel.Telepath, TimerSyncStanding.NotAsked));

            Assert.Equal(ours, timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.True(row.HasConflict);
            Assert.False(row.WasAutoMerged);
            Assert.False(row.WasSynced);
            Assert.True(row.KeepOursSelected);
            Assert.Equal(weHoldOne ? "Keep ours" : "Take none", row.KeepOursLabel);
            Assert.Contains("1 to pick, 0 merged in, 0 synced", window.Status);

            window.ApplyCommand.Execute(null);   // nothing picked
            Assert.Equal(ours, timers.KilledAt("ogre king"));
            Assert.False(closedWithWrites);
        }
    }

    // ----- the merge window: what each kind of request makes of its answers ---

    // Asked by telepath of one player, through the button: the player the game says
    // the telepath reached is the one asked (the name typed may be the first
    // letters of theirs). From them a timer we hold none for is merged in and one
    // that differs syncs. A telepath from anyone else, a player called exactly what
    // was typed included, answers no request: listed, nothing written.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SyncWindow_AskedByTelepathOfOnePlayer_MergesAndSyncsOnlyFromThePlayerItReached(bool weHoldOne)
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            DateTimeOffset? ours = weHoldOne ? Held : null;
            if (weHoldOne) timers.MarkKilled("ogre king", Held);
            Requests(window, 1, "bo");
            Assert.Equal("/bo @timer sync", _syncSent.Single());
            GameConfirms("Bob");

            Hears(ChatChannel.TelepathIncoming, "Bo", hoursLater: 2);
            Hears(ChatChannel.TelepathIncoming, "Sue", hoursLater: 3);
            Assert.Equal(ours, timers.KilledAt("ogre king"));
            Assert.Equal(new[] { "Bo", "Sue" }, Assert.Single(window.Rows).Responders.Select(c => c.Responder));

            Hears(ChatChannel.TelepathIncoming, "Bob");
            Assert.Equal(Killed, timers.KilledAt("ogre king"));
            Assert.Contains(weHoldOne ? "1 to pick, 0 merged in, 1 synced" : "1 to pick, 1 merged in, 0 synced", window.Status);
        }
    }

    // Until the game shows the telepath sent (its letters fitted two players, or
    // nobody; or it was not sent), nobody was asked: a telepathed timer answers no
    // request, even from a player whose name is exactly what was typed.
    [Fact]
    public void SyncWindow_ATelepathTheGameNeverShowedSent_AsksNobody()
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            Requests(window, 1, "Bob");

            Hears(ChatChannel.TelepathIncoming, "Bob");

            Assert.Null(timers.KilledAt("ogre king"));
            Assert.Equal("Bob", Assert.Single(Assert.Single(window.Rows).Responders).Responder);
            Assert.Contains("1 to pick, 0 merged in, 0 synced", window.Status);
        }
    }

    // A telepath goes to one word, and to somebody: with no name, or with two
    // words, nothing is sent and nothing is collected.
    [Theory]
    [InlineData("", "Enter a player name")]
    [InlineData("   ", "Enter a player name")]
    [InlineData("Sir Bob", "A telepath goes to one name")]
    [InlineData("Bob\tSmith", "A telepath goes to one name")]
    public void SyncWindow_ATelepathWithNoNameOrTwoWords_IsNotSent(string target, string status)
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            Requests(window, 1, target);

            Assert.Empty(_syncSent);
            Assert.False(window.Requested);
            Assert.StartsWith(status, window.Status);
            Hears(ChatChannel.TelepathIncoming, "Sir");
            Assert.Empty(window.Rows);
            Assert.Null(timers.KilledAt("ogre king"));
        }
    }

    // ----- the merge window: where several can answer, the replies are gathered --

    // The requests several players can answer, each with the channel its answers
    // come by: gang, say, a say answered by telepath (a sneaking client answers
    // that way), broadcast, and (button 1) three players asked by telepath.
    public static TheoryData<int, ChatChannel> WhereSeveralCanAnswer => new()
    {
        { 0, ChatChannel.Gangpath },
        { 2, ChatChannel.Local },
        { 2, ChatChannel.TelepathIncoming },
        { 3, ChatChannel.Broadcast },
        { 1, ChatChannel.TelepathIncoming },
    };

    // Such a request, made with the window's button. The players telepathed are
    // the three who answer in these tests.
    private void AsksSeveral(BossTimerSyncViewModel window, int button)
    {
        if (button != 1) { Requests(window, button); return; }
        foreach (string name in new[] { "Bob", "Sue", "Ann" })
        {
            Requests(window, 1, name);
            GameConfirms(name);
        }
    }

    // We hold no timer and every reply agrees (co-kills seconds apart): nothing is
    // written as they arrive, each is shown and can be picked, and the status says
    // the replies are being gathered. Once they have settled the timer is merged
    // in, and closing the window keeps it.
    [Theory]
    [MemberData(nameof(WhereSeveralCanAnswer))]
    public void SyncWindow_Gathered_WithNoneHeld_RepliesThatAgree_AreMergedInAtTheSettle_NotBefore(
        int button, ChatChannel answeredOn)
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            bool? closedWithWrites = null;
            window.CloseRequested += wrote => closedWithWrites = wrote;
            AsksSeveral(window, button);

            Hears(answeredOn, "Bob");
            Hears(answeredOn, "Sue", hoursLater: 20.0 / 3600);

            Assert.Null(timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.True(row.HasConflict);
            Assert.Equal("Take none", row.KeepOursLabel);
            Assert.Equal(new[] { "Bob", "Sue" }, row.Responders.Select(c => c.Responder));
            Assert.StartsWith("2 responder(s) so far, replies still being gathered: 1 waiting for every reply, 0 to pick, 0 merged in", window.Status);
            Assert.Contains("no reply has come for 10 seconds", window.Status);
            Assert.All(_syncTimers, t => Assert.Equal(TimeSpan.FromSeconds(10), t.Delay));

            QuietPasses();

            Assert.Equal(Killed, timers.KilledAt("ogre king"));
            Assert.False(row.HasConflict);
            Assert.True(row.WasAutoMerged);
            Assert.Empty(row.Responders);
            Assert.StartsWith("Merged in from Bob, Sue", row.ResolvedStatus);
            Assert.Equal("2 responder(s): 0 to pick, 1 merged in, 0 synced, 0 already in sync.", window.Status);
            window.CancelCommand.Execute(null);
            Assert.True(closedWithWrites);
        }
    }

    // We hold no timer and the replies differ: nothing is written, as they arrive
    // or once they have settled. Every reply is listed with "take none" chosen,
    // and the one picked is written by Apply.
    [Theory]
    [MemberData(nameof(WhereSeveralCanAnswer))]
    public void SyncWindow_Gathered_WithNoneHeld_RepliesThatDiffer_AreListed_AndNothingIsWritten(
        int button, ChatChannel answeredOn)
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            AsksSeveral(window, button);

            Hears(answeredOn, "Bob");
            Hears(answeredOn, "Sue", hoursLater: 3);
            QuietPasses();

            Assert.Null(timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.True(row.HasConflict);
            Assert.False(row.WasAutoMerged);
            Assert.True(row.KeepOursSelected);
            Assert.Equal("Take none", row.KeepOursLabel);
            Assert.Equal(new[] { "Bob", "Sue" }, row.Responders.Select(c => c.Responder));
            Assert.Equal(answeredOn == ChatChannel.Broadcast, row.Responders[0].Text.EndsWith("heard on broadcast"));
            Assert.Equal("2 responder(s): 1 to pick, 0 merged in, 0 synced, 0 already in sync.", window.Status);

            row.Responders[1].AdoptCommand.Execute(null);
            window.ApplyCommand.Execute(null);
            Assert.Equal(Killed.AddHours(3), timers.KilledAt("ogre king"));
        }
    }

    // A single reply where several could have answered, and no timer held: all one
    // of them agree, so it is merged in, but only once the replies have settled.
    [Theory]
    [MemberData(nameof(WhereSeveralCanAnswer))]
    public void SyncWindow_Gathered_WithNoneHeld_ASingleReply_IsMergedInAtTheSettle(int button, ChatChannel answeredOn)
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            AsksSeveral(window, button);

            Hears(answeredOn, "Bob");
            Assert.Null(timers.KilledAt("ogre king"));
            QuietPasses();

            Assert.Equal(Killed, timers.KilledAt("ogre king"));
            Assert.Equal("1 responder(s): 0 to pick, 1 merged in, 0 synced, 0 already in sync.", window.Status);
        }
    }

    // We hold a timer and every reply matches it: nothing happens, then or later.
    [Theory]
    [MemberData(nameof(WhereSeveralCanAnswer))]
    public void SyncWindow_Gathered_WithATimerHeld_RepliesThatAllMatchOurs_ChangeNothing(int button, ChatChannel answeredOn)
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            bool? closedWithWrites = null;
            window.CloseRequested += wrote => closedWithWrites = wrote;
            timers.MarkKilled("ogre king", Killed);
            AsksSeveral(window, button);

            Hears(answeredOn, "Bob", hoursLater: 20.0 / 3600);
            Hears(answeredOn, "Sue", hoursLater: -30.0 / 3600);
            QuietPasses();

            Assert.Equal(Killed, timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.False(row.HasConflict);
            Assert.Empty(row.Responders);
            Assert.Equal("2 responder(s): 0 to pick, 0 merged in, 0 synced, 1 already in sync.", window.Status);
            window.CancelCommand.Execute(null);
            Assert.False(closedWithWrites);
        }
    }

    // We hold a timer and one reply of three differs from it: the boss is listed,
    // with ours as the default and the one that differs. Ours is untouched.
    [Theory]
    [MemberData(nameof(WhereSeveralCanAnswer))]
    public void SyncWindow_Gathered_WithATimerHeld_OneReplyOfThreeThatDiffers_IsListedWithOurs(
        int button, ChatChannel answeredOn)
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            timers.MarkKilled("ogre king", Killed);
            AsksSeveral(window, button);

            Hears(answeredOn, "Bob", hoursLater: 20.0 / 3600);
            Hears(answeredOn, "Sue", hoursLater: 3);
            Hears(answeredOn, "Ann");
            QuietPasses();

            Assert.Equal(Killed, timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.True(row.HasConflict);
            Assert.True(row.KeepOursSelected);
            Assert.Equal("Keep ours", row.KeepOursLabel);
            Assert.Equal("Sue", Assert.Single(row.Responders).Responder);
            Assert.Equal("3 responder(s): 1 to pick, 0 merged in, 0 synced, 0 already in sync.", window.Status);
        }
    }

    // A reply that comes after the others were judged is compared with what we hold
    // then. It differs from the timer merged in: the boss is listed with it, beside
    // our timer and whose it was. One that matches adds nothing.
    [Theory]
    [MemberData(nameof(WhereSeveralCanAnswer))]
    public void SyncWindow_Gathered_ALateReplyThatDiffers_IsListedAgainstTheTimerMergedIn(int button, ChatChannel answeredOn)
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            AsksSeveral(window, button);
            Hears(answeredOn, "Bob");
            QuietPasses();
            Assert.Equal(Killed, timers.KilledAt("ogre king"));

            Hears(answeredOn, "Sue", hoursLater: 3);
            Hears(answeredOn, "Ann", hoursLater: 20.0 / 3600);

            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.True(row.HasConflict);
            Assert.Equal("Keep ours", row.KeepOursLabel);
            Assert.EndsWith("merged in from Bob", row.OursText);
            Assert.Equal("Sue", Assert.Single(row.Responders).Responder);

            QuietPasses();
            Assert.Equal(Killed, timers.KilledAt("ogre king"));
            Assert.Equal("Sue", Assert.Single(row.Responders).Responder);
            Assert.Equal("3 responder(s): 1 to pick, 1 merged in, 0 synced, 0 already in sync.", window.Status);
        }
    }

    // One responder's set of timers, as the chat lines the window's collector
    // reads, cut into pieces of pieceChars.
    private static string[] SetLines(int pieceChars, params BossTimerSyncRecord[] records)
    {
        IReadOnlyList<string> pieces = BossTimerSyncCodec.Chunk(BossTimerSyncCodec.Encode(records), pieceChars);
        return pieces.Select((p, i) => $"{{{BossTimerQueryHandler.SyncResponseToken} {i + 1}/{pieces.Count} {p}}}").ToArray();
    }

    private void HearsLine(ChatChannel channel, string sender, string line) =>
        ChatLines.Hear(_syncRouter, channel, sender, line);

    // A late reply that is the first to name a boss starts a gather of its own:
    // nothing is written for that boss until the replies have settled again.
    [Fact]
    public void SyncWindow_Gathered_ALateReplyThatIsTheFirstToNameABoss_IsGatheredInItsTurn()
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            _syncBosses.Save(_syncBosses.Resolve().Append(Boss("guardian golem", number: 51, rooms: "1/2")).ToList());
            Requests(window, 0);
            Hears(ChatChannel.Gangpath, "Bob");
            QuietPasses();
            Assert.Equal(Killed, timers.KilledAt("ogre king"));

            foreach (string line in SetLines(200,
                         new BossTimerSyncRecord(50, null, Killed.AddSeconds(20)),
                         new BossTimerSyncRecord(51, null, Killed.AddHours(1))))
                HearsLine(ChatChannel.Gangpath, "Sue", line);

            Assert.Null(timers.KilledAt("guardian golem"));
            Assert.Contains("1 waiting for every reply", window.Status);
            QuietPasses();

            Assert.Equal(Killed.AddHours(1), timers.KilledAt("guardian golem"));
            Assert.Equal(Killed, timers.KilledAt("ogre king"));
            Assert.Equal("2 responder(s): 0 to pick, 2 merged in, 0 synced, 0 already in sync.", window.Status);
        }
    }

    // Every line of every reply starts the quiet period again: a timer armed before
    // the latest line judges nothing when it runs out, so a set still arriving is
    // never judged without its last line.
    [Fact]
    public void SyncWindow_Gathered_ALineOfAReplyStillArriving_HoldsTheSettleBack()
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            Requests(window, 0);
            Hears(ChatChannel.Gangpath, "Bob");
            string[] sues = SetLines(6, new BossTimerSyncRecord(50, null, Killed.AddHours(3)));
            Assert.True(sues.Length >= 2);
            HearsLine(ChatChannel.Gangpath, "Sue", sues[0]);
            var armedBeforeTheLastLine = _syncTimers.ToList();
            _syncTimers.Clear();
            foreach (string line in sues.Skip(1)) HearsLine(ChatChannel.Gangpath, "Sue", line);

            foreach ((_, Action fire) in armedBeforeTheLastLine) fire();
            Assert.Null(timers.KilledAt("ogre king"));
            Assert.Contains("replies still being gathered", window.Status);

            QuietPasses();
            Assert.Null(timers.KilledAt("ogre king"));
            Assert.Equal(new[] { "Bob", "Sue" }, Assert.Single(window.Rows).Responders.Select(c => c.Responder));
        }
    }

    // A pick made before the replies have settled is written by Apply like any
    // other, and the window closes as it always did; what the quiet period would
    // have judged afterwards is judged by nobody.
    [Theory]
    [MemberData(nameof(WhereSeveralCanAnswer))]
    public void SyncWindow_Gathered_ApplyBeforeTheSettle_WritesThePick_AndNothingMore(int button, ChatChannel answeredOn)
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            bool? closedWithWrites = null;
            window.CloseRequested += wrote => closedWithWrites = wrote;
            _syncBosses.Save(_syncBosses.Resolve().Append(Boss("guardian golem", number: 51, rooms: "1/2")).ToList());
            AsksSeveral(window, button);
            foreach (string line in SetLines(200,
                         new BossTimerSyncRecord(50, null, Killed),
                         new BossTimerSyncRecord(51, null, Killed.AddHours(1))))
                HearsLine(answeredOn, "Bob", line);
            Hears(answeredOn, "Sue", hoursLater: 3);

            RowOf(window, "ogre king").Responders[1].AdoptCommand.Execute(null);
            window.ApplyCommand.Execute(null);

            Assert.Equal(Killed.AddHours(3), timers.KilledAt("ogre king"));
            Assert.Null(timers.KilledAt("guardian golem"));
            Assert.True(closedWithWrites);
            window.Dispose();
            QuietPasses();
            Assert.Null(timers.KilledAt("guardian golem"));
            Assert.Equal(Killed.AddHours(3), timers.KilledAt("ogre king"));
        }
    }

    // Closed before the replies have settled: the gather is abandoned. Nothing is
    // written then or when the quiet period would have run out, and the program
    // log says which bosses were left.
    [Theory]
    [MemberData(nameof(WhereSeveralCanAnswer))]
    public void SyncWindow_Gathered_ClosedBeforeTheSettle_WritesNothing(int button, ChatChannel answeredOn)
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            bool? closedWithWrites = null;
            window.CloseRequested += wrote => closedWithWrites = wrote;
            AsksSeveral(window, button);
            Hears(answeredOn, "Bob");
            Hears(answeredOn, "Sue", hoursLater: 20.0 / 3600);
            Assert.Contains("closing the window writes nothing more", window.Status);

            window.CancelCommand.Execute(null);
            window.Dispose();
            QuietPasses();

            Assert.False(closedWithWrites);
            Assert.Null(timers.KilledAt("ogre king"));
            Assert.Contains(_syncLog.Snapshot(), e => e.Message.Contains(
                "closed while replies were still being gathered: nothing was written for 1 boss(es) whose replies had not been judged (ogre king)"));
        }
    }

    // With one player asked by telepath nothing waits: no quiet period is started,
    // and that player's timer is merged in as it arrives.
    [Fact]
    public void SyncWindow_WithOnePlayerAsked_NothingWaitsForAQuietPeriod()
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            Requests(window, 1, "bo");
            GameConfirms("Bob");
            Hears(ChatChannel.TelepathIncoming, "Bob");

            Assert.Equal(Killed, timers.KilledAt("ogre king"));
            Assert.Empty(_syncTimers);
            Assert.Equal("1 responder(s): 0 to pick, 1 merged in, 0 synced, 0 already in sync.", window.Status);
        }
    }

    // A request the client sent for another player gathers nothing: its answers
    // are listed, and no quiet period ever merges them in, however well they agree.
    [Theory]
    [InlineData(RemoteChannel.Gangpath, ChatChannel.Gangpath)]
    [InlineData(RemoteChannel.Local, ChatChannel.Local)]
    [InlineData(RemoteChannel.Local, ChatChannel.TelepathIncoming)]
    [InlineData(RemoteChannel.Broadcast, ChatChannel.Broadcast)]
    public void SyncWindow_OpenedForARequestTheClientSentForAnotherPlayer_GathersNothing(
        RemoteChannel sentOn, ChatChannel answeredOn)
    {
        var (window, timers) = NewSyncWindow(sentOn, to: null, byUser: false);
        using (window)
        {
            Hears(answeredOn, "Mallory");
            Hears(answeredOn, "Sue", hoursLater: 20.0 / 3600);
            Assert.Empty(_syncTimers);
            QuietPasses();

            Assert.Null(timers.KilledAt("ogre king"));
            Assert.Equal("2 responder(s): 1 to pick, 0 merged in, 0 synced, 0 already in sync.", window.Status);
        }
    }

    // With a timer of ours held, on each channel: an answer with the same timer is
    // left alone; asked again, an answer that differs syncs from the one player
    // telepathed and is listed everywhere else, with ours untouched.
    [Theory]
    [InlineData(1, ChatChannel.TelepathIncoming, true)]
    [InlineData(0, ChatChannel.Gangpath, false)]
    [InlineData(2, ChatChannel.Local, false)]
    [InlineData(2, ChatChannel.TelepathIncoming, false)]
    [InlineData(3, ChatChannel.Broadcast, false)]
    public void SyncWindow_WithATimerHeld_TheSameIsLeftAlone_AndWhatDiffersSyncsOrIsListed(
        int button, ChatChannel answeredOn, bool syncs)
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            timers.MarkKilled("ogre king", Held);
            Requests(window, button, "Bob");
            if (button == 1) GameConfirms("Bob");

            Hears(answeredOn, "Bob", hoursLater: -10 + 20.0 / 3600);   // ours, marked 20 s later
            Assert.Equal(Held, timers.KilledAt("ogre king"));
            Assert.Contains("0 to pick, 0 merged in, 0 synced, 1 already in sync", window.Status);

            Requests(window, button, "Bob");
            if (button == 1) GameConfirms("Bob");
            Hears(answeredOn, "Bob");

            Assert.Equal(syncs ? Killed : Held, timers.KilledAt("ogre king"));
            Assert.Contains(syncs ? "0 to pick, 0 merged in, 1 synced" : "1 to pick, 0 merged in, 0 synced", window.Status);
            Assert.Equal(!syncs, Assert.Single(window.Rows).HasConflict);
        }
    }

    // More than one player asked by telepath in one window: each is asked, and
    // several did answer. Their replies are judged together: they differ, so both
    // are listed, and neither is merged in or synced.
    [Fact]
    public void SyncWindow_TwoPlayersAskedByTelepath_AreBothAsked_AndWhatDiffersIsListed()
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            Requests(window, 1, "Bob");
            Requests(window, 1, "Sue");
            GameConfirms("Bob");
            GameConfirms("Sue");

            Hears(ChatChannel.TelepathIncoming, "Sue");
            Assert.Null(timers.KilledAt("ogre king"));
            Hears(ChatChannel.TelepathIncoming, "Bob", hoursLater: 3);
            QuietPasses();

            Assert.Null(timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.False(row.WasSynced);
            Assert.False(row.WasAutoMerged);
            Assert.Equal(new[] { "Sue", "Bob" }, row.Responders.Select(c => c.Responder));
        }
    }

    // One player asked by telepath and a shared channel asked as well, in either
    // order: several can answer, so the telepathed player's differing timer is
    // listed too, and no telepath syncs.
    [Theory]
    [InlineData(2, 1)]   // say, then telepath
    [InlineData(1, 2)]   // telepath, then say
    [InlineData(0, 1)]   // gang, then telepath
    [InlineData(1, 3)]   // telepath, then broadcast
    public void SyncWindow_OnceASharedChannelWasAsked_NoTelepathSyncs(int first, int second)
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            timers.MarkKilled("ogre king", Held);
            Requests(window, first, "Bob");
            Requests(window, second, "Bob");
            Assert.Equal(2, _syncSent.Count);
            GameConfirms("Bob");

            Hears(ChatChannel.TelepathIncoming, "Bob");

            Assert.Equal(Held, timers.KilledAt("ogre king"));
            Assert.Equal("Bob", Assert.Single(Assert.Single(window.Rows).Responders).Responder);
            Assert.Contains("1 to pick, 0 merged in, 0 synced", window.Status);
        }
    }

    // A second request by the button adds its channel: answers to the first are
    // still read, and a player who answered the first may answer the second.
    [Fact]
    public void SyncWindow_ASecondRequestByTheButton_AddsItsChannel()
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            timers.MarkKilled("ogre king", Held);
            Requests(window, 0);
            Hears(ChatChannel.Gangpath, "Bob");
            Requests(window, 2);

            Hears(ChatChannel.Gangpath, "Sue", hoursLater: 3);   // a late answer to the first
            Hears(ChatChannel.Local, "Bob", hoursLater: 5);      // Bob again, to the second

            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.Equal(new[] { "Sue", "Bob" }, row.Responders.Select(c => c.Responder));
            Assert.Equal(Killed.AddHours(5), row.Responders[1].KilledAt);
        }
    }

    // A request typed at the terminal is the user's like the button's: the window
    // it opens is told who the telepath reached, and that player's differing timer
    // syncs.
    [Fact]
    public void SyncWindow_OpenedByATelepathTheUserTyped_SyncsFromThePlayerItReached()
    {
        var (window, timers) = NewSyncWindow(RemoteChannel.Telepath, to: "Bob", heldWhenAsked: Held);
        using (window)
        {
            Assert.Contains("from the player your telepath reached", window.Status);

            Hears(ChatChannel.TelepathIncoming, "Sue", hoursLater: 3);
            Assert.Equal(Held, timers.KilledAt("ogre king"));
            Hears(ChatChannel.TelepathIncoming, "Bob");

            Assert.Equal(Killed, timers.KilledAt("ogre king"));
            Assert.Contains("1 to pick, 0 merged in, 1 synced", window.Status);
        }
    }

    // Typed on a shared channel with the window open, the request is asked there:
    // its answers are gathered and merged in once they have settled, and with the
    // gang asked too the telepathed player's differing timer is listed.
    [Fact]
    public void SyncWindow_ARequestTypedOnASharedChannelWhileOpen_IsAskedThere()
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            Requests(window, 1, "Sue");
            GameConfirms("Sue");
            UserTypes("bg @timer sync");
            SeesUsAsk(ChatChannel.Gangpath);

            Hears(ChatChannel.Gangpath, "Ann");
            Assert.Null(timers.KilledAt("ogre king"));
            QuietPasses();
            Assert.Equal(Killed, timers.KilledAt("ogre king"));
            Hears(ChatChannel.TelepathIncoming, "Sue", hoursLater: 3);
            Assert.Equal(Killed, timers.KilledAt("ogre king"));
            Assert.Equal("Sue", Assert.Single(Assert.Single(window.Rows).Responders).Responder);
        }
    }

    // With lines of the client's own going out around it, a telepath the user typed
    // is still the user's request, and the player the game confirms it reached is
    // the one asked.
    [Fact]
    public void SyncWindow_ATypedRequestAmongClientSends_StillAsksThePlayerItReached()
    {
        var (window, timers) = NewSyncWindow(askedOn: RemoteChannel.Telepath, byUser: false);
        using (window)
        {
            ClientSends("/Sue @health");
            ClientSends("/Mallory {ok}");
            UserTypes("/bo @timer sync");
            BoardShows("/Sue @health");
            GameConfirms("Sue");
            BoardShows("/Mallory {ok}");
            GameConfirms("Mallory");
            GameConfirms("Bob");

            Hears(ChatChannel.TelepathIncoming, "Sue", hoursLater: 3);
            Assert.Null(timers.KilledAt("ogre king"));
            Hears(ChatChannel.TelepathIncoming, "Bob");
            Assert.Equal(Killed, timers.KilledAt("ogre king"));
        }
    }

    // ----- the merge window: a request the user did not make ------------------

    // The client also sends `@timer sync` for other players: a relay-back, a `@do`.
    // The window opens for it, says the request was not the user's, and listens;
    // nothing that answers it is merged in or synced, on any channel, whether we
    // hold a timer or not. Closing writes nothing.
    [Theory]
    [InlineData(RemoteChannel.Telepath, ChatChannel.TelepathIncoming, false)]
    [InlineData(RemoteChannel.Telepath, ChatChannel.TelepathIncoming, true)]
    [InlineData(RemoteChannel.Gangpath, ChatChannel.Gangpath, false)]
    [InlineData(RemoteChannel.Gangpath, ChatChannel.Gangpath, true)]
    [InlineData(RemoteChannel.Local, ChatChannel.Local, false)]
    [InlineData(RemoteChannel.Local, ChatChannel.TelepathIncoming, true)]
    [InlineData(RemoteChannel.Broadcast, ChatChannel.Broadcast, false)]
    public void SyncWindow_OpenedForARequestTheClientSentForAnotherPlayer_WritesNothing(
        RemoteChannel sentOn, ChatChannel answeredOn, bool weHoldOne)
    {
        var (window, timers) = NewSyncWindow(sentOn, to: null, byUser: false);
        using (window)
        {
            bool? closedWithWrites = null;
            window.CloseRequested += wrote => closedWithWrites = wrote;
            DateTimeOffset? ours = weHoldOne ? Held : null;
            if (weHoldOne) timers.MarkKilled("ogre king", Held);
            Assert.Contains("this client sent for another player's remote command", window.Status);
            Assert.DoesNotContain("your @timer sync", window.Status);

            Hears(answeredOn, "Mallory");
            Hears(answeredOn, "Sue", hoursLater: 3);

            Assert.Equal(ours, timers.KilledAt("ogre king"));
            Assert.Equal(new[] { "Mallory", "Sue" }, Assert.Single(window.Rows).Responders.Select(c => c.Responder));
            Assert.Contains("1 to pick, 0 merged in, 0 synced", window.Status);
            window.CancelCommand.Execute(null);
            Assert.False(closedWithWrites);
        }
    }

    // The same with a window already open for a request of the user's: the channel
    // the client asked on for another player is listened on, and what comes on it
    // is listed. The player the user did ask is still an answer to the user: taken
    // as it arrives while that player is the one thing asked, and with the others'
    // once the replies have settled when a shared channel is listened on as well.
    [Theory]
    [InlineData("/Mallory @timer sync", ChatChannel.TelepathIncoming)]
    [InlineData("bg @timer sync", ChatChannel.Gangpath)]
    public void SyncWindow_ARequestTheClientSendsForAnotherPlayerWhileOpen_AsksNobody(string sent, ChatChannel answeredOn)
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            Requests(window, 1, "Bob");
            GameConfirms("Bob");

            ClientSends(sent);
            if (answeredOn == ChatChannel.Gangpath) SeesUsAsk(ChatChannel.Gangpath);
            else GameConfirms("Mallory");
            Hears(answeredOn, "Mallory", hoursLater: 3);

            Assert.Null(timers.KilledAt("ogre king"));
            Assert.Equal("Mallory", Assert.Single(Assert.Single(window.Rows).Responders).Responder);

            Hears(ChatChannel.TelepathIncoming, "Bob");
            if (answeredOn == ChatChannel.Gangpath)
            {
                Assert.Null(timers.KilledAt("ogre king"));
                QuietPasses();
            }
            Assert.Equal(Killed, timers.KilledAt("ogre king"));
        }
    }

    // A player holding Execute commands sends `@do /<her> @timer sync`, and the
    // board shows each line the client sends back as a row; any number of further
    // `@do` lines follow, each with its `{ok}` confirmed. No row on the screen is a
    // send of the user's, so she was never asked: her data is listed, never written.
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void SyncWindow_WhatTheBoardShowsOfTheClientsOwnSends_NeverMakesAnyoneAsked(int furtherDos)
    {
        var (window, timers) = NewSyncWindow(RemoteChannel.Telepath, to: null, byUser: false);
        using (window)
        {
            ClientSends("/Mallory @timer sync");
            ClientSends("/Mallory {ok}");
            BoardShows("/Mallory @timer sync");
            GameConfirms("Mallory");
            BoardShows("/Mallory {ok}");
            GameConfirms("Mallory");
            for (int i = 0; i < furtherDos; i++)
            {
                ClientSends("/Mallory {ok}");
                BoardShows("/Mallory {ok}");
                GameConfirms("Mallory");
            }

            Hears(ChatChannel.TelepathIncoming, "Mallory");

            Assert.Null(timers.KilledAt("ogre king"));
            Assert.Equal("Mallory", Assert.Single(Assert.Single(window.Rows).Responders).Responder);
            Assert.Contains("1 to pick, 0 merged in, 0 synced", window.Status);
        }
    }

    // The three ways a row on the screen fails to match what the client sent for
    // her (an 8-bit character drawn as another, a copy that comes late, a copy cut
    // just before a second slash), each followed by one more line to her and its
    // confirmation. The row is nobody's send: nothing of hers is ever written.
    [Theory]
    [InlineData("eight-bit")]
    [InlineData("late")]
    [InlineData("cut")]
    public void SyncWindow_ARowThatDoesNotMatchWhatTheClientSent_NeverMakesAnyoneAsked(string door)
    {
        DateTimeOffset now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
        _syncClock = () => now;
        var (window, timers) = NewSyncWindow(RemoteChannel.Telepath, to: null, byUser: false);
        using (window)
        {
            switch (door)
            {
                case "eight-bit":
                    ClientSends("/Mallory @timer sync \u0081");
                    BoardShows("/Mallory @timer sync ü");
                    break;
                case "late":
                    ClientSends("/Mallory @timer sync");
                    now = now.AddSeconds(20);
                    BoardShows("/Mallory @timer sync");
                    break;
                default:
                    ClientSends("/Mallory x /Mallory @timer sync");
                    BoardShows("/Mallory x ");
                    BoardShows("Sue gossips: hello");
                    BoardShows("/Mallory @timer sync");
                    break;
            }
            GameConfirms("Mallory");
            ClientSends("/Mallory {ok}");
            GameConfirms("Mallory");
            GameConfirms("Mallory");

            Hears(ChatChannel.TelepathIncoming, "Mallory");

            Assert.Null(timers.KilledAt("ogre king"));
            Assert.Equal("Mallory", Assert.Single(Assert.Single(window.Rows).Responders).Responder);
            Assert.Contains("1 to pick, 0 merged in, 0 synced", window.Status);
        }
    }

    // A confirmation is its own telepath's. The user's request reached nobody; then
    // a telepath to someone else is confirmed, the user's own or the client's, or
    // the client's reply to a player whose `@do` sent a request to nobody. None of
    // those players was asked: nothing of theirs is written.
    [Theory]
    [InlineData("/bo @timer sync", true, "/Sue hello", true, "Sue")]
    [InlineData("/Bob @timer sync", true, "/Sue @health", false, "Sue")]
    [InlineData("/qq @timer sync", false, "/Mallory {ok}", false, "Mallory")]
    public void SyncWindow_AConfirmationForAnotherTelepath_AsksNobody(
        string request, bool requestIsUsers, string other, bool otherIsUsers, string confirmed)
    {
        var (window, timers) = NewSyncWindow(askedOn: RemoteChannel.Telepath, byUser: false);
        using (window)
        {
            if (requestIsUsers) UserTypes(request); else ClientSends(request);
            if (otherIsUsers) UserTypes(other); else ClientSends(other);
            GameConfirms(confirmed);

            Hears(ChatChannel.TelepathIncoming, confirmed);

            Assert.Null(timers.KilledAt("ogre king"));
            Assert.Equal(confirmed, Assert.Single(Assert.Single(window.Rows).Responders).Responder);
        }
    }

    // Another player's confirmation arriving between the request and its own does
    // not take the request's place.
    [Fact]
    public void SyncWindow_AConfirmationForAnotherPlayerInBetween_DoesNotTakeTheRequestsPlace()
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            Requests(window, 1, "bo");
            ClientSends("/Sue @health");
            GameConfirms("Sue");
            GameConfirms("Bob");

            Hears(ChatChannel.TelepathIncoming, "Sue", hoursLater: 3);
            Assert.Null(timers.KilledAt("ogre king"));
            Hears(ChatChannel.TelepathIncoming, "Bob");
            Assert.Equal(Killed, timers.KilledAt("ogre king"));
        }
    }

    // ----- the merge window: a timer of ours that moved while it was open ----

    // A pick is made against the timer the row showed as ours. We kill the boss
    // while the window is open; the pick, an older time, is not written over that
    // kill. The row is shown against our timer as it is now and the window stays
    // open. Picked again, it is written.
    [Fact]
    public void SyncWindow_APickIsNotWrittenOverATimerOfOursThatChangedSince()
    {
        var (window, timers) = NewSyncWindow(RemoteChannel.Gangpath);
        using (window)
        {
            int closes = 0;
            window.CloseRequested += _ => closes++;
            timers.MarkKilled("ogre king", Held);
            window.Fold(Offers("Bob", RemoteChannel.Gangpath));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            row.Responders[0].AdoptCommand.Execute(null);

            DateTimeOffset ownKill = Killed.AddHours(6);
            timers.MarkKilled("ogre king", ownKill);
            window.ApplyCommand.Execute(null);

            Assert.Equal(ownKill, timers.KilledAt("ogre king"));
            Assert.Equal(0, closes);
            Assert.Contains("Applied 0 timer update(s). Not written: ogre king", window.Status);
            Assert.Equal(ownKill, row.OursKilledAt);
            Assert.True(row.KeepOursSelected);
            Assert.Null(row.SelectedKilledAt);

            // Picked again, against our timer as it now is, the pick is written.
            row.Responders[0].AdoptCommand.Execute(null);
            window.ApplyCommand.Execute(null);
            Assert.Equal(Killed, timers.KilledAt("ogre king"));
            Assert.Equal(1, closes);
        }
    }

    // On arrival too the comparison is with what we hold at that moment, and a
    // timer of ours that changed after the user asked is never written over: a row
    // built while we held no timer, then a kill of our own, then the asked player's
    // older timer. It differs from our kill. Our kill stands and the boss is listed,
    // from the one player asked as from one of several.
    [Theory]
    [InlineData(TimerSyncStanding.TheOneAsked)]
    [InlineData(TimerSyncStanding.OneOfSeveral)]
    public void SyncWindow_AnArrivingTimerIsComparedWithWhatWeHoldThen(TimerSyncStanding standing)
    {
        var (window, timers) = NewSyncWindow(RemoteChannel.Gangpath);
        using (window)
        {
            window.Fold(Offers("Mallory", RemoteChannel.Telepath, TimerSyncStanding.NotAsked, hoursLater: 3));   // builds the row
            DateTimeOffset ownKill = Killed.AddHours(6);
            timers.MarkKilled("ogre king", ownKill);

            window.Fold(Offers("Bob", RemoteChannel.Telepath, standing));
            QuietPasses();

            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.False(row.WasAutoMerged);
            Assert.False(row.WasSynced);
            Assert.Equal(ownKill, timers.KilledAt("ogre king"));
            Assert.Equal(new[] { "Mallory", "Bob" }, row.Responders.Select(c => c.Responder));
            Assert.EndsWith("changed since you asked", row.OursText);
        }
    }

    // ----- the merge window: a timer of ours that changed after asking --------

    // One player asked by telepath, and the game shows it sent. Then we kill the
    // boss ourselves, and then the player's set arrives with an older timer. Our
    // kill is not replaced: the boss is listed, ours shown as changed since asking,
    // with "keep ours" chosen. A reply that matches our kill is left alone.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SyncWindow_AKillOfOursAfterAsking_IsNotReplacedByTheAskedPlayersTimer(bool heldWhenAsked)
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            bool? closedWithWrites = null;
            window.CloseRequested += wrote => closedWithWrites = wrote;
            if (heldWhenAsked) timers.MarkKilled("ogre king", Held);
            Requests(window, 1, "bo");
            GameConfirms("Bob");
            DateTimeOffset ownKill = Killed.AddHours(6);
            timers.MarkKilled("ogre king", ownKill);

            Hears(ChatChannel.TelepathIncoming, "Bob");

            Assert.Equal(ownKill, timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.True(row.HasConflict);
            Assert.False(row.WasSynced);
            Assert.False(row.WasAutoMerged);
            Assert.True(row.KeepOursSelected);
            Assert.Equal("Keep ours", row.KeepOursLabel);
            Assert.EndsWith("changed since you asked", row.OursText);
            Assert.Equal("Bob", Assert.Single(row.Responders).Responder);
            Assert.Equal("1 responder(s): 1 to pick, 0 merged in, 0 synced, 0 already in sync.", window.Status);
            window.CancelCommand.Execute(null);
            Assert.False(closedWithWrites);
        }
    }

    // A timer of ours cleared after asking is a change of ours too: the asked
    // player's timer is not merged into its place as it arrives. It is listed.
    [Fact]
    public void SyncWindow_ATimerOfOursClearedAfterAsking_IsNotMergedOver()
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            timers.MarkKilled("ogre king", Held);
            Requests(window, 1, "bo");
            GameConfirms("Bob");
            timers.Reset("ogre king");

            Hears(ChatChannel.TelepathIncoming, "Bob");

            Assert.Null(timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.True(row.HasConflict);
            Assert.False(row.WasAutoMerged);
            Assert.Equal("Take none", row.KeepOursLabel);
            Assert.EndsWith("changed since you asked", row.OursText);
        }
    }

    // What the window itself merged in or synced is no change of ours: the asked
    // player's next set is compared with it like any timer held when asked.
    [Fact]
    public void SyncWindow_WhatTheWindowWroteItself_IsNotAChangeOfOurs()
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            Requests(window, 1, "bo");
            GameConfirms("Bob");
            Hears(ChatChannel.TelepathIncoming, "Bob");
            Assert.Equal(Killed, timers.KilledAt("ogre king"));

            Requests(window, 1, "bo");
            GameConfirms("Bob");
            Hears(ChatChannel.TelepathIncoming, "Bob", hoursLater: 3);

            Assert.Equal(Killed.AddHours(3), timers.KilledAt("ogre king"));
            Assert.True(Assert.Single(window.Rows).WasSynced);
        }
    }

    // The same where several can answer. A kill of ours made while the replies are
    // being gathered is what we hold when they are judged: replies that differ from
    // it are listed, and it stands. A timer of ours cleared after asking is not
    // merged over either, however well the replies agree.
    [Theory]
    [MemberData(nameof(WhereSeveralCanAnswer))]
    public void SyncWindow_Gathered_ATimerOfOursThatChangedAfterAsking_IsNeverWrittenOver(int button, ChatChannel answeredOn)
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            AsksSeveral(window, button);
            Hears(answeredOn, "Bob");
            DateTimeOffset ownKill = Killed.AddHours(6);
            timers.MarkKilled("ogre king", ownKill);
            Hears(answeredOn, "Sue", hoursLater: 20.0 / 3600);
            QuietPasses();

            Assert.Equal(ownKill, timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.False(row.WasAutoMerged);
            Assert.Equal("Keep ours", row.KeepOursLabel);
            Assert.EndsWith("changed since you asked", row.OursText);
            Assert.Equal(new[] { "Bob", "Sue" }, row.Responders.Select(c => c.Responder));

            // Cleared: replies that agree are still not merged into its place.
            timers.Reset("ogre king");
            Hears(answeredOn, "Ann");
            QuietPasses();
            Assert.Null(timers.KilledAt("ogre king"));
            Assert.Equal(3, row.Responders.Count);
        }
    }

    // A stranger cannot become the one asked. The user types letters that fit two
    // players, which reaches nobody; inside the same quarter minute the client
    // telepaths a player whose name those letters begin (a refusal of her own
    // remote command), and the game confirms that telepath. She was not asked:
    // her timers are listed, never merged in or synced.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SyncWindow_AClientTelepathToAPlayerTheTypedLettersBegin_DoesNotMakeHerTheOneAsked(bool weHoldOne)
    {
        var (window, timers) = NewSyncWindow(askedOn: RemoteChannel.Telepath, byUser: false);
        using (window)
        {
            DateTimeOffset? ours = weHoldOne ? Held : null;
            if (weHoldOne) timers.MarkKilled("ogre king", Held);
            UserTypes("/b @timer sync");
            BoardShows("Please be more specific.  You could have meant any of these:");
            ClientSends("/Betty {command invalid or not allowed}");
            GameConfirms("Betty");

            Hears(ChatChannel.TelepathIncoming, "Betty");

            Assert.Equal(ours, timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.False(row.WasAutoMerged);
            Assert.False(row.WasSynced);
            Assert.Equal("Betty", Assert.Single(row.Responders).Responder);
            Assert.Equal("1 responder(s): 1 to pick, 0 merged in, 0 synced, 0 already in sync.", window.Status);
        }
    }

    // A request that was not sent asks nobody. The window's request goes to letters
    // that fit two players (the game lists them), or the game refuses it on every
    // try; the user then telepaths that player something else by the same letters
    // or fewer, and the game confirms it. That player was not asked: the timers
    // they send are listed, never merged in or synced.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SyncWindow_ARequestThatWasNotSent_ThenAnotherTelepathToThatPlayer_AsksNobody(
        bool refusedOnEveryTry, bool weHoldOne)
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            DateTimeOffset? ours = weHoldOne ? Held : null;
            if (weHoldOne) timers.MarkKilled("ogre king", Held);
            if (refusedOnEveryTry)
            {
                Requests(window, 1, "Bob");
                for (int i = 0; i < TelepathPacer.MaxAttempts; i++) BoardShows("--- Telepath Not Sent ---");
                _syncChat.TelepathGivenUp("/Bob @timer sync");   // the pacer, out of tries
            }
            else
            {
                Requests(window, 1, "bo");
                BoardShows("Please be more specific.  You could have meant any of these:");
                BoardShows("-- Bob");
                BoardShows("-- Bobby");
            }
            UserTypes(refusedOnEveryTry ? "/Bob hi" : "/bo hi");
            GameConfirms("Bob");

            Hears(ChatChannel.TelepathIncoming, "Bob");

            Assert.Equal(ours, timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.False(row.WasAutoMerged);
            Assert.False(row.WasSynced);
            Assert.Equal("Bob", Assert.Single(row.Responders).Responder);
            Assert.Equal("1 responder(s): 1 to pick, 0 merged in, 0 synced, 0 already in sync.", window.Status);
        }
    }

    // Nor can a player with Execute commands choose who is. The window's request
    // goes to letters that fit two players and reaches nobody; she then has the
    // client telepath one of them (`@do /bel hi`), by more letters or by the full
    // name, and the game confirms that telepath. The confirmation is that send's,
    // not the request's: the player it names was not asked, and the timers she
    // then sends are listed, never merged in or synced.
    [Theory]
    [InlineData("/bel hi", true)]
    [InlineData("/Bella hi", true)]
    [InlineData("/bel hi", false)]
    [InlineData("/Bella hi", false)]
    public void SyncWindow_ATelepathSentForAnotherPlayerAfterARequestThatReachedNobody_AsksNobody(
        string sentForHer, bool weHoldOne)
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            DateTimeOffset? ours = weHoldOne ? Held : null;
            if (weHoldOne) timers.MarkKilled("ogre king", Held);
            Requests(window, 1, "b");
            Assert.Equal("/b @timer sync", _syncSent.Single());
            BoardShows("Please be more specific.  You could have meant any of these:");
            ClientSends(sentForHer);
            BoardShows(sentForHer);
            GameConfirms("Bella");

            Hears(ChatChannel.TelepathIncoming, "Bella");

            Assert.Equal(ours, timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.False(row.WasAutoMerged);
            Assert.False(row.WasSynced);
            Assert.True(row.KeepOursSelected);
            Assert.Equal("Bella", Assert.Single(row.Responders).Responder);
            Assert.Equal("1 responder(s): 1 to pick, 0 merged in, 0 synced, 0 already in sync.", window.Status);
        }
    }

    // A shortened request that really reached its player, with a telepath of the
    // client's to the same player going out around it, in either order: the player
    // is the one asked, once, and their timer is merged in.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SyncWindow_AShortenedRequestWithAClientTelepathToTheSamePlayer_StillAsksThatPlayer(bool requestFirst)
    {
        var (window, timers) = NewSyncWindow(askedOn: RemoteChannel.Telepath, byUser: false);
        using (window)
        {
            if (requestFirst) UserTypes("/bo @timer sync");
            ClientSends("/Bob @health");
            if (!requestFirst) UserTypes("/bo @timer sync");
            GameConfirms("Bob");
            GameConfirms("Bob");

            Hears(ChatChannel.TelepathIncoming, "Bob");

            Assert.Equal(Killed, timers.KilledAt("ogre king"));
            Assert.Empty(_syncTimers);
            Assert.Equal("1 responder(s): 0 to pick, 1 merged in, 0 synced, 0 already in sync.", window.Status);
        }
    }

    // ----- the merge window: a boss that was not on our list -----------------

    private static BossTimerSyncResponse OffersBoth(string sender, RemoteChannel heardOn) => new(sender, new[]
    {
        new BossTimerSyncRecord(50, null, Killed),
        new BossTimerSyncRecord(51, "guardian golem", Killed),
    }, heardOn, TimerSyncStanding.OneOfSeveral);

    private static BossTimerSyncRowViewModel RowOf(BossTimerSyncViewModel window, string boss) =>
        window.Rows.Single(r => r.BossName == boss);

    // A boss we do not track always asks, whoever offers it. Its pick is spent once
    // it is written: the boss is on the list now and the row shows the timer as
    // ours, so a second Apply does not write the offered time again over a kill
    // made since.
    [Fact]
    public void SyncWindow_AnAdoptedBossIsNotWrittenAgainByASecondApply()
    {
        var (window, timers) = NewSyncWindow(RemoteChannel.Gangpath);
        using (window)
        {
            int closes = 0;
            window.CloseRequested += _ => closes++;
            timers.MarkKilled("ogre king", Held);
            window.Fold(OffersBoth("Bob", RemoteChannel.Gangpath));
            Assert.Null(timers.KilledAt("guardian golem"));
            foreach (BossTimerSyncRowViewModel row in window.Rows) row.Responders[0].AdoptCommand.Execute(null);

            // Our own kill of the tracked boss keeps the window open after Apply.
            timers.MarkKilled("ogre king", Killed.AddHours(6));
            window.ApplyCommand.Execute(null);

            Assert.Contains("Applied 1 timer update(s). Not written: ogre king", window.Status);
            Assert.Equal(0, closes);
            Assert.Equal(Killed, timers.KilledAt("guardian golem"));
            BossTimerSyncRowViewModel golem = RowOf(window, "guardian golem");
            Assert.Equal("guardian golem", golem.MatchName);
            Assert.Equal(Killed, golem.OursKilledAt);
            Assert.Equal("Keep ours", golem.KeepOursLabel);
            Assert.Null(golem.SelectedKilledAt);
            Assert.False(golem.Responders[0].IsSelected);

            DateTimeOffset fresh = Killed.AddHours(8);
            timers.MarkKilled("guardian golem", fresh);
            window.ApplyCommand.Execute(null);

            Assert.Equal(fresh, timers.KilledAt("guardian golem"));
            Assert.Equal(Killed.AddHours(6), timers.KilledAt("ogre king"));
            Assert.Equal("Applied 0 timer update(s).", window.Status);
            Assert.Equal(1, closes);
        }
    }

    // The same guard for a row built while the boss was off our list: we add the
    // boss and kill it while the window is open, and the pick, an older time, is
    // not written over that kill. The row and the status name the boss as our
    // list does, and a later offer for it lands on the same row.
    [Fact]
    public void SyncWindow_APickForABossAddedSinceIsNotWrittenOverOurKillOfIt()
    {
        var (window, timers) = NewSyncWindow(RemoteChannel.Gangpath);
        using (window)
        {
            int closes = 0;
            window.CloseRequested += _ => closes++;
            window.Fold(new BossTimerSyncResponse("Bob",
                new[] { new BossTimerSyncRecord(51, null, Killed) }, RemoteChannel.Gangpath, TimerSyncStanding.OneOfSeveral));
            BossTimerSyncRowViewModel golem = Assert.Single(window.Rows);
            Assert.Equal("Monster #51", golem.BossName);
            Assert.Null(golem.MatchName);

            _syncBosses.Save(_syncBosses.Resolve().Append(Boss("guardian golem", number: 51, rooms: "1/2")).ToList());
            DateTimeOffset fresh = Killed.AddHours(8);
            timers.MarkKilled("guardian golem", fresh);
            golem.Responders[0].AdoptCommand.Execute(null);
            window.ApplyCommand.Execute(null);

            Assert.Equal(fresh, timers.KilledAt("guardian golem"));
            Assert.Contains("Not written: guardian golem", window.Status);
            Assert.Equal(0, closes);
            Assert.Equal("guardian golem", golem.BossName);
            Assert.Equal("guardian golem", golem.MatchName);
            Assert.Equal(fresh, golem.OursKilledAt);
            Assert.Null(golem.SelectedKilledAt);

            window.Fold(new BossTimerSyncResponse("Sue",
                new[] { new BossTimerSyncRecord(51, "guardian golem", Killed.AddHours(3)) }, RemoteChannel.Gangpath, TimerSyncStanding.OneOfSeveral));
            Assert.Same(golem, Assert.Single(window.Rows));
            Assert.Equal(new[] { "Bob", "Sue" }, golem.Responders.Select(c => c.Responder));
        }
    }

    // Added to our list with no timer yet, the boss is a tracked one we hold none
    // for: the pick is written, as the user chose.
    [Fact]
    public void SyncWindow_APickForABossAddedSinceWithNoTimerIsWritten()
    {
        var (window, timers) = NewSyncWindow(RemoteChannel.Gangpath);
        using (window)
        {
            window.Fold(new BossTimerSyncResponse("Bob",
                new[] { new BossTimerSyncRecord(51, "guardian golem", Killed) }, RemoteChannel.Gangpath, TimerSyncStanding.OneOfSeveral));
            BossTimerSyncRowViewModel golem = Assert.Single(window.Rows);
            Assert.True(golem.HasConflict);
            _syncBosses.Save(_syncBosses.Resolve().Append(Boss("guardian golem", number: 51, rooms: "1/2")).ToList());

            golem.Responders[0].AdoptCommand.Execute(null);
            window.ApplyCommand.Execute(null);

            Assert.Equal(Killed, timers.KilledAt("guardian golem"));
        }
    }

    // A number that is no boss anywhere, with no name sent: listed, and a pick of it
    // is skipped, since there is nothing to add to the list.
    [Fact]
    public void SyncWindow_ATimerForAMonsterThatIsNoBoss_IsNeverWritten()
    {
        var (window, timers) = NewSyncWindow(RemoteChannel.Gangpath);
        using (window)
        {
            window.Fold(new BossTimerSyncResponse("Bob",
                new[] { new BossTimerSyncRecord(9999, null, Killed) }, RemoteChannel.Gangpath, TimerSyncStanding.OneOfSeveral));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.True(row.HasConflict);

            row.Responders[0].AdoptCommand.Execute(null);
            window.ApplyCommand.Execute(null);

            Assert.Equal("Applied 0 timer update(s).", window.Status);
            Assert.Single(_syncBosses.Resolve());
        }
    }

    // ----- the merge window: a second telepath request on its way --------------

    // Two players asked by telepath one after the other, the first answering
    // before the game shows the second request sent. The first answer is gathered,
    // not written; once the second is shown sent, several were asked and both
    // answers are judged together: merged in if they agree and we hold none,
    // listed otherwise.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void SyncWindow_TheFirstOfTwoTelepathedAnswersBeforeTheSecondIsShownSent_IsGathered(bool agree, bool weHoldOne)
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            DateTimeOffset? ours = weHoldOne ? Held : null;
            if (weHoldOne) timers.MarkKilled("ogre king", Held);
            Requests(window, 1, "Bob");
            Requests(window, 1, "Sue");
            GameConfirms("Bob");
            Hears(ChatChannel.TelepathIncoming, "Bob");

            Assert.Equal(ours, timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.True(row.HasConflict);
            Assert.Contains("1 waiting for the game to show your other telepath request sent", window.Status);

            GameConfirms("Sue");
            Hears(ChatChannel.TelepathIncoming, "Sue", hoursLater: agree ? 20.0 / 3600 : 3);
            Assert.Equal(ours, timers.KilledAt("ogre king"));
            QuietPasses();

            bool mergedIn = agree && !weHoldOne;
            Assert.Equal(mergedIn ? Killed : ours, timers.KilledAt("ogre king"));
            Assert.Equal(mergedIn, row.WasAutoMerged);
            Assert.False(row.WasSynced);
            Assert.Equal(mergedIn ? Array.Empty<string>() : new[] { "Bob", "Sue" }, row.Responders.Select(c => c.Responder));
        }
    }

    // The second request reaches nobody (refused on every try): the first player
    // is still the only one asked, and their answer is written then, merged in or
    // synced, as it would have been on arrival.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SyncWindow_ASecondTelepathThatReachesNobody_LeavesTheFirstPlayerTheOneAsked(bool weHoldOne)
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            if (weHoldOne) timers.MarkKilled("ogre king", Held);
            Requests(window, 1, "Bob");
            Requests(window, 1, "Sue");
            GameConfirms("Bob");
            Hears(ChatChannel.TelepathIncoming, "Bob");
            Assert.NotEqual(Killed, timers.KilledAt("ogre king"));

            for (int i = 0; i < TelepathPacer.MaxAttempts; i++) BoardShows("--- Telepath Not Sent ---");
            _syncChat.TelepathGivenUp("/Sue @timer sync");   // the pacer, out of tries

            Assert.Equal(Killed, timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.Equal(weHoldOne, row.WasSynced);
            Assert.Equal(!weHoldOne, row.WasAutoMerged);
            Assert.Empty(row.Responders);
            Assert.Equal($"1 responder(s): 0 to pick, {(weHoldOne ? 0 : 1)} merged in, {(weHoldOne ? 1 : 0)} synced, 0 already in sync.", window.Status);
        }
    }

    // A second request the game never answers is waited on only as long as a
    // confirmation could still be paired with it.
    [Fact]
    public void SyncWindow_ASecondTelepathTheGameNeverAnswers_IsWaitedOnOnlyAsLongAsAConfirmationCanCome()
    {
        DateTimeOffset clock = DateTimeOffset.Now;
        _syncClock = () => clock;
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            Requests(window, 1, "Bob");
            Requests(window, 1, "Sue");
            GameConfirms("Bob");
            Hears(ChatChannel.TelepathIncoming, "Bob");

            QuietPasses();
            Assert.Null(timers.KilledAt("ogre king"));

            clock += TimeSpan.FromSeconds(16);
            QuietPasses();
            Assert.Equal(Killed, timers.KilledAt("ogre king"));
            Assert.True(Assert.Single(window.Rows).WasAutoMerged);
        }
    }

    // Two telepath requests sent together (a macro's `/Bob …;/Sue …`): the window
    // the first one's confirmation opens never saw the second go out. Still
    // unconfirmed, it holds the first player's answer back all the same, and once
    // the game shows it sent both players are asked and nothing is written.
    [Fact]
    public void SyncWindow_OpenedByTheFirstOfTwoTelepathsSentTogether_HoldsTheFirstAnswerBack()
    {
        var (window, timers) = NewSyncWindow(askedOn: RemoteChannel.Telepath, to: "Bob", beforeOpen: _ =>
        {
            UserPressesEnter("/Bob @timer sync");
            UserPressesEnter("/Sue @timer sync");
            GameConfirms("Bob");
        });
        using (window)
        {
            Hears(ChatChannel.TelepathIncoming, "Bob");

            Assert.Null(timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.True(row.HasConflict);
            Assert.Contains("1 waiting for the game to show your other telepath request sent", window.Status);
            Assert.Contains(_syncLog.Snapshot(), e =>
                e.Message.EndsWith("1 gathered until the game shows the user's other telepath request sent or not"));

            GameConfirms("Sue");
            Assert.Null(timers.KilledAt("ogre king"));
            Assert.Contains(row.Responders, c => c.Responder == "Bob");
        }
    }

    // ----- the merge window: a pick applied with the window left open ---------

    // A pick applied is the user's own doing, like what the window merges in or
    // syncs: with the window left open (another pick not written), that boss does
    // not read "changed since you asked", a reply matching the pick is left alone,
    // and the one player asked can still sync it. A kill of ours after the pick is
    // a change: the next differing reply is listed, and our kill stands.
    [Fact]
    public void SyncWindow_APickAppliedWithTheWindowLeftOpen_IsNoChangeOfOurs()
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            int closes = 0;
            window.CloseRequested += _ => closes++;
            timers.MarkKilled("ogre king", Held);
            Requests(window, 1, "bo");
            GameConfirms("Bob");
            // A player not asked offers both bosses: each is a pick.
            window.Fold(new BossTimerSyncResponse("Mallory", new[]
            {
                new BossTimerSyncRecord(50, null, Killed.AddHours(3)),
                new BossTimerSyncRecord(51, "guardian golem", Killed),
            }, RemoteChannel.Telepath, TimerSyncStanding.NotAsked));
            foreach (BossTimerSyncRowViewModel picked in window.Rows) picked.Responders[0].AdoptCommand.Execute(null);
            timers.MarkKilled("ogre king", Killed.AddHours(6));   // keeps the window open after Apply
            window.ApplyCommand.Execute(null);

            Assert.Equal(0, closes);
            Assert.Equal(Killed, timers.KilledAt("guardian golem"));
            BossTimerSyncRowViewModel golem = RowOf(window, "guardian golem");
            Assert.DoesNotContain("changed since you asked", golem.OursText);

            BossTimerSyncResponse BobOffers(double hoursLater) => new("Bob",
                new[] { new BossTimerSyncRecord(51, "guardian golem", Killed.AddHours(hoursLater)) },
                RemoteChannel.Telepath, TimerSyncStanding.TheOneAsked);
            window.Fold(BobOffers(20.0 / 3600));
            Assert.Equal(Killed, timers.KilledAt("guardian golem"));
            Assert.DoesNotContain(golem.Responders, c => c.Responder == "Bob");
            window.Fold(BobOffers(2));
            Assert.Equal(Killed.AddHours(2), timers.KilledAt("guardian golem"));
            Assert.True(golem.WasSynced);

            DateTimeOffset ownKill = Killed.AddHours(8);
            timers.MarkKilled("guardian golem", ownKill);
            window.Fold(BobOffers(1));
            Assert.Equal(ownKill, timers.KilledAt("guardian golem"));
            Assert.Contains(golem.Responders, c => c.Responder == "Bob");
            Assert.EndsWith("changed since you asked", golem.OursText);
        }
    }

    // ----- the merge window: what we held when the user pressed Enter ----------

    // A request typed at the terminal into a window opened from the Bosses tab,
    // before anything was asked in it, is the window's own: no second window
    // opens, and its answers are collected and judged as the button's would be.
    [Fact]
    public void SyncWindow_ARequestTypedIntoAWindowWhereNothingWasAskedYet_IsCollected()
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            UserTypes("bg @timer sync");
            SeesUsAsk(ChatChannel.Gangpath);
            Assert.StartsWith("Collecting responses to the @timer sync you typed", window.Status);

            Hears(ChatChannel.Gangpath, "Ann");
            QuietPasses();

            Assert.Equal(Killed, timers.KilledAt("ogre king"));
            Assert.True(Assert.Single(window.Rows).WasAutoMerged);
        }
    }

    // What we held when asked is what we held as the user pressed Enter, not when
    // the game showed the request: a kill of ours in between is a change after
    // asking. A telepath's answer does not sync over it, and agreeing replies on
    // the gang are not merged over a timer we cleared in between.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SyncWindow_AChangeOfOursBetweenEnterAndTheEcho_IsAChangeAfterAsking(bool byTelepath)
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            if (byTelepath)
            {
                UserPressesEnter("/bo @timer sync");
                DateTimeOffset ownKill = Killed.AddHours(6);
                timers.MarkKilled("ogre king", ownKill);
                BoardShows("/bo @timer sync");
                GameConfirms("Bob");
                Hears(ChatChannel.TelepathIncoming, "Bob");

                Assert.Equal(ownKill, timers.KilledAt("ogre king"));
            }
            else
            {
                timers.MarkKilled("ogre king", Held);
                UserPressesEnter("bg @timer sync");
                timers.Reset("ogre king");
                SeesUsAsk(ChatChannel.Gangpath);
                Hears(ChatChannel.Gangpath, "Ann");
                Hears(ChatChannel.Gangpath, "Sue", hoursLater: 20.0 / 3600);
                QuietPasses();

                Assert.Null(timers.KilledAt("ogre king"));
            }
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.True(row.HasConflict);
            Assert.False(row.WasSynced);
            Assert.False(row.WasAutoMerged);
            Assert.EndsWith("changed since you asked", row.OursText);
        }
    }

    // The same for a window the request's echo opened: it is handed our kill
    // times as the user pressed Enter, so a kill of ours before the game showed
    // the telepath sent is a change after asking, and the one player's answer
    // does not sync over it. Nothing gathered, the log line says nothing of it.
    [Fact]
    public void SyncWindow_OpenedByTheEcho_CountsAKillBeforeTheEchoAsAChangeAfterAsking()
    {
        DateTimeOffset ownKill = Killed.AddHours(6);
        var (window, timers) = NewSyncWindow(askedOn: RemoteChannel.Telepath, to: "Bob", heldWhenAsked: Held,
            killTimesWhenAsked: (bossList, store) => BossTimerSyncViewModel.KillTimesNow(bossList, store, RealmType.ParaMud),
            beforeOpen: store =>
            {
                UserPressesEnter("/bo @timer sync");
                store.MarkKilled("ogre king", ownKill);
                GameConfirms("Bob");
            });
        using (window)
        {
            Hears(ChatChannel.TelepathIncoming, "Bob");

            Assert.Equal(ownKill, timers.KilledAt("ogre king"));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.True(row.HasConflict);
            Assert.False(row.WasSynced);
            Assert.EndsWith("changed since you asked", row.OursText);
            Assert.Contains(_syncLog.Snapshot(), e => e.Message.EndsWith("1 to pick, 0 gathered"));
        }
    }

    // A typed broadcast request with the broadcast channel switched off starts no
    // collecting in a window where nothing was asked yet: every answer there
    // would be passed over.
    [Fact]
    public void SyncWindow_ABroadcastRequestTypedWithTheChannelOff_IsNotCollected()
    {
        var (window, _) = NewSyncWindow(askedOn: null, broadcastOff: true);
        using (window)
        {
            UserTypes("br @timer sync");
            SeesUsAsk(ChatChannel.Broadcast);

            Assert.False(window.Requested);
            Assert.Equal("Pick a channel and request timers.", window.Status);
        }
    }

    // ----- the merge window: a timer of ours that has run out ------------------

    // A pick is written over a timer of ours that merely ran out while the window
    // was open: running out is no change of ours. The row is shown holding none.
    [Fact]
    public void SyncWindow_APickAgainstATimerOfOursThatRanOutSince_IsWritten()
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            int closes = 0;
            window.CloseRequested += _ => closes++;
            timers.MarkKilled("ogre king", Held);   // nineteen hours ago: running on the 24-hour respawn
            window.Fold(Offers("Mallory", RemoteChannel.Telepath, TimerSyncStanding.NotAsked));
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            Assert.Equal(Held, row.OursKilledAt);

            // Its respawn set to twelve hours: the same kill has run out.
            BossDef shorter = Boss("ogre king", number: 50, rooms: "1/1");
            shorter.RespawnHoursOverride = 12;
            _syncBosses.Save(new List<BossDef> { shorter });
            Assert.Equal(Held, timers.KilledAt("ogre king"));
            Assert.Null(timers.StatusFor(Assert.Single(_syncBosses.ResolveForRealm(RealmType.ParaMud)), RealmType.ParaMud));
            row.Responders[0].AdoptCommand.Execute(null);
            window.ApplyCommand.Execute(null);

            Assert.Equal(Killed, timers.KilledAt("ogre king"));
            Assert.Equal(1, closes);
        }
    }

    // An expired timer counts as holding none (user, 2026-10-17): the row offers
    // "Take none" and says ours ran out, and a reply is merged in as for a boss we
    // hold no timer for: from the one player asked as it arrives, and where
    // several can answer once every reply agrees. Nothing is listed for a pick.
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void SyncWindow_AnExpiredTimerOfOurs_CountsAsNone(int button)
    {
        var (window, timers) = NewSyncWindow(askedOn: null);
        using (window)
        {
            timers.MarkKilled("ogre king", Killed.AddHours(-30));   // ran out on its 24-hour respawn
            Requests(window, button, "Bob");
            if (button == 1) GameConfirms("Bob");
            Hears(button == 1 ? ChatChannel.TelepathIncoming : ChatChannel.Gangpath, "Bob");
            BossTimerSyncRowViewModel row = Assert.Single(window.Rows);
            if (button == 0)
            {
                Assert.Equal("Take none", row.KeepOursLabel);
                Assert.Contains("yours ran out", row.OursText);
                QuietPasses();
            }

            Assert.Equal(Killed, timers.KilledAt("ogre king"));
            Assert.True(row.WasAutoMerged);
            Assert.False(row.WasSynced);
            Assert.False(row.HasConflict);
        }
    }

    // The readable report is an ordinary remote command, and with the broadcast
    // channel opened those are read there too: it is answered on the channel, once
    // a minute to each asker like everything said there.
    [Fact]
    public void TimerReport_OnTheBroadcastChannel_IsAnsweredThere_OnceAMinute()
    {
        var (engine, timers) = SetupHandler(RealmType.ParaMud, ("ogre king", 50, 24));
        timers.MarkKilled("ogre king");

        ChatLines.Hear(_routers[engine], new ChatLine(ChatChannel.Broadcast, "Bob", "@timer"));
        ChatLines.Hear(_routers[engine], new ChatLine(ChatChannel.Broadcast, "Bob", "@timer ogre"));

        Assert.StartsWith("br {ogre king - ", Encoding.Latin1.GetString(Assert.Single(_replies)));

        _engineNow += BroadcastAnswerBudget.Window;
        ChatLines.Hear(_routers[engine], new ChatLine(ChatChannel.Broadcast, "Bob", "@timer ogre"));
        Assert.Equal(2, _replies.Count);
    }

    // With the box ticked the report is not read there, like everything else.
    [Fact]
    public void TimerReport_OnTheBroadcastChannelSwitchedOff_IsNotAnswered()
    {
        var (engine, timers) = SetupHandler(RealmType.ParaMud, ("ogre king", 50, 24));
        timers.MarkKilled("ogre king");
        engine.DisableBroadcastChannel = true;

        ChatLines.Hear(_routers[engine], new ChatLine(ChatChannel.Broadcast, "Bob", "@timer"));

        Assert.Empty(_replies);
    }

    [Fact]
    public void Timer_NoneActive_ReportsEmptySet()
    {
        var (engine, _) = SetupHandler(RealmType.ParaMud, ("ogre king", 50, 24));
        ChatLines.Hear(_routers[engine], Telepath("@timer"));
        Assert.Equal("no boss timers active", Reply());
    }

    [Fact]
    public void Timer_NoArg_ListsActiveBossWithFullAndNext()
    {
        var (engine, timers) = SetupHandler(RealmType.ParaMud, ("ogre king", 50, 24));
        timers.MarkKilled("ogre king");
        ChatLines.Hear(_routers[engine], Telepath("@timer"));

        string reply = Reply();
        Assert.Contains("ogre king", reply);
        Assert.Contains("full", reply);
        Assert.Contains("-20%", reply);   // freshly killed → earliest paradigm window
    }

    [Fact]
    public void Timer_SubstringFilter_MatchesByName()
    {
        var (engine, timers) = SetupHandler(RealmType.ParaMud, ("crimson mist", 10, 12), ("ogre king", 50, 24));
        timers.MarkKilled("crimson mist");
        timers.MarkKilled("ogre king");

        ChatLines.Hear(_routers[engine], Telepath("@timer crimson"));
        string reply = Reply();
        Assert.Contains("crimson mist", reply);
        Assert.DoesNotContain("ogre king", reply);
    }

    [Fact]
    public void Timer_QueryWeDoNotHold_ReportsExpired()
    {
        var (engine, _) = SetupHandler(RealmType.ParaMud, ("crimson mist", 10, 12));
        ChatLines.Hear(_routers[engine], Telepath("@timer crimson"));
        Assert.Equal("expired", Reply());
    }

    [Fact]
    public void Timer_MultiMatch_RepliesOnePerLine()
    {
        var (engine, timers) = SetupHandler(RealmType.ParaMud,
            ("great green dragon", 10, 12), ("huge black dragon", 20, 24), ("ogre king", 50, 24));
        timers.MarkKilled("great green dragon");
        timers.MarkKilled("huge black dragon");
        timers.MarkKilled("ogre king");

        ChatLines.Hear(_routers[engine], Telepath("@timer dragon"));

        List<string> replies = Replies();
        Assert.Equal(2, replies.Count);   // two dragons, two lines — ogre king excluded
        Assert.Contains(replies, r => r.Contains("great green dragon"));
        Assert.Contains(replies, r => r.Contains("huge black dragon"));
        Assert.DoesNotContain(replies, r => r.Contains("ogre king"));
    }

    [Fact]
    public void Timer_MoreThanFiveMatches_CapsWithKeywordOverflow()
    {
        var (engine, timers) = SetupHandler(RealmType.ParaMud,
            ("red dragon", 1, 12), ("blue dragon", 2, 12), ("green dragon", 3, 12),
            ("black dragon", 4, 12), ("white dragon", 5, 12), ("gold dragon", 6, 12));
        foreach (string n in new[] { "red dragon", "blue dragon", "green dragon", "black dragon", "white dragon", "gold dragon" })
            timers.MarkKilled(n);

        ChatLines.Hear(_routers[engine], Telepath("@timer dragon"));

        List<string> replies = Replies();
        Assert.Equal(6, replies.Count);   // 5 timer lines + 1 overflow line
        Assert.Contains("1 more timers matching 'dragon'", replies.Last());
    }

    [Fact]
    public void FormatHours_UsesHoursMinutesStyle()
    {
        Assert.Equal("2h14m", BossTimerMath.FormatHours(2 + 14 / 60.0));
        Assert.Equal("45m", BossTimerMath.FormatHours(0.75));
        Assert.Equal("0m", BossTimerMath.FormatHours(0));
    }

    // ----- mark-time + display-order helpers ---------------------------------

    [Fact]
    public void MarkKilled_WithExplicitTime_StoresThatInstant()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (_, timers, _) = NewStores();

        var when = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        timers.MarkKilled("ogre king", when);
        Assert.Equal(when, timers.KilledAt("ogre king"));
    }

    [Fact]
    public void EarlyFractionsAndLabels_AreRealmSpecific()
    {
        Assert.Equal(new[] { 0.95, 0.90, 0.80 }, BossTimerMath.EarlyFractionsInDisplayOrder(RealmType.ParaMud));
        Assert.Equal(new[] { 0.875 }, BossTimerMath.EarlyFractionsInDisplayOrder(RealmType.Stock));
        Assert.Equal(new[] { "-5%", "-10%", "-20%" }, BossTimerMath.EarlyColumnLabels(RealmType.ParaMud));
        Assert.Equal(new[] { "87.5%" }, BossTimerMath.EarlyColumnLabels(RealmType.Stock));
    }

    // ----- row VM live columns (blank-when-expired) --------------------------

    private BossRowViewModel Row(BossTimerStore timers, RealmType realm, int hours)
    {
        var def = Boss("ogre king", number: 50, rooms: "3/300");
        return new BossRowViewModel(def, realm, hours, timers, () => { }, _ => { });
    }

    [Fact]
    public void Row_EarlyWindow_BlanksOncePassed_WhileFullStillCounts()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (_, timers, _) = NewStores();
        BossRowViewModel row = Row(timers, RealmType.ParaMud, 24);

        // Killed 20h ago on a 24h timer: 20% window (19.2h) has passed; 10% (21.6h)
        // and 5% (22.8h) are still counting, as is 100% (24h).
        timers.MarkKilled("ogre king", DateTimeOffset.UtcNow - TimeSpan.FromHours(20));
        row.RefreshStatus();

        Assert.True(row.IsActive);
        Assert.NotEqual(string.Empty, row.StatusDisplay);
        Assert.NotEqual(string.Empty, row.Early1Display);   // 5% — pending
        Assert.NotEqual(string.Empty, row.Early2Display);   // 10% — pending
        Assert.Equal(string.Empty, row.Early3Display);      // 20% — passed
    }

    [Fact]
    public void Row_NearFull_AllEarlyWindowsBlank_FullStillCounts()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (_, timers, _) = NewStores();
        BossRowViewModel row = Row(timers, RealmType.ParaMud, 24);

        // 23h elapsed: every early window (19.2 / 21.6 / 22.8h) has passed; only the
        // 100% guaranteed spawn is still counting.
        timers.MarkKilled("ogre king", DateTimeOffset.UtcNow - TimeSpan.FromHours(23));
        row.RefreshStatus();

        Assert.True(row.IsActive);
        Assert.NotEqual(string.Empty, row.StatusDisplay);
        Assert.Equal(string.Empty, row.Early1Display);
        Assert.Equal(string.Empty, row.Early2Display);
        Assert.Equal(string.Empty, row.Early3Display);
    }

    [Fact]
    public void Row_PastFullTimer_IsInactive_AllBlank()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (_, timers, _) = NewStores();
        BossRowViewModel row = Row(timers, RealmType.ParaMud, 24);

        timers.MarkKilled("ogre king", DateTimeOffset.UtcNow - TimeSpan.FromHours(25));
        row.RefreshStatus();

        Assert.False(row.IsActive);
        Assert.Equal(string.Empty, row.StatusDisplay);
        Assert.Equal(long.MaxValue, row.FullSortKey);
    }

    // ----- Mark + Manage dialogs (VM logic) ----------------------------------

    [Fact]
    public void MarkDialog_Ok_ReturnsCombinedDateAndTime()
    {
        var vm = new MarkTimerDialogViewModel("ogre king", new DateTimeOffset(2026, 8, 1, 10, 30, 0, TimeSpan.Zero));
        DateTimeOffset? got = null;
        bool fired = false;
        vm.CloseRequested += r => { got = r; fired = true; };

        vm.OkCommand.Execute(null);

        Assert.True(fired);
        Assert.NotNull(got);
        Assert.Equal(2026, got!.Value.Year);
        Assert.Equal(10, got.Value.Hour);
        Assert.Equal(30, got.Value.Minute);
    }

    [Fact]
    public void MarkDialog_Cancel_ReturnsNull()
    {
        var vm = new MarkTimerDialogViewModel("ogre king", DateTimeOffset.Now);
        bool fired = false;
        DateTimeOffset? got = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        vm.CloseRequested += r => { got = r; fired = true; };

        vm.CancelCommand.Execute(null);

        Assert.True(fired);
        Assert.Null(got);
    }

    [Fact]
    public void ManageDialog_Add_Save_PersistsNewBoss()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (bosses, _, cache) = NewStores();
        var vm = new ManageBossesDialogViewModel(bosses, cache);

        Assert.Single(vm.Rows);   // ogre king loaded for the realm
        vm.AddRowCommand.Execute(null);
        ManageBossRowViewModel added = vm.Rows.Cast<ManageBossRowViewModel>().Last();
        added.Name = "new boss";
        added.Rooms = "1/100";
        added.InParadigm = true;

        bool? closed = null;
        vm.CloseRequested += r => closed = r;
        vm.SaveCommand.Execute(null);

        Assert.True(closed);
        Assert.Contains(bosses.Resolve(), b => b.Name == "new boss");
    }

    [Fact]
    public void ManageDialog_Remove_Save_DropsBoss()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1), ("chimera", 60, 12, 1));
        SeedBosses(
            Boss("ogre king", number: 50, rooms: "3/300"),
            Boss("chimera", number: 60, rooms: "3/583"));
        var (bosses, _, cache) = NewStores();
        var vm = new ManageBossesDialogViewModel(bosses, cache);

        ManageBossRowViewModel drop = vm.Rows.Cast<ManageBossRowViewModel>().First(r => r.Name == "chimera");
        vm.RemoveRowCommand.Execute(drop);
        vm.SaveCommand.Execute(null);

        Assert.DoesNotContain(bosses.Resolve(), b => b.Name == "chimera");
        Assert.Contains(bosses.Resolve(), b => b.Name == "ogre king");
    }

    [Fact]
    public void ManageDialog_Cancel_ReturnsFalse_NoWrite()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (bosses, _, cache) = NewStores();
        var vm = new ManageBossesDialogViewModel(bosses, cache);

        vm.AddRowCommand.Execute(null);
        vm.Rows.Cast<ManageBossRowViewModel>().Last().Name = "should not persist";
        bool? closed = null;
        vm.CloseRequested += r => closed = r;
        vm.CancelCommand.Execute(null);

        Assert.False(closed);
        Assert.DoesNotContain(bosses.Resolve(), b => b.Name == "should not persist");
    }

    [Fact]
    public void ManageDialog_RespawnOverride_FillsInWhenGameDataHasNoTimer()
    {
        // "ghost king" has no game-data monster, so the tab shows "?" and StatusFor
        // can't resolve a timer — until a manual respawn override is entered.
        SeedGameData(RealmType.ParaMud, ("some mob", 1, 0, 0));
        SeedBosses(Boss("ghost king", number: 999, rooms: "1/1"));
        var (bosses, timers, cache) = NewStores();
        timers.MarkKilled("ghost king");

        BossDef before = bosses.ResolveForRealm(RealmType.ParaMud).First(b => b.Name == "ghost king");
        Assert.Null(timers.StatusFor(before, RealmType.ParaMud));   // no timer resolvable

        var vm = new ManageBossesDialogViewModel(bosses, cache);
        vm.Rows.Cast<ManageBossRowViewModel>().First(r => r.Name == "ghost king").RespawnHoursText = "10";
        vm.SaveCommand.Execute(null);

        BossDef after = bosses.ResolveForRealm(RealmType.ParaMud).First(b => b.Name == "ghost king");
        Assert.Equal(10, after.RespawnHoursOverride);
        Assert.NotNull(timers.StatusFor(after, RealmType.ParaMud));   // override drives the timer
    }

    [Fact]
    public void ManageDialog_ShowInTable_Unchecked_Persists_ButBossStillResolves()
    {
        SeedGameData(RealmType.ParaMud, ("ogre king", 50, 24, 1));
        SeedBosses(Boss("ogre king", number: 50, rooms: "3/300"));
        var (bosses, _, cache) = NewStores();
        var vm = new ManageBossesDialogViewModel(bosses, cache);

        vm.Rows.Cast<ManageBossRowViewModel>().First(r => r.Name == "ogre king").ShowInTable = false;
        vm.SaveCommand.Execute(null);

        // Hidden from the tab, but still present in the store (tracked for @timer etc).
        BossDef def = bosses.ResolveForRealm(RealmType.ParaMud).First(b => b.Name == "ogre king");
        Assert.False(def.ShowInTable);
    }

    // ----- cleanup bosses (DEAD / ALIVE) -------------------------------------

    [Fact]
    public void NextCleanup_IsFirstCleanupAfterKill()
    {
        var tz = TimeZoneInfo.Utc;
        var cleanup = TimeSpan.FromHours(21);

        // Killed at 20:00 → today's 21:00.
        Assert.Equal(new DateTimeOffset(2026, 8, 5, 21, 0, 0, TimeSpan.Zero),
            BossTimerMath.NextCleanup(new DateTimeOffset(2026, 8, 5, 20, 0, 0, TimeSpan.Zero), cleanup, tz));
        // Killed at 22:00 (after cleanup) → tomorrow's 21:00.
        Assert.Equal(new DateTimeOffset(2026, 8, 6, 21, 0, 0, TimeSpan.Zero),
            BossTimerMath.NextCleanup(new DateTimeOffset(2026, 8, 5, 22, 0, 0, TimeSpan.Zero), cleanup, tz));
    }

    private (BossStore bosses, BossTimerStore timers) CleanupStores()
    {
        SeedGameData(RealmType.ParaMud, ("lord feyr", 60, 24, 1));
        SeedBosses(Boss("lord feyr", number: 60, type: BossRespawnType.Cleanup, rooms: "17/2718"));
        var (bosses, timers, _) = NewStores();
        timers.SetCleanupConfig(() => new BossCleanupConfig(TimeSpan.FromHours(21), TimeZoneInfo.Utc));
        return (bosses, timers);
    }

    [Fact]
    public void Cleanup_MarkedNow_IsDead_MarkedLongAgo_IsAlive()
    {
        var (_, timers) = CleanupStores();

        // Just marked: the next cleanup is always in the future → DEAD.
        timers.MarkKilled("lord feyr", DateTimeOffset.UtcNow);
        Assert.True(timers.IsCleanupDead("lord feyr"));

        // Marked over a day ago: the cleanup that clears it has passed → ALIVE.
        timers.MarkKilled("lord feyr", DateTimeOffset.UtcNow - TimeSpan.FromHours(25));
        Assert.False(timers.IsCleanupDead("lord feyr"));
    }

    [Fact]
    public void Cleanup_NoConfig_StaysDeadUntilCleared()
    {
        SeedGameData(RealmType.ParaMud, ("lord feyr", 60, 24, 1));
        SeedBosses(Boss("lord feyr", number: 60, type: BossRespawnType.Cleanup, rooms: "17/2718"));
        var (_, timers, _) = NewStores();   // no cleanup config set

        timers.MarkKilled("lord feyr", DateTimeOffset.UtcNow - TimeSpan.FromDays(3));
        Assert.True(timers.IsCleanupDead("lord feyr"));   // can't compute a flip → stays dead
    }

    [Fact]
    public void Row_Cleanup_ShowsAliveThenDead()
    {
        var (_, timers) = CleanupStores();
        var def = Boss("lord feyr", number: 60, type: BossRespawnType.Cleanup, rooms: "17/2718");
        var row = new BossRowViewModel(def, RealmType.ParaMud, null, timers, () => { }, _ => { });

        row.RefreshStatus();                            // unmarked
        Assert.Equal("ALIVE", row.StatusDisplay);
        Assert.False(row.IsActive);

        timers.MarkKilled("lord feyr", DateTimeOffset.UtcNow);
        row.RefreshStatus();
        Assert.Equal("DEAD", row.StatusDisplay);
        Assert.True(row.IsActive);                      // Clear button shows while dead
    }

    // The Darken Beast Lord sits in a den too dark to list what is in it, and its
    // death line isn't on record, so nothing named the kill and no timer started.
    // Its name in the fight's damage lines says it is there. It is worth 4,000,000
    // exp against 17,750 for the biggest thing that shares its den, so even split six
    // ways its kill is the only gain that size: the exp line marks it.
    private (BossTimerStore Timers, Func<int, MonsterDeathEvent> Gain) DarkDen(long bossExp = 4_000_000, params long[] others)
    {
        if (others.Length == 0) others = new long[] { 17_750, 14_750 };
        SeedGameData(RealmType.ParaMud, ("darken beast lord", 930, 15, 1));
        SeedBosses(Boss("darken beast lord", number: 930,
            rooms: new[] { "17/2812", "17/2813", "17/2814", "17/2815" }));
        var (_, timers, _) = NewStores();
        timers.SetRoomExpResolver((_, _) => (bossExp, others));
        return (timers, exp => new MonsterDeathEvent(ExperienceGained: exp, At: DateTimeOffset.UtcNow));
    }

    // It has four rooms, and the kill counts in any of them; a full party's share
    // (a sixth) is still far more than an add gives.
    [Theory]
    [InlineData(2812)]
    [InlineData(2813)]
    [InlineData(2814)]
    [InlineData(2815)]
    public void UnnamedDeathInTheDark_WithABossSizedExpGain_MarksTheBoss(int room)
    {
        var (timers, gain) = DarkDen();

        timers.OnMonsterDied(gain(666_666), new RoomKey(17, room), engagedName: null,
            recentFoes: new[] { "Darken Beast Lord", "darken beast" });

        Assert.NotNull(timers.KilledAt("darken beast lord"));
    }

    // An add dying in the same fight gives an add's exp: not the boss.
    [Fact]
    public void UnnamedDeathInTheDark_WithAnAddSizedExpGain_DoesNotMarkTheBoss()
    {
        var (timers, gain) = DarkDen();

        timers.OnMonsterDied(gain(17_750), new RoomKey(17, 2812), engagedName: null,
            recentFoes: new[] { "Darken Beast Lord", "darken beast" });

        Assert.Null(timers.KilledAt("darken beast lord"));
    }

    // The boss has to have been in the fight's lines: a big gain alone marks nothing.
    [Fact]
    public void UnnamedDeath_WithTheBossNeverNamed_MarksNothing()
    {
        var (timers, gain) = DarkDen();

        timers.OnMonsterDied(gain(4_000_000), new RoomKey(17, 2812), engagedName: null,
            recentFoes: new[] { "darken beast" });

        Assert.Null(timers.KilledAt("darken beast lord"));
    }

    // Outside its rooms the same lines mark nothing.
    [Fact]
    public void UnnamedDeath_OutsideTheBosssRooms_MarksNothing()
    {
        var (timers, gain) = DarkDen();

        timers.OnMonsterDied(gain(4_000_000), new RoomKey(17, 2811), engagedName: null,
            recentFoes: new[] { "Darken Beast Lord" });

        Assert.Null(timers.KilledAt("darken beast lord"));
    }

    // The rule fits a boss worth far less than its neighbours as well: a monkey spirit
    // pays 40,000 among monsters worth 400,000, whose smallest share is 66,666.
    [Theory]
    [InlineData(40_000, true)]      // the spirit, solo
    [InlineData(6_667, true)]       // the spirit, shared six ways
    [InlineData(400_000, false)]    // one of the others
    [InlineData(66_667, false)]     // one of the others, shared six ways
    public void UnnamedDeath_OfABossWorthLessThanItsNeighbours_IsToldByWhatItPays(int gained, bool isBoss)
    {
        var (timers, gain) = DarkDen(bossExp: 40_000, 400_000);

        timers.OnMonsterDied(gain(gained), new RoomKey(17, 2812), engagedName: null,
            recentFoes: new[] { "Darken Beast Lord", "darken beast" });

        Assert.Equal(isBoss, timers.KilledAt("darken beast lord") is not null);
    }

    // Where the boss and an add could both have paid the gain, exp can't say: then it
    // is the boss's only when nothing else was named.
    [Fact]
    public void UnnamedDeath_WhereExpCantTell_NeedsTheBossToBeTheOnlyOneNamed()
    {
        var (timers, gain) = DarkDen(bossExp: 60_000, 17_750);

        // 12,000 is a fifth of the boss, and also within what the add pays.
        timers.OnMonsterDied(gain(12_000), new RoomKey(17, 2812), engagedName: null,
            recentFoes: new[] { "Darken Beast Lord", "darken beast" });
        Assert.Null(timers.KilledAt("darken beast lord"));

        timers.OnMonsterDied(gain(12_000), new RoomKey(17, 2812), engagedName: null,
            recentFoes: new[] { "Darken Beast Lord" });
        Assert.NotNull(timers.KilledAt("darken beast lord"));
    }

    // With the dead monster named some other way, the damage lines decide nothing.
    [Fact]
    public void NamedDeathOfAnAdd_IsNotTakenForTheBoss()
    {
        var (timers, gain) = DarkDen();

        timers.OnMonsterDied(gain(4_000_000), new RoomKey(17, 2812), engagedName: "shadow wolf",
            recentFoes: new[] { "Darken Beast Lord" });

        Assert.Null(timers.KilledAt("darken beast lord"));
    }

    // ----- Bosses the game gives one name ------------------------------------

    private static BossDef Named(string name, string gameName, int number, string room) => new()
    {
        Name = name, GameName = gameName, MonsterNumber = number, Rooms = new() { room },
        InStock = true, InParadigm = true, RespawnType = BossRespawnType.Timed,
    };

    // The Nahr in the throne room and the one in the pit are two bosses with one name
    // and two timers. Read by name, both took the first Nahr record's hours.
    [Fact]
    public void TwoBossesOfOneGameName_EachReadTheirOwnRecordsTimer()
    {
        SeedGameData(RealmType.ParaMud, ("Nahr", 1004, 3, 1), ("Nahr", 1010, 10, 1), ("Nahr", 1011, 4, 1));
        SeedBosses(Named("nahr (spheres)", "nahr", 1010, "17/3087"), Named("nahr (spaceghost)", "nahr", 1011, "12/2378"));
        var (bosses, _, cache) = NewStores();

        Assert.Equal(2, bosses.Resolve().Count);
        Assert.Equal(10, BossCatalog.EffectiveRegenHours(cache, bosses.Resolve().First(b => b.Name == "nahr (spheres)")));
        Assert.Equal(4, BossCatalog.EffectiveRegenHours(cache, bosses.Resolve().First(b => b.Name == "nahr (spaceghost)")));
    }

    [Fact]
    public void TwoBossesOfOneGameName_AreToldApartByTheRoomOfTheKill()
    {
        SeedGameData(RealmType.ParaMud, ("Nahr", 1010, 10, 1), ("Nahr", 1011, 4, 1));
        SeedBosses(Named("nahr (spheres)", "nahr", 1010, "17/3087"), Named("nahr (spaceghost)", "nahr", 1011, "12/2378"));
        var (_, timers, _) = NewStores();

        timers.OnMonsterDied(Death(), new RoomKey(12, 2378), engagedName: "Nahr");

        Assert.NotNull(timers.KilledAt("nahr (spaceghost)"));
        Assert.Null(timers.KilledAt("nahr (spheres)"));
    }

    [Fact]
    public void RosterFallback_MarksTheBossOfThatRoom_WhenTwoShareAGameName()
    {
        SeedGameData(RealmType.ParaMud, ("Nahr", 1010, 10, 1), ("Nahr", 1011, 4, 1));
        SeedBosses(Named("nahr (spheres)", "nahr", 1010, "17/3087"), Named("nahr (spaceghost)", "nahr", 1011, "12/2378"));
        var (_, timers, _) = NewStores();
        RoomKey throne = new(17, 3087);
        List<string> killed = new();
        timers.BossKilled += def => killed.Add(def.Name);

        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.AlsoHere, "Nahr"), throne, "Vaulted Throne Room");
        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.AlsoHere), throne, "Vaulted Throne Room");
        timers.OnRoomDisplayed("Vaulted Throne Room");

        Assert.NotNull(timers.KilledAt("nahr (spheres)"));
        Assert.Null(timers.KilledAt("nahr (spaceghost)"));
        Assert.Equal(new[] { "nahr (spheres)" }, killed);
    }

    // A boss with one name can still have several records: the great green dragon of
    // the cavern is 15 hours, and an earlier record of the same name is 1.
    [Fact]
    public void RegenHours_AreReadFromTheBosssOwnRecord_NotTheFirstOfItsName()
    {
        SeedGameData(RealmType.ParaMud, ("great green dragon", 1038, 1, 1), ("great green dragon", 1058, 15, 1));
        var (_, _, cache) = NewStores();

        Assert.Equal(15, BossCatalog.ResolveRegenHours(cache, Boss("great green dragon", number: 1058, rooms: "1/3111")));
        Assert.Equal(1, BossCatalog.ResolveRegenHours(cache, Boss("great green dragon", number: null, rooms: "1/3111")));
    }

    // In other game data the number may be some other monster, or one that isn't a
    // boss: the name is what's left to go on.
    [Fact]
    public void RegenHours_FallBackToTheName_WhenTheNumberIsAnotherMonster()
    {
        SeedGameData(RealmType.ParaMud, ("giant rat", 792, 0, 0), ("giant roc", 571, 8, 1));
        var (_, _, cache) = NewStores();

        Assert.Equal(8, BossCatalog.ResolveRegenHours(cache, Boss("giant roc", number: 792, rooms: "10/159")));
    }

    // Malformation comes only from Lord Chisholm's death, so the two are one entry on
    // his record: the hour runs from his death, and the malformation dying afterwards
    // leaves it where it was (user, 2026-10-07).
    [Fact]
    public void BossThatDiesIntoAnother_IsTimedFromItsOwnDeath()
    {
        SeedGameData(RealmType.ParaMud, ("Lord Chisholm", 1297, 1, 1), ("malformation", 1298, 0, 1));
        SeedBosses(Named("lord chisholm (malformation)", "lord chisholm", 1297, "14/6216"));
        var (bosses, timers, cache) = NewStores();
        RoomKey chambers = new(14, 6216);

        timers.OnMonsterDied(Death(), chambers, engagedName: "Lord Chisholm");
        DateTimeOffset? atHisDeath = timers.KilledAt("lord chisholm (malformation)");
        Assert.NotNull(atHisDeath);
        Assert.Equal(1, BossCatalog.EffectiveRegenHours(cache, bosses.Resolve().Single()));

        timers.MarkKilled("lord chisholm (malformation)", DateTimeOffset.UtcNow.AddMinutes(-10));
        DateTimeOffset? before = timers.KilledAt("lord chisholm (malformation)");
        timers.OnMonsterDied(Death(), chambers, engagedName: "malformation");
        Assert.Equal(before, timers.KilledAt("lord chisholm (malformation)"));
    }

    // ----- What a boss's death summons ---------------------------------------

    private (BossTimerStore timers, List<(string Boss, string? Dead, int? Exp)> loot, List<string> kills) ChisholmsChambers()
    {
        SeedGameData(RealmType.ParaMud, ("Lord Chisholm", 1297, 1, 1), ("malformation", 1298, 0, 1));
        SeedBosses(Named("lord chisholm (malformation)", "lord chisholm", 1297, "14/6216"));
        var (_, timers, _) = NewStores();
        timers.SetDeathSummonResolver(def => def.MonsterNumber == 1297 ? new[] { "malformation" } : Array.Empty<string>());
        List<(string, string?, int?)> loot = new();
        List<string> kills = new();
        timers.BossLootDropped += (def, dead, exp) => loot.Add((def.Name, dead, exp));
        timers.BossKilled += def => kills.Add(def.Name);
        return (timers, loot, kills);
    }

    // The malformation is where Lord Chisholm's loot is. Its death is not his: it
    // starts no timer and is no boss kill, but it is when the drops land.
    [Fact]
    public void DeathOfWhatABossSummons_DropsLoot_WithoutBeingAKill()
    {
        var (timers, loot, kills) = ChisholmsChambers();
        RoomKey chambers = new(14, 6216);

        timers.OnMonsterDied(Death(), chambers, engagedName: "Lord Chisholm");
        Assert.Equal(new[] { "lord chisholm (malformation)" }, kills);
        Assert.Equal(("lord chisholm (malformation)", "Lord Chisholm", 100), Assert.Single(loot));

        timers.OnMonsterDied(Death(), chambers, engagedName: "malformation");
        Assert.Single(kills);
        Assert.Equal(2, loot.Count);
        Assert.Equal(("lord chisholm (malformation)", "malformation", 100), loot[1]);
    }

    [Fact]
    public void DeathOfWhatABossSummons_OutsideItsRooms_IsNothing()
    {
        var (timers, loot, _) = ChisholmsChambers();

        timers.OnMonsterDied(Death(), new RoomKey(14, 1), engagedName: "malformation");

        Assert.Empty(loot);
    }

    // The boss gone from its room without a death seen still drops: nothing is
    // known of the dead monster.
    [Fact]
    public void RosterFallback_DropsLoot_WithNothingKnownOfTheDeath()
    {
        var (timers, loot, _) = ChisholmsChambers();
        RoomKey chambers = new(14, 6216);

        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.AlsoHere, "Lord Chisholm"), chambers, "Lord's Chambers");
        timers.OnRoomEntitiesObserved(Roster(RoomObservationSource.AlsoHere), chambers, "Lord's Chambers");
        timers.OnRoomDisplayed("Lord's Chambers");

        Assert.Equal(("lord chisholm (malformation)", (string?)null, (int?)null), Assert.Single(loot));
    }
}
