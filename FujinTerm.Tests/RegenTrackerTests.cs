using FujinTerm.Game;
using Xunit;

namespace FujinTerm.Tests;

public sealed class RegenTrackerTests
{
    /// <summary>
    /// Test-controllable clock that the tracker reads via the
    /// <see cref="RegenTracker(PlayerState, Func{DateTimeOffset}?)"/>
    /// constructor's clock hook. Tests advance it manually so the
    /// cycle's interval-vs-elapsed comparison sees realistic gaps.
    /// </summary>
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan delta) => Now += delta;
        public DateTimeOffset Read() => Now;
    }

    private static (PlayerState state, RegenTracker tracker, FakeClock clock) Setup()
    {
        PlayerState state = new();
        FakeClock clock = new();
        RegenTracker tracker = new(state, clock.Read);
        return (state, tracker, clock);
    }

    [Fact]
    public void FirstHpObservation_OnlySetsBaseline_NoSampleFired()
    {
        var (state, tracker, _) = Setup();
        int fires = 0;
        tracker.HpTickObserved += _ => fires++;

        state.Hp = 100;

        Assert.Equal(0, fires);
        Assert.Equal(0, tracker.HpNatural.Stat.SampleCount);
        Assert.False(tracker.HpNatural.IsActive);   // not anchored until first uptick.
        tracker.Dispose();
    }

    [Fact]
    public void HpUptick_AnchorsAndSamplesHpNatural()
    {
        var (state, tracker, clock) = Setup();
        state.Hp = 100;                            // baseline
        clock.Advance(TimeSpan.FromSeconds(30));   // a natural tick later…
        state.Hp = 105;                            // …+5 HP

        Assert.True(tracker.HpNatural.IsActive);
        Assert.Equal(1, tracker.HpNatural.Stat.SampleCount);
        Assert.True(tracker.HpNatural.Stat.EstimatedAmount > 0);
        Assert.False(tracker.HpRest.IsActive);     // not resting → bonus cycle stays off.
        tracker.Dispose();
    }

    [Fact]
    public void HpUptick_WhileResting_ClaimsHpRest()
    {
        var (state, tracker, clock) = Setup();
        state.Hp = 100;
        state.Position = PlayerPosition.Resting;   // anchors HpRest at now.
        clock.Advance(TimeSpan.FromSeconds(20));   // one rest cycle later.
        state.Hp = 108;

        Assert.Equal(1, tracker.HpRest.Stat.SampleCount);
        Assert.Equal(0, tracker.HpNatural.Stat.SampleCount);
        tracker.Dispose();
    }

    [Fact]
    public void LeavingResting_StopsHpRestCycle()
    {
        var (state, tracker, _) = Setup();
        state.Position = PlayerPosition.Resting;
        Assert.True(tracker.HpRest.IsActive);

        state.Position = PlayerPosition.Standing;
        Assert.False(tracker.HpRest.IsActive);
        tracker.Dispose();
    }

    [Fact]
    public void HpDecrease_NotASample()
    {
        var (state, tracker, clock) = Setup();
        state.Hp = 100;
        clock.Advance(TimeSpan.FromSeconds(30));
        state.Hp = 80;                             // took damage.

        Assert.Equal(0, tracker.HpNatural.Stat.SampleCount);
        tracker.Dispose();
    }

    [Fact]
    public void RecordArtifact_DropsNextHpIncreaseWithinGraceWindow()
    {
        var (state, tracker, clock) = Setup();
        state.Hp = 100;
        clock.Advance(TimeSpan.FromSeconds(30));
        tracker.RecordArtifact();
        clock.Advance(TimeSpan.FromSeconds(1));    // still inside the 3 s grace window.
        state.Hp = 130;                            // looks like a heal — should be dropped.

        Assert.Equal(0, tracker.HpNatural.Stat.SampleCount);
        tracker.Dispose();
    }

    [Fact]
    public void RecordArtifact_AfterGraceWindowExpires_SampleAccepted()
    {
        var (state, tracker, clock) = Setup();
        state.Hp = 100;
        clock.Advance(TimeSpan.FromSeconds(30));
        tracker.RecordArtifact();
        clock.Advance(TimeSpan.FromSeconds(10));   // well past the 3 s window.
        state.Hp = 103;

        Assert.Equal(1, tracker.HpNatural.Stat.SampleCount);
        tracker.Dispose();
    }

    [Fact]
    public void MaUptick_WhileMeditating_ClaimsMpMedi()
    {
        var (state, tracker, clock) = Setup();
        state.Ma = 50;
        state.Position = PlayerPosition.Meditating;
        clock.Advance(TimeSpan.FromSeconds(10));   // one medi cycle.
        state.Ma = 55;

        Assert.Equal(1, tracker.MpMedi.Stat.SampleCount);
        tracker.Dispose();
    }

    [Fact]
    public void MaUptick_StandingIdling_ClaimsMpNatural()
    {
        var (state, tracker, clock) = Setup();
        state.Ma = 50;
        clock.Advance(TimeSpan.FromSeconds(30));
        state.Ma = 51;

        Assert.Equal(1, tracker.MpNatural.Stat.SampleCount);
        Assert.Equal(0, tracker.MpMedi.Stat.SampleCount);
        tracker.Dispose();
    }

    [Fact]
    public void TimeToNextHpNaturalTick_NullBeforeFirstObservation()
    {
        var (_, tracker, _) = Setup();
        Assert.Null(tracker.GetTimeToNextHpNaturalTick());
        tracker.Dispose();
    }

    [Fact]
    public void TimeToNextHpRestTick_NullWhenNotResting()
    {
        var (_, tracker, _) = Setup();
        Assert.Null(tracker.GetTimeToNextHpRestTick());
        tracker.Dispose();
    }

    [Fact]
    public void TimeToNextHpRestTick_NonNullOncePositionEntersResting()
    {
        var (state, tracker, _) = Setup();
        state.Position = PlayerPosition.Resting;
        Assert.NotNull(tracker.GetTimeToNextHpRestTick());
        tracker.Dispose();
    }

    [Fact]
    public void ConfidenceTiers_LowMediumHigh()
    {
        RegenStat stat = new(TimeSpan.FromSeconds(30));
        Assert.Equal(RegenConfidence.Low, stat.Confidence);

        for (int i = 0; i < 3; i++) stat.AddSample(TimeSpan.FromSeconds(30), 4);
        Assert.Equal(RegenConfidence.Medium, stat.Confidence);

        for (int i = 0; i < 10; i++) stat.AddSample(TimeSpan.FromSeconds(30), 4);
        Assert.Equal(RegenConfidence.High, stat.Confidence);
    }

    [Fact]
    public void OutlierInterval_IsDropped()
    {
        RegenStat stat = new(TimeSpan.FromSeconds(30));
        stat.AddSample(TimeSpan.FromSeconds(5), 4);    // round(5/30) = 0 cycles → drop.
        stat.AddSample(TimeSpan.FromSeconds(180), 4);  // round(180/30) = 6 cycles → drop.
        Assert.Equal(0, stat.SampleCount);

        stat.AddSample(TimeSpan.FromSeconds(30), 4);   // 1 cycle → accept.
        Assert.Equal(1, stat.SampleCount);
    }

    [Fact]
    public void Reset_ClearsSampleCountAndAmount_KeepsSeedInterval()
    {
        RegenStat stat = new(TimeSpan.FromSeconds(20));
        stat.AddSample(TimeSpan.FromSeconds(22), 6);
        Assert.Equal(1, stat.SampleCount);

        stat.Reset();
        Assert.Equal(0, stat.SampleCount);
        Assert.Equal(TimeSpan.FromSeconds(20), stat.EstimatedInterval);
        Assert.Equal(0, stat.EstimatedAmount);
    }

    [Fact]
    public void SeedIntervalsMatchMmudExplorerValues()
    {
        // Pin the documented constants from MMUD-Explorer's modExpPerHour.bas
        // so a stray refactor doesn't silently re-tune them.
        Assert.Equal(TimeSpan.FromSeconds(30), RegenConstants.SeedStandingInterval);
        Assert.Equal(TimeSpan.FromSeconds(20), RegenConstants.SeedRestingInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), RegenConstants.SeedMeditatingInterval);
    }
}
