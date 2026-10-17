using System.Collections.Generic;
using System.Linq;
using System.Text;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// RoomSpellCounterWear over a real EquipmentManager and MovementCoordinator: which
// carried item goes on for a room's spell, where (next to the room and in it), what
// the backstop before a step does, what a refusal, a line that never comes or a wear
// that was never sent does, how sneaking past things changes the moment, and when
// the slot goes back (and to what). The items are the game data's, cut down to what
// is weighed: phoenix feather and magma amulet both negate magma heat #526 from the
// neck, 10 / 1 against 0 / 0.
public sealed class RoomSpellCounterWearTests
{
    private const int MagmaHeat = 526, FreezingCold = 5242;
    private const int Feather = 1000, Amulet = 487, SapphireRing = 433, WinterhideTunic = 3660;

    private sealed class World
    {
        public List<EquippedItem> Worn { get; } = new();
        public List<string> Pack { get; } = new();
        public Dictionary<RoomKey, Room> Rooms { get; } = new();
        public EquipmentSettings Sets { get; } = new();
        public MovementCoordinator Coordinator { get; } = new();
        public EquipmentManager Gear { get; }
        // What Gear put on the wire, through the sender the client binds.
        public List<byte[]> GearWire { get; } = new();
        public RoomSpellCounterWear Wear { get; }
        public LogService Log { get; } = new();
        public List<string> Info { get; } = new();
        public List<Action> Timers { get; } = new();
        public bool Enabled { get; set; } = true;
        public bool MasterOff { get; set; }
        public bool SneakKept { get; set; }
        public bool SneakingPast { get; set; }
        public bool RoomEmpty { get; set; } = true;
        public bool SendGateLocked { get; set; }
        public int GearNotes { get; private set; }
        public int UsabilityChanges { get; private set; }
        public HashSet<int> Unwearable { get; } = new();
        public RoomKey? Here { get; private set; }

        // The moving engine's plan, as room numbers on map 16, in the order entered.
        public List<int> Plan { get; } = new();

        // name → (id, slot label as the game prints it, slot, armour class, damage resist)
        private readonly Dictionary<string, (int Id, string Label, EquipmentSlot Slot, int Ac, int Dr)> _items =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["phoenix feather"] = (Feather, "Neck", EquipmentSlot.Neck, 10, 1),
                ["magma amulet"] = (Amulet, "Neck", EquipmentSlot.Neck, 0, 0),
                ["silver necklace"] = (9001, "Neck", EquipmentSlot.Neck, 5, 0),
                ["holy symbol"] = (9002, "Neck", EquipmentSlot.Neck, 8, 0),
                ["sapphire ring"] = (SapphireRing, "Finger", EquipmentSlot.Finger1, 10, 0),
                ["ruby ring"] = (9004, "Finger", EquipmentSlot.Finger1, 20, 0),
                ["emerald ring"] = (9005, "Finger", EquipmentSlot.Finger1, 15, 0),
                ["copper ring"] = (9006, "Finger", EquipmentSlot.Finger1, 1, 0),
                ["winterhide tunic"] = (WinterhideTunic, "Torso", EquipmentSlot.Torso, 270, 27),
                ["platemail"] = (9003, "Torso", EquipmentSlot.Torso, 400, 50),
                ["leather jerkin"] = (9007, "Torso", EquipmentSlot.Torso, 5, 0),
                ["crystal ward"] = (3701, "Worn", EquipmentSlot.Neck, 0, 0),
            };

        private static readonly Dictionary<int, int[]> Negators = new()
        {
            [MagmaHeat] = new[] { Amulet, Feather },
            [FreezingCold] = new[] { SapphireRing, WinterhideTunic },
        };

        public World()
        {
            Log.EntryAdded += e => { if (e.Severity == LogSeverity.Info) Info.Add(e.Message); };
            Gear = new EquipmentManager(
                readEquipment: () => Sets,
                getSnapshot: Snapshot,
                readCombat: () => new CombatSettings(),
                writeCombat: _ => { },
                resolveItemSlot: name => _items.TryGetValue(name, out var item) ? item.Slot : null,
                log: Log);
            Gear.SetWireSender(GearWire.Add);
            Gear.SetGearHold(() => SneakKept);
            Gear.SetSendGateProbe(() => SendGateLocked);
            Wear = new RoomSpellCounterWear(
                Gear, Coordinator,
                enabled: () => Enabled,
                roomOf: key => Rooms.GetValueOrDefault(key),
                currentRoom: () => Here,
                negatorsOf: spell => Negators.TryGetValue(spell, out int[]? ids) ? ids : Array.Empty<int>(),
                describe: id => Describe(_items.FirstOrDefault(i => i.Value.Id == id).Key),
                isWorn: id => Worn.Any(w => _items[w.Name].Id == id),
                isCarried: id => Worn.Any(w => _items[w.Name].Id == id) || Pack.Any(p => _items[p].Id == id),
                wornIn: slot => Worn.Where(w => EquipmentManager.SharePlaces(_items[w.Name].Slot, slot))
                    .Select(w => Describe(w.Name)!.Value).ToList(),
                spellName: spell => spell == MagmaHeat ? "magma heat" : spell == FreezingCold ? "freezing cold" : null,
                sneakingPast: () => SneakingPast,
                roomEmpty: () => RoomEmpty,
                plannedAhead: count => _planAt >= 0 && _planAt < Plan.Count
                    ? Plan.Skip(_planAt + 1).Take(count).Select(At).ToList()
                    : new List<RoomKey>(),
                schedule: (_, action) => Timers.Add(action),
                gearCommandSent: () => GearNotes++,
                log: Log)
            {
                MasterSwitchOff = () => MasterOff,
            };
            Wear.UsabilityChanged += () => UsabilityChanges++;
        }

        private RoomSpellCounterItem? Describe(string? name) =>
            name is not null && _items.TryGetValue(name, out var item)
                ? new RoomSpellCounterItem(item.Id, name, item.Slot, item.Ac, item.Dr, !Unwearable.Contains(item.Id))
                : null;

        private InventorySnapshot Snapshot() => new(
            CurrencyHoldings.Empty, EncumbranceReading.Empty, Worn.ToList(), Pack.ToList(), DateTimeOffset.UtcNow);

        public List<string> Sent => GearWire
            .Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();

        public bool Held => Coordinator.IsGateAsserted(MovementCoordinator.CounterWearGate);

        public void Arrive(int room)
        {
            Here = At(room);
            // The engine's place in its plan: the next step when this is it (a
            // loop comes through the same room more than once), else wherever
            // the room is on the plan.
            _planAt = _planAt + 1 < Plan.Count && Plan[_planAt + 1] == room ? _planAt + 1 : Plan.IndexOf(room);
            Wear.OnArrived(At(room));
        }

        private int _planAt = -1;

