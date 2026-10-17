using MudPlay.Game.Combat;
using Xunit;

namespace MudPlay.Tests;

// SessionActivityTracker kill / exp counters and the
// kills/hour running-average series. An injected clock makes the time arithmetic
// deterministic: kills and exp are pushed via the Note* forwarders and
// time advanced by hand, so each test pins exactly how a counter or the running
// sparkline rate is derived.
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
        t.NoteCurrencyCollected(1500, 15);
        t.NoteCurrencyCollected(500, 50);
        t.NoteCurrencyCollected(0, 3);    // ignored
        t.NoteCurrencyCollected(-10, 1);  // ignored
        t.NoteCurrencyStashed(1200, 12);
        t.NoteCurrencyStashed(0, 4);      // ignored

        SessionActivityStats s = t.Snapshot();
        Assert.Equal(2000L, s.CurrencyCollected);
        Assert.Equal(65L, s.CoinsCollected);
        Assert.Equal(1200L, s.CurrencyStashed);
        Assert.Equal(12L, s.CoinsStashed);
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
        t.NoteCurrencyCollected(9000, 90);
        c.Advance(30);

        Assert.Equal(18_000d, t.Snapshot().CurrencyPerHour, 3); // 9,000 / 0.5 h
        Assert.Equal(180d, t.Snapshot().CoinsPerHour, 3);       // 90 coins / 0.5 h
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
        c.Advance(70);       // 70 min later — still inside the 4-hour rate window

        SessionActivityStats s = t.Snapshot();
        Assert.Equal(1, s.MonstersKilled);
        // The kill is inside the 4-hour window, so the curve stays non-zero and
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
        c.Advance(70);          // 70 min later — still inside the 4-hour rate window

        SessionActivityStats s = t.Snapshot();
        Assert.Equal(5000L, s.ExperienceEarned);
        // The gain is inside the 4-hour window, so the curve stays non-zero and
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

    // ----- 4-hour rate window cap --------------------------------------

    [Fact]
    public void RateWindow_CapsAtFourHours_DropsOlderKills_LifetimeTotalKeepsThem()
    {
        (SessionActivityTracker t, Clock c) = Make();
        t.NoteKill();          // t=0
        c.Advance(5 * 60);     // +5 h — past the 4-hour window
        t.NoteKill();          // t=5h; the eager trim drops the t=0 kill from the window

        SessionActivityStats s = t.Snapshot();
        Assert.Equal(2, s.MonstersKilled);                   // lifetime total keeps both
        Assert.Equal(1, s.RateKills);                        // window kept only the recent one
        Assert.Equal(TimeSpan.FromHours(4), s.TimeOnline);   // window (and TimeOnline) caps at 4 h
        Assert.Equal(0.25d, s.KillsPerHour, 3);              // 1 kill / 4 h
    }

    [Fact]
    public void TimeOnline_CapsAtFourHours_EvenWithNoEvents()
    {
        (SessionActivityTracker t, Clock c) = Make();
        c.Advance(6 * 60); // +6 h idle
        Assert.Equal(TimeSpan.FromHours(4), t.Snapshot().TimeOnline);
    }

    [Fact]
    public void RateWindow_CapsCurrencyAtFourHours()
    {
        (SessionActivityTracker t, Clock c) = Make();
        t.NoteCurrencyCollected(10_000, 1); // t=0
        c.Advance(5 * 60);               // +5 h
        t.NoteCurrencyCollected(2_000, 1);  // t=5h; old pickup trimmed from the window

        SessionActivityStats s = t.Snapshot();
        Assert.Equal(12_000L, s.CurrencyCollected); // lifetime keeps both
        Assert.Equal(2_000L, s.RateCurrency);       // window kept only the recent one
        Assert.Equal(500d, s.CurrencyPerHour, 3);   // 2,000 / 4 h
    }

    [Fact]
    public void KillsPerHourSeries_RightEdgeMatchesHeadline_PastFourHours()
    {
        (SessionActivityTracker t, Clock c) = Make();
        t.NoteKill();          // t=0 — will fall outside the trailing 4-hour window
        c.Advance(3 * 60);     // t=3h
        t.NoteKill();          // t=3h — inside the window at snapshot time
        c.Advance(3 * 60);     // t=6h — window is now [2h, 6h]

        SessionActivityStats s = t.Snapshot();
        Assert.Equal(2, s.MonstersKilled); // lifetime total keeps both
        Assert.Equal(1, s.RateKills);      // only the t=3h kill is inside [2h, 6h]
        // Chart and headline share EffectiveStart as their origin, so the curve's
        // right edge still equals the headline kills/hour even once the window trails.
        IReadOnlyList<double> series = t.KillsPerHourSeries(6);
        Assert.True(series[^1] > 0);
        Assert.Equal(s.KillsPerHour, series[^1], 3);
    }

    // ----- copper / items ----------------------------------------------

    [Fact]
    public void ItemsAndDepositSold_SumSeparately_AndIgnoreNonPositive()
    {
        (SessionActivityTracker t, _) = Make();
        t.NoteItemsCollected(3);
        t.NoteItemsCollected(0);          // ignored
        t.NoteItemsStashed(5);
        t.NoteItemsStashed(-1);           // ignored
        t.NoteCurrencyDeposited(1000);    // bank
        t.NoteCurrencyDeposited(0);       // ignored
        t.NoteSale(2, 250);               // two items for 250 copper
        t.NoteSale(1, 0);                 // sold for nothing still counts the item

        SessionActivityStats s = t.Snapshot();
        Assert.Equal(3, s.ItemsCollected);
        Assert.Equal(5, s.ItemsStashed);
        Assert.Equal(3, s.ItemsSold);
        Assert.Equal(1250L, s.CurrencyDeposited);   // deposits + sale proceeds
        Assert.Equal(0L, s.CurrencyStashed);        // deposits no longer count as stashed
        Assert.Equal(0L, s.CurrencyCollected);      // sales aren't pickups
    }

    // ----- sneak ---------------------------------------------------------

    [Fact]
    public void SneakPercent_IsHeldOverEntries_NullBeforeAny()
    {
        (SessionActivityTracker t, _) = Make();
        Assert.Null(t.Snapshot().SneakPercent);

        t.NoteSneakEntry(true);
        t.NoteSneakEntry(true);
        t.NoteSneakEntry(true);
        t.NoteSneakEntry(false);

        SessionActivityStats s = t.Snapshot();
        Assert.Equal(4, s.SneakEntries);
        Assert.Equal(3, s.SneakHeld);
        Assert.Equal(75d, s.SneakPercent!.Value, 3);
    }

    [Fact]
    public void DisarmPercent_IsDisarmedOverAttempts_NullBeforeAny()
    {
        (SessionActivityTracker t, _) = Make();
        Assert.Null(t.Snapshot().DisarmPercent);

        t.NoteDisarmAttempt(false);
        t.NoteDisarmAttempt(true);
        t.NoteDisarmAttempt(false);
        t.NoteDisarmAttempt(true);

        SessionActivityStats s = t.Snapshot();
        Assert.Equal(4, s.DisarmAttempts);
        Assert.Equal(2, s.TrapsDisarmed);
        Assert.Equal(50d, s.DisarmPercent!.Value, 3);

        t.ResetMovement();
        Assert.Equal(0, t.Snapshot().DisarmAttempts);
    }

    // ----- walk ----------------------------------------------------------

    [Fact]
    public void Walk_AveragesSendToArrival_OnlyForTimedSteps()
    {
        (SessionActivityTracker t, Clock c) = Make();
        Assert.Null(t.Snapshot().AverageStep);

        t.NoteStepSent();
        c.Advance(1.0 / 60);              // 1 s
        t.NoteStepArrived();

        c.Advance(5);                     // stopped between steps (a fight): not counted

        t.NoteStepSent();
        c.Advance(2.0 / 60);              // 2 s
        t.NoteStepArrived();

        t.NoteStepArrived();              // no step in flight: ignored

        SessionActivityStats s = t.Snapshot();
        Assert.Equal(2, s.Steps);
        Assert.Equal(1.5, s.AverageStep!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void Walk_AbandonedStep_IsNotCounted()
    {
        (SessionActivityTracker t, Clock c) = Make();
        t.NoteStepSent();
        c.Advance(1);
        Assert.True(t.NoteStepAbandoned());   // refused / re-looked
        Assert.False(t.NoteStepAbandoned());  // nothing left to drop
        t.NoteStepArrived();                  // late arrival no longer timed

        Assert.Equal(0, t.Snapshot().Steps);
    }

    // ----- reset / change ----------------------------------------------

    [Fact]
    public void Reset_ZeroesTheCopperItemsSneakAndWalkFigures()
    {
        (SessionActivityTracker t, Clock c) = Make();
        t.NoteItemsCollected(2);
        t.NoteItemsStashed(2);
        t.NoteSale(1, 100);
        t.NoteCurrencyDeposited(500);
        t.NoteSneakEntry(true);
        t.NoteStepSent();
        c.Advance(1.0 / 60);
        t.NoteStepArrived();
        t.NoteStepSent();                 // in flight across the reset

        t.Reset();
        c.Advance(1.0 / 60);
        t.NoteStepArrived();              // belongs to the old session: dropped

        SessionActivityStats s = t.Snapshot();
        Assert.Equal(0, s.ItemsCollected);
        Assert.Equal(0, s.ItemsStashed);
        Assert.Equal(0, s.ItemsSold);
        Assert.Equal(0L, s.CurrencyDeposited);
        Assert.Equal(0, s.SneakEntries);
        Assert.Equal(0, s.Steps);
        Assert.Equal(TimeSpan.Zero, s.StepTime);
    }

    [Fact]
    public void Reset_ZeroesEverything_AndRestartsTheClock()
    {
        (SessionActivityTracker t, Clock c) = Make();
        for (int i = 0; i < 4; i++) t.NoteKill();
        t.NoteExperience(5000);
        t.NoteCurrencyCollected(7000, 1);
        t.NoteCurrencyStashed(3000, 1);
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

    // Sneak and Walk show under Time Analysis, so each section's Reset clears only
    // its own figures.
    [Fact]
    public void SectionResets_ClearOnlyTheirOwnFigures()
    {
        (SessionActivityTracker t, Clock c) = Make();
        t.NoteKill();
        t.NoteSneakEntry(true);
        t.NoteStepSent();
        c.Advance(1);
        t.NoteStepArrived();

        t.ResetTotals();
        SessionActivityStats s = t.Snapshot();
        Assert.Equal(0, s.MonstersKilled);
        Assert.Equal(1, s.SneakEntries);
        Assert.Equal(1, s.Steps);

        t.NoteKill();
        t.ResetMovement();
        s = t.Snapshot();
        Assert.Equal(1, s.MonstersKilled);
        Assert.Equal(0, s.SneakEntries);
        Assert.Equal(0, s.Steps);
    }

    [Fact]
    public void ResetTotals_NewEventsDriveAFreshRate()
    {
        (SessionActivityTracker t, Clock c) = Make();
        for (int i = 0; i < 10; i++) t.NoteKill();
        c.Advance(60);
        t.ResetTotals();

        t.NoteKill();
        t.NoteKill();
        c.Advance(30); // half an hour into the new window

        SessionActivityStats s = t.Snapshot();
        Assert.Equal(2, s.MonstersKilled);
        Assert.Equal(4d, s.KillsPerHour, 3);  // 2 kills / 0.5 h in the new window only

        // The sparkline re-anchors to the new window, so its right edge still
        // equals the (now windowed) headline kills/hour.
        IReadOnlyList<double> series = t.KillsPerHourSeries(6);
        Assert.Equal(s.KillsPerHour, series[^1], 3);
    }

    [Fact]
    public void ResetTotals_FiresChanged()
    {
        (SessionActivityTracker t, _) = Make();
        int fired = 0;
        t.Changed += () => fired++;
        t.ResetTotals();
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
