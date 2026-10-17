using System;
using System.Collections.Generic;
using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// RouteEtaEstimator projects arrival time over a walk's REMAINING route: pure
// per-hop travel, plus — only when auto-combat is on — a fixed dwell for every
// lair room the walker steps INTO. These tests pin the hop math, the
// lair-dwell gate, the monster-count parse (with the default-to-one fallback),
// and the "skip the room we're standing in" rule.
public sealed class RouteEtaEstimatorTests
{
    private static Room Plain(int room, string name = "room") =>
        new() { Key = new RoomKey(1, room), Name = name, Exits = RoomExitSet.Empty };

    private static Room Lair(int room, string lairTag, string name = "lair") =>
        new() { Key = new RoomKey(1, room), Name = name, RawLairTag = lairTag, Exits = RoomExitSet.Empty };

    // 1 s/hop flat model keeps the travel arithmetic trivial so assertions
    // isolate the lair-dwell accounting.
    private static ITravelCostModel Flat() => new FlatTravelCostModel(1.0);

    private static Func<RoomKey, Room?> Lookup(params Room[] rooms)
    {
        Dictionary<RoomKey, Room> map = new();
        foreach (Room r in rooms) map[r.Key] = r;
        return key => map.TryGetValue(key, out Room? r) ? r : null;
    }

    [Fact]
    public void FewerThanTwoRooms_ReturnsZero()
    {
        Assert.Equal(TimeSpan.Zero, RouteEtaEstimator.Estimate(
            Array.Empty<RoomKey>(), Flat(), _ => null, includeLairDwell: true));
        Assert.Equal(TimeSpan.Zero, RouteEtaEstimator.Estimate(
            new[] { new RoomKey(1, 1) }, Flat(), _ => null, includeLairDwell: true));
    }

    [Fact]
    public void NullRooms_ReturnsZero()
    {
        Assert.Equal(TimeSpan.Zero, RouteEtaEstimator.Estimate(
            null!, Flat(), _ => null, includeLairDwell: true));
    }

    [Fact]
    public void PureTravel_NoLairDwellWhenDisabled()
    {
        // 4 rooms → 3 hops → 3 s. Lair rooms are present but includeLairDwell is
        // false, so the ETA stays pure travel time.
        RoomKey[] rooms = { new(1, 1), new(1, 2), new(1, 3), new(1, 4) };
        Func<RoomKey, Room?> lookup = Lookup(
            Plain(1), Lair(2, "(Max 3): 100"), Lair(3, "(Max 2): 200"), Plain(4));
        Assert.Equal(TimeSpan.FromSeconds(3), RouteEtaEstimator.Estimate(
            rooms, Flat(), lookup, includeLairDwell: false));
    }

    [Fact]
    public void LairDwell_AddsPerMonsterForEachLairSteppedInto()
    {
        // 3 hops = 3 s travel. Room 2 is a Max-3 lair (3 × 5 = 15 s), room 3 a
        // Max-2 lair (2 × 5 = 10 s). Total 3 + 15 + 10 = 28 s.
        RoomKey[] rooms = { new(1, 1), new(1, 2), new(1, 3), new(1, 4) };
        Func<RoomKey, Room?> lookup = Lookup(
            Plain(1), Lair(2, "(Max 3): 100"), Lair(3, "(Max 2): 200"), Plain(4));
        Assert.Equal(TimeSpan.FromSeconds(28), RouteEtaEstimator.Estimate(
            rooms, Flat(), lookup, includeLairDwell: true));
    }

    [Fact]
    public void CurrentRoomLair_NotCounted()
    {
        // The room at index 0 is the one the walker is standing in — its lair is
        // being left, not entered, so it must not add dwell. 1 hop = 1 s only.
        RoomKey[] rooms = { new(1, 1), new(1, 2) };
        Func<RoomKey, Room?> lookup = Lookup(Lair(1, "(Max 5): 100"), Plain(2));
        Assert.Equal(TimeSpan.FromSeconds(1), RouteEtaEstimator.Estimate(
            rooms, Flat(), lookup, includeLairDwell: true));
    }

    [Fact]
    public void UnparseableLairMax_DefaultsToOneMonster()
    {
        // A lair tag without a "(Max N)" token still counts as a lair (HasLair
        // keys on RawLairTag being non-empty); the dwell falls back to one
        // monster. 1 hop = 1 s + 1 × 5 = 6 s.
        RoomKey[] rooms = { new(1, 1), new(1, 2) };
        Func<RoomKey, Room?> lookup = Lookup(Plain(1), Lair(2, "some lair, no max token"));
        Assert.Equal(TimeSpan.FromSeconds(6), RouteEtaEstimator.Estimate(
            rooms, Flat(), lookup, includeLairDwell: true));
    }

    [Fact]
    public void ZeroLairMax_DefaultsToOneMonster()
    {
        // "(Max 0)" parses to 0 — not a positive count — so the dwell floors at
        // one monster rather than zeroing out. 1 hop = 1 s + 1 × 5 = 6 s.
        RoomKey[] rooms = { new(1, 1), new(1, 2) };
        Func<RoomKey, Room?> lookup = Lookup(Plain(1), Lair(2, "(Max 0): 100"));
        Assert.Equal(TimeSpan.FromSeconds(6), RouteEtaEstimator.Estimate(
            rooms, Flat(), lookup, includeLairDwell: true));
    }

    [Fact]
    public void MissingRoomInLookup_TreatedAsNonLair()
    {
        // A room the graph can't resolve (lookup returns null) adds no dwell —
        // the walk still counts its hop. 2 hops = 2 s, no lair extension.
        RoomKey[] rooms = { new(1, 1), new(1, 99), new(1, 2) };
        Func<RoomKey, Room?> lookup = Lookup(Plain(1), Plain(2));
        Assert.Equal(TimeSpan.FromSeconds(2), RouteEtaEstimator.Estimate(
            rooms, Flat(), lookup, includeLairDwell: true));
    }

    [Fact]
    public void FriendlyLairs_AddNoDwell_WhenPredicateSaysWontFight()
    {
        // Both lair rooms hold occupants the walker won't fight (friendly town
        // guardsmen), so the ETA charges no combat dwell — killing the phantom
        // over-estimate on hostile-free routes (report paradigm-20260909-004947).
        // 3 hops = 3 s only.
        RoomKey[] rooms = { new(1, 1), new(1, 2), new(1, 3), new(1, 4) };
        Func<RoomKey, Room?> lookup = Lookup(
            Plain(1), Lair(2, "(Max 3): 100"), Lair(3, "(Max 2): 200"), Plain(4));
        Assert.Equal(TimeSpan.FromSeconds(3), RouteEtaEstimator.Estimate(
            rooms, Flat(), lookup, includeLairDwell: true, lairWillBeFought: _ => false));
    }

    [Fact]
    public void MixedLairs_OnlyFoughtOnesCountDwell()
    {
        // Room 2's lair will be fought (3 × 5 = 15 s); room 3's is friendly (0).
        // 3 hops + 15 = 18 s.
        RoomKey[] rooms = { new(1, 1), new(1, 2), new(1, 3), new(1, 4) };
        Func<RoomKey, Room?> lookup = Lookup(
            Plain(1), Lair(2, "(Max 3): 100"), Lair(3, "(Max 2): 200"), Plain(4));
        Assert.Equal(TimeSpan.FromSeconds(18), RouteEtaEstimator.Estimate(
            rooms, Flat(), lookup, includeLairDwell: true,
            lairWillBeFought: r => r.Key.Room == 2));
    }

}