        // The game puts the piece on, in the order the client hears it. Each line
        // reaches the router's handlers first (the equipment manager, then the
        // counter) and the inventory's worn list only after, and a piece pushed out
        // of the slot has its own `You have removed <old>.` line ahead of the wear
        // line. A pair with a free place takes the piece and pushes nothing out;
        // a full one gives up `evicts`, or its first-listed piece.
        public void GameWears(string name, string? evicts = null)
        {
            var item = _items[name];
            List<EquippedItem> inSlot = Worn.Where(w => EquipmentManager.SharePlaces(_items[w.Name].Slot, item.Slot)).ToList();
            string? evicted = evicts ?? (inSlot.Count >= EquipmentManager.PlacesFor(item.Slot) ? inSlot[0].Name : null);
            if (evicted is not null) GameRemoves(evicted);
            Gear.NoteEquipSucceeded(name);
            Wear.NoteWorn(name);
            Pack.Remove(name);
            Worn.Add(new EquippedItem(name, item.Label));
            Wear.Recheck();   // the inventory's change notice
        }

        public void GameRemoves(string name)
        {
            Gear.NoteWornPieceRemoved();
            Wear.NoteRemoved(name);
            Worn.RemoveAll(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
            Pack.Add(name);
            Wear.Recheck();
        }

        // The game answers every command sent since the last call, in order.
        public void Answer()
        {
            for (; _answered < Sent.Count; _answered++)
            {
                string[] command = Sent[_answered].Split(' ', 2);
                if (command[0] is "wear" or "eq")
                {
                    if (Pack.Contains(command[1], StringComparer.OrdinalIgnoreCase)) GameWears(command[1]);
                    else Gear.NoteNotLeftUnequipped(command[1]);
                }
                else if (command[0] == "rem" && Worn.Any(w => w.Name == command[1]))
                    GameRemoves(command[1]);
            }
        }

        private int _answered;

        // Rooms 1..count in a line on map 16, joined east and west; `lava` are the
        // room numbers that carry magma heat.
        public void Line(int count, params int[] lava)
        {
            for (int n = 1; n <= count; n++)
            {
                var exits = new Dictionary<Direction, RoomExit>();
                if (n > 1) exits[Direction.W] = new RoomExit(new RoomKey(16, n - 1), RoomExitHint.None, RawHint: null);
                if (n < count) exits[Direction.E] = new RoomExit(new RoomKey(16, n + 1), RoomExitHint.None, RawHint: null);
                Rooms[new RoomKey(16, n)] = new Room
                {
                    Key = new RoomKey(16, n), Name = lava.Contains(n) ? "Lava Tube" : "Tunnel",
                    Spell = lava.Contains(n) ? MagmaHeat : 0, Exits = exits,
                };
            }
        }
    }

    private static RoomKey At(int room) => new(16, room);

    // The usual start: a necklace on, the feather and the amulet in the pack, lava
    // in rooms 3 and 4 of a line of twelve.
    private static World Volcano(params int[] lava)
    {
        World w = new();
        w.Worn.Add(new EquippedItem("silver necklace", "Neck"));
        w.Pack.AddRange(new[] { "phoenix feather", "magma amulet" });
        w.Line(12, lava.Length == 0 ? new[] { 3, 4 } : lava);
        return w;
    }

    // ----- where it is on: in the room, and next to it ---------------------------

    [Fact]
    public void NextToTheRoom_ItGoesOn_InIt_ItStaysOn_SteppedOutButStillNextTo_ItStaysOn_OneRoomFurther_ItComesOff()
    {
        World w = Volcano();

        w.Arrive(1);                                   // two rooms from the lava
        Assert.Empty(w.Sent);

        w.Arrive(2);                                   // next to it
        Assert.Equal(new[] { "wear phoenix feather" }, w.Sent);
        Assert.False(w.Held);
        Assert.Single(w.Info, l => l.Contains("wearing 'phoenix feather' for magma heat (#526) on arriving next to 16/3"));
        w.Answer();
        Assert.Equal(("phoenix feather", "silver necklace", MagmaHeat), w.Wear.OwnedSnapshot().Single());

        w.Arrive(3);                                   // in it
        w.Arrive(4);
        w.Arrive(5);                                   // out, with the lava still one room off
        Assert.Single(w.Sent);

        w.Arrive(6);                                   // neither in it nor next to it
        Assert.Equal(new[] { "wear phoenix feather", "wear silver necklace" }, w.Sent);
        Assert.Empty(w.Wear.OwnedSnapshot());
        Assert.Equal(2, w.GearNotes);                  // the wear, and the restore
        Assert.Single(w.Info, l => l.Contains("at or next to 16/6 — its slot was given back"));
    }

    [Fact]
    public void ALoopRunningAlongsideLava_KeepsItOn_WithoutSwapping()
    {
        // A corridor of six rooms, each with lava to its south.
        World w = new();
        w.Worn.Add(new EquippedItem("silver necklace", "Neck"));
        w.Pack.Add("phoenix feather");
        w.Line(6);
        for (int n = 1; n <= 6; n++)
        {
            RoomKey lava = new(16, 100 + n);
            var exits = new Dictionary<Direction, RoomExit>(w.Rooms[At(n)].Exits)
            {
                [Direction.S] = new RoomExit(lava, RoomExitHint.None, RawHint: null),
            };
            w.Rooms[At(n)] = new Room { Key = At(n), Name = "Ledge", Spell = 0, Exits = exits };
            w.Rooms[lava] = new Room
            {
                Key = lava, Name = "Lava Tube", Spell = MagmaHeat,
                Exits = new Dictionary<Direction, RoomExit> { [Direction.N] = new RoomExit(At(n), RoomExitHint.None, RawHint: null) },
            };
        }

        for (int lap = 0; lap < 3; lap++)
            foreach (int room in new[] { 1, 2, 3, 4, 5, 6, 5, 4, 3, 2 })
            {
                w.Arrive(room);
                w.Answer();
            }

        Assert.Equal(new[] { "wear phoenix feather" }, w.Sent);
    }

    [Fact]
    public void AWalkThatLeavesTheLavaByTwoRoomsAndComesBack_SwapsOnce_EachWay()
    {
        World w = Volcano();

        foreach (int room in new[] { 2, 3, 4, 5, 6, 5, 4 })
        {
            w.Arrive(room);
            w.Answer();
        }

        Assert.Equal(new[] { "wear phoenix feather", "wear silver necklace", "wear phoenix feather" }, w.Sent);
    }

    [Fact]
    public void StandingNextToTheRoom_ItGoesOnWhenItTurnsUpInThePack_OrTheSettingIsSwitchedOn()
    {
        World w = Volcano();
        w.Pack.Clear();
        w.Arrive(3);
        Assert.Empty(w.Sent);

        w.Pack.Add("phoenix feather");                 // picked up, or handed over
        w.Wear.Recheck();
        Assert.Equal(new[] { "wear phoenix feather" }, w.Sent);
        Assert.Single(w.Info, l => l.Contains("standing in 16/3"));

        World off = Volcano();
        off.Enabled = false;
        off.Arrive(2);
        Assert.Empty(off.Sent);
        off.Enabled = true;                            // saved on the tab
        off.Wear.Recheck();
        Assert.Equal(new[] { "wear phoenix feather" }, off.Sent);
    }

    [Fact]
    public void TheSameRoomConfirmedTwice_SendsNothingMore()
    {
        World w = Volcano();
        w.Arrive(2);
        w.Arrive(2);
        w.Answer();
        w.Arrive(2);

        Assert.Single(w.Sent);
    }

    // ----- the backstop before a step ------------------------------------------

