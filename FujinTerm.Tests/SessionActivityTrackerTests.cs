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
    public void KillsPerHourSeries_KeepsOldKills_RightEdgeMatchesHeadline()
    {
        (SessionActivityTracker t, Clock c) = Make();
        t.NoteKill();        // t=0
        c.Advance(70);       // 70 min later — a rolling window would have dropped it

        SessionActivityStats s = t.Snapshot();
        Assert.Equal(1, s.MonstersKilled);
        // The kill is kept for the whole session, so the curve stays non-zero and
        // its right edge equals the headline kills/hour the panel prints.
        IReadOnlyList<double> series = t.KillsPerHourSeries(6);
        Assert.True(series[^1] > 0);
        Assert.Equal(s.KillsPerHour, series[^1], 3); // 1 kill / (70/60 h)
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
    public void ExperiencePerHourSeries_KeepsOldGains_RightEdgeMatchesHeadline()
    {
        (SessionActivityTracker t, Clock c) = Make();
        t.NoteExperience(5000); // t=0
        c.Advance(70);          // 70 min later — a rolling window would have dropped it

        SessionActivityStats s = t.Snapshot();
        Assert.Equal(5000L, s.ExperienceEarned);
        // The gain is kept for the whole session, so the curve stays non-zero and
        // its right edge equals the headline exp/hour the panel prints.
        IReadOnlyList<double> series = t.ExperiencePerHourSeries(6);
        Assert.True(series[^1] > 0);
        Assert.Equal(s.ExperiencePerHour, series[^1], 3); // 5000 exp / (70/60 h)
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
    public void ResetRates_KeepsLifetimeTotals_ButZeroesTheRates()
    {
        (SessionActivityTracker t, Clock c) = Make();
        for (int i = 0; i < 4; i++) t.NoteKill();
        t.NoteExperience(5000);
        t.NoteCurrencyCollected(7000);
        t.NoteCurrencyStashed(3000);
        c.Advance(30);

        t.ResetRates();     // the Time Analysis reset restarts only the rate window
        c.Advance(15);

        SessionActivityStats s = t.Snapshot();
        // Lifetime totals survive the rate-window reset.
        Assert.Equal(4, s.MonstersKilled);
        Assert.Equal(5000L, s.ExperienceEarned);
        Assert.Equal(7000L, s.CurrencyCollected);
        Assert.Equal(3000L, s.CurrencyStashed);
        // Rates fall to zero — no events have landed in the fresh window yet.
        Assert.Equal(0d, s.KillsPerHour);
        Assert.Equal(0d, s.ExperiencePerHour);
        Assert.Equal(0d, s.CurrencyPerHour);
        // The window clock restarted at ResetRates, so TimeOnline measures from there.
        Assert.Equal(TimeSpan.FromMinutes(15), s.TimeOnline);
    }

    [Fact]
    public void ResetRates_NewEventsDriveAFreshRate_TotalsKeepClimbing()
    {
        (SessionActivityTracker t, Clock c) = Make();
        for (int i = 0; i < 10; i++) t.NoteKill();
        c.Advance(60);
        t.ResetRates();

        t.NoteKill();
        t.NoteKill();
        c.Advance(30); // half an hour into the new window

        SessionActivityStats s = t.Snapshot();
        Assert.Equal(12, s.MonstersKilled);   // lifetime total spans both windows
        Assert.Equal(4d, s.KillsPerHour, 3);  // 2 kills / 0.5 h in the new window only

        // The sparkline re-anchors to the new window, so its right edge still
        // equals the (now windowed) headline kills/hour.
        IReadOnlyList<double> series = t.KillsPerHourSeries(6);
        Assert.Equal(s.KillsPerHour, series[^1], 3);
    }

    [Fact]
    public void ResetRates_FiresChanged()
    {
        (SessionActivityTracker t, _) = Make();
        int fired = 0;
        t.Changed += () => fired++;
        t.ResetRates();
        Assert.Equal(1, fired);
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
