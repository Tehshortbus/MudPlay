using System.Text;
using System.Text.Json;
using MudPlay.Game.Health;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A board can drop some of a character's items on the floor for a hang-up in a
// fight. On a realm whose settings say so, the client compares what was held
// before the link dropped with the login's inventory read and picks up what is
// short and lying in the room. Nothing here decides a hang-up.
public sealed class HangupItemRecheckTests
{
    private static InventorySnapshot Snap(
        (string Name, string Slot)[]? worn = null, string[]? carried = null, string[]? keys = null,
        ReadiedLight? light = null) =>
        new(CurrencyHoldings.Empty, EncumbranceReading.Empty,
            (worn ?? []).Select(w => new EquippedItem(w.Name, w.Slot)).ToList(),
            carried ?? [], DateTimeOffset.Now, light, keys);

    // ----- The realm's settings ----------------------------------------

    [Theory]
    [InlineData(false, 3, false, 0, 0)]   // master switch off
    [InlineData(true, 0, false, 0, 0)]    // HP only, as most boards
    [InlineData(true, 2, false, 0, 2)]    // PvP side drops items
    [InlineData(true, 0, false, 4, 0)]    // PvE count set but that side isn't ticked
    [InlineData(true, 0, true, 4, 4)]     // PvE side drops items
    [InlineData(true, 3, true, 5, 5)]     // either side may have applied: the larger
    [InlineData(true, 6, true, 5, 6)]
    [InlineData(true, -5, true, -1, 0)]   // a hand-edited file reads through the limits
    public void TheMostARealmDrops_IsTheLargerCountOfTheSidesThatApply(
        bool enabled, int pvpItems, bool pve, int pveItems, int expected)
    {
        RealmProfile realm = new()
        {
            HangupPenaltyEnabled = enabled,
            HangupPvpItemsDropped = pvpItems,
            HangupPvePenaltyEnabled = pve,
            HangupPveItemsDropped = pveItems,
        };

        Assert.Equal(expected, HangupPenaltyNotice.MaxItemsDropped(realm));
        Assert.Equal(0, HangupPenaltyNotice.MaxItemsDropped(null));
    }

    // ----- What is held, and what is short ------------------------------

    [Fact]
    public void Held_CountsByName_OverWornCarriedLitAndKeyRing()
    {
        List<HeldItem> held = HangupItemPlan.Held(Snap(
            worn: [("gold ring", "Finger"), ("gold ring", "Finger"), ("dagger", "Weapon Hand")],
            carried: ["2 dagger", "3 torch", "rope"],
            keys: ["2 black star key", "brass key"],
            light: new ReadiedLight("torch", 40)));

        Assert.Equal(2, held.Single(h => h.Name == "gold ring").Count);
        Assert.Equal(3, held.Single(h => h.Name == "dagger").Count);
        Assert.Equal(4, held.Single(h => h.Name == "torch").Count);
        Assert.Equal(1, held.Single(h => h.Name == "rope").Count);
        Assert.Equal(2, held.Single(h => h.Name == "black star key").Count);
        Assert.Equal(1, held.Single(h => h.Name == "brass key").Count);
    }

    [Fact]
    public void Missing_IsByNameAndCount()
    {
        List<HeldItem> before = HangupItemPlan.Held(Snap(
            worn: [("chainmail hauberk", "Torso")], carried: ["3 torch", "rope"], keys: ["2 black star key"]));

        List<(string Name, int Count)> missing = HangupItemPlan.Missing(before, Snap(
            carried: ["torch", "rope"], keys: ["black star key"]));

        Assert.Equal([("chainmail hauberk", 1), ("torch", 2), ("black star key", 1)], missing);
    }

    [Fact]
    public void Missing_NothingWhenEverythingIsStillHeld_HoweverItIsHeld()
    {
        List<HeldItem> before = HangupItemPlan.Held(Snap(
            worn: [("dagger", "Weapon Hand")], carried: ["2 torch"], keys: ["brass key"]));

        // The weapon is in the pack now, a torch is lit, and something new was gained.
        Assert.Empty(HangupItemPlan.Missing(before, Snap(
            carried: ["dagger", "torch", "waterskin"], keys: ["brass key"], light: new ReadiedLight("torch", 80))));
    }

    [Fact]
    public void Missing_Duplicates_OnlyTheShortCopies()
    {
        List<HeldItem> before = HangupItemPlan.Held(Snap(
            worn: [("gold ring", "Finger"), ("gold ring", "Finger"), ("dagger", "Weapon Hand")],
            carried: ["2 dagger"]));

        List<(string Name, int Count)> missing = HangupItemPlan.Missing(before, Snap(
            worn: [("gold ring", "Finger"), ("dagger", "Weapon Hand")], carried: ["dagger"]));

        Assert.Equal([("gold ring", 1), ("dagger", 1)], missing);
    }

    // ----- The pickup plan ----------------------------------------------

    [Fact]
    public void Pickup_OnlyMissingNames_OnlyUpToTheMissingCount()
    {
        List<(string Name, int Count)> missing = [("torch", 2), ("chainmail hauberk", 1), ("rope", 1)];

        // Five torches lie here, a hauberk, and things that were never ours.
        List<(string Name, int Count)> plan = HangupItemPlan.Pickup(
            missing, ["5 torch", "a chainmail hauberk", "rusty dagger", "2 black star key"]);

        Assert.Equal([("torch", 2), ("chainmail hauberk", 1)], plan);
    }

    [Fact]
    public void Pickup_TakesNoMoreThanLiesThere_AndNothingFromAnEmptyFloor()
    {
        List<(string Name, int Count)> missing = [("torch", 3)];

        Assert.Equal([("torch", 1)], HangupItemPlan.Pickup(missing, ["torch"]));
        Assert.Empty(HangupItemPlan.Pickup(missing, []));
    }

    // Never more copies than the board can have dropped, in the plan's order.
    [Fact]
    public void Capped_CutsThePlanToTheMostTheBoardDrops()
    {
        List<(string Name, int Count)> plan = [("chainmail hauberk", 1), ("torch", 3), ("rope", 1)];

        Assert.Equal([("chainmail hauberk", 1), ("torch", 2)], HangupItemPlan.Capped(plan, 3));
        Assert.Equal(plan, HangupItemPlan.Capped(plan, 5));
        Assert.Equal(plan, HangupItemPlan.Capped(plan, 100));
        Assert.Empty(HangupItemPlan.Capped(plan, 0));
    }

    [Fact]
    public void Outstanding_DropsWhatCameBack()
    {
        List<(string Name, int Count)> left = HangupItemPlan.Outstanding(
            [("gold ring", 2), ("torch", 1)],
            new Dictionary<string, int> { ["gold ring"] = 1, ["torch"] = 1 });

        Assert.Equal([("gold ring", 1)], left);
    }

    // ----- The list on disk ---------------------------------------------

    [Fact]
    public void HeldList_RoundTripsThroughTheProfileFile()
    {
        CharacterProfile profile = new()
        {
            HeldAtDisconnect = new HeldAtDisconnect
            {
                At = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero),
                Realm = "Board/PvP",
                Room = new RoomRef(1, 3),
                PenaltiesSpanned = 2,
                Items = [new HeldItem("gold ring", 2), new HeldItem("torch", 3)],
            },
        };

        CharacterProfile back = JsonSerializer.Deserialize<CharacterProfile>(
            JsonSerializer.Serialize(profile, JsonStore.Options), JsonStore.Options)!;