    [Fact]
    public void BeforeAnEnginesStep_WithNoArrivalNextToTheRoomFirst_TheFeatherGoesOn_AndTheStepWaitsForTheGamesAnswer()
    {
        World w = Volcano();

        Assert.False(w.Wear.ReadyToEnter(At(3)));
        Assert.Equal(new[] { "wear phoenix feather" }, w.Sent);
        Assert.True(w.Held);
        Assert.Equal(1, w.GearNotes);   // a wear mid-fight is picked up by the combat engine

        // Asked again while it waits: still not ready, and nothing more is sent.
        Assert.False(w.Wear.ReadyToEnter(At(3)));
        Assert.Single(w.Sent);

        w.GameWears("phoenix feather");
        Assert.False(w.Held);
        Assert.True(w.Wear.ReadyToEnter(At(3)));
        Assert.Single(w.Sent);
        Assert.Equal(("phoenix feather", "silver necklace", MagmaHeat), w.Wear.OwnedSnapshot().Single());
        Assert.Single(w.Info, l => l.Contains("wearing 'phoenix feather' for magma heat (#526) before entering 16/3"));
        Assert.Single(w.Info, l => l.Contains("'phoenix feather' is on in place of 'silver necklace'"));
    }

    [Fact]
    public void AWalkersResume_InsideTheWearLine_BeforeTheInventoryHasIt_SendsNoSecondWear()
    {
        // The walker resumes inside the gate's release, which is inside the wear
        // line's handler: the inventory's worn list doesn't have the feather yet.
        World w = Volcano();
        bool stepSent = false;
        w.Coordinator.PauseStateChanged += paused =>
        {
            if (!paused && !stepSent && w.Wear.ReadyToEnter(At(3))) stepSent = true;
        };

        Assert.False(w.Wear.ReadyToEnter(At(3)));
        w.GameWears("phoenix feather");

        Assert.Equal(new[] { "wear phoenix feather" }, w.Sent);
        Assert.False(w.Held);
        Assert.True(stepSent);
        Assert.Equal("silver necklace", w.Wear.OwnedSnapshot().Single().Displaced);

        // And on the way out, with no set dressing the neck, the necklace goes back.
        foreach (int room in new[] { 3, 4, 5, 6 }) w.Arrive(room);
        Assert.Equal("wear silver necklace", w.Sent.Last());
    }

    [Fact]
    public void ALoopsDeferredResume_SendsOneWear()
    {
        World w = Volcano();
        Assert.False(w.Wear.ReadyToEnter(At(3)));
        w.GameWears("phoenix feather");

        Assert.True(w.Wear.ReadyToEnter(At(3)));
        Assert.Single(w.Sent);
    }

    [Fact]
    public void AnEnginesStepIntoTheRoom_WaitsForAWearSentOnArrival_AStepThatPassesItBy_DoesNot()
    {
        World w = Volcano();
        w.Arrive(2);                                   // the wear goes out, unanswered

        Assert.True(w.Wear.ReadyToEnter(At(1)));       // walking on past the lava
        Assert.False(w.Held);

        Assert.False(w.Wear.ReadyToEnter(At(3)));      // into it
        Assert.True(w.Held);
        Assert.Single(w.Sent);

        w.GameWears("phoenix feather");
        Assert.False(w.Held);
        Assert.True(w.Wear.ReadyToEnter(At(3)));
    }

    [Fact]
    public void AStepIntoAnOrdinaryRoom_IsNotHeld()
    {
        World w = Volcano();

        Assert.True(w.Wear.ReadyToEnter(At(12)));
        Assert.True(w.Wear.ReadyToEnter(null));
        Assert.Empty(w.Sent);
    }

    [Fact]
    public void ATypedStep_GetsTheWearAheadOfIt_WithNoHold()
    {
        World w = Volcano();

        w.Wear.BeforeTypedStep(At(3));

        Assert.Equal(new[] { "wear phoenix feather" }, w.Sent);
        Assert.False(w.Held);
    }

    // ----- which item ----------------------------------------------------------

    [Theory]
    [InlineData(true)]    // a necklace in the slot
    [InlineData(false)]   // the slot free
    public void TheOwnersOrder_TheFeatherBeforeTheAmulet(bool neckTaken)
    {
        World w = Volcano();
        if (!neckTaken) w.Worn.Clear();

        w.Arrive(2);

        Assert.Equal(new[] { "wear phoenix feather" }, w.Sent);
    }

    [Fact]
    public void WithNoFeather_TheAmuletGoesOn()
    {
        World w = Volcano();
        w.Pack.Remove("phoenix feather");

        w.Arrive(2);

        Assert.Equal(new[] { "wear magma amulet" }, w.Sent);
    }

    [Fact]
    public void AnItemThisCharacterCannotWear_IsNoCandidate_AndIsSaidOnce()
    {
        World w = Volcano();
        w.Unwearable.Add(Feather);

        w.Arrive(2);
        Assert.Equal(new[] { "wear magma amulet" }, w.Sent);
        Assert.False(w.Wear.WillWear(Feather));
        Assert.True(w.Wear.WillWear(Amulet));

        w.Answer();
        w.Arrive(3);
        w.Arrive(4);
        Assert.Single(w.Info, l => l.Contains("'phoenix feather' negates magma heat (#526) but this character can't wear it"));
    }

    [Fact]
    public void APieceThatFillsAFreePlace_ComesBeforeOneThatPushesSomethingOut_WhateverTheArmour()
    {
        // Freezing cold: a ring for an empty finger (+10), or a tunic that would
        // push a leather jerkin out of the torso slot (+265).
        World w = new();
        w.Worn.Add(new EquippedItem("leather jerkin", "Torso"));
        w.Pack.AddRange(new[] { "winterhide tunic", "sapphire ring" });
        w.Rooms[At(1)] = new Room { Key = At(1), Name = "Tundra", Spell = FreezingCold, Exits = new Dictionary<Direction, RoomExit>() };

        w.Arrive(1);

        Assert.Equal(new[] { "eq sapphire ring" }, w.Sent);
    }

    [Fact]
    public void WithNoFreePlaceEitherWay_ThePieceThatCostsTheLeastArmourGoesOn()
    {
        World w = new();
        w.Worn.AddRange(new[]
        {
            new EquippedItem("leather jerkin", "Torso"),
            new EquippedItem("ruby ring", "Finger"), new EquippedItem("emerald ring", "Finger"),
        });
        w.Pack.AddRange(new[] { "winterhide tunic", "sapphire ring" });
        w.Rooms[At(1)] = new Room { Key = At(1), Name = "Tundra", Spell = FreezingCold, Exits = new Dictionary<Direction, RoomExit>() };

        w.Arrive(1);

        Assert.Equal(new[] { "wear winterhide tunic" }, w.Sent);   // +265, against a ring swap that loses armour
    }

    [Fact]
    public void WithACounterAlreadyOn_NothingIsSent()
    {
        World w = Volcano();
        w.Pack.Remove("magma amulet");
        w.Worn.Clear();
        w.Worn.Add(new EquippedItem("magma amulet", "Neck"));   // on by the user's own hand: not swapped for the feather

        Assert.True(w.Wear.ReadyToEnter(At(3)));
        w.Arrive(3);

        Assert.Empty(w.Sent);
        Assert.Empty(w.Wear.OwnedSnapshot());
    }

