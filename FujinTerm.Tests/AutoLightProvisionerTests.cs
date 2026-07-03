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
/// <see cref="AutoLightProvisioner"/> — the active engine that turns the pure
/// <see cref="AutoLightPlanner"/> verdict into wire commands on a planned route.
/// Exercises the master-toggle gate, the ready / swap sends, the deferred buy,
/// and the pending latch that keeps a still-unconfirmed <c>use</c> from being
/// re-sent on a stale inventory snapshot.
/// </summary>
public sealed class AutoLightProvisionerTests
{
    // torch: 100 illu, 40-min burn; lantern: 175 illu, 2 h burn.
    private static readonly LightItem Torch = new(1, "torch", Strength: 100, UseCount: 800);
    private static readonly LightItem Lantern = new(2, "lantern", Strength: 175, UseCount: 2400);
    private static readonly IReadOnlyList<LightItem> Catalogue = new[] { Torch, Lantern };

    private static readonly RoomKey A = new(1, 100);

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
    public void DarkRoute_CoveringCarry_SendsUseNoRem()
    {
        // -300 room, worn 0 → need 150; carried lantern (175) covers, nothing lit.
        Harness h = new() { Snapshot = Snap(carried: new[] { "lantern" }) };
        h.Plan(A);
        Assert.Equal(new[] { "use lantern" }, h.Sent);
    }

    [Fact]
    public void DarkRoute_WeakLitLight_SwapsWithRemThenUse()
    {
        // Torch (100) lit but the -300 room needs 150 → put the torch away and
        // ready the carried lantern that covers. Reorder off so the dwindling
        // torch doesn't take the restock branch ahead of the swap.
        Harness h = new()
        {
            Settings = new() { CarryHours = 12, ReorderThresholdMinutes = 0 },
            Snapshot = Snap(carried: new[] { "lantern" }, readied: new ReadiedLight("torch", 60)),
        };
        h.Plan(A);
        Assert.Equal(new[] { "rem torch", "use lantern" }, h.Sent);
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
    public void PendingLatch_SameStaleSnapshot_DoesNotDoubleSend()
    {
        // Two walk-starts before an `i` dump confirms the readied light: the
        // second must not re-fire `use` on the same stale snapshot.
        Harness h = new() { Snapshot = Snap(carried: new[] { "lantern" }, stamp: 1) };
        h.Plan(A);
        h.Plan(A);
        Assert.Equal(new[] { "use lantern" }, h.Sent);
    }

    [Fact]
    public void PendingLatch_NewerDumpWithoutLight_Resends()
    {
        // First send readies the lantern; a newer dump lands still not showing it
        // (the `use` didn't take) → the latch retires and the engine retries.
        Harness h = new() { Snapshot = Snap(carried: new[] { "lantern" }, stamp: 1) };
        h.Plan(A);
        h.Snapshot = Snap(carried: new[] { "lantern" }, stamp: 2);
        h.Plan(A);
        Assert.Equal(new[] { "use lantern", "use lantern" }, h.Sent);
    }

    [Fact]
    public void PendingLatch_DumpConfirmsLight_ThenGuardHolds()
    {
        // First send readies the lantern; the next dump shows it lit and covering
        // → the planner's "already covers" guard returns nothing, no re-send.
        Harness h = new() { Snapshot = Snap(carried: new[] { "lantern" }, stamp: 1) };
        h.Plan(A);
        h.Snapshot = Snap(readied: new ReadiedLight("lantern", 200), stamp: 2);
        h.Plan(A);
        Assert.Equal(new[] { "use lantern" }, h.Sent);
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
}
