using FujinTerm.Game.Combat;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Phase 11 — <see cref="SessionActivityTracker"/> kill / exp counters and the
/// kills/hour running-average series. An injected clock makes the time arithmetic
/// deterministic: kills and exp are pushed via the <c>Note*</c> forwarders and
/// time advanced by hand, so each test pins exactly how a counter or the running
/// sparkline rate is derived.
/// </summary>
public sealed class SessionActivityTrackerTests
{
    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public void Advance(double minutes) => Now += TimeSpan.FromMinutes(minutes);
    }

    private static (SessionActivityTracker, Clock) Make()
    {
        Clock c = new();
        return (new SessionActivityTracker(() => c.Now), c);
    }

    // ----- fresh / counters --------------------------------------------

    [Fact]
    public void Fresh_AllZero()
    {
        (SessionActivityTracker t, _) = Make();
        SessionActivityStats s = t.Snapshot();
        Assert.Equal(0, s.MonstersKilled);
        Assert.Equal(0L, s.ExperienceEarned);
        Assert.Equal(TimeSpan.Zero, s.TimeOnline);
        Assert.Equal(0d, s.KillsPerHour);
    }

    [Fact]
    public void NoteKill_IncrementsCount()
    {
        (SessionActivityTracker t, _) = Make();
        t.NoteKill();
        t.NoteKill();
        t.NoteKill();
        Assert.Equal(3, t.Snapshot().MonstersKilled);
    }

    [Fact]
    public void NoteExperience_Sums_AndIgnoresNonPositive()
    {
        (SessionActivityTracker t, _) = Make();
        t.NoteExperience(1200);
        t.NoteExperience(800);
        t.NoteExperience(0);    // ignored
        t.NoteExperience(-50);  // ignored
        Assert.Equal(2000L, t.Snapshot().ExperienceEarned);
    }

    [Fact]
    public void NoteCurrency_SumsCollectedAndStashed_AndIgnoresNonPositive()
    {
        (SessionActivityTracker t, _) = Make();
        t.NoteCurrencyCollected(1500);
        t.NoteCurrencyCollected(500);
        t.NoteCurrencyCollected(0);    // ignored
        t.NoteCurrencyCollected(-10);  // ignored
        t.NoteCurrencyStashed(1200);
        t.NoteCurrencyStashed(0);      // ignored

        SessionActivityStats s = t.Snapshot();
        Assert.Equal(2000L, s.CurrencyCollected);
        Assert.Equal(1200L, s.CurrencyStashed);
    }

    // ----- derived rates -----------------------------------------------

    [Fact]
    public void KillsPerHour_DerivesFromTimeOnline()
    {
        (SessionActivityTracker t, Clock c) = Make();
        for (int i = 0; i < 6; i++) t.NoteKill();
        c.Advance(30); // half an hour

        SessionActivityStats s = t.Snapshot();
        Assert.Equal(TimeSpan.FromMinutes(30), s.TimeOnline);
        Assert.Equal(12d, s.KillsPerHour, 3); // 6 kills / 0.5 h
    }

    [Fact]
    public void ExperiencePerHour_DerivesFromTimeOnline()
    {
        (SessionActivityTracker t, Clock c) = Make();
        t.NoteExperience(16_200);
        c.Advance(30);

        Assert.Equal(32_400d, t.Snapshot().ExperiencePerHour, 3); // 16,200 / 0.5 h
    }

    [Fact]
    public void CurrencyPerHour_DerivesFromTimeOnline()
    {
        (SessionActivityTracker t, Clock c) = Make();
        t.NoteCurrencyCollected(9000);
        c.Advance(30);

        Assert.Equal(18_000d, t.Snapshot().CurrencyPerHour, 3); // 9,000 / 0.5 h
    }

    // ----- kills/hour series -------------------------------------------

    [Fact]
    public void KillsPerHourSeries_IsRunningRate_EndingAtTheHeadlineFigure()
    {
        (SessionActivityTracker t, Clock c) = Make();
        // 60-min session, 6 buckets ⇒ 10 min each. Kills land in bucket 1 and 4.
        c.Advance(15); t.NoteKill();  // t=15 ⇒ bucket 1 (10–20)
        c.Advance(30); t.NoteKill();  // t=45 ⇒ bucket 4 (40–50)
        c.Advance(15);                // t=60, snapshot edge

        IReadOnlyList<double> series = t.KillsPerHourSeries(6);
        Assert.Equal(6, series.Count);
        // Each point is the CUMULATIVE rate at that slice end: kills so far ÷
        // hours so far (slice end = (i+1)/6 h).
        Assert.Equal(0d,   series[0], 3); // 0 / (1/6 h)
        Assert.Equal(3d,   series[1], 3); // 1 / (2/6 h)
        Assert.Equal(2d,   series[2], 3); // 1 / (3/6 h)
        Assert.Equal(1.5d, series[3], 3); // 1 / (4/6 h)
        Assert.Equal(2.4d, series[4], 3); // 2 / (5/6 h)
        Assert.Equal(2d,   series[5], 3); // 2 / (6/6 h)
        // The right-most point is the same kills/hour the panel prints.
        Assert.Equal(t.Snapshot().KillsPerHour, series[^1], 3);
    }

    [Fact]
    public void KillsPerHourSeries_PrunesBeyondWindow_ButTotalKeepsTheKill()
    {
        (SessionActivityTracker t, Clock c) = Make();
        t.NoteKill();        // t=0
        c.Advance(70);       // 70 min later — the kill is outside the 60-min window

        // Total still counts the old kill...
        Assert.Equal(1, t.Snapshot().MonstersKilled);
        // ...but it's aged out of the rolling sparkline window.
        IReadOnlyList<double> series = t.KillsPerHourSeries(6);
        Assert.All(series, v => Assert.Equal(0d, v, 6));
    }

    [Fact]
    public void ExperiencePerHourSeries_IsRunningRate_WeightedByAmount()
    {
        (SessionActivityTracker t, Clock c) = Make();
        // 60-min session, 6 buckets ⇒ 10 min each. Gains land in bucket 1 and 4.
        c.Advance(15); t.NoteExperience(1000);  // t=15 ⇒ bucket 1 (10–20)
        c.Advance(30); t.NoteExperience(3000);  // t=45 ⇒ bucket 4 (40–50)
        c.Advance(15);                          // t=60, snapshot edge

        IReadOnlyList<double> series = t.ExperiencePerHourSeries(6);
        Assert.Equal(6, series.Count);
        // Cumulative exp ÷ hours so far, weighted by the gain amount.
        Assert.Equal(0d,     series[0], 3); // 0    / (1/6 h)
        Assert.Equal(3000d,  series[1], 3); // 1000 / (2/6 h)
        Assert.Equal(2000d,  series[2], 3); // 1000 / (3/6 h)
        Assert.Equal(1500d,  series[3], 3); // 1000 / (4/6 h)
        Assert.Equal(4800d,  series[4], 3); // 4000 / (5/6 h)
        Assert.Equal(4000d,  series[5], 3); // 4000 / (6/6 h)
        // The right-most point is the same exp/hour the panel prints.
        Assert.Equal(t.Snapshot().ExperiencePerHour, series[^1], 3);
    }

    [Fact]
    public void ExperiencePerHourSeries_PrunesBeyondWindow_ButTotalKeepsTheGain()
    {
        (SessionActivityTracker t, Clock c) = Make();
        t.NoteExperience(5000); // t=0
        c.Advance(70);          // 70 min later — outside the 60-min window

        // Total still counts the old gain...
        Assert.Equal(5000L, t.Snapshot().ExperienceEarned);
        // ...but it's aged out of the rolling sparkline window.
        IReadOnlyList<double> series = t.ExperiencePerHourSeries(6);
        Assert.All(series, v => Assert.Equal(0d, v, 6));
    }

    [Fact]
    public void KillsPerHourSeries_EmptyBeforeAnyTimeElapses()
    {
        (SessionActivityTracker t, _) = Make();
        t.NoteKill();
        Assert.Empty(t.KillsPerHourSeries(6)); // zero span ⇒ nothing to plot
    }

    [Fact]
    public void KillsPerHourSeries_RejectsNonPositiveBuckets()
    {
        (SessionActivityTracker t, Clock c) = Make();
        c.Advance(10);
        Assert.Empty(t.KillsPerHourSeries(0));
    }

    // ----- reset / change ----------------------------------------------

    [Fact]
    public void Reset_ZeroesEverything_AndRestartsTheClock()
    {
        (SessionActivityTracker t, Clock c) = Make();
        for (int i = 0; i < 4; i++) t.NoteKill();
        t.NoteExperience(5000);
        t.NoteCurrencyCollected(7000);
        t.NoteCurrencyStashed(3000);
        c.Advance(20);

        t.Reset();
        c.Advance(10);

        SessionActivityStats s = t.Snapshot();
        Assert.Equal(0, s.MonstersKilled);
        Assert.Equal(0L, s.ExperienceEarned);
        Assert.Equal(0L, s.CurrencyCollected);
        Assert.Equal(0L, s.CurrencyStashed);
        Assert.Equal(TimeSpan.FromMinutes(10), s.TimeOnline); // clock restarted at Reset
    }

    [Fact]
    public void Changed_FiresOnInput()
    {
        (SessionActivityTracker t, _) = Make();
        int fired = 0;
        t.Changed += () => fired++;
        t.NoteKill();
        t.NoteExperience(100);
        t.Reset();
        Assert.True(fired >= 3);
    }
}