    [Fact]
    public void ARoomWhoseSpellHasNoNegatingItemOnRecord_IsNotDressedFor()
    {
        // What is covered is what the hazard index gives a negating item for. The
        // class-filter rooms the crystal ward is for have no hazard there (they do
        // no damage), so the ward in the pack is never put on.
        World w = new();
        w.Pack.Add("crystal ward");
        w.Line(4);
        w.Rooms[At(3)] = new Room { Key = At(3), Name = "Guild Hall", Spell = 1325, Exits = w.Rooms[At(3)].Exits };

        w.Arrive(2);
        Assert.True(w.Wear.ReadyToEnter(At(3)));
        w.Arrive(3);

        Assert.Empty(w.Sent);
    }

    // ----- a wear that comes to nothing ------------------------------------------

    [Fact]
    public void ARefusedWear_LetsTheStepGo_ClosesTheRoomToRoutes_AndIsTriedAgainAfterALevelUp()
    {
        World w = Volcano();
        w.Pack.Remove("magma amulet");
        Assert.False(w.Wear.ReadyToEnter(At(3)));

        w.Gear.NoteWearRefused();                      // `You may not wear that item!`

        Assert.False(w.Held);
        Assert.True(w.Wear.ReadyToEnter(At(3)));
        w.Arrive(3);
        w.Arrive(4);
        Assert.Equal(new[] { "wear phoenix feather" }, w.Sent);   // the one refused wear
        Assert.Empty(w.Wear.OwnedSnapshot());
        Assert.False(w.Wear.WillWear(Feather));                   // and no route is planned on it
        Assert.Single(w.Info, l => l.Contains("the game refused 'phoenix feather'"));
        Assert.Contains(w.Wear.OutOfUseSnapshot(), o => o.Item == Feather && o.Why.Contains("Refused"));

        w.Wear.LiftRefusals("the level changed");
        Assert.True(w.Wear.WillWear(Feather));
        Assert.Equal(new[] { "wear phoenix feather", "wear phoenix feather" }, w.Sent);   // standing in the lava: at once
    }

    [Fact]
    public void ARefusedWear_IsNotSentAgainByTheAlignmentReadItAsksFor_OnlyByAChangeOfAlignment()
    {
        // Every refusal has the client read the alignment (`pro`). Lifting the
        // refusal on that read sent the wear again, to be refused again, for as
        // long as the character stood in the lava.
        World w = Volcano();
        w.Pack.Remove("magma amulet");
        w.Wear.NoteAlignmentRead("Neutral");           // known from before
        w.Arrive(3);
        for (int cycle = 0; cycle < 4; cycle++)
        {
            w.Gear.NoteWearRefused();                  // `You may not wear that item!`
            w.Wear.NoteAlignmentRead("Neutral");       // the read the refusal asked for
        }

        Assert.Equal(new[] { "wear phoenix feather" }, w.Sent);
        Assert.False(w.Wear.WillWear(Feather));

        w.Wear.NoteAlignmentRead("Good");              // it moved
        Assert.Equal(new[] { "wear phoenix feather", "wear phoenix feather" }, w.Sent);
    }

    [Fact]
    public void TheFirstAlignmentReading_IsNoChange()
    {
        World w = Volcano();
        w.Pack.Remove("magma amulet");
        w.Arrive(3);
        w.Gear.NoteWearRefused();

        w.Wear.NoteAlignmentRead("Neutral");           // never read before the refusal

        Assert.Single(w.Sent);
    }

    [Fact]
    public void ARefusalOfAnotherWearSentFirst_IsNotTakenForOurs()
    {
        // A gear set's piece went out ahead of the counter's; the refusal that
        // names no item answers the older of the two.
        World w = Volcano();
        w.Pack.Add("platemail");
        EquipmentSet set = new()
        {
            Trigger = EquipTriggerType.PreRestHp, Enabled = true, Name = "Rest",
            Slots = { new EquipmentSlotEntry(EquipmentSlot.Torso, "platemail") },
        };
        w.Sets.Sets.Add(set);
        w.Gear.ApplyBySetId(set.Id);
        Assert.False(w.Wear.ReadyToEnter(At(3)));
        Assert.Equal(new[] { "wear platemail", "wear phoenix feather" }, w.Sent);

        w.Gear.NoteWearRefused();                      // the platemail's

        Assert.True(w.Held);                           // ours is still waiting
        Assert.True(w.Wear.WillWear(Feather));
        w.GameWears("phoenix feather");
        Assert.False(w.Held);
        Assert.Single(w.Wear.OwnedSnapshot());         // worn and owned: given back later
    }

    [Fact]
    public void ARefusalWithNothingOfOursWaiting_IsNotOurs()
    {
        World w = Volcano();

        w.Gear.NoteWearRefused();

        Assert.True(w.Wear.WillWear(Feather));
    }

    [Fact]
    public void NoRoomToWearIt_IsOfTheMoment_AndItIsTriedAgainWhenAPieceComesOff()
    {
        World w = Volcano();
        w.Pack.Remove("magma amulet");
        Assert.False(w.Wear.ReadyToEnter(At(3)));

        w.Gear.NoteCannotBeWorn(null);                 // `You have no more room to wear that item!`

        Assert.False(w.Held);
        Assert.False(w.Wear.WillWear(Feather));
        Assert.Empty(w.Wear.OwnedSnapshot());

        w.Wear.NoteRemoved("leather jerkin");
        Assert.True(w.Wear.WillWear(Feather));
    }

    [Theory]
    [InlineData(false)]   // `You do not have phoenix feather left unequipped.`
    [InlineData(true)]    // `You are already wearing silver necklace and it may not be removed.`
    public void AnAnswerThatPutNothingOn_FreesTheStepAtOnce_AndTheItemWaitsForAFullInventoryRead(bool occupantStuck)
    {
        World w = Volcano();
        w.Pack.Remove("magma amulet");
        Assert.False(w.Wear.ReadyToEnter(At(3)));

        if (occupantStuck) w.Gear.NoteOccupantNotRemovable("silver necklace");
        else w.Gear.NoteNotLeftUnequipped("phoenix feather");

        Assert.False(w.Held);
        Assert.Empty(w.Timers.Skip(1));                // freed by the answer, not by the cap
        Assert.True(w.Wear.ReadyToEnter(At(3)));
        Assert.False(w.Wear.WillWear(Feather));
        w.Arrive(3);
        w.Arrive(4);
        Assert.Single(w.Sent);                         // not tried again room after room

        w.Wear.NoteInventoryRead();
        Assert.True(w.Wear.WillWear(Feather));
        Assert.Equal(2, w.Sent.Count);                 // and tried again, standing in the lava
    }

    [Fact]
    public void AWearTheGameNeverAnswers_HoldsTheStepNoLongerThanTheCap_AndTheNextCandidateGoesOutWithNoHold()
    {
        World w = Volcano();
        Assert.False(w.Wear.ReadyToEnter(At(3)));
        Assert.True(w.Held);

        w.Timers.Single()();   // the cap runs out

        Assert.False(w.Held);
        Assert.False(w.Wear.WillWear(Feather));
        Assert.Single(w.Info, l => l.Contains("no answer to the wear of 'phoenix feather' in 3s"));

        // The same step asks again: the amulet is tried, ahead of the step, and
        // the step is not held a second time.
        Assert.True(w.Wear.ReadyToEnter(At(3)));
        Assert.Equal(new[] { "wear phoenix feather", "wear magma amulet" }, w.Sent);
        Assert.False(w.Held);
    }

