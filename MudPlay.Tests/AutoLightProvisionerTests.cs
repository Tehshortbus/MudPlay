using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MudPlay.Game.Inventory;
using MudPlay.Game.Light;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// AutoLightProvisioner — the engine behind auto-light. A planned route only
// PROVISIONS the pack (a buy / reorder hand-off); the route-scan's own Ready verdict
// is never acted on. A room is lit predictively one room ahead (OnApproachingRoom,
// off the target room's mapped light) or reactively on the server's live can't-see
// line (dispatched through the MessageRouter, the fallback for an unmapped room), and
// the light is `rem`d on stepping into a room seeable without it. Exercises the
// master-toggle gate, the buy / reorder hand-offs, the room-light spell (a buff goes
// first and stacks; a light-making spell is one more light source; a spell found not
// enough is latched), spell-only mode, the floor-light and stock-up advisors, and
// the pending latch that keeps a still-unconfirmed `use` from being re-sent.
public sealed class AutoLightProvisionerTests
{
    // torch: 100 illu, 40-min burn; lantern: 175 illu, 2 h burn; light ball (what
    // Stock's illuminate makes): 100 illu.
    private static readonly LightItem Torch = new(1, "torch", Strength: 100, UseCount: 800);
    private static readonly LightItem Lantern = new(2, "lantern", Strength: 175, UseCount: 2400);
    private static readonly LightItem Ball = new(3, "light ball", Strength: 100, UseCount: 800);
    private static readonly IReadOnlyList<LightItem> Catalogue = new[] { Torch, Lantern, Ball };

    // The two room-light spell shapes: starlight (a RoomIllu buff) and Stock
    // illuminate (makes a light ball).
    private static readonly RoomLightSpell Starlight = new("star", "starlight", RoomLightSpellKind.Buff, 175, null);
    private static readonly RoomLightSpell Illuminate = new("illu", "illuminate", RoomLightSpellKind.MakesLightItem, 100, Ball);

    private static readonly RoomKey A = new(1, 100);
    // Target rooms for the predictive-lookahead tests, resolved via the harness graph.
    private static readonly RoomKey DarkAhead = new(1, 200);
    private static readonly RoomKey LitAhead = new(1, 201);
    private static readonly RoomKey DeepAhead = new(1, 202);
    private static readonly RoomKey Unmapped = new(1, 999);

    private static Room RoomAt(RoomKey key, int light) => new()
    {
        Key = key,
        Name = "r" + key,
        Light = light,
        Exits = new Dictionary<Direction, RoomExit>(),
    };

    private static Func<RoomKey, Room?> GraphOf(params Room[] rooms)
    {
        var map = new Dictionary<RoomKey, Room>();
        foreach (Room r in rooms) map[r.Key] = r;
        return k => map.TryGetValue(k, out Room? room) ? room : null;
    }

    private static InventorySnapshot Snap(
        IReadOnlyList<string>? carried = null, ReadiedLight? readied = null, long stamp = 1) =>
        InventorySnapshot.Empty with
        {
            CarriedItems = carried ?? Array.Empty<string>(),
            ReadiedLight = readied,
            LastUpdated = DateTimeOffset.FromUnixTimeSeconds(stamp),
        };

    private sealed class Harness
    {
        public bool Enabled = true;
        public int WornIllu;
        public RoomLightSpell? Spell;
        // Cast codes whose buff timer reads up; a cast the harness lets through
        // arms it, as CastingDirector.NoteManualBuffCast does in the client.
        public readonly HashSet<string> SpellsUp = new();
        public bool CastAllowed = true;
        public readonly List<string> CastSpells = new();
        public InventorySnapshot Snapshot = Snap();
        public AutoLightSettings Settings = new() { CarryHours = 12, ReorderThresholdMinutes = 60 };
        public Func<RoomKey, Room?> Graph = GraphOf(RoomAt(A, -300));
        public Room? Here;
        public bool InDark;
        public bool SightTakenAway;
        public DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        // The light-making spell's caster line, as the Messages data gives it.
        public string? CastLine;
        // What one copy of a light costs at the cheapest shop (null: not known).
        public long? LightPrice;
        public readonly List<AutoLightBuyRequest> BuyRequests = new();
        public readonly List<string> Sent = new();
        public readonly MessageRouter Router = new();
        public readonly AutoLightProvisioner Engine;
        private readonly List<byte[]> _wire = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Engine = new AutoLightProvisioner(
                isEnabled:   () => Enabled,
                snapshot:    () => Snapshot,
                catalogue:   () => Catalogue,
                resolveRoom: k => Graph(k),
                wornIllu:    () => WornIllu,
                roomLightSpell: () => Spell,
                castRoomLightSpell: code =>
                {
                    if (!CastAllowed) return false;
                    CastSpells.Add(code);
                    if (Spell is { IsBuff: true } b && b.CastCode == code) SpellsUp.Add(code);
                    return true;
                },
                roomLightSpellUp: SpellsUp.Contains,
                settings:    () => Settings,
                currentRoom: () => Here,
                inDarkRoom:  () => InDark,
                castLineFor: _ => CastLine,
                sightTakenAway: () => SightTakenAway,
                now: () => Now,
                lightPrice: _ => LightPrice);
            Engine.SetWireSender(b => Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
            Engine.SetProvisioner(req => { BuyRequests.Add(req); return true; });
            Engine.AttachRouter(Router);
        }

        public void Plan(params RoomKey[] route) => Engine.OnRoutePlanned(route);

        public void Poll() => Engine.OnInventoryChanged();

        public void Feed(string line) => Router.Dispatch(new LineExtractor.EmittedLine(
            line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));

        public void Dark() => Feed("The room is pitch black.");

        public void Approach(RoomKey target) => Engine.OnApproachingRoom(target);

        public void Expire() => Feed("Your torch flickers and goes out.");

        public void Enter(int roomLight) => Engine.OnRoomEntered(RoomAt(A, roomLight));
    }

    [Fact]
    public void Disabled_RequestsNoBuy()
    {
        // The same torch-only pack and dark route that hand off a lantern buy when
        // enabled (BuyPlan_HandsOffToProvisioner_NoWireSend).
        Harness h = new() { Enabled = false, Snapshot = Snap(carried: new[] { "torch" }) };
        h.Plan(A);
        Assert.Empty(h.BuyRequests);
    }

