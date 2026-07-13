using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FujinTerm.Game.Inventory;
using FujinTerm.Game.Light;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="AutoLightProvisioner"/> — the active engine behind auto-light. A
/// planned route only PROVISIONS the pack (deferred buy / reorder); the route-scan's
/// own Ready verdict is suppressed. A light is `use`d predictively one room ahead
/// (OnApproachingRoom, off the target room's mapped light) or reactively on the
/// server's live can't-see line (OnDarkRoomObserved, the fallback for an unmapped
/// room), and `rem`d on stepping into a room seeable on worn gear alone. Exercises
/// the master-toggle gate, the deferred buy / reorder, the predictive + reactive
/// ready and the rem, and the pending latch that keeps a still-unconfirmed `use`
/// from being re-sent on a stale inventory snapshot.
/// </summary>
public sealed class AutoLightProvisionerTests
{
    // torch: 100 illu, 40-min burn; lantern: 175 illu, 2 h burn.
    private static readonly LightItem Torch = new(1, "torch", Strength: 100, UseCount: 800);
    private static readonly LightItem Lantern = new(2, "lantern", Strength: 175, UseCount: 2400);
    private static readonly IReadOnlyList<LightItem> Catalogue = new[] { Torch, Lantern };

    private static readonly RoomKey A = new(1, 100);
    // Target rooms for the predictive-lookahead tests, resolved via the harness graph.
    private static readonly RoomKey DarkAhead = new(1, 200);
    private static readonly RoomKey LitAhead = new(1, 201);
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
        public InventorySnapshot Snapshot = Snap();
        public AutoLightSettings Settings = new() { CarryHours = 12, ReorderThresholdMinutes = 60 };
        public Func<RoomKey, Room?> Graph = GraphOf(RoomAt(A, -300));
        public readonly List<AutoLightBuyRequest> BuyRequests = new();
        public readonly AutoLightProvisioner Engine;

        public Harness()
        {
            Engine = new AutoLightProvisioner(
                isEnabled:   () => Enabled,
                snapshot:    () => Snapshot,
                catalogue:   () => Catalogue,
                resolveRoom: k => Graph(k),
                wornIllu:    () => WornIllu,
                settings:    () => Settings);
            Engine.SetWireSender(_ => { });
            Engine.SetProvisioner(BuyRequests.Add);
        }

        public void Plan(params RoomKey[] route) => Engine.OnRoutePlanned(route);

        public void Poll() => Engine.OnInventoryChanged();

        public void Dark() => Engine.OnDarkRoomObserved();

        public void Approach(RoomKey target) => Engine.OnApproachingRoom(target);

        public void Expire() => Engine.OnReadiedLightExpired();

        public void Enter(int roomLight) => Engine.OnRoomEntered(RoomAt(A, roomLight));

        public IReadOnlyList<string> Sent => Engine.LastSentForTests
            .Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r'))
            .ToList();
    }

    [Fact]
    public void Disabled_SendsNothing()
    {
        Harness h = new() { Enabled = false, Snapshot = Snap(carried: new[] { "lantern" }) };
        h.Plan(A);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void DarkRoute_CoveringCarry_SuppressesPredictiveReady()
    {
        // -300 room, worn 0, carried lantern covers — the planner returns a Ready
        // verdict, but predictive readying is suppressed: a light is `use`d only on
        // the server's live can't-see line, never ahead of a route.
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
    public void WornIlluCoversRoute_SendsNothing()
    {
        // +250 worn illu against a -300 room clears the see threshold on its own
        // (worn 250 + room -300 = -50 >= -150) → no light to ready.
        Harness h = new() { WornIllu = 250, Snapshot = Snap(carried: new[] { "lantern" }) };
        h.Plan(A);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void LitRoute_SendsNothing()
    {
        Harness h = new()
        {
            Graph = GraphOf(RoomAt(A, 0)),
            Snapshot = Snap(carried: new[] { "lantern" }),
        };
        h.Plan(A);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void ReadiedLightAlreadyCovers_SendsNothing()
    {
        // Lantern (175) lit and healthy covers the -300 room → leave it be.
        Harness h = new()
        {
            Snapshot = Snap(carried: new[] { "torch" }, readied: new ReadiedLight("lantern", 200)),
        };
        h.Plan(A);
        Assert.Empty(h.Sent);
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
        Assert.Equal(new[] { "use torch" }, h.Sent);
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
        // No light in the pack → leave buying/getting to the reactive need-poster.
        Harness h = new() { Snapshot = Snap(carried: new[] { "5 dagger" }) };
        h.Dark();
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Reactive_DarkObserved_PendingLatch_DoesNotDoubleSend()
    {
        // Two "can't see" lines before an `i` dump confirms the readied light: the
        // second must not re-fire `use` on the same stale snapshot.
        Harness h = new() { Snapshot = Snap(carried: new[] { "5 torch" }, stamp: 1) };
        h.Dark();
        h.Dark();
        Assert.Equal(new[] { "use torch" }, h.Sent);
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
        Assert.Equal(new[] { "use lantern", "use lantern" }, h.Sent);
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
        Assert.Equal(new[] { "use lantern" }, h.Sent);
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
        Assert.Equal(new[] { "use torch" }, h.Sent);
    }

    [Fact]
    public void Reactive_DarkObserved_NoPreference_PicksStrongest()
    {
        // No preferred name → the strongest carried light (lantern 175 > torch 100).
        Harness h = new() { Snapshot = Snap(carried: new[] { "5 torch", "lantern" }) };
        h.Dark();
        Assert.Equal(new[] { "use lantern" }, h.Sent);
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
        Assert.Equal(new[] { "use torch" }, h.Sent);
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
        Assert.Equal(new[] { "use torch", "use torch" }, h.Sent);
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
        Assert.Equal(new[] { "use torch", "rem torch" }, h.Sent);
    }

    [Fact]
    public void RoomEntered_StillUnseeableRoom_KeepsLightLit()
    {
        // The entered room is itself unseeable on worn gear (worn 0 + room -300 is
        // well below the see threshold) → keep the auto-readied light, no `rem`.
        Harness h = new() { Snapshot = Snap(carried: new[] { "5 torch" }) };
        h.Dark();
        h.Enter(-300);
        Assert.Equal(new[] { "use torch" }, h.Sent);
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
    public void RoomEntered_Disabled_SendsNothing()
    {
        Harness h = new() { Enabled = false, Snapshot = Snap(carried: new[] { "5 torch" }) };
        h.Enter(0);
        Assert.Empty(h.Sent);
    }
}