    [Fact]
    public void TheFirstCandidateNeverAnswers_TheSecondGoesOn_AndIsStillOwnedWhenTheFirstIsGivenUpOn()
    {
        // The feather's clean-up lets go of the feather's claim, not of the amulet's.
        World w = Volcano();
        Assert.False(w.Wear.ReadyToEnter(At(3)));      // wear phoenix feather, held
        w.Timers[0]();                                 // 3 s: no answer
        Assert.True(w.Wear.ReadyToEnter(At(3)));       // the amulet goes out, no hold
        w.Pack.Remove("phoenix feather");              // (the feather really isn't there)
        w.GameWears("magma amulet");                   // pushes the necklace out
        foreach (Action t in w.Timers.ToList()) t();   // the time a late answer is listened for ends

        Assert.Equal(("magma amulet", "silver necklace", MagmaHeat), w.Wear.OwnedSnapshot().Single());

        foreach (int room in new[] { 3, 4, 5, 6 }) w.Arrive(room);
        Assert.Equal(new[] { "wear phoenix feather", "wear magma amulet", "wear silver necklace" }, w.Sent);
        Assert.Empty(w.Wear.OwnedSnapshot());
    }

    [Fact]
    public void TheFirstCandidateAnswersLate_TheSecondThenPushesItOut_AndTakesOverWhatItHadPushedOut()
    {
        // Lag over the cap: both wears land, the feather first. The feather coming
        // off again is our own second wear's doing, not other hands'.
        World w = Volcano();
        Assert.False(w.Wear.ReadyToEnter(At(3)));
        w.Timers[0]();
        Assert.True(w.Wear.ReadyToEnter(At(3)));       // wear magma amulet sent
        w.GameWears("phoenix feather");                // late: necklace out, feather on
        w.GameWears("magma amulet");                   // then the amulet pushes the feather out
        foreach (Action t in w.Timers.ToList()) t();

        Assert.Equal(("magma amulet", "silver necklace", MagmaHeat), w.Wear.OwnedSnapshot().Single());
        Assert.DoesNotContain(w.Info, l => l.Contains("was taken off"));

        foreach (int room in new[] { 3, 4, 5, 6 }) w.Arrive(room);
        Assert.Equal(new[] { "wear phoenix feather", "wear magma amulet", "wear silver necklace" }, w.Sent);
        Assert.Empty(w.Wear.OwnedSnapshot());
    }

    [Fact]
    public void AnUnansweredItem_IsNotTriedAgainRoomAfterRoom_OnlyAfterAFullInventoryRead()
    {
        World w = Volcano();
        w.Pack.Remove("magma amulet");
        Assert.False(w.Wear.ReadyToEnter(At(3)));
        w.Timers[0]();         // the cap
        w.Timers[1]();         // and the time a late answer is listened for

        w.Arrive(3);
        Assert.True(w.Wear.ReadyToEnter(At(4)));
        w.Arrive(4);
        Assert.Single(w.Sent);
        Assert.Contains(w.Wear.OutOfUseSnapshot(), o => o.Item == Feather && o.Why.Contains("no answer"));

        w.Wear.NoteInventoryRead();
        Assert.Equal(2, w.Sent.Count);
    }

    [Fact]
    public void AnAnswerThatComesAfterTheCap_StillLeavesTheItemOwned_AndThePieceItPushedOutGoesBack()
    {
        World w = Volcano();
        w.Pack.Remove("magma amulet");
        Assert.False(w.Wear.ReadyToEnter(At(3)));
        w.Timers[0]();

        w.GameWears("phoenix feather");                // lag: the lines arrive now

        Assert.Equal(("phoenix feather", "silver necklace", MagmaHeat), w.Wear.OwnedSnapshot().Single());
        Assert.True(w.Wear.WillWear(Feather));
        w.Timers[1]();                                 // the late window closing changes nothing
        Assert.Single(w.Wear.OwnedSnapshot());

        w.Arrive(6);
        Assert.Equal(new[] { "wear phoenix feather", "wear silver necklace" }, w.Sent);
    }

    [Fact]
    public void AWearWhoseAnswerWasLost_IsOwnedAgainOnceTheInventoryShowsItOn()
    {
        World w = Volcano();
        w.Pack.Remove("magma amulet");
        Assert.False(w.Wear.ReadyToEnter(At(3)));
        w.Wear.NoteRemoved("silver necklace");         // this line came through; the wear line didn't
        w.Timers[0]();
        w.Timers[1]();
        Assert.Empty(w.Wear.OwnedSnapshot());

        w.Worn.Clear();
        w.Worn.Add(new EquippedItem("phoenix feather", "Neck"));
        w.Pack.Remove("phoenix feather");
        w.Pack.Add("silver necklace");
        w.Arrive(3);
        w.Wear.NoteInventoryRead();

        Assert.Equal(("phoenix feather", "silver necklace", MagmaHeat), w.Wear.OwnedSnapshot().Single());
        Assert.Single(w.Sent);
    }

    [Fact]
    public void AnAnswerInTime_LeavesTheCapNothingToDo()
    {
        World w = Volcano();
        Assert.False(w.Wear.ReadyToEnter(At(3)));
        w.GameWears("phoenix feather");

        w.Timers.Single()();

        Assert.DoesNotContain(w.Info, l => l.Contains("no answer"));
        Assert.Single(w.Wear.OwnedSnapshot());
    }

    [Fact]
    public void AWearTheSendGateWouldDrop_IsNotClaimedSentOrWaitedFor_AndGoesOutWhenTheGateOpens()
    {
        World w = Volcano();
        w.SendGateLocked = true;                       // a password prompt, the trainer form

        w.Arrive(2);
        Assert.True(w.Wear.ReadyToEnter(At(3)));
        w.Wear.Recheck();

        Assert.Empty(w.Sent);
        Assert.False(w.Held);
        Assert.Equal(0, w.GearNotes);                  // combat isn't told of a command that never left
        Assert.Empty(w.Wear.OwnedSnapshot());
        Assert.Null(w.Wear.PendingItem);
        Assert.True(w.Wear.WillWear(Feather));
        Assert.Single(w.Info, l => l.Contains("commands are being held back just now"));

        w.SendGateLocked = false;
        w.Wear.Recheck();                              // the gate's release
        Assert.Equal(new[] { "wear phoenix feather" }, w.Sent);
    }

    // ----- a slot a Location rule holds --------------------------------------------

    [Fact]
    public void ALocationRuleHoldingTheSlotWithAnotherPiece_GivesWayToTheCounter_AndHasItsPieceBackAfterwards()
    {
        World w = Volcano();
        w.Worn.Clear();
        w.Worn.Add(new EquippedItem("holy symbol", "Neck"));
        Assert.True(w.Gear.SetSlotOverride("holy symbol"));   // the rule: already worn, slot held

        Assert.False(w.Wear.ReadyToEnter(At(3)));
        Assert.Equal(new[] { "wear phoenix feather" }, w.Sent);   // sent, so there is something to wait for
        Assert.True(w.Held);
        Assert.Equal(1, w.GearNotes);

        w.GameWears("phoenix feather");
        Assert.False(w.Held);
        Assert.Equal("holy symbol", w.Wear.OwnedSnapshot().Single().Displaced);

        // The rule fires again in every room of its area: it waits its turn.
        Assert.True(w.Gear.SetSlotOverride("holy symbol"));
        w.Arrive(3);
        w.Arrive(4);
        Assert.Single(w.Sent);

        w.Arrive(6);
        Assert.Equal(new[] { "wear phoenix feather", "wear holy symbol" }, w.Sent);
    }

