using System.IO;
using System.Text;
using MudPlay.Game.Cash;
using MudPlay.Game.Combat;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// Pins <see cref="DeathRecoveryManager"/>'s stock corpse-recovery flow: on
/// re-entering the death room, auto-recover reads the "You notice" survey and, if
/// our "corpse of &lt;given-name&gt;" is on the floor, sends ONE
/// <c>recover corpse &lt;name&gt;</c>; the single "You have recovered the corpse
/// of &lt;name&gt;." line finalises. When the corpse is NOT in the survey the pile
/// is marked Missing (terminal) so it never spam-retries. (The <c>@comeback</c>
/// party-pickup flow is a separate concern owned by
/// <see cref="MudPlay.Game.Remote.PartyComebackManager"/>.)
/// </summary>
public sealed partial class DeathRecoveryManagerTests
{
    private sealed class GraphHarness : IDisposable
    {
        private readonly string _root;
        private readonly GameDataCache _cache;
        private readonly string _graphJson;
        private readonly MessageRouter _router;
        public ProfileService Profile { get; } = TestProfiles.Service();
        public RoomTracker Tracker { get; }
        public DeathLineWatcher Watcher { get; }
        public GroundItemTracker Ground { get; }
        public DeathRecoveryManager Recovery { get; }
        // The game's output as the client receives it, stamped on the harness clock.
        public ServerFeed Feed { get; }
        public List<string> Sent { get; } = new();
        public InventorySnapshot Snapshot { get; set; } = InventorySnapshot.Empty;
        // The clock the room tracker and recovery stamp what they see on, and the
        // harness's own lines and inventory snapshots with them: the real one, except
        // that each reading is always later than the one before. Two things a test
        // does one after the other are told apart by their order alone, however
        // coarse the system clock is, with no pause between them.
        private DateTimeOffset _clock;
        public DateTimeOffset Now()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return _clock = now > _clock ? now : _clock.AddTicks(1);
        }

        // Combat-interleave spies: Hostiles drives the hostilesPresent probe,
        // ResumeArmed counts NoteBetweenRoundCast nudges, GateHeld mirrors the
        // CorpseRecovery gate, Ac backs the ArmourClass lookup.
        public bool Hostiles { get; set; }
        public int ResumeArmed { get; private set; }
        public bool GateHeld { get; private set; }
        public Dictionary<string, int> Ac { get; } = new();
        // Realm probe: true = Paradigm (corpse), false = Stock (loose ground items).
        // Defaults Paradigm so the existing corpse tests keep their behaviour.
        public bool Paradigm { get; set; } = true;
        public void CombatRound() => Recovery.OnRecoveryCombatRound();
        public void Heartbeat() => Recovery.OnRecoveryHeartbeat();

        private const string GraphJson = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "Town Gates",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "1/3", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "North Square",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "1/1", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;

