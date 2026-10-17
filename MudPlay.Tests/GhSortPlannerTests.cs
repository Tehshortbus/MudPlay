using System.Collections.Generic;
using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// Pure decision-logic tests for the Roomba sort-trip planner: fill the pack to
// capacity (nearest fitting source) before delivering (nearest carried dest).
public sealed class GhSortPlannerTests
{
    private static RoomKey R(int room) => new(1, room);

    private static Dictionary<RoomKey, int> Dist(params (int room, int d)[] entries)
    {
        Dictionary<RoomKey, int> m = new();
        foreach ((int room, int d) in entries) m[R(room)] = d;
        return m;
    }

    private static GhSortPlanner.PickupRoom Pick(int room, int weight) => new(R(room), weight);

    private static GhSortPlanner.CarriedLoad Load(int room, int weight) => new(R(room), weight);

    [Fact]
    public void EmptyHands_RoutesToNearestFittingSource()
    {
        RoomKey? next = GhSortPlanner.NextTarget(
            carried: System.Array.Empty<GhSortPlanner.CarriedLoad>(),
            pickups: new[] { Pick(2, 10), Pick(3, 10) },
            headroom: 100,
            distancesFromHere: Dist((2, 5), (3, 2)));
        Assert.Equal(R(3), next);
    }

    [Fact]
    public void CarryingButSourceStillFits_KeepsFilling_DoesNotDeliverEarly()
    {
        // Carrying an item bound for room 9; a source at room 2 still fits headroom.
        // Even though room 9 is NEARER, the planner keeps filling the pack first —
        // this is the trip-cutting behavior the old "deliver immediately" router lacked.
        RoomKey? next = GhSortPlanner.NextTarget(
            carried: new[] { Load(9, 1) },
            pickups: new[] { Pick(2, 10) },
            headroom: 50,
            distancesFromHere: Dist((2, 4), (9, 1)));
        Assert.Equal(R(2), next);
    }

    [Fact]
    public void PackFull_NoSourceFits_DeliversNearestCarriedDestination()
    {
        RoomKey? next = GhSortPlanner.NextTarget(
            carried: new[] { Load(8, 1), Load(9, 1) },
            pickups: new[] { Pick(2, 40) },   // 40 > headroom 5, won't fit now
            headroom: 5,
            distancesFromHere: Dist((2, 3), (8, 6), (9, 2)));
        Assert.Equal(R(9), next);            // nearest carried destination
    }

    [Fact]
    public void SourceTooHeavyForNow_ButCarrying_DeliversToFreeHeadroom()
    {
        RoomKey? next = GhSortPlanner.NextTarget(
            carried: new[] { Load(9, 1) },
            pickups: new[] { Pick(2, 200) },  // won't fit at current headroom
            headroom: 30,
            distancesFromHere: Dist((2, 1), (9, 5)));
        Assert.Equal(R(9), next);            // deliver first; the heavy source waits
    }

    [Fact]
    public void NothingCarried_NothingFitsRightNow_ReturnsNull()
    {
        RoomKey? next = GhSortPlanner.NextTarget(
            carried: System.Array.Empty<GhSortPlanner.CarriedLoad>(),
            pickups: new[] { Pick(2, 200) },
            headroom: 30,
            distancesFromHere: Dist((2, 1)));
        Assert.Null(next);
    }

    [Fact]
    public void SkipsUnreachableSource()
    {
        RoomKey? next = GhSortPlanner.NextTarget(
            carried: System.Array.Empty<GhSortPlanner.CarriedLoad>(),
            pickups: new[] { Pick(2, 10), Pick(3, 10) },
            headroom: 100,
            distancesFromHere: Dist((3, 4)));  // room 2 absent = unreachable
        Assert.Equal(R(3), next);
    }

    [Fact]
    public void TieBreak_ByMapThenRoom()
    {
        RoomKey? next = GhSortPlanner.NextTarget(
            carried: System.Array.Empty<GhSortPlanner.CarriedLoad>(),
            pickups: new[] { Pick(5, 10), Pick(2, 10) },
            headroom: 100,
            distancesFromHere: Dist((5, 3), (2, 3)));   // equal distance
        Assert.Equal(R(2), next);            // lower room number wins
    }

    [Fact]
    public void SplitIntoTrips_ChunksWithRemainderLast()
    {
        // 140 torches, 77 fit per trip → 77 + 63 across two trips (not stranded).
        Assert.Equal(new[] { 77, 63 }, GhSortPlanner.SplitIntoTrips(140, 77));
    }

    [Fact]
    public void SplitIntoTrips_ExactMultiple_HasNoRemainder()
    {
        Assert.Equal(new[] { 3, 3, 3 }, GhSortPlanner.SplitIntoTrips(9, 3));
    }

    [Fact]
    public void SplitIntoTrips_FitsOneTrip_IsSingleLoad()
    {
        Assert.Equal(new[] { 5 }, GhSortPlanner.SplitIntoTrips(5, 10));
    }

    [Fact]
    public void SplitIntoTrips_NonPositive_IsEmpty()
    {
        Assert.Empty(GhSortPlanner.SplitIntoTrips(0, 10));
        Assert.Empty(GhSortPlanner.SplitIntoTrips(10, 0));
    }

    [Fact]
    public void FullRoom_IsDrainedFirst_EvenWhenFarther()
    {
        // The foreign items sitting in a full room are the only things whose
        // removal frees its capacity, so relieving it beats a nearer source that
        // isn't blocking anything.
        RoomKey? next = GhSortPlanner.NextTarget(
            carried: System.Array.Empty<GhSortPlanner.CarriedLoad>(),
            pickups: new[] { Pick(2, 10), Pick(7, 10) },
            headroom: 100,
            distancesFromHere: Dist((2, 1), (7, 9)),
            full: new HashSet<RoomKey> { R(7) });
        Assert.Equal(R(7), next);
    }