    [Fact]
    public void ASlotALocationRuleHoldsWithTheSamePiece_IsWornOnce_AndRevertedOnlyWhenBothLetGo()
    {
        // A Location rule that wears the feather across the volcano, and the counter
        // wear for the lava inside it: one slot, two reasons to hold it.
        World w = Volcano();
        Assert.False(w.Wear.ReadyToEnter(At(3)));                 // the counter's wear, before the step
        Assert.True(w.Gear.SetSlotOverride("phoenix feather"));   // the rule's claim, on arrival
        Assert.Single(w.Sent);                                    // not sent a second time
        w.GameWears("phoenix feather");

        w.Arrive(10);                                             // clear of the lava, still inside the rule's area
        Assert.Single(w.Sent);
        Assert.Empty(w.Wear.OwnedSnapshot());

        w.Gear.ClearSlotOverride("phoenix feather");              // the rule's area ends
        Assert.Equal(new[] { "wear phoenix feather", "rem phoenix feather" }, w.Sent);
    }

    [Fact]
    public void TheRulesOwnWearOfTheSamePiece_IsWaitedFor_NotSentAgain()
    {
        World w = Volcano();
        Assert.True(w.Gear.SetSlotOverride("phoenix feather"));   // the rule got there first
        Assert.Single(w.Sent);

        Assert.False(w.Wear.ReadyToEnter(At(3)));
        Assert.Single(w.Sent);
        Assert.True(w.Held);
        Assert.Equal(0, w.GearNotes);                             // the counter sent nothing itself

        w.GameWears("phoenix feather");
        Assert.False(w.Held);
    }

    // ----- sneaking ------------------------------------------------------------

    [Fact]
    public void AutoSneakOnAutoCombatOff_ItGoesOnInAnEmptyRoomAheadOfTheHazard_AndComesOffInAnEmptyRoomAfterIt()
    {
        World w = Volcano(6, 7);
        w.Plan.AddRange(Enumerable.Range(1, 12));
        w.SneakingPast = true;
        w.SneakKept = true;

        w.Arrive(1);                                   // rooms 2 to 4 ahead: no lava next to any
        Assert.Empty(w.Sent);

        w.RoomEmpty = false;
        w.Arrive(2);                                   // room 5 is ahead and next to the lava, but an NPC is here
        Assert.Empty(w.Sent);
        Assert.True(w.Wear.WaitingForEmptyRoom);

        w.RoomEmpty = true;
        w.Arrive(3);                                   // nobody here: on, three rooms short of the lava
        Assert.Equal(new[] { "wear phoenix feather" }, w.Sent);
        Assert.Single(w.Info, l => l.Contains("in an empty room, ahead of 16/6"));
        w.Answer();

        foreach (int room in new[] { 4, 5, 6, 7, 8 }) w.Arrive(room);
        Assert.Single(w.Sent);

        w.RoomEmpty = false;
        w.Arrive(9);                                   // clear of the lava, with an NPC to sneak past
        Assert.Single(w.Sent);
        Assert.Single(w.Wear.OwnedSnapshot());         // still owned until the restore goes out
        Assert.True(w.Wear.WaitingForEmptyRoom);
        Assert.Single(w.Info, l => l.Contains("its slot is given back in the next room with none"));

        w.RoomEmpty = true;                            // the NPC wanders off:
        w.SneakKept = false;                           // nobody to sneak past, so the guard keeps nothing
        w.Wear.Poll();
        Assert.Equal(new[] { "wear phoenix feather", "wear silver necklace" }, w.Sent);
        Assert.Empty(w.Wear.OwnedSnapshot());
        Assert.False(w.Wear.WaitingForEmptyRoom);
    }

    [Fact]
    public void AutoSneakOnAutoCombatOff_WithNoEmptyRoomOnTheWay_ItGoesOnNextToTheHazardAnyway()
    {
        World w = Volcano(6, 7);
        w.Plan.AddRange(Enumerable.Range(1, 12));
        w.SneakingPast = true;
        w.SneakKept = true;
        w.RoomEmpty = false;

        foreach (int room in new[] { 1, 2, 3, 4 }) w.Arrive(room);
        Assert.Empty(w.Sent);

        w.Arrive(5);
        Assert.Equal(new[] { "wear phoenix feather" }, w.Sent);
    }

    [Fact]
    public void AutoSneakOnAutoCombatOff_ASlotWaitingToBeGivenBack_StaysHeldWhenTheItemIsNeededAgain()
    {
        World w = Volcano();
        w.SneakingPast = true;
        w.Arrive(2);
        w.Answer();
        foreach (int room in new[] { 3, 4, 5 }) w.Arrive(room);

        w.RoomEmpty = false;
        w.Arrive(6);                                   // not needed here, and somebody is here
        Assert.Single(w.Sent);

        w.Arrive(5);                                   // back beside the lava
        w.RoomEmpty = true;
        w.Wear.Poll();

        Assert.Single(w.Sent);
        Assert.Single(w.Wear.OwnedSnapshot());
    }

    [Fact]
    public void AutoSneakOnAutoCombatOn_ThereIsNoLookAhead_TheWearGoesThroughAKeptSneak_TheRestoreWaitsForIt()
    {
        World w = Volcano(6, 7);
        w.Plan.AddRange(Enumerable.Range(1, 12));
        w.SneakKept = true;                            // the guard is keeping a sneak for the backstab

        foreach (int room in new[] { 1, 2, 3, 4 }) w.Arrive(room);
        Assert.Empty(w.Sent);                          // empty rooms, lava ahead on the plan: not this case

        w.Arrive(5);
        Assert.Equal(new[] { "wear phoenix feather" }, w.Sent);   // the room's damage is at stake
        w.Answer();

        w.RoomEmpty = false;
        foreach (int room in new[] { 6, 7, 8, 9 }) w.Arrive(room);
        // Clear of the lava with a monster to open on: the restore isn't worth the
        // backstab, and the item stays owned until it has gone out.
        Assert.Single(w.Sent);
        Assert.Single(w.Wear.OwnedSnapshot());
        Assert.Equal(1, w.GearNotes);
        Assert.Single(w.Info, l => l.Contains("a sneak is being kept"));

        w.SneakKept = false;
        w.Gear.RunHeldGear();                          // the guard lets go: nothing was queued blind
        Assert.Single(w.Sent);
        w.Wear.Recheck();                              // and the counter decides again
        Assert.Equal(new[] { "wear phoenix feather", "wear silver necklace" }, w.Sent);
        Assert.Empty(w.Wear.OwnedSnapshot());
    }