        // Stock spill-sweep wiring, as the app does it. Graph is the room graph the
        // sweep plans from. Walker (when asked for) is the real walker, its moves
        // landing in Sent beside recovery's own commands, and Controller the real
        // movement controller with the sweep listed as a solver. Movement holds and
        // the hold on sends are read off the real Coordinator's gates. OtherEngine
        // stands in for a loop / Auto-Lair / errand / following; AutoSearches and
        // Searched for auto-search; Stays and StashRooms back the last two probes.
        public RoomGraphManager Graph { get; }
        public AutoWalkManager? Walker { get; }
        public MovementController? Controller { get; }
        public MovementCoordinator Coordinator { get; } = new();
        public AvoidFilter Filter { get; } = new();
        public bool OtherEngine { get; set; }
        // Rooms an engine's own walk (a PvP flee, its come-back) has just ended in.
        public HashSet<RoomKey> EngineWalkEndedAt { get; } = new();
        public bool AutoSearches { get; set; }
        public List<RoomKey> Searched { get; } = new();
        public HashSet<string> Stays { get; } = new(StringComparer.OrdinalIgnoreCase);
        // Items that are keys by their game data.
        public HashSet<string> KeyItems { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<RoomKey> StashRooms { get; } = new();
        private readonly List<IDisposable> _engines = new();

        public GraphHarness(string characterName = "Ermias", string graphJson = GraphJson, bool withWalker = false)
        {
            _root = Path.Combine(Path.GetTempPath(), "mudplay-deathrec-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(_root, "alpha"));
            File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), graphJson);
            GameDataCache cache = new(_root);
            cache.SwitchSet("alpha");
            _cache = cache;
            _graphJson = graphJson;
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");
            Graph = graph;

            _router = new MessageRouter();
            DefaultPatterns.Seed(_router);
            LogService log = new();
            Tracker = new RoomTracker(graph, log: null, now: Now);
            Tracker.AttachInventorySnapshot(() => Snapshot);
            Watcher = new DeathLineWatcher(_router, log);
            Ground = new GroundItemTracker(_router, new CurrencyNaming());
            Recovery = new DeathRecoveryManager(Watcher, Profile, Tracker, log, now: Now);
            Feed = new ServerFeed(clock: Now);
            Recovery.AttachLineExtractor(Feed.Lines);
            Recovery.AttachGroundItems(Ground);
            Recovery.SetWireSender(b => Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
            Recovery.AttachInventorySnapshot(() => Snapshot);
            Recovery.AttachCombatInterleave(
                () => Hostiles,
                () => ResumeArmed++,
                () => GateHeld = true,
                () => GateHeld = false,
                name => Ac.TryGetValue(name, out int v) ? v : 0);
            Recovery.SetRealmProbe(() => Paradigm);
            Recovery.AttachSpillSweep(
                roomLookup: graph.GetRoom,
                movementHeld: () => Coordinator.IsPaused,
                isStashRoom: StashRooms.Contains,
                otherEngineDrives: () => OtherEngine,
                engineWalkEndedAt: EngineWalkEndedAt.Contains,
                restHeld: () => Coordinator.IsGateAsserted(MovementCoordinator.HealthRecoveryGate)
                    || Coordinator.IsGateAsserted(MovementCoordinator.ManaRecoveryGate),
                userPaused: () => Coordinator.IsGateAsserted(MovementCoordinator.UserGate),
                autoSearchesRooms: () => AutoSearches,
                noteRoomSearched: Searched.Add);
            Recovery.SetStaysOnDeathProbe(Stays.Contains, isKeyItem: KeyItems.Contains);
            // As the app wires it, after recovery's own subscription: the floor list
            // belongs to the room just left, so a genuine room change empties it.
            Tracker.StateChanged += t =>
            {
                if (t.NewRoom is null || (t.PreviousRoom is not null && t.PreviousRoom.Key.Equals(t.NewRoom.Key))) return;
                Ground.OnRoomChanged();
            };
            if (withWalker)
            {
                BfsMapper bfs = new(graph);
                Walker = new AutoWalkManager(graph, bfs, Tracker, Coordinator, Filter);
                Walker.SetWireSender(b => Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
                Recovery.AttachWalker(Walker);
                LoopRunner loops = new(Tracker, Coordinator, graph: graph, bfs: bfs);
                loops.SetWireSender(_ => { });
                LairTimerStore timers = new(cache, graph, Tracker);
                AutoLairManager autoLair = new(Walker, Tracker, graph, bfs, timers, log: null, coordinator: Coordinator);
                Controller = new MovementController(Walker, loops, autoLair, Coordinator);
                Controller.AddSolver(
                    active: () => Recovery.SpillSweepActive,
                    held: () => Recovery.SpillSweepHeld,
                    stop: Recovery.StopSpillSweep);
                Recovery.SpillSweepStateChanged += Controller.NoteSolverStateChanged;
                Controller.Stopping += Recovery.DropDeferredSweep;
                _engines.Add(Controller);
                _engines.Add(autoLair);
                _engines.Add(timers);
            }

            Profile.LoadDefaultProfile();
            Profile.Current!.Name = characterName;
            Tracker.Hydrate(Profile.Current!);
        }

        private static RoomObservation Obs(string name, params Direction[] exits)
            => new(name, new HashSet<Direction>(exits));

        // Confirm at Town Gates (1/1) — both initial landing and re-entry.
        public void EnterGates() => Tracker.NoteRoomObserved(Obs("Town Gates", Direction.N));

        // Feed a room floor survey ("You notice … here.") to the GroundItemTracker,
        // as the room display would after entry — this is what drives the corpse grab.
        public void FeedSurvey(string list) =>
            _router.Dispatch(new LineExtractor.EmittedLine(
                $"You notice {list} here.", Array.Empty<CellAttributes>(),
                Now(), IsPromptLine: false));

        public InventorySnapshot SnapWith(EquippedItem[] worn, string[] carried)
            => new(CurrencyHoldings.Empty, EncumbranceReading.Empty, worn, carried, Now());

        public DeathRecord Latest => Profile.Current!.DeathHistory![^1];

        // Make another game-data set the active one, its rooms the same as alpha's,
        // and read the graph in from it as the app does on a switch.
        public void SwitchGameData(string set)
        {
            string folder = Path.Combine(_root, set);
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "Rooms.json"), _graphJson);
            }
            _cache.SwitchSet(set);
            Graph.OnActiveSetChanged(set);
        }

        public void Dispose()
        {
            foreach (IDisposable engine in _engines) engine.Dispose();
            Recovery.Dispose();
            Ground.Dispose();
            Watcher.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static void Die(GraphHarness h, EquippedItem[] worn, string[] carried)
    {
        h.EnterGates();
        h.Snapshot = h.SnapWith(worn, carried);
        h.Tracker.NoteDeath(2, "You now have 2 lives remaining.");   // tracker → PendingRespawn
        h.Sent.Clear();
    }

    [Fact]
    public void ReEntry_AutoRecover_CorpsePresent_RecoversViaCorpseCommand()
    {
        using GraphHarness h = new();
        Die(h, new[] { new EquippedItem("rusty dagger", "Weapon Hand") }, new[] { "torch" });
        h.Recovery.AutoRecover = true;

        h.FeedSurvey("corpse of Ermias");                  // the floor survey shows our corpse,
        Assert.Empty(h.Sent);                              // and nothing is sent for it
        h.EnterGates();                                    // until the exits line says which room it is

        Assert.Contains("recover corpse Ermias", h.Sent);
        Assert.DoesNotContain(h.Sent, s => s.StartsWith("get ")); // NOT the old per-item get spam
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);

        h.Feed.Line("You have recovered the corpse of Ermias.");
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    // A death worked out after the fact (a hang-up penalty that killed, found on
    // the next entry) is a record like any other: the grid hears of it, and the
    // pile is recovered when its room is walked into. Nothing is sent for it where
    // the character woke.
    [Fact]
    public void ADeathWorkedOutAfterTheFact_ShowsInTheGrid_AndRecoversLikeAnyOther()
    {
        using GraphHarness h = new();
        h.Tracker.NoteRoomObserved(new RoomObservation("North Square", new HashSet<Direction> { Direction.S }));
        int gridRefreshes = 0;
        h.Recovery.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DeathRecoveryManager.Records)) gridRefreshes++;
        };

        h.Tracker.NoteUnwitnessedDeath(new UnwitnessedDeath(
            new RoomRef(1, 1), DateTimeOffset.UtcNow.AddMinutes(-3), LivesRemaining: 6,
            "Killed by the hang-up penalty (not seen: worked out on entering the game).",
            Equipped: [new DeathItem("rusty dagger", "Weapon Hand")],
            Lost: [new DeathItem("torch")],
            Coins: null));

        Assert.True(gridRefreshes > 0);
        DeathRecord record = Assert.Single(h.Recovery.Records);
        Assert.Equal(DeathRecoveryStatus.Active, record.Status);
        Assert.Equal("Town Gates", record.RoomName);
        Assert.Empty(h.Sent);

        h.Recovery.AutoRecover = true;
        h.FeedSurvey("corpse of Ermias");                  // the floor line, ahead of the exits line
        h.EnterGates();

        Assert.Contains("recover corpse Ermias", h.Sent);
        h.Feed.Line("You have recovered the corpse of Ermias.");
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    [Fact]
    public void CorpseMatch_UsesLiveName_NotStaleProfileName()
    {
        // report stock-20260828-104653: a profile COPIED from another character keeps
        // the old name in CharacterProfile.Name. Corpse matching must identify self by
        // the LIVE in-game name (PartyManager.LocalCharacterName), never the stale
        // profile name — matching another player's corpse would be a mis-recovery.
        // The lone-corpse rule only auto-takes when we have NO name to match, so
        // recovering "corpse of Raijin" here proves it matched the live name, not
        // the stale "Fujin" (which would leave it unmatched → nothing sent).
        using GraphHarness h = new("Fujin");                     // stale copied-profile name
        h.Recovery.AttachLiveSelfName(() => "Raijin WuzHere");   // the real, live name
        Die(h, new[] { new EquippedItem("rusty dagger", "Weapon Hand") }, new[] { "torch" });
        h.Recovery.AutoRecover = true;

        h.FeedSurvey("corpse of Raijin");
        h.EnterGates();

        Assert.Contains("recover corpse Raijin", h.Sent);
    }

    [Fact]
    public void ReEntry_AutoRecover_CorpseAbsent_MarksMissing_SendsNothing()
    {
        // The reported bug: the pile's corpse is gone (only coins on the floor).
        // We must send NO recover/get and mark the pile Missing instead of looping.
        using GraphHarness h = new();
        Die(h, new[] { new EquippedItem("padded helm", "Head") }, new[] { "club" });
        h.Recovery.AutoRecover = true;

        h.FeedSurvey("3 copper farthings");                // cash only — no corpse
        h.EnterGates();

        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Missing, h.Latest.Status);
    }

    [Fact]
    public void MissingPile_DoesNotReArm_OnReEntry()
    {
        using GraphHarness h = new();
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.FeedSurvey("3 copper farthings");
        h.EnterGates();                                    // → Missing
        Assert.Equal(DeathRecoveryStatus.Missing, h.Latest.Status);

        // Leave and come back: a Missing pile must not re-arm, even if the corpse
        // now appears in the survey — no spam. (Recover Now is the explicit retry.)
        h.Tracker.NoteRoomObserved(Obs3());                // 1/3 North Square
        h.Sent.Clear();
        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();
        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Missing, h.Latest.Status);
    }

    [Fact]
    public void ReEntry_NoAutoRecover_SendsNothing_ManualRecoverStillFinalizes()
    {
        using GraphHarness h = new();
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });

        h.EnterGates();
        h.FeedSurvey("corpse of Ermias");
        Assert.Empty(h.Sent);                              // no auto-grab without the toggle
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);

        // The user recovers manually; we key off the confirmation line either way.
        h.Feed.Line("You have recovered the corpse of Ermias.");
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    [Fact]
    public void AutoEquip_ReequipsWornItems_OnCorpseRecovered()
    {
        using GraphHarness h = new();
        Die(h, new[] { new EquippedItem("plate mail", "Torso") }, Array.Empty<string>());
        h.Recovery.AutoRecover = true;
        h.Recovery.AutoEquip = true;

        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();
        Assert.Contains("recover corpse Ermias", h.Sent);

        h.Feed.Line("You have recovered the corpse of Ermias.");
        Assert.Contains("wear plate mail", h.Sent);        // armour → wear, after the corpse is back
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    // Report paradigm-20261004-201808: Auto-All off, the corpse recovered by hand
    // with two monsters in the room, and the client fired eleven wear commands.
    [Fact]
    public void AutoEquip_WithAutoAllOff_HoldsTheGear_UntilItIsBackOn()
    {
        using GraphHarness h = new();
        bool autoOn = false;
        h.Recovery.SetAutoEnabledProbe(() => autoOn);
        Die(h, new[]
        {
            new EquippedItem("plate mail", "Torso"),
            new EquippedItem("platinum mace", "Weapon Hand"),
        }, new[] { "torch" });
        h.Recovery.AutoEquip = true;
        h.EnterGates();
        h.Sent.Clear();

        h.Feed.Line("You have recovered the corpse of Ermias.");   // by hand

        Assert.Empty(h.Sent);
        Assert.Equal(2, h.Recovery.HeldReequipCount);
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);

        autoOn = true;
        h.Recovery.OnAutoAllRestored();

        Assert.Equal(new[] { "eq platinum mace", "wear plate mail" }, h.Sent.ToArray());
        Assert.Equal(0, h.Recovery.HeldReequipCount);
    }

    [Fact]
    public void AutoEquip_HeldForAutoAll_LeavesOutWhatWasWornByHandMeanwhile()
    {
        using GraphHarness h = new();
        bool autoOn = false;
        h.Recovery.SetAutoEnabledProbe(() => autoOn);
        Die(h, new[]
        {
            new EquippedItem("plate mail", "Torso"),
            new EquippedItem("platinum mace", "Weapon Hand"),
        }, Array.Empty<string>());
        h.Recovery.AutoEquip = true;
        h.EnterGates();
        h.Feed.Line("You have recovered the corpse of Ermias.");
        h.Sent.Clear();

        // The user put the armour back on themselves while Auto-All was off.
        h.Snapshot = h.SnapWith(new[] { new EquippedItem("plate mail", "Torso") }, new[] { "platinum mace" });
        autoOn = true;
        h.Recovery.OnAutoAllRestored();

        Assert.Equal(new[] { "eq platinum mace" }, h.Sent.ToArray());
    }

    [Fact]
    public void AutoEquip_HeldForAutoAll_IsDroppedByANewDeath()
    {
        using GraphHarness h = new();
        bool autoOn = false;
        h.Recovery.SetAutoEnabledProbe(() => autoOn);
        Die(h, new[] { new EquippedItem("plate mail", "Torso") }, Array.Empty<string>());
        h.Recovery.AutoEquip = true;
        h.EnterGates();
        h.Feed.Line("You have recovered the corpse of Ermias.");
        Assert.Equal(1, h.Recovery.HeldReequipCount);

        h.Tracker.NoteDeath(1, "You now have 1 lives remaining.");

        Assert.Equal(0, h.Recovery.HeldReequipCount);
    }

    // ----- gear handed back by a party member -------------------------

    [Fact]
    public void HandedBack_AllPileItems_FinalisesAndReequipsWorn()
    {
        // report paradigm-20260926-102406: the leader recovered the follower's corpse
        // and gave the gear back; it sat unworn in the pack. Each "X just gave you …"
        // strikes an item off the pile; once the burst goes quiet the pile finalises
        // and the worn half goes back on.
        using GraphHarness h = new();
        Die(h, new[]
        {
            new EquippedItem("plate mail", "Torso"),
            new EquippedItem("platinum mace", "Weapon Hand"),
        }, new[] { "torch" });
        h.Recovery.AutoEquip = true;

        h.Recovery.OnItemReceived("plate mail", "Nineteen");
        h.Recovery.OnItemReceived("platinum mace", "Nineteen");
        h.Recovery.OnItemReceived("torch", "Nineteen");
        Assert.Empty(h.Sent);                          // still settling — nothing worn yet

        h.Heartbeat(); h.Heartbeat(); h.Heartbeat();   // the hand-off went quiet

        Assert.Equal(new[] { "eq platinum mace", "wear plate mail" }, h.Sent.ToArray());
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
        Assert.Contains("Nineteen", h.Latest.RecoveryMessage);
    }

    [Fact]
    public void HandedBack_SomeItems_HoldsPartial_AndReequipsWhatCameBack()
    {
        using GraphHarness h = new();
        Die(h, new[]
        {
            new EquippedItem("plate mail", "Torso"),
            new EquippedItem("steel helm", "Head"),
        }, Array.Empty<string>());
        h.Recovery.AutoEquip = true;

        h.Recovery.OnItemReceived("plate mail", "Nineteen");
        h.Heartbeat(); h.Heartbeat(); h.Heartbeat();

        Assert.Equal(new[] { "wear plate mail" }, h.Sent.ToArray());
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
        Assert.Equal(new[] { "steel helm" }, h.Latest.UnrecoveredItems);
    }

    [Fact]
    public void HandedBack_ItemNotFromThePile_Ignored()
    {
        using GraphHarness h = new();
        Die(h, new[] { new EquippedItem("plate mail", "Torso") }, Array.Empty<string>());
        h.Recovery.AutoEquip = true;

        h.Recovery.OnItemReceived("loaf of bread", "Nineteen");
        h.Heartbeat(); h.Heartbeat(); h.Heartbeat();

        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Active, h.Latest.Status);
    }

    [Fact]
    public void HandedBack_AutoEquipOff_FinalisesWithoutWearing()
    {
        using GraphHarness h = new();
        Die(h, new[] { new EquippedItem("plate mail", "Torso") }, Array.Empty<string>());
        h.Recovery.AutoEquip = false;

        h.Recovery.OnItemReceived("plate mail", "Nineteen");
        h.Heartbeat(); h.Heartbeat(); h.Heartbeat();

        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    [Fact]
    public void AutoEquip_ReequipsHeldWeapon_WithEq_NotHold()
    {
        // Report: corpse recovery sent "hold platinum mace" for the weapon. A held
        // item (Weapon Hand / Off-Hand) must be wielded with `eq`, matching the
        // equipment manager — `hold` only carries it in hand, it doesn't wield it.
        using GraphHarness h = new();
        Die(h, new[] { new EquippedItem("platinum mace", "Weapon Hand") }, Array.Empty<string>());
        h.Recovery.AutoRecover = true;
        h.Recovery.AutoEquip = true;

        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();
        h.Feed.Line("You have recovered the corpse of Ermias.");

        Assert.Contains("eq platinum mace", h.Sent);
        Assert.DoesNotContain("hold platinum mace", h.Sent);
    }

    [Fact]
    public void GivenNameOnly_UsedForCorpseMatchAndCommand()
    {
        // The survey shows the GIVEN name only ("corpse of Ermias"), and the
        // command must use it too — never the family name.
        using GraphHarness h = new("Ermias Asghedom");
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;

        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();
        Assert.Contains("recover corpse Ermias", h.Sent);
        Assert.DoesNotContain("recover corpse Ermias Asghedom", h.Sent);
    }

    [Fact]
    public void AnotherPlayersCorpse_NotRecovered_WhenNameMismatches()
    {
        using GraphHarness h = new("Ermias");
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;

        h.FeedSurvey("corpse of Bob");                     // someone else's corpse
        h.EnterGates();
        Assert.Empty(h.Sent);                              // never recover another player's corpse
        Assert.Equal(DeathRecoveryStatus.Missing, h.Latest.Status);
    }

    [Fact]
    public void ReEntry_KnownEmptyDeathpile_RecoveredImmediately()
    {
        using GraphHarness h = new();
        h.EnterGates();
        h.Snapshot = InventorySnapshot.Empty;   // nothing worn / carried
        h.Tracker.NoteDeath(2, "You now have 2 lives remaining.");

        h.EnterGates();
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    // Recover Now away from the corpse is a walk the user asked for: it is handed to
    // the route cards like any walk-to, and the grab is armed for the arrival.
    [Fact]
    public void RecoverNow_AwayFromTheDeathRoom_GoesThroughTheRouteCards()
    {
        using GraphHarness h = new();
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        List<RoomKey> asked = new();
        h.Recovery.SetDemandedWalk(room => { asked.Add(room); return true; });

        Assert.True(h.Recovery.RecoverNow(h.Latest));

        Assert.Equal(new RoomKey(1, 1), Assert.Single(asked));

        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();                                    // arrived, however the cards routed it
        Assert.Contains("recover corpse Ermias", h.Sent);
    }

    [Fact]
    public void RecoverNow_InRoom_LooksThenRecoversOnSurvey()
    {
        using GraphHarness h = new();
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.EnterGates();                                    // auto re-entry: Partial, no grab (AutoRecover off)
        h.FeedSurvey("corpse of Ermias");
        Assert.Empty(h.Sent);                              // AutoRecover off → nothing yet

        Assert.True(h.Recovery.RecoverNow(h.Latest));
        Assert.Contains("look", h.Sent);                   // re-look to re-render the survey
        h.FeedSurvey("corpse of Ermias");                  // the look's survey
        Assert.Contains("recover corpse Ermias", h.Sent);
    }

    [Fact]
    public void OrderForReequip_WeaponFirst_ThenArmourByHighestAc()
    {
        var worn = new List<DeathItem>
        {
            new("cloth cap", "Head"),
            new("plate mail", "Torso"),
            new("platinum mace", "Weapon Hand"),
            new("small shield", "Off-Hand"),
            new("leather boots", "Feet"),
        };
        var ac = new Dictionary<string, int>
        {
            ["cloth cap"] = 2, ["plate mail"] = 30, ["leather boots"] = 5,
        };
        List<DeathItem> ordered =
            DeathRecoveryManager.OrderForReequip(worn, n => ac.TryGetValue(n, out int v) ? v : 0);

        // Weapon Hand, then Off-Hand, then armour highest-AC-first.
        Assert.Equal(
            new[] { "platinum mace", "small shield", "plate mail", "leather boots", "cloth cap" },
            ordered.Select(i => i.Name).ToArray());
    }

    [Fact]
    public void InCombat_Recovery_PacesReequipAcrossRounds_ThenFlushesOnRoomClear()
    {
        using GraphHarness h = new();
        var worn = new[]
        {
            new EquippedItem("platinum mace", "Weapon Hand"),
            new EquippedItem("plate mail", "Torso"),
            new EquippedItem("steel helm", "Head"),
            new EquippedItem("steel greaves", "Legs"),
            new EquippedItem("leather boots", "Feet"),
            new EquippedItem("silver ring", "Finger"),
        };
        Die(h, worn, Array.Empty<string>());
        h.Recovery.AutoRecover = true;
        h.Recovery.AutoEquip = true;
        h.Hostiles = true;                                 // a live hostile shares the death room

        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();
        Assert.Contains("recover corpse Ermias", h.Sent);  // recovery itself is unchanged (safe)

        h.Sent.Clear();
        h.Feed.Line("You have recovered the corpse of Ermias.");
        Assert.Empty(h.Sent);                              // NOT fired all at once — paced
        Assert.True(h.GateHeld);                           // walker held while pieces pend

        h.CombatRound();                                   // first round-gap: 4 pieces + one re-attack
        Assert.Equal(4, h.Sent.Count);
        Assert.Equal("eq platinum mace", h.Sent[0]);       // weapon first
        Assert.Equal(1, h.ResumeArmed);
        Assert.True(h.GateHeld);                           // 2 still pending

        h.Hostiles = false;                                // mob dies between rounds → room clears
        h.Heartbeat();                                     // remainder flushes at once
        Assert.Equal(6, h.Sent.Count);
        Assert.False(h.GateHeld);                          // gate released
    }

    [Fact]
    public void NoHostile_Recovery_EquipsAllAtOnce_WeaponFirst_NoGate()
    {
        using GraphHarness h = new();
        Die(h, new[]
        {
            new EquippedItem("plate mail", "Torso"),
            new EquippedItem("platinum mace", "Weapon Hand"),
        }, Array.Empty<string>());
        h.Recovery.AutoRecover = true;
        h.Recovery.AutoEquip = true;
        // Hostiles defaults false — an empty room recovers exactly as before.

        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();
        h.Sent.Clear();
        h.Feed.Line("You have recovered the corpse of Ermias.");

        Assert.Equal(new[] { "eq platinum mace", "wear plate mail" }, h.Sent.ToArray());
        Assert.False(h.GateHeld);                          // no gate when there's no fight to pace against
    }

    [Fact]
    public void Stock_Recovery_GetsFloorItems_NotCorpse_ThenReequipsOnceAllBack()
    {
        using GraphHarness h = new();
        Die(h,
            new[] { new EquippedItem("platinum mace", "Weapon Hand"), new EquippedItem("plate mail", "Torso") },
            new[] { "torch" });
        h.Paradigm = false;                 // Stock: items scattered loose on the floor
        h.Recovery.AutoRecover = true;
        h.Recovery.AutoEquip = true;

        h.FeedSurvey("a platinum mace, a plate mail, and a torch");   // our pile on the floor,
        h.EnterGates();                                                // read before the room confirms

        // `get` each present pile item (article-insensitive) — NOT `recover corpse`.
        Assert.Contains("get platinum mace", h.Sent);
        Assert.Contains("get plate mail", h.Sent);
        Assert.Contains("get torch", h.Sent);
        Assert.DoesNotContain(h.Sent, s => s.StartsWith("recover corpse"));
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);

        h.Sent.Clear();
        h.Feed.Line("You took a platinum mace.");
        h.Feed.Line("You took a plate mail.");
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);   // torch still out → not done
        h.Feed.Line("You took a torch.");

        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
        // Worn gear re-equipped once the whole pile is back — weapon first (no hostile → all at once).
        Assert.Equal(new[] { "eq platinum mace", "wear plate mail" }, h.Sent.ToArray());
    }

    [Fact]
    public void Stock_Recovery_OnlyGetsItemsPresentOnTheFloor_NoGetSpam()
    {
        // A worn helm spilled elsewhere — it is NOT in this room's survey, so we must
        // not `get` it (that was the old spam). We grab what's here and hold Partial.
        using GraphHarness h = new();
        Die(h,
            new[] { new EquippedItem("iron sword", "Weapon Hand"), new EquippedItem("steel helm", "Head") },
            Array.Empty<string>());
        h.Paradigm = false;
        h.Recovery.AutoRecover = true;
        h.Recovery.AutoEquip = true;

        h.FeedSurvey("an iron sword");       // only the sword is here; the helm spilled
        h.EnterGates();

        Assert.Contains("get iron sword", h.Sent);
        Assert.DoesNotContain("get steel helm", h.Sent);   // absent → never `get`-spammed
        h.Feed.Line("You took an iron sword.");
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);   // helm still out → Partial
    }

    [Fact]
    public void Stock_Recovery_StackedItem_GetsBareNamePerUnit_NotBatched()
    {
        using GraphHarness h = new();
        Die(h, Array.Empty<EquippedItem>(), new[] { "3 torch" });   // captured as a stack
        h.Paradigm = false;
        h.Recovery.AutoRecover = true;

        h.FeedSurvey("3 torch");
        h.EnterGates();

        // Stock has no batched `get N item` — send bare `get torch`, once per unit.
        Assert.Equal(3, h.Sent.Count(s => s == "get torch"));
        Assert.DoesNotContain(h.Sent, s => s.Contains("get 3 torch"));

        h.Feed.Line("You took torch.");
        h.Feed.Line("You took torch.");
        Assert.NotEqual(DeathRecoveryStatus.Recovered, h.Latest.Status);   // one still out
        h.Feed.Line("You took torch.");
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    [Fact]
    public void Stock_Recovery_YouTookOnPromptRow_StillDecrements()
    {
        using GraphHarness h = new();
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Paradigm = false;
        h.Recovery.AutoRecover = true;

        h.FeedSurvey("a torch");
        h.EnterGates();
        Assert.Contains("get torch", h.Sent);

        // The get confirmation is redrawn onto the prompt row (IsPromptLine=true) —
        // recovery must still count it (else the pile never finalises).
        h.Feed.Raw("[HP=100/MA=50]:get torch\u001b[79D\u001b[KYou took torch.\r\n");
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    [Fact]
    public void Stock_Recovery_OnlyCurrencyLeft_FinalisesRecovered()
    {
        // "If currency is the only thing not recovered, treat the death as recovered."
        // Coins drop from the item survey (they recover as cash, never `get`-ed), so
        // they linger in UnrecoveredItems — a pile down to pocket change still finalises.
        using GraphHarness h = new();
        Die(h,
            new[] { new EquippedItem("iron sword", "Weapon Hand") },
            new[] { "1500 gold" });
        h.Paradigm = false;
        h.Recovery.AutoRecover = true;

        h.FeedSurvey("an iron sword and 1500 gold");   // sword + coins on the floor
        h.EnterGates();

        Assert.Contains("get iron sword", h.Sent);
        Assert.DoesNotContain(h.Sent, s => s.Contains("gold"));   // coins never `get`-ed

        h.Feed.Line("You took an iron sword.");        // sword back; only coins left
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    [Fact]
    public void Stock_ManualReentry_SpilledItems_DoesNotFireLookSweep()
    {
        // Manual walk into a death room (walker idle → not a directed walk-to) must NOT
        // fire the adjacent-room look sweep, even with Auto-Recover on. Grab the floor,
        // hold Partial for the spilled piece — no `look <dir>` peeks.
        using GraphHarness h = new();
        Die(h,
            new[] { new EquippedItem("iron sword", "Weapon Hand"), new EquippedItem("steel helm", "Head") },
            Array.Empty<string>());
        h.Paradigm = false;
        h.Recovery.AutoRecover = true;

        h.FeedSurvey("an iron sword");         // helm spilled to a neighbour, sword is here
        h.EnterGates();
        h.Feed.Line("You took an iron sword.");
        h.Sent.Clear();
        h.Heartbeat();                         // settle the grab (StockSettleTicks = 2)
        h.Heartbeat();

        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
        Assert.DoesNotContain(h.Sent, s => s.StartsWith("look "));   // no sweep on a manual walk-in
    }

    [Fact]
    public void RecoverNow_Stock_SpilledItems_FiresLookSweep()
    {
        // Recover Now is a deliberate recovery — with a piece spilled to a neighbour it
        // sweeps the death-room exits, peeking each with `look <dir>`.
        using GraphHarness h = new();
        Die(h,
            new[] { new EquippedItem("iron sword", "Weapon Hand"), new EquippedItem("steel helm", "Head") },
            Array.Empty<string>());
        h.Paradigm = false;

        h.EnterGates();                        // in the death room, Auto-Recover off
        Assert.True(h.Recovery.RecoverNow(h.Latest));
        h.FeedSurvey("an iron sword");         // the re-look's survey — helm spilled
        h.Feed.Line("You took an iron sword.");
        h.Sent.Clear();
        h.Heartbeat();                         // settle → sweep the exits
        h.Heartbeat();

        Assert.Contains(h.Sent, s => s.StartsWith("look "));   // deliberate → look-sweep started
    }

    [Fact]
    public void Stock_PassThrough_GrabsSpilloverFromAdjacentRoom()
    {
        using GraphHarness h = new();
        Die(h, new[] { new EquippedItem("steel helm", "Head") }, Array.Empty<string>());
        // Simulate a pile whose helm overflowed into the room next door.
        h.Latest.UnrecoveredItems = new List<string> { "steel helm" };
        h.Latest.Status = DeathRecoveryStatus.Partial;
        h.Paradigm = false;
        h.Recovery.AutoRecover = true;
        h.Recovery.AutoEquip = true;

        // Walk into North Square (1/3), which borders the death room (1/1).
        h.Tracker.NoteRoomObserved(Obs3());
        h.FeedSurvey("a steel helm");                 // our overflow is on this floor
        Assert.Contains("get steel helm", h.Sent);    // grabbed in-stride, no detour

        h.Feed.Line("You took a steel helm.");
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
        Assert.Contains("wear steel helm", h.Sent);   // recovered gear re-worn
    }

    // North Square (1/3) — the adjacent room, used to leave and re-enter the death room.
    private static RoomObservation Obs3()
        => new("North Square", new HashSet<Direction>(new[] { Direction.S }));

    // ----- Stock: what is never on a floor ----------------------------

    [Fact]
    public void Stock_ItemReturnedToItsRightfulPlace_IsNotWaitedFor()
    {
        // The line prints as the death happens, before the lives readout that makes
        // the record, and names an item that is gone for good.
        using GraphHarness h = new() { Paradigm = false };
        h.EnterGates();
        h.Snapshot = h.SnapWith(
            new[] { new EquippedItem("iron sword", "Weapon Hand"), new EquippedItem("steel helm", "Head") },
            Array.Empty<string>());
        h.Feed.Line("You have been killed!");
        h.Feed.Line("Your steel helm has returned to its rightful place.");
        h.Tracker.NoteDeath(2, "You have 2 lives left.");
        h.Recovery.AutoRecover = true;

        Assert.Equal(new[] { "steel helm" }, h.Latest.ReturnedItems);

        h.FeedSurvey("an iron sword");
        h.EnterGates();
        Assert.DoesNotContain("get steel helm", h.Sent);
        h.Feed.Line("You took an iron sword.");

        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);   // the helm isn't waited for
    }

    [Fact]
    public void Paradigm_ReturnedLine_ChangesNothing()
    {
        using GraphHarness h = new();   // Paradigm
        h.EnterGates();
        h.Snapshot = h.SnapWith(new[] { new EquippedItem("steel helm", "Head") }, Array.Empty<string>());
        h.Feed.Line("Your steel helm has returned to its rightful place.");
        h.Tracker.NoteDeath(2, "You have 2 lives left.");

        Assert.Null(h.Latest.ReturnedItems);
        Assert.Null(h.Latest.Trail);
        h.EnterGates();
        Assert.Equal(new[] { "steel helm" }, h.Latest.UnrecoveredItems);
    }

    [Fact]
    public void Stock_ItemThatStaysOnTheCharacter_IsNeverMissing_AndIsWornAgain()
    {
        // A Loyal or CursedMajor item isn't dropped (the probe reads its abilities),
        // but a death takes everything off, so it goes back on with the rest.
        using GraphHarness h = new() { Paradigm = false };
        h.Stays.Add("signet ring");
        Die(h,
            new[] { new EquippedItem("iron sword", "Weapon Hand"), new EquippedItem("signet ring", "Finger") },
            Array.Empty<string>());
        h.Recovery.AutoRecover = true;
        h.Recovery.AutoEquip = true;

        h.FeedSurvey("an iron sword and a signet ring");   // someone else's ring on the floor
        h.EnterGates();
        Assert.Equal(new[] { "iron sword" }, h.Latest.UnrecoveredItems);
        Assert.DoesNotContain("get signet ring", h.Sent);
        h.Feed.Line("You took an iron sword.");

        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
        Assert.Contains("wear signet ring", h.Sent);
    }

    [Fact]
    public void Stock_ReturnedUnit_IsStruckOnce_NotAgainOnEveryReEntry()
    {
        // Three torches, one of them gone for good. Two are out; get one and one is.
        // Coming back must not strike the returned torch a second time and close the
        // pile with a torch still on a floor somewhere.
        using GraphHarness h = new() { Paradigm = false };
        h.EnterGates();
        h.Snapshot = h.SnapWith(Array.Empty<EquippedItem>(), new[] { "3 torch" });
        h.Feed.Line("Your torch has returned to its rightful place.");
        h.Tracker.NoteDeath(2, "You have 2 lives left.");
        h.Recovery.AutoRecover = true;

        h.FeedSurvey("a torch");
        h.EnterGates();
        Assert.Equal(new[] { "torch", "torch" }, h.Latest.UnrecoveredItems);
        h.Feed.Line("You took torch.");
        Assert.Equal(new[] { "torch" }, h.Latest.UnrecoveredItems);

        h.Tracker.NoteRoomObserved(Obs3());
        h.EnterGates();

        Assert.Equal(new[] { "torch" }, h.Latest.UnrecoveredItems);
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
    }

    [Fact]
    public void Stock_PileOfNothingButWhatStays_IsDone_AndTheGearGoesBackOn()
    {
        using GraphHarness h = new() { Paradigm = false };
        h.Stays.Add("signet ring");
        Die(h, new[] { new EquippedItem("signet ring", "Finger") }, Array.Empty<string>());
        h.Recovery.AutoEquip = true;

        h.EnterGates();

        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
        Assert.Contains("wear signet ring", h.Sent);
    }

    [Fact]
    public void Stock_ReEnteringAPartlyRecoveredPile_KeepsWhatWasCountedDown()
    {
        using GraphHarness h = new() { Paradigm = false };
        Die(h,
            new[] { new EquippedItem("iron sword", "Weapon Hand"), new EquippedItem("steel helm", "Head") },
            Array.Empty<string>());
        h.Recovery.AutoRecover = true;
        h.FeedSurvey("an iron sword");
        h.EnterGates();
        h.Feed.Line("You took an iron sword.");
        Assert.Equal(new[] { "steel helm" }, h.Latest.UnrecoveredItems);

        h.Tracker.NoteRoomObserved(Obs3());   // leave
        h.EnterGates();                       // and come back: the sword is in the pack, not missing

        Assert.Equal(new[] { "steel helm" }, h.Latest.UnrecoveredItems);
    }

    [Fact]
    public void Stock_WalkIn_ReadsTheSurveyThatPrintedBeforeTheRoomConfirmed()
    {
        // A room prints its floor before the exits line that confirms the move, so on
        // a walk-in the survey is already read when the grab is armed.
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;

        h.FeedSurvey("a torch");
        h.EnterGates();

        Assert.Contains("get torch", h.Sent);
    }
}