    [Fact]
    public void FullRoomWithNothingToPullOut_DoesNotDivertTheTrip()
    {
        // Full but no pending pickups there — nothing to drain, so the ordinary
        // nearest-source rule stands.
        RoomKey? next = GhSortPlanner.NextTarget(
            carried: System.Array.Empty<GhSortPlanner.CarriedLoad>(),
            pickups: new[] { Pick(2, 10) },
            headroom: 100,
            distancesFromHere: Dist((2, 4), (7, 1)),
            full: new HashSet<RoomKey> { R(7) });
        Assert.Equal(R(2), next);
    }

    [Fact]
    public void FullRoomPickupTooHeavy_FallsBackToTheOrdinaryRule()
    {
        // Relief only jumps the queue for something that actually fits the pack.
        RoomKey? next = GhSortPlanner.NextTarget(
            carried: System.Array.Empty<GhSortPlanner.CarriedLoad>(),
            pickups: new[] { Pick(2, 10), Pick(7, 500) },
            headroom: 100,
            distancesFromHere: Dist((2, 4), (7, 1)),
            full: new HashSet<RoomKey> { R(7) });
        Assert.Equal(R(2), next);
    }

    [Fact]
    public void ShouldUnload_EntersAt80Percent_AndHoldsUntilBelow40()
    {
        // Hysteresis, not a threshold. A single line leaves the pack sitting AT
        // the limit alternating deliver-one / collect-one, which is the
        // 14-hops-per-item shuttle this exists to stop.
        Assert.False(GhSortPlanner.ShouldUnload(false, carriedWeight: 79, workingBudget: 100));
        Assert.True(GhSortPlanner.ShouldUnload(false, carriedWeight: 80, workingBudget: 100));

        // Once unloading, keep going through the middle band rather than
        // resuming collection the moment we dip under 80.
        Assert.True(GhSortPlanner.ShouldUnload(true, carriedWeight: 79, workingBudget: 100));
        Assert.True(GhSortPlanner.ShouldUnload(true, carriedWeight: 41, workingBudget: 100));
        Assert.False(GhSortPlanner.ShouldUnload(true, carriedWeight: 40, workingBudget: 100));
    }

    [Fact]
    public void ShouldUnload_WithNoWeightData_NeverEngages()
    {
        // No Encum data → no cap → the whole weight model is off.
        Assert.False(GhSortPlanner.ShouldUnload(false, carriedWeight: 500, workingBudget: int.MaxValue));
        Assert.False(GhSortPlanner.ShouldUnload(true, carriedWeight: 500, workingBudget: 0));
    }

    [Fact]
    public void Unloading_IgnoresSourcesAndHeadsForTheHeaviestStop()
    {
        // A light pickup is one hop away and would "fit", but topping up here is
        // exactly what pinned the pack at the limit. Deliver instead — and to the
        // stop that sheds the most, not the nearest.
        RoomKey? next = GhSortPlanner.NextTarget(
            carried: new[] { Load(8, 10), Load(9, 90) },
            pickups: new[] { Pick(2, 1) },
            headroom: 5,
            distancesFromHere: Dist((2, 1), (8, 2), (9, 6)),
            full: null,
            unloading: true);
        Assert.Equal(R(9), next);
    }

    [Fact]
    public void Unloading_EquallyLadenStops_TakesTheNearer()
    {
        RoomKey? next = GhSortPlanner.NextTarget(
            carried: new[] { Load(8, 50), Load(9, 50) },
            pickups: System.Array.Empty<GhSortPlanner.PickupRoom>(),
            headroom: 0,
            distancesFromHere: Dist((8, 7), (9, 2)),
            full: null,
            unloading: true);
        Assert.Equal(R(9), next);
    }

    [Fact]
    public void NotUnloading_StillFillsBeforeDelivering()
    {
        // The ordinary rule is untouched below the entry threshold.
        RoomKey? next = GhSortPlanner.NextTarget(
            carried: new[] { Load(9, 90) },
            pickups: new[] { Pick(2, 1) },
            headroom: 5,
            distancesFromHere: Dist((2, 4), (9, 1)),
            full: null,
            unloading: false);
        Assert.Equal(R(2), next);
    }

    [Fact]
    public void NothingFitsAndNothingCarried_HasNowhereToGo()
    {
        // The caller ends the sweep on null. With a pack too full to collect and
        // nothing owed, there is genuinely no next room — better than shuttling
        // one item per round trip, which is what a barely-viable budget produces.
        RoomKey? next = GhSortPlanner.NextTarget(
            carried: System.Array.Empty<GhSortPlanner.CarriedLoad>(),
            pickups: System.Array.Empty<GhSortPlanner.PickupRoom>(),
            headroom: 5,
            distancesFromHere: Dist((2, 1)));
        Assert.Null(next);
    }

    [Fact]
    public void NoPickupsButStillCarrying_DeliversBeforeGivingUp()
    {
        // A tight pack stops collecting but must still deliver what it holds —
        // stranding a carried load would hand the player the exact mess the
        // suspended-sweep record exists to avoid.
        RoomKey? next = GhSortPlanner.NextTarget(
            carried: new[] { Load(9, 200) },
            pickups: System.Array.Empty<GhSortPlanner.PickupRoom>(),
            headroom: 5,
            distancesFromHere: Dist((9, 3)));
        Assert.Equal(R(9), next);
    }
}