    [Fact]
    public void ARestoreHeldForAKeptSneak_IsDropped_WhenTheItemIsNeededAgainByTheTimeTheSneakIsLetGo()
    {
        World w = Volcano();
        w.Arrive(2);
        w.Answer();
        foreach (int room in new[] { 3, 4, 5 }) w.Arrive(room);
        w.SneakKept = true;                            // a backstab owed in the room being entered
        w.Arrive(6);
        Assert.Single(w.Sent);

        w.Arrive(5);                                   // back beside the lava, still sneaking
        w.SneakKept = false;
        w.Gear.RunHeldGear();
        w.Wear.Recheck();

        Assert.Single(w.Sent);
        Assert.Single(w.Wear.OwnedSnapshot());
    }

    // ----- one step of the plan keeps it on --------------------------------------

    // Thirteen rooms in a line, with a lava room to the south of every `every`th
    // room from the second on.
    private static World Corridor(int every)
    {
        World w = new();
        w.Worn.Add(new EquippedItem("silver necklace", "Neck"));
        w.Pack.Add("phoenix feather");
        w.Line(13);
        for (int n = 2; n <= 12; n += every)
        {
            RoomKey lava = new(16, 100 + n);
            var exits = new Dictionary<Direction, RoomExit>(w.Rooms[At(n)].Exits)
            {
                [Direction.S] = new RoomExit(lava, RoomExitHint.None, RawHint: null),
            };
            w.Rooms[At(n)] = new Room { Key = At(n), Name = "Ledge", Spell = 0, Exits = exits };
            w.Rooms[lava] = new Room
            {
                Key = lava, Name = "Lava Tube", Spell = MagmaHeat,
                Exits = new Dictionary<Direction, RoomExit> { [Direction.N] = new RoomExit(At(n), RoomExitHint.None, RawHint: null) },
            };
        }
        return w;
    }

    private static void Walk(World w, IEnumerable<int> rooms)
    {
        foreach (int room in rooms)
        {
            w.Arrive(room);
            w.Answer();
        }
    }

    [Fact]
    public void APlannedWalk_PastAHazardOffEverySecondRoom_WearsOnceAndRestoresOnce()
    {
        World w = Corridor(every: 2);
        w.Plan.AddRange(Enumerable.Range(1, 13));

        Walk(w, Enumerable.Range(1, 12));
        Assert.Equal(new[] { "wear phoenix feather" }, w.Sent);   // on at room 2, kept through the odd rooms

        w.Arrive(3);                                   // (looked at part-way: the bug report can say why it is on)
        Assert.Equal(new[] { "phoenix feather" }, w.Wear.KeptForNextStep);
        w.Arrive(12);
        Assert.Empty(w.Wear.KeptForNextStep);

        Walk(w, new[] { 13 });                         // past the last one, nothing ahead
        Assert.Equal(new[] { "wear phoenix feather", "wear silver necklace" }, w.Sent);
    }

    [Fact]
    public void ALoop_PastAHazardOffEverySecondRoom_WearsOnce_AndRestoresWhenItStops()
    {
        World w = Corridor(every: 2);
        List<int> lap = Enumerable.Range(1, 13).Concat(Enumerable.Range(2, 11).Reverse()).ToList();   // 1..13, 12..2
        for (int laps = 0; laps < 3; laps++) w.Plan.AddRange(lap);
        w.Plan.Add(1);

        Walk(w, w.Plan.Take(w.Plan.Count - 1).ToList());
        // Room 13 and room 1 are neither in nor next to the lava, and the lap's next
        // step from each is: nothing is swapped at either end, lap after lap.
        Assert.Equal(new[] { "wear phoenix feather" }, w.Sent);

        Walk(w, new[] { 1 });                          // the last step of the plan: nothing ahead
        Assert.Equal(new[] { "wear phoenix feather", "wear silver necklace" }, w.Sent);
    }

    [Fact]
    public void APlannedWalk_PastAHazardOffEveryThirdRoom_StillSwaps()
    {
        // The look is one step deep: from room 3 the next step is room 4, which
        // is not next to the lava off room 5.
        World w = Corridor(every: 3);
        w.Plan.AddRange(Enumerable.Range(1, 13));

        Walk(w, Enumerable.Range(1, 13));

        Assert.Equal(8, w.Sent.Count);                 // on and off at each of rooms 2, 5, 8 and 11
        Assert.Equal(4, w.Sent.Count(c => c == "wear phoenix feather"));
        Assert.Equal(4, w.Sent.Count(c => c == "wear silver necklace"));
    }

    [Fact]
    public void ATypedWalk_AlongTheSameCorridor_Swaps_ThereBeingNoPlan()
    {
        World w = Corridor(every: 2);

        Walk(w, Enumerable.Range(1, 13));

        Assert.Equal(12, w.Sent.Count);                // on at every even room, off at every odd one
    }

    [Fact]
    public void ThePlanDroppedMidCorridor_ItComesOffByThePlainRule()
    {
        World w = Corridor(every: 2);
        w.Plan.AddRange(Enumerable.Range(1, 13));
        Walk(w, new[] { 1, 2, 3 });
        Assert.Single(w.Sent);                         // kept at room 3 for the step to room 4

        w.Plan.Clear();                                // the walk is stopped there
        w.Wear.Recheck();

        Assert.Equal(new[] { "wear phoenix feather", "wear silver necklace" }, w.Sent);
    }

    [Fact]
    public void TheNextStep_KeepsACounterOn_AndPutsNoneOn()
    {
        World w = Corridor(every: 2);
        w.Plan.AddRange(Enumerable.Range(1, 13));

        w.Arrive(1);                                   // the next step, room 2, is next to the lava

        Assert.Empty(w.Sent);
    }

    // ----- giving the slot back ------------------------------------------------

    private static World WearingTheFeatherIn(int room)
    {
        World w = Volcano();
        w.Arrive(room);
        w.Answer();
        Assert.Single(w.Wear.OwnedSnapshot());
        return w;
    }

    [Fact]
    public void TheSetInForceOnTheWayOut_IsWhatGoesBackOn()
    {
        // A gear set is applied while the feather is on: its neck piece is left
        // alone then, and is what the slot returns to.
        World w = WearingTheFeatherIn(3);
        w.Pack.Add("holy symbol");
        EquipmentSet resting = new()
        {
            Trigger = EquipTriggerType.PreRestHp, Enabled = true, Name = "Rest",
            Slots = { new EquipmentSlotEntry(EquipmentSlot.Neck, "holy symbol") },
        };
        w.Sets.Sets.Add(resting);
        w.Gear.ApplyBySetId(resting.Id);
        Assert.Equal(new[] { "wear phoenix feather" }, w.Sent);   // the set did not take the feather off

        w.Arrive(10);

        Assert.Equal(new[] { "wear phoenix feather", "wear holy symbol" }, w.Sent);
    }

    [Fact]
    public void WithAnEmptySlotBefore_TheCounterSimplyComesOff()
    {
        World w = Volcano();
        w.Worn.Clear();
        w.Arrive(2);
        w.Answer();
        Assert.Null(w.Wear.OwnedSnapshot().Single().Displaced);

        w.Arrive(1);

        Assert.Equal(new[] { "wear phoenix feather", "rem phoenix feather" }, w.Sent);
    }

    [Fact]
    public void TakenOffByHandMeanwhile_NothingIsSentForIt()
    {
        World w = WearingTheFeatherIn(3);
        w.Worn.Clear();
        w.Worn.Add(new EquippedItem("silver necklace", "Neck"));   // the user put the necklace back
        w.Wear.NoteRemoved("phoenix feather");

        w.Arrive(10);

        Assert.Single(w.Sent);
        Assert.Empty(w.Wear.OwnedSnapshot());
    }