        HeldAtDisconnect held = back.HeldAtDisconnect!;
        Assert.Equal(profile.HeldAtDisconnect.At, held.At);
        Assert.Equal("Board/PvP", held.Realm);
        Assert.Equal(3, held.Room!.Room);
        Assert.Equal(2, held.PenaltiesSpanned);
        Assert.Equal(2, held.Items.Count);
        Assert.Equal(3, held.Items[1].Count);
    }

    // The list is a new optional field: a profile from before has none, and the
    // schema upgrade neither needs a step for it nor invents one.
    [Fact]
    public void AnOlderProfile_LoadsWithNoList_AndMigratesWithoutOne()
    {
        CharacterProfile old = JsonSerializer.Deserialize<CharacterProfile>(
            """{ "SchemaVersion": 8, "Name": "Ermias" }""", JsonStore.Options)!;

        Assert.Null(old.HeldAtDisconnect);
        Assert.True(ProfileMigrations.Apply(old));
        Assert.Equal(CharacterProfile.CurrentSchemaVersion, old.SchemaVersion);
        Assert.Null(old.HeldAtDisconnect);
    }

    // ----- The check ----------------------------------------------------

    private sealed class Harness
    {
        public const string RealmKey = "Board/Main";

        public MovementCoordinator Coordinator { get; } = new();
        public CharacterProfile Profile { get; } = new();
        // False: the default profile is loaded, not a named character.
        public bool Named { get; set; } = true;
        public string? Realm { get; set; } = RealmKey;
        public int MaxItems { get; set; } = 10;
        public InventorySnapshot Inventory { get; set; } = InventorySnapshot.Empty;
        public RoomKey? Room { get; set; } = new RoomKey(1, 3);
        public List<string> Floor { get; } = new();
        public bool Fighting { get; set; }
        public bool Hostile { get; set; }
        public bool AutoAll { get; set; } = true;
        public List<(string Name, int Count)> Collected { get; } = new();
        // The names handed to the equipment manager each time gear came back, and
        // the set it says it applied.
        public List<string[]> GearAsked { get; } = new();
        public string? GearSetApplied { get; set; } = "Default";
        public List<string> Notices { get; } = new();
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Now;
        // The name on the `stat` screen.
        public string StatName { get; set; } = "Ermias Asghedom";
        // What the death question reads: the statline's HP, the client's lives,
        // the fight, and the realm's largest HP share for each side (null: that
        // side isn't penalised).
        public (int Hp, int? MaxHp)? Vitals { get; set; }
        public int? Lives { get; set; }
        public bool PvpFight { get; set; }
        public bool MonsterFight { get; set; }
        public int? PvpShare { get; set; }
        public int? PveShare { get; set; }
        public int? OutsideFightShare { get; set; }
        // The Stock engine: a death unequips everything and the board prints its
        // hang-up lines, which the death question then asks for.
        public bool Stock { get; set; } = true;
        // Item names that stay with the character through a death (loyal, cursed).
        public HashSet<string> Stays { get; } = new(StringComparer.OrdinalIgnoreCase);
        // The room the character comes back in is read against the real list of
        // where the dead arrive (HangupDeathArrivalRooms), as the app wires it: an
        // unmodified Stock set, or Paradigm when Stock above is off. Set, the
        // board is a Stock one whose sysop may have edited its rooms, where the
        // list isn't trusted to say "wrong room".
        public bool EditedBoard { get; set; }
        // The graph's word on which rooms the map can't tell an arrival room from.
        public Func<RoomKey, RoomKey, bool>? CouldBeTakenFor { get; set; }
        // Paradigm's Albion: the rooms the graph can walk to from the Burial Vault.
        public HashSet<RoomKey> Albion { get; } = new();
        // What the game-data set has learned from deaths seen: room died in → the
        // room its dead were seen to arrive in (LearnedArrivalRoomStore).
        public Dictionary<RoomKey, RoomKey> LearnedArrivals { get; } = new();
        // Set, the death that taught them was seen in this other room of the land,
        // not in the room itself.
        public RoomKey? LearnedIn { get; set; }
        public Dictionary<RoomKey, string> RoomNames { get; } = new();
        // The game-data set whose rooms the map holds.
        public string? GameDataSet { get; set; } = "alpha";
        public List<MudPlay.Game.Recovery.UnwitnessedDeath> Deaths { get; } = new();
        public HangupItemRecheck Check { get; }
        // What Check put on the wire, through the sender the client binds.
        public List<byte[]> CheckWire { get; } = new();

        public bool Held => Coordinator.IsGateAsserted(MovementCoordinator.HangupItemCheckGate);
        public List<string> Sent => CheckWire.Select(b => Encoding.Latin1.GetString(b)).ToList();

        public Harness()
        {
            Check = new HangupItemRecheck(
                Coordinator,
                profile: () => Named ? Profile : null,
                // A profile save runs the saving hook, as ProfileService does.
                saveProfile: () => Check!.StampForSave(Profile),
                realmKey: () => Realm,
                maxItemsDropped: () => MaxItems,
                inventory: () => Inventory,
                confirmedRoom: () => Room,
                floor: () => Floor,
                fighting: () => Fighting,
                hostilePresent: () => Hostile || Fighting,
                isAutoEnabled: () => AutoAll,
                roomRedisplayFree: () => true,
                collect: (name, count) => Collected.Add((name, count)),
                reapplyGearSet: names => { GearAsked.Add(names.ToArray()); return GearSetApplied; },
                notice: Notices.Add,
                now: () => Now,
                vitals: () => Vitals,
                lives: () => Lives,
                pvpFight: () => PvpFight,
                monsterFight: () => MonsterFight,
                hpShareTop: (pvp, inFight) => pvp ? PvpShare : inFight ? PveShare : OutsideFightShare,
                stockRealm: () => Stock,
                recordDeath: death =>
                {
                    Deaths.Add(death);
                    Profile.DeathHistory ??= new List<DeathRecord>();
                    DeathRecord record = new(death.At, death.Room, death.LivesRemaining, death.Message)
                    {
                        RecordNumber = Profile.DeathHistory.Count + 1,
                        NothingLost = death.NothingLost,
                    };
                    Profile.DeathHistory.Add(record);
                    return record;
                },
                staysOnDeath: Stays.Contains,
                entryRoomSays: (hungUpIn, enteredIn) => MudPlay.Game.Recovery.HangupDeathArrivalRooms.Read(
                    hungUpIn, enteredIn,
                    !Stock ? MudPlay.Game.Recovery.HangupArrivalListTrust.Paradigm
                    : EditedBoard ? MudPlay.Game.Recovery.HangupArrivalListTrust.Unverified
                    : MudPlay.Game.Recovery.HangupArrivalListTrust.VanillaStock,
                    CouldBeTakenFor, Albion.Contains,
                    room => LearnedArrivals.TryGetValue(room, out RoomKey seen)
                        ? new[] { new MudPlay.Game.Recovery.LearnedArrival(seen, LearnedIn ?? room) }
                        : null),
                roomLabel: room => RoomNames.TryGetValue(room, out string? name) ? $"{name} ({room})" : $"room {room}",
                roomsGameData: () => GameDataSet);
            Check.SetWireSender(CheckWire.Add);
        }

        // The list a save left when the link dropped with the character at this HP
        // of 200, with 7 lives, in room 1/3.
        public void StoredAt(int hp, InventorySnapshot before, int? lives = 7, bool pvp = false, RoomRef? room = null,
            bool inFight = true)
        {
            Stored(before, room);
            HeldAtDisconnect list = Profile.HeldAtDisconnect!;
            list.Hp = hp;
            list.MaxHp = 200;
            list.Lives = lives;
            list.PvpFight = pvp;
            list.InCombat = inFight;
            list.Worn = DeathLootCapture.FromSnapshot(before).Equipped;
            list.Carried = DeathLootCapture.LostOf(before);
            list.Coins = before.Currency;
        }

        // The first line a Stock board prints on the entry after a hang-up it
        // didn't let go free.
        public void BoardSaysHungUp() => Check.NoteHangupLoginLine();

        // The login's `stat`, ahead of its `i`.
        public void ReadLives(int lives) => Check.NoteLivesRead(lives, StatName);

        // A whole connection the client played on: the login's `stat` and `i`, then
        // how things stood when the link went down ten minutes later.
        public void PlayAndDrop(int lives, InventorySnapshot held, int hpAtDrop, RoomKey room, bool fight = true)
        {
            Check.NoteConnected();
            Vitals = (200, 200);
            Check.NoteRoomDisplayed();
            Check.OnInGameChanged(true);
            Lives = lives;
            ReadLives(lives);
            ReadInventory(held);
            Room = room;
            Vitals = (hpAtDrop, 200);
            MonsterFight = fight;
            Now = Now.AddMinutes(10);
            Check.NoteLinkDropping();
            Check.NoteDisconnected();
            MonsterFight = false;
        }

        // The next entry: this room, this HP at the first prompt, the board's line
        // or not, the login's `stat` or not, then its `i`. A null hp is a first
        // prompt the statline's HP wasn't read from.
        public void ComeBack(int? hp, int? lives, InventorySnapshot held, bool boardLine = true, RoomKey? room = null)
        {
            Room = room ?? HallsOfTheDead;
            Now = Now.AddMinutes(1);
            Check.NoteConnected();
            if (boardLine) BoardSaysHungUp();
            Check.NoteRoomDisplayed();
            Vitals = hp is { } read0 ? (read0, 200) : null;
            Check.OnInGameChanged(true);
            if (lives is { } read)
            {
                Lives = read;
                ReadLives(read);
            }
            ReadInventory(held);
        }

        // The list a save left on disk before the link dropped, in room 1/3.
        public void Stored(InventorySnapshot before, RoomRef? room = null) =>
            Profile.HeldAtDisconnect = new HeldAtDisconnect
            {
                At = DateTimeOffset.Now.AddMinutes(-2),
                Realm = RealmKey,
                Room = room ?? new RoomRef(1, 3),
                Items = HangupItemPlan.Held(before),
            };

        // Connect, and the login's room display with this floor, then the prompt.
        public void ConnectAndEnter(params string[] floor)
        {
            Check.NoteConnected();
            ShowRoom(floor);
            Check.OnInGameChanged(true);
        }

        public void ShowRoom(params string[] floor)
        {
            Floor.Clear();
            Floor.AddRange(floor);
            if (floor.Length > 0) Check.NoteFloorSurveyed();
            Check.NoteRoomDisplayed();
        }

        public void ReadInventory(InventorySnapshot now)
        {
            Inventory = now;
            Check.OnInventoryRead();
        }

        public void Heartbeats(int count)
        {
            for (int i = 0; i < count; i++) Check.OnHeartbeat();
        }
    }

    // The auto-open engine asks what is coming back, so a container the check is
    // picking up again isn't opened as a new arrival. That is a question about
    // now: counted down as copies arrive, and nothing once the check has ended.
    [Fact]
    public void BeingPickedUp_CountsWhatTheCheckIsStillWaitingFor_AndNothingAfterIt()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["2 oak chest", "torch"]));
        h.ConnectAndEnter("2 oak chest");
        Assert.Equal(0, h.Check.BeingPickedUp("oak chest"));   // nothing asked for yet

        h.ReadInventory(Snap(carried: ["torch"]));
        Assert.Equal([("oak chest", 2)], h.Collected);
        Assert.Equal(2, h.Check.BeingPickedUp("oak chest"));
        Assert.Equal(0, h.Check.BeingPickedUp("torch"));

        h.Check.OnItemTaken("oak chest", 1);
        Assert.Equal(1, h.Check.BeingPickedUp("oak chest"));

        h.Check.OnItemTaken("oak chest", 1);
        Assert.Equal(0, h.Check.BeingPickedUp("oak chest"));
    }

    // A check that ended with a container still missing leaves it on its report
    // for the rest of the session. That must not stop later chests being opened.
    [Fact]
    public void AContainerTheCheckNeverFound_DoesNotStopLaterOnesBeingOpened()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["oak chest", "torch"]));
        h.ConnectAndEnter("torch");                 // the chest isn't on the floor
        h.ReadInventory(Snap());
        h.Check.OnItemTaken("torch", 1);
        Assert.False(h.Held);                       // the check is over
        Assert.Equal([("oak chest", 1)], h.Check.LastStillMissing);

        List<string> carried = ["torch"];
        List<string> opened = [];
        AutoOpenManager engine = new(
            carriedItems: () => carried,
            resolve: name => name == "oak chest" ? new AutoOpenManager.ResolvedOpen(907, name, true) : null,
            isEnabled: () => true,
            isLoaded: () => true,
            open: name => { opened.Add(name); return true; })
        {
            ComingBack = h.Check.BeingPickedUp,     // as AppServices wires it
        };
        engine.OnInventoryChanged();                // baseline

        for (int kill = 0; kill < 3; kill++)
        {
            carried = ["torch", "oak chest"];       // a fresh chest off a kill
            engine.OnInventoryChanged();
            carried = ["torch"];                    // opened and gone
            engine.OnInventoryChanged();
            engine.OnOpenSettled(new ChestOpenTracker.OpenResult([("ruby", 1)], CurrencyHoldings.Empty, Read: true));
        }

        Assert.Equal(["oak chest", "oak chest", "oak chest"], opened);
    }

    [Fact]
    public void ARealmThatDropsNoItems_IsLeftAlone()
    {
        Harness h = new() { MaxItems = 0 };
        h.Stored(Snap(carried: ["2 torch"]));

        h.ConnectAndEnter("2 torch");
        h.ReadInventory(Snap());

        Assert.False(h.Held);
        Assert.Empty(h.Collected);
        Assert.Empty(h.Sent);
    }

    // No list, nothing said and nothing held: a dial with nothing to check
    // leaves no trace in the gate history.
    [Fact]
    public void NoStoredList_NothingToCheck_AndNoGateTouched()
    {
        Harness h = new();

        h.ConnectAndEnter("2 torch");
        h.ReadInventory(Snap());

        Assert.Empty(h.Collected);
        Assert.Empty(h.Coordinator.History);
        Assert.Equal("idle", h.Check.Status);
    }

    // A list taken on another board or realm says nothing about the pack here.
    [Fact]
    public void AListFromAnotherRealm_IsIgnored()
    {
        Harness h = new() { Realm = "Board/PvP" };
        h.Stored(Snap(carried: ["2 torch"]));

        h.ConnectAndEnter("2 torch");
        h.ReadInventory(Snap());

        Assert.Empty(h.Collected);
        Assert.Empty(h.Coordinator.History);
    }

    // The hold goes up at the connect, ahead of the first game prompt a loop
    // restarts on, and comes down when the inventory read shows nothing short.
    [Fact]
    public void MovementIsHeldFromTheConnect_UntilTheInventoryShowsNothingMissing()
    {
        Harness h = new();
        h.Stored(Snap(worn: [("dagger", "Weapon Hand")], carried: ["2 torch"]));

        h.Check.NoteConnected();
        Assert.True(h.Held);
        h.ShowRoom();
        h.Check.OnInGameChanged(true);
        Assert.True(h.Held);

        h.ReadInventory(Snap(worn: [("dagger", "Weapon Hand")], carried: ["2 torch"]));

        Assert.False(h.Held);
        Assert.Empty(h.Collected);
        Assert.Empty(h.Sent);
        Assert.Empty(h.Notices);
        Assert.Contains("nothing held before is missing", h.Check.LastOutcome);
    }

    // What comes back is handed to the equipment manager by name; which pieces go
    // on is its business.
    [Fact]
    public void MissingItemsOnTheFloor_ArePickedUp_AndTheEquipmentManagerIsAsked()
    {
        Harness h = new();
        h.Stored(Snap(worn: [("chainmail hauberk", "Torso")], carried: ["3 torch", "rope"]));

        h.ConnectAndEnter("2 torch", "chainmail hauberk", "a rusty dagger");
        h.ReadInventory(Snap(carried: ["torch"]));

        // Only what is ours and lying here: not the dagger, and no get for the rope.
        Assert.Equal([("chainmail hauberk", 1), ("torch", 2)], h.Collected);
        Assert.True(h.Held);
        Assert.Empty(h.GearAsked);

        h.Check.OnItemTaken("chainmail hauberk", 1);
        h.Check.OnItemTaken("torch", 2);

        Assert.False(h.Held);
        Assert.Equal(new[] { "chainmail hauberk", "torch" }, Assert.Single(h.GearAsked));
        Assert.Equal([("rope", 1)], h.Check.LastStillMissing);
        Assert.Equal(3, h.Check.LastMissing.Count);
        // The terminal hears what came back, and what didn't.
        Assert.Equal(2, h.Notices.Count);
        Assert.Contains("picked up chainmail hauberk, 2 torch", h.Notices[0]);
        Assert.Contains("rope missing", h.Notices[1]);
    }

    // Stock answers one `You took` a copy; the count is reached over several.
    [Fact]
    public void GetsAnsweredOneCopyAtATime_FinishWhenTheLastLands()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["3 torch"]));
        h.ConnectAndEnter("3 torch");
        h.ReadInventory(Snap());

        h.Check.OnItemTaken("torch", 1);
        h.Check.OnItemTaken("torch", 1);
        Assert.True(h.Held);
        h.Check.OnItemTaken("torch", 1);

        Assert.False(h.Held);
        Assert.Empty(h.Check.LastStillMissing);
    }

    // A get the game refuses prints no `You took`: movement isn't held on it, and
    // with nothing back there is nothing to ask the equipment manager for.
    [Fact]
    public void GetsThatAreNeverAnswered_EndTheCheck_WithTheItemStillMissing()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["torch"]));
        h.ConnectAndEnter("torch");
        h.ReadInventory(Snap());
        Assert.True(h.Held);

        h.Heartbeats(3);

        Assert.False(h.Held);
        Assert.Single(h.Check.LastStillMissing);
        Assert.Empty(h.GearAsked);
        Assert.Contains("didn't confirm", Assert.Single(h.Notices));
    }

    // However the check ends with something still missing, the terminal says what
    // and why: not only when the floor was empty.
    [Fact]
    public void EveryEndingWithSomethingMissing_SaysSoOnTheTerminal()
    {
        Harness autoOff = new() { AutoAll = false };
        autoOff.Stored(Snap(carried: ["torch"]));
        autoOff.ConnectAndEnter("torch");
        autoOff.ReadInventory(Snap());
        Assert.Contains("Auto-All is off", Assert.Single(autoOff.Notices));

        Harness elsewhere = new() { Room = new RoomKey(1, 99) };
        elsewhere.Stored(Snap(carried: ["torch"]));
        elsewhere.ConnectAndEnter("torch");
        elsewhere.ReadInventory(Snap());
        Assert.Contains("1/99", Assert.Single(elsewhere.Notices));

        Harness hostile = new() { Hostile = true };
        hostile.Stored(Snap(carried: ["torch"]));
        hostile.ConnectAndEnter("torch");
        hostile.ReadInventory(Snap());
        Assert.Contains("hostile", Assert.Single(hostile.Notices));
    }

    // ----- No more than the board drops ---------------------------------

    [Fact]
    public void NoMoreIsPickedUp_ThanTheRealmSaysAHangupDrops()
    {
        Harness h = new() { MaxItems = 2 };
        h.Stored(Snap(worn: [("chainmail hauberk", "Torso")], carried: ["5 torch", "rope"]));

        // Everything is gone and all of it lies here: a list this far out can't be
        // one hang-up's doing, so only two copies are the board's.
        h.ConnectAndEnter("chainmail hauberk", "5 torch", "rope");
        h.ReadInventory(Snap());

        Assert.Equal([("chainmail hauberk", 1), ("torch", 1)], h.Collected);

        h.Check.OnItemTaken("chainmail hauberk", 1);
        h.Check.OnItemTaken("torch", 1);

        Assert.False(h.Held);
        Assert.Equal([("torch", 4), ("rope", 1)], h.Check.LastStillMissing);
    }

    // ----- Where and when it holds back ---------------------------------

    [Fact]
    public void ComingInSomewhereElse_PicksNothingUp()
    {
        Harness h = new() { Room = new RoomKey(1, 99) };
        h.Stored(Snap(carried: ["2 torch"]), new RoomRef(1, 3));

        h.ConnectAndEnter("2 torch");
        h.ReadInventory(Snap());

        Assert.Empty(h.Collected);
        Assert.False(h.Held);
        Assert.Contains("1/99", h.Check.LastOutcome);
    }

    // The login's display moved the map to another room: known to be elsewhere
    // without drawing the room a second time.
    [Fact]
    public void ComingInSomewhereElse_AsksForNoRedisplay()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["torch"]));
        h.Check.NoteConnected();
        h.ShowRoom("torch");
        h.Room = new RoomKey(1, 99);
        h.Check.OnRoomChanged();
        h.Check.OnInGameChanged(true);

        h.ReadInventory(Snap());

        Assert.Empty(h.Sent);
        Assert.Empty(h.Collected);
        Assert.False(h.Held);
    }

    // The room the character left wasn't known, and the map isn't sure where it
    // is now: the floor of the room it is in is checked all the same.
    [Fact]
    public void RoomNotKnown_ChecksTheFloorOfTheRoomItIsIn()
    {
        Harness h = new() { Room = null };
        h.Stored(Snap(carried: ["torch"]));
        h.Profile.HeldAtDisconnect!.Room = null;

        h.ConnectAndEnter("torch");
        h.ReadInventory(Snap());

        Assert.Equal([("torch", 1)], h.Collected);
    }

    // The floor list the tracker holds is from before the link dropped when the
    // room printed none this time: an empty floor prints no list.
    [Fact]
    public void AFloorListFromBeforeTheConnect_IsNotTheFloorNow()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["2 torch"]));
        h.Floor.Add("2 torch");

        h.Check.NoteConnected();
        h.Check.NoteRoomDisplayed();
        h.Check.OnInGameChanged(true);
        h.ReadInventory(Snap());

        Assert.Empty(h.Collected);
        Assert.Contains("none of them is on the floor", h.Check.LastOutcome);
        Assert.Contains("2 torch missing", Assert.Single(h.Notices));
    }

    [Fact]
    public void AFightInTheRoom_ThePickupWaitsForItToEnd()
    {
        Harness h = new() { Fighting = true };
        h.Stored(Snap(carried: ["2 torch"]));

        h.ConnectAndEnter("2 torch");
        h.ReadInventory(Snap());
        h.Heartbeats(5);

        Assert.Empty(h.Collected);
        Assert.True(h.Held);

        h.Fighting = false;
        h.Check.OnRoomObserved();

        Assert.Equal([("torch", 2)], h.Collected);
    }

    // Auto-All can be switched off while a fight is waited out.
    [Fact]
    public void AutoAllSwitchedOffDuringTheFight_NothingIsPickedUpWhenItEnds()
    {
        Harness h = new() { Fighting = true };
        h.Stored(Snap(carried: ["2 torch"]));
        h.ConnectAndEnter("2 torch");
        h.ReadInventory(Snap());

        h.AutoAll = false;
        h.Fighting = false;
        h.Check.OnRoomObserved();

        Assert.Empty(h.Collected);
        Assert.False(h.Held);
        Assert.Contains("Auto-All is off", h.Check.LastOutcome);
    }

    [Fact]
    public void LeavingTheRoomMidFight_EndsTheCheck()
    {
        Harness h = new() { Fighting = true };
        h.Stored(Snap(carried: ["torch"]));
        h.ConnectAndEnter("torch");
        h.ReadInventory(Snap());

        h.Check.OnRoomChanged();
        h.Fighting = false;
        h.Check.OnRoomObserved();

        Assert.Empty(h.Collected);
        Assert.False(h.Held);
    }

    // A monster the client isn't set to fight won't go away by itself: nothing is
    // picked up under it, and nothing waits on it.
    [Fact]
    public void AHostileNotBeingFought_LeavesTheItemsOnTheFloor()
    {
        Harness h = new() { Hostile = true };
        h.Stored(Snap(carried: ["torch"]));

        h.ConnectAndEnter("torch");
        h.ReadInventory(Snap());

        Assert.Empty(h.Collected);
        Assert.False(h.Held);
        Assert.Contains("hostile", h.Check.LastOutcome);
    }

    [Fact]
    public void NoRoomDisplayRead_AsksForOneRedisplay_ThenPicksUp()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["torch"]));
        h.Check.NoteConnected();
        h.Check.OnInGameChanged(true);

        h.ReadInventory(Snap());

        Assert.Equal(["\r"], h.Sent);
        Assert.Empty(h.Collected);

        h.ShowRoom("torch");

        Assert.Equal([("torch", 1)], h.Collected);
        Assert.Equal(["\r"], h.Sent);
    }

    // A dark room, or a blinded character, gets no room display. That is the floor
    // not being seen, which is not the items being gone.
    [Fact]
    public void ARedisplayThatNeverComes_DoesNotHoldMovement_AndSaysTheFloorWasNotSeen()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["torch"]));
        h.Check.NoteConnected();
        h.Check.OnInGameChanged(true);
        h.ReadInventory(Snap());

        h.Heartbeats(2);

        Assert.False(h.Held);
        Assert.Empty(h.Collected);
        Assert.Contains("couldn't be seen", h.Check.LastOutcome);
        Assert.DoesNotContain("on the floor", h.Check.LastOutcome);
        Assert.Contains("couldn't be seen", Assert.Single(h.Notices));
    }

    // The realm's figure was set to 0 between the connect and the pickup.
    [Fact]
    public void TheRealmSetToDropNothingMidCheck_NothingIsPickedUp()
    {
        Harness h = new() { Fighting = true };
        h.Stored(Snap(carried: ["2 torch"]));
        h.ConnectAndEnter("2 torch");
        h.ReadInventory(Snap());
        Assert.True(h.Held);

        h.MaxItems = 0;
        h.Fighting = false;
        h.Check.OnRoomObserved();

        Assert.Empty(h.Collected);
        Assert.False(h.Held);
        Assert.Contains("no longer", h.Check.LastOutcome);
    }

    // The hold doesn't stop a move typed by hand. Once one has gone out, only a map
    // that is sure of both rooms can say the floor here is the one the items fell on.
    [Fact]
    public void AMoveSentBeforeTheCompare_NeedsTheMapToVouchForTheRoom()
    {
        Harness unsure = new() { Room = null };
        unsure.Stored(Snap(carried: ["torch"]));
        unsure.ConnectAndEnter("torch");
        unsure.Check.NoteMoveSent();
        unsure.ShowRoom("torch");
        unsure.ReadInventory(Snap());
        Assert.Empty(unsure.Collected);
        Assert.False(unsure.Held);

        // A move that bonked: the map still has the room.
        Harness sure = new();
        sure.Stored(Snap(carried: ["torch"]));
        sure.ConnectAndEnter("torch");
        sure.Check.NoteMoveSent();
        sure.ReadInventory(Snap());
        Assert.Equal([("torch", 1)], sure.Collected);
    }

    [Fact]
    public void AutoAllOff_NothingIsSent()
    {
        Harness h = new() { AutoAll = false };
        h.Stored(Snap(carried: ["torch"]));

        h.ConnectAndEnter("torch");
        h.Heartbeats(4);
        Assert.Empty(h.Sent);
        Assert.False(h.Held);

        // And with the read in hand, the missing item is reported and left.
        Harness read = new() { AutoAll = false };
        read.Stored(Snap(carried: ["torch"]));
        read.ConnectAndEnter("torch");
        read.ReadInventory(Snap());

        Assert.Empty(read.Collected);
        Assert.Empty(read.Sent);
        Assert.False(read.Held);
        Assert.Single(read.Check.LastMissing);
    }

    [Fact]
    public void OnePassAConnection_ALaterInventoryReadStartsNothing()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["2 torch"]));
        h.ConnectAndEnter();
        h.ReadInventory(Snap());
        Assert.False(h.Held);

        h.ShowRoom("2 torch");
        h.ReadInventory(Snap());

        Assert.Empty(h.Collected);
        Assert.False(h.Held);
    }

    // Out to the board's menu and back in on the same link: no second check, and
    // the list goes on being written.
    [Fact]
    public void ASecondEntryOnOneLink_StartsNoCheck_ButTheListIsStillWritten()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["torch"]));
        h.ConnectAndEnter();
        h.ReadInventory(Snap(carried: ["torch"]));
        h.Check.OnInGameChanged(false);

        h.Check.OnInGameChanged(true);
        h.ShowRoom("torch");
        h.Inventory = Snap(carried: ["torch", "rope"]);
        h.Check.StampForSave(h.Profile);

        Assert.False(h.Held);
        Assert.Empty(h.Collected);
        Assert.Equal(2, h.Profile.HeldAtDisconnect!.Items.Count);
    }

    // ----- The hold never outlasts its reason ---------------------------

    [Fact]
    public void ALinkThatDropsBeforeTheGame_GivesTheHoldUp_AndKeepsTheList()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["torch"]));
        HeldAtDisconnect stored = h.Profile.HeldAtDisconnect!;

        h.Check.NoteConnected();
        Assert.True(h.Held);
        h.Check.NoteDisconnected();

        Assert.False(h.Held);
        Assert.Same(stored, h.Profile.HeldAtDisconnect);
    }

    // An entry made by hand sends no `i`: one is asked for, once. With still no
    // read the hold is given up, the list is kept, and the read that does come is
    // compared without the hold.
    [Fact]
    public void NoInventoryRead_OneIsAskedFor_ThenTheHoldIsGivenUp_AndALateReadIsStillCompared()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["torch"]));
        HeldAtDisconnect stored = h.Profile.HeldAtDisconnect!;
        h.ConnectAndEnter("torch");

        h.Heartbeats(3);
        Assert.Empty(h.Sent);
        h.Heartbeats(1);
        Assert.Equal(["i\r"], h.Sent);
        Assert.True(h.Held);

        h.Heartbeats(4);

        Assert.Equal(["i\r"], h.Sent);
        Assert.False(h.Held);
        Assert.Same(stored, h.Profile.HeldAtDisconnect);
        Assert.Contains("without the hold", h.Check.LastOutcome);

        // A save in between can't write over the list: no inventory has been read.
        h.Check.StampForSave(h.Profile);
        Assert.Same(stored, h.Profile.HeldAtDisconnect);

        // Still standing in the room it came in at: the late read picks up.
        h.Heartbeats(30);
        h.ReadInventory(Snap());

        Assert.Equal(["i\r"], h.Sent);
        Assert.Equal([("torch", 1)], h.Collected);
        Assert.False(h.Held);
    }

    // The character walked on before the late read: what is missing is reported,
    // and the floor here, some other room's, is left alone.
    [Fact]
    public void ALateInventoryRead_AfterMovingOn_OnlyReports()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["torch"]));
        h.ConnectAndEnter("torch");
        h.Heartbeats(8);
        Assert.False(h.Held);

        h.Room = new RoomKey(1, 4);
        h.Check.OnRoomChanged();
        h.ShowRoom("torch");
        h.ReadInventory(Snap());

        Assert.Empty(h.Collected);
        Assert.Equal([("torch", 1)], h.Check.LastMissing);
        Assert.Equal("idle", h.Check.Status);
    }

    // A map that isn't sure keeps its old room, or has none, so walking doesn't
    // show as a room change. Without the hold nothing kept the character where it
    // came in, and a floor the map can't vouch for is left alone.
    [Fact]
    public void ALateInventoryRead_WithTheRoomNotKnown_PicksNothingUp()
    {
        // The map has no room now.
        Harness unlocated = new() { Room = null };
        unlocated.Stored(Snap(carried: ["torch"]));
        unlocated.ConnectAndEnter("torch");
        unlocated.Heartbeats(8);
        Assert.False(unlocated.Held);
        unlocated.ShowRoom("torch");
        unlocated.ReadInventory(Snap());
        Assert.Empty(unlocated.Collected);
        Assert.Contains("torch missing", Assert.Single(unlocated.Notices));

        // The room the character left wasn't recorded; the same after Reset States.
        Harness unrecorded = new();
        unrecorded.Stored(Snap(carried: ["torch"]));
        unrecorded.Profile.HeldAtDisconnect!.Room = null;
        unrecorded.ConnectAndEnter("torch");
        unrecorded.Check.Cancel("reset");
        unrecorded.ReadInventory(Snap());
        Assert.Empty(unrecorded.Collected);
        Assert.Equal("idle", unrecorded.Check.Status);
    }

    // A death while the read is still awaited, the hold already given up: the list
    // is void, and the read the graveyard brings compares nothing.
    [Fact]
    public void ADeathWhileAwaitingTheInventory_WithTheHoldGivenUp_EndsTheCheck()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["torch"]));
        h.ConnectAndEnter("torch");
        h.Heartbeats(8);
        Assert.False(h.Held);
        Assert.Contains("movement no longer held", h.Check.Status);

        h.Check.OnPlayerDied();

        Assert.Equal("idle", h.Check.Status);
        Assert.Null(h.Profile.HeldAtDisconnect);
        Assert.Empty(h.Notices);

        h.ReadInventory(Snap());
        Assert.Empty(h.Collected);
        Assert.Empty(h.Check.LastMissing);
    }

    // A statline the prompt scanner can't read gives no game prompt. Only the game
    // draws a room, so a room display is entry enough.
    [Fact]
    public void ARoomDisplayWithNoGamePrompt_CountsAsEnteringTheGame()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["torch"]));
        h.Check.NoteConnected();

        h.ShowRoom("torch");
        h.ReadInventory(Snap());

        Assert.Equal([("torch", 1)], h.Collected);

        // And leaving is seen at the disconnect, though no prompt ever said "in".
        h.Check.OnItemTaken("torch", 1);
        h.Inventory = Snap(carried: ["torch"]);
        h.Check.NoteDisconnected();
        Assert.Equal("torch", Assert.Single(h.Profile.HeldAtDisconnect!.Items).Name);
    }

    // A login that stops at the board's menu can wait as long as the user likes;
    // the hold can't.
    [Fact]
    public void TheGameNotEnteredInThreeMinutes_TheHoldIsGivenUp_AndTheListKept()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["torch"]));
        HeldAtDisconnect stored = h.Profile.HeldAtDisconnect!;
        h.Check.NoteConnected();

        h.Heartbeats(179);
        Assert.True(h.Held);
        h.Heartbeats(1);

        Assert.False(h.Held);
        Assert.Same(stored, h.Profile.HeldAtDisconnect);

        // Entered at last: compared all the same, without the hold or an `i` of its own.
        h.ShowRoom("torch");
        h.Check.OnInGameChanged(true);
        h.Heartbeats(10);
        Assert.Empty(h.Sent);
        h.ReadInventory(Snap());

        Assert.Equal([("torch", 1)], h.Collected);
        Assert.False(h.Held);
    }

    // Reset States frees the hold from wherever the check stands. Before the
    // comparison the list is kept and still compared; after it the check stops.
    [Fact]
    public void ResetStates_FreesTheHold_FromEveryPhase()
    {
        // Waiting for the game.
        Harness entry = new();
        entry.Stored(Snap(carried: ["torch"]));
        entry.Check.NoteConnected();
        entry.Check.Cancel("reset");
        Assert.False(entry.Held);

        // Waiting for the inventory: the late read is compared.
        Harness inventory = new();
        inventory.Stored(Snap(carried: ["torch"]));
        HeldAtDisconnect stored = inventory.Profile.HeldAtDisconnect!;
        inventory.ConnectAndEnter("torch");
        inventory.Check.Cancel("reset");
        Assert.False(inventory.Held);
        inventory.Heartbeats(10);
        Assert.Empty(inventory.Sent);
        Assert.Same(stored, inventory.Profile.HeldAtDisconnect);
        inventory.ReadInventory(Snap());
        Assert.Equal([("torch", 1)], inventory.Collected);

        // Waiting for a room display.
        Harness room = new();
        room.Stored(Snap(carried: ["torch"]));
        room.Check.NoteConnected();
        room.Check.OnInGameChanged(true);
        room.ReadInventory(Snap());
        Assert.True(room.Held);
        room.Check.Cancel("reset");
        Assert.False(room.Held);
        Assert.Equal("idle", room.Check.Status);

        // Waiting out a fight.
        Harness fight = new() { Fighting = true };
        fight.Stored(Snap(carried: ["torch"]));
        fight.ConnectAndEnter("torch");
        fight.ReadInventory(Snap());
        fight.Check.Cancel("reset");
        Assert.False(fight.Held);
        fight.Fighting = false;
        fight.Check.OnRoomObserved();
        Assert.Empty(fight.Collected);

        // Picking up.
        Harness picking = new();
        picking.Stored(Snap(carried: ["torch"]));
        picking.ConnectAndEnter("torch");
        picking.ReadInventory(Snap());
        Assert.True(picking.Held);
        picking.Check.Cancel("reset");
        Assert.False(picking.Held);
        Assert.Equal("idle", picking.Check.Status);
    }

    // Another character loaded: the check was the other one's, and so is what it found.
    [Fact]
    public void AnotherProfileLoaded_FreesTheHold_FromEveryPhase_AndForgetsTheLastCheck()
    {
        Harness entry = new();
        entry.Stored(Snap(carried: ["torch"]));
        entry.Check.NoteConnected();
        entry.Check.OnProfileLoaded();
        Assert.False(entry.Held);
        Assert.Equal("idle", entry.Check.Status);

        Harness inventory = new();
        inventory.Stored(Snap(carried: ["torch"]));
        inventory.ConnectAndEnter("torch");
        inventory.Check.OnProfileLoaded();
        Assert.False(inventory.Held);
        inventory.ReadInventory(Snap());
        Assert.Empty(inventory.Collected);

        Harness fight = new() { Fighting = true };
        fight.Stored(Snap(carried: ["torch"]));
        fight.ConnectAndEnter("torch");
        fight.ReadInventory(Snap());
        Assert.Single(fight.Check.LastMissing);
        fight.Check.OnProfileLoaded();
        Assert.False(fight.Held);
        Assert.Empty(fight.Check.LastMissing);
        Assert.Empty(fight.Check.LastStillMissing);
        Assert.Contains("none", fight.Check.LastOutcome);

        Harness picking = new();
        picking.Stored(Snap(carried: ["torch"]));
        picking.ConnectAndEnter("torch");
        picking.ReadInventory(Snap());
        picking.Check.OnProfileLoaded();
        Assert.False(picking.Held);
        // The new character's profile isn't stamped from the old one's inventory.
        picking.Profile.HeldAtDisconnect = null;
        picking.Check.StampForSave(picking.Profile);
        Assert.Null(picking.Profile.HeldAtDisconnect);
    }

    // ----- When the list is written -------------------------------------

    [Fact]
    public void TheList_IsWrittenOnlyInTheGame_WithAnInventoryReadOnThisConnection()
    {
        Harness h = new();
        h.Inventory = Snap(carried: ["2 torch"]);

        // At the board's login: the record in memory is the last session's.
        h.Check.NoteConnected();
        h.Check.StampForSave(h.Profile);
        Assert.Null(h.Profile.HeldAtDisconnect);

        // In the game, before the login's `i`: still nothing new known.
        h.Check.OnInGameChanged(true);
        h.Check.StampForSave(h.Profile);
        Assert.Null(h.Profile.HeldAtDisconnect);

        h.ReadInventory(Snap(worn: [("dagger", "Weapon Hand")], carried: ["2 torch"], keys: ["brass key"]));
        h.Check.StampForSave(h.Profile);

        HeldAtDisconnect held = h.Profile.HeldAtDisconnect!;
        Assert.Equal(Harness.RealmKey, held.Realm);
        Assert.Equal(3, held.Room!.Room);
        Assert.Equal(1, held.PenaltiesSpanned);
        Assert.Equal(3, held.Items.Count);
        Assert.Equal(2, held.Items.Single(i => i.Name == "torch").Count);
    }

    // The default profile is the template new characters are made from, and some
    // other profile being saved isn't the one the inventory belongs to.
    [Fact]
    public void TheList_IsWrittenOnlyToTheNamedCharacterThatIsLoaded()
    {
        Harness unnamed = new() { Named = false };
        unnamed.Check.NoteConnected();
        unnamed.Check.OnInGameChanged(true);
        unnamed.ReadInventory(Snap(carried: ["torch"]));
        unnamed.Check.StampForSave(unnamed.Profile);
        Assert.Null(unnamed.Profile.HeldAtDisconnect);

        Harness h = new();
        h.Check.NoteConnected();
        h.Check.OnInGameChanged(true);
        h.ReadInventory(Snap(carried: ["torch"]));
        CharacterProfile other = new();
        h.Check.StampForSave(other);
        Assert.Null(other.HeldAtDisconnect);
    }

    // Leaving the game saves, so the list is as of the moment the link dropped.
    [Fact]
    public void LeavingTheGame_SavesWhatIsHeldThen()
    {
        Harness h = new();
        h.Check.NoteConnected();
        h.Check.OnInGameChanged(true);
        h.ReadInventory(Snap(carried: ["torch"]));

        h.Inventory = Snap(carried: ["torch", "rope"]);
        h.Check.OnInGameChanged(false);

        Assert.Equal(2, h.Profile.HeldAtDisconnect!.Items.Count);

        // Out of the game, a later save changes nothing.
        h.Inventory = Snap();
        h.Check.StampForSave(h.Profile);
        Assert.Equal(2, h.Profile.HeldAtDisconnect!.Items.Count);
    }

    // The inventory record names what went into the deathpile until it is read
    // again at the graveyard, and the link can drop first. A list written from it
    // would name the whole pile as held, and the next entry would take it all for
    // a hang-up's drop.
    [Fact]
    public void ADeath_StoresNoList_UntilAnInventoryIsReadAgain()
    {
        Harness h = new();
        h.Check.NoteConnected();
        h.Check.OnInGameChanged(true);
        h.ReadInventory(Snap(worn: [("chainmail hauberk", "Torso")], carried: ["2 torch"]));
        h.Check.StampForSave(h.Profile);
        Assert.NotNull(h.Profile.HeldAtDisconnect);

        h.Check.OnPlayerDied();
        Assert.Null(h.Profile.HeldAtDisconnect);

        // A save after the death, and the link dropping, still store none.
        h.Check.StampForSave(h.Profile);
        h.Check.OnInGameChanged(false);
        Assert.Null(h.Profile.HeldAtDisconnect);

        // Back in with a fresh read: the list is what is really held.
        h.Check.NoteConnected();
        h.Check.OnInGameChanged(true);
        h.ReadInventory(Snap(carried: ["torch"]));
        h.Check.StampForSave(h.Profile);
        Assert.Equal("torch", Assert.Single(h.Profile.HeldAtDisconnect!.Items).Name);
    }

    [Fact]
    public void ADeathMidCheck_EndsIt_AndStoresNoList()
    {
        Harness h = new() { Fighting = true };
        h.Stored(Snap(carried: ["2 torch"]));
        h.ConnectAndEnter("2 torch");
        h.ReadInventory(Snap());
        Assert.True(h.Held);

        h.Check.OnPlayerDied();

        Assert.False(h.Held);
        Assert.Null(h.Profile.HeldAtDisconnect);
        h.Fighting = false;
        h.Check.OnRoomObserved();
        Assert.Empty(h.Collected);
    }

    // The stored list must outlast a second drop that comes before the login's
    // inventory read: the record in memory is no newer than the list.
    [Fact]
    public void ADropBeforeTheInventoryRead_KeepsTheListStillToBeCompared()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["3 torch"]));
        HeldAtDisconnect stored = h.Profile.HeldAtDisconnect!;
        h.Inventory = Snap(carried: ["torch"]);

        h.ConnectAndEnter("2 torch");
        h.Check.OnInGameChanged(false);
        h.Check.NoteDisconnected();

        Assert.Same(stored, h.Profile.HeldAtDisconnect);
        Assert.False(h.Held);
    }

    // Hanging up again mid-fight, before the first drop was picked up: the next
    // entry still knows about it, and two drops may have cost twice the count.
    [Fact]
    public void ADropMidCheck_CarriesTheUnfoundItemsToTheNextEntry()
    {
        Harness h = new() { Fighting = true, MaxItems = 2 };
        h.Stored(Snap(worn: [("chainmail hauberk", "Torso")], carried: ["2 torch"]));
        h.ConnectAndEnter("chainmail hauberk", "2 torch");
        h.ReadInventory(Snap(carried: ["rope"]));

        h.Check.OnInGameChanged(false);
        h.Check.NoteDisconnected();

        HeldAtDisconnect carriedOver = h.Profile.HeldAtDisconnect!;
        Assert.Equal(2, carriedOver.PenaltiesSpanned);
        Assert.Equal(2, carriedOver.Items.Single(i => i.Name == "torch").Count);
        Assert.Equal(1, carriedOver.Items.Single(i => i.Name == "chainmail hauberk").Count);

        // The second drop took the rope too. Four copies may be the board's now.
        h.Fighting = false;
        h.ConnectAndEnter("chainmail hauberk", "2 torch", "rope");
        h.ReadInventory(Snap());

        Assert.Equal([("rope", 1), ("chainmail hauberk", 1), ("torch", 2)], h.Collected);
    }

    // Each check cut short adds a drop to what the list covers.
    [Fact]
    public void TwoChecksCutShort_TheListCoversThreeDrops()
    {
        Harness h = new() { Fighting = true, MaxItems = 1 };
        h.Stored(Snap(carried: ["3 torch"]));

        h.ConnectAndEnter("3 torch");
        h.ReadInventory(Snap());
        h.Check.OnInGameChanged(false);
        h.Check.NoteDisconnected();
        Assert.Equal(2, h.Profile.HeldAtDisconnect!.PenaltiesSpanned);

        h.ConnectAndEnter("3 torch");
        h.ReadInventory(Snap());
        h.Check.OnInGameChanged(false);
        h.Check.NoteDisconnected();
        Assert.Equal(3, h.Profile.HeldAtDisconnect!.PenaltiesSpanned);

        h.Fighting = false;
        h.ConnectAndEnter("3 torch");
        h.ReadInventory(Snap());
        Assert.Equal([("torch", 3)], h.Collected);
    }

    // What a finished check didn't find is not carried: it isn't looked for again.
    [Fact]
    public void AFinishedCheck_LeavesOnlyWhatIsHeldOnTheList()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["torch", "rope"]));
        h.ConnectAndEnter();

        h.ReadInventory(Snap(carried: ["rope"]));

        HeldAtDisconnect list = h.Profile.HeldAtDisconnect!;
        Assert.Equal("rope", Assert.Single(list.Items).Name);
        Assert.Equal(1, list.PenaltiesSpanned);
    }

    // ----- A hang-up the penalty killed for -----------------------------
    //
    // A board that penalises a hang-up kills a dropped character, and a standing
    // one its HP share takes under the death threshold, after the link is gone. The
    // same pass works that out first. Only a life lost says "died": the count the
    // list has against a `stat` read on this connection.

    private static readonly InventorySnapshot Geared =
        Snap(worn: [("chainmail hauberk", "Torso")], carried: ["3 torch", "rope"]) with
        {
            Currency = new CurrencyHoldings(0, 0, 25, 0, 0, 2500),
        };

    // What a record made for a hang-up in room 1/3 says of the room the character
    // came back in, when that is the temple (no room names are given to the
    // harness in these tests).
    private const string TempleFromRoom3 = "room 1/2189, where the dead of room 1/3 arrive";

    // Back in the temple, full HP, a life down, the board saying it was a hang-up.
    private static void ComeBackDead(Harness h, int? lives = 6, params string[] floor)
    {
        h.Room = HallsOfTheDead;
        h.Check.NoteConnected();
        h.BoardSaysHungUp();
        h.ShowRoom(floor);
        h.Vitals = (200, 200);
        h.Check.OnInGameChanged(true);
        if (lives is { } read) h.ReadLives(read);
    }

    // The list's room is a number of the game data the map held when it was written,
    // and the death it turns into is stamped with that set, not with one switched to
    // before the next entry.
    [Fact]
    public void TheHeldList_IsStampedWithItsGameData_AndTheDeathKeepsIt()
    {
        Harness h = new() { PveShare = 50 };
        h.ConnectAndEnter();
        h.ReadInventory(Geared);
        h.Check.NoteLinkDropping();
        h.Check.NoteDisconnected();
        Assert.Equal("alpha", h.Profile.HeldAtDisconnect!.GameData);

        h.StoredAt(-5, Geared);
        h.Profile.HeldAtDisconnect!.GameData = "alpha";
        h.GameDataSet = "beta";
        ComeBackDead(h, 6);
        h.ReadInventory(Snap());

        Assert.Equal("alpha", Assert.Single(h.Deaths).GameData);
    }

    [Fact]
    public void ADroppedHangUp_ThatCostALife_IsRecordedAsADeath_AndThePickupStandsDown()
    {
        Harness h = new() { PveShare = 50 };
        h.StoredAt(-5, Geared);
        DateTimeOffset dropped = h.Profile.HeldAtDisconnect!.At;

        // Torches lie in the temple too: they are not ours to take as "dropped".
        ComeBackDead(h, 6, "3 torch");
        Assert.True(h.Held);
        h.ReadInventory(Snap());

        MudPlay.Game.Recovery.UnwitnessedDeath death = Assert.Single(h.Deaths);
        Assert.Equal(new RoomRef(1, 3).Room, death.Room!.Room);
        Assert.Equal(dropped, death.At);
        Assert.Equal(6, death.LivesRemaining);
        // Back in the temple, where the dead of room 1/3 arrive: the line says so.
        Assert.Equal($"{HangupItemRecheck.DeathMessage} It came back in {TempleFromRoom3}.", death.Message);
        DeathItem worn = Assert.Single(death.Equipped!);
        Assert.Equal(("chainmail hauberk", "Torso"), (worn.Name, worn.Slot));
        Assert.Equal(new[] { "3 torch", "rope" }, death.Lost!.Select(i => i.Name).ToArray());
        Assert.True(death.AtEntry);
        Assert.Equal(25, death.Coins!.Value.Gold);

        Assert.Empty(h.Collected);
        Assert.Empty(h.Sent);
        Assert.False(h.Held);
        // One line, and not the pickup's "missing since you were last in the game".
        string notice = Assert.Single(h.Notices);
        Assert.Contains("died to the hang-up penalty", notice);
        Assert.Contains("Recover Now", notice);
        Assert.DoesNotContain("missing", notice);
        Assert.Empty(h.Check.LastMissing);
        Assert.Contains("died", h.Check.LastDeathCheck);
        // The list is spent: what is held now replaces it, so it isn't judged twice.
        Assert.Empty(h.Profile.HeldAtDisconnect!.Items);
        Assert.Equal(200, h.Profile.HeldAtDisconnect.Hp);
    }

    // The share is taken off HP with no floor, so a standing character can die of
    // it too. The realm's largest share says how low is low enough to look.
    [Theory]
    [InlineData(40, 50, true)]     // 50% of 200 is 100: enough to drop 40 HP
    [InlineData(100, 50, true)]
    [InlineData(101, 50, false)]   // more than the penalty takes at most
    [InlineData(40, 0, false)]     // a side that takes no HP kills only the dropped
    [InlineData(0, 0, true)]
    public void AStandingCharacter_IsLookedAtOnlyWhenThePenaltyCouldDropIt(int hp, int share, bool looked)
    {
        Harness h = new() { MaxItems = 0, PveShare = share };
        h.StoredAt(hp, Geared);

        ComeBackDead(h);
        Assert.Equal(looked, h.Held);
        h.ReadInventory(Snap());

        Assert.Equal(looked ? 1 : 0, h.Deaths.Count);
    }

    // The side that applied: in a fight with a player the PvP figures, otherwise
    // the monster side's and only when that side is ticked.
    [Theory]
    [InlineData(true, 50, null, true)]     // PvP fight, PvP side set
    [InlineData(false, 50, null, false)]   // no PvP fight, and the monster side isn't ticked
    [InlineData(false, 50, 25, true)]      // monster side ticked
    [InlineData(true, null, null, false)]  // master switch off: nothing is penalised
    public void TheSideOfThePenalty_FollowsTheFightTheCharacterLeftIn(bool pvpFight, int? pvpShare, int? pveShare, bool looked)
    {
        Harness h = new() { MaxItems = 0, PvpShare = pvpShare, PveShare = pveShare };
        h.StoredAt(-5, Geared, pvp: pvpFight);

        ComeBackDead(h);
        h.ReadInventory(Snap());

        Assert.Equal(looked ? 1 : 0, h.Deaths.Count);
        if (!looked) Assert.Empty(h.Coordinator.History);
    }

    // A hang-up with no fight on is free on a board that penalises only while
    // attacked: nothing is held and no `stat` is sent for it. On a realm set as
    // penalising every hang-up it is looked at like any other.
    [Theory]
    [InlineData(null, false)]
    [InlineData(50, true)]
    public void AHangUpOutsideAFight_IsLookedAtOnlyWhereEveryHangUpIsPenalised(int? outsideShare, bool looked)
    {
        Harness h = new() { MaxItems = 0, PveShare = 50, OutsideFightShare = outsideShare };
        h.StoredAt(-5, Geared, inFight: false);

        ComeBackDead(h, lives: null);
        Assert.Equal(looked, h.Held);
        h.ReadInventory(Snap());

        Assert.Equal(looked ? 1 : 0, h.Sent.Count(s => s == "stat\r"));
        Assert.Empty(h.Deaths);
        if (!looked) Assert.Empty(h.Coordinator.History);
    }

    // A free room, or a level that didn't apply: the character comes back as it
    // left. After a restart of the client no maximum has been seen yet (a dropped
    // character's statline never shows one), and HP alone still says so.
    [Theory]
    [InlineData(200)]
    [InlineData(null)]
    public void StillDropped_IsNoDeath_AndNothingIsSaid(int? maxHpKnown)
    {
        Harness h = new() { MaxItems = 0, PveShare = 50 };
        h.StoredAt(-5, Geared);

        h.Check.NoteConnected();
        h.ShowRoom();
        h.Vitals = (-5, maxHpKnown);
        h.Check.OnInGameChanged(true);
        h.ReadLives(7);
        h.ReadInventory(Geared);

        Assert.Empty(h.Deaths);
        Assert.Empty(h.Notices);
        Assert.False(h.Held);
        Assert.Contains("no death", h.Check.LastDeathCheck);
    }

    // No life lost is no death, whatever HP reads; the item check then runs as ever.
    [Fact]
    public void LivesUnchanged_IsNoDeath_AndTheItemCheckGoesOn()
    {
        Harness h = new() { PveShare = 50 };
        h.StoredAt(-5, Geared);

        h.Check.NoteConnected();
        h.ShowRoom("3 torch");
        h.Vitals = (60, 200);
        h.Check.OnInGameChanged(true);
        h.ReadLives(7);
        h.ReadInventory(Snap(worn: [("chainmail hauberk", "Torso")], carried: ["rope"]));

        Assert.Empty(h.Deaths);
        Assert.Equal([("torch", 3)], h.Collected);
    }

    // The lives are the surest sign, and only a `stat` gives them: an entry made by
    // hand gets one asked for, and the answer decides.
    [Fact]
    public void WithNoStatReadOnThisConnection_OneIsAskedFor_AndItsLivesDecide()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50 };
        h.StoredAt(-5, Geared);

        ComeBackDead(h, lives: null);
        h.ReadInventory(Snap());

        Assert.Equal(["stat\r"], h.Sent);
        Assert.Empty(h.Deaths);
        Assert.True(h.Held);
        // A save made while it waits must not write over the list still to be judged.
        h.Check.StampForSave(h.Profile);
        Assert.Equal(-5, h.Profile.HeldAtDisconnect!.Hp);

        h.ReadLives(6);

        Assert.Equal(6, Assert.Single(h.Deaths).LivesRemaining);
        Assert.False(h.Held);
    }

    // No answer to the `stat`: another room and an empty pack say nothing. The
    // question stays open without a hold, the terminal is told once, and whatever
    // `stat` is read next on the connection answers it.
    [Fact]
    public void WithNoAnswerToTheStat_NothingIsRecorded_UntilAStatIsRead()
    {
        Harness h = new() { PveShare = 50 };
        h.StoredAt(-5, Geared);

        // The pack is empty and torches lie in the temple: neither is a death, and
        // neither is "dropped by a hang-up" while a death isn't ruled out.
        ComeBackDead(h, lives: null, "3 torch");
        h.ReadInventory(Snap());
        h.Heartbeats(3);

        Assert.Empty(h.Deaths);
        Assert.False(h.Held);
        Assert.Empty(h.Collected);
        string waiting = Assert.Single(h.Notices);
        Assert.Contains("hasn't been checked", waiting);
        Assert.Contains("`stat`", waiting);
        Assert.Contains("still to be judged", h.Check.Status);

        h.ReadLives(6);

        Assert.Equal(6, Assert.Single(h.Deaths).LivesRemaining);
        Assert.Equal(2, h.Notices.Count);
        Assert.Contains("died to the hang-up penalty", h.Notices[1]);
        Assert.Equal("idle", h.Check.Status);
    }

    // Auto-All off sends nothing, the `stat` included; the user's own answers it.
    [Fact]
    public void AutoAllOff_NoStatIsSent_AndTheUsersOwnStatAnswers()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50, AutoAll = false };
        h.StoredAt(-5, Geared);

        ComeBackDead(h, lives: null);
        h.ReadInventory(Snap());

        Assert.Empty(h.Sent);
        Assert.Empty(h.Deaths);
        Assert.Contains("Auto-All is off", Assert.Single(h.Notices));

        h.ReadLives(7);

        Assert.Empty(h.Deaths);
        Assert.Contains("no death", h.Check.LastDeathCheck);
        Assert.Single(h.Notices);
    }

    // The answer can come long after the entry. The pile is what was gone at the
    // first inventory read, not what has been used up or put on since.
    [Fact]
    public void ALateAnswer_IsJudgedOnWhatTheEntryShowed()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50, AutoAll = false };
        // The rope stays with the character through a death, so it is still held.
        h.Stays.Add("rope");
        h.StoredAt(90, Geared);

        ComeBackDead(h, lives: null);
        h.ReadInventory(Snap(carried: ["rope"]));
        // Played on: hurt, a sword found and wielded, the rope used up.
        h.Vitals = (35, 200);
        h.Room = new RoomKey(1, 9);
        h.Check.NoteMoveSent();
        h.Check.OnRoomChanged();
        h.Inventory = Snap(worn: [("short sword", "Weapon Hand")]);
        h.Check.OnInventoryRead();

        h.ReadLives(6);

        MudPlay.Game.Recovery.UnwitnessedDeath death = Assert.Single(h.Deaths);
        Assert.Equal("chainmail hauberk", Assert.Single(death.Equipped!).Name);
        Assert.Equal("3 torch", Assert.Single(death.Lost!).Name);
        // Found long after the login: the record is made and no more. The loop that
        // has run since isn't stopped and the buffs cast since keep their timers.
        Assert.False(death.AtEntry);
    }

    // The 3-minute wait at the board's menu gives the hold up; that is said once,
    // with what will answer the question.
    [Fact]
    public void GivingTheHoldUpWithADeathInQuestion_IsSaidOnce()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50 };
        h.StoredAt(-5, Geared);
        h.Check.NoteConnected();

        h.Heartbeats(180);

        Assert.False(h.Held);
        Assert.Contains("Type `stat` and `i`", Assert.Single(h.Notices));

        // Entered by hand later; the user's `stat` and `i` are all it needs.
        h.BoardSaysHungUp();
        h.Room = HallsOfTheDead;
        h.ShowRoom();
        h.Vitals = (200, 200);
        h.Check.OnInGameChanged(true);
        h.Heartbeats(30);
        Assert.Empty(h.Sent);
        h.ReadLives(6);
        Assert.Empty(h.Deaths);
        h.ReadInventory(Snap());

        Assert.Single(h.Deaths);
        Assert.Equal(2, h.Notices.Count);
    }

    // The link drops while the `stat` is waited for: the list was never written
    // over, so the next connection asks again.
    [Fact]
    public void ALinkDropWhileTheLivesAreWaitedFor_IsJudgedOnTheNextConnection()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50 };
        h.StoredAt(-5, Geared);

        ComeBackDead(h, lives: null);
        h.ReadInventory(Snap());
        Assert.Equal(["stat\r"], h.Sent);
        h.Check.NoteLinkDropping();
        h.Check.OnInGameChanged(false);
        h.Check.NoteDisconnected();

        Assert.Empty(h.Deaths);
        Assert.Equal(-5, h.Profile.HeldAtDisconnect!.Hp);

        ComeBackDead(h, 6);
        h.ReadInventory(Snap());

        Assert.Single(h.Deaths);
    }

    // A question left open ends with the link, and leaves nothing behind that the
    // next connection could pin on the wrong hang-up: the list written on a
    // connection that read no `stat` carries no lives.
    [Fact]
    public void AnOpenQuestion_EndsWithTheLink_AndTheNextListCarriesNoLives()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50, AutoAll = false, Lives = 7 };
        h.StoredAt(-5, Geared);

        ComeBackDead(h, lives: null);
        h.ReadInventory(Snap());
        h.Vitals = (-8, 200);
        h.MonsterFight = true;
        h.Check.NoteLinkDropping();
        h.Check.OnInGameChanged(false);
        h.Check.NoteDisconnected();

        Assert.Contains("not checked", h.Check.LastDeathCheck);
        Assert.Equal(-8, h.Profile.HeldAtDisconnect!.Hp);
        Assert.Null(h.Profile.HeldAtDisconnect.Lives);

        // Dropped again, back with 6 lives: the life went before this list was
        // written, and no count on it says otherwise.
        ComeBackDead(h, 6);
        h.ReadInventory(Snap());

        Assert.Empty(h.Deaths);
        Assert.Contains("No death was recorded: its lives weren't read", h.Notices[^1]);
    }

    // A life lost on another client between two sessions here is not this
    // hang-up's death. On Stock two things the entry shows say so.
    [Theory]
    [InlineData(true, true)]     // gear still worn, though the board printed its lines
    [InlineData(false, false)]   // nothing worn, but the board didn't print its lines
    public void ALifeLostElsewhere_IsNotRecorded_AndIsTold(bool wornNow, bool boardSaidHungUp)
    {
        Harness h = new() { MaxItems = 0, PveShare = 50 };
        h.StoredAt(60, Geared);

        h.Check.NoteConnected();
        if (boardSaidHungUp) h.BoardSaysHungUp();
        h.ShowRoom();
        h.Vitals = (200, 200);
        h.Check.OnInGameChanged(true);
        h.ReadLives(6);
        h.ReadInventory(wornNow ? Geared : Snap(carried: ["chainmail hauberk", "3 torch", "rope"]));

        Assert.Empty(h.Deaths);
        string notice = Assert.Single(h.Notices);
        Assert.Contains("a life was lost", notice);
        Assert.Contains("No death was recorded", notice);
    }

    // Off Stock the board's lines aren't asked for: nobody has confirmed another
    // board prints them. What is worn is asked on both realms: after a death a
    // loyal piece is back in the pack, so nothing is worn.
    [Theory]
    [InlineData(false, true)]    // the loyal amulet is in the pack: a death
    [InlineData(true, false)]    // still worn: it didn't die to this hang-up
    public void OffStock_TheBoardsLinesArentAskedFor_ButNothingMayBeWorn(bool amuletWorn, bool died)
    {
        Harness h = new() { MaxItems = 0, PvpShare = 50, Stock = false };
        h.StoredAt(-5, Geared, pvp: true);

        h.Room = HallsOfTheDead;
        h.Check.NoteConnected();
        h.ShowRoom();
        h.Vitals = (200, 200);
        h.Check.OnInGameChanged(true);
        h.ReadLives(6);
        h.ReadInventory(amuletWorn ? Snap(worn: [("soulbound amulet", "Neck")]) : Snap(carried: ["soulbound amulet"]));

        Assert.Equal(died ? 1 : 0, h.Deaths.Count);
        if (!died) Assert.Contains("still worn", Assert.Single(h.Notices));
    }

    // The list has no lives (none was read on the connection it was written on),
    // and the room it came back in has nothing to say: it hung up in the temple
    // itself, where its dead arrive anyway, or the map isn't sure of the room.
    // Then nothing will tell. Nothing is recorded, whatever the pack looks like,
    // and it is said once. (Where the room does speak, it settles this case: the
    // tests under "The room it comes back in".)
    [Theory]
    [InlineData(true)]    // in the room it left, which is the temple
    [InlineData(false)]   // the map isn't sure where it is
    public void WhenNeitherTheLivesNorTheRoomCanTell_NothingIsRecorded_AndOneNoticeSaysWhatWasSeen(bool sameRoom)
    {
        Harness h = new() { PveShare = 50, AutoAll = false };
        h.StoredAt(-5, Geared, lives: null, room: sameRoom ? new RoomRef(1, 2189) : null);

        h.Room = sameRoom ? HallsOfTheDead : null;
        h.Check.NoteConnected();
        h.BoardSaysHungUp();
        h.ShowRoom("3 torch");
        h.Vitals = (200, 200);
        h.Check.OnInGameChanged(true);
        h.ReadLives(6);
        h.ReadInventory(Snap());

        Assert.Empty(h.Deaths);
        Assert.Null(h.Profile.DeathHistory);
        Assert.Contains("No death was recorded", h.Notices[0]);
        Assert.Contains("chainmail hauberk", h.Notices[0]);
        Assert.Contains("can't tell", h.Check.LastDeathCheck);
        Assert.False(h.Held);
        Assert.Empty(h.Collected);
        // Where it left the game the item check still has its say; elsewhere what is
        // missing may be a pile, and nothing more is said about it.
        Assert.Equal(sameRoom ? 2 : 1, h.Notices.Count);
    }

    // A death seen before the link dropped voids the list, so there is nothing to
    // work out on the way back in and no second record.
    [Fact]
    public void ADeathSeenBeforeTheLinkDropped_IsNotRecordedAgain()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50, Vitals = (-30, 200), Lives = 7 };
        h.ConnectAndEnter();
        h.ReadInventory(Geared);
        h.Check.StampForSave(h.Profile);
        Assert.Equal(-30, h.Profile.HeldAtDisconnect!.Hp);

        h.Check.OnPlayerDied();
        h.Check.NoteLinkDropping();
        h.Check.NoteDisconnected();
        Assert.Null(h.Profile.HeldAtDisconnect);

        ComeBackDead(h);
        h.ReadInventory(Snap());

        Assert.Empty(h.Deaths);
        Assert.Empty(h.Notices);
    }

    // A death line read on the way in, before the first prompt, has its own record:
    // the list from before it says nothing more.
    [Fact]
    public void ADeathRecordedSinceTheListWasWritten_EndsThePassQuietly()
    {
        Harness h = new() { PveShare = 50 };
        h.StoredAt(-5, Geared);
        h.Profile.DeathHistory = [new DeathRecord(DateTimeOffset.Now, new RoomRef(1, 3), 6, "You have 6 lives left.")];

        ComeBackDead(h, 6, "3 torch");
        h.ReadInventory(Snap());

        Assert.Empty(h.Deaths);
        Assert.Empty(h.Collected);
        Assert.Empty(h.Notices);
        Assert.False(h.Held);
    }

    // The list says what the character left the game with, the fight included, and
    // is written before the drop tears the fight down.
    [Fact]
    public void TheList_CarriesHpLivesFightWornAndCoins_WrittenAsTheLinkGoesDown()
    {
        Harness h = new() { Vitals = (120, 200), Lives = 7 };
        h.ConnectAndEnter();
        h.ReadInventory(Geared);
        // The count the client carries over from an earlier session can be a death
        // behind: it goes on the list only once the game has given it here.
        h.Check.StampForSave(h.Profile);
        Assert.Null(h.Profile.HeldAtDisconnect!.Lives);
        h.ReadLives(7);

        h.Vitals = (-12, 200);
        h.PvpFight = true;
        h.MonsterFight = true;
        h.Check.NoteLinkDropping();
        // What the Disconnected handler clears next must not reach the list.
        h.PvpFight = false;
        h.MonsterFight = false;
        h.Vitals = null;
        h.Check.OnInGameChanged(false);
        h.Check.NoteDisconnected();

        HeldAtDisconnect list = h.Profile.HeldAtDisconnect!;
        Assert.Equal(-12, list.Hp);
        Assert.Equal(200, list.MaxHp);
        Assert.Equal(7, list.Lives);
        Assert.True(list.PvpFight);
        Assert.True(list.InCombat);
        Assert.Equal("Torso", Assert.Single(list.Worn!).Slot);
        Assert.Equal(new[] { "3 torch", "rope" }, list.Carried!.Select(i => i.Name).ToArray());
        Assert.Equal(25, list.Coins!.Value.Gold);
    }

    // A death that was seen gives the lives too, in its own readout.
    [Fact]
    public void AWitnessedDeath_MakesTheLivesKnownForTheNextList()
    {
        Harness h = new() { Vitals = (200, 200), Lives = 7 };
        h.ConnectAndEnter();
        h.ReadInventory(Geared);

        h.Check.OnPlayerDied();
        h.Lives = 6;
        h.ReadInventory(Snap());
        h.Check.StampForSave(h.Profile);

        Assert.Equal(6, h.Profile.HeldAtDisconnect!.Lives);
    }

    [Fact]
    public void TheNewFields_RoundTrip_AndAListWithoutThemRaisesNoQuestion()
    {
        CharacterProfile profile = new()
        {
            HeldAtDisconnect = new HeldAtDisconnect
            {
                Realm = "Board/Main",
                Items = [new HeldItem("torch", 3)],
                Hp = -4,
                MaxHp = 180,
                Lives = 5,
                PvpFight = true,
                InCombat = true,
                Worn = [new DeathItem("chainmail hauberk", "Torso")],
                Carried = [new DeathItem("3 torch")],
                Coins = new CurrencyHoldings(1, 2, 3, 4, 5, 0),
            },
        };

        HeldAtDisconnect back = JsonSerializer.Deserialize<CharacterProfile>(
            JsonSerializer.Serialize(profile, JsonStore.Options), JsonStore.Options)!.HeldAtDisconnect!;

        Assert.Equal(-4, back.Hp);
        Assert.Equal(180, back.MaxHp);
        Assert.Equal(5, back.Lives);
        Assert.True(back.PvpFight && back.InCombat);
        Assert.Equal("Torso", Assert.Single(back.Worn!).Slot);
        Assert.Equal("3 torch", Assert.Single(back.Carried!).Name);
        Assert.Equal(4, back.Coins!.Value.Platinum);

        // A list written before these were kept.
        HeldAtDisconnect old = JsonSerializer.Deserialize<CharacterProfile>(
            """{ "Name": "Ermias", "HeldAtDisconnect": { "Realm": "Board/Main", "Items": [ { "Name": "torch", "Count": 3 } ] } }""",
            JsonStore.Options)!.HeldAtDisconnect!;
        Assert.Null(old.Hp);
        Assert.Null(old.Worn);
        Assert.False(MudPlay.Game.Recovery.HangupDeath.Suspected(old.Hp, old.MaxHp, hpShareTop: 50));

        Harness h = new() { MaxItems = 0, PveShare = 50 };
        h.Profile.HeldAtDisconnect = old;
        ComeBackDead(h);
        h.ReadInventory(Snap());
        Assert.Empty(h.Deaths);
        Assert.Empty(h.Coordinator.History);
    }

    // ----- No false deaths ------------------------------------------------
    //
    // From the review of the first version, which also went by HP, the room and
    // the pack: each of these recorded a death that never happened, or missed one
    // that did.

    // Sat at the board's menu past the 3-minute hold, entered by hand, no death
    // (the share took 50 HP). Nothing asks for `i` or `stat` once the hold is
    // gone. The character rests, walks on, a torch burns; the first inventory
    // read comes then.
    [Fact]
    public void ALateInventoryRead_AfterTheHoldWasGivenUpAtTheMenu_RecordsNoDeath()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50 };
        h.StoredAt(60, Geared);
        h.Check.NoteConnected();
        h.Heartbeats(180);
        Assert.False(h.Held);
        h.ShowRoom();
        h.Vitals = (10, 200);
        h.Check.OnInGameChanged(true);
        h.Heartbeats(30);
        Assert.Empty(h.Sent);
        h.Check.NoteMoveSent();
        h.Room = new RoomKey(1, 7);
        h.Check.OnRoomChanged();
        h.Vitals = (200, 200);
        h.ReadInventory(Geared with { CarriedItems = new[] { "2 torch", "rope" } });

        Assert.Empty(h.Deaths);
        // It came in under the HP it left with: no death, whatever HP reads now.
        Assert.Contains("no death", h.Check.LastDeathCheck);
    }

    // Auto-All off: entered at once, the hold given up after 4 s with nothing
    // sent. Played by hand for a while, then `i` typed.
    [Fact]
    public void ALateInventoryRead_WithAutoAllOff_RecordsNoDeath()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50, AutoAll = false };
        h.StoredAt(60, Geared);
        h.Check.NoteConnected();
        h.ShowRoom();
        h.Vitals = (60, 200);
        h.Check.OnInGameChanged(true);
        h.Heartbeats(10);
        Assert.False(h.Held);
        Assert.Empty(h.Sent);
        // It came in at exactly the HP it left with, short of its maximum: the
        // penalty took nothing and a death would have filled it. Nothing to say.
        Assert.Empty(h.Notices);
        h.Check.NoteMoveSent();
        h.Room = new RoomKey(1, 7);
        h.Check.OnRoomChanged();
        h.Vitals = (180, 200);
        h.ReadInventory(Geared with { CarriedItems = new[] { "2 torch", "rope" } });

        Assert.Empty(h.Deaths);
        Assert.Empty(h.Notices);
        Assert.Contains("no death", h.Check.LastDeathCheck);
    }

    // Left at 60/200 by a hang-up that cost nothing; died once on another client
    // in between (lives 7 to 6) and got everything back; logs in here rested, in
    // the same room.
    [Fact]
    public void ADeathOnAnotherClientInBetween_IsNotThisHangUps()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50 };
        h.StoredAt(60, Geared);
        h.Room = new RoomKey(1, 3);
        h.Check.NoteConnected();
        h.ShowRoom();
        h.Vitals = (200, 200);
        h.Check.OnInGameChanged(true);
        h.ReadLives(6);
        h.ReadInventory(Geared);

        Assert.Empty(h.Deaths);
    }

    // Two drops in a row: the second connection never reads an inventory.
    [Fact]
    public void TwoDropsBeforeTheCheck_MakeOneRecord()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50 };
        h.StoredAt(-5, Geared);
        h.Room = HallsOfTheDead;
        h.Check.NoteConnected();
        h.ShowRoom();
        h.Vitals = (200, 200);
        h.Check.OnInGameChanged(true);
        h.Check.NoteLinkDropping();
        h.Check.OnInGameChanged(false);
        h.Check.NoteDisconnected();
        Assert.Equal(-5, h.Profile.HeldAtDisconnect!.Hp);

        ComeBackDead(h, 6);
        h.ReadInventory(Snap());
        Assert.Single(h.Deaths);

        // A third connection does not record it again.
        h.Check.NoteLinkDropping();
        h.Check.OnInGameChanged(false);
        h.Check.NoteDisconnected();
        ComeBackDead(h, 6);
        h.ReadInventory(Snap());
        Assert.Single(h.Deaths);
    }

    // A real death, entered late; by the first inventory read the character has
    // fought and sits under the HP it left with. HP is judged as it was at the
    // first prompt.
    [Fact]
    public void ARealDeath_ReadLate_IsStillRecorded_ThoughHpHasFallenSince()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50 };
        h.StoredAt(90, Geared);
        h.Room = HallsOfTheDead;
        h.Check.NoteConnected();
        h.BoardSaysHungUp();
        h.Heartbeats(180);
        h.ShowRoom();
        h.Vitals = (200, 200);
        h.Check.OnInGameChanged(true);
        h.Check.NoteMoveSent();
        h.Room = new RoomKey(1, 9);
        h.Check.OnRoomChanged();
        h.Vitals = (50, 200);
        h.ReadLives(6);
        h.ReadInventory(Snap());

        Assert.Single(h.Deaths);
    }

    // ----- A life lost where this client wasn't looking --------------------
    //
    // From the second review: each of these pinned a life lost in a session this
    // client never saw on its own last hang-up, or hid a real one.

    // Paradigm, where nothing is asked of what is worn or of the board's lines:
    // the character left at 80 of 200 in a monster fight with 7 lives. Days on
    // another client follow; it dies once there, recovers its corpse and wears
    // everything again. Back here at full HP, in another room, with 6 lives and
    // nothing missing. A death takes all but what stays with the character, so
    // one that still holds everything didn't die to this hang-up.
    //
    // The other room is the temple: the one room that wouldn't itself say "not
    // this hang-up's", so it is the pack that speaks here.
    [Fact]
    public void ALifeLostElsewhere_WithNothingMissing_IsNotRecorded_OnEitherRealm()
    {
        foreach (bool stock in new[] { false, true })
        {
            Harness h = new() { MaxItems = 0, PveShare = 50, Stock = stock };
            h.PlayAndDrop(lives: 7, Geared, hpAtDrop: 80, new RoomKey(1, 3));

            h.Now = h.Now.AddDays(4);
            // On Stock the gear is back but not worn, and the board printed its
            // lines for some later hang-up: only what is held speaks.
            h.ComeBack(hp: 200, lives: 6, stock ? Geared with { EquippedItems = [], CarriedItems = ["chainmail hauberk", "3 torch", "rope"] } : Geared,
                boardLine: stock, room: HallsOfTheDead);

            Assert.Empty(h.Deaths);
            Assert.Null(h.Profile.DeathHistory);
            string notice = Assert.Single(h.Notices);
            Assert.Contains("a life was lost", notice);
            Assert.Contains("still held", notice);
            Assert.Contains("No death was recorded", notice);
        }
    }

    // A list that held nothing can't be read that way: the lives decide alone.
    [Fact]
    public void AListThatHeldNothing_CantSayNothingIsMissing()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50, Stock = false };
        h.PlayAndDrop(lives: 7, Snap(), hpAtDrop: -5, new RoomKey(1, 3));

        h.ComeBack(hp: 200, lives: 6, Snap());

        Assert.Single(h.Deaths);
    }

    // A profile loaded over another character (a copied profile, a shared
    // account): the list is one character's and the `stat` screen names another.
    // It answers nothing, about a death or about dropped items.
    [Fact]
    public void AListOfAnotherCharacter_AnswersNothing()
    {
        Harness h = new() { PveShare = 50, Stock = false };
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, new RoomKey(1, 3));
        Assert.Equal("Ermias Asghedom", h.Profile.HeldAtDisconnect!.Character);

        h.StatName = "Raijin WuzHere";
        h.Floor.Add("3 torch");
        h.Check.NoteFloorSurveyed();
        h.ComeBack(hp: 200, lives: 6, Snap(), room: new RoomKey(1, 3));

        Assert.Empty(h.Deaths);
        Assert.Empty(h.Collected);
        Assert.Empty(h.Notices);
        Assert.False(h.Held);
        Assert.Contains("Ermias Asghedom's", h.Check.LastDeathCheck);
        // The list written next is the other character's own.
        Assert.Equal("Raijin WuzHere", h.Profile.HeldAtDisconnect!.Character);
    }

    // The same name in another case is the same character.
    [Fact]
    public void TheSameNameInAnotherCase_IsTheSameCharacter()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50, Stock = false };
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, new RoomKey(1, 3));

        h.StatName = "ermias asghedom";
        h.ComeBack(hp: 200, lives: 6, Snap());

        Assert.Single(h.Deaths);
    }

    // A life given back after a death (Sysop god lives sends `sys god <name> add
    // life`; a level trained gives lives) changes the game's count with no screen
    // telling the client. The count it holds is then one under the real one, and a
    // list carrying it read the next death by a hang-up as "no life lost": nothing
    // recorded, nothing said. The count is unknown until a `stat` gives it, and
    // one is sent for it.
    [Fact]
    public void ALifeGivenBackAfterADeath_IsReadAgain_SoTheNextHangUpDeathIsRecorded()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50 };
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, new RoomKey(1, 3));
        h.ComeBack(hp: 200, lives: 6, Snap());
        Assert.Single(h.Deaths);

        // The life is asked back: the game has 7, the client still holds 6.
        h.Check.NoteLivesChangedUnread("a life was asked back");
        h.Check.StampForSave(h.Profile);
        Assert.Null(h.Profile.HeldAtDisconnect!.Lives);

        h.Heartbeats(5);
        Assert.Equal("stat\r", h.Sent[^1]);
        h.Lives = 7;
        h.ReadLives(7);

        // Same connection: gear recovered, back in a fight, dropped, link down.
        h.ReadInventory(Geared);
        h.Room = new RoomKey(1, 40);
        h.Vitals = (-8, 200);
        h.MonsterFight = true;
        h.Now = h.Now.AddMinutes(30);
        h.Check.NoteLinkDropping();
        h.Check.NoteDisconnected();
        h.MonsterFight = false;
        Assert.Equal(7, h.Profile.HeldAtDisconnect!.Lives);

        // The second death by a hang-up: 7 to 6 again.
        h.ComeBack(hp: 200, lives: 6, Snap());

        Assert.Equal(2, h.Deaths.Count);
        Assert.Equal(40, h.Deaths[1].Room!.Room);
    }

    // With Auto-All off no `stat` goes out for it. The list then carries no count,
    // and the next entry says it can't tell where it used to say nothing.
    //
    // The second hang-up is in the temple (where the first death left it), which
    // is why nothing tells: back in the room its own dead arrive in, the room has
    // nothing to say. Hung up anywhere else, the room would settle it without a
    // count (the test after this one).
    [Fact]
    public void ALifeGivenBack_WithNoStatReadAfter_AndHungUpInTheTempleItself_TheNextEntryIsToldItCantTell()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50 };
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, new RoomKey(1, 3));
        h.ComeBack(hp: 200, lives: 6, Snap());
        Assert.Single(h.Deaths);

        h.AutoAll = false;
        h.Check.NoteLivesChangedUnread("a level was trained");
        h.Heartbeats(10);
        Assert.DoesNotContain("stat\r", h.Sent);

        h.ReadInventory(Geared);
        h.Vitals = (-8, 200);
        h.MonsterFight = true;
        h.Check.NoteLinkDropping();
        h.Check.NoteDisconnected();
        h.MonsterFight = false;
        Assert.Null(h.Profile.HeldAtDisconnect!.Lives);

        int said = h.Notices.Count;
        h.ComeBack(hp: 200, lives: 6, Snap());

        Assert.Single(h.Deaths);
        Assert.Contains("No death was recorded: its lives weren't read", Assert.Single(h.Notices.Skip(said)));
    }

    // The same, but it had walked out to Darkwood Forest before the second
    // hang-up: back stripped in the temple, the room tells the death the missing
    // count can't.
    [Fact]
    public void ALifeGivenBack_WithNoStatReadAfter_TheRoomStillTellsTheNextDeath()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50 };
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, new RoomKey(1, 3));
        h.ComeBack(hp: 200, lives: 6, Snap());
        Assert.Single(h.Deaths);

        h.AutoAll = false;
        h.Check.NoteLivesChangedUnread("a level was trained");
        h.ReadInventory(Geared);
        h.Room = Darkwood;
        h.Vitals = (-8, 200);
        h.MonsterFight = true;
        h.Now = h.Now.AddMinutes(5);
        h.Check.NoteLinkDropping();
        h.Check.NoteDisconnected();
        h.MonsterFight = false;
        Assert.Null(h.Profile.HeldAtDisconnect!.Lives);

        h.ComeBack(hp: 200, lives: 6, Snap());

        Assert.Equal(2, h.Deaths.Count);
        Assert.Equal((1, 109), (h.Deaths[1].Room!.Map, h.Deaths[1].Room!.Room));
        Assert.Contains("its lives weren't read on the connection it left the game on", h.Check.LastDeathCheck);
    }

    // A death that was seen gives the lives in its own readout, but not when the
    // life is asked back in the same breath: the readout doesn't count that one.
    [Fact]
    public void AWitnessedDeathWithTheLifeAskedBack_LeavesTheCountUnknown()
    {
        Harness h = new() { Vitals = (200, 200), Lives = 7 };
        h.ConnectAndEnter();
        h.ReadLives(7);
        h.ReadInventory(Geared);

        // The order the app raises them in: the life is asked back first.
        h.Check.NoteLivesChangedUnread("a life was asked back");
        h.Check.OnPlayerDied();
        h.Lives = 6;
        h.ReadInventory(Snap());
        h.Check.StampForSave(h.Profile);

        Assert.Null(h.Profile.HeldAtDisconnect!.Lives);
    }

    // A connection on which no inventory was read used to leave the older list
    // standing, so a death by the hang-up that ended it was recorded at the older
    // list's room and time, with a pile taken from the older list, and Death
    // Recovery walked to the wrong room. When the link drops with the character
    // low enough for the penalty to kill, a list is written for that hang-up
    // anyway: the room, the time and HP, and no claim about what was held.
    [Fact]
    public void ADropThatCouldKill_OnAConnectionWithNoInventoryRead_WritesItsOwnList()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50, AutoAll = false };
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: 60, new RoomKey(1, 3));
        DateTimeOffset firstDrop = h.Profile.HeldAtDisconnect!.At;

        // Entered by hand, Auto-All off, no `i`, no `stat`. The hang-up cost HP
        // only (60 to 20). The character walks on and is dropped in 1/50.
        h.Room = new RoomKey(1, 3);
        h.Now = h.Now.AddMinutes(1);
        h.Check.NoteConnected();
        h.BoardSaysHungUp();
        h.Check.NoteRoomDisplayed();
        h.Vitals = (20, 200);
        h.Check.OnInGameChanged(true);
        h.Heartbeats(10);
        Assert.False(h.Held);
        h.Check.NoteMoveSent();
        h.Room = new RoomKey(1, 50);
        h.Check.OnRoomChanged();
        h.Vitals = (-9, 200);
        h.MonsterFight = true;
        h.Now = h.Now.AddHours(2);
        h.Check.NoteLinkDropping();
        h.Check.NoteDisconnected();
        h.MonsterFight = false;

        HeldAtDisconnect list = h.Profile.HeldAtDisconnect!;
        Assert.NotEqual(firstDrop, list.At);
        Assert.Equal(h.Now, list.At);
        Assert.Equal(50, list.Room!.Room);
        Assert.Equal(-9, list.Hp);
        // It walked on for two hours with no inventory read: what it holds now
        // isn't known.
        Assert.True(list.ItemsUnknown);
        Assert.Empty(list.Items);
        Assert.Null(list.Worn);
        // No `stat` was read on that connection either, but the first hang-up is
        // known not to have killed (it came in at 20 HP where it had left at 60),
        // so the lives and name of the older list still hold.
        Assert.Equal(7, list.Lives);
        Assert.Equal("Ermias Asghedom", list.Character);

        // It died in 1/50: recorded there, at that time, never at 1/3 two hours early.
        h.AutoAll = true;
        h.ComeBack(hp: 200, lives: 6, Snap());

        MudPlay.Game.Recovery.UnwitnessedDeath death = Assert.Single(h.Deaths);
        Assert.Equal(50, death.Room!.Room);
        Assert.Equal(list.At, death.At);
        Assert.Null(death.Lost);
    }

    // The same, with a `stat` read on that connection: the death is recorded where
    // and when that hang-up was, and its pile is unknown, not the older list's.
    [Fact]
    public void ADeathAfterAConnectionWithNoInventoryRead_IsRecordedThere_WithAnUnknownPile()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50, AutoAll = false };
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: 60, new RoomKey(1, 3));

        h.Room = new RoomKey(1, 3);
        h.Now = h.Now.AddMinutes(1);
        h.Check.NoteConnected();
        h.BoardSaysHungUp();
        h.Check.NoteRoomDisplayed();
        h.Vitals = (20, 200);
        h.Check.OnInGameChanged(true);
        h.ReadLives(7);
        h.Check.NoteMoveSent();
        h.Room = new RoomKey(1, 50);
        h.Check.OnRoomChanged();
        h.Vitals = (-9, 200);
        h.MonsterFight = true;
        h.Now = h.Now.AddHours(2);
        DateTimeOffset secondDrop = h.Now;
        h.Check.NoteLinkDropping();
        h.Check.NoteDisconnected();
        h.MonsterFight = false;

        h.AutoAll = true;
        h.ComeBack(hp: 200, lives: 6, Snap());

        MudPlay.Game.Recovery.UnwitnessedDeath death = Assert.Single(h.Deaths);
        Assert.Equal(50, death.Room!.Room);
        Assert.Equal(secondDrop, death.At);
        Assert.Null(death.Equipped);
        Assert.Null(death.Lost);
        Assert.Null(death.Coins);
        Assert.Contains("What it held then isn't known", death.Message);
        Assert.Contains("lists no pile", h.Notices[^1]);
    }

    // The commonest way the penalty kills. A penalised hang-up at 20 HP costs HP
    // only and leaves the character dropped at -10. It comes back dropped with the
    // monster still there, and the health settings hang up again at the first
    // prompt, before any `stat` or `i`: a dropped, penalised hang-up is a certain
    // death. On the third connection it stands in the temple with 6 lives where it
    // had 7. The death is the SECOND hang-up's: the first is known not to have
    // killed (it came back dropped), so the lives, the name and what was held are
    // still those of the older list, and go onto the list written for the second.
    [Theory]
    [InlineData(true)]    // every hang-up penalised: the client needs no fight for it
    [InlineData(false)]   // only in a fight, and the client had read no room yet
    public void ADroppedReentry_HungUpAgainAtTheFirstPrompt_IsRecordedAtTheSecondHangUp(bool everyHangUpPenalised)
    {
        Harness h = new() { MaxItems = 0, PveShare = 50, OutsideFightShare = everyHangUpPenalised ? 50 : null };
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: 20, new RoomKey(1, 3));
        DateTimeOffset firstDrop = h.Profile.HeldAtDisconnect!.At;

        h.Room = new RoomKey(1, 3);
        h.Now = h.Now.AddMinutes(1);
        h.Check.NoteConnected();
        h.BoardSaysHungUp();
        h.Vitals = (-10, 200);
        h.Check.OnInGameChanged(true);   // the first prompt: no room display, no occupants yet
        h.MonsterFight = false;
        DateTimeOffset secondDrop = h.Now;
        h.Check.NoteLinkDropping();
        h.Check.OnInGameChanged(false);
        h.Check.NoteDisconnected();

        HeldAtDisconnect list = h.Profile.HeldAtDisconnect!;
        Assert.Equal(secondDrop, list.At);
        Assert.NotEqual(firstDrop, list.At);
        Assert.Equal(-10, list.Hp);
        Assert.Equal(7, list.Lives);
        Assert.Equal("Ermias Asghedom", list.Character);
        Assert.False(list.ItemsUnknown);
        Assert.Equal(3, list.Items.Single(i => i.Name == "torch").Count);
        // The fight it left is the fight it came back to.
        Assert.True(list.InCombat);

        h.ComeBack(hp: 200, lives: 6, Snap());

        MudPlay.Game.Recovery.UnwitnessedDeath death = Assert.Single(h.Deaths);
        Assert.Equal(3, death.Room!.Room);
        Assert.Equal(secondDrop, death.At);
        Assert.Equal(6, death.LivesRemaining);
        Assert.Equal($"{HangupItemRecheck.DeathMessage} It came back in {TempleFromRoom3}.", death.Message);
        Assert.Equal(("chainmail hauberk", "Torso"), (Assert.Single(death.Equipped!).Name, death.Equipped![0].Slot));
        Assert.Equal(new[] { "3 torch", "rope" }, death.Lost!.Select(i => i.Name).ToArray());
        Assert.Equal(25, death.Coins!.Value.Gold);
        Assert.True(death.AtEntry);
    }

    // On a realm whose penalty drops items the first hang-up may have taken some,
    // so what was held isn't carried: the record has no pile.
    [Fact]
    public void ADroppedReentry_OnARealmThatDropsItems_CarriesTheLivesButNotTheItems()
    {
        Harness h = new() { MaxItems = 3, PveShare = 50 };
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: 20, new RoomKey(1, 3));

        h.Now = h.Now.AddMinutes(1);
        h.Check.NoteConnected();
        h.Vitals = (-10, 200);
        h.Check.OnInGameChanged(true);
        h.Check.NoteLinkDropping();
        h.Check.NoteDisconnected();

        HeldAtDisconnect list = h.Profile.HeldAtDisconnect!;
        Assert.Equal(7, list.Lives);
        Assert.True(list.ItemsUnknown);
    }

    // A level trained on the short connection changes the lives unread: the count
    // of the older list is then not carried.
    [Fact]
    public void ADroppedReentry_AfterTheLivesChangedUnread_CarriesNoCount()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50 };
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: 20, new RoomKey(1, 3));

        h.Now = h.Now.AddMinutes(1);
        h.Check.NoteConnected();
        h.Vitals = (-10, 200);
        h.Check.OnInGameChanged(true);
        h.Check.NoteLivesChangedUnread("a level was trained");
        h.Check.NoteLinkDropping();
        h.Check.NoteDisconnected();

        Assert.Null(h.Profile.HeldAtDisconnect!.Lives);
    }

    // The first hang-up killed (7 to 6) and was never judged: connection 2 was
    // played by hand with a `stat` (6) and no `i`, and ended low in a fight, so
    // its own list replaces the older one. The older death used to be let go with
    // a log line. It is judged with what connection 2 knows before it goes: one
    // life fewer on that `stat` is a death by the first hang-up, recorded then,
    // from the older list.
    [Fact]
    public void AnOlderDeathNeverJudged_IsRecordedBeforeItsListIsLetGo()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50, AutoAll = false };
        h.Stays.Add("rope");
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, new RoomKey(1, 3));
        DateTimeOffset firstDrop = h.Profile.HeldAtDisconnect!.At;

        h.Room = HallsOfTheDead;
        h.Now = h.Now.AddMinutes(1);
        h.Check.NoteConnected();
        h.BoardSaysHungUp();
        h.Check.NoteRoomDisplayed();
        h.Vitals = (200, 200);
        h.Check.OnInGameChanged(true);
        h.Lives = 6;
        h.ReadLives(6);
        h.Heartbeats(10);
        h.Room = new RoomKey(1, 9);
        h.Vitals = (30, 200);
        h.MonsterFight = true;
        h.Now = h.Now.AddMinutes(20);
        h.Check.NoteLinkDropping();
        h.Check.OnInGameChanged(false);
        h.Check.NoteDisconnected();
        h.MonsterFight = false;

        MudPlay.Game.Recovery.UnwitnessedDeath death = Assert.Single(h.Deaths);
        Assert.Equal(3, death.Room!.Room);
        Assert.Equal(firstDrop, death.At);
        Assert.Equal(6, death.LivesRemaining);
        // No inventory was read since, so the pile is everything on the older list
        // that a death takes: not the rope, which stays with the character.
        Assert.Equal("chainmail hauberk", Assert.Single(death.Equipped!).Name);
        Assert.Equal("3 torch", Assert.Single(death.Lost!).Name);
        // Found as the link dropped again: the record, and nothing stopped.
        Assert.False(death.AtEntry);
        Assert.Contains("died to the hang-up penalty", h.Notices[^1]);
        // The list on file is now the second hang-up's.
        HeldAtDisconnect list = h.Profile.HeldAtDisconnect!;
        Assert.Equal(9, list.Room!.Room);
        Assert.Equal(30, list.Hp);
        Assert.Equal(6, list.Lives);

        // The second hang-up cost HP only: no second record.
        h.AutoAll = true;
        h.ComeBack(hp: 5, lives: 6, Snap(), room: new RoomKey(1, 9));
        Assert.Single(h.Deaths);
    }

    // Where the older hang-up can't be judged as its list is let go, that is said
    // then: nothing will ask again.
    [Fact]
    public void AnOlderQuestionThatCantBeJudged_IsSaidAsItsListIsLetGo()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50, AutoAll = false };
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, new RoomKey(1, 3));

        h.Room = HallsOfTheDead;
        h.Now = h.Now.AddMinutes(1);
        h.Check.NoteConnected();
        h.BoardSaysHungUp();
        h.Check.NoteRoomDisplayed();
        h.Vitals = (200, 200);
        h.Check.OnInGameChanged(true);
        h.Heartbeats(10);
        int said = h.Notices.Count;
        h.Vitals = (30, 200);
        h.MonsterFight = true;
        h.Check.NoteLinkDropping();
        h.Check.NoteDisconnected();

        Assert.Empty(h.Deaths);
        Assert.Contains("can no longer be told", Assert.Single(h.Notices.Skip(said)));
        Assert.Null(h.Profile.HeldAtDisconnect!.Lives);
    }

    // A death read on the way in, ahead of the first prompt, has its own record and
    // owns the life. The link then drops low in a fight with no inventory read: the
    // older list is let go, not judged, or the same life is recorded a second time.
    [Fact]
    public void AnOlderList_WhoseDeathWasWitnessed_IsLetGoUnjudged_NotRecordedTwice()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50 };
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, new RoomKey(1, 3));

        h.Now = h.Now.AddMinutes(1);
        h.Room = HallsOfTheDead;
        h.Check.NoteConnected();
        h.BoardSaysHungUp();
        h.Profile.DeathHistory = [new DeathRecord(h.Now, new RoomRef(1, 3), 6, "You have 6 lives left.") { RecordNumber = 1 }];
        h.Lives = 6;
        h.Check.OnPlayerDied();
        h.Check.NoteRoomDisplayed();
        h.Vitals = (200, 200);
        h.Check.OnInGameChanged(true);
        h.ReadLives(6);
        h.Vitals = (30, 200);
        h.MonsterFight = true;
        h.Now = h.Now.AddMinutes(3);
        h.Check.NoteLinkDropping();
        h.Check.OnInGameChanged(false);
        h.Check.NoteDisconnected();
        h.MonsterFight = false;

        Assert.Empty(h.Deaths);
        Assert.Single(h.Profile.DeathHistory!);
        Assert.Empty(h.Notices);
    }

    // Off Stock nothing but an inventory read separates a death by the hang-up from
    // a life lost on another client (died, pile recovered, everything worn again),
    // and none was read: the older list is told about, not recorded, at the stale
    // room. On Stock the board's lines are still there to ask for; they were not
    // printed here, so it is told as well.
    //
    // The room it came back in must not be what answers, for this to be about the
    // inventory and the lines. Off Stock it hung up in one of Paradigm's own
    // rooms, whose arrival room isn't known; on Stock it is back in the temple.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnOlderHangUp_JudgedWithNoInventoryRead_IsToldNotRecorded_WhereNothingElseSeparatesItFromAnotherClient(bool stock)
    {
        Harness h = new() { MaxItems = 0, PveShare = 50, Stock = stock };
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: 80, stock ? new RoomKey(1, 3) : new RoomKey(18, 40));

        h.Now = h.Now.AddDays(4);
        h.Room = stock ? HallsOfTheDead : new RoomKey(18, 41);
        h.Check.NoteConnected();
        h.Check.NoteRoomDisplayed();
        h.Vitals = (200, 200);
        h.Check.OnInGameChanged(true);
        h.Lives = 6;
        h.ReadLives(6);
        h.Vitals = (30, 200);
        h.MonsterFight = true;
        h.Now = h.Now.AddMinutes(3);
        h.Check.NoteLinkDropping();
        h.Check.OnInGameChanged(false);
        h.Check.NoteDisconnected();
        h.MonsterFight = false;

        Assert.Empty(h.Deaths);
        string notice = Assert.Single(h.Notices);
        Assert.Contains("can no longer be told", notice);
        Assert.Contains("another client", notice);
    }

    // The older hang-up read as harmless to the client (no fight it knew of), so its
    // count of 7 looked safe to carry. The board killed for it (7 to 6) and the
    // character came back at full HP, which HP cannot do out of the game except by
    // a death. Carried, the count would be one too high and the next `stat` would
    // read as no life lost. It is not carried, and no death is recorded at the
    // second hang-up's room. The older hang-up is then judged by the client's own
    // knowledge (not penalised without a fight), which records nothing.
    [Fact]
    public void ACharacterThatCameBackWithMoreHpThanItLeftWith_CarriesNoCount()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50 };
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, new RoomKey(1, 3), fight: false);

        h.Now = h.Now.AddMinutes(1);
        h.Room = HallsOfTheDead;
        h.Check.NoteConnected();
        h.BoardSaysHungUp();
        h.Check.NoteRoomDisplayed();
        h.Vitals = (200, 200);
        h.Check.OnInGameChanged(true);
        h.Check.NoteMoveSent();
        h.Room = new RoomKey(1, 9);
        h.Check.OnRoomChanged();
        h.Vitals = (30, 200);
        h.MonsterFight = true;
        h.Now = h.Now.AddMinutes(5);
        h.Check.NoteLinkDropping();
        h.Check.OnInGameChanged(false);
        h.Check.NoteDisconnected();
        h.MonsterFight = false;

        HeldAtDisconnect list = h.Profile.HeldAtDisconnect!;
        Assert.Equal(9, list.Room!.Room);
        Assert.Null(list.Lives);

        // That second hang-up cost HP only; the lives read now give nothing to
        // compare with.
        h.ComeBack(hp: 5, lives: 6, Snap(), room: new RoomKey(1, 9));
        Assert.Empty(h.Deaths);
    }

    // Paradigm: died on another client, the corpse recovered into the pack, nothing
    // put back on, and one torch burnt since. "Everything still held" missed it for
    // the one torch, and a death was recorded at the stale room with a pile of one
    // torch. A death takes every item that doesn't stay with the character, so
    // holding even one of them says the life wasn't lost to this hang-up.
    //
    // It is back in the temple, so the room it came back in is not what says so.
    [Fact]
    public void ALifeLostElsewhere_WithThePileBackButOneTorchBurnt_IsNotRecorded()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50, Stock = false };
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: 80, new RoomKey(1, 3));

        h.Now = h.Now.AddDays(4);
        h.ComeBack(hp: 200, lives: 6,
            Geared with { EquippedItems = [], CarriedItems = ["chainmail hauberk", "2 torch", "rope"] },
            boardLine: false, room: HallsOfTheDead);

        Assert.Empty(h.Deaths);
        string notice = Assert.Single(h.Notices);
        Assert.Contains("a life was lost", notice);
        Assert.Contains("is still held", notice);
    }

    // A piece that stays with the character through a death is no such sign: a
    // real death leaves it held. With nothing else of the list held, it is a death.
    [Fact]
    public void HoldingOnlyWhatStaysThroughADeath_DoesNotHideADeath()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50, Stock = false };
        h.Stays.Add("loyal blade");
        h.PlayAndDrop(lives: 7, Snap(worn: [("loyal blade", "Weapon Hand")], carried: ["3 torch"]), hpAtDrop: -5, new RoomKey(1, 3));

        h.ComeBack(hp: 200, lives: 6, Snap(carried: ["loyal blade"]));

        MudPlay.Game.Recovery.UnwitnessedDeath death = Assert.Single(h.Deaths);
        Assert.Equal("3 torch", Assert.Single(death.Lost!).Name);
        Assert.Empty(death.Equipped!);

        // A list of nothing but pieces that stay can't be read that way at all.
        Harness loyal = new() { MaxItems = 0, PveShare = 50, Stock = false };
        loyal.Stays.Add("loyal blade");
        loyal.PlayAndDrop(lives: 7, Snap(worn: [("loyal blade", "Weapon Hand")]), hpAtDrop: -5, new RoomKey(1, 3));
        loyal.ComeBack(hp: 200, lives: 6, Snap(carried: ["loyal blade"]));
        Assert.Single(loyal.Deaths);
    }

    // What can't be separated: died elsewhere by an ordinary death and logged off
    // stripped. The record is made, and says only where this client had the character.
    [Fact]
    public void ADeathElsewhereLeftUnrecovered_IsRecordedWithTheHedgedWording()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50, Stock = false };
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: 80, new RoomKey(1, 3));

        h.Now = h.Now.AddDays(4);
        h.ComeBack(hp: 200, lives: 6, Snap(), boardLine: false);

        Assert.Contains("where this client last had the character", Assert.Single(h.Deaths).Message);
        Assert.Contains("If it was played from another client since", Assert.Single(h.Notices));
    }

    // A list of coins only, and a real death: the coins are gone, so it is one.
    [Fact]
    public void AListOfCoinsOnly_GoneAtTheEntry_IsADeath()
    {
        InventorySnapshot purse = Snap() with { Currency = new CurrencyHoldings(0, 0, 25, 0, 0, 2500) };
        Harness h = new() { MaxItems = 0, PveShare = 50 };
        h.PlayAndDrop(lives: 7, purse, hpAtDrop: -5, new RoomKey(1, 3));
        h.ComeBack(hp: 200, lives: 6, Snap());
        Assert.Single(h.Deaths);

        // Still holding coins: a death takes every one.
        Harness kept = new() { MaxItems = 0, PveShare = 50 };
        kept.PlayAndDrop(lives: 7, purse, hpAtDrop: -5, new RoomKey(1, 3));
        kept.ComeBack(hp: 200, lives: 6, Snap() with { Currency = new CurrencyHoldings(0, 0, 3, 0, 0, 300) });
        Assert.Empty(kept.Deaths);
    }

    // A death takes every piece off the body on Stock, a cursed one that stays
    // with the character included. Paradigm is treated the same way (the user's
    // ruling), so anything worn at the first inventory read is no death there too.
    [Theory]
    [InlineData(true, "cursed collar")]     // Stock
    [InlineData(false, "cursed collar")]    // Paradigm: a piece that stays, worn, is no death
    [InlineData(false, "plain helm")]       // Paradigm: a piece a death takes, worn, is no death
    public void AWornPieceThatStaysThroughADeath_VetoesADeathOnEitherRealm(bool stock, string wornNow)
    {
        Harness h = new() { MaxItems = 0, PveShare = 50, Stock = stock };
        h.Stays.Add("cursed collar");
        h.PlayAndDrop(lives: 7, Snap(worn: [("cursed collar", "Neck")], carried: ["3 torch"]), hpAtDrop: -5, new RoomKey(1, 3));

        h.ComeBack(hp: 200, lives: 6, Snap(worn: [(wornNow, "Neck")]));

        Assert.Empty(h.Deaths);
        Assert.Contains("still worn", h.Notices[^1]);
    }

    // A chat line shaped like the exit's takes the client "out of the game" until
    // the next prompt. A dropped character can't leave by the exit command at all,
    // so such a mark is not believed; and a prompt after it takes the mark off
    // again.
    [Fact]
    public void AnExitShapedLine_DoesNotMarkAHangUpAsACleanExit()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50 };
        h.Check.NoteConnected();
        h.Vitals = (200, 200);
        h.Check.NoteRoomDisplayed();
        h.Check.OnInGameChanged(true);
        h.Lives = 7;
        h.ReadLives(7);
        h.ReadInventory(Geared);
        h.Vitals = (-5, 200);
        h.MonsterFight = true;
        h.Check.OnInGameChanged(false);   // the exit-shaped line
        h.Now = h.Now.AddSeconds(1);
        h.Check.NoteLinkDropping();       // the hang-up, before another prompt
        h.Check.OnInGameChanged(false);
        h.Check.NoteDisconnected();
        h.MonsterFight = false;
        Assert.False(h.Profile.HeldAtDisconnect!.CleanExit);

        h.ComeBack(hp: 200, lives: 6, Snap());
        Assert.Single(h.Deaths);

        // Standing, so the mark is believed until a prompt shows the game wasn't left.
        Harness s = new() { MaxItems = 0, PveShare = 50 };
        s.Check.NoteConnected();
        s.Vitals = (200, 200);
        s.Check.NoteRoomDisplayed();
        s.Check.OnInGameChanged(true);
        s.ReadInventory(Geared);
        s.Vitals = (30, 200);
        s.Check.OnInGameChanged(false);
        Assert.True(s.Profile.HeldAtDisconnect!.CleanExit);
        s.Check.OnInGameChanged(true);
        Assert.False(s.Profile.HeldAtDisconnect!.CleanExit);
    }

    // A connection that came and went with no inventory read, and nothing that
    // could kill, leaves the older list to be judged later. On Stock the board
    // prints its lines on that first entry and not again, so what it saw there is
    // kept on the list for the connection that does judge it.
    [Fact]
    public void AMiddleConnectionThatJudgedNothing_HandsOnWhatItSawOfTheEntry()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50 };
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, new RoomKey(1, 3));
        DateTimeOffset dropped = h.Profile.HeldAtDisconnect!.At;

        // The first entry after the hang-up: the board's lines, full HP, the
        // temple. The link drops again before `stat` or `i`.
        h.Room = HallsOfTheDead;
        h.Now = h.Now.AddMinutes(1);
        h.Check.NoteConnected();
        h.BoardSaysHungUp();
        h.Check.NoteRoomDisplayed();
        h.Vitals = (200, 200);
        h.Check.OnInGameChanged(true);
        h.Check.NoteLinkDropping();
        h.Check.NoteDisconnected();

        HeldAtDisconnect list = h.Profile.HeldAtDisconnect!;
        Assert.Equal(dropped, list.At);
        Assert.True(list.LoginLinesSeenSince);
        Assert.Equal(200, list.HpAtFirstEntrySince);

        // The next one judges it. The board prints nothing this time (the temple
        // let that drop go free), and the character has been hurt since.
        h.ComeBack(hp: 150, lives: 6, Snap(), boardLine: false);

        MudPlay.Game.Recovery.UnwitnessedDeath death = Assert.Single(h.Deaths);
        Assert.Equal(3, death.Room!.Room);
        Assert.Equal(dropped, death.At);
    }

    // Leaving by the game's own exit command is not a hang-up: the board takes
    // nothing for it and kills nobody, whatever the HP. With the every-hang-up box
    // on, the next login was held and a `stat` sent for it all the same.
    [Fact]
    public void ACleanExit_RaisesNoQuestion()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50, OutsideFightShare = 50 };
        h.Check.NoteConnected();
        h.Vitals = (200, 200);
        h.Check.NoteRoomDisplayed();
        h.Check.OnInGameChanged(true);
        h.ReadInventory(Geared);
        h.Vitals = (30, 200);

        // `x`, the meditation, "Your character has been saved." with the link up.
        h.Check.OnInGameChanged(false);
        HeldAtDisconnect list = h.Profile.HeldAtDisconnect!;
        Assert.True(list.CleanExit);
        Assert.Equal(30, list.Hp);
        // The link dropping at the board's menu afterwards changes nothing.
        h.Check.NoteLinkDropping();
        h.Check.NoteDisconnected();
        Assert.True(h.Profile.HeldAtDisconnect!.CleanExit);

        h.ComeBack(hp: 200, lives: null, Geared);

        Assert.Empty(h.Coordinator.History);
        Assert.Empty(h.Sent);
        Assert.Empty(h.Notices);
        Assert.Empty(h.Deaths);
    }

    // The same low HP when the link goes down is a hang-up, and is looked into.
    [Fact]
    public void TheSameHp_WhenTheLinkGoesDown_IsAHangUp()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50, OutsideFightShare = 50 };
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: 30, new RoomKey(1, 3), fight: false);

        Assert.False(h.Profile.HeldAtDisconnect!.CleanExit);
        h.Now = h.Now.AddMinutes(1);
        h.Check.NoteConnected();
        Assert.True(h.Held);
    }

    // No second record across reconnects, and a death seen on the connection
    // closes a question it left open.
    [Fact]
    public void NoSecondRecordAcrossReconnects_AndAWitnessedDeathClosesAnOpenQuestion()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50 };
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, new RoomKey(1, 3));
        h.ComeBack(hp: 200, lives: 6, Snap());
        Assert.Single(h.Deaths);
        h.Vitals = (200, 200);
        h.Check.NoteLinkDropping();
        h.Check.NoteDisconnected();
        h.ComeBack(hp: 200, lives: 6, Snap());
        h.Check.NoteLinkDropping();
        h.Check.NoteDisconnected();
        h.ComeBack(hp: 200, lives: 6, Snap());
        Assert.Single(h.Deaths);

        Harness o = new() { MaxItems = 0, PveShare = 50, AutoAll = false };
        o.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, new RoomKey(1, 3));
        o.ComeBack(hp: 200, lives: null, Snap());
        Assert.Empty(o.Deaths);
        o.Check.OnPlayerDied();
        o.Lives = 6;
        o.ReadLives(6);
        Assert.Empty(o.Deaths);
    }

    // The answer comes while the item pickup is under way: the death is recorded
    // and the pass ends, with movement let go.
    [Fact]
    public void AnAnswerDuringThePickup_EndsThePassCleanly()
    {
        // It hung up in the temple itself: back there, the room says nothing either
        // way, so the question stays open while the floor is looked at.
        Harness h = new() { MaxItems = 5, PveShare = 50 };
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, HallsOfTheDead);

        // Back in the room it left, by the map, with a rope on the floor and no
        // `stat` read: one is sent and goes unanswered.
        h.Room = HallsOfTheDead;
        h.Now = h.Now.AddMinutes(1);
        h.Check.NoteConnected();
        h.BoardSaysHungUp();
        h.ShowRoom("rope");
        h.Vitals = (200, 200);
        h.Check.OnInGameChanged(true);
        h.ReadInventory(Snap());
        Assert.Contains("stat\r", h.Sent);
        h.Heartbeats(3);

        h.Lives = 6;
        h.ReadLives(6);

        Assert.Single(h.Deaths);
        Assert.False(h.Held);
        Assert.Equal("idle", h.Check.Status);
    }

    // What the record claims: where this client last had the character, and that
    // the pile is elsewhere if it was played from another client since. Nothing
    // on the connection tells a death by this hang-up from one by a hang-up made
    // on another client in between.
    [Fact]
    public void TheRecord_ClaimsOnlyWhereThisClientLastHadTheCharacter()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50 };
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, new RoomKey(1, 3));

        h.ComeBack(hp: 200, lives: 6, Snap());

        Assert.Contains("where this client last had the character", Assert.Single(h.Deaths).Message);
        string notice = Assert.Single(h.Notices);
        Assert.Contains("This client last had it in the game at", notice);
        Assert.Contains("another client", notice);
    }

    // ----- The room it comes back in ----------------------------------------
    //
    // A death puts the character where the dead of that room arrive. So the first
    // room the map is sure of on the way back in, before the character has moved,
    // is one more mark: there, having hung up somewhere else, the hang-up killed
    // it; back where it left, or anywhere else, it did not. Darkwood Forest's dead
    // arrive in the Temple, Halls of the Dead.

    private static readonly RoomKey Darkwood = new(1, 109);
    private static readonly RoomKey HallsOfTheDead = new(1, 2189);
    private const string BackInTheHalls =
        "Temple, Halls of the Dead (1/2189), where the dead of Darkwood Forest (1/109) arrive";

    private static Harness WithArrivalRooms(bool stock = true)
    {
        Harness h = new() { MaxItems = 0, PveShare = 50, Stock = stock };
        h.RoomNames[Darkwood] = "Darkwood Forest";
        h.RoomNames[HallsOfTheDead] = "Temple, Halls of the Dead";
        return h;
    }

    // The owner's example: in Darkwood Forest, low, hung up under a penalty; back
    // in the Halls of the Dead with a life less.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HungUpLowInDarkwood_BackInTheHallsOfTheDead_ALifeDown_IsRecorded_NamingTheRoom(bool stock)
    {
        Harness h = WithArrivalRooms(stock);
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: 40, Darkwood);

        h.ComeBack(hp: 200, lives: 6, Snap(), room: HallsOfTheDead);

        MudPlay.Game.Recovery.UnwitnessedDeath death = Assert.Single(h.Deaths);
        Assert.Equal((1, 109), (death.Room!.Map, death.Room.Room));
        Assert.Equal(6, death.LivesRemaining);
        Assert.Equal($"{HangupItemRecheck.DeathMessage} It came back in {BackInTheHalls}.", death.Message);
        Assert.Contains($": it came back in {BackInTheHalls}. ", Assert.Single(h.Notices));
        Assert.Contains($"lives went from 7 to 6, and it came back in {BackInTheHalls}", h.Check.LastDeathCheck);
        Assert.Contains("a death by this hang-up puts it there", h.Check.LastDeathCheck);
    }

    // A life is gone, but the character stands where it left the game: no death
    // moved it, so that life was lost somewhere else.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BackWhereItLeft_ALifeDown_IsNotThisHangUps(bool stock)
    {
        Harness h = WithArrivalRooms(stock);
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, Darkwood);

        h.ComeBack(hp: 200, lives: 6, Snap(), room: Darkwood);

        Assert.Empty(h.Deaths);
        string notice = Assert.Single(h.Notices);
        Assert.Contains("No death was recorded", notice);
        Assert.Contains("it came back in Darkwood Forest (1/109), the room it left the game in", notice);
        Assert.Contains("can't tell", h.Check.LastDeathCheck);
    }

    // Back in a room that is neither. Where the list is the world's own, the life
    // was lost somewhere else: an unmodified Stock set, and Paradigm for a room it
    // shares with Stock (the owner, 2026-10-10). On a Stock board whose sysop may
    // have edited its rooms the list can't say a room is the wrong one, and the
    // lives decide as they did.
    [Theory]
    [InlineData(true, false, 0)]
    [InlineData(false, false, 0)]
    [InlineData(true, true, 1)]
    public void BackSomewhereElse_ALifeDown_IsNotThisHangUps_WhereTheListIsTheWorldsOwn(bool stock, bool edited, int recorded)
    {
        Harness h = WithArrivalRooms(stock);
        h.EditedBoard = edited;
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, Darkwood);

        h.ComeBack(hp: 200, lives: 6, Snap(), room: new RoomKey(1, 110));

        Assert.Equal(recorded, h.Deaths.Count);
        if (recorded == 1) Assert.Equal(HangupItemRecheck.DeathMessage, h.Deaths[0].Message);
        else Assert.Contains("which is not where a death by this hang-up puts it", Assert.Single(h.Notices));
    }

    // The lives weren't read on the connection it left on, so there is no count to
    // compare. Back in the Halls of the Dead with nothing left of what a death
    // takes, the room says it.
    [Fact]
    public void LivesNotReadBefore_BackStrippedInTheHallsOfTheDead_IsRecorded()
    {
        Harness h = WithArrivalRooms();
        h.StoredAt(40, Geared, lives: null, room: new RoomRef(1, 109));

        h.ComeBack(hp: 200, lives: 6, Snap(), room: HallsOfTheDead);

        MudPlay.Game.Recovery.UnwitnessedDeath death = Assert.Single(h.Deaths);
        Assert.Equal(6, death.LivesRemaining);
        Assert.Contains($"It came back in {BackInTheHalls}.", death.Message);
        Assert.Contains("its lives weren't read on the connection it left the game on", h.Check.LastDeathCheck);
        Assert.Single(death.Equipped!);
    }

    // The record carries the lives the character has now, so with no `stat` read
    // yet one is asked for, where before nothing could have told.
    [Fact]
    public void LivesNotReadBefore_BackStrippedInTheHallsOfTheDead_WaitsForAStat()
    {
        Harness h = WithArrivalRooms();
        h.StoredAt(40, Geared, lives: null, room: new RoomRef(1, 109));

        h.ComeBack(hp: 200, lives: null, Snap(), room: HallsOfTheDead);
        Assert.Empty(h.Deaths);
        Assert.Contains("stat\r", h.Sent);

        h.Lives = 6;
        h.ReadLives(6);
        Assert.Equal(6, Assert.Single(h.Deaths).LivesRemaining);
    }

    // The room alone records nothing: a character that walked to the temple on
    // another client and left the game there still holds its gear.
    [Fact]
    public void LivesNotReadBefore_BackInTheHallsOfTheDead_StillHoldingItsGear_IsNotRecorded()
    {
        Harness h = WithArrivalRooms();
        h.StoredAt(40, Geared, lives: null, room: new RoomRef(1, 109));

        h.ComeBack(hp: 200, lives: 6, Geared, room: HallsOfTheDead);

        Assert.Empty(h.Deaths);
        Assert.Contains("its lives weren't read on the connection it left the game on", Assert.Single(h.Notices));
    }

    // Hung up in the Halls of the Dead themselves: standing there afterwards says
    // nothing, and the lives decide alone, as they did.
    [Fact]
    public void HungUpInTheRoomItsDeadArriveIn_TheRoomSaysNothing()
    {
        Harness h = WithArrivalRooms();
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, HallsOfTheDead);
        h.ComeBack(hp: 200, lives: 6, Snap(), room: HallsOfTheDead);

        Assert.Equal(HangupItemRecheck.DeathMessage, Assert.Single(h.Deaths).Message);
        Assert.Contains("which says nothing either way", h.Check.LastDeathCheck);

        // With no count from before, nothing says it there.
        Harness o = WithArrivalRooms();
        o.StoredAt(-5, Geared, lives: null, room: new RoomRef(1, 2189));
        o.ComeBack(hp: 200, lives: 6, Snap(), room: HallsOfTheDead);
        Assert.Empty(o.Deaths);
    }

    // The map isn't sure of the room on the way back in: the mark says nothing.
    [Fact]
    public void TheRoomItCameBackIn_NotConfirmed_SaysNothing()
    {
        Harness h = WithArrivalRooms();
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, Darkwood);

        h.Room = null;
        h.Now = h.Now.AddMinutes(1);
        h.Check.NoteConnected();
        h.BoardSaysHungUp();
        h.Check.NoteRoomDisplayed();
        h.Vitals = (200, 200);
        h.Check.OnInGameChanged(true);
        h.ReadLives(6);
        h.ReadInventory(Snap());

        Assert.Equal(HangupItemRecheck.DeathMessage, Assert.Single(h.Deaths).Message);
        Assert.Contains("the room it came back in not confirmed before it moved", h.Check.LastDeathCheck);
    }

    // Only the room it comes back in counts, not one it has walked to: with a move
    // sent before any room was confirmed, the room it is confirmed in later (here
    // the one it left, which would say "no death") is not taken.
    [Fact]
    public void ARoomConfirmedOnlyAfterAMove_IsNotTheRoomItCameBackIn()
    {
        Harness h = WithArrivalRooms();
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, Darkwood);

        h.Room = Darkwood;
        h.Now = h.Now.AddMinutes(1);
        h.Check.NoteConnected();
        h.BoardSaysHungUp();
        h.Vitals = (200, 200);
        h.Check.OnInGameChanged(true);
        h.Check.NoteMoveSent();
        h.Check.NoteRoomDisplayed();
        h.ReadLives(6);
        h.ReadInventory(Snap());

        Assert.Single(h.Deaths);
        Assert.Contains("the room it came back in not confirmed before it moved", h.Check.LastDeathCheck);
    }

    // A connection that came and went with no inventory read saw the room the
    // character came back in. The one that judges the list finds it somewhere
    // else by then, so the first one's room is kept on the list for it.
    [Fact]
    public void AMiddleConnectionThatJudgedNothing_HandsOnTheRoomItCameBackIn()
    {
        Harness h = WithArrivalRooms();
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, Darkwood);

        h.Room = HallsOfTheDead;
        h.Now = h.Now.AddMinutes(1);
        h.Check.NoteConnected();
        h.BoardSaysHungUp();
        h.Check.NoteRoomDisplayed();
        h.Vitals = (200, 200);
        h.Check.OnInGameChanged(true);
        h.Check.NoteLinkDropping();
        h.Check.NoteDisconnected();

        RoomRef kept = h.Profile.HeldAtDisconnect!.EntryRoomSince!;
        Assert.Equal((1, 2189), (kept.Map, kept.Room));
        RoomRef back = JsonSerializer.Deserialize<CharacterProfile>(
            JsonSerializer.Serialize(h.Profile, JsonStore.Options), JsonStore.Options)!.HeldAtDisconnect!.EntryRoomSince!;
        Assert.Equal((1, 2189), (back.Map, back.Room));

        // Walked out of the temple on another client since: this entry is in a room
        // that, taken for the one it came back in, would say "no death".
        h.ComeBack(hp: 200, lives: 6, Snap(), boardLine: false, room: new RoomKey(1, 110));

        Assert.Contains($"It came back in {BackInTheHalls}.", Assert.Single(h.Deaths).Message);
    }

    // ----- Dropped in an arena room -----------------------------------------
    //
    // A hang-up while dropped in an arena room is the arena's death: the engine
    // puts HP back and moves the character to where that room's dead arrive, and
    // takes no life and no item, on any board, penalty or none. The lives can't
    // say it, so the room it comes back in does, and it is recorded as an arena
    // death: nothing lost, no pile. The Training Grounds' dead arrive in their own
    // Halls of the Dead (16/542).

    private static readonly RoomKey TrainingGrounds = new(16, 500);
    private static readonly RoomKey ArenaHalls = new(16, 542);
    private const string BackInTheArenaHalls =
        "Training Grounds, Halls of the Dead (16/542), where the dead of Training Grounds (16/500) arrive";

    // No penalty set for the realm unless a test gives one.
    private static Harness InTheArena(bool stock = true, int? pveShare = null)
    {
        Harness h = new() { MaxItems = 0, PveShare = pveShare, Stock = stock };
        h.RoomNames[TrainingGrounds] = "Training Grounds";
        h.RoomNames[ArenaHalls] = "Training Grounds, Halls of the Dead";
        return h;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DroppedInAnArenaRoom_BackWhereItsDeadArrive_IsRecordedAsAnArenaDeath(bool stock)
    {
        Harness h = InTheArena(stock);
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, TrainingGrounds);
        DateTimeOffset dropped = h.Profile.HeldAtDisconnect!.At;

        // HP put back, everything still held and worn, the lives as they were. A
        // free hang-up prints no board line.
        h.ComeBack(hp: 150, lives: 7, Geared, boardLine: false, room: ArenaHalls);

        MudPlay.Game.Recovery.UnwitnessedDeath death = Assert.Single(h.Deaths);
        Assert.True(death.NothingLost);
        Assert.Null(death.Equipped);
        Assert.Null(death.Lost);
        Assert.Null(death.Coins);
        Assert.Equal((16, 500), (death.Room!.Map, death.Room.Room));
        Assert.Equal(dropped, death.At);
        Assert.Equal(7, death.LivesRemaining);
        Assert.True(death.AtEntry);
        Assert.Equal($"{HangupItemRecheck.ArenaDeathMessage} It came back in {BackInTheArenaHalls}.", death.Message);

        string notice = Assert.Single(h.Notices);
        Assert.Contains("died in the arena after the link went down", notice);
        Assert.Contains($"came back in {BackInTheArenaHalls}", notice);
        Assert.Contains("it has 7 lives, and there is no pile to recover", notice);
        Assert.DoesNotContain("Recover Now", notice);
        Assert.Contains("died in the arena", h.Check.LastDeathCheck);
        Assert.Contains("(an arena room)", h.Check.LastDeathCheck);
        Assert.False(h.Held);
        Assert.Empty(h.Sent);
        // The list is spent, and what is held now replaces it.
        Assert.NotEmpty(h.Profile.HeldAtDisconnect!.Items);
        Assert.Equal(150, h.Profile.HeldAtDisconnect.Hp);
    }

    // No `stat` was read on the way in: one is asked for, since the record carries
    // a count read on this connection.
    [Fact]
    public void AnArenaDeath_WithNoStatRead_AsksForOne_AndRecordsWithItsCount()
    {
        Harness h = InTheArena();
        h.StoredAt(-5, Geared, lives: null, room: new RoomRef(16, 500), inFight: false);

        h.ComeBack(hp: 150, lives: null, Geared, boardLine: false, room: ArenaHalls);
        Assert.Empty(h.Deaths);
        Assert.Contains("stat\r", h.Sent);
        Assert.True(h.Held);

        h.Lives = 6;
        h.ReadLives(6);
        MudPlay.Game.Recovery.UnwitnessedDeath death = Assert.Single(h.Deaths);
        Assert.True(death.NothingLost);
        Assert.Equal(6, death.LivesRemaining);
        Assert.False(h.Held);
    }

    // On Stock the arena switch can be off, and an arena room is then a room like
    // any other: a penalised hang-up while dropped there is an ordinary death, a
    // life down and stripped, with a pile.
    [Fact]
    public void DroppedInAnArenaRoom_BackStrippedWithALifeDown_IsAnOrdinaryDeath_WithItsPile()
    {
        Harness h = InTheArena(pveShare: 50);
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, TrainingGrounds);

        h.ComeBack(hp: 200, lives: 6, Snap(), room: ArenaHalls);

        MudPlay.Game.Recovery.UnwitnessedDeath death = Assert.Single(h.Deaths);
        Assert.False(death.NothingLost);
        Assert.Single(death.Equipped!);
        Assert.Equal(6, death.LivesRemaining);
        Assert.Equal($"{HangupItemRecheck.DeathMessage} It came back in {BackInTheArenaHalls}.", death.Message);
        Assert.Contains("Recover Now", Assert.Single(h.Notices));
    }

    // Back dropped where it left (the arena switch off, the hang-up free or too
    // small to kill), or standing there: no death of either kind, and nothing said.
    [Theory]
    [InlineData(-5, null)]
    [InlineData(-5, 50)]
    [InlineData(150, null)]
    public void DroppedInAnArenaRoom_BackWhereItLeft_IsNoDeath(int hpAtEntry, int? pveShare)
    {
        Harness h = InTheArena(pveShare: pveShare);
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, TrainingGrounds);

        h.ComeBack(hp: hpAtEntry, lives: 7, Geared, boardLine: false, room: TrainingGrounds);

        Assert.Empty(h.Deaths);
        Assert.Empty(h.Notices);
        Assert.Contains("no death", h.Check.LastDeathCheck);
        Assert.False(h.Held);
    }

    // Hung up in the arena's own Halls of the Dead, or back in a room the map
    // wasn't sure of: the room says nothing, and without it nothing tells an
    // arena death.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DroppedInAnArenaRoom_WithNothingTheRoomCanSay_RecordsNothing(bool hungUpInTheHalls)
    {
        Harness h = InTheArena();
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, hungUpInTheHalls ? ArenaHalls : TrainingGrounds);

        h.Now = h.Now.AddMinutes(1);
        h.Room = hungUpInTheHalls ? ArenaHalls : null;
        h.Check.NoteConnected();
        h.Check.NoteRoomDisplayed();
        h.Vitals = (150, 200);
        h.Check.OnInGameChanged(true);
        h.ReadLives(7);
        h.ReadInventory(Geared);

        Assert.Empty(h.Deaths);
        Assert.Empty(h.Notices);
        Assert.Contains("no arena death", h.Check.LastDeathCheck);
    }

    // Standing in an arena room when the link went down, on a realm with no
    // penalty: nothing to look into, and movement is not held.
    [Fact]
    public void StandingInAnArenaRoom_OnARealmWithNoPenalty_IsNotLookedAt()
    {
        Harness h = InTheArena();
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: 40, TrainingGrounds);

        h.Room = ArenaHalls;
        h.Check.NoteConnected();

        Assert.False(h.Held);
    }

    // An arena death costs no life, so it is recorded with no count from before
    // the hang-up (the owner, 2026-10-10), here for a character that also held
    // nothing a death takes. On a realm that doesn't penalise the hang-up it can
    // only have been the arena's death.
    [Fact]
    public void AnArenaDeath_WithNoCountFromBefore_AndNothingOwned_IsRecorded_WhereNoPenaltyCouldHaveKilled()
    {
        Harness h = InTheArena();
        h.StoredAt(-5, Snap(), lives: null, room: new RoomRef(16, 500), inFight: false);

        h.ComeBack(hp: 150, lives: 6, Snap(), boardLine: false, room: ArenaHalls);

        MudPlay.Game.Recovery.UnwitnessedDeath death = Assert.Single(h.Deaths);
        Assert.True(death.NothingLost);
        Assert.Equal(6, death.LivesRemaining);
    }

    // The one corner told and not recorded: the realm penalises that hang-up, so
    // with the board's arenas off it would have been an ordinary death, a life
    // gone. With no count from before and a pack that can't say, nothing tells
    // which of the two it was.
    [Fact]
    public void AnArenaDeath_WithNoCountAndAPackThatCantSay_OnARealmThatPenalises_IsTold_NotRecorded()
    {
        Harness h = InTheArena(pveShare: 50);
        h.StoredAt(-5, Snap(), lives: null, room: new RoomRef(16, 500));

        h.ComeBack(hp: 150, lives: 6, Snap(), boardLine: false, room: ArenaHalls);

        Assert.Empty(h.Deaths);
        string notice = Assert.Single(h.Notices);
        Assert.Contains("No death was recorded", notice);
        Assert.Contains("doesn't tell an arena death, which takes nothing, from an ordinary one", notice);
    }

    // ----- Rooms the map can't tell apart ------------------------------------
    //
    // The dead of map 11's arena arrive in the Portal Room 11/35, which prints only
    // its south exit: the map confirms the other Portal Room, 11/37, whose exits
    // are exactly that. The dead of the Dark Cave 15/2057 arrive in 15/2056, which
    // looks the same, so the map never leaves 15/2057. The graph says which rooms
    // look alike (RoomGraphManager.CouldBeTakenFor; pinned with the real rows in
    // HangupDeathArrivalRoomsTests).

    private static Harness WithLookAlikes(int? pveShare)
    {
        Harness h = new() { MaxItems = 0, PveShare = pveShare };
        h.CouldBeTakenFor = (shown, other) =>
            (shown == new RoomKey(11, 35) && other == new RoomKey(11, 37))
            || (shown == new RoomKey(15, 2056) && other == new RoomKey(15, 2057));
        h.RoomNames[new RoomKey(11, 5)] = "Crimson Passage";
        h.RoomNames[new RoomKey(11, 44)] = "Ebony Passage";
        h.RoomNames[new RoomKey(11, 35)] = "Portal Room";
        h.RoomNames[new RoomKey(11, 37)] = "Portal Room";
        return h;
    }

    [Fact]
    public void AnArenaDeath_ConfirmedInTheArrivalRoomsTwin_IsStillRecorded_AndNamesTheRealRoom()
    {
        Harness h = WithLookAlikes(pveShare: null);
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, new RoomKey(11, 5));

        h.ComeBack(hp: 150, lives: 7, Geared, boardLine: false, room: new RoomKey(11, 37));

        MudPlay.Game.Recovery.UnwitnessedDeath death = Assert.Single(h.Deaths);
        Assert.True(death.NothingLost);
        Assert.Contains("It came back in Portal Room (11/37), which the map can't tell from Portal Room (11/35), "
            + "where the dead of Crimson Passage (11/5) arrive.", death.Message);
    }

    // An ordinary hang-up death in 11/44 on Stock (not an arena room): a life down
    // and stripped, confirmed in 11/37. It is recorded, as it was before the room
    // was read at all, and not told as "the wrong room".
    [Fact]
    public void AnOrdinaryDeath_ConfirmedInTheArrivalRoomsTwin_IsRecorded()
    {
        Harness h = WithLookAlikes(pveShare: 50);
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, new RoomKey(11, 44));

        h.ComeBack(hp: 200, lives: 6, Snap(), room: new RoomKey(11, 37));

        MudPlay.Game.Recovery.UnwitnessedDeath death = Assert.Single(h.Deaths);
        Assert.False(death.NothingLost);
        Assert.Contains("Portal Room (11/37), which the map can't tell from Portal Room (11/35)", death.Message);
        Assert.Contains("Recover Now", Assert.Single(h.Notices));
    }

    // Killed in the Dark Cave, the map still shows the Dark Cave it left: that is
    // not "back where it left", and the lives decide alone.
    [Fact]
    public void ADeathInARoomThatLooksLikeItsArrivalRoom_IsNotTakenForNeverHavingMoved()
    {
        Harness h = WithLookAlikes(pveShare: 50);
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, new RoomKey(15, 2057));

        h.ComeBack(hp: 200, lives: 6, Snap(), room: new RoomKey(15, 2057));

        Assert.Equal(HangupItemRecheck.DeathMessage, Assert.Single(h.Deaths).Message);
        Assert.Contains("which says nothing either way", h.Check.LastDeathCheck);
    }

    // ----- What the review and the owner's rulings added ---------------------

    // No count was kept, and the character is back in the (ordinary) room it left:
    // no death moved it. The question closes without a word and the item check
    // has its say, where it used to be "can't tell".
    [Fact]
    public void BackInTheRoomItLeft_WithNoCountKept_IsNoDeath_AndTheItemCheckGoesOn()
    {
        Harness h = new() { PveShare = 50 };
        h.StoredAt(-5, Geared, lives: null);

        h.Room = new RoomKey(1, 3);
        h.Check.NoteConnected();
        h.BoardSaysHungUp();
        h.ShowRoom("3 torch");
        h.Vitals = (200, 200);
        h.Check.OnInGameChanged(true);
        h.ReadLives(6);
        h.ReadInventory(Snap());

        Assert.Empty(h.Deaths);
        Assert.Contains("no death", h.Check.LastDeathCheck);
        Assert.Contains("the room it left the game in", h.Check.LastDeathCheck);
        Assert.DoesNotContain(h.Notices, n => n.Contains("No death was recorded"));
        // The item check goes on in the same pass, still holding movement: the
        // torches on the floor are asked for, and nothing has been said yet.
        Assert.Contains(("torch", 3), h.Collected);
        Assert.True(h.Held);
        Assert.Empty(h.Notices);

        // They land, and the pass ends as any pickup does: what came back, what
        // didn't, the equipment manager asked, movement let go.
        h.Check.OnItemTaken("torch", 3);

        Assert.False(h.Held);
        Assert.Equal(2, h.Notices.Count);
        Assert.Contains("picked up 3 torch", h.Notices[0]);
        Assert.Contains("chainmail hauberk", h.Notices[1]);
        Assert.Contains("rope", h.Notices[1]);
        Assert.Equal(new[] { "torch" }, Assert.Single(h.GearAsked));
        Assert.Empty(h.Deaths);
        Assert.Null(h.Profile.DeathHistory);
    }

    // A connection that came and went with no inventory read never learned the
    // room it came back in, and moved on. The room the next connection starts in
    // is where it walked to, not where the hang-up left it: read as the entry
    // room it would say "it never moved" (or, in the temple, "it died") wrongly.
    [Theory]
    [InlineData(true)]    // walked back to where it hung up: must not read as "never moved"
    [InlineData(false)]   // walked into the temple alive: must not read as "back where its dead arrive"
    public void AMiddleConnectionThatMovedBeforeItsRoomWasKnown_LeavesTheRoomUnknownForGood(bool died)
    {
        Harness h = WithArrivalRooms();
        if (died) h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, Darkwood);
        else h.StoredAt(40, Geared, lives: null, room: new RoomRef(1, 109));

        // The map isn't sure of the room at the first display, and a move goes out.
        h.Room = null;
        h.Now = h.Now.AddMinutes(1);
        h.Check.NoteConnected();
        h.BoardSaysHungUp();
        h.Check.NoteRoomDisplayed();
        h.Vitals = (200, 200);
        h.Check.OnInGameChanged(true);
        h.Check.NoteMoveSent();
        h.Room = died ? Darkwood : HallsOfTheDead;
        h.Check.NoteRoomDisplayed();
        h.Check.NoteLinkDropping();
        h.Check.NoteDisconnected();

        HeldAtDisconnect list = h.Profile.HeldAtDisconnect!;
        Assert.Null(list.EntryRoomSince);
        Assert.True(list.MovedBeforeEntryRoomKnownSince);
        Assert.True(JsonSerializer.Deserialize<CharacterProfile>(
            JsonSerializer.Serialize(h.Profile, JsonStore.Options), JsonStore.Options)!
            .HeldAtDisconnect!.MovedBeforeEntryRoomKnownSince);

        h.ComeBack(hp: 200, lives: 6, Snap(), boardLine: false, room: died ? Darkwood : HallsOfTheDead);

        if (died)
        {
            // A life down, and the room says nothing: recorded, as by the lives alone.
            Assert.Equal(HangupItemRecheck.DeathMessage, Assert.Single(h.Deaths).Message);
        }
        else
        {
            // No count from before, and no room to go by: can't tell.
            Assert.Empty(h.Deaths);
            Assert.Contains("only a life lost tells a death", h.Check.LastDeathCheck);
        }
        Assert.Contains("the room it came back in not confirmed before it moved", h.Check.LastDeathCheck);
    }

    // With the master switch off no `stat` is sent. A character back stripped
    // where its dead arrive, with no count from before, is still in question, and
    // is told so with what will answer it, as one with a count is.
    [Fact]
    public void AutoAllOff_BackStrippedWhereItsDeadArrive_WithNoCountFromBefore_IsToldAStatWillAnswer()
    {
        Harness h = WithArrivalRooms();
        h.AutoAll = false;
        h.StoredAt(40, Geared, lives: null, room: new RoomRef(1, 109));

        h.ComeBack(hp: 200, lives: null, Snap(), room: HallsOfTheDead);

        Assert.Empty(h.Sent);
        Assert.Empty(h.Deaths);
        string notice = Assert.Single(h.Notices);
        Assert.Contains("Auto-All is off", notice);
        Assert.Contains("Type `stat` and `i`", notice);

        h.Lives = 6;
        h.ReadLives(6);
        Assert.Contains($"It came back in {BackInTheHalls}.", Assert.Single(h.Deaths).Message);
    }

    // The same character still holding its gear: a `stat` would answer nothing,
    // so nothing asks for one.
    [Fact]
    public void AutoAllOff_BackWhereItsDeadArrive_StillHoldingItsGear_IsNotToldToTypeStat()
    {
        Harness h = WithArrivalRooms();
        h.AutoAll = false;
        h.StoredAt(40, Geared, lives: null, room: new RoomRef(1, 109));

        h.ComeBack(hp: 200, lives: null, Geared, room: HallsOfTheDead);

        Assert.Empty(h.Deaths);
        Assert.DoesNotContain(h.Notices, n => n.Contains("Type `stat`"));
    }

    // Naked and broke: the character owned nothing a death takes, so there is
    // nothing for it to come back stripped of. With no count from before, the
    // room it comes back in is enough (the owner, 2026-10-10).
    [Fact]
    public void OwningNothingADeathTakes_WithNoCountFromBefore_BackWhereItsDeadArrive_IsRecorded()
    {
        Harness h = WithArrivalRooms();
        h.Stays.Add("soulbound amulet");
        h.StoredAt(-5, Snap(carried: ["soulbound amulet"]), lives: null, room: new RoomRef(1, 109));

        h.ComeBack(hp: 200, lives: 6, Snap(carried: ["soulbound amulet"]), room: HallsOfTheDead);

        MudPlay.Game.Recovery.UnwitnessedDeath death = Assert.Single(h.Deaths);
        Assert.Equal(6, death.LivesRemaining);
        Assert.Empty(death.Equipped!);
        Assert.Empty(death.Lost!);
        Assert.Contains($"It came back in {BackInTheHalls}.", death.Message);
        Assert.Contains("held nothing a death takes when it left", h.Check.LastDeathCheck);
    }

    // Every "no" that still applies is kept for it: the board's lines on Stock,
    // the room it left, HP at the first prompt.
    [Theory]
    [InlineData(false, 2189, 200)]   // Stock, and the board printed no hang-up lines
    [InlineData(true, 109, 200)]     // back in the room it left
    [InlineData(true, 2189, -5)]     // back where its dead arrive, still dropped
    public void OwningNothingADeathTakes_IsNotRecordedAgainstWhatStillSaysNo(bool boardLine, int roomBack, int hpAtEntry)
    {
        Harness h = WithArrivalRooms();
        h.StoredAt(-5, Snap(), lives: null, room: new RoomRef(1, 109));

        h.ComeBack(hp: hpAtEntry, lives: 6, Snap(), boardLine: boardLine, room: new RoomKey(1, roomBack));

        Assert.Empty(h.Deaths);
    }

    // What a character held isn't the same as holding nothing: a list written
    // with no inventory read doesn't know, and the room alone is not enough then.
    [Fact]
    public void AListThatDoesNotKnowWhatWasHeld_IsNotTakenForOwningNothing()
    {
        Harness h = WithArrivalRooms();
        h.StoredAt(-5, Snap(), lives: null, room: new RoomRef(1, 109));
        h.Profile.HeldAtDisconnect!.ItemsUnknown = true;

        h.ComeBack(hp: 200, lives: 6, Snap(), room: HallsOfTheDead);

        Assert.Empty(h.Deaths);
        Assert.Contains("what it holds doesn't bear a death out", Assert.Single(h.Notices));
    }

    // ----- Paradigm's own rooms ----------------------------------------------
    //
    // Paradigm sends the dead of Albion to 14/1077, the Burial Vault, and Albion
    // is "the rooms that can be reached from 14/1077 without taking the boat" (the
    // owner, 2026-10-10). For a room in it, being found in the vault is a yes and
    // no other room is ever a no. Paradigm's other own rooms (Port Blackwater,
    // Terra Ista, Terra Fuego, Tal'kiran, Shadowmere) have no known arrival room.
    // The Stock list speaks only for the zones Paradigm shares with Stock: 14/1 is
    // a Stock room number, and on Paradigm it is the Bank of Albion.

    private static readonly RoomKey Kingsport = new(14, 5000);
    private static readonly RoomKey BankOfAlbion = new(14, 1);
    private static readonly RoomKey BurialVault = new(14, 1077);

    private static Harness OnParadigm()
    {
        Harness h = new() { MaxItems = 0, PveShare = 50, Stock = false };
        h.Albion.UnionWith([BurialVault, Kingsport, BankOfAlbion]);
        h.RoomNames[BurialVault] = "Burial Vault";
        h.RoomNames[BankOfAlbion] = "Bank of Albion";
        return h;
    }

    private const string BackInTheVault = "Burial Vault (14/1077), where the dead of ";
    private const string ByAlbion = " arrive (Albion: reachable from that room without a boat)";

    [Fact]
    public void Paradigm_HungUpInAlbion_BackInTheBurialVault_IsRecorded_NamingIt()
    {
        Harness h = OnParadigm();
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, Kingsport);

        h.ComeBack(hp: 200, lives: 6, Snap(), boardLine: false, room: BurialVault);

        Assert.Equal($"{HangupItemRecheck.DeathMessage} It came back in {BackInTheVault}room 14/5000{ByAlbion}.",
            Assert.Single(h.Deaths).Message);

        // With no count from before, back there stripped is enough.
        Harness o = OnParadigm();
        o.StoredAt(-5, Geared, lives: null, room: new RoomRef(14, 5000));
        o.ComeBack(hp: 200, lives: 6, Snap(), boardLine: false, room: BurialVault);
        Assert.Single(o.Deaths);
    }

    // The case that was lost by reading Stock's list by room number: 14/1 is in
    // Stock's range, where that list sends its dead to the Temple. On Paradigm it
    // is the Bank of Albion. A life down and stripped in the Burial Vault is a
    // death, with a count from before or without one.
    [Theory]
    [InlineData(7)]
    [InlineData(null)]
    public void Paradigm_AStockRoomNumberThatIsAlbion_BackInTheBurialVault_IsRecorded(int? livesBefore)
    {
        Harness h = OnParadigm();
        h.StoredAt(-5, Geared, lives: livesBefore, room: new RoomRef(14, 1));

        h.ComeBack(hp: 200, lives: 6, Snap(), boardLine: false, room: BurialVault);

        MudPlay.Game.Recovery.UnwitnessedDeath death = Assert.Single(h.Deaths);
        Assert.Equal(6, death.LivesRemaining);
        Assert.Equal($"{HangupItemRecheck.DeathMessage} It came back in {BackInTheVault}Bank of Albion (14/1){ByAlbion}.",
            death.Message);
    }

    // Anywhere else, the room says nothing for an Albion room: a hang-up goes
    // through the room's leave-the-game room first and Paradigm's aren't in the
    // data, so neither "the wrong room" nor "it never moved" can be said, and the
    // lives decide alone.
    [Theory]
    [InlineData(14, 5001)]
    [InlineData(14, 5000)]
    [InlineData(1, 2189)]
    public void Paradigm_HungUpInAlbion_NoOtherRoomSaysNo(int map, int room)
    {
        Harness h = OnParadigm();
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, Kingsport);

        h.ComeBack(hp: 200, lives: 6, Snap(), boardLine: false, room: new RoomKey(map, room));

        Assert.Equal(HangupItemRecheck.DeathMessage, Assert.Single(h.Deaths).Message);
    }

    // One of Paradigm's own rooms outside Albion: Terra Ista (14/1813, across the
    // water), Port Blackwater (14/9, a Stock room number), Shadowmere (5/3, Stock's
    // Tournament Arena), map 18. Where its dead arrive isn't known, so the Burial
    // Vault says nothing for it either. With a life down the lives decide alone,
    // and the record names no room; with no count from before nothing tells.
    [Theory]
    [InlineData(14, 1813)]
    [InlineData(14, 9)]
    [InlineData(5, 3)]
    [InlineData(18, 40)]
    public void Paradigm_OneOfItsOwnRoomsOutsideAlbion_TheBurialVaultSaysNothing(int map, int room)
    {
        Harness h = OnParadigm();
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, new RoomKey(map, room));
        h.ComeBack(hp: 200, lives: 6, Snap(), boardLine: false, room: BurialVault);
        Assert.Equal(HangupItemRecheck.DeathMessage, Assert.Single(h.Deaths).Message);
        Assert.Contains("which says nothing either way", h.Check.LastDeathCheck);

        Harness o = OnParadigm();
        o.StoredAt(-5, Geared, lives: null, room: new RoomRef(map, room));
        o.ComeBack(hp: 200, lives: 6, Snap(), boardLine: false, room: BurialVault);
        Assert.Empty(o.Deaths);
        Assert.Contains("only a life lost tells a death", Assert.Single(o.Notices));
    }

    // A room Paradigm only renamed is still shared with Stock: 15/1530 is `Old
    // Mine Tunnel` there and `Webbed Mine Tunnel` here, with the same exits. Its
    // dead arrive in the Temple on both, and another room is the wrong one.
    [Fact]
    public void Paradigm_ARoomOnlyRenamed_StillGoesByTheStockList()
    {
        Harness h = OnParadigm();
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, new RoomKey(15, 1530));
        h.ComeBack(hp: 200, lives: 6, Snap(), boardLine: false, room: HallsOfTheDead);
        Assert.Contains("It came back in room 1/2189, where the dead of room 15/1530 arrive.", Assert.Single(h.Deaths).Message);

        Harness o = OnParadigm();
        o.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, new RoomKey(15, 1530));
        o.ComeBack(hp: 200, lives: 6, Snap(), boardLine: false, room: BurialVault);
        Assert.Empty(o.Deaths);
        Assert.Contains("which is not where a death by this hang-up puts it", Assert.Single(o.Notices));
    }

    // ----- A room whose arrival room was learned -------------------------------
    //
    // No list says where the dead of Terra Ista arrive. A death the client saw
    // there ended in a shrine, and the set remembers it (LearnedArrivalRoomStore).
    // A hang-up in that room, back in the shrine, is back where its dead arrive.
    // It is a room to say yes in and no more: nothing is "the wrong room" on its
    // strength, and the room it left is not "never moved".

    private static readonly RoomKey TerraIsta = new(14, 1813);
    private static readonly RoomKey IsleShrine = new(14, 1900);

    private static Harness WithALearnedArrival()
    {
        Harness h = OnParadigm();
        h.LearnedArrivals[TerraIsta] = IsleShrine;
        h.RoomNames[IsleShrine] = "Isle Shrine";
        return h;
    }

    [Theory]
    [InlineData(7)]
    [InlineData(null)]
    public void ALearnedArrivalRoom_BackThereAfterAHangUp_IsADeath_AndSaysHowItIsKnown(int? livesBefore)
    {
        Harness h = WithALearnedArrival();
        h.StoredAt(-5, Geared, lives: livesBefore, room: new RoomRef(14, 1813));

        h.ComeBack(hp: 200, lives: 6, Snap(), boardLine: false, room: IsleShrine);

        Assert.Equal($"{HangupItemRecheck.DeathMessage} It came back in Isle Shrine (14/1900), where the dead of room 14/1813 "
            + "arrive (learned from a death seen there).", Assert.Single(h.Deaths).Message);
        Assert.Equal("14/1813 → 14/1900", h.Check.LastLearnedArrivalUsed);
    }

    // A death seen in one room stands for its land: hung up on the path beside
    // the landing, back in the shrine the landing's dead were seen to arrive in.
    // The record and the bug report say which room the death was seen in.
    [Fact]
    public void ARoomOfTheLandADeathWasSeenIn_BackWhereThatDeathEnded_IsADeath_AndSaysWhoseReadingItIs()
    {
        RoomKey path = new(14, 1814);
        Harness h = OnParadigm();
        h.LearnedArrivals[path] = IsleShrine;
        h.LearnedIn = TerraIsta;
        h.RoomNames[IsleShrine] = "Isle Shrine";
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, path);

        h.ComeBack(hp: 200, lives: 6, Snap(), boardLine: false, room: IsleShrine);

        Assert.Equal($"{HangupItemRecheck.DeathMessage} It came back in Isle Shrine (14/1900), where the dead of room 14/1814 "
            + "arrive (learned from a death seen in room 14/1813, a room of the same land).", Assert.Single(h.Deaths).Message);
        Assert.Equal("14/1813 → 14/1900 (standing for 14/1814, a room of its land)", h.Check.LastLearnedArrivalUsed);
    }

    // A learned room is where a DEATH there ends, which can be where a hang-up
    // leaves a character that lived. So where the room alone would record a death
    // (no count from before, nothing owned for a death to take), a learned room
    // needs HP at the first prompt above what the character left with as well.
    [Fact]
    public void ALearnedArrivalRoom_IsNotEnoughByItself_ForACharacterThatOwnedNothing()
    {
        Harness h = WithALearnedArrival();
        h.StoredAt(-5, Snap(), lives: null, room: new RoomRef(14, 1813));
        h.ComeBack(hp: null, lives: 6, Snap(), boardLine: false, room: IsleShrine);
        Assert.Empty(h.Deaths);
        Assert.Contains("which is only known from a death seen to end there", h.Check.LastDeathCheck);
        Assert.Contains("nor its HP (not read at the first prompt) says which this was", Assert.Single(h.Notices));

        // HP read, and above the -5 it left with: recorded.
        Harness o = WithALearnedArrival();
        o.StoredAt(-5, Snap(), lives: null, room: new RoomRef(14, 1813));
        o.ComeBack(hp: 200, lives: 6, Snap(), boardLine: false, room: IsleShrine);
        Assert.Contains("(learned from a death seen there)", Assert.Single(o.Deaths).Message);

        // A room the list has needs no more than itself for that character.
        Harness l = WithArrivalRooms();
        l.StoredAt(-5, Snap(), lives: null, room: new RoomRef(1, 109));
        l.ComeBack(hp: null, lives: 6, Snap(), room: HallsOfTheDead);
        Assert.Single(l.Deaths);
    }

    // Anywhere else, the learned room says nothing: a hang-up leaves through the
    // room's leave-the-game room first, so its dead needn't end where a death's
    // do. With a life down the lives decide alone; with no count nothing tells.
    [Theory]
    [InlineData(14, 1813)]   // the room it left
    [InlineData(14, 1950)]   // some other room
    [InlineData(14, 1077)]   // Albion's vault, which is not this room's
    public void ALearnedArrivalRoom_NeverSaysNo(int map, int room)
    {
        Harness h = WithALearnedArrival();
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, TerraIsta);
        h.ComeBack(hp: 200, lives: 6, Snap(), boardLine: false, room: new RoomKey(map, room));
        Assert.Equal(HangupItemRecheck.DeathMessage, Assert.Single(h.Deaths).Message);
        Assert.Null(h.Check.LastLearnedArrivalUsed);

        Harness o = WithALearnedArrival();
        o.StoredAt(-5, Geared, lives: null, room: new RoomRef(14, 1813));
        o.ComeBack(hp: 200, lives: 6, Snap(), boardLine: false, room: new RoomKey(map, room));
        Assert.Empty(o.Deaths);
    }

    // What was learned is never set against the list where the list is the
    // world's own: a pair on file for Darkwood Forest (a zone Paradigm shares with
    // Stock, and Stock's own on Stock data) changes nothing.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ALearnedPair_IsNeverSetAgainstTheList_WhereTheListIsTheWorldsOwn(bool stock)
    {
        Harness h = WithArrivalRooms(stock);
        h.LearnedArrivals[Darkwood] = IsleShrine;
        h.PlayAndDrop(lives: 7, Geared, hpAtDrop: -5, Darkwood);

        // Back in the learned room, which the list doesn't have: the wrong room.
        h.ComeBack(hp: 200, lives: 6, Snap(), room: IsleShrine);

        Assert.Empty(h.Deaths);
        Assert.Contains("which is not where a death by this hang-up puts it", Assert.Single(h.Notices));
        Assert.Null(h.Check.LastLearnedArrivalUsed);
    }

    // On a Stock board whose rooms may have been edited the list is only Stock's
    // word, and what was seen there is one more room to say yes in.
    [Fact]
    public void OnAnEditedBoard_ALearnedPair_AddsARoomToSayYesIn()
    {
        Harness h = WithArrivalRooms();
        h.EditedBoard = true;
        h.LearnedArrivals[Darkwood] = new RoomKey(1, 500);
        h.StoredAt(-5, Geared, lives: null, room: new RoomRef(1, 109));

        h.ComeBack(hp: 200, lives: 6, Snap(), room: new RoomKey(1, 500));

        Assert.Contains("(learned from a death seen there)", Assert.Single(h.Deaths).Message);

        // The list's own room still says yes there.
        Harness o = WithArrivalRooms();
        o.EditedBoard = true;
        o.LearnedArrivals[Darkwood] = new RoomKey(1, 500);
        o.StoredAt(-5, Geared, lives: null, room: new RoomRef(1, 109));
        o.ComeBack(hp: 200, lives: 6, Snap(), room: HallsOfTheDead);
        Assert.Contains($"It came back in {BackInTheHalls}.", Assert.Single(o.Deaths).Message);
        Assert.Null(o.Check.LastLearnedArrivalUsed);
    }
}
