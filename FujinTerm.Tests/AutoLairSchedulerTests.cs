using FujinTerm.Game.Map;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 7.19 — AutoLairScheduler.PickNext is the deterministic target
/// picker. Pure function over <see cref="LairCandidate"/>s + a travel
/// model — no graph dependency, no clock, no global state. The tests
/// pin down the scoring contract: default heuristic balances wasted-
/// respawn vs idle-wait under idlePenalty; throughput ignores idle.
/// </summary>
public sealed class AutoLairSchedulerTests
{
    private static readonly DateTimeOffset _t0 =
        new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly ITravelCostModel _flat = new FlatTravelCostModel(secondsPerHop: 1.0);

    private static LairCandidate Reachable(
        int map, int room,
        DateTimeOffset? readyAt,
        int hops,
        int waitRoomNum = 999)
        => new(new RoomKey(map, room), readyAt, hops, new RoomKey(map, waitRoomNum));

    // ----- empty / unreachable --------------------------------------

    [Fact]
    public void PickNext_EmptyList_ReturnsNull()
    {
        Assert.Null(AutoLairScheduler.PickNext(
            Array.Empty<LairCandidate>(), _flat, now: _t0));
    }

    [Fact]
    public void PickNext_AllUnreachable_ReturnsNull()
    {
        // Hops=null OR WaitRoom=null both render a candidate
        // unschedulable; PickNext skips both shapes.
        LairCandidate[] cands =
        {
            new(new RoomKey(1, 1), null, ApproachHops: null,   WaitRoom: new RoomKey(1, 2)),
            new(new RoomKey(1, 3), null, ApproachHops: 5,      WaitRoom: null),
        };
        Assert.Null(AutoLairScheduler.PickNext(cands, _flat, now: _t0));
    }

    // ----- default heuristic ----------------------------------------

    [Fact]
    public void PickNext_DefaultHeuristic_PrefersCloserReadyLairOverFarReadyLair()
    {
        // Both ready now. The closer one wins because it minimises
        // wasted-respawn time.
        LairCandidate[] cands =
        {
            Reachable(1, 100, readyAt: _t0,                       hops: 10),  // wasted=11s
            Reachable(1, 200, readyAt: _t0,                       hops:  3),  // wasted= 4s
        };

        LairDecision? best = AutoLairScheduler.PickNext(cands, _flat, now: _t0);

        Assert.NotNull(best);
        Assert.Equal(new RoomKey(1, 200), best!.Lair);
        Assert.Equal(4.0, best.Score, precision: 3);
        Assert.True(best.SlackAtEntry > TimeSpan.Zero);
    }

    [Fact]
    public void PickNext_DefaultHeuristic_PrefersReadySoonerOverIdleWait()
    {
        // Lair A: ready 100s from now, 2 hops (entryArrival = 3s). Slack
        // = 3 - 100 = -97s idle. Score = 97.
        // Lair B: ready in 5s, 10 hops (entryArrival = 11s). Slack =
        // 11 - 5 = 6s wasted. Score = 6.
        // Idle-penalised default → B wins.
        LairCandidate[] cands =
        {
            Reachable(1, 100, readyAt: _t0.AddSeconds(100), hops:  2),
            Reachable(1, 200, readyAt: _t0.AddSeconds(  5), hops: 10),
        };

        LairDecision? best = AutoLairScheduler.PickNext(cands, _flat, now: _t0);

        Assert.NotNull(best);
        Assert.Equal(new RoomKey(1, 200), best!.Lair);
    }

    [Fact]
    public void PickNext_DefaultHeuristic_IdlePenaltyZero_PrefersIdleOverWasted()
    {
        // With idlePenalty=0 the default reduces to throughput. The
        // candidate with idle (negative slack) scores 0 and beats any
        // candidate with even small wasted respawn.
        LairCandidate[] cands =
        {
            Reachable(1, 100, readyAt: _t0.AddSeconds(100), hops:  2),  // idle=97s, score=0
            Reachable(1, 200, readyAt: _t0,                 hops:  3),  // wasted=4s, score=4
        };

        LairDecision? best = AutoLairScheduler.PickNext(
            cands, _flat, AutoLairHeuristic.Default, idlePenalty: 0.0, now: _t0);

        Assert.NotNull(best);
        Assert.Equal(new RoomKey(1, 100), best!.Lair);
        Assert.Equal(0.0, best.Score, precision: 3);
    }

