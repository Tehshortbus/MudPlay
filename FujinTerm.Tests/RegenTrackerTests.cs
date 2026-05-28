using FujinTerm.Game;
using Xunit;

namespace FujinTerm.Tests;

public sealed class RegenTrackerTests
{
    /// <summary>
    /// Test-controllable clock that the tracker reads via the
    /// <see cref="RegenTracker(PlayerState, Func{DateTimeOffset}?)"/>
    /// constructor's clock hook. Tests advance it manually so the
    /// per-position outlier filter sees realistic intervals.
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
        Assert.Equal(0, tracker.HpStanding.SampleCount);
        tracker.Dispose();
    }

    [Fact]
    public void HpIncrease_WhileStanding_AccumulatesIntoStandingStat()
    {
        var (state, tracker, clock) = Setup();
        state.Position = PlayerPosition.Standing;
        state.Hp = 100;                            // baseline
        clock.Advance(TimeSpan.FromSeconds(30));   // a tick later…
        state.Hp = 105;                            // …+5 HP

        Assert.Equal(1, tracker.HpStanding.SampleCount);
        Assert.Equal(0, tracker.HpResting.SampleCount);
        Assert.True(tracker.HpStanding.EstimatedAmount > 0);
        tracker.Dispose();
    }

    [Fact]
    public void HpIncrease_WhileResting_AccumulatesIntoRestingStat()
    {
        var (state, tracker, clock) = Setup();
        state.Position = PlayerPosition.Resting;
        state.Hp = 100;
        clock.Advance(TimeSpan.FromSeconds(20));
        state.Hp = 108;

        Assert.Equal(1, tracker.HpResting.SampleCount);
        Assert.Equal(0, tracker.HpStanding.SampleCount);
        tracker.Dispose();
    }

    [Fact]
    public void HpDecrease_NotASample()
    {
        var (state, tracker, clock) = Setup();
        state.Hp = 100;
        clock.Advance(TimeSpan.FromSeconds(30));
        state.Hp = 80;                             // took damage

        Assert.Equal(0, tracker.HpStanding.SampleCount);
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

        Assert.Equal(0, tracker.HpStanding.SampleCount);
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

        Assert.Equal(1, tracker.HpStanding.SampleCount);
        tracker.Dispose();
    }

    [Fact]
    public void MaIncrease_WhileMeditating_AccumulatesIntoMaMeditating()
    {
        var (state, tracker, clock) = Setup();
        state.Position = PlayerPosition.Meditating;
        state.Ma = 50;
        clock.Advance(TimeSpan.FromSeconds(10));
        state.Ma = 55;

        Assert.Equal(1, tracker.MaMeditating.SampleCount);
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
        stat.AddSample(TimeSpan.FromSeconds(5), 4);    // too fast → drop
        stat.AddSample(TimeSpan.FromSeconds(180), 4);  // too slow → drop
        Assert.Equal(0, stat.SampleCount);

        stat.AddSample(TimeSpan.FromSeconds(30), 4);   // in band → accept
        Assert.Equal(1, stat.SampleCount);
    }

    [Fact]
    public void Reset_ReturnsToSeed()
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