    [Fact]
    public void TakenOffInARoomThatNeedsIt_ItIsNotPutStraightBackOn_ButIsInTheNextRoom()
    {
        // The user's own `rem`, or another swap that borrows the slot for a moment:
        // the two must not trade the slot back and forth where the character stands.
        World w = WearingTheFeatherIn(3);

        w.GameRemoves("phoenix feather");              // ends with the inventory's change notice

        Assert.Single(w.Sent);
        Assert.Empty(w.Wear.OwnedSnapshot());
        Assert.Single(w.Info, l => l.Contains("'phoenix feather' was taken off"));

        w.Arrive(4);                                   // still lava
        Assert.Equal(new[] { "wear phoenix feather", "wear phoenix feather" }, w.Sent);
    }

    // ----- fingers and wrists: two places ----------------------------------------

    private static World Tundra(params string[] rings)
    {
        World w = new();
        w.Worn.AddRange(rings.Select(r => new EquippedItem(r, "Finger")));
        w.Pack.Add("sapphire ring");
        w.Line(12);
        w.Rooms[At(3)] = new Room { Key = At(3), Name = "Tundra", Spell = FreezingCold, Exits = w.Rooms[At(3)].Exits };
        return w;
    }

    [Fact]
    public void ARingIntoAFreeFinger_PushesNothingOut_AndComesOffAloneAfterwards()
    {
        World w = Tundra("ruby ring");

        w.Arrive(2);
        w.Answer();
        Assert.Equal(new[] { "eq sapphire ring" }, w.Sent);
        Assert.Null(w.Wear.OwnedSnapshot().Single().Displaced);

        w.Arrive(1);
        Assert.Equal(new[] { "eq sapphire ring", "rem sapphire ring" }, w.Sent);
    }

    [Theory]
    [InlineData("ruby ring")]      // Paradigm pushes out the first of the pair
    [InlineData("emerald ring")]   // Stock the second
    public void ARingIntoAFullPair_RemembersThePieceTheGameTookOff_AndPutsThatOneBack(string evicted)
    {
        World w = Tundra("ruby ring", "emerald ring");

        w.Arrive(2);
        w.GameWears("sapphire ring", evicts: evicted);
        Assert.Equal(evicted, w.Wear.OwnedSnapshot().Single().Displaced);

        w.Arrive(1);
        // Off first, then the other into the place it left: a bare `eq` into the
        // full pair would push out whichever the realm picks.
        Assert.Equal(new[] { "eq sapphire ring", "rem sapphire ring", $"eq {evicted}" }, w.Sent);
    }

    [Fact]
    public void AGearSetsRingForTheOtherFinger_StillGoesOn_AndTheCountersRingIsNotTakenOff()
    {
        World w = Tundra();
        w.Pack.AddRange(new[] { "ruby ring", "emerald ring" });
        w.Arrive(2);
        w.Answer();                                    // the sapphire ring is on, one finger free
        EquipmentSet set = new()
        {
            Trigger = EquipTriggerType.PreRestHp, Enabled = true, Name = "Rest",
            Slots =
            {
                new EquipmentSlotEntry(EquipmentSlot.Finger1, "ruby ring"),
                new EquipmentSlotEntry(EquipmentSlot.Finger2, "emerald ring"),
            },
        };
        w.Sets.Sets.Add(set);

        w.Gear.ApplyBySetId(set.Id);

        // One place is free: one of the set's two goes on, the other waits.
        Assert.Equal(new[] { "eq sapphire ring", "eq ruby ring" }, w.Sent);
    }

    [Fact]
    public void AGearSetsRing_TakesThePlaceOfAnOddPiece_NotOfTheCounter()
    {
        World w = Tundra("copper ring");
        w.Pack.Add("ruby ring");
        w.Arrive(2);
        w.Answer();                                    // copper and sapphire on: the pair is full
        EquipmentSet set = new()
        {
            Trigger = EquipTriggerType.PreRestHp, Enabled = true, Name = "Rest",
            Slots = { new EquipmentSlotEntry(EquipmentSlot.Finger1, "ruby ring") },
        };
        w.Sets.Sets.Add(set);

        w.Gear.ApplyBySetId(set.Id);

        Assert.Equal(new[] { "eq sapphire ring", "rem copper ring", "eq ruby ring" }, w.Sent);
    }

    // ----- the setting and the master switch ------------------------------------

    [Fact]
    public void SwitchedOffInSettings_NothingIsPutOn_AndNoRouteCountsTheCarriedItem()
    {
        World w = Volcano();
        w.Enabled = false;

        Assert.True(w.Wear.ReadyToEnter(At(3)));
        w.Arrive(3);
        w.Wear.BeforeTypedStep(At(4));

        Assert.Empty(w.Sent);
        Assert.False(w.Wear.WillWear(Feather));
    }

    [Fact]
    public void SwitchedOffInSettingsWhileOneIsOn_ItIsStillGivenBack()
    {
        World w = WearingTheFeatherIn(3);
        w.Enabled = false;

        w.Arrive(10);

        Assert.Equal(new[] { "wear phoenix feather", "wear silver necklace" }, w.Sent);
    }

    [Fact]
    public void WithTheMasterSwitchOff_NothingIsPutOnOrGivenBack_UntilItIsOnAgain()
    {
        World w = WearingTheFeatherIn(3);
        w.MasterOff = true;

        w.Arrive(10);                      // walked out by hand: no gear is swapped
        Assert.Single(w.Sent);
        Assert.False(w.Wear.WillWear(Amulet));

        w.MasterOff = false;
        w.Wear.Recheck();                  // the switch-on settles the room stood in
        Assert.Equal(new[] { "wear phoenix feather", "wear silver necklace" }, w.Sent);
    }

    [Fact]
    public void ThePlannersQuestion_ReadsTheSwitchWithoutCountingASkip()
    {
        World w = Volcano();
        int counted = 0, plain = 0;
        w.Wear.MasterSwitchOff = () => { counted++; return true; };
        w.Wear.MasterSwitchIsOff = () => { plain++; return true; };

        for (int i = 0; i < 50; i++) Assert.False(w.Wear.WillWear(Feather));

        Assert.Equal(0, counted);
        Assert.Equal(50, plain);
    }

    [Fact]
    public void WhatARouteMayCountOn_IsAnnouncedWhenItChanges()
    {
        World w = Volcano();
        w.Pack.Remove("magma amulet");
        Assert.False(w.Wear.ReadyToEnter(At(3)));
        int before = w.UsabilityChanges;

        w.Gear.NoteWearRefused();
        Assert.True(w.UsabilityChanges > before);

        before = w.UsabilityChanges;
        w.Wear.LiftRefusals("the stat screen was read");
        Assert.True(w.UsabilityChanges > before);
    }

    [Fact]
    public void AnotherCharacterLoaded_NothingOfTheOldOnesIsOwnedOrHeld()
    {
        World w = Volcano();
        Assert.False(w.Wear.ReadyToEnter(At(3)));
        Assert.True(w.Held);

        w.Wear.Reset();

        Assert.False(w.Held);
        Assert.Empty(w.Wear.OwnedSnapshot());
        Assert.Null(w.Wear.PendingItem);
    }
}