    [Fact]
    public void DarkRoute_CoveringCarry_SuppressesPredictiveReady()
    {
        // -300 room, worn 0, carried lantern covers — the planner returns a Ready
        // verdict, but the route-wide ready is suppressed: only the one-room
        // lookahead or the can't-see line lights.
        Harness h = new() { Snapshot = Snap(carried: new[] { "lantern" }) };
        h.Plan(A);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void DarkRoute_WeakLitLight_SuppressesPredictiveSwap()
    {
        // A weak torch is lit and the -300 room needs more — the planner would
        // predictively swap to the carried lantern, but that Ready(-swap) verdict is
        // suppressed too. The reactive dark path handles the swap when the game
        // actually reports we can't see. Reorder off so the dwindling torch doesn't
        // take the restock branch.
        Harness h = new()
        {
            Settings = new() { CarryHours = 12, ReorderThresholdMinutes = 0 },
            Snapshot = Snap(carried: new[] { "lantern" }, readied: new ReadiedLight("torch", 60)),
        };
        h.Plan(A);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void WornIlluCoversRoute_RequestsNoBuy()
    {
        // +250 worn illu against a -300 room clears the see threshold on its own
        // (worn 250 + room -300 = -50 >= -150) → no light to buy, though only a
        // torch is carried.
        Harness h = new() { WornIllu = 250, Snapshot = Snap(carried: new[] { "torch" }) };
        h.Plan(A);
        Assert.Empty(h.BuyRequests);
    }

    [Fact]
    public void LitRoute_RequestsNoBuy()
    {
        Harness h = new()
        {
            Graph = GraphOf(RoomAt(A, 0)),
            Snapshot = Snap(carried: new[] { "torch" }),
        };
        h.Plan(A);
        Assert.Empty(h.BuyRequests);
    }

    [Fact]
    public void ReadiedLightAlreadyCovers_RequestsNoBuy()
    {
        // Lantern (175) lit and healthy covers the -300 room → nothing to buy,
        // though the carried torch alone wouldn't cover it.
        Harness h = new()
        {
            Snapshot = Snap(carried: new[] { "torch" }, readied: new ReadiedLight("lantern", 200)),
        };
        h.Plan(A);
        Assert.Empty(h.BuyRequests);
    }

    [Fact]
    public void BuyPlan_HandsOffToProvisioner_NoWireSend()
    {
        // Need 150, only a torch (100) carried → planner says Buy a lantern. The
        // engine resolves the catalogue id and hands the carry batch to the shop
        // router; the buy itself happens at the shop, so nothing hits the wire.
        Harness h = new() { Snapshot = Snap(carried: new[] { "torch" }) };
        h.Plan(A);
        AutoLightBuyRequest req = Assert.Single(h.BuyRequests);
        Assert.Equal(2, req.ItemId);            // lantern MDB id
        Assert.Equal("lantern", req.LightName);
        Assert.Equal(6, req.Count);             // CarryHours 12 / lantern 2 h burn
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void RoomLightSpellCovers_NoBuyHandoff()
    {
        // Same dark route + only a torch, but a room-light spell (175) covers the
        // -300 room from the worn baseline — so the engine provisions no items.
        Harness h = new()
        {
            Snapshot = Snap(carried: new[] { "torch" }),
            Spell = Starlight,
        };
        h.Plan(A);
        Assert.Empty(h.BuyRequests);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Reorder_ReadiedBelowThreshold_HandsRestockToProvisioner()
    {
        // Lantern readied at 100 pts → 50 min left, below the 60-min threshold. An
        // `i` dump lands (poll) → hand a full carry batch to the shop router; the
        // still-lit lantern keeps burning, so nothing hits the wire.
        Harness h = new() { Snapshot = Snap(readied: new ReadiedLight("lantern", 100)) };
        h.Poll();
        AutoLightBuyRequest req = Assert.Single(h.BuyRequests);
        Assert.Equal(2, req.ItemId);            // lantern MDB id
        Assert.Equal("lantern", req.LightName);
        Assert.Equal(6, req.Count);             // CarryHours 12 / lantern 2 h burn
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Reorder_SameDwindlingLight_RequestsOnlyOnce()
    {
        // The readied charge only refreshes on an `i` dump, so a second poll on the
        // same still-dwindling lantern must not re-detour — the latch holds.
        Harness h = new() { Snapshot = Snap(readied: new ReadiedLight("lantern", 100)) };
        h.Poll();
        h.Snapshot = Snap(readied: new ReadiedLight("lantern", 90));   // drained further
        h.Poll();
        Assert.Single(h.BuyRequests);
    }

    [Fact]
    public void Reorder_FreshLightLit_RetiresLatchThenRefiresWhenItDwindles()
    {
        // Reorder once for the dwindling lantern; a fresh lantern gets lit (charge
        // climbs past the threshold) → latch retires, no duplicate. When that one
        // in turn dwindles below the threshold, a new reorder fires.
        Harness h = new() { Snapshot = Snap(readied: new ReadiedLight("lantern", 100)) };
        h.Poll();
        h.Snapshot = Snap(readied: new ReadiedLight("lantern", 240));  // fresh copy lit
        h.Poll();
        Assert.Single(h.BuyRequests);                                  // no duplicate
        h.Snapshot = Snap(readied: new ReadiedLight("lantern", 100));  // now dwindling again
        h.Poll();
        Assert.Equal(2, h.BuyRequests.Count);
    }

    [Fact]
    public void Reorder_ReadiedAboveThreshold_DoesNotRequest()
    {
        // 240 pts → 120 min left, above the 60-min threshold → no restock.
        Harness h = new() { Snapshot = Snap(readied: new ReadiedLight("lantern", 240)) };
        h.Poll();
        Assert.Empty(h.BuyRequests);
    }

    [Fact]
    public void Reorder_NoReadiedLight_DoesNotRequest()
    {
        // Nothing lit → nothing to reorder (a fresh ground-pickup's charge is
        // unknown until `use`d, so we never reorder off carried spares).
        Harness h = new() { Snapshot = Snap(carried: new[] { "lantern" }) };
        h.Poll();
        Assert.Empty(h.BuyRequests);
    }

    [Fact]
    public void Reorder_Disabled_DoesNotRequest()
    {
        Harness h = new() { Enabled = false, Snapshot = Snap(readied: new ReadiedLight("lantern", 100)) };
        h.Poll();
        Assert.Empty(h.BuyRequests);
    }

    [Fact]
    public void Reorder_RoutePlannedAndPoll_ShareTheLatch()
    {
        // The route-planned path fires the same reorder branch (it precedes the
        // route-dark check), so a route announcement that already reordered must
        // latch a following poll — one request across both seams.
        Harness h = new()
        {
            Snapshot = Snap(carried: new[] { "lantern" }, readied: new ReadiedLight("lantern", 100)),
        };
        h.Plan(A);
        h.Poll();
        Assert.Single(h.BuyRequests);
        Assert.Empty(h.Sent);           // reorder never readies — the lantern stays lit
    }

    // ----- Reactive dark-room fallback (OnDarkRoomObserved) --------------------

    [Fact]
    public void Reactive_DarkObserved_CountPrefixedTorch_ReadiesTorch()
    {
        // The live "can't see" path with a stack-counted pack ("5 torch") and
        // nothing lit → strip the count, ready the carried torch. This is the
        // path the predictive route planner misses on a loop lap / manual step.
        Harness h = new() { Snapshot = Snap(carried: new[] { "5 torch" }) };
        h.Dark();
        // `use torch` then a bare CR to redisplay the now-lit room (reveals standing mobs).
        Assert.Equal(new[] { "use torch", "" }, h.Sent);
    }

    [Fact]
    public void Reactive_DarkObserved_AlreadyLit_SendsNothing()
    {
        Harness h = new()
        {
            Snapshot = Snap(carried: new[] { "5 torch" }, readied: new ReadiedLight("lantern", 200)),
        };
        h.Dark();
        Assert.Empty(h.Sent);
    }

    // Report stock-20261110-180204: a loop walked a dark circuit for half an hour
    // with spare torches in the pack, because the last dump said a torch was lit.
    [Fact]
    public void Reactive_DarkWhereTheLitLightShouldSee_ReadsTheInventoryOnce_ThenLightsASpare()
    {
        Harness h = new()
        {
            Here = RoomAt(A, -175), InDark = true,
            Snapshot = Snap(carried: new[] { "2 torch" }, readied: new ReadiedLight("torch", 30)),
        };
        h.Dark();
        h.Dark();   // the read is on its way: nothing more goes out
        Assert.Equal(new[] { "i" }, h.Sent);

        h.Snapshot = Snap(carried: new[] { "2 torch" }, stamp: 2);
        h.Poll();
        Assert.Equal(new[] { "i", "use torch", "" }, h.Sent);
    }

    [Fact]
    public void Reactive_DarkAfterAUseTheGameNeverAnswered_ReadsTheInventory()
    {
        Harness h = new() { Here = RoomAt(A, -175), InDark = true, Snapshot = Snap(carried: new[] { "2 torch" }) };
        h.Dark();
        Assert.Equal(new[] { "use torch", "" }, h.Sent);

        h.Dark();   // the CR's answer: still dark, so the `use` didn't light it
        Assert.Equal(new[] { "use torch", "", "i" }, h.Sent);

        h.Snapshot = Snap(carried: new[] { "2 torch" }, stamp: 2);
        h.Poll();
        Assert.Equal(new[] { "use torch", "", "i", "use torch", "" }, h.Sent);
    }

    [Fact]
    public void Reactive_DarkWhileTheLightReallyIsLit_ReadsTheInventoryOnlyOnce()
    {
        Harness h = new()
        {
            Here = RoomAt(A, -175), InDark = true,
            Snapshot = Snap(carried: new[] { "torch" }, readied: new ReadiedLight("torch", 30)),
        };
        h.Dark();
        h.Snapshot = Snap(carried: new[] { "torch" }, readied: new ReadiedLight("torch", 30), stamp: 2);
        h.Poll();
        h.Dark();
        Assert.Equal(new[] { "i" }, h.Sent);
    }

    [Fact]
    public void Reactive_ALightReadiedAfterACheck_IsCheckedAgainWhenTheRoomStaysDark()
    {
        Harness h = new()
        {
            Here = RoomAt(A, -175), InDark = true,
            Snapshot = Snap(carried: new[] { "lantern" }, readied: new ReadiedLight("torch", 30)),
        };
        h.Dark();
        Assert.Equal(new[] { "i", "rem torch", "use lantern", "" }, h.Sent);

        h.Dark();   // the lantern's `use` went unanswered too
        Assert.Equal(new[] { "i", "rem torch", "use lantern", "", "i" }, h.Sent);
    }

    [Fact]
    public void Reactive_DarkWhileTheTrackerStillHoldsALitRoom_ReadsNothing()
    {
        Harness h = new()
        {
            Here = RoomAt(A, 0), InDark = true,
            Snapshot = Snap(carried: new[] { "torch" }, readied: new ReadiedLight("torch", 30)),
        };
        h.Dark();
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Reactive_DarkTooDeepForTheLitLight_ReadsNothing()
    {
        Harness h = new()
        {
            Here = RoomAt(A, -300), InDark = true,
            Snapshot = Snap(carried: new[] { "torch" }, readied: new ReadiedLight("torch", 30)),
        };
        h.Dark();
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Reactive_DarkObserved_Disabled_SendsNothing()
    {
        Harness h = new() { Enabled = false, Snapshot = Snap(carried: new[] { "5 torch" }) };
        h.Dark();
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Reactive_DarkObserved_NothingCarried_SendsNothing()
    {
        // No light in the pack and no room-light spell → nothing to light the room
        // with; the can't-see line never buys, and nothing is cast either.
        Harness h = new() { Snapshot = Snap(carried: new[] { "5 dagger" }) };
        h.Dark();
        Assert.Empty(h.Sent);
        Assert.Empty(h.CastSpells);
    }

    [Fact]
    public void Reactive_DarkObserved_NothingCarried_BuffSpell_CastsThenRedisplays()
    {
        // Nothing carried, a room-light buff is set → cast it, then the bare CR that
        // redisplays the room it lit (no wire `use`).
        Harness h = new()
        {
            Snapshot = Snap(carried: new[] { "5 dagger" }),
            Spell = Starlight,
        };
        h.Dark();
        Assert.Equal(new[] { "" }, h.Sent);
        Assert.Equal(new[] { "star" }, h.CastSpells);
    }

    [Fact]
    public void Reactive_DarkObserved_CarriedLightAndBuff_CastsBuffFirst()
    {
        // A carried light AND a room-light buff. The buff goes first: it adds to a
        // lit light, and the planner already counts it as the baseline, so a light
        // item covers only what the buff leaves short.
        Harness h = new()
        {
            Snapshot = Snap(carried: new[] { "torch" }),
            Spell = Starlight,
        };
        h.Dark();
        Assert.Equal(new[] { "" }, h.Sent);
        Assert.Equal(new[] { "star" }, h.CastSpells);
    }

    [Fact]
    public void Reactive_DarkObserved_BuffCastSlotBusy_ReadiesCarriedLight()
    {
        // The cast slot is taken this round (CastCoordinator refuses): the carried
        // light lights the room now instead of waiting.
        Harness h = new()
        {
            Snapshot = Snap(carried: new[] { "torch" }),
            Spell = Starlight,
            CastAllowed = false,
        };
        h.Dark();
        Assert.Equal(new[] { "use torch", "" }, h.Sent);
    }

    [Fact]
    public void Reactive_DarkObserved_Disabled_DoesNotCast()
    {
        Harness h = new()
        {
            Enabled = false,
            Snapshot = Snap(carried: new[] { "5 dagger" }),
            Spell = Starlight,
        };
        h.Dark();
        Assert.Empty(h.CastSpells);
    }

    [Fact]
    public void Reactive_DarkObserved_LightMakingSpell_CastsReadsInventoryThenLightsTheItem()
    {
        // Stock illuminate: nothing carried → cast it and read the inventory so the
        // ball it makes shows up. The dump with the ball, landing while the room is
        // still dark, lights it and redisplays the room; no second cast meanwhile.
        Harness h = new()
        {
            Snapshot = Snap(carried: new[] { "5 dagger" }, stamp: 1),
            Spell = Illuminate,
            InDark = true,
        };
        h.Dark();
        h.Dark();
        Assert.Equal(new[] { "illu" }, h.CastSpells);
        Assert.Equal(new[] { "i", "" }, h.Sent);   // the read, then the CR after a cast here

        h.Snapshot = Snap(carried: new[] { "5 dagger", "light ball" }, stamp: 2);
        h.Poll();
        Assert.Equal(new[] { "i", "", "use light ball", "" }, h.Sent);
        Assert.Equal(new[] { "illu" }, h.CastSpells);
    }

    [Fact]
    public void Reactive_DarkObserved_PendingLatch_DoesNotDoubleSend()
    {
        // Two "can't see" lines before an `i` dump confirms the readied light: the
        // second must not re-fire `use` on the same stale snapshot.
        Harness h = new() { Snapshot = Snap(carried: new[] { "5 torch" }, stamp: 1) };
        h.Dark();
        h.Dark();
        // First Dark: use + redisplay CR. Second Dark is pending-latched — no `use`,
        // so no CR either (the CR is paired only with an actual light-up).
        Assert.Equal(new[] { "use torch", "" }, h.Sent);
    }

    [Fact]
    public void Reactive_DarkObserved_NewerDumpWithoutLight_Resends()
    {
        // First dark line readies the lantern; a newer `i` dump lands still not
        // showing it (the `use` didn't take) → the latch retires and a second dark
        // line re-issues the `use`.
        Harness h = new() { Snapshot = Snap(carried: new[] { "lantern" }, stamp: 1) };
        h.Dark();
        h.Snapshot = Snap(carried: new[] { "lantern" }, stamp: 2);
        h.Dark();
        Assert.Equal(new[] { "use lantern", "", "use lantern", "" }, h.Sent);
    }

    [Fact]
    public void Reactive_DarkObserved_DumpConfirmsLight_ThenGuardHolds()
    {
        // First dark line readies the lantern; the next dump shows it readied (moved
        // out of the carried list) → nothing carried is left to ready, so a following
        // dark line sends nothing.
        Harness h = new() { Snapshot = Snap(carried: new[] { "lantern" }, stamp: 1) };
        h.Dark();
        h.Snapshot = Snap(readied: new ReadiedLight("lantern", 200), stamp: 2);
        h.Dark();
        Assert.Equal(new[] { "use lantern", "" }, h.Sent);
    }

    [Fact]
    public void Reactive_DarkObserved_WeakerCarried_DoesNotDowngradeLitLight()
    {
        // A healthy lantern (175) is lit and the room is dark; the only carried spare
        // is a weaker torch (100). Readying it would only downgrade — the room is
        // simply darker than anything we carry, so hold the lantern.
        Harness h = new()
        {
            Snapshot = Snap(carried: new[] { "5 torch" }, readied: new ReadiedLight("lantern", 200)),
        };
        h.Dark();
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Reactive_DarkObserved_PreferredNameWins()
    {
        // Both carried; preferred torch beats the stronger lantern on the reactive
        // pick, mirroring the route planner's preferred-first policy.
        Harness h = new()
        {
            Settings = new() { CarryHours = 12, ReorderThresholdMinutes = 60, PreferredLightName = "torch" },
            Snapshot = Snap(carried: new[] { "5 torch", "lantern" }),
        };
        h.Dark();
        Assert.Equal(new[] { "use torch", "" }, h.Sent);
    }

    [Fact]
    public void Reactive_DarkObserved_NoPreference_PicksStrongest()
    {
        // No preferred name → the strongest carried light (lantern 175 > torch 100).
        Harness h = new() { Snapshot = Snap(carried: new[] { "5 torch", "lantern" }) };
        h.Dark();
        Assert.Equal(new[] { "use lantern", "" }, h.Sent);
    }

    // ----- Predictive one-room lookahead (OnApproachingRoom) -------------------

    [Fact]
    public void Approach_DarkMappedRoom_ReadiesLightAheadOfStep()
    {
        // The room we're about to step into is mapped dark (worn 0 + -300 well below
        // the see threshold) → `use` the carried torch now, before the move, so the
        // room is lit on arrival instead of one blind step late.
        Harness h = new()
        {
            Graph = GraphOf(RoomAt(DarkAhead, -300)),
            Snapshot = Snap(carried: new[] { "5 torch" }),
        };
        h.Approach(DarkAhead);
        Assert.Equal(new[] { "use torch" }, h.Sent);
    }

    [Fact]
    public void Approach_SeeableMappedRoom_SendsNothing()
    {
        // The next room reads lit on worn gear alone (worn 0 + 0 clears the
        // threshold) → no predictive `use`. This is the one-room horizon that keeps
        // the burn timer from being spent on a room that renders fine.
        Harness h = new()
        {
            Graph = GraphOf(RoomAt(LitAhead, 0)),
            Snapshot = Snap(carried: new[] { "5 torch" }),
        };
        h.Approach(LitAhead);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Approach_UnmappedRoom_SendsNothing()
    {
        // The next room isn't in the active graph → nothing to predict from; the
        // reactive OnDarkRoomObserved catches it one room late instead.
        Harness h = new()
        {
            Graph = GraphOf(RoomAt(DarkAhead, -300)),
            Snapshot = Snap(carried: new[] { "5 torch" }),
        };
        h.Approach(Unmapped);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Approach_WornIlluCoversDarkRoom_SendsNothing()
    {
        // +250 worn illu against a -300 room clears the threshold (-50 >= -150) →
        // the room reads seeable, so no light is readied ahead of it.
        Harness h = new()
        {
            WornIllu = 250,
            Graph = GraphOf(RoomAt(DarkAhead, -300)),
            Snapshot = Snap(carried: new[] { "5 torch" }),
        };
        h.Approach(DarkAhead);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Approach_Disabled_SendsNothing()
    {
        Harness h = new()
        {
            Enabled = false,
            Graph = GraphOf(RoomAt(DarkAhead, -300)),
            Snapshot = Snap(carried: new[] { "5 torch" }),
        };
        h.Approach(DarkAhead);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Approach_AlreadyLitCovers_SendsNothing()
    {
        // A healthy lantern is lit and covers the dark room ahead → don't downgrade
        // to a weaker carried torch.
        Harness h = new()
        {
            Graph = GraphOf(RoomAt(DarkAhead, -300)),
            Snapshot = Snap(carried: new[] { "5 torch" }, readied: new ReadiedLight("lantern", 200)),
        };
        h.Approach(DarkAhead);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Approach_PreferredNameWins()
    {
        // Both carried; the preferred torch beats the stronger lantern, same policy
        // as the reactive pick.
        Harness h = new()
        {
            Settings = new() { CarryHours = 12, ReorderThresholdMinutes = 60, PreferredLightName = "torch" },
            Graph = GraphOf(RoomAt(DarkAhead, -300)),
            Snapshot = Snap(carried: new[] { "5 torch", "lantern" }),
        };
        h.Approach(DarkAhead);
        Assert.Equal(new[] { "use torch" }, h.Sent);
    }

    [Fact]
    public void Approach_PendingLatch_DoesNotDoubleSend()
    {
        // Two steps toward dark rooms before an `i` dump confirms the readied light
        // (same snapshot) → the pending latch stops the second `use`.
        Harness h = new()
        {
            Graph = GraphOf(RoomAt(DarkAhead, -300)),
            Snapshot = Snap(carried: new[] { "5 torch" }, stamp: 1),
        };
        h.Approach(DarkAhead);
        h.Approach(DarkAhead);
        Assert.Equal(new[] { "use torch" }, h.Sent);
    }

    [Fact]
    public void Approach_ThenSeeableRoomEntered_RemsLight()
    {
        // End-to-end: predictively light for the dark room ahead, then `rem` it on
        // stepping into a room seeable on worn gear — the predictive equip feeds the
        // same auto-readied latch the removal path keys on.
        Harness h = new()
        {
            Graph = GraphOf(RoomAt(DarkAhead, -300)),
            Snapshot = Snap(carried: new[] { "5 torch" }),
        };
        h.Approach(DarkAhead);
        h.Enter(0);
        Assert.Equal(new[] { "use torch", "rem torch" }, h.Sent);
    }

    // ----- Readied light burning out (OnReadiedLightExpired) -------------------

    [Fact]
    public void Reactive_LightExpired_ThenDark_RereadiesDespiteStaleReadied()
    {
        // The 154840 case: a torch burned out ("flickers and goes out") but the
        // snapshot still shows it readied (no `i` dump has landed since). The dark
        // room that follows must re-ready a carried spare, discounting the stale
        // readied value — no `rem` first since the light already went out.
        Harness h = new()
        {
            Snapshot = Snap(carried: new[] { "4 torch" }, readied: new ReadiedLight("torch", 10)),
        };
        h.Expire();
        h.Dark();
        Assert.Equal(new[] { "use torch", "" }, h.Sent);
    }

    [Fact]
    public void Reactive_LightExpired_NoDark_SendsNothing()
    {
        // Expiry alone must not ready: if the room is lit by ambient light no spare
        // is wanted. The re-ready waits for the dark-room "can't see" line.
        Harness h = new()
        {
            Snapshot = Snap(carried: new[] { "4 torch" }, readied: new ReadiedLight("torch", 10)),
        };
        h.Expire();
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Reactive_LightExpired_FreshDumpRetiresFlag_NoRereadyOverLitLight()
    {
        // Expiry latches the bridge flag, but the next `i` dump is ground truth:
        // it shows a light genuinely lit, so the flag retires and a later dark
        // room must NOT stomp the lit light with a redundant re-ready.
        Harness h = new()
        {
            Snapshot = Snap(carried: new[] { "4 torch" }, readied: new ReadiedLight("torch", 240)),
        };
        h.Expire();
        h.Poll();   // fresh dump — ground truth, retires the bridge flag
        h.Dark();
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Reactive_LightExpired_WhileDisabled_LeavesNoLatentFlag()
    {
        // Expiry seen with AutoLight off must not latch: re-enabling then hitting a
        // dark room with a (stale) readied light stays a no-op, so a toggle can't
        // resurrect a re-ready the user never wanted.
        Harness h = new()
        {
            Enabled = false,
            Snapshot = Snap(carried: new[] { "4 torch" }, readied: new ReadiedLight("torch", 10)),
        };
        h.Expire();
        h.Enabled = true;
        h.Dark();
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Reactive_LightExpired_ClearsPendingLatch_ThenDarkRereadiesSameName()
    {
        // The 175844 stuck-blind bug: we auto-ready a torch (pending latch = "torch",
        // no `i` dump has landed yet), it burns out, and the next dark room carries
        // an identical spare torch on the SAME snapshot. The burnout must clear the
        // pending latch — otherwise ReadyLight sees the stale same-name latch and
        // skips the re-ready, leaving the player stuck in the dark. Same stamp
        // throughout so no dump lands to retire the latch on its own.
        Harness h = new() { Snapshot = Snap(carried: new[] { "5 torch" }, stamp: 5) };
        h.Dark();       // readies the first torch, latches "torch"
        h.Expire();     // burnout clears the pending latch
        h.Dark();       // same snapshot — re-readies a carried spare
        Assert.Equal(new[] { "use torch", "", "use torch", "" }, h.Sent);
    }

    // ----- Stepping into a seeable room (OnRoomEntered) ------------------------

    [Fact]
    public void RoomEntered_SeeableRoom_RemsAutoReadiedLight()
    {
        // We auto-readied a torch for a dark room; stepping into a room seeable on
        // worn gear alone (worn 0 + room 0 clears the see threshold) puts it away —
        // the "only use a light in rooms we can't see" half of the policy.
        Harness h = new() { Snapshot = Snap(carried: new[] { "5 torch" }) };
        h.Dark();
        h.Enter(0);
        Assert.Equal(new[] { "use torch", "", "rem torch" }, h.Sent);
    }

    [Fact]
    public void RoomEntered_StillUnseeableRoom_KeepsLightLit()
    {
        // The entered room is itself unseeable on worn gear (worn 0 + room -300 is
        // well below the see threshold) → keep the auto-readied light, no `rem`.
        Harness h = new() { Snapshot = Snap(carried: new[] { "5 torch" }) };
        h.Dark();
        h.Enter(-300);
        Assert.Equal(new[] { "use torch", "" }, h.Sent);
    }

    [Fact]
    public void RoomEntered_NothingAutoReadied_SendsNothing()
    {
        // No light was auto-readied → entering a lit room is a no-op; we only put
        // away what THIS engine lit.
        Harness h = new() { Snapshot = Snap(carried: new[] { "5 torch" }) };
        h.Enter(0);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void RoomEntered_ManualReadiedLight_NotRemmed()
    {
        // A light the player readied by hand (auto-readied name never set) is theirs
        // to manage — entering a seeable room must not `rem` it out from under them.
        Harness h = new() { Snapshot = Snap(readied: new ReadiedLight("lantern", 200)) };
        h.Enter(0);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void RoomEntered_Disabled_DoesNotRemTheLight()
    {
        // Lit by this engine, then the toggle turned off: the light stays readied.
        Harness h = new() { Snapshot = Snap(carried: new[] { "5 torch" }) };
        h.Dark();
        h.Enabled = false;
        h.Enter(0);
        Assert.Equal(new[] { "use torch", "" }, h.Sent);
    }

    [Fact]
    public void RoomEntered_BuffUpCoversTheRoom_RemsLight()
    {
        // The torch was lit for a dark room; the next room (-300) is seeable on the
        // starlight buff alone (0 + 175 − 300 = −125 ≥ −150), so the torch goes away.
        Harness h = new() { Snapshot = Snap(carried: new[] { "5 torch" }) };
        h.Dark();
        h.Spell = Starlight;
        h.SpellsUp.Add("star");
        h.Enter(-300);
        Assert.Equal(new[] { "use torch", "", "rem torch" }, h.Sent);
    }

    // ----- The room-light spell one room ahead ---------------------------------

    [Fact]
    public void Approach_BuffCovers_CastsOneRoomAhead_NoLightItem()
    {
        // The -300 room ahead needs 150; starlight's 175 covers it, so the buff goes
        // up before the step and the carried torch stays in the pack.
        Harness h = new()
        {
            Graph = GraphOf(RoomAt(DarkAhead, -300)),
            Snapshot = Snap(carried: new[] { "5 torch" }),
            Spell = Starlight,
        };
        h.Approach(DarkAhead);
        Assert.Equal(new[] { "star" }, h.CastSpells);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Approach_BuffAlreadyUp_DoesNothing()
    {
        Harness h = new()
        {
            Graph = GraphOf(RoomAt(DarkAhead, -300)),
            Snapshot = Snap(carried: new[] { "5 torch" }),
            Spell = Starlight,
        };
        h.SpellsUp.Add("star");
        h.Approach(DarkAhead);
        Assert.Empty(h.CastSpells);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Approach_BuffShort_CastsAndReadiesALightForTheRest()
    {
        // The -450 room needs 300: the buff's 175 leaves 125, so the lantern is lit
        // too — a buff and a light item add up.
        Harness h = new()
        {
            Graph = GraphOf(RoomAt(DeepAhead, -450)),
            Snapshot = Snap(carried: new[] { "lantern" }),
            Spell = Starlight,
        };
        h.Approach(DeepAhead);
        Assert.Equal(new[] { "star" }, h.CastSpells);
        Assert.Equal(new[] { "use lantern" }, h.Sent);
    }

    [Fact]
    public void SpellInEffectAndStillDark_IsLatched_NotRecastForRoomsAsDark()
    {
        RoomKey darker = new(1, 203);
        Harness h = new()
        {
            Graph = GraphOf(RoomAt(DeepAhead, -450), RoomAt(darker, -500), RoomAt(DarkAhead, -300)),
            Spell = Starlight,
            Here = RoomAt(DeepAhead, -450),
        };
        h.Approach(DeepAhead);          // cast ahead
        h.Dark();                       // arrived, buff up, still can't see → latched
        h.Dark();
        Assert.Equal(new[] { "star" }, h.CastSpells);
        Assert.Contains("star not enough at Light -450", h.Engine.DescribeState());

        h.SpellsUp.Clear();             // the buff ran out
        h.Dark();                       // in the same dark: not cast again
        h.Approach(darker);             // a room as dark or darker: not cast either
        Assert.Equal(new[] { "star" }, h.CastSpells);

        h.Approach(DarkAhead);          // a brighter room it can light: cast
        Assert.Equal(new[] { "star", "star" }, h.CastSpells);
    }

    [Fact]
    public void SpellLatch_LiftsWhenWornIlluRises_OrOnForget()
    {
        Harness h = new()
        {
            Graph = GraphOf(RoomAt(DeepAhead, -450)),
            Spell = Starlight,
            Here = RoomAt(DeepAhead, -450),
        };
        h.Approach(DeepAhead);
        h.Dark();
        h.SpellsUp.Clear();
        h.Approach(DeepAhead);
        Assert.Single(h.CastSpells);

        h.WornIllu = 50;                // better gear: the spell may now be enough
        h.Approach(DeepAhead);
        Assert.Equal(2, h.CastSpells.Count);

        h.Dark();                       // latched again at worn 50
        h.SpellsUp.Clear();
        h.Engine.Forget("profile loaded");
        h.Approach(DeepAhead);
        Assert.Equal(3, h.CastSpells.Count);
    }

    [Fact]
    public void LightMakingSpell_ItsItemLitAndStillDark_IsLatched()
    {
        // The ball illuminate made is lit and the room still can't be seen: not
        // cast again here, before or after the ball burns out.
        Harness h = new()
        {
            Spell = Illuminate,
            Here = RoomAt(A, -450),
            Snapshot = Snap(readied: new ReadiedLight("light ball", 70)),
        };
        h.Dark();
        h.Expire();
        h.Dark();
        Assert.Empty(h.CastSpells);
        Assert.Empty(h.Sent);
    }

    // Spell first, the light-making kind too (user, 2026-10-17): illuminate is cast
    // ahead of the dark room though a torch is carried, and the ball it makes is
    // lit for that room once the inventory shows it — the torch stays in the pack.
    [Fact]
    public void Approach_LightMakingSpell_CastsFirst_AndLightsItsBall_NoCarriedLightOnTop()
    {
        Harness h = new()
        {
            Graph = GraphOf(RoomAt(DarkAhead, -300)),
            Snapshot = Snap(carried: new[] { "torch" }, stamp: 1),
            Spell = Illuminate,
        };
        h.Approach(DarkAhead);
        Assert.Equal(new[] { "illu" }, h.CastSpells);
        Assert.Equal(new[] { "i" }, h.Sent);

        h.Snapshot = Snap(carried: new[] { "torch", "light ball" }, stamp: 2);
        h.Poll();
        Assert.Equal(new[] { "i", "use light ball" }, h.Sent);
    }

    // The probe that found 4 casts and 4 balls: a torch or lantern lit, the room
    // still dark, a can't-see line after another. A ball would be no brighter than
    // what is lit, so nothing is cast.
    [Theory]
    [InlineData("torch", 80)]
    [InlineData("lantern", 240)]
    public void LightMakingSpell_ALightAsStrongIsLit_RepeatedDarkLines_NeverCast(string lit, int readied)
    {
        Harness h = new()
        {
            Spell = Illuminate,
            Here = RoomAt(A, -450),
            Snapshot = Snap(readied: new ReadiedLight(lit, readied)),
        };
        h.Dark();
        h.Dark();
        h.Dark();
        Assert.Empty(h.CastSpells);
        Assert.Empty(h.Sent);
    }

    // A ball already in the pack (here one no brighter than the lit lantern) is
    // never made again.
    [Fact]
    public void LightMakingSpell_ABallCarried_NotCastAgain()
    {
        Harness h = new()
        {
            Spell = Illuminate,
            Here = RoomAt(A, -450),
            Snapshot = Snap(carried: new[] { "light ball" }, readied: new ReadiedLight("lantern", 240)),
        };
        h.Dark();
        h.Dark();
        Assert.Empty(h.CastSpells);
    }

    // The game gives the ball with the spell's own cast message and no line of its
    // own: the inventory read waits for "You cast illuminate!", and a fizzle
    // instead drops the wait so the spell can be tried again.
    [Fact]
    public void LightMakingSpell_ReadsTheInventoryAfterItsCastMessage_AFizzleEndsTheWait()
    {
        Harness h = new()
        {
            Spell = Illuminate,
            CastLine = "You cast {spellname}!",
            Here = RoomAt(A, -300),
            InDark = true,
        };
        h.Dark();
        Assert.Equal(new[] { "illu" }, h.CastSpells);
        Assert.Equal(new[] { "" }, h.Sent);         // the CR after a cast here; no read yet

        h.Feed("You cast illuminate!");
        Assert.Equal(new[] { "", "i" }, h.Sent);

        h.Snapshot = Snap(stamp: 5);    // the dump: no ball (it didn't take after all)
        h.Poll();
        h.Dark();
        Assert.Equal(new[] { "illu", "illu" }, h.CastSpells);
        h.Feed("You attempt to cast illuminate, but fail.");
        h.Dark();
        Assert.Equal(new[] { "illu", "illu", "illu" }, h.CastSpells);
        Assert.Equal(new[] { "", "i", "", "" }, h.Sent);
    }

    // Blind or under a darkening spell (globe of darkness and the like, which print
    // the can't-see line — user, 2026-10-17), a dark room says nothing about the
    // spell: it isn't latched. Lighting goes on, and once the debuff ends the latch
    // works again.
    [Fact]
    public void SightTakenAway_LightsAsUsual_ButNeverLatches()
    {
        Harness h = new()
        {
            Spell = Starlight,
            Here = RoomAt(A, -300),
            SightTakenAway = true,
        };
        h.Dark();                       // the buff is cast as usual
        Assert.Equal(new[] { "star" }, h.CastSpells);
        Assert.Equal(new[] { "" }, h.Sent);
        h.Dark();                       // up and still dark: not counted against it
        Assert.DoesNotContain("not enough at Light", h.Engine.DescribeState());

        h.SightTakenAway = false;
        h.Dark();
        Assert.Contains("not enough at Light", h.Engine.DescribeState());
    }

    // ----- A light-making cast whose word never comes ---------------------------

    // The caster line is awaited: no second cast, but the carried torch lights
    // the room meanwhile (the probe that found the character left in the dark).
    [Fact]
    public void LightMakingCast_AwaitingItsLine_CarriedLightsStillLight()
    {
        Harness h = new()
        {
            Spell = Illuminate,
            CastLine = "You cast {spellname}!",
            Snapshot = Snap(carried: new[] { "5 dagger" }),
            Here = RoomAt(A, -450),
        };
        h.Dark();                       // cast, CR
        h.Snapshot = Snap(carried: new[] { "torch" });   // a torch picked up, same dump time
        h.Dark();                       // the CR's dark line: the line never came
        Assert.Equal(new[] { "illu" }, h.CastSpells);
        Assert.Equal(new[] { "", "use torch", "" }, h.Sent);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("room")]
    public void LightMakingCast_TheWaitEnds_TheSpellIsTriedAgain(string how)
    {
        Harness h = new()
        {
            Spell = Illuminate,
            CastLine = "You cast {spellname}!",
            Here = RoomAt(A, -300),
        };
        h.Dark();
        h.Dark();
        Assert.Single(h.CastSpells);

        // A wait whose cast line didn't come reads the inventory once; the dump
        // (without a ball) or that read's own wait running out ends it.
        if (how == "timeout")
        {
            h.Now += AutoLightProvisioner.MadeItemWait + TimeSpan.FromSeconds(1);
            h.Dark();
            Assert.Contains("i", h.Sent);
            Assert.Single(h.CastSpells);
            h.Now += AutoLightProvisioner.MadeItemWait + TimeSpan.FromSeconds(1);
        }
        if (how == "room")
        {
            h.Engine.OnRoomChanged();
            Assert.Contains("i", h.Sent);
            h.Snapshot = Snap(stamp: 9);
            h.Poll();
        }
        h.Dark();
        Assert.Equal(2, h.CastSpells.Count);
    }

    // Another engine's `i`, sent just before the cast, lands while the caster line
    // is still awaited: it shows no ball, which says nothing about the cast. The
    // caster line then reads the inventory, and the ball is found.
    [Fact]
    public void LightMakingCast_ADumpBeforeTheCastLine_DoesNotEndTheWait()
    {
        Harness h = new()
        {
            Spell = Illuminate,
            CastLine = "You cast {spellname}!",
            Here = RoomAt(A, -300),
            InDark = true,
            Snapshot = Snap(stamp: 1),
        };
        h.Dark();                                   // cast, CR
        h.Snapshot = Snap(stamp: 2);                // the other engine's dump, no ball
        h.Poll();
        h.Dark();
        Assert.Single(h.CastSpells);

        h.Feed("You cast illuminate!");
        Assert.Equal(new[] { "", "i" }, h.Sent);
        h.Snapshot = Snap(carried: new[] { "light ball" }, stamp: 3);
        h.Poll();
        Assert.Equal(new[] { "", "i", "use light ball", "" }, h.Sent);
    }

    // The probe: the Messages data's caster line isn't what the game printed, and
    // the walk moves on room after room. The first room change reads the
    // inventory, the ball turns up in that dump and is lit; illuminate is cast once.
    [Fact]
    public void LightMakingCast_LineNeverMatches_TheRoomChangeReadsTheInventory_NoRecast()
    {
        Harness h = new()
        {
            Graph = GraphOf(RoomAt(DarkAhead, -300)),
            Spell = Illuminate,
            CastLine = "You conjure {spellname}!",
            Snapshot = Snap(stamp: 1),
        };
        h.Approach(DarkAhead);
        h.Feed("You cast illuminate!");      // not the template's line
        h.Engine.OnRoomChanged();
        h.Approach(DarkAhead);
        h.Engine.OnRoomChanged();
        h.Approach(DarkAhead);
        Assert.Equal(new[] { "illu" }, h.CastSpells);
        Assert.Equal(new[] { "i" }, h.Sent);

        h.Snapshot = Snap(carried: new[] { "light ball" }, stamp: 2);
        h.Poll();
        Assert.Equal(new[] { "i", "use light ball" }, h.Sent);
    }

    // The latch holds for the dark stretch it was found in: a room shown ends it,
    // and a brighter light lit (or better worn gear) makes it void.
    [Fact]
    public void SpellLatch_EndsWithTheDarkStretch_AndWithABrighterLight()
    {
        Harness h = new()
        {
            Graph = GraphOf(RoomAt(DeepAhead, -450)),
            Spell = Starlight,
            Here = RoomAt(DeepAhead, -450),
        };
        h.Approach(DeepAhead);
        h.Dark();                       // latched, nothing lit
        h.SpellsUp.Clear();
        h.Snapshot = Snap(readied: new ReadiedLight("torch", 80));
        h.Approach(DeepAhead);          // a torch now lit: the latch was for nothing lit
        Assert.Equal(2, h.CastSpells.Count);

        h.Dark();                       // latched again with the torch
        h.SpellsUp.Clear();
        h.Approach(DeepAhead);
        Assert.Equal(2, h.CastSpells.Count);
        h.Enter(0);                     // out of the dark
        h.Approach(DeepAhead);
        Assert.Equal(3, h.CastSpells.Count);
    }

    [Fact]
    public void Approach_LightMakingSpell_NothingCarried_CastsOnceAndReadsInventory()
    {
        Harness h = new()
        {
            Graph = GraphOf(RoomAt(DarkAhead, -300)),
            Spell = Illuminate,
        };
        h.Approach(DarkAhead);
        h.Approach(DarkAhead);          // the made item is still awaited
        Assert.Equal(new[] { "illu" }, h.CastSpells);
        Assert.Equal(new[] { "i" }, h.Sent);
    }

    // ----- Only use my room-light spell ----------------------------------------

    [Fact]
    public void SpellOnly_Dark_CastsTheBuff_NeverLightsACarriedLight()
    {
        Harness h = new()
        {
            Settings = new() { CarryHours = 12, UseRoomLightSpellOnly = true },
            Snapshot = Snap(carried: new[] { "5 torch" }),
            Spell = Starlight,
        };
        h.Dark();
        h.Dark();                       // buff up, still dark: the torch stays put
        Assert.Equal(new[] { "star" }, h.CastSpells);
        Assert.Equal(new[] { "" }, h.Sent);
    }

    [Fact]
    public void SpellOnly_NoSpell_DarkOrApproach_SendsNothing()
    {
        Harness h = new()
        {
            Settings = new() { CarryHours = 12, UseRoomLightSpellOnly = true },
            Graph = GraphOf(RoomAt(DarkAhead, -300)),
            Snapshot = Snap(carried: new[] { "5 torch" }),
        };
        h.Dark();
        h.Approach(DarkAhead);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void SpellOnly_LightMakingSpell_ReadiesOnlyTheItemItMade()
    {
        Harness h = new()
        {
            Settings = new() { CarryHours = 12, UseRoomLightSpellOnly = true },
            Graph = GraphOf(RoomAt(DarkAhead, -300)),
            Snapshot = Snap(carried: new[] { "lantern", "light ball" }),
            Spell = Illuminate,
        };
        h.Approach(DarkAhead);
        Assert.Equal(new[] { "use light ball" }, h.Sent);
    }

    [Fact]
    public void SpellOnly_ProvisionsNothing()
    {
        // No buy before a dark walk, no reorder of a dwindling light, nothing off the
        // floor, no stock-up.
        Harness h = new()
        {
            Settings = new() { CarryHours = 12, ReorderThresholdMinutes = 60, UseRoomLightSpellOnly = true },
            Snapshot = Snap(readied: new ReadiedLight("lantern", 100)),
        };
        h.Plan(A);
        h.Poll();
        Assert.Empty(h.BuyRequests);
        Assert.Equal(0, h.Engine.FloorLightsWanted(Torch.Number));
        Assert.Null(h.Engine.PlanStockUp(new[] { A }));
    }

    // ----- A light on the floor -------------------------------------------------

    [Fact]
    public void FloorLight_AsManyAsTheCarryHoursShortfall()
    {
        Harness h = new();
        Assert.Equal(18, h.Engine.FloorLightsWanted(Torch.Number));   // 12 h of 40-minute torches
        h.Snapshot = Snap(carried: new[] { "5 lantern" });    // 10 h of 12: 2 h short
        Assert.Equal(3, h.Engine.FloorLightsWanted(Torch.Number));
        Assert.Equal(1, h.Engine.FloorLightsWanted(Lantern.Number));
        h.Snapshot = Snap(carried: new[] { "6 lantern" });    // 6 × 2 h = the 12 h target
        Assert.Equal(0, h.Engine.FloorLightsWanted(Torch.Number));
        Assert.Equal(0, h.Engine.FloorLightsWanted(99));      // not a light
    }

    [Fact]
    public void FloorLight_CarryHoursZero_OnlyWhenNoLightCarried()
    {
        Harness h = new() { Settings = new() { CarryHours = 0 } };
        Assert.Equal(1, h.Engine.FloorLightsWanted(Torch.Number));
        h.Snapshot = Snap(carried: new[] { "torch" });
        Assert.Equal(0, h.Engine.FloorLightsWanted(Torch.Number));
    }

    [Fact]
    public void FloorLight_Disabled_NotWanted()
    {
        Harness h = new() { Enabled = false };
        Assert.Equal(0, h.Engine.FloorLightsWanted(Torch.Number));
    }

    // ----- Stocking up on a trip that passes a shop ----------------------------

    [Fact]
    public void StockUp_PreferredLight_UpToCarryHours()
    {
        // 2 lanterns carried (4 h) of a 12 h target → 4 more, a target of 6.
        Harness h = new()
        {
            Settings = new() { CarryHours = 12, PreferredLightName = "lantern" },
            Snapshot = Snap(carried: new[] { "2 lantern" }),
        };
        AutoLightBuyRequest? buy = h.Engine.PlanStockUp(new[] { A });
        Assert.Equal(new AutoLightBuyRequest(2, "lantern", 6), buy);
        Assert.Contains("lantern x4 to stock up", h.Engine.DescribeState());
    }

    [Fact]
    public void StockUp_NoDarkAheadAndNothingLit_NothingBought()
    {
        Harness h = new()
        {
            Settings = new() { CarryHours = 12, PreferredLightName = "lantern" },
            Graph = GraphOf(RoomAt(A, 0)),
        };
        Assert.Null(h.Engine.PlanStockUp(new[] { A }));

        // A light lit right now says light is in use (an Auto-Lair run has no
        // circuit to read).
        h.Snapshot = Snap(readied: new ReadiedLight("lantern", 240));
        Assert.NotNull(h.Engine.PlanStockUp(new[] { A }));
    }

    [Fact]
    public void StockUp_ShortByLessThanOneLight_NothingBought()
    {
        // 5 lanterns (10 h) and one lit with an hour left: 11 h of 12 — no whole
        // lantern fits under the target.
        Harness h = new()
        {
            Settings = new() { CarryHours = 12, PreferredLightName = "lantern" },
            Snapshot = Snap(carried: new[] { "5 lantern" }, readied: new ReadiedLight("lantern", 120)),
        };
        Assert.Null(h.Engine.PlanStockUp(new[] { A }));
    }

    [Fact]
    public void StockUp_NoPreferredAndNothingLit_NothingToStock()
    {
        Harness h = new();
        Assert.Null(h.Engine.PlanStockUp(new[] { A }));
    }

    // ----- A reorder while a loop runs -----------------------------------------

    [Fact]
    public void Reorder_WhileLoopRuns_GoesToTheRestockTrip_Once()
    {
        Harness h = new() { Snapshot = Snap(readied: new ReadiedLight("lantern", 100)) };
        List<AutoLightBuyRequest> trips = new();
        h.Engine.SetLoopRestock(() => true, req => { trips.Add(req); return LightRestockOutcome.Started; });
        h.Poll();
        h.Snapshot = Snap(readied: new ReadiedLight("lantern", 90));
        h.Poll();
        Assert.Equal(new[] { new AutoLightBuyRequest(2, "lantern", 6) }, trips);
        Assert.Empty(h.BuyRequests);
    }

    [Fact]
    public void Reorder_WhileLoopRuns_TripBusy_AsksAgainNextDump()
    {
        Harness h = new() { Snapshot = Snap(readied: new ReadiedLight("lantern", 100)) };
        int asks = 0;
        h.Engine.SetLoopRestock(() => true, _ => { asks++; return LightRestockOutcome.Busy; });
        h.Poll();
        h.Poll();
        Assert.Equal(2, asks);
    }

    [Fact]
    public void Reorder_NoEngineRunning_GoesToTheShopRouter()
    {
        Harness h = new() { Snapshot = Snap(readied: new ReadiedLight("lantern", 100)) };
        int asks = 0;
        h.Engine.SetLoopRestock(() => false, _ => { asks++; return LightRestockOutcome.Started; });
        h.Poll();
        Assert.Equal(0, asks);
        Assert.Single(h.BuyRequests);
    }

    // ----- The shop router's answer ---------------------------------------------

    // A reorder the walk-to router turns down (another errand has the walker) isn't
    // latched: the next `i` dump asks again, and once the router takes it the
    // reorder is latched and logged as a plan.
    [Fact]
    public void Reorder_RouterRefuses_NotLatched_AskedAgainUntilItTakesIt()
    {
        Harness h = new() { Snapshot = Snap(readied: new ReadiedLight("lantern", 100)) };
        bool errandWalking = true;
        List<RoomKey> walks = new();
        RoomKey shop = new(1, 150), here = new(1, 100), dest = new(1, 200);
        AutoLightShopRouter router = new(
            shopRoomsSellingItem: _ => new[] { shop },
            currentRoom: () => here,
            walkDestination: () => dest,
            distanceBetween: (_, _) => 3,
            carriedCount: _ => 0,
            isEnabled: () => true,
            engineWalkActive: () => errandWalking,
            walkTo: walks.Add,
            post: a => a(),
            buyTimeout: TimeSpan.FromHours(1));
        h.Engine.SetProvisioner(router.OnBuyRequested);

        h.Poll();
        Assert.Empty(walks);
        Assert.Contains("last planned buy none", h.Engine.DescribeState());

        errandWalking = false;
        h.Poll();
        Assert.Equal(new[] { shop }, walks);
        Assert.Contains("reorder latched lantern", h.Engine.DescribeState());

        h.Poll();
        Assert.Single(walks);
        router.Dispose();
    }

    // ----- A loop with no light ---------------------------------------------------

    // A loop whose circuit crosses a room nothing lights, with no light carried:
    // a light trip off the loop, asked once per loop and lights carried.
    [Fact]
    public void Loop_CircuitRunsDark_NothingCarried_OneLightTrip()
    {
        Harness h = new();
        List<AutoLightBuyRequest> trips = new();
        h.Engine.SetLoopRestock(() => true, req => { trips.Add(req); return LightRestockOutcome.NotPossible; },
            engineRooms: () => new[] { A });

        h.Engine.OnEngineStarted(userStarted: false);
        h.Poll();
        Assert.Equal(new[] { new AutoLightBuyRequest(2, "lantern", 6) }, trips);   // the weakest light that covers -300

        h.Snapshot = Snap(carried: new[] { "torch" });   // the lights carried changed: asked again
        h.Poll();
        Assert.Equal(2, trips.Count);
    }

    // A loop trip that couldn't buy is tried again when the user starts a run,
    // when the purse grows, or after the back-off — not on the trip's own resume.
    [Theory]
    [InlineData("user start")]
    [InlineData("wealth")]
    [InlineData("back-off")]
    public void Loop_TripThatCouldNotBuy_TriedAgain(string how)
    {
        Harness h = new() { LightPrice = 1000 };
        int asks = 0;
        h.Engine.SetLoopRestock(() => true, _ => { asks++; return LightRestockOutcome.NotPossible; },
            engineRooms: () => new[] { A });
        h.Engine.OnEngineStarted(userStarted: false);
        h.Engine.OnEngineStarted(userStarted: false);   // a trip's resume: held back
        Assert.Equal(1, asks);

        if (how == "user start") h.Engine.OnEngineStarted(userStarted: true);
        if (how == "wealth")
        {
            h.Snapshot = h.Snapshot with { Currency = new CurrencyHoldings(0, 0, 50, 0, 0, 5000) };
            h.Poll();
        }
        if (how == "back-off")
        {
            h.Now += AutoLightProvisioner.EngineTripRetry + TimeSpan.FromSeconds(1);
            h.Poll();
        }
        Assert.Equal(2, asks);
    }

    // A drop that cuts off a loop trip undoes only that try: three misses before it
    // still count, so the next miss waits the fourth back-off (80 minutes), not the
    // first (10).
    [Fact]
    public void Loop_TripCutOffByADrop_KeepsTheEarlierMisses()
    {
        Harness h = new();
        int asks = 0;
        LightRestockOutcome answer = LightRestockOutcome.NotPossible;
        h.Engine.SetLoopRestock(() => true, _ => { asks++; return answer; }, engineRooms: () => new[] { A });
        h.Engine.OnEngineStarted(userStarted: false);                 // miss 1
        h.Now += TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1);
        h.Poll();                                                     // miss 2
        h.Now += TimeSpan.FromMinutes(20) + TimeSpan.FromSeconds(1);
        h.Poll();                                                     // miss 3
        Assert.Equal(3, asks);

        answer = LightRestockOutcome.Started;
        h.Now += TimeSpan.FromMinutes(40) + TimeSpan.FromSeconds(1);
        h.Poll();                                                     // the 4th: under way
        Assert.Equal(4, asks);
        h.Engine.NoteEngineTripCutOff();                              // the drop cuts it off

        answer = LightRestockOutcome.NotPossible;
        h.Poll();                                                     // free again at once: miss 4
        Assert.Equal(5, asks);
        h.Now += TimeSpan.FromMinutes(40) + TimeSpan.FromSeconds(1);
        h.Poll();
        Assert.Equal(5, asks);                                        // within the 80-minute back-off
    }

    // A few coppers picked up after the try don't send the loop shopping again;
    // reaching the price of one copy (when it was short of it) does, and a purse
    // that could already pay never does.
    [Fact]
    public void Loop_TripThatCouldNotBuy_WealthCountsOnlyWhenItNowPaysForOne()
    {
        Harness h = new() { LightPrice = 1000 };
        int asks = 0;
        h.Engine.SetLoopRestock(() => true, _ => { asks++; return LightRestockOutcome.NotPossible; },
            engineRooms: () => new[] { A });
        h.Engine.OnEngineStarted(userStarted: false);
        h.Snapshot = h.Snapshot with { Currency = new CurrencyHoldings(12, 0, 0, 0, 0, 12) };
        h.Poll();
        Assert.Equal(1, asks);
        h.Snapshot = h.Snapshot with { Currency = new CurrencyHoldings(0, 0, 10, 0, 0, 1000) };
        h.Poll();
        Assert.Equal(2, asks);
        h.Snapshot = h.Snapshot with { Currency = new CurrencyHoldings(0, 0, 90, 0, 0, 9000) };
        h.Poll();
        Assert.Equal(2, asks);           // it could pay last time: coin wasn't the trouble
    }

    // Failure after failure the wait doubles from 10 minutes up to 2 hours; a
    // circuit that comes up lit starts it over.
    [Fact]
    public void Loop_TripThatKeepsFailing_BacksOffDoubling_AndALitCircuitResetsIt()
    {
        Harness h = new();
        int asks = 0;
        h.Engine.SetLoopRestock(() => true, _ => { asks++; return LightRestockOutcome.NotPossible; },
            engineRooms: () => new[] { A });
        h.Engine.OnEngineStarted(userStarted: false);                 // try 1
        h.Now += TimeSpan.FromMinutes(11);
        h.Poll();                                                     // try 2 (after 10 min)
        h.Now += TimeSpan.FromMinutes(15);
        h.Poll();                                                     // 15 of 20: held
        Assert.Equal(2, asks);
        h.Now += TimeSpan.FromMinutes(6);
        h.Poll();                                                     // try 3 (after 20 min)
        Assert.Equal(3, asks);
        for (int i = 0; i < 6; i++)
        {
            h.Now += AutoLightProvisioner.EngineTripRetryCap + TimeSpan.FromSeconds(1);
            h.Poll();
        }
        Assert.Equal(9, asks);                                        // capped at 2 hours
        h.Now += TimeSpan.FromMinutes(100);
        h.Poll();
        Assert.Equal(9, asks);

        h.Snapshot = Snap(readied: new ReadiedLight("lantern", 240)); // lit: the trouble is over
        h.Poll();
        h.Snapshot = Snap();
        h.Poll();
        Assert.Equal(10, asks);                                       // asked at once again
    }

    // Every start or recovery re-plan begins the circle at the room it re-enters:
    // the same loop, rotated, is still the same loop for the back-off.
    [Fact]
    public void Loop_RotatedStartRoom_IsTheSameLoop_ForTheBackOff()
    {
        RoomKey b = new(1, 101);
        Harness h = new() { Graph = GraphOf(RoomAt(A, -300), RoomAt(b, -300)) };
        int asks = 0;
        bool rotated = false;
        h.Engine.SetLoopRestock(() => true, _ => { asks++; return LightRestockOutcome.NotPossible; },
            engineRooms: () => rotated ? new[] { b, A, b } : new[] { A, b, A });
        h.Engine.OnEngineStarted(userStarted: false);
        for (int i = 0; i < 4; i++)
        {
            rotated = !rotated;
            h.Engine.OnEngineStarted(userStarted: false);   // a recovery re-plan's start
        }
        Assert.Equal(1, asks);

        h.Now += AutoLightProvisioner.EngineTripRetry + TimeSpan.FromSeconds(1);
        h.Poll();
        Assert.Equal(2, asks);
    }

    [Fact]
    public void Loop_TripBusy_AskedAgain_SpellOnly_NeverAsked()
    {
        Harness h = new();
        int asks = 0;
        h.Engine.SetLoopRestock(() => true, _ => { asks++; return LightRestockOutcome.Busy; },
            engineRooms: () => new[] { A });
        h.Poll();
        h.Poll();
        Assert.Equal(2, asks);

        h.Settings = new() { CarryHours = 12, UseRoomLightSpellOnly = true };
        h.Poll();
        Assert.Equal(2, asks);
    }

    // The last light burning out mid-loop leaves the circuit unlit: a trip.
    [Fact]
    public void Loop_LastLightBurnsOut_LightTrip()
    {
        Harness h = new() { Snapshot = Snap(readied: new ReadiedLight("lantern", 200)) };
        List<AutoLightBuyRequest> trips = new();
        h.Engine.SetLoopRestock(() => true, req => { trips.Add(req); return LightRestockOutcome.Started; },
            engineRooms: () => new[] { A });
        h.Engine.OnEngineStarted(userStarted: false);
        Assert.Empty(trips);            // the lit lantern covers the circuit

        h.Feed("Your lamp runs out of oil, and goes out.");
        Assert.Single(trips);
    }
}