    // ----- throughput heuristic -------------------------------------

    [Fact]
    public void PickNext_ThroughputHeuristic_PrefersIdleOverWasted()
    {
        // Same fixture as the default-zero-penalty test — throughput
        // explicitly ignores idle wait. Idle candidate wins.
        LairCandidate[] cands =
        {
            Reachable(1, 100, readyAt: _t0.AddSeconds(100), hops:  2),  // idle=97s, score=0
            Reachable(1, 200, readyAt: _t0,                 hops:  3),  // wasted=4s, score=4
        };

        LairDecision? best = AutoLairScheduler.PickNext(
            cands, _flat, AutoLairHeuristic.Throughput, now: _t0);

        Assert.NotNull(best);
        Assert.Equal(new RoomKey(1, 100), best!.Lair);
        Assert.Equal(0.0, best.Score, precision: 3);
    }

    // ----- ready-at semantics ---------------------------------------

    [Fact]
    public void PickNext_NullReadyAt_TreatedAsReadyNow()
    {
        // Never-entered lair has ReadyAt = null → scheduler treats as
        // ready now → slack = entryArrival - now > 0 (wasted).
        LairCandidate cand = Reachable(1, 100, readyAt: null, hops: 5);

        LairDecision? best = AutoLairScheduler.PickNext(
            new[] { cand }, _flat, now: _t0);

        Assert.NotNull(best);
        Assert.True(best!.SlackAtEntry > TimeSpan.Zero,
            "ReadyAt=null should give positive slack (wasted respawn).");
    }

    [Fact]
    public void PickNext_ExactlyOnTime_SlackZero_WinsOverPositiveAndNegative()
    {
        // Perfect timing — slack=0 → score=0 → beats anything else.
        // entryArrival = _t0 + 3 hops × 1s + 1s entry = _t0 + 4s.
        LairCandidate[] cands =
        {
            Reachable(1, 100, readyAt: _t0.AddSeconds( 4), hops: 3),  // perfect
            Reachable(1, 200, readyAt: _t0.AddSeconds(10), hops: 3),  // 6s idle
            Reachable(1, 300, readyAt: _t0,                hops: 3),  // 4s wasted
        };

        LairDecision? best = AutoLairScheduler.PickNext(cands, _flat, now: _t0);

        Assert.NotNull(best);
        Assert.Equal(new RoomKey(1, 100), best!.Lair);
        Assert.Equal(0.0, best.Score, precision: 3);
    }

    // ----- decision payload -----------------------------------------

    [Fact]
    public void PickNext_DecisionCarriesWaitRoomAndApproachDuration()
    {
        LairCandidate cand = Reachable(7, 50, readyAt: _t0.AddSeconds(60), hops: 4, waitRoomNum: 49);

        LairDecision? best = AutoLairScheduler.PickNext(
            new[] { cand }, _flat, now: _t0);

        Assert.NotNull(best);
        Assert.Equal(new RoomKey(7, 50), best!.Lair);
        Assert.Equal(new RoomKey(7, 49), best.WaitRoom);
        Assert.Equal(TimeSpan.FromSeconds(4), best.ApproachDuration);
        // entryArrival = _t0 + 4s (approach) + 1s (entry hop) = _t0 + 5s.
        Assert.Equal(_t0.AddSeconds(5), best.EntryArrival);
        // slack = _t0+5 - (_t0+60) = -55s idle.
        Assert.Equal(TimeSpan.FromSeconds(-55), best.SlackAtEntry);
    }

    [Fact]
    public void PickNext_IdlePenaltyAboveOne_PunishesIdleMore()
    {
        // idle=10s vs wasted=5s at idlePenalty=2 → score(idle)=20, score(wasted)=5.
        // Wasted candidate wins.
        LairCandidate[] cands =
        {
            Reachable(1, 100, readyAt: _t0.AddSeconds(20), hops: 4),  // idle=15s under penalty 2 = score 30
            Reachable(1, 200, readyAt: _t0.AddSeconds(2),  hops: 4),  // wasted=3s = score 3
        };

        LairDecision? best = AutoLairScheduler.PickNext(
            cands, _flat, AutoLairHeuristic.Default, idlePenalty: 2.0, now: _t0);

        Assert.NotNull(best);
        Assert.Equal(new RoomKey(1, 200), best!.Lair);
    }
}
